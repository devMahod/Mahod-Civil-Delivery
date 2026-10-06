using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using System.Windows.Markup;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Discriminating offline probes for the reported empty combo, not a claim of native reproduction.</summary>
public sealed class PriceBookLiveRefreshContractTests
{
    [Fact]
    public void ActualPrivatePriceBookChoiceDisplaysPublicLabelInWpfWithoutProjectRestart() =>
        ManualMappingBatchDialogTests.RunSta(() =>
        {
            const string expected = "מחירון בדיקה בלבד · 2026 · 100 סעיפים";
            var type = typeof(CivilDeliveryControl).GetNestedType("PriceBookChoice", BindingFlags.NonPublic)!;
            type.Should().NotBeNull();
            var choice = Activator.CreateInstance(type, nonPublic: true)!;
            type.GetProperty("Id")!.SetValue(choice, "SYNTHETIC-ONLY");
            type.GetProperty("Label")!.SetValue(choice, expected);
            var xml = XDocument.Load(Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryControl.xaml"));
            foreach (var attribute in xml.Root!.DescendantsAndSelf().Attributes().Where(a =>
                         a.Name.LocalName == "Class" || a.Value.StartsWith("On", StringComparison.Ordinal)).ToArray())
                attribute.Remove();
            var control = (System.Windows.Controls.UserControl)XamlReader.Parse(xml.ToString());
            ((System.Windows.Controls.TabControl)control.FindName("Tabs")).SelectedIndex = 1;
            var box = (System.Windows.Controls.ComboBox)control.FindName("PriceBookCombo");
            control.Width = 720; control.Height = 980;
            void Layout()
            {
                control.Measure(new System.Windows.Size(720, 980));
                control.Arrange(new Rect(0, 0, 720, 980)); control.UpdateLayout();
                box.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                control.UpdateLayout();
            }
            Layout();
            box.ItemsSource = new[] { choice }; box.SelectedItem = choice; Layout();
            box.SelectionBoxItem.Should().BeSameAs(choice);
            string[] VisibleTexts(DependencyObject parent)
            {
                return Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(parent))
                    .Select( i => VisualTreeHelper.GetChild(parent, i))
                    .SelectMany(child => (child is TextBlock t ? new[] { t.Text } : Array.Empty<string>()).Concat(VisibleTexts(child)))
                    .ToArray();
            }
            VisibleTexts(box).Should().Contain(expected);
            CivilDeliveryControl.RefreshPriceBookCombo(box, null); Layout();
            VisibleTexts(box).Should().NotContain(expected, "a missing/invalid profile cannot keep the last book visible");
        });

    [Fact]
    public void GeneratedProfileRegistersReloadsThroughActiveLoaderAndPublishesActualComboBeforeDisciplineReview() =>
      ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "mhd-generated-book-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var documentToken = new object();
            var drawing = Path.Combine(dir, "SYNTHETIC-generated-book.dwg");
            var identity = SavedDrawingPathPolicy.Evaluate(1, drawing, drawing);
            var workflow = (SectionsWorkflowService)RuntimeHelpers.GetUninitializedObject(typeof(SectionsWorkflowService));
            var seed = ActiveProjectProfileService.LoadForCapturedDrawingIdentity(documentToken, identity, workflow);
            seed.IsGeneratedForDrawing.Should().BeTrue();
            var profile = seed.Profile!;
            var serialized = JsonSerializer.Serialize(profile, SectionsWorkflowService.Json);
            var target = Path.Combine(dir, "project", "project-profile.yaml");
            var cas = ProjectProfileWriter.CaptureExpectedGeneratedState(profile, ArtifactHash.Sha256OfText(serialized), target);
            var book = new MiniXlsx.Workbook { SheetName = "TEST-ONLY" };
            book.Rows.Add(new MiniXlsx.OutRow(1).Text("A", "קוד", 0).Text("B", "תיאור", 0).Text("C", "יחידה", 0).Text("D", "מחיר", 0));
            book.Rows.Add(new MiniXlsx.OutRow(2).Text("A", "51.01.0250", 0).Text("B", "TEST-ONLY", 0).Text("C", "מטר", 0).Text("D", "95", 0));
            var input = Path.Combine(dir, "test-only.xlsx"); MiniXlsx.Write(book, input);
            var result = new EstimateWorkflowService().RegisterPriceBook(profile, input, "TEST-ONLY approver", target, cas,
                makeActive: true, expectedInspectionHash: ArtifactHash.Sha256OfFile(input));
            var reopened = ProjectProfileLoader.LoadFromFile(target); reopened.IsUsable.Should().BeTrue();
            PriceBookRegistry.Active(reopened.Profile!)!.Id.Should().Be(result.Entry.Id);
            // The test redirects only the write target to TEMP and binds that exact local profile through the real
            // session selector. It does not write/clean engineer runtime profiles, or claim native default discovery.
            ExistingProjectProfileSelection.Bind(documentToken, drawing, ExistingProjectProfileSelection.Inspect(target), null);
            ActiveProjectProfileService.ActiveLoadResult Reload() =>
                ActiveProjectProfileService.LoadForCapturedDrawingIdentity(documentToken, identity, workflow);
            var combo = new System.Windows.Controls.ComboBox { DisplayMemberPath = "Label" };
            CivilDeliveryControl.PublishPriceBookProfile(reopened, target, () =>
            {
                var actual = Reload(); actual.Profile.Should().NotBeSameAs(reopened.Profile);
                return (actual.Profile, actual.ProfileHash, actual.ProfileWriteTarget);
            }, combo);
            combo.SelectedItem.Should().NotBeNull(); combo.Items.Count.Should().Be(1);
            combo.SelectedItem!.GetType().GetProperty("Id")!.GetValue(combo.SelectedItem).Should().Be(result.Entry.Id);
            var beforeStart = Reload();
            EstimateProjectStartService.NeedsStart(beforeStart.Profile, beforeStart.ProfileWriteState).Should().BeTrue();
            var bytes = File.ReadAllBytes(target);
            EstimateProjectStartService.Save(new EstimateWorkflowService(), beforeStart.Profile!, beforeStart.ProfileWriteState!, null).Should().BeNull();
            File.ReadAllBytes(target).Should().Equal(bytes);
            EstimateProjectStartService.Save(new EstimateWorkflowService(), beforeStart.Profile!, beforeStart.ProfileWriteState!,
                new("SYNTHETIC landscape", "TEST-ONLY", true, "landscape"));
            var completed = Reload(); completed.Profile!.Estimate.Discipline.Should().Be("landscape");
            PriceBookRegistry.Active(completed.Profile)!.Id.Should().Be(result.Entry.Id);
            PriceBookRegistry.Active(completed.Profile)!.FileHash.Should().Be(result.Entry.FileHash);
            EstimateProjectStartService.NeedsStart(completed.Profile, completed.ProfileWriteState).Should().BeFalse();
            Action stale = () => CivilDeliveryControl.PublishPriceBookProfile(reopened, target,
                () => (seed.Profile, seed.ProfileHash, target), combo);
            stale.Should().Throw<InvalidOperationException>("a different/stale active reload cannot publish durable success");
        }
        finally { Directory.Delete(dir, true); }
    });
}
