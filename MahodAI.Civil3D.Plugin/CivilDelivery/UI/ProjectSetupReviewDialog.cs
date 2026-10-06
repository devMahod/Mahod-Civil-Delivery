using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Review all discovered inputs in one place. No CAD/profile writes occur in this window.</summary>
internal sealed class ProjectSetupReviewDialog : Window
{
    private readonly ProjectSetupScan _scan;
    internal readonly Dictionary<string, CheckBox> Layers = new(StringComparer.OrdinalIgnoreCase),
        Alignments = new(StringComparer.OrdinalIgnoreCase), Sources = new(StringComparer.OrdinalIgnoreCase);
    internal readonly CheckBox Confirm = Toggle("בדקתי את השכבות, התוואים והמקורות המסומנים. שמירת ההגדרה אינה יצירת חתכים או אישור האומדן.");
    internal readonly Button Save = Action("שמור הגדרה"), Cancel = Action("ביטול — ללא שמירה"),
        ExternalCl = Action("בחר קובץ CL נפרד…");
    internal readonly TextBlock Validation = Text("");
    // 1.4.1 (984): an explicit station-markers mode; never switched on by the tool.
    internal readonly CheckBox StationMarkers = Toggle("סימוני תחנות: הקווים בשכבות שסומנו הם סימוני תחנה קצרים (כמו AeccTickLine), " +
        "לא קווי חתך לרוחב הדרך. כל סימון יורחב מנקודת החצייה עם הציר לחתך של ± חצי-הרוחב שלהלן, בכיוון הסימון.");
    internal readonly TextBox StationMarkerHalfWidth = new() { MinWidth = 90, MaxWidth = 140, MinHeight = 28,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Left, FlowDirection = System.Windows.FlowDirection.LeftToRight,
        Foreground = Brushes.Black, Background = Brushes.White, Margin = new Thickness(0, 3, 0, 7) };
    internal readonly ScrollViewer BodyScroll;
    internal readonly Expander SurfacePairReview;
    internal sealed record SurfacePairControls(AlignmentCandidateSummary Alignment,
        CheckBox Enabled, ComboBox Existing, ComboBox Design);
    internal readonly Dictionary<string, SurfacePairControls> SurfacePairs = new(StringComparer.OrdinalIgnoreCase);
    internal ProjectSetupSelection? ApprovedSelection { get; private set; }
    internal bool PickExternalClRequested { get; private set; }

    // b24: the confirmed session approver, passed in by the palette; never the Windows account.
    private readonly string? _approvedBy;

