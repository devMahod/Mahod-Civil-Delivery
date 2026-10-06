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

public sealed class SectionHatchRegionSupportTests
{
    private static IReadOnlyList<P2> Box(double left, double bottom, double right, double top) =>
        new[] { new P2(left, bottom), new P2(right, bottom), new P2(right, top), new P2(left, top) };

    private static SectionHatchSpanLabelService.Region Region(
        IReadOnlyList<IReadOnlyList<P2>> loops, string label = "מדרכה", string handle = "92B0",
        double tolerance = 0, SectionRegionCoverageLogic.FillStyle style = SectionRegionCoverageLogic.FillStyle.Normal) =>
        SectionHatchSpanLabelService.CreateRegion(loops, style, label, "HW-HATCH- SIDEWALK", "GM",
            handle, "C:/source/GM.dwg", new string('A', 64), tolerance);

    private static Crossing Mark(double offset, P2 point, string handle) =>
        new(offset, null, "HW-CURB", "GM", new ProjectionRuleMatch("curb", "אבן שפה", 7),
            handle, point.X, point.Y);

    private static List<SpanLabelOverride> Resolve(SectionHatchSpanLabelService.Region region,
        P2? start = null, P2? end = null) => SectionHatchSpanLabelService.Resolve(new[] { region },
        new[] { Mark(-3, start ?? new P2(-3, 0), "L"), Mark(3, end ?? new P2(3, 0), "R") },
        new[] { new UnresolvedSpan(-3, 3, 6, "curb", "curb", "no-confident-strip-label") });

    [Theory]
    [InlineData("sidewalk", "מדרכה")]
    [InlineData("bike", "שביל אופניים")]
    [InlineData("bike", "נתיב אופניים")]
    [InlineData("garden", "גינון")]
    [InlineData("parking", "רצועת חניה")]
    [InlineData("shoulder", "שול")]
    [InlineData("island", "מפרדה")]
    [InlineData("lane", "נתיב נסיעה")]
    [InlineData("strip", "נת\"צ")]
    public void ExistingSemanticRoleAndLabelPairs_KeepTheirNamesAndRequireWholeSpanCoverage(string kind, string label)
    {
        SectionHatchSpanLabelService.IsSupportedSemanticRule(new(kind, label, 7)).Should().BeTrue();
        var region = Region(new[] { Box(-5, -2, 5, 2) }, label);
        Resolve(region).Should().ContainSingle().Which.Label.Should().Be(label);
        Resolve(Region(new[] { Box(-5, -2, 2, 2) }, label)).Should().BeEmpty();
        region.Loops.Should().ContainSingle().Which.Should().HaveCount(4,
            "a semantic region remains an area with its full loop, never a line-length measurement");
    }

    [Theory]
    [InlineData("curb", "מדרכה")]
    [InlineData("row", "גינון")]
    [InlineData("mark", "חניה")]
    [InlineData("utility", "נת\"צ")]
    [InlineData("sidewalk", "לא ידוע")]
    [InlineData("garden", "גבול גינון")]
    [InlineData("lane", "שפת מיסעה")]
    [InlineData("bike", "שפת אופניים")]
    [InlineData("island", "אבן שפה")]
    [InlineData("parking", "גבול חניה")]
    [InlineData("strip", "אזור")]
    public void BoundaryOnlyOrUnknownRolesAndLabels_DoNotBecomeSemanticAreas(string kind, string label) =>
        SectionHatchSpanLabelService.IsSupportedSemanticRule(new(kind, label, 7)).Should().BeFalse();

    [Fact]
    public void ExistingPlBikeProfileRule_UsesRegionEvidenceWithoutAddingAnAliasOrChangingItsLabel()
    {
        var approvedRules = new List<ProjectionRuleConfig> { new("*BIKE*", null, "שביל אופניים", "bike", null) };
        var match = Classify("PL-BIKE", null, WithObservedPlanMarkDefaults(approvedRules));
        match.Should().NotBeNull();
        SectionHatchSpanLabelService.IsSupportedSemanticRule(match!).Should().BeTrue();
        Resolve(Region(new[] { Box(-5, -2, 5, 2) }, match!.Label))
            .Should().ContainSingle().Which.Label.Should().Be("שביל אופניים");
        approvedRules.Should().ContainSingle().Which.LayerPattern.Should().Be("*BIKE*");
    }

    [Fact]
    public void ACompleteSidewalkRegion_ResolvesAnExistingCurbSpanWithSourceEvidence()
    {
        var region = Region(new[] { Box(-5, -2, 5, 2) });
        var result = Resolve(region);
        result.Should().ContainSingle().Which.Should().Be(
            new SpanLabelOverride(0, "מדרכה", SectionHatchSpanLabelService.EvidenceSource, region.Evidence));
        var analysis = AnalyzePresentationCoverage(new[]
        {
            (-3.0, "curb", "אבן שפה"), (3.0, "curb", "אבן שפה"),
        }, approvedOverrides: result);
        analysis.Summary.IsComplete.Should().BeTrue();
        analysis.DimensionMarks.Should().HaveCount(2, "region loops never become synthetic dimension anchors");
    }

