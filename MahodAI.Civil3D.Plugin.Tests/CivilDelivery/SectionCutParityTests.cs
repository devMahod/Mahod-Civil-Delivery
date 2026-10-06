using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public class SectionCutParityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PlanApplyDigestAndFullRegionSpan_AreIdenticalForSkewReversalAndDisplayPadding(
        bool reverse, bool padded)
    {
        var a = new P2(-3, -3); var b = new P2(3, 3);
        SectionCutFrame.TryCreate(a, b, new(0, 0), 90, out var plannedFrame).Should().BeTrue();
        SectionCutFrame.TryCreate(reverse ? b : a, reverse ? a : b, new(0, 0), 90,
            out var appliedFrame).Should().BeTrue();
        var lines = new[] { LineAt(-3, "A"), LineAt(3, "B") };
        var planned = SectionCutGeometry.CrossingsFor(lines, plannedFrame!);
        var applied = SectionCutGeometry.CrossingsFor(lines.Reverse(), appliedFrame!);
        appliedFrame!.MatchesNativeLeftEndpoint(a).Should().BeTrue();
        appliedFrame.IsContainedInDisplay(padded ? -10 : plannedFrame!.MinOffset,
            padded ? 10 : plannedFrame!.MaxOffset).Should().BeTrue();

        applied.Should().Equal(planned);
        applied.Select(ProjectionEvidenceKey).Should().Equal(planned.Select(ProjectionEvidenceKey));
        var planCoverage = Analyze(plannedFrame!, planned);
        var applyCoverage = Analyze(appliedFrame, applied);
        planCoverage.Summary.IsComplete.Should().BeTrue();
        applyCoverage.Summary.Should().Be(planCoverage.Summary);
        applyCoverage.WidthSpans.Should().ContainSingle().Which.Width
            .Should().BeApproximately(6 * Math.Sqrt(2), 1e-9);
        applyCoverage.StripLabels.Should().ContainSingle().Which.Label.Should().Be("מדרכה");
    }

    [Fact]
    public void SourceReplacementStillChangesTheProjectionAndPresentationDigests()
    {
        SectionCutFrame.TryCreate(new(-3, -3), new(3, 3), new(0, 0), 90, out var frame)
            .Should().BeTrue();
        var planned = SectionCutGeometry.CrossingsFor(new[] { LineAt(-3, "A"), LineAt(3, "B") }, frame!);
        var replaced = SectionCutGeometry.CrossingsFor(new[] { LineAt(-3, "OTHER"), LineAt(3, "B") }, frame!);
        replaced.Select(ProjectionEvidenceKey).Should().NotEqual(planned.Select(ProjectionEvidenceKey));
        Analyze(frame!, replaced).Summary.EvidenceDigest.Should().NotBe(Analyze(frame!, planned).Summary.EvidenceDigest);
    }

    [Fact]
    public void CanonicalCutTraversalKeepsNearbyMergedWorldEvidenceStable()
    {
        SectionCutFrame.TryCreate(new(-3, -3), new(3, 3), new(0, 0), 90, out var first).Should().BeTrue();
        SectionCutFrame.TryCreate(new(3, 3), new(-3, -3), new(0, 0), 90, out var reversed).Should().BeTrue();
        var lines = new[] { LineAt(-2, "B"), LineAt(-2.2, "A"), LineAt(3, "C") };
        var planned = SectionCutGeometry.CrossingsFor(lines, first!);
        var applied = SectionCutGeometry.CrossingsFor(lines.Reverse(), reversed!);
        planned.Should().HaveCount(2);
        applied.Should().Equal(planned);
        applied[0].SourceHandle.Should().Be("A");
        applied[0].WcsX.Should().BeApproximately(-2.2, 1e-9);
        applied[0].WcsY.Should().BeApproximately(-2.2, 1e-9);
    }

    [Fact]
    public void HostPathsUseTheSameFrameAndDoNotUseDisplayBoundsAsEvidence()
    {
        var plan = Source("SectionPlanService.cs");
        var apply = Source("SectionDecorationService.cs");
        plan.Should().Contain("SectionCutGeometry.CrossingsFor(projectable.Utilities, frame!)")
            .And.Contain("SectionCutGeometry.DimensionCrossingsFor(rowPolicy.PlanMarks, frame)")
            .And.Contain("frame.OffsetAtAlignmentProjection(point)")
            .And.Contain("var point = frame.PointAt(laneMidOffset)")
            .And.Contain("SectionCutGeometry.RequireFrame(r).Width")
            .And.NotContain("requiredLeftOffset: -(record.LeftExtent");
        apply.Should().Contain("SectionCutGeometry.CrossingsFor(collected.Utilities, frame)")
            .And.Contain("SectionCutGeometry.DimensionCrossingsFor(rowPolicy.PlanMarks, frame)")
            .And.Contain("frame.MatchesNativeLeftEndpoint(leftEnd)")
            .And.Contain("frame.IsContainedInDisplay(offMin, offMax)")
            .And.Contain("plannedCoverage.EvidenceDigest, actualCoverage.EvidenceDigest")
            .And.NotContain("requiredLeftOffset: offMin")
            .And.NotContain("requiredRightOffset: offMax");
        Source("SectionPreviewService.cs").Should().Contain("Offset = frame.OffsetOf(");
    }

    private static PresentationAnalysis Analyze(SectionCutFrame frame, IReadOnlyList<Crossing> marks)
    {
        var sourceMarks = marks.Select(mark => new PresentationMark(mark.Offset, mark.Rule.Kind,
            mark.Rule.Label, ProjectionEvidenceKey(mark))).ToList();
        var baseline = AnalyzePresentationCoverage(sourceMarks, frame.MinOffset, frame.MaxOffset);
        var region = SectionHatchSpanLabelService.CreateRegion(new IReadOnlyList<P2>[]
            { new P2[] { new(-4, -4), new(4, -4), new(4, 4), new(-4, 4) } },
            SectionRegionCoverageLogic.FillStyle.Normal, "מדרכה", "approved-layer", "GM", "H1",
            "C:/source.dwg", new string('A', 64));
        var overrides = SectionHatchSpanLabelService.Resolve(new[] { region }, marks, baseline);
        return AnalyzePresentationCoverage(sourceMarks, frame.MinOffset, frame.MaxOffset,
            approvedOverrides: overrides);
    }

    private static SectionGeometryCollector.CollectedLine LineAt(double x, string handle) =>
        new("curb", "GM", handle, "C:/source.dwg", new string('A', 64),
            new() { new(x, -10, 0), new(x, 10, 0) }, false, new("curb", "אבן שפה", 7), "plan");

    private static string Source(string file)
    {
        var root = typeof(SectionCutParityTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(root, "CivilDelivery", "Sections", "Services", file));
    }
}
