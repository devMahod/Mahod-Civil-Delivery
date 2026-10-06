using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Bidi = MahodAI.CivilDelivery.Shared.Bidi;
using Binding = System.Windows.Data.Binding;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using DataGrid = System.Windows.Controls.DataGrid;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Image = System.Windows.Controls.Image;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using TextBox = System.Windows.Controls.TextBox;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// Review-only presentation of recognition proposals and of the profile's saved family decisions, built in code like the
/// other testable dialogs. Nothing starts checked, and an abstained group starts without a family. Closing with a result
/// asks for exactly one action (<see cref="ReviewAction"/>): approve the checked families, or revoke one saved decision.
/// The caller turns it into profile decisions and revalidates them against the scan.
/// </summary>
public sealed partial class FamilyDecisionsDialog : Window
{
    private static readonly Brush PanelBrush = FrozenBrush(29, 35, 47);
    private static readonly Brush LineBrush = FrozenBrush(60, 69, 85);
    private static readonly Brush MutedBrush = FrozenBrush(185, 208, 231);
    private static readonly Brush WarningBrush = FrozenBrush(224, 164, 76);
    private static readonly Brush PrimaryBrush = FrozenBrush(47, 125, 222);
    private readonly FamilyDecisionReviewModel _model;
    private readonly EngineerBoqLibrary _library;
    private readonly FamilyAssistant? _assistant;
    private readonly UIElement _reasonLine;
    private readonly UIElement _revokeReasonLine;
    private readonly DockPanel _savedView = new();
    private CancellationTokenSource? _assistCancellation;
    private VisionImage? _preview;

    /// <summary>
    /// The assistant as the palette offers it: the group, the local proposals, the engineer's words and (only with a permit
    /// for exactly the shown image) a shape preview. Null when no assistant is configured.
    /// </summary>
    public delegate Task<IReadOnlyList<RecognitionProposal>> FamilyAssistant(RecognitionGroupInput group,
        IReadOnlyList<RecognitionProposal> localProposals, string? engineerContext, VisionPayload? vision, CancellationToken cancellationToken);

    internal readonly DataGrid RowsGrid = CreateGrid();
    internal readonly TextBox ReasonBox = Input(), ApproverBox = Input();
    internal readonly CheckBox ConfirmBox = Toggle(
        "בדקתי את הראיות של כל הקבוצות המסומנות ואני מאשר במפורש שהן המשפחה שנבחרה. ידוע לי שאישור משפחה אינו מאשר סעיפים או מחירים.");
    internal readonly Button BtnSave = ActionButton("אשר משפחות לקבוצות המסומנות", true), BtnSelectProposed = ActionButton("סמן את כל ההצעות"),
        BtnCancel = ActionButton("ביטול");
    internal readonly TextBlock SummaryText = Text(""), ValidationText = Text(""), AssistStatus = Text("");
    internal readonly TextBox AssistContext = Input();
    internal readonly Button BtnAskAi = ActionButton("שאל את העוזר על הקבוצה המסומנת");
    internal readonly Button BtnAskAiBatch = ActionButton("הצעות AI לכל הקבוצות — בדיקה באצווה");
    internal readonly CheckBox SendPreview = Toggle("לצרף תמונת צורה של הקבוצה (רק הצורה: בלי קואורדינטות, טקסט או שמות). אני מאשר לשלוח את התמונה המוצגת.");
    internal readonly Image PreviewImage = new() { Width = 96, Height = 96, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    internal readonly Expander AssistExpander = new()
    {
        Header = new TextBlock { Text = "עוזר AI לקבוצות שלא הוכרעו (לא חובה)", Foreground = Brushes.White },
        IsExpanded = false, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6),
    };
    internal readonly ItemsControl StaleList = new() { Margin = new Thickness(0, 0, 0, 6) };

    // Saved decisions: listed with what they cover now, and one of them can be revoked with a reason and a confirmation.
    internal readonly DataGrid SavedGrid = CreateGrid();
    internal readonly TextBox RevokeReasonBox = Input();
    internal readonly CheckBox RevokeConfirmBox = Toggle(
        "אני מבטל במפורש את החלטת המשפחה המסומנת. ההחלטה תישמר בפרופיל כמבוטלת, וכל הקבוצות שהיא מכסה יחזרו לזיהוי ולאישור מחדש.");
    internal readonly Button BtnRevoke = ActionButton("בטל את החלטת המשפחה המסומנת", true);
    internal readonly TextBlock RevokeValidationText = Text("");
    internal readonly Button BtnShowRows = ActionButton(""), BtnShowSaved = ActionButton("");
    internal readonly DockPanel ViewSwitch = new() { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };

