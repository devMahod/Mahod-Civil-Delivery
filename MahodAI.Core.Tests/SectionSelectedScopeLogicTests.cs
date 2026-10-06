using System;
using System.Collections.Generic;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SamplingReconciliationLogic;

namespace MahodAI.Core.Tests;

/// <summary>
/// Host-free simulation of the selected-scope contracts: APPLY→VERIFY layout
/// evidence, shared-group sampling (the A+B scenario), owned-sample-line children,
/// shared-resource policy, and fail-closed overlap against every live view.
/// </summary>
public sealed class SectionSelectedScopeLogicTests
{
    // ------------------------------------------------------- layout evidence

    [Fact]
    public void SelectedApply_CapturesEvidenceThatVerifyAccepts_WithoutAnyMove()
    {
        var live = (MinX: 214833.2, MinY: 654985.0, MaxX: 214928.7, MaxY: 655003.4,
            LocX: 214833.2, LocY: 654985.0);
        var captured = LayoutEvidenceContract.Capture(
            live.MinX, live.MinY, live.MaxX, live.MaxY, live.LocX, live.LocY);

        LayoutEvidenceContract.Verify(
                captured.Bounds, captured.Location,
                new[] { live.MinX, live.MinY, live.MaxX, live.MaxY },
                new[] { live.LocX, live.LocY }, 0.01)
            .Pass.Should().BeTrue();
    }

    [Fact]
    public void BatchApply_CapturesEvidenceAfterTheSheetMove()
    {
        var captured = LayoutEvidenceContract.Capture(
            0, 0, 100, 20, locationX: 300, locationY: -40, deltaX: 300, deltaY: -40);
        captured.Bounds.Should().Equal(300, -40, 400, -20);
        captured.Location.Should().Equal(300, -40);
    }

    [Fact]
    public void MissingEvidence_IsAFailure_NotAPass()
    {
        LayoutEvidenceContract.Verify(null, null, new[] { 0.0, 0, 1, 1 }, new[] { 0.0, 0 }, 0.01)
            .Pass.Should().BeFalse();
        LayoutEvidenceContract.Verify(new[] { 0.0, 0, 1, 1 }, new[] { 0.0, 0 }, null, null, 0.01)
            .Pass.Should().BeFalse();
    }

    [Fact]
    public void Tolerance_IsAppliedPerCoordinate()
    {
        var bounds = new[] { 0.0, 0, 10, 5 };
        var loc = new[] { 0.0, 0 };
        LayoutEvidenceContract.Verify(bounds, loc, new[] { 0.009, 0, 10, 5 }, loc, 0.01)
            .BoundsPass.Should().BeTrue();
        LayoutEvidenceContract.Verify(bounds, loc, new[] { 0.011, 0, 10, 5 }, loc, 0.01)
            .BoundsPass.Should().BeFalse();
    }

