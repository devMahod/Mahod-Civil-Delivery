using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TabControl = System.Windows.Controls.TabControl;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Real WPF routed input and selection; the callback replaces only the native CAD command.</summary>
public sealed class BoqTabLauncherTests
{
    [Fact]
    public void Loading_restoring_selection_and_reshowing_never_launches() => RunSta(() =>
    {
        using var ui = new Fixture(show: false);
        ui.Tabs.SelectedItem = ui.Launcher;
        Assert.Same(ui.Project, ui.Tabs.SelectedItem);
        ui.Show();
        for (var i = 0; i < 3; i++)
        {
            ui.Tabs.SelectedItem = ui.Launcher;
            ui.Window.Hide(); ui.Show();
            ui.Launcher.Focus(); Fixture.Pump();
            Assert.Same(ui.Project, ui.Tabs.SelectedItem);
        }
        Assert.Equal(0, ui.Launches);
        ui.MouseUp(ui.Header);
        Assert.Equal(1, ui.Launches);
    });

    [Fact]
    public void Navigation_and_nested_selection_never_launches() => RunSta(() =>
    {
        using var ui = new Fixture();
        ui.Other.IsSelected = true;
        ui.Choices.SelectedIndex = 1;
        ui.Project.IsSelected = true;
        ui.Tabs.SelectedItem = ui.Launcher;
        ui.Key(ui.Launcher, Key.Right);
        ui.Key(ui.Launcher, Key.Tab);
        ui.MouseUp(ui.Header, MouseButton.Right);
        Assert.Equal(0, ui.Launches);
        Assert.Same(ui.Project, ui.Tabs.SelectedItem);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_header_click_launches_once_and_returns_to_content(bool headerChild) => RunSta(() =>
    {
        using var ui = new Fixture();
        UIElement target = headerChild ? ui.Header : ui.Launcher;
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Fixture.Pump();
        Assert.Equal(0, ui.Launches);
        var input = ui.MouseUp(target);
        Assert.True(input.Handled);
        Assert.Equal(1, ui.Launches);
        Assert.Same(ui.Project, ui.Tabs.SelectedItem);
    });

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    public void Explicit_keyboard_activation_launches_once(Key key) => RunSta(() =>
    {
        using var ui = new Fixture();
        Assert.True(ui.Key(ui.Launcher, key).Handled);
        Assert.Equal(1, ui.Launches);
        Assert.Same(ui.Project, ui.Tabs.SelectedItem);
    });

    [Fact]
    public void Content_and_other_tabs_do_not_activate_the_launcher() => RunSta(() =>
    {
        using var ui = new Fixture();
        ui.MouseUp(ui.ProjectBody);
        ui.MouseUp(ui.Other);
        ui.MouseUp(ui.LauncherBody);
        ui.Key(ui.LauncherBody, Key.Enter);
        ui.Key(ui.Choices, Key.Space);
        Assert.Equal(0, ui.Launches);
    });

    [Theory]
    [InlineData("unloaded")]
    [InlineData("disabled")]
    [InlineData("hidden")]
    public void Inactive_launcher_cannot_open_a_window(string state) => RunSta(() =>
    {
        using var ui = new Fixture(show: state != "unloaded");
        if (state == "disabled") ui.Launcher.IsEnabled = false;
        if (state == "hidden") ui.Launcher.Visibility = Visibility.Collapsed;
        ui.MouseUp(ui.Header);
        if (state != "unloaded") ui.Key(ui.Launcher, Key.Enter);
        Assert.Equal(0, ui.Launches);
    });

    [Fact]
    public void Production_markup_has_named_launcher_and_no_selection_launch_handler()
    {
        var markup = XDocument.Load(Path.Combine(TestXaml.UiDirectory, "CivilDeliveryControl.xaml"));
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var tabs = Assert.Single(markup.Descendants(wpf + "TabControl"), e => (string?)e.Attribute(x + "Name") == "Tabs");
        Assert.Null(tabs.Attribute("SelectionChanged"));
        Assert.Single(tabs.Elements(wpf + "TabItem"), e => (string?)e.Attribute(x + "Name") == "BoqLauncherTab");
        Assert.Single(tabs.Elements(wpf + "TabItem"), e => (string?)e.Attribute(x + "Name") == "ProjectTab");
        var source = File.ReadAllText(Path.Combine(TestXaml.UiDirectory, "CivilDeliveryControl.xaml.cs"));
        Assert.Contains("InitializeBoqLauncher();", source);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly TabControl Tabs = new();
        internal readonly TextBlock Header = new() { Text = "כתב כמויות" };
        internal readonly TextBlock LauncherBody = new() { Text = "Legacy body" };
        internal readonly TextBlock ProjectBody = new() { Text = "Project" };
        internal readonly ComboBox Choices = new() { ItemsSource = new[] { "one", "two" } };
        internal readonly TabItem Launcher, Project, Other;
        internal readonly Window Window;
        internal int Launches;

        internal Fixture(bool show = true)
        {
            Launcher = new TabItem { Header = Header, Content = LauncherBody };
            Project = new TabItem { Header = "פרויקט", Content = ProjectBody };
            Other = new TabItem { Header = "חתכים", Content = Choices };
            Tabs.Items.Add(Other); Tabs.Items.Add(Launcher); Tabs.Items.Add(Project);
            Tabs.SelectedItem = Project;
            Window = new Window { Content = Tabs, Width = 500, Height = 300,
                Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false };
            Window.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri(Path.Combine(TestXaml.UiDirectory, "CivilDeliveryTheme.xaml")) });
            _ = new BoqTabLauncher(Tabs, Launcher, Project, () => Launches++);
            if (show) Show();
        }

        internal void Show() { Window.Show(); Pump(); }
        internal static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        internal MouseButtonEventArgs MouseUp(UIElement target, MouseButton button = MouseButton.Left)
        {
            var input = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
            { RoutedEvent = Mouse.PreviewMouseUpEvent };
            target.RaiseEvent(input); Pump(); return input;
        }
        internal KeyEventArgs Key(UIElement target, Key key)
        {
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Window)!, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            target.RaiseEvent(input); Pump(); return input;
        }
        public void Dispose() { Window.Close(); Pump(); }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF launcher test timed out.");
        if (failure != null) throw new InvalidOperationException("WPF launcher regression failed.", failure);
    }
}
