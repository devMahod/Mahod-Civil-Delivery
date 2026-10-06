using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Cache;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    public class DependencyGraphTests
    {
        #region AddDependency / HasDependency

        [Fact]
        public void AddDependency_CreatesBidirectionalRelationship()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("alignment1", "profile1");

            graph.HasDependency("alignment1", "profile1").Should().BeTrue();
            graph.GetDirectDependencies("profile1").Should().Contain("alignment1");
        }

        [Fact]
        public void HasDependency_ReturnsFalse_WhenNoDependencyExists()
        {
            var graph = new DependencyGraph();
            graph.HasDependency("a", "b").Should().BeFalse();
        }

        [Fact]
        public void AddDependency_HandlesMultipleChildren()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("alignment1", "profile1");
            graph.AddDependency("alignment1", "profile2");

            graph.GetDirectDependents("alignment1").Should().HaveCount(2);
            graph.GetDirectDependents("alignment1").Should().Contain("profile1");
            graph.GetDirectDependents("alignment1").Should().Contain("profile2");
        }

        [Fact]
        public void Count_ReflectsTotalRelationships()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("a", "b");
            graph.AddDependency("a", "c");
            graph.AddDependency("d", "e");

            graph.Count.Should().Be(3);
        }

        #endregion

        #region RemoveDependency

        [Fact]
        public void RemoveDependency_RemovesBothDirections()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("a", "b");
            graph.RemoveDependency("a", "b");

            graph.HasDependency("a", "b").Should().BeFalse();
            graph.GetDirectDependencies("b").Should().BeEmpty();
        }

        [Fact]
        public void RemoveDependency_DoesNotAffectOtherRelationships()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("a", "b");
            graph.AddDependency("a", "c");
            graph.RemoveDependency("a", "b");

            graph.HasDependency("a", "c").Should().BeTrue();
            graph.Count.Should().Be(1);
        }

        #endregion

        #region RemoveObject

        [Fact]
        public void RemoveObject_RemovesAsParent()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("a", "b");
            graph.AddDependency("a", "c");
            graph.RemoveObject("a");

            graph.Count.Should().Be(0);
            graph.GetDirectDependencies("b").Should().BeEmpty();
            graph.GetDirectDependencies("c").Should().BeEmpty();
        }

        [Fact]
        public void RemoveObject_RemovesAsChild()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("a", "b");
            graph.AddDependency("c", "b");
            graph.RemoveObject("b");

            graph.HasDependency("a", "b").Should().BeFalse();
            graph.HasDependency("c", "b").Should().BeFalse();
        }

        #endregion

        #region GetDependents (Transitive)

        [Fact]
        public void GetDependents_ReturnsTransitiveDependents()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("alignment", "profile");
            graph.AddDependency("profile", "corridor");

            var dependents = graph.GetDependents("alignment").ToList();
            dependents.Should().Contain("profile");
            dependents.Should().Contain("corridor");
        }

        [Fact]
        public void GetDependents_HandlesNoChildren()
        {
            var graph = new DependencyGraph();
            graph.GetDependents("lonely").Should().BeEmpty();
        }

        [Fact]
        public void GetDependents_HandlesCycleSafely()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("a", "b");
            graph.AddDependency("b", "c");
            graph.AddDependency("c", "a");

            // Should not infinite loop - visited set prevents re-processing
            var dependents = graph.GetDependents("a").ToList();
            dependents.Should().Contain("b");
            dependents.Should().Contain("c");
        }

        #endregion

        #region GetDependencies (Transitive)

        [Fact]
        public void GetDependencies_ReturnsTransitiveParents()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("alignment", "profile");
            graph.AddDependency("profile", "corridor");

            var dependencies = graph.GetDependencies("corridor").ToList();
            dependencies.Should().Contain("profile");
            dependencies.Should().Contain("alignment");
        }

        [Fact]
        public void GetDependencies_HandlesNoParents()
        {
            var graph = new DependencyGraph();
            graph.GetDependencies("root").Should().BeEmpty();
        }

        #endregion

        #region GetStats / Clear

        [Fact]
        public void GetStats_ReturnsCorrectStatistics()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("a", "b");
            graph.AddDependency("a", "c");
            graph.AddDependency("d", "c");

            var stats = graph.GetStats();
            stats.TotalRelationships.Should().Be(3);
            stats.ParentCount.Should().Be(2); // a, d
            stats.ChildCount.Should().Be(2); // b, c
            stats.MaxChildren.Should().Be(2); // a has 2 children
        }

        [Fact]
        public void Clear_RemovesAllRelationships()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("a", "b");
            graph.AddDependency("c", "d");
            graph.Clear();

            graph.Count.Should().Be(0);
            graph.GetStats().TotalObjects.Should().Be(0);
        }

        #endregion
    }
}
