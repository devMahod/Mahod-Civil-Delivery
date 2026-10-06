using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using Xunit;
using CheckBox = System.Windows.Controls.CheckBox;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Unshown compiled WPF, synthetic input. No Civil, no active desktop, no live profile.</summary>
public sealed class EstimateSourceSelectionDialogTests
{
    private static EstimateSourceInventory Inventory() => new("918f198d-bca7-4ff3-925e-e9be4239a767", @"C:\TEST-ONLY\984.dwg",
        new[] { new EstimateSourceDefinition("host", "984.dwg", @"C:\TEST-ONLY\984.dwg", "מארח", true) }
            .Concat(Enumerable.Range(1, 8).Select(i => new EstimateSourceDefinition(
                EstimateSourceSelectionPolicy.XrefKey("HW-SIMUN-" + i, $"SIMUN-{i}.dwg", i.ToString()),
                "HW-SIMUN-" + i, $"C:\\TEST-ONLY\\קובץ-הדגמה-עם-נתיב-ארוך\\SIMUN-{i}.dwg",
                i == 8 ? "לא טעון" : "טעון"))).ToArray());

    [Theory]
    [InlineData(920, 710)]
    [InlineData(550, 500)]
    public void RenderAndEditScopeWithoutShowingWindowOrMutatingProfile(int width, int height) =>
        ManualMappingBatchDialogTests.RunSta(() =>
        {
            var inventory = Inventory();
            var saved = EstimateSourceSelectionPolicy.Approve(inventory,
                EstimateSourceSelectionPolicy.CreateDraft(inventory, null), "TEST-ONLY", DateTime.UtcNow);
            var dialog = new EstimateSourceReviewDialog(inventory, saved,
                "מצאי סינתטי לבדיקה בלבד. מקור מקונן יורש בחירת הענף. אין מדידות בדוגמה זו.",
                "כל המקורות כלולים בתחילה. אפשר להחריג מקור במפורש. מקור שנבחר ואינו טעון עדיין חוסם את האומדן. הבחירה אינה אישור למדידות או למחירים.");
            try
            {
                var frame = UnshownDialogRender.Attach(dialog, width, height);
                frame.Resources = dialog.Resources;
                Pump(); frame.UpdateLayout();
                dialog.SourcesGrid.ActualHeight.Should().BeGreaterThan(180, "at least a source row and its group must be readable");
                UnshownDialogRender.AssertWithin(dialog.BtnApprove, frame);
                UnshownDialogRender.AssertWithin(dialog.BtnCancel, frame);
                UnshownDialogRender.AssertWithin(dialog.SelectionSummary, frame);
                dialog.IsVisible.Should().BeFalse();
                var header = Descendants<CheckBox>(dialog.SourcesGrid).First(c => c.DataContext is CollectionViewGroup group && group.Name.ToString() == "סימון ותמרור");
                header.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump(); frame.UpdateLayout();
                dialog.Rows.Where(r => r.Category == "markings").Should().OnlyContain(r => !r.Included);
                saved.Sources.Should().OnlyContain(s => s.Included, "the project is untouched until explicit save");
                dialog.Rows[1].Category = "landscape";
                Pump(); frame.UpdateLayout();
                dialog.Rows[1].Included.Should().BeFalse("category changes never toggle the source check");
                var view = (ListCollectionView)dialog.SourcesGrid.ItemsSource;
                view.Groups!.Cast<CollectionViewGroup>().Should().Contain(g => g.Name.ToString() == "אדריכלות נוף");
                UnshownDialogRender.Save(frame, "MHD_SCOPE_UI_CAPTURE_DIR", $"scope-edited-{width}");
                foreach (var row in dialog.Rows) row.Included = false;
                Pump(); frame.UpdateLayout();
                dialog.BtnApprove.IsEnabled.Should().BeFalse();
                UnshownDialogRender.Save(frame, "MHD_SCOPE_UI_CAPTURE_DIR", $"scope-empty-{width}");
                dialog.Rows.Last().Included = true;
                Pump(); frame.UpdateLayout();
                dialog.BtnApprove.IsEnabled.Should().BeTrue("an unloaded selected source remains selectable, not silently excluded");
                dialog.SourcesGrid.ScrollIntoView(dialog.Rows.Last()); frame.UpdateLayout();
                UnshownDialogRender.Save(frame, "MHD_SCOPE_UI_CAPTURE_DIR", $"scope-unloaded-{width}");
                dialog.DialogResult.Should().BeNull();
            }
            finally { dialog.Close(); }
        });

