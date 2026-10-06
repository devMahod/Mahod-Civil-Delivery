using System;
using System.IO;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests
{
    public class SectionTrafficDirectionDecisionProfileTests
    {
        private const string ClHash =
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

        [Fact]
        public void Approval_IsLaneSpecific_ReplacesPriorApproval_AndRoundTrips()
        {
            var profile = Profile();
            SectionDecisionProfileService.ApproveTrafficDirection(
                profile, ClHash, "7ace", "ROAD", 4.250,
                TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment,
                " Natalie ", DateTime.Parse("2026-09-01T06:00:00Z").ToUniversalTime());
            SectionDecisionProfileService.ApproveTrafficDirection(
                profile, ClHash, "7ACE", "ROAD", 4.252,
                TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment,
                "Natalie", DateTime.Parse("2026-09-01T07:00:00Z").ToUniversalTime());

            profile.Sections.Decisions.TrafficDirections.Should().ContainSingle();
            var decision = profile.Sections.Decisions.TrafficDirections[0];
            decision.SourceHandle.Should().Be("7ACE");
            decision.Flow.Should().Be(SectionVehicleDirectionPlanner.AgainstFlowToken);
            decision.ApprovedBy.Should().Be("Natalie");
            decision.ApprovedAtUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);

            var directory = Path.Combine(Path.GetTempPath(),
                "mhd-traffic-direction-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "project-profile.yaml");
                var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(
                    profile, EstimateTraceIdentity.EffectiveProfileHash(profile), path);
                ProjectProfileWriter.Save(profile, path,
                    "approve traffic direction", "Natalie", expected);
                var reloaded = ProjectProfileLoader.LoadFromFile(path);

                reloaded.IsUsable.Should().BeTrue(string.Join("; ",
                    reloaded.Findings.ConvertAll(f => f.Code + ":" + f.Message)));
                reloaded.Profile!.Sections.Decisions.TrafficDirections
                    .Should().ContainSingle()
                    .Which.Flow.Should().Be(SectionVehicleDirectionPlanner.AgainstFlowToken);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void UnknownFlowOrPartialIdentity_CannotBeApproved()
        {
            var profile = Profile();

            Action unknown = () => SectionDecisionProfileService.ApproveTrafficDirection(
                profile, ClHash, "7ACE", "ROAD", 4.25,
                TrafficDirectionEvidenceLogic.RelativeFlow.Unknown,
                "Natalie", DateTime.UtcNow);
            Action partial = () => SectionDecisionProfileService.ApproveTrafficDirection(
                profile, "short", "7ACE", "ROAD", 4.25,
                TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment,
                "Natalie", DateTime.UtcNow);

            unknown.Should().Throw<ArgumentOutOfRangeException>();
            partial.Should().Throw<ArgumentException>();
            profile.Sections.Decisions.TrafficDirections.Should().BeEmpty();
        }

        [Fact]
        public void BatchApproval_CoversEveryDisplayedLaneBeforeMutatingProfile()
        {
            var profile = Profile();
            var first = DirectionRecord("7ACE", -3.5);
            var second = DirectionRecord("7ACF", 3.5);
            var firstDirection = first.TrafficDirections.Single();
            var secondDirection = second.TrafficDirections.Single();

            var count = SectionDecisionProfileService.ApproveTrafficDirectionsBatch(
                profile,
                new[] { first, second },
                new[]
                {
                    new SectionDecisionProfileService.TrafficDirectionBatchApproval(
                        first, firstDirection,
                        TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment),
                    new SectionDecisionProfileService.TrafficDirectionBatchApproval(
                        second, secondDirection,
                        TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment),
                },
                "Natalie", new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc));

            count.Should().Be(2);
            profile.Sections.Decisions.TrafficDirections.Should().HaveCount(2);
            profile.Sections.Decisions.TrafficDirections.Select(item => item.Flow)
                .Should().BeEquivalentTo(
                    SectionVehicleDirectionPlanner.AlongFlowToken,
                    SectionVehicleDirectionPlanner.AgainstFlowToken);
        }

        [Fact]
        public void PartialTrafficBatch_IsRejectedWithoutReplacingExistingDecision()
        {
            var profile = Profile();
            SectionDecisionProfileService.ApproveTrafficDirection(
                profile, ClHash, "AAAA", "ROAD", 1.0,
                TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment,
                "old", DateTime.UtcNow);
            var first = DirectionRecord("7ACE", -3.5);
            var second = DirectionRecord("7ACF", 3.5);

            var act = () => SectionDecisionProfileService.ApproveTrafficDirectionsBatch(
                profile,
                new[] { first, second },
                new[]
                {
                    new SectionDecisionProfileService.TrafficDirectionBatchApproval(
                        first, first.TrafficDirections.Single(),
                        TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment),
                },
                "Natalie", DateTime.UtcNow);

            act.Should().Throw<InvalidOperationException>();
            profile.Sections.Decisions.TrafficDirections.Should().ContainSingle()
                .Which.SourceHandle.Should().Be("AAAA");
        }

        [Fact]
        public void LoaderRejectsIncompleteOrCompetingTrafficDecisions()
        {
            var profile = Profile();
            profile.Sections.Decisions.TrafficDirections.Add(new()
            {
                SourceDrawingHash = ClHash,
                SourceHandle = "7ACE",
                AlignmentName = "ROAD",
                LaneMidOffsetM = 4.250,
                Flow = SectionVehicleDirectionPlanner.AlongFlowToken,
                ApprovedBy = "Natalie",
                ApprovedAtUtc = DateTime.UtcNow,
            });
            profile.Sections.Decisions.TrafficDirections.Add(new()
            {
                SourceDrawingHash = ClHash,
                SourceHandle = "7ACE",
                AlignmentName = "ROAD",
                LaneMidOffsetM = 4.253,
                Flow = SectionVehicleDirectionPlanner.AgainstFlowToken,
                ApprovedBy = "Natalie",
                ApprovedAtUtc = DateTime.UtcNow,
            });
            profile.Sections.Decisions.TrafficDirections.Add(new()
            {
                SourceDrawingHash = "short",
                SourceHandle = "7ACE",
            });

            var findings = ProjectProfileLoader.Validate(profile);

            findings.Should().Contain(f =>
                f.Code == "SHR-SECTION-TRAFFIC-DIRECTION-DUPLICATE");
            findings.Should().Contain(f =>
                f.Code == "SHR-SECTION-TRAFFIC-DIRECTION-INCOMPLETE");
        }

        [Fact]
        public void ExplicitNullTrafficDecisionList_IsAControlledFinding_NotACrash()
        {
            var loaded = ProjectProfileLoader.LoadFromText("""
                schema_version: 1
                profile_id: traffic-null-test
                sections:
                  decisions:
                    traffic_directions: null
                """);

            loaded.Findings.Should().Contain(f =>
                f.Code == "SHR-SECTION-TRAFFIC-DIRECTION-LIST-NULL");
            loaded.IsUsable.Should().BeFalse();
        }

        private static ProjectProfile Profile() => new()
        {
            ProfileId = "traffic-direction-test",
            Sections =
            {
                Cl =
                {
                    SourceFiles = { "CL.dwg" },
                    IntersectionToleranceM = 0.01,
                },
            },
        };

        private static SectionPlanRecord DirectionRecord(string handle, double laneOffset)
        {
            var record = new SectionPlanRecord
            {
                RecordId = "row-" + handle,
                SelectedAlignment = "ROAD",
                Cl = new ClSourceRecord
                {
                    RecordId = "row-" + handle,
                    SourceDrawing = "CL.dwg",
                    SourceDrawingHash = ClHash,
                    SourceHandle = handle,
                    SourceEntityType = "LINE",
                    SourceLayer = "CL",
                    SourceEndpoints = new[] { 0d, 0d, 10d, 0d },
                    WcsEndpoints = new[] { 0d, 0d, 10d, 0d },
                },
            };
            record.TrafficDirections.Add(new SectionTrafficDirectionPlan
            {
                FromOffsetM = laneOffset - 1.5,
                ToOffsetM = laneOffset + 1.5,
                LaneMidOffsetM = laneOffset,
                StripLabel = "נתיב נסיעה",
                StripKind = "road",
                EvidenceMode = "motor",
                State = "unknown",
                Reason = "manual decision required",
            });
            return record;
        }
    }
}
