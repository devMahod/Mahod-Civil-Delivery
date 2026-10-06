using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MahodAI.CivilDelivery.Estimate;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private void ShowEstimateMaterialAreas()
    {
        // Navigation is read-only; export separately proves the exact captured scan.
        var capturedScan = _scan;
        var observations = capturedScan?.MaterialAreas.ToArray() ?? System.Array.Empty<MaterialSectionAreaObservation>();
        var text = new System.Windows.Controls.TextBox
        {
            IsReadOnly = true, IsReadOnlyCaretVisible = false, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(12), FontSize = 13,
            Background = System.Windows.Media.Brushes.White, Foreground = System.Windows.Media.Brushes.Black,
        };
        var previous = new Button { Content = "תצפיות קודמות", MinHeight = 38, Margin = new Thickness(4) };
        var next = new Button { Content = "תצפיות הבאות", MinHeight = 38, Margin = new Thickness(4) };
        var close = new Button { Content = "סגור", IsCancel = true, MinHeight = 38, Margin = new Thickness(4) };
        var export = new Button { Content = "ייצא את כל המדידות ל־Excel", MinHeight = 38,
            Margin = new Thickness(4), IsEnabled = observations.Length > 0 };
        var open = new Button { Content = "פתח את הקובץ", MinHeight = 38,
            Margin = new Thickness(4), Visibility = Visibility.Collapsed };
        var exportStatus = new TextBox { IsReadOnly = true, BorderThickness = new Thickness(0),
            TextWrapping = TextWrapping.Wrap, Padding = new Thickness(6),
            Text = "שטח חתך בכל תחנה — לא שטח במבט־על ולא כתב כמויות. הייצוא כולל את כל העמודים." };
        var pageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center,
            FlowDirection = System.Windows.FlowDirection.LeftToRight, Margin = new Thickness(12, 4, 12, 4) };
        var buttons = new WrapPanel { Margin = new Thickness(4) };
        buttons.Children.Add(previous); buttons.Children.Add(next); buttons.Children.Add(pageLabel);
        buttons.Children.Add(export); buttons.Children.Add(open); buttons.Children.Add(close);
        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        DockPanel.SetDock(exportStatus, Dock.Bottom); panel.Children.Add(exportStatus); panel.Children.Add(text);
        var dialog = new Window
        {
            Title = "שטחי חומר בחתכים — ראיות מדידה, לא כתב כמויות", Content = panel,
            Width = 820, Height = 680, MinWidth = 540, MinHeight = 380,
            ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FlowDirection = System.Windows.FlowDirection.RightToLeft, FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
        };
        var offset = 0;
        void Render(int requested)
        {
            var page = MaterialSectionAreaReviewPolicy.Read(observations, requested);
            offset = page.Offset; text.Text = page.Text; text.ScrollToHome();
            previous.IsEnabled = page.HasPrevious; next.IsEnabled = page.HasNext;
            pageLabel.Text = $"{(page.Count == 0 ? 0 : page.Offset + 1)}–{page.Offset + page.Count} / {page.Total}";
        }
        previous.Click += (_, _) => Render(offset - MaterialSectionAreaReviewPolicy.PageSize);
        next.Click += (_, _) => Render(offset + MaterialSectionAreaReviewPolicy.PageSize);
        close.Click += (_, _) => dialog.Close();
        string? exportedPath = null;
        export.Click += (_, _) =>
        {
            try
            {
                if (capturedScan == null || !ReferenceEquals(capturedScan, _scan))
                    throw new System.InvalidOperationException("הסריקה התחלפה — סגור חלון זה ופתח את המדידות העדכניות.");
                var doc = Doc() ?? throw new System.InvalidOperationException("אין שרטוט פעיל.");
                var written = _estimate.ExportMaterialAreas(doc, capturedScan);
                exportedPath = written.XlsxPath;
                exportStatus.Text = $"יוצאו {written.ObservationCount} מדידות שטח חתך, ללא תמחור:\n{exportedPath}";
                export.IsEnabled = false; open.Visibility = Visibility.Visible;
                Log(exportStatus.Text); SetStatus("דו״ח מדידות שטח החתך יוצא — לא כתב כמויות");
            }
            catch (System.Exception ex)
            {
                exportStatus.Text = "הייצוא לא הושלם: " + ex.Message;
                Log(exportStatus.Text);
            }
        };
        open.Click += (_, _) =>
        {
            try
            {
                if (exportedPath != null)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exportedPath) { UseShellExecute = true });
            }
            catch (System.Exception ex) { exportStatus.Text = "הקובץ נשמר, אך פתיחתו נכשלה: " + ex.Message; }
        };
        Render(0); UiGuard.Attach(dialog, "שטחי חומר בחתכים"); CivilModalHost.ShowFromPalette(dialog);
    }
}
