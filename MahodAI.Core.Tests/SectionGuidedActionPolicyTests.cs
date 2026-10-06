using System;
using System.Collections.Generic;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class SectionGuidedActionPolicyTests
{
    private static SectionGuidedActionSnapshot Selected() => new()
    {
        HasDrawing = true,
        ProfileUsable = true,
        HasPlan = true,
        SelectedRecord = true,
        PlanRecordCount = 28,
    };

    [Fact]
    public void RecordedPlan_28Records_0Ready_27RowApprovals_86Names_DoesNotImplyCreation()
    {
        // Aggregate counts describe the recorded plan, not evidence that any view
        // was created. The selected record still needs its ROW authority resolved.
        var plan = (Records: 28, Ready: 0, RowApprovals: 27, UnnamedSpans: 86);
        var state = Selected() with
        {
            PlanRecordCount = plan.Records,
            CanApproveRow = plan.RowApprovals > 0,
            CanNameSpans = plan.UnnamedSpans > 0,
            CanApplySelected = plan.Ready > 0,
        };

        var decision = SectionGuidedActionPolicy.Evaluate(state);

        decision.Action.Should().Be(SectionGuidedActionKind.ApproveRow);
        decision.Stage.Should().Be(SectionGuidedStage.Review);
        decision.Title.Should().Contain("גבולות הדרך");
        SectionGuidedActionPolicy.Evaluate(state with { SelectedRecord = false })
            .Action.Should().Be(SectionGuidedActionKind.ChooseRecord);
    }

    [Fact]
    public void SetupAndActualPreviewRecovery_PrecedePositiveDownstreamFlags()
    {
        var state = Selected() with
        {
            HasDrawing = false, ProfileUsable = false,
            PreviewCleanupBlocked = true, StalePlan = true,
            CanApplySelected = true, CanVerifySelected = true,
            HasCreatedView = true, SelectedVerified = true,
        };

        SectionGuidedActionPolicy.Evaluate(state).Action
            .Should().Be(SectionGuidedActionKind.OpenDrawing);
        state = state with { HasDrawing = true };
        SectionGuidedActionPolicy.Evaluate(state).Action
            .Should().Be(SectionGuidedActionKind.ConfigureProfile);
        state = state with { ProfileUsable = true };
        SectionGuidedActionPolicy.Evaluate(state).Action
            .Should().Be(SectionGuidedActionKind.RecoverPreview);
        state = state with { PreviewCleanupBlocked = false };
        SectionGuidedActionPolicy.Evaluate(state).Action
            .Should().Be(SectionGuidedActionKind.Plan);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MissingOrStalePlan_RequiresDiscoveryBeforeCreateOrVerify(bool hasPlan, bool stale)
    {
        var decision = SectionGuidedActionPolicy.Evaluate(Selected() with
        {
            HasPlan = hasPlan, StalePlan = stale,
            CanApplySelected = true, CanVerifySelected = true,
            HasCreatedView = true, SelectedVerified = true,
        });

        decision.Action.Should().Be(SectionGuidedActionKind.Plan);
        decision.Stage.Should().Be(SectionGuidedStage.Planning);
        decision.Detail.Should().Contain("אינו יוצר חתכים");
        decision.Title.Should().NotContain("אומת");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EmptyOrInvalidRecordCount_DoesNotAskForAnUnavailableSelection(int recordCount)
    {
        var decision = SectionGuidedActionPolicy.Evaluate(Selected() with
        {
            PlanRecordCount = recordCount, SelectedRecord = false,
            CanApplySelected = true, CanVerifySelected = true,
        });

        decision.Action.Should().Be(SectionGuidedActionKind.ConfigureSectionSources);
        decision.Detail.Should().Contain("מקור קווי CL").And.Contain("חיתוכים");
    }

    [Fact]
    public void MissingSelection_PrecedesEverySelectedRecordAction()
    {
        var decision = SectionGuidedActionPolicy.Evaluate(Selected() with
        {
            SelectedRecord = false, CanApplySelected = true,
            CanVerifySelected = true, HasCreatedView = true, SelectedVerified = true,
        });

        decision.Action.Should().Be(SectionGuidedActionKind.ChooseRecord);
        decision.Detail.Should().Contain("28 רשומות");
        decision.Detail.Should().Contain(SectionGuidedActionPolicy.PrecreationLocationExplanation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EvidenceFailure_BlocksCreateVerifyAndVerifiedClaimsEvenWhenViewExists(bool hasView)
    {
        var decision = SectionGuidedActionPolicy.Evaluate(Selected() with
        {
            EvidenceBlocked = true, CanApplySelected = true, CanVerifySelected = true,
            HasCreatedView = hasView, SelectedVerified = true,
            CanChooseCrossing = true, CanApproveRow = true, CanNameSpans = true,
        });

        decision.Action.Should().Be(SectionGuidedActionKind.InspectIssues);
        decision.Stage.Should().Be(SectionGuidedStage.Recovery);
        decision.Detail.Should().Contain("יצירה ואימות יישארו חסומים");
        decision.Title.Should().NotContain("אומת");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void EvidenceRecoveryCanReplan_WithAnExplicitExplanationAndNoCreationClaim(bool hasPlan, bool stale)
    {
        var decision = SectionGuidedActionPolicy.Evaluate(Selected() with
        {
            HasPlan = hasPlan, StalePlan = stale, EvidenceBlocked = true,
            CanApplySelected = true, CanVerifySelected = true,
        });

        decision.Action.Should().Be(SectionGuidedActionKind.Plan);
        decision.Detail.Should().Contain("לשחזר");
        decision.Detail.Should().Contain("אינו יוצר חתכים");
        decision.Detail.Should().Contain("יצירה ואימות יישארו חסומים");
    }

    [Fact]
    public void OrdinaryBlockedRow_DoesNotInventAPreviewCleanupFailure()
    {
        var decision = SectionGuidedActionPolicy.Evaluate(Selected());

        decision.Action.Should().Be(SectionGuidedActionKind.InspectIssues);
        decision.Detail.Should().Contain(SectionGuidedActionPolicy.PrecreationLocationExplanation);
        decision.Detail.Should().NotContain("ניקוי");
    }

    [Fact]
    public void VerifiedResultPrecedesStillEnabledReverification_ThenUnverifiedViewRequiresVerification()
    {
        var state = Selected() with
        {
            CanVerifySelected = true, HasCreatedView = true, SelectedVerified = true,
            CanChooseCrossing = true, CanApproveRow = true, CanNameSpans = true,
            CanResolveDirection = true, CanApplySelected = true,
        };

        SectionGuidedActionPolicy.Evaluate(state).Action
            .Should().Be(SectionGuidedActionKind.ShowVerifiedView);
        state = state with { SelectedVerified = false };
        SectionGuidedActionPolicy.Evaluate(state).Action
            .Should().Be(SectionGuidedActionKind.VerifySelected);
        state = state with { CanVerifySelected = false };
        SectionGuidedActionPolicy.Evaluate(state).Action
            .Should().Be(SectionGuidedActionKind.ChooseCrossing);
    }

    [Fact]
    public void SuccessfulVerification_AdvancesToShowEvenWhenHostReverifyButtonStaysEnabled()
    {
        var before = Selected() with { HasCreatedView = true, CanVerifySelected = true };
        var after = before with { SelectedVerified = true };

        SectionGuidedActionPolicy.Evaluate(before).Action
            .Should().Be(SectionGuidedActionKind.VerifySelected);
        var decision = SectionGuidedActionPolicy.Evaluate(after);
        decision.Action.Should().Be(SectionGuidedActionKind.ShowVerifiedView);
        decision.Stage.Should().Be(SectionGuidedStage.Result);
        after.CanVerifySelected.Should().BeTrue("the host still permits explicit reverification");
    }

    [Fact]
    public void PrerequisitesFollowCrossingRowNamesLaneDirectionBeforeApply()
    {
        var state = Selected() with
        {
            CanChooseCrossing = true, CanApproveRow = true, CanNameSpans = true,
            CanResolveDirection = true, CanApplySelected = true, HasCreatedView = true,
        };

        SectionGuidedActionPolicy.Evaluate(state).Action
            .Should().Be(SectionGuidedActionKind.ChooseCrossing);
        state = state with { CanChooseCrossing = false };
        SectionGuidedActionPolicy.Evaluate(state).Action
            .Should().Be(SectionGuidedActionKind.ApproveRow);
        state = state with { CanApproveRow = false };
        SectionGuidedActionPolicy.Evaluate(state).Action
            .Should().Be(SectionGuidedActionKind.NameSpans);
        state = state with { CanNameSpans = false };
        var direction = SectionGuidedActionPolicy.Evaluate(state);
        direction.Action.Should().Be(SectionGuidedActionKind.ResolveDirection);
        direction.Detail.Should().Contain("לכל נתיב").And.Contain("כיוון המבט");
        state = state with { CanResolveDirection = false };
        SectionGuidedActionPolicy.Evaluate(state).Action
            .Should().Be(SectionGuidedActionKind.ApplySelected);
    }

    [Fact]
    public void PlanningAndSelectedVerifiedFlagAlone_DoNotProveACreatedView()
    {
        var decision = SectionGuidedActionPolicy.Evaluate(Selected() with
        {
            SelectedVerified = true, HasCreatedView = false,
        });

        decision.Action.Should().Be(SectionGuidedActionKind.InspectIssues);
        decision.Stage.Should().NotBe(SectionGuidedStage.Result);
        decision.Title.Should().NotContain("אומת");
    }

    [Fact]
    public void ExistingViewWithoutCurrentVerification_IsShownWithAnExplicitLimit()
    {
        var decision = SectionGuidedActionPolicy.Evaluate(Selected() with { HasCreatedView = true });

        decision.Action.Should().Be(SectionGuidedActionKind.ShowExistingView);
        decision.Stage.Should().NotBe(SectionGuidedStage.Result);
        decision.Detail.Should().Contain("האימות הנוכחי שלה טרם הושלם");
    }

    [Fact]
    public void CompleteWorkflow_AdvancesOnlyWhenAuthoritativeSnapshotChanges()
    {
        var state = new SectionGuidedActionSnapshot();
        var actions = new List<SectionGuidedActionKind>();
        void Capture() => actions.Add(SectionGuidedActionPolicy.Evaluate(state).Action);

        Capture();
        state = state with { HasDrawing = true };
        Capture();
        state = state with { ProfileUsable = true };
        Capture();
        state = state with { HasPlan = true, PlanRecordCount = 28 };
        Capture();
        state = state with { SelectedRecord = true, CanApproveRow = true };
        Capture();
        state = state with { CanApproveRow = false, CanNameSpans = true };
        Capture();
        state = state with { CanNameSpans = false, CanResolveDirection = true };
        Capture();
        state = state with { CanResolveDirection = false, CanApplySelected = true };
        Capture();

        // Clicking, cancelling or reopening a dialog provides no committed result.
        // The pure policy has no UI event or cancellation input and cannot advance.
        SectionGuidedActionPolicy.Evaluate(state).Should().Be(SectionGuidedActionPolicy.Evaluate(state));
        state.HasCreatedView.Should().BeFalse();
        state.SelectedVerified.Should().BeFalse();

        state = state with { CanApplySelected = false, HasCreatedView = true, CanVerifySelected = true };
        Capture();
        state = state with { SelectedVerified = true };
        Capture();

        actions.Should().Equal(
            SectionGuidedActionKind.OpenDrawing, SectionGuidedActionKind.ConfigureProfile,
            SectionGuidedActionKind.Plan, SectionGuidedActionKind.ChooseRecord,
            SectionGuidedActionKind.ApproveRow, SectionGuidedActionKind.NameSpans,
            SectionGuidedActionKind.ResolveDirection, SectionGuidedActionKind.ApplySelected,
            SectionGuidedActionKind.VerifySelected, SectionGuidedActionKind.ShowVerifiedView);
    }

    [Fact]
    public void UnchangedPlanOffersLookupWithoutClaimingAFoundOrVerifiedView()
    {
        var state = Selected() with { ManagedViewLookupAvailable = true, SelectedVerified = true };
        var decision = SectionGuidedActionPolicy.Evaluate(state);
        decision.Action.Should().Be(SectionGuidedActionKind.LocateExistingView);
        decision.Stage.Should().NotBe(SectionGuidedStage.Result);
        decision.Detail.Should().Contain("אינו אימות הנדסי");
        SectionGuidedActionPolicy.Evaluate(state with { EvidenceBlocked = true }).Action
            .Should().Be(SectionGuidedActionKind.InspectIssues);
        SectionGuidedActionPolicy.Evaluate(state with { StalePlan = true }).Action
            .Should().Be(SectionGuidedActionKind.Plan);
    }

    [Fact]
    public void NullSnapshotIsRejectedInsteadOfInventingState()
    {
        var act = () => SectionGuidedActionPolicy.Evaluate(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GlobalInventoryFailure_PrecedesReadyVerifiedAndSelectionFlags(bool selected)
    {
        var decision = SectionGuidedActionPolicy.Evaluate(Selected() with
        {
            SelectedRecord = selected, GlobalPlanningBlockReason = "89 רישומים ללא ישויות",
            CanApplySelected = true, CanVerifySelected = true, HasCreatedView = true,
            SelectedVerified = true, CanNameSpans = true, CanApproveRow = true,
        });
        decision.Action.Should().Be(SectionGuidedActionKind.InspectIssues);
        decision.Stage.Should().Be(SectionGuidedStage.Recovery);
        decision.Title.Should().Be("התכנון חסום לכל החתכים");
        decision.Detail.Should().Contain("89 רישומים").And.Contain("לא יפתרו אותה");
    }

    [Fact]
    public void StaleGlobalFailureRequiresFreshPlan_AndClearedFailureRestoresCreation()
    {
        var state = Selected() with
        {
            GlobalPlanningBlockReason = "רישום לא תואם", StalePlan = true, CanApplySelected = true,
        };
        SectionGuidedActionPolicy.Evaluate(state).Action.Should().Be(SectionGuidedActionKind.Plan);
        SectionGuidedActionPolicy.Evaluate(state with
        { GlobalPlanningBlockReason = null, StalePlan = false }).Action.Should().Be(SectionGuidedActionKind.ApplySelected);
    }

    [Fact]
    public void RecoveryNavigationIsExplicit_AndNotPermissionToChangeAnotherSection()
    {
        var state = Selected() with { CanNavigateToAnnotationRecovery = true, CanApplySelected = true };
        var decision = SectionGuidedActionPolicy.Evaluate(state);
        decision.Action.Should().Be(SectionGuidedActionKind.ChooseAnnotationRecoveryRecord);
        decision.Detail.Should().Contain("לא תתקן אוטומטית חתכים אחרים");
        SectionGuidedActionPolicy.Evaluate(state with { EvidenceBlocked = true }).Action
            .Should().Be(SectionGuidedActionKind.InspectIssues);
        var update = SectionGuidedActionPolicy.Evaluate(state with
        { CanNavigateToAnnotationRecovery = false, RepairingDeadAnnotations = true });
        update.Action.Should().Be(SectionGuidedActionKind.ApplySelected);
        update.Title.Should().Contain("ושחזר");
        update.Detail.Should().Contain("גם תיקון הרישום יבוטל");
    }

    [Fact]
    public void MultipleRepairsRequireBatch_WithoutHidingAvailablePreparationActions()
    {
        var state = Selected() with { RequiresBatchAnnotationRecovery = true, CanApplySelected = true };
        SectionGuidedActionPolicy.Evaluate(state).Action.Should().Be(SectionGuidedActionKind.InspectIssues);
        SectionGuidedActionPolicy.Evaluate(state with { CanNameSpans = true }).Action
            .Should().Be(SectionGuidedActionKind.NameSpans);
    }

    [Fact]
    public void NewUsableButEmptyProfile_OpensSourceSetupBeforePlanning()
    {
        var initial = new SectionGuidedActionSnapshot
        { HasDrawing = true, ProfileUsable = true, NeedsSectionSetup = true };
        var decision = SectionGuidedActionPolicy.Evaluate(initial);
        decision.Action.Should().Be(SectionGuidedActionKind.ConfigureSectionSources);
        decision.Detail.Should().Contain("קובץ CL נפרד").And.Contain("LINE")
            .And.Contain("שאישר המהנדס").And.Contain("אומדן זמינים גם ללא CL");
        // Cancel/reopen is not an approval and does not advance the source snapshot.
        SectionGuidedActionPolicy.Evaluate(initial).Should().Be(decision);
        SectionGuidedActionPolicy.Evaluate(initial with { NeedsSectionSetup = false })
            .Action.Should().Be(SectionGuidedActionKind.Plan);
    }

    [Fact]
    public void MissingClCurrentPlan_OpensBothHostLayerAndExternalFileRecovery()
    {
        var state = Selected() with
        {
            PlanRecordCount = 0, SelectedRecord = false, CanRecoverClSource = true,
            GlobalPlanningBlockReason = "לא נמצאה אף הוראת CL",
        };
        var decision = SectionGuidedActionPolicy.Evaluate(state);
        decision.Action.Should().Be(SectionGuidedActionKind.ConfigureSectionSources);
        decision.Detail.Should().Contain("שכבת CL במודל").And.Contain("קובץ CL נפרד")
            .And.Contain("אינה יוצרת חתכים").And.Contain("אומדן אינם דורשים CL");
        SectionGuidedActionPolicy.Evaluate(state with { StalePlan = true })
            .Action.Should().Be(SectionGuidedActionKind.Plan);
        SectionGuidedActionPolicy.Evaluate(state with { EvidenceBlocked = true })
            .Action.Should().Be(SectionGuidedActionKind.InspectIssues);
    }

    [Fact]
    public void EmptyPlanEvidenceOrUnrelatedGlobalFailure_IsNotMisdiagnosedAsMissingCl()
    {
        var empty = Selected() with { PlanRecordCount = 0, SelectedRecord = false };
        SectionGuidedActionPolicy.Evaluate(empty with { EvidenceBlocked = true })
            .Action.Should().Be(SectionGuidedActionKind.InspectIssues);
        SectionGuidedActionPolicy.Evaluate(empty with { GlobalPlanningBlockReason = "source hash changed" })
            .Action.Should().Be(SectionGuidedActionKind.InspectIssues);
    }

    [Fact]
    public void NoAlignmentCrossing_OffersSourceReviewWithoutInventingCivilAlignment()
    {
        var state = Selected() with { SelectedHasNoAlignmentCrossing = true };
        var decision = SectionGuidedActionPolicy.Evaluate(state);
        decision.Action.Should().Be(SectionGuidedActionKind.ConfigureSectionSources);
        decision.Detail.Should().Contain("תוואי Civil").And.Contain("שאישר המהנדס")
            .And.Contain("XREF לבדו אינו תוואי");
        state.HasCreatedView.Should().BeFalse();
        SectionGuidedActionPolicy.Evaluate(state with { EvidenceBlocked = true })
            .Action.Should().Be(SectionGuidedActionKind.InspectIssues);
        SectionGuidedActionPolicy.Evaluate(state with { CanChooseCrossing = true })
            .Action.Should().Be(SectionGuidedActionKind.ChooseCrossing);
    }

    /// <summary>
    /// Live 29/09: after STA-12145 was created and verified, the ready STA-42676 row
    /// showed "בדוק פרטי רשומה" and its details listed no blocker. The committed
    /// APPLY had consumed the plan; the guide must ask for a fresh PLAN instead.
    /// </summary>
    [Fact]
    public void ReadyRecordAfterAnotherCommittedApply_AsksForAFreshPlan_NotRowDetails()
    {
        var state = Selected() with { ApplyAwaitsFreshPlan = true };
        var decision = SectionGuidedActionPolicy.Evaluate(state);
        decision.Action.Should().Be(SectionGuidedActionKind.Plan);
        decision.ButtonText.Should().Be("תכנן מחדש");
        decision.Title.Should().Contain("חתך נוסף");
        decision.Detail.Should().Contain("אינו משנה את השרטוט");

        // Creation itself stays first when it is allowed, and a missing decision
        // or blocked evidence is still reported before re-planning.
        SectionGuidedActionPolicy.Evaluate(state with { CanApplySelected = true })
            .Action.Should().Be(SectionGuidedActionKind.ApplySelected);
        SectionGuidedActionPolicy.Evaluate(state with { CanNameSpans = true })
            .Action.Should().Be(SectionGuidedActionKind.NameSpans);
        SectionGuidedActionPolicy.Evaluate(state with { EvidenceBlocked = true })
            .Action.Should().Be(SectionGuidedActionKind.InspectIssues);
        SectionGuidedActionPolicy.Evaluate(state with { ApplyAwaitsFreshPlan = false })
            .Action.Should().Be(SectionGuidedActionKind.InspectIssues);
    }

    /// <summary>
    /// Audit Z24 (30.09.2026): the guide is gender-neutral for every Mahod employee, and the card texts it pictures
    /// were masculine imperatives ("בדוק…", "הרץ תכנון מחדש", "הצג…"). Card details use the impersonal form; titles and
    /// button captions keep the usual short form.
    /// </summary>
    [Fact]
    public void CardDetailsShownInTheGuide_UseTheImpersonalForm()
    {
        var states = new[]
        {
            Selected() with { StalePlan = true },
            Selected() with { HasPlan = false },
            Selected() with { CanRecoverClSource = true },
            Selected() with { PlanRecordCount = 0, SelectedRecord = false },
            Selected() with { SelectedHasNoAlignmentCrossing = true },
            Selected() with { CanApproveRow = true },
            Selected() with { CanNameSpans = true },
            Selected() with { CanResolveDirection = true },
            Selected() with { HasCreatedView = true, SelectedVerified = true },
            Selected() with { CanVerifySelected = true },
            Selected() with { CanApplySelected = true },
            Selected() with { ApplyAwaitsFreshPlan = true },
            new SectionGuidedActionSnapshot { HasDrawing = true, ProfileUsable = true, NeedsSectionSetup = true },
        };
        // A masculine imperative as a whole word (not the infinitive "לבדוק", "להריץ", "לאשר"…).
        var imperative = new System.Text.RegularExpressions.Regex(
            @"(?<![֐-׿])(הרץ|בדוק|הצג|שמור|צור|אשר|פתח|בחר|חזור|תקן)(?=\s)");
        foreach (var state in states)
        {
            var detail = SectionGuidedActionPolicy.Evaluate(state).Detail;
            imperative.IsMatch(detail).Should().BeFalse("card detail \"{0}\" is read by every employee", detail);
        }
        SectionGuidedActionPolicy.Evaluate(Selected() with { CanNameSpans = true }).Detail
            .Should().StartWith("יש לבדוק את הרצועות");
        SectionGuidedActionPolicy.Evaluate(Selected() with { ApplyAwaitsFreshPlan = true }).Detail
            .Should().Contain("יש להריץ תכנון מחדש");
        ResultScope.For("a.dwg", "h1").StaleReason("b.dwg", "h1").Should().Be("התוצאה שייכת לשרטוט אחר — יש להריץ תכנון מחדש");
        ResultScope.For("a.dwg", "h1").StaleReason("a.dwg", "h2").Should().Be("פרופיל הפרויקט השתנה מאז התכנון — יש להריץ תכנון מחדש");
    }

    [Fact]
    public void MissingDocumentOrInvalidProfile_RecoversBeforeEmptyProfileSetup()
    {
        var state = new SectionGuidedActionSnapshot { NeedsSectionSetup = true };
        SectionGuidedActionPolicy.Evaluate(state).Action.Should().Be(SectionGuidedActionKind.OpenDrawing);
        SectionGuidedActionPolicy.Evaluate(state with { HasDrawing = true })
            .Action.Should().Be(SectionGuidedActionKind.ConfigureProfile);
        SectionGuidedActionPolicy.Evaluate(state with
        { HasDrawing = true, ProfileUsable = true, PreviewCleanupBlocked = true })
            .Action.Should().Be(SectionGuidedActionKind.RecoverPreview);
    }
}
