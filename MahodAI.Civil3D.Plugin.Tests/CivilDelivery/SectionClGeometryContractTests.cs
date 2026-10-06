using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Geometry;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SectionClGeometryContractTests
{
    [Theory]
    [InlineData(0.02, 0.4, true)]
    [InlineData(-0.02, -0.4, true)]
    [InlineData(0.03, 0.6, false)]
    [InlineData(-0.03, -0.6, false)]
    [InlineData(1.0, 20, false)]
    [InlineData(2.0, 0.01, false)]
    public void TwoVertexBulgeMustProveWorldSagitta_NotAutomaticallyStraight(double bulge, double sagitta, bool eligible)
    {
        var result = Analyze(new[] { new Pt2(0, 0), new Pt2(40, 0) }, new[] { bulge }, new[] { new Pt2(0, sagitta) });
        (result.Kind == SectionClCandidateLogic.Decision.CollapseToChord).Should().Be(eligible);
        if (!eligible) result.Kind.Should().Be(SectionClCandidateLogic.Decision.Degenerate);
    }

    [Fact]
    public void AllLegacyVerticesAreRead_BentMiddleCannotDisappearIntoStartEndChord()
    {
        var visited = new List<int>();
        var native = new[] { new SectionClGeometryContract.Vertex(0, 0, 7, 0), new(4, 3, 7, 0), new(8, 0, 7, 0) };
        var read = SectionClGeometryContract.ReadLegacyVertices(new[] { 0, 1, 2 }, id => { visited.Add(id); return native[id]; });
        visited.Should().Equal(0, 1, 2);
        read.Should().Equal(native);
        Analyze(read.Select(v => new Pt2(v.X, v.Y)).ToArray(), new[] { 0d, 0d }, new Pt2[2])
            .Kind.Should().Be(SectionClCandidateLogic.Decision.Degenerate);
    }

    [Fact]
    public void LegacyMissingOrFittedVertexCannotBeSkippedToProduceSuccess()
    {
        Action unavailable = () => SectionClGeometryContract.ReadLegacyVertices(new[] { 0, 1, 2 }, id =>
            id == 1 ? throw new InvalidOperationException("vertex read failure") : new(10 * id, 0, 0, 0));
        unavailable.Should().Throw<InvalidOperationException>().WithMessage("vertex read failure");
        Action fitted = () => SectionClGeometryContract.ReadLegacyVertices(new[] { 0 }, _ => new(0, 0, 0, 0, false));
        fitted.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void LegacyBulgeRemainsEvidenceEvenIfEndpointsAreCollinear()
    {
        var vertices = SectionClGeometryContract.ReadLegacyVertices(new[] { 0, 1 }, id => new(40 * id, 0, 0, id == 0 ? 1 : 0));
        Analyze(vertices.Select(v => new Pt2(v.X, v.Y)).ToArray(), new[] { vertices[0].Bulge }, new[] { new Pt2(0, 20) })
            .Kind.Should().Be(SectionClCandidateLogic.Decision.Degenerate);
    }

    [Fact]
    public void MultiSegmentSagittaBoundIncludesBothVertexBendAndArcDeviation()
    {
        var points = new[] { new Pt2(0, 0), new Pt2(20, 0.3), new Pt2(40, 0) };
        Analyze(points, new[] { 0d, 0d }, new Pt2[2]).Kind.Should().Be(SectionClCandidateLogic.Decision.CollapseToChord);
        Analyze(points, new[] { 0.03, 0d }, new[] { new Pt2(0, 0.3), new Pt2(0, 0) })
            .Kind.Should().Be(SectionClCandidateLogic.Decision.Degenerate);
    }

    [Fact]
    public void WorldMetresControlAcceptance_NotSourceChordOrUniformScaleAssumptions()
    {
        var metres = new[] { new Pt2(700000, 250000), new Pt2(700040, 250000) };
        // Source millimetres: chord40000, bulge0.02 -> sagitta400mm ->0.4m.
        Analyze(metres, new[] { 0.02 }, new[] { new Pt2(0, 0.4) }).Kind
            .Should().Be(SectionClCandidateLogic.Decision.CollapseToChord);
        // A non-uniform/sheared insert may preserve the chord but enlarge normal deviation.
        Analyze(metres, new[] { 0.02 }, new[] { new Pt2(100, 4) }).Kind
            .Should().Be(SectionClCandidateLogic.Decision.Degenerate);
        // Rotating the same coordinates and displacement preserves the bound.
        Analyze(new[] { new Pt2(0, 0), new Pt2(0, 40) }, new[] { -0.02 }, new[] { new Pt2(-0.4, 0) })
            .MaxSagitta.Should().BeApproximately(0.4, 1e-12);
    }

    [Fact]
    public void ClosedZeroChordAndNonfiniteGeometryNeverBecomeInstructions()
    {
        var ordinary = new[] { new Pt2(0, 0), new Pt2(40, 0) };
        Analyze(ordinary, new[] { 0d }, new Pt2[1], true).Kind.Should().Be(SectionClCandidateLogic.Decision.IgnoreClosedNonInstruction);
        Analyze(new[] { new Pt2(2, 2), new Pt2(2, 2) }, new[] { 0d }, new Pt2[1]).Kind.Should().Be(SectionClCandidateLogic.Decision.Degenerate);
        Analyze(new[] { new Pt2(double.NaN, 0), new Pt2(2, 0) }, new[] { 0d }, new Pt2[1]).Kind.Should().Be(SectionClCandidateLogic.Decision.Degenerate);
        Analyze(new[] { new Pt2(-1e308, 0), new Pt2(1e308, 0) }, new[] { 0d }, new Pt2[1]).Kind.Should().Be(SectionClCandidateLogic.Decision.Degenerate);
        Analyze(ordinary, new[] { double.NaN }, new Pt2[1]).Kind.Should().Be(SectionClCandidateLogic.Decision.Degenerate);
        Analyze(ordinary, new[] { 0.02 }, new[] { new Pt2(double.PositiveInfinity, 0) }).Kind.Should().Be(SectionClCandidateLogic.Decision.Degenerate);
    }

    [Fact]
    public void DeterminantOverflowAndSingularityAreRefused_OrdinaryAffineTransformsRemainAllowed()
    {
        var matrix = new double[] { 1, 0, 0, 100, 0, 1, 0, 200, 0, 0, 1, 300, 0, 0, 0, 1 };
        ProjectSetupScanner.IsUsableTransformValues(matrix).Should().BeTrue();
        matrix[0] = -2; matrix[1] = 3; matrix[5] = 4;
        ProjectSetupScanner.IsUsableTransformValues(matrix).Should().BeTrue();
        matrix[10] = 0;
        ProjectSetupScanner.IsUsableTransformValues(matrix).Should().BeFalse();
        matrix[0] = 1e308; matrix[5] = 1e308; matrix[10] = 1e308;
        ProjectSetupScanner.IsUsableTransformValues(matrix).Should().BeFalse();
        matrix[0] = double.NaN;
        ProjectSetupScanner.IsUsableTransformValues(matrix).Should().BeFalse();
    }

    [Fact]
    public void NativeAdapterUsesWcsVerticesActiveTransactionAndEveryBulge_InBothDiscoveryAndPlan()
    {
        var sourceDir = typeof(SectionClGeometryContractTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "MahodPluginSourceDir").Value!;
        string Read(string file) => File.ReadAllText(Path.Combine(sourceDir, "CivilDelivery", "Sections", "Services", file));
        Read("SectionClGeometryContract.cs").Should().Contain("polyline.GetPoint3dAt(i)")
            .And.Contain("polyline.GetBulgeAt(i)").And.Contain("transaction.GetObject(id, OpenMode.ForRead)")
            .And.Contain("legacy.VertexPosition(vertex)").And.Contain("vertex.Bulge")
            .And.Contain("legacy.PolyType != Poly2dType.SimplePoly").And.NotContain("legacy.StartPoint");
        Read("ClInstructionReader.cs").Should().Contain("SectionClGeometryContract.Read(entity, transaction, candidate.Transform)")
            .And.Contain("SectionClGeometryContract.Analyze(geometry)").And.Contain("InvalidGeometryFinding")
            .And.Contain("DeliveryStatus.Blocked");
        Read("ProjectSetupScanner.cs").Should().Contain("SectionClGeometryContract.Read(ent, transaction, xform)")
            .And.Contain("SectionClGeometryContract.Analyze(geometry)");
    }

    private static SectionClCandidateLogic.Result Analyze(Pt2[] points, double[] bulges, Pt2[] sagittas, bool closed = false) =>
        SectionClGeometryContract.Analyze(new(points, points, bulges, sagittas, closed));
}
