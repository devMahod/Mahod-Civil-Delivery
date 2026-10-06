using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class SectionClCandidateLogicTests
{
    [Fact]
    public void NearStraightMultiVertexInstruction_CollapsesToItsChord()
    {
        var result = SectionClCandidateLogic.Analyze(
            new[] { (0d, 0d), (20d, 0.1d), (40d, 0d) }, 0.5);

        result.Kind.Should().Be(SectionClCandidateLogic.Decision.CollapseToChord);
        result.ChordLength.Should().BeApproximately(40, 0.001);
    }

    [Fact]
    public void StandaloneOpenBentPolyline_RemainsFailClosedReviewGeometry()
    {
        var result = SectionClCandidateLogic.Analyze(
            new[] { (0d, 0d), (4d, 3d), (8d, 0d) }, 0.5);

        result.Kind.Should().Be(SectionClCandidateLogic.Decision.Degenerate);
        result.MaxSagitta.Should().BeApproximately(3, 0.001);
    }

    [Fact]
    public void ClosedTriangle_IsAnnotationRegardlessOfItsObservedLength()
    {
        var shortMarker = SectionClCandidateLogic.Analyze(
            new[] { (0d, 0d), (4d, 3d), (8d, 0d) }, 0.5, isClosed: true);
        var scaledMarker = SectionClCandidateLogic.Analyze(
            new[] { (0d, 0d), (40d, 30d), (80d, 0d) }, 0.5, isClosed: true);

        shortMarker.Kind.Should().Be(
            SectionClCandidateLogic.Decision.IgnoreClosedNonInstruction);
        scaledMarker.Kind.Should().Be(
            SectionClCandidateLogic.Decision.IgnoreClosedNonInstruction);
    }

    [Fact]
    public void NatalieClShapeCensus_LeavesAll28OpenCuts_AndSuppresses56ClosedMarkers()
    {
        var candidates = Enumerable.Range(0, 28)
            .Select(_ => SectionClCandidateLogic.Analyze(
                new[] { (0d, 0d), (100d, 0d) }, 0.5, isClosed: false))
            .Concat(Enumerable.Range(0, 56).Select(_ =>
                SectionClCandidateLogic.Analyze(
                    new[] { (0d, 0d), (4d, 3d), (8d, 0d) }, 0.5,
                    isClosed: true)))
            .ToList();

        candidates.Count(result => result.Kind == SectionClCandidateLogic.Decision.Straight)
            .Should().Be(28);
        candidates.Count(result =>
                result.Kind == SectionClCandidateLogic.Decision.IgnoreClosedNonInstruction)
            .Should().Be(56);
        candidates.Should().HaveCount(84);
    }

    [Fact]
    public void ZeroChord_RemainsDegenerateInsteadOfBeingIgnoredAsNoise()
    {
        SectionClCandidateLogic.Analyze(
                new[] { (2d, 2d), (3d, 3d), (2d, 2d) }, 0.5)
            .Kind.Should().Be(SectionClCandidateLogic.Decision.Degenerate);
    }
}