    internal ProjectSetupReviewDialog(ProjectSetupScan scan, ProjectProfile profile, string? approvedBy = null)
    {
        _scan = scan;
        _approvedBy = approvedBy;
        Title = "הגדרת חתכים — בחירת מקורות";
        Width = 820; Height = 760; MinWidth = 560; MinHeight = 430;
        FlowDirection = System.Windows.FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(20, 24, 33)); Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        var root = new DockPanel { Margin = new Thickness(14) };
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(Confirm); footer.Children.Add(Validation);
        var actions = new WrapPanel(); actions.Children.Add(Save); actions.Children.Add(Cancel);
        footer.Children.Add(actions); root.Children.Add(footer);
        var body = new StackPanel();
        body.Children.Add(Text("מה נמדוד ובאילו מיקומים?", 20));
        body.Children.Add(Text("בחר את המקורות האמיתיים בפרויקט. שמות שכבות אינם חייבים להיות CL או שם משרדי קבוע. לאחר השמירה חוזרים לפאנל ולוחצים תכנון חתכים."));
        body.Children.Add(Text(Bidi.Ltr(scan.Drawing ?? "")));
        body.Children.Add(Text("1 · מיקומי חתכים — שכבות במודל, בבלוקים, ב-XREF או בקובץ שנבחר", 16));
        body.Children.Add(ExternalCl);
        if (scan.ClLayerCandidates.Count == 0)
            body.Children.Add(Text("לא נמצאו קווי חתך. אפשר לבחור קובץ CL נפרד, או לשרטט במודל LINE / פוליליין פתוח וישר לרוחב התוואי בשכבה ייעודית, לשמור ולהריץ שוב הגדרה. מיקום הקו ורוחבו הם בחירה הנדסית — הכלי אינו ממציא אותם."));
        foreach (var candidate in scan.ClLayerCandidates)
        {
            var label = Bidi.Ltr(candidate.Layer) + $" · {candidate.TwoPointCount} קווים · {candidate.CrossingCount} חציות שנבדקו";
            if (candidate.AlignmentsCrossed.Count > 0)
                label += "\nתוואים: " + Bidi.Ltr(string.Join(", ", candidate.AlignmentsCrossed));
            if (candidate.Why.Any(w => w.StartsWith("קובץ CL נפרד:")))
                label += "\n" + string.Join(" · ", candidate.Why.Where(w => w.StartsWith("קובץ CL נפרד:")));
            AddChoice(body, Layers, candidate.Layer, label,
                profile.Sections.Cl.LayerPatterns.Contains(candidate.Layer, StringComparer.OrdinalIgnoreCase));
        }
        body.Children.Add(Text("מצב קווי ה-CL", 15));
        var shortLayers = scan.ClLayerCandidates.Where(c => c.MedianLength > 0 &&
            c.MedianLength < SectionStationMarkerLogic.ShortLineWarningM).Select(c => c.Layer).ToList();
        if (shortLayers.Count > 0)
            body.Children.Add(Text("שים לב: בשכבות " + Bidi.Ltr(string.Join(", ", shortLayers)) +
                " הקווים קצרים מ-2 מ' — ייתכן שאלה סימוני תחנות. ההחלטה שלך; הכלי לא מחליף מצב לבד."));
        StationMarkers.IsChecked = SectionStationMarkerLogic.IsActive(profile.Sections.Cl);
        StationMarkerHalfWidth.Text = profile.Sections.Cl.StationMarkerHalfWidthM?.ToString(
            System.Globalization.CultureInfo.InvariantCulture) ?? "";
        body.Children.Add(StationMarkers);
        body.Children.Add(Text("חצי-רוחב החתך לכל צד (מטרים), למצב סימוני תחנות בלבד:"));
        body.Children.Add(StationMarkerHalfWidth);
        StationMarkers.Checked += (_, _) => { Confirm.IsChecked = false; Refresh(); };
        StationMarkers.Unchecked += (_, _) => { Confirm.IsChecked = false; Refresh(); };
        StationMarkerHalfWidth.TextChanged += (_, _) => { Confirm.IsChecked = false; Refresh(); };
        body.Children.Add(Text("2 · תוואים במודל Civil הפעיל", 16));
        if (scan.Alignments.Count == 0)
            body.Children.Add(Text("אין Alignment במודל הפעיל. פתח את מודל Civil שבו התוואי קיים, צור הפניית Data Shortcut תקינה, או צור Alignment מפוליליין תכנון מאושר ב-Civil. ציר שמופיע רק ב-XREF אינו מספיק ליצירת חתך. מדידת כתב כמויות זמינה גם בלי CL או Alignment."));
        foreach (var alignment in scan.Alignments)
            AddChoice(body, Alignments, alignment.Name,
                Bidi.Ltr(alignment.Name) + $" · תחנות {alignment.StartStation:N2}–{alignment.EndStation:N2}",
                profile.Sections.Alignments.AllowedNames.Contains(alignment.Name, StringComparer.OrdinalIgnoreCase));
        body.Children.Add(Text("3 · מקורות לדגימה בחתכים", 16));
        if (scan.Sources.Count == 0)
            body.Children.Add(Text("אין מקור דגימה זמין. טען למודל משטח, קורידור או רשת מתאימים, ובדוק יחידות וגבהים. קווי ציור בלבד אינם מגדירים משטח תכנון."));
        foreach (var source in scan.Sources)
            AddChoice(body, Sources, SourceKey(source), Bidi.Ltr(source.Name) + " · " + Kind(source.Kind),
                profile.Sections.Sources.SampledSourceRules.Any(r => r.Required &&
                    string.Equals(r.Name, source.Name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.Kind, source.Kind, StringComparison.OrdinalIgnoreCase)));
        var pairBody = new StackPanel();
        SurfacePairReview = new Expander { Header = Text("4 · זוג משטחים מפורש לכל תוואי — אופציונלי", 16),
            Content = pairBody, Foreground = Brushes.White,
            IsExpanded = profile.Sections.Sources.SurfacePairs.Count > 0 };
        body.Children.Add(SurfacePairReview);
        pairBody.Children.Add(Text("לשמות שאינם לפי המוסכמה הקיימת: בחר משטח קרקע קיימת ומשטח תכנון עבור התוואי. בחירת זוג מוסיפה את שני המקורות לדגימה. ללא סימון נשאר מסלול השמות הקיים; ביטול סימון של זוג קודם מחזיר אותו למסלול זה רק לאחר שמירה."));
        foreach (var alignment in scan.Alignments)
            AddSurfacePair(pairBody, alignment, profile);
        foreach (var finding in scan.Findings)
            body.Children.Add(Text(finding.Title + "\n" + finding.Message + "\n" + finding.RecommendedAction));
        BodyScroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(BodyScroll); Content = root;
        Confirm.Checked += (_, _) => Refresh(); Confirm.Unchecked += (_, _) => Refresh();
        Save.Click += (_, _) =>
        {
            Refresh(); if (!Save.IsEnabled) return;
            ApprovedSelection = new ProjectSetupSelection
            {
                ClLayers = Checked(Layers), AllowedAlignments = Checked(Alignments),
                ClSourceFiles = ClSourceSelection.MergeSources(scan.ExternalClHashes.Keys, scan.Drawing),
                SampledSources = SelectedSources()
                    .ToDictionary(s => s.Name, s => s.Kind, StringComparer.OrdinalIgnoreCase),
                SurfacePairs = SelectedSurfacePairs(),
                IntersectionToleranceM = profile.Sections.Cl.IntersectionToleranceM,
                ClLayerScope = SelectedLayersAreFromSeparateFile() ? ClInstructionReader.LayerScopeSourceFile : null,
                ClMode = StationMarkers.IsChecked == true ? SectionStationMarkerLogic.ModeStationMarkers : null,
                StationMarkerHalfWidthM = StationMarkers.IsChecked == true ? ParseHalfWidth() : null,
                ApprovedBy = _approvedBy,
            };
            Close();
        };
        Cancel.IsCancel = true; Cancel.Click += (_, _) => Close();
        ExternalCl.Click += (_, _) => { PickExternalClRequested = true; Close(); };
        Refresh(); UiGuard.Attach(this, "הגדרת מקורות חתכים");
    }

    private void AddChoice(StackPanel body, Dictionary<string, CheckBox> choices, string key, string label, bool selected)
    {
        var box = Toggle(label); box.IsChecked = selected;
        choices.Add(key, box); body.Children.Add(box);
        box.Checked += (_, _) => { Confirm.IsChecked = false; Refresh(); };
        box.Unchecked += (_, _) => { Confirm.IsChecked = false; Refresh(); };
    }
    private void Refresh()
    {
        foreach (var row in SurfacePairs.Values)
        {
            var active = Alignments[row.Alignment.Name].IsChecked == true;
            row.Enabled.IsEnabled = active;
            row.Existing.IsEnabled = row.Design.IsEnabled = active && row.Enabled.IsChecked == true;
        }
        var error = !_scan.ScanComplete || _scan.Findings.Any(f => f.Severity == FindingSeverity.Error)
            ? "הסריקה אינה מלאה. יש לטפל במקור החסר או בשגיאת הקריאה לפני שמירה."
            : Checked(Layers).Count == 0 ? "בחר לפחות שכבת קווי חתך אחת, או טען קובץ CL נפרד."
            : Checked(Alignments).Count == 0 ? "בחר לפחות תוואי אחד שקיים במודל הפעיל."
            : Checked(Sources).Count == 0 ? "בחר לפחות מקור דגימה אחד."
            : SelectedSources().GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)
                ? "נבחרו שני מקורות בעלי אותו שם. בחר אחד מהם או שנה את שמותיהם במודל וסרוק מחדש; הם לא יאוחדו בשקט."
            : SelectedSources().Any(s => _scan.Sources.Count(other =>
                string.Equals(s.Name, other.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Kind, other.Kind, StringComparison.OrdinalIgnoreCase)) != 1)
                ? "זהות מקור הדגימה אינה ייחודית לפי שם וסוג. יש לתת שמות ייחודיים במודל ולסרוק מחדש."
            : SurfacePairError() is { } pairError ? pairError
            : StationMarkers.IsChecked == true && SectionStationMarkerLogic.ValidateHalfWidth(ParseHalfWidth()) is { } widthError
                ? widthError + ". הזן מספר במטרים, למשל 12.5."
            : StationMarkers.IsChecked == true && string.IsNullOrWhiteSpace(_approvedBy)
                ? "מצב סימוני תחנות דורש שם מאשר מהפאנל."
            : Confirm.IsChecked != true ? "בדוק את הבחירות וסמן את האישור למטה."
            : null;
        Save.IsEnabled = error == null;
        Validation.Text = error ?? $"לשמירה: {Checked(Layers).Count} שכבות, {Checked(Alignments).Count} תוואים, {Checked(Sources).Count} מקורות. לאחר מכן: תכנון חתכים.";
    }
    internal double? ParseHalfWidth() =>
        double.TryParse(StationMarkerHalfWidth.Text?.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>True when every checked CL layer was offered ONLY by the separate CL drawing (never a mixed row).</summary>
    private bool SelectedLayersAreFromSeparateFile() =>
        ProjectSetupLayerScope.AllFromSeparateFileOnly(Checked(Layers), _scan);

    private static List<string> Checked(Dictionary<string, CheckBox> choices) =>
        choices.Where(c => c.Value.IsChecked == true).Select(c => c.Key).ToList();
    private IEnumerable<SourceCandidateSummary> SelectedSources() =>
        _scan.Sources.Where(s => Sources[SourceKey(s)].IsChecked == true)
            .Concat(SurfacePairs.Values.Where(p => p.Enabled.IsChecked == true &&
                    Alignments[p.Alignment.Name].IsChecked == true)
                .SelectMany(p => new[] { p.Existing.SelectedItem, p.Design.SelectedItem })
                .OfType<SourceCandidateSummary>()).DistinctBy(SourceKey);

    private void AddSurfacePair(StackPanel body, AlignmentCandidateSummary alignment, ProjectProfile profile)
    {
        var enabled = Toggle("זוג מפורש לתוואי " + Bidi.Ltr(alignment.Name));
        ComboBox Picker() => new() { ItemsSource = _scan.Sources.Where(s => s.Kind == "surface").ToArray(),
            DisplayMemberPath = nameof(SourceCandidateSummary.Name), MinHeight = 32,
            Foreground = Brushes.Black, Background = Brushes.White, Margin = new Thickness(0, 3, 0, 7) };
        var existing = Picker(); var design = Picker();
        var row = new SurfacePairControls(alignment, enabled, existing, design);
        SurfacePairs.Add(alignment.Name, row);
        var saved = profile.Sections.Sources.SurfacePairs.Where(p =>
            string.Equals(p.AlignmentName, alignment.Name, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(alignment.Handle) &&
             string.Equals(p.AlignmentHandle, alignment.Handle, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (saved.Length > 0)
        {
            enabled.IsChecked = true;
            if (saved.Length == 1)
            {
                existing.SelectedItem = _scan.Sources.SingleOrDefault(s => s.Kind == "surface" &&
                    s.Name == saved[0].ExistingName && s.Handle == saved[0].ExistingHandle);
                design.SelectedItem = _scan.Sources.SingleOrDefault(s => s.Kind == "surface" &&
                    s.Name == saved[0].DesignName && s.Handle == saved[0].DesignHandle);
            }
        }
        body.Children.Add(enabled);
        body.Children.Add(Text("קרקע קיימת")); body.Children.Add(existing);
        body.Children.Add(Text("משטח תכנון של התוואי")); body.Children.Add(design);
        void Changed()
        {
            if (enabled.IsChecked == true && Alignments[alignment.Name].IsChecked == true)
                foreach (var source in new[] { existing.SelectedItem, design.SelectedItem }.OfType<SourceCandidateSummary>())
                    Sources[SourceKey(source)].IsChecked = true;
            Confirm.IsChecked = false; Refresh();
        }
        enabled.Checked += (_, _) => Changed(); enabled.Unchecked += (_, _) => Changed();
        existing.SelectionChanged += (_, _) => Changed(); design.SelectionChanged += (_, _) => Changed();
    }

    private List<ProjectProfile.SectionsProfile.SourcesProfile.SurfacePair> SelectedSurfacePairs() =>
        SurfacePairs.Values.Where(p => p.Enabled.IsChecked == true && Alignments[p.Alignment.Name].IsChecked == true)
            .Select(p => new ProjectProfile.SectionsProfile.SourcesProfile.SurfacePair
            {
                AlignmentName = p.Alignment.Name, AlignmentHandle = p.Alignment.Handle,
                ExistingName = (p.Existing.SelectedItem as SourceCandidateSummary)?.Name,
                ExistingHandle = (p.Existing.SelectedItem as SourceCandidateSummary)?.Handle,
                DesignName = (p.Design.SelectedItem as SourceCandidateSummary)?.Name,
                DesignHandle = (p.Design.SelectedItem as SourceCandidateSummary)?.Handle,
            }).ToList();

    private string? SurfacePairError()
    {
        try
        {
            foreach (var pair in SelectedSurfacePairs())
            {
                var pending = pair with { DrawingFingerprint = _scan.DrawingFingerprint,
                    ApprovedBy = "(validation only — not saved)", ApprovedAtUtc = DateTime.UtcNow };
                var result = SectionSourceSelectionLogic.SelectExplicitPair(_scan.DrawingFingerprint,
                    pair.AlignmentName, pair.AlignmentHandle,
                    _scan.Sources.Where(s => s.Kind == "surface")
                        .Select(s => new SectionSourceSelectionLogic.Identity(s.Name, s.Handle ?? "")).ToArray(),
                    new[] { pending });
                if (!result.IsValid) throw new InvalidOperationException(result.Error);
            }
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return "בדוק זוג משטחים שונה ומלא לכל תוואי מסומן; מקור חסר או שהוחלף דורש בחירה מחדש. " + ex.Message;
        }
    }
    private static string SourceKey(SourceCandidateSummary source) => source.Kind + "\n" + source.Name + "\n" + source.Handle;
    private static string Kind(string value) => value switch
    { "surface" => "משטח", "corridor" => "קורידור", "pipe-network" => "רשת צינורות", _ => value };
    private static TextBlock Text(string text, double size = 13) => new()
    { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, FontSize = size, Margin = new Thickness(0, 5, 0, 5) };
    private static CheckBox Toggle(string label) => new() { Content = Text(label), Foreground = Brushes.White,
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch, Margin = new Thickness(0, 3, 0, 3),
        VerticalContentAlignment = VerticalAlignment.Center };
    private static Button Action(string label) => new() { Content = label, MinHeight = 38,
        Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6),
        Background = new SolidColorBrush(Color.FromRgb(232, 239, 247)),
        Foreground = new SolidColorBrush(Color.FromRgb(20, 24, 33)) };
}
