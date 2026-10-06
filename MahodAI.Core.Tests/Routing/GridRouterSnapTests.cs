using System.Threading;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;
using NetTopologySuite.Geometries;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Routing
{
    /// <summary>
    /// Strict endpoint contract of <see cref="GridRouter"/>:
    ///   • Clicks outside the surface AABB fail (no boundary-clamp-and-continue).
    ///   • Clicks on a non-walkable cell beyond the snap threshold fail.
    ///   • Clicks within threshold snap, and the SNAPPED point is used as the routed
    ///     endpoint — the raw off-surface click is never substituted back.
    /// </summary>
    public class GridRouterSnapTests
    {
        // Full-rectangle walkable mask 0..100 × 0..100 at the given cell size.
        private static (bool[,] mask, Envelope env) FullMask(double cellSize = 2.0, double size = 100.0)
        {
            int n = (int)System.Math.Ceiling(size / cellSize);
            var mask = new bool[n, n];
            for (int x = 0; x < n; x++)
                for (int y = 0; y < n; y++)
                    mask[x, y] = true;
            return (mask, new Envelope(0, n * cellSize, 0, n * cellSize));
        }

        // Mask walkable only for x ≥ holeEndX (left strip non-walkable but inside the AABB).
        private static (bool[,] mask, Envelope env) LeftStripUnwalkable(
            double cellSize, double size, double holeEndX)
        {
            int n = (int)System.Math.Ceiling(size / cellSize);
            var mask = new bool[n, n];
            for (int x = 0; x < n; x++)
            {
                double wx = (x + 0.5) * cellSize;
                for (int y = 0; y < n; y++)
                    mask[x, y] = wx >= holeEndX;
            }
            return (mask, new Envelope(0, n * cellSize, 0, n * cellSize));
        }

        [Fact]
        public void Route_StartClickOutsideAabb_FailsWithStartOffSurface()
        {
            var (mask, env) = FullMask();
            // 1 km away from the surface — the old WorldToCell clamp would have routed
            // from a boundary cell and "succeeded".
            var a = new Pt2(-1000, -1000);
            var b = new Pt2(50, 50);

            var result = GridRouter.Route(mask, env, 2.0, a, b, CancellationToken.None);

            result.Success.Should().BeFalse();
            result.FailureCode.Should().Be(GridRouter.FailureCodes.StartOffSurface);
        }

        [Fact]
        public void Route_EndClickOutsideAabb_FailsWithEndOffSurface()
        {
            var (mask, env) = FullMask();
            var a = new Pt2(50, 50);
            var b = new Pt2(5000, 50);

            var result = GridRouter.Route(mask, env, 2.0, a, b, CancellationToken.None);

            result.Success.Should().BeFalse();
            result.FailureCode.Should().Be(GridRouter.FailureCodes.EndOffSurface);
        }

        [Fact]
        public void Route_StartInAabbButBeyondSnapThreshold_Fails()
        {
            // Left 60 m of the AABB is non-walkable; click at x=5 is ~55 m from the
            // nearest walkable cell — beyond the 20 m default threshold.
            var (mask, env) = LeftStripUnwalkable(cellSize: 2.0, size: 100.0, holeEndX: 60.0);
            var a = new Pt2(5, 50);
            var b = new Pt2(90, 50);

            var result = GridRouter.Route(mask, env, 2.0, a, b, CancellationToken.None);

            result.Success.Should().BeFalse();
            result.FailureCode.Should().Be(GridRouter.FailureCodes.StartOffSurface);
        }

        [Fact]
        public void Route_StartWithinSnapThreshold_SnapsAndUsesSnappedPoint()
        {
            // Left 10 m non-walkable; click at x=5 is ~6 m from walkable — within the
            // 20 m threshold. The routed start must be the snapped point, NOT the click.
            var (mask, env) = LeftStripUnwalkable(cellSize: 2.0, size: 100.0, holeEndX: 10.0);
            var a = new Pt2(5, 50);
            var b = new Pt2(90, 50);

            var result = GridRouter.Route(mask, env, 2.0, a, b, CancellationToken.None);

            result.Success.Should().BeTrue(result.FailureReason);
            result.StartSnap.Should().NotBeNull();
            result.StartSnap!.Snapped.Should().BeTrue();
            result.StartSnap.DistanceM.Should().BeGreaterThan(0).And.BeLessOrEqualTo(20.0);

            // The route endpoint is the SNAPPED point — never the raw off-surface click.
            result.Path[0].X.Should().NotBe(a.X);
            result.Path[0].X.Should().BeApproximately(result.StartSnap.Point.X, 1e-9);
            result.Path[0].Y.Should().BeApproximately(result.StartSnap.Point.Y, 1e-9);
            result.Path[0].X.Should().BeGreaterOrEqualTo(10.0,
                "the snapped start must sit on a walkable cell, not inside the unwalkable strip");

            result.EndSnap.Should().NotBeNull();
            result.EndSnap!.Snapped.Should().BeFalse("the end click was on a walkable cell");
        }

        [Fact]
        public void Route_WalkableClicks_NotSnappedAndEndpointsExact()
        {
            var (mask, env) = FullMask();
            var a = new Pt2(10, 10);
            var b = new Pt2(90, 90);

            var result = GridRouter.Route(mask, env, 2.0, a, b, CancellationToken.None);

            result.Success.Should().BeTrue(result.FailureReason);
            result.StartSnap!.Snapped.Should().BeFalse();
            result.StartSnap.DistanceM.Should().Be(0);
            result.EndSnap!.Snapped.Should().BeFalse();
            result.Path[0].X.Should().Be(a.X);
            result.Path[0].Y.Should().Be(a.Y);
            result.Path[^1].X.Should().Be(b.X);
            result.Path[^1].Y.Should().Be(b.Y);
        }

        [Fact]
        public void ResolveEndpoint_OutsideAabb_ReturnsError()
        {
            var (mask, env) = FullMask();
            var (snap, _, _, error, _) = GridRouter.ResolveEndpoint(
                new Pt2(-50, 50), mask, env, 2.0, mask.GetLength(0), mask.GetLength(1), 20.0,
                new int[mask.GetLength(0), mask.GetLength(1)], 0, 0.0);

            snap.Should().BeNull();
            error.Should().Contain("bounding box");
        }

        [Fact]
        public void ResolveEndpoint_SnapDistanceHonoursThreshold()
        {
            // Click 14 m from walkable with a 10 m threshold → reject even though a
            // walkable cell exists within the spiral search radius.
            var (mask, env) = LeftStripUnwalkable(cellSize: 2.0, size: 100.0, holeEndX: 16.0);
            var (snap, _, _, error, _) = GridRouter.ResolveEndpoint(
                new Pt2(2, 50), mask, env, 2.0, mask.GetLength(0), mask.GetLength(1), 10.0,
                new int[mask.GetLength(0), mask.GetLength(1)], 0, 0.0);

            snap.Should().BeNull();
            error.Should().NotBeNull();
        }
    }
}
