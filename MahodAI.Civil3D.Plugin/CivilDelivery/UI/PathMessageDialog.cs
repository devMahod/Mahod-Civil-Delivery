using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Image = System.Windows.Controls.Image;
using MenuItem = System.Windows.Controls.MenuItem;
using Size = System.Windows.Size;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// A palette message that carries a file or folder path (b23; b22 live 08:19, Codex contract D887AD54). The Win32
/// message box wrapped a long path by its own width and laid each row out right-to-left, so the path came apart
/// ("C:" alone, "-Users\…" on the next row). Here the Hebrew text stays right-to-left and the path has its own read-only
/// left-to-right field that never wraps (a long one scrolls sideways, starting at its beginning), shown part by part so
/// Hebrew and number folders keep their order. "העתק נתיב", Ctrl+C and the field's menu copy the exact raw string.
/// The caller keeps every action (open file, open folder) on the real path.
/// Enter is the first button, as in the message box it replaces; Escape, the close box or a failure is never Yes.
/// </summary>
public sealed class PathMessageDialog : Window
{
    private const double WorkAreaMargin = 16, PreferredWidth = 600, NarrowestWidth = 360;
    private readonly string _rawPath;
    private readonly Action<string> _copy;

    /// <summary>Yes or No for a question (No unless Yes was chosen), OK for a notice.</summary>
    public MessageBoxResult Result { get; private set; }
    internal ScrollViewer PathField { get; }
    internal StackPanel PathSegments { get; }
    internal MenuItem CopyMenuItem { get; }
    internal TextBlock? NameLine { get; }

    /// <summary>The path as shown: the parts joined back together, which is exactly rawPath.</summary>
    internal string DisplayedPath => string.Concat(System.Linq.Enumerable.Select(
        System.Linq.Enumerable.OfType<TextBlock>(PathSegments.Children), part => part.Text));

    /// <summary>The path cut before and after every '\' and '/', each separator a part of its own.</summary>
    internal static System.Collections.Generic.List<string> Parts(string rawPath)
    {
        var parts = new System.Collections.Generic.List<string>();
        var start = 0;
        for (var i = 0; i < rawPath.Length; i++)
        {
            if (rawPath[i] is not ('\\' or '/')) continue;
            if (i > start) parts.Add(rawPath[start..i]);
            parts.Add(rawPath[i].ToString());
            start = i + 1;
        }
        if (start < rawPath.Length) parts.Add(rawPath[start..]);
        return parts;
    }
    internal Button CopyButton { get; }
    internal Button DefaultButton { get; }
    internal Button? SecondButton { get; }

