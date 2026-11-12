using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using Theme = MahodAI.Civil3D.Plugin.CivilDelivery.UI.CivilDeliveryTheme;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>The earthworks choices: which surface is the ground and which the design, the platform level, the factors.</summary>
internal sealed class EarthworksSettingsWindow : Window
{
    private sealed record Choice(string Label, string? Surface, bool Platform = false, bool Typed = false);

    internal Earthworks.Settings? Result { get; private set; }
    private readonly ComboBox _ground = Combo("earth-ground"), _design = Combo("earth-design");
    private readonly TextBox _level = Box("earth-level"), _strip = Box("earth-strip"), _shrink = Box("earth-shrink"), _swell = Box("earth-swell"),
        _rock = Box("earth-rock"), _unsuitable = Box("earth-unsuitable"), _fillSlope = Box("earth-fill-slope"), _cutSlope = Box("earth-cut-slope");
    private readonly CheckBox _stockpile = new() { Content = "עירום לשימוש חוזר בגינון ובשיקום (אחרת — סילוק)", Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _pavement = new() { Content = "עד פני השתית: להוריד את מבנה המיסעה מתחת לכבישים", Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _error = new() { Foreground = Theme.Blocked, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    internal EarthworksSettingsWindow(Earthworks.Settings s, IReadOnlyList<EarthworksReader.SurfaceInfo> surfaces, EarthworksReader.Result? last)
    {
        Title = "עבודות עפר — הגדרות"; Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        FlowDirection = System.Windows.FlowDirection.RightToLeft; Theme.Apply(this, 13); Background = Theme.Canvas;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var grounds = new List<Choice> { new("אוטומטי" + (last?.Ground is { Length: > 0 } g ? " — " + g : ""), null) };
        grounds.AddRange(surfaces.Select(x => new Choice($"{x.Name} · {x.RoleName}", x.Name)));
        var designs = new List<Choice> { new("אוטומטי" + (last?.Design is { Length: > 0 } d ? " — " + d : ""), null) };
        designs.AddRange(surfaces.Select(x => new Choice($"{x.Name} · {x.RoleName}", x.Name)));
        designs.Add(new("פילוס — מפלס מאוזן (חפירה = מילוי)", null, Platform: true));
        designs.Add(new("פילוס — מפלס שאני מזין", null, Platform: true, Typed: true));
        _ground.ItemsSource = grounds; _design.ItemsSource = designs;
        _ground.SelectedItem = grounds.FirstOrDefault(c => c.Surface == s.ExistingName) ?? grounds[0];
        _design.SelectedItem = s.ForcePlatform ? designs.First(c => c.Platform && c.Typed == (s.PlatformLevel != null))
            : designs.FirstOrDefault(c => c.Surface != null && c.Surface == s.DesignName) ?? designs[0];
        _level.Text = s.PlatformLevel?.ToString("0.00", CultureInfo.InvariantCulture) ?? (last?.Level is { } l ? l.ToString("0.00", CultureInfo.InvariantCulture) : "");
        _strip.Text = N(s.StripCm); _shrink.Text = N(s.Shrink); _swell.Text = N(s.Swell); _rock.Text = N(s.RockPercent);
        _unsuitable.Text = N(s.UnsuitablePercent); _fillSlope.Text = N(s.FillSlope); _cutSlope.Text = N(s.CutSlope);
        _stockpile.IsChecked = s.StripToStockpile; _pavement.IsChecked = s.SubtractPavement;
        _design.SelectionChanged += (_, _) => _level.IsEnabled = _design.SelectedItem is Choice { Typed: true };
        _level.IsEnabled = _design.SelectedItem is Choice { Typed: true };

        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(new TextBlock { Text = "עבודות עפר", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Theme.Ink });
        body.Children.Add(new TextBlock { Text = "ערכים שלא שונו הם ברירות מחדל טיפוסיות — יש לאמת מול דו״ח הקרקע.", Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 10) });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Row(string label, UIElement control)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4), Foreground = Theme.TextSoft };
            Grid.SetRow(t, grid.RowDefinitions.Count - 1); grid.Children.Add(t);
            Grid.SetRow(control, grid.RowDefinitions.Count - 1); Grid.SetColumn(control, 1); grid.Children.Add(control);
        }
        Row("קרקע קיימת", _ground);
        Row("תכנון", _design);
        Row("מפלס פילוס (מ׳)", _level);
        Row("חישוף (ס״מ)", _strip);
        Row("מקדם התכווצות", _shrink);
        _shrink.ToolTip = "מ״ק מילוי מהודק מכל מ״ק חפירה במצב טבעי. חול 0.90–0.95, חרסית 0.85–0.90.";
        Row("מקדם תפיחה (להובלה)", _swell);
        Row("סלע מתוך החפירה (%)", _rock);
        Row("חומר לא מתאים למילוי (%)", _unsuitable);
        Row("שיפוע מדרון מילוי 1:", _fillSlope);
        Row("שיפוע מדרון חפירה 1:", _cutSlope);
        body.Children.Add(grid);
        body.Children.Add(_stockpile); body.Children.Add(_pavement); body.Children.Add(_error);

        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        var ok = Btn("חשב מחדש", "GoButton", "earth-apply"); ok.IsDefault = true; ok.Click += (_, _) => Apply();
        var cancel = Btn("ביטול", "PillButton", "earth-cancel"); cancel.IsCancel = true;
        var reset = Btn("ברירות מחדל", "QuietButton", "earth-reset");
        reset.Click += (_, _) => { var d = new Earthworks.Settings(); _strip.Text = N(d.StripCm); _shrink.Text = N(d.Shrink); _swell.Text = N(d.Swell); _rock.Text = N(d.RockPercent);
            _unsuitable.Text = N(d.UnsuitablePercent); _fillSlope.Text = N(d.FillSlope); _cutSlope.Text = N(d.CutSlope); _stockpile.IsChecked = d.StripToStockpile; _pavement.IsChecked = d.SubtractPavement;
            _ground.SelectedIndex = 0; _design.SelectedIndex = 0; };
        var guide = Btn("איך זה מחושב?", "QuietButton", "earth-guide"); guide.Click += (_, _) => EarthworksGuideWindow.Show(this);
        DockPanel.SetDock(ok, Dock.Left); DockPanel.SetDock(cancel, Dock.Left); DockPanel.SetDock(reset, Dock.Right); DockPanel.SetDock(guide, Dock.Right);
        buttons.Children.Add(ok); buttons.Children.Add(cancel); buttons.Children.Add(guide); buttons.Children.Add(reset);
        body.Children.Add(buttons);
        Content = body;
    }

    private void Apply()
    {
        try
        {
            var design = (Choice)_design.SelectedItem;
            double? level = null;
            if (design.Typed)
            {
                level = Num(_level, "מפלס הפילוס", -440, 2850);
            }
            Result = new Earthworks.Settings
            {
                ExistingName = ((Choice)_ground.SelectedItem).Surface,
                DesignName = design.Surface, ForcePlatform = design.Platform, PlatformLevel = level,
                StripCm = Num(_strip, "החישוף", 0, 100), Shrink = Num(_shrink, "מקדם ההתכווצות", 0.5, 1.5), Swell = Num(_swell, "מקדם התפיחה", 1, 2),
                RockPercent = Num(_rock, "אחוז הסלע", 0, 100), UnsuitablePercent = Num(_unsuitable, "אחוז החומר הלא מתאים", 0, 100),
                FillSlope = Num(_fillSlope, "שיפוע המילוי", 0, 10), CutSlope = Num(_cutSlope, "שיפוע החפירה", 0, 10),
                StripToStockpile = _stockpile.IsChecked == true, SubtractPavement = _pavement.IsChecked == true,
            };
            DialogResult = true;
        }
        catch (FormatException ex) { _error.Text = ex.Message; }
    }

    private static double Num(TextBox box, string what, double min, double max)
    {
        if (!double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
            throw new FormatException($"{what}: יש להזין מספר בין {min:G} ל־{max:G}.");
        return v;
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static ComboBox Combo(string id) { var c = new ComboBox { DisplayMemberPath = "Label", Padding = new Thickness(6), Margin = new Thickness(0, 3, 0, 3) }; Id(c, id); return c; }
    private static TextBox Box(string id) { var t = new TextBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 3, 0, 3), FlowDirection = System.Windows.FlowDirection.LeftToRight, HorizontalContentAlignment = HorizontalAlignment.Right }; Id(t, id); return t; }
    private static Button Btn(string text, string style, string id)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), MinWidth = 96 };
        if (Theme.Dictionary[style] is Style s) b.Style = s;
        Id(b, id); return b;
    }
    private static void Id(UIElement e, string id) => e.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, id);
}