    [Fact]
    public void APartialRegionOrOffCentreHole_CannotNameTheWholeExistingSpan()
    {
        Resolve(Region(new[] { Box(-5, -2, 2, 2) })).Should().BeEmpty();
        Resolve(Region(new[] { Box(-5, -2, 5, 2), Box(2.123, -0.2, 2.125, 0.2) }))
            .Should().BeEmpty();
    }

    [Fact]
    public void CompetingSourceRegionsRemainAnExplicitConflict()
    {
        var regions = new[] { Region(new[] { Box(-5, -2, 5, 2) }), Region(new[] { Box(-5, -2, 5, 2) }, "גינון", "G") };
        var result = SectionHatchSpanLabelService.Resolve(regions,
            new[] { Mark(-3, new(-3, 0), "L"), Mark(3, new(3, 0), "R") },
            new[] { new UnresolvedSpan(-3, 3, 6, "curb", "curb", "no-confident-strip-label") });
        var analysis = AnalyzePresentationCoverage(new[] { (-3.0, "curb", ""), (3.0, "curb", "") }, approvedOverrides: result);
        analysis.UnresolvedSpans.Should().ContainSingle().Which.Reason.Should().Be("conflicting-strip-label-evidence");
    }

    [Fact]
    public void MissingOrAmbiguousWcsEndpoints_DoNotInventAnOffsetConversion()
    {
        var region = Region(new[] { Box(-5, -2, 5, 2) });
        var marks = new[] { Mark(-3, new(-3, 0), "L"), Mark(-3, new(-3, 1), "L2"), Mark(3, new(3, 0), "R") };
        SectionHatchSpanLabelService.Resolve(new[] { region }, marks,
            new[] { new UnresolvedSpan(-3, 3, 6, "curb", "curb", "no-confident-strip-label") })
            .Should().BeEmpty();
    }

    [Fact]
    public void CurvedApproximationRequiresClearanceFromEveryBoundary()
    {
        var loops = new[] { Box(-3, -2, 3, 2) };
        Resolve(Region(loops)).Should().ContainSingle();
        Resolve(Region(loops, tolerance: 0.005)).Should().BeEmpty(
            "the tessellated hole/edge uncertainty must not be rounded into semantic certainty");
    }

