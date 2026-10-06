using System;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Alignment;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Routing
{
    /// <summary>Pure tests for the host-free station-labelling math.</summary>
    public class StationLabelGeometryTests
    {
        [Fact]
        public void Builds_interval_stations_with_endpoints()
        {
            var s = StationLabelGeometry.BuildStationList(0, 100, 20);
            s.Should().Equal(0, 20, 40, 60, 80, 100);
        }

        [Fact]
        public void Includes_exact_start_and_end_off_the_interval_grid()
        {
            var s = StationLabelGeometry.BuildStationList(12.5, 71.0, 20);
            s[0].Should().Be(12.5);          // exact start
            s[^1].Should().Be(71.0);         // exact end
            s.Should().Contain(20).And.Contain(40).And.Contain(60);
            // No interior multiple is duplicated next to an endpoint.
            s.Should().OnlyHaveUniqueItems();
        }

        [Fact]
        public void Formats_chainage_in_k_plus_sss()
        {
            StationLabelGeometry.FormatChainage(50).Should().Be("0+050");
            StationLabelGeometry.FormatChainage(1250).Should().Be("1+250");
            StationLabelGeometry.FormatChainage(1250.5).Should().Be("1+250.50");
            StationLabelGeometry.FormatChainage(0).Should().Be("0+000");
        }

        [Fact]
        public void Section_index_counts_intervals_from_start()
        {
            StationLabelGeometry.SectionIndex(60, 0, 20).Should().Be(3);
            StationLabelGeometry.SectionIndex(12.5, 12.5, 20).Should().Be(0);
        }

        [Fact]
        public void Upright_rotation_flips_left_pointing_text()
        {
            // Eastbound: unchanged.
            StationLabelGeometry.UprightRotation(0).Should().BeApproximately(0, 1e-9);
            // Westbound (π): flipped to 0 so it stays upright.
            StationLabelGeometry.UprightRotation(Math.PI).Should().BeApproximately(0, 1e-9);
            // Just past vertical-down stays within [-π/2, π/2] after the flip.
            var r = StationLabelGeometry.UprightRotation(2.0);
            r.Should().BeInRange(-Math.PI / 2 - 1e-9, Math.PI / 2 + 1e-9);
        }
    }
}
