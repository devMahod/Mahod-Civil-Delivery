using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TabControl = System.Windows.Controls.TabControl;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// A launcher tab is an explicit action. Selection and restored keyboard focus
/// are navigation events and must never queue a CAD command or open a window.
/// </summary>
internal sealed class BoqTabLauncher
{
    private readonly TabControl _tabs;
    private readonly TabItem _launcher, _fallback;
    private readonly Action _launch;

    internal BoqTabLauncher(TabControl tabs, TabItem launcher, TabItem fallback, Action launch)
    {
        _tabs = tabs; _launcher = launcher; _fallback = fallback; _launch = launch;
        _tabs.SelectionChanged += OnSelectionChanged;
        _launcher.PreviewMouseUp += OnHeaderMouseUp;
        _launcher.PreviewKeyDown += OnHeaderKeyDown;
        RestoreContentTab();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, _tabs)) RestoreContentTab();
    }

    private void RestoreContentTab()
    {
        if (ReferenceEquals(_tabs.SelectedItem, _launcher)) _tabs.SelectedItem = _fallback;
    }

    private bool CanActivate => _tabs.IsLoaded && _launcher.IsLoaded && _launcher.IsVisible && _launcher.IsEnabled;

    private void OnHeaderMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount > 1 || !CanActivate || !IsHeader(e.OriginalSource)) return;
        e.Handled = true;
        RestoreContentTab();
        _launch();
    }

    private void OnHeaderKeyDown(object sender, KeyEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, _launcher) || e.IsRepeat ||
            e.Key is not (Key.Enter or Key.Space) || !CanActivate) return;
        e.Handled = true;
        RestoreContentTab();
        _launch();
    }

    private bool IsHeader(object originalSource)
    {
        // A selected TabItem's body is hosted elsewhere in the TabControl's
        // visual tree. Only its header is a launch target, including its padding.
        for (var current = originalSource as DependencyObject; current != null;)
        {
            if (ReferenceEquals(current, _launcher)) return true;
            current = current is Visual ? VisualTreeHelper.GetParent(current)
                : (current as FrameworkContentElement)?.Parent;
        }
        return false;
    }
}
