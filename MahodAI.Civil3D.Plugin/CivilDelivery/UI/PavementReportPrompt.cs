using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using Theme = MahodAI.Civil3D.Plugin.CivilDelivery.UI.CivilDeliveryTheme;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// "Build quantities" starts by asking for the drawing's pavement-structure report (engineers' call, 2026-11-09): the
/// asphalt and base layers, their volumes and prices come from it. The report is optional (coworkers, same day): without
/// it the layers are calculated from the default structures of the road type chosen here, and every screen, row and
/// export says they are defaults. Asked once per run; a drawing that already has a report offers to keep it.
/// </summary>
internal sealed class PavementReportPrompt : Window
{
    internal enum Choice { Without, KeepExisting, Attach }
    internal Choice Result { get; private set; } = Choice.Without;
    internal IReadOnlyList<string> Files { get; private set; } = Array.Empty<string>();
    /// <summary>The road type for default structures (<see cref="PavementDefaults.NoneId"/> = no defaults).</summary>
    internal string DefaultClass { get; private set; }

    private const string NoDefaults = "— ללא ברירת מחדל (לא לתמחר)";

    internal PavementReportPrompt(string drawing, PavementReportSet existing, string? savedClass)
    {
        Title = "דו״ח מבנה מיסעה";
        Width = 600; SizeToContent = SizeToContent.Height; MinWidth = 440;
        FlowDirection = System.Windows.FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
        Background = Theme.Canvas; Foreground = Theme.Ink;
        Theme.Apply(this, 13);
        var current = PavementDefaults.Resolve(savedClass);
        DefaultClass = current?.Id ?? PavementDefaults.NoneId;

        var body = new StackPanel { Margin = new Thickness(18) };
        // Two sentences; how the report is read is a closed section (owner 2026-11-11: the windows carried too much text).
        body.Children.Add(new TextBlock { Text = "דו״ח מבנה מיסעה (לא חובה)", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink, Margin = new Thickness(0, 0, 0, 6) });
        body.Children.Add(Paragraph("עם דו״ח היועץ (PDF) — שכבות האספלט והמצעים לפי הדו״ח. בלי דו״ח — לפי ברירת מחדל של סוג הדרך שתבחר כאן."));
        body.Children.Add(new Expander
        {
            Header = "איך זה עובד", Margin = new Thickness(0, 2, 0, 2), Foreground = Theme.TextSoft,
            Content = Paragraph("MahodAI קורא את הדו״ח — מבני המיסעה, עובי כל שכבה ובאילו מקטעים חל כל מבנה — ושכבות המבנה נפרסות על שטחי הכבישים בשרטוט לפי קווי הציר. " +
                                "ערכי ברירת מחדל הם מבנים טיפוסיים ולא תכנון הפרויקט, ומסומנים \"ברירת מחדל\" בכל שורה ובייצוא."),
        });
        if (existing.HasReport)
        {
            var list = string.Join("\n", existing.Reports.Select(r => "• " + r.FileName + (r.Content.Document.Consultant is { Length: > 0 } c ? " — " + c : "") +
                                                                      $" (נקרא {r.ReadUtc.ToLocalTime():dd/MM/yyyy})"));
            var card = new Border { Background = Theme.AccentTint, BorderBrush = Theme.Accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 8, 0, 4),
                Child = new TextBlock { Text = "צורף לשרטוט זה:\n" + list, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Ink } };
            body.Children.Add(card);
        }

        // The road type for the defaults: for the whole area without a report, or for the areas the report does not cover.
        var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 2) };
        typeRow.Children.Add(new TextBlock { Text = existing.HasReport ? "ברירת מחדל לשטחים בלי מבנה:" : "סוג דרך:", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Ink });
        var pick = new ComboBox { MinWidth = 280, Padding = new Thickness(5), Margin = new Thickness(8, 0, 0, 0),
            ItemsSource = PavementDefaults.Classes.Select(k => k.Name).Prepend(NoDefaults).ToArray(), SelectedItem = current?.Name ?? NoDefaults };
        pick.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "pavement-default-class");
        typeRow.Children.Add(pick);
        body.Children.Add(typeRow);
        var detail = Paragraph(""); detail.FontSize = 11.5;
        void Describe()
        {
            var k = PavementDefaults.Classes.FirstOrDefault(c => c.Name == pick.SelectedItem as string);
            DefaultClass = k?.Id ?? PavementDefaults.NoneId;
            detail.Text = k == null ? "שטחים בלי מבנה לא יתומחרו." : $"{k.Traffic}. מיסעה: {k.StructureFor(RoadSurfaceKind.Road)?.LayersDisplay}";
        }
        pick.SelectionChanged += (_, _) => Describe();
        Describe();
        body.Children.Add(detail);

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (existing.HasReport)
        {
            var keep = Action("המשך עם הדו״ח שצורף", "GoButton", "pavement-keep");
            keep.IsDefault = true;
            keep.Click += (_, _) => { Result = Choice.KeepExisting; DialogResult = true; };
            actions.Children.Add(keep);
        }
        var attach = Action(existing.HasReport ? "החלף בדו״ח אחר…" : "צרף דו״ח מבנה…", existing.HasReport ? "PillButton" : "GoButton", "pavement-attach");
        if (!existing.HasReport) attach.IsDefault = true;
        attach.Click += (_, _) =>
        {
            var files = PickFiles(this, drawing);
            if (files.Count == 0) return;
            Files = files; Result = Choice.Attach; DialogResult = true;
        };
        actions.Children.Add(attach);
        // Without a report there is one way on: the defaults of the chosen road type ("— ללא ברירת מחדל" = nothing priced).
        var close = Action(existing.HasReport ? "סגור" : "המשך בלי דו״ח", existing.HasReport ? "QuietButton" : "PillButton", "pavement-without");
        close.IsCancel = true;
        close.Click += (_, _) => { Result = existing.HasReport ? Choice.KeepExisting : Choice.Without; DialogResult = false; };
        actions.Children.Add(close);
        body.Children.Add(actions);
        Content = body;
        UiGuard.Attach(this, "דו״ח מבנה מיסעה");
    }

    /// <summary>The engineer's report files (one or more — a large drawing may be split between reports); empty if cancelled.</summary>
    internal static IReadOnlyList<string> PickFiles(Window? owner, string drawing)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "דו״ח מבנה מיסעה (PDF)", Multiselect = true, CheckFileExists = true,
            Filter = "דו״ח מבנה מיסעה (*.pdf;*.png;*.jpg)|*.pdf;*.png;*.jpg;*.jpeg|כל הקבצים (*.*)|*.*",
        };
        try { var folder = Path.GetDirectoryName(drawing); if (Directory.Exists(folder)) dialog.InitialDirectory = folder; } catch (Exception) { }
        return (owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FileNames : Array.Empty<string>();
    }

    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Theme.TextSoft, Margin = new Thickness(0, 3, 0, 3) };

    private static Button Action(string text, string style, string id)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 4, 8, 0), MinHeight = 34 };
        if (Theme.Dictionary[style] is Style s) button.Style = s;
        button.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, id);
        return button;
    }
}
