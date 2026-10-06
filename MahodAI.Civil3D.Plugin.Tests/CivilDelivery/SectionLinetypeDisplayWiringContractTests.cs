using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Host-free wiring contracts for the 1.2.29 fixes (Claude review of 1.2.28):
    /// the annotation linetype entity scale honours LTSCALE, MSLTSCALE and the
    /// annotation scale; APPLY records the display state and VERIFY re-reads it;
    /// batch layer normalization logs every thawed sheet viewport; the PLAN remedy
    /// names the versioned layers; the exclusions-only APPLY never promises a
    /// verification baseline; and the release-gate policy self-test completes on
    /// Windows PowerShell 5.1. The decision logic is simulated in MahodAI.Core.Tests.
    /// </summary>
    public class SectionLinetypeDisplayWiringContractTests
    {
        private static string PluginSourceDir =>
            typeof(SectionLinetypeDisplayWiringContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Service(string name) => File.ReadAllText(Path.Combine(
            PluginSourceDir, "CivilDelivery", "Sections", "Services", name));

        private static string Repo(params string[] parts) => File.ReadAllText(
            Path.Combine(new[] { PluginSourceDir, ".." }.Concat(parts).ToArray()));

        private static string Between(string source, string start, string end)
        {
            var from = source.IndexOf(start, StringComparison.Ordinal);
            from.Should().BeGreaterThanOrEqualTo(0, $"missing anchor {start}");
            var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            to.Should().BeGreaterThan(from, $"missing end anchor {end}");
            return source.Substring(from, to - from);
        }

        [Fact]
        public void EntityLinetypeScale_ComesFromTheCoreDisplayLogic_NotFromLtscaleAlone()
        {
            var decoration = Service("SectionDecorationService.cs");

            var scale = Between(decoration, "private static double EffectiveAnnotationLinetypeScale(Database db)",
                "internal static AnnotationLinetypeDisplayLogic.DrawingState ReadLinetypeDisplayState(");
            scale.Should().Contain("AnnotationLinetypeDisplayLogic.EntityScale(ReadLinetypeDisplayState(db))")
                .And.NotContain("1.0 / global");

            var reader = Between(decoration, "internal static AnnotationLinetypeDisplayLogic.DrawingState ReadLinetypeDisplayState(",
                "internal static string LinetypeDisplayContract(");
            reader.Should().Contain("db.Ltscale")
                .And.Contain("Convert.ToBoolean(db.MsLtScale)")
                .And.Contain("db.Cannoscale")
                .And.Contain("scale?.PaperUnits ?? double.NaN")
                .And.Contain("scale?.DrawingUnits ?? double.NaN")
                .And.Contain("scale?.Scale ?? double.NaN");

            decoration.Should().Contain("AnnotationLinetypeDisplayLogic.Describe(ReadLinetypeDisplayState(db))");
            // Every guide line and label still goes through the single scale function.
            Between(decoration, "private static Line NewLine(", "private static double EffectiveAnnotationLinetypeScale(")
                .Should().Contain("LinetypeScale = EffectiveAnnotationLinetypeScale(db)");
        }

        [Fact]
        public void Apply_RecordsTheDisplayState_AndVerify_RequiresItUnchanged()
        {
            Service("SectionDecorationService.cs")
                .Should().Contain("rec.LinetypeDisplayEvidence = LinetypeDisplayContract(db);");
            Repo("MahodAI.Civil3D.Plugin", "CivilDelivery", "Sections", "Contracts", "SectionApplyModels.cs")
                .Should().Contain("[JsonPropertyName(\"linetype_display_evidence\")]")
                .And.Contain("public string? LinetypeDisplayEvidence { get; set; }");

            var verify = Service("SectionVerifyService.cs");
            var check = Between(verify, "Check(\"annotation_linetype_display_exact\"", "var plannedCoverage");
            check.Should().Contain("applyRecord.LinetypeDisplayEvidence")
                .And.Contain("string.Equals(applyRecord.LinetypeDisplayEvidence, liveDisplay, StringComparison.Ordinal)")
                .And.Contain("!liveDisplay.EndsWith(\";entity_scale=(invalid)\"")
                .And.Contain("\"(missing)\"");
            Between(verify, "private static bool TryReadLinetypeDisplayContract(", "private static bool TryReadInvalidLinetypes(")
                .Should().Contain("SectionDecorationService.LinetypeDisplayContract(db)")
                .And.Contain("error = ex.Message;");
        }

        [Fact]
        public void BatchLayerNormalization_LogsEveryThawedSheetViewport()
        {
            var decoration = Service("SectionDecorationService.cs");
            decoration.Should().Contain("EnsureLayer(tr, db, AnnoLayer, allowModify, log);");
            var thaw = Between(decoration, "foreach (var viewport in PaperViewports(tr, db).Where(v =>", "var readBack = SectionAnnotationResourceContracts.ValidateLayer(");
            thaw.Should().Contain("apply.annotation_layer.thaw_viewport")
                .And.Contain("viewport={viewport.Handle}");
            thaw.IndexOf("log?.Info(", StringComparison.Ordinal)
                .Should().BeLessThan(thaw.IndexOf("viewport.UpgradeOpen();", StringComparison.Ordinal),
                    "the log line must precede the write");
        }

        [Fact]
        public void PlanInventoryRemedy_NamesTheVersionedAndLegacyLayers()
        {
            var plan = Service("SectionPlanService.cs");
            var remedy = Between(plan, "Code = SectionFindingCodes.AnnotationInventoryConflict,", "ProjectProfileId = profile.ProfileId,");
            remedy.Should().Contain("SectionAnnotationResourceContracts.AnnotationLayerName")
                .And.Contain("SectionAnnotationResourceContracts.LegacyAnnotationLayerName")
                .And.Contain("לשחזר את השרטוט מעותק נקי")
                .And.NotContain("שכבת MHD-SECT-ANNO ");
        }

        [Fact]
        public void ExclusionsOnlyApply_NeverPromisesAVerificationBaseline()
        {
            var ui = Repo("MahodAI.Civil3D.Plugin", "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");
            ui.Should().Contain("לפרסם את ראיות ההחרגה החתומות עבור")
                .And.Contain("\"פרסום ראיות החרגה\"")
                .And.Contain("ראיות ההחרגה פורסמו — השרטוט לא שונה")
                .And.NotContain("להכין בסיס אימות")
                .And.NotContain("בסיס האימות הוכן");
        }

        [Fact]
        public void ReleaseGatePolicySelfTest_RunsOnWindowsPowerShell51()
        {
            var script = Repo("installer", "Test-ReleaseGatePolicy.ps1");
            script.Should().Contain("(Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')");
            // Windows PowerShell's Set-Content -Encoding utf8 writes a BOM and Get-Content
            // decodes BOM-less files as ANSI: adversarial fixtures must be saved and
            // restored as BYTES, and every JSON text read must name UTF-8 explicitly.
            script.Should().NotContain("Get-Content -LiteralPath")
                .And.Contain("Bytes = [System.IO.File]::ReadAllBytes(")
                .And.Contain("[System.IO.File]::WriteAllBytes(")
                .And.NotMatchRegex(@"WriteAllText\([^)]*\$saved");
            Repo("release", "ReleaseGatePolicy.ps1").Should().NotContain("Get-Content -LiteralPath")
                .And.Contain("[System.IO.File]::ReadAllText(");
            // No unguarded PowerShell-7-only parameter may remain anywhere in the gate scripts.
            foreach (var line in script.Split('\n').Where(l =>
                         l.Contains("-DateKind", StringComparison.Ordinal) &&
                         !l.TrimStart().StartsWith("#", StringComparison.Ordinal)))
                line.Should().Contain("ConvertFrom-Json -DateKind String", "only the guarded branch may use it");
            script.Split('\n').Count(l => l.Contains("-DateKind", StringComparison.Ordinal) &&
                                          !l.TrimStart().StartsWith("#", StringComparison.Ordinal))
                .Should().Be(1);
        }
    }
}
