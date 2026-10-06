using System.Collections.ObjectModel;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.CivilDelivery;

public sealed class SectionRefreshReentrancyTests
{
    [Fact] public void SynchronousClearSelectionRefreshCannotDoubleTwentyEightRowsOr109Spans()
    {
        var guard = new NonReentrantExecution();
        var rows = new ObservableCollection<int>(Enumerable.Range(0, 28));
        var planSpans = Enumerable.Range(0, 28).Select(i => i == 0 ? 109 : 0).ToArray();
        void Rebuild() => guard.Run(() => { rows.Clear(); foreach (var count in planSpans) rows.Add(count); });
        // Models the synchronous selection/gate callback caused by Clear/Add.
        rows.CollectionChanged += (_, _) => Rebuild();
        Rebuild(); Rebuild();
        Assert.Equal(28, rows.Count);
        Assert.Equal(109, rows.Sum());
        Assert.False(guard.IsActive);
    }

    [Fact] public void AFailedRefreshAlwaysReleasesGuard()
    {
        var guard = new NonReentrantExecution();
        Assert.Throws<InvalidOperationException>(() => guard.Run(() => throw new InvalidOperationException()));
        Assert.False(guard.IsActive);
        var ran = false;
        Assert.True(guard.Run(() => ran = true)); Assert.True(ran);
    }

    [Fact] public void ExplicitRebuildGuideTakesPriorityOverAnotherVerifyButNeverStaleOrBlockedEvidence()
    {
        var state = new SectionGuidedActionSnapshot { HasDrawing = true, ProfileUsable = true,
            HasPlan = true, SelectedRecord = true, PlanRecordCount = 28, HasCreatedView = true,
            CanRebuildSelected = true, CanVerifySelected = true };
        var admitted = SectionGuidedActionPolicy.Evaluate(state);
        Assert.Equal(SectionGuidedActionKind.ApplySelected, admitted.Action);
        Assert.Contains("מחדש", admitted.ButtonText);
        Assert.NotEqual(SectionGuidedActionKind.ApplySelected,
            SectionGuidedActionPolicy.Evaluate(state with { StalePlan = true }).Action);
        Assert.NotEqual(SectionGuidedActionKind.ApplySelected,
            SectionGuidedActionPolicy.Evaluate(state with { EvidenceBlocked = true }).Action);
    }
}
