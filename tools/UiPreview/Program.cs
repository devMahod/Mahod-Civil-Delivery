using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Mahod.CivilDelivery.UiPreview;

/// <summary>
/// The gallery: every view of the tool, loaded from the real XAML with sample data. Without arguments it opens the
/// gallery window; with <c>--snapshot DIR</c> it writes one PNG per view into DIR and exits (what a review reads).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var uiDir = Meta("CivilDeliveryUiDir");
        var assetsDir = Meta("CivilDeliveryAssetsDir");
        var snapshotIndex = Array.IndexOf(args, "--snapshot");
        if (snapshotIndex >= 0)
        {
            var dir = snapshotIndex + 1 < args.Length ? args[snapshotIndex + 1] : "snapshots";
            Directory.CreateDirectory(dir);
            var written = 0;
            foreach (var view in Views.All(uiDir, assetsDir))
            {
                try
                {
                    var (element, size) = view.Build();
                    Snapshot.Save(element, size, Path.Combine(dir, view.FileName + ".png"));
                    written++;
                    Console.WriteLine("wrote " + view.FileName + ".png");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("FAILED " + view.FileName + ": " + ex.GetBaseException().Message);
                }
            }
            Console.WriteLine($"{written} snapshots in {Path.GetFullPath(dir)}");
            return 0;
        }
        app.Run(new GalleryWindow(uiDir, assetsDir));
        return 0;
    }

    private static string Meta(string key) =>
        typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;
}

/// <summary>Renders a detached element at a given size, the way the plugin's own layout tests do.</summary>
public static class Snapshot
{
    public static void Save(FrameworkElement element, Size size, string path)
    {
        element.Width = size.Width; element.Height = size.Height;
        for (var pass = 0; pass < 2; pass++)
        {
            element.Measure(size); element.Arrange(new Rect(size)); element.UpdateLayout();
        }
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

/// <summary>The gallery window: views on the right (RTL), the chosen view in the middle at its real size.</summary>
public sealed class GalleryWindow : Window
{
    private readonly ScrollViewer _stage = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(8) };
    private readonly ComboBox _sizes = new() { Margin = new Thickness(8, 8, 8, 0) };
    private readonly string _uiDir, _assetsDir;

    public GalleryWindow(string uiDir, string assetsDir)
    {
        _uiDir = uiDir; _assetsDir = assetsDir;
        Title = "Mahod Civil Delivery — תצוגה מקדימה של הממשק";
        Width = 1280; Height = 960; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FlowDirection = FlowDirection.RightToLeft;
        FontFamily = new FontFamily(new Uri(Path.Combine(uiDir, "Fonts") + Path.DirectorySeparatorChar), "./#Rubik, Segoe UI, Arial");
        FontSize = 13;
        Background = new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xEB));

        var side = new DockPanel { Width = 250, Background = Brushes.White };
        var title = new TextBlock { Text = "חלונות הכלי", Margin = new Thickness(14, 14, 14, 4), FontWeight = FontWeights.SemiBold, FontSize = 15 };
        DockPanel.SetDock(title, Dock.Top); side.Children.Add(title);
        var hint = new TextBlock { Text = "ה-XAML האמיתי של הכלי, עם נתוני דוגמה. שינוי בקובץ XAML נטען מחדש בלחיצה על החלון.",
            Margin = new Thickness(14, 0, 14, 8), TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x75, 0x70)), FontSize = 11.5 };
        DockPanel.SetDock(hint, Dock.Top); side.Children.Add(hint);
        foreach (var size in new[] { "720 × 900 (ברירת מחדל)", "600 × 760 (מינימום)", "480 × 600 (צר)", "900 × 1100 (רחב)" }) _sizes.Items.Add(size);
        _sizes.SelectedIndex = 0; _sizes.SelectionChanged += (_, _) => Show();
        DockPanel.SetDock(_sizes, Dock.Top); side.Children.Add(_sizes);
        var open = new Button { Content = "פתח כחלון אמיתי", Margin = new Thickness(8), Padding = new Thickness(10, 7, 10, 7) };
        open.Click += (_, _) => OpenAsWindow();
        DockPanel.SetDock(open, Dock.Bottom); side.Children.Add(open);
        side.Children.Add(_list);
        foreach (var view in Views.All(uiDir, assetsDir)) _list.Items.Add(new ListBoxItem { Content = view.Title, Tag = view, Padding = new Thickness(10, 7, 10, 7) });
        _list.SelectionChanged += (_, _) => Show();

        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Right); root.Children.Add(side);
        root.Children.Add(_stage);
        Content = root;
        _list.SelectedIndex = 0;
    }

    private View? Current => (_list.SelectedItem as ListBoxItem)?.Tag as View;

    private Size StageSize()
    {
        var current = Current;
        if (current == null) return new Size(720, 900);
        if (!current.IsPalette) return current.WindowSize;
        return _sizes.SelectedIndex switch { 1 => new Size(600, 760), 2 => new Size(480, 600), 3 => new Size(900, 1100), _ => new Size(720, 900) };
    }

    private void Show()
    {
        var view = Current;
        if (view == null) return;
        try
        {
            var (element, _) = view.Build();
            var size = StageSize();
            element.Width = size.Width; element.Height = size.Height;
            var frame = new Border { Child = element, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xC3, 0xCA, 0xD2)), BorderThickness = new Thickness(1), Background = Brushes.White };
            _stage.Content = frame;
        }
        catch (Exception ex)
        {
            _stage.Content = new TextBox { Text = ex.ToString(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24), FlowDirection = FlowDirection.LeftToRight };
        }
    }

    private void OpenAsWindow()
    {
        var view = Current;
        if (view == null) return;
        try
        {
            var window = view.BuildWindow();
            window.Owner = this;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.GetBaseException().Message, "לא נפתח", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
