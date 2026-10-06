using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionVerificationRecoveryPolicy;

namespace MahodAI.Core.Tests.CivilDelivery;

public sealed class SectionVerificationRecoveryPolicyTests
{
    private static Identity Original => new("C:/local/host.dwg", "project", "profile-hash", "CL1", "key1", "inputs1");

    [Fact] public void FreshReplanAndReopenCanRevalidateExactCommittedIdentity() =>
        Assert.Null(Rejection(Original, Original with { }, true, true, true, true));

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void MissingEvidenceRollbackFailedRecordOrAmbiguousOwnershipCannotRecover(
        bool published, bool committed, bool applied, bool owned) =>
        Assert.NotNull(Rejection(Original, Original, published, committed, applied, owned));

    [Fact] public void OtherDrawingNeverInheritsApply() => Assert.NotNull(
        Rejection(Original, Original with { Drawing = "C:/other/host.dwg" }, true, true, true, true));
    [Fact] public void ProfileChangeRequiresUpdate() => Assert.NotNull(
        Rejection(Original, Original with { ProfileHash = "changed" }, true, true, true, true));
    [Fact] public void InputChangeRequiresUpdate() => Assert.NotNull(
        Rejection(Original, Original with { InputContract = "changed" }, true, true, true, true));
    [Fact] public void AnotherSelectedRecordNeverInheritsApply() => Assert.NotNull(
        Rejection(Original, Original with { RecordId = "CL2" }, true, true, true, true));

    [Fact] public void EquivalentChainsWithDifferentCollinearSamplingMatch() => Assert.Null(SurfaceMismatch(
        new[] { new Sample(-3, 10), new Sample(0, 13), new Sample(3, 16) },
        new[] { new Sample(-3, 10), new Sample(3, 16) }));
    [Fact] public void NewTerrainCrestBetweenOldSectionPointsCannotPass() => Assert.NotNull(SurfaceMismatch(
        new[] { new Sample(-3, 10), new Sample(0, 12), new Sample(3, 10) },
        new[] { new Sample(-3, 10), new Sample(3, 10) }));
    [Fact] public void EditedSectionCrestCannotPass() => Assert.NotNull(SurfaceMismatch(
        new[] { new Sample(-3, 10), new Sample(3, 10) },
        new[] { new Sample(-3, 10), new Sample(0, 12), new Sample(3, 10) }));
    [Fact] public void CoverageChangeCannotExtrapolateGreen() => Assert.NotNull(SurfaceMismatch(
        new[] { new Sample(-2, 10), new Sample(3, 10) },
        new[] { new Sample(-3, 10), new Sample(3, 10) }));
    [Fact] public void DuplicateOffsetsAreAmbiguous() => Assert.NotNull(SurfaceMismatch(
        new[] { new Sample(-3, 10), new Sample(-3, 11), new Sample(3, 10) },
        new[] { new Sample(-3, 10), new Sample(3, 10) }));
    [Fact] public void NonfiniteSourceCannotPass() => Assert.NotNull(SurfaceMismatch(
        new[] { new Sample(-3, double.NaN), new Sample(3, 10) },
        new[] { new Sample(-3, 10), new Sample(3, 10) }));

    [Fact] public void StaleCreatedSectionOffersNewValidationNotOldGreen()
    {
        var result = SectionGuidedActionPolicy.Evaluate(new()
        {
            HasDrawing = true, ProfileUsable = true, HasPlan = true, SelectedRecord = true,
            PlanRecordCount = 1, StalePlan = true, HasCreatedView = true,
            CanRevalidateCreatedView = true, SelectedVerified = true,
        });
        Assert.Equal(SectionGuidedActionKind.VerifySelected, result.Action);
        Assert.Equal(SectionGuidedStage.Verification, result.Stage);
    }
    [Fact] public void EvidencePublicationFailureCannotOfferRevalidationShortcut()
    {
        var result = SectionGuidedActionPolicy.Evaluate(new()
        {
            HasDrawing = true, ProfileUsable = true, HasPlan = true, SelectedRecord = true,
            PlanRecordCount = 1, StalePlan = true, EvidenceBlocked = true,
            CanRevalidateCreatedView = true,
        });
        Assert.NotEqual(SectionGuidedActionKind.VerifySelected, result.Action);
    }
    [Fact] public void ANewUncreatedStaleRecordMustReplan()
    {
        var result = SectionGuidedActionPolicy.Evaluate(new()
        {
            HasDrawing = true, ProfileUsable = true, HasPlan = true, SelectedRecord = true,
            PlanRecordCount = 1, StalePlan = true,
        });
        Assert.Equal(SectionGuidedActionKind.Plan, result.Action);
    }
}