    [Fact]
    public void InvalidOrTouchingContoursAndMissingSourceIdentityAreRejected()
    {
        Action crossing = () => Region(new[] { Box(-5, -2, 5, 2), Box(4, -1, 6, 1) });
        Action touching = () => Region(new[] { Box(-5, -2, 5, 2), Box(3, -1, 5, 1) });
        Action bowTie = () => Region(new[] { new[] { new P2(0, 0), new P2(2, 2), new P2(0, 2), new P2(2, 0) } });
        Action missingSource = () => SectionHatchSpanLabelService.CreateRegion(new[] { Box(-5, -2, 5, 2) },
            SectionRegionCoverageLogic.FillStyle.Normal, "מדרכה", "HW-HATCH- SIDEWALK", null, "H", "GM.dwg", null);
        crossing.Should().Throw<InvalidOperationException>();
        touching.Should().Throw<InvalidOperationException>();
        bowTie.Should().Throw<InvalidOperationException>();
        missingSource.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MovingAHoleOrReplacingItsSourceChangesTheEvidenceDigest()
    {
        var original = Region(new[] { Box(-5, -2, 5, 2), Box(1, 0.5, 2, 1) });
        var moved = Region(new[] { Box(-5, -2, 5, 2), Box(1.1, 0.5, 2.1, 1) });
        var replaced = Region(original.Loops, handle: "OTHER");
        moved.Evidence.Should().NotBe(original.Evidence);
        replaced.Evidence.Should().NotBe(original.Evidence);
    }

    [Fact]
    public void RecoveredGm92B0DiagnosticFixture_IsAReadableOuterStyleSidewalkRegion()
    {
        // Source FAD0AC...3D88B90, native GM hatch 92B0, GOST_GROUND, Z=0,
        // normal=(0,0,1), four exactly closed Line edges. Recovered by the
        // warning-preserving partial ACadSharp reader on 2026-09-06. This is a
        // regression geometry fixture, NOT a complete DWG read or real CL proof.
        IReadOnlyList<P2> loop = new[]
        {
            new P2(203516.20277282086, 649306.6053217507),
            new P2(203509.0450329217, 649307.4936577772),
            new P2(203508.69644266204, 649304.5561869937),
            new P2(203515.82097816878, 649303.671453874),
        };
        var region = SectionHatchSpanLabelService.CreateRegion(new[] { loop },
            SectionRegionCoverageLogic.FillStyle.Outer, "מדרכה", "HW-HATCH- SIDEWALK", null, "92B0",
            "6422-GM-MODEL-NATAZ 1.dwg", "FAD0ACACA3B064F55E53CC20476B30ACF5542BCAA2F7E4CC462B45B393D88B90");
        var center = new P2(loop.Average(p => p.X), loop.Average(p => p.Y));
        Resolve(region, new(center.X - 1, center.Y), new(center.X + 1, center.Y))
            .Should().ContainSingle().Which.Label.Should().Be("מדרכה");
        Resolve(region, new(center.X - 10, center.Y), new(center.X + 10, center.Y)).Should().BeEmpty();
    }

    private static string Source(string file)
    {
        var root = typeof(SectionHatchRegionSupportTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(root, "CivilDelivery", "Sections", "Services", file));
    }

    [Fact]
    public void CollectorAdmitsOnlyRecognizedSemanticHatchesAndFailsClosedOnUnreadableLoops()
    {
        var collector = Source("SectionGeometryCollector.cs");
        collector.Should().Contain("entity is Hatch hatch && isExplicitMark")
            .And.Contain("SectionHatchSpanLabelService.IsSupportedSemanticRule(selected)")
            .And.Contain("var region = SectionHatchRegionReader.Read(")
            .And.Contain("result.PlanRegions.Add(region)")
            .And.Contain("region.Deferred is { } partial")
            .And.Contain("partial.Loops.Where(loop => loop.Failure != null)")
            .And.Contain("loop.Bounds")
            .And.Contain("entity, source, transform, handlePath, \"plan-region\", selected, ex.Message");
        var reader = Source("SectionHatchRegionReader.cs");
        reader.Should().Contain("loopIndex < hatch.NumberOfLoops")
            .And.Contain("loop.LoopType & unsupported")
            .And.Contain("new Point3d(point.X, point.Y, hatch.Elevation)")
            .And.Contain("Matrix3d.PlaneToWorld(hatch.Normal)")
            .And.Contain("case LineSegment2d line:")
            .And.Contain("case CircularArc2d arc:")
            .And.Contain("SectionHatchBoundaryGeometry.TessellateClosedPolyline(")
            .And.Contain("SectionHatchBoundaryGeometry.JoinClosedEdgesWithEndpointTolerance(")
            .And.Contain("SectionHatchBoundaryGeometry.MicrometricEndpointToleranceM")
            .And.Contain("HatchLoopTypes.NotClosed | HatchLoopTypes.SelfIntersecting")
            .And.Contain("HatchLoopTypes.Duplicate | HatchLoopTypes.Textbox | HatchLoopTypes.TextIsland")
            .And.Contain("SectionHatchSpanLabelService.CreateSourceRegion(")
            .And.Contain("sourceLoops.Add(new(loopIndex")
            .And.Contain("failure, curved ? SectionGeometryCollector.MaxCurveSagittaM : 0)")
            .And.Contain("HatchStyle.Outer => SectionRegionCoverageLogic.FillStyle.Outer")
            .And.NotContain("EvaluateHatch(").And.NotContain(".Explode(");
        var flagsGate = reader.IndexOf("var failure = (loop.LoopType & unsupported) != 0", StringComparison.Ordinal);
        var boundedJoin = reader.IndexOf("SectionHatchBoundaryGeometry.JoinClosedEdgesWithEndpointTolerance(",
            StringComparison.Ordinal);
        var sourceInventory = reader.IndexOf("SectionHatchSpanLabelService.CreateSourceRegion(", StringComparison.Ordinal);
        flagsGate.Should().BeGreaterThanOrEqualTo(0);
        boundedJoin.Should().BeGreaterThan(flagsGate, "native invalid-loop flags remain failure evidence even when samples prove a local envelope");
        sourceInventory.Should().BeGreaterThan(boundedJoin, "the source inventory must retain all loop outcomes");
        var service = Source("SectionHatchSpanLabelService.cs");
        var localProof = service[service.IndexOf("internal static Region? CompleteForSegment", StringComparison.Ordinal)..];
        localProof.Should().Contain("!SectionProjectionFailureScope.HasUsableBounds(loop.Bounds)")
            .And.Contain("SectionProjectionFailureScope.MayIntersect(loop.Bounds, endpoints)")
            .And.Contain("relevant.Any(loop => loop.Failure != null)")
            .And.Contain("var region = CreateRegion(relevant.Select(loop => loop.Points).ToArray()");
        localProof.IndexOf("relevant.Any(loop => loop.Failure != null)", StringComparison.Ordinal)
            .Should().BeLessThan(localProof.IndexOf("var region = CreateRegion(", StringComparison.Ordinal),
                "a flagged/failed relevant loop cannot enter complete-region topology validation as usable geometry");
    }

    [Fact]
    public void PlanAndApplyRecomputeTheSameRegionsInsteadOfReusingOldHatchOverrides()
    {
        Source("SectionPlanService.cs").Should().Contain("SectionHatchSpanLabelService.Resolve(")
            .And.Contain("projectable.PlanRegions, merged, baseAnalysis)");
        Source("SectionDecorationService.cs").Should()
            .Contain("!SectionHatchSpanLabelService.IsRegionEvidence(item.Source)")
            .And.Contain("SectionHatchSpanLabelService.Resolve(")
            .And.Contain("collected.PlanRegions, markCrossings, baseRegionAnalysis)")
            .And.Contain("plannedCoverage.EvidenceDigest, actualCoverage.EvidenceDigest");
    }
}