    [Fact]
    public void Capture_RefusesNonFiniteOrInvertedExtents()
    {
        var nan = () => LayoutEvidenceContract.Capture(double.NaN, 0, 1, 1, 0, 0);
        nan.Should().Throw<ArgumentOutOfRangeException>();
        var inverted = () => LayoutEvidenceContract.Capture(5, 0, 1, 1, 0, 0);
        inverted.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ------------------------------------------------ sampling reconciliation

    private static readonly string[] PlanUnion = { "MK", "MK-EAST", "2000-DESIGN-FINAL" };

    [Fact]
    public void BatchApply_ConvergesALegacyGroup_DisablingForeignSources()
    {
        var current = new List<(string?, bool)>
        {
            ("MK", true), ("MK-EAST", true), ("2000-DESIGN-FINAL", false),
            ("700 bot- (2)", true), ("1000D@Top@01", true), ("3000-DESIGN-FINAL", false),
        };
        var plan = Decide(current, PlanUnion, Mode.Batch);
        plan.IsBlocked.Should().BeFalse();
        plan.ToEnable.Should().Equal("2000-DESIGN-FINAL");
        plan.ToDisable.Should().Equal("1000D@Top@01", "700 bot- (2)");
        plan.AlreadySampled.Should().Equal("MK", "MK-EAST");
    }

    [Fact]
    public void ApplySelectedA_LeavesB_Untouched_SharedGroupWithAnyDelta_IsRefused()
    {
        var groupBeforeA = new List<(string?, bool)>
        {
            ("MK", true), ("MK-EAST", true), ("2000-DESIGN-FINAL", false), ("700 bot- (2)", true),
        };
        var plan = Decide(groupBeforeA, PlanUnion, Mode.SelectedSharedGroup);
        plan.IsBlocked.Should().BeTrue();
        plan.Blocked.Should().Equal("+2000-DESIGN-FINAL", "-700 bot- (2)");
        plan.ToEnable.Should().BeEmpty();
        plan.ToDisable.Should().BeEmpty();
    }

    [Fact]
    public void ApplySelectedA_OnASharedGroup_EnableOnlyDelta_IsStillRefused()
    {
        var plan = Decide(
            new List<(string?, bool)> { ("MK", true), ("MK-EAST", true), ("2000-DESIGN-FINAL", false) },
            PlanUnion, Mode.SelectedSharedGroup);
        plan.IsBlocked.Should().BeTrue();
        plan.Blocked.Should().Equal("+2000-DESIGN-FINAL");
    }

    [Fact]
    public void BatchApply_WithForeignOrOmittedWork_RefusesAnyGroupSamplingDelta()
    {
        var plan = Decide(
            new List<(string?, bool)>
            {
                ("MK", true), ("MK-EAST", true),
                ("2000-DESIGN-FINAL", false), ("foreign", true),
            },
            PlanUnion, Mode.BatchSharedGroup);

        plan.IsBlocked.Should().BeTrue();
        plan.Blocked.Should().Equal("+2000-DESIGN-FINAL", "-foreign");
        plan.ToEnable.Should().BeEmpty();
        plan.ToDisable.Should().BeEmpty();
    }

    [Fact]
    public void BatchApply_WithForeignOrOmittedWork_AllowsOnlyTrueSamplingNoOp()
    {
        var plan = Decide(
            new List<(string?, bool)>
            {
                ("MK", true), ("MK-EAST", true),
                ("2000-DESIGN-FINAL", true), ("foreign", false),
            },
            PlanUnion, Mode.BatchSharedGroup);

        plan.IsBlocked.Should().BeFalse();
        plan.IsNoOp.Should().BeTrue();
    }

    [Fact]
    public void ApplySelectedA_OnASharedGroupAlreadyAtTheUnion_IsANoOp()
    {
        var plan = Decide(
            new List<(string?, bool)> { ("MK", true), ("MK-EAST", true), ("2000-DESIGN-FINAL", true), ("700 bot- (2)", false) },
            PlanUnion, Mode.SelectedSharedGroup);
        plan.IsBlocked.Should().BeFalse();
        plan.IsNoOp.Should().BeTrue();
    }

    [Fact]
    public void Project6422_SelectedCl7C89_OnExisting1000Group_IsASetterFreeNoOp()
    {
        // Frozen 31/08 APPLY evidence: the existing alignment-1000 MCD group samples
        // exactly these two sources for its four managed records.  The 01/09 PLAN for
        // cl-7C89 requests the same pair.  The one-section live gate therefore does
        // not depend on resolving the other 138 rows and cannot resample its siblings.
        var current = new List<(string?, bool)>
        {
            ("MK", true),
            ("1000D@Top@01", true),
        };
        var desired = new[] { "MK", "1000D@Top@01" };

        var plan = Decide(current, desired, Mode.SelectedSharedGroup);

        plan.IsBlocked.Should().BeFalse();
        plan.IsNoOp.Should().BeTrue();
        plan.ToEnable.Should().BeEmpty();
        plan.ToDisable.Should().BeEmpty();
        plan.AlreadySampled.Should().BeEquivalentTo(desired);
    }

    [Fact]
    public void Project6422_Alignment1000Union_IgnoresUnresolvedRecordsOnOtherAlignments()
    {
        static MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts.SectionPlanRecord Record(
            string id, string alignment, string existing, string design) => new()
            {
                RecordId = id,
                Cl = new MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts.ClSourceRecord
                {
                    RecordId = id,
                    SourceDrawing = "CL.dwg",
                    SourceDrawingHash = "hash",
                    SourceHandle = id,
                    SourceEntityType = "LWPOLYLINE",
                    SourceLayer = "GFC111",
                    SourceEndpoints = new[] { 0.0, 0, 1, 0 },
                    WcsEndpoints = new[] { 0.0, 0, 1, 0 },
                },
                SelectedAlignment = alignment,
                PlannedSources =
                {
                    new MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts.SectionSourcePlan
                    {
                        SourceName = existing, SourceType = "surface",
                        PlannedState = "sampled", Required = true,
                    },
                    new MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts.SectionSourcePlan
                    {
                        SourceName = design, SourceType = "surface",
                        PlannedState = "sampled", Required = true,
                    },
                },
            };

        var plan = new MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts.SectionPlan
        {
            RunId = "6422-offline-selected-fixture",
            ProjectProfileId = "6422",
        };
        plan.Records.AddRange(new[]
        {
            Record("cl-7C89", "1000", "MK", "1000D@Top@01"),
            Record("sibling-a", "1000", "MK", "1000D@Top@01"),
            Record("sibling-b", "1000", "MK", "1000D@Top@01"),
            Record("sibling-c", "1000", "MK", "1000D@Top@01"),
            Record("unresolved-other-alignment", "2000", "MK", "2000-DESIGN-FINAL"),
        });

        var group = MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services.SectionPlanLogic
            .ExpectedGroupSampling(plan, "1000");

        group.Names.Should().BeEquivalentTo("MK", "1000D@Top@01");
        group.RequiredNames.Should().BeEquivalentTo("MK", "1000D@Top@01");
    }

    [Fact]
    public void ApplySelected_OnAGroupItHasToItself_MayConvergeLikeBatch()
    {
        var plan = Decide(
            new List<(string?, bool)> { ("MK", true), ("2000-DESIGN-FINAL", false), ("700 bot- (2)", true) },
            PlanUnion, Mode.SelectedExclusiveGroup);
        plan.IsBlocked.Should().BeFalse();
        plan.ToEnable.Should().Equal("2000-DESIGN-FINAL");
        plan.ToDisable.Should().Equal("700 bot- (2)");
    }

    [Fact]
    public void UnresolvedSampledSource_IsForeign_AndIsRefusedInASharedGroup()
    {
        var plan = Decide(
            new List<(string?, bool)> { ("MK", true), ("MK-EAST", true), ("2000-DESIGN-FINAL", true), (null, true) },
            PlanUnion, Mode.SelectedSharedGroup);
        plan.IsBlocked.Should().BeTrue();
        plan.Blocked.Should().Equal("-<unresolved>");
    }

    [Fact]
    public void MissingDesiredSources_AreReportedForTheCallerToFailClosed() =>
        Decide(new List<(string?, bool)> { ("MK", true) }, PlanUnion, Mode.Batch)
            .MissingDesired.Should().Equal("2000-DESIGN-FINAL", "MK-EAST");

    [Fact]
    public void NamesAreMatchedCaseInsensitively_LikeProspector()
    {
        var plan = Decide(
            new List<(string?, bool)> { ("mk", true), ("Mk-East", false), ("2000-design-final", false) },
            PlanUnion, Mode.SelectedExclusiveGroup);
        plan.IsBlocked.Should().BeFalse();
        plan.ToEnable.Should().Equal("2000-design-final", "Mk-East");
    }

    // ------------------------------------------- owned sample-line children

    [Fact]
    public void OwnedSampleLine_WithAForeignView_MayNotBeErasedOrResampled()
    {
        // The engineer drew her own SectionView from our sample line (Nataly's
        // "reuse existing sections" requirement). Update must not erase its parent.
        var verdict = OwnedSampleLineChildrenLogic.Decide(true, new[]
        {
            ("A1B2", true), ("C3D4", false),
        });
        verdict.IsSafeToMutate.Should().BeFalse();
        verdict.ForeignHandles.Should().Equal("C3D4");
    }

    [Fact]
    public void OwnedSampleLine_WithOnlyOwnedViews_IsSafe() =>
        OwnedSampleLineChildrenLogic.Decide(true, new[] { ("A1B2", true) })
            .IsSafeToMutate.Should().BeTrue();

    [Fact]
    public void UnreadableChildren_AreNeverTreatedAsNoChildren()
    {
        // 1.2.24 swallowed the read failure and continued as if the line were childless.
        var verdict = OwnedSampleLineChildrenLogic.Decide(false, Array.Empty<(string, bool)>());
        verdict.Readable.Should().BeFalse();
        verdict.IsSafeToMutate.Should().BeFalse();
    }

    [Fact]
    public void MatchingLogicalKey_CannotCrossFeatureRoleOrProjectOwnershipBoundary()
    {
        static OwnershipMetadata Meta(string feature, string role, string project) => new()
        {
            Feature = feature,
            Role = role,
            ProjectProfileId = project,
            RunId = "run",
            LogicalKey = "section-key",
            SourceClHandle = "A12",
            InputFingerprint = "fingerprint",
            CreatedByToolVersion = "test",
        };

        SectionRecordOwnershipLogic.IsOwnedByRecord(
            Meta("estimate", "sample-line", "6422"), "6422", "sample-line",
            "section-key", "A12").Should().BeFalse();
        SectionRecordOwnershipLogic.IsOwnedByRecord(
            Meta("sections", "section-view", "6422"), "6422", "sample-line",
            "section-key", "A12").Should().BeFalse();
        SectionRecordOwnershipLogic.IsOwnedByRecord(
            Meta("sections", "sample-line", "other"), "6422", "sample-line",
            "section-key", "A12").Should().BeFalse();
    }

    [Fact]
    public void CurrentAndLegacyKeys_AreAcceptedOnlyInsideExactOwnershipBoundary()
    {
        var exact = new OwnershipMetadata
        {
            Feature = "sections", Role = "sample-line", ProjectProfileId = "6422",
            RunId = "run", LogicalKey = "section-key", SourceClHandle = "A12",
            InputFingerprint = "fingerprint", CreatedByToolVersion = "test",
        };
        SectionRecordOwnershipLogic.IsOwnedByRecord(
            exact, "6422", "sample-line", "section-key", "A12").Should().BeTrue();

        var legacy = new OwnershipMetadata
        {
            Feature = exact.Feature, Role = exact.Role,
            ProjectProfileId = exact.ProjectProfileId, RunId = exact.RunId,
            LogicalKey = "v1-hash-dependent-key", SourceClHandle = exact.SourceClHandle,
            InputFingerprint = exact.InputFingerprint,
            CreatedByToolVersion = exact.CreatedByToolVersion,
        };
        SectionRecordOwnershipLogic.IsOwnedByRecord(
            legacy, "6422", "sample-line", "section-key", "A12").Should().BeTrue();
        SectionRecordOwnershipLogic.IsOwnedByRecord(
            legacy, "6422", "sample-line", "section-key", "A12",
            allowLegacySourceHandle: false).Should().BeFalse();
        SectionRecordOwnershipLogic.IsOwnedByRecord(
            legacy, "6422", "sample-line", "section-key", "DIFFERENT").Should().BeFalse();
    }

    // ------------------------------------------------ shared resource policy

    [Theory]
    [InlineData(false, false, SharedResourceLogic.Mode.CreateOnlyNeverModify, SharedResourceLogic.Action.Create)]
    [InlineData(true, true, SharedResourceLogic.Mode.CreateOnlyNeverModify, SharedResourceLogic.Action.NoOp)]
    [InlineData(true, false, SharedResourceLogic.Mode.CreateOnlyNeverModify, SharedResourceLogic.Action.Block)]
    [InlineData(true, false, SharedResourceLogic.Mode.NormalizeAll, SharedResourceLogic.Action.Modify)]
    [InlineData(false, false, SharedResourceLogic.Mode.NormalizeAll, SharedResourceLogic.Action.Create)]
    [InlineData(true, true, SharedResourceLogic.Mode.NormalizeAll, SharedResourceLogic.Action.NoOp)]
    public void SharedResources_SelectedScope_NeverModifiesWhatOtherSectionsReference(
        bool exists, bool matches, SharedResourceLogic.Mode mode, SharedResourceLogic.Action expected) =>
        SharedResourceLogic.Decide(exists, matches, mode).Should().Be(expected);

    // ------------------------------------------------------------- overlap

    [Fact]
    public void SelectedView_IsCheckedAgainstEveryOtherLiveView()
    {
        var self = new LayoutOverlapLogic.Box(100, 0, 200, 40);
        var others = new List<(string, LayoutOverlapLogic.Box)>
        {
            ("B-far", new LayoutOverlapLogic.Box(300, 0, 400, 40)),
            ("C-touching", new LayoutOverlapLogic.Box(200, 0, 300, 40)),
            ("D-manual-overlap", new LayoutOverlapLogic.Box(150, 20, 260, 80)),
            ("E-inside", new LayoutOverlapLogic.Box(120, 10, 130, 20)),
        };
        var report = LayoutOverlapLogic.Inspect(self, others);
        report.SelfValid.Should().BeTrue();
        report.CollidingIds.Should().Equal("D-manual-overlap", "E-inside");
        report.UnmeasurableIds.Should().BeEmpty();
        report.IsClean.Should().BeFalse();
    }

    [Fact]
    public void InvalidSelfExtents_FailTheCheck_NeverPassIt()
    {
        var others = new List<(string, LayoutOverlapLogic.Box)> { ("B", new LayoutOverlapLogic.Box(0, 0, 1, 1)) };
        foreach (var bad in new[]
        {
            new LayoutOverlapLogic.Box(double.NaN, 0, 10, 10),     // non-finite
            new LayoutOverlapLogic.Box(10, 10, 0, 0),              // inverted
            new LayoutOverlapLogic.Box(5, 5, 5, 9),                // zero width
            new LayoutOverlapLogic.Box(5, 5, 9, 5),                // zero height
        })
        {
            var report = LayoutOverlapLogic.Inspect(bad, others);
            report.SelfValid.Should().BeFalse();
            report.IsClean.Should().BeFalse();
        }
    }

    [Fact]
    public void InvalidForeignExtents_AreUnmeasurable_NotSeparated()
    {
        var self = new LayoutOverlapLogic.Box(0, 0, 10, 10);
        var report = LayoutOverlapLogic.Inspect(self, new List<(string, LayoutOverlapLogic.Box)>
        {
            ("nan", new LayoutOverlapLogic.Box(double.NaN, 0, 10, 10)),
            ("zero-area", new LayoutOverlapLogic.Box(3, 3, 3, 3)),
            ("clean", new LayoutOverlapLogic.Box(20, 20, 30, 30)),
        });
        report.SelfValid.Should().BeTrue();
        report.CollidingIds.Should().BeEmpty();
        report.UnmeasurableIds.Should().Equal("nan", "zero-area");
        report.IsClean.Should().BeFalse("an unmeasurable neighbour is not a proven separation");
    }

    [Fact]
    public void Overlaps_RefusesInvalidBoxesInsteadOfGuessing()
    {
        var act = () => LayoutOverlapLogic.Overlaps(
            new LayoutOverlapLogic.Box(0, 0, 1, 1), new LayoutOverlapLogic.Box(double.NaN, 0, 1, 1));
        act.Should().Throw<ArgumentException>();
    }
}
