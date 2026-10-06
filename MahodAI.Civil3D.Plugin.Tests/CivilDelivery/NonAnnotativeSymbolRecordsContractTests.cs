using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Live 06/09 16:46 (1.2.39): the first APPLY of a Ready section created its sample
    /// line and section view, then the decoration rolled everything back with the native
    /// "eNotImplementedYet". Layer and linetype records carry no annotative protocol
    /// extension, so setting Annotative on them throws; text styles, blocks and text
    /// entities do support it. These contracts pin the split and the stack logging that
    /// makes the next native failure locatable from the stage log alone.
    /// </summary>
    public class NonAnnotativeSymbolRecordsContractTests
    {
        private static string PluginSourceDir =>
            typeof(NonAnnotativeSymbolRecordsContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Service(string name) => File.ReadAllText(Path.Combine(
            PluginSourceDir, "CivilDelivery", "Sections", "Services", name));

        private static string Between(string source, string start, string end)
        {
            var a = source.IndexOf(start, StringComparison.Ordinal);
            a.Should().BeGreaterThan(-1, start);
            var b = source.IndexOf(end, a, StringComparison.Ordinal);
            b.Should().BeGreaterThan(a, end);
            return source[a..b];
        }

        [Fact]
        public void LayerAndLinetypeRecords_AreNeverAssignedAnnotative()
        {
            var decoration = Service("SectionDecorationService.cs");
            var layer = Between(decoration, "private static void EnsureLayer(", "/// Hebrew labels need a TrueType font");
            layer.Should().NotContain("Annotative = AnnotativeStates", "a layer has no annotative protocol extension")
                .And.Contain("rec.LineWeight = LineWeight.ByLineWeightDefault;")
                .And.Contain("viewport.ThawLayersInViewport");
            var linetype = Between(decoration, "private static void WriteLinetypeContract(",
                "internal static SectionAnnotationResourceContracts.LinetypeState ReadLinetypeState(");
            linetype.Should().NotContain("Annotative = AnnotativeStates", "a linetype has no annotative protocol extension")
                .And.Contain("rec.IsScaledToFit = false;")
                .And.Contain("rec.PatternLength = spec.PatternLength;");
            // Reading stays: the contract still rejects a record that reports True.
            decoration.Should().Contain("rec.Annotative.ToString(), rec.PatternLength, elements);")
                .And.Contain("linetype?.Name, rec.LineWeight.ToString(), rec.Annotative.ToString()");
            // Annotative-capable objects keep their explicit False.
            Between(decoration, "private static void EnsureTextStyle(", "failed Arial TrueType read-back")
                .Should().Contain("rec.Annotative = AnnotativeStates.False;");
            Service("SectionVehicleBlockService.cs").Should().Contain("block.Annotative = AnnotativeStates.False;");
        }

        [Fact]
        public void TransparencyReads_NeverTouchAlphaOrClearOnAByLayerValue()
        {
            // Live 07/09 07:12 (1.2.40): the decoration reached the annotation fingerprint
            // for the first time and Transparency.IsClear threw eInvalidKey on a ByLayer
            // value. Alpha/IsClear/IsSolid are read only behind an IsByAlpha check.
            var registry = Service("SectionAnnotationRegistry.cs");
            registry.Should().Contain("internal static string? TransparencyText(Autodesk.AutoCAD.Colors.Transparency value) =>")
                .And.Contain("value.IsByAlpha ? \"ByAlpha:\" + value.Alpha.ToString(CultureInfo.InvariantCulture)")
                .And.Contain("if (!visible || (value.IsByAlpha && value.Alpha < byte.MaxValue))")
                .And.NotContain("value.IsClear").And.NotContain("value.IsSolid");
            Service("SectionVehicleBlockService.cs")
                .Should().Contain("SectionProjectionAnnotationSemantics.TransparencyText(value) ??")
                .And.NotContain("value.IsClear").And.NotContain("value.IsSolid");
            foreach (var name in new[] { "SectionDecorationService.cs", "SectionVerifyService.cs", "SectionApplyService.cs" })
                Service(name).Should().NotContain(".IsClear").And.NotContain(".IsSolid", name);
        }

        [Fact]
        public void ApplyCatchSites_LogTheFullExceptionWithItsStack()
        {
            var apply = Service("SectionApplyService.cs");
            apply.Should().Contain("_log?.Info(\"apply.decorate skipped: \" + ex);")
                .And.Contain("_log?.Info(\"apply.arrange_views failed: \" + ex);")
                .And.Contain("_log?.Info(\"apply.final_visual_layout failed: \" + ex);")
                .And.NotContain("skipped: \" + ex.Message")
                .And.NotContain("failed: \" + ex.Message");
        }
    }
}
