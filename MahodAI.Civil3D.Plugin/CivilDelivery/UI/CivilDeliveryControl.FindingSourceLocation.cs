using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private void ShowEstimateFindingSources(IReadOnlyList<FindingSourceLocationPolicy.Target> targets,
        EstimateWorkflowService.ScanResult reviewedScan)
    {
        var document = Doc();
        if (document == null || !ReferenceEquals(reviewedScan, _scan))
        {
            SetStatus("הסריקה השתנתה; פתח את הממצאים מחדש לפני איתור מקור");
            return;
        }
        var picker = new FindingSourceLocationDialog(targets, source =>
            VerifiedHatchDiagnosticPath(reviewedScan, source));
        ApplyReviewDialogButtonStyle(picker);
        CivilModalHost.ShowFromPalette(picker);
        if (picker.ChosenTarget == null) return; // Cancel/Escape never dispatches native work.
        if (!ReferenceEquals(document, Doc()) || !ReferenceEquals(reviewedScan, _scan))
        {
            SetStatus("השרטוט או הסריקה השתנו בזמן בחירת המקור; לא בוצע איתור");
            return;
        }
        try
        {
            var outcome = FindingSourceLocatorService.Show(document, reviewedScan, picker.ChosenTarget.Source);
            QuantityDetail.Text = picker.Details.Text + Environment.NewLine + outcome.Message;
            Log("איתור מקור ממצא — " + outcome.Message);
            SetStatus(outcome.Message);
        }
        catch (Exception ex) { ShowError("איתור מקור ממצא — הממצא לא נפתר", ex); }
    }

    private static string? VerifiedHatchDiagnosticPath(EstimateWorkflowService.ScanResult scan, ProvenanceRef source)
    {
        if (source.MeasurementMethod != "hatch-area" || source.RunId != scan.RunId ||
            !System.Text.RegularExpressions.Regex.IsMatch(scan.RunId, "\\A[A-Za-z0-9][A-Za-z0-9_-]{0,159}\\z")) return null;
        try
        {
            const string artifact = "hatch_area_failure_diagnostics.json";
            var path = RuntimeRunManifestService.ArtifactPath(scan.RunId, artifact);
            // The only read is a fixed-name local run artifact, never the DWG path in SourceRefs.
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var proof = RuntimeRunManifestService.RequirePublishedArtifact(scan.RunId, artifact,
                document.RootElement, SectionsWorkflowService.Json, "estimate", "extract", "propose", "build", "export", "export-partial");
            return FindingSourceContextPolicy.ContainsExactDiagnosticSource(document.RootElement, source) ? proof.Path : null;
        }
        catch { return null; } // Missing/stale diagnostics never hide the original failure or enable repair.
    }
}

