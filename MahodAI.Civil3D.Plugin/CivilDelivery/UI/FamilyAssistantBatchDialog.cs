using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Image = System.Windows.Controls.Image;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
// Integration adapter (Claude, int-l): the Plugin also has global using System.Drawing.
using Color = System.Windows.Media.Color;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>One complete local manifest and one queue. Results are suggestions; the parent still owns explicit approval.</summary>
public sealed class FamilyAssistantBatchDialog : Window
{
    private readonly EngineerBoqLibrary _library;
    private readonly FamilyAssistBatch.Assistant _assistant;
    private readonly bool _visionEnabled;
    private readonly string _approvedBy;
    private readonly StackPanel _cards = new();
    private readonly TextBlock _status = Label("");
    private readonly CheckBox _visionConsent = Check("בדקתי את כל התמונות במניפסט של הקבוצות המסומנות. אני מאשר לשלוח רק את התמונות המדויקות האלה, כל אחת עם הקבוצה וההקשר שלה.");
    private readonly Button _send = Action("שלח את המניפסט המסומן לקבלת הצעות"), _stop = Action("עצור משלוח"),
        _selectAll = Action("סמן את כל הקבוצות למשלוח"), _clearAll = Action("נקה סימון משלוח"), _resume = Action("המשך לקבוצות שטרם נשלחו"),
        _selectResults = Action("סמן את ההצעות שהוחזרו לבחירה"), _take = Action("העבר בחירות שנבדקו לסקירת המשפחות");
    private readonly List<Card> _shown = new();
    private CancellationTokenSource? _cancel;
    private CancellationTokenSource? _prepareCancellation;
    private FamilyAssistManifest? _reviewed, _active;
    private bool _closed, _quiet, _preparing;
    private int _scopeVersion;
    private readonly Guid _receiptSessionId = Guid.NewGuid();