    // The selected row in full, in a column beside the table and the approval fields: whether it can be approved, the scope
    // of a partition rule, what is missing, the observed evidence, the alternatives and the assistant's suggestion. The text
    // wraps and scrolls only vertically, so no horizontal scrolling is needed to read why a row can or cannot be approved.
    internal readonly ScrollViewer RowDetailPanel = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };
    internal readonly TextBlock RowDetailTitle = Text(""), RowDetailState = Text(""), RowDetailScope = Text(""),
        RowDetailMissing = Text(""), RowDetailEvidence = Text(""), RowDetailAlternatives = Text(""), RowDetailAssistant = Text("");
    private readonly Border _rowDetailFrame;
    private readonly Border _rowDetailScopeBox;
    private readonly ColumnDefinition _rowDetailColumn = new()
    {
        Width = new GridLength(1, GridUnitType.Star), MinWidth = RowDetailMinWidth, MaxWidth = 420,
    };
    private const double RowDetailMinWidth = 280;

    public FamilyDecisionsDialog(FamilyDecisionReviewModel model)
        : this(model, (model ?? throw new ArgumentNullException(nameof(model))).Library, null, false) { }

    public FamilyDecisionsDialog(FamilyDecisionReviewModel model, EngineerBoqLibrary library, FamilyAssistant? assistant, bool visionEnabled)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        // One library per window: the rows were proposed against the model's library (Codex 01:27, 02/10).
        if (!ReferenceEquals(_library, _model.Library))
            throw new InvalidOperationException("ספריית החלון אינה הספרייה שבה נבנתה הסקירה — לא נפתח חלון.");
        _assistant = assistant;
        if (model.Rows.Count == 0 && model.StaleLines.Count == 0 && model.SavedDecisions.Count == 0)
            throw new ArgumentException("There is nothing to review.", nameof(model));
        Title = "זיהוי שכבות ואישור משפחות";
        Width = 1240; Height = 760; MinWidth = 940; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        FlowDirection = FlowDirection.RightToLeft; FontFamily = new FontFamily("Segoe UI"); FontSize = 12;
        Background = new SolidColorBrush(Color.FromRgb(20, 24, 33)); Foreground = Brushes.White;

        var root = new DockPanel { Margin = new Thickness(14) };
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top);
        var title = Text("זיהוי שכבות ואישור משפחות", 17);
        title.Foreground = Brushes.White;
        heading.Children.Add(title);
        heading.Children.Add(SummaryText);
        heading.Children.Add(_approvalBanner = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(42, 33, 21)), BorderBrush = WarningBrush, BorderThickness = new Thickness(1),
            Padding = new Thickness(9), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 4, 0, 8),
            Child = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White,
                Text = "ההצעות מבוססות על ראיות מהשרטוט (בלוק, תכונות, מקרא, PropertySet, טקסט סמוך, רוחב) ולא על שם השכבה. " +
                       "אישור משפחה קובע מה הקבוצות המסומנות — החלטה אחת לכל משפחה, לכל הקבוצות יחד. הוא לא מאשר סעיפים או מחירים: " +
                       "שורות המתכון נשארות הצעה בטיוטה. אם הראיות של קבוצה ישתנו, ההחלטה תסומן 'לא בתוקף' ולא תוחל.",
            },
        });
        var staleTemplate = new DataTemplate();
        var staleText = new FrameworkElementFactory(typeof(TextBlock));
        staleText.SetBinding(TextBlock.TextProperty, new Binding());
        staleText.SetValue(TextBlock.ForegroundProperty, WarningBrush);
        staleText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        staleTemplate.VisualTree = staleText;
        StaleList.ItemTemplate = staleTemplate;
        // Many stale decisions must not push the table and the buttons out of a small window.
        var staleScroll = _staleScroll = new ScrollViewer
        {
            Content = StaleList, MaxHeight = 66,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        heading.Children.Add(staleScroll);
        DockPanel.SetDock(BtnShowRows, Dock.Left);
        DockPanel.SetDock(BtnShowSaved, Dock.Left);
        ViewSwitch.Children.Add(BtnShowRows);
        ViewSwitch.Children.Add(BtnShowSaved);
        heading.Children.Add(ViewSwitch);
        root.Children.Add(heading);

        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(AssistPanel(visionEnabled));
        _reasonLine = Labeled("על מה מבוסס האישור:", ReasonBox);
        footer.Children.Add(_reasonLine);
        _revokeReasonLine = Labeled("סיבת הביטול:", RevokeReasonBox);
        footer.Children.Add(_revokeReasonLine);
        footer.Children.Add(Labeled("מאשר:", ApproverBox));
        footer.Children.Add(ConfirmBox);
        footer.Children.Add(RevokeConfirmBox);
        ValidationText.Foreground = WarningBrush;
        footer.Children.Add(ValidationText);
        RevokeValidationText.Foreground = WarningBrush;
        footer.Children.Add(RevokeValidationText);
        var buttons = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(BtnSelectProposed, Dock.Left);
        DockPanel.SetDock(BtnCancel, Dock.Right);
        DockPanel.SetDock(BtnSave, Dock.Right);
        DockPanel.SetDock(BtnRevoke, Dock.Right);
        buttons.Children.Add(BtnSelectProposed);
        buttons.Children.Add(BtnCancel);
        buttons.Children.Add(BtnSave);
        buttons.Children.Add(BtnRevoke);
        footer.Children.Add(buttons);
        // The table and the approval fields share one column; the selected row's details get the full height beside them.
        var approvalColumn = new DockPanel();
        approvalColumn.Children.Add(footer);

        // Full evidence remains in the selected-row panel; long cells must not enlarge every row.
        RowsGrid.RowHeight = 40;
        AddCheckColumn();
        AddFamilyColumn();
        AddColumn("שכבה", nameof(FamilyDecisionRow.Layer), 1.2, star: true, min: 140, ltr: true);
        AddColumn("מקור", nameof(FamilyDecisionRow.Source), 1, star: true, min: 120, ltr: true);
        AddColumn("סוג", nameof(FamilyDecisionRow.Kind), 95, min: 95);
        AddColumn("עצמים", nameof(FamilyDecisionRow.ObjectCount), 62, min: 62, ltr: true);
        AddColumn("כמות", nameof(FamilyDecisionRow.Quantity), 105, min: 105);
        AddColumn("ראיות מהשרטוט", nameof(FamilyDecisionRow.Evidence), 2, star: true, min: 230);
        AddColumn("פרשנות וחלופות", nameof(FamilyDecisionRow.Interpretation), 1.6, star: true, min: 200);
        AddColumn("מה חסר להכרעה", nameof(FamilyDecisionRow.Missing), 1.4, star: true, min: 180);
        AddColumn("מקור ההצעה", nameof(FamilyDecisionRow.Origin), 86, min: 86);
        AddColumn("הצעת העוזר", nameof(FamilyDecisionRow.AiStatus), 1.4, star: true, min: 200);
        var view = CollectionViewSource.GetDefaultView(model.Rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FamilyDecisionRow.BatchLabel)));
        RowsGrid.GroupStyle.Add(GroupHeader());
        RowsGrid.ItemsSource = view;

        SavedGrid.IsReadOnly = true;
        AddColumn(SavedGrid, "משפחה", nameof(SavedFamilyDecision.Family), 1.2, star: true, min: 150);
        AddColumn(SavedGrid, "קבוצות בסריקה", nameof(SavedFamilyDecision.Groups), 110, min: 110);
        AddColumn(SavedGrid, "מצב", nameof(SavedFamilyDecision.State), 1.3, star: true, min: 170);
        AddColumn(SavedGrid, "שכבות", nameof(SavedFamilyDecision.Layers), 1.2, star: true, min: 150, ltr: true);
        AddColumn(SavedGrid, "ראיות מצוטטות", nameof(SavedFamilyDecision.Evidence), 1, star: true, min: 140);
        AddColumn(SavedGrid, "אושר", nameof(SavedFamilyDecision.Approval), 150, min: 150);
        AddColumn(SavedGrid, "נימוק", nameof(SavedFamilyDecision.Reason), 1.4, star: true, min: 180);
        AddColumn(SavedGrid, "מזהה", nameof(SavedFamilyDecision.ShortId), 95, min: 95, ltr: true);
        SavedGrid.ItemsSource = model.SavedDecisions;
        var savedNote = Text("ביטול אינו מוחק את ההחלטה: היא נשמרת בפרופיל כמבוטלת, עם הסיבה והמאשר, וכל הקבוצות שהיא מכסה יחזרו " +
                             "לרשימת הזיהוי לאישור מחדש. בכל פעם אפשר לבטל החלטה אחת, ולא יחד עם אישור משפחות.");
        DockPanel.SetDock(savedNote, Dock.Top);
        _savedView.Children.Add(savedNote);
        _savedView.Children.Add(SavedGrid);

        var main = new Grid();
        main.Children.Add(RowsGrid);
        main.Children.Add(_savedView);
        approvalColumn.Children.Add(main);
        _rowDetailScopeBox = new Border
        {
            BorderBrush = WarningBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7), Margin = new Thickness(0, 4, 0, 4), Child = RowDetailScope,
        };
        RowDetailPanel.Content = RowDetail();
        _rowDetailFrame = new Border
        {
            Background = PanelBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Margin = new Thickness(10, 0, 0, 0), Child = RowDetailPanel,
        };
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        body.ColumnDefinitions.Add(_rowDetailColumn);
        body.Children.Add(approvalColumn);
        Grid.SetColumn(_rowDetailFrame, 1);
        body.Children.Add(_rowDetailFrame);
        root.Children.Add(body);
        Content = root;

        foreach (var row in model.Rows) row.PropertyChanged += OnRowChanged;
        SummaryText.Text = model.Summary;
        StaleList.ItemsSource = model.StaleLines;
        StaleList.Visibility = model.StaleLines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        staleScroll.Visibility = StaleList.Visibility;
        BtnShowRows.Content = $"קבוצות לזיהוי ואישור ({model.Rows.Count})";
        BtnShowSaved.Content = $"החלטות שמורות ({model.SavedDecisions.Count}) — בדיקה וביטול";
        BtnShowRows.IsEnabled = model.Rows.Count > 0;
        ViewSwitch.Visibility = model.SavedDecisions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        BtnSelectProposed.IsEnabled = model.Rows.Count > 0;
        ApproverBox.Text = ApproverContext.Session.Name ?? ""; // b24: the session approver or empty, never Windows
        ReasonBox.TextChanged += (_, _) => RefreshState();
        ApproverBox.TextChanged += (_, _) => RefreshState();
        ConfirmBox.Checked += (_, _) => RefreshState();
        ConfirmBox.Unchecked += (_, _) => RefreshState();
        RevokeReasonBox.TextChanged += (_, _) => RefreshState();
        RevokeConfirmBox.Checked += (_, _) => RefreshState();
        RevokeConfirmBox.Unchecked += (_, _) => RefreshState();
        // A confirmation names the decision that was selected when it was given.
        SavedGrid.SelectionChanged += (_, _) => { RevokeConfirmBox.IsChecked = false; RefreshState(); };
        BtnSelectProposed.Click += (_, _) => { _model.SelectAllProposed(); ResetConsents(); RefreshState(); };
        BtnSave.Click += (_, _) =>
        {
            if (!BtnSave.IsEnabled || ShowingSaved) return;
            ReviewAction = FamilyReviewAction.ApproveFamilies;
            DialogResult = true;
        };
        BtnRevoke.Click += (_, _) =>
        {
            if (!ShowingSaved || _model.RevokeValidationError(RevokeTarget, ApproverBox.Text, RevokeReasonBox.Text,
                    RevokeConfirmBox.IsChecked == true) != null) return;
            ReviewAction = FamilyReviewAction.RevokeDecision;
            DialogResult = true;
        };
        BtnCancel.Click += (_, _) => DialogResult = false;
        BtnShowRows.Click += (_, _) => ShowView(saved: false);
        BtnShowSaved.Click += (_, _) => ShowView(saved: true);
        BtnAskAi.Click += async (_, _) => await AskAssistantAsync();
        // A consent is given for the group and the image on screen: another group needs a fresh one.
        RowsGrid.SelectionChanged += (_, _) => { ResetConsents(); RefreshAssist(); };
        RowsGrid.SelectionChanged += (_, _) => RefreshRowDetail();
        SendPreview.Checked += (_, _) => RefreshAssist();
        SendPreview.Unchecked += (_, _) => RefreshAssist();
        BtnCancel.IsCancel = true;
        UiGuard.Attach(this, "זיהוי שכבות ואישור משפחות");
        ShowView(saved: model.Rows.Count == 0 && model.SavedDecisions.Count > 0);
        RefreshState();
        RefreshAssist();
        RefreshRowDetail();
    }

    public string ApprovedBy => ApproverBox.Text.Trim();
    public string Reason => ReasonBox.Text.Trim();
    public IReadOnlyList<FamilyApprovalBatch> Batches => _model.SelectedBatches();

    /// <summary>The one action the engineer closed the dialog with.</summary>
    public FamilyReviewAction ReviewAction { get; private set; }

    /// <summary>The saved decision selected for revocation, if any.</summary>
    public SavedFamilyDecision? RevokeTarget => SavedGrid.SelectedItem as SavedFamilyDecision;

    public string RevokeReason => RevokeReasonBox.Text.Trim();

    /// <summary>True while the saved-decisions view (revoke) is shown instead of the approval view.</summary>
    internal bool ShowingSaved { get; private set; }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The attestation covered the checked groups and families it was given for; any change needs a fresh one.
        if (e.PropertyName is nameof(FamilyDecisionRow.IsSelected) or nameof(FamilyDecisionRow.ChosenFamily)
            or nameof(FamilyDecisionRow.ChosenFromAi))
            ResetConsents();
        RefreshState();
        // The assistant's answer (and any other change) of the row on display is shown at once.
        if (ReferenceEquals(sender, SelectedRow)) RefreshRowDetail();
    }

    private void ResetConsents()
    {
        ConfirmBox.IsChecked = false;
        SendPreview.IsChecked = false;
    }

    private Border? _approvalBanner;
    private ScrollViewer? _staleScroll;

    private void ShowView(bool saved)
    {
        ShowingSaved = saved && _model.SavedDecisions.Count > 0;
        var approve = ShowingSaved ? Visibility.Collapsed : Visibility.Visible;
        var revoke = ShowingSaved ? Visibility.Visible : Visibility.Collapsed;
        RowsGrid.Visibility = approve;
        AssistExpander.Visibility = approve;
        // The approval guidance and the re-approval list belong to the approval view; the saved table shows each state.
        if (_approvalBanner != null) _approvalBanner.Visibility = approve;
        if (_staleScroll != null) _staleScroll.Visibility = ShowingSaved || _model.StaleLines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _reasonLine.Visibility = approve;
        ConfirmBox.Visibility = approve;
        ValidationText.Visibility = approve;
        BtnSave.Visibility = approve;
        BtnSelectProposed.Visibility = approve;
        _savedView.Visibility = revoke;
        // The row details belong to the approval view; the saved table then gets the whole width.
        _rowDetailFrame.Visibility = approve;
        _rowDetailColumn.MinWidth = ShowingSaved ? 0 : RowDetailMinWidth;
        _rowDetailColumn.Width = ShowingSaved ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _revokeReasonLine.Visibility = revoke;
        RevokeConfirmBox.Visibility = revoke;
        RevokeValidationText.Visibility = revoke;
        BtnRevoke.Visibility = revoke;
        BtnShowRows.Background = ShowingSaved ? PanelBrush : PrimaryBrush;
        BtnShowSaved.Background = ShowingSaved ? PrimaryBrush : PanelBrush;
        RefreshState();
    }

    private UIElement AssistPanel(bool visionEnabled)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        BtnAskAiBatch.ToolTip = "מניפסט אחד, התקדמות וביטול; התוצאות הן הצעות בלבד. לא מאשר משפחות או מחירים.";
        BtnAskAiBatch.Click += (_, _) => {
            if (_assistant == null || _assistCancellation != null) return;
            var batch = new FamilyAssistantBatchDialog(_model.Rows, _library,
                (group, local, context, vision, token) => _assistant(group, local, context, vision, token),
                visionEnabled, ApprovedBy) { Owner = this };
            batch.ShowDialog();
            ResetConsents(); RefreshState(); RefreshAssist(); RowsGrid.Items.Refresh();
        };
        panel.Children.Add(Labeled("תיאור הקבוצה לעוזר (לא חובה):", AssistContext));
        var line = new DockPanel { LastChildFill = true };
        // Both question actions share the preview's existing row. An extra full-width
        // row pushed Save/Cancel below the 940x580 minimum window when an image was shown.
        var questionButtons = new StackPanel();
        questionButtons.Children.Add(BtnAskAi);
        questionButtons.Children.Add(BtnAskAiBatch);
        DockPanel.SetDock(questionButtons, Dock.Left);
        line.Children.Add(questionButtons);
        DockPanel.SetDock(PreviewImage, Dock.Right);
        line.Children.Add(PreviewImage);
        AssistStatus.Margin = new Thickness(8, 4, 0, 0);
        line.Children.Add(AssistStatus);
        panel.Children.Add(line);
        SendPreview.Visibility = visionEnabled ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(BuildVisionPanel(visionEnabled)); // Vision: explicit image choice, shown with its disclosure above the consent
        panel.Children.Add(SendPreview);
        if (_assistant == null)
        {
            BtnAskAi.IsEnabled = false;
            BtnAskAiBatch.IsEnabled = false;
            BtnAskAiBatch.ToolTip = "עוזר ה-AI אינו מוגדר במחשב הזה; לא ניתן לשלוח בקשות.";
            AssistStatus.Text = "עוזר ה-AI אינו מוגדר במחשב הזה. אפשר להמשיך בבחירה ידנית.";
        }
        // Collapsed by default so a small window keeps its table; the assistant is optional.
        AssistExpander.Content = panel;
        return AssistExpander;
    }

    private FamilyDecisionRow? SelectedRow => RowsGrid.SelectedItem as FamilyDecisionRow;

    private void RefreshAssist()
    {
        var row = SelectedRow;
        BtnAskAi.IsEnabled = _assistant != null && _assistCancellation == null && row is { CanAskAi: true };
        BtnAskAiBatch.IsEnabled = _assistant != null && _assistCancellation == null && _model.Rows.Any(r => r.CanAskAi);
        _preview = null;
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        if (row == null || _assistant == null) return;
        if (!row.CanAskAi)
        {
            AssistStatus.Text = row.IsPartialGroup
                ? "העוזר אינו נשאל על קבוצה מעורבת."
                : row.ProposedFamily != null
                    ? "לקבוצה הזו כבר יש הצעה מראיות השרטוט; העוזר נשאל רק על קבוצות שלא הוכרעו."
                    : "אין בספרייה משפחה שמתאימה לסוג המדידה של הקבוצה הזו.";
            return;
        }
        AssistStatus.Text = row.AiStatus;
        if (ShowVisionSelectionOnly(row)) return; // Vision: only an image chosen and shown explicitly, never one rendered on consent
        if (SendPreview.IsChecked != true) return;
        var png = GroupPreviewRenderer.Render(GroupPreviewRenderer.Samples(row.Group.Records));
        if (png == null || !VisionImagePolicy.TryCreate(png, "group-preview", out var image, out _) || image == null)
        {
            AssistStatus.Text = "אין לקבוצה דגימת צורה קריאה, ולכן אין תמונה לשלוח.";
            return;
        }
        _preview = image;
        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(png);
        bitmap.EndInit();
        bitmap.Freeze();
        PreviewImage.Source = bitmap;
        PreviewImage.Visibility = Visibility.Visible;
    }

    private async Task AskAssistantAsync()
    {
        var row = SelectedRow;
        if (FamilyAssistProviderAdmission.IsBusy) { AssistStatus.Text = FamilyAssistProviderAdmission.RetryBlocked; return; }
        if (_assistant == null || row is not { CanAskAi: true } || _assistCancellation != null) return;
        var context = string.IsNullOrWhiteSpace(AssistContext.Text) ? null : AssistContext.Text.Trim();
        VisionPayload? vision = null;
        if (!TakeVisionPayload(row, context, out vision)) return; // Vision: the reviewed image of this group and context, once; nothing if stale
        if (SendPreview.IsChecked == true && _preview != null)
        {
            // The permit names exactly the image on screen and this request's context; nothing else may be sent.
            var prepared = FamilyRecognitionAssist.Prepare(row.Group, _library, context, new[] { _preview });
            if (prepared.Request == null)
            {
                AssistStatus.Text = prepared.AbstentionMessage ?? "הבקשה לא הוכנה.";
                return;
            }
            vision = new VisionPayload(new[] { _preview },
                new VisionSendPermit(prepared.Request.ContextId, new[] { _preview.Sha256 }, ApprovedBy, DateTimeOffset.UtcNow));
        }
        // The consent this question uses; the engineer may give a new one for a new image while it is in flight.
        var usedConsent = _visionPending?.Reviewed;
        _assistCancellation = new CancellationTokenSource();
        BtnAskAi.IsEnabled = false;
        AssistStatus.Text = "שואל את העוזר…";
        try
        {
            var results = await _assistant(row.Group, new[] { row.Proposal }, context, vision, _assistCancellation.Token);
            if (!AcceptVisionAnswer(row, vision, results)) return; // Vision: an answer for a replaced/removed image or an older context is dropped
            row.ApplyAssistant(results, id => _library.Rules.FirstOrDefault(r => r.Id == id)?.Element ?? id);
            if (ReferenceEquals(SelectedRow, row)) AssistStatus.Text = row.AiStatus;
        }
        catch (Exception)
        {
            AssistStatus.Text = "העוזר אינו זמין כרגע. אפשר להמשיך בבחירה ידנית.";
        }
        finally
        {
            _assistCancellation?.Dispose();
            _assistCancellation = null;
            // One consent covers one send of the image on screen; the next image needs a fresh one. A consent given meanwhile
            // for a replacement image is not this question's and stays.
            if (vision != null && (_visionReviewed == null || ReferenceEquals(_visionReviewed, usedConsent)))
                SendPreview.IsChecked = false;
            RefreshAssist();
        }
    }

    private StackPanel RowDetail()
    {
        var panel = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };
        var heading = Text("פרטי השורה שנבחרה בטבלה", 13);
        heading.Foreground = Brushes.White;
        heading.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(heading);
        RowDetailTitle.Foreground = Brushes.White;
        panel.Children.Add(RowDetailTitle);
        panel.Children.Add(RowDetailState);
        // A partition rule's scope comes first: it is what the approval will cover, now and on later scans.
        RowDetailScope.Foreground = Brushes.White;
        panel.Children.Add(_rowDetailScopeBox);
        panel.Children.Add(DetailCaption("מה חסר להכרעה"));
        panel.Children.Add(RowDetailMissing);
        panel.Children.Add(DetailCaption("ראיות מהשרטוט"));
        panel.Children.Add(RowDetailEvidence);
        panel.Children.Add(DetailCaption("פרשנות וחלופות"));
        panel.Children.Add(RowDetailAlternatives);
        panel.Children.Add(DetailCaption("הצעת העוזר"));
        panel.Children.Add(RowDetailAssistant);
        return panel;
    }

    private static TextBlock DetailCaption(string value)
    {
        var caption = Text(value);
        caption.Foreground = Brushes.White;
        caption.FontWeight = FontWeights.SemiBold;
        caption.Margin = new Thickness(0, 8, 0, 0);
        return caption;
    }

    /// <summary>Shows the selected row in full (nothing is chosen or checked by showing it).</summary>
    private void RefreshRowDetail()
    {
        var row = SelectedRow;
        _rowDetailScopeBox.Visibility = row is { IsPartition: true } ? Visibility.Visible : Visibility.Collapsed;
        if (row == null)
        {
            RowDetailTitle.Text = "יש לבחור שורה בטבלה כדי לראות כאן את הראיות שלה, מה חסר להכרעה, החלופות והצעת העוזר.";
            RowDetailState.Text = RowDetailScope.Text = RowDetailMissing.Text = RowDetailEvidence.Text =
                RowDetailAlternatives.Text = RowDetailAssistant.Text = string.Empty;
            return;
        }
        RowDetailTitle.Text = $"{Bidi.Ltr(row.Layer)} · {Bidi.Ltr(row.Source)} · {row.Kind} · {row.ObjectCount} עצמים · {row.Quantity}";
        RowDetailState.Text = row.IsPartition
            ? "אפשר לאשר את השורה כתת־קבוצה מוכחת של קבוצה מעורבת: לבדוק את תחולת האישור ולסמן אותה במפורש."
            : row.CanSelect
                ? row.ChosenFamily == null
                    ? "אפשר לאשר אחרי בחירת משפחה ידנית בעמודה 'משפחה' וסימון השורה."
                    : "אפשר לאשר: לסמן את השורה ולבדוק את המשפחה שנבחרה."
                : row.IsPartialGroup
                    ? "אי אפשר לאשר את השורה בחלון הזה — הסיבה ב'מה חסר להכרעה'."
                    : "אי אפשר לאשר את השורה: אין בספרייה משפחה שמתאימה לסוג המדידה שלה.";
        RowDetailState.Foreground = row.CanSelect ? MutedBrush : WarningBrush;
        RowDetailScope.Text = row.ScopeText;
        RowDetailMissing.Text = OrDash(row.Missing);
        RowDetailEvidence.Text = OrDash(row.Evidence);
        RowDetailAlternatives.Text = OrDash(row.Interpretation);
        RowDetailAssistant.Text = OrDash(row.AiStatus);
    }

    private static string OrDash(string? text) => string.IsNullOrWhiteSpace(text) ? "—" : text;

    private void RefreshState()
    {
        var error = _model.ValidationError(ApproverBox.Text, ReasonBox.Text, ConfirmBox.IsChecked == true);
        BtnSave.IsEnabled = error == null && !ShowingSaved;
        // The caution about groups saved without cited CAD evidence, and the scope of checked partition rules, are shown
        // before the attestation, not after it.
        ValidationText.Text = string.Join(" · ", new[] { error, _model.CitationNotice(), _model.PartitionNotice() }
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        var revokeError = _model.RevokeValidationError(RevokeTarget, ApproverBox.Text, RevokeReasonBox.Text, RevokeConfirmBox.IsChecked == true);
        BtnRevoke.IsEnabled = revokeError == null && ShowingSaved;
        RevokeValidationText.Text = revokeError ?? string.Empty;
    }

    protected override void OnClosed(EventArgs e)
    {
        _assistCancellation?.Cancel();
        foreach (var row in _model.Rows) row.PropertyChanged -= OnRowChanged;
        base.OnClosed(e);
    }

    private void AddCheckColumn()
    {
        var box = new FrameworkElementFactory(typeof(CheckBox));
        box.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(nameof(FamilyDecisionRow.IsSelected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        box.SetBinding(IsEnabledProperty, new Binding(nameof(FamilyDecisionRow.CanSelect)));
        box.SetValue(HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        box.SetValue(VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        RowsGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "לאשר", Width = new DataGridLength(52), MinWidth = 52, CellTemplate = new DataTemplate { VisualTree = box },
        });
    }

    private void AddFamilyColumn()
    {
        var combo = new FrameworkElementFactory(typeof(ComboBox));
        combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(FamilyDecisionRow.FamilyOptions)));
        combo.SetValue(ItemsControl.DisplayMemberPathProperty, nameof(FamilyOption.Label));
        combo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
            new Binding(nameof(FamilyDecisionRow.ChosenFamily)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        combo.SetBinding(IsEnabledProperty, new Binding(nameof(FamilyDecisionRow.CanSelect)));
        combo.SetValue(ForegroundProperty, FrozenBrush(20, 24, 33));
        combo.SetValue(MarginProperty, new Thickness(3, 2, 3, 2));
        combo.SetValue(HeightProperty, 30d);
        combo.SetValue(VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        combo.SetValue(Control.VerticalContentAlignmentProperty, System.Windows.VerticalAlignment.Center);
        combo.SetValue(ComboBox.MaxDropDownHeightProperty, 320d);
        RowsGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "משפחה", Width = new DataGridLength(220), MinWidth = 220, CellTemplate = new DataTemplate { VisualTree = combo },
        });
    }

    private void AddColumn(string title, string property, double width, bool star = false, double min = 0, bool ltr = false) =>
        AddColumn(RowsGrid, title, property, width, star, min, ltr, compact: true);

    private static void AddColumn(DataGrid grid, string title, string property, double width, bool star = false, double min = 0, bool ltr = false, bool compact = false)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, compact ? TextWrapping.NoWrap : TextWrapping.Wrap));
        if (compact)
        {
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(ToolTipProperty, new Binding(property)));
        }
        style.Setters.Add(new Setter(MarginProperty, new Thickness(5, 3, 5, 3)));
        style.Setters.Add(new Setter(VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center));
        if (ltr) style.Setters.Add(new Setter(FlowDirectionProperty, FlowDirection.LeftToRight));
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = title, Binding = new Binding(property) { Mode = BindingMode.OneWay }, IsReadOnly = true,
            Width = new DataGridLength(width, star ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel),
            MinWidth = min, ElementStyle = style,
        });
    }

    private static GroupStyle GroupHeader()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new MultiBinding
        {
            StringFormat = "{0} · {1} קבוצות",
            Bindings = { new Binding("Name"), new Binding("ItemCount") },
        });
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetValue(TextBlock.ForegroundProperty, Brushes.White);
        text.SetValue(MarginProperty, new Thickness(6, 8, 6, 4));
        return new GroupStyle { HeaderTemplate = new DataTemplate { VisualTree = text } };
    }

    private static StackPanel Labeled(string label, TextBox box)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var caption = Text(label);
        caption.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        caption.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(caption, Dock.Left);
        panel.Children.Add(caption);
        panel.Children.Add(box);
        return new StackPanel { Children = { panel } };
    }

    private static Brush FrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static TextBlock Text(string value, double size = 12) => new()
    { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = size, Foreground = MutedBrush, Margin = new Thickness(0, 3, 0, 3) };

    private static TextBox Input() => new()
    { Background = PanelBrush, Foreground = Brushes.White, BorderBrush = LineBrush, Padding = new Thickness(7, 5, 7, 5), MinHeight = 30 };

    private static CheckBox Toggle(string value) => new()
    {
        Content = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White },
        Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 6),
    };

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
        disabled.Setters.Add(new Setter(OpacityProperty, 0.45));
        style.Triggers.Add(disabled);
        return new Button
        {
            Content = value, Style = style,
            Background = primary ? PrimaryBrush : PanelBrush,
            Foreground = Brushes.White, BorderBrush = LineBrush, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(6, 4, 0, 0),
        };
    }

    private static DataGrid CreateGrid()
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = false, CanUserAddRows = false, CanUserDeleteRows = false,
            CanUserReorderColumns = false, CanUserSortColumns = false, SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column, Background = PanelBrush, Foreground = Brushes.White,
            BorderBrush = LineBrush, RowBackground = PanelBrush, AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(37, 44, 57)),
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = LineBrush,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
        var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        header.Setters.Add(new Setter(Control.BackgroundProperty, LineBrush));
        header.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6)));
        grid.ColumnHeaderStyle = header;
        return grid;
    }
}
