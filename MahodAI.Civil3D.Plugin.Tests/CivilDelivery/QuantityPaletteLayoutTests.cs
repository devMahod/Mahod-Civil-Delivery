using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;
using UserControl = System.Windows.Controls.UserControl;
using TabControl = System.Windows.Controls.TabControl;
using TextBox = System.Windows.Controls.TextBox;
using DataGrid = System.Windows.Controls.DataGrid;
using Size = System.Windows.Size;
using Point = System.Windows.Point;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class QuantityPaletteLayoutTests
{
    [Theory]
    [InlineData(540, 880)]
    [InlineData(720, 980)]
    public void ActualPaletteMarkupKeepsSearchGridAndEditingActionsVisible(int width, int height) => Run(width, height, collapsed: false);

    /// <summary>Codex 00:45: in a normal palette the tools collapse so the quantity list becomes a working list (about
    /// ten rows), while the scan, search and row actions stay visible; expanding brings everything back (test above).</summary>
    [Theory]
    [InlineData(540, 880)]
    [InlineData(720, 980)]
    public void CollapsingTheToolsGivesTheQuantityListRoom(int width, int height) => Run(width, height, collapsed: true);

    /// <summary>b22 (Codex 06:58, b21 live): opening "פירוט מקורות ובדיקות" (EstimateAdvancedActions) left the quantity list a
    /// few pixels high in a normal palette. With the section open the list keeps a working height (the palette content grows
    /// to at least 1130 px and scrolls), the search text and the selected row are kept, and "הגדל רשימה" still works.
    /// Normal, tall and narrow palettes.</summary>
    [Theory]
    [InlineData(540, 880)]
    [InlineData(720, 980)]
    [InlineData(720, 1200)]
    [InlineData(460, 880)]
    public void OpeningTheSourcesSectionKeepsTheQuantityList(int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var xml = XDocument.Load(Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryControl.xaml"));
                foreach (var attribute in xml.Root!.DescendantsAndSelf().Attributes().Where(a =>
                             a.Name.LocalName == "Class" || a.Value.StartsWith("On", StringComparison.Ordinal)).ToArray())
                    attribute.Remove();
                var control = (UserControl)XamlReader.Parse(xml.ToString());
                T Find<T>(string name) where T : class => (T)control.FindName(name);
                Find<TabControl>("Tabs").SelectedIndex = 1;
                Find<TextBlock>("EstimateFlowProgress").Text = "1 מדידה ← 2 בדיקת כמויות ← 3 היקף אומדן ← 4 תמחור ← 5 Excel";
                Find<TextBlock>("EstimateNextAction").Text = "בדיקת תצוגה בלבד: חפש קבוצה, בדוק את מקורה ובחר או שנה סעיף מחירון. לא נשמר אישור.";
                Find<TextBlock>("EstimateReviewOverview").Visibility = Visibility.Visible;
                Find<TextBlock>("EstimateReviewOverview").Text = "144 קבוצות מדידה · 0 עם שיוך שמור · 1 עם הצעות לבדיקה · 2 קבוצות עזר לבדיקה יחד\n95 ממצאי מקור/מדידה ב־9 סוגי בדיקה — לא מספר שיוכי המחירון.";
                Find<TextBox>("QuantitySearchBox").Text = "LA-TREE";
                var grid = Find<DataGrid>("QuantitiesGrid");
                grid.ItemsSource = new[] { new QuantityRowViewModel
                {
                    RuleKey = "VISUAL-FIXTURE", Layer = "LA-TREE-RPL", EntityType = "BLOCKREFERENCE", Quantity = 219,
                    Unit = "יח'", Method = "count", MappingState = "בדיקת מדידה", ObjectCount = 219,
                } };
                grid.SelectedIndex = 0;
                void Layout()
                {
                    for (var pass = 0; pass < 2; pass++) // the viewport-bound content height settles on the second pass
                    {
                        control.Measure(new Size(width, height)); control.Arrange(new Rect(0, 0, width, height)); control.UpdateLayout();
                    }
                }
                control.Width = width; control.Height = height;
                Layout();
                var closed = grid.ActualHeight;
                Assert.True(closed >= 60, "list with the section closed: " + closed);

                var sources = Find<Expander>("EstimateAdvancedActions");
                sources.IsExpanded = true;
                Layout();
                var opened = grid.ActualHeight;
                Assert.True(opened >= 180, $"with the sources section open the list is {opened} px (closed: {closed})");
                var earthworks = Find<FrameworkElement>("BtnEarthworksDecision");
                Assert.True(earthworks.ActualHeight > 0, "the section's first action must be laid out");
                Assert.Equal("LA-TREE", Find<TextBox>("QuantitySearchBox").Text);
                Assert.Equal(0, grid.SelectedIndex);

                var enlarge = Find<System.Windows.Controls.Primitives.ToggleButton>("BtnExpandQuantityList");
                enlarge.IsChecked = true;
                Layout();
                Assert.True(grid.ActualHeight >= opened, "enlarging the list with the section open");
                enlarge.IsChecked = false;
                Layout();
                Assert.Equal(opened, grid.ActualHeight, 1);
                sources.IsExpanded = false;
                Layout();
                Assert.Equal(closed, grid.ActualHeight, 1);
                Assert.Equal(0, grid.SelectedIndex);
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure != null) throw new InvalidOperationException("Offline palette layout (sources section) failed", failure);
    }

    private static void Run(int width, int height, bool collapsed)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var xml = XDocument.Load(Path.Combine(TestPaths.PluginSourceDir,
                    "CivilDelivery", "UI", "CivilDeliveryControl.xaml"));
                // Layout-only replay of the exact product markup, with no Autodesk host,
                // event handlers, native window or claim of real-host interaction coverage.
                foreach (var attribute in xml.Root!.DescendantsAndSelf().Attributes().Where(a =>
                             a.Name.LocalName == "Class" || a.Value.StartsWith("On", StringComparison.Ordinal)).ToArray())
                    attribute.Remove();
                var control = (UserControl)XamlReader.Parse(xml.ToString());
                T Find<T>(string name) where T : class => (T)control.FindName(name);
                Find<TabControl>("Tabs").SelectedIndex = 1;
                var priceBooks = Find<System.Windows.Controls.ComboBox>("PriceBookCombo");
                priceBooks.ItemsSource = new[] { new { Label = "מחירון תצוגה בלבד · 2026 · 12,000 סעיפים · SYNTHETIC-ONLY · גיליון דוגמה · מחיר=E" } };
                priceBooks.SelectedIndex = 0;
                Find<TextBlock>("EstimateFlowProgress").Text = "1 מדידה ← 2 בדיקת כמויות ← 3 היקף אומדן ← 4 תמחור ← 5 Excel";
                Find<TextBlock>("EstimateNextAction").Text = "בדיקת תצוגה בלבד: חפש קבוצה, בדוק את מקורה ובחר או שנה סעיף מחירון. לא נשמר אישור.";
                Find<TextBox>("QuantitySearchBox").Text = "HW-CURB";
                Find<TextBlock>("QuantityFilterCount").Text = "מוצגת 1 מתוך 1,041 קבוצות · סינון תצוגה בלבד; אינו משנה את האומדן או הייצוא";
                Find<TextBlock>("EstimateReviewOverview").Visibility = Visibility.Visible;
                Find<TextBlock>("EstimateReviewOverview").Text = "1,031 קבוצות מדידה · 0 משויכות · 341 עם הצעות לבדיקה · 170 קבוצות עזר לבדיקה יחד\n455 ממצאי מקור/מדידה ב־9 סוגי בדיקה — לא מספר שיוכי המחירון.";
                Find<System.Windows.Controls.Button>("BtnReviewMappings").Content = "שיוך קבוצות יחד…";
                Find<System.Windows.Controls.Button>("BtnFilterDrawingNoise").Content = "בדוק סימוני עזר יחד (170)…";
                Find<TextBlock>("EstimateRowTitle").Text = "HW-CURB · אורך · 29,287.63 מטר — דוגמת תצוגה בלבד";
                Find<TextBlock>("EstimateRowActionHint").Text = "בחר או שנה סעיף מחירון; אם אין מחיר, ניתן להזין מחיר פרויקט מנומק. השיוך אינו אישור למדידה.";
                Find<TextBlock>("PricedDraftSummary").Visibility = Visibility.Visible;
                Find<TextBlock>("PricedDraftSummary").Text = "טיוטה חלקית לבדיקה: 625.00 ₪ · שתי שורות בלבד. 1,039 קבוצות נוספות וחסרי כיסוי נשארים פתוחים.";
                Find<System.Windows.Controls.Button>("BtnExportPricedDraft").IsEnabled = true;
                Find<DataGrid>("QuantitiesGrid").ItemsSource = new[] { new QuantityRowViewModel
                {
                    RuleKey = "VISUAL-FIXTURE", Layer = "HW-CURB", EntityType = "LWPOLYLINE", Quantity = 29287.62954513264,
                    Unit = "מטר", Method = "polyline-length", MappingState = "לבדיקה", ObjectCount = 297,
                    ProposedCode = "U51.06.1900", SourceCategory = "כבישים",
                }, new QuantityRowViewModel
                {
                    RuleKey = "VISUAL-FIXTURE-APPROVED", Layer = "VISUAL-ONLY-LANDSCAPE", EntityType = "LWPOLYLINE",
                    Unit = "מטר", Quantity = 1, Method = "polyline-length", MappingState = "דוגמת תצוגה בלבד", ObjectCount = 1,
                    SourceCategory = "אדריכלות נוף", CatalogCode = "VISUAL-ONLY",
                    CatalogDescription = "תיאור עברי ארוך לדוגמת תצוגה בלבד — אינו סעיף מחירון או אישור הנדסי",
                } };
                var proposedRow = (QuantityRowViewModel)Find<DataGrid>("QuantitiesGrid").Items[0];
                const string visualDescription = "אבן שפה — תיאור חזותי לבדיקה בלבד, ארוך במכוון כדי לבדוק שלוש נקודות וריחוף מלא ללא אישור";
                proposedRow.ProposalSearchText = proposedRow.ProposedCode + " · " + visualDescription;
                proposedRow.RefreshProposalDescription(new MahodAI.CivilDelivery.Estimate.CatalogSnapshot
                {
                    SnapshotId = "VISUAL-ONLY", FileHash = new string('a', 64),
                    Items = new()
                    {
                        ["U51.06.1900"] = new() { Code = "U51.06.1900", Description = visualDescription, UnitRaw = "מטר" },
                    },
                });
                if (collapsed) Find<System.Windows.Controls.Primitives.ToggleButton>("BtnExpandQuantityList").IsChecked = true;
                control.Width = width; control.Height = height;
                control.Measure(new Size(width, height)); control.Arrange(new Rect(0, 0, width, height)); control.UpdateLayout();
                Assert.True(double.IsNaN(Find<DataGrid>("QuantitiesGrid").RowHeight),
                    "Quantity rows must grow for the approved Hebrew description, not inherit a clipped 30 px height.");
                if (collapsed)
                {
                    // One instance, both directions (Codex 01:38): collapsed → expanded → collapsed. The search text and the
                    // selected row survive every switch, and the reachable controls stay inside the palette on both axes.
                    var toggle = Find<System.Windows.Controls.Primitives.ToggleButton>("BtnExpandQuantityList");
                    var grid = Find<DataGrid>("QuantitiesGrid");
                    var tools = Find<FrameworkElement>("EstimateToolsPanel");
                    grid.SelectedIndex = 0;
                    void Layout()
                    {
                        control.Measure(new Size(width, height)); control.Arrange(new Rect(0, 0, width, height)); control.UpdateLayout();
                    }
                    void Reachable(string state)
                    {
                        foreach (var name in new[] { "BtnStartEstimateGuided", "BtnExpandQuantityList", "QuantitySearchBox", "QuantityFilterBox", "BtnApprove", "BtnProjectPrice", "BtnShowQuantity", "BtnRelevance" })
                        {
                            var item = Find<FrameworkElement>(name);
                            var bounds = item.TransformToAncestor(control).TransformBounds(new Rect(new Point(0, 0), item.RenderSize));
                            Assert.True(bounds.Height > 0 && bounds.Width > 0 && bounds.Top >= 0 && bounds.Bottom <= height + 0.5 &&
                                        bounds.Left >= -0.5 && bounds.Right <= width + 0.5, $"{state} {name}: {bounds}");
                        }
                        Assert.Equal("HW-CURB", Find<TextBox>("QuantitySearchBox").Text);
                        Assert.Equal(0, grid.SelectedIndex);
                    }
                    Layout();
                    Assert.True(grid.ActualHeight >= 250, "Collapsed tools leave the quantity list only " + grid.ActualHeight + " px");
                    Assert.Equal(Visibility.Collapsed, tools.Visibility);
                    Reachable("collapsed");
                    var collapsedListHeight = grid.ActualHeight;

                    toggle.IsChecked = false;
                    Layout();
                    Assert.Equal(Visibility.Visible, tools.Visibility);
                    Assert.True(grid.ActualHeight < collapsedListHeight, "expanding the tools gives their room back");
                    Reachable("expanded");

                    toggle.IsChecked = true;
                    Layout();
                    Assert.Equal(Visibility.Collapsed, tools.Visibility);
                    Assert.Equal(collapsedListHeight, grid.ActualHeight, 3);
                    Reachable("collapsed again");
                    return;
                }
                var output = Environment.GetEnvironmentVariable("MHD_PALETTE_RENDER_DIR");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(control);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = new FileStream(Path.Combine(output, $"quantity-palette-{width}x{height}.png"), FileMode.Create);
                    encoder.Save(stream);
                    File.WriteAllText(Path.Combine(output, $"quantity-palette-{width}x{height}.layout.txt"),
                        "QuantitiesGrid height=" + Find<DataGrid>("QuantitiesGrid").ActualHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    static T? Child<T>(DependencyObject item) where T : DependencyObject
                    {
                        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
                        {
                            var child = VisualTreeHelper.GetChild(item, i);
                            if (child is T match) return match;
                            if (Child<T>(child) is { } nested) return nested;
                        }
                        return null;
                    }
                    var tableScroll = Child<ScrollViewer>(Find<DataGrid>("QuantitiesGrid"))!;
                    tableScroll.ScrollToHorizontalOffset(tableScroll.ScrollableWidth);
                    control.UpdateLayout();
                    var detailBitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    detailBitmap.Render(control);
                    var detailEncoder = new PngBitmapEncoder(); detailEncoder.Frames.Add(BitmapFrame.Create(detailBitmap));
                    using var detailStream = new FileStream(Path.Combine(output, $"quantity-palette-{width}x{height}-scrolled.png"), FileMode.Create);
                    detailEncoder.Save(detailStream);
                }
                Assert.True(Find<DataGrid>("QuantitiesGrid").ActualHeight >= 110,
                    "QuantitiesGrid height: " + Find<DataGrid>("QuantitiesGrid").ActualHeight);
                foreach (var name in new[] { "BtnLoadPriceBook", "BtnKnownPriceBooks", "PriceBookCombo", "BtnStartEstimateGuided", "EstimateFlowProgress", "EstimateNextAction", "QuantitySearchBox", "QuantityFilterBox", "QuantityFilterCount", "BtnApprove", "BtnProjectPrice", "BtnQuantityAdjustment", "BtnShowQuantity", "BtnRelevance", "BtnBuild", "BtnExportPricedDraft", "PricedDraftSummary", "EstimateReviewOverview", "BtnFilterDrawingNoise", "BtnApproveEstimateScope" })
                {
                    var item = Find<FrameworkElement>(name);
                    var bounds = item.TransformToAncestor(control).TransformBounds(new Rect(new Point(0, 0), item.RenderSize));
                    // IsVisible is false without a presentation source; no HWND is created.
                    Assert.True(item.Visibility == Visibility.Visible && bounds.Height > 0, name);
                    Assert.True(bounds.Left >= -0.5 && bounds.Top >= 0 && bounds.Right <= width + 0.5 && bounds.Bottom <= height + 0.5,
                        name + ": " + bounds);
                }
                var scanButton = Find<FrameworkElement>("BtnStartEstimateGuided");
                var scanBounds = scanButton.TransformToAncestor(control).TransformBounds(new Rect(new Point(0, 0), scanButton.RenderSize));
                foreach (var name in new[] { "EstimateFlowProgress", "EstimateNextAction" })
                {
                    var text = Find<FrameworkElement>(name);
                    var textBounds = text.TransformToAncestor(control).TransformBounds(new Rect(new Point(0, 0), text.RenderSize));
                    Assert.False(scanBounds.IntersectsWith(textBounds), name + " overlaps guided scan: " + textBounds + " / " + scanBounds);
                }
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure != null) throw new InvalidOperationException("Offline palette layout failed", failure);
    }
}
