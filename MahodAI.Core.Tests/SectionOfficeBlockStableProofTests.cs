using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests
{
    /// <summary>
    /// Live 6422 STA-12145 (29.09.2026): the front-car definition was unchanged, but AutoCAD re-derived one
    /// polyline's extents after APPLY (8.370361393584146 → 8.37036139358417), so the exact stamped geometry hash no
    /// longer matched and VERIFY failed 77/78. The host now also proves the definition against the audited source
    /// with extents rounded to a micron; only that proof lets a drifted stamp bind.
    /// </summary>
    public class SectionOfficeBlockStableProofTests
    {
        private const SectionFurnitureLogic.OfficeCarView Front = SectionFurnitureLogic.OfficeCarView.Front;

        [Fact]
        public void TheLiveDriftRoundsToTheSameStableValue()
        {
            BlockDefinitionFingerprintLogic.StableExtentValue(8.370361393584146)
                .Should().Be(BlockDefinitionFingerprintLogic.StableExtentValue(8.37036139358417));
            BlockDefinitionFingerprintLogic.StableExtentValue(8.912273241769334)
                .Should().Be(BlockDefinitionFingerprintLogic.StableExtentValue(8.912273241769304));
            BlockDefinitionFingerprintLogic.StableExtentValue(7.38191501530375E-09).Should().Be("0");
            BlockDefinitionFingerprintLogic.StableExtentValue(-4e-9).Should().Be("0", "−0 is written as 0");
            BlockDefinitionFingerprintLogic.StableExtentValue(8.3703614).Should().NotBe(
                BlockDefinitionFingerprintLogic.StableExtentValue(8.3703624), "a micron is still a real change");
        }

        [Fact]
        public void TheStableEntityHashHasItsOwnVersionAndIgnoresOrder()
        {
            var a = BlockDefinitionFingerprintLogic.ComposeStableEntities(new[] { "x|1", "y|2" });
            a.Should().Be(BlockDefinitionFingerprintLogic.ComposeStableEntities(new[] { "y|2", "x|1" }));
            a.Should().NotBe(BlockDefinitionFingerprintLogic.ComposeEntities(new[] { "x|1", "y|2" }));
        }

        private static bool Validate(string? comments, string recomputed, bool proven, out string error) =>
            SectionOfficeVehicleAssetEvidenceLogic.TryValidateLiveEvidence(Front,
                SectionOfficeVehicleAssetEvidenceLogic.SourceSha256(Front).Substring(0, 16),
                SectionOfficeVehicleAssetEvidenceLogic.ProtectedBlockName(Front),
                comments, recomputed, proven, out error);

        [Fact]
        public void ADriftedStampBindsOnlyWithTheStableSourceProof()
        {
            var stamped = SectionOfficeVehicleAssetEvidenceLogic.ProvenanceComment(Front,
                "d3aa33f9864cf97fe23c66d7e9b76f82627d0a5084b01aeb4989a97fc755369e");
            const string recomputed = "a877240c603c8f1d8029d772a14b1fc21444e4cfeeb8c40e142924b148813c32";

            Validate(stamped, recomputed, proven: false, out var error).Should().BeFalse();
            error.Should().Contain("block comments do not bind");
            Validate(stamped, recomputed, proven: true, out error).Should().BeTrue(error);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("MahodAI Civil Delivery embedded office asset sha256=412e0e6f226271aa1a66a2749f598c2a2f13a237d94d0eb567e07ec206924428")]
        [InlineData("MahodAI Civil Delivery embedded office asset sha256=412e0e6f226271aa1a66a2749f598c2a2f13a237d94d0eb567e07ec206924428; geometry_sha256=D3AA33F9864CF97FE23C66D7E9B76F82627D0A5084B01AEB4989A97FC755369E")]
        [InlineData("MahodAI Civil Delivery embedded office asset sha256=412e0e6f226271aa1a66a2749f598c2a2f13a237d94d0eb567e07ec206924428; geometry_sha256=d3aa; extra")]
        [InlineData("MahodAI Civil Delivery embedded office asset sha256=e2823d50d85e2f669944cdb68b2cc87f44090801cda75a86f66a6cc11ea38968; geometry_sha256=d3aa33f9864cf97fe23c66d7e9b76f82627d0a5084b01aeb4989a97fc755369e")]
        public void EvenWithTheProofOnlyTheExactToolGrammarOfThisViewBinds(string? comments)
        {
            SectionOfficeVehicleAssetEvidenceLogic.IsExactProvenanceGrammar(Front, comments).Should().BeFalse();
            Validate(comments, new string('a', 64), proven: true, out _).Should().BeFalse();
        }

        [Fact]
        public void TheProofNeverExcusesAWrongNameOrDetail()
        {
            var stamped = SectionOfficeVehicleAssetEvidenceLogic.ProvenanceComment(Front, new string('b', 64));
            SectionOfficeVehicleAssetEvidenceLogic.TryValidateLiveEvidence(Front, "0000000000000000",
                    SectionOfficeVehicleAssetEvidenceLogic.ProtectedBlockName(Front), stamped, new string('a', 64),
                    liveGeometryMatchesPinnedSource: true, out var error)
                .Should().BeFalse();
            error.Should().Contain("annotation detail");
            SectionOfficeVehicleAssetEvidenceLogic.TryValidateLiveEvidence(Front,
                    SectionOfficeVehicleAssetEvidenceLogic.SourceSha256(Front).Substring(0, 16),
                    SectionOfficeVehicleAssetEvidenceLogic.ProtectedBlockName(SectionFurnitureLogic.OfficeCarView.Rear),
                    stamped, new string('a', 64), liveGeometryMatchesPinnedSource: true, out error)
                .Should().BeFalse();
            error.Should().Contain("name");
        }

        [Fact]
        public void TrafficArrowsTakeTheSameProof()
        {
            var stamped = SectionTrafficArrowAssetEvidenceLogic.ProvenanceComment(new string('b', 64));
            bool Arrow(bool proven, out string error) => SectionTrafficArrowAssetEvidenceLogic.TryValidateLiveEvidence(
                SectionTrafficArrowAssetEvidenceLogic.SourceSha256, new string('b', 64),
                SectionTrafficArrowAssetEvidenceLogic.ProtectedBlockName, stamped, new string('a', 64),
                definitionUsesByBlockColor: true, proven, out error);

            Arrow(false, out var error).Should().BeFalse();
            Arrow(true, out error).Should().BeTrue(error);
            SectionTrafficArrowAssetEvidenceLogic.IsExactProvenanceGrammar(stamped + " ").Should().BeFalse();
            SectionTrafficArrowAssetEvidenceLogic.TryValidateLiveEvidence(
                    SectionTrafficArrowAssetEvidenceLogic.SourceSha256, new string('b', 64),
                    SectionTrafficArrowAssetEvidenceLogic.ProtectedBlockName, stamped, new string('a', 64),
                    definitionUsesByBlockColor: false, liveGeometryMatchesPinnedSource: true, out error)
                .Should().BeFalse("ByBlock display is never excused");
        }
    }
}
