using System.Collections.Generic;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Events;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    public class ChangeEventTests
    {
        #region ChangeEvent Properties

        [Fact]
        public void ChangeEvent_DefaultCategory_IsOther()
        {
            var evt = new ChangeEvent();
            evt.Category.Should().Be(ObjectCategories.Other);
        }

        [Fact]
        public void ChangeEvent_HasDefaultTimestamp()
        {
            var evt = new ChangeEvent();
            evt.Timestamp.Should().BeCloseTo(System.DateTime.UtcNow, System.TimeSpan.FromSeconds(5));
        }

        #endregion

        #region StationRange

        [Fact]
        public void StationRange_Overlaps_WhenRangesIntersect()
        {
            var r1 = new Events.StationRange(0, 100);
            var r2 = new Events.StationRange(50, 150);
            r1.Overlaps(r2).Should().BeTrue();
        }

        [Fact]
        public void StationRange_DoesNotOverlap_WhenRangesDisjoint()
        {
            var r1 = new Events.StationRange(0, 50);
            var r2 = new Events.StationRange(60, 100);
            r1.Overlaps(r2).Should().BeFalse();
        }

        [Fact]
        public void StationRange_Overlaps_WhenTouching()
        {
            var r1 = new Events.StationRange(0, 50);
            var r2 = new Events.StationRange(50, 100);
            r1.Overlaps(r2).Should().BeTrue();
        }

        [Fact]
        public void StationRange_ExpandTo_ExtendsRange()
        {
            var r1 = new Events.StationRange(10, 50);
            r1.ExpandTo(new Events.StationRange(0, 100));
            r1.Start.Should().Be(0);
            r1.End.Should().Be(100);
        }

        [Fact]
        public void StationRange_ExpandTo_NoChangeWhenSubset()
        {
            var r1 = new Events.StationRange(0, 100);
            r1.ExpandTo(new Events.StationRange(20, 80));
            r1.Start.Should().Be(0);
            r1.End.Should().Be(100);
        }

        #endregion

        #region ChangeBatch Merge Logic

        [Fact]
        public void ChangeBatch_AddedThenDeleted_RemovesBoth()
        {
            var batch = new ChangeBatch();
            batch.AddChange(new ChangeEvent { ObjectId = "obj1", ChangeType = ChangeType.Added });
            batch.AddChange(new ChangeEvent { ObjectId = "obj1", ChangeType = ChangeType.Deleted });

            batch.HasChanges.Should().BeFalse();
            batch.Changes.Should().BeEmpty();
        }

        [Fact]
        public void ChangeBatch_AddedThenModified_StaysAdded()
        {
            var batch = new ChangeBatch();
            batch.AddChange(new ChangeEvent { ObjectId = "obj1", ChangeType = ChangeType.Added });
            batch.AddChange(new ChangeEvent { ObjectId = "obj1", ChangeType = ChangeType.Modified, ObjectName = "NewName" });

            batch.Changes.Should().HaveCount(1);
            batch.Changes[0].ChangeType.Should().Be(ChangeType.Added);
            batch.Changes[0].ObjectName.Should().Be("NewName");
        }

        [Fact]
        public void ChangeBatch_ModifiedThenDeleted_BecomesDeleted()
        {
            var batch = new ChangeBatch();
            batch.AddChange(new ChangeEvent { ObjectId = "obj1", ChangeType = ChangeType.Modified });
            batch.AddChange(new ChangeEvent { ObjectId = "obj1", ChangeType = ChangeType.Deleted });

            batch.Changes.Should().HaveCount(1);
            batch.Changes[0].ChangeType.Should().Be(ChangeType.Deleted);
        }

        [Fact]
        public void ChangeBatch_ModifiedThenModified_CombinesStationRanges()
        {
            var batch = new ChangeBatch();
            batch.AddChange(new ChangeEvent
            {
                ObjectId = "obj1",
                ChangeType = ChangeType.Modified,
                AffectedStations = new Events.StationRange(0, 100)
            });
            batch.AddChange(new ChangeEvent
            {
                ObjectId = "obj1",
                ChangeType = ChangeType.Modified,
                AffectedStations = new Events.StationRange(80, 200)
            });

            batch.Changes.Should().HaveCount(1);
            batch.Changes[0].AffectedStations!.Start.Should().Be(0);
            batch.Changes[0].AffectedStations!.End.Should().Be(200);
        }

        [Fact]
        public void ChangeBatch_ModifiedThenModified_CombinesChangedProperties()
        {
            var batch = new ChangeBatch();
            batch.AddChange(new ChangeEvent
            {
                ObjectId = "obj1",
                ChangeType = ChangeType.Modified,
                ChangedProperties = new List<string> { "Name" }
            });
            batch.AddChange(new ChangeEvent
            {
                ObjectId = "obj1",
                ChangeType = ChangeType.Modified,
                ChangedProperties = new List<string> { "Radius", "Name" }
            });

            batch.Changes.Should().HaveCount(1);
            batch.Changes[0].ChangedProperties.Should().Contain("Name");
            batch.Changes[0].ChangedProperties.Should().Contain("Radius");
            batch.Changes[0].ChangedProperties.Should().HaveCount(2); // no duplicates
        }

        [Fact]
        public void ChangeBatch_DifferentObjects_KeptSeparate()
        {
            var batch = new ChangeBatch();
            batch.AddChange(new ChangeEvent { ObjectId = "obj1", ChangeType = ChangeType.Added });
            batch.AddChange(new ChangeEvent { ObjectId = "obj2", ChangeType = ChangeType.Modified });

            batch.Changes.Should().HaveCount(2);
        }

        [Fact]
        public void ChangeBatch_Summary_CountsCorrectly()
        {
            var batch = new ChangeBatch();
            batch.AddChange(new ChangeEvent { ObjectId = "obj1", ChangeType = ChangeType.Added });
            batch.AddChange(new ChangeEvent { ObjectId = "obj2", ChangeType = ChangeType.Modified });
            batch.AddChange(new ChangeEvent { ObjectId = "obj3", ChangeType = ChangeType.Deleted });

            var summary = batch.Summary;
            summary.Added.Should().Be(1);
            summary.Modified.Should().Be(1);
            summary.Deleted.Should().Be(1);
            summary.Total.Should().Be(3);
        }

        #endregion

        #region ObjectCategories Constants

        [Fact]
        public void ObjectCategories_HasExpectedValues()
        {
            ObjectCategories.Alignment.Should().Be("Alignment");
            ObjectCategories.Profile.Should().Be("Profile");
            ObjectCategories.Surface.Should().Be("Surface");
            ObjectCategories.Corridor.Should().Be("Corridor");
            ObjectCategories.PipeNetwork.Should().Be("PipeNetwork");
        }

        #endregion
    }
}
