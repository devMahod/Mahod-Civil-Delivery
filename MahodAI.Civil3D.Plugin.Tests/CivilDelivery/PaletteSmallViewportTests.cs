using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using FlowDirection = System.Windows.FlowDirection;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using DataGrid = System.Windows.Controls.DataGrid;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using TabControl = System.Windows.Controls.TabControl;
using TextBlock = System.Windows.Controls.TextBlock;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using UserControl = System.Windows.Controls.UserControl;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

// Executes real production WPF markup, without palette constructor, handlers,
// Autodesk assemblies in use, HWND, profile mutation or a native drawing.
public sealed class PaletteSmallViewportTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Docked228PixelViewportCanReachEveryTabAndActionAndResizeBackWithoutOverflow(int tab)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { ReviewDockedViewport(tab); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Offline docked WPF rendering timed out.");
        if (failure != null) throw new InvalidOperationException("Docked viewport layout failed.", failure);
    }

    private static void ReviewDockedViewport(int tab)
    {
        var control = LoadActualMarkup();
        T Find<T>(string name) where T : class => (T)control.FindName(name);
        Find<TextBlock>("ProjectLine").Text = "TEST-ONLY · פרופיל בדיקה סינתטי";
        Find<TextBlock>("ProfileSummary").Text = "TEST-ONLY — בדיקת תצוגה בלבד\nמקורות ויחידות טעונים בדיקת מהנדס; אין אישור הנדסי.";
        Find<TextBlock>("CatalogSummary").Text = "מחירון מקומי לדוגמה; בחירה אינה אישור לשיוך או למחיר.";
        Find<TabControl>("Tabs").SelectedIndex = tab;
        var grids = new[] { Find<DataGrid>("SectionsGrid"), Find<DataGrid>("QuantitiesGrid") };
        foreach (var grid in grids)
            grid.ItemsSource = Enumerable.Range(0, 20).Select(i => new
            {
                SectionId = "STA-" + (100 + i), Status = "לבדיקה", NextStep = "הגדרת מקורות",
                Alignment = "TEST-ROAD", Station = "100.00", Utilities = "מים", Severity = "review",
                Source = "TEST-LAYER", MethodDisplay = "אורך", QuantityDisplay = "30.00 מטר",
                CatalogCodeDisplay = "טרם נבחר", StatusDisplay = "בדיקת מדידה", PriceDisplay = "—",
            }).ToArray();
        void Layout(int width)
        {
            control.Width = width; control.Height = 600;
            control.Measure(new Size(width, 600)); control.Arrange(new Rect(0, 0, width, 600)); Pump(control);
        }
        Layout(228);
        var viewport = Find<ScrollViewer>("PaletteViewport");
        var panel = (System.Windows.Controls.DockPanel)viewport.Content;
        Assert.True(double.IsFinite(viewport.ViewportWidth) && viewport.ViewportWidth > 0);
        Assert.Equal(440d, panel.ActualWidth, 1);
        Assert.True(viewport.ScrollableWidth > 200, "A 228 px dock needs real horizontal navigation to its 440 px content.");
        Assert.True(viewport.ScrollableHeight > 0, "Vertical navigation must remain available.");
        var targets = tab switch
        {
            0 => new[] { "BtnSectionNext", "BtnShow", "BtnNameAllSpans" },
            1 => new[] { "BtnStartEstimateGuided", "BtnLoadPriceBook", "BtnReviewMappings", "BtnBuild", "BtnExportPricedDraft", "BtnExportMeasurementDraft", "BtnApprove", "BtnProjectPrice", "BtnQuantityAdjustment", "BtnShowQuantity", "BtnRelevance" },
            _ => new[] { "BtnReloadProfile", "BtnSelectProjectProfile", "BtnOpenProfile", "BtnPickClProfile", "BtnRunSetup" },
        };
        foreach (TabItem item in Find<TabControl>("Tabs").Items)
        {
            item.BringIntoView(); Pump(control);
            AssertInsideContentViewport(viewport, item);
        }
        foreach (var name in targets)
        {
            var button = Find<Button>(name);
            button.BringIntoView(); Pump(control);
            AssertInsideContentViewport(viewport, button);
            Assert.True(button.ActualHeight >= 34, name + " target height");
            var label = Descendants(button).OfType<TextBlock>().Single();
            Assert.True(label.ActualWidth > 0 && label.DesiredSize.Width <= label.ActualWidth + .5, name + " clipped label width");
            Assert.True(label.DesiredSize.Height <= label.ActualHeight + .5, name + " clipped label height");
        }
        foreach (var bottom in new[] { false, true })
        {
            if (bottom) viewport.ScrollToBottom(); else viewport.ScrollToTop();
            foreach (var end in new[] { false, true })
            {
                viewport.ScrollToHorizontalOffset(end ? viewport.ScrollableWidth : 0); Pump(control);
                Assert.Equal(end ? viewport.ScrollableWidth : 0, viewport.HorizontalOffset, 1);
                Save(control, 228, 600, tab, $"dock-{(bottom ? "bottom" : "top")}-{(end ? "end" : "start")}");
            }
        }
        // Resizing the SAME live visual tree must undo outer horizontal overflow.
        Layout(600);
        Assert.Equal(0d, viewport.ScrollableWidth, 1);
        Assert.Equal(viewport.ViewportWidth, panel.ActualWidth, 1);
        Assert.True(viewport.ScrollableHeight > 0);
        if (tab < 2)
        {
            var grid = grids[tab];
            Assert.True(grid.ActualHeight >= 120 && grid.ActualHeight < 900, "Resized table must retain a usable finite height.");
            Assert.True(double.IsFinite(grid.ActualWidth) && grid.ActualWidth > 200 && grid.ActualWidth < 600);
            Assert.All(grid.Columns, column => Assert.True(double.IsFinite(column.ActualWidth) && column.ActualWidth >= column.MinWidth));
        }
        foreach (var name in targets)
        {
            var button = Find<Button>(name); button.BringIntoView(); Pump(control);
            AssertInsideContentViewport(viewport, button);
        }
        viewport.ScrollToTop(); Pump(control);
        Save(control, 600, 600, tab, "resized-from-dock");
    }

    private static void AssertInsideContentViewport(ScrollViewer viewport, FrameworkElement element)
    {
        var presenter = Assert.IsType<ScrollContentPresenter>(viewport.Template.FindName("PART_ScrollContentPresenter", viewport));
        var bounds = element.TransformToAncestor(presenter).TransformBounds(new Rect(new Point(), element.RenderSize));
        Assert.True(bounds.Left >= -.5 && bounds.Right <= presenter.ActualWidth + .5 &&
            bounds.Top >= -.5 && bounds.Bottom <= presenter.ActualHeight + .5, element.Name + ": " + bounds);
    }

    [Theory]
    [InlineData(480, 600, 0)]
    [InlineData(480, 600, 1)]
    [InlineData(600, 760, 0)]
    [InlineData(600, 760, 1)]
    public void ActualMarkupHasFiniteUsableTablesAndReachableTopAndBottomActions(int width, int height, int tab)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Review(width, height, tab); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Offline WPF rendering timed out.");
        if (failure != null) throw new InvalidOperationException("Small-viewport layout failed.", failure);
    }

    // b25 (Codex 15:12, b24 live smoke): at the docked 1080p palette (888 px) the scrolling minimum hid half of the
    // session-approver line. The status strip is a fixed bottom row now: fully visible at any height and scroll offset,
    // and the tab content keeps the height it had (at 888 px no more of it is hidden than before: 900 - 888 = 12 px).
    [Theory]
    [InlineData(480, 600)]
    [InlineData(600, 760)]
    [InlineData(720, 888)]
    public void TheStatusStripAndTheSessionApproverStayFullyVisibleAtAnyPaletteHeight(int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { ReviewStatusStrip(width, height); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Offline WPF rendering timed out.");
        if (failure != null) throw new InvalidOperationException("Status strip layout failed.", failure);
    }

    // Codex 16:10: a long approver name at the narrowest docked widths stays inside the fixed strip (it wraps).
    [Theory]
    [InlineData(228, 600)]
    [InlineData(480, 600)]
    public void ALongApproverNameWrapsInsideTheStripAtNarrowWidths(int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { ReviewStatusStrip(width, height, "מאשר נוכחי: ישראלה ישראלי-כהן, מהנדסת אומדנים בכירה — מחלקת תשתיות ותנועה"); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Offline WPF rendering timed out.");
        if (failure != null) throw new InvalidOperationException("Status strip layout failed.", failure);
    }

    private static void ReviewStatusStrip(int width, int height, string? approverText = null)
    {
        var control = LoadActualMarkup();
        T Find<T>(string name) where T : class => (T)control.FindName(name);
        Find<TabControl>("Tabs").SelectedIndex = 1;
        Find<TextBlock>("StatusLabel").Text = "נסרקו 857 רשומות; המחירון דורש טיפול";
        if (approverText != null) Find<System.Windows.Documents.Run>("ApproverRun").Text = approverText;
        control.Width = width; control.Height = height;
        control.Measure(new Size(width, height)); control.Arrange(new Rect(0, 0, width, height)); Pump(control);
        var viewport = Find<ScrollViewer>("PaletteViewport");
        var strip = Find<System.Windows.Controls.Border>("StatusStrip");
        Assert.DoesNotContain(strip, Descendants(viewport));
        var approver = (TextBlock)Find<System.Windows.Documents.Run>("ApproverRun").Parent;
        foreach (var offset in new[] { 0d, viewport.ScrollableHeight })
        {
            viewport.ScrollToVerticalOffset(offset); Pump(control);
            foreach (var element in new FrameworkElement[] { strip, approver, Find<TextBlock>("StatusLabel") })
            {
                var bounds = element.TransformToAncestor(control).TransformBounds(new Rect(new Point(), element.RenderSize));
                Assert.True(bounds.Left >= -.5 && bounds.Right <= width + .5 && bounds.Top >= -.5 && bounds.Bottom <= height + .5,
                    $"{element.Name} at offset {offset}: {bounds} (strip {strip.ActualHeight:0.0} px)");
                Assert.True(element.DesiredSize.Height <= element.ActualHeight + .5, $"{element.Name} is cut vertically");
            }
        }
        if (height == 888)
            Assert.True(viewport.ScrollableHeight <= 12.5,
                $"The tab content gained hidden height: {viewport.ScrollableHeight:0.0} px (strip {strip.ActualHeight:0.0} px).");
    }

    private static void Review(int width, int height, int tab)
    {
        var control = LoadActualMarkup();
        T Find<T>(string name) where T : class => (T)control.FindName(name);
        void Text(string name, string text) => Find<TextBlock>(name).Text = text;
        Find<TabControl>("Tabs").SelectedIndex = tab;
        Text("ProjectLine", "בדיקת פריסה לא מקוונת — נתונים סינתטיים");
        Text("DashboardBody", "שלושה תוואים · שני משטחים\nקווי חתך טרם הוגדרו");
        var firstUse = SectionGuidedActionPolicy.Evaluate(new SectionGuidedActionSnapshot
        { HasDrawing = true, ProfileUsable = true, NeedsSectionSetup = true });
        Text("SectionStepTitle", firstUse.Title);
        Text("SectionStepDetail", firstUse.Detail);
        Find<Button>("BtnSectionNext").Content = firstUse.ButtonText;
        Text("EstimateFlowProgress", "1 מדידה ← 2 בדיקת כמויות ← 3 היקף אומדן ← 4 תמחור ← 5 Excel");
        Text("EstimateNextAction", "חפש קבוצה, בדוק את מקורה ובחר או שנה סעיף מחירון. הצעה אינה אישור.");
        // b24: the longest form of the fixed units line next to the scan.
        Text("EstimateUnitsLine", "יחידות: ללא יחידה (Unitless) (0) — לא הוכרעו, נדרשת בדיקה; ללא תמחור");
        Text("EstimateRowTitle", "שכבה חדשה · אורך · 30.00 מטר — דוגמת תצוגה בלבד");
        Text("EstimateRowActionHint", "בחר או שנה סעיף מחירון; אם אין מחיר, ניתן להזין מחיר פרויקט מנומק. השיוך אינו אישור למדידה.");
        Text("QuantityFilterCount", "מוצגת קבוצה אחת — סינון תצוגה בלבד; אינו משנה את האומדן או הייצוא");
        var grid = Find<DataGrid>(tab == 0 ? "SectionsGrid" : "QuantitiesGrid");
        grid.ItemsSource = Enumerable.Range(0, 20).Select(i => new
        {
            SectionId = "STA-" + (100 + i), Status = "לבדיקה", NextStep = "הגדרת מקורות",
            Alignment = "ROAD-A", Station = "100.00", Utilities = "מים", Severity = "review",
            Source = "Road / ROAD — דוגמה", MethodDisplay = "אורך", QuantityDisplay = "30.00 מטר",
            CatalogCodeDisplay = "טרם נבחר", StatusDisplay = "בדיקת מדידה", PriceDisplay = "—",
        }).ToArray();
        control.Width = width; control.Height = height;
        control.Measure(new Size(width, height)); control.Arrange(new Rect(0, 0, width, height)); Pump(control);
        var viewport = Find<ScrollViewer>("PaletteViewport");
        Assert.True(double.IsFinite(viewport.ViewportHeight) && viewport.ViewportHeight > 0);
        Assert.True(viewport.ScrollableHeight > 0, "Short viewports require real outer scrolling.");
        Assert.True(grid.ActualHeight >= 120, $"Table collapsed to {grid.ActualHeight} px.");

        Assert.True(double.IsFinite(grid.ActualHeight) && grid.ActualHeight < 900, "Table must retain finite virtualization height.");
        Assert.True(viewport.ScrollableWidth == 0, "The palette must not acquire whole-panel horizontal scrolling.");
        if (tab == 1)
        {
            var widths = new[] { 150d, 105, 62, 104, 160, 118, 66 }; // source category + code/description retain readable minima
            for (var i = 0; i < widths.Length; i++)
            {
                Assert.True(grid.Columns[i].MinWidth >= widths[i], $"Quantity column {i} has no readable minimum.");
                Assert.True(grid.Columns[i].ActualWidth >= widths[i] - .5, $"Quantity column {i} compressed to {grid.Columns[i].ActualWidth} px.");
            }
        }
        viewport.ScrollToTop(); Pump(control);
        Save(control, width, height, tab, "top");
        var topAction = Find<Button>(tab == 0 ? "BtnSectionNext" : "BtnStartEstimateGuided");
        AssertVisible(viewport, topAction);
        var targets = tab == 0
            ? new[] { "BtnSectionNext", "BtnShow", "BtnNameAllSpans" }
            : new[] { "BtnReviewMappings", "BtnBuild", "BtnExportMeasurementDraft", "BtnApprove", "BtnProjectPrice", "BtnQuantityAdjustment", "BtnShowQuantity", "BtnRelevance" };
        foreach (var name in targets)
        {
            var button = Find<Button>(name);
            button.BringIntoView(); Pump(control);
            AssertVisible(viewport, button);
            Assert.True(button.ActualHeight >= 34, name + " target height");
            var label = Descendants(button).OfType<TextBlock>().Single();
            Assert.True(label.ActualWidth > 0 && label.DesiredSize.Width <= label.ActualWidth + .5, name + " clipped label width");
            Assert.True(label.DesiredSize.Height <= label.ActualHeight + .5, name + " clipped label height");
        }
        viewport.ScrollToBottom(); Pump(control);
        if (tab == 1)
        {
            // A narrow palette intentionally scrolls its table horizontally;
            // it must not squeeze 'מדידה' to 'מד' or 'אורך' to 'א…'.
            foreach (var index in new[] { 2, grid.Columns.Count - 1 })
            {
                var column = grid.Columns[index];
                grid.ScrollIntoView(grid.Items[0], column); Pump(control);
                var header = Descendants(grid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>()
                    .Single(h => ReferenceEquals(h.Column, column));
                var headerText = new FormattedText((string)header.Content,
                    System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.RightToLeft,
                    new Typeface(header.FontFamily, header.FontStyle, header.FontWeight, header.FontStretch),
                    header.FontSize, Brushes.White, 1);
                Assert.True(header.ActualWidth >= headerText.WidthIncludingTrailingWhitespace + 6, "Clipped quantity header: " + header.Content);
                var cell = (TextBlock)column.GetCellContent(grid.Items[0]);
                Assert.Equal(index == 2 ? "אורך" : "—", cell.Text);
                var valueText = new FormattedText(cell.Text,
                    System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.RightToLeft,
                    new Typeface(cell.FontFamily, cell.FontStyle, cell.FontWeight, cell.FontStretch),
                    cell.FontSize, Brushes.White, 1);
                Assert.True(cell.ActualWidth >= valueText.WidthIncludingTrailingWhitespace, "Clipped quantity value: " + cell.Text);
                var bounds = cell.TransformToAncestor(grid).TransformBounds(new Rect(new Point(), cell.RenderSize));
                Assert.True(bounds.Left >= -.5 && bounds.Right <= grid.ActualWidth + .5, "Quantity column must be horizontally reachable: " + header.Content);
            }
            viewport.ScrollToBottom(); Pump(control);
        }
        Save(control, width, height, tab, "bottom");
        var tableBounds = grid.TransformToAncestor(viewport).TransformBounds(new Rect(new Point(), grid.RenderSize));
        Assert.True(tableBounds.Bottom > 0 && tableBounds.Top < viewport.ActualHeight, "Table cannot be reached in the scrolled viewport.");
        if (tab == 1) AssertVisible(viewport, Find<Button>("BtnRelevance"));
    }

    private static UserControl LoadActualMarkup()
    {
        var root = typeof(PaletteSmallViewportTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "MahodPluginSourceDir").Value!;
        var xml = XDocument.Load(Path.Combine(root, "CivilDelivery", "UI", "CivilDeliveryControl.xaml"));
        foreach (var a in xml.Root!.DescendantsAndSelf().Attributes()
                     .Where(a => a.Name.LocalName == "Class" || a.Value.StartsWith("On", StringComparison.Ordinal)).ToArray()) a.Remove();
        return (UserControl)XamlReader.Parse(xml.ToString());
    }

    private static void Pump(UserControl control)
    {
        control.UpdateLayout();
        control.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        control.UpdateLayout();
    }

    private static void AssertVisible(ScrollViewer viewport, FrameworkElement element)
    {
        var bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(new Point(), element.RenderSize));
        Assert.True(bounds.Left >= -.5 && bounds.Right <= viewport.ActualWidth + .5 &&
            bounds.Top >= -.5 && bounds.Bottom <= viewport.ActualHeight + .5, element.Name + ": " + bounds);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Save(UserControl control, int width, int height, int tab, string position)
    {
        var output = Environment.GetEnvironmentVariable("MHD_SMALL_PALETTE_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(control);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(Path.Combine(output, $"tab{tab}-{width}x{height}-{position}.png"), FileMode.CreateNew);
        encoder.Save(stream);
    }
}