/// <summary>Selection of captured evidence only. Optional proof lookup reads a local diagnostic, never a source DWG.</summary>
internal sealed class FindingSourceLocationDialog : Window
{
    internal TextBox Search { get; } = new() { MinHeight = 30, Margin = new Thickness(0, 6, 0, 6) };
    internal ListBox Targets { get; } = new() { DisplayMemberPath = "Display", MinHeight = 70,
        FlowDirection = System.Windows.FlowDirection.LeftToRight };
    internal TextBox Details { get; } = new() { IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8) };
    internal Button Locate { get; } = new() { Content = "אתר את העצם שנבחר", IsEnabled = false,
        MinHeight = 38, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6) };
    internal FindingSourceLocationPolicy.Target? ChosenTarget { get; private set; }

    internal FindingSourceLocationDialog(IReadOnlyList<FindingSourceLocationPolicy.Target> targets,
        Func<ProvenanceRef, string?>? verifiedDiagnosticPath = null)
    {
        Title = "איתור עצם מתוך ממצא — קריאה בלבד";
        Width = 850; Height = 650; MinWidth = 540; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = System.Windows.FlowDirection.RightToLeft;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"); FontSize = 13;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 24, 33));
        Foreground = System.Windows.Media.Brushes.White;
        var fieldBackground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 35, 47));
        var border = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(95, 112, 136));
        foreach (var field in new System.Windows.Controls.Control[] { Search, Targets, Details })
        {
            field.Background = fieldBackground; field.Foreground = System.Windows.Media.Brushes.White;
            field.BorderBrush = border;
        }
        Search.CaretBrush = Details.CaretBrush = System.Windows.Media.Brushes.White;
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, System.Windows.Media.Brushes.White));
        var selectedStyle = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selectedStyle.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty,
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 83, 132))));
        selectedStyle.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, System.Windows.Media.Brushes.White));
        itemStyle.Triggers.Add(selectedStyle); Targets.ItemContainerStyle = itemStyle;
        ShowInTaskbar = false;
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var intro = new TextBlock { Text = $"{targets.Count:N0} מקורות מתועדים. חיפוש לפי נתיב, שכבה, handle או סוג ממצא.\n" +
            "איתור אינו תיקון או אישור; הממצא נשאר פתוח. לא נפתח או נשמר קובץ מקור.", TextWrapping = TextWrapping.Wrap };
        root.Children.Add(intro);
        Grid.SetRow(Search, 1); root.Children.Add(Search);
        Grid.SetRow(Targets, 2); root.Children.Add(Targets);
        Grid.SetRow(Details, 3); root.Children.Add(Details);
        var cancel = new Button { Content = "ביטול — ללא איתור", IsCancel = true,
            MinHeight = 38, Margin = new Thickness(6), Padding = new Thickness(12, 6, 12, 6) };
        foreach (var button in new[] { Locate, cancel })
        {
            button.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 239, 247));
            button.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 24, 33));
            button.BorderBrush = border;
        }
        var actions = new WrapPanel(); actions.Children.Add(Locate); actions.Children.Add(cancel);
        Grid.SetRow(actions, 4); root.Children.Add(actions);
        Targets.ItemsSource = targets;
        Search.TextChanged += (_, _) =>
        {
            var query = Search.Text.Trim();
            Targets.ItemsSource = targets.Where(target => query.Length == 0 ||
                (target.Display + " " + target.Title + " " + target.Code + " " +
                    string.Join(" ", target.Contexts.Select(context => context.ExactFailure ?? "")))
                    .Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            Targets.SelectedItem = null;
        };
        Targets.SelectionChanged += (_, _) =>
        {
            var target = Targets.SelectedItem as FindingSourceLocationPolicy.Target;
            Locate.IsEnabled = target != null && target.UnavailableReason == null;
            if (target == null) { Details.Text = "בחר מקור אחד מהרשימה; לא בוצע איתור."; return; }
            var source = target.Source;
            var diagnostic = verifiedDiagnosticPath?.Invoke(source);
            Details.Text = string.Join("\n", target.Contexts.Select(context => "הצעד הבא: " + context.NextStep).Distinct()) + "\n" +
                $"{target.Title}\n{Bidi.Ltr(target.Code)}\n" +
                $"נתיב: {Bidi.Ltr(source.SourcePathOrUri)}\nSHA-256: {Bidi.Ltr(source.DrawingChecksum)}\n" +
                $"שרשרת/עצם: {Bidi.Ltr(source.SourceHandle)}\nXREF: {Bidi.Ltr(source.XrefPath ?? "מארח")}\n" +
                $"שכבה: {Bidi.Ltr(source.Layer)}\nסוג: {Bidi.Ltr(source.EntityType)}\nשיטת מדידה מתועדת: {Bidi.Ltr(source.MeasurementMethod)}\n" +
                FindingSourceContextPolicy.Format(target.Contexts) + "\n\n" +
                (diagnostic == null ? "" : "קובץ אבחון מאומת לעיון ידני (נתיב להעתקה בלבד, לא שטח מחושב):\n" + Bidi.Ltr(diagnostic) + "\n\n") +
                (target.UnavailableReason ?? "האימות והאיתור יבוצעו רק לאחר לחיצה מפורשת.");
            Details.ScrollToHome();
        };
        Locate.Click += (_, _) =>
        {
            if (Targets.SelectedItem is not FindingSourceLocationPolicy.Target target || target.UnavailableReason != null) return;
            ChosenTarget = target;
            Close();
        };
        cancel.Click += (_, _) => Close();
        System.Windows.Automation.AutomationProperties.SetName(Search, "חיפוש מקורות ממצא");
        System.Windows.Automation.AutomationProperties.SetName(Targets, "מקורות מדויקים לאיתור");
        Content = root;
        UiGuard.Attach(this, "איתור מקור ממצא");
    }
}