    [Fact]
    public void SourceSelectionIsAppliedBeforeXrefClipTransformAndMeasurement()
    {
        var dir = typeof(EstimateSourceSelectionDialogTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "MahodPluginSourceDir").Value!;
        var code = File.ReadAllText(Path.Combine(dir, "CivilDelivery", "Estimate", "CivilQuantityExtractionService.cs"));
        var branch = code.IndexOf("if (!Included(scopeKey)) return;", StringComparison.Ordinal);
        branch.Should().BeGreaterThan(0).And.BeLessThan(code.IndexOf("if (!TryInspectActiveSpatialClip(tr, reference", StringComparison.Ordinal));
        code.IndexOf("if (!Included(source.ScopeKey)) return;", StringComparison.Ordinal)
            .Should().BeLessThan(code.IndexOf("result.ScannedEntities++;", StringComparison.Ordinal));
        code.Should().Contain("MatchProblems(inventory, selection)").And.Contain("sourceChoices.ContainsKey(scopeKey)");
    }

    [Theory]
    [InlineData(920, 710)]
    [InlineData(550, 500)]
    public void Project984Names_CreateReadableMultiCategoryGroupsWithoutAutoExclusion(int width, int height) =>
        ManualMappingBatchDialogTests.RunSta(() =>
        {
            // Actual definition names, synthetic paths/metadata. No live drawing is opened.
            var names = new[] { "ETCH-AR-MIFLAS-2.40", "HW-MAAR-EV-CD", "SR-Eilat_TH-MHD",
                "TR-FloorGrd-GM-PD-M30", "TR-FloorGrd-GM-SD-M30", "TR-FloorGrd-HA-PD", "TR-Rampa-M30", "TR-Rampa-M30-new" };
            var inventory = Inventory() with { Sources = new[] { Inventory().Sources[0] }.Concat(names.Select((n, i) =>
                new EstimateSourceDefinition(EstimateSourceSelectionPolicy.XrefKey(n, n + ".dwg", i.ToString("X")),
                    n, @"C:\TEST-ONLY\984\" + n + ".dwg", i is 0 or 3 ? "טעון" : "לא טעון"))).ToArray() };
            var dialog = new EstimateSourceReviewDialog(inventory, null,
                "שמות 984; נתיבים ומדידות אינם אמיתיים. הדמיית פריסה בלבד.",
                "קטגוריה היא הצעה בלבד. המקורות כלולים עד בחירה מפורשת; אין כאן אישור כמויות.");
            try
            {
                var frame = UnshownDialogRender.Attach(dialog, width, height);
                frame.Resources = dialog.Resources;
                Pump(); frame.UpdateLayout();
                var view = (ListCollectionView)dialog.SourcesGrid.ItemsSource;
                view.Groups!.Cast<CollectionViewGroup>().Select(g => g.Name.ToString()).Should()
                    .BeEquivalentTo(new[] { "לא מסווג", "אדריכלות", "כבישים ופיתוח", "מדידה ומצב קיים" });
                dialog.Rows.Should().HaveCount(9).And.OnlyContain(r => r.Included);
                dialog.SourcesGrid.ActualHeight.Should().BeGreaterThan(180);
                UnshownDialogRender.AssertWithin(dialog.BtnApprove, frame);
                UnshownDialogRender.AssertWithin(dialog.BtnCancel, frame);
                UnshownDialogRender.Save(frame, "MHD_SCOPE_UI_CAPTURE_DIR", $"scope-984-groups-{width}");
                var survey = dialog.Rows.Single(r => r.Name == "SR-Eilat_TH-MHD");
                dialog.SourcesGrid.ScrollIntoView(survey); Pump(); frame.UpdateLayout();
                UnshownDialogRender.Save(frame, "MHD_SCOPE_UI_CAPTURE_DIR", $"scope-984-survey-{width}");
                dialog.IsVisible.Should().BeFalse();
            }
            finally { dialog.Close(); }
        });

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
