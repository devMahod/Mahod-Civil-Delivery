using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Button = System.Windows.Controls.Button;
using Binding = System.Windows.Data.Binding;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using DataGrid = System.Windows.Controls.DataGrid;
using FontFamily = System.Windows.Media.FontFamily;
using FlowDirection = System.Windows.FlowDirection;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Stages explicit human choices in one window. No profile, geometry or price is written here.</summary>
public sealed partial class ManualMappingReviewDialog : Window
{
    public sealed record Group(string RuleKey, string Layer, string EntityType, string MeasurementKind,
        string Unit, double Quantity, int ObjectCount, string? CurrentCode, string? AlternativeRuleKey,
        string? AlternativeQuantityDisplay, string FindingsSummary, IReadOnlyList<MappingProposal> Proposals,
        string? ReadOnlyReason = null,
        IReadOnlyList<QuantityCadMetadataPolicy.FieldSummary>? CadMetadata = null,
        SemanticHintPolicy.Draft? SemanticHint = null, string? SemanticHintRevision = null,
        bool SemanticHintStoreAvailable = true,
        MahodAI.CivilDelivery.Estimate.Recognition.CatalogEvidenceBridge.Evidence? RecognitionEvidence = null);
    public sealed record Choice(string RuleKey, string CatalogCode, string? ExcludedAlternativeRuleKey);
    public sealed record CatalogOption(string Code, string Description, string Unit, decimal? Price,
        string Evidence, bool Compatible)
    {
        public string PriceText => Price?.ToString("N2", CultureInfo.CurrentCulture) ?? "אין מחיר";
    }
    public sealed class GroupRow(Group subject) : INotifyPropertyChanged
    {
        public Group Subject { get; } = subject;
        public string Layer => Subject.Layer;
        public string QuantityText => $"{Subject.Quantity:N2} {Subject.Unit}";
        public string CurrentCode => Subject.CurrentCode ?? "—";
        public string Overview { get; internal set; } = subject.Layer;
        public string OverviewDetail { get; internal set; } = subject.Layer;
        public string ProposalSearch { get; internal set; } = "";
        public bool HasCatalogProposal { get; internal set; }
        public string Draft { get; private set; } = "—";
        public string MappingDisplay => (string.IsNullOrWhiteSpace(Subject.CurrentCode)
            ? "טרם שויך" : "קיים: " + Bidi.Ltr(Subject.CurrentCode)) +
            (Draft == "—" ? "" : "\nלשמירה: " + Bidi.Ltr(Draft));
        private bool _markedForBatch;
        public bool MarkedForBatch
        {
            get => _markedForBatch;
            set
            {
                if (_markedForBatch == value) return;
                _markedForBatch = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MarkedForBatch)));
            }
        }
        internal void SetDraft(Choice? value)
        {
            Draft = value == null ? "—" : value.CatalogCode +
                (value.ExcludedAlternativeRuleKey == null ? "" : " · חלופה נבחרה");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Draft)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MappingDisplay)));
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static readonly Brush PanelBrush = FrozenBrush(29, 35, 47);
    private static readonly Brush LineBrush = FrozenBrush(60, 69, 85);
    private static readonly Brush MutedBrush = FrozenBrush(185, 208, 231);
    private readonly CatalogSnapshot _catalog;
    private readonly Action<IReadOnlyList<Choice>, string>? _saveReviewed;
    private readonly List<GroupRow> _rows;
    private readonly Dictionary<string, Choice> _choices = new(StringComparer.Ordinal);
    internal readonly DataGrid GroupsGrid = CreateGrid(), CatalogGrid = CreateGrid();
    internal readonly TextBox GroupSearch = Input(), CatalogSearch = Input(), Approver = Input();
    internal readonly TextBlock ReviewerLabel = Text("שם הבודק/ת:");
    internal readonly CheckBox Confirm = Toggle("בדקתי את כל הבחירות לשמירה; שיוך אינו אישור המדידה או המחיר."),
        ConfirmAlternative = Toggle(""), ChosenOnly = Toggle("הצג רק בחירות לשמירה");
    internal readonly Button StageButton = ActionButton("הוסף לבחירות ועבור להבא", true),
        RemoveButton = ActionButton("בטל בחירה בשורה"), SaveButton = ActionButton("שמור בחירות", true);
    internal readonly Border SubjectDetails = new() { MaxHeight = 22 };
    internal readonly ScrollViewer DecisionDetails = new() { MaxHeight = 42, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _summary = Text(""), _subject = Text(""), _detail = Text(""),
        _selection = Text("בחר סעיף מהרשימה או חפש לפי תיאור / מספר."), _alternativeDetail = Text(""),
        _validation = Text(""), _results = Text("");
    private bool _loadingGroup;
    private string? _catalogSearchGroupUnit;
    public IReadOnlyList<Choice>? ApprovedChoices { get; private set; }
    public string ApprovedBy => Approver.Text.Trim();

    public ManualMappingReviewDialog(IReadOnlyList<Group> groups, CatalogSnapshot catalog,
        Action<IReadOnlyList<Choice>, string>? saveReviewed = null,
        Func<Group, string, System.Threading.CancellationToken,
            System.Threading.Tasks.Task<SemanticMappingAssistResult>>? suggestWithAi = null,
        string? initialRuleKey = null,
        Action<Group, SemanticHintPolicy.Draft, string>? saveSemanticHint = null)
    {
        if (groups.Count == 0 || groups.Any(group => string.IsNullOrWhiteSpace(group.RuleKey)) ||
            groups.Select(group => group.RuleKey).Distinct(StringComparer.Ordinal).Count() != groups.Count)
            throw new ArgumentException("נדרשות קבוצות כמות בעלות מזהים ייחודיים.", nameof(groups));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _saveReviewed = saveReviewed;
        _suggestWithAi = suggestWithAi;
        _saveSemanticHint = saveSemanticHint;
        foreach (var group in groups)
            if (group.SemanticHint != null) _semanticDrafts[group.RuleKey] = group.SemanticHint with { };
        _rows = groups.Select(group => new GroupRow(group)).ToList();
        InitializeGroupOverviews();
        Title = "בדיקת שיוכים — בחירה ידנית מרוכזת";
        Width = 1180; Height = 780; MinWidth = 900; MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SourceInitialized += (_, _) => FitReviewWindowToMonitor();
        FlowDirection = FlowDirection.RightToLeft; FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        Background = new SolidColorBrush(Color.FromRgb(20, 24, 33)); Foreground = Brushes.White;
        var root = new DockPanel { Margin = new Thickness(14) };
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top);
        var title = Text("1 בחר קבוצה · 2 בחר סעיף · 3 הוסף לבחירות · 4 אשר ושמור", 16);
        title.Margin = new Thickness(0);
        title.ToolTip = "בדוק או חפש סעיף לכל קבוצה. השיוכים נשמרים רק באישור בסיום.";
        heading.Children.Add(title);
        _summary.Margin = new Thickness(0);
        heading.Children.Add(_summary);
        AddSemanticAssistance(heading);
        root.Children.Add(heading);

        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom);
        Confirm.ToolTip = "הצעות הן נקודת פתיחה בלבד. מחירים חסרים וממצאי מדידה נשארים פתוחים גם לאחר השיוך.";
        footer.Children.Add(Confirm);
        var approval = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        approval.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        approval.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        approval.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var actions = new WrapPanel(); Grid.SetColumn(actions, 2);
        actions.Children.Add(SaveButton);
        var cancel = ActionButton("סגור — בלי לשמור בחירות"); cancel.IsCancel = true;
        cancel.Click += (_, _) => Close(); actions.Children.Add(cancel); approval.Children.Add(actions);
        ReviewerLabel.Margin = new Thickness(0, 0, 8, 0); ReviewerLabel.VerticalAlignment = VerticalAlignment.Center;
        approval.Children.Add(ReviewerLabel);
        Approver.MinWidth = 170; Grid.SetColumn(Approver, 1); approval.Children.Add(Approver); footer.Children.Add(approval);
        _validation.Foreground = Brushes.Orange; footer.Children.Add(_validation); root.Children.Add(footer);

        var body = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        var groupsPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var groupFilters = new StackPanel { Margin = new Thickness(0, 0, 0, 3) }; DockPanel.SetDock(groupFilters, Dock.Top);
        var groupSearchRow = new DockPanel();
        var searchLabel = Text("חיפוש:"); searchLabel.VerticalAlignment = VerticalAlignment.Center;
        searchLabel.Margin = new Thickness(0, 0, 6, 0); DockPanel.SetDock(searchLabel, Dock.Right);
        groupSearchRow.Children.Add(searchLabel); groupSearchRow.Children.Add(GroupSearch);
        GroupSearch.ToolTip = "חיפוש בשם השכבה, בסעיף, בתיאור ההצעה או בנימוקים. סינון אינו אישור או החרגה.";
        groupFilters.Children.Add(groupSearchRow);
        AddGroupReviewFilters(groupFilters);
        AddBatchReview(groupFilters); groupsPanel.Children.Add(groupFilters);
        var sourceDetails = new Expander { Header = "פרטי המקור והממצאים", Foreground = MutedBrush,
            Content = new ScrollViewer { Content = _detail, MaxHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        DockPanel.SetDock(sourceDetails, Dock.Bottom); groupsPanel.Children.Add(sourceDetails);
        AddColumn(GroupsGrid, "שכבה והצעה לבדיקה", nameof(GroupRow.Overview), 1, true, 145,
            compact: true, tooltipProperty: nameof(GroupRow.OverviewDetail), previewLines: 3);
        AddColumn(GroupsGrid, "כמות", nameof(GroupRow.QuantityText), 74);
        AddColumn(GroupsGrid, "שיוך ובחירה", nameof(GroupRow.MappingDisplay), 94);
        groupsPanel.Children.Add(GroupsGrid); body.Children.Add(groupsPanel);

        var editor = new DockPanel { Margin = new Thickness(10, 0, 0, 8) }; Grid.SetColumn(editor, 1);
        var subjectPanel = new StackPanel(); DockPanel.SetDock(subjectPanel, Dock.Top);
        // Full source identity/count remain available in the tooltip, without a
        // tiny scrollbar or displacing catalog rows at the minimum review size.
        _subject.FontWeight = FontWeights.SemiBold; _subject.Margin = new Thickness(0);
        _subject.TextWrapping = TextWrapping.NoWrap; _subject.TextTrimming = TextTrimming.CharacterEllipsis;
        SubjectDetails.Child = _subject;
        subjectPanel.Children.Add(SubjectDetails);
        subjectPanel.Children.Add(Text("חפש סעיף במחירון לפי תיאור או מספר (לפחות שני תווים)"));
        subjectPanel.Children.Add(CatalogSearch);
        subjectPanel.Children.Add(_results); editor.Children.Add(subjectPanel);
        var decision = new StackPanel { Margin = new Thickness(0, 6, 0, 0) }; DockPanel.SetDock(decision, Dock.Bottom);
        var evidence = new StackPanel(); evidence.Children.Add(_alternativeDetail); evidence.Children.Add(_selection);
        DecisionDetails.Content = evidence; decision.Children.Add(DecisionDetails);
        // Keep the explicit alternative decision and actions outside the scrolling
        // explanation; neither is hidden by a long proposal or source description.
        decision.Children.Add(ConfirmAlternative);
        var chooseActions = new WrapPanel(); chooseActions.Children.Add(StageButton); chooseActions.Children.Add(RemoveButton);
        decision.Children.Add(chooseActions); editor.Children.Add(decision);
        AddColumn(CatalogGrid, "סעיף", nameof(CatalogOption.Code), 105);
        AddColumn(CatalogGrid, "תיאור", nameof(CatalogOption.Description), 1, true, 180);
        AddColumn(CatalogGrid, "יחידה", nameof(CatalogOption.Unit), 65);
        AddColumn(CatalogGrid, "מחירון", nameof(CatalogOption.PriceText), 82);
        editor.Children.Add(CatalogGrid); body.Children.Add(editor); root.Children.Add(body); Content = root;

        GroupsGrid.SelectionChanged += (_, _) => LoadGroup();
        CatalogGrid.SelectionChanged += (_, _) => RefreshSelection();
        CatalogSearch.TextChanged += (_, _) => RefreshCatalog();
        GroupSearch.TextChanged += (_, _) => RefreshGroups();
        ChosenOnly.Checked += (_, _) => RefreshGroups(); ChosenOnly.Unchecked += (_, _) => RefreshGroups();
        Confirm.Checked += (_, _) => { ApprovedChoices = null; RefreshSave(); };
        Confirm.Unchecked += (_, _) => { ApprovedChoices = null; RefreshSave(); };
        Approver.TextChanged += (_, _) => { ApprovedChoices = null; RefreshSave(); };
        ConfirmAlternative.Checked += (_, _) => AlternativeChanged();
        ConfirmAlternative.Unchecked += (_, _) => AlternativeChanged();
        StageButton.Click += (_, _) => StageSelection(advance: true);
        RemoveButton.Click += (_, _) => RemoveSelection();
        SaveButton.Click += (_, _) => { if (TryConfirm()) DialogResult = true; };
        RefreshGroups();
        // A supplied key is an exact scan-group identity, not a layer-name hint.
        // Missing/stale keys must not silently open an unrelated first group.
        GroupsGrid.SelectedItem = initialRuleKey == null ? _rows[0] :
            _rows.FirstOrDefault(row => string.Equals(row.Subject.RuleKey, initialRuleKey, StringComparison.Ordinal));
        if (GroupsGrid.SelectedItem == null) LoadGroup();
        GroupsGrid.Loaded += (_, _) =>
        {
            if (GroupsGrid.SelectedItem is GroupRow row) GroupsGrid.ScrollIntoView(row);
        };
        RefreshSave();
        UiGuard.Attach(this, "בדיקת שיוכים מרוכזת");
    }

    private void RefreshGroups()
    {
        var selected = (GroupsGrid.SelectedItem as GroupRow)?.Subject.RuleKey;
        var term = GroupSearch.Text.Trim();
        GroupsGrid.ItemsSource = _rows.Where(row => MatchesReviewFilters(row) &&
            (ChosenOnly.IsChecked != true || _choices.ContainsKey(row.Subject.RuleKey)) &&
            (term.Length == 0 || row.Layer.Contains(term, StringComparison.OrdinalIgnoreCase) ||
             row.Subject.RuleKey.Contains(term, StringComparison.OrdinalIgnoreCase) ||
             row.CurrentCode.Contains(term, StringComparison.OrdinalIgnoreCase) || row.Draft.Contains(term, StringComparison.OrdinalIgnoreCase) ||
             row.ProposalSearch.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        GroupsGrid.SelectedItem = GroupsGrid.Items.Cast<GroupRow>().FirstOrDefault(row => row.Subject.RuleKey == selected);
        _summary.Text = $"מוצגות {GroupsGrid.Items.Count:N0} מתוך {_rows.Count:N0} קבוצות · {_choices.Count:N0} בחירות לשמירה · " +
            $"מחירון {Bidi.Ltr(_catalog.SnapshotId)} · השיוכים נשמרים רק באישור בסיום";
        RefreshBatchReview();
    }

    private void LoadGroup()
    {
        RememberSemanticDraft();
        CancelSemanticAssistance();
        _loadingGroup = true;
        try
        {
            // Preserve the user's catalog query across groups. RefreshCatalog still
            // clears the selected item; no proposed or previously selected code is approved.
            if (GroupsGrid.SelectedItem is GroupRow next)
            {
                if (_catalogSearchGroupUnit != null && !Units.Parse(_catalogSearchGroupUnit).SameUnit(Units.Parse(next.Subject.Unit)))
                    CatalogSearch.Text = "";
                _catalogSearchGroupUnit = next.Subject.Unit;
            }
            LoadSemanticDraft();
            AiStatus.Text = "";
            if (GroupsGrid.SelectedItem is not GroupRow row)
            {
                _subject.Text = "בחר קבוצה מהרשימה"; _detail.Text = ""; _alternativeDetail.Text = ""; CatalogGrid.ItemsSource = null;
                _alternativeDetail.Visibility = Visibility.Collapsed;
                ConfirmAlternative.Visibility = Visibility.Collapsed; RefreshSelection(); return;
            }
            var group = row.Subject;
            if (!group.SemanticHintStoreAvailable)
                AiStatus.Text = "התיאור המקומי השמור אינו זמין. אפשר לתאר ולחפש ידנית; יש לתקן את קובץ התיאור לפני שמירה נוספת.";
            _subject.Text = $"{Bidi.Ltr(group.Layer)} · {row.QuantityText} · {group.ObjectCount:N0} עצמים";
            _subject.ToolTip = SubjectDetails.ToolTip = _subject.Text; DecisionDetails.ScrollToTop();
            _detail.Text = $"קבוצה: {Bidi.Ltr(group.RuleKey)}\nשיוך קיים: {Bidi.Ltr(group.CurrentCode ?? "אין")}\n" +
                (group.ReadOnlyReason == null ? "" : group.ReadOnlyReason + "\n") + group.FindingsSummary +
                "\n\n" + row.OverviewDetail;
            ConfirmAlternative.Visibility = group.AlternativeRuleKey == null ? Visibility.Collapsed : Visibility.Visible;
            _alternativeDetail.Visibility = ConfirmAlternative.Visibility;
            _alternativeDetail.Text = group.AlternativeRuleKey == null ? "" :
                $"בחירה ב־{group.Unit} תחריג את החלופה {group.AlternativeQuantityDisplay ?? group.AlternativeRuleKey} של אותם עצמים.";
            ConfirmAlternative.Content = Text($"מאשר {group.Unit} והחרגת החלופה המפורטת מעל");
            ConfirmAlternative.IsChecked = _choices.TryGetValue(group.RuleKey, out var choice) && choice.ExcludedAlternativeRuleKey != null;
            RefreshCatalog();
        }
        finally { _loadingGroup = false; RefreshSelection(); RefreshSemanticButton(); }
    }

    private void RefreshCatalog()
    {
        if (GroupsGrid.SelectedItem is not GroupRow row) return;
        var group = row.Subject; var term = CatalogSearch.Text.Trim();
        IEnumerable<(CatalogItem Item, string Why)> items;
        if (term.Length < 2)
        {
            items = (_semanticProposals.GetValueOrDefault(group.RuleKey) ?? group.Proposals)
                .OrderByDescending(proposal => proposal.Score)
                .DistinctBy(proposal => proposal.ProposedCode, StringComparer.OrdinalIgnoreCase)
                .Where(proposal => _catalog.Items.ContainsKey(proposal.ProposedCode))
                .Select(proposal => (_catalog.Items[proposal.ProposedCode], "הצעה בלבד: " + string.Join("; ", proposal.Reasons)));
        }
        else
        {
            var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            items = _catalog.Items.Values.Where(item => item.Code.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                words.All(word => item.Description.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(item => item.Unit.SameUnit(Units.Parse(group.Unit)))
                .ThenBy(item => item.Code, StringComparer.Ordinal).Take(400).Select(item => (item, "חיפוש ידני במחירון"));
        }
        var options = items.Select(value => new CatalogOption(value.Item.Code, value.Item.Description, value.Item.UnitRaw,
            _catalog.Prices.TryGetValue(value.Item.Code, out var price) ? price.Price : null, value.Why,
            value.Item.Unit.SameUnit(Units.Parse(group.Unit)))).ToArray();
        CatalogGrid.ItemsSource = options; CatalogGrid.SelectedItem = null;
        _results.Text = options.Length == 0 ? "אין תוצאות — ניתן לחפש סעיף אחר" :
            options.Length == 400 ? "400 תוצאות לכל היותר — צמצם את החיפוש" : $"{options.Length} תוצאות; בחר ובדוק לפני הוספה";
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        RefreshBatchReview();
        StageButton.IsEnabled = false;
        var row = GroupsGrid.SelectedItem as GroupRow;
        RemoveButton.IsEnabled = row != null && _choices.ContainsKey(row.Subject.RuleKey);
        if (row == null) { _selection.Text = "בחר קבוצה מהרשימה."; return; }
        if (row.Subject.ReadOnlyReason != null) { _selection.Text = row.Subject.ReadOnlyReason; return; }
        if (CatalogGrid.SelectedItem is not CatalogOption option) { _selection.Text = "בחר סעיף מהרשימה או חפש לפי תיאור / מספר."; return; }
        if (!option.Compatible) { _selection.Text = $"לא ניתן לשייך: הכמות ב־{row.Subject.Unit}, הסעיף ב־{option.Unit}."; return; }
        _selection.Text = $"{Bidi.Ltr(option.Code)} · {option.Evidence}\nיחידה תואמת" +
            (option.Price == null ? " · המחיר חסר; ניתן לשייך, אך עדיין אין תמחור." : $" · מחירון: {option.Price:N2}");
        if (row.Subject.AlternativeRuleKey != null && ConfirmAlternative.IsChecked != true)
        {
            _selection.Text += "\nיש לאשר במפורש את בחירת חלופת המדידה למטה."; return;
        }
        if (_choices.Values.Any(choice => choice.RuleKey == row.Subject.AlternativeRuleKey ||
            choice.ExcludedAlternativeRuleKey == row.Subject.RuleKey))
        {
            _selection.Text += "\nהחלופה השנייה כבר נבחרה. בטל את בחירתה לפני החלפה."; return;
        }
        StageButton.IsEnabled = true;
    }

    internal bool StageSelection(bool advance = false)
    {
        RefreshSelection();
        if (!StageButton.IsEnabled || GroupsGrid.SelectedItem is not GroupRow row || CatalogGrid.SelectedItem is not CatalogOption option)
            return false;
        var choice = new Choice(row.Subject.RuleKey, option.Code, row.Subject.AlternativeRuleKey);
        _choices[choice.RuleKey] = choice; row.SetDraft(choice); row.MarkedForBatch = false; ResetConfirmation(); RefreshGroups();
        if (advance)
        {
            // Existing approved mappings remain explicitly editable, but are not
            // unresolved work and must not pull automatic continuation backwards.
            GroupsGrid.SelectedItem = GroupsGrid.Items.Cast<GroupRow>().FirstOrDefault(candidate =>
                candidate.Subject.ReadOnlyReason == null && string.IsNullOrWhiteSpace(candidate.Subject.CurrentCode) &&
                !_choices.ContainsKey(candidate.Subject.RuleKey) &&
                !_choices.Values.Any(value => value.ExcludedAlternativeRuleKey == candidate.Subject.RuleKey));
            if (GroupsGrid.SelectedItem is GroupRow next) GroupsGrid.ScrollIntoView(next);
        }
        RefreshSelection(); return true;
    }

    private void RemoveSelection()
    {
        if (GroupsGrid.SelectedItem is not GroupRow row) return;
        _choices.Remove(row.Subject.RuleKey); row.SetDraft(null); ResetConfirmation(); RefreshGroups(); RefreshSelection();
    }

    private void AlternativeChanged()
    {
        if (_loadingGroup) return;
        if (GroupsGrid.SelectedItem is GroupRow row && _choices.Remove(row.Subject.RuleKey))
        {
            row.SetDraft(null); ResetConfirmation(); RefreshGroups();
        }
        RefreshSelection();
    }

    private void ResetConfirmation() { ApprovedChoices = null; Confirm.IsChecked = false; RefreshSave(); }
    private void RefreshSave()
    {
        SaveButton.Content = $"אשר ושמור {_choices.Count} בחירות";
        SaveButton.IsEnabled = _choices.Count > 0 && Confirm.IsChecked == true && ApprovedBy.Length > 0 &&
            ApprovedBy.IndexOfAny(new[] { '\r', '\n' }) < 0 && !_rows.Any(row => row.MarkedForBatch);
        _validation.Text = _rows.Any(row => row.MarkedForBatch) ? "הסימון המרוכז עדיין אינו בחירה לשמירה: הוסף את הסעיף למסומנות או נקה את הסימון." :
            _choices.Count == 0 ? "לא נוספו בחירות לשיוך. תיאור נשמר רק בלחיצה על 'שמור משמעות'." :
            Confirm.IsChecked != true ? "הבחירות טרם נשמרו — בדוק אותן ואשר במפורש." :
            !SaveButton.IsEnabled ? "יש להזין שם מאשר בשורה אחת." : "ישמרו רק הבחירות שנוספו; יתר הקבוצות יישארו ללא שינוי.";
    }
    internal bool TryConfirm()
    {
        ApprovedChoices = null; RefreshSave();
        if (!SaveButton.IsEnabled) return false;
        var choices = _choices.Values.OrderBy(choice => choice.RuleKey, StringComparer.Ordinal).ToArray();
        try
        {
            _saveReviewed?.Invoke(choices, ApprovedBy);
            ApprovedChoices = choices;
            return true;
        }
        catch (Exception ex)
        {
            // Keep the user's staged choices visible/editable on an ordinary refusal.
            // The caller retains source/context validation and atomic publication.
            _validation.Text = "השמירה לא הושלמה. הבחירות נשארו בחלון לתיקון: " + ex.Message;
            return false;
        }
    }

    private static Brush FrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static TextBlock Text(string value, double size = 13) => new()
    { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = size, Foreground = MutedBrush, Margin = new Thickness(0, 3, 0, 3) };
    private static TextBox Input() => new()
    { Background = PanelBrush, Foreground = Brushes.White, BorderBrush = LineBrush, Padding = new Thickness(7), MinHeight = 32 };
    private static CheckBox Toggle(string value) => new()
    { Content = Text(value), Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 3) };
    private static Button ActionButton(string value, bool primary = false)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(presenter);
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Button)) { VisualTree = border }));
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.45)); style.Triggers.Add(disabled);
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 0.85)); style.Triggers.Add(hover);
        return new Button { Content = value, Style = style,
            Background = primary ? new SolidColorBrush(Color.FromRgb(47, 125, 222)) : PanelBrush,
            Foreground = Brushes.White, BorderBrush = LineBrush, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 4, 6, 0) };
    }
    private static DataGrid CreateGrid()
    {
        var result = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
            Background = PanelBrush, Foreground = Brushes.White, BorderBrush = LineBrush, RowBackground = PanelBrush,
            AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(37, 44, 57)), GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = LineBrush, EnableRowVirtualization = true,
        };
        var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        header.Setters.Add(new Setter(Control.BackgroundProperty, LineBrush));
        header.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6)));
        result.ColumnHeaderStyle = header;
        return result;
    }
    private static void AddColumn(DataGrid grid, string title, string property, double width, bool star = false, double min = 0,
        bool compact = false, string? tooltipProperty = null, int previewLines = 0)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, compact ? TextWrapping.NoWrap : TextWrapping.Wrap));
        if (compact)
        {
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(property)));
        }
        if (tooltipProperty != null)
            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(tooltipProperty)));
        if (previewLines > 0)
        {
            style.Setters.Add(new Setter(TextBlock.LineHeightProperty, 17d));
            style.Setters.Add(new Setter(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight));
            style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, previewLines * 17d));
        }
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(5)));
        grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property),
            Width = new DataGridLength(width, star ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel),
            MinWidth = min, ElementStyle = style });
    }
}
