using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// Typical sections (2026-10): one choice the evidence in the drawing cannot make — which corridor of an alignment
/// covers the marked stations, which TIN is the existing ground. Asked only when ambiguous; nothing is preselected;
/// the choice holds for this session's runs.
/// </summary>
internal sealed class TypicalChoiceDialog : Window
{
    private readonly ListBox _list = new() { MinHeight = 90, Margin = new Thickness(0, 6, 0, 10) };
    private readonly Button _ok, _cancel = Action("ביטול");

    internal string? Chosen { get; private set; }

    internal TypicalChoiceDialog(string title, string prompt, IReadOnlyList<string> items, string okLabel, string listName)
    {
        _ok = Action(okLabel);
        Title = title;
        Width = 460; SizeToContent = SizeToContent.Height; MinWidth = 340;
        FlowDirection = System.Windows.FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = CivilDeliveryTheme.Canvas; Foreground = CivilDeliveryTheme.Ink;
        CivilDeliveryTheme.Apply(this, 13);

        var body = new StackPanel { Margin = new Thickness(14) };
        body.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Foreground = CivilDeliveryTheme.Ink });
        foreach (var item in items) _list.Items.Add(item);
        System.Windows.Automation.AutomationProperties.SetName(_list, listName);
        body.Children.Add(_list);
        var actions = new WrapPanel();
        actions.Children.Add(_ok);
        actions.Children.Add(_cancel);
        body.Children.Add(actions);
        Content = body;

        _ok.IsEnabled = false;
        _ok.IsDefault = true; _cancel.IsCancel = true;
        _list.SelectionChanged += (_, _) => _ok.IsEnabled = _list.SelectedItem is string;
        _list.MouseDoubleClick += (_, _) => Approve();
        _ok.Click += (_, _) => Approve();
        _cancel.Click += (_, _) => { Chosen = null; DialogResult = false; };
        UiGuard.Attach(this, title);
    }

    internal static TypicalChoiceDialog ForCorridor(string alignmentName, IReadOnlyList<string> corridors) => new(
        "בחירת קורידור", $"לתוואי {alignmentName} כמה קורידורים מכסים את החתכים שסומנו", corridors,
        "השתמש בקורידור", "קורידורים");

    internal static TypicalChoiceDialog ForExistingGround(IReadOnlyList<string> surfaces) => new(
        "בחירת קרקע קיימת", "כמה משטחים בשרטוט יכולים להיות הקרקע הקיימת. על איזה מהם לחתוך?", surfaces,
        "השתמש במשטח", "משטחים");

    private void Approve()
    {
        if (_list.SelectedItem is not string item) return;
        Chosen = item;
        DialogResult = true;
    }

    private static Button Action(string label) => new()
    {
        Content = label, MinHeight = 34, MinWidth = 90, Padding = new Thickness(12, 4, 12, 4),
        Margin = new Thickness(0, 4, 8, 0), Foreground = CivilDeliveryTheme.Ink,
        Background = CivilDeliveryTheme.Panel,
    };
}
