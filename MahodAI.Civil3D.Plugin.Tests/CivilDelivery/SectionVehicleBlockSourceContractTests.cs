using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public class SectionVehicleBlockSourceContractTests
    {
        private static string PluginSourceDir =>
            typeof(SectionVehicleBlockSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Service(string name) => File.ReadAllText(Path.Combine(
            PluginSourceDir, "CivilDelivery", "Sections", "Services", name));

        [Fact]
        public void OfficeBlocks_AreVerifiedImportedAndNormalized_NotReadFromDownloads()
        {
            var source = Service("SectionVehicleBlockService.cs");

            source.Should().Contain("GetManifestResourceStream")
                .And.Contain("SHA256.HashData")
                .And.Contain("ReadDwgFile")
                .And.Contain("targetDb.Insert")
                .And.Contain("DefinitionBounds")
                .And.Contain("ScaleFactors = placement.ScaleFactors")
                .And.Contain("SectionAnnotationPlacementLogic.TryCarPlacement")
                .And.Contain("TryOfficeBlockPhysicalHeight")
                .And.Contain("GeometryFingerprint")
                .And.Contain("EntityFingerprint")
                .And.Contain("existingEntityFingerprint, sourceEntityFingerprint")
                .And.Contain("changed after import")
                .And.Contain("var legacy = Provenance(asset)")
                .And.Contain("it was not touched")
                .And.Contain("Guid.NewGuid().ToString(\"N\")");
            source.Should().NotContain(@"C:\Users")
                .And.NotContain("Downloads")
                .And.NotContain("Materials\\blocks");

            source.Should().NotContain("groundElevation + spec.HeightM",
                "the approved block must retain its audited aspect ratio");
        }

        [Fact]
        public void OfficeBlockFingerprint_ProtectsLayerAndDisplayColorAsWellAsGeometry()
        {
            var source = Service("SectionVehicleBlockService.cs");

            source.Should().Contain("layer=")
                .And.Contain("color-index=")
                .And.Contain("color-method=")
                .And.Contain("linetype=")
                .And.Contain("linetype-scale=")
                .And.Contain("lineweight=")
                .And.Contain("transparency=")
                .And.Contain("visible=")
                .And.Contain("entity is Solid solid")
                .And.Contain("solid.GetPointAt(i)")
                .And.NotContain("Layer/color are intentionally excluded");
            Service("SectionAnnotationRegistry.cs")
                .Should().Contain("SectionVehicleBlockService.GeometryFingerprint(");
        }

        [Fact]
        public void OfficeBlockAttestation_SeparatesSourceEntitiesFromFinalTargetHeader()
        {
            var vehicle = Service("SectionVehicleBlockService.cs");
            var arrow = Service("SectionTrafficDirectionArrowService.cs");

            vehicle.Should().Contain("BlockDefinitionFingerprintLogic.ComposeEntities(EntitySignatures(tr, block))")
                .And.Contain("block.Annotative.ToString(), block.PaperOrientation.ToString()")
                .And.Contain("block.Annotative = AnnotativeStates.False;")
                .And.Contain("block.PaperOrientation != PaperOrientationStates.True")
                .And.Contain("if (!TargetHeaderMatches(block, scaling))")
                .And.NotContain("block.PaperOrientation =",
                    "PaperOrientation is read-only in the Autodesk API; an unrepairable True value must fail closed");

            var existingEntity = vehicle.IndexOf(
                "var existingEntityFingerprint = EntityFingerprintWithEvidence(tr, existing,", StringComparison.Ordinal);
            var existingHeader = vehicle.IndexOf(
                "if (!TargetHeaderMatches(existing, BlockScaling.Any))", StringComparison.Ordinal);
            var existingAttestation = vehicle.IndexOf(
                "var targetFingerprint = GeometryFingerprintWithEvidence(tr, existing,", StringComparison.Ordinal);
            existingEntity.Should().BeGreaterThan(0);
            existingHeader.Should().BeGreaterThan(existingEntity,
                "source-equivalent entities must be proven before shared target-header normalization");
            existingAttestation.Should().BeGreaterThan(existingHeader,
                "the provenance comment must attest the normalized final target definition");

            var importedNormalize = vehicle.IndexOf(
                "NormalizeTargetHeader(imported, BlockScaling.Any);", StringComparison.Ordinal);
            var importedEntity = vehicle.IndexOf(
                "var importedEntityFingerprint = EntityFingerprintWithEvidence(tr, imported,", StringComparison.Ordinal);
            var importedAttestation = vehicle.IndexOf(
                "var importedFingerprint = GeometryFingerprintWithEvidence(tr, imported,", StringComparison.Ordinal);
            importedNormalize.Should().BeGreaterThan(0);
            importedEntity.Should().BeGreaterThan(importedNormalize);
            importedAttestation.Should().BeGreaterThan(importedEntity);

            arrow.Should().Contain("SectionVehicleBlockService.EntityFingerprint(")
                .And.Contain("SectionVehicleBlockService.TargetHeaderMatches(")
                .And.Contain("SectionVehicleBlockService.NormalizeTargetHeader(")
                .And.Contain("SectionVehicleBlockService.GeometryFingerprint(");
        }

        [Fact]
        public void FingerprintDiagnostics_CopyTheOriginalRead_WithoutNativeWritesOrHashReplay()
        {
            var vehicle = Service("SectionVehicleBlockService.cs");
            var start = vehicle.IndexOf("private static IReadOnlyList<string> EntitySignatures(", StringComparison.Ordinal);
            var end = vehicle.IndexOf("internal static bool TargetHeaderMatches(", start, StringComparison.Ordinal);
            var read = vehicle.Substring(start, end - start);
            // 1.3.4: the same single read also serves the stable (micron-extents) proof via its flag.
            read.Split(new[] { "GeometrySignature(entity, stableExtents)" }, StringSplitOptions.None).Length.Should().Be(2,
                "the native signature is read exactly once and copied to the diagnostic snapshot");
            read.Should().Contain("signatures.Add(signature);")
                .And.Contain("SectionOfficeBlockFingerprintDiagnostics.Entity(snapshot, entity, signature);");

            Service("SectionOfficeBlockFingerprintDiagnostics.cs").Should()
                .NotContain("GeometrySignature(")
                .And.NotContain("GeometryFingerprint(")
                .And.NotContain("OpenMode.ForWrite")
                .And.NotContain("UpgradeOpen(")
                .And.NotContain("block.Comments =")
                .And.Contain("_writer.WriteOnce(");
            Service("SectionVerifyService.cs").Should()
                .Contain("SectionOfficeBlockFingerprintDiagnostics.TryCreate(db, result.RunId)");
            Service("SectionAnnotationRegistry.cs").Should()
                .Contain("SectionOfficeBlockFingerprintDiagnostics? fingerprintDiagnostics = null")
                .And.Contain("SectionVehicleBlockService.ReadFingerprintWithDiagnostics(");
        }

        [Fact]
        public void ManagedAnnotationFingerprint_ProtectsTextAndDisplaySemantics()
        {
            var registry = Service("SectionAnnotationRegistry.cs");

            registry.Should().Contain("dbText.WidthFactor, dbText.Oblique")
                .And.Contain("dbText.IsMirroredInX ? 1 : 0, dbText.IsMirroredInY ? 1 : 0")
                .And.Contain("dbText.Normal.X, dbText.Normal.Y, dbText.Normal.Z")
                .And.Contain("dbText.Thickness")
                .And.Contain("dbText.Annotative")
                .And.Contain("dbText.PaperOrientation")
                .And.Contain("effective-linetype=")
                .And.Contain("linetype-scale=")
                .And.Contain("transparency=")
                .And.Contain("visible=")
                .And.Contain("lineweight=");
        }

        [Fact]
        public void Decoration_RequiresOfficeCarBlock_AndFailsClosed()
        {
            var source = Service("SectionDecorationService.cs");

            var attempt = source.IndexOf("TryCreateCarReference", StringComparison.Ordinal);
            attempt.Should().BeGreaterThan(0);
            source.Should().Contain("SectionFindingCodes.VehicleBlockUnavailable")
                .And.Contain("rec.VehicleBlocks.Add(evidence)")
                .And.Contain("created.Add(officeBlock)")
                .And.Contain("schematic fallback is not a deliverable")
                .And.Contain("Severity = FindingSeverity.Error")
                .And.Contain("SectionAnnotationRegistry.Record");
        }

        [Fact]
        public void Decoration_CannotVerifyEmptyWhenCarStripHasNoProvenGround()
        {
            var source = Service("SectionDecorationService.cs");

            source.Should().Contain("if (groundZ is not { } gz || gz <= elevMin || gz >= elevMax)")
                .And.Contain("vehicle ground unavailable error=")
                .And.Contain("empty vehicle evidence is not a deliverable")
                .And.Contain("var expectedVehicleInstances = SectionTrafficPlanContract.VehicleInstances(record)")
                .And.Contain("rec.VehicleBlocks.Count != expectedVehicleInstances")
                .And.Contain("trackEnvelope.SourceTracks.Select(track => track.OffsetM)")
                .And.Contain("SectionFindingCodes.VehicleBlockUnavailable")
                .And.Contain("Severity = FindingSeverity.Error");
            source.Should().NotContain(
                "if (groundZ is not { } gz || gz <= elevMin || gz >= elevMax) continue;");
        }

        [Fact]
        public void VehicleService_QueriesCompleteReferencesButNeverEditsOrErasesThem()
        {
            var source = Service("SectionVehicleBlockService.cs");
            source.Should().Contain("existing.GetBlockReferenceIds(false, true).Count")
                .And.Contain("targetDb.Purge(candidates)")
                .And.Contain("candidates.Count == 1 && candidates[0] == existingId")
                .And.NotContain("case BlockReference")
                .And.NotContain(".Erase(")
                .And.Contain("SHA-addressed name");
        }

        [Fact]
        public void DirectionChoice_RequiresResolvedPerLaneEvidenceAndAuditedConvention()
        {
            var service = Service("SectionVehicleBlockService.cs");
            var core = File.ReadAllText(Path.Combine(
                PluginSourceDir, "..", "MahodAI.CivilDelivery.Core", "Shared",
                "SectionFurnitureLogic.cs"));

            core.Should().Contain("TryOfficeCarViewForFlow")
                .And.Contain("along-alignment=rear")
                .And.Contain("against-alignment=front")
                .And.NotContain("OfficeCarViewForSignedOffset");
            service.Should().Contain("SectionVehicleDirectionPlanner.DirectionPlan")
                .And.Contain("ArrowEvidenceMode.MotorTraffic")
                .And.Contain("direction-source=")
                .And.Contain("direction-digest=")
                .And.NotContain("signed offset cannot select a car view")
                .And.NotContain("[Obsolete(")
                .And.NotContain("OfficeCarViewSelectionBasis");
            Service("SectionDecorationService.cs")
                .Should().Contain("tr, view, spec, midOff, gz, plannedDirection");
        }

        [Fact]
        public void ManualSectionReuse_AddsOnlyToolOwnedAnnotations()
        {
            var decoration = Service("SectionDecorationService.cs");

            decoration.Should().Contain("targets.Any(t => t.ManualSectionReuse == null)")
                .And.Contain("!hasManagedTargets || EnsureSectionStyles")
                .And.Contain("var civilOpenMode = record.ManualSectionReuse == null")
                .And.Contain("? OpenMode.ForWrite")
                .And.Contain(": OpenMode.ForRead")
                .And.Contain("if (record.ManualSectionReuse == null)")
                .And.Contain("ApplySurfaceStyles(tr, profile, record, sampleLine, rec, surfaceStylesOk)")
                .And.Contain("SectionAnnotationRegistry.EraseExisting")
                .And.Contain("SectionAnnotationRegistry.Record");
        }
    }
}
