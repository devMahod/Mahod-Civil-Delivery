using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Existing systems must appear in the sections — that is the core project
    /// requirement. A section that silently omits a utility present in the drawing
    /// is the exact failure these tests exist to prevent (directive §16).
    /// </summary>
    public class UtilityCoverageTests
    {
        [Fact]
        public void ProjectionCoverage_DefaultsToNotRunInsteadOfPretendingTheScanCompleted()
        {
            var coverage = new UtilityCoverageReport();

            coverage.ProjectionScanState.Should().Be(UtilityProjectionScanState.NotRun);
            coverage.ProjectionDrawingEntityCount.Should().Be(0);
            coverage.ProjectionSectionCrossingCount.Should().Be(0);
        }

        private static SectionPlanRecord Record() => new()
        {
            RecordId = "r1",
            Cl = new ClSourceRecord
            {
                RecordId = "r1",
                SourceDrawing = "CL.dwg",
                SourceDrawingHash = "h",
                SourceHandle = "AB",
                SourceEntityType = "LINE",
                SourceLayer = "CL",
                SourceEndpoints = new double[] { 0, 10, 0, -10 },
                WcsEndpoints = new double[] { 0, 10, 0, -10 },
            },
            Status = DeliveryStatus.Ready,
        };

        private static DiscoveredUtility Util(string name, string kind = "pipe-network", bool native = true) =>
            new() { Name = name, Kind = kind, Handle = "H" + name, NativelySampleable = native };

        private static SectionSourcePlan Planned(string name, string type = "pipe-network", string state = "sampled") =>
            new()
            {
                SourceName = name,
                SourceType = type,
                NativeSampleCapability = true,
                PlannedState = state,
                Required = true,
                Status = DeliveryStatus.Ready,
            };

        private static ProjectProfile Profile() => new() { ProfileId = "6422" };

        [Fact]
        public void UtilityInDrawingButNotConfigured_IsSurfacedNotSilent()
        {
            var r = Record();

            SectionPlanLogic.BuildUtilityCoverage(r, new[] { Util("WATER-EXIST") }, Profile());

            r.UtilityCoverage.NotConfigured.Should().ContainSingle().Which.Should().Be("WATER-EXIST");
            r.UtilityCoverage.Represented.Should().BeEmpty();
            r.UtilityCoverage.Complete.Should().BeFalse();
            r.Status.Should().Be(DeliveryStatus.ReviewRequired,
                "a section that would omit an existing system is not READY");
            r.Findings.Should().Contain(f => f.Code == SectionFindingCodes.UtilityUnsupported);
        }

        [Fact]
        public void ConfiguredAndPresentUtility_IsRepresented()
        {
            var r = Record();
            r.PlannedSources.Add(Planned("WATER-EXIST"));

            SectionPlanLogic.BuildUtilityCoverage(r, new[] { Util("WATER-EXIST") }, Profile());

            r.UtilityCoverage.Represented.Should().ContainSingle().Which.Should().Be("WATER-EXIST");
            r.UtilityCoverage.NotConfigured.Should().BeEmpty();
            r.UtilityCoverage.Complete.Should().BeTrue();
            r.Status.Should().Be(DeliveryStatus.Ready);
            r.Findings.Should().BeEmpty();
        }

        [Fact]
        public void ConfiguredButAbsentUtility_IsMissingAndReview()
        {
            var r = Record();
            r.PlannedSources.Add(Planned("SEWER-EXIST"));

            SectionPlanLogic.BuildUtilityCoverage(r, new List<DiscoveredUtility>(), Profile());

            r.UtilityCoverage.Missing.Should().ContainSingle().Which.Should().Be("SEWER-EXIST");
            r.Status.Should().Be(DeliveryStatus.ReviewRequired);
            r.Findings.Should().Contain(f => f.Code == SectionFindingCodes.SourceMissing);
        }

        [Fact]
        public void NonNativeUtilityWithoutAdapter_IsUnsupportedWithReason()
        {
            var r = Record();

            SectionPlanLogic.BuildUtilityCoverage(
                r, new[] { Util("OLD-DUCT", kind: "polyline-3d", native: false) }, Profile());

            r.UtilityCoverage.Unsupported.Should().ContainKey("OLD-DUCT");
            r.UtilityCoverage.Unsupported["OLD-DUCT"].Should().Contain("adapter");
            r.Status.Should().Be(DeliveryStatus.ReviewRequired);
        }

        [Fact]
        public void PressureNetwork_CountsAsNativelySampleable()
        {
            var r = Record();
            r.PlannedSources.Add(Planned("PRESSURE-MAIN", type: "pressure-network"));

            SectionPlanLogic.BuildUtilityCoverage(
                r, new[] { Util("PRESSURE-MAIN", kind: "pressure-network") }, Profile());

            r.UtilityCoverage.Represented.Should().Contain("PRESSURE-MAIN");
            r.UtilityCoverage.Complete.Should().BeTrue();
        }

        [Fact]
        public void WildcardRule_AccountsForEveryDiscoveredUtility()
        {
            var r = Record();
            r.PlannedSources.Add(Planned("UT-*"));

            SectionPlanLogic.BuildUtilityCoverage(r, new[] { Util("UT-WATER"), Util("UT-SEWER") }, Profile());

            (r.UtilityCoverage.Represented.Count + r.UtilityCoverage.NotConfigured.Count)
                .Should().Be(2, "every discovered utility is accounted for exactly once");
        }

        [Fact]
        public void NoUtilitiesAnywhere_IsCompleteAndQuiet()
        {
            var r = Record();

            SectionPlanLogic.BuildUtilityCoverage(r, new List<DiscoveredUtility>(), Profile());

            r.UtilityCoverage.Complete.Should().BeTrue("nothing exists, so nothing is omitted");
            r.Status.Should().Be(DeliveryStatus.Ready);
            r.Findings.Should().BeEmpty();
        }

        [Fact]
        public void MixedCase_ReportsEveryCategoryWithSummary()
        {
            var r = Record();
            r.PlannedSources.Add(Planned("WATER"));
            r.PlannedSources.Add(Planned("GONE"));

            SectionPlanLogic.BuildUtilityCoverage(r, new[]
            {
                Util("WATER"),
                Util("SEWER"),
                Util("DUCT", kind: "polyline-3d", native: false),
            }, Profile());

            r.UtilityCoverage.Represented.Should().BeEquivalentTo(new[] { "WATER" });
            r.UtilityCoverage.NotConfigured.Should().BeEquivalentTo(new[] { "SEWER" });
            r.UtilityCoverage.Missing.Should().BeEquivalentTo(new[] { "GONE" });
            r.UtilityCoverage.Unsupported.Keys.Should().BeEquivalentTo(new[] { "DUCT" });
            r.UtilityCoverage.Summary.Should().Contain("1 represented");
            r.UtilityCoverage.Complete.Should().BeFalse();
        }

        [Fact]
        public void Coverage_IsRebuiltNotAccumulatedOnRepeatCalls()
        {
            var r = Record();
            r.PlannedSources.Add(Planned("WATER"));

            SectionPlanLogic.BuildUtilityCoverage(r, new[] { Util("WATER") }, Profile());
            SectionPlanLogic.BuildUtilityCoverage(r, new[] { Util("WATER") }, Profile());

            r.UtilityCoverage.Represented.Should().ContainSingle("a re-plan must not double-count");
            r.UtilityCoverage.Relevant.Should().ContainSingle();
        }

        [Fact]
        public void SurfacesAndCorridors_AreNotTreatedAsUtilities()
        {
            var r = Record();
            r.PlannedSources.Add(new SectionSourcePlan
            {
                SourceName = "EG",
                SourceType = "surface",
                NativeSampleCapability = true,
                PlannedState = "sampled",
                Required = true,
            });

            SectionPlanLogic.BuildUtilityCoverage(r, new List<DiscoveredUtility>(), Profile());

            r.UtilityCoverage.Missing.Should().BeEmpty("a surface is a terrain source, not a utility");
            r.Status.Should().Be(DeliveryStatus.Ready);
        }
    }
}
