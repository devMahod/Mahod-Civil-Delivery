using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Source contract of the BoQ-rules lane in the plugin: the bounded segment collector (read-only, in the scan
    /// transaction, before nested transformation), the palette action beside the measurement export, and the export
    /// service's freshness / published-evidence / review-required boundaries. Code shape only; no Autodesk member runs.
    /// </summary>
    public sealed class BoqRulesV2SourceContractTests
    {
        private static string PluginRoot => typeof(BoqRulesV2SourceContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;

        private static string Source(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { PluginRoot, "CivilDelivery" }.Concat(parts).ToArray()));

        [Fact]
        public void SegmentEvidenceIsReadOnlyBoundedAndAttachedWithTheOtherCadEvidence()
        {
            var reader = Source("Estimate", "QuantitySegmentEvidenceReader.cs");
            reader.Should().NotContain("OpenMode.ForWrite")
                .And.NotContain("UpgradeOpen")
                .And.NotContain("StartTransaction")
                .And.NotContain("DeliveryFinding")
                .And.Contain("QuantityGeometryEvidence.MaxVertices")
                .And.Contain("QuantityGeometryEvidence.OverLimit(")
                .And.Contain("\"unavailable:\" + ex.GetType().Name");
            Regex.Matches(reader, @"tr\.GetObject\([^)]*\)").Cast<Match>()
                .Should().OnlyContain(match => match.Value.Contains("OpenMode.ForRead"));

            // Appended inside the raw CAD metadata read, so it rides the existing AppendEvidence call that runs
            // before any nested transformation and never touches the rule key.
            var metadata = Source("Estimate", "QuantityCadMetadataReader.cs");
            var call = metadata.IndexOf("QuantitySegmentEvidenceReader.Read(entity, tr, values,", StringComparison.Ordinal);
            // b24 (Codex 12:04 A): arc density uses the host's resolved physical factor, for a host entity only.
            metadata.Should().Contain("hostEntity && host!.Units.IsSupported ? host.Units.LinearToMetres : null")
                .And.Contain("var hostEntity = host != null && entity.Database == host.Database;");
            var ret = metadata.IndexOf("return values;", StringComparison.Ordinal);
            call.Should().BeGreaterThan(0).And.BeLessThan(ret);
            metadata.Should().NotContain("OpenMode.ForWrite");
        }

        [Fact]
        public void TheBoqRulesActionSitsUnderTheScanAndIsGatedLikeTheMeasurementExport()
        {
            var tree = XDocument.Parse(Source("UI", "CivilDeliveryControl.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var scan = tree.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "BtnStartEstimateGuided");
            var boq = tree.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "BtnExportBoqRules");
            boq.Name.LocalName.Should().Be("Button");
            // One click after the scan, in the scan's own column: no extra palette row, so the quantities grid keeps
            // its minimum height in a 540 px palette (QuantityPaletteLayoutTests).
            boq.Parent.Should().BeSameAs(scan.Parent, "the rules BoQ sits directly under the scan that feeds it");
            ((string?)boq.Attribute("Click")).Should().Be("OnExportBoqRules");
            ((string?)boq.Attribute("Content")).Should().Contain("כתב כמויות");
            ((string?)boq.Attribute("ToolTip")).Should().Contain("לפי כללי הפרויקט")
                // Audit Z4: the rules export prices from the ruleset's own book; the palette's «מחירון פעיל» does not
                // reach it (EstimateWorkflowService.BoqRules → BoqRuleset.LoadEmbedded6422), and the button says so.
                .And.Contain("המחירים לפי המחירון שמוגדר בכללי הפרויקט")
                .And.Contain("«מחירון פעיל» שלמטה אינו משפיע על קובץ זה");
            ((string?)boq.Attribute("IsEnabled")).Should().Be("False");
            boq.Ancestors().Should().NotContain(e => e.Name.LocalName == "Expander");

            var ui = Source("UI", "CivilDeliveryControl.BoqRules.cs");
            ui.Should().Contain("scanFresh && _scan is { Records.Count: > 0 }")
                .And.Contain("_estimate.ExportBoqRules(doc, capturedScan, selectedCorridor)")
                .And.Contain("RunBusy(")
                .And.Contain("UseShellExecute = true")
                // Audit Z26: the "לפתוח ב-Excel?" question is right-to-left like the other Hebrew messages — b23: the
                // path message (right-to-left text, the path in its own left-to-right field).
                .And.Contain("RtlMessageBox.ShowPath(\"נוצר כתב כמויות לפי כללים:\", createdPath,");
            Source("UI", "CivilDeliveryControl.xaml.cs").Should().Contain("RefreshBoqRulesExport(profileUsable, estimateScanFresh);");
        }

        [Fact]
        public void TheExportKeepsFreshnessPublishedEvidenceAndReviewRequiredStatus()
        {
            var service = Source("Estimate", "EstimateWorkflowService.BoqRules.cs");
            service.Should().Contain("RequireFresh(doc, scan, operation);")
                .And.Contain("RequirePublishedScanEvidence(scan)")
                .And.Contain("SameProof(before, after)")
                .And.Contain("BoqRuleset.LoadEmbedded6422()")
                // L05: the export and the proposals' project-rule context share one preparation (one resolution, one run).
                .And.Contain("var plan = PrepareBoqPlan(scan, rules, corridor.Input == null ? corridor.Notes : null);")
                .And.Contain("SectionsWorkflowService.PersistEvidenceBundle(")
                .And.Contain("DeliveryStatus.ReviewRequired")
                .And.Contain("\"boq-rules\"")
                .And.NotContain("SaveApprovedMappings")
                .And.NotContain("ProjectProfileWriter");
            service.IndexOf("RequireFresh(doc, scan, operation);", StringComparison.Ordinal)
                .Should().BeLessThan(service.IndexOf("BoqRulesWorkbookWriter.Write(", StringComparison.Ordinal));
            Source("Estimate", "EstimateWorkflowService.ProjectRules.cs").Should()
                .Contain("var observed = BoqRulesSourceResolver.ResolveObserved(rules, ActiveScanOf(scan), SectionsWorkflowService.RunsRoot);")
                .And.Contain("input.Warnings.AddRange(observed.Resolution.Notes);")
                .And.Contain("if (warningsBeforeRun != null) input.Warnings.AddRange(warningsBeforeRun);")
                .And.Contain("return new BoqPlan(rules, observed, input, BoqRulesEngine.Run(rules, input));")
                .And.NotContain("ProjectProfileWriter")
                .And.NotContain("SaveApprovedMappings");
        }

        [Fact]
        public void TheCollectorPublishesTheHostTransformAndTheDefinitionSignatureReadOnly()
        {
            // Rules 2.8 (BOQ-N1): the approved-footprint rule needs the full INSERT transform and the definition signature of a
            // host model-space block — read in the scan transaction, one signature per definition, never opened for write.
            var service = Source("Estimate", "CivilQuantityExtractionService.cs");
            service.Should().Contain("measurement.Parameters[QuantityGeometryEvidence.InsertScaleKey] = Triple(scale.X, scale.Y, scale.Z);")
                .And.Contain("measurement.Parameters[QuantityGeometryEvidence.InsertNormalKey] = Triple(normal.X, normal.Y, normal.Z);")
                .And.Contain("definitionSignatures[countReference.BlockTableRecord] = signature = BlockDefinitionSignature(countReference.BlockTableRecord, tr);")
                // Codex 01:23: widths reach the descriptor, so a wide polyline leaves the digest unproven (never ignored).
                .And.Contain("BlockDefinitionDigest.LwPolyline(pl.Closed, pl.Elevation, V(pl.Normal), pl.ConstantWidth,")
                .And.Contain("pl.GetStartWidthAt(i), pl.GetEndWidthAt(i)");
            var rotationGate = service.IndexOf("if (!transformMeasurement && transform.IsEqualTo(Matrix3d.Identity) && double.IsFinite(countReference.Rotation))",
                StringComparison.Ordinal);
            rotationGate.Should().BeGreaterThan(0).And.BeLessThan(service.IndexOf("QuantityGeometryEvidence.InsertScaleKey", StringComparison.Ordinal),
                "only a block placed directly in the host model space carries a host transform");
            var helper = service[service.IndexOf("internal static string? BlockDefinitionSignature(", StringComparison.Ordinal)..];
            helper[..helper.IndexOf("catch (InvalidOperationException) { return null; }", StringComparison.Ordinal)]
                .Should().NotContain("OpenMode.ForWrite").And.NotContain("UpgradeOpen").And.Contain("OpenMode.ForRead");
            // Under the insert-point prefix: geometry keys, never summarized and never part of a saved decision's scope.
            foreach (var key in new[] { MahodAI.CivilDelivery.Estimate.QuantityGeometryEvidence.InsertScaleKey,
                         MahodAI.CivilDelivery.Estimate.QuantityGeometryEvidence.InsertNormalKey,
                         MahodAI.CivilDelivery.Estimate.QuantityGeometryEvidence.InsertBlockSignatureKey })
                MahodAI.CivilDelivery.Estimate.QuantityGeometryEvidence.IsGeometryKey(key).Should().BeTrue(key);
        }

        [Fact]
        public void TheOpenInExcelQuestionNamesTheCorridorChapters()
        {
            // Live b2 (30.09): the palette notice was not visible on the estimate tab, so the only message the user
            // reads after the export is this question — it carries the corridor line (in the bill, or why not).
            var ui = Source("UI", "CivilDeliveryControl.BoqRules.cs");
            ui.Should().Contain("corridorSummary = written.Corridor;")
                .And.Contain("$\"{corridorSummary}\\n\\nלפתוח ב-Excel?\"",
                    "the corridor line stays in the question itself (in the bill, or why not)")
                .And.Contain("System.IO.Path.GetFileName(createdPath)) == MessageBoxResult.Yes)",
                    "b14 live 17:31: the file name is shown for recognition; b23: the full path is in its own " +
                    "left-to-right field instead of a message-box row that wrapped at its separators");
        }
    }
}
