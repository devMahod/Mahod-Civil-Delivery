using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Button = System.Windows.Controls.Button;
using DataGrid = System.Windows.Controls.DataGrid;
using Expander = System.Windows.Controls.Expander;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using TextBlock = System.Windows.Controls.TextBlock;
using UserControl = System.Windows.Controls.UserControl;
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SectionPaletteLayoutTests
{
    [Theory]
    [InlineData(480, 880, 6)]
    [InlineData(540, 880, 6)]
    [InlineData(480, 880, 10)]
    public void UnloadedXrefListStaysInTheCardAndTheTableKeepsItsHeight(int width, int height, int xrefCount)
    {
        // b40 live (b40_01; Codex 15:20/15:23): the vertical name list was shown in the card and again above the
        // table, which squeezed the table to 38.5 px at 480 x 880. The card keeps the list; the table line counts.
        OnSta(() =>
        {
            var control = LoadActualMarkup();
            T Find<T>(string name) where T : class => (T)control.FindName(name);
            var blockers = Enumerable.Range(1, xrefCount).Select(i => new DeliveryFinding
            {
                Code = SectionFindingCodes.XrefTraversalUnresolved, Domain = "sections", Severity = FindingSeverity.Error,
                Title = XrefAvailabilityText.Title($"TR-FloorGrd-GM-SD-M30-{i:00}", true, "לא ניתן להוכיח חצי כיוון נסיעה"),
                Message = XrefAvailabilityText.Message($"TR-FloorGrd-GM-SD-M30-{i:00}", true, $@"C:\p\{i}.dwg"),
            }).ToList();
            var decision = SectionGuidedActionPolicy.Evaluate(new SectionGuidedActionSnapshot
            {
                HasDrawing = true, ProfileUsable = true, HasPlan = true, PlanRecordCount = 28,
                GlobalPlanningBlockReason = XrefAvailabilityText.Summarize(blockers),
            });
            Find<TextBlock>("SectionStepTitle").Text = decision.Title;
            Find<TextBlock>("SectionStepDetail").Text = decision.Detail;
            Find<TextBlock>("GateReason").Text = SectionPlanBlockerSummaryLogic.Describe(blockers, id => id,
                anyRecordReady: false, xrefNamesShownElsewhere: true);
            Find<DataGrid>("SectionsGrid").ItemsSource = Enumerable.Range(0, 28).Select(i => new
            {
                SectionId = (100 + i).ToString(), Status = "חסום", NextStep = "טיפול בחסימת התכנון",
                Alignment = "100", Station = (i * 5.0).ToString("0.00"), Utilities = "", Severity = "error"
            }).ToArray();
            Layout(control, width, height);
            SaveRender(control, $"section-xref-{xrefCount}-{width}x{height}.png", width, height);
            Assert.DoesNotContain("TR-FloorGrd-GM-SD-M30-01", Find<TextBlock>("GateReason").Text);
            Assert.Contains("TR-FloorGrd-GM-SD-M30-01", Find<TextBlock>("SectionStepDetail").Text);
            Assert.True(Find<DataGrid>("SectionsGrid").ActualHeight >= 150,
                $"The XREF list must not squeeze the section table ({Find<DataGrid>("SectionsGrid").ActualHeight:0.0} px).");
        });
    }

    [Theory]
    [InlineData(480, 880, false, false)]
    [InlineData(480, 880, true, false)]
    [InlineData(540, 880, false, false)]
    [InlineData(540, 880, true, false)]
    [InlineData(720, 980, true, false)]
    [InlineData(540, 880, false, true)]
    public void ActualMarkupKeepsActionsReadableAndTableUsable(int width, int height, bool expanded, bool blocked)
    {
        OnSta(() =>
        {
            var control = LoadActualMarkup();
            T Find<T>(string name) where T : class => (T)control.FindName(name);
            Find<TextBlock>("ProjectLine").Text = "בדיקת תצוגה לא מקוונת — נתוני דוגמה";
            Find<TextBlock>("DashboardTitle").Text = "השרטוט הזה — 6422-CIVIL-WEST.natali-060926.native54.dwg";
            Find<TextBlock>("DashboardBody").Text = "22 תוואים · 10 משטחים\nקובץ CL: 28 קווי חתך\n4 חתכים של הכלי בשרטוט";
            Find<TextBlock>("SectionStepTitle").Text = blocked
                ? "חתך STA-42676 · יש להשלים את נתוני החתך"
                : "חתך STA-42676 · החתך הנבחר אומת";
            Find<TextBlock>("SectionStepDetail").Text = blocked
                ? "לא ניתן ליצור את החתך עד להשלמת הנתונים. פתח את פרטי החתך כדי לקרוא את הסיבה ואת הפעולה הנדרשת."
                : "האימות של החתך הנבחר הושלם. הצג את תצוגת החתך ובדוק את התוצאה על המסך.";
            Find<Button>("BtnSectionNext").Content = blocked ? "השלם נתוני חתך" : "הצג חתך מאומת";
            Find<Button>("BtnSectionNext").IsEnabled = !blocked;
            Find<Button>("BtnShow").Content = "הצג חתך קיים";
            Find<Button>("BtnNameAllSpans").Content = "סקירת כל הרצועות (89)…";
            Find<Button>("BtnResolveDirection").Content = "ערוך כיווני נסיעה בחתך…";
            Find<Button>("BtnNameSpans").Content = "ערוך שמות רצועות בחתך…";
            foreach (var name in new[] { "BtnApplySelected", "BtnApply", "BtnVerify", "BtnResolveSection", "BtnApproveRow" })
                Find<Button>(name).IsEnabled = false;
            Find<Expander>("SectionAdvanced").IsExpanded = expanded;
            Find<TextBlock>("GateReason").Text = "שלושה חתכים אחרים דורשים בדיקת מקור. החתך הנבחר אינו מושפע.\nSEC-PROJECTION-GEOMETRY-UNSUPPORTED — פירוט טכני לבדיקה.";
            Find<DataGrid>("SectionsGrid").ItemsSource = Enumerable.Range(0, 28).Select(i => new
            {
                SectionId = i == 0 ? "STA-42676" : "STA-" + (12145 + i * 60),
                Status = i == 0 ? "אומת" : "דרושה בדיקה",
                NextStep = i == 0 ? "אומת — אפשר להציג" : "שמות רצועות",
                Alignment = "2000", Station = "42675.85", Utilities = "מים, חשמל, בזק", Severity = i == 0 ? "ok" : "review"
            }).ToArray();
            Layout(control, width, height);
            SaveRender(control, $"section-{width}x{height}-{(expanded ? "expanded" : "compact")}-{(blocked ? "blocked" : "verified")}.png", width, height);
            Assert.True(Find<DataGrid>("SectionsGrid").ActualHeight >= 150,
                "The action groups must not consume the section table.");
            foreach (var name in new[] { "BtnSectionNext", "BtnShow", "BtnNameAllSpans" })
            {
                var button = Find<Button>(name);
                AssertWithin(control, button, width, height);
                Assert.True(button.ActualHeight >= 34, name);
                AssertLabelFits(button);
            }
            if (expanded)
            {
                var scroll = Descendants(Find<Expander>("SectionAdvanced")).OfType<ScrollViewer>().First();
                Assert.True(scroll.ViewportHeight > 0 && scroll.ViewportHeight <= 180);
                foreach (var name in new[] { "BtnPickCl", "BtnNameSpans", "BtnVerifySelected", "BtnClearPreview" })
                {
                    var button = Find<Button>(name);
                    button.BringIntoView();
                    control.UpdateLayout();
                    AssertLabelFits(button);
                    var visible = button.TransformToAncestor(scroll).TransformBounds(new Rect(new Point(), button.RenderSize));
                    Assert.True(visible.Top >= -1 && visible.Bottom <= scroll.ActualHeight + 1, name + " must be reachable by scrolling");
                }
            }
            var next = Find<Button>("BtnSectionNext");
            var border = (System.Windows.Controls.Border)next.Template.FindName("b", next);
            var label = Descendants(next).OfType<TextBlock>().Single();
            Assert.Equal(next.Foreground.ToString(), label.Foreground.ToString());
            Assert.Equal(1, border.Opacity);
            Assert.True(Contrast(((SolidColorBrush)label.Foreground).Color, ((SolidColorBrush)border.Background).Color) >= 4.5,
                "Primary and disabled labels must remain legible.");
        });
    }

    internal static UserControl LoadActualMarkup()
    {
        var xml = XDocument.Load(Path.Combine(TestPaths.PluginSourceDir,
            "CivilDelivery", "UI", "CivilDeliveryControl.xaml"));
        // Exact production XAML, without an HWND, event handlers or Autodesk host.
        foreach (var a in xml.Root!.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName == "Class" || a.Value.StartsWith("On", StringComparison.Ordinal)).ToArray()) a.Remove();
        return (UserControl)XamlReader.Parse(xml.ToString());
    }

    [Theory]
    [InlineData(2, "project")]
    [InlineData(3, "support")]
    public void SharedButtonStyleKeepsOtherTabsUsable(int tab, string label)
    {
        OnSta(() =>
        {
            var control = LoadActualMarkup();
            ((System.Windows.Controls.TabControl)control.FindName("Tabs")).SelectedIndex = tab;
            ((TextBlock)control.FindName("ProfileSummary")).Text = "פרופיל דוגמה לבדיקה חזותית בלבד. מקורות, תוואים והחלטות נשמרים לפי השרטוט.";
            ((TextBlock)control.FindName("CatalogSummary")).Text = "מחירון לדוגמה — בחירת סעיף אינה אישור מדידה או מחיר.";
            ((TextBlock)control.FindName("ActivityLog")).Text = "בדיקת תצוגה ללא הפעלת Civil.\nלא שונו שרטוטים או אישורים.";
            Layout(control, 480, 880);
            SaveRender(control, label + "-480x880.png", 480, 880);
            var names = tab == 2
                ? new[] { "BtnReloadProfile", "BtnOpenProfile", "BtnPickClProfile", "BtnRunSetup" }
                : new[] { "BtnOpenRuns", "BtnOpenLogs", "BtnExportSupport", "BtnCheckUpdates" };
            foreach (var name in names)
            {
                var button = (Button)control.FindName(name);
                AssertWithin(control, button, 480, 880); AssertLabelFits(button);
            }
            // A real long action must wrap; it must not silently crop to one line.
            var longAction = (Button)control.FindName(names[0]);
            longAction.Content = "בנה מחדש את החתך הנבחר לאחר השלמת הנתונים הנדרשים";
            Layout(control, 480, 880);
            AssertLabelFits(longAction);
            Assert.True(longAction.ActualHeight > 34);
        });
    }

    private static void Layout(UserControl control, int width, int height)
    {
        control.Width = width; control.Height = height;
        control.Measure(new Size(width, height)); control.Arrange(new Rect(0, 0, width, height)); control.UpdateLayout();
    }

    private static void AssertWithin(UserControl control, FrameworkElement element, int width, int height)
    {
        var bounds = element.TransformToAncestor(control).TransformBounds(new Rect(new Point(), element.RenderSize));
        Assert.True(bounds.Left >= -0.5 && bounds.Top >= 0 && bounds.Right <= width + 0.5 && bounds.Bottom <= height + 0.5,
            element.Name + ": " + bounds);
    }

    private static void AssertLabelFits(Button button)
    {
        var label = Descendants(button).OfType<TextBlock>().Single();
        Assert.True(label.ActualWidth > 0 && label.DesiredSize.Width <= label.ActualWidth + 0.5, button.Name + " label width");
        Assert.True(label.DesiredSize.Height <= label.ActualHeight + 0.5, button.Name + " label height");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte v) { var s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var x = Luminance(a); var y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static void SaveRender(UserControl control, string name, int width, int height)
    {
        var output = Environment.GetEnvironmentVariable("MHD_PALETTE_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(control);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(Path.Combine(output, name), FileMode.Create); encoder.Save(stream);
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure != null) throw new InvalidOperationException("Offline section palette layout failed", failure);
    }
}
