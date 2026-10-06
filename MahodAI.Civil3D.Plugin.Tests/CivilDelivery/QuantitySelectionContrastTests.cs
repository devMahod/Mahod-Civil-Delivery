using System;
using System.Collections.Generic;
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
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class QuantitySelectionContrastTests
{
    [Theory]
    [InlineData(540, 880)]
    [InlineData(720, 980)]
    public void InactiveSelectedQuantityKeepsItsDescriptionLegible(int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var xml = XDocument.Load(Path.Combine(TestPaths.PluginSourceDir,
                    "CivilDelivery", "UI", "CivilDeliveryControl.xaml"));
                foreach (var attribute in xml.Root!.DescendantsAndSelf().Attributes().Where(a =>
                             a.Name.LocalName == "Class" || a.Value.StartsWith("On", StringComparison.Ordinal)).ToArray())
                    attribute.Remove();
                var control = (UserControl)XamlReader.Parse(xml.ToString());
                ((TabControl)control.FindName("Tabs")).SelectedIndex = 1;
                ((TextBlock)control.FindName("EstimateNextAction")).Text =
                    "בדיקת תצוגה בלבד: שורה נבחרת כשהמיקוד מחוץ לטבלה. אין אישור הנדסי.";
                ((System.Windows.Controls.Primitives.ToggleButton)control.FindName("BtnExpandQuantityList")).IsChecked = true;
                var grid = (DataGrid)control.FindName("QuantitiesGrid");
                grid.ItemsSource = new[] { new QuantityRowViewModel
                {
                    RuleKey = "SYNTHETIC-CONTRAST", Layer = "VISUAL-ONLY", EntityType = "LINE", Quantity = 2,
                    Unit = "מטר", Method = "length", SourceCategory = "כבישים", MappingState = "לבדיקה", ObjectCount = 1,
                    CatalogCode = "VISUAL-ONLY", CatalogDescription = "תיאור עברי לבדיקת ניגודיות בלבד — לא סעיף מאושר",
                }, new QuantityRowViewModel
                {
                    RuleKey = "SYNTHETIC-UNMAPPED", Layer = "VISUAL-OTHER", EntityType = "LINE", Quantity = 1,
                    Unit = "מטר", Method = "length", SourceCategory = "לא מסווג", MappingState = "לבדיקה", ObjectCount = 1,
                } };
                grid.SelectedIndex = 0;
                control.Width = width; control.Height = height;
                void Layout()
                {
                    for (var i = 0; i < 3; i++)
                    {
                        control.Measure(new Size(width, height)); control.Arrange(new Rect(0, 0, width, height));
                        control.UpdateLayout();
                    }
                }
                Layout();
                grid.ScrollIntoView(grid.SelectedItem, grid.Columns.OfType<DataGridTemplateColumn>().Single());
                Layout();
                Assert.False(grid.IsKeyboardFocusWithin); // no HWND or live Civil control in this test
                var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
                Assert.True(row.IsSelected);
                var description = Descendants<TextBlock>(row).Single(t => t.Text.StartsWith("תיאור עברי לבדיקת"));
                var cell = Ancestor<DataGridCell>(description);
                Assert.True(cell.IsSelected);
                var background = (SolidColorBrush)cell.Background;
                var foreground = (SolidColorBrush)description.Foreground;
                Assert.Equal((byte)255, background.Color.A);
                var ratio = Contrast(foreground.Color, background.Color);
                Assert.True(ratio >= 4.5, $"Inactive selected description contrast {ratio:0.00}: {foreground.Color} / {background.Color}");
                Assert.Equal("SYNTHETIC-CONTRAST", ((QuantityRowViewModel)grid.SelectedItem).RuleKey);
                var output = Environment.GetEnvironmentVariable("MHD_SELECTION_RENDER_DIR");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(control);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, $"quantity-inactive-selection-{width}x{height}.png"));
                    encoder.Save(stream);
                    var scroll = Descendants<ScrollViewer>(grid).First();
                    if (scroll.ScrollableWidth > 0)
                    {
                        scroll.ScrollToHorizontalOffset(scroll.ScrollableWidth);
                        Layout();
                        var scrolled = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                        scrolled.Render(control);
                        var detail = new PngBitmapEncoder(); detail.Frames.Add(BitmapFrame.Create(scrolled));
                        using var detailStream = File.Create(Path.Combine(output, $"quantity-inactive-selection-{width}x{height}-scrolled.png"));
                        detail.Save(detailStream);
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure != null) throw new InvalidOperationException("Offline inactive-selection contrast", failure);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static T Ancestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        throw new InvalidOperationException("Missing ancestor " + typeof(T).Name);
    }

    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte c) { var v = c / 255d; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
