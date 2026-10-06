using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Xunit;
using Color = System.Windows.Media.Color;
using RadioButton = System.Windows.Controls.RadioButton;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using PathMessageDialog = MahodAI.Civil3D.Plugin.CivilDelivery.UI.PathMessageDialog;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// b20, from the b19 live run on the landscape test copy (02.10): the exclusion dialog opened at 560×390 with its
/// save and cancel buttons cut off below the window edge, and the restore prompt showed its Hebrew reason
/// left-to-right. Layout-only replay of the exact product markup: no Autodesk host, handlers or native window.
/// </summary>
public sealed class PaletteDialogLayoutAndRtlTests
{
    private const string LongRuleKey = "layer:La-help|count|block:15462000";

    // Client sizes: the 560 px window less a 16 px frame; heights less a 39 px title bar and frame.
    [Theory]
    [InlineData(544, double.PositiveInfinity)] // SizeToContent: the window opens at the content's own height
    [InlineData(544, 351)]                     // the former fixed 390 px window
    [InlineData(484, 311)]                     // MinWidth × MinHeight
    public void ExclusionDialogKeepsReasonApproverAndButtonsInsideTheWindow(double width, double height) => Sta(() =>
    {
        var window = Parse("QuantityExclusionDecisionDialog.xaml");
        Assert.Equal(SizeToContent.Height, window.SizeToContent);
        Assert.True(double.IsNaN(window.Height), "a fixed Height would override SizeToContent on reopen");
        T Find<T>(string name) where T : class => (T)window.FindName(name);
        Find<TextBlock>("QuantitySummaryText").Text =
            $"שכבה: La-help\nכמות: 1,392,720.14 יחידת שרטוט² · 15 עצמים\nחוק: {LongRuleKey}";
        Find<TextBox>("ReasonBox").Text =
            "חלופת מדידה לא נבחרת לאותם פוליליינים סגורים; החלופה ההנדסית אושרה בסעיף U51.06.1900";
        Find<TextBox>("ApproverBox").Text = "ArthurF";
        Find<TextBlock>("ValidationText").Text = "סיבה הנדסית היא שדה חובה";

        var content = Detach(window);
        content.Measure(new Size(width, height));
        var arranged = new Size(width, double.IsInfinity(height) ? content.DesiredSize.Height : height);
        content.Arrange(new Rect(arranged)); content.UpdateLayout();

        var cancel = Buttons(content).Single(button => Equals(button.Content, "ביטול"));
        var visible = new List<(string Name, FrameworkElement Element)>
        {
            ("BtnSave", Find<Button>("BtnSave")), ("cancel", cancel),
            ("ApproverBox", Find<TextBox>("ApproverBox")), ("ValidationText", Find<TextBlock>("ValidationText")),
        };
        if (double.IsInfinity(height))
            visible.AddRange(new (string, FrameworkElement)[]
            {
                ("QuantitySummaryText", Find<TextBlock>("QuantitySummaryText")), ("ReasonBox", Find<TextBox>("ReasonBox")),
            });
        foreach (var (name, element) in visible)
        {
            var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(new Point(0, 0), element.RenderSize));
            Assert.True(bounds.Width > 0 && bounds.Height > 0, $"{name} has no size at {width}x{height}");
            Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5 &&
                        bounds.Right <= content.RenderSize.Width + 0.5 && bounds.Bottom <= content.RenderSize.Height + 0.5,
                $"{name} at {width}x{height}: {bounds} outside the content box {content.RenderSize}");
        }
    });

    /// <summary>Every palette dialog, empty, at its declared and at its minimum size: each action button outside a
    /// scrolling list stays inside the window (the exclusion dialog's clipped buttons were found live, not by a test).</summary>
    [Theory]
    [MemberData(nameof(Dialogs))]
    public void EveryDialogKeepsItsActionButtonsInsideTheWindowAtDeclaredAndMinimumSize(string file) => Sta(() =>
    {
        var window = Parse(file);
        var sizes = new List<Size> { new(window.MinWidth - 16, window.MinHeight - 39) };
        if (!double.IsNaN(window.Height)) sizes.Add(new Size(window.Width - 16, window.Height - 39));
        var content = Detach(window);
        foreach (var size in sizes)
        {
            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
            foreach (var button in Buttons(content).Where(b => b.Visibility == Visibility.Visible && !InScrollingList(b)))
            {
                var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(new Point(0, 0), button.RenderSize));
                if (bounds.Width == 0 && bounds.Height == 0) continue; // inside a collapsed parent
                Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5 &&
                            bounds.Right <= content.RenderSize.Width + 0.5 && bounds.Bottom <= content.RenderSize.Height + 0.5,
                    $"{file} '{button.Content}' at {size.Width}x{size.Height}: {bounds} outside the content box {content.RenderSize}");
            }
        }
    });

    public static IEnumerable<object[]> Dialogs() =>
        Directory.EnumerateFiles(Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery", "UI"), "*.xaml")
            .Where(path => File.ReadLines(path).Take(3).Any(line => line.TrimStart().StartsWith("<Window", StringComparison.Ordinal)))
            .Select(path => new object[] { Path.GetFileName(path) }).OrderBy(row => (string)row[0]);

    private static bool InScrollingList(DependencyObject element)
    {
        for (var parent = LogicalTreeHelper.GetParent(element); parent != null; parent = LogicalTreeHelper.GetParent(parent))
            if (parent is System.Windows.Controls.ScrollViewer or System.Windows.Controls.ItemsControl) return true;
        return false;
    }

    /// <summary>A window that is never shown builds no visual tree: lay out its exact content on its own, with the
    /// window's resources (implicit Button/TextBox styles), direction and font.</summary>
    private static FrameworkElement Detach(Window window)
    {
        var content = (FrameworkElement)window.Content;
        var resources = window.Resources;
        window.Content = null; window.Resources = new ResourceDictionary();
        content.Resources = resources;
        content.FlowDirection = window.FlowDirection;
        TextElement.SetFontFamily(content, window.FontFamily);
        TextElement.SetFontSize(content, window.FontSize);
        return content;
    }

    /// <summary>b21 (Codex 05:50): the footer must not jump ahead in the keyboard order. Tab follows declaration order,
    /// so the choice and the reason come before the approver and the buttons, as in b19; no TabIndex patches the
    /// order, and the scrolling content area is not a tab stop of its own.</summary>
    [Theory]
    [InlineData("QuantityExclusionDecisionDialog.xaml", "ReasonBox")]
    [InlineData("EarthworksDecisionDialog.xaml", "IncludeRadio", "ExcludeRadio", "ReasonBox")]
    public void DecisionDialogsKeepInputBeforeApproverBeforeButtonsInTabOrder(string file, params string[] inputs)
    {
        var path = Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery", "UI", file);
        var xaml = File.ReadAllText(path);
        Assert.DoesNotContain("TabIndex", xaml, StringComparison.Ordinal);
        var x = (XNamespace)"http://schemas.microsoft.com/winfx/2006/xaml";
        var elements = XDocument.Load(path).Root!.Descendants().ToList();
        int At(Func<XElement, bool> match) => elements.FindIndex(e => match(e));
        int Named(string name) => At(e => (string?)e.Attribute(x + "Name") == name);
        var order = inputs.Select(Named).Concat(new[]
        {
            Named("ApproverBox"),
            At(e => e.Name.LocalName == "Button" && (string?)e.Attribute("Content") == "ביטול"),
            Named("BtnSave"),
        }).ToArray();
        Assert.DoesNotContain(-1, order);
        Assert.True(order.Zip(order.Skip(1)).All(pair => pair.First < pair.Second),
            file + " declaration order (tab order): " + string.Join(", ", order));
        foreach (var scroller in elements.Where(e => e.Name.LocalName == "ScrollViewer"))
        {
            Assert.Equal("False", (string?)scroller.Attribute("Focusable"));
            Assert.Equal("False", (string?)scroller.Attribute("IsTabStop"));
        }
    }

    /// <summary>b22 (Codex 06:58): the earthworks choice titles inherited the RadioButton theme's near-black text on the
    /// dark dialog. Both enabled choice titles must read at WCAG AA contrast against the window background with no choice,
    /// with "include" chosen and with "exclude" chosen (Codex 07:24); the selection is checked before the colour. The
    /// disabled look is the theme's and is not covered here.</summary>
    [Fact]
    public void EarthworksChoiceTitlesAreReadableOnTheDarkDialog() => Sta(() =>
    {
        var window = Parse("EarthworksDecisionDialog.xaml");
        var background = ((SolidColorBrush)window.Background).Color;
        var include = (RadioButton)window.FindName("IncludeRadio");
        var exclude = (RadioButton)window.FindName("ExcludeRadio");
        var content = Detach(window);
        foreach (var (state, includeChecked, excludeChecked) in new[] { ("none", false, false), ("include", true, false), ("exclude", false, true) })
        {
            include.IsChecked = includeChecked;
            exclude.IsChecked = excludeChecked;
            content.Measure(new Size(564, 416)); content.Arrange(new Rect(0, 0, 564, 416)); content.UpdateLayout();
            Assert.Equal(includeChecked, include.IsChecked == true);
            Assert.Equal(excludeChecked, exclude.IsChecked == true);
            foreach (var choice in new[] { include, exclude })
            {
                Assert.True(choice.IsEnabled, $"{choice.Name} ({state}) is expected enabled");
                var title = LogicalTreeHelper.GetChildren((DependencyObject)choice.Content).OfType<TextBlock>().First();
                var color = ((SolidColorBrush)title.Foreground).Color;
                var ratio = Contrast(color, background);
                Assert.True(ratio >= 4.5, $"{choice.Name} title ({state} chosen) {color} on {background}: contrast {ratio:F2}");
            }
        }
    });

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte c) { var s = c / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Fact]
    public void EveryPaletteMessageBoxReadsRightToLeft()
    {
        var root = Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery");
        var helper = File.ReadAllText(Path.Combine(root, "UI", "RtlMessageBox.cs"));
        Assert.Contains("System.Windows.MessageBoxOptions.RtlReading | System.Windows.MessageBoxOptions.RightAlign", helper);
        Assert.Contains("System.Windows.MessageBox.Show(text, caption, buttons, icon, System.Windows.MessageBoxResult.None, Options)", helper);

        var leftToRight = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Split(Path.DirectorySeparatorChar).Contains("obj") || Path.GetFileName(path) == "RtlMessageBox.cs") continue;
            var text = File.ReadAllText(path);
            for (var at = text.IndexOf("MessageBox.Show(", StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf("MessageBox.Show(", at + 1, StringComparison.Ordinal))
            {
                if (at >= 3 && text.Substring(at - 3, 3) == "Rtl") continue;
                var call = Call(text, at + "MessageBox.Show(".Length);
                // An explicit RTL call passes the options itself (literal flags, or the guide's Rtl constant).
                if (call.Contains("MessageBoxOptions.RtlReading", StringComparison.Ordinal) ||
                    call.TrimEnd().EndsWith(", Rtl)", StringComparison.Ordinal)) continue;
                leftToRight.Add($"{Path.GetFileName(path)}:{text[..at].Count(c => c == '\n') + 1}");
            }
        }
        Assert.True(leftToRight.Count == 0, "left-to-right message boxes: " + string.Join(", ", leftToRight));
    }

    // b23 (b22 live 08:19, Codex contract D887AD54): the Win32 message box wrapped a long path by its own width and laid
    // each row out right-to-left — with or without embedding marks. A path is now the exact string in its own read-only
    // left-to-right field that never wraps, next to right-to-left Hebrew text.
    public static IEnumerable<object?[]> PathCases() => new[]
    {
        // The path that failed live (generic Excel export), a UNC share with Hebrew folders, one long component with no
        // separator at all, a folder with spaces, a bare name.
        new object?[] { @"C:\Users\arthurf\Documents\MahodCivilDelivery\drawing-clean-generic-b21-027e93e320b7\אומדן-מוקדם-drawing-clean-generic-b21-027e93e320b7-20261002-0819.xlsx", "אומדן-מוקדם-drawing-clean-generic-b21-027e93e320b7-20261002-0819.xlsx" },
        new object?[] { @"\\server\share\פרויקטים\6422\כתב-כמויות-לפי-כללים-6422-20261002-082735.xlsx", "כתב-כמויות-לפי-כללים-6422-20261002-082735.xlsx" },
        // A local path with Hebrew folders next to number folders (Codex 09:19) — the case that reversed in one field.
        new object?[] { @"C:\Users\נטלי\OneDrive - מהוד\מסמכים\6422\2026\כתב-כמויות-לפי-כללים-6422-20261002-082735.xlsx", "כתב-כמויות-לפי-כללים-6422-20261002-082735.xlsx" },
        new object?[] { @"C:\Users\arthurf\Documents\ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ.zip", null },
        new object?[] { @"C:\Users\arthurf\Documents\Mahod Civil Delivery\support package\", null },
        new object?[] { "x.xlsx", "x.xlsx" },
    };

    [Theory]
    [MemberData(nameof(PathCases))]
    public void PathMessageKeepsTheExactPathInOneLeftToRightFieldAndCopiesIt(string rawPath, string? displayName) => Sta(() =>
    {
        string? copied = null;
        var dialog = new MahodAI.Civil3D.Plugin.CivilDelivery.UI.PathMessageDialog("נוצר:", rawPath, "לפתוח ב-Excel?",
            "ייצוא Excel", MessageBoxButton.YesNo, MessageBoxImage.Information, displayName, text => copied = text);
        Assert.Equal(System.Windows.FlowDirection.RightToLeft, dialog.FlowDirection);
        var field = dialog.PathField;
        Assert.Equal(rawPath, dialog.DisplayedPath); // unchanged: no marks, rows, shortening or normalisation
        Assert.Equal(System.Windows.FlowDirection.LeftToRight, field.FlowDirection);
        Assert.Equal(System.Windows.Controls.ScrollBarVisibility.Auto, field.HorizontalScrollBarVisibility);
        Assert.Equal(System.Windows.Controls.ScrollBarVisibility.Disabled, field.VerticalScrollBarVisibility);
        Assert.True(field.Focusable && field.IsTabStop);
        // b23 (Codex 09:19, option b): each folder name, file name and separator is its own left-to-right text, laid out
        // left to right — no text spans two parts, so Hebrew and number folders can never join into one reversed run.
        Assert.Equal(System.Windows.Controls.Orientation.Horizontal, dialog.PathSegments.Orientation);
        Assert.Equal(System.Windows.FlowDirection.LeftToRight, dialog.PathSegments.FlowDirection);
        var parts = dialog.PathSegments.Children.OfType<TextBlock>().ToArray();
        Assert.Equal(dialog.PathSegments.Children.Count, parts.Length);
        Assert.All(parts, part =>
        {
            Assert.Equal(System.Windows.FlowDirection.LeftToRight, part.FlowDirection);
            Assert.Equal(TextWrapping.NoWrap, part.TextWrapping);
            Assert.True(part.Text is "\\" or "/" || !(part.Text.Contains('\\') || part.Text.Contains('/')), $"part spans a separator: '{part.Text}'");
        });
        Assert.Equal(rawPath.Count(c => c is '\\' or '/'), parts.Count(part => part.Text is "\\" or "/"));
        Assert.Equal(displayName, dialog.NameLine?.Text);
        if (dialog.NameLine != null) Assert.Equal(System.Windows.FlowDirection.LeftToRight, dialog.NameLine.FlowDirection);

        dialog.CopyPath();
        Assert.Equal(rawPath, copied); // exactly the raw string, never the display name
        Assert.Equal("הנתיב הועתק", dialog.CopyButton.Content);

        // Ctrl+C / Ctrl+Insert and the field's menu run the Copy command bound on the field: the raw string as well.
        copied = null;
        Assert.True(System.Windows.Input.ApplicationCommands.Copy.CanExecute(null, field));
        System.Windows.Input.ApplicationCommands.Copy.Execute(null, field);
        Assert.Equal(rawPath, copied);
        Assert.Same(System.Windows.Input.ApplicationCommands.Copy, dialog.CopyMenuItem.Command);
        Assert.Same(field, dialog.CopyMenuItem.CommandTarget);
        Assert.Same(dialog.CopyMenuItem, field.ContextMenu!.Items[0]);
    });

    /// <summary>b23 (Codex 09:42): a clipboard failure says to try again (the field has no partial selection, and Ctrl+C
    /// runs the same copy), changes neither the path nor the answer, and a retry copies the exact raw path.</summary>
    [Fact]
    public void PathMessageCopyFailureAsksToRetryAndARetryCopiesTheRawPath() => Sta(() =>
    {
        const string raw = @"C:\Users\נטלי\OneDrive - מהוד\מסמכים\6422\2026\כתב-כמויות-לפי-כללים-6422-20261002-082735.xlsx";
        var attempts = 0; string? copied = null;
        var dialog = new PathMessageDialog("נוצר:", raw, "לפתוח ב-Excel?", "ייצוא Excel", MessageBoxButton.YesNo,
            MessageBoxImage.Information, "כתב-כמויות-לפי-כללים-6422-20261002-082735.xlsx", text =>
            {
                if (attempts++ == 0) throw new System.Runtime.InteropServices.ExternalException("OpenClipboard failed", unchecked((int)0x800401D0));
                copied = text;
            });
        dialog.CopyPath();
        Assert.Equal("ההעתקה נכשלה — נסו שוב", dialog.CopyButton.Content);
        Assert.Null(copied);
        Assert.Equal(raw, dialog.DisplayedPath);
        Assert.Equal(MessageBoxResult.No, dialog.Result);

        System.Windows.Input.ApplicationCommands.Copy.Execute(null, dialog.PathField); // the retry, by Ctrl+C
        Assert.Equal(raw, copied);
        Assert.Equal("הנתיב הועתק", dialog.CopyButton.Content);
        Assert.Equal(MessageBoxResult.No, dialog.Result);
        Assert.Equal(2, attempts);
    });

    /// <summary>Yes only from the Yes button (the default, as the first button of the message box it replaces); No, the
    /// cancel key, the close box or a host that returns without an answer is never Yes. A notice answers OK.</summary>
    [Fact]
    public void PathMessageAnswersYesOnlyWhenYesIsChosen() => Sta(() =>
    {
        PathMessageDialog Question() => new("נוצר:", @"C:\a\b.xlsx", "לפתוח?", "ייצוא Excel",
            MessageBoxButton.YesNo, MessageBoxImage.Information, "b.xlsx", _ => { });
        static void Press(Button button) =>
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        var yes = Question();
        Assert.Equal("כן", yes.DefaultButton.Content); Assert.True(yes.DefaultButton.IsDefault); Assert.False(yes.DefaultButton.IsCancel);
        Assert.Equal("לא", yes.SecondButton!.Content); Assert.True(yes.SecondButton.IsCancel); Assert.False(yes.SecondButton.IsDefault);
        Assert.Equal(MessageBoxResult.No, yes.Result);
        Press(yes.DefaultButton);
        Assert.Equal(MessageBoxResult.Yes, yes.Result);

        var no = Question();
        Press(no.SecondButton!);
        Assert.Equal(MessageBoxResult.No, no.Result);

        Assert.Equal(MessageBoxResult.No, MahodAI.Civil3D.Plugin.CivilDelivery.UI.RtlMessageBox.ShowPath(Question(), _ => null));
        Assert.Equal(MessageBoxResult.No, MahodAI.Civil3D.Plugin.CivilDelivery.UI.RtlMessageBox.ShowPath(Question(), _ => true));
        Assert.Throws<InvalidOperationException>(() => MahodAI.Civil3D.Plugin.CivilDelivery.UI.RtlMessageBox.ShowPath(
            Question(), _ => throw new InvalidOperationException("Civil main window is unavailable.")));

        var notice = new PathMessageDialog("התיקייה עדיין לא קיימת:", @"C:\a\", null, "פתיחת תיקייה",
            MessageBoxButton.OK, MessageBoxImage.Information, null, _ => { });
        Assert.Equal("אישור", notice.DefaultButton.Content);
        Assert.True(notice.DefaultButton.IsDefault && notice.DefaultButton.IsCancel);
        Assert.Null(notice.SecondButton);
        Assert.Equal(MessageBoxResult.OK, notice.Result);
    });

    /// <summary>At its preferred width, at its narrowest and in a short window the buttons and the path field stay inside
    /// the window; a long path scrolls inside its field instead of widening the window. The window is bounded by the
    /// work area (device-independent units), never wider than the screen.</summary>
    [Theory]
    [InlineData(584, double.PositiveInfinity)] // 600 px window less the frame, content height
    [InlineData(344, double.PositiveInfinity)] // the narrowest window
    [InlineData(344, 240)]                     // short: the text scrolls, the buttons row stays
    public void PathMessageKeepsButtonsAndPathFieldInsideTheWindow(double width, double height) => Sta(() =>
    {
        var dialog = new PathMessageDialog("נוצר כתב כמויות לפי כללים:", (string)PathCases().First()[0]!,
            "עפר, מצעים ואספלט: פרקים 51.01–51.04 מהמדידה …\n\nלפתוח ב-Excel?", "כתב כמויות לפי כללים",
            MessageBoxButton.YesNo, MessageBoxImage.Information, "אומדן-מוקדם-drawing-clean-generic-b21-027e93e320b7-20261002-0819.xlsx", _ => { });
        var field = dialog.PathField; var yes = dialog.DefaultButton; var no = dialog.SecondButton!;
        var content = Detach(dialog);
        content.Measure(new Size(width, height));
        var arranged = new Size(width, double.IsInfinity(height) ? content.DesiredSize.Height : height);
        content.Arrange(new Rect(arranged)); content.UpdateLayout();
        foreach (var (name, element) in new (string, FrameworkElement)[] { ("yes", yes), ("no", no), ("path", field) })
        {
            if (name == "path" && !double.IsInfinity(height)) continue; // a short window scrolls the text; checked below
            var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(new Point(0, 0), element.RenderSize));
            Assert.True(bounds.Width > 0 && bounds.Height > 0, $"{name} has no size at {width}x{height}");
            Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5 &&
                        bounds.Right <= content.RenderSize.Width + 0.5 && bounds.Bottom <= content.RenderSize.Height + 0.5,
                $"{name} at {width}x{height}: {bounds} outside the content box {content.RenderSize}");
        }
        Assert.True(field.ExtentWidth > field.ViewportWidth, "the long path scrolls inside its own field");

        var bounded = new PathMessageDialog(null, @"C:\a.xlsx", null, "x", MessageBoxButton.OK, MessageBoxImage.None, null, _ => { });
        bounded.ApplyWorkArea(new Size(500, 400));
        Assert.Equal(484.0, bounded.MaxWidth); Assert.Equal(384.0, bounded.MaxHeight);
        Assert.Equal(360.0, bounded.MinWidth); Assert.Equal(484.0, bounded.Width);
        bounded.ApplyWorkArea(new Size(300, 400));
        Assert.Equal(284.0, bounded.MaxWidth); Assert.Equal(284.0, bounded.MinWidth); // never wider than the screen
    });

    /// <summary>The eight palette messages that carry a path go through RtlMessageBox.ShowPath with the raw path
    /// variable; no message-box text formats a path (no DisplayPath, no embedding helpers); and what is opened —
    /// Process.Start / OpenFolder — is the raw path, never the displayed file name.</summary>
    [Fact]
    public void PathMessagesShowTheRawPathInTheFieldAndOpenTheRawPath()
    {
        var ui = Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery", "UI");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(ui, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("PathLines(", text);
            Assert.DoesNotContain("RtlMessageBox.LeftToRight(", text);
            void Scan(string marker, Func<string, bool> bad, string why)
            {
                for (var at = text.IndexOf(marker, StringComparison.Ordinal); at >= 0;
                     at = text.IndexOf(marker, at + 1, StringComparison.Ordinal))
                    if (bad(Call(text, at + marker.Length)))
                        offenders.Add($"{Path.GetFileName(file)}:{text[..at].Count(c => c == '\n') + 1} {why}");
            }
            Scan("MessageBox.Show(", call => call.Contains("DisplayPath(", StringComparison.Ordinal), "DisplayPath in message-box text");
            foreach (var opener in new[] { "new ProcessStartInfo(", "OpenFolder(" })
                Scan(opener, call => call.Contains("GetFileName(", StringComparison.Ordinal) ||
                                     call.Contains("ShowPath(", StringComparison.Ordinal), "display name opened");
        }
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
        foreach (var (file, rawPaths) in new[]
                 {
                     ("CivilDeliveryControl.xaml.cs", new[] { "clPath", "written.XlsxPath", "dl.Path", "zip", "path" }),
                     ("CivilDeliveryControl.EngineerDraft.cs", new[] { "createdPath" }),
                     ("CivilDeliveryControl.CorridorBoq.cs", new[] { "createdPath" }),
                     ("CivilDeliveryControl.BoqRules.cs", new[] { "createdPath" }),
                     // b24 (b23 live 10:37): the ninth path message, the save-before-scan question.
                     ("CivilDeliveryControl.SaveGuidance.cs", new[] { "source.DrawingPath!" }),
                 })
        {
            var text = File.ReadAllText(Path.Combine(ui, file));
            var calls = new List<string>();
            for (var at = text.IndexOf("RtlMessageBox.ShowPath(", StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf("RtlMessageBox.ShowPath(", at + 1, StringComparison.Ordinal))
                calls.Add(Call(text, at + "RtlMessageBox.ShowPath(".Length));
            Assert.True(calls.Count == rawPaths.Length, $"{file}: {calls.Count} path messages, expected {rawPaths.Length}");
            // The raw path variable is exactly the second argument — the one the dialog shows and copies. The prefix
            // before it may be any expression, including a concatenation (Codex 11:18).
            foreach (var raw in rawPaths)
                Assert.True(calls.Any(call => ShowsRawPathAsSecondArgument(call, raw)),
                    $"{file}: no path message passes the raw '{raw}' as its second argument");
        }
    }

    [Theory]
    [InlineData("\"נוצר:\", createdPath, \"לפתוח?\")", "createdPath", true)]
    [InlineData("\"לפני \" + operation + \" יש לשמור, ואז להמשיך.\", source.DrawingPath!, saveQuestion, \"שמירה והמשך\")", "source.DrawingPath!", true)]
    [InlineData("string.Concat(\"a\", b), zip, $\"{skipped}, {more}\")", "zip", true)]
    [InlineData("\"prefix\", \"wrong-path\", source.DrawingPath!, \"q\")", "source.DrawingPath!", false)]
    [InlineData("\"prefix\" + source.DrawingPath!, other, \"q\")", "source.DrawingPath!", false)]
    [InlineData("source.DrawingPath!, \"q\")", "source.DrawingPath!", false)]
    public void TheRawPathGuardChecksTheSecondArgumentItself(string call, string raw, bool expected) =>
        Assert.Equal(expected, ShowsRawPathAsSecondArgument(call + ")", raw));

    private static bool ShowsRawPathAsSecondArgument(string call, string raw)
    {
        var arguments = Arguments(call);
        return arguments.Count >= 2 && arguments[1].Trim() == raw;
    }

    // Top-level arguments of a call text as returned by Call (after "(" up to and including the closing ")").
    private static List<string> Arguments(string call)
    {
        var arguments = new List<string>();
        var depth = 0; var start = 0;
        for (var i = 0; i < call.Length; i++)
        {
            var c = call[i];
            if (c is '"' or '\'')
            {
                for (i++; i < call.Length && call[i] != c; i++)
                    if (call[i] == '\\') i++;
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0) { arguments.Add(call[start..i]); return arguments; }
                depth--;
            }
            else if (c == ',' && depth == 0) { arguments.Add(call[start..i]); start = i + 1; }
        }
        arguments.Add(call[start..]);
        return arguments;
    }

    /// <summary>The argument text of one call: up to its closing parenthesis, skipping string and char literals.</summary>
    private static string Call(string text, int start)
    {
        var depth = 1;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                for (i++; i < text.Length && text[i] != c; i++)
                    if (text[i] == '\\') i++;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return text[start..(i + 1)];
        }
        return text[start..];
    }

    private static Window Parse(string file)
    {
        if (file == "EstimateSourceReviewDialog.xaml")
            return new MahodAI.Civil3D.Plugin.CivilDelivery.UI.EstimateSourceReviewDialog("", "");
        var xml = XDocument.Load(Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery", "UI", file));
        foreach (var attribute in xml.Root!.DescendantsAndSelf().Attributes().Where(a =>
                     a.Name.LocalName == "Class" || a.Value.StartsWith("On", StringComparison.Ordinal)).ToArray())
            attribute.Remove();
        return (Window)XamlReader.Parse(xml.ToString());
    }

    private static IEnumerable<Button> Buttons(DependencyObject root) =>
        LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()
            .SelectMany(child => (child is Button button ? new[] { button } : Array.Empty<Button>()).Concat(Buttons(child)));

    private static void Sta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure != null) throw new InvalidOperationException("Offline dialog layout failed", failure);
    }
}
