using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public class SectionPlanLogicTests
    {
        private static ClSourceRecord Cl(double x1, double y1, double x2, double y2, string handle = "ABC1") => new()
        {
            RecordId = "r1",
            SourceDrawing = "CL.dwg",
            SourceDrawingHash = "hash-cl",
            SourceHandle = handle,
            SourceEntityType = "LINE",
            SourceLayer = "CL",
            SourceEndpoints = new[] { x1, y1, x2, y2 },
            WcsEndpoints = new[] { x1, y1, x2, y2 },
        };

        private static SectionPlanRecord Record(ClSourceRecord cl) => new() { RecordId = cl.RecordId, Cl = cl };

        private static SectionSourcePlan Sampled(string name, bool required = true) => new()
        {
            SourceName = name,
            SourceType = "surface",
            NativeSampleCapability = true,
            PlannedState = "sampled",
            AdapterRequired = false,
            Required = required,
        };

        private static AlignmentCrossing Crossing(
            string name, double x, double y, double station, double tangent) => new()
        {
            AlignmentName = name,
            Point = new[] { x, y },
            Station = station,
            TangentDeg = tangent,
        };

        private static ProjectProfile Profile() => new() { ProfileId = "6422" };

        [Fact]
        public void NoCrossing_IsReviewRequired_WithLockedCode()
        {
            var r = Record(Cl(0, 10, 0, -10));
            SectionPlanLogic.ResolveAlignment(r, new List<AlignmentCrossing>(), Profile());

            r.Status.Should().Be(DeliveryStatus.ReviewRequired);
            r.Findings.Should().ContainSingle(f => f.Code == SectionFindingCodes.ClNoIntersection);
            r.SelectedAlignment.Should().BeNull();
        }

        [Fact]
        public void TwoAlignments_IsAmbiguous_NeverSilentlyPicked()
        {
            var r = Record(Cl(0, 10, 0, -10));
            SectionPlanLogic.ResolveAlignment(r, new List<AlignmentCrossing>
            {
                Crossing("A-North", 0, 2, 100, 0),
                Crossing("A-South", 0, -2, 250, 0),
            }, Profile());

            r.Status.Should().Be(DeliveryStatus.ReviewRequired);
            r.Findings.Should().ContainSingle(f => f.Code == SectionFindingCodes.AlignmentAmbiguous);
            r.SelectedAlignment.Should().BeNull("silent nearest-pick is forbidden by the locked plan");
        }

        [Fact]
        public void OneAlignmentTwice_IsMultipleIntersections()
        {
            var r = Record(Cl(0, 10, 0, -10));
            SectionPlanLogic.ResolveAlignment(r, new List<AlignmentCrossing>
            {
                Crossing("LOOP", 0, 3, 120, 10),
                Crossing("LOOP", 0, -3, 480, 190),
            }, Profile());

            r.Status.Should().Be(DeliveryStatus.ReviewRequired);
            r.Findings.Should().ContainSingle(f => f.Code == SectionFindingCodes.ClMultipleIntersections);
        }

        [Fact]
        public void ExactApprovedAlignmentAndStation_ResolvesOneOfMultipleCrossings()
        {
            var profile = Profile();
            profile.Sections.Decisions.Crossings.Add(new
                ProjectProfile.SectionsProfile.DecisionsProfile.CrossingDecision
                {
                    SourceDrawingHash = "hash-cl",
                    SourceHandle = "ABC1",
                    AlignmentName = "LOOP",
                    Station = 480.0,
                    ApprovedBy = "nataly",
                    ApprovedAtUtc = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
                });
            var r = Record(Cl(0, 10, 0, -10));

            SectionPlanLogic.ResolveAlignment(r, new List<AlignmentCrossing>
            {
                Crossing("LOOP", 0, 3, 120, 10),
                Crossing("LOOP", 0, -3, 480, 190),
            }, profile);

            r.Status.Should().Be(DeliveryStatus.Ready);
            r.SelectedAlignment.Should().Be("LOOP");
            r.Station.Should().Be(480.0,
                "the persisted station selects the exact candidate, not merely an alignment name");
        }

        [Theory]
        [InlineData(false, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        public void Exclusion_RequiresReasonApproverAndTimestamp(
            bool reason, bool approver, bool timestamp)
        {
            var profile = Profile();
            profile.Sections.Decisions.Exclusions.Add(new
                ProjectProfile.SectionsProfile.DecisionsProfile.ExclusionDecision
                {
                    SourceDrawingHash = "hash-cl",
                    SourceHandle = "ABC1",
                    FindingCode = SectionFindingCodes.ClNoIntersection,
                    Reason = reason ? "not part of delivery" : " ",
                    ApprovedBy = approver ? "nataly" : " ",
                    ApprovedAtUtc = timestamp ? DateTime.UtcNow : null,
                });
            var r = Record(Cl(0, 10, 0, -10));

            SectionPlanLogic.TryApplyExplicitExclusion(r, profile).Should().BeFalse();
            r.Action.Should().NotBe(PlanAction.Excluded);
            r.Status.Should().Be(DeliveryStatus.ReviewRequired);
        }

        [Fact]
        public void CompleteExclusion_ResolvesOnlyWhenExactFindingWasReproduced()
        {
            var profile = Profile();
            profile.Sections.Decisions.Exclusions.Add(new
                ProjectProfile.SectionsProfile.DecisionsProfile.ExclusionDecision
                {
                    SourceDrawingHash = "hash-cl",
                    SourceHandle = "ABC1",
                    FindingCode = SectionFindingCodes.ClNoIntersection,
                    Reason = "not part of delivery",
                    ApprovedBy = "nataly",
                    ApprovedAtUtc = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
                });
            var r = Record(Cl(0, 10, 0, -10));
            r.Findings.Add(new DeliveryFinding
            {
                Code = SectionFindingCodes.ClNoIntersection,
                Domain = SectionPlanLogic.Domain,
                Severity = FindingSeverity.ReviewRequired,
                Title = "current no-intersection finding",
            });

            SectionPlanLogic.TryApplyExplicitExclusion(r, profile).Should().BeTrue();
            SectionPlanLogic.HasValidExplicitExclusion(r).Should().BeTrue();
            r.Action.Should().Be(PlanAction.Excluded);
            r.ExplicitExclusion!.Reason.Should().Be("not part of delivery");
            r.ExplicitExclusion.FindingCode.Should().Be(SectionFindingCodes.ClNoIntersection);
        }

        [Fact]
        public void StaleExclusion_CannotSkipCurrentPlanChecks()
        {
            var profile = Profile();
            profile.Sections.Decisions.Exclusions.Add(new
                ProjectProfile.SectionsProfile.DecisionsProfile.ExclusionDecision
                {
                    SourceDrawingHash = "hash-cl",
                    SourceHandle = "ABC1",
                    FindingCode = SectionFindingCodes.ClNoIntersection,
                    Reason = "historic condition",
                    ApprovedBy = "nataly",
                    ApprovedAtUtc = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
                });
            var record = Record(Cl(0, 10, 0, -10));
            record.Findings.Add(new DeliveryFinding
            {
                Code = SectionFindingCodes.StyleMissing,
                Domain = SectionPlanLogic.Domain,
                Severity = FindingSeverity.ReviewRequired,
                Title = "a different current finding",
            });

            SectionPlanLogic.TryApplyExplicitExclusion(record, profile).Should().BeFalse();
            record.Action.Should().NotBe(PlanAction.Excluded);
            record.ExplicitExclusion.Should().BeNull();
            record.Findings.Should().Contain(f =>
                f.Code == SectionFindingCodes.ExclusionStale);
        }

        [Fact]
        public void TwentySevenReadyAndFiftySevenUnresolved_CannotEnterApplyOrVerifyFlow()
        {
            SectionPlanRecord R(string id, bool ready) => new()
            {
                RecordId = id,
                Cl = Cl(0, 10, 0, -10, id),
                Status = ready ? DeliveryStatus.Ready : DeliveryStatus.ReviewRequired,
                Action = ready ? PlanAction.Create : PlanAction.ReviewRequired,
            };
            var plan = new SectionPlan { RunId = "run", ProjectProfileId = "6422" };
            plan.Records.AddRange(Enumerable.Range(0, 27).Select(i => R($"ready-{i}", true)));
            plan.Records.AddRange(Enumerable.Range(0, 57).Select(i => R($"review-{i}", false)));

            var partialApply = new SectionApplyResult { RunId = "partial", Committed = true };
            partialApply.Records.AddRange(plan.Records.Take(27).Select(r => new SectionApplyRecordResult
            {
                RecordId = r.RecordId,
                ActionTaken = PlanAction.Create,
                Status = DeliveryStatus.Applied,
            }));

            SectionPlanLogic.UnresolvedBatchRecords(plan).Should().HaveCount(57);
            var gate = WorkflowGate.From(
                profileUsable: true, plan, planStale: false, apply: partialApply, previewShown: false);
            gate.CanApply.Should().BeFalse();
            gate.CanVerify.Should().BeFalse();
            SectionPlanLogic.UnresolvedVerificationRecords(plan, partialApply).Should().HaveCount(57);
            gate.Reason.Should().Contain("57");
        }

        [Fact]
        public void UnchangedPlanClassification_IsNotVerificationEvidence()
        {
            var record = new SectionPlanRecord
            {
                RecordId = "A1",
                Cl = Cl(0, 10, 0, -10, "A1"),
                Status = DeliveryStatus.Ready,
                Action = PlanAction.Unchanged,
            };
            var plan = new SectionPlan
            {
                RunId = "run",
                ProjectProfileId = "6422",
                Records = { record },
            };
            var fabricatedNoOp = new SectionApplyResult { RunId = "apply", Committed = true };
            fabricatedNoOp.Records.Add(new SectionApplyRecordResult
            {
                RecordId = record.RecordId,
                ActionTaken = PlanAction.Unchanged,
                Status = DeliveryStatus.Verified,
            });

            SectionPlanLogic.UnresolvedVerificationRecords(plan, fabricatedNoOp)
                .Should().ContainSingle().Which.Should().BeSameAs(record);

            fabricatedNoOp.Records[0].Status = DeliveryStatus.Applied;
            SectionPlanLogic.UnresolvedVerificationRecords(plan, fabricatedNoOp)
                .Should().BeEmpty("only an actual APPLY record can establish object evidence");
        }

        [Fact]
        public void SingleValidCrossing_BecomesReady_WithDerivedGeometry()
        {
            // East-heading alignment at y=0, CL from 12 left to 18 right.
            var r = Record(Cl(0, 12, 0, -18));
            SectionPlanLogic.ResolveAlignment(r, new List<AlignmentCrossing>
            {
                Crossing("MAIN", 0, 0, 1086.0, 0),
            }, Profile());

            r.Status.Should().Be(DeliveryStatus.Ready);
            r.SelectedAlignment.Should().Be("MAIN");
            r.Station.Should().Be(1086.0);
            r.SkewDeg.Should().BeApproximately(0, 1e-6);
            r.LeftExtent.Should().BeApproximately(12, 1e-6);
            r.RightExtent.Should().BeApproximately(18, 1e-6);
            r.Findings.Should().BeEmpty();
        }

        [Fact]
        public void SkewedCl_PreservesSkew_NotForcedPerpendicular()
        {
            // CL at 60° vs east alignment (normal is 90°) => skew -30°.
            var r = Record(Cl(-10 * 0.5, -10 * 0.866, 10 * 0.5, 10 * 0.866));
            SectionPlanLogic.ResolveAlignment(r, new List<AlignmentCrossing>
            {
                Crossing("MAIN", 0, 0, 500, 0),
            }, Profile());

            r.Status.Should().Be(DeliveryStatus.Ready);
            r.SkewDeg.Should().BeApproximately(-30, 1e-3);
        }

        [Fact]
        public void DegenerateCl_Fails()
        {
            var r = Record(Cl(5, 5, 5, 5));
            SectionPlanLogic.ResolveAlignment(r, new List<AlignmentCrossing>(), Profile());

            r.Status.Should().Be(DeliveryStatus.Failed);
            r.Findings.Should().ContainSingle(f => f.Code == SectionFindingCodes.ClDegenerate);
        }

        [Fact]
        public void EndpointsOnOneSide_IsReview_NoInventedExtents()
        {
            // Both endpoints above the alignment: grazing, swath undefined.
            var r = Record(Cl(0, 2, 0, 30));
            SectionPlanLogic.ResolveAlignment(r, new List<AlignmentCrossing>
            {
                Crossing("MAIN", 0, 2, 700, 0),
            }, Profile());

            r.Status.Should().Be(DeliveryStatus.ReviewRequired);
        }

        [Fact]
        public void ExplicitProfileMapping_WinsOverAmbiguity()
        {
            var profile = Profile();
            profile.Sections.Alignments.ExplicitSourceToAlignment["ABC1"] = "A-South";

            var r = Record(Cl(0, 10, 0, -10));
            SectionPlanLogic.ResolveAlignment(r, new List<AlignmentCrossing>
            {
                Crossing("A-North", 0, 2, 100, 0),
                Crossing("A-South", 0, -2, 250, 0),
            }, profile);

            r.Status.Should().Be(DeliveryStatus.Ready);
            r.SelectedAlignment.Should().Be("A-South");
            r.Station.Should().Be(250);
        }

        [Fact]
        public void ExplicitMapping_ToNonCrossingAlignment_IsReview()
        {
            var profile = Profile();
            profile.Sections.Alignments.ExplicitSourceToAlignment["ABC1"] = "ELSEWHERE";

            var r = Record(Cl(0, 10, 0, -10));
            SectionPlanLogic.ResolveAlignment(r, new List<AlignmentCrossing>
            {
                Crossing("A-North", 0, 2, 100, 0),
            }, profile);

            r.Status.Should().Be(DeliveryStatus.ReviewRequired);
            r.SelectedAlignment.Should().BeNull();
        }

        [Fact]
        public void NumberingValidation_Disabled_NeverFlags()
        {
            var cl = Cl(0, 10, 0, -10);
            cl.CandidateSectionNumber = "1086";
            var r = Record(cl);
            r.Station = 2000; // wildly different from the label

            SectionPlanLogic.ValidateNumbering(r, Profile());

            r.Findings.Should().BeEmpty("1086 == 1+086 is a hypothesis until the profile confirms the rule");
        }

        [Fact]
        public void NumberingValidation_EnabledAndMismatched_IsReview()
        {
            var profile = Profile();
            profile.Sections.Cl.Numbering.StationValidationEnabled = true;
            profile.Sections.Cl.Numbering.StationValidationToleranceM = 1.0;

            var cl = Cl(0, 10, 0, -10);
            cl.CandidateSectionNumber = "1086";
            var r = Record(cl);
            r.Station = 1200;

            SectionPlanLogic.ValidateNumbering(r, profile);

            r.Findings.Should().ContainSingle(f => f.Code == SectionFindingCodes.ClStationLabelMismatch);
            r.Status.Should().Be(DeliveryStatus.ReviewRequired);
        }

        [Fact]
        public void NumberingValidation_EnabledAndClose_Passes()
        {
            var profile = Profile();
            profile.Sections.Cl.Numbering.StationValidationEnabled = true;
            profile.Sections.Cl.Numbering.StationValidationToleranceM = 1.0;

            var cl = Cl(0, 10, 0, -10);
            cl.CandidateSectionNumber = "חתך 1086";
            var r = Record(cl);
            r.Station = 1086.4;

            SectionPlanLogic.ValidateNumbering(r, profile);

            r.Findings.Should().BeEmpty();
        }

        [Fact]
        public void ExpectedGroupSampling_IsExactAlignmentWideUnion()
        {
            var first = Record(Cl(0, 10, 0, -10, "A1"));
            first.SelectedAlignment = "700";
            first.PlannedSources.AddRange(new[] { Sampled("MK"), Sampled("700D@Top@01") });
            first.UtilityCoverage.Represented.Add("PIPE-A");

            var second = Record(Cl(10, 10, 10, -10, "A2"));
            second.SelectedAlignment = "700";
            second.PlannedSources.AddRange(new[] { Sampled("MK"), Sampled("700D@Top@01") });
            second.UtilityCoverage.Represented.Add("PIPE-B");

            var otherAxis = Record(Cl(20, 10, 20, -10, "B1"));
            otherAxis.SelectedAlignment = "2000";
            otherAxis.PlannedSources.AddRange(new[] { Sampled("MK"), Sampled("2000-DESIGN-FINAL") });

            var plan = new SectionPlan
            {
                RunId = "run",
                ProjectProfileId = "6422",
                Records = { first, second, otherAxis },
            };

            var group = SectionPlanLogic.ExpectedGroupSampling(plan, "700");

            group.Names.Should().BeEquivalentTo("MK", "700D@Top@01", "PIPE-A", "PIPE-B");
            group.RequiredNames.Should().BeEquivalentTo("MK", "700D@Top@01");
            group.Names.Should().NotContain("2000-DESIGN-FINAL",
                "a shared group is still scoped to exactly one alignment");
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public void AnyMutationSelectsTheWholeReadyManagedBatchAndRefusesPartialApproval()
        {
            SectionPlanRecord R(string id, PlanAction action) => new()
            {
                RecordId = id, Cl = Cl(0, 10, 0, -10, id),
                Status = DeliveryStatus.Ready, Action = action,
            };
            var changedA = R("A", PlanAction.Update);
            var changedB = R("B", PlanAction.Create);
            var unchanged = R("C", PlanAction.Unchanged);
            var plan = new SectionPlan
            {
                RunId = "run", ProjectProfileId = "6422",
                Records = { changedA, changedB, unchanged },
            };

            SectionApplyService.SelectTargets(plan, null)
                .Select(r => r.RecordId).Should().BeEquivalentTo("A", "B", "C");

            var act = () => SectionApplyService.SelectTargets(plan, new[] { "A" });
            act.Should().Throw<InvalidOperationException>().WithMessage("*Partial section APPLY*");

            var omitsUnchanged = () => SectionApplyService.SelectTargets(plan, new[] { "A", "B" });
            omitsUnchanged.Should().Throw<InvalidOperationException>()
                .WithMessage("*Partial section APPLY*", "full-batch recreation also touches unchanged views");
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public void SelectedApplyTarget_IsOneExactCurrentPlanRecord_WithoutBroadeningScope()
        {
            SectionPlanRecord R(string id) => new()
            {
                RecordId = id,
                Cl = Cl(0, 10, 0, -10, id),
                Status = DeliveryStatus.Ready,
                Action = PlanAction.Create,
            };
            var selected = R("A");
            var other = R("B");
            var plan = new SectionPlan
            {
                RunId = "run",
                ProjectProfileId = "6422",
                Records = { selected, other },
            };

            SectionApplyService.SelectSingleTarget(plan, "A")
                .Should().BeSameAs(selected);
            var missing = () => SectionApplyService.SelectSingleTarget(plan, "C");
            missing.Should().Throw<InvalidOperationException>();
            plan.Records.Should().Equal(new[] { selected, other },
                "selection does not rewrite, exclude or mark omitted PLAN records");
        }

        [Theory]
        [InlineData(null, "f-new", true, PlanAction.Create)]
        [InlineData("f-same", "f-same", true, PlanAction.Unchanged)]
        [InlineData("f-old", "f-new", true, PlanAction.Update)]
        [InlineData("f-old", "f-new", false, PlanAction.ReviewRequired)]
        public void RerunDecision_FollowsOwnershipAndFingerprint(
            string? existing, string fresh, bool toolOwned, PlanAction expected)
        {
            SectionPlanLogic.DecideRerunAction(existing, fresh, toolOwned).Should().Be(expected);
        }

        [Fact]
        public void Fingerprint_StableForSameInputs_ChangesWhenGeometryChanges()
        {
            var r1 = Record(Cl(0, 12, 0, -18));
            SectionPlanLogic.ResolveAlignment(r1, new List<AlignmentCrossing>
            {
                Crossing("MAIN", 0, 0, 1086.0, 0),
            }, Profile());

            var r2 = Record(Cl(0, 12, 0, -18));
            SectionPlanLogic.ResolveAlignment(r2, new List<AlignmentCrossing>
            {
                Crossing("MAIN", 0, 0, 1086.0, 0),
            }, Profile());

            SectionPlanLogic.ComputeFingerprint(r1).Should().Be(SectionPlanLogic.ComputeFingerprint(r2));

            var r3 = Record(Cl(0, 12, 0, -25)); // wider swath
            SectionPlanLogic.ResolveAlignment(r3, new List<AlignmentCrossing>
            {
                Crossing("MAIN", 0, 0, 1086.0, 0),
            }, Profile());

            SectionPlanLogic.ComputeFingerprint(r3).Should().NotBe(SectionPlanLogic.ComputeFingerprint(r1));
        }

        [Fact]
        public void Fingerprint_ChangesWhenProjectedUtilityDepthOrRuleColorChanges()
        {
            SectionPlanRecord WithProjection(double? z, short color)
            {
                var record = Record(Cl(0, 12, 0, -18));
                var crossing = new SectionProjectionLogic.Crossing(
                    5, z, "MAIM", "UT", new SectionProjectionLogic.ProjectionRuleMatch(
                        "utility", "מים", color), "A1", 100, 200);
                record.ProjectedEntities.Add(new ProjectedEntityPlan
                {
                    ProjectionKey = SectionProjectionLogic.ProjectionEvidenceKey(crossing),
                    SystemLabel = "מים", SourceLayer = "MAIM", SourceXref = "UT",
                    SourceHandle = "A1", IntersectionWcs = new[] { 100.0, 200.0 },
                });
                record.ProjectedSystems.Add("מים");
                return record;
            }

            var baseline = SectionPlanLogic.ComputeFingerprint(WithProjection(281.0, 5));
            SectionPlanLogic.ComputeFingerprint(WithProjection(282.0, 5)).Should().NotBe(baseline);
            SectionPlanLogic.ComputeFingerprint(WithProjection(281.0, 6)).Should().NotBe(baseline);
        }

        [Fact]
        public void Fingerprint_ChangesWhenUtilityProjectionCoverageChanges()
        {
            var complete = Record(Cl(0, 12, 0, -18));
            complete.UtilityCoverage.ProjectionScanState = UtilityProjectionScanState.Complete;
            complete.UtilityCoverage.ProjectionDrawingEntityCount = 12;
            complete.UtilityCoverage.ProjectionSectionCrossingCount = 0;

            var blocked = Record(Cl(0, 12, 0, -18));
            blocked.UtilityCoverage.ProjectionScanState = UtilityProjectionScanState.Blocked;
            blocked.UtilityCoverage.ProjectionDrawingEntityCount = 12;
            blocked.UtilityCoverage.ProjectionSectionCrossingCount = 0;

            SectionPlanLogic.ComputeFingerprint(blocked)
                .Should().NotBe(SectionPlanLogic.ComputeFingerprint(complete));
        }

        [Fact]
        public void Fingerprint_ChangesWhenPresentationBoundaryOrApprovalChanges()
        {
            SectionPlanRecord WithCoverage(
                string digest, string rowState, string label, double boundary)
            {
                var record = Record(Cl(0, 12, 0, -18));
                record.PresentationCoverage = new SectionPresentationCoveragePlan
                {
                    EvidenceDigest = digest,
                    Complete = true,
                    PlanMarkCount = 4,
                    DimensionMarkCount = 4,
                    WidthSpanCount = 3,
                    NamedStripCount = 3,
                    BoundarySource = "cl-extents",
                    BoundaryFromM = -boundary,
                    BoundaryToM = boundary,
                    RowAuthorityState = rowState,
                };
                record.PresentationCoverage.ExplicitSpanOverrides.Add(
                    new SectionSpanLabelOverridePlan
                    {
                        OffsetM = 0,
                        Label = label,
                        Source = "manual-profile",
                    });
                return record;
            }

            var baseline = SectionPlanLogic.ComputeFingerprint(
                WithCoverage("digest-a", "suppressed", "חניה", 6));
            SectionPlanLogic.ComputeFingerprint(
                    WithCoverage("digest-b", "suppressed", "חניה", 6))
                .Should().NotBe(baseline);
            SectionPlanLogic.ComputeFingerprint(
                    WithCoverage("digest-a", "authoritative", "חניה", 6))
                .Should().NotBe(baseline);
            SectionPlanLogic.ComputeFingerprint(
                    WithCoverage("digest-a", "suppressed", "גינון", 6))
                .Should().NotBe(baseline);
            SectionPlanLogic.ComputeFingerprint(
                    WithCoverage("digest-a", "suppressed", "חניה", 8))
                .Should().NotBe(baseline);
        }

        [Fact]
        public void SelectedIntegrityGate_IgnoresOtherRecordButKeepsGlobalAndAffectedBlockers()
        {
            var selected = new SectionPlanRecord
            {
                RecordId = "A",
                Cl = Cl(-5, 0, 5, 0, "A"),
            };
            selected.Status = DeliveryStatus.Ready;
            selected.Action = PlanAction.Create;
            var other = new SectionPlanRecord
            {
                RecordId = "B",
                Cl = Cl(-5, 1, 5, 1, "B"),
            };
            other.Status = DeliveryStatus.Blocked;
            other.Action = PlanAction.ReviewRequired;
            other.Findings.Add(Blocker("other-record", "B"));

            var plan = new SectionPlan
            {
                RunId = "plan-selected-scope",
                ProjectProfileId = "p",
                SourceDrawing = "host.dwg",
                SourceUnitCode = SectionSourceIntegrityLogic.MetresUnitCode,
                SourceDatabaseRevision = "db-revision",
            };
            plan.Records.AddRange(new[] { selected, other });

            SectionInputIntegrityService.HasSelectedPlanningIntegrityBlocker(plan, selected)
                .Should().BeFalse("a blocked sibling record is outside selected scope");

            plan.Findings.Add(Blocker("other-plan-finding", "B"));
            SectionInputIntegrityService.HasSelectedPlanningIntegrityBlocker(plan, selected)
                .Should().BeFalse("an explicitly scoped finding for B does not affect A");

            plan.Findings.Add(Blocker("global-source"));
            SectionInputIntegrityService.HasSelectedPlanningIntegrityBlocker(plan, selected)
                .Should().BeTrue("a finding without affected ids is global and fail-closed");

            plan.Findings.Clear();
            plan.Findings.Add(Blocker("selected-source", "A"));
            SectionInputIntegrityService.HasSelectedPlanningIntegrityBlocker(plan, selected)
                .Should().BeTrue("a PLAN finding that names A remains blocking");

            static DeliveryFinding Blocker(string code, string? affected = null)
            {
                var finding = new DeliveryFinding
                {
                    Code = code,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.ReviewRequired,
                    Title = code,
                };
                if (affected != null) finding.AffectedRecordIds.Add(affected);
                return finding;
            }
        }
    }
}
