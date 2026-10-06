using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// The CL drawing is a separate file whose layer name changes from project to
    /// project. 1.0.0 shipped with the 6422 profile pointing at one machine's absolute
    /// path, so on the engineer's machine every candidate resolved to nothing and the
    /// panel could only say "none of the configured CL files was found" — with no way
    /// to point it anywhere else. These tests lock the way out of that.
    /// </summary>
    public class ClSourceSelectionTests
    {
        private static string PluginSourceDir =>
            typeof(ClSourceSelectionTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { PluginSourceDir }.Concat(parts).ToArray()));

        // ------------------------------------------------------------- stored path

        [Fact]
        public void StoredPath_KeepsOnlyTheNameWhenTheClSitsBesideTheModel()
        {
            var stored = ClSourceSelection.StoredPath(@"P:\proj\6422\CL.dwg", @"P:\proj\6422\6422-CIVIL-WEST.dwg");

            stored.Should().Be("CL.dwg",
                "a name resolves next to whatever model is open — an absolute path only ever " +
                "resolves on the machine that produced it");
        }

        [Fact]
        public void StoredPath_KeepsTheFullPathWhenTheClLivesElsewhere()
        {
            var stored = ClSourceSelection.StoredPath(@"D:\shared\CL.dwg", @"P:\proj\6422\model.dwg");

            stored.Should().Be("D:/shared/CL.dwg");
        }

        [Fact]
        public void StoredPath_UsesForwardSlashes_SoTheYamlNeedsNoEscaping()
        {
            ClSourceSelection.StoredPath(@"D:\a\b\CL.dwg", null)
                .Should().NotContain("\\");
        }

        [Fact]
        public void StoredPath_SurvivesAnUnsavedModel()
        {
            ClSourceSelection.StoredPath(@"D:\shared\CL.dwg", "")
                .Should().Be("D:/shared/CL.dwg");
            ClSourceSelection.StoredPath("", @"P:\proj\model.dwg")
                .Should().BeEmpty();
        }

        [Fact]
        public void StoredPath_IsCaseInsensitiveAboutTheFolder()
        {
            ClSourceSelection.StoredPath(@"P:\Proj\6422\CL.dwg", @"p:\proj\6422\model.dwg")
                .Should().Be("CL.dwg");
        }

        // -------------------------------------------------------------- host check

        [Fact]
        public void IsHostDrawing_RecognisesTheOpenDrawing()
        {
            ClSourceSelection.IsHostDrawing(@"P:\proj\model.dwg", @"P:\proj\model.dwg").Should().BeTrue();
            ClSourceSelection.IsHostDrawing(@"P:\proj\CL.dwg", @"P:\proj\model.dwg").Should().BeFalse();
            ClSourceSelection.IsHostDrawing(@"P:\proj\model.dwg", null).Should().BeFalse();
        }

        // ------------------------------------------------------------ merge policy

        [Fact]
        public void MergeSources_KeepsTheExternalClDrawingSetupUsedToDestroy()
        {
            var merged = ClSourceSelection.MergeSources(
                new[] { "CL.dwg", "D:/shared/CL.dwg" }, @"P:\proj\6422\model.dwg");

            merged.Should().Contain("CL.dwg");
            merged.Should().Contain("D:/shared/CL.dwg",
                "project setup must never silently drop the CL drawing the engineer chose");
            merged.Should().Contain("model.dwg",
                "CL lines drawn in the open model (or reached through an XREF) still count");
        }

        [Fact]
        public void MergeSources_DoesNotDuplicateTheHostDrawing()
        {
            var merged = ClSourceSelection.MergeSources(new[] { "model.dwg" }, @"P:\proj\model.dwg");

            merged.Should().ContainSingle().Which.Should().Be("model.dwg");
        }

        [Fact]
        public void MergeSources_IsCaseInsensitiveAndTrims()
        {
            var merged = ClSourceSelection.MergeSources(
                new[] { " CL.dwg ", "cl.dwg", "" }, null);

            merged.Should().ContainSingle().Which.Should().Be("CL.dwg");
        }

        [Fact]
        public void MergeSources_HandlesAnEmptyProfileAndAnUnsavedDrawing()
        {
            ClSourceSelection.MergeSources(null, null).Should().BeEmpty();
            ClSourceSelection.MergeSources(new List<string>(), @"P:\proj\model.dwg")
                .Should().ContainSingle().Which.Should().Be("model.dwg");
        }

        [Fact]
        public void Describe_ShowsFileAndLayerWithoutScramblingThemInRtl()
        {
            var text = ClSourceSelection.Describe(new[] { "D:/shared/CL.dwg" }, new[] { "GFC111" });

            text.Should().Contain("CL.dwg");
            text.Should().Contain("GFC111");
            text.Should().Contain("\u200E", "latin names inside Hebrew text need LRM or they read reversed");
        }

        // --------------------------------------------------- wiring (source level)

        [Fact]
        public void SetupNoLongerReplacesTheClSourceList()
        {
            var setup = Read("CivilDelivery", "Commands", "MhdSetupCommand.cs");

            setup.Should().Contain("ClSourceSelection.MergeSources",
                "replacing the list is what emptied the engineer's sections");
            setup.Should().NotContain("new List<string> { Path.GetFileName(scan.Drawing) }");
        }

        [Fact]
        public void ThePanelCanPointTheProjectAtAClDrawing()
        {
            var xaml = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml");
            var code = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");

            xaml.Should().Contain("BtnPickCl", "hand-editing YAML is not a way to configure a product");
            code.Should().Contain("private void OnPickCl");
            code.Should().Contain("CaptureReadySource(doc, \"CL source selection\")",
                "an unsaved Drawing1 must not persist a profile that leaks to a future drawing");
            code.Should().Contain("ScanClFile", "the picked file is scanned for real evidence");
            code.Should().Contain("ClSourceSelection.StoredPath");
            code.Should().Contain("ProjectProfileWriter.Save", "the choice must survive a restart");
        }

        [Fact]
        public void TheLayerIsChosenFromEvidence_NotFromItsName()
        {
            var scanner = Read("CivilDelivery", "Sections", "Services", "ProjectSetupScanner.cs");
            var picker = Read("CivilDelivery", "UI", "ClLayerPickerDialog.xaml.cs");

            scanner.Should().Contain("public ExternalClScan ScanClFile",
                "the CL drawing is neither open nor attached, so it must be scanned on its own");
            scanner.Should().Contain("CollectAlignments(hostCivilDoc, hostTr, log)",
                "crossing evidence comes from the alignments of the model the engineer has open");
            scanner.Should().Contain("layer name hints at section/CL (weak evidence only)");

            picker.Should().Contain("RawLayer",
                "the profile must receive the AutoCAD layer name, not the RTL display string");
            picker.Should().Contain("חיתוכים", "the engineer sees the evidence, not a bare score");
            picker.Should().NotContain("חוצים תוואי{crossed}",
                "crossings are counted per axis, so that phrasing claimed more lines than the layer holds");
            picker.Should().Contain("שכבה אחת", "\"ב-1 שכבות\" is not Hebrew");
        }
    }
}