/// <summary>"How is it worked out?" — the method, the sources and the standards behind the earthworks rows, in Hebrew.</summary>
internal sealed class EarthworksGuideWindow : Window
{
    internal static void Show(Window? owner)
    {
        var w = new EarthworksGuideWindow();
        if (owner != null) { w.Owner = owner; w.ShowDialog(); }
        else CivilModalHost.ShowFromPalette(w);   // owned by AutoCAD: an ownerless window opened behind it (live 2026-11-11)
    }

    private EarthworksGuideWindow()
    {
        Title = "איך מחושבות עבודות העפר"; Width = 760; Height = 820; MinWidth = 520; MinHeight = 400;
        FlowDirection = System.Windows.FlowDirection.RightToLeft; Theme.Apply(this, 13); Background = Theme.Canvas;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "earth-guide-window");
        var body = new StackPanel { Margin = new Thickness(26, 20, 26, 26) };
        body.Children.Add(new TextBlock { Text = "טופוגרפיה ועבודות עפר בכתב הכמויות", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Theme.Ink });
        body.Children.Add(P("הכלי משווה את הקרקע הקיימת לתכנון בתוך האזור המסומן (או בכל תחום משטח התכנון), מחשב חפירה ומילוי, " +
                            "מנתח את פני השטח ומוסיף לכתב הכמויות את סעיפי פרק 51 במחירון נתיבי ישראל. השרטוט אינו משתנה."));
        foreach (var (title, paragraphs) in Sections) { body.Children.Add(H(title)); foreach (var p in paragraphs) body.Children.Add(P(p)); }
        body.Children.Add(H("סעיפי כתב הכמויות"));
        body.Children.Add(ItemsTable());
        body.Children.Add(H("לפי מה"));
        foreach (var p in Sources) body.Children.Add(P("• " + p));
        var close = new Button { Content = "סגור", Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsCancel = true };
        if (Theme.Dictionary["PillButton"] is Style s) close.Style = s;
        body.Children.Add(close);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static readonly (string Title, string[] Paragraphs)[] Sections =
    {
        ("1. הקרקע הקיימת", new[]
        {
            "משטח Civil 3D ששמו, שכבתו או תיאורו מעידים על קרקע קיימת: EG, EXIST, TOPO, SURVEY, MK, קיים, טופו, מדידה. כשיש שני משטחים בלי שם מזהה — הרחב מביניהם הוא הקרקע.",
            "בלי משטח — נבנה משטח (TIN, דלוני) מהמדידה שבשרטוט ובשרטוטים המצורפים (XREF): נקודות גובה ונקודות מדידה (M1501, M1502, M1505), קווי גובה (M1506, M1507), קווי שבר, מדרונות וסוללות (M1508–M1513) לפי קודי השכבות של מפ״י, וגם שכבות TOPO / CONTOUR / SPOT / קווי גובה. כיתובים (M1500, M1509) אינם קרקע.",
            "שגיאות מדידה נפסלות לפני הבנייה: נקודה בגובה 0 בין נקודות בגובה אמיתי, \"גובה\" שהוא בעצם קואורדינטה, ונקודה בודדת שחורגת ביותר מ־3 מ׳ מכל שכנותיה. משולשים ארוכים במיוחד (מעל 100 מ׳, או פי 5 מהרגיל במדידה) מוסרים — הם מגשרים על אזורים שלא נמדדו.",
        }),
        ("2. התכנון", new[]
        {
            "משטח שתית (Datum, Subgrade, שתית) — כבר בפני השתית, משתמשים בו כפי שהוא.",
            "משטח תכנון או קורידור (FG, Design, Top, תכנון) — פני כביש סופיים. עבודות העפר מסתיימות בפני השתית, ולכן מתחת לכל משטח כביש בכתב הכמויות (מיסעה, מדרכה, שביל אופניים…) המשטח מונמך בעובי מבנה המיסעה שכתב הכמויות כבר מתמחר — מהדו״ח, מהזנה ידנית או מברירת המחדל של סוג הדרך.",
            "אין משטח תכנון — פילוס של האזור המסומן למפלס אחד: המפלס שמאזן חפירה ומילוי (בחישוב שיטת החצייה, כולל מקדם ההתכווצות והמדרונות), או מפלס שמזינים בהגדרות. המפלס המאוזן הוא הצעה ומסומן \"ברירת מחדל\".",
        }),
        ("3. חישוב הנפחים", new[]
        {
            "שיטת הרשת: על האזור נפרשת רשת תאים (עד כ־400 אלף תאים; 0.5 מ׳ באזור קטן, עד 5 מ׳ ויותר באזור גדול). בכל תא נדגמים גובה הקרקע וגובה התכנון; ההפרש כפול שטח התא הוא חפירה או מילוי. חפירה ומילוי נסכמים בנפרד — לעולם לא מקזזים בתוך תא.",
            "הנפחים נמדדים במצבם הטבעי (מ״ק \"בבנק\"), בין הקרקע לפני העבודה לקווי התכנון, כפי שהמפרטים מודדים חפירה; המילוי הוא מ״ק מהודק במקום.",
            "חישוף: שכבה עליונה (20 ס״מ כברירת מחדל) מוסרת מכל שטח העבודה ומתומחרת למ״ר. החפירה נמדדת מתחת לחישוף, והמילוי משלים גם את השכבה שחושפה — \"באזורי חפירה יופחת החישוף מכמות החפירה ובאזורי מילוי יתווסף\".",
            "מדרונות הפילוס: מכל נקודה בשולי האזור יורד (או עולה) מדרון עד הקרקע, 1:2 במילוי ו־1:1.5 בחפירה כברירת מחדל; נפחו ½·m·h² לכל מטר שוליים. משטח תכנון של קורידור כבר כולל את המדרונות שלו.",
            "בקרת דיוק: אותו חישוב חוזר על רשת כפולה בגודלה. ההפרש בין השתיים מוצג; מעל 2% מומלץ לסמן אזור קטן יותר.",
        }),
        ("4. ניתוח טופוגרפי", new[]
        {
            "רום מזערי, מרבי וממוצע; שיפוע קרקע לכל תא והתפלגות השטח לפי שיפוע (עד 2%, 2–5%, 5–10%, 10–15%, מעל 15%); כיוון הניקוז הכללי (לאן המים זורמים).",
            "סוג השטח לפי טבלה 2.1 ב\"תבחיני תכן למזעור הפגיעה בקרקע ובסביבה\" (משרד התחבורה, 2010; על פי הנחיות מע״צ 1994): מישורית — פחות מ־30 מ׳ הפרש גובה לק״מ (שיפועים עד 1:10), גבעית — 30–70 מ׳ לק״מ, הררית — יותר מ־70 מ׳ לק״מ (בדרך כלל כרוך בחציבה). הסוג משמש גם לטבלאות השיפועים בתכנון הדרך.",
            "מילוי על קרקע תלולה מ־15% דורש מידור (מדרגות ברוחב 2 מ׳ לפחות) — הכלי מתריע כשחלק ניכר מהמילוי נמצא על קרקע כזו.",
        }),
        ("5. מאזן העפר", new[]
        {
            "מילוי מהודק = חפירה ראויה × מקדם התכווצות (0.90 כברירת מחדל: חול 0.90–0.95, חרסית 0.85–0.90). החפירה שהמילוי צורך היא \"חפירה והובלה לאזורי המילוי\"; היתר — \"חפירה וסילוק עודפים\". חסר במילוי — \"מילוי מובא\".",
            "חומר לא מתאים (אחוז שמזינים) נשלח לסילוק. סלע: לפי המפרטים החציבה כלולה במחיר החפירה; אחוז סלע שמזינים יוצר סעיף חציבה נפרד.",
            "מקדם התפיחה (1.25) רק מעריך את נפח ההובלה במשאיות — כתב הכמויות במצב טבעי.",
            "עדיפות לאיזון בתכנון (פרופיל וקווי דיפון), לקליטת עודפים בגינון ובשיקום, ולשימור הקרקע העליונה — לכן ברירת המחדל לחישוף היא עירום לשימוש חוזר.",
        }),
        ("6. מה לבדוק", new[]
        {
            "כל המקדמים, המדרונות והחישוף הם ערכים טיפוסיים ומסומנים \"ברירת מחדל\" בכל שורה ובייצוא — יש להחליף אותם בערכי דו״ח הקרקע (הגדרות).",
            "לחפירה למבנים, לגשרים ולקירות תמך יש סעיפים משלהם (51.02.0110–0230) — הכלי לא מחליט עליהם.",
            "כשיש קורידור עם קווי דגימה, ניתן לקרוא גם את נפחי החתכים שלו (מתקדם › כבישים); השוו בין שתי השיטות.",
        }),
    };

