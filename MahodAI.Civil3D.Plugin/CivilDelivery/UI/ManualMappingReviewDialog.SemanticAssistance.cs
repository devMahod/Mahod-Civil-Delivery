using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Panel = System.Windows.Controls.Panel;
using ComboBox = System.Windows.Controls.ComboBox;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public sealed partial class ManualMappingReviewDialog
{
    private readonly Func<Group, string, CancellationToken, Task<SemanticMappingAssistResult>>? _suggestWithAi;
    private readonly Action<Group, SemanticHintPolicy.Draft, string>? _saveSemanticHint;
    private readonly Dictionary<string, SemanticHintPolicy.Draft> _semanticDrafts = new(StringComparer.Ordinal);
    private string? _semanticDraftKey;
    private readonly Dictionary<string, IReadOnlyList<MappingProposal>> _semanticProposals = new(StringComparer.Ordinal);
    private CancellationTokenSource? _semanticCancellation;
    private int _semanticRevision;
    private bool _semanticClosed;
    private bool _publishingSemanticResults;
    internal readonly TextBox AiContext = Input();
    internal readonly ComboBox SemanticRole = new() { MinWidth = 150, Width = 170,
        DisplayMemberPath = nameof(SemanticHintPolicy.Role.Label), SelectedValuePath = nameof(SemanticHintPolicy.Role.Id),
        Foreground = System.Windows.Media.Brushes.Black, Background = System.Windows.Media.Brushes.White,
        Margin = new Thickness(0, 4, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
    internal readonly TextBlock AiStatus = Text("");
    internal readonly Button AiSuggest = ActionButton("הצע התאמות חכמות"), AiCancel = ActionButton("עצור חיפוש"),
        SaveSemanticHint = ActionButton("שמור משמעות");

    private void AddSemanticAssistance(Panel panel)
    {
        // This is an optional inline expansion of the existing editor, not another
        // approval dialog. No API call runs on selection, scan or ordinary search.
        // Use the full review width, not a tall stack above the narrow catalog.
        // At minimum window size the catalog must still show selectable rows.
        var content = new StackPanel();
        content.Children.Add(Text("הצעה לקבוצה הנבחרת: ל-Google נשלחים שם השכבה, מאפייני CAD, התיאור ונוסחי סעיפים; לא DWG, מיקומים, כמויות או מחירים."));
        // Validate the complete role + text at the action boundary. MaxLength would
        // silently cut a pasted explanation before the engineer can review it.
        AiContext.ToolTip = "תיאור אופציונלי של העצמים, למשל: אבני שפה מונמכות. אין צורך לשנות שמות שכבות.";
        SemanticRole.ItemsSource = SemanticHintPolicy.Roles;
        SemanticRole.SelectedValue = "unknown";
        SemanticRole.ToolTip = "מה מייצגת הקבוצה? בחירה אינה משנה את שם השכבה, המדידה או שיוך הסעיף.";
        var inputRow = new DockPanel();
        var actions = new WrapPanel(); actions.Children.Add(AiSuggest); actions.Children.Add(AiCancel); actions.Children.Add(SaveSemanticHint);
        DockPanel.SetDock(actions, Dock.Left); inputRow.Children.Add(actions);
        DockPanel.SetDock(SemanticRole, Dock.Right); inputRow.Children.Add(SemanticRole);
        AiContext.Margin = new Thickness(0, 4, 8, 0); inputRow.Children.Add(AiContext);
        AiCancel.IsEnabled = false; content.Children.Add(inputRow);
        content.Children.Add(new ScrollViewer { Content = AiStatus, MaxHeight = 34,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var expander = new Expander { Header = "משמעות הקבוצה ועזרה חכמה — ללא שינוי בשכבה", Foreground = MutedBrush,
            IsExpanded = true, Content = content, Margin = new Thickness(0) };
        panel.Children.Add(expander);
        AiSuggest.Click += async (_, _) => await RequestSemanticSuggestionsAsync();
        AiCancel.Click += (_, _) => { CancelSemanticAssistance(); AiStatus.Text = "החיפוש בוטל; הבחירות לא השתנו."; };
        SaveSemanticHint.Click += (_, _) => TrySaveSemanticHint();
        AiContext.TextChanged += (_, _) => SemanticDescriptionEdited();
        SemanticRole.SelectionChanged += (_, _) => SemanticDescriptionEdited();
        CatalogSearch.TextChanged += (_, _) => SemanticInputEdited();
        CatalogGrid.SelectionChanged += (_, _) => { if (CatalogGrid.SelectedItem != null) SemanticInputEdited(); };
        Closed += (_, _) => { _semanticClosed = true; CancelSemanticAssistance(); };
    }

    private SemanticHintPolicy.Draft CurrentSemanticDraft() =>
        new(SemanticRole.SelectedValue as string ?? "unknown", AiContext.Text);

    private void RememberSemanticDraft()
    {
        if (_semanticDraftKey != null) _semanticDrafts[_semanticDraftKey] = CurrentSemanticDraft();
    }

    private void LoadSemanticDraft()
    {
        _semanticDraftKey = (GroupsGrid.SelectedItem as GroupRow)?.Subject.RuleKey;
        var draft = _semanticDraftKey == null ? new SemanticHintPolicy.Draft() :
            _semanticDrafts.GetValueOrDefault(_semanticDraftKey) ?? new SemanticHintPolicy.Draft();
        SemanticRole.SelectedValue = draft.RoleId;
        AiContext.Text = draft.Description;
    }

    private void SemanticDescriptionEdited()
    {
        if (_loadingGroup) return;
        SemanticInputEdited();
        RememberSemanticDraft();
        if (GroupsGrid.SelectedItem is GroupRow row && _semanticProposals.Remove(row.Subject.RuleKey)) RefreshCatalog();
        if (!SemanticHintPolicy.TryContext(CurrentSemanticDraft(), out _, out var error)) AiStatus.Text = error;
    }

    internal bool TrySaveSemanticHint()
    {
        if (_saveSemanticHint == null || GroupsGrid.SelectedItem is not GroupRow { Subject.ReadOnlyReason: null, Subject.SemanticHintStoreAvailable: true } row)
            return false;
        RememberSemanticDraft();
        var input = CurrentSemanticDraft();
        if (!SemanticHintPolicy.TryContext(input, out var context, out var error) || context.Length == 0)
        { AiStatus.Text = error.Length == 0 ? "בחר משמעות או הוסף תיאור לפני שמירה." : error; return false; }
        if (ApprovedBy.Length == 0 || ApprovedBy.Any(char.IsControl))
        { AiStatus.Text = "הזן שם בודק/ת בתחתית החלון. שמירת משמעות אינה אישור שיוך."; Approver.Focus(); return false; }
        CancelSemanticAssistance();
        try
        {
            _saveSemanticHint(row.Subject, input, ApprovedBy);
            AiStatus.Text = "המשמעות נשמרה במחשב זה למקור ולקבוצה האלה בלבד. בחירות השיוך נשארו בחלון; לא אושר סעיף או מחיר.";
            return true;
        }
        catch (Exception ex)
        { AiStatus.Text = "שמירת המשמעות לא הושלמה או לא אומתה; יש לפתוח מחדש כדי לבדוק מה נשמר. הקלט והבחירות נשארו בחלון. " + ex.Message; return false; }
    }

    private void SemanticInputEdited()
    {
        if (_loadingGroup || _publishingSemanticResults || _semanticCancellation == null) return;
        CancelSemanticAssistance();
        AiStatus.Text = "הקלט או הבחירה השתנו; הבקשה הקודמת בוטלה כדי לא להחליף את עבודתך.";
    }

    private void RefreshSemanticButton()
    {
        AiSuggest.IsEnabled = !_semanticClosed && _semanticCancellation == null && _suggestWithAi != null &&
            GroupsGrid.SelectedItem is GroupRow { Subject.ReadOnlyReason: null };
        AiCancel.IsEnabled = _semanticCancellation != null;
        SaveSemanticHint.IsEnabled = !_semanticClosed && _saveSemanticHint != null &&
            GroupsGrid.SelectedItem is GroupRow { Subject.ReadOnlyReason: null, Subject.SemanticHintStoreAvailable: true };
    }

    private void CancelSemanticAssistance()
    {
        _semanticRevision++;
        var previous = _semanticCancellation; _semanticCancellation = null;
        previous?.Cancel();
        RefreshSemanticButton();
    }

    internal async Task RequestSemanticSuggestionsAsync()
    {
        RefreshSemanticButton();
        if (!AiSuggest.IsEnabled || _suggestWithAi == null || GroupsGrid.SelectedItem is not GroupRow selected) return;
        if (!SemanticHintPolicy.TryContext(CurrentSemanticDraft(), out var context, out var inputError))
        { AiStatus.Text = inputError; return; }
        var group = selected.Subject;
        var requestRevision = ++_semanticRevision;
        using var cancellation = new CancellationTokenSource();
        _semanticCancellation = cancellation; RefreshSemanticButton();
        AiStatus.Text = "מחפש ומדרג סעיפים לקבוצה הנבחרת… אפשר להמשיך בחיפוש הידני או לבטל.";
        try
        {
            var result = await _suggestWithAi(group, context, cancellation.Token);
            if (_semanticClosed || cancellation.IsCancellationRequested || requestRevision != _semanticRevision ||
                GroupsGrid.SelectedItem is not GroupRow current || current.Subject.RuleKey != group.RuleKey) return;
            // Defense at the editor boundary as well: an injected/future provider
            // cannot replace the catalog or supply another group's/code's unit.
            var valid = result.Proposals.Count <= MappingProposalEngine.MaxProposalsPerGroup &&
                (result.Proposals.Count == 0 ||
                 (string.Equals(result.CatalogId, _catalog.SnapshotId, StringComparison.Ordinal) &&
                  string.Equals(result.CatalogHash, _catalog.FileHash, StringComparison.OrdinalIgnoreCase))) &&
                result.Proposals.Select(p => p.ProposedCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() == result.Proposals.Count &&
                result.Proposals.All(p => p.RuleKey == group.RuleKey && p.MeasurementKind == group.MeasurementKind &&
                    Units.Parse(p.MeasuredUnit).SameUnit(Units.Parse(group.Unit)) &&
                    _catalog.Items.TryGetValue(p.ProposedCode, out var item) && item.Unit.SameUnit(Units.Parse(group.Unit)) &&
                    SemanticMappingAssist.IsCandidateCompatible(new MappingProposalEngine.DiscoveredGroup(
                        group.RuleKey, group.Layer, group.MeasurementKind, group.Unit, group.ObjectCount,
                        group.Quantity, group.CadMetadata, group.RecognitionEvidence), item.Description, context));
            if (!valid) { AiStatus.Text = "תשובת ההצעות לא התאימה לקבוצה ולמחירון; לא שונה אף שיוך. אפשר לחפש ידנית."; return; }
            AiStatus.Text = result.Message;
            if (result.Proposals.Count == 0) return;
            _semanticProposals[group.RuleKey] = result.Proposals.ToArray();
            // Never select, stage, approve or save an AI answer automatically.
            _publishingSemanticResults = true;
            try { CatalogSearch.Text = ""; RefreshCatalog(); }
            finally { _publishingSemanticResults = false; }
        }
        catch (OperationCanceledException)
        {
            if (!_semanticClosed && requestRevision == _semanticRevision)
                AiStatus.Text = "החיפוש בוטל; הבחירות לא השתנו.";
        }
        catch
        {
            // Transport/credential errors can contain sensitive provider details.
            if (!_semanticClosed && requestRevision == _semanticRevision)
                AiStatus.Text = "החיפוש החכם לא זמין כרגע. הבחירות נשמרו בחלון וניתן להמשיך בחיפוש ידני.";
        }
        finally
        {
            if (ReferenceEquals(_semanticCancellation, cancellation)) _semanticCancellation = null;
            RefreshSemanticButton();
        }
    }
}
