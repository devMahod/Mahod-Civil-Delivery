using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class SavedDrawingContinuationTokenTests
{
    [Theory]
    [InlineData(null, null, false, false)]
    [InlineData(null, null, true, false)]
    [InlineData("", "", true, false)]
    [InlineData("A", "A", false, false)]
    [InlineData("B", "A", true, false)]
    [InlineData("A", "a", true, false)]
    [InlineData("A", "A", true, true)]
    public void OnlyCurrentExplicitPendingTicketCanBeConsumed(
        string? current, string? queued, bool pending, bool allowed) =>
        Assert.Equal(allowed, SavedDrawingContinuationPolicy.CanConsume(current, queued, pending));

    [Fact]
    public void CancelThenRetryCannotBeResumedByOldCommand()
    {
        Assert.True(SavedDrawingContinuationPolicy.CanConsume("old", "old", true));
        Assert.False(SavedDrawingContinuationPolicy.CanConsume(null, "old", false));
        Assert.False(SavedDrawingContinuationPolicy.CanConsume("retry", "old", true));
        Assert.True(SavedDrawingContinuationPolicy.CanConsume("retry", "retry", true));
        Assert.False(SavedDrawingContinuationPolicy.CanConsume(null, "retry", false));
    }

    [Theory]
    [InlineData(false, true, SavedDrawingContinuationDecision.DrawingChanged)]
    [InlineData(true, false, SavedDrawingContinuationDecision.SaveIncomplete)]
    [InlineData(true, true, SavedDrawingContinuationDecision.Resume)]
    public void CorrectTicketStillRequiresSameSavedSource(bool sameDocument, bool saved,
        SavedDrawingContinuationDecision expected)
    {
        Assert.True(SavedDrawingContinuationPolicy.CanConsume("ticket", "ticket", true));
        Assert.Equal(expected, SavedDrawingContinuationPolicy.Decide(true, sameDocument, saved));
    }
}
