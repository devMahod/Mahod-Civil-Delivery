using System.Dynamic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace Mahod.CivilDelivery.UiPreview;

/// <summary>One view of the gallery: a palette tab or a dialog, built from its XAML file with sample data.</summary>
public sealed class View
{
    public required string Title { get; init; }
    public required string FileName { get; init; }
    public required string XamlPath { get; init; }
    public required Func<FrameworkElement, Size> Fill { get; init; }
    public bool IsPalette { get; init; }
    public Size WindowSize { get; set; } = new(720, 900);

    /// <summary>The view as a detached element at its natural size (a palette at 720x900, a dialog at its own size).</summary>
    public (FrameworkElement Element, Size Size) Build()
    {
        var root = Load();
        if (root is Window window)
        {
            WindowSize = new Size(double.IsNaN(window.Width) ? 640 : window.Width,
                double.IsNaN(window.Height) ? Math.Max(window.MinHeight, 520) : window.Height);
            Fill(window); // names resolve through the window's name scope, so fill before detaching
            var content = (FrameworkElement)window.Content;
            var resources = window.Resources;
            window.Content = null; window.Resources = new ResourceDictionary();
            var host = new Border
            {
                Child = content, Background = window.Background, FlowDirection = window.FlowDirection,
                UseLayoutRounding = true,
            };
            host.Resources = resources;
            TextElement.SetFontFamily(host, window.FontFamily);
            TextElement.SetFontSize(host, window.FontSize);
            TextElement.SetForeground(host, window.Foreground);
            return (host, WindowSize);
        }
        var element = (FrameworkElement)root;
        var size = Fill(element);
        return (element, size);
    }

    /// <summary>The view as a real window (a dialog opens as itself; the palette in a palette-sized window).</summary>
    public Window BuildWindow()
    {
        var root = Load();
        if (root is Window window)
        {
            Fill(window);
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.ShowInTaskbar = true;
            return window;
        }
        var element = (FrameworkElement)root;
        var size = Fill(element);
        return new Window
        {
            Title = Title, Content = element, Width = size.Width + 16, Height = size.Height + 39,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = element is Control c ? c.Background : Brushes.White,
        };
    }

    private object Load()
    {
        var xml = XDocument.Load(XamlPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        // Exactly what the plugin's layout tests do: no code-behind, no event handlers, no plugin types.
        foreach (var attribute in xml.Root!.DescendantsAndSelf().Attributes()
                     .Where(a => a.Name.LocalName == "Class" || a.Value.StartsWith("On", StringComparison.Ordinal)).ToArray())
            attribute.Remove();
        foreach (var element in xml.Root.Descendants().Where(e => e.Name.NamespaceName.StartsWith("clr-namespace:MahodAI", StringComparison.Ordinal)).ToArray())
            element.Remove();
        foreach (var attribute in xml.Root.DescendantsAndSelf().Attributes().Where(a => a.Value.Contains("Converter={StaticResource GroupCheck}", StringComparison.Ordinal)).ToArray())
            attribute.Remove();
        var context = new ParserContext { BaseUri = new Uri(XamlPath) };
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml.ToString()));
        return XamlReader.Load(stream, context);
    }
}

