using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
using System.Windows.Media.Imaging;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Support;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcadExtents3d = Autodesk.AutoCAD.DatabaseServices.Extents3d;
using AcadPoint2d = Autodesk.AutoCAD.Geometry.Point2d;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>
    /// The engineer-facing Mahod Civil Delivery panel.
    ///
    /// This is presentation only. Every number it shows comes from the same
    /// deterministic services the direct commands and the MahodAI tools call —
    /// there is no second engineering implementation behind these buttons.
    /// </summary>
    public partial class CivilDeliveryControl : System.Windows.Controls.UserControl
    {
        private readonly SectionsWorkflowService _sections = new();
        private readonly EstimateWorkflowService _estimate = new();

        private ProjectProfile? _profile;
        private string? _profileHash;
        private string? _profileSource;
        private string? _profileWriteTarget;
        private ProjectProfileWriter.ExpectedProfileState? _profileWriteState;
        private SectionPlan? _plan;
        private SectionApplyResult? _apply;
        private string? _applyPlanRunId;
        private SectionVerifyResult? _lastVerifyResult;
        private string? _verifySummary;
        private EstimateWorkflowService.ScanResult? _scan;
        // Display-only capture: never supplied to BUILD, EXPORT, approvals or native locators.
        private EstimateWorkflowService.ScanResult? _historicalScan;
        private EstimateResult? _estimateResult;
        private CatalogSnapshot? _catalog;
        private List<DeliveryFinding> _catalogFindings = new();
        private List<MappingProposal> _proposals = new();
        /// <summary>L05 per-group project-rule reviews of the active scan (by rule key); cleared and rebuilt with the proposals.</summary>
        private Dictionary<string, EstimateWorkflowService.ProjectRuleReview> _projectRuleReviews = new(StringComparer.Ordinal);
        /// <summary>L05: the context the reviews were made from; the mapping assistant re-proves it before and after it runs.</summary>
        private EstimateWorkflowService.ProjectRuleContext? _projectRuleContext;

        private void ClearProjectRules()
        {
            _projectRuleReviews.Clear();
            _projectRuleContext = null;
        }
        private bool _previewShown;

        /// <summary>
        /// Identity of the drawing the current results belong to. Switching drawings
        /// must invalidate everything — a plan from another DWG must never look like
        /// it belongs to the one on screen.
        /// </summary>
        private string? _sectionResultsDrawing;
        private string? _estimateResultsDrawing;
        private string? _lastEstimateFreshnessReason;

        private readonly ObservableCollection<SectionRowViewModel> _sectionRows = new();
        private readonly NonReentrantExecution _sectionRowRefresh = new();
        private readonly NonReentrantExecution _gateRefresh = new();
        private readonly ObservableCollection<QuantityRowViewModel> _quantityRows = new();
        private readonly Dictionary<string, DeliveryStatus> _sectionDisplayStatuses =
            new(StringComparer.Ordinal);
        private readonly StringBuilder _activity = new();

        public CivilDeliveryControl()
        {
            InitializeComponent();
#if MAHOD_CD_STANDALONE && MAHOD_CD_GUIDE
            // Only a build that stages the approved guide PDF shows the button (review GUIDE-1).
            BtnUserGuide.Visibility = Visibility.Visible;
#endif
            SectionSurfaceLegend.Text = SectionSurfaceLegendPresentation.Legend;
            SectionSurfaceLegend.ToolTip = SectionSurfaceLegendPresentation.Help;
            LoadBrandLogo();
            SectionsGrid.ItemsSource = _sectionRows;
            QuantitiesGrid.ItemsSource = _quantityRows;
            InitializeQuantityReviewFilter();
            HookApprover();
            // A decision saved from the MahodAI chat changes the profile file under the palette. Subscribed
            // while shown only (Loaded fires again on every re-show, so never twice), so a closed palette
            // is not kept alive by the static event.
            Loaded += (_, _) =>
            {
                ProjectProfileWriter.Saved -= OnProfileSavedElsewhere;
                ProjectProfileWriter.Saved += OnProfileSavedElsewhere;
            };
            Unloaded += (_, _) => ProjectProfileWriter.Saved -= OnProfileSavedElsewhere;

            Loaded += (_, _) =>
            {
                var loadTimer = System.Diagnostics.Stopwatch.StartNew();
                HookDocumentEvents();
                ReloadProfile();
                RefreshDrawingLabel();
                RefreshDashboard();
                RefreshGates();
                // How long the panel waits before it can paint (live 30.09.2026: several seconds on 6422).
                MahodAI.Civil3D.Plugin.Utilities.MahodLogger.Info($"palette load {loadTimer.ElapsedMilliseconds} ms");
                // By now AutoCAD has restored whatever palette size it saved last session.
                // A 225 px palette makes both tables unreadable, so grow it back once.
                CivilDeliveryPalette.EnsureUsableSize();

                // Updates are shown only once Mahod actually publishes a channel. A button
                // whose only outcome is "not configured" reads as an unfinished product, so
                // it stays out of the panel until there is something for it to reach.
                BtnCheckUpdates.Visibility = UpdateService.ReadChannel() == null
                    ? System.Windows.Visibility.Collapsed
                    : System.Windows.Visibility.Visible;
            };
        }

        // ------------------------------------------------------------- branding

        /// <summary>
        /// Loads the official white Mahod logo from the embedded resource. No local
        /// file path is ever consulted, so the product looks right on any machine.
        /// </summary>
        private void LoadBrandLogo()
        {
            try
            {
                using var stream = typeof(CivilDeliveryControl).Assembly
                    .GetManifestResourceStream("MahodAI.Civil3D.Plugin.assets.mahod_logo_white.png");
                if (stream == null) return;

                var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                buffer.Position = 0;

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = buffer;
                image.EndInit();
                image.Freeze();
                LogoImage.Source = image;
            }
            catch
            {
                // A missing logo must never break the workflow.
            }
        }

        // --------------------------------------------------- document awareness

        private void HookDocumentEvents()
        {
            try
            {
                // Loaded can occur more than once for a docked/hidden palette.
                // Named remove/add handlers prevent multiplying native callbacks.
                var manager = AcadApp.DocumentManager;
                manager.DocumentActivated -= OnObservedDocumentActivated;
                manager.DocumentToBeDeactivated -= OnObservedDocumentDeactivating;
                manager.DocumentToBeDestroyed -= OnObservedDocumentDestroying;
                manager.DocumentActivated += OnObservedDocumentActivated;
                manager.DocumentToBeDeactivated += OnObservedDocumentDeactivating;
                manager.DocumentToBeDestroyed += OnObservedDocumentDestroying;
                HookDrawingSaveContext();
            }
            catch { }
        }

        private static string? CurrentDrawing()
        {
            // Display the active document, never the DWT retained by its database.
            // Source identity is captured separately by DrawingRevisionTracker.
            try { return AcadApp.DocumentManager.MdiActiveDocument?.Name; }
            catch { return null; }
        }

        /// <summary>
        /// Results belong to one drawing. On a switch they are discarded rather than
        /// left on screen looking current — a stale Apply/Show against a different DWG
        /// is exactly the cross-drawing mistake this guards.
        /// </summary>
        private void OnDrawingChanged()
        {
            var activeDocument = Doc();
            HookDrawingSaveContext();
            if (_pendingWorkflowSaveDocument != null &&
                !ReferenceEquals(_pendingWorkflowSaveDocument, activeDocument))
                CancelWorkflowSave(_pendingWorkflowSaveDocument,
                    "השרטוט הוחלף — הפעולה הממתינה בוטלה");
            var sectionNow = activeDocument == null
                ? null : DrawingScopeIdentity.For(activeDocument);
            var estimateNow = activeDocument == null
                ? null : EstimateWorkflowService.DrawingIdentity(activeDocument);
            var sectionChanged = _sectionResultsDrawing != null &&
                                 !string.Equals(sectionNow, _sectionResultsDrawing,
                                     StringComparison.OrdinalIgnoreCase);
            var estimateChanged = _estimateResultsDrawing != null &&
                                  !string.Equals(estimateNow, _estimateResultsDrawing,
                                      StringComparison.OrdinalIgnoreCase);
            if (sectionChanged)
            {
                var previewCleared = TryClearPreview(
                    "החלפת שרטוט", showDialog: false);
                if (previewCleared) _evidenceBlockingStatus = null;
                _plan = null;
                _apply = null;
                _applyPlanRunId = null;
                _lastVerifyResult = null;
                _verifySummary = null;
                _sectionDisplayStatuses.Clear();
                _sectionRows.Clear();
                _sectionResultsDrawing = null;
            }
            if (estimateChanged)
            {
                _scan = null;
                _historicalScan = null;
                _estimateResult = null;
                _catalog = null;
                _catalogFindings.Clear();
                _proposals.Clear();
                ClearProjectRules();
                _quantityRows.Clear();
                _estimateResultsDrawing = null;
                _lastEstimateFreshnessReason = null;
                QuantityDetail.Text = "השרטוט הוחלף — אין מדידות מהשרטוט הקודם בתצוגה";
            }
            // An export notice names the previous drawing's files and runs (e.g. a 6422 bill); it is scoped to the
            // drawing that wrote it, with or without a scan (a corridor export has none).
            if (_exportNotice.ClearUnlessOwnedBy(estimateNow))
                MeasurementDraftNotice.Text = string.Empty;
            if (sectionChanged || estimateChanged)
            {
                Log("השרטוט הוחלף — התוצאות הקודמות בוטלו.");
                SetStatus("השרטוט הוחלף — יש להריץ תכנון מחדש");
            }
            RefreshDrawingLabel();
            ReloadProfile();
            RefreshDashboard();
            RefreshGates();
        }

        // ------------------------------------------------------------ dashboard

        /// <summary>
        /// Fills the "this drawing" card the moment the palette opens: what the tool
        /// already understands before a single click. Any failure shows as text - the
        /// dashboard never blocks the workflow.
        /// </summary>
        private void RefreshDashboard()
        {
            try
            {
                var doc = Doc();
                DashboardTitle.Text = doc == null
                    ? "השרטוט הזה — אין שרטוט פתוח"
                    : "השרטוט הזה — " + Ltr(Path.GetFileName(doc.Name));
                if (doc == null || _profile == null)
                {
                    DashboardBody.Text = doc == null ? "אין שרטוט פתוח" : "אין פרופיל פרויקט";
                    return;
                }
                var civilDoc = CivilDocument.GetCivilDocument(doc.Database);
                var dash = new ProjectDashboardService().Build(doc.Database, civilDoc, _profile)
                    with { DrawingName = Path.GetFileName(doc.Name) };
                DashboardTitle.Text = "השרטוט הזה — " + Ltr(dash.DrawingName);
                DashboardBody.Text = dash.Summary();
            }
            catch (Exception ex)
            {
                DashboardBody.Text = "לא ניתן לקרוא את השרטוט: " + ex.Message;
            }
        }

        private void RefreshDrawingLabel()
        {
            var f = CurrentDrawing();
            DrawingLabel.Text = "שרטוט: " + (string.IsNullOrEmpty(f) ? "—" : Ltr(Path.GetFileName(f)));
        }

        /// <summary>True when results were produced against a different drawing than the active one.</summary>
        private bool PlanIsStale()
        {
            if (_plan == null) return false;
            var doc = Doc();
            if (doc == null || _profile == null) return true;
            if (!string.Equals(_sectionResultsDrawing, DrawingScopeIdentity.For(doc),
                    StringComparison.OrdinalIgnoreCase)) return true;

            // A green row is evidence about one exact live database revision and the
            // exact CL/XREF bytes captured by PLAN, not only about a filename/profile
            // pair.  Use the post-commit APPLY revision once that result is
            // authoritative; before APPLY, the immutable PLAN revision is the correct
            // baseline.  RefreshGates calls this on every UI interaction, so an
            // external source changed on disk withdraws old green immediately rather
            // than waiting for the next APPLY/VERIFY click.
            var expectedRevision = IsAuthoritativeVerify(_lastVerifyResult) &&
                                   !string.IsNullOrWhiteSpace(_lastVerifyResult!.VerifiedDatabaseRevision)
                ? _lastVerifyResult.VerifiedDatabaseRevision
                : IsAuthoritativeApply(_apply) && _applyPlanRunId == _plan.RunId
                    ? _apply!.PostApplyDatabaseRevision
                    : _plan.SourceDatabaseRevision;
            try
            {
                var currentProfile = ActiveProjectProfileService.ReloadForExistingWorkflow(
                    doc, _sections, _plan.ProjectProfileId,
                    _profileSource, _profileWriteTarget);
                if (!currentProfile.IsUsable || currentProfile.Profile == null ||
                    string.IsNullOrWhiteSpace(currentProfile.ProfileHash) ||
                    SectionPlanLogic.ScopeStaleReason(
                        _plan, DrawingScopeIdentity.For(doc), currentProfile.Profile,
                        currentProfile.ProfileHash) != null)
                    return true;
                if (SectionInputIntegrityService.StaleReason(
                        doc.Database, _plan, expectedRevision, "UI-GATE", currentProfile.Profile) != null)
                    return true;

                // Green is also a claim about a complete, untampered evidence chain.
                // A run folder can be deleted or edited after publication; re-prove
                // each artifact that currently contributes a green display.
                SectionsWorkflowService.RequirePlanEvidence(_plan);
                if (IsAuthoritativeApply(_apply))
                    SectionsWorkflowService.RequireApplyEvidence(_apply!);
                if (_verifySummary != null)
                {
                    // A published failed VERIFY is current negative evidence, not
                    // stale PLAN input. Keep Failed visible; only green uses the
                    // stronger IsAuthoritativeVerify predicate.
                    if (_lastVerifyResult == null || EvidenceWriteFailed(_lastVerifyResult.Findings)) return true;
                    RuntimeRunManifestService.RequirePublishedArtifact(
                        _lastVerifyResult!.RunId,
                        "verify_result.json",
                        _lastVerifyResult,
                        SectionsWorkflowService.Json,
                        "sections",
                        "verify",
                        "verify-selected");
                }
                return false;
            }
            catch
            {
                // Unreadable host/XREF evidence is not permission to keep a green result.
                return true;
            }
        }

        /// <summary>
        /// Revalidates the complete Section PLAN after a modal engineering-decision
        /// dialog and immediately before mutating the durable profile.  A dialog can
        /// stay open while the active drawing, its revision, an XREF, or the profile
        /// changes; no decision captured from that stale evidence may be persisted.
        /// </summary>
        private ActiveProjectProfileService.ActiveLoadResult RequireFreshSectionPlan(
            string stage)
        {
            var doc = Doc() ?? throw new InvalidOperationException(
                "אין שרטוט פעיל — לא ניתן לשמור הכרעת חתך.");
            if (_plan == null || _profile == null)
                throw new InvalidOperationException(
                    "אין תכנון חתכים פעיל — יש להריץ תכנון מחדש.");

            var current = ActiveProjectProfileService.ReloadForExistingWorkflow(
                doc, _sections, _plan.ProjectProfileId,
                _profileSource, _profileWriteTarget);
            if (!current.IsUsable || current.Profile == null ||
                string.IsNullOrWhiteSpace(current.ProfileHash))
                throw new InvalidOperationException(
                    "לא ניתן לאמת את פרופיל הפרויקט הנוכחי — יש לטעון פרופיל תקין ולהריץ תכנון מחדש.");
            var scopeReason = SectionPlanLogic.ScopeStaleReason(
                _plan, DrawingScopeIdentity.For(doc), current.Profile, current.ProfileHash);
            if (scopeReason != null ||
                !string.Equals(_profileHash, current.ProfileHash, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    scopeReason ?? "פרופיל הפרויקט השתנה בזמן ההכרעה — יש להריץ תכנון מחדש.");

            // VERIFY-SELECTED-CURRENT is an explicit new validation, not a stale
            // decision/save or a revision waiver. Its service refreshes PLAN and
            // proves exact producer identity plus live source/output geometry.
            if (stage == "VERIFY-SELECTED-CURRENT") return current;
            var expectedRevision = stage.StartsWith("VERIFY", StringComparison.Ordinal) &&
                                   IsAuthoritativeApply(_apply)
                ? _apply!.PostApplyDatabaseRevision : _plan.SourceDatabaseRevision;
            var integrityReason = SectionInputIntegrityService.StaleReason(
                doc.Database, _plan, expectedRevision, stage, current.Profile);
            if (integrityReason != null)
                throw new InvalidOperationException(
                    "מקורות החתך השתנו בזמן ההכרעה — יש להריץ תכנון מחדש. " + integrityReason);
            return current;
        }

        /// <summary>
        /// Cheap gate-time freshness check. The scan itself already verified host and
        /// XREF hashes. Row selection only compares the tracked live database revision,
        /// drawing identity and in-memory profile identity; full file hashing is done
        /// immediately before an evidence-consuming action.
        /// </summary>
        private bool EstimateScanIsKnownStale()
        {
            if (_scan == null) return false;
            if (_lastEstimateFreshnessReason != null) return true;
            var doc = Doc();
            if (doc == null)
            {
                _lastEstimateFreshnessReason = "אין שרטוט פעיל";
                return true;
            }
            string revision;
            try { revision = DrawingRevisionTracker.Capture(doc.Database); }
            catch (Exception ex)
            {
                _lastEstimateFreshnessReason =
                    "לא ניתן לאמת את גרסת מסד השרטוט: " + ex.Message;
                return true;
            }
            _lastEstimateFreshnessReason = _scan.StaleReason(
                EstimateWorkflowService.DrawingIdentity(doc),
                _profile?.ProfileId,
                _profileHash,
                revision);
            return _lastEstimateFreshnessReason != null;
        }

        private bool VerifyEstimateSourcesForAction(Document doc, string operation)
        {
            if (_scan == null) return false;
            var reason = EstimateWorkflowService.FreshnessReason(doc, _scan);
            if (reason == null)
            {
                _lastEstimateFreshnessReason = null;
                return true;
            }

            _lastEstimateFreshnessReason = reason;
            SetStatus(operation + " נעצרה — הסריקה אינה עדכנית");
            RtlMessageBox.Show(
                reason + "\n\nיש להריץ סריקת כמויות חדשה.",
                operation + " נעצרה",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            RefreshGates();
            return false;
        }

        // ------------------------------------------------------------- plumbing

        private System.Windows.Media.Brush? _statusBrush;
        private string? _evidenceBlockingStatus;
        private string? _previewCleanupBlockingStatus;

        /// <summary>
        /// Reloads when a save that was not ours (a chat decision) changed our profile file. The palette's own
        /// saves read back through <see cref="PublishSavedProfile"/> first, so by the time this runs on the UI
        /// thread their hash already matches and nothing happens.
        /// </summary>
        private void OnProfileSavedElsewhere(ProjectProfileWriter.SaveResult saved)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(_profileWriteTarget) ||
                        !string.Equals(System.IO.Path.GetFullPath(_profileWriteTarget), System.IO.Path.GetFullPath(saved.Path),
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(_profileHash, saved.NewHash, StringComparison.Ordinal))
                        return;
                    ReloadProfile();
                    RefreshDashboard();
                    RefreshGates();
                    SetStatus($"פרופיל הפרויקט עודכן מהצ'אט (גרסה {saved.NewVersion}) — הנתונים נטענו מחדש");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Civil Delivery profile reload: " + ex.Message);
                }
            }));
        }

        private void SetStatus(string s)
        {
            // Evidence publication happens after the Civil transaction commits.  If
            // that publication failed, a later selection/refresh/status update must
            // not repaint the palette as healthy while the committed drawing still
            // has no authoritative run artifact.
            if (_evidenceBlockingStatus != null || _previewCleanupBlockingStatus != null)
            {
                StatusLabel.Text = _evidenceBlockingStatus ?? _previewCleanupBlockingStatus;
                StatusLabel.Foreground = System.Windows.Media.Brushes.Red;
                StatusLabel.FontWeight = FontWeights.Bold;
                return;
            }

            StatusLabel.Text = s;
            if (_statusBrush != null) StatusLabel.Foreground = _statusBrush;
            StatusLabel.FontWeight = FontWeights.Normal;
        }

        /// <summary>Red, bold status for a state that blocks delivery — never a ✓.</summary>
        private void SetBlockingStatus(string s)
        {
            _statusBrush ??= StatusLabel.Foreground;
            _evidenceBlockingStatus = s;
            StatusLabel.Text = s;
            StatusLabel.Foreground = System.Windows.Media.Brushes.Red;
            StatusLabel.FontWeight = FontWeights.Bold;
        }

        /// <summary>
        /// A deliberate APPLY/VERIFY retry is allowed to replace the previous evidence
        /// blocker. Incidental status changes are not. The attempt will install a fresh
        /// blocker again if its own evidence publication fails.
        /// </summary>
        private void BeginEvidenceAttempt()
        {
            _evidenceBlockingStatus = null;
            if (_statusBrush != null) StatusLabel.Foreground = _statusBrush;
            StatusLabel.FontWeight = FontWeights.Normal;
        }

        /// <summary>
        /// A VERIFY retry may not borrow the previous retry's green rows while the new
        /// read-back is running or after it throws. Restore only the authoritative
        /// APPLY baseline; a stale-input invalidation passes false and removes every
        /// post-PLAN display override.
        /// </summary>
        private void ResetDisplayedSectionVerification(
            bool restoreApplyBaseline,
            string? preferredRecordId = null)
        {
            _verifySummary = null;
            _lastVerifyResult = null;
            _sectionDisplayStatuses.Clear();
            if (restoreApplyBaseline && IsAuthoritativeApply(_apply))
            {
                foreach (var record in _apply!.Records)
                    _sectionDisplayStatuses[record.RecordId] = record.Status;
            }
            RebuildSectionRows(preferredRecordId);
        }

        /// <summary>
        /// A new APPLY attempt replaces the previous APPLY/VERIFY evidence.  Clear it
        /// before entering Civil and again on failure so a thrown retry cannot leave
        /// the old green rows or VERIFY gate looking like the result of this attempt.
        /// PLAN remains visible because it is the immutable input being applied.
        /// </summary>
        private void ResetDisplayedSectionApplyAttempt(string? preferredRecordId = null)
        {
            _apply = null;
            _verifySummary = null;
            _lastVerifyResult = null;
            _sectionDisplayStatuses.Clear();
            RebuildSectionRows(preferredRecordId);
        }

        /// <summary>
        /// PLAN is also a replacement operation.  Once a fresh PLAN is requested, no
        /// result from the previous PLAN/APPLY/VERIFY chain may remain displayed if
        /// the new Civil traversal throws or is cancelled.
        /// </summary>
        private void ResetDisplayedSectionPlanAttempt()
        {
            _plan = null;
            _apply = null;
            _verifySummary = null;
            _lastVerifyResult = null;
            _sectionDisplayStatuses.Clear();
            _sectionRows.Clear();
            _sectionResultsDrawing = null;
        }

        // Called only after the explicit profile-selection dialog was confirmed.
        // A different project profile must not inherit old actionable output or
        // marked history. No DWG, source, price or saved profile is changed here.
        private bool ResetForExplicitProjectProfileSelection()
        {
            if (!TryClearPreview("בחירת פרופיל פרויקט")) return false;
            CancelWorkflowSave(_pendingWorkflowSaveDocument,
                "נבחר פרופיל אחר — הפעולה הממתינה בוטלה");
            ResetDisplayedSectionPlanAttempt();
            _evidenceBlockingStatus = null;
            _scan = null;
            _historicalScan = null;
            _estimateResult = null;
            _catalog = null;
            _catalogFindings.Clear();
            _proposals.Clear();
            ClearProjectRules();
            _quantityRows.Clear();
            _estimateResultsDrawing = null;
            _lastEstimateFreshnessReason = null;
            _reviewScanIdentity = null;
            _reviewResultIdentity = null;
            _reviewCatalogIdentity = null;
            _reviewIssues = null;
            _exportNotice.Clear();
            MeasurementDraftNotice.Text = string.Empty;
            QuantityDetail.Text = "פרופיל הפרויקט הוחלף — יש לסרוק כמויות לפי המקורות שנבחרו";
            SectionDetail.Text = "פרופיל הפרויקט הוחלף — יש לתכנן מחדש לפני יצירה או אימות";
            return true;
        }

        private static DeliveryFinding? EvidenceWriteFailure(IEnumerable<DeliveryFinding>? findings) =>
            findings?.FirstOrDefault(f => string.Equals(
                f.Code, SectionFindingCodes.EvidenceWriteFailed, StringComparison.Ordinal));

        private static bool EvidenceWriteFailed(IEnumerable<DeliveryFinding>? findings) =>
            EvidenceWriteFailure(findings) != null;

        private static bool HasErrorFindings(IEnumerable<DeliveryFinding>? findings) =>
            findings?.Any(finding => finding.Severity == FindingSeverity.Error) == true;

        private static bool IsAuthoritativeApply(SectionApplyResult? apply) =>
            apply != null &&
            apply.Committed &&
            apply.Status is DeliveryStatus.Applied or DeliveryStatus.Verified &&
            !EvidenceWriteFailed(apply.Findings) &&
            !EvidenceWriteFailed(apply.Records.SelectMany(record => record.Findings)) &&
            !HasErrorFindings(apply.Findings) &&
            !HasErrorFindings(apply.Records.SelectMany(record => record.Findings)) &&
            apply.Records.Count > 0 &&
            apply.Records.All(record =>
                record.Status is DeliveryStatus.Applied or DeliveryStatus.Verified);

        private static bool IsAuthoritativeVerify(SectionVerifyResult? verify) =>
            verify != null &&
            verify.Status == DeliveryStatus.Verified &&
            !EvidenceWriteFailed(verify.Findings) &&
            !HasErrorFindings(verify.Findings) &&
            verify.Records.Count > 0 &&
            verify.Records.All(record =>
                record.Status == DeliveryStatus.Verified &&
                record.Checks.All(check => check.Pass));

        /// <summary>
        /// An evidence write failure after commit means the drawing changed but the run
        /// folder does not prove it. The result stays in memory, the status turns red,
        /// VERIFY is gated off (see RefreshGates) and the operator is told what to do.
        /// Returns true when the stage is blocked.
        /// </summary>
        private bool ReportEvidenceWriteFailure(string stage, IEnumerable<DeliveryFinding>? findings)
        {
            var failure = EvidenceWriteFailure(findings);
            if (failure == null) return false;
            Log($"  ✗ {stage}: כתיבת הראיות לתיקיית הריצה נכשלה — {failure.Message}");
            Log("  ✗ ללא ראיות אין אימות ואין מסירה. בדקי מקום פנוי/הרשאות בתיקיית הפרויקט והריצי מחדש.");
            SetBlockingStatus($"✗ {stage}: הראיות לא נשמרו — חסום למסירה");
            RtlMessageBox.Show(
                $"{stage} הסתיים, אך כתיבת קובצי הראיות לתיקיית הריצה נכשלה:\n{failure.Message}\n\n" +
                "השרטוט עשוי להכיל את השינוי, אך ללא ראיות אין אימות ואין מסירה.\n" +
                "בדקי מקום פנוי והרשאות בתיקיית הפרויקט והריצי את הפעולה מחדש.",
                "Mahod Civil Delivery — ראיות לא נשמרו", MessageBoxButton.OK, MessageBoxImage.Error);
            return true;
        }

        /// <summary>
        /// The palette's loader. Work runs synchronously on the UI thread (Civil
        /// document operations must), so the overlay is pushed to the screen with an
        /// explicit render flush BEFORE the work starts, and BusyStep() repaints the
        /// stage text between coarse steps.
        /// </summary>
        private BusyProgressWindow? _busyProgress;
        private string _busyMessage = string.Empty;

        private void RunBusy(string message, Action work)
        {
            BusyText.Text = message;
            _busyMessage = message;
            BusyOverlay.Visibility = Visibility.Visible;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            // Civil work runs synchronously on this thread, so the overlay above cannot
            // animate ("the loader looked stuck"). The progress window lives on its own
            // thread and shows every StageLog stage as it begins.
            _busyProgress = BusyProgressWindow.Show("Mahod Civil Delivery", message);
            void OnStage(string stage, string? detail) =>
                _busyProgress?.Update(StageLabel(stage, detail));
            StageLog.StageObserver += OnStage;
            try
            {
                FlushRender();
                work();
            }
            finally
            {
                StageLog.StageObserver -= OnStage;
                _busyProgress?.Dispose();
                _busyProgress = null;
                System.Windows.Input.Mouse.OverrideCursor = null;
                BusyOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void BusyStep(string message)
        {
            BusyText.Text = message;
            _busyProgress?.Update(message);
            FlushRender();
        }

        /// <summary>
        /// Hebrew label for a StageLog stage id. The operator never sees an internal id
        /// (live 29/09: the PLAN overlay showed "project.collect" for a minute); an
        /// unknown stage falls back to its family's Hebrew label.
        /// </summary>
        internal static string StageLabel(string stage, string? detail)
        {
            var text = stage switch
            {
                "workflow.plan.start_transaction" => "פותח את השרטוט לקריאה",
                "workflow.plan.get_civil_document" or "workflow.apply.get_civil_document"
                    or "workflow.apply-selected.get_civil_document" => "ניגש למסמך Civil",
                "plan.read_cl" => "קורא את קווי ה-CL",
                "cl.hash_drawing" => "מחשב חתימה לשרטוט",
                "cl.scan_modelspace" => "סורק את השרטוט וה-XREF",
                "cl.external_file" => "קורא את קובץ ה-CL החיצוני",
                "cl.build_records" => "בונה רשומות חתך",
                "cl.attach_labels" => "מצמיד תוויות תחנה",
                "project.collect" => "אוסף את גאומטריית התכנון (קווים, אבנים, סימון)",
                "traffic-arrows.collect" => "אוסף חיצי נתיב מהשרטוט",
                "plan.scan_owned_objects" => "בודק חתכים קיימים של הכלי",
                "plan.scan_manual_sections" => "בודק חתכים ידניים",
                "plan.list_surfaces" => "קורא משטחים",
                "plan.list_corridors" => "קורא מסדרונות",
                "plan.list_pipe_networks" => "קורא רשתות צנרת",
                "plan.discover_utilities" => "מאתר מערכות תשתית",
                "plan.find_crossings" => "מחשב חיתוכים עם תוואים",
                "plan.layout" => "מתכנן פריסת חתכים",
                "apply.section_sources.plan" => "מתכנן מקורות דגימה",
                "apply.lock_document" or "apply-selected.lock_document" => "נועל את השרטוט לעריכה",
                "apply.start_transaction" or "apply-selected.start_transaction" => "פותח עסקת עריכה",
                "apply.remove_previous_tool_objects" => "מסיר את גרסת החתך הקודמת של הכלי",
                "apply.find_or_create_group" => "מכין קבוצת קווי דגימה",
                "apply.sampleline_create" => "יוצר קו דגימה",
                "apply.section_sources" => "מגדיר מקורות דגימה",
                "apply.enable_sources" => "מפעיל מקורות דגימה",
                "apply.sectionview_create" => "יוצר תצוגת חתך",
                "apply.apply_style" => "מחיל סגנון חתך",
                "apply.view_dress" => "מסדר את תצוגת החתך",
                "apply.decorate" => "מוסיף תוויות, מידות ובלוקים",
                "decorate.vehicle_block.import" => "טוען בלוק רכב משרדי",
                "decorate.traffic_arrow.import" => "טוען חץ כיוון משרדי",
                "apply.commit_transaction" or "apply-selected.commit_transaction" => "שומר את השינוי בשרטוט",
                "apply.abort_transaction" or "apply-selected.abort_transaction" => "מבטל את השינוי",
                "workflow.preview.sample_surfaces" => "דוגם משטחים לתצוגה מקדימה",
                "estimate.start_transaction" => "פותח את השרטוט לסריקת כמויות",
                "estimate.discovery_scan" => "סורק אובייקטים לכתב הכמויות",
                _ when stage.StartsWith("resolver.", StringComparison.Ordinal) => "מחשב חיתוך תוואי",
                _ when stage.StartsWith("apply", StringComparison.Ordinal) => "מחיל את החתך",
                _ when stage.StartsWith("decorate.", StringComparison.Ordinal) => "מוסיף עיטורי חתך",
                _ when stage.StartsWith("workflow.verify", StringComparison.Ordinal) => "מאמת מול המודל",
                _ when stage.StartsWith("estimate.", StringComparison.Ordinal) => "סורק כמויות",
                _ when stage.StartsWith("setup.", StringComparison.Ordinal) => "סורק את הגדרות הפרויקט",
                _ when stage.StartsWith("discover.", StringComparison.Ordinal) => "סורק את מבנה השרטוט",
                _ => "מעבד…",
            };
            if (string.IsNullOrWhiteSpace(detail) || detail!.Length > 60) return text;
            // "alignment=600" / "candidates=28" are log keys; the operator sees the value only.
            var keyed = System.Text.RegularExpressions.Regex.Match(detail, @"^[a-z_]+=(.+)$");
            return text + " · " + (keyed.Success ? keyed.Groups[1].Value : detail);
        }

        private void FlushRender()
        {
            try
            {
                Dispatcher.Invoke(() => { },
                    System.Windows.Threading.DispatcherPriority.Render);
            }
            catch { }
        }

        /// <summary>Newest lines are kept; the palette log is a window, not an archive.</summary>
        internal const int MaxActivityChars = 200_000;

        private void Log(string message)
        {
            _activity.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            if (_activity.Length > MaxActivityChars) _activity.Length = MaxActivityChars;
            ActivityLog.Text = _activity.ToString();
        }

        /// <summary>At most a few lines per finding code; the rest is one counted line.</summary>
        internal const int MaxLoggedFindingsPerCode = 5;

        /// <summary>
        /// The full 6422 working copy measured 74,900 records and 75,321 blocking
        /// findings (74,900 EST-UNMAPPED). One Log() per finding rebuilt a growing
        /// 9 MB TextBox 75,000 times on the UI thread: Civil stopped responding for
        /// hours after the scan (live 07/09 18:56). The complete list stays in
        /// quantity_preflight.json and in the grid; the log shows a bounded digest.
        /// </summary>
        private void LogFindingsBounded(IReadOnlyList<DeliveryFinding> findings)
        {
            foreach (var group in findings.GroupBy(f => f.Code ?? "?", StringComparer.Ordinal))
            {
                var items = group.ToList();
                foreach (var finding in items.Take(MaxLoggedFindingsPerCode))
                    Log(Bidi.FindingLine(finding));
                if (items.Count > MaxLoggedFindingsPerCode)
                    Log($"{group.Key}: עוד {items.Count - MaxLoggedFindingsPerCode} ממצאים מאותו סוג " +
                        $"(סה\"כ {items.Count}) — הרשימה המלאה בטבלה וב-quantity_preflight.json");
            }
        }

        private void ReloadProfile()
        {
            var doc = Doc();
            if (doc == null)
            {
                _profile = null;
                _profileHash = null;
                _profileSource = null;
                _profileWriteTarget = null;
                _profileWriteState = null;
                ProfileSummary.Text = "אין שרטוט פעיל.";
                CatalogSummary.Text = "—";
                EarthworksDecisionState.Text = "אין פרופיל פרויקט";
                RefreshPriceBookCombo();
                return;
            }

            var loaded = new ActiveProjectProfileService().LoadForDocument(doc, _sections);
            _profile = ActiveProjectProfileService.SelectUsableProfile(loaded);
            _profileHash = _profile == null ? null : loaded.ProfileHash;
            _profileSource = loaded.ProfileSource;
            _profileWriteTarget = loaded.ProfileWriteTarget;
            _profileWriteState = loaded.ProfileWriteState;

            if (_profile == null)
            {
                // A parsed-but-invalid profile is not merely a banner condition. It
                // invalidates every prior result so no row, estimate or green status
                // from the last valid version remains visible/actionable.
                TryClearPreview("ביטול תוצאות מפרופיל לא תקין", showDialog: false);
                _plan = null;
                _apply = null;
                _verifySummary = null;
                _sectionResultsDrawing = null;
                _sectionDisplayStatuses.Clear();
                _sectionRows.Clear();
                InvalidateEstimateEvidence("פרופיל לא תקין — המדידות הקודמות אינן ראיה עדכנית");
                var errors = loaded.ErrorFindings.ToList();
                ProjectLine.FlowDirection = System.Windows.FlowDirection.RightToLeft;
                ProjectLine.Text = loaded.Profile == null
                    ? "פרופיל הפרויקט לא נמצא"
                    : $"פרופיל {loaded.Profile.ProfileId} — לא תקין";
                ProfileSummary.Text = (loaded.Profile == null
                        ? "הפרופיל לא נמצא."
                        : "הפרופיל נקרא אך אינו תקין — כל תהליכי החתכים והאומדן חסומים.") +
                    "\n" + string.Join("\n", (errors.Count > 0 ? errors : loaded.Findings)
                        .Select(Bidi.FindingLine));
                CatalogSummary.Text = "—";
                EarthworksDecisionState.Text = "פרופיל לא תקין — האומדן חסום";
                RefreshPriceBookCombo();
                return;
            }

            var needsSetup = ProjectSetupService.NeedsSetup(_profile);
            ProjectLine.Text = $"פרויקט {_profile.ProfileId} — {_profile.ProjectName}";
            // A Hebrew-Latin project name (e.g. a drawing called "…-Q-כביש") keeps its order only in a left-to-right line.
            ProjectLine.FlowDirection = Bidi.MixesHebrewAndLatin(_profile.ProjectName) || Bidi.MixesHebrewAndLatin(_profile.ProfileId)
                ? System.Windows.FlowDirection.LeftToRight : System.Windows.FlowDirection.RightToLeft;
            ProfileSummary.Text =
                $"מזהה: {_profile.ProfileId}\n" +
                $"גרסת פרופיל: {_profile.Provenance.Version}\n" +
                $"אושר ע\"י: {_profile.Provenance.ApprovedBy ?? "—"}\n" +
                $"שכבות CL: {(_profile.Sections.Cl.LayerPatterns.Count == 0 ? "לא מוגדר" : string.Join(", ", _profile.Sections.Cl.LayerPatterns))}\n" +
                $"קובצי CL: {(_profile.Sections.Cl.SourceFiles.Count == 0 ? "השרטוט הפתוח" : Bidi.Ltr(string.Join(", ", _profile.Sections.Cl.SourceFiles)))}\n" +
                $"תוואים מותרים: {(_profile.Sections.Alignments.AllowedNames.Count == 0 ? "כולם" : string.Join(", ", _profile.Sections.Alignments.AllowedNames))}\n" +
                $"מקורות חתך: {(_profile.Sections.Sources.SampledSourceRules.Count == 0 ? "לא מוגדר" : string.Join(", ", _profile.Sections.Sources.SampledSourceRules.Select(r => r.Name)))}\n" +
                $"סבילות חיתוך: {_profile.Sections.Cl.IntersectionToleranceM?.ToString("F2") ?? "חיתוך מדויק"}\n" +
                (needsSetup ? "\n⚠ חתכים עדיין דורשים הגדרת CL ומקורות דגימה. לאומדן בלבד אפשר להמשיך בלשונית אומדן, ללא הגדרת חתכים." : "");

            var quantityRuleApprovals = EstimateWorkflowService.QuantityRuleApprovals(_profile);
            var earthworksDecision = EstimateWorkflowService.GetEarthworksDecision(_profile);
            EarthworksDecisionState.Text = earthworksDecision.IsResolved
                ? earthworksDecision.DisplayText
                : EstimateGuidedActionPolicy.EarthworksNotAssessed;
            CatalogSummary.Text =
                // Defaults shipped with the product are useful proposals, but only a
                // named rule with an approval timestamp is an engineering approval.
                $"מחירון: {_profile.Estimate.Catalog.CatalogVersion ?? "—"}\n" +
                $"קובץ: {_profile.Estimate.Catalog.CatalogFile ?? "—"}\n" +
                $"מקורות אומדן: {(EstimateWorkflowService.IsReviewedSourceScopeApproved(_profile) ? EstimateWorkflowService.SourceScopeDisplay(_profile) : "לא אושרו — הייצוא חסום")}\n" +
                earthworksDecision.DisplayText + "\n" +
                $"חוקי כמויות מאושרים: {quantityRuleApprovals.Approved}\n" +
                $"הצעות מיפוי לא מאושרות: {quantityRuleApprovals.Unapproved}\n" +
                $"מקדמים מאושרים: {_profile.Estimate.ApprovedAdjustments.Count}\n" +
                $"מקדמים במועמדות (לא מוחלים): {_profile.Estimate.CandidateAdjustments.Count}";
            RefreshPriceBookCombo();
        }

        private string RequireProfileWriteTarget() =>
            !string.IsNullOrWhiteSpace(_profileWriteTarget) &&
            Path.IsPathFullyQualified(_profileWriteTarget)
                ? _profileWriteTarget!
                : throw new InvalidOperationException(
                    "לא נקבע יעד כתיבה לפרופיל הפעיל — הפעולה נחסמה כדי לא לכתוב לקובץ אחר.");

        private ProjectProfileWriter.ExpectedProfileState CaptureExpectedProfileState()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument ??
                      throw new InvalidOperationException("אין שרטוט פעיל");
            ProjectSetupService.CaptureReadySource(
                doc, "שמירת החלטה בפרופיל הפרויקט");
            var profile = _profile ?? throw new InvalidOperationException(
                "פרופיל הפרויקט שנקרא בתחילת העבודה אינו זמין");
            var expected = _profileWriteState ?? throw new InvalidOperationException(
                "ראיית מקור/יעד הפרופיל מתחילת העבודה חסרה — יש לטעון מחדש");
            ProjectProfileWriter.RequireExpectedStateUnchanged(profile, expected);
            return expected;
        }

        private static ProjectProfile CloneProfileForDecision(ProjectProfile source) =>
            JsonSerializer.Deserialize<ProjectProfile>(
                JsonSerializer.Serialize(source, SectionsWorkflowService.Json),
                SectionsWorkflowService.Json)
            ?? throw new InvalidOperationException(
                "לא ניתן להכין עותק עבודה מבודד של פרופיל הפרויקט.");

        private static ProjectProfileWriter.ExpectedProfileState
            CaptureExpectedProfileState(
                ActiveProjectProfileService.ActiveLoadResult current)
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument ??
                      throw new InvalidOperationException("אין שרטוט פעיל");
            ProjectSetupService.CaptureReadySource(
                doc, "שמירת החלטה בפרופיל הפרויקט");
            var profile = current.Profile ?? throw new InvalidOperationException(
                "פרופיל הפרויקט הפעיל אינו זמין");
            var expected = current.ProfileWriteState ?? throw new InvalidOperationException(
                "ראיית מקור/יעד הפרופיל מתחילת העבודה חסרה — יש לטעון מחדש");
            ProjectProfileWriter.RequireExpectedStateUnchanged(profile, expected);
            return expected;
        }

        private void PublishSavedProfile(ProjectProfileWriter.SaveResult saved)
        {
            ReloadProfile();
            if (_profile == null ||
                !string.Equals(_profileHash, saved.NewHash, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "הפרופיל נשמר אך לא ניתן לקרוא בחזרה ולאמת את אותם בתים; התהליך נחסם.");
        }

        // ----------------------------------------------------------- price books

        private sealed class PriceBookChoice
        {
            public required string Id { get; init; }
            public required string Label { get; init; }
        }

        private bool _refreshingPriceBooks;

        /// <summary>"08/2025" from a long edition note; falls back to the id.</summary>
        private static string ShortEdition(string? edition, string? id)
        {
            if (!string.IsNullOrWhiteSpace(edition))
            {
                var m = System.Text.RegularExpressions.Regex.Match(edition, @"(0[1-9]|1[0-2])[/\-. ]?(20\d\d)");
                if (m.Success) return $"{m.Groups[1].Value}/{m.Groups[2].Value}";
                var y = System.Text.RegularExpressions.Regex.Match(edition, @"20\d\d");
                if (y.Success) return y.Value;
                return edition.Length > 28 ? edition[..28] + "…" : edition;
            }
            return id ?? "";
        }

        private void RefreshPriceBookCombo()
        {
            _refreshingPriceBooks = true;
            try { RefreshPriceBookCombo(PriceBookCombo, _profile); }
            finally { _refreshingPriceBooks = false; }
        }

        internal static void RefreshPriceBookCombo(System.Windows.Controls.ComboBox combo, ProjectProfile? profile)
        {
            if (profile == null)
            {
                combo.ItemsSource = null; combo.SelectedItem = null;
                return;
            }
            PriceBookRegistry.EnsureLegacyEntry(profile);
            var active = PriceBookRegistry.Active(profile);
            var items = profile.Estimate.PriceBooks
                .Select(b => new PriceBookChoice
                {
                    Id = b.Id ?? "",
                    // Two readings of the same workbook are separate entries (b15); the id and the reading make
                    // them distinguishable here and in the switch log (review S5-1).
                    Label = $"{b.Publisher ?? "?"} · {ShortEdition(b.Edition, b.Id)} · {(b.ItemCount is { } n ? n.ToString("N0") + " סעיפים" : "")}".Trim(' ', '·') +
                            $" · {Bidi.Ltr(b.Id ?? "")}" +
                            (b.Mapping is { } m ? $" · גיליון {m.SheetName} · מחיר={Bidi.Ltr(m.PriceColumn ?? "")}" : ""),
                })
                .ToList();
            combo.ItemsSource = items;
            combo.SelectedItem = items.FirstOrDefault(i => i.Id == active?.Id);
        }

        private void OnPriceBookChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshingPriceBooks || _profile == null) return;
            if (PriceBookCombo.SelectedItem is not PriceBookChoice choice) return;
            if (PriceBookRegistry.Active(_profile)?.Id == choice.Id) return;
            // b24 (Codex 13:02 §4): the profile, its target and CAS are fixed before the first window and checked after
            // it; a cancelled window or a changed context writes nothing.
            ProfileDecisionScope scope;
            try { scope = CaptureProfileDecisionScope("בחירת מחירון פעיל"); }
            catch (Exception ex) { ShowError("בחירת מחירון פעיל", ex); RefreshPriceBookCombo(); return; }
            // Choosing the active price book is a decision with a name; without one the combo returns.
            var approver = RequireApprover("בחירת מחירון פעיל");
            if (approver == null) { RefreshPriceBookCombo(); return; }

            try
            {
                RequireProfileDecisionScope(scope);
                var profileForSave = CloneProfileForDecision(scope.Profile);
                _estimate.SetActivePriceBook(
                    profileForSave, choice.Id, approver,
                    scope.ExpectedState.TargetPath, scope.ExpectedState);
                var savedProfile = ProjectProfileLoader.LoadFromFile(
                    scope.ExpectedState.TargetPath);
                if (!savedProfile.IsUsable || savedProfile.Profile == null ||
                    !CatalogIdentity.IsValidSha256(savedProfile.ProfileHash) ||
                    !string.Equals(
                        PriceBookRegistry.Active(savedProfile.Profile)?.Id,
                        choice.Id,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "המחירון נשמר אך הפרופיל לא נקרא בחזרה באופן מאומת.");
                // Mapping approvals are bound to the old snapshot identity. A scan
                // carrying those codes, its proposals and its estimate are all stale.
                InvalidateEstimateEvidence("המחירון הוחלף — יש להריץ סריקה חדשה");
                PublishPriceBookProfile(savedProfile, scope.ExpectedState.TargetPath);
                Log($"מחירון פעיל הוחלף ל-{choice.Label} · אושר ע\"י {approver}. יש להריץ סריקת כמויות חדשה.");
                SetStatus("המחירון הוחלף — יש להריץ סריקה חדשה");
            }
            catch (Exception ex)
            {
                InvalidateEstimateEvidence(
                    "החלפת המחירון לא הושלמה באופן מאומת — יש לסרוק מחדש");
                ReloadProfile();
                RtlMessageBox.Show(ex.Message, "החלפת מחירון נכשלה", MessageBoxButton.OK, MessageBoxImage.Warning);
                RefreshPriceBookCombo();
            }
            finally { RefreshGates(); }
        }

        private void OnLoadPriceBook(object sender, RoutedEventArgs e)
        {
            if (_profile == null) return;
            // b24 (Codex 13:02 §4): fixed before the first window, checked after the last; nothing is written otherwise.
            ProfileDecisionScope scope;
            try { scope = CaptureProfileDecisionScope("רישום מחירון"); }
            catch (Exception ex) { ShowError("טעינת מחירון", ex); return; }
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "טען מחירון (Excel)",
                Filter = "Excel price book (*.xlsx;*.xls)|*.xlsx;*.xls",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;

            // Inspect first and SHOW what was understood before anything is registered. The review window also lets
            // the engineer choose another sheet, header row or columns, even when the automatic reading is usable: a
            // usable first sheet can still be the wrong one (Codex 15:55). Nothing is registered before "אשר ורשום".
            PriceBookXlsxLoader.Inspection insp;
            try { insp = PriceBookXlsxLoader.Inspect(dlg.FileName); }
            catch (Exception ex)
            {
                RtlMessageBox.Show("לא ניתן לקרוא את הקובץ:" + Environment.NewLine + ex.Message,
                    "טעינת מחירון", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var review = new PriceBookMappingDialog(dlg.FileName, insp);
            if (CivilModalHost.ShowFromPalette(review) != true || review.ApprovedInspection is not { IsUsable: true } approved)
            {
                SetStatus("טעינת המחירון בוטלה — לא נרשם דבר");
                return;
            }
            var approver = RequireApprover("רישום מחירון");
            if (approver == null) return;

            var writeAttempted = false;
            PriceBookRegistry.RegisterResult? published = null;
            try
            {
                RequireProfileDecisionScope(scope);
                var profileForSave = CloneProfileForDecision(scope.Profile);
                writeAttempted = true;
                var r = _estimate.RegisterPriceBook(
                    profileForSave, dlg.FileName, approver,
                    makeActive: true, targetProfilePath: scope.ExpectedState.TargetPath,
                    expectedProfileState: scope.ExpectedState,
                    expectedInspectionHash: approved.FileHash,
                    mapping: review.ApprovedMapping);
                var durable = ProjectProfileLoader.LoadFromFile(
                    scope.ExpectedState.TargetPath);
                if (!durable.IsUsable || durable.Profile == null ||
                    !CatalogIdentity.IsValidSha256(durable.ProfileHash) ||
                    !string.Equals(
                        PriceBookRegistry.Active(durable.Profile)?.Id,
                        r.Entry.Id,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        PriceBookRegistry.Active(durable.Profile)?.FileHash,
                        r.Entry.FileHash,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "המחירון נרשם אך הפרופיל לא נקרא בחזרה באופן מאומת.");
                InvalidateEstimateEvidence("מחירון חדש פעיל — יש להריץ סריקה חדשה");
                PublishPriceBookProfile(durable, scope.ExpectedState.TargetPath);
                Log($"מחירון נרשם: {r.Entry.Id} ({r.Entry.Publisher}, {r.Entry.ItemCount:N0} סעיפים) והוגדר כפעיל · אושר ע\"י {approver}." +
                    (r.Entry.Mapping is { } chosen
                        ? $" נקרא לפי בחירה: גיליון {chosen.SheetName}, שורת כותרת {chosen.HeaderRow}."
                        : ""));
                SetStatus("מחירון חדש פעיל — יש להריץ סריקה חדשה");
                published = r;
            }
            catch (Exception ex)
            {
                if (writeAttempted)
                {
                    InvalidateEstimateEvidence(
                        "רישום המחירון לא הושלם באופן מאומת — יש לסרוק מחדש");
                    ReloadProfile();
                }
                RtlMessageBox.Show(ex.Message, "רישום מחירון נכשל", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { RefreshGates(); }
            if (published != null) RememberPriceBook(published);
        }

        private void RefreshGates() => _gateRefresh.Run(RefreshGatesCore);

        private void RefreshGatesCore()
        {
            var profileUsable = _profile != null &&
                                !string.IsNullOrWhiteSpace(_profileWriteTarget);
            var planStale = PlanIsStale();
            if (planStale &&
                (_verifySummary != null || _sectionDisplayStatuses.Count > 0))
            {
                var selectedRecordId =
                    (SectionsGrid.SelectedItem as SectionRowViewModel)?.Record.RecordId;
                ResetDisplayedSectionVerification(
                    restoreApplyBaseline: false, selectedRecordId);
                SetStatus("תוצאות החתכים בוטלו — השרטוט השתנה ויש להריץ תכנון מחדש");
            }
            if (planStale && _previewShown)
                TryClearPreview(
                    "ביטול תצוגה מקדימה ממקורות חתך לא עדכניים",
                    showDialog: false);
            var gate = WorkflowGate.From(
                profileUsable, _plan, planStale, _apply, _previewShown, _verifySummary);

            BtnPickCl.IsEnabled = profileUsable;
            BtnPickClProfile.IsEnabled = profileUsable;
            BtnSetup.IsEnabled = profileUsable;
            BtnRunSetup.IsEnabled = profileUsable;
            BtnDrawingUnits.IsEnabled = profileUsable;
            RefreshDrawingUnitsSummary(profileUsable);
            BtnLoadPriceBook.IsEnabled = profileUsable;
            BtnKnownPriceBooks.IsEnabled = profileUsable;
            PriceBookCombo.IsEnabled = profileUsable;

            BtnPlan.IsEnabled = gate.CanPlan;
            BtnNextSectionIssue.IsEnabled = _sectionRows.Any(IsActionableSectionRow);
            BtnPreview.IsEnabled = gate.CanPreview &&
                                   SectionsGrid.SelectedItem is SectionRowViewModel previewRow &&
                                   _plan != null &&
                                   SectionPreviewService.CanPreview(_plan, previewRow.Record);
            BtnClearPreview.IsEnabled = gate.CanClearPreview || _sections.HasActivePreview ||
                                        _previewCleanupBlockingStatus != null;
            BtnApply.IsEnabled = gate.CanApply;
            BtnApply.Content = gate.IsNoOpBaseline ? "אשר החרגות" : "החל אצווה…";
            // An APPLY whose evidence never reached the run folder cannot be verified:
            // VERIFY would compare the drawing against an artifact that does not exist.
            var applyEvidenceLost = EvidenceWriteFailed(_apply?.Findings) ||
                                    EvidenceWriteFailed(_apply?.Records.SelectMany(record => record.Findings));
            var authoritativeApply = IsAuthoritativeApply(_apply);
            BtnVerify.IsEnabled = gate.CanVerify && !applyEvidenceLost && authoritativeApply &&
                                  (_apply == null || string.Equals(_apply.Scope,
                                      "batch", StringComparison.Ordinal));
            BtnShow.IsEnabled = gate.CanShow && SectionsGrid.SelectedItem != null;
            var selectedSection = SectionsGrid.SelectedItem as SectionRowViewModel;
            var canRebuildSelected = selectedSection != null && _evidenceBlockingStatus == null &&
                IsAuthoritativeApply(_apply) && SectionSelectedRebuildPolicy.Rejection(
                    _plan, selectedSection.Record.RecordId, _lastVerifyResult, !planStale) == null;
            BtnVerifySelected.IsEnabled = profileUsable && _plan != null &&
                                          !applyEvidenceLost && _evidenceBlockingStatus == null &&
                                          selectedSection?.Record is { } selectedVerify &&
                                          selectedVerify.ManualSectionReuse == null &&
                                          (selectedVerify.Action == PlanAction.Unchanged ||
                                           authoritativeApply && _apply!.Records.Any(item =>
                                               item.RecordId == selectedVerify.RecordId &&
                                               item.Status == DeliveryStatus.Applied));
            var selectedApplyReady = profileUsable && _plan != null &&
                                     !planStale &&
                                     selectedSection?.Record is { } selectedApply &&
                                     !SectionInputIntegrityService
                                         .HasSelectedApplyPlanningIntegrityBlocker(
                                             _plan, selectedApply) &&
                                     selectedApply.Status == DeliveryStatus.Ready &&
                                     selectedApply.PresentationCoverage.Complete &&
                                     selectedApply.PresentationCoverage.UnresolvedSpans.Count == 0 &&
                                     selectedApply.TrafficDirections.All(direction => direction.IsResolved) &&
                                     (selectedApply.Action is PlanAction.Create or
                                         PlanAction.Update or PlanAction.Replace || canRebuildSelected);
            BtnApplySelected.IsEnabled = selectedApplyReady &&
                                         (_apply is not { Committed: true } || canRebuildSelected);
            // A committed APPLY consumed this plan as its baseline. Another record is
            // created only from a fresh PLAN; the guide says so instead of pointing at
            // row details that show no blocker (live 29/09: 42676 after 12145).
            _applyAwaitsFreshPlan = selectedApplyReady && _apply is { Committed: true } &&
                                    !canRebuildSelected;
            BtnApplySelected.Content = canRebuildSelected ? "בנה מחדש חתך נבחר…" : "החל חתך נבחר";
            BtnResolveSection.IsEnabled = gate.CanResolveRecords && selectedSection?.CanChooseCrossing == true;
            BtnExcludeSection.IsEnabled = gate.CanResolveRecords && selectedSection?.CanExclude == true;
            var unresolvedDirections = _sectionRows.Sum(row =>
                row.Record.TrafficDirections.Count(direction => !direction.IsResolved));
            var selectedUnresolvedDirections = selectedSection?.Record.TrafficDirections
                .Count(direction => !direction.IsResolved) ?? 0;
            var unresolvedSpans = _sectionRows
                .Where(row => row.CanNameSpans)
                .Sum(row => row.Record.PresentationCoverage.UnresolvedSpans.Count);
            var selectedUnresolvedSpans = selectedSection?.CanNameSpans == true
                ? selectedSection.Record.PresentationCoverage.UnresolvedSpans.Count
                : 0;
            BtnResolveDirection.IsEnabled = gate.CanResolveRecords &&
                                            selectedSection?.CanEditTrafficDirections == true;
            BtnResolveDirection.Content = selectedUnresolvedDirections > 0
                ? $"כיוונים בחתך ({selectedUnresolvedDirections})…"
                : selectedSection?.CanEditTrafficDirections == true ? "ערוך כיווני נסיעה בחתך…"
                : unresolvedDirections > 0
                    ? $"כיווני נסיעה — בחר חתך ({unresolvedDirections})"
                : "כיווני נסיעה";
            BtnNameSpans.IsEnabled = gate.CanResolveRecords && selectedSection?.CanEditSpanLabels == true;
            BtnNameAllSpans.IsEnabled = gate.CanResolveRecords && HasWholePlanSpanLabelCandidates() &&
                                       _pendingWorkflowSaveDocument == null && _evidenceBlockingStatus == null;
            BtnNameAllSpans.Content = unresolvedSpans > 0
                ? $"סקירת כל הרצועות ({unresolvedSpans})…" : "סקירת רצועות בכל החתכים…";
            BtnNameSpans.Content = selectedUnresolvedSpans > 0
                ? $"שמות רצועות בחתך ({selectedUnresolvedSpans})…"
                : selectedSection?.CanEditSpanLabels == true ? "ערוך שמות רצועות בחתך…"
                : unresolvedSpans > 0
                    ? $"שמות רצועות — בחר חתך ({unresolvedSpans})"
                : "שמות רצועות";
            BtnApproveRow.IsEnabled = gate.CanResolveRecords &&
                                      selectedSection?.CanApproveRowSource == true;
            GateReason.Text = gate.Reason;

            var doc = Doc();
            var saveMayBeRequired = EstimateSaveMayBeRequired(doc, _scan, _profile?.ProfileId, _profileHash);
            var estimateSavePending = _pendingWorkflowSaveDocument != null;
            BtnScan.IsEnabled = profileUsable && doc != null && !estimateSavePending;
            BtnScan.Content = estimateSavePending
                ? "ממתין לשמירה…"
                : saveMayBeRequired
                ? "שמור וסרוק"
                : _scan == null ? "סריקה" : "סריקה מחדש";
            var estimateScopeApproved = EstimateWorkflowService.IsReviewedSourceScopeApproved(_profile);
            var earthworksDecision = EstimateWorkflowService.GetEarthworksDecision(_profile);
            BtnApproveEstimateScope.IsEnabled = profileUsable && !estimateSavePending;
            BtnApproveEstimateScope.Content = estimateScopeApproved ? "שנה מקורות…" : "בחר ואשר מקורות…";
            BtnEarthworksDecision.IsEnabled = profileUsable;
            BtnEarthworksDecision.Content = earthworksDecision.IsResolved
                ? "שנה החלטה…" : "החלטת עבודות עפר…";
            EarthworksDecisionState.Text = earthworksDecision.DisplayText;
            // Compute freshness once. It hashes the saved host and every XREF, so doing
            // it separately for BUILD and EXPORT made every row click perform two full
            // source passes. More importantly, every action based on scan evidence is
            // disabled together when that evidence is stale.
            var estimateScanFresh = _scan != null && !EstimateScanIsKnownStale();
            RefreshMeasurementDraftExport(profileUsable, estimateScanFresh);
            RefreshEngineerDraftExport(profileUsable, estimateScanFresh);
            RefreshBoqRulesExport(profileUsable, estimateScanFresh);
            RefreshCorridorBoqExport(profileUsable);
            RefreshFamilyReview(profileUsable, estimateScanFresh);
            BtnApprove.IsEnabled = profileUsable && estimateScanFresh && _catalog != null &&
                                   QuantitiesGrid.SelectedItem is QuantityRowViewModel mappingRow &&
                                   mappingRow.CanApproveCatalogMapping;
            var provenMappingCount = estimateScanFresh
                ? ProvenBatchMappingCandidates().Count
                : 0;
            BtnApproveProvenMappings.IsEnabled = profileUsable && estimateScanFresh &&
                                                  estimateScopeApproved &&
                                                  provenMappingCount > 0;
            BtnApproveProvenMappings.Content = provenMappingCount > 0
                ? $"אשר מיפויים חד־משמעיים ({provenMappingCount})…"
                : "אשר מיפויים חד־משמעיים…";
            BtnShowQuantity.IsEnabled = profileUsable && estimateScanFresh &&
                                        QuantitiesGrid.SelectedItem != null;
            BtnRelevance.IsEnabled = estimateScanFresh && profileUsable &&
                                     QuantitiesGrid.SelectedItem != null;
            BtnRelevance.Content = QuantitiesGrid.SelectedItem is QuantityRowViewModel sel
                ? sel.IsIgnored
                    ? "החזר לרשימה"
                    : sel.IsUnselectedClosedPolylineAlternative
                        ? "החרג חלופת מדידה"
                        : "לא רלוונטי"
                : "לא רלוונטי";
            var bulkNoiseCount = estimateScanFresh ? BulkNoiseCandidates().Count : 0;
            BtnFilterDrawingNoise.IsEnabled = estimateScanFresh && estimateScopeApproved &&
                                               profileUsable && bulkNoiseCount > 0;
            BtnFilterDrawingNoise.Content = bulkNoiseCount > 0
                ? $"בדוק סימוני עזר יחד ({bulkNoiseCount})…"
                : "בדוק סימוני עזר יחד…";
            BtnBuild.IsEnabled = profileUsable && estimateScanFresh &&
                                 estimateScopeApproved &&
                                 _scan!.Records.Count > 0 && _catalog != null;
            BtnBuild.Content = _estimateResult == null ? "חשב תמחור" : "חשב מחדש";
            BtnExport.IsEnabled = profileUsable && _estimateResult != null &&
                                  EstimatePreflightPolicy.CanExport(_estimateResult) &&
                                  estimateScanFresh;
            RefreshPricedDraftExport(profileUsable, estimateScanFresh);
            BtnTrace.IsEnabled = profileUsable && _estimateResult != null && estimateScanFresh;

            var preflightBlockers = _scan?.Findings.Count(EstimatePreflightPolicy.IsBlocking) ?? 0;
            var sourceScopeNotice = estimateScopeApproved
                ? "היקף מקורות מאושר; הכיסוי בפועל נבדק בסריקה." + Environment.NewLine
                : "היקף המקורות יאושר לפני אומדן סופי; ניתן למדוד כעת." + Environment.NewLine;
            var freshnessGate = _lastEstimateFreshnessReason == null
                ? string.Empty
                : "הסריקה אינה עדכנית — " + _lastEstimateFreshnessReason + Environment.NewLine;
            var catalogGate = _catalogFindings.Count == 0
                ? string.Empty
                : "המחירון אינו מוכן: " + string.Join(" · ", _catalogFindings.Take(3).Select(f => f.Title)) + Environment.NewLine;
            EstimateGateReason.Text = sourceScopeNotice + freshnessGate + catalogGate + (_historicalScan != null && _scan == null
                ? $"{_historicalScan.Records.Count} רשומות מהסריקה הקודמת מוצגות כהיסטוריה בלבד — אין אישור, בנייה או ייצוא עד סריקה חדשה."
                : _scan == null
                ? "סרוק תחילה — אין צורך במחירון או בהחלטת עבודות עפר."
                : $"{_scan.Records.Count} רשומות כמות · " +
                  (_estimateResult == null ? "מדידות לעיון ושיוך, עדיין לא אומדן מאושר" :
                   $"{_estimateResult.Lines.Count(l => l.IncludedInTotals)} שורות מתומחרות · " +
                    $"{_estimateResult.ExcludedLineCount} דורשות טיפול") +
                  (preflightBlockers > 0
                      ? Environment.NewLine + $"{preflightBlockers} ממצאי מדידה/היקף פתוחים; כל המדידות והפרטים נשמרים לעיון"
                      : "") +
                   UnmappedPotentialText());
            var materialAvailability = _scan?.Findings.FirstOrDefault(finding =>
                finding.Code == CorridorMaterialAvailabilityPolicy.UnavailableCode);
            if (materialAvailability != null)
                EstimateGateReason.Text += Environment.NewLine + materialAvailability.Title;

            var sourceReady = !estimateSavePending && !saveMayBeRequired && doc != null;
            var pendingReview = estimateScanFresh
                ? _quantityRows.Count(row => !row.IsIgnored && string.IsNullOrWhiteSpace(row.CatalogCode) &&
                    !row.IsUnselectedClosedPolylineAlternative)
                : 0;
            var flow = EstimateFlowPolicy.Evaluate(new EstimateFlowPolicy.Snapshot(
                estimateScopeApproved,
                earthworksDecision.IsResolved,
                sourceReady,
                estimateSavePending || saveMayBeRequired,
                estimateScanFresh,
                _quantityRows.Count,
                pendingReview,
                _catalog != null,
                _estimateResult != null,
                BtnExport.IsEnabled,
                EstimateSourceReviewFindingCount()));
            EstimateFlowProgress.Text = flow.Progress;
            EstimateNextAction.Text = "עכשיו: " + flow.NextAction;
            RefreshSectionGuidance(profileUsable, planStale);
            RefreshEstimateGuidance(BtnStartEstimateGuided, profileUsable,
                estimateScanFresh, saveMayBeRequired, preflightBlockers);
            RefreshEstimateRowActions(profileUsable, estimateScanFresh, estimateSavePending);
            // The worklist separates raw source findings from mapping decisions.
            // Its detailed evidence remains available; no finding is waived here.
            EstimateAdvancedActions.Header = "פירוט מקורות ובדיקות";
            MeasurementContext.Text = EstimateGuidedActionPolicy.MeasurementContext;
            if (_pendingWorkflowSaveDocument != null)
                BtnStartEstimateGuided.IsEnabled = false;
        }

        private static bool EstimateSaveMayBeRequired(Document? doc, EstimateWorkflowService.ScanResult? scan,
            string? profileId, string? profileHash)
        {
            if (doc == null) return false;
            try
            {
                var probe = DrawingRevisionTracker.CaptureLive(doc, includeFileHash: false);
                // This only avoids a misleading save prompt after locate/zoom.
                // Build/export/decisions still hash the host/XREFs and verify the
                // profile plus published scan before doing any authoritative work.
                var viewOnlyAfterSavedScan = probe.Failure == null && scan != null &&
                    EstimateSourceSnapshotPolicy.IsViewOnlySavedScanRevisionMatch(
                        scan.SourceDbMod, scan.DatabaseRevision, probe.DbMod, probe.DatabaseRevision) &&
                    scan.StaleReason(probe.DrawingPath, profileId, profileHash,
                        probe.DatabaseRevision) == null;
                return (probe.DbMod != 0 && !viewOnlyAfterSavedScan) || string.IsNullOrWhiteSpace(probe.DrawingPath) ||
                       !File.Exists(probe.DrawingPath);
            }
            catch { return false; }
        }

        /// <summary>
        /// The measured work still waiting for a catalog decision, summed per unit - so an
        /// estimate with one priced line still shows the engineer the size of the project.
        /// </summary>
        private string UnmappedPotentialText()
        {
            if (_scan == null) return "";
            var unmapped = _scan.Records
                .Where(r => !EstimateWorkflowService.HasApprovedCatalogMapping(_profile, r))
                .ToList();
            if (unmapped.Count == 0) return "";
            var byUnit = unmapped
                .GroupBy(r => r.Measurement.Unit)
                .Select(g => $"{g.Sum(r => r.Measurement.RawValue):N0} {g.Key}")
                .ToList();
            return Environment.NewLine + "ממתינות לשיוך: " + string.Join(" · ", byUnit);
        }

        private static Document? Doc()
        {
            try { return AcadApp.DocumentManager.MdiActiveDocument; }
            catch { return null; }
        }

        private void ShowError(string title, Exception ex)
        {
            UiGuard.Record(title, ex);
            Log($"{title}: {ex.Message}");
            SetStatus(title + " נכשל");
            RtlMessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ------------------------------------------------------------- sections

        /// <summary>
        /// Points the project at the drawing that carries the section locations.
        ///
        /// The CL drawing is usually a separate file that is never opened and never
        /// attached, and its layer name differs from project to project. Before this
        /// existed the only way to configure either was to hand-edit YAML, so a profile
        /// built on one machine resolved to nothing on another and the engineer got
        /// "none of the configured CL files was found" with no way forward.
        /// </summary>
        private void OnPickCl(object sender, RoutedEventArgs e)
        {
            var doc = Doc();
            if (doc == null || _profile == null) return;
            if (!EnsureSavedForAction("בחירת מקור CL", OnPickCl)) return;
            ProfileDecisionScope decisionScope;

            // A generated profile is keyed to the drawing's final absolute path.
            // Never let "Drawing1.dwg" (or another unsaved document name) create a
            // durable CL/profile contract which would be selected by an unrelated
            // future drawing, or stop matching immediately after SaveAs.
            try
            {
                ProjectSetupService.CaptureReadySource(doc, "CL source selection");
                decisionScope = CaptureProfileDecisionScope("בחירת מקור CL");
            }
            catch (Exception ex)
            {
                ShowError("בחירת מקור CL", ex);
                return;
            }

            var hostPath = decisionScope.Identity.DrawingPath;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "בחר את שרטוט ה-CL (מיקומי החתכים)",
                Filter = "שרטוטי AutoCAD (*.dwg)|*.dwg|כל הקבצים (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };
            if (!string.IsNullOrEmpty(hostPath))
            {
                try { dialog.InitialDirectory = System.IO.Path.GetDirectoryName(hostPath); } catch { }
            }
            if (dialog.ShowDialog() != true) return;

            var clPath = dialog.FileName;
            try
            {
                RequireProfileDecisionScope(decisionScope);
                SetStatus("קורא את שרטוט ה-CL…");
                using var log = new StageLog("ui_pick_cl");
                ProjectSetupScanner.ExternalClScan scan;

                if (ClSourceSelection.IsHostDrawing(clPath, hostPath))
                {
                    RtlMessageBox.Show(
                        "זהו השרטוט הפתוח עצמו. קווי CL שנמצאים בו נקראים ממילא — " +
                        "בחר את שרטוט ה-CL הנפרד, או הרץ הגדרת פרויקט כדי לבחור שכבה בשרטוט הזה.",
                        "בחירת קובץ CL", MessageBoxButton.OK, MessageBoxImage.Information);
                    SetStatus("מוכן");
                    return;
                }

                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var civilDoc = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(doc.Database);
                    scan = new ProjectSetupScanner().ScanClFile(clPath, civilDoc, tr, log);
                    tr.Abort();
                }

                if (scan.Candidates.Count == 0)
                {
                    RtlMessageBox.ShowPath(
                        $"בקובץ נסרקו {scan.Entities:N0} עצמים, אך אף שכבה לא נראית כמקור למיקומי חתכים " +
                        "(קווים ישרים שחוצים תוואי).", clPath, null,
                        "בחירת קובץ CL", MessageBoxButton.OK, MessageBoxImage.Warning, Path.GetFileName(clPath));
                    SetStatus("לא נמצאו שכבות מועמדות");
                    return;
                }

                var picker = new ClLayerPickerDialog(
                    clPath, scan.Entities, scan.LayersScanned, scan.AlignmentsProbed, scan.Candidates);
                if (CivilModalHost.ShowFromPalette(picker) != true || string.IsNullOrWhiteSpace(picker.SelectedLayer))
                {
                    SetStatus("מוכן");
                    return;
                }
                var approver = RequireApprover("בחירת מקור CL");
                if (approver == null) return;

                // The picker may stay open while the external DWG or profile changes.
                // Reconcile both immutable sources before mutating the in-memory profile.
                scan.RequireSourceUnchanged();
                RequireProfileDecisionScope(decisionScope);
                var expectedProfileState = decisionScope.ExpectedState;
                var stored = ClSourceSelection.StoredPath(clPath, hostPath);
                var profileForSave = CloneProfileForDecision(decisionScope.Profile);
                profileForSave.Sections.Cl.SourceFiles.Clear();
                profileForSave.Sections.Cl.SourceFiles.Add(stored);
                profileForSave.Sections.Cl.LayerPatterns.Clear();
                profileForSave.Sections.Cl.LayerPatterns.Add(picker.SelectedLayer!);
                // 1.4.1: the layer was picked in this drawing, so host lines on a same-named layer are not CL.
                profileForSave.Sections.Cl.LayerScope = ClInstructionReader.LayerScopeSourceFile;

                var saved = ProjectProfileWriter.Save(
                    profileForSave,
                    decisionScope.ExpectedState.TargetPath,
                    $"cl source: {stored} | cl layer: {picker.SelectedLayer}",
                    approver,
                    expectedProfileState,
                    new Dictionary<string, string>
                    {
                        [scan.Path] = scan.SourceHash,
                    });

                Log($"נשמר (אושר ע\"י {approver}): קובץ CL {Bidi.Ltr(stored)}, שכבה {Bidi.Ltr(picker.SelectedLayer!)} " +
                    $"(גרסת פרופיל {saved.NewVersion}).");
                PublishSavedProfile(saved);
                RefreshDashboard();
                RefreshGates();
                SetStatus("שרטוט ה-CL נשמר לפרויקט");
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
                when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.FileSharingViolation)
            {
                ReloadProfile();
                // Should be unreachable now that SideDwg falls back to a shadow copy, but a
                // raw "eFileSharingViolation" must never be what the engineer reads.
                Log("בחירת קובץ CL: " + ex.Message);
                SetStatus("הקובץ נעול על ידי תוכנית אחרת");
                RtlMessageBox.Show(
                    "הקובץ נעול על ידי תוכנית אחרת ולא ניתן לקרוא אותו כרגע.\n" +
                    "אפשר לסגור אותו בתוכנית שמחזיקה אותו ולנסות שוב, או להעתיק אותו הצידה ולבחור את העותק.",
                    "בחירת קובץ CL", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                ReloadProfile();
                ShowError("בחירת קובץ CL", ex);
            }
        }

        private void OnSetup(object sender, RoutedEventArgs e)
        {
            var doc = Doc();
            if (doc == null || _profile == null) return;
            if (!EnsureSavedForAction("הגדרת פרויקט", OnSetup)) return;
            try
            {
                SetStatus("מריץ הגדרת פרויקט…");
                var scope = CaptureProfileDecisionScope("הגדרת פרויקט");
                using var log = new StageLog("ui_setup");
                ProjectSetupScan? scan = null;
                RunBusy("מאתר מקורות לפרויקט…", () =>
                {
                    scan = new ProjectSetupService().Scan(doc, scope.Profile,
                        scope.Identity.ProfileHash, scope.Identity.ProfileSource,
                        scope.ExpectedState.TargetPath, scope.ExpectedState, log);
                });
                if (scan == null) return;
                RequireProfileDecisionScope(scope);
                var approver = RequireApprover("הגדרת פרויקט");
                if (approver == null) return;
                var review = new ProjectSetupReviewDialog(scan, scope.Profile, approver);
                CivilModalHost.ShowFromPalette(review);
                if (review.PickExternalClRequested)
                {
                    RequireProfileDecisionScope(scope);
                    var before = _profileHash;
                    OnPickCl(sender, e);
                    if (!string.Equals(before, _profileHash, StringComparison.Ordinal)) OnSetup(sender, e);
                    return;
                }
                if (review.ApprovedSelection == null) { SetStatus("ההגדרה בוטלה — לא נשמר דבר"); return; }
                RequireProfileDecisionScope(scope);
                var saved = ProjectSetupService.Save(doc, scope.Profile, review.ApprovedSelection,
                    scan, scope.Identity.ProfileHash, scope.Identity.ProfileSource, scope.ExpectedState.TargetPath);
                PublishSavedProfile(saved);
                RefreshDashboard(); RefreshGates();
                SetStatus("מקורות הפרויקט נשמרו — כעת לחץ תכנון חתכים");
            }
            catch (Exception ex) { ShowError("הגדרת פרויקט", ex); }
        }

        private void OnPlan(object sender, RoutedEventArgs e)
        {
            var doc = Doc();
            if (doc == null) return;
            if (!EnsureSavedForAction("תכנון חתכים", OnPlan)) return;
            var preferredSectionId =
                (SectionsGrid.SelectedItem as SectionRowViewModel)?.Record.RecordId;
            // Clicking PLAN starts a replacement attempt. Invalidate the entire old
            // chain before even rereading the profile: an I/O/CAS failure during that
            // reread must not leave yesterday's PLAN/APPLY/VERIFY green on screen.
            ResetDisplayedSectionPlanAttempt();
            RefreshGates();
            try
            {
                // The profile file is the engineer's contract - reread it every run so
                // an updated profile never waits for a Civil restart (bit us live, 31/08).
                ReloadProfile();
                if (_profile == null) return;
                SetStatus("מריץ תכנון…");
                if (!TryClearPreview("תכנון")) return;
                using var log = new StageLog("ui_plan");
                _sections.Log = log;

                RunBusy("מאתר ובודק חתכים…\nשלב התכנון בלבד — עדיין לא נוצרים חתכים בשרטוט", () =>
                {
                    _plan = _sections.Plan(doc, _profile, _profileHash);
                });
                _apply = null;
                _verifySummary = null;
                _lastVerifyResult = null;
                _sectionResultsDrawing = DrawingScopeIdentity.For(doc);

                if (_plan == null) return;
                SectionsWorkflowService.RequirePlanEvidence(_plan);
                // Fresh PLAN replaces the failed run baseline, not its unverified output.
                _evidenceBlockingStatus = null;
                _sectionDisplayStatuses.Clear();
                RebuildSectionRows(preferredSectionId);
                SelectBestSectionRow(preferredSectionId);

                var ready = _plan.Records.Count(r => r.Status == DeliveryStatus.Ready);
                var taskSummary = WorkflowGate.SectionTaskSummary(_plan);
                Log($"תכנון: {_plan.Records.Count} רשומות CL, {ready} מוכנות. {taskSummary}");
                SetStatus($"תכנון הושלם — {taskSummary}");

                if (_plan.Records.Count == 0)
                {
                    var layers = string.Join(", ", _profile.Sections.Cl.LayerPatterns);
                    Log($"לא נמצאו רשומות CL. שכבות שנסרקו: {(string.IsNullOrEmpty(layers) ? "כל השכבות" : layers)}");
                }
            }
            catch (Exception ex)
            {
                ResetDisplayedSectionPlanAttempt();
                ShowError("תכנון", ex);
            }
            finally { _sections.Log = null; RefreshGates(); }
        }

        private void OnResolveSection(object sender, RoutedEventArgs e) =>
            ReviewSectionDecision(SectionDecisionDialog.DecisionKind.Crossing);

        private void OnNextSectionIssue(object sender, RoutedEventArgs e)
        {
            var actionable = _sectionRows.Where(IsActionableSectionRow).ToList();
            if (actionable.Count == 0) return;
            var current = SectionsGrid.SelectedItem as SectionRowViewModel;
            var currentIndex = current == null ? -1 : actionable.IndexOf(current);
            var next = actionable[(currentIndex + 1) % actionable.Count];
            SectionsGrid.SelectedItem = next;
            SectionsGrid.ScrollIntoView(next);
        }

        private void OnExcludeSection(object sender, RoutedEventArgs e) =>
            ReviewSectionDecision(SectionDecisionDialog.DecisionKind.Exclusion);

        private void OnResolveTrafficDirection(object sender, RoutedEventArgs e)
        {
            if (_profile == null || _plan == null) return;
            if (!EnsureSavedForAction("הכרעת כיווני נסיעה", OnResolveTrafficDirection, replan: true)) return;
            var selected = SectionsGrid.SelectedItem is SectionRowViewModel selectedRow &&
                           selectedRow.CanEditTrafficDirections
                ? selectedRow.Record
                : null;
            if (selected == null)
            {
                RtlMessageBox.Show(
                    "יש לבחור בטבלה חתך עם רצועות נסיעה. ניתן לערוך גם כיוון קיים; השינוי נשמר לחתך ולרצועות שבחרת בלבד.",
                    "כיווני נסיעה",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var records = new[] { selected };

            if (!TryCaptureSectionDecisionScope(selected, "TRAFFIC-DIRECTION-DECISION", out var decisionScope)) return;
            var dialog = new TrafficDirectionDecisionDialog(records, includeResolvedDirections: true);
            if (CivilModalHost.ShowFromPalette(dialog) != true) return;

            try
            {
                var decisionWrite = RequireSectionDecisionScope(decisionScope);
                var currentProfile = decisionWrite.CurrentProfile;
                var profileForSave = CloneProfileForDecision(currentProfile.Profile!);
                var count = SectionDecisionProfileService.ApproveEditedTrafficDirectionsBatch(
                    profileForSave,
                    records,
                    dialog.Approvals,
                    dialog.ApprovedBy,
                    dialog.ApprovedAtUtc);
                var summary = $"traffic directions selected record: " +
                              $"record={selected.RecordId}; lanes={count}";
                var saved = ProjectProfileWriter.Save(
                    profileForSave,
                    currentProfile.ProfileWriteTarget,
                    summary,
                    dialog.ApprovedBy,
                    expectedState: decisionWrite.ExpectedState);
                PublishSavedProfile(saved);
                _apply = null;
                _verifySummary = null;
                _sectionDisplayStatuses.Clear();
                Log($"הכרעת כיוון נשמרה בפרופיל {saved.NewVersion}: {summary}");
                SetStatus("הכרעת הכיוון נשמרה — מריץ תכנון מחדש");

                // The next PLAN consumes the durable profile decision and either
                // resolves this row or exposes the next unresolved strip.
                OnPlan(this, new RoutedEventArgs());
            }
            catch (Exception ex)
            {
                ReloadProfile();
                ShowError("שמירת הכרעת כיוון", ex);
                RefreshGates();
            }
        }

        private void OnNameSectionSpans(object sender, RoutedEventArgs e)
        {
            if (!EnsureSavedForAction("שמות רצועות", OnNameSectionSpans, replan: true)) return;
            if (_profile == null || _plan == null) return;
            var candidates = _plan.Records
                .Where(record => record.Action != PlanAction.Excluded &&
                                 !string.IsNullOrWhiteSpace(record.SelectedAlignment) &&
                                 (record.PresentationCoverage.UnresolvedSpans.Count > 0 ||
                                  record.PresentationCoverage.ResolvedSpans.Count > 0) &&
                                 (string.Equals(record.PresentationCoverage.RowAuthorityState,
                                      "authoritative", StringComparison.Ordinal) ||
                                  string.Equals(record.PresentationCoverage.RowAuthorityState,
                                      "nocandidates", StringComparison.Ordinal)))
                .OrderBy(record => record.Station ?? double.MaxValue)
                .ToList();
            if (candidates.Count == 0)
            {
                RtlMessageBox.Show(
                    "אין כרגע רצועות שניתן לאשר. אם מקור ROW עדיין לא הוכרע, יש לאשר אותו קודם. " +
                    "חתך ללא סימוני תכנית דורש טעינה/תיקון של מקור SM/GM אמיתי או החרגה הנדסית מפורשת.",
                    "שמות רצועות", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var selected = SectionsGrid.SelectedItem is SectionRowViewModel selectedRow &&
                           selectedRow.CanEditSpanLabels && candidates.Contains(selectedRow.Record)
                ? selectedRow.Record
                : null;
            if (selected == null)
            {
                RtlMessageBox.Show("בחר בטבלה את החתך שאת שמות הרצועות שלו ברצונך להשלים.",
                    "בחירת חתך", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var records = new[] { selected };
            if (!TryCaptureSectionDecisionScope(selected, "SPAN-LABEL-DECISION", out var decisionScope)) return;
            var dialog = new SectionSpanLabelDecisionDialog(records,
                initiallyApproveStrongSuggestions: false,
                previousDecisions: _profile.Sections.Decisions.SpanLabels,
                includeResolvedSpans: true);
            if (CivilModalHost.ShowFromPalette(dialog) != true) return;

            try
            {
                var decisionWrite = RequireSectionDecisionScope(decisionScope);
                var currentProfile = decisionWrite.CurrentProfile;
                var profileForSave = CloneProfileForDecision(currentProfile.Profile!);
                var count = SectionDecisionProfileService.ApproveEditedSpanLabelsBatch(
                    profileForSave, records, dialog.Approvals,
                    dialog.ApprovedBy, dialog.ApprovedAtUtc);
                var summary = $"selected section span labels: record={selected.RecordId}; spans={count}";
                var saved = ProjectProfileWriter.Save(
                    profileForSave,
                    currentProfile.ProfileWriteTarget,
                    summary,
                    dialog.ApprovedBy,
                    expectedState: decisionWrite.ExpectedState);
                PublishSavedProfile(saved);
                _apply = null;
                _verifySummary = null;
                _sectionDisplayStatuses.Clear();
                Log($"שמות הרצועות נשמרו בפרופיל {saved.NewVersion}: {summary}");
                SetStatus("שמות הרצועות נשמרו — מריץ תכנון מחדש");
                OnPlan(this, new RoutedEventArgs());
            }
            catch (Exception ex)
            {
                ReloadProfile();
                ShowError("שמירת שמות רצועות", ex);
                RefreshGates();
            }
        }

        private void OnApproveSectionRow(object sender, RoutedEventArgs e)
        {
            if (!EnsureSavedForAction("אישור מקור זכות דרך", OnApproveSectionRow, replan: true)) return;
            if (_profile == null || _plan == null ||
                SectionsGrid.SelectedItem is not SectionRowViewModel row)
                return;
            if (!TryCaptureSectionDecisionScope(row.Record, "ROW-AUTHORITY-DECISION", out var decisionScope)) return;
            var dialog = new SectionRowAuthorityDecisionDialog(row.Record);
            if (CivilModalHost.ShowFromPalette(dialog) != true || dialog.SelectedSourceKey == null) return;

            try
            {
                var decisionWrite = RequireSectionDecisionScope(decisionScope);
                var currentProfile = decisionWrite.CurrentProfile;
                var profileForSave = CloneProfileForDecision(currentProfile.Profile!);
                var authority = SectionDecisionProfileService.ApproveRowAuthority(
                    profileForSave, row.Record, dialog.SelectedSourceKey,
                    dialog.ApprovedBy, dialog.ApprovedAtUtc);
                var summary = $"ROW source authority: sha256={authority.SourceDrawingSha256}; " +
                              $"path={authority.SourcePathPattern}; xref={authority.XrefPattern}";
                var saved = ProjectProfileWriter.Save(
                    profileForSave,
                    currentProfile.ProfileWriteTarget,
                    summary,
                    dialog.ApprovedBy,
                    expectedState: decisionWrite.ExpectedState);
                PublishSavedProfile(saved);
                _apply = null;
                _verifySummary = null;
                _sectionDisplayStatuses.Clear();
                Log($"מקור ROW נשמר בפרופיל {saved.NewVersion}: {summary}");
                SetStatus("מקור ROW אושר — מריץ תכנון מחדש");
                OnPlan(this, new RoutedEventArgs());
            }
            catch (Exception ex)
            {
                ReloadProfile();
                ShowError("אישור מקור ROW", ex);
                RefreshGates();
            }
        }

        private void ReviewSectionDecision(SectionDecisionDialog.DecisionKind initialKind)
        {
            if (!EnsureSavedForAction("הכרעת חתך", (_, _) => ReviewSectionDecision(initialKind), replan: true)) return;
            if (_profile == null || _plan == null ||
                SectionsGrid.SelectedItem is not SectionRowViewModel row)
                return;

            if (!TryCaptureSectionDecisionScope(row.Record, "SECTION-DECISION", out var decisionScope)) return;
            var dialog = new SectionDecisionDialog(row.Record, _plan, initialKind);
            if (CivilModalHost.ShowFromPalette(dialog) != true) return;

            try
            {
                var decisionWrite = RequireSectionDecisionScope(decisionScope);
                var currentProfile = decisionWrite.CurrentProfile;
                var profileForSave = CloneProfileForDecision(currentProfile.Profile!);
                string summary;
                if (dialog.SelectedKind == SectionDecisionDialog.DecisionKind.Crossing)
                {
                    if (dialog.SelectedCrossing == null) return;
                    SectionDecisionProfileService.ApproveCrossing(
                        profileForSave, row.Record, dialog.SelectedCrossing,
                        dialog.ApprovedBy, DateTime.UtcNow);
                    summary = $"CL {row.Record.Cl.SourceHandle}: crossing " +
                              $"{dialog.SelectedCrossing.AlignmentName}@{dialog.SelectedCrossing.Station:F3}";
                }
                else
                {
                    var code = dialog.FindingCode
                        ?? throw new InvalidOperationException("לא נבחר קוד ממצא להחרגה.");
                    var targets = dialog.BatchSameFindingCode
                        ? _plan.Records.Where(r =>
                                r.Action != PlanAction.Excluded &&
                                r.Findings.Any(f => string.Equals(f.Code, code, StringComparison.Ordinal)))
                            .ToList()
                        : new List<SectionPlanRecord> { row.Record };

                    if (dialog.BatchSameFindingCode && RtlMessageBox.Show(
                            $"סומנה החרגה קבוצתית. להחריג {targets.Count} רשומות CL עם הקוד {code}?\n\n" +
                            $"סיבה: {dialog.Reason}\nמאשר: {dialog.ApprovedBy}",
                            "אישור החרגה קבוצתית", MessageBoxButton.YesNo,
                            MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        return;

                    var count = SectionDecisionProfileService.ApproveExclusions(
                        profileForSave, targets, code, dialog.Reason,
                        dialog.ApprovedBy, DateTime.UtcNow);
                    summary = $"explicit CL exclusion: {count} records; finding={code}; reason={dialog.Reason}";
                }

                // The explicit batch-exclusion confirmation is another modal boundary.
                decisionWrite = RequireSectionDecisionScope(decisionScope);
                var saved = ProjectProfileWriter.Save(
                    profileForSave,
                    currentProfile.ProfileWriteTarget,
                    summary,
                    dialog.ApprovedBy,
                    expectedState: decisionWrite.ExpectedState);
                PublishSavedProfile(saved);
                _apply = null;
                _verifySummary = null;
                _sectionDisplayStatuses.Clear();
                Log($"הכרעת חתך נשמרה בפרופיל {saved.NewVersion}: {summary}");
                SetStatus("ההכרעה נשמרה — מריץ תכנון מחדש");

                // The saved profile is the engineering contract. Re-read it and PLAN
                // immediately so the table/gates show the durable decision, not an
                // in-memory optimistic edit.
                OnPlan(this, new RoutedEventArgs());
            }
            catch (Exception ex)
            {
                ReloadProfile();
                ShowError("שמירת הכרעת חתך", ex);
                RefreshGates();
            }
        }

        private void OnPreview(object sender, RoutedEventArgs e)
        {
            var doc = Doc();
            if (doc == null || _profile == null || _plan == null) return;
            if (SectionsGrid.SelectedItem is not SectionRowViewModel selected)
            {
                RtlMessageBox.Show(
                    "יש לבחור בטבלה חתך עם גאומטריה מוכחת. גם שורת 'דרושה בדיקה' " +
                    "מותרת רק לבדיקה אבחונית שאינה התוצר הסופי.",
                    "בדיקת גאומטריה",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var previewBlockReason = SectionPreviewService.PreviewBlockReason(_plan, selected.Record);
            if (previewBlockReason != null)
            {
                RtlMessageBox.Show(previewBlockReason, "בדיקת גאומטריה",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                var currentProfile = RequireFreshSectionPlan("PREVIEW");
                var preview = _sections.Preview(
                    doc, currentProfile.Profile!, currentProfile.ProfileHash,
                    _plan, selected.Record.RecordId);
                Log($"בדיקת גאומטריה אבחונית {selected.SectionId}: קרקע קיימת {preview.ExistingGroundSamples} נקודות, " +
                    $"תכנון {preview.DesignSamples} נקודות, {preview.ProjectedUtilityCount} מערכות. " +
                    $"{preview.ScopeNotice}. לא נשמר דבר בשרטוט.");
                if (!ZoomPreviewTightly(doc, preview.Extents))
                {
                    SetStatus("בדיקת הגאומטריה הוכנה, אך לא ניתן להתמקד בה בתצוגה הפעילה. " +
                              NativeViewZoomService.RecoveryGuidance);
                    return;
                }
                SetStatus("בדיקת גאומטריה אבחונית בלבד (לא אישור ולא תוצר סופי) — " +
                          selected.SectionId);
            }
            catch (Exception ex) { ShowError("בדיקת גאומטריה", ex); }
            finally
            {
                // ShowPreview clears the previous transient before sampling the next
                // one. If sampling fails, derive UI state from the actual transient
                // registry instead of leaving the old successful flag set.
                _previewShown = _sections.HasActivePreview;
                RefreshGates();
            }
        }

        private void OnClearPreview(object sender, RoutedEventArgs e)
        {
            if (!TryClearPreview("ניקוי תצוגה מקדימה"))
            {
                RefreshGates();
                return;
            }
            Log("התצוגה המקדימה נוקתה — השרטוט ללא שינוי.");
            SetStatus("התצוגה נוקתה");
            RefreshGates();
        }

        private bool TryClearPreview(string operation, bool showDialog = true)
        {
            try
            {
                _sections.ClearPreview();
                _previewShown = _sections.HasActivePreview;
                if (_previewShown)
                    throw new InvalidOperationException(
                        "נותרו ידיות תצוגה זמניות פעילות לאחר ניסיון הניקוי.");
                _previewCleanupBlockingStatus = null;
                return true;
            }
            catch (Exception ex)
            {
                _previewShown = _sections.HasActivePreview;
                var message = $"✗ {operation}: התצוגה המקדימה לא נוקתה במלואה — הפעולה חסומה";
                Log(message + " · " + ex.Message);
                _previewCleanupBlockingStatus = message;
                _statusBrush ??= StatusLabel.Foreground;
                SetStatus(message);
                Utilities.MahodLogger.Error("Civil Delivery preview cleanup: " + operation, ex);
                if (showDialog)
                    RtlMessageBox.Show(
                        ex.Message + "\n\nלא ממשיכים ל-PLAN/APPLY עד שניקוי התצוגה מצליח.",
                        "Mahod Civil Delivery — ניקוי תצוגה נכשל",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void ClearPreviewBeforeDocumentTransition(Document document)
        {
            try
            {
                _sections.ClearPreviewForDocument(document);
                _previewShown = _sections.HasActivePreview;
                if (!_previewShown) _previewCleanupBlockingStatus = null;
            }
            catch (Exception ex)
            {
                _previewShown = _sections.HasActivePreview;
                _previewCleanupBlockingStatus = "ניקוי תצוגה לא הושלם — חזור לשרטוט המקורי ולחץ נקה תצוגה";
                _statusBrush ??= StatusLabel.Foreground;
                SetStatus(_previewCleanupBlockingStatus);
                Log(_previewCleanupBlockingStatus + " · " + ex.Message);
                Utilities.MahodLogger.Error("Civil Delivery preview before drawing transition", ex);
            }
        }

        private void OnApply(object sender, RoutedEventArgs e)
        {
            var doc = Doc();
            if (doc == null || _profile == null || _plan == null) return;
            var preferredRecordId =
                (SectionsGrid.SelectedItem as SectionRowViewModel)?.Record.RecordId;

            var changed = _plan.Records.Count(r =>
                r.Status == DeliveryStatus.Ready &&
                r.Action is PlanAction.Create or PlanAction.Update or PlanAction.Replace);
            var batch = _plan.Records.Count(r =>
                r.Status == DeliveryStatus.Ready &&
                r.Action is PlanAction.Create or PlanAction.Update or PlanAction.Replace or PlanAction.Unchanged);

            // changed == 0 is reachable only when every record is a signed exclusion
            // (WorkflowGate disables APPLY for an all-Unchanged plan): this publishes
            // exclusion evidence and never claims a verification baseline.
            var confirmation = changed == 0
                ? $"אין חתכים לשינוי. לפרסם את ראיות ההחרגה החתומות עבור {_plan.Records.Count} רשומות " +
                  "ללא שינוי בשרטוט?"
                : $"לרענן {batch} חתכים באצווה אחת ({changed} השתנו)?";
            if (RtlMessageBox.Show(
                    confirmation, changed == 0 ? "פרסום ראיות החרגה" : "החלה",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            try
            {
                ResetDisplayedSectionApplyAttempt(preferredRecordId);
                var currentProfile = RequireFreshSectionPlan("APPLY");
                if (!TryClearPreview("החלה")) return;
                BeginEvidenceAttempt();
                SetStatus("מחיל…");
                using var log = new StageLog("ui_apply");
                _sections.Log = log;

                RunBusy($"יוצר / מעדכן {batch} חתכים…", () =>
                {
                    _apply = _sections.Apply(
                        doc, currentProfile.Profile!, currentProfile.ProfileHash, _plan);
                });
                if (_apply == null) return;
                _applyPlanRunId = _plan.RunId;
                var authoritativeApply = IsAuthoritativeApply(_apply);
                _verifySummary = null;
                _sectionDisplayStatuses.Clear();
                foreach (var result in _apply.Records)
                {
                    if (!authoritativeApply)
                        _sectionDisplayStatuses[result.RecordId] = DeliveryStatus.Failed;
                    else if (_apply.Committed || result.Status == DeliveryStatus.Failed)
                        _sectionDisplayStatuses[result.RecordId] = result.Status;
                }
                // Never show Applied/Verified from a non-authoritative APPLY. A global
                // post-commit evidence error invalidates every returned row together.
                RebuildSectionRows();
                // The drawing now holds more tool-owned sections: the card must say so.
                RefreshDashboard();

                var applied = _apply.Records.Count(r => r.Status == DeliveryStatus.Applied);
                Log(authoritativeApply
                    ? changed == 0
                        ? "ראיות ההחרגה פורסמו ללא שינוי בשרטוט."
                        : $"החלה הושלמה: {applied} חתכים נוצרו."
                    : _apply.Committed
                        ? "הטרנזקציה נשמרה, אך תוצאת APPLY אינה מוסמכת — האימות והמסירה חסומים."
                    : "ההחלה בוטלה עם rollback מלא — אין חצי-יצירה בשרטוט.");
                if (ReportEvidenceWriteFailure("החלה", _apply.Findings.Concat(
                        _apply.Records.SelectMany(record => record.Findings)))) return;
                if (authoritativeApply && applied > 0) OnShow(sender, e);
                SetStatus(authoritativeApply
                    ? changed == 0 ? "ראיות ההחרגה פורסמו — השרטוט לא שונה" : $"הוחלו {applied} חתכים"
                    : _apply.Committed ? "ההחלה נכשלה — חסום לאימות ולמסירה" : "ההחלה בוטלה");
            }
            catch (Exception ex)
            {
                ResetDisplayedSectionApplyAttempt(preferredRecordId);
                ShowError("החלה", ex);
            }
            finally { _sections.Log = null; RefreshGates(); }
        }

        private void OnApplySelected(object sender, RoutedEventArgs e)
        {
            var doc = Doc();
            if (doc == null || _profile == null || _plan == null ||
                SectionsGrid.SelectedItem is not SectionRowViewModel row)
                return;

            var record = row.Record;
            if (!BtnApplySelected.IsEnabled)
            {
                RtlMessageBox.Show(
                    "החתך המסומן עדיין אינו READY עם רצועות, כיוונים וחוזה תצוגה מלא, או שה-PLAN אינו טרי.",
                    "החל חתך נבחר", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var section = record.SectionId ?? record.RecordId;
            var failedVerification = _lastVerifyResult;
            var rebuilding = SectionSelectedRebuildPolicy.Rejection(
                _plan, record.RecordId, failedVerification, !PlanIsStale()) == null;
            if (RtlMessageBox.Show(
                    rebuilding
                        ? $"לבנות מחדש את חתך {section} לאחר האימות שנכשל?\n\n" +
                          "יוחלפו רק קו הדגימה, תצוגת החתך וההערות שבבעלות הכלי עבור חתך זה, לפי ה-PLAN הקיים והמקורות הנוכחיים. " +
                          "החלטות התכנון לא ישתנו. יתר החתכים יישמרו. הפעולה אינה אישור אימות; לאחריה חובה לאמת מחדש."
                        : $"ליצור עכשיו את חתך {section}?\n\nרק החתך הזה ייווצר. שאר השורות לא ישתנו.",
                    rebuilding ? "בנייה מחדש מפורשת של חתך נבחר" : "החל חתך נבחר", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            try
            {
                ResetDisplayedSectionApplyAttempt(record.RecordId);
                var currentProfile = RequireFreshSectionPlan("APPLY-SELECTED");
                if (!TryClearPreview("החלת חתך נבחר")) return;
                BeginEvidenceAttempt();
                SetStatus($"מחיל חתך נבחר {section}…");
                using var log = new StageLog("ui_apply_selected");
                _sections.Log = log;
                RunBusy($"מחיל חתך {section}…", () =>
                {
                    _apply = rebuilding
                        ? _sections.RebuildSelected(doc, currentProfile.Profile!, currentProfile.ProfileHash,
                            _plan, record.RecordId, failedVerification!)
                        : _sections.ApplySelected(doc, currentProfile.Profile!, currentProfile.ProfileHash,
                            _plan, record.RecordId);
                });
                if (_apply == null) return;
                _applyPlanRunId = _plan.RunId;
                var authoritativeApply = IsAuthoritativeApply(_apply);
                _verifySummary = null;
                _sectionDisplayStatuses.Clear();
                foreach (var item in _apply.Records)
                {
                    if (!authoritativeApply)
                        _sectionDisplayStatuses[item.RecordId] = DeliveryStatus.Failed;
                    else if (_apply.Committed || item.Status == DeliveryStatus.Failed)
                        _sectionDisplayStatuses[item.RecordId] = item.Status;
                }
                RebuildSectionRows();
                RefreshDashboard();

                var applied = _apply.Records.Count(item =>
                    item.Status == DeliveryStatus.Applied);
                Log(authoritativeApply
                    ? $"חתך נבחר הוחל: {section}. יתר רשומות ה-PLAN לא השתנו."
                    : _apply.Committed
                        ? "הטרנזקציה נשמרה, אך תוצאת החתך הנבחר אינה מוסמכת — האימות והמסירה חסומים."
                    : "החלת החתך הנבחר בוטלה עם rollback מלא.");
                if (ReportEvidenceWriteFailure("החלת חתך נבחר", _apply.Findings.Concat(
                        _apply.Records.SelectMany(item => item.Findings)))) return;
                if (authoritativeApply && applied == 1) OnShow(sender, e);
                SetStatus(authoritativeApply && applied == 1
                    ? $"חתך {section} נוצר במודל וראיות הריצה נשמרו — קובץ DWG לא נשמר אוטומטית; טרם אומת"
                    : _apply.Committed
                        ? $"החלת חתך {section} נכשלה — חסום לאימות ולמסירה"
                        : "החלת החתך הנבחר בוטלה");
            }
            catch (Exception ex)
            {
                ResetDisplayedSectionApplyAttempt(record.RecordId);
                ShowError("החלת חתך נבחר", ex);
            }
            finally { _sections.Log = null; RefreshGates(); }
        }

        private void OnVerify(object sender, RoutedEventArgs e)
        {
            var doc = Doc();
            if (doc == null || _profile == null || _plan == null || _apply == null) return;
            var preferredRecordId =
                (SectionsGrid.SelectedItem as SectionRowViewModel)?.Record.RecordId;
            try
            {
                ResetDisplayedSectionVerification(
                    restoreApplyBaseline: true, preferredRecordId);
                var currentProfile = RequireFreshSectionPlan("VERIFY");
                BeginEvidenceAttempt();
                SetStatus("מאמת…");
                var verify = _sections.Verify(
                    doc, currentProfile.Profile!, currentProfile.ProfileHash,
                    _plan, _apply);
                _lastVerifyResult = verify;
                var verified = verify.Records.Count(r => r.Status == DeliveryStatus.Verified);
                var checks = verify.Records.Sum(r => r.Checks.Count);
                var failed = verify.Records.SelectMany(r => r.Checks.Where(c => !c.Pass)).ToList();

                Log($"אימות: {verified}/{verify.Records.Count} רשומות, {checks} בדיקות מול בסיס הנתונים.");
                foreach (var f in failed.Take(5))
                    Log($"  ✗ {f.Check}: צפוי {f.Expected}, בפועל {f.Actual}");

                var verifyEvidenceLost = EvidenceWriteFailed(verify.Findings);
                var authoritativeVerify = IsAuthoritativeVerify(verify);
                _verifySummary = verifyEvidenceLost
                    ? "✗ קובצי האימות לא נכתבו לתיקיית הריצה — אין ראיות, אין מסירה"
                    : authoritativeVerify
                        ? $"אומתו {verified}/{verify.Records.Count} חתכים מול המודל · {checks} בדיקות עברו ✓"
                        : $"אימות נכשל — {failed.Count} בדיקות לא עברו (ראי יומן)";
                foreach (var result in verify.Records)
                    _sectionDisplayStatuses[result.RecordId] = authoritativeVerify
                        ? result.Status
                        : DeliveryStatus.Failed;
                RebuildSectionRows();
                SetStatus(authoritativeVerify
                    ? $"אומת — {verified} חתכים"
                    : $"אימות נכשל ({failed.Count} בדיקות)");
                ReportEvidenceWriteFailure("אימות", verify.Findings);
            }
            catch (Exception ex)
            {
                ResetDisplayedSectionVerification(
                    restoreApplyBaseline: true, preferredRecordId);
                ShowError("אימות", ex);
            }
            finally { RefreshGates(); }
        }

        /// <summary>
        /// Rebuilds presentation rows from immutable PLAN evidence plus the latest
        /// APPLY/VERIFY display statuses.  The selected record survives the rebuild.
        /// </summary>
        private void RebuildSectionRows(string? preferredRecordId = null)
        {
            if (!_sectionRowRefresh.Run(() => RebuildSectionRowsCore(preferredRecordId))) return;
            RefreshSectionSelection();
        }

        private void RebuildSectionRowsCore(string? preferredRecordId)
        {
            if (_plan == null)
            {
                _sectionRows.Clear();
                return;
            }

            var selectedId = preferredRecordId ??
                             (SectionsGrid.SelectedItem as SectionRowViewModel)?.Record.RecordId;
            _sectionRows.Clear();

            var globalPlanningBlockReason = WorkflowGate.From(true, _plan, false,
                null, false).GlobalPlanningBlockReason;
            var repairRecords = _plan.Records.Where(record => _apply is not { Committed: true } && record.Findings.Any(finding =>
                finding.Code == SectionFindingCodes.AnnotationRegistryRepairable)).ToList();
            // Ready rows first — 57 bent-CL review rows buried the 27 the engineer
            // actually works with (live table, 31/08).
            foreach (var record in _plan.Records
                         .OrderBy(x => x.Status == DeliveryStatus.Ready ? 0 : 1)
                         .ThenBy(x => x.Station ?? double.MaxValue))
            {
                _sectionRows.Add(new SectionRowViewModel
                {
                    Record = record,
                    GlobalPlanningBlockReason = globalPlanningBlockReason,
                    CreationBlockReason = record.Status == DeliveryStatus.Ready &&
                        repairRecords.Any(repair => repair.RecordId != record.RecordId)
                        ? repairRecords.Count == 1
                            ? $"תחילה שחזור {repairRecords[0].SectionId}"
                            : "נדרש שחזור אצווה"
                        : null,
                    StatusOverride = _sectionDisplayStatuses.TryGetValue(record.RecordId, out var status)
                        ? status
                        : null,
                });
            }

            SectionsGrid.SelectedItem = selectedId == null
                ? null
                : _sectionRows.FirstOrDefault(r => r.Record.RecordId == selectedId);
        }

        private void SelectBestSectionRow(string? preferredRecordId = null)
        {
            if (_sectionRows.Count == 0)
            {
                SectionsGrid.SelectedItem = null;
                return;
            }

            var preferred = preferredRecordId == null
                ? null
                : _sectionRows.FirstOrDefault(row =>
                    string.Equals(row.Record.RecordId, preferredRecordId, StringComparison.Ordinal));
            var selected = preferred ?? _sectionRows.FirstOrDefault(row =>
                               IsActionableSectionRow(row))
                           ?? _sectionRows.FirstOrDefault(row =>
                               row.Record.Status == DeliveryStatus.Ready)
                           ?? _sectionRows[0];
            SectionsGrid.SelectedItem = selected;
            SectionsGrid.ScrollIntoView(selected);
        }

        private static bool IsActionableSectionRow(SectionRowViewModel row) =>
            row.CanApproveRowSource || row.CanChooseCrossing || row.CanNameSpans ||
            row.CanResolveTrafficDirection || row.CanExclude;

        /// <summary>
        /// Preview uses the same native WCS/DCS and current-viewport contract as Show.
        /// Only its margin is tighter; it must not use the whole split drawing's pixel aspect.
        /// </summary>
        private static bool ZoomPreviewTightly(Document doc, AcadExtents3d ext)
            => NativeViewZoomService.TryZoom(doc, ext, margin: 1.10);

        private void OnShow(object sender, RoutedEventArgs e)
        {
            var doc = Doc();
            if (doc == null) return;
            if (SectionsGrid.SelectedItem is not SectionRowViewModel row) return;

            var handle = SelectedSectionViewHandle(row);
            if (string.IsNullOrEmpty(handle) && _plan != null &&
                row.Record.Action == PlanAction.Unchanged && row.Record.ManualSectionReuse == null)
            {
                try
                {
                    var lookup = ManagedSectionViewLookupService.Resolve(doc, _plan, row.Record);
                    if (!lookup.Found)
                    {
                        SetStatus(lookup.Reason);
                        Log(lookup.Reason);
                        return;
                    }
                    handle = lookup.SectionViewHandle;
                }
                catch (Exception ex) { ShowError("איתור חתך קיים", ex); return; }
            }
            if (string.IsNullOrEmpty(handle))
            {
                // Nothing created yet: show where the section WILL be - the CL line itself.
                if (!ViewZoomPlan.HasFinitePlanBounds(row.Record.Cl.WcsEndpoints))
                {
                    SetStatus("החתך עדיין לא נוצר; גבולות קו ה-CL חסרים או אינם תקינים להצגה.");
                    return;
                }
                if (ZoomToClLines(new[] { row.Record }, 40.0))
                {
                    Log($"מציג את קו ה-CL של {row.SectionId} (החתך עדיין לא נוצר).");
                    SetStatus("מוצג קו CL " + row.SectionId + " — החתך ייווצר ב'החל'");
                }
                else
                {
                    SetStatus("החתך עדיין לא נוצר. קו ה-CL קיים, אך לא ניתן להתמקד בו בתצוגה הפעילה. " +
                              NativeViewZoomService.RecoveryGuidance);
                }
                return;
            }

            try
            {
                // Zoom by the view's real extents (same mechanism as הצג בשרטוט). A ZOOM
                // command string left Civil waiting at the option prompt (seen live
                // 2026-08-19); reading the object and setting the view cannot.
                var db = doc.Database;
                Autodesk.AutoCAD.DatabaseServices.Extents3d? ext = null;
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var id = QuantityLocatorService.Resolve(db, handle);
                    if (!id.IsNull && !id.IsErased &&
                        tr.GetObject(id, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead) is Autodesk.AutoCAD.DatabaseServices.Entity ent)
                    {
                        ext = ent.GeometricExtents;
                        // Include registered owned labels/furniture outside the
                        // SectionView frame. Geometry comes from native bounds,
                        // never from an arbitrary zoom factor or stale layout JSON.
                        if (!string.IsNullOrWhiteSpace(row.Record.LogicalKey))
                        {
                            var annotations = SectionAnnotationRegistry.ReadAnnotationContractEvidence(
                                tr, db, row.Record.LogicalKey);
                            if (!annotations.IsValid || !annotations.EntryExists ||
                                annotations.Entries.Any(item => !item.IsLive || !item.OwnershipValid))
                                throw new InvalidOperationException(
                                    "לא ניתן להציג תוצר מלא: מלאי ההערות חסר או אינו בבעלות מוכחת.");
                            foreach (var item in annotations.Entries)
                            {
                                if (tr.GetObject(item.EntityId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead)
                                    is not Autodesk.AutoCAD.DatabaseServices.Entity annotation)
                                    throw new InvalidOperationException("הערת חתך אינה ניתנת לקריאה.");
                                ext = QuantityLocatorService.Union(ext.Value, annotation.GeometricExtents);
                            }
                        }
                    }
                    // Show is navigation only. Never persist lazy Civil updates.
                    tr.Abort();
                }
                if (ext == null)
                {
                    SetStatus("החתך לא נמצא בשרטוט הנוכחי (ייתכן שנמחק או שהשרטוט הוחלף).");
                    return;
                }
                var v = ext.Value;
                var pad = 0.08 * Math.Max(v.MaxPoint.X - v.MinPoint.X, v.MaxPoint.Y - v.MinPoint.Y);
                var padded = new Autodesk.AutoCAD.DatabaseServices.Extents3d(
                    new Autodesk.AutoCAD.Geometry.Point3d(v.MinPoint.X - pad, v.MinPoint.Y - pad, 0),
                    new Autodesk.AutoCAD.Geometry.Point3d(v.MaxPoint.X + pad, v.MaxPoint.Y + pad, 0));
                if (!QuantityLocatorService.ZoomTo(doc, padded))
                {
                    var message = "חתך " + row.SectionId +
                        " קיים, אך לא ניתן להתמקד בו בתצוגה הפעילה — מצב האימות לא השתנה. " +
                        NativeViewZoomService.RecoveryGuidance;
                    Log(message);
                    Log("מסגור: " + Bidi.Ltr(NativeViewZoomService.LastDiagnostic));
                    SetStatus(message);
                    return;
                }
                // Live 1.2.85/1.2.86 (24.09.2026): the plain fit equalled the saved view, so
                // "nothing moved" could not be told apart from "framing did not engage".
                // The decision line names the branch, the canvas, the palette and the fit.
                Log($"מציג חתך {row.SectionId} (handle {Bidi.Ltr(handle)}).");
                Log("מסגור: " + Bidi.Ltr(NativeViewZoomService.LastDiagnostic));
                SetStatus("מוצג חתך " + row.SectionId);
            }
            catch (Exception ex) { ShowError("הצג חתך", ex); }
        }

        /// <summary>
        /// Zooms the active view to the WCS extents of the given CL lines. A single line
        /// gets a fixed margin in metres so the crossing is readable; a set gets a
        /// relative margin. Returns false for absent/invalid bounds or navigation failure.
        /// </summary>
        private bool ZoomToClLines(IEnumerable<SectionPlanRecord> records, double margin)
        {
            var doc = Doc();
            if (doc == null) return false;
            Autodesk.AutoCAD.DatabaseServices.Extents3d? ext = null;
            foreach (var r in records)
            {
                var p = r.Cl.WcsEndpoints;
                if (!ViewZoomPlan.HasFinitePlanBounds(p)) continue;
                var e = new Autodesk.AutoCAD.DatabaseServices.Extents3d(
                    new Autodesk.AutoCAD.Geometry.Point3d(Math.Min(p[0], p[2]), Math.Min(p[1], p[3]), 0),
                    new Autodesk.AutoCAD.Geometry.Point3d(Math.Max(p[0], p[2]), Math.Max(p[1], p[3]), 0));
                ext = ext == null ? e : QuantityLocatorService.Union(ext.Value, e);
            }
            if (ext == null) return false;
            var v = ext.Value;
            double pad = margin >= 1.0
                ? margin
                : Math.Max(v.MaxPoint.X - v.MinPoint.X, v.MaxPoint.Y - v.MinPoint.Y) * margin;
            var padded = new Autodesk.AutoCAD.DatabaseServices.Extents3d(
                new Autodesk.AutoCAD.Geometry.Point3d(v.MinPoint.X - pad, v.MinPoint.Y - pad, 0),
                new Autodesk.AutoCAD.Geometry.Point3d(v.MaxPoint.X + pad, v.MaxPoint.Y + pad, 0));
            return QuantityLocatorService.ZoomTo(doc, padded);
        }

        private void OnSectionSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_sectionRowRefresh.IsActive) return;
            RefreshSectionSelection();
        }

        private void RefreshSectionSelection()
        {
            if (SectionsGrid.SelectedItem is not SectionRowViewModel row)
            {
                var globalDetail = GlobalSectionDiagnosticDetail();
                SectionDetail.Text = string.IsNullOrWhiteSpace(globalDetail)
                    ? "יש לבחור שורה בטבלה" : globalDetail;
                RefreshGates();
                return;
            }

            var u = row.Record.UtilityCoverage;
            var sb = new StringBuilder();
            var globalDetailText = GlobalSectionDiagnosticDetail();
            if (!string.IsNullOrWhiteSpace(globalDetailText))
                sb.AppendLine(globalDetailText + "\n");
            sb.AppendLine($"מקור CL: {row.ClEvidence}");
            sb.AppendLine($"מועמדים לתוואי: {row.Candidates}");
            sb.AppendLine($"סגנונות: {row.Styles}");
            sb.AppendLine(SectionSurfaceLegendPresentation.DescribePlannedSources(row.Record.PlannedSources));
            sb.AppendLine($"מיקום בפריסה: {row.Layout}");
            if (row.Boundary is { } boundary)
                sb.AppendLine(boundary);
            if (u.Represented.Count > 0) sb.AppendLine($"מערכות מיוצגות: {string.Join(", ", u.Represented)}");
            if (u.NotConfigured.Count > 0) sb.AppendLine($"קיימות בשרטוט אך לא מוגדרות: {string.Join(", ", u.NotConfigured)}");
            if (u.Missing.Count > 0) sb.AppendLine($"מוגדרות אך חסרות: {string.Join(", ", u.Missing)}");
            foreach (var kv in u.Unsupported) sb.AppendLine($"לא נתמכת — {kv.Key}: {kv.Value}");
            foreach (var direction in row.Record.TrafficDirections.OrderBy(d => d.LaneMidOffsetM))
                sb.AppendLine($"כיוון {direction.StripLabel} @ {direction.LaneMidOffsetM:F3}: " +
                              (direction.IsResolved
                                  ? TrafficDirectionReasonText.Flow(direction.Flow) +
                                    (direction.DirectionSource == "arrow" ? " · מחץ במקור" : " · אישור ידני")
                                  : TrafficDirectionReasonText.Describe(direction.State, direction.Reason)));
            foreach (var f in row.Record.Findings) sb.AppendLine(Bidi.FindingLine(f));
            if (!string.IsNullOrWhiteSpace(row.DecisionEvidence))
                sb.AppendLine(row.DecisionEvidence);

            SectionDetail.Text = sb.ToString().TrimEnd();
            RefreshGates();
        }

        // ------------------------------------------------------------- estimate

        private void OnApproveEstimateScope(object sender, RoutedEventArgs e)
        {
            if (_profile == null) return;
            if (!EnsureSavedForAction("אישור מקורות האומדן", OnApproveEstimateScope)) return;
            if (NeedsEstimateProjectStart) { OnScan(sender, e); return; }
            ProfileDecisionScope decisionScope;
            try { decisionScope = CaptureProfileDecisionScope("אישור מקורות האומדן"); }
            catch (Exception ex)
            {
                ShowError("אישור מקורות אומדן", ex);
                return;
            }

            var inventory = EstimateSourceInventoryService.Capture(decisionScope.Document);
            if (!inventory.IsComplete)
            {
                QuantityDetail.Text = inventory.Text;
                ShowError("קריאת רשימת מקורות", new InvalidOperationException(inventory.ReadFailure));
                return;
            }
            var scopeText = "האישור יגדיר את האומדן כך:\n\n" +
                "• למדוד את כל סוגי הישויות הנתמכים רק במקורות שסומנו.\n" +
                "• בחירת XREF כוללת את הענף המקונן שלו; מקור מוחרג אינו נמדד.\n" +
                "• שורת המארח כוללת את הגאומטריה המקומית ואת כמויות מודל ה-Civil.\n\n" +
                "מקור שנבחר ונשאר חסר/לא פתור, שינוי בקובץ או טרנספורמציה שאינה ניתנת למדידה יחסמו את האומדן. " +
                "האישור יישמר בפרופיל עם שמך וזמן האישור, ולאחריו חובה להריץ סריקה חדשה.\n\n" +
                "האישור הוא להיקף הסריקה בלבד — הוא אינו אישור לכמויות או למחירים.";
            var selectionInventory = inventory.ToSelectionInventory();
            var dialog = new EstimateSourceReviewDialog(selectionInventory, decisionScope.Profile.Estimate.SourceSelection, inventory.Text, scopeText);
            if (CivilModalHost.ShowFromPalette(dialog) != true) return;
            var approver = RequireApprover("אישור מקורות האומדן");
            if (approver == null) return;

            try
            {
                RequireProfileDecisionScope(decisionScope);
                var target = decisionScope.ExpectedState.TargetPath;
                var expectedProfileState = decisionScope.ExpectedState;
                var profileForSave = CloneProfileForDecision(decisionScope.Profile);
                var currentInventory = EstimateSourceInventoryService.Capture(decisionScope.Document).ToSelectionInventory();
                if (EstimateSourceSelectionPolicy.InventoryHash(currentInventory) != EstimateSourceSelectionPolicy.InventoryHash(selectionInventory))
                    throw new InvalidOperationException("מצאי המקורות השתנה בזמן האישור — יש לפתוח את בחירת המקורות מחדש");
                var selection = EstimateSourceSelectionPolicy.Approve(currentInventory, dialog.Choices, approver, DateTime.UtcNow);
                var saved = _estimate.ApproveCompleteDiscoveryScope(
                    profileForSave, approver, target, expectedProfileState, selection);
                PublishSavedProfile(saved);

                InvalidateEstimateEvidence("מקורות האומדן אושרו או השתנו — יש להריץ סריקה חדשה");

                Log($"מקורות אומדן אושרו ע\"י {approver}: {EstimateSourceSelectionPolicy.ScopeSummary(selection)} (גרסת פרופיל {saved.NewVersion}).");
                SetStatus("המקורות אושרו — יש להריץ סריקה חדשה");
            }
            catch (Exception ex)
            {
                InvalidateEstimateEvidence(
                    "אישור המקורות לא הושלם באופן מאומת — יש לטעון פרופיל ולסרוק מחדש");
                ReloadProfile();
                ShowError("אישור מקורות אומדן", ex);
            }
            finally { RefreshGates(); }
        }

        private void OnEarthworksDecision(object sender, RoutedEventArgs e)
        {
            if (_profile == null) return;
            if (!EnsureSavedForAction("החלטת עבודות עפר", OnEarthworksDecision)) return;
            ProfileDecisionScope decisionScope;
            try { decisionScope = CaptureProfileDecisionScope("החלטת עבודות עפר"); }
            catch (Exception ex)
            {
                ShowError("החלטת עבודות עפר", ex);
                return;
            }

            var dialog = new EarthworksDecisionDialog(
                EstimateWorkflowService.GetEarthworksDecision(decisionScope.Profile));
            if (CivilModalHost.ShowFromPalette(dialog) != true) return;

            try
            {
                RequireProfileDecisionScope(decisionScope);
                var profileForSave = CloneProfileForDecision(decisionScope.Profile);
                var saved = _estimate.SaveEarthworksDecision(
                    profileForSave,
                    dialog.IncludeEarthworks,
                    dialog.EngineeringReason,
                    dialog.ApprovedBy,
                    decisionScope.ExpectedState.TargetPath, decisionScope.ExpectedState);
                PublishSavedProfile(saved);

                InvalidateEstimateEvidence("החלטת עבודות העפר נשמרה — יש להריץ סריקה חדשה");

                Log(dialog.IncludeEarthworks
                    ? $"עבודות עפר נכללו באומדן (גרסת פרופיל {saved.NewVersion})."
                    : $"עבודות עפר הוגדרו מחוץ להיקף עם סיבה הנדסית (גרסת פרופיל {saved.NewVersion}).");
                SetStatus("החלטת עבודות העפר נשמרה — יש להריץ סריקה חדשה");
            }
            catch (Exception ex)
            {
                InvalidateEstimateEvidence(
                    "החלטת עבודות העפר לא הושלמה באופן מאומת — יש לטעון פרופיל ולסרוק מחדש");
                ReloadProfile();
                ShowError("שמירת החלטת עבודות עפר", ex);
            }
            finally { RefreshGates(); }
        }

        private void OnScan(object sender, RoutedEventArgs e)
        {
            using var diagnostic = EstimateScanTrace.Start("palette");
            EstimateScanTrace.Mark("palette.click.entry");
            if (_pendingWorkflowSaveDocument != null)
            {
                SetStatus("כבר ממתין להשלמת שמירת DWG; לא נשלחה פקודת שמירה נוספת");
                RefreshGates();
                return;
            }
            var doc = Doc();
            if (doc == null) return;
            // Opt-in diagnostic only (MAHOD_SCAN_DIAGNOSTICS=1): which objects change from here to the end of the scan and its
            // palette continuation. Disposed before the trace; it never writes, resets DBMOD, saves or closes.
            using var databaseProbe = ScanDatabaseChangeProbe.Attach(doc);
            EstimateScanTrace.Step("profile.reload", ReloadProfile);
            if (_profile == null) return;
            // Measurement precedes scope decisions. The extractor records real
            // supported geometry plus unresolved scope/source findings; it does not
            // approve sources or treat undecided earthworks as zero/excluded.
            var source = DrawingRevisionTracker.CaptureLive(doc);
            var readiness = EstimateSourceSnapshotPolicy.ForScan(
                source.DrawingPath, source.DrawingHash, source.DbMod, source.Failure);
            if (!readiness.IsReady)
            {
                if (readiness.CanSaveAndResume)
                    OfferExplicitSaveAndResume(doc, readiness);
                else
                    ShowEstimateSourceBlocker(readiness);
                RefreshGates();
                return;
            }

            EstimateScanTrace.Mark("profile.ensure.begin");
            if (!EnsureEstimateProjectForScan(doc)) return;
            if (!EnsureUnitReviewRecorded(doc)) return;
            EstimateScanTrace.Mark("profile.ensure.end");
            ScanDatabaseChangeProbe.SampleDbmod(doc, "profile.ensure.end");
            RunEstimateScan(doc);
            ScanDatabaseChangeProbe.SampleDbmod(doc, "palette.scan.end");
        }

        private void OfferExplicitSaveAndResume(
            Document doc,
            EstimateSourceSnapshotPolicy.ScanSourceReadiness readiness)
        {
            if (!ReferenceEquals(Doc(), doc)) return;
            // Use the same exact-document, token-bound QSAVE continuation as the
            // section decisions. It owns cancellation/failure/close cleanup and
            // rechecks the saved source before starting a fresh scan.
            if (EnsureSavedForAction("סריקת כמויות", OnScan))
                OnScan(this, new RoutedEventArgs());
        }

        internal void ResumeEstimateScanAfterExplicitSave()
        {
            // Compatibility command only: an old queued, untokened command must
            // never consume a newer explicit request or start a scan on its own.
            Log("פקודת המשך שמירה ישנה דולגה — המשך סריקה דורש את מזהה הבקשה הנוכחית.");
        }

        private void ShowEstimateSourceBlocker(
            EstimateSourceSnapshotPolicy.ScanSourceReadiness readiness,
            string prefix = "")
        {
            RtlMessageBox.Show(
                prefix + readiness.Title + "\n\n" + readiness.Detail + "\n\n" +
                "לא נמדדה ולא תומחרה אף כמות מהשרטוט במצב זה.",
                "סריקת האומדן לא התחילה",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SetStatus("הסריקה לא התחילה — " + readiness.Title);
            QuantityDetail.Text = readiness.Title + Environment.NewLine + readiness.Detail;
        }

        private void RunEstimateScan(Document doc)
        {
            using var diagnostic = EstimateScanTrace.Start("palette");
            EstimateScanTrace.Mark("palette.scan.entry");
            var profile = _profile ?? throw new InvalidOperationException(
                "אין פרופיל פרויקט תקין לסריקת האומדן.");
            var profileHash = _profileHash;
            var profileSource = _profileSource ?? throw new InvalidOperationException(
                "מקור פרופיל הפרויקט אינו זמין לסריקת אומדן");
            var profileWriteState = CaptureExpectedProfileState();

            // Revoke actionable evidence before entering Civil. If traversal fails,
            // previous measurements remain explicitly historical, never exportable.
            InvalidateEstimateEvidence("הסריקה הקודמת בוטלה — ממתין לתוצאת סריקה חדשה");
            try
            {
                SetStatus("סורק כמויות…");
                using var log = new StageLog("ui_estimate_scan");
                _estimate.Log = log;

                RunBusy("מודד כמויות כלליות בשרטוט ובהפניות שניתן לאמת…\nלא מאשר היקף, לא מתמחר ולא יוצר חתכים", () =>
                {
                    EstimateScanTrace.Mark("palette.workflow.begin");
                    _scan = _estimate.Scan(
                        doc, profile, profileHash, profileSource,
                        RequireProfileWriteTarget(), profileWriteState);
                    EstimateScanTrace.Mark("palette.workflow.end", _scan.Records.Count);
                    _historicalScan = null;
                    _estimateResultsDrawing = EstimateWorkflowService.DrawingIdentity(doc);
                    _lastEstimateFreshnessReason = null;

                    // Publish measured rows before optional catalog/proposal work.
                    // A price-book failure must never hide successful measurements.
                    ScanDatabaseChangeProbe.SampleDbmod(doc, "palette.workflow.end");
                    EstimateScanTrace.Mark("palette.rows.begin");
                    RebuildQuantityRows();
                    EstimateScanTrace.Mark("palette.rows.end");
                    ScanDatabaseChangeProbe.SampleDbmod(doc, "palette.rows.end");
                    SelectNextQuantityRow();

                    BusyStep("טוען מחירון…");
                    try
                    {
                        var catalog = EstimateScanTrace.Step("catalog.load", () => _estimate.LoadCatalog(profile, profileSource));
                        _catalog = catalog.Snapshot;
                        _catalogFindings = catalog.Findings.ToList();
                    }
                    catch (Exception ex)
                    {
                        _catalog = null;
                        Log("המדידות נשמרו; טעינת המחירון נכשלה: " + ex.Message);
                        _catalogFindings.Add(new DeliveryFinding
                        {
                            Code = "EST-CATALOG-LOAD-FAILED", Domain = "estimate",
                            Severity = FindingSeverity.Error,
                            Title = "הכמויות נמדדו; המחירון לא נטען", Message = ex.Message,
                        });
                    }
                    ScanDatabaseChangeProbe.SampleDbmod(doc, "catalog.load");
                    BusyStep("מכין הצעות מיפוי…");
                    try
                    {
                        var publication = _catalog != null
                            ? _estimate.ProposeAndPublishMappings(_scan, _catalog, profile)
                            : null;
                        _proposals = publication?.Proposals.ToList() ?? new List<MappingProposal>();
                        _projectRuleReviews = (publication?.ProjectRuleReviews ?? Array.Empty<EstimateWorkflowService.ProjectRuleReview>())
                            .ToDictionary(review => review.RuleKey, StringComparer.Ordinal);
                        _projectRuleContext = publication?.ProjectRules;
                        if (publication?.ProjectRules is { State: EstimateWorkflowService.ProjectRuleContextState.Pending
                                or EstimateWorkflowService.ProjectRuleContextState.Stale } rulesContext)
                            _catalogFindings.Add(new DeliveryFinding
                            {
                                Code = "EST-PROJECT-RULES-CONTEXT-PENDING", Domain = "estimate",
                                Severity = FindingSeverity.Warning,
                                Title = "לא הוצעו סעיפים כלליים — הקשר כללי הכמויות של הפרויקט לא אומת",
                                Message = rulesContext.Reason,
                            });
                        if (publication?.EvidenceRefusals is { Count: > 0 } refusals)
                            _catalogFindings.Add(new DeliveryFinding
                            {
                                Code = "EST-EVIDENCE-CONTRADICTS-PROPOSALS", Domain = "estimate",
                                Severity = FindingSeverity.Warning,
                                Title = $"ראיות CAD סותרות — לא הוצעו סעיפים אוטומטית ל-{refusals.Count} קבוצות",
                                Message = string.Join(" | ", refusals.Take(5).Select(refusal =>
                                    $"{refusal.Layer} ({refusal.ObjectCount}): {refusal.Message}")) +
                                    (refusals.Count > 5 ? $" | ועוד {refusals.Count - 5} קבוצות" : string.Empty),
                            });
                    }
                    catch (Exception ex)
                    {
                        _proposals.Clear();
                        ClearProjectRules();
                        Log("המדידות נשמרו; הצעות השיוך לא הוכנו: " + ex.Message);
                        _catalogFindings.Add(new DeliveryFinding
                        {
                            Code = "EST-MAPPING-PROPOSALS-FAILED", Domain = "estimate",
                            Severity = FindingSeverity.Warning,
                            Title = "לא הוכנו הצעות; ניתן לבחור סעיף ידנית", Message = ex.Message,
                        });
                    }
                });
                if (_scan == null) return;

                RebuildQuantityRows();
                SelectNextQuantityRow();
                Log($"סריקה: {_scan.ScannedEntities} ישויות, {_scan.Records.Count} רשומות כמות, " +
                    $"{_proposals.Count} הצעות מיפוי.");
                var blockers = _scan.Findings.Where(EstimatePreflightPolicy.IsBlocking).ToList();
                LogFindingsBounded(blockers);
                LogFindingsBounded(_scan.Findings.Where(f =>
                    f.Code == CorridorMaterialAvailabilityPolicy.UnavailableCode).ToList());
                LogFindingsBounded(_catalogFindings.ToList());
                SetStatus(_catalog == null
                    ? $"נסרקו {_scan.Records.Count} רשומות; המחירון דורש טיפול"
                    : blockers.Count == 0
                        ? $"נסרקו {_scan.Records.Count} רשומות כמות"
                        : ProjectHasBoqRules()
                            ? $"נמדדו {_scan.Records.Count} רשומות — אפשר להפיק כתב כמויות לפי כללים; מה שדורש החלטה מסומן בתוך הקובץ"
                            : $"נמדדו {_scan.Records.Count} רשומות — {blockers.Count} ממצאים לעיון לפני אומדן סופי");
            }
            catch (Exception ex) { ShowError("סריקה", ex); }
            finally { _estimate.Log = null; RefreshGates(); }
        }

        private void SelectNextQuantityRow()
        {
            var next = _quantityRows.FirstOrDefault(row =>
                           !row.IsIgnored && string.IsNullOrWhiteSpace(row.CatalogCode))
                       ?? _quantityRows.FirstOrDefault(row => !row.IsIgnored)
                       ?? _quantityRows.FirstOrDefault();
            SelectQuantityReviewRow(next);
            if (next != null) QuantitiesGrid.ScrollIntoView(next);
        }

        private void RebuildQuantityRows()
        {
            _quantityRows.Clear();
            if (_scan == null) return;

            var ignored = new HashSet<string>(
                _profile == null
                    ? Array.Empty<string>()
                    : IgnoredRulePolicy.ApprovedKeys(_profile),
                StringComparer.Ordinal);

            // Reading order: the decisions that carry the money first. On the real 6422 model
            // the scan found 192 groups and 99.4% of the measured quantity sat in the top 20;
            // discovery order buried them under 135 station-named layers.
            var groups = _scan.Records
                .GroupBy(r => r.Classification.RuleKey ?? "(ללא חוק)")
                .ToList();
            var exactAlternatives = ClosedPolylineAlternativePolicy
                .FindUnambiguousExactPairs(_scan.Records)
                .SelectMany(pair => new[]
                {
                    (RuleKey: pair.FirstRuleKey, Alternative: pair.SecondRuleKey),
                    (RuleKey: pair.SecondRuleKey, Alternative: pair.FirstRuleKey),
                })
                .GroupBy(choice => choice.RuleKey, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single().Alternative,
                    StringComparer.Ordinal);
            var order = QuantitySignificance.Order(groups.Select(g => new QuantitySignificance.Group(
                    g.Key,
                    g.First().Source.Layer,
                    g.First().Measurement.Unit,
                    g.Sum(r => r.Measurement.RawValue),
                    g.Count())))
                .Select((x, i) => (x.RuleKey, Index: i))
                .ToDictionary(x => x.RuleKey, x => x.Index, StringComparer.Ordinal);

            foreach (var group in groups
                         .OrderBy(g => ignored.Contains(g.Key) ? 1 : 0)
                         .ThenBy(g => order.TryGetValue(g.Key, out var i) ? i : int.MaxValue))
            {
                var first = group.First();
                var semanticLayer = SectionProjectionLogic.LayerLeaf(first.Source.Layer);
                var code = EstimateWorkflowService.ApprovedCatalogCodeForDisplay(
                    _profile,
                    group.Key,
                    semanticLayer,
                    first.Measurement.Kind,
                    first.Classification.CandidateCatalogCode);
                var ruleReview = _projectRuleReviews.GetValueOrDefault(group.Key);
                // L05: a group without generic eligibility never carries a proposal (also one kept from before a rebase).
                var proposal = ruleReview?.Governed == true ? null : _proposals.FirstOrDefault(p => p.RuleKey == group.Key);

                string state;
                string? price = null;
                string? description = null;

                if (string.IsNullOrWhiteSpace(code))
                {
                    state = "דרוש מיפוי";
                }
                else if (_catalog != null && _catalog.Items.TryGetValue(code, out var item))
                {
                    description = item.Description;
                    var measured = Units.Parse(first.Measurement.Unit);
                    if (!measured.SameUnit(item.Unit))
                    {
                        state = "אי-התאמת יחידות";
                    }
                    else if (_catalog.Prices.TryGetValue(code, out var pr) && !pr.IsMissing)
                    {
                        state = "מאושר";
                        price = pr.Price!.Value.ToString("N2");
                    }
                    else
                    {
                        state = "חסר מחיר";
                        price = "חסר";
                    }
                }
                else
                {
                    state = "סעיף לא קיים";
                }

                var quantity = group.Sum(r => r.Measurement.RawValue);
                var verdict = QuantitySignificance.Classify(new QuantitySignificance.Group(
                    group.Key, first.Source.Layer, first.Measurement.Unit, quantity, group.Count()));
                var isIgnored = ignored.Contains(group.Key);
                var groupRecordIds = group.Select(r => r.RecordId).ToHashSet(StringComparer.Ordinal);
                var groupFindingCodes = group.SelectMany(r => r.Findings)
                    .Concat(_scan.Findings.Where(f =>
                        f.AffectedRecordIds.Any(groupRecordIds.Contains)))
                    .Select(f => f.Code)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var alternative = exactAlternatives.TryGetValue(group.Key, out var alternativeRuleKey)
                    ? groups.FirstOrDefault(candidate => string.Equals(
                        candidate.Key, alternativeRuleKey, StringComparison.Ordinal))
                    : null;
                string? alternativeCode = null;
                if (alternative != null)
                {
                    var alternativeFirst = alternative.First();
                    alternativeCode = EstimateWorkflowService.ApprovedCatalogCodeForDisplay(
                        _profile,
                        alternative.Key,
                        SectionProjectionLogic.LayerLeaf(alternativeFirst.Source.Layer),
                        alternativeFirst.Measurement.Kind,
                        alternativeFirst.Classification.CandidateCatalogCode);
                }

                var shownState = isIgnored
                    ? "לא רלוונטי"
                    : state == "דרוש מיפוי" && verdict.Kind == QuantitySignificance.Kind.ExistingUtility
                        ? "תשתית קיימת"
                    : state == "דרוש מיפוי" && ruleReview?.Governed == true ? "לפי כללי הפרויקט"
                    : state == "דרוש מיפוי" && proposal != null ? "דרוש מיפוי · הצעה"
                    : state == "דרוש מיפוי" && !verdict.IsLikelyQuantity ? "נראה כסימון/עזר"
                    : state;

                _quantityRows.Add(new QuantityRowViewModel
                {
                    RuleKey = group.Key,
                    Layer = string.IsNullOrWhiteSpace(semanticLayer) ? "—" : semanticLayer,
                    BlockType = first.Measurement.Parameters.TryGetValue("block_name", out var sourceBlockName)
                        ? BlockQuantityGrouping.Name(sourceBlockName) : null,
                    EntityType = first.Source.EntityType,
                    Method = first.Measurement.Method,
                    ObjectCount = group.Count(),
                    Quantity = quantity,
                    Unit = first.Measurement.Unit,
                    CatalogCode = code,
                    CatalogDescription = description,
                    SourceCategory = string.Join(" / ", group.Select(r =>
                        r.Measurement.Parameters.TryGetValue("source_category", out var category) &&
                        EstimateSourceSelectionPolicy.Categories.TryGetValue(category, out var label)
                            ? label : "לא מסווג").Distinct(StringComparer.Ordinal)),
                    ProposedCode = proposal?.ProposedCode,
                    ProposalReason = proposal == null ? null : string.Join(" · ", proposal.Reasons),
                    ProjectRuleNote = ruleReview?.Message,
                    ProjectRuleGoverned = ruleReview?.Governed == true,
                    ProjectRuleLabel = ruleReview?.Label,
                    Price = price,
                    MappingState = shownState,
                    IsIgnored = isIgnored,
                    Verdict = verdict,
                    Findings = string.Join(" | ", groupFindingCodes),
                    AlternativeRuleKey = alternative?.Key,
                    AlternativeQuantityDisplay = alternative == null
                        ? null
                        : $"{alternative.Sum(record => record.Measurement.RawValue):N2} {alternative.First().Measurement.Unit}",
                    AlternativeCatalogCode = alternativeCode,
                });
            }
        }

        private void OnQuantitySelected(object sender, SelectionChangedEventArgs e)
        {
            if (QuantitiesGrid.SelectedItem is not QuantityRowViewModel row)
            {
                QuantityDetail.Text = "יש לבחור שורה בטבלה";
                RefreshGates();
                return;
            }

            var sb = new StringBuilder();
            if (row.IsHistorical)
            {
                sb.AppendLine(row.StatusDisplay);
                sb.AppendLine(row.StatusDetail);
                sb.AppendLine($"ריצת המקור ההיסטורית: {_historicalScan?.RunId}");
            }
            sb.AppendLine($"חוק: {row.RuleKey}");
            sb.AppendLine($"מדידה: {row.MethodDisplay} ({Bidi.Ltr(row.Method)}) · {row.QuantityDisplay} מתוך {row.ObjectCount} עצמים");
            sb.AppendLine($"מצב מיפוי: {row.MappingState}");
            if (row.Verdict is { IsLikelyQuantity: false } v) sb.AppendLine($"סיווג: {v.Reason}");
            if (row.IsIgnored && !row.IsHistorical)
            {
                sb.AppendLine("סומן כלא רלוונטי — נמדד ונשמר במעקב, אך אינו נכלל במסמך המתומחר.");
                var exclusion = EstimateWorkflowService.ApprovedIgnoredRuleDecision(
                    _profile, row.RuleKey);
                if (exclusion != null)
                    sb.AppendLine($"החלטה: {exclusion.Reason} · מאשר: {exclusion.ApprovedBy} · " +
                                  $"{exclusion.ApprovedAtUtc?.ToUniversalTime():yyyy-MM-dd HH:mm} UTC");
            }
            if (row.CatalogCode != null) sb.AppendLine($"סעיף: {row.CatalogCode} — {row.CatalogDescription}");
            if (row.ProposedCode != null)
            {
                sb.AppendLine($"הצעה (לא מאושרת): {Bidi.Ltr(row.ProposedCode)}");
                sb.AppendLine($"  נימוק: {row.ProposalReason}");
            }
            if (row.HasClosedPolylineAlternative)
            {
                sb.AppendLine(row.IsUnselectedClosedPolylineAlternative
                    ? $"חלופת מדידה לאותה גיאומטריה: {row.AlternativeQuantityDisplay}. " +
                      $"החלופה השנייה כבר אושרה לסעיף {Bidi.Ltr(row.AlternativeCatalogCode)}; " +
                      "כדי למנוע כפל יש לאשר שהשורה הנוכחית אינה רלוונטית."
                    : $"אותם פוליליינים סגורים נמדדו גם בחלופה {row.AlternativeQuantityDisplay}. " +
                      "יש לבחור משמעות הנדסית אחת בלבד; הכלי אינו מתמחר את שתי החלופות יחד.");
            }
            if (!string.IsNullOrEmpty(row.Findings)) sb.AppendLine($"ממצאים: {row.Findings}");
            var displayedScan = row.IsHistorical ? _historicalScan : _scan;
            if (displayedScan != null)
            {
                sb.AppendLine($"שרטוט שנרשם: {Ltr(displayedScan.SourceDrawing)}");
                var measurements = displayedScan.Records.Where(record =>
                    (record.Classification.RuleKey ?? "(ללא חוק)") == row.RuleKey)
                    .Select(record => record.Measurement);
                var fields = QuantityCadMetadataPolicy.Summarize(measurements);
                if (fields.Count > 0)
                {
                    sb.AppendLine("נתוני CAD שנקראו מהעצמים — מידע לבדיקה, לא שיוך מחירון:");
                    foreach (var field in fields)
                        sb.AppendLine(Ltr(field.Key) + ": " + Ltr(field.DisplayValue));
                }
            }
            AppendEstimateReviewDetails(sb, row);
            QuantityDetail.Text = sb.ToString().TrimEnd();
            RefreshGates();
        }

        /// <summary>
        /// A Latin file name inside a Hebrew sentence is reordered by the bidi algorithm
        /// ("6422-CIVIL-WEST.dwg" renders as "CIVIL-WEST.dwg-6422"). Wrapping it in an
        /// explicit left-to-right embedding keeps the name exactly as the engineer knows it.
        /// </summary>
        internal static string Ltr(string? s) => Bidi.Ltr(s);

        private object? _quantityLocateScan;
        private string? _quantityLocateRuleKey;
        private int _quantityLocateIndex;

        private void OnQuantityDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton != System.Windows.Input.MouseButton.Left ||
                e.OriginalSource is not DependencyObject source ||
                System.Windows.Controls.ItemsControl.ContainerFromElement(QuantitiesGrid, source)
                    is not System.Windows.Controls.DataGridRow gridRow ||
                gridRow.Item is not QuantityRowViewModel row)
                return;
            // Use the clicked row, never an old selection when the header or empty space is clicked.
            QuantitiesGrid.SelectedItem = row;
            e.Handled = true;
            var doc = Doc();
            if (doc == null || _scan == null || !VerifyEstimateSourcesForAction(doc, "מיקוד אובייקט")) return;
            try
            {
                var records = _scan.Records.Where(r => (r.Classification.RuleKey ?? "(ללא חוק)") == row.RuleKey)
                    .DistinctBy(r => (r.Source.Xref, r.Source.Handle))
                    .OrderBy(r => r.Source.Handle, StringComparer.Ordinal).ToList();
                if (records.Count == 0) { SetStatus("לא נמצאו אובייקטים להצגה ברשומה זו"); return; }
                if (!ReferenceEquals(_quantityLocateScan, _scan) || _quantityLocateRuleKey != row.RuleKey)
                    _quantityLocateIndex = 0;
                var index = _quantityLocateIndex % records.Count;
                var record = records[index];
                var outcome = QuantityLocatorService.Show(doc, new[] { record }, _profile);
                _quantityLocateScan = _scan;
                _quantityLocateRuleKey = row.RuleKey;
                _quantityLocateIndex = (index + 1) % records.Count;
                var message = $"אובייקט {index + 1} מתוך {records.Count} · שכבה {row.Layer} · Handle {record.Source.Handle} · {outcome.Message}";
                SetStatus(message);
                Log(message);
            }
            catch (Exception ex) { ShowError("מיקוד אובייקט", ex); }
        }

        private void OnShowQuantity(object sender, RoutedEventArgs e)
        {
            var doc = Doc();
            if (doc == null || _scan == null) return;
            if (QuantitiesGrid.SelectedItem is not QuantityRowViewModel row) return;
            if (!VerifyEstimateSourcesForAction(doc, "הצג כמות")) return;
            try
            {
                var records = _scan.Records.Where(r => (r.Classification.RuleKey ?? "(ללא חוק)") == row.RuleKey).ToList();
                var outcome = QuantityLocatorService.Show(doc, records, _profile);
                Log($"הצג בשרטוט — {row.Layer}: {outcome.Message}");
                SetStatus(outcome.Message);
            }
            catch (Exception ex) { ShowError("הצג בשרטוט", ex); }
        }

        /// <summary>
        /// Marks a group as "not a construction quantity" (or takes the mark back). The
        /// decision is the engineer's and is persisted in the project profile, so the next
        /// scan — and every future project that reuses the profile — starts already tidy.
        /// The quantity itself is never destroyed: it stays in the scan and in audit.json.
        /// </summary>
        private void OnToggleRelevance(object sender, RoutedEventArgs e)
        {
            if (_profile == null) return;
            var doc = Doc();
            if (doc == null || _scan == null) return;
            if (QuantitiesGrid.SelectedItem is not QuantityRowViewModel row) return;

            var current = EstimateWorkflowService.ApprovedIgnoredRuleDecision(
                _profile, row.RuleKey);
            var wasIgnored = current != null;
            string? reason = null;
            string? approvedBy = null;

            if (wasIgnored)
            {
                var utc = current!.ApprovedAtUtc?.ToUniversalTime();
                var answer = RtlMessageBox.Show(
                    $"להחזיר את הקבוצה לאומדן?\n\n{row.Layer}\n{row.QuantityDisplay}\n\n" +
                    $"החרגה נוכחית: {current.Reason}\n" +
                    $"אושר ע\"י {current.ApprovedBy} · {utc:yyyy-MM-dd HH:mm} UTC\n\n" +
                    "השינוי יישמר בפרופיל והרשימה תעודכן להמשך הבדיקה.",
                    "החזרת קבוצת כמות",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
                approvedBy = RequireApprover("החזרת קבוצה לאומדן");
                if (approvedBy == null) return;
            }
            else
            {
                var dialog = new QuantityExclusionDecisionDialog(
                    row.RuleKey, row.Layer, row.QuantityDisplay, row.ObjectCount,
                    row.IsUnselectedClosedPolylineAlternative
                        ? $"חלופת מדידה לא נבחרת לאותם פוליליינים סגורים; החלופה ההנדסית אושרה בסעיף {row.AlternativeCatalogCode}"
                        : null);
                if (CivilModalHost.ShowFromPalette(dialog) != true) return;
                reason = dialog.EngineeringReason;
                approvedBy = dialog.ApprovedBy;
            }

            try
            {
                // The dialog may have stayed open while the host/XREF/profile changed.
                // Refuse the durable engineering decision against stale quantities.
                EstimateWorkflowService.RequireFreshForDecision(
                    doc, _scan, "שמירת החלטת הרלוונטיות");
                var profileForSave = CloneProfileForDecision(_profile);
                var saved = _estimate.SaveIgnoredRuleDecision(
                    profileForSave, _scan, row.RuleKey, !wasIgnored, reason, approvedBy!,
                    RequireProfileWriteTarget(), _scan.ProfileWriteState ??
                    throw new InvalidOperationException(
                        "לסריקת האומדן אין ראיית CAS של הפרופיל מתחילת העבודה"));
                Log(wasIgnored
                    ? $"הוחזר לרשימה: {Bidi.Ltr(row.Layer)} · אושר ע\"י {approvedBy} (גרסת פרופיל {saved.NewVersion})"
                    : $"סומן כלא רלוונטי: {Bidi.Ltr(row.Layer)} — {row.QuantityDisplay} · אושר ע\"י {approvedBy} (גרסת פרופיל {saved.NewVersion})");
                ContinueEstimateReviewAfterDecision(
                    saved, profileForSave, mappedRuleKey: null, row.RuleKey);
                SetStatus(wasIgnored
                    ? "הקבוצה הוחזרה — אפשר להמשיך בהחלטה הבאה"
                    : "הקבוצה הוחרגה — אפשר להמשיך בהחלטה הבאה");
            }
            catch (Exception ex)
            {
                InvalidateEstimateEvidence(
                    "לא ניתן להמשיך מאותה סריקה — יש להריץ סריקה חדשה");
                ReloadProfile();
                ShowError("סימון רלוונטיות", ex);
            }
            finally { RefreshGates(); }
        }

        private List<QuantityRowViewModel> BulkNoiseCandidates() => _quantityRows
            .Where(row => row.IsBulkNoiseCandidate)
            .OrderBy(row => row.Verdict!.Kind)
            .ThenBy(row => row.RuleKey, StringComparer.Ordinal)
            .ToList();

        private IReadOnlyList<EstimateWorkflowService.ProvenBatchMappingCandidate>
            ProvenBatchMappingCandidates()
        {
            if (_scan == null || _profile == null || _catalog == null ||
                _proposals.Count == 0)
                return Array.Empty<EstimateWorkflowService.ProvenBatchMappingCandidate>();
            return EstimateWorkflowService.ProvenBatchMappingCandidates(
                _scan, _catalog, _profile, _proposals);
        }

        /// <summary>
        /// One reviewed, signed decision replaces the 153-click failure mode seen in
        /// the 6422 acceptance drawing.  The dialog exposes every exact key, measured
        /// quantity and classifier reason.  The service independently reclassifies the
        /// current scan and performs one atomic profile save; this UI cannot broaden
        /// the eligible set or hide existing utilities/real quantities.
        /// </summary>
        private void OnFilterDrawingNoise(object sender, RoutedEventArgs e)
        {
            var doc = Doc();
            if (doc == null || _profile == null || _scan == null) return;
            if (!EstimateWorkflowService.IsReviewedSourceScopeApproved(_profile) ||
                !_scan.DiscoveryMode)
            {
                RtlMessageBox.Show(
                    "סינון מרוכז מותר רק לאחר אישור מקורות וסריקת host+XREF מלאה. " +
                    "יש ללחוץ 'אשר מקורות' ולהריץ סריקה חדשה.",
                    "סינון סימוני עזר",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var candidates = BulkNoiseCandidates();
            if (candidates.Count == 0)
            {
                RtlMessageBox.Show(
                    "אין בסריקה העדכנית קבוצות שהוכחו כסימוני תחנות או עזר. " +
                    "תשתיות קיימות וכמויות בנייה נשארות תמיד לבדיקה פרטנית.",
                    "סינון סימוני עזר",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dialog = new QuantityNoiseBatchDecisionDialog(candidates);
            if (CivilModalHost.ShowFromPalette(dialog) != true) return;

            try
            {
                // The review window may remain open while the DWG/XREF/profile changes.
                // Freshness is evidence and must be rechecked after the human confirms.
                EstimateWorkflowService.RequireFreshForDecision(
                    doc, _scan, "אישור מרוכז של סימוני עזר");

                var requests = candidates.Select(row =>
                    new EstimateWorkflowService.IgnoredRuleDecisionRequest(
                        row.RuleKey, row.Verdict!.Reason)).ToList();
                var profileForSave = CloneProfileForDecision(_profile);
                var saved = _estimate.SaveIgnoredRuleDecisions(
                    profileForSave, _scan, requests, dialog.ApprovedBy,
                    RequireProfileWriteTarget(), _scan.ProfileWriteState ??
                    throw new InvalidOperationException(
                        "לסריקת האומדן אין ראיית CAS של הפרופיל מתחילת העבודה"));

                Log($"אושרו {requests.Count} קבוצות סימוני תחנות/עזר כהחרגות מתועדות " +
                    $"(גרסת פרופיל {saved.NewVersion}).");
                ContinueEstimateReviewAfterDecision(
                    saved, profileForSave, mappedRuleKey: null, candidates[0].RuleKey);
                SetStatus($"סוננו {requests.Count} קבוצות עזר — הכמויות והתשתיות נשארו לבדיקה");
            }
            catch (Exception ex)
            {
                InvalidateEstimateEvidence(
                    "לא ניתן להמשיך מאותה סריקה — יש להריץ סריקה חדשה");
                ReloadProfile();
                ShowError("סינון סימוני עזר", ex);
            }
            finally { RefreshGates(); }
        }

        private void OnApproveMapping(object sender, RoutedEventArgs e)
        {
            if (_scan == null || _profile == null || _catalog == null) return;
            var doc = Doc();
            if (doc == null) return;
            if (QuantitiesGrid.SelectedItem is not QuantityRowViewModel row) return;
            if (ManualMappingCaseScope.RequiresFullReview(_scan.Records, new[] { row.RuleKey }))
            {
                OnReviewMappings(sender, e);
                return;
            }
            if (!row.CanApproveCatalogMapping)
            {
                RtlMessageBox.Show(
                    row.IsIgnored
                        ? "יש להחזיר את הקבוצה לרשימה לפני אישור מיפוי."
                        : "החלופה האחות לאותם פוליליינים סגורים כבר נבחרה. " +
                          "השורה הזו נעולה כהחרגה מתועדת כדי למנוע תמחור area+perimeter כפול.",
                    "אישור מיפוי אינו זמין",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var sample = _scan.Records.First(r => (r.Classification.RuleKey ?? "(ללא חוק)") == row.RuleKey);
            var mappingLayer = SectionProjectionLogic.LayerLeaf(sample.Source.Layer);

            // Any layer, any name: the engineer searches the active price book by Hebrew
            // text or by code, sees unit/price, and approves. Proposals are pre-listed.
            var picker = new CatalogPickerDialog(
                row.RuleKey, string.IsNullOrWhiteSpace(mappingLayer) ? "*" : mappingLayer,
                sample.Measurement.Kind, sample.Measurement.Unit,
                row.Quantity, row.ObjectCount, _catalog,
                _proposals.Where(p => p.RuleKey == row.RuleKey));
            if (CivilModalHost.ShowFromPalette(picker) != true || string.IsNullOrWhiteSpace(picker.SelectedCode)) return;
            var code = picker.SelectedCode;
            if (row.HasClosedPolylineAlternative &&
                RtlMessageBox.Show(
                    $"אישור {row.MethodDisplay} לסעיף {Bidi.Ltr(code)} יבחר את המשמעות ההנדסית " +
                    "של אותם פוליליינים סגורים ויחריג אוטומטית, בשם המאשר ובחותמת זמן, " +
                    $"את החלופה {row.AlternativeQuantityDisplay}.\n\nלהמשיך?",
                    "בחירת חלופת מדידה לפוליליין סגור",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            var approver = RequireApprover("אישור שיוך לסעיף");
            if (approver == null) return;
            try
            {
                // Re-check after the picker closes: source/profile freshness is part
                // of the approval evidence, not merely an EXPORT concern.
                EstimateWorkflowService.RequireFreshForDecision(
                    doc, _scan, "אישור המיפוי");
                var approval = new EstimateWorkflowService.MappingApproval(
                    RuleKey: row.RuleKey,
                    CatalogCode: code.Trim().ToUpperInvariant(),
                    LayerPattern: string.IsNullOrWhiteSpace(mappingLayer) ? "*" : mappingLayer,
                    EntityType: sample.Source.EntityType,
                    MeasurementKind: sample.Measurement.Kind,
                    MeasuredUnit: sample.Measurement.Unit);
                var profileForSave = CloneProfileForDecision(_profile);
                ManualMappingCaseScope.RequireLegacyScopeIsExact(_scan.Records, new[] { row.RuleKey });
                var saved = row.HasClosedPolylineAlternative
                    ? _estimate.SaveApprovedClosedPolylineMapping(
                        profileForSave, _catalog, _scan, approval,
                        row.AlternativeRuleKey!, approver,
                        RequireProfileWriteTarget(), _scan.ProfileWriteState ??
                        throw new InvalidOperationException(
                            "לסריקת האומדן אין ראיית CAS של הפרופיל מתחילת העבודה"))
                    : _estimate.SaveApprovedMappings(
                        profileForSave, _catalog, new[] { approval }, approver,
                        RequireProfileWriteTarget(), _scan.ProfileWriteState ??
                        throw new InvalidOperationException(
                            "לסריקת האומדן אין ראיית CAS של הפרופיל מתחילת העבודה"));

                Log($"מיפוי אושר ע\"י {approver}: {row.RuleKey} → {code} (גרסת פרופיל {saved.NewVersion}).");
                if (row.HasClosedPolylineAlternative)
                    Log($"חלופת המדידה {row.AlternativeRuleKey} הוחרגה באותה שמירה כדי למנוע כפל area/perimeter.");
                ContinueEstimateReviewAfterDecision(
                    saved, profileForSave, row.RuleKey, row.RuleKey);
                SetStatus("המיפוי נשמר — אפשר להמשיך לשורה הבאה");
            }
            catch (Exception ex)
            {
                // Unknown code / unit mismatch land here as an explicit refusal.
                InvalidateEstimateEvidence(
                    "לא ניתן להמשיך מאותה סריקה — יש להריץ סריקה חדשה");
                ReloadProfile();
                Log($"המיפוי נדחה: {ex.Message}");
                RtlMessageBox.Show(ex.Message, "המיפוי נדחה", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { RefreshGates(); }
        }

        private void OnApproveProvenMappings(object sender, RoutedEventArgs e)
        {
            if (_scan == null || _profile == null || _catalog == null) return;
            var doc = Doc();
            if (doc == null) return;

            var displayedCatalog = _catalog;
            var displayed = ProvenBatchMappingCandidates();
            if (ManualMappingCaseScope.RequiresFullReview(_scan.Records,
                    displayed.Select(candidate => candidate.Approval.RuleKey)))
            {
                OnReviewMappings(sender, e);
                return;
            }
            if (displayed.Count == 0)
            {
                RtlMessageBox.Show(
                    "אין כרגע הצעות שעוברות את שער האישור המרוכז. " +
                    "הצעות עמומות, כלליות, ללא מחיר, בעלות יחידה שונה או עם ממצא גאומטרי נשארות לבדיקה פרטנית.",
                    "אישור מיפויים חד־משמעיים",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dialog = new ProvenMappingBatchDecisionDialog(displayed);
            if (CivilModalHost.ShowFromPalette(dialog) != true) return;

            try
            {
                // The engineer can leave this review table open. Re-read every
                // authority immediately before the durable decision: drawing/profile
                // evidence, exact catalog bytes, and the closed candidate set.
                EstimateWorkflowService.RequireFreshForDecision(
                    doc, _scan, "אישור המיפויים המרוכז");
                var verifiedCatalog = _estimate.LoadCatalog(_profile, _scan.ProfileSource);
                if (verifiedCatalog.Snapshot == null ||
                    !string.Equals(displayedCatalog.SnapshotId,
                        verifiedCatalog.Snapshot.SnapshotId,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(displayedCatalog.FileHash,
                        verifiedCatalog.Snapshot.FileHash,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "מהדורת המחירון השתנתה בזמן בדיקת הטבלה — לא נשמר אף מיפוי.");

                var rechecked = EstimateWorkflowService.ProvenBatchMappingCandidates(
                    _scan, verifiedCatalog.Snapshot, _profile, _proposals);
                var displayedIdentity = displayed.Select(CandidateIdentity)
                    .OrderBy(value => value, StringComparer.Ordinal).ToList();
                var recheckedIdentity = rechecked.Select(CandidateIdentity)
                    .OrderBy(value => value, StringComparer.Ordinal).ToList();
                if (!displayedIdentity.SequenceEqual(recheckedIdentity,
                        StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        "רשימת ההצעות החד־משמעיות השתנתה בזמן הבדיקה — לא נשמר אף מיפוי.");

                var profileForSave = CloneProfileForDecision(_profile);
                var approvals = rechecked.Select(candidate => candidate.Approval).ToList();
                // b19: an older proposal for a group the active library now holds (no item) is never saved by the batch.
                EstimateWorkflowService.RequireNoLibraryHeldCandidates(
                    EstimateWorkflowService.LibraryDispositions(_scan, verifiedCatalog.Snapshot!, _profile),
                    approvals.Select(approval => approval.RuleKey));
                ManualMappingCaseScope.RequireLegacyScopeIsExact(_scan.Records,
                    approvals.Select(approval => approval.RuleKey));
                var saved = _estimate.SaveApprovedMappings(
                    profileForSave, verifiedCatalog.Snapshot, approvals,
                    dialog.ApprovedBy, RequireProfileWriteTarget(),
                    _scan.ProfileWriteState ?? throw new InvalidOperationException(
                        "לסריקת האומדן אין ראיית CAS של הפרופיל מתחילת העבודה"));

                Log($"אושרו במפורש {approvals.Count} מיפויים חד־משמעיים " +
                    $"(גרסת פרופיל {saved.NewVersion}).");
                ContinueEstimateReviewAfterDecision(
                    saved, profileForSave,
                    approvals.Select(approval => approval.RuleKey).ToList(),
                    approvals[0].RuleKey);
                SetStatus($"נשמרו {approvals.Count} מיפויים מאומתים — השאר נשארו לבדיקה");
            }
            catch (Exception ex)
            {
                InvalidateEstimateEvidence(
                    "לא ניתן להמשיך מאותה סריקה — יש להריץ סריקה חדשה");
                ReloadProfile();
                ShowError("אישור מיפויים מרוכז", ex);
            }
            finally { RefreshGates(); }

            static string CandidateIdentity(
                EstimateWorkflowService.ProvenBatchMappingCandidate candidate) =>
                string.Join("|", candidate.Approval.RuleKey,
                    candidate.Approval.CatalogCode.ToUpperInvariant(),
                    Units.Parse(candidate.Approval.MeasuredUnit).Canonical,
                    candidate.Price.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    candidate.EvidenceKind);
        }

        private void OnVerifySelected(object sender, RoutedEventArgs e)
        {
            var doc = Doc();
            if (doc == null || _profile == null || _plan == null ||
                SectionsGrid.SelectedItem is not SectionRowViewModel row)
                return;

            if (!BtnVerifySelected.IsEnabled)
            {
                SetStatus("בחר חתך שנוצר בהצלחה או חתך מנוהל ללא שינוי; האימות יבדוק מחדש את המקורות והתוצר.");
                return;
            }

            var section = row.Record.SectionId ?? row.Record.RecordId;
            try
            {
                ResetDisplayedSectionVerification(
                    restoreApplyBaseline: true, row.Record.RecordId);
                var currentProfile = RequireFreshSectionPlan("VERIFY-SELECTED-CURRENT");
                BeginEvidenceAttempt();
                SetStatus($"מאמת חתך נבחר {section}…");
                SectionVerifyResult? verify = null;
                using var log = new StageLog("ui_verify_selected");
                _sections.Log = log;
                RunBusy($"בודק מקורות ותוצר לחתך {section}…\nאם הנתונים השתנו יתבצע תכנון מחדש; עדיין אין אישור אימות", () =>
                {
                    var current = _sections.VerifySelectedCurrent(
                        doc, currentProfile.Profile!, currentProfile.ProfileHash,
                        _plan, row.Record.RecordId);
                    _plan = current.Plan;
                    _apply = current.Applied;
                    _applyPlanRunId = current.ProducerPlanRunId;
                    _sectionResultsDrawing = DrawingScopeIdentity.For(doc);
                    verify = current.Result;
                });
                if (verify == null) return;
                _lastVerifyResult = verify;

                var checks = verify.Records.Sum(result => result.Checks.Count);
                var failed = verify.Records.SelectMany(result =>
                    result.Checks.Where(check => !check.Pass)).ToList();
                var selectedEvidenceLost = EvidenceWriteFailed(verify.Findings);
                var authoritativeVerify = IsAuthoritativeVerify(verify);
                var selectedVerified =
                    !selectedEvidenceLost &&
                    authoritativeVerify &&
                    string.Equals(verify.Scope, "selected-record", StringComparison.Ordinal) &&
                    string.Equals(verify.SelectedRecordId, row.Record.RecordId,
                        StringComparison.Ordinal) &&
                    verify.Records.Count == 1 &&
                    verify.Records[0].Status == DeliveryStatus.Verified;

                foreach (var failure in failed.Take(5))
                    Log($"  ✗ {failure.Check}: צפוי {failure.Expected}, בפועל {failure.Actual}");
                foreach (var finding in verify.Findings)
                    Log($"  {finding.Title}: {finding.Message}");

                _verifySummary = selectedEvidenceLost
                    ? $"✗ קובצי האימות של חתך {section} לא נכתבו לתיקיית הריצה — אין ראיות, אין מסירה"
                    : selectedVerified
                        ? $"אומת חתך נבחר {section} בלבד · {checks} בדיקות עברו ✓ · יתר החתכים לא אומתו"
                        : $"אימות חתך נבחר {section} נכשל — {failed.Count} בדיקות לא עברו";
                foreach (var recordResult in verify.Records)
                    _sectionDisplayStatuses[recordResult.RecordId] = selectedVerified
                        ? recordResult.Status
                        : DeliveryStatus.Failed;
                RebuildSectionRows(row.Record.RecordId);
                Log(_verifySummary);
                if (ReportEvidenceWriteFailure("אימות חתך נבחר", verify.Findings)) return;
                if (selectedVerified)
                {
                    OnShow(sender, e);
                    // The framing decision goes to the stage log too, so a live round can be
                    // judged from the file and not only from a screenshot (live 24.09.2026).
                    log.Info("framing: " + NativeViewZoomService.LastDiagnostic);
                }
                SetStatus(selectedVerified
                    ? $"אומת חתך {section} בלבד — יתר החתכים לא אומתו"
                    : $"אימות חתך {section} נכשל ({failed.Count} בדיקות)");
            }
            catch (SectionsWorkflowService.VerificationRefreshRequiredException ex)
            {
                _plan = ex.CurrentPlan;
                _apply = null;
                _applyPlanRunId = null;
                _lastVerifyResult = null;
                _verifySummary = null;
                _sectionDisplayStatuses.Clear();
                _sectionResultsDrawing = DrawingScopeIdentity.For(doc);
                RebuildSectionRows(row.Record.RecordId);
                var currentRecord = _plan.Records.SingleOrDefault(r => r.RecordId == row.Record.RecordId);
                Log($"התכנון העדכני הוצג; לא בוצע אימות: {ex.Message}");
                if (currentRecord?.Action == PlanAction.Unchanged)
                    SetBlockingStatus($"לא ניתן להוכיח את ראיות היצירה של החתך — האימות חסום: {ex.Message}");
                else
                    SetStatus($"התכנון עודכן — יש להשלים את ההחלטות או את עדכון החתך לפני אימות: {ex.Message}");
            }
            catch (Exception ex)
            {
                ResetDisplayedSectionVerification(
                    restoreApplyBaseline: true, row.Record.RecordId);
                ShowError("אימות חתך נבחר", ex);
            }
            finally { _sections.Log = null; RefreshGates(); }
        }

        /// <summary>
        /// A catalog/scope/mapping mutation changes the meaning of candidate codes.
        /// Clear every actionable artifact together. The exact previous scan may
        /// remain as marked display-only history inside the same drawing.
        /// </summary>
        private void InvalidateEstimateEvidence(string detail)
        {
            var doc = Doc();
            var previous = _scan ?? _historicalScan;
            _historicalScan = EstimateQuantityHistoryPolicy.CanRetain(
                previous?.SourceDrawing, doc == null ? null : EstimateWorkflowService.DrawingIdentity(doc),
                previous?.Records.Count ?? 0) ? previous : null;
            _scan = null;
            _estimateResult = null;
            _catalog = null;
            _catalogFindings.Clear();
            _proposals.Clear();
            ClearProjectRules();
            if (_historicalScan == null)
                _quantityRows.Clear();
            else
                foreach (var row in _quantityRows)
                {
                    row.HistoricalReason = detail;
                    row.Presentation = EstimateQuantityHistoryPolicy.Presentation(detail);
                }
            _estimateResultsDrawing = _historicalScan?.SourceDrawing;
            _lastEstimateFreshnessReason = _historicalScan == null ? null : detail;
            QuantityDetail.Text = _historicalScan == null ? detail :
                EstimateQuantityHistoryPolicy.Presentation(detail).Detail;
        }

        /// <summary>
        /// A mapping/relevance decision changes only classification, never measured
        /// geometry. Rebase the still-fresh source snapshot onto the newly persisted
        /// profile and repaint the grid. Mapping/relevance advances to the next
        /// unresolved group; price review explicitly keeps the reviewed group. This
        /// keeps one deliberate scan for a complete review session instead of one full
        /// Civil/XREF scan per row.
        /// </summary>
        private void ContinueEstimateReviewAfterDecision(
            ProjectProfileWriter.SaveResult saved,
            ProjectProfile savedProfile,
            string? mappedRuleKey,
            string previousRuleKey,
            bool preservePreviousSelection = false)
            => ContinueEstimateReviewAfterDecision(
                saved, savedProfile,
                mappedRuleKey == null ? null : new[] { mappedRuleKey },
                previousRuleKey, preservePreviousSelection);

        private void ContinueEstimateReviewAfterDecision(
            ProjectProfileWriter.SaveResult saved,
            ProjectProfile savedProfile,
            IReadOnlyList<string>? mappedRuleKeys,
            string previousRuleKey,
            bool preservePreviousSelection = false)
        {
            if (_scan == null || _profile == null)
                throw new InvalidOperationException("אין סריקת אומדן פעילה לעדכון.");

            var previousScan = _scan;
            EstimateWorkflowService.ScanResult rebasedScan;
            try
            {
                rebasedScan = EstimateWorkflowService.PublishProfileDecisionOrRestore(
                    previousScan, savedProfile, saved,
                    mappedRuleKeys);
            }
            catch
            {
                // The service has withdrawn the durable write (or blocked because a
                // safe restore was impossible). Never leave the palette on the
                // optimistic in-memory profile that preceded evidence publication.
                ReloadProfile();
                throw;
            }
            _scan = rebasedScan;
            _profileHash = saved.NewHash;
            _profileSource = saved.Path;
            _profileWriteTarget = saved.Path;
            _lastEstimateFreshnessReason = null;
            _estimateResult = null;

            PublishSavedProfile(saved);
            RefreshProjectRuleReviewsAfterRebase();
            RebuildQuantityRows();

            var next = EstimateReviewContinuationSelection.Select(
                _quantityRows, mappedRuleKeys, previousRuleKey, preservePreviousSelection);
            SelectQuantityReviewRow(next);
            if (next != null) QuantitiesGrid.ScrollIntoView(next);
        }

        /// <summary>L05: a decision rebases the scan and changes the profile, so the bound guidance of the old stamp is not
        /// reused; the context is prepared again for the rebased scan (no CAD rescan). On failure the notes are cleared —
        /// a governed group then shows no proposal and no note, never a raw-sum proposal.</summary>
        private void RefreshProjectRuleReviewsAfterRebase()
        {
            if (_scan == null || _catalog == null || _projectRuleContext == null) { ClearProjectRules(); return; }
            try
            {
                var context = _estimate.PrepareProjectRuleContext(_scan, _catalog);
                // L05 and the library's reviews, merged as at publication (Codex 04:04): a held group keeps its reason.
                _projectRuleReviews = (_profile == null
                        ? EstimateWorkflowService.ProjectRuleReviews(_scan.Records, context)
                        : EstimateWorkflowService.ReviewsWithLibrary(_scan, _catalog, _profile, context))
                    .ToDictionary(review => review.RuleKey, StringComparer.Ordinal);
                _projectRuleContext = context;
                if (context.State != EstimateWorkflowService.ProjectRuleContextState.Bound)
                    Log("הנחיות כללי הפרויקט לא חודשו אחרי ההחלטה: " + context.Reason);
            }
            catch (Exception ex)
            {
                ClearProjectRules();
                Log("הנחיות כללי הפרויקט לא חודשו אחרי ההחלטה: " + ex.Message);
            }
        }

        private void OnBuildEstimate(object sender, RoutedEventArgs e)
        {
            if (_scan == null || _profile == null) return;
            var doc = Doc();
            if (doc == null) return;
            if (!VerifyEstimateSourcesForAction(doc, "בניית האומדן")) return;
            // A rebuild is replacement, not an optimistic refresh. If any catalog,
            // calculation or evidence write fails, an older green result must not stay
            // exportable in the palette.
            _estimateResult = null;
            RefreshGates();
            try
            {
                SetStatus("בונה אומדן…");
                var catalog = _estimate.LoadCatalog(_profile, _scan.ProfileSource);
                if (catalog.Snapshot == null)
                {
                    RtlMessageBox.Show(string.Join("\n", catalog.Findings.Select(f => f.Title)),
                        "המחירון לא נטען", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (_catalog == null || !string.Equals(_catalog.SnapshotId, catalog.Snapshot.SnapshotId, StringComparison.Ordinal) ||
                    !string.Equals(_catalog.FileHash, catalog.Snapshot.FileHash, StringComparison.OrdinalIgnoreCase))
                    ClearProjectRules();
                _catalog = catalog.Snapshot;
                _estimateResult = _estimate.BuildForReview(doc, _scan, _catalog, _profile);

                var included = _estimateResult.Lines.Count(l => l.IncludedInTotals);
                Log($"אומדן: {_estimateResult.Lines.Count} שורות, {included} מתומחרות, " +
                    $"סכום שורות מתומחרות {_estimateResult.CleanTotal:N2} ₪, {_estimateResult.ExcludedLineCount} דורשות טיפול.");
                var blockers = EstimatePreflightPolicy.BlockingFindings(_estimateResult);
                SetStatus(blockers.Count == 0
                    ? $"אומדן נבנה — {_estimateResult.CleanTotal:N2} ₪"
                    : included > 0
                        ? $"טיוטה חלקית חושבה: {_estimateResult.CleanTotal:N2} ₪ · {included} שורות בלבד; האומדן המלא חסום"
                        : "אין עדיין שורות שניתן לתמחר — בחר קבוצה והשלם שיוך, מחיר ובדיקת מדידה");
                RebuildQuantityRows();
            }
            catch (Exception ex) { ShowError("בניית אומדן", ex); }
            finally { RefreshGates(); }
        }

        private void OnExportEstimate(object sender, RoutedEventArgs e)
        {
            if (_profile == null || _estimateResult == null || _scan == null) return;
            var doc = Doc();
            if (doc == null) return;
            if (!VerifyEstimateSourcesForAction(doc, "ייצוא האומדן")) return;
            try
            {
                var written = _estimate.Export(doc, _scan, _estimateResult, _profile, null,
                    Path.GetFileName(doc.Name ?? ""));
                Log($"יוצא: {SupportPackage.DisplayPath(written.XlsxPath).Replace(Environment.NewLine, " ")}");
                SetStatus("האומדן יוצא");
                if (RtlMessageBox.ShowPath("נוצר:", written.XlsxPath, "לפתוח?", "ייצוא Excel",
                        MessageBoxButton.YesNo, MessageBoxImage.Information, Path.GetFileName(written.XlsxPath)) == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(written.XlsxPath) { UseShellExecute = true });
                }
            }
            catch (Exception ex) { ShowError("ייצוא", ex); }
        }

        private void OnTrace(object sender, RoutedEventArgs e)
        {
            if (_profile == null || _estimateResult == null || _scan == null) return;
            var doc = Doc();
            if (doc == null || !VerifyEstimateSourcesForAction(doc, "הצגת מעקב")) return;
            if (QuantitiesGrid.SelectedItem is not QuantityRowViewModel row)
            {
                RtlMessageBox.Show("יש לבחור שורת כמות כדי לראות את המעקב.", "מעקב",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var line = _estimateResult.Lines.FirstOrDefault(l =>
            {
                var rec = _scan.Records.FirstOrDefault(r => r.RecordId == l.RecordId);
                return rec != null && (rec.Classification.RuleKey ?? "(ללא חוק)") == row.RuleKey;
            });

            if (line == null)
            {
                RtlMessageBox.Show("אין שורת אומדן לקבוצה הזו.", "מעקב",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var record = _scan.Records.First(r => r.RecordId == line.RecordId);
            var sb = new StringBuilder();
            sb.AppendLine("שרטוט: " + Ltr(record.Source.Drawing));
            sb.AppendLine($"עצם: {record.Source.EntityType} handle {record.Source.Handle} · שכבה {record.Source.Layer}");
            sb.AppendLine($"מדידה: {record.Measurement.Method} = {record.Measurement.RawValue:N3} {record.Measurement.Unit}");
            sb.AppendLine($"כמות גולמית: {line.RawQuantity:N3}");
            foreach (var a in line.Adjustments)
                sb.AppendLine($"מקדם {a.RuleId}: ×{a.Factor} → {a.Output:N3}");
            sb.AppendLine($"כמות לכתב כמויות: {line.BoqQuantity:N3}");
            sb.AppendLine($"סעיף: {line.CatalogCode ?? "—"}");
            sb.AppendLine($"מחיר: {(line.Price?.ToString("N2") ?? "חסר")} · מקור: {line.PriceBookId ?? "—"}");
            sb.AppendLine($"סה\"כ: {(line.Total?.ToString("N2") ?? "לא נכלל")}");
            foreach (var f in line.Findings) sb.AppendLine(Bidi.FindingLine(f));

            RtlMessageBox.Show(sb.ToString(), $"מעקב — {line.LineId}",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // -------------------------------------------------------------- project

        private void OnReloadProfile(object sender, RoutedEventArgs e)
        {
            ReloadProfile();
            Log("הפרופיל נטען מחדש.");
            RefreshGates();
        }

        private void OnOpenProfileFolder(object sender, RoutedEventArgs e)
        {
            var profilesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MahodAI_Civil3D", "civil-delivery", "profiles");
            var runtimeProfile = _profileWriteTarget;
            var runtimeDirectory = string.IsNullOrWhiteSpace(runtimeProfile)
                ? null
                : Path.GetDirectoryName(runtimeProfile);
            var path = runtimeDirectory != null && Directory.Exists(runtimeDirectory)
                ? runtimeProfile!
                : profilesRoot;
            OpenFolder(path);
        }

        private void OnOpenRuns(object sender, RoutedEventArgs e) =>
            OpenFolder(SectionsWorkflowService.RunsRoot);

        private void OnOpenLogs(object sender, RoutedEventArgs e) =>
            OpenFolder(StageLog.DefaultDirectory);

        /// <summary>
        /// Asks Mahod's update channel whether a newer package was published. Nothing is
        /// installed automatically: the engineer sees what changed, and only if she asks is
        /// the setup downloaded — and then verified against the published SHA-256 before it
        /// is offered. Running it stays her decision, with Civil closed.
        /// </summary>
        private void OnCheckUpdates(object sender, RoutedEventArgs e)
        {
            try
            {
                SetStatus("בודק עדכונים…");
                var channel = UpdateService.ReadChannel();
                var installed = UpdateService.InstalledVersion();
                var json = channel == null ? null : UpdateService.FetchManifest(channel, TimeSpan.FromSeconds(15));
                var result = UpdateChannel.Evaluate(installed, channel, json);

                Log($"בדיקת עדכונים: {result.Headline}");
                SetStatus(result.Headline);

                if (!result.CanDownload)
                {
                    var text = $"{result.Headline}\n\n{result.Detail}";
                    if (result.State == UpdateChannel.State.NotConfigured)
                    {
                        // Only reachable if the channel disappeared after the panel loaded.
                        BtnCheckUpdates.Visibility = System.Windows.Visibility.Collapsed;
                        return;
                    }

                    RtlMessageBox.Show(text, "בדיקת עדכונים", MessageBoxButton.OK,
                        result.State == UpdateChannel.State.UpToDate
                            ? MessageBoxImage.Information
                            : MessageBoxImage.Warning);
                    return;
                }

                if (RtlMessageBox.Show(
                        $"{result.Headline}\n\n{result.Detail}\n\nלהוריד את קובץ ההתקנה?",
                        "בדיקת עדכונים", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                    return;

                SetStatus("מוריד עדכון…");
                var dl = UpdateService.Download(channel!, result.Manifest!);
                Log(dl.Ok
                    ? $"עדכון הורד ואומת: {SupportPackage.DisplayPath(dl.Path).Replace(Environment.NewLine, " ")}"
                    : $"הורדת העדכון נכשלה: {dl.Message}");
                SetStatus(dl.Ok ? "העדכון הורד" : "הורדת העדכון נכשלה");

                if (dl.Ok)
                    RtlMessageBox.ShowPath(null, dl.Path, dl.Message, "בדיקת עדכונים", MessageBoxButton.OK,
                        MessageBoxImage.Information, Path.GetFileName(dl.Path));
                else
                    RtlMessageBox.Show(dl.Message, "בדיקת עדכונים", MessageBoxButton.OK, MessageBoxImage.Warning);

                if (dl.Ok) OpenFolder(dl.Path);
            }
            catch (Exception ex) { ShowError("בדיקת עדכונים", ex); }
        }

        private void OnExportSupport(object sender, RoutedEventArgs e)
        {
            try
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MahodAI_Civil3D", "civil-delivery");
                var zip = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"MahodCivilDelivery_Support_{DateTime.Now:yyyyMMdd-HHmmss}.zip");

                var temp = Path.Combine(Path.GetTempPath(), "mcd_support_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);

                // Bounded on purpose: the first real export zipped every run artifact and
                // produced 180 MB, which no engineer can email (2026-08-19).
                var candidates = new List<SupportPackage.Candidate>();
                if (Directory.Exists(root))
                {
                    foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            candidates.Add(new SupportPackage.Candidate(
                                Path.GetRelativePath(root, f), fi.Length, fi.LastWriteTimeUtc));
                        }
                        catch { }
                    }
                }
                var plan = SupportPackage.Choose(candidates);
                foreach (var item in plan.Included)
                {
                    var src = Path.Combine(root, item.RelativePath);
                    var dst = Path.Combine(temp, "civil-delivery", item.RelativePath);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        File.Copy(src, dst, true);
                    }
                    catch { }
                }
                File.WriteAllText(Path.Combine(temp, "MANIFEST.txt"),
                    SupportPackage.Manifest(plan, root), System.Text.Encoding.UTF8);

                System.IO.Compression.ZipFile.CreateFromDirectory(temp, zip);
                Directory.Delete(temp, true);

                var mb = new FileInfo(zip).Length / 1024.0 / 1024.0;
                Log($"חבילת תמיכה נוצרה ({mb:F1} MB, {plan.Included.Count} קבצים): {Bidi.Ltr(zip)}");
                RtlMessageBox.ShowPath(
                    $"נוצר ({mb:F1} MB):", zip,
                    plan.Skipped.Count > 0
                        ? $"{plan.Skipped.Count} קבצים גדולים לא נכללו כדי שאפשר יהיה לשלוח את החבילה (ראי MANIFEST.txt בתוכה)."
                        : "כל הקבצים נכללו.",
                    "ייצוא לתמיכה", MessageBoxButton.OK, MessageBoxImage.Information, Path.GetFileName(zip));
            }
            catch (Exception ex) { ShowError("ייצוא לתמיכה", ex); }
        }

        private static void CopyTree(string src, string dst)
        {
            if (!Directory.Exists(src)) return;
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src))
                CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        private void OpenFolder(string path)
        {
            try
            {
                var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (dir != null && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                else
                    RtlMessageBox.ShowPath("התיקייה עדיין לא קיימת:", path, null, "פתיחת תיקייה",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { ShowError("פתיחת תיקייה", ex); }
        }
    }
}
