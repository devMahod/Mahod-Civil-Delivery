using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using Xunit;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using Border = System.Windows.Controls.Border;
using Binding = System.Windows.Data.Binding;
using FlowDirection = System.Windows.FlowDirection;
using Brushes = System.Windows.Media.Brushes;
using Size = System.Windows.Size;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class InfrastructureRecommendationUiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recommended_item_keeps_one_visible_badge_when_selection_moves(bool selectRecommended)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var ordinary = new InfrastructureCatalogOption("57.02.0001", "TEST ONLY — צינור מים חלופי", "מטר", 10, "TEST ONLY", 20);
                var recommended = new InfrastructureCatalogOption("57.02.0002", "TEST ONLY — צינור מים מפוליאתילן בקוטר 200 מ״מ", "מטר", 27, "קוטר וחומר לפי שם שכבה — TEST ONLY", 100)
                { MatchesLayerTopic = true, IsRecommended = true };
                var grid = InfrastructureBoqWindow.Table();
                InfrastructureBoqWindow.RecommendedCodeColumn(grid);
                grid.Columns.Add(new DataGridTextColumn { Header = "תיאור", Binding = new Binding("Description"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
                grid.Columns.Add(new DataGridTextColumn { Header = "₪ ליחידה", Binding = new Binding("PriceDisplay"), Width = 90 });
                grid.EnableRowVirtualization = false;
                grid.ItemsSource = new[] { ordinary, recommended };
                grid.SelectedItem = selectRecommended ? recommended : ordinary;
                grid.FlowDirection = FlowDirection.RightToLeft;
                grid.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
                var frame = new Border { Child = grid, Background = Brushes.White, Padding = new Thickness(12) };
                frame.Measure(new Size(700, 230)); frame.Arrange(new Rect(0, 0, 700, 230)); frame.UpdateLayout();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                frame.UpdateLayout();
                var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(recommended));
                var badge = Assert.Single(Descendants<TextBlock>(row), t => t.Text == "מומלץ");
                Assert.True(badge.ActualWidth > 0 && badge.ActualHeight > 0);
                var badgeBorder = Assert.IsType<Border>(VisualTreeHelper.GetParent(badge));
                Assert.Equal(Visibility.Visible, badgeBorder.Visibility);
                Assert.Equal(FontWeights.SemiBold, row.FontWeight);
                Assert.All(Descendants<DataGridCell>(row), cell =>
                {
                    var color = Assert.IsType<SolidColorBrush>(cell.Background).Color;
                    Assert.True(color.G > color.R && color.G > color.B, "Recommendation background must stay green under the selection theme.");
                });
                Assert.DoesNotContain(Descendants<TextBlock>((DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(ordinary)), t => t.Text == "מומלץ");
                var directory = Environment.GetEnvironmentVariable("MHD_BOQ_RECOMMENDATION_RENDER_DIR");
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                    var bitmap = new RenderTargetBitmap(700, 230, 96, 96, PixelFormats.Pbgra32); bitmap.Render(frame);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(directory, $"recommendation-{(selectRecommended ? "selected" : "unselected")}.png"));
                    encoder.Save(output);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Recommendation rendering timed out.");
        if (failure != null) throw new InvalidOperationException("Recommendation rendering failed.", failure);
    }
    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