/// <summary>The gallery's views, in the order the engineer meets them.</summary>
public static class Views
{
    public static IEnumerable<View> All(string uiDir, string assetsDir)
    {
        var palette = Path.Combine(uiDir, "CivilDeliveryControl.xaml");
        yield return Palette("הפאנל · חתכים", "palette-sections", palette, assetsDir, 0);
        yield return Palette("הפאנל · כתב כמויות", "palette-estimate", palette, assetsDir, 1);
        yield return Palette("הפאנל · פרויקט", "palette-project", palette, assetsDir, 2);
        yield return Palette("הפאנל · תמיכה", "palette-support", palette, assetsDir, 3);
        yield return Palette("הפאנל · בזמן עבודה", "palette-busy", palette, assetsDir, 1, busy: true);
        foreach (var (file, title) in new[]
                 {
                     ("CatalogPickerDialog", "שיוך שכבה לסעיף מחירון"),
                     ("ClLayerPickerDialog", "בחירת שכבת קווי חתך"),
                     ("CorridorSourcePickerDialog", "בחירת מדידת קורידורים"),
                     ("EarthworksDecisionDialog", "החלטת היקף עבודות עפר"),
                     ("EstimateSourceReviewDialog", "אישור מקורות האומדן"),
                     ("PriceBookMappingDialog", "טעינת מחירון"),
                     ("ProvenMappingBatchDecisionDialog", "אישור מיפויים חד־משמעיים"),
                     ("QuantityExclusionDecisionDialog", "אישור החרגת קבוצת כמות"),
                     ("QuantityNoiseBatchDecisionDialog", "אישור מרוכז — סימוני עזר"),
                     ("SectionDecisionDialog", "הכרעת רשומת חתך"),
                     ("SectionRowAuthorityDecisionDialog", "אישור מקור זכות דרך"),
                     ("SectionSpanLabelDecisionDialog", "שמות רצועות"),
                     ("TrafficDirectionDecisionDialog", "כיווני נסיעה"),
                 })
        {
            var path = Path.Combine(uiDir, file + ".xaml");
            if (!File.Exists(path)) continue;
            yield return new View { Title = title, FileName = "dialog-" + file, XamlPath = path, Fill = SampleDialog };
        }
    }

    private static View Palette(string title, string file, string path, string assetsDir, int tab, bool busy = false) => new()
    {
        Title = title, FileName = file, XamlPath = path, IsPalette = true,
        Fill = element => SamplePalette(element, assetsDir, tab, busy),
    };

    private static T? Find<T>(FrameworkElement root, string name) where T : class => root.FindName(name) as T;
    private static void Text(FrameworkElement root, string name, string text) { if (Find<TextBlock>(root, name) is { } t) t.Text = text; }

