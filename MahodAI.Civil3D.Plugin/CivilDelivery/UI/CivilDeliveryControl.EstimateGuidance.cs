using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Button = System.Windows.Controls.Button;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    public partial class CivilDeliveryControl
    {
        private EstimateGuidedActionPolicy.State? _estimateGuidedAction;

        /// <summary>Called after RefreshGates has calculated source freshness once.</summary>
        private void RefreshEstimateGuidance(
            Button primaryAction, bool profileUsable, bool estimateScanFresh,
            bool saveMayBeRequired, int blockingFindings)
        {
            RefreshEstimateRowPresentation(estimateScanFresh);
            RefreshEstimateReviewOverview(estimateScanFresh);
            RefreshQuantityReviewFilter();
            var sourceReviewCount = EstimateSourceReviewFindingCount();
            _estimateGuidedAction = EstimateGuidedActionPolicy.Evaluate(new(
                Available: profileUsable && Doc() != null,
                SavePending: _pendingWorkflowSaveDocument != null,
                SourcesApproved: EstimateWorkflowService.IsReviewedSourceScopeApproved(_profile),
                EarthworksResolved: EstimateWorkflowService.GetEarthworksDecision(_profile).IsResolved,
                SaveMayBeRequired: saveMayBeRequired,
                HasFreshScan: estimateScanFresh,
                QuantityGroups: estimateScanFresh ? _quantityRows.Count : 0,
                PendingReviewGroups: estimateScanFresh
                    ? _quantityRows.Count(row => !row.IsIgnored &&
                        string.IsNullOrWhiteSpace(row.CatalogCode) &&
                        !row.IsUnselectedClosedPolylineAlternative)
                    : 0,
                CatalogReady: _catalog != null,
                EstimateBuilt: _estimateResult != null,
                ExportReady: BtnExport.IsEnabled,
                BlockingFindings: estimateScanFresh ? sourceReviewCount : 0,
                ProvenMappingGroups: estimateScanFresh && BtnApproveProvenMappings.IsEnabled
                    ? ProvenBatchMappingCandidates().Count : 0,
                HasApprovedMappings: estimateScanFresh && _quantityRows.Any(row =>
                    !row.IsIgnored && !string.IsNullOrWhiteSpace(row.CatalogCode)),
                PartialPricedDraftReady: BtnExportPricedDraft.IsEnabled,
                HasExistingScan: _scan != null || _historicalScan != null,
                FreshnessReason: _lastEstimateFreshnessReason,
                NoiseReviewGroups: estimateScanFresh ? BulkNoiseCandidates().Count : 0));
            if (profileUsable && Doc() != null && NeedsEstimateProjectStart && _pendingWorkflowSaveDocument == null)
            {
                var completing = _profileWriteState?.SourceExisted == true;
                _estimateGuidedAction = new(EstimateGuidedActionPolicy.Action.Scan,
                    completing ? "השלם תחום עבודה והתחל מדידה…" : saveMayBeRequired ? "שמור והתחל אומדן חדש…" : "התחל אומדן בפרויקט חדש…",
                    completing ? "בחר תחום עבודה במפורש לפני המדידה. המחירונים, המקורות וההחלטות הקיימות יישמרו." : EstimateProjectStartService.Guidance);
                BtnScan.Content = completing ? "השלם תחום עבודה…" : "התחל אומדן חדש…";
                BtnApproveEstimateScope.Content = completing ? "השלם תחום עבודה…" : "התחל אומדן ואשר מקורות…";
            }
            // 30.09 (Arthur): with project BoQ rules, a fresh scan leads straight to the rules bill — not to per-layer review.
            if (profileUsable && estimateScanFresh && _pendingWorkflowSaveDocument == null && _scan is { Records.Count: > 0 } &&
                ProjectHasBoqRules() && _estimateGuidedAction.Next is EstimateGuidedActionPolicy.Action.ReviewFindings
                    or EstimateGuidedActionPolicy.Action.ReviewSources or EstimateGuidedActionPolicy.Action.ReviewQuantities
                    or EstimateGuidedActionPolicy.Action.ReviewProvenMappings or EstimateGuidedActionPolicy.Action.ReviewDrawingNoise
                    or EstimateGuidedActionPolicy.Action.LoadCatalog or EstimateGuidedActionPolicy.Action.Build
                    or EstimateGuidedActionPolicy.Action.Export or EstimateGuidedActionPolicy.Action.ExportPricedDraft)
                _estimateGuidedAction = new(EstimateGuidedActionPolicy.Action.ExportBoqRules,
                    EstimateGuidedActionPolicy.BoqRulesCaption, EstimateGuidedActionPolicy.BoqRulesDetail,
                    BtnExportBoqRules.IsEnabled);
            primaryAction.Content = _estimateGuidedAction.Caption;
            primaryAction.ToolTip = _estimateGuidedAction.Detail;
            primaryAction.IsEnabled = _estimateGuidedAction.Enabled;
            EstimateNextAction.Text = _estimateGuidedAction.Detail;
            BtnReviewMappings.Content = "שיוך קבוצות יחד…";
            BtnReviewMappings.ToolTip = "הצעות ותיאורים בטבלה אחת. חפש למשל מים או חשמל, בחר סעיף וסמן קבוצות מוצגות תואמות. שמירה אחת של הבחירות שנבדקו; אין שינוי בכמויות. " +
                "זמין גם כשכיסוי המקורות עדיין חסום: השיוך נשמר, והסכום נפתח רק אחרי שהמקור יושלם.";
            if (_estimateGuidedAction.Next == EstimateGuidedActionPolicy.Action.ReviewQuantities)
                EstimateNextAction.Text += " אפשר גם לחפש סעיף אחר לפי תיאור או מספר באותו חלון.";
            QuantityPriceColumn.Header = _estimateResult == null ? "מחירון" : "מחיר בנוי";
            BtnReviewEstimateEvidence.IsEnabled = _scan != null || _estimateResult != null;
        }

        // Presentation routing only. Never remove deferred scope findings from
        // the scan/result: final BUILD/EXPORT still requires those decisions.
        private int EstimateSourceReviewFindingCount() => _scan == null ? 0 :
            EstimateGuidedActionPolicy.SourceReviewFindingCount(_scan.Findings,
                    _quantityRows.Where(row => row.HasClosedPolylineAlternative)
                        .SelectMany(row => new[]
                        {
                            $"{row.RuleKey} <> {row.AlternativeRuleKey}",
                            $"{row.AlternativeRuleKey} <> {row.RuleKey}",
                        }));

        private void OnStartEstimateGuided(object sender, RoutedEventArgs e)
        {
            // Re-evaluate before routing. A changed profile or DWG must not cause a
            // caption from the previous state to execute a later operation.
            ReloadProfile();
            RefreshGates();
            var action = _estimateGuidedAction;
            if (action?.Enabled != true) return;

            try
            {
                // Exactly one existing handler per click. Cancelling source scope,
                // earthworks, save, mapping or export never advances the workflow.
                switch (action.Next)
                {
                    case EstimateGuidedActionPolicy.Action.ApproveSources:
                        OnApproveEstimateScope(sender, e);
                        break;
                    case EstimateGuidedActionPolicy.Action.DecideEarthworks:
                        OnEarthworksDecision(sender, e);
                        break;
                    case EstimateGuidedActionPolicy.Action.Scan:
                        OnScan(sender, e);
                        break;
                    case EstimateGuidedActionPolicy.Action.LoadCatalog:
                        OnLoadPriceBook(sender, e);
                        break;
                    case EstimateGuidedActionPolicy.Action.ReviewQuantities:
                        OpenNextEstimateQuantityDecision(sender, e);
                        break;
                    case EstimateGuidedActionPolicy.Action.ReviewProvenMappings:
                        if (BtnApproveProvenMappings.IsEnabled) OnApproveProvenMappings(sender, e);
                        break;
                    case EstimateGuidedActionPolicy.Action.ReviewDrawingNoise:
                        if (BtnFilterDrawingNoise.IsEnabled) OnFilterDrawingNoise(sender, e);
                        break;
                    case EstimateGuidedActionPolicy.Action.Build:
                        if (BtnBuild.IsEnabled) OnBuildEstimate(sender, e);
                        break;
                    case EstimateGuidedActionPolicy.Action.Export:
                        if (BtnExport.IsEnabled) OnExportEstimate(sender, e);
                        break;
                    case EstimateGuidedActionPolicy.Action.ExportPricedDraft:
                        if (BtnExportPricedDraft.IsEnabled) OnExportPricedDraft(sender, e);
                        break;
                    case EstimateGuidedActionPolicy.Action.ReviewSources:
                    case EstimateGuidedActionPolicy.Action.ReviewFindings:
                        ShowEstimateGuidedReview();
                        break;
                    case EstimateGuidedActionPolicy.Action.ExportBoqRules:
                        if (BtnExportBoqRules.IsEnabled) OnExportBoqRules(sender, e);
                        break;
                }
            }
            catch (Exception ex) { ShowError("המשך אומדן", ex); }
            finally { RefreshGates(); }
        }

        private void OpenNextEstimateQuantityDecision(object sender, RoutedEventArgs e)
        {
            static bool NeedsDecision(QuantityRowViewModel row) =>
                !row.IsIgnored && string.IsNullOrWhiteSpace(row.CatalogCode) &&
                row.CanApproveCatalogMapping;

            if (BtnReviewMappings.IsEnabled && _quantityRows.Count(NeedsDecision) > 1)
            {
                OnReviewMappings(sender, e);
                return;
            }

            var selected = QuantitiesGrid.SelectedItem as QuantityRowViewModel;
            var next = selected != null && NeedsDecision(selected)
                ? selected
                : _quantityRows.FirstOrDefault(NeedsDecision);
            if (next == null)
            {
                ShowEstimateGuidedReview();
                return;
            }
            SelectQuantityReviewRow(next);
            QuantitiesGrid.ScrollIntoView(next);
            RefreshGates();
            if (BtnApprove.IsEnabled) OnApproveMapping(sender, e);
        }

        private void ShowEstimateGuidedReview()
        {
            // Existing captured evidence only: no native inventory, path resolution,
            // new source approval, or drawing mutation occurs when opening review.
            var sources = _scan?.ExternalSources
                .Select(source => ((string?)source.DrawingPath, (string?)source.XrefChain))
                ?? Enumerable.Empty<(string?, string?)>();
            var issues = CurrentEstimateReviewIssues();
            var groups = EstimateReviewGroupingPolicy.Group(issues,
                _scan?.Records ?? new List<NeutralQuantityRecord>());
            var selectedGroup = groups.FirstOrDefault();
            var pager = new EstimateReviewPager(
                EstimateGateReason.Text, _scan?.SourceDrawing, sources,
                selectedGroup?.Issues ?? Array.Empty<EstimateReviewPolicy.Issue>());
            QuantityDetail.Text = $"{groups.Count:N0} קבוצות החלטה מתוך {issues.Count:N0} ממצאים. כל הפרטים נשמרים בתוך הקבוצות.";

            // A MessageBox cannot scroll a long XREF chain/error report. Keep every
            // source and finding available in a selectable, wrapping read-only box.
            var report = new System.Windows.Controls.TextBox
            {
                Text = pager.Text,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(12),
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x1C, 0x22, 0x30)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
            };
            System.Windows.Automation.AutomationProperties.SetName(report, "פרטי מקורות וממצאים מלאים");
            var close = new Button
            {
                Content = "סגור", IsCancel = true, IsDefault = false,
                MinWidth = 88, MinHeight = 38, Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            };
            var buttons = new WrapPanel();
            buttons.Children.Add(close);
            var previous = new Button { Content = "הקודם", MinHeight = 38, MinWidth = 72,
                Margin = new Thickness(8, 12, 0, 0) };
            var next = new Button { Content = "הבא", MinHeight = 38, MinWidth = 72,
                Margin = new Thickness(8, 12, 0, 0) };
            buttons.Children.Add(previous);
            buttons.Children.Add(next);
            var position = new TextBlock { Text = pager.Position, TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 8) };
            var categories = new[] { new { Label = $"כל סוגי הממצאים ({groups.Count:N0} קבוצות)", Code = (string?)null } }
                .Concat(groups.GroupBy(group => group.Code).OrderByDescending(group => group.Count())
                    .Select(group => new { Label = $"{group.Key} · {group.Count():N0} קבוצות החלטה · {group.Sum(item => item.Count):N0} ממצאים", Code = (string?)group.Key }))
                .ToArray();
            var category = new System.Windows.Controls.ComboBox { ItemsSource = categories, DisplayMemberPath = "Label",
                SelectedIndex = 0, MinHeight = 32, Margin = new Thickness(0, 0, 0, 8) };
            System.Windows.Automation.AutomationProperties.SetName(category, "סינון לפי סוג ממצא; אינו משנה החלטות");
            var groupChoice = new System.Windows.Controls.ComboBox
            {
                ItemsSource = groups.Select(group => new { Group = group, Label = group.Caption }).ToArray(),
                DisplayMemberPath = "Label", SelectedIndex = groups.Count > 0 ? 0 : -1,
                MinHeight = 34, MaxDropDownHeight = 300, Margin = new Thickness(0, 0, 0, 8),
            };
            System.Windows.Automation.AutomationProperties.SetName(groupChoice, "קבוצת החלטה; כל הממצאים המקוריים נשמרים בפירוט");
            var visibleGroups = groups;
            var groupPosition = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 0, 0, 8),
            };
            var selectedRecovery = EstimateReviewRecoveryTarget(pager.CurrentBatch);
            var recover = new Button
            {
                MinHeight = 38, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(8, 12, 0, 0),
                MaxWidth = 400,
            };
            buttons.Children.Add(recover);
            var locateSource = new Button
            {
                Content = "אתר עצם מתוך ממצא…", MinHeight = 38,
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(8, 12, 0, 0),
                ToolTip = "בחירת מקור מתועד גם כשלא נוצרה מדידה; איתור בלבד, ללא תיקון או אישור",
            };
            buttons.Children.Add(locateSource);
            void RefreshPage()
            {
                report.Text = pager.Text;
                report.ScrollToHome();
                groupPosition.Text = $"{visibleGroups.Count:N0} קבוצות החלטה · {issues.Count:N0} ממצאים בסריקה ובתמחור. " +
                    "בחר קבוצה לבדיקה או לעריכה; הקיבוץ אינו משנה כמויות או אישורים.";
                position.Text = pager.Position;
                previous.IsEnabled = pager.CanPrevious;
                next.IsEnabled = pager.CanNext;
                selectedRecovery = EstimateReviewRecoveryTarget(pager.CurrentBatch);
                recover.Content = selectedRecovery == null ? "אין פעולת שיוך בעמוד זה" :
                        (selectedRecovery.Value.Issue.Action == EstimateReviewPolicy.Recovery.Mapping
                            ? "בדוק שיוך: " : "בדוק מחיר: ") + selectedRecovery.Value.Row.RuleKey;
                recover.ToolTip = selectedRecovery?.Row.RuleKey;
                recover.Visibility = selectedRecovery == null ? Visibility.Collapsed : Visibility.Visible;
                var sourceCount = FindingSourceLocationPolicy.Collect(pager.CurrentBatch).Count;
                locateSource.Content = $"אתר עצם מתוך ממצא ({sourceCount})…";
                locateSource.Visibility = sourceCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            previous.Click += (_, _) => { pager.MovePrevious(); RefreshPage(); };
            next.Click += (_, _) => { pager.MoveNext(); RefreshPage(); };
            RefreshPage();
            var changingGroups = false;
            groupChoice.SelectionChanged += (_, _) =>
            {
                if (changingGroups) return;
                var index = groupChoice.SelectedIndex;
                selectedGroup = index >= 0 && index < visibleGroups.Count ? visibleGroups[index] : null;
                pager = new EstimateReviewPager(EstimateGateReason.Text, _scan?.SourceDrawing, sources,
                    selectedGroup?.Issues ?? Array.Empty<EstimateReviewPolicy.Issue>());
                RefreshPage();
            };
            category.SelectionChanged += (_, _) =>
            {
                var index = category.SelectedIndex;
                if (index < 0 || index >= categories.Length) return;
                var code = categories[index].Code;
                visibleGroups = code == null ? groups : groups.Where(group => group.Code == code).ToArray();
                changingGroups = true;
                groupChoice.ItemsSource = visibleGroups.Select(group => new { Group = group, Label = group.Caption }).ToArray();
                groupChoice.SelectedIndex = visibleGroups.Count > 0 ? 0 : -1;
                changingGroups = false;
                selectedGroup = visibleGroups.FirstOrDefault();
                pager = new EstimateReviewPager(EstimateGateReason.Text, _scan?.SourceDrawing, sources,
                    selectedGroup?.Issues ?? Array.Empty<EstimateReviewPolicy.Issue>());
                RefreshPage();
            };
            var loadCatalog = new Button
            {
                Content = "טען מחירון…", MinHeight = 38,
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(8, 12, 0, 0),
                Visibility = issues.Any(issue => issue.Action == EstimateReviewPolicy.Recovery.Catalog)
                    ? Visibility.Visible : Visibility.Collapsed,
            };
            buttons.Children.Add(loadCatalog);
            var materialAreas = new Button
            {
                Content = $"שטחי חומר בחתכים ({_scan?.MaterialAreas.Count ?? 0})…", MinHeight = 38,
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(8, 12, 0, 0),
                Visibility = _scan?.MaterialAreas.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
            };
            buttons.Children.Add(materialAreas);
            var panel = new DockPanel { Margin = new Thickness(16) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            panel.Children.Add(buttons);
            DockPanel.SetDock(position, Dock.Top);
            panel.Children.Add(position);
            DockPanel.SetDock(category, Dock.Top);
            panel.Children.Add(category);
            DockPanel.SetDock(groupPosition, Dock.Top);
            panel.Children.Add(groupPosition);
            DockPanel.SetDock(groupChoice, Dock.Top);
            panel.Children.Add(groupChoice);
            panel.Children.Add(report);
            var dialog = new Window
            {
                Title = "בדיקת כמויות, מחירים וחסמי אומדן",
                Content = panel, Width = 780, Height = 640, MinWidth = 540, MinHeight = 360,
                ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                FlowDirection = System.Windows.FlowDirection.RightToLeft,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x14, 0x18, 0x21)),
            };
            var requestedRecovery = false;
            ApplyReviewDialogButtonStyle(dialog);
            var requestedCatalog = false;
            var requestedAreas = false;
            IReadOnlyList<FindingSourceLocationPolicy.Target>? requestedSources = null;
            var reviewedScan = _scan;
            var reviewedResult = _estimateResult;
            var reviewedProfile = _profile;
            recover.Click += (_, _) => { requestedRecovery = true; dialog.Close(); };
            loadCatalog.Click += (_, _) => { requestedCatalog = true; dialog.Close(); };
            materialAreas.Click += (_, _) => { requestedAreas = true; dialog.Close(); };
            locateSource.Click += (_, _) =>
            {
                requestedSources = FindingSourceLocationPolicy.Collect(pager.CurrentBatch);
                dialog.Close();
            };
            close.Click += (_, _) => dialog.Close();
            UiGuard.Attach(dialog, "בדיקת מקורות וכמויות");
            CivilModalHost.ShowFromPalette(dialog);
            // Only an explicit action button dispatches a confirmed existing handler.
            // Closing/Escape leaves every source, decision and quantity unchanged.
            if ((requestedSources != null || requestedAreas || requestedCatalog || requestedRecovery) &&
                (!ReferenceEquals(reviewedScan, _scan) || !ReferenceEquals(reviewedResult, _estimateResult) ||
                 !ReferenceEquals(reviewedProfile, _profile)))
            {
                SetStatus("הסריקה, הפרופיל או התמחור השתנו; פתח את הממצאים מחדש. לא בוצעה פעולה.");
                return;
            }
            if (requestedSources != null && reviewedScan != null)
                ShowEstimateFindingSources(requestedSources, reviewedScan);
            else if (requestedAreas) ShowEstimateMaterialAreas();
            else if (requestedCatalog) OnLoadPriceBook(this, new RoutedEventArgs());
            else if (requestedRecovery && selectedRecovery != null)
            {
                SelectQuantityReviewRow(selectedRecovery.Value.Row);
                QuantitiesGrid.ScrollIntoView(selectedRecovery.Value.Row);
                RefreshGates();
                if (selectedRecovery.Value.Issue.Action == EstimateReviewPolicy.Recovery.Mapping && BtnApprove.IsEnabled)
                    OnApproveMapping(this, new RoutedEventArgs());
                else if (selectedRecovery.Value.Issue.Action == EstimateReviewPolicy.Recovery.Price)
                    OnApproveProjectPrice(selectedRecovery.Value.Row);
            }
        }

        private void ApplyReviewDialogButtonStyle(Window dialog)
        {
            // Reuse the exact tested palette style: disabled/hover/focus and wrapped
            // string labels, rather than a second disconnected light button theme.
            if (Resources[typeof(Button)] is Style buttonStyle)
                dialog.Resources[typeof(Button)] = buttonStyle;
        }

        private object? _reviewScanIdentity;
        private object? _reviewResultIdentity;
        private string? _reviewCatalogIdentity;
        private IReadOnlyList<EstimateReviewPolicy.Issue>? _reviewIssues;
        private IReadOnlyList<EstimateReviewPolicy.Issue> CurrentEstimateReviewIssues()
        {
            var catalogIdentity = string.Join("|", _catalogFindings.Select(finding => finding.FindingId));
            if (_reviewIssues != null && ReferenceEquals(_reviewScanIdentity, _scan) &&
                ReferenceEquals(_reviewResultIdentity, _estimateResult) && _reviewCatalogIdentity == catalogIdentity)
                return _reviewIssues;
            _reviewScanIdentity = _scan; _reviewResultIdentity = _estimateResult; _reviewCatalogIdentity = catalogIdentity;
            return _reviewIssues = EstimateReviewPolicy.Collect(
                _scan?.Records ?? Enumerable.Empty<NeutralQuantityRecord>(),
                _scan?.Findings ?? Enumerable.Empty<DeliveryFinding>(), _catalogFindings, _estimateResult);
        }

        private (QuantityRowViewModel Row, EstimateReviewPolicy.Issue Issue)? EstimateReviewRecoveryTarget(
            IReadOnlyList<EstimateReviewPolicy.Issue> issues)
        {
            foreach (var issue in issues.Where(issue => issue.Blocking &&
                         issue.Action is EstimateReviewPolicy.Recovery.Mapping or EstimateReviewPolicy.Recovery.Price))
                foreach (var key in issue.RuleKeys)
                {
                    var row = _quantityRows.FirstOrDefault(candidate => candidate.RuleKey == key && !candidate.IsIgnored);
                    if (row != null) return (row, issue);
                }
            return null;
        }

        private void RefreshEstimateRowPresentation(bool sourceFresh)
        {
            if (_scan == null) return;
            var records = _scan.Records.ToLookup(record => record.Classification.RuleKey ?? "(ללא חוק)", StringComparer.Ordinal);
            var global = _scan.Findings.Where(finding => finding.AffectedRecordIds.Count == 0).ToList();
            var affected = _scan.Findings.SelectMany(finding => finding.AffectedRecordIds.Select(id => (id, finding)))
                .ToLookup(entry => entry.id, entry => entry.finding, StringComparer.Ordinal);
            var lines = _estimateResult?.Lines.ToLookup(line => line.RecordId, StringComparer.Ordinal);
            var findingImpactIndex = EstimateFindingImpactPolicy.CreateIndex(_scan.Findings);
            foreach (var row in _quantityRows)
            {
                var group = records[row.RuleKey].ToList();
                var findings = global.Concat(group.SelectMany(record => affected[record.RecordId])).Distinct().ToList();
                row.Presentation = EstimateQuantityPresentationPolicy.Evaluate(group,
                    findings, row.CatalogCode, _catalog, _estimateResult, row.IsIgnored,
                    sourceFresh, BtnExport.IsEnabled,
                    lines == null ? null : group.SelectMany(record => lines[record.RecordId]).ToList(),
                    findingImpactIndex: findingImpactIndex);
            }
        }

        private void OnReviewEstimateEvidence(object sender, RoutedEventArgs e) => ShowEstimateGuidedReview();

        // Small call from OnQuantitySelected appends actual built-price and result
        // evidence to existing CAD details; it performs no new native reads.
        private void AppendEstimateReviewDetails(StringBuilder text, QuantityRowViewModel row)
        {
            text.AppendLine();
            text.AppendLine(row.Presentation?.Detail ?? "תמחור טרם נבדק");
            text.Append(EstimateReviewPager.RowSummary(CurrentEstimateReviewIssues()
                .Where(issue => issue.RuleKeys.Contains(row.RuleKey))));
        }
    }

    /// <summary>Pure formatting of already captured evidence; never resolves or changes a source.</summary>
    internal static class EstimateGuidedReviewText
    {
        internal const string XrefRepairInstructions =
            "להפניה חסרה או לא טעונה: פתח ב-Civil את חלונית ההפניות החיצוניות (XREF), " +
            "אתר את שם ההפניה והנתיב המדויק המפורטים להלן, ושחזר את קובץ המקור הנכון או עדכן במפורש את הקישור אליו. " +
            "טען/רענן (Reload) את ההפניה, שמור את השרטוט, ואז הרץ סריקת אומדן חדשה. " +
            "קובץ בעל שם דומה אינו הוכחה שזה אותו מקור מאושר; שינוי מקור עשוי לדרוש אישור חדש. " +
            "החלון הזה אינו מקשר, טוען או מאשר קבצים. כמויות שנמדדו נשארות לעיון, אך האומדן המלא אינו מאומת עד לפתרון הממצא.";

        internal static string Build(
            string? gateReason, string? sourceDrawing,
            IEnumerable<(string? DrawingPath, string? XrefChain)> sources,
            IEnumerable<DeliveryFinding> findings)
        {
            var blockers = findings.Where(EstimatePreflightPolicy.IsBlocking).ToList();
            var text = new StringBuilder();
            text.AppendLine(EstimateGuidedActionPolicy.MeasurementContext);
            text.AppendLine();
            text.AppendLine(gateReason);
            if (blockers.Any(finding => finding.Code.StartsWith("EST-XREF-", StringComparison.Ordinal)))
            {
                text.AppendLine();
                text.AppendLine(XrefRepairInstructions);
            }
            if (!string.IsNullOrWhiteSpace(sourceDrawing))
            {
                text.AppendLine();
                text.AppendLine("מקורות שתועדו בסריקה (לא אישור שכל ההפניות נמדדו):");
                text.AppendLine("שרטוט: " + Bidi.Ltr(sourceDrawing));
                foreach (var source in sources)
                    text.AppendLine("• " + Bidi.Ltr(source.DrawingPath) + " · " + Bidi.Ltr(source.XrefChain));
            }
            foreach (var finding in blockers)
            {
                text.AppendLine();
                text.AppendLine(finding.Title + " · " + Bidi.Ltr(finding.Code));
                if (!string.IsNullOrWhiteSpace(finding.Message))
                    text.AppendLine("פרטי המקור/הממצא: " + Bidi.Ltr(finding.Message));
                if (!string.IsNullOrWhiteSpace(finding.RecommendedAction))
                    text.AppendLine(finding.RecommendedAction);
            }
            return text.ToString().TrimEnd();
        }

        internal static string BuildUnified(string? gateReason, string? sourceDrawing,
            IEnumerable<(string? DrawingPath, string? XrefChain)> sources,
            IReadOnlyList<EstimateReviewPolicy.Issue> issues)
        {
            var text = new StringBuilder();
            text.AppendLine($"ממצאים בבחירה זו: {issues.Count}; חסמים: {issues.Count(issue => issue.Blocking)}");
            foreach (var issue in issues)
            {
                text.AppendLine($"\n{issue.Stage} · {(issue.Blocking ? "חסם" : "מידע/אזהרה")} · {issue.Title} · {Bidi.Ltr(issue.Code)}");
                text.AppendLine("הצעד הבא: " + issue.NextStep);
                if (issue.RuleKeys.Count > 0) text.AppendLine("קבוצות: " + Bidi.Ltr(string.Join(", ", issue.RuleKeys)));
                foreach (var source in issue.Sources)
                {
                    text.AppendLine("מקור מדויק: " + Bidi.Ltr(source.SourcePathOrUri ?? "נתיב לא תועד"));
                    text.AppendLine("עצם: " + Bidi.Ltr(source.SourceHandle ?? "מזהה לא תועד") +
                        " · שכבה: " + Bidi.Ltr(source.Layer ?? "—") +
                        " · שיטה: " + Bidi.Ltr(source.MeasurementMethod ?? "—"));
                    if (!string.IsNullOrWhiteSpace(source.XrefPath))
                        text.AppendLine("שרשרת XREF: " + Bidi.Ltr(source.XrefPath));
                    if (!string.IsNullOrWhiteSpace(source.DrawingChecksum))
                        text.AppendLine("SHA-256: " + Bidi.Ltr(source.DrawingChecksum));
                }
                if (issue.Sources.Count == 0 && issue.RecordIds.Count > 0)
                    text.AppendLine("לא קיימת זהות מקור מוכחת לאיתור מתוך ממצא זה; מזהי הרשומות והפרטים נשמרים לבדיקה. לא נבחר עצם חלופי.");
                if (!string.IsNullOrWhiteSpace(issue.Detail)) text.AppendLine("פרטי הממצא: " + Bidi.Ltr(issue.Detail));
                if (issue.RecordIds.Count > 0) text.AppendLine("רשומות: " + Bidi.Ltr(string.Join(", ", issue.RecordIds)));
            }
            if (issues.Any(issue => issue.Code.StartsWith("EST-XREF-", StringComparison.Ordinal)))
                text.AppendLine("\n\n" + XrefRepairInstructions);
            text.AppendLine("\n\nהקשר הסריקה הכללי — אינו פירוט הקבוצה שנבחרה:");
            text.Append(Build(gateReason, sourceDrawing, sources, Array.Empty<DeliveryFinding>()));
            return text.ToString().TrimEnd();
        }
    }
}
