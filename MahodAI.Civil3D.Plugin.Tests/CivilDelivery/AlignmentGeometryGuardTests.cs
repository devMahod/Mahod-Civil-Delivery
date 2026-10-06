using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Project 984 (employee install, 06.10.2026) holds an alignment "4" with length 0 and no entities. Civil's
    /// IntersectWith throws eDegenerateGeometry on it, so b34 marked the setup scan incomplete for every layer
    /// (saving blocked) and picking the separate CL drawing aborted with the bare error status. An empty alignment is
    /// skipped and reported as information; a read failure is not emptiness and stays fail-closed.
    /// </summary>
    public class AlignmentGeometryGuardTests
    {
        private static string PluginSourceDir =>
            typeof(AlignmentGeometryGuardTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { PluginSourceDir }.Concat(parts).ToArray()));

        [Theory]
        [InlineData(0.0, 0, true)]          // the 984 alignment "4"
        [InlineData(0.0, null, true)]       // zero length even when the entity list cannot be read
        [InlineData(5e-7, 1, true)]         // below the usable-length floor
        [InlineData(137.53, 0, true)]       // a stale length with no entities left
        [InlineData(137.53, 5, false)]      // the 984 alignment "100"
        [InlineData(137.53, null, false)]   // unreadable entities: not proven empty
        [InlineData(double.NaN, 5, false)]  // unreadable length: a read failure, not emptiness
        [InlineData(-0.5, 5, false)]        // a negative length is contradictory evidence, not emptiness
        [InlineData(-0.5, 0, true)]         // ...but a readable zero entity count still proves emptiness
        [InlineData(double.PositiveInfinity, null, false)]
        public void Only_proven_empty_geometry_is_skipped(double length, int? entities, bool expected) =>
            AlignmentGeometryGuard.HasNoGeometry(length, entities).Should().Be(expected);

        [Fact]
        public void The_finding_is_information_in_Hebrew_and_reported_once_per_alignment()
        {
            var findings = new List<DeliveryFinding>();
            AlignmentGeometryGuard.Report(findings, "4", "p984");
            AlignmentGeometryGuard.Report(findings, "4", "p984");
            AlignmentGeometryGuard.Report(findings, "7", "p984");

            findings.Should().HaveCount(2);
            findings.Should().OnlyContain(f => f.Severity == FindingSeverity.Info &&
                f.Code == AlignmentGeometryGuard.FindingCode && f.ProjectProfileId == "p984");
            findings[0].Title.Should().Contain("תוואי ריק").And.Contain("4");
            findings[0].Message.Should().Contain("אינו משתתף");
        }

        [Fact]
        public void Every_crossing_probe_skips_an_empty_alignment_before_IntersectWith()
        {
            var scanner = Read("CivilDelivery", "Sections", "Services", "ProjectSetupScanner.cs");
            var collect = scanner.IndexOf("private List<(CivilDb.Alignment Alignment, AlignmentCandidateSummary Summary)> CollectAlignments(");
            collect.Should().BeGreaterThan(0);
            scanner.IndexOf("AlignmentGeometryGuard.Read(a)", collect).Should().BeGreaterThan(collect);

            var resolver = Read("CivilDelivery", "Sections", "Services", "AlignmentCandidateResolver.cs");
            var guard = resolver.IndexOf("AlignmentGeometryGuard.Read(alignment)");
            guard.Should().BeGreaterThan(0);
            guard.Should().BeLessThan(resolver.IndexOf("CollectCrossingsWithAlignment(alignment, a3, b3"));
        }

        [Fact]
        public void A_separate_CL_drawing_never_surfaces_a_bare_Civil_error_status()
        {
            var scanner = Read("CivilDelivery", "Sections", "Services", "ProjectSetupScanner.cs");
            scanner.Should().NotContain("if (scan == null) throw;\n                            MarkIncomplete(scan,\n                                $\"crossing probe");
            scanner.Should().Contain("בדיקת החציה של שכבה");
        }
    }
}
