using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests
{
    public class SectionTrafficArrowAssetEvidenceLogicTests
    {
        [Fact]
        public void ExactPinnedAssetAndRecomputedLiveGeometry_AreAccepted()
        {
            var geometry = new string('a', 64);

            SectionTrafficArrowAssetEvidenceLogic.TryValidateLiveEvidence(
                    SectionTrafficArrowAssetEvidenceLogic.SourceSha256,
                    geometry,
                    SectionTrafficArrowAssetEvidenceLogic.ProtectedBlockName,
                    SectionTrafficArrowAssetEvidenceLogic.ProvenanceComment(geometry),
                    geometry,
                    definitionUsesByBlockColor: true,
                    out var error)
                .Should().BeTrue(error);
        }

        [Theory]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        public void AlteredGeometryCommentOrByBlockStyle_FailsClosed(
            bool sameGeometry,
            bool exactComments,
            bool byBlock)
        {
            var annotated = new string('a', 64);
            var live = sameGeometry ? annotated : new string('b', 64);
            var comments = exactComments
                ? SectionTrafficArrowAssetEvidenceLogic.ProvenanceComment(live)
                : SectionTrafficArrowAssetEvidenceLogic.SourceComment;

            SectionTrafficArrowAssetEvidenceLogic.TryValidateLiveEvidence(
                    SectionTrafficArrowAssetEvidenceLogic.SourceSha256,
                    annotated,
                    SectionTrafficArrowAssetEvidenceLogic.ProtectedBlockName,
                    comments,
                    live,
                    byBlock,
                    out _)
                .Should().BeFalse();
        }
    }
}