    private static readonly string[] Sources =
    {
        "המפרט הכללי לעבודות בנייה (הספר הכחול), פרק 51 עבודות סלילה — 51.03 חישוף, 51.04 עבודות עפר, 51.04.14 הידוק — כפי שמצוטט במפרטים טכניים של מכרזים (למשל רהט צפון 2023, טייבה 2026, רשות הטבע והגנים).",
        "תבחיני תכן למזעור הפגיעה בקרקע ובסביבה, משרד התחבורה 2010 — סוג השטח (טבלה 2.1), איזון עפר, שימור קרקע עליונה.",
        "הנחיות לתכנון גאומטרי של דרכים בין־עירוניות, פרק 3 §3.6 — שיפועי מדרונות (1:4–1:6 במילוי, 1:3–1:6 בחפירה לבטיחות; תלולים מזה — עם מעקה).",
        "מחירון נתיבי ישראל המאוחד, יולי 2026 — פרקים 51.01 ו־51.02.",
    };

    private UIElement ItemsTable()
    {
        var rows = new (string Code, string What)[]
        {
            (Earthworks.Items.StripStockpile + " / " + Earthworks.Items.StripRemove, "חישוף — עירום זמני לשימוש חוזר / סילוק (מ״ר)"),
            (Earthworks.Items.CutToFill, "חפירה והובלת החומר לאזורי המילוי (מ״ק במצב טבעי)"),
            (Earthworks.Items.CutToWaste, "חפירה וסילוק העודפים (מ״ק במצב טבעי)"),
            (Earthworks.Items.Rock, "חציבה — רק כשהוזן אחוז סלע (מ״ק)"),
            (Earthworks.Items.ImportFill, "מילוי מובא — מה שהחפירה אינה מספקת (מ״ק מהודק)"),
            (Earthworks.Items.Compaction, "הידוק מבוקר של כל המילוי (מ״ק)"),
            (Earthworks.Items.Subgrade, "הידוק קרקע יסוד — שתית בכל שטח העבודה (מ״ר)"),
        };
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (code, what) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var c = new TextBlock { Text = code, FlowDirection = System.Windows.FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 3, 0, 3), FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink };
            var w = new TextBlock { Text = what, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 3, 0, 3), Foreground = Theme.TextSoft };
            Grid.SetRow(c, grid.RowDefinitions.Count - 1); Grid.SetRow(w, grid.RowDefinitions.Count - 1); Grid.SetColumn(w, 1);
            grid.Children.Add(c); grid.Children.Add(w);
        }
        return grid;
    }

    private static TextBlock H(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Theme.AccentInk, Margin = new Thickness(0, 16, 0, 4) };
    private static TextBlock P(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Ink, LineHeight = 21, Margin = new Thickness(0, 3, 0, 3) };
}
