using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public class SectionSlopeAnnotationSourceContractTests
    {
        private static string PluginSourceDir =>
            typeof(SectionSlopeAnnotationSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Service(string name) => File.ReadAllText(Path.Combine(
            PluginSourceDir, "CivilDelivery", "Sections", "Services", name));

        [Fact]
        public void Decoration_LabelsEveryMarkedWidthFromUniqueDesignGeometryOrFailsClosed()
        {
            var source = Service("SectionDecorationService.cs");
            var placement = Service("SectionAnnotationPlacementContract.cs");

            source.Should().Contain("var widthSpans = presentation.WidthSpans")
                .And.Contain("var design = surfaceChains.Design")
                .And.Contain("SectionFurnitureLogic.TrySlopeEvidence")
                .And.Contain("SectionFurnitureLogic.FormatSlopePercent")
                .And.Contain("SectionFindingCodes.SlopeUnproven")
                .And.Contain("Severity = FindingSeverity.Error")
                .And.Contain("throw SlopeFailure")
                .And.Contain("SlopeLabels.Add(SectionAnnotationContractLogic.FormatSlopeReference");
            placement.Should().Contain("ReadSurfaceChains(")
                .And.Contain("counts[0] != 1 || counts[1] != 1")
                .And.Contain("rawByIndex[1]")
                .And.NotContain("PickDesignSurface")
                .And.NotContain("ExistingPatterns");
        }

        [Fact]
        public void ApplyAndVerify_CarryExactRegisteredSlopeEvidence()
        {
            var decoration = Service("SectionDecorationService.cs");
            var verify = Service("SectionVerifyService.cs");

            decoration.Should().Contain("rec.SlopeEvidenceComplete = true")
                .And.Contain("SectionAnnotationContractLogic.CurrentVersion")
                .And.Contain("text.Handle.ToString()")
                .And.Contain("SectionAnnotationRegistry.Record");
            verify.Should().Contain("slope_evidence_complete")
                .And.Contain("TryParseSlopeReference")
                .And.Contain("design_slope_labels_live_exact")
                .And.Contain("nameof(DBText)")
                .And.Contain("string.Equals(e.Text, s.Label")
                .And.Contain("SlopeEvidenceMatchesLiveDesign(")
                .And.Contain("SectionAnnotationContractLogic.TryVerifySlopeEvidence(design,")
                .And.Contain("matchingSpans[0].FromOffsetM, matchingSpans[0].ToOffsetM")
                .And.Contain("SectionAnnotationPlacementContract.SlopePosition(");
        }

        [Fact]
        public void CarStripExpectation_CannotVerifyAsEmptyEvidence()
        {
            var decoration = Service("SectionDecorationService.cs");
            var verify = Service("SectionVerifyService.cs");

            // A composite strip can carry several source-proven traffic tracks.
            // Count required instances, not envelopes, in BOTH APPLY and VERIFY.
            decoration.Should().Contain("rec.ExpectedOfficeCarBlocks = SectionTrafficPlanContract.OfficeCarInstances(record)")
                .And.Contain("SectionTrafficPlanContract.Validate(record)")
                .And.Contain("SectionFurnitureLogic.Car.Key")
                .And.Contain("rec.VehicleBlocks.Count != expectedVehicleInstances");
            verify.Should().Contain("office_vehicle_evidence_matches_expected_strips")
                .And.Contain("SectionTrafficPlanContract.OfficeCarInstances(planRecord)")
                .And.Contain("applyRecord.ExpectedOfficeCarBlocks == expectedOfficeCarInstances")
                .And.Contain("officeEvidence.Count == applyRecord.ExpectedOfficeCarBlocks");
        }

        [Fact]
        public void LayoutFingerprint_ChangesForGroundedSlopePresentation()
        {
            var core = File.ReadAllText(Path.Combine(
                PluginSourceDir, "..", "MahodAI.CivilDelivery.Core", "Shared",
                "SectionProjectionLogic.cs"));
            // Review of 1.3.9 (30/09): content-sized band, closed dimension chain,
            // drawn legend/title and located datum change the drawn layout, so every
            // existing section is re-planned as an update.
            // b32 (04.10): the word-gap spacing changes the drawn layout of every existing section again.
            core.Should().Contain("layout-v18-header-group-gaps")
                .And.NotContain("layout-v17-word-gap-rows")
                .And.NotContain("layout-v16-aligned-band-rows")
                .And.NotContain("layout-v14-rotated-label-ink-ladder");
        }
    }
}