    private static Size SamplePalette(FrameworkElement root, string assetsDir, int tab, bool busy)
    {
        if (Find<TabControl>(root, "Tabs") is { } tabs) tabs.SelectedIndex = tab;
        if (Find<Image>(root, "LogoImage") is { } logo && File.Exists(Path.Combine(assetsDir, "mahod_mark.png")))
            logo.Source = new BitmapImage(new Uri(Path.Combine(assetsDir, "mahod_mark.png")));
        Text(root, "ProjectLine", "פרויקט: כביש 34 — שדרות · פרופיל נטען");
        Text(root, "DashboardTitle", "השרטוט הזה — MHD-HW-SHDR-000Rd-34-SD-120X.dwg");
        Text(root, "DashboardBody", "3 תוואים · 2 משטחים · רשת ניקוז אחת. קובץ CL: Survey-Cuts.dwg (18 קווי חתך). 4 קווי דגימה של הכלי קיימים בשרטוט.");
        Text(root, "SectionStepTitle", "חתך STA-12145 · שמות הרצועות של החתך");
        Text(root, "SectionStepDetail", "אשר את שמות הרצועות שהוצעו מהראיות, או בחר שם אחר. השם נשמר בפרופיל ומריץ תכנון מחדש.");
        Text(root, "GateReason", "18 רשומות · 11 מוכנות · 5 שמות רצועות · 2 כיווני נסיעה — יש להשלים את המשימות או להחריג CL רק מסיבה הנדסית מאושרת");
        Text(root, "SectionSurfaceLegend", "קיים — ירוק · תכנון — כחול · מערכות — לפי שכבת המקור");
        foreach (var (name, state) in new[] { ("SectionStage1", "done"), ("SectionStage2", "current") })
            if (Find<Border>(root, name) is { } tile) tile.Tag = state;
        if (Find<DataGrid>(root, "SectionsGrid") is { } sections)
            sections.ItemsSource = Enumerable.Range(0, 18).Select(i => new
            {
                SectionId = "STA-" + (12145 + i * 60),
                Status = i % 5 == 0 ? "אומת" : i % 7 == 3 ? "חסום" : "דרושה בדיקה",
                NextStep = i % 5 == 0 ? "אומת — אפשר להציג" : i % 7 == 3 ? "בחירת מקור זכות דרך" : i % 2 == 0 ? "שמות רצועות" : "כיווני נסיעה",
                Alignment = "2000", Station = (12145 + i * 60).ToString("0.00"), Utilities = "מים, חשמל, בזק",
                Skew = "90.0°", Extents = "25.0 / 25.0", Sources = "GM, UT", TrafficDirections = i % 2 == 0 ? "2/2 מוכרעים" : "1/2 · דרושה הכרעה",
                Severity = i % 5 == 0 ? "ok" : i % 7 == 3 ? "blocked" : "review",
            }).ToList();
        Text(root, "EstimateFlowProgress", "1 מדידה ← 2 בדיקת כמויות ← 3 היקף אומדן ← 4 תמחור ← 5 Excel");
        Text(root, "EstimateNextAction", "חפש קבוצה, בדוק את מקורה ובחר או שנה סעיף מחירון. הצעה אינה אישור.");
        Text(root, "EstimateUnitsLine", "יחידות: מטר (Civil) · מאושר · מחירון נת\"י האחוד 07/2026");
        if (Find<ComboBox>(root, "PriceBookCombo") is { } books)
        {
            books.ItemsSource = new[] { new { Label = "נתיבי ישראל — המחירון האחוד · 07/2026" }, new { Label = "מחירון עירוני · 08/2025" } };
            books.SelectedIndex = 0;
        }
        Text(root, "QuantityFilterCount", "מוצגות 6 מתוך 1,041 קבוצות · סינון תצוגה בלבד");
        Text(root, "EstimateRowTitle", "HW-CURB · אורך · 29,287.63 מטר");
        Text(root, "EstimateRowActionHint", "בחר או שנה סעיף מחירון; אם אין מחיר, ניתן להזין מחיר פרויקט מנומק. השיוך אינו אישור למדידה.");
        if (Find<TextBlock>(root, "EstimateReviewOverview") is { } overview)
        {
            overview.Visibility = Visibility.Visible;
            overview.Text = "1,031 קבוצות מדידה · 12 משויכות · 341 עם הצעות לבדיקה · 170 קבוצות עזר לבדיקה יחד";
        }
        foreach (var name in new[] { "BtnExportEngineerDraft", "BtnFamilyReview", "BtnEditionLinks", "BtnExportBoqRules", "BtnExportPricedDraft", "BtnExportMeasurementDraft" })
            if (Find<Button>(root, name) is { } button) button.IsEnabled = true;
        if (Find<DataGrid>(root, "QuantitiesGrid") is { } quantities)
            quantities.ItemsSource = new[]
            {
                Row("HW-CURB · 297 × LWPOLYLINE", "כבישים", "אורך", "29,287.63 מטר", "הצעה: U51.06.1900", "אבן שפה טרומית 23/15 — הצעה לבדיקה", "בדיקת מדידה", "—", "review"),
                Row("HW-ASPH-01 · 12 × HATCH", "כבישים", "שטח", "4,120.50 מ\"ר", "51.04.0020", "אספלט בטון 5 ס\"מ, שכבה נושאת", "שיוך מאושר", "98.00", "ok"),
                Row("LA-TREE-RPL · 219 × BLOCKREFERENCE", "אדריכלות נוף", "ספירה", "219 יח'", "הצעה: 41.01.0110", "נטיעת עץ בוגר — הצעה לבדיקה", "בדיקת מדידה", "—", "review"),
                Row("UT-WATER-EX · 44 × LWPOLYLINE", "תשתיות", "אורך", "1,204.00 מטר", "—", "טרם אושר סעיף", "מצב קיים", "—", "muted"),
                Row("HW-SIGN · 31 × BLOCKREFERENCE", "תנועה", "ספירה", "31 יח'", "57.01.0010", "תמרור מסוג א' כולל עמוד", "שיוך מאושר", "420.00", "ok"),
                Row("GM-STATION · 1,041 × LINE", "עזר", "אורך", "520.10 מטר", "—", "סימוני תחנות — לבדיקה יחד", "סימון עזר", "—", "blocked"),
            };
        Text(root, "ProfileSummary", "פרופיל: כביש 34 — שדרות · נשמר 24.10.2026 · 3 תוואים, 2 משטחים · 14 החלטות מאשר");
        Text(root, "DrawingUnitsSummary", "יחידות השרטוט: מטר (Civil) — אושרו ע\"י נ. כהן ב-20.10.2026");
        Text(root, "CatalogSummary", "מחירון פעיל: נתיבי ישראל — המחירון האחוד 07/2026 · 12,118 סעיפים · קובץ CL: Survey-Cuts.dwg");
        Text(root, "ActivityLog", "11:02 פתיחת פאנל · פרופיל נטען\n11:03 תכנון: 18 רשומות, 11 מוכנות\n11:07 סריקת כמויות: 1,041 קבוצות, 857 רשומות\n11:09 ייצוא כתב כמויות: Estimate_2026-10-26.xlsx");
        Text(root, "StatusLabel", "נסרקו 857 רשומות · 341 הצעות לבדיקה");
        Text(root, "DrawingLabel", "שרטוט: MHD-HW-SHDR-000Rd-34-SD-120X.dwg");
        if (root.FindName("ApproverRun") is System.Windows.Documents.Run approver) approver.Text = "מאשר נוכחי: נ. כהן";
        if (busy && Find<Grid>(root, "BusyOverlay") is { } overlay)
        {
            overlay.Visibility = Visibility.Visible;
            Text(root, "BusyText", "סורק כמויות… 3/5 קבצים");
        }
        return new Size(720, 900);
    }

