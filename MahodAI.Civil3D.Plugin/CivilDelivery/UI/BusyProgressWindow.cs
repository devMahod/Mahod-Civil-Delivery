using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>
    /// Civil work runs synchronously on AutoCAD's UI thread, so an overlay on the
    /// palette cannot animate ("the loader looked stuck", 2026-09-03). This window
    /// lives on its own STA thread with its own dispatcher: it keeps animating and
    /// shows the current StageLog stage while the main thread is inside Civil. It
    /// never touches AutoCAD objects.
    /// </summary>
    internal sealed class BusyProgressWindow : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(false);
        private Dispatcher? _dispatcher;
        private Window? _window;
        private TextBlock? _title;
        private TextBlock? _step;
        private bool _disposed;

        public static BusyProgressWindow Show(string title, string message)
        {
            var progress = new BusyProgressWindow();
            progress.Start(title, message);
            return progress;
        }

        private void Start(string title, string message)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    _window = BuildWindow(title, message);
                    _window.Closed += (_, _) => _dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    _window.Show();
                    _ready.Set();
                    Dispatcher.Run();
                }
                catch
                {
                    _ready.Set(); // a missing progress window must never block the work itself
                }
            })
            {
                IsBackground = true,
                Name = "MahodCivilDeliveryProgress",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(3));
        }

        public void Update(string step)
        {
            var dispatcher = _dispatcher;
            if (dispatcher == null || _disposed) return;
            try
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_step != null) _step.Text = step;
                }), DispatcherPriority.Normal);
            }
            catch
            {
                // progress is advisory
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var dispatcher = _dispatcher;
            try
            {
                dispatcher?.BeginInvoke(new Action(() => _window?.Close()), DispatcherPriority.Send);
            }
            catch
            {
                // the background thread ends with the process at worst
            }
        }

        private Window BuildWindow(string title, string message)
        {
            _title = new TextBlock
            {
                Text = message,
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            _step = new TextBlock
            {
                Text = "מתחיל…",
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0xA4, 0xB2)),
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            };
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(_title);
            panel.Children.Add(new System.Windows.Controls.ProgressBar
            {
                IsIndeterminate = true,
                Height = 12,
                Margin = new Thickness(0, 10, 0, 0),
            });
            panel.Children.Add(_step);
            return new Window
            {
                Title = title,
                Content = panel,
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                FlowDirection = System.Windows.FlowDirection.RightToLeft,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x18, 0x21)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6)),
                BorderThickness = new Thickness(2),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            };
        }
    }
}
