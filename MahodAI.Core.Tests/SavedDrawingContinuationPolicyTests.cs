using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class SavedDrawingContinuationPolicyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ManuallyInvokedOrCancelledContinuation_HasNoPermissionToResume(
        bool sameDocument, bool sourceReady)
    {
        SavedDrawingContinuationPolicy.Decide(false, sameDocument, sourceReady)
            .Should().Be(SavedDrawingContinuationDecision.NoPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherDrawingCannotConsumeTheRequest_EvenIfItsSavedSourceIsReady(bool sourceReady)
    {
        SavedDrawingContinuationPolicy.Decide(true, false, sourceReady)
            .Should().Be(SavedDrawingContinuationDecision.DrawingChanged);
    }

    [Fact]
    public void FailedOrIncompleteSaveCannotResume_ButFreshSuccessfulReadbackCan()
    {
        SavedDrawingContinuationPolicy.Decide(true, true, false)
            .Should().Be(SavedDrawingContinuationDecision.SaveIncomplete);
        SavedDrawingContinuationPolicy.Decide(true, true, true)
            .Should().Be(SavedDrawingContinuationDecision.Resume);
    }

    [Fact]
    public void CivilRedirtyingTheDrawingDuringTheFirstSave_GetsExactlyOneMoreExplicitSave()
    {
        // 07/09 17:29: QSAVE completed (51,910,949 bytes on disk) and DBMOD read 1 again;
        // the second explicit save (18:18) read 0 and the PLAN ran.
        SavedDrawingContinuationPolicy.Decide(true, true, false, canSaveAndResume: true, savesRequested: 1)
            .Should().Be(SavedDrawingContinuationDecision.SaveAgain);
        SavedDrawingContinuationPolicy.Decide(true, true, false, canSaveAndResume: true, savesRequested: 2)
            .Should().Be(SavedDrawingContinuationDecision.SaveIncomplete, "a second dirty read-back is reported, never looped");
        SavedDrawingContinuationPolicy.Decide(true, true, false, canSaveAndResume: false, savesRequested: 1)
            .Should().Be(SavedDrawingContinuationDecision.SaveIncomplete, "an identity failure is never answered by saving again");
        SavedDrawingContinuationPolicy.Decide(true, true, false, canSaveAndResume: true, savesRequested: 0)
            .Should().Be(SavedDrawingContinuationDecision.SaveIncomplete, "a retry needs one completed explicit save first");
        SavedDrawingContinuationPolicy.Decide(true, true, true, canSaveAndResume: true, savesRequested: 1)
            .Should().Be(SavedDrawingContinuationDecision.Resume);
        SavedDrawingContinuationPolicy.Decide(true, false, false, canSaveAndResume: true, savesRequested: 1)
            .Should().Be(SavedDrawingContinuationDecision.DrawingChanged);
        SavedDrawingContinuationPolicy.Decide(false, true, false, canSaveAndResume: true, savesRequested: 1)
            .Should().Be(SavedDrawingContinuationDecision.NoPending);
        SavedDrawingContinuationPolicy.MaxExplicitSaves.Should().Be(2);
    }

    [Fact]
    public void SuccessfulContinuationIsOneShot_WhenCallerConsumesPendingBeforeInvokingCallback()
    {
        SavedDrawingContinuationPolicy.Decide(true, true, true)
            .Should().Be(SavedDrawingContinuationDecision.Resume);
        SavedDrawingContinuationPolicy.Decide(false, true, true)
            .Should().Be(SavedDrawingContinuationDecision.NoPending);
    }
}