    private static object Row(string source, string category, string method, string quantity, string code, string description, string status, string price, string severity) => new
    {
        Source = source, SourceCategory = category, MethodDisplay = method, QuantityDisplay = quantity,
        CatalogCodeDisplay = code, CatalogDescriptionDisplay = description, CatalogCodeDetail = description,
        StatusDisplay = status, StatusDetail = status, PriceDisplay = price, Severity = severity,
    };

    private static Size SampleDialog(FrameworkElement root)
    {
        Text(root, "SubjectTitle", "HW-CURB · אורך · 29,287.63 מטר");
        Text(root, "SubjectDetail", "297 פוליליינים בשכבה HW-CURB · שרטוט MHD-HW-SHDR-000Rd-34-SD-120X.dwg · SHA-256 7f3a…");
        Text(root, "Headline", "מחירון נת\"י האחוד 07-2026.xlsx");
        Text(root, "FileTitle", "Survey-Cuts.dwg");
        Text(root, "ScanSummary", "נסרקו 6 שכבות · 18 קווים ישרים חוצים תוואי");
        Text(root, "SummaryText", "12 קבוצות עם הצעה חד־משמעית · 4 קטגוריות · מחירון נת\"י האחוד 07/2026");
        Text(root, "CurrentStateText", "עבודות עפר טרם נבדקו — לא אפס ולא הוחרגו.");
        Text(root, "QuantitySummaryText", "GM-STATION · אורך · 520.10 מטר · 1,041 קווים");
        Text(root, "ResultCount", "24 תוצאות");
        Text(root, "ReviewGroupDetail", "4 שורות ללא שם · 2 הצעות חזקות");
        Text(root, "BatchHint", "יחול על 3 רשומות נוספות עם ממצא CL_NO_INTERSECTION");
        Text(root, "SelectionSummary", "נבחרו 5 מתוך 7 מקורות · 2 קטגוריות");
        if (Find<TextBox>(root, "InventoryText") is { } inventory)
            inventory.Text = "MHD-HW-SHDR-000Rd-34-SD-120X.dwg — מודל\n  GM.dwg — XREF (תכנון)\n  UT.dwg — XREF (תשתיות)\n  Survey-Cuts.dwg — קובץ CL";
        if (Find<TextBox>(root, "SummaryBox") is { } summary)
            summary.Text = "גיליון: מחירון · שורת כותרת: 3 · 12,118 סעיפים נקראו · 11,980 עם מחיר · יחידות: מ\"א, מ\"ר, מ\"ק, יח', טון, קומפ'";
        if (Find<TextBox>(root, "ReasonBox") is { } reason) reason.Text = "";
        if (Find<TextBox>(root, "ApproverBox") is { } approverBox) approverBox.Text = "נ. כהן";
        foreach (var grid in Descendants(root).OfType<DataGrid>())
        {
            if (grid.ItemsSource != null) continue;
            if (grid.AutoGenerateColumns && grid.Columns.Count == 0)
            {
                grid.ItemsSource = Enumerable.Range(1, 8).Select(i => new { סעיף = $"51.0{i}.00{i}0", תיאור = "סעיף לדוגמה " + i, יחידה = "מ\"א", מחיר = (i * 48.5).ToString("0.00") }).ToList();
                continue;
            }
            var rows = new List<ExpandoObject>();
            for (var i = 0; i < 7; i++)
            {
                var row = new ExpandoObject();
                var dict = (IDictionary<string, object?>)row;
                foreach (var column in grid.Columns)
                {
                    if (column is DataGridBoundColumn { Binding: System.Windows.Data.Binding binding } && binding.Path?.Path is { } path)
                        Set(dict, path, Sample(path, i));
                }
                dict["IsApproved"] = i % 3 == 0; dict["Included"] = i % 4 != 1; dict["Label"] = i % 2 == 0 ? "נתיב נסיעה" : "";
                dict.TryAdd("Name", i == 0 ? "MHD-HW-SHDR-000Rd-34-SD-120X.dwg" : new[] { "GM.dwg", "UT.dwg", "Survey-Cuts.dwg", "TR_GISHA_hlfa-5.dwg", "ROW.dwg", "Topo.dwg" }[(i - 1) % 6]);
                dict.TryAdd("Path", @"C:\Projects\Road34\dwg\" + dict["Name"]);
                dict.TryAdd("Status", i % 4 == 1 ? "לא נטען" : "זמין");
                dict["Choices"] = new[] { "נתיב נסיעה", "שול", "מדרכה", "אי תנועה" };
                dict["CategoryOptions"] = new[] { new KeyValuePair<string, string>("design", "תכנון"), new KeyValuePair<string, string>("existing", "מצב קיים") };
                dict["Category"] = "design";
                dict["SelectedChoice"] = null; dict["PreviousNames"] = Array.Empty<object>();
                rows.Add(row);
            }
            grid.ItemsSource = rows;
            if (grid.Visibility == Visibility.Collapsed) grid.Visibility = Visibility.Visible;
        }
        foreach (var list in Descendants(root).OfType<ListView>())
        {
            if (list.ItemsSource != null) continue;
            list.ItemsSource = Enumerable.Range(0, 5).Select(i => new
            {
                Layer = i == 0 ? "GM-CL-SECTIONS" : "GM-LINE-" + i, ScoreDisplay = (96 - i * 17) + "%",
                Evidence = $"{18 - i * 3} קווים ישרים · {16 - i * 3} חוצים תוואי · אורך אופייני 50 מ'",
                Name = i == 0 ? "MHD-HW-SHDR-000Rd-34-SD-120X.dwg" : $"TR_GISHA_hlfa-{i}.dwg", Folder = @"C:\Projects\Road34\dwg",
                FullPath = @"C:\Projects\Road34\dwg\TR.dwg", // not shown
            }).ToList();
            list.SelectedIndex = 0;
        }
        foreach (var combo in Descendants(root).OfType<ComboBox>())
        {
            if (combo.ItemsSource != null || combo.Items.Count > 0 || combo.Name is "BatchLabelBox") continue;
            combo.ItemsSource = combo.Name switch
            {
                "FindingCodeBox" => new[] { new { Label = "CL_NO_INTERSECTION — אין חיתוך עם תוואי" }, new { Label = "ALIGNMENT_AMBIGUOUS — כמה תוואים" } },
                "ReviewGroupBox" => new[] { new { Title = "רצועות בלי שם (4)" }, new { Title = "הצעות חזקות (2)" } },
                "BatchFlowBox" => new[] { new { Label = "עם הציר (עולה)" }, new { Label = "נגד הציר (יורד)" } },
                "SheetBox" or "CodeBox" or "DescriptionBox" or "UnitBox" or "PriceBox" => new[] { "A — סעיף", "B — תיאור", "C — יחידה", "D — מחיר" },
                _ => new[] { "אפשרות 1", "אפשרות 2" },
            };
            combo.SelectedIndex = 0;
        }
        return new Size(0, 0);
    }

    private static void Set(IDictionary<string, object?> row, string path, object value)
    {
        var parts = path.Split('.');
        if (parts.Length == 1) { row[path] = value; return; }
        if (!row.TryGetValue(parts[0], out var nested) || nested is not ExpandoObject expando)
        {
            expando = new ExpandoObject(); row[parts[0]] = expando;
        }
        Set((IDictionary<string, object?>)expando, string.Join('.', parts.Skip(1)), value);
    }

    private static string Sample(string path, int i) => path.Split('.').Last() switch
    {
        "Code" or "CatalogCode" => $"51.0{1 + i % 4}.{1900 + i * 10}",
        "Description" or "CatalogDescription" => i % 2 == 0 ? "אבן שפה טרומית 23/15 ס\"מ, כולל יסוד בטון" : "פירוק אספלט קיים בעובי עד 10 ס\"מ והובלה לאתר מאושר",
        "Unit" => i % 2 == 0 ? "מ\"א" : "מ\"ר",
        "PriceDisplay" or "Price" => (86.5 + i * 12).ToString("0.00"),
        "Why" or "EvidenceSummary" or "Evidence" => "טקסט זהה · יחידה תואמת · מחיר נעוץ",
        "Layer" => "HW-CURB-" + (i + 1),
        "Section" => "STA-" + (12145 + i * 60),
        "Alignment" => "2000",
        "Station" => (12145 + i * 60).ToString("0.00"),
        "Strip" => i % 2 == 0 ? "נתיב ימני" : "נתיב שמאלי",
        "CurrentDirection" => i % 2 == 0 ? "עם הציר · חץ HW-ARRW" : "— · ללא ראיה",
        "Offset" => (-3.5 + i).ToString("0.0"),
        "Width" => "3.50", "From" => (-7 + i).ToString("0.0"), "To" => (-3.5 + i).ToString("0.0"),
        "CurrentLabel" => i % 3 == 0 ? "שול" : "— ללא שם",
        "PreviousSummary" => i % 2 == 0 ? "נתיב נסיעה · 3.50" : "—",
        "Bounds" => "curb → curb", "Suggestion" => "נתיב נסיעה", "Homology" => "קבוצה " + (1 + i % 2), "Reason" => "רוחב 3.5 מ' ואבני שפה משני הצדדים",
        "Hash" => "7f3a9c…" + i, "Path" => @"C:\Projects\Road34\dwg\GM.dwg", "Xref" => "GM → MHD-HW-SHDR",
        "Source" => "HW-CURB · 297 × LWPOLYLINE", "EntityType" => "LWPOLYLINE", "QuantityDisplay" or "TotalQuantity" => (1204.5 + i * 30).ToString("N2"),
        "SignificanceReason" => "קווים קצרים (0.5 מ') בפיזור קבוע של 20 מ' — סימוני תחנות",
        "RuleKey" => "LWPOLYLINE|HW-CURB|length|m", "MeasurementKind" => "length", "ObjectCount" => (31 + i * 7).ToString(),
        "Point" => "212345.12 / 654321.98", "Gap" => "0.02", "Name" => "GM.dwg", "Status" => "זמין",
        _ => "דוגמה " + (i + 1),
    };

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