    private sealed class Card
    {
        public required FamilyDecisionRow Row;
        public readonly CheckBox Send = Check("כלול בבקשת ההצעות");
        public readonly TextBox Context = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 28, Margin = new Thickness(0, 3, 0, 3), Background = Brushes.White, Foreground = Brushes.Black };
        public readonly CheckBox Choose = Check("בדקתי את ההצעה; העבר אותה כבחירה לסקירת המשפחות — עדיין לא אישור.");
        public readonly TextBlock Status = Label("");
        public VisionImage? Image;
        public FamilyAssistBatchOutcome? Outcome;
    }

    public FamilyAssistantBatchDialog(IReadOnlyList<FamilyDecisionRow> rows, EngineerBoqLibrary library,
        FamilyAssistBatch.Assistant assistant, bool visionEnabled, string approvedBy)
    {
        _library = library; _assistant = assistant; _visionEnabled = visionEnabled; _approvedBy = approvedBy;
        Title = "הצעות משפחה לכל הקבוצות — מניפסט ובדיקה";
        Width = 900; Height = 780; MinWidth = 640; MinHeight = 520;
        FlowDirection = System.Windows.FlowDirection.RightToLeft; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(29, 35, 47)); Foreground = Brushes.White;
        var root = new DockPanel { Margin = new Thickness(14) };
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top);
        var eligible = rows.Where(r => r.CanAskAi).ToArray();
        header.Children.Add(Label($"{eligible.Length} קבוצות כשירות לממשק; {rows.Count - eligible.Length} מעורבות/מוכרעות/ללא משפחה תואמת לא נשלחות. חוסר ראיות עדיין יכול למנוע בקשה."));
        header.Children.Add(Label("חלון אחד ותור יחיד. עד 25 שניות לבקשה ו־120 שניות לאצווה; אפשר להמשיך רק לקבוצות שטרם נשלחו, בלי לאבד תשובות קודמות. אין אישור אוטומטי, מחיר או שינוי מדידה."));
        header.Children.Add(Label(visionEnabled
            ? "התמונות סכמטיות בלבד, מדגימות הקבוצה ללא קואורדינטות, טקסט או שמות — אינן צילום או מדידה. הצורה אינה מוכיחה חומר או תפקיד הנדסי."
            : "שליחה חזותית אינה מופעלת במדיניות הארגון. כרגע יישלחו רק ראיות CAD טקסטואליות כשיש ראיה מספקת; אין שינוי policy או הרשאות."));
        header.Children.Add(_status); root.Children.Add(header);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom);
        _visionConsent.Visibility = visionEnabled ? Visibility.Visible : Visibility.Collapsed;
        footer.Children.Add(_visionConsent);
        var controls = new WrapPanel();
        foreach (var button in new[] { _send, _stop, _resume, _selectAll, _clearAll, _selectResults, _take }) controls.Children.Add(button);
        footer.Children.Add(controls); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;
        _quiet = true;
        foreach (var row in eligible) AddCard(row);
        _quiet = false;
        _send.Click += async (_, _) => await SendAsync();
        _stop.Click += (_, _) => { _prepareCancellation?.Cancel(); _cancel?.Cancel(); _status.Text = "מבטל — לא יתחיל משלוח נוסף ולא תוחל תשובה מאוחרת."; };
        _selectAll.Click += (_, _) => SetRequests(true);
        _clearAll.Click += (_, _) => SetRequests(false);
        _resume.Click += (_, _) => ResumeUnattempted();
        _selectResults.Click += (_, _) => { foreach (var card in _shown.Where(c => c.Choose.IsEnabled)) card.Choose.IsChecked = true; };
        _take.Click += (_, _) => TakeReviewed();
        _visionConsent.Checked += async (_, _) => await ReviewImagesAsync();
        _visionConsent.Unchecked += (_, _) => { if (!_quiet) { _scopeVersion++; _reviewed?.Invalidate(); _reviewed = null; _active?.Invalidate(); _cancel?.Cancel(); } };
        Closed += (_, _) => { _closed = true; _active?.Invalidate(); _prepareCancellation?.Cancel(); _cancel?.Cancel(); };
        UiGuard.Attach(this, "הצעות משפחה באצווה");
        RefreshManifestLabel(); SetBusy(false);
    }

    private void AddCard(FamilyDecisionRow row)
    {
        var card = new Card { Row = row }; _shown.Add(card);
        var panel = new StackPanel { Margin = new Thickness(10) };
        panel.Children.Add(Label($"{row.Source} · {row.Layer} · {row.Kind} · {row.ObjectCount} עצמים · {row.Quantity}"));
        panel.Children.Add(Label("ראיות CAD: " + row.Evidence));
        panel.Children.Add(Label("הקשר ספציפי לקבוצה (לא חובה; אין לנחש):")); panel.Children.Add(card.Context);
        card.Send.IsChecked = true; panel.Children.Add(card.Send);
        if (_visionEnabled)
        {
            var png = GroupPreviewRenderer.Render(GroupPreviewRenderer.Samples(row.Group.Records));
            if (png != null && VisionImagePolicy.TryCreate(png, VisionImagePolicy.GroupPreview, out var image, out _) && image != null)
            {
                card.Image = image;
                using var stream = new MemoryStream(image.ToArray());
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                panel.Children.Add(new Image { Source = bitmap, Width = 128, Height = 128, Stretch = Stretch.Uniform,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    ToolTip = ReviewedFamilyImage.DisclosureOf(FamilyImageOrigin.SchematicGroup) });
                panel.Children.Add(Label("תמונת המניפסט: " + image.Sha256));
            }
            else panel.Children.Add(Label("אין דגימת צורה קריאה; לא תישלח תמונה לקבוצה זו."));
        }
        card.Context.TextChanged += (_, _) => ScopeChanged();
        card.Send.Checked += (_, _) => ScopeChanged(); card.Send.Unchecked += (_, _) => ScopeChanged();
        card.Choose.IsEnabled = false; panel.Children.Add(card.Status); panel.Children.Add(card.Choose);
        _cards.Children.Add(new Border { BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 5, 0, 5), Child = panel });
    }

    private FamilyAssistSelection[] Selections(bool images) => _shown.Where(c => c.Send.IsChecked == true)
        .Select(c => new FamilyAssistSelection(c.Row, string.IsNullOrWhiteSpace(c.Context.Text) ? null : c.Context.Text.Trim(), images ? c.Image : null)).ToArray();
    private async Task ReviewImagesAsync()
    {
        if (_quiet) return;
        var selections = Selections(true); var version = _scopeVersion;
        _preparing = true; _prepareCancellation = new CancellationTokenSource(); var token = _prepareCancellation.Token; SetBusy(true);
        try
        {
            var reviewed = await Task.Run(() => FamilyAssistManifest.Review(selections, _library, true, _approvedBy, token));
            if (_closed || version != _scopeVersion || _visionConsent.IsChecked != true) { reviewed.Invalidate(); return; }
            _reviewed = reviewed;
            _status.Text = $"מניפסט {_reviewed.Id[..12]}: {_reviewed.Entries.Count} קבוצות, {_reviewed.ImageCount} תמונות מדויקות. ההסכמה חד־פעמית; דבר טרם נשלח.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { _quiet = true; _visionConsent.IsChecked = false; _quiet = false; _reviewed = null; _status.Text = ex.Message; }
        catch (OperationCanceledException)
        { _quiet = true; _visionConsent.IsChecked = false; _quiet = false; _reviewed = null; _status.Text = "הכנת המניפסט בוטלה. שום בקשה לא נשלחה."; }
        finally { _prepareCancellation?.Dispose(); _prepareCancellation = null; _preparing = false; if (!_closed) SetBusy(false); }
    }
    private void ScopeChanged()
    {
        if (_quiet) return;
        _scopeVersion++;
        _reviewed?.Invalidate(); _active?.Invalidate(); _reviewed = null; _cancel?.Cancel(); ClearResults();
        _quiet = true; _visionConsent.IsChecked = false; _quiet = false; RefreshManifestLabel();
    }
    private void ClearResults()
    {
        foreach (var manifest in _shown.Select(c => c.Outcome?.Manifest).OfType<FamilyAssistManifest>().Distinct()) manifest.Invalidate();
        ResetRequestResults(_shown);
    }
    private void ResetRequestResults(IEnumerable<Card> cards)
    {
        // A new remaining-only manifest must not revoke successful answers from an earlier one.
        foreach (var card in cards)
        {
            if (card.Outcome?.Entry.ContextId is { } context) card.Row.WithdrawBatchAnswer(context);
            card.Outcome = null; card.Choose.IsChecked = false; card.Choose.IsEnabled = false; card.Status.Text = "";
        }
    }
    private void RefreshManifestLabel()
    {
        var selected = _shown.Where(c => c.Send.IsChecked == true).ToArray();
        _status.Text = $"מניפסט: {selected.Length} קבוצות מסומנות; {selected.Count(c => c.Image != null)} תמונות זמינות. בלי הסכמה חזותית תישלח בקשה טקסטואלית בלבד.";
    }
    private void SetRequests(bool selected)
    { _quiet = true; foreach (var c in _shown) c.Send.IsChecked = selected; _quiet = false; ScopeChanged(); }

    private void ResumeUnattempted()
    {
        if (_cancel != null || _preparing) return;
        var remaining = FamilyAssistBatch.Unattempted(_shown.Select(c => c.Outcome).OfType<FamilyAssistBatchOutcome>());
        if (remaining.Count == 0) return;
        _scopeVersion++; _reviewed?.Invalidate(); _reviewed = null;
        _quiet = true;
        foreach (var card in _shown) card.Send.IsChecked = remaining.Contains(card.Row);
        _visionConsent.IsChecked = false;
        _quiet = false;
        _status.Text = $"המשך: {remaining.Count} קבוצות שטרם נשלחו בלבד. תשובות ובחירות קודמות נשמרו. בדוק את ההיקף ולחץ שליחה; לתמונות נדרשת הסכמה חדשה למניפסט שנותר. בקשה שבוטלה לאחר שהחלה אינה נשלחת שוב אוטומטית.";
    }

    private async Task SendAsync()
    {
        if (_cancel != null || _preparing) return;
        if (FamilyAssistProviderAdmission.IsBusy) { _status.Text = FamilyAssistProviderAdmission.RetryBlocked; return; }
        FamilyAssistManifest manifest;
        var version = _scopeVersion; var selections = Selections(false);
        _preparing = true; _prepareCancellation = new CancellationTokenSource(); var preparingToken = _prepareCancellation.Token; SetBusy(true);
        try
        {
            if (_visionConsent.IsChecked == true)
                manifest = _reviewed ?? throw new InvalidOperationException("אין הסכמה תקפה למניפסט המוצג; יש לבדוק אותו מחדש.");
            else manifest = await Task.Run(() => FamilyAssistManifest.Review(selections, _library, false, _approvedBy, preparingToken));
            if (_closed || version != _scopeVersion) { manifest.Invalidate(); return; }
            if (!manifest.IsCurrent(_library)) throw new InvalidOperationException("נתוני המקור השתנו; יש להכין ולבדוק מניפסט מחדש.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { _status.Text = ex.Message; return; }
        catch (OperationCanceledException)
        { _status.Text = "הכנת המניפסט בוטלה. שום בקשה לא נשלחה."; return; }
        finally { _prepareCancellation?.Dispose(); _prepareCancellation = null; _preparing = false; if (!_closed) SetBusy(false); }
        ResetRequestResults(_shown.Where(c => manifest.Entries.Any(e => ReferenceEquals(e.Row, c.Row))));
        _active = manifest; _cancel = new CancellationTokenSource(); SetBusy(true);
        try
        {
            var progress = new Progress<FamilyAssistBatchProgress>(p => {
                if (!_closed) _status.Text = $"{p.Completed}/{p.Total} · {State(p.State)} — הצעות בלבד";
            });
            var token = _cancel.Token;
            var outcomes = await Task.Run(() => FamilyAssistBatch.RunAsync(manifest, _library, _assistant, true, progress, token));
            foreach (var outcome in outcomes)
            {
                var card = _shown.Single(c => ReferenceEquals(c.Row, outcome.Entry.Row)); card.Outcome = outcome;
                if (_closed) continue; // Still record cancellation, never present after the window closed.
                var presented = FamilyAssistBatch.Present(outcome, _library);
                card.Status.Text = presented ? card.Row.AiStatus : outcome.Message;
                card.Choose.IsChecked = false;
                card.Choose.IsEnabled = presented && card.Row.AiProposal is { Status: RecognitionStatus.Proposed };
            }
            var receipt = FamilyAssistBatchDiagnostics.Capture(_shown.Count, _shown.Select(c => c.Outcome).OfType<FamilyAssistBatchOutcome>());
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MahodAI_Civil3D", "civil-delivery", "diagnostics", "family-batch");
            var saved = FamilyAssistBatchDiagnostics.TryWrite(receipt, _receiptSessionId, folder, out var receiptPath);
            if (_closed) return;
            _status.Text = receipt.Counts.PublicSummary + (saved ? " קבלה מקומית נשמרה (הנתיב בהסבר המרחף)." : " הקבלה המקומית לא נשמרה; התוצאות בחלון עדיין בתוקף.");
            _status.ToolTip = saved ? receiptPath : "אחסון האבחון מוגבל ל־32 קבלות; אין מחיקה אוטומטית. ייתכנו גם שגיאת הרשאה או אחסון.";
            if (FamilyAssistProviderAdmission.IsBusy) _status.Text += " " + FamilyAssistProviderAdmission.RetryBlocked;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { _status.Text = ex.Message; }
        finally
        {
            _active = null; _reviewed = null; _cancel?.Dispose(); _cancel = null;
            if (!_closed) { _quiet = true; _visionConsent.IsChecked = false; _quiet = false; SetBusy(false); }
        }
    }
    private void TakeReviewed()
    {
        var cards = _shown.Where(c => c.Choose.IsChecked == true && c.Outcome != null).ToArray();
        var count = cards.Count(c => FamilyAssistBatch.SelectReviewedSuggestion(c.Outcome!, _library, true));
        _status.Text = $"{count} בחירות הועברו. סגירת החלון מחזירה לסקירת המשפחות; נדרשים סיבה ואישור מפורש שם. עדיין לא נשמר דבר.";
        foreach (var c in cards) c.Choose.IsChecked = false;
    }
    private void SetBusy(bool busy)
    {
        _send.IsEnabled = !busy && _shown.Count > 0; _stop.IsEnabled = busy; _take.IsEnabled = !busy;
        _resume.IsEnabled = !busy && _shown.Any(c => c.Outcome?.State == "not-sent");
        _selectAll.IsEnabled = _clearAll.IsEnabled = _selectResults.IsEnabled = _visionConsent.IsEnabled = !busy;
        foreach (var c in _shown)
        {
            c.Context.IsEnabled = !busy; c.Send.IsEnabled = !busy;
            c.Choose.IsEnabled = !busy && c.Outcome is { State: "answered" } outcome &&
                outcome.Manifest.IsCurrent(_library) && outcome.Entry.IsCurrent(_library) &&
                c.Row.AiProposal is { Status: RecognitionStatus.Proposed } proposal && outcome.Proposals.Contains(proposal);
        }
    }
    private static string State(string state) => state switch {
        "sending" => "שולח בקשה", "answered" => "התקבלה תשובה", "blocked" => "חסרה ראיה/הרשאה", "not-sent" => "לא נשלח",
        "cancelled" => "בוטל", "stale" => "הראיות השתנו", _ => "העוזר אינו זמין" };
    private static TextBlock Label(string text) => new() { Text = text, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
    private static CheckBox Check(string text) => new() { Content = Label(text), Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 4) };
    private static Button Action(string text) => new() { Content = text, Foreground = Brushes.White,
        Background = new SolidColorBrush(Color.FromRgb(42, 57, 74)), Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(3) };
}

public sealed partial class FamilyDecisionRow
{
    // UI-only withdrawal of this batch's answer. Other proposals/decisions stay untouched.
    internal void WithdrawBatchAnswer(string contextId)
    {
        if (AiProposal == null || !AiProposal.Inferred.Contains("ai_context=" + contextId, StringComparer.Ordinal)) return;
        var chosen = ChosenFromAi;
        if (chosen) { IsSelected = false; ChosenFamily = null; }
        AiProposal = null;
        AiStatus = "הצעת האצווה הוסרה כי היקף הבקשה או ההקשר השתנו; נדרשת בדיקה חדשה.";
        Changed(nameof(ChosenFromAi)); if (chosen) IsSelected = false;
    }
}
