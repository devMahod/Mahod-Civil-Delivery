using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Utilities;
using MahodAI.Civil3D.Plugin.Utilities.CurveFitting;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Utilities
{
    /// <summary>
    /// Tests for <see cref="AlignmentSource.ChainSegments"/> — the chaining algorithm
    /// that orders segments end-to-end and flips arc-sweep signs on reversal. The
    /// <c>FixAlignmentGeometryTool</c> radius-override step reads each chained arc's
    /// <c>ArcCenter</c> / <c>ArcRadius</c> after this ordering completes.
    ///
    /// These tests construct <see cref="AlignmentSource.ChainedSegment"/> instances
    /// directly. Because <c>ChainedSegment</c> carries an <c>ObjectId</c> field
    /// (needed in production for source-entity erasure), the CLR resolves
    /// <c>acdbmgd.dll</c> at type-layout time. They therefore only run inside a
    /// runtime that can load the Civil 3D 2026 .NET API (i.e. running the test
    /// project from Visual Studio with Civil 3D installed). The skip is automatic
    /// when the assembly cannot be resolved.
    /// </summary>
    public class AlignmentSourceTests
    {
        private const string SkipWithoutCivil3D = "Requires acdbmgd.dll (Civil 3D 2026 .NET API) on the test runtime probing path.";

        private static AlignmentSource.ChainedSegment Line(double x0, double y0, double x1, double y1)
            => new()
            {
                Kind = AlignmentSource.SegmentKind.Line,
                Start = new Pt2D(x0, y0),
                End = new Pt2D(x1, y1),
                Samples = new[] { new Pt2D(x0, y0), new Pt2D(x1, y1) },
            };

        private static AlignmentSource.ChainedSegment Arc(
            Pt2D start, Pt2D end, Pt2D center, double radius, double sweepRad)
        {
            return new AlignmentSource.ChainedSegment
            {
                Kind = AlignmentSource.SegmentKind.Arc,
                Start = start,
                End = end,
                Samples = new[] { start, end },
                ArcCenter = center,
                ArcRadius = radius,
                ArcSweepRad = sweepRad,
            };
        }

        [Fact(Skip = SkipWithoutCivil3D)]
        [Trait("Category", "RequiresCivil3D")]
        public void ChainSegments_OrdersByEndpoints()
        {
            var b = Line(10, 0, 20, 0);
            var a = Line(0, 0, 10, 0);

            var ordered = AlignmentSource.ChainSegments(new List<AlignmentSource.ChainedSegment> { b, a }, 0.01);

            ordered.Should().HaveCount(2);
            var firstStart = ordered[0].Start;
            (firstStart == new Pt2D(0, 0) || firstStart == new Pt2D(20, 0)).Should().BeTrue();
            ordered[0].End.Should().Be(ordered[1].Start);
        }

        [Fact(Skip = SkipWithoutCivil3D)]
        [Trait("Category", "RequiresCivil3D")]
        public void ChainSegments_ReversesArcSweepSign_WhenSegmentTraversedBackwards()
        {
            var line = Line(0, 0, 1, 0);
            var arcCenter = new Pt2D(1, 1);
            var arcEnd = new Pt2D(2, 1);
            var arc = Arc(new Pt2D(1, 0), arcEnd, arcCenter, 1.0, System.Math.PI / 2);

            var ordered = AlignmentSource.ChainSegments(
                new List<AlignmentSource.ChainedSegment> { line, arc }, 0.01);

            ordered.Should().HaveCount(2);
            var orderedArc = ordered.First(s => s.Kind == AlignmentSource.SegmentKind.Arc);
            orderedArc.ArcSweepRad.Should().BeApproximately(System.Math.PI / 2, 1e-9);

            var arcReversedNatural = Arc(arcEnd, new Pt2D(1, 0), arcCenter, 1.0, System.Math.PI / 2);

            var orderedFlip = AlignmentSource.ChainSegments(
                new List<AlignmentSource.ChainedSegment> { line, arcReversedNatural }, 0.01);

            orderedFlip.Should().HaveCount(2);
            var orderedArcFlip = orderedFlip.First(s => s.Kind == AlignmentSource.SegmentKind.Arc);
            orderedArcFlip.ArcSweepRad.Should().BeApproximately(-System.Math.PI / 2, 1e-9);
            orderedArcFlip.Start.Should().Be(new Pt2D(1, 0));
            orderedArcFlip.End.Should().Be(arcEnd);
        }

        [Fact(Skip = SkipWithoutCivil3D)]
        [Trait("Category", "RequiresCivil3D")]
        public void ChainSegments_EmptyInput_ReturnsEmpty()
        {
            var result = AlignmentSource.ChainSegments(new List<AlignmentSource.ChainedSegment>(), 0.01);
            result.Should().BeEmpty();
        }

        [Fact(Skip = SkipWithoutCivil3D)]
        [Trait("Category", "RequiresCivil3D")]
        public void ChainSegments_SingleSegment_ReturnsSingleSegment()
        {
            var line = Line(0, 0, 5, 0);
            var ordered = AlignmentSource.ChainSegments(new List<AlignmentSource.ChainedSegment> { line }, 0.01);
            ordered.Should().HaveCount(1);
            ordered[0].Start.Should().Be(new Pt2D(0, 0));
            ordered[0].End.Should().Be(new Pt2D(5, 0));
        }
    }
}
