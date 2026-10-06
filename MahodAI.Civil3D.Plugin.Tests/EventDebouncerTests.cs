using System.Threading;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Events;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    public class EventDebouncerTests
    {
        #region Basic Behavior

        [Fact]
        public void AddChange_IncreasesPendingCount()
        {
            using var debouncer = new EventDebouncer(debounceDelayMs: 5000);
            debouncer.AddChange(new ChangeEvent { ObjectId = "obj1", ChangeType = ChangeType.Modified });

            debouncer.PendingCount.Should().Be(1);
            debouncer.HasPendingChanges.Should().BeTrue();
        }

        [Fact]
        public void Flush_SendsBatchAndResetsPending()
        {
            ChangeBatch? received = null;
            using var debouncer = new EventDebouncer(
                onBatchReady: batch => received = batch,
                debounceDelayMs: 5000);

            debouncer.AddChange(new ChangeEvent { ObjectId = "obj1", ChangeType = ChangeType.Added });
            debouncer.Flush();

            received.Should().NotBeNull();
            received!.Changes.Should().HaveCount(1);
            debouncer.PendingCount.Should().Be(0);
        }

        [Fact]
        public void Flush_DoesNothing_WhenNoPendingChanges()
        {
            ChangeBatch? received = null;
            using var debouncer = new EventDebouncer(
                onBatchReady: batch => received = batch);

            debouncer.Flush();
            received.Should().BeNull();
        }

        [Fact]
        public void Clear_RemovesPendingWithoutSending()
        {
            ChangeBatch? received = null;
            using var debouncer = new EventDebouncer(
                onBatchReady: batch => received = batch,
                debounceDelayMs: 5000);

            debouncer.AddChange(new ChangeEvent { ObjectId = "obj1", ChangeType = ChangeType.Added });
            debouncer.Clear();

            debouncer.PendingCount.Should().Be(0);
            debouncer.HasPendingChanges.Should().BeFalse();
            received.Should().BeNull();
        }

        #endregion

        #region Debounce Timer

        [Fact]
        public void DebouncedCallback_FiresAfterDelay()
        {
            ChangeBatch? received = null;
            using var debouncer = new EventDebouncer(
                onBatchReady: batch => received = batch,
                debounceDelayMs: 100);

            debouncer.AddChange(new ChangeEvent { ObjectId = "obj1", ChangeType = ChangeType.Modified });

            // Should not have fired yet
            received.Should().BeNull();

            // Wait for debounce to fire
            Thread.Sleep(300);

            received.Should().NotBeNull();
            received!.Changes.Should().HaveCount(1);
        }

        [Fact]
        public void RapidChanges_ResetTimer_BatchAllTogether()
        {
            ChangeBatch? received = null;
            using var debouncer = new EventDebouncer(
                onBatchReady: batch => received = batch,
                debounceDelayMs: 200);

            // Add multiple changes rapidly (each resets the timer)
            debouncer.AddChange(new ChangeEvent { ObjectId = "obj1", ChangeType = ChangeType.Modified });
            Thread.Sleep(50);
            debouncer.AddChange(new ChangeEvent { ObjectId = "obj2", ChangeType = ChangeType.Added });
            Thread.Sleep(50);
            debouncer.AddChange(new ChangeEvent { ObjectId = "obj3", ChangeType = ChangeType.Deleted });

            // Not enough time has passed since last change
            received.Should().BeNull();

            // Wait for debounce to fire after last change
            Thread.Sleep(400);

            received.Should().NotBeNull();
            received!.Changes.Should().HaveCount(3);
        }

        #endregion

        #region MaxPending Auto-Flush

        [Fact]
        public void MaxPendingChanges_ForcesFlush()
        {
            ChangeBatch? received = null;
            using var debouncer = new EventDebouncer(
                onBatchReady: batch => received = batch,
                debounceDelayMs: 5000, // long delay to prove force flush triggers
                maxPendingChanges: 3);

            debouncer.AddChange(new ChangeEvent { ObjectId = "obj1", ChangeType = ChangeType.Added });
            debouncer.AddChange(new ChangeEvent { ObjectId = "obj2", ChangeType = ChangeType.Added });

            // Not yet at max
            received.Should().BeNull();

            // This should trigger force flush
            debouncer.AddChange(new ChangeEvent { ObjectId = "obj3", ChangeType = ChangeType.Added });

            received.Should().NotBeNull();
            received!.Changes.Should().HaveCount(3);
            debouncer.PendingCount.Should().Be(0);
        }

        #endregion

        #region BatchReady Event

        [Fact]
        public void BatchReadyEvent_Fires_OnFlush()
        {
            ChangeBatch? eventBatch = null;
            using var debouncer = new EventDebouncer(debounceDelayMs: 5000);
            debouncer.BatchReady += (sender, batch) => eventBatch = batch;

            debouncer.AddChange(new ChangeEvent { ObjectId = "obj1", ChangeType = ChangeType.Modified });
            debouncer.Flush();

            eventBatch.Should().NotBeNull();
            eventBatch!.Changes.Should().HaveCount(1);
        }

        #endregion

        #region Dispose

        [Fact]
        public void Dispose_PreventsNewChanges()
        {
            ChangeBatch? received = null;
            var debouncer = new EventDebouncer(
                onBatchReady: batch => received = batch,
                debounceDelayMs: 5000);

            debouncer.Dispose();
            debouncer.AddChange(new ChangeEvent { ObjectId = "obj1", ChangeType = ChangeType.Added });

            debouncer.PendingCount.Should().Be(0);
            received.Should().BeNull();
        }

        #endregion
    }
}
