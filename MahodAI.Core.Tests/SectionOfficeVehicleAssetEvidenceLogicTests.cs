using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests
{
    public class SectionOfficeVehicleAssetEvidenceLogicTests
    {
        [Theory]
        [InlineData(SectionFurnitureLogic.OfficeCarView.Rear)]
        [InlineData(SectionFurnitureLogic.OfficeCarView.Front)]
        public void ExactPinnedSourceAndRecomputedGeometry_AreAccepted(
            SectionFurnitureLogic.OfficeCarView view)
        {
            var source = SectionOfficeVehicleAssetEvidenceLogic.SourceSha256(view);
            var geometry = new string('a', 64);

            SectionOfficeVehicleAssetEvidenceLogic.TryValidateLiveEvidence(
                    view,
                    source.Substring(0, 16),
                    SectionOfficeVehicleAssetEvidenceLogic.ProtectedBlockName(view),
                    SectionOfficeVehicleAssetEvidenceLogic.ProvenanceComment(view, geometry),
                    geometry,
                    out var error)
                .Should().BeTrue(error);
        }

        [Fact]
        public void CommentedGeometryThatDoesNotMatchLiveDefinition_FailsClosed()
        {
            var view = SectionFurnitureLogic.OfficeCarView.Rear;
            var source = SectionOfficeVehicleAssetEvidenceLogic.SourceSha256(view);

            SectionOfficeVehicleAssetEvidenceLogic.TryValidateLiveEvidence(
                    view,
                    source.Substring(0, 16),
                    SectionOfficeVehicleAssetEvidenceLogic.ProtectedBlockName(view),
                    SectionOfficeVehicleAssetEvidenceLogic.ProvenanceComment(
                        view, new string('a', 64)),
                    new string('b', 64),
                    out _)
                .Should().BeFalse(
                    "a provenance comment cannot hide edited live block geometry");
        }

        [Theory]
        [InlineData("suffix")]
        [InlineData("; geometry_sha256=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa; extra=1")]
        public void PrefixOrExtraCommentText_IsNeverAccepted(string suffix)
        {
            var view = SectionFurnitureLogic.OfficeCarView.Front;
            var source = SectionOfficeVehicleAssetEvidenceLogic.SourceSha256(view);
            var geometry = new string('a', 64);
            var comments = suffix == "suffix"
                ? SectionOfficeVehicleAssetEvidenceLogic.SourceComment(view) + suffix
                : SectionOfficeVehicleAssetEvidenceLogic.SourceComment(view) + suffix;

            SectionOfficeVehicleAssetEvidenceLogic.TryValidateLiveEvidence(
                    view,
                    source.Substring(0, 16),
                    SectionOfficeVehicleAssetEvidenceLogic.ProtectedBlockName(view),
                    comments,
                    geometry,
                    out _)
                .Should().BeFalse();
        }

        [Fact]
        public void UnpinnedNameOrShortAnnotationDetail_FailsClosed()
        {
            var view = SectionFurnitureLogic.OfficeCarView.Rear;
            var geometry = new string('a', 64);
            var comments = SectionOfficeVehicleAssetEvidenceLogic.ProvenanceComment(
                view, geometry);

            SectionOfficeVehicleAssetEvidenceLogic.TryValidateLiveEvidence(
                    view, "e2823d50", "MHD-CD-CAR-REAR-" + new string('A', 64),
                    comments, geometry, out _)
                .Should().BeFalse();
        }
    }
}
