using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Host-free wiring contracts for the 1.2.27 resource contracts (Codex review of
    /// 1.2.26): linetype, layer, text style and block definition are validated through
    /// the Core validators on both APPLY and VERIFY, an evidence write failure is shown
    /// red and blocks VERIFY, legacy group metadata is never rewritten in selected scope,
    /// and the source manifest excludes scratch while including global.json.
    /// The decision logic itself is simulated in MahodAI.Core.Tests.
    /// </summary>
    public class SectionAnnotationResourceWiringContractTests
    {
        private static string PluginSourceDir =>
            typeof(SectionAnnotationResourceWiringContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Service(string name) => File.ReadAllText(Path.Combine(
            PluginSourceDir, "CivilDelivery", "Sections", "Services", name));

        private static string Ui() => File.ReadAllText(Path.Combine(
            PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));

        private static string Between(string source, string start, string end)
        {
            var from = source.IndexOf(start, StringComparison.Ordinal);
            from.Should().BeGreaterThanOrEqualTo(0, $"missing anchor {start}");
            var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            to.Should().BeGreaterThan(from, $"missing end anchor {end}");
            return source.Substring(from, to - from);
        }

        [Fact]
        public void Linetypes_AreProvenPresent_AndNeverFallBackSilently()
        {
            var decoration = Service("SectionDecorationService.cs");

            var ensure = Between(decoration,
                "private static void EnsureLinetypes(Transaction tr, Database db, bool allowModify)",
                "private static void WriteLinetypeContract(");
            ensure.Should().Contain("SectionAnnotationResourceContracts.RequiredLinetypeSpecs")
                .And.Contain("SectionAnnotationResourceContracts.ValidateLinetype(")
                .And.Contain("ReadLinetypeState(rec)")
                .And.Contain("SharedResourceLogic.Decide(true, violations.Count == 0")
                .And.Contain("SharedResourceLogic.Mode.CreateOnlyNeverModify")
                .And.Contain("WriteLinetypeContract(rec, spec)")
                .And.Contain("failed semantic read-back");
            decoration.Should().Contain("EnsureLinetypes(tr, db, allowModify);")
                .And.NotContain("EnsureLinetypes(db);");

            var writer = Between(decoration, "private static void WriteLinetypeContract(",
                "internal static SectionAnnotationResourceContracts.LinetypeState ReadLinetypeState(");
            writer.Should().Contain("rec.AsciiDescription = spec.Description")
                .And.Contain("rec.PatternLength = spec.PatternLength")
                .And.Contain("rec.SetDashLengthAt(i, spec.DashLengths[i])")
                .And.Contain("rec.SetShapeScaleAt(i, 1.0)")
                .And.NotContain("rec.SetTextAt(")
                .And.NotContain("rec.SetShapeStyleAt(")
                .And.NotContain("rec.SetShapeNumberAt(");

            var invalid = Between(decoration, "internal static IReadOnlyList<string> InvalidLinetypes(",
                "internal static SectionAnnotationResourceContracts.LayerState ReadLayerState(");
            invalid.Should().Contain("SectionAnnotationResourceContracts.RequiredLinetypeSpecs")
                .And.Contain("SectionAnnotationResourceContracts.ValidateLinetype(")
                .And.Contain("ReadLinetypeState(rec)")
                .And.Contain("unreadable:");

            var newLine = Between(decoration, "private static Line NewLine(", "private static void EnsureLinetypes(");
            newLine.Should().Contain("SectionAnnotationResourceContracts.LinetypeApplied(linetype, line.Linetype)")
                .And.Contain("EffectiveAnnotationLinetypeScale(db)")
                .And.Contain("SectionFindingCodes.AnnotationLinetypeNotApplied")
                .And.NotContain("catch { }")
                .And.NotContain("catch {}");
        }

        [Fact]
        public void LayerAndTextStyle_UseTheFullCoreContract_OnApply()
        {
            var decoration = Service("SectionDecorationService.cs");

            var layer = Between(decoration, "private static void EnsureLayer(", "private static void EnsureTextStyle(");
            layer.Should().Contain("SectionAnnotationResourceContracts.ValidateLayer(")
                .And.Contain("ReadLayerState(tr, db, rec)")
                .And.Contain("var compliant = violations.Count == 0;")
                .And.Contain("rec.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci,")
                .And.Contain("SectionAnnotationResourceContracts.AnnotationLayerColorIndex")
                .And.Contain("rec.Transparency = new Transparency(SectionAnnotationResourceContracts.OpaqueAlpha)")
                .And.Contain("rec.ViewportVisibilityDefault = false;")
                .And.Contain("if (rec.HasOverrides) rec.RemoveAllOverrides();")
                .And.Contain("rec.LinetypeObjectId = db.ContinuousLinetype;")
                .And.Contain("rec.LineWeight = LineWeight.ByLineWeightDefault;")
                .And.Contain("viewport.ThawLayersInViewport")
                .And.Contain("var readBack = SectionAnnotationResourceContracts.ValidateLayer(")
                .And.Contain("if (readBack.Count > 0)");
            // Only a pure-ACI colour counts; a true colour reports -1 and fails the contract.
            var readLayer = Between(decoration, "internal static SectionAnnotationResourceContracts.LayerState ReadLayerState(",
                "private static (bool Complete, int FrozenCount) InspectPaperViewportVisibility(");
            readLayer.Should().Contain("color.ColorMethod == ColorMethod.ByAci ? color.ColorIndex : (short)-1")
                .And.Contain("InspectPaperViewportVisibility(tr, db, rec.ObjectId)")
                .And.Contain("transparency.IsByAlpha, transparency.Alpha")
                .And.Contain("linetype?.Name, rec.LineWeight.ToString(), rec.Annotative.ToString()");

            var text = Between(decoration, "private static void EnsureTextStyle(", "failed Arial TrueType read-back");
            text.Should().Contain("SectionAnnotationResourceContracts.ValidateTextStyle(ReadTextStyleState(rec))")
                .And.Contain("rec.XScale = 1.0;")
                .And.Contain("rec.ObliquingAngle = 0.0;")
                .And.Contain("rec.IsVertical = false;")
                .And.Contain("rec.FlagBits = 0;")
                .And.Contain("rec.TextSize = 0.0;")
                .And.Contain("rec.Annotative = AnnotativeStates.False;")
                .And.Contain("var readBack = SectionAnnotationResourceContracts.ValidateTextStyle(ReadTextStyleState(rec));");
            var readText = Between(decoration, "internal static SectionAnnotationResourceContracts.TextStyleState ReadTextStyleState(",
                "private static void EnsureLayer(");
            readText.Should().Contain("rec.XScale, rec.ObliquingAngle, rec.IsVertical, rec.FlagBits")
                .And.Contain("rec.TextSize, font.Bold, font.Italic, font.CharacterSet, font.PitchAndFamily")
                .And.Contain("rec.Annotative.ToString(), rec.PaperOrientation.ToString()");
        }

        [Fact]
        public void Verify_ReReadsEveryResource_ThroughTheSameContract()
        {
            var verify = Service("SectionVerifyService.cs");

            verify.Should().Contain("SectionAnnotationResourceContracts.ValidateLayer(layerState)")
                .And.Contain("SectionAnnotationResourceContracts.ValidateTextStyle(textStyleState)")
                .And.Contain("layerReadable && layerViolations.Count == 0")
                .And.Contain("textStyleReadable && textStyleViolations.Count == 0")
                .And.Contain("unfrozen globally/in every paper viewport; no viewport overrides")
                .And.Contain("Continuous; default lineweight")
                .And.Contain("fixed-height=0; non-annotative")
                .And.Contain("Check(\"annotation_linetypes_semantics_exact\"")
                .And.Contain("linetypesReadable && invalidLinetypes.Count == 0")
                .And.NotContain("\"exists; on=true; unfrozen=true; plottable=true\"");

            // The readers delegate to the decoration service so APPLY and VERIFY cannot drift.
            verify.Should().Contain("state = SectionDecorationService.ReadLayerState(tr, db, layer);")
                .And.Contain("state = SectionDecorationService.ReadTextStyleState(style);")
                .And.Contain("invalid = SectionDecorationService.InvalidLinetypes(tr, db);");
            // An unreadable resource is a failure with a reason, never a silent pass.
            Between(verify, "private static bool TryReadAnnotationLayerState(", "private static bool TryReadAnnotationTextStyle(")
                .Should().Contain("error = ex.Message;").And.Contain("{ error = \"missing\"; return false; }");
        }

        [Fact]
        public void BlockDefinitionFingerprint_IncludesHeader_AndAcceptsOlderToolProvenance()
        {
            var vehicle = Service("SectionVehicleBlockService.cs");
            var fingerprint = Between(vehicle, "internal static string GeometryFingerprint(",
                "private static IReadOnlyList<string> EntitySignatures(");
            fingerprint.Should().Contain("new BlockDefinitionFingerprintLogic.Header(")
                .And.Contain("block.Origin.X, block.Origin.Y, block.Origin.Z")
                .And.Contain("block.Units.ToString(), block.BlockScaling.ToString(), block.Explodable")
                .And.Contain("block.Annotative.ToString(), block.PaperOrientation.ToString()")
                .And.Contain("BlockDefinitionFingerprintLogic.Compose(header, signatures)")
                .And.Contain("BlockDefinitionFingerprintLogic.ComposeEntities(EntitySignatures(tr, block))")
                .And.NotContain("SHA256.HashData");

            var signature = Between(vehicle, "private static string GeometrySignature(", "private static void AddPoint(");
            signature.Should().Contain("\"elevation=\" + F(polyline.Elevation)")
                .And.Contain("polyline.Normal.X")
                .And.Contain("solid.Normal.X");

            // A block imported by 1.2.9–1.2.26 carries a v1 fingerprint comment. It is still
            // tool provenance (geometry is re-proven with the current algorithm); batch
            // refreshes the comment, selected scope blocks (existing 1.2.26 policy).
            vehicle.Should().Contain("BlockDefinitionFingerprintLogic.IsToolProvenance(")
                .And.Contain("existing.Comments, legacy)");
            Service("SectionTrafficDirectionArrowService.cs")
                .Should().Contain("BlockDefinitionFingerprintLogic.IsToolProvenance(")
                .And.Contain("existing.Comments, legacy)");
        }

        [Fact]
        public void SelectedScope_NeverRewritesLegacyGroupMetadata()
        {
            var apply = Service("SectionApplyService.cs");
            apply.Should().Contain("allowMetadataRewrite: batchGroupScope");
            var legacy = Between(apply, "if (!legacyGroupId.IsNull)", "return legacyGroupId;");
            legacy.Should().Contain("if (!allowMetadataRewrite)")
                .And.Contain("SectionFindingCodes.SharedResourceChangeRequired");
            legacy.IndexOf("if (!allowMetadataRewrite)", StringComparison.Ordinal)
                .Should().BeLessThan(legacy.IndexOf("SectionOwnershipService.Write(tr, legacy", StringComparison.Ordinal),
                    "the guard must precede the metadata rewrite");
        }

        [Fact]
        public void EvidenceWriteFailure_IsRedBlocking_AndNeverShowsACheckmark()
        {
            var ui = Ui();

            ui.Should().Contain("private void SetBlockingStatus(string s)")
                .And.Contain("System.Windows.Media.Brushes.Red")
                .And.Contain("SectionFindingCodes.EvidenceWriteFailed")
                .And.Contain("ReportEvidenceWriteFailure(\"החלה\", _apply.Findings.Concat(")
                .And.Contain("ReportEvidenceWriteFailure(\"החלת חתך נבחר\", _apply.Findings.Concat(")
                .And.Contain("_apply.Records.SelectMany(record => record.Findings)")
                .And.Contain("ReportEvidenceWriteFailure(\"אימות\", verify.Findings);")
                .And.Contain("if (ReportEvidenceWriteFailure(\"אימות חתך נבחר\", verify.Findings)) return;");

            var report = Between(ui, "private bool ReportEvidenceWriteFailure(", "return true;");
            report.Should().Contain("SetBlockingStatus(")
                .And.Contain("MessageBoxImage.Error")
                .And.Contain("Log(");

            // The ✓ summaries are reachable only when the evidence write succeeded.
            var batchSummary = Between(ui, "var verifyEvidenceLost = EvidenceWriteFailed(verify.Findings);", "בדיקות עברו ✓");
            batchSummary.Should().Contain("_verifySummary = verifyEvidenceLost");
            var selectedSummary = Between(ui, "var selectedEvidenceLost = EvidenceWriteFailed(verify.Findings);", "בדיקות עברו ✓");
            selectedSummary.Should().Contain("_verifySummary = selectedEvidenceLost");

            // VERIFY (batch and selected) is gated off after an APPLY whose evidence was lost.
            var gates = Between(ui,
                "var applyEvidenceLost = EvidenceWriteFailed(_apply?.Findings) ||",
                "BtnApplySelected.IsEnabled");
            gates.Should().Contain("EvidenceWriteFailed(_apply?.Records.SelectMany(record => record.Findings))")
                .And.Contain("var authoritativeApply = IsAuthoritativeApply(_apply);");
            gates.Should().Contain("BtnVerify.IsEnabled = gate.CanVerify && !applyEvidenceLost &&")
                .And.Contain("BtnVerifySelected.IsEnabled = profileUsable && _plan != null &&")
                .And.Contain("!applyEvidenceLost &&")
                .And.Contain("_evidenceBlockingStatus == null")
                .And.Contain("selectedVerify.Action == PlanAction.Unchanged")
                .And.Contain("authoritativeApply && _apply!.Records.Any");

            // The enabled command is a new source/output validation, not permission
            // to inherit old green on a changed revision or an UNCHANGED PLAN label.
            var workflow = Service("SectionsWorkflowService.cs");
            workflow.Should().Contain("SectionVerificationRecoveryService.RecoverSelected(doc, current, recordId)")
                .And.Contain("_verifyService.VerifyRecoveredSelected(");
            Service("SectionVerificationRecoveryService.cs").Should()
                .Contain("SectionsWorkflowService.RequireApplyEvidence(applied)")
                .And.Contain("SectionsWorkflowService.RequirePlanEvidence(producer)")
                .And.Contain("SectionVerificationRecoveryPolicy.SurfaceMismatch");

            // The service boundary is authoritative too: callers that bypass the
            // palette cannot verify an APPLY with a record-level error/stale status.
            var verify = Service("SectionVerifyService.cs");
            verify.Should().Contain("applied.Records.SelectMany(record => record.Findings)")
                .And.Contain("record.Status is not (DeliveryStatus.Applied or DeliveryStatus.Verified)")
                .And.Contain("record.ActionTaken == PlanAction.Unchanged")
                .And.Contain("VERIFY סורב — תוצאת APPLY אינה ראיה סמכותית");
        }

        [Fact]
        public void SourceManifest_FreezesBinarySource_WithoutCreatingAPostLiveCycle()
        {
            var script = File.ReadAllText(Path.Combine(PluginSourceDir, "..", "release", "build-source-manifest.py"));
            script.Should().Contain("'global.json'")
                .And.Contain("'tmp'")
                .And.Contain("part.startswith('stage_')")
                .And.Contain("rel == 'release/release.json'")
                .And.Contain("rel.startswith('nataly/guide/')")
                .And.Contain("rel.startswith('runtime-gate/') and f.suffix.lower() == '.md'")
                .And.Contain("'canonical_employee_package': release['canonical_employee_package']",
                    "immutable package identity stays frozen even though live status and guide evidence remain mutable");
        }
    }
}