    public PathMessageDialog(string? prefix, string rawPath, string? suffix, string caption,
        MessageBoxButton buttons, MessageBoxImage icon, string? displayName = null, Action<string>? copy = null)
    {
        if (string.IsNullOrEmpty(rawPath)) throw new ArgumentException("A path message needs the path.", nameof(rawPath));
        if (buttons is not (MessageBoxButton.OK or MessageBoxButton.YesNo))
            throw new ArgumentOutOfRangeException(nameof(buttons), buttons, "Only OK and Yes/No path messages exist.");
        _rawPath = rawPath;
        _copy = copy ?? (text => System.Windows.Clipboard.SetText(text));
        Result = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.OK;

        Title = caption;
        FlowDirection = System.Windows.FlowDirection.RightToLeft;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"); FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = System.Windows.Media.Brushes.White;
        Width = PreferredWidth; MinWidth = NarrowestWidth; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanResize;

        var body = new StackPanel { Margin = new Thickness(16, 16, 16, 8) };
        var head = new DockPanel { LastChildFill = true };
        if (IconSource(icon) is { } source)
        {
            var image = new Image { Source = source, Width = 32, Height = 32, Margin = new Thickness(0, 0, 0, 0),
                                    VerticalAlignment = System.Windows.VerticalAlignment.Top };
            DockPanel.SetDock(image, Dock.Left); // right-to-left: the icon sits on the right, as in a Hebrew message box
            head.Children.Add(image);
        }
        var texts = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        if (!string.IsNullOrWhiteSpace(prefix))
            texts.Children.Add(new TextBlock { Text = prefix, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            // The file name for recognition only; the field below holds the real path that is copied and opened.
            NameLine = new TextBlock
            {
                Text = displayName, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                FlowDirection = System.Windows.FlowDirection.LeftToRight, TextAlignment = TextAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0),
            };
            texts.Children.Add(NameLine);
        }
        // Every folder name, file name and separator is its own left-to-right text, laid out left to right (Codex 09:19,
        // option b). In one plain field the bidi algorithm joined neighbouring Hebrew and number folders into one
        // right-to-left run and showed them in reverse order ("…\6422\פרויקטים"); here no text spans two parts, so the
        // order on screen is the order of the path, and each part reads as it does on its own (".xlsx" stays at the end).
        // Nothing is added to or changed in the path: the parts are cut at '\' and '/', and joined they are rawPath.
        PathSegments = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal, FlowDirection = System.Windows.FlowDirection.LeftToRight,
        };
        foreach (var part in Parts(rawPath))
            PathSegments.Children.Add(new TextBlock
            {
                Text = part, FlowDirection = System.Windows.FlowDirection.LeftToRight, TextWrapping = TextWrapping.NoWrap,
            });
        PathField = new ScrollViewer
        {
            Content = PathSegments, FlowDirection = System.Windows.FlowDirection.LeftToRight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true, IsTabStop = true, Padding = new Thickness(6, 4, 6, 4),
        };
        // Copying from the field (Ctrl+C, Ctrl+Insert, its menu) is the exact raw path, like the button; Ctrl+A has nothing
        // partial to select, the whole path is the unit. Home/End scroll to the start/end of a long path.
        PathField.CommandBindings.Add(new System.Windows.Input.CommandBinding(System.Windows.Input.ApplicationCommands.Copy,
            (_, e) => { CopyPath(); e.Handled = true; }, (_, e) => { e.CanExecute = true; e.Handled = true; }));
        PathField.CommandBindings.Add(new System.Windows.Input.CommandBinding(System.Windows.Input.ApplicationCommands.SelectAll,
            (_, e) => e.Handled = true, (_, e) => { e.CanExecute = true; e.Handled = true; }));
        CopyMenuItem = new MenuItem
        {
            Header = "העתק נתיב", Command = System.Windows.Input.ApplicationCommands.Copy, CommandTarget = PathField,
        };
        PathField.ContextMenu = new ContextMenu { FlowDirection = System.Windows.FlowDirection.RightToLeft, Items = { CopyMenuItem } };
        System.Windows.Automation.AutomationProperties.SetName(PathField, "נתיב מלא");
        System.Windows.Automation.AutomationProperties.SetHelpText(PathField, rawPath);
        texts.Children.Add(new Border
        {
            Child = PathField, Margin = new Thickness(0, 8, 0, 0), BorderThickness = new Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xAB, 0xAD, 0xB3)),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF5, 0xF7)),
        });
        CopyButton = new Button
        {
            Content = "העתק נתיב", HorizontalAlignment = System.Windows.HorizontalAlignment.Left, MinHeight = 30,
            Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(0, 6, 0, 0),
        };
        CopyButton.Click += (_, _) => CopyPath();
        texts.Children.Add(CopyButton);
        if (!string.IsNullOrWhiteSpace(suffix))
            texts.Children.Add(new TextBlock { Text = suffix, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        head.Children.Add(texts);
        body.Children.Add(head);

        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(16, 8, 16, 16),
        };
        if (buttons == MessageBoxButton.YesNo)
        {
            DefaultButton = ActionButton("כן", MessageBoxResult.Yes);
            SecondButton = ActionButton("לא", MessageBoxResult.No);
            SecondButton.IsCancel = true;
            actions.Children.Add(DefaultButton); actions.Children.Add(SecondButton);
        }
        else
        {
            DefaultButton = ActionButton("אישור", MessageBoxResult.OK);
            DefaultButton.IsCancel = true;
            actions.Children.Add(DefaultButton);
        }
        DefaultButton.IsDefault = true;

        // The text scrolls when the window is shorter than it; the buttons row is never scrolled away or cut.
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroller = new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, IsTabStop = false,
        };
        Grid.SetRow(scroller, 0); Grid.SetRow(actions, 1);
        root.Children.Add(scroller); root.Children.Add(actions);
        Content = root;

        SourceInitialized += (_, _) => FitToWorkArea();
        Loaded += (_, _) =>
        {
            PathField.ScrollToHome(); // the start of the path (the drive or server) is what is shown first
            DefaultButton.Focus();
        };
        UiGuard.Attach(this, caption);
    }

    private Button ActionButton(string text, MessageBoxResult result)
    {
        var button = new Button { Content = text, MinWidth = 88, MinHeight = 32, Margin = new Thickness(0, 0, 8, 0) };
        // The answer is Result, never the window's DialogResult: closing the window any other way leaves the
        // initial No (or OK for a notice) in place.
        button.Click += (_, _) => { Result = result; Close(); };
        return button;
    }

    internal void CopyPath()
    {
        try
        {
            _copy(_rawPath);
            CopyButton.Content = "הנתיב הועתק";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            // The field has no partial selection and Ctrl+C runs this same copy, so the only real next step is a retry.
            CopyButton.Content = "ההעתקה נכשלה — נסו שוב";
        }
    }

    /// <summary>Bounded by the monitor work area in device-independent units (the device→DIP pattern of
    /// ManualMappingReviewDialog), so a long path never pushes the window or its buttons off the screen.</summary>
    private void FitToWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var target = HwndSource.FromHwnd(handle)?.CompositionTarget;
        if (handle == IntPtr.Zero || target == null) return;
        var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var size = target.TransformFromDevice.Transform(new Vector(work.Width, work.Height));
        ApplyWorkArea(new Size(size.X, size.Y));
    }

    internal void ApplyWorkArea(Size workArea)
    {
        if (!double.IsFinite(workArea.Width) || !double.IsFinite(workArea.Height) || workArea.Width <= 0 || workArea.Height <= 0) return;
        MaxWidth = Math.Max(1, workArea.Width - WorkAreaMargin);
        MaxHeight = Math.Max(1, workArea.Height - WorkAreaMargin);
        MinWidth = Math.Min(NarrowestWidth, MaxWidth);
        if (Width > MaxWidth) Width = MaxWidth;
    }

    private static BitmapSource? IconSource(MessageBoxImage icon)
    {
        var system = icon switch
        {
            MessageBoxImage.Warning => System.Drawing.SystemIcons.Warning,
            MessageBoxImage.Error => System.Drawing.SystemIcons.Error,
            MessageBoxImage.Question => System.Drawing.SystemIcons.Question,
            MessageBoxImage.Information => System.Drawing.SystemIcons.Information,
            _ => null,
        };
        return system == null ? null
            : Imaging.CreateBitmapSourceFromHIcon(system.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
    }
}
