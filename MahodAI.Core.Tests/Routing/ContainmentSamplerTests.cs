using System;
using System.Collections.Generic;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;
using NetTopologySuite.Geometries;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Routing
{
    /// <summary>
    /// Pure-math tests for the post-creation containment readback: station list
    /// generation and per-sample walkable-mask checks.
    /// </summary>
    public class ContainmentSamplerTests
    {
        private static (bool[,] mask, Envelope env) HalfWalkableMask()
        {
            // 50×50 grid at 2 m cells (0..100 × 0..100); walkable only for x < 50 m.
            const int n = 50;
            const double cell = 2.0;
            var mask = new bool[n, n];
            for (int x = 0; x < n; x++)
            {
                double wx = (x + 0.5) * cell;
                for (int y = 0; y < n; y++)
                    mask[x, y] = wx < 50.0;
            }
            return (mask, new Envelope(0, n * cell, 0, n * cell));
        }

        [Fact]
        public void BuildStations_IncludesStartEveryStepAndExactEnd()
        {
            var stations = ContainmentSampler.BuildStations(0, 35, 10);

            stations.Should().Equal(new double[] { 0, 10, 20, 30, 35 });
        }

        [Fact]
        public void BuildStations_EndOnStepBoundary_NoDuplicate()
        {
            var stations = ContainmentSampler.BuildStations(0, 30, 10);

            stations.Should().Equal(new double[] { 0, 10, 20, 30 });
        }

        [Fact]
        public void BuildStations_NonPositiveStep_DefaultsTo10()
        {
            var stations = ContainmentSampler.BuildStations(0, 25, 0);

            stations.Should().Equal(new double[] { 0, 10, 20, 25 });
        }

        [Fact]
        public void Off_surface_and_sub_clearance_are_reported_separately()
        {
            // The two failure kinds must not be conflated: OFF-SURFACE (no ground data, P0-02
            // absolute) vs SUB-CLEARANCE (ground exists, boundary margin short — the existing-
            // road-in-a-narrow-band case). Conflating them rejected the road-73 evidence
            // alignment (2026-07-28).
            int nx = 10, ny = 3;
            var walk = new bool[nx, ny];
            var dtb = new int[nx, ny];
            for (int x = 0; x < nx; x++)
            {
                walk[x, 1] = true;
                dtb[x, 1] = Math.Min(x + 1, nx - x);
            }
            var env2 = new Envelope(0, nx, 0, ny);

            var kindSamples = new List<(double, Pt2)>
            {
                (0.0, new Pt2(5.5, 1.5)),   // mid-strip, dtb=5 → inside at 3 m clearance
                (10.0, new Pt2(0.5, 1.5)),  // on ground, dtb=1 → SUB-CLEARANCE
                (20.0, new Pt2(5.5, 2.5)),  // off the ground row → OFF-SURFACE
                (30.0, new Pt2(-4.0, 1.5)), // outside the envelope → OFF-SURFACE
            };

            var r = ContainmentSampler.Check(kindSamples, walk, env2, cellSize: 1.0,
                                             dtb: dtb, minClearanceM: 3.0);

            r.Contained.Should().BeFalse();
            r.OffSurfaceSamples.Should().HaveCount(2);
            r.SubClearanceSamples.Should().HaveCount(1);
            r.SubClearanceSamples[0].Station.Should().Be(10.0);
            r.OutsideSamples.Should().HaveCount(3, "the union list keeps its old meaning");
        }

        [Fact]
        public void No_clearance_requirement_yields_no_sub_clearance_kind()
        {
            int nx = 4, ny = 1;
            var walk = new bool[nx, ny];
            for (int x = 0; x < nx; x++) walk[x, 0] = true;
            var env2 = new Envelope(0, nx, 0, 1);

            var r = ContainmentSampler.Check(
                new List<(double, Pt2)> { (0.0, new Pt2(0.5, 0.5)) },
                walk, env2, 1.0, dtb: new int[nx, ny], minClearanceM: 0.0);

            r.Contained.Should().BeTrue();
            r.SubClearanceSamples.Should().BeEmpty();
            r.OffSurfaceSamples.Should().BeEmpty();
        }

        [Fact]
        public void Check_AllSamplesInsideMask_Contained()
        {
            var (mask, env) = HalfWalkableMask();
            var samples = new List<(double, Pt2)>
            {
                (0, new Pt2(10, 50)),
                (10, new Pt2(20, 50)),
                (20, new Pt2(30, 50)),
            };

            var result = ContainmentSampler.Check(samples, mask, env, 2.0);

            result.Contained.Should().BeTrue();
            result.OutsideStations.Should().BeEmpty();
            result.SamplesChecked.Should().Be(3);
        }

        [Fact]
        public void Check_SampleInUnwalkableCell_ReportsStation()
        {
            var (mask, env) = HalfWalkableMask();
            var samples = new List<(double, Pt2)>
            {
                (0, new Pt2(10, 50)),
                (10, new Pt2(75, 50)),   // x ≥ 50 → unwalkable half
                (20, new Pt2(30, 50)),
            };

            var result = ContainmentSampler.Check(samples, mask, env, 2.0);

            result.Contained.Should().BeFalse();
            result.OutsideStations.Should().Equal(new double[] { 10 });
        }

        [Fact]
        public void Check_SampleOutsideEnvelope_ReportsStation()
        {
            var (mask, env) = HalfWalkableMask();
            var samples = new List<(double, Pt2)>
            {
                (0, new Pt2(10, 50)),
                (10, new Pt2(-5, 50)),    // outside the AABB entirely
            };

            var result = ContainmentSampler.Check(samples, mask, env, 2.0);

            result.Contained.Should().BeFalse();
            result.OutsideStations.Should().Equal(new double[] { 10 });
        }

        [Fact]
        public void BuildStations_MergesMandatoryStations_DedupedAndSorted()
        {
            // P0-02: entity endpoints / curve apexes (mandatory) merge into the fixed grid,
            // sorted ascending and de-duplicated (the 20 coincides with a step boundary).
            var stations = ContainmentSampler.BuildStations(
                0, 35, 10, new double[] { 7.5, 20, 12.5 });

            stations.Should().Equal(new double[] { 0, 7.5, 10, 12.5, 20, 30, 35 });
        }

        [Fact]
        public void BuildStations_MandatoryOutsideRange_Ignored()
        {
            // Mandatory stations at/beyond the endpoints must not duplicate or extend the range.
            var stations = ContainmentSampler.BuildStations(
                0, 30, 10, new double[] { -5, 0, 30, 99 });

            stations.Should().Equal(new double[] { 0, 10, 20, 30 });
        }

        [Fact]
        public void BuildStations_ShortBulgeStation_CaughtByMandatory_MissedByFixedStep()
        {
            // The core P0-02 fix: a curve apex at 15 m falls between the 10 m and 20 m fixed
            // samples. Without it as a mandatory station the readback never probes there.
            var fixedOnly = ContainmentSampler.BuildStations(0, 30, 10);
            fixedOnly.Should().NotContain(15.0);

            var withApex = ContainmentSampler.BuildStations(0, 30, 10, new double[] { 15 });
            withApex.Should().Contain(15.0);
        }

        [Fact]
        public void Check_OutsideSamples_CarryStationAndPoint()
        {
            var (mask, env) = HalfWalkableMask();
            var samples = new List<(double, Pt2)>
            {
                (0, new Pt2(10, 50)),
                (15, new Pt2(75, 50)),   // unwalkable
            };

            var result = ContainmentSampler.Check(samples, mask, env, 2.0);

            result.Contained.Should().BeFalse();
            result.OutsideSamples.Should().ContainSingle();
            result.OutsideSamples[0].Station.Should().Be(15);
            result.OutsideSamples[0].Point.X.Should().Be(75);
        }

        [Fact]
        public void Check_PointExactlyOnMaxEdge_MapsToLastCellNotOutOfRange()
        {
            // Fully-walkable mask: a point exactly on the envelope max edge must be
            // accepted (mapped onto the last cell), not crash or report outside.
            const int n = 10;
            const double cell = 2.0;
            var mask = new bool[n, n];
            for (int x = 0; x < n; x++)
                for (int y = 0; y < n; y++)
                    mask[x, y] = true;
            var env = new Envelope(0, n * cell, 0, n * cell);

            var samples = new List<(double, Pt2)> { (0, new Pt2(n * cell, n * cell)) };

            var result = ContainmentSampler.Check(samples, mask, env, cell);

            result.Contained.Should().BeTrue();
        }
    }
}
