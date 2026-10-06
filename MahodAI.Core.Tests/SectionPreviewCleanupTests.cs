using System;
using System.Collections.Generic;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class SectionPreviewCleanupTests
{
    private sealed class Context : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void EmptyCleanup_DoesNotEnterHostContext_EvenWhenOwnerHasClosed()
    {
        var handles = new List<int>();
        var entered = false;
        SectionPreviewCleanup.Clear<int, Context>(handles,
            () => { entered = true; throw new InvalidOperationException("closed graphics context"); },
            (_, _) => throw new Exception("must not erase"),
            _ => throw new Exception("must not dispose"));
        entered.Should().BeFalse();
        handles.Should().BeEmpty();
    }

    [Theory]
    [InlineData(10L, 20L, true)]
    [InlineData(null, 20L, true)]
    [InlineData(0L, 20L, true)]
    [InlineData(10L, 10L, false)]
    [InlineData(10L, null, true)]
    public void UnavailableOrDifferentOwner_RetainsEveryHandleWithoutErasing(
        long? owner, long? active, bool ownerIsOpen)
    {
        var handles = new List<int> { 1, 2 };
        var erased = new List<int>();
        Action cleanup = () => SectionPreviewCleanup.Clear(handles,
            () =>
            {
                SectionPreviewCleanup.RequireActiveOwner(owner, active, ownerIsOpen);
                return new Context();
            },
            (_, handle) => erased.Add(handle), _ => throw new Exception("must not dispose"));
        cleanup.Should().Throw<InvalidOperationException>();
        handles.Should().Equal(1, 2);
        erased.Should().BeEmpty();
    }

    [Fact]
    public void ReturnToOwner_RetryErasesTheRetainedHandlesAndReleasesContext()
    {
        var handles = new List<int> { 1, 2 };
        var active = 20L;
        var erased = new List<int>();
        var disposed = new List<int>();
        var context = new Context();
        Action cleanup = () => SectionPreviewCleanup.Clear(handles,
            () =>
            {
                SectionPreviewCleanup.RequireActiveOwner(10, active, true);
                return context;
            }, (_, handle) => erased.Add(handle), handle => disposed.Add(handle));
        cleanup.Should().Throw<InvalidOperationException>();
        active = 10;
        cleanup.Should().NotThrow();
        erased.Should().Equal(1, 2);
        disposed.Should().Equal(1, 2);
        handles.Should().BeEmpty();
        context.Disposed.Should().BeTrue();
    }

    [Fact]
    public void PartialNativeFailure_OnlyFailedHandleSurvives_AndRetryDoesNotEraseOthersAgain()
    {
        var handles = new List<int> { 1, 2, 3 };
        var eraseCalls = new List<int>();
        var disposed = new List<int>();
        var fail = true;
        var contexts = new List<Context>();
        Action cleanup = () => SectionPreviewCleanup.Clear(handles,
            () => { var context = new Context(); contexts.Add(context); return context; },
            (_, handle) =>
            {
                eraseCalls.Add(handle);
                if (handle == 2 && fail) throw new InvalidOperationException("native erase failed");
            }, handle => disposed.Add(handle));
        cleanup.Should().Throw<AggregateException>()
            .Which.InnerExceptions.Should().ContainSingle();
        handles.Should().Equal(2);
        disposed.Should().Equal(1, 3);
        contexts[0].Disposed.Should().BeTrue();

        fail = false;
        cleanup.Should().NotThrow();
        eraseCalls.Should().Equal(1, 2, 3, 2);
        disposed.Should().Equal(1, 3, 2);
        handles.Should().BeEmpty();
        contexts[1].Disposed.Should().BeTrue();
    }

    [Fact]
    public void DisposingAlreadyErasedHandleFailure_IsAWarning_NotAFictitiousVisiblePreview()
    {
        var handles = new List<int> { 1 };
        var warnings = new List<Exception>();
        SectionPreviewCleanup.Clear(handles, () => new Context(), (_, _) => { },
            _ => throw new InvalidOperationException("dispose failed"), warnings.Add);
        handles.Should().BeEmpty();
        warnings.Should().ContainSingle().Which.Message.Should().Be("dispose failed");
    }
}
