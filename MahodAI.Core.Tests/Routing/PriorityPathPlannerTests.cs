using System.Collections.Generic;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Routing
{
    /// <summary>
    /// Pure tests for the priority-path planner (honor the drawn polyline; reroute only
    /// the infeasible spans; report impossibility). No AutoCAD host types.
    /// </summary>
    public class PriorityPathPlannerTests
    {
        // Detour delegate that returns a single mid-vertex dogleg a → mid → b.
        private static System.Func<Pt2, Pt2, IReadOnlyList<Pt2>?> Dogleg(Pt2 mid) =>
            (a, b) => new List<Pt2> { a, mid, b };

        [Fact]
        public void Honors_a_fully_buildable_drawn_path_verbatim()
        {
            var wp = new[] { new Pt2(0, 0), new Pt2(100, 0), new Pt2(200, 50) };

            var plan = PriorityPathPlanner.Build(wp, chordViable: (_, _) => true,
                                                 subRoute: (_, _) => null);

            plan.Infeasible.Should().BeFalse();
            plan.ReroutedSpanIndices.Should().BeEmpty();
            plan.Path.Should().Equal(wp); // straight drawn path → straight road
        }

        [Fact]
        public void Reroutes_only_the_infeasible_span_and_records_it()
        {
            var wp = new[] { new Pt2(0, 0), new Pt2(100, 0), new Pt2(200, 0) };
            // First span buildable, second span (100→200) not → dogleg via (150, 40).
            bool ChordViable(Pt2 a, Pt2 b) => !(a.X == 100 && b.X == 200);

            var plan = PriorityPathPlanner.Build(wp, ChordViable, Dogleg(new Pt2(150, 40)));

            plan.Infeasible.Should().BeFalse();
            plan.ReroutedSpanIndices.Should().Equal(1);
            // Path: 0,0 → 100,0 (honored) → 150,40 → 200,0 (rerouted), no duplicate at 100,0.
            plan.Path.Should().Equal(
                new Pt2(0, 0), new Pt2(100, 0), new Pt2(150, 40), new Pt2(200, 0));
        }

        [Fact]
        public void Reports_infeasible_when_a_span_cannot_be_routed()
        {
            var wp = new[] { new Pt2(0, 0), new Pt2(100, 0), new Pt2(200, 0) };
            bool ChordViable(Pt2 a, Pt2 b) => !(a.X == 100 && b.X == 200);

            var plan = PriorityPathPlanner.Build(wp, ChordViable, subRoute: (_, _) => null);

            plan.Infeasible.Should().BeTrue();
            plan.InfeasibleSpanIndex.Should().Be(1);
            plan.InfeasibleFrom.Should().Be(new Pt2(100, 0));
            plan.InfeasibleTo.Should().Be(new Pt2(200, 0));
        }

        [Fact]
        public void No_duplicate_vertices_at_reroute_junctions()
        {
            var wp = new[] { new Pt2(0, 0), new Pt2(100, 0) };
            var plan = PriorityPathPlanner.Build(wp, (_, _) => false, Dogleg(new Pt2(50, 30)));

            plan.Path.Should().Equal(new Pt2(0, 0), new Pt2(50, 30), new Pt2(100, 0));
            plan.ReroutedSpanIndices.Should().Equal(0);
        }

        [Fact]
        public void Single_vertex_is_returned_as_is()
        {
            var wp = new[] { new Pt2(5, 5) };
            var plan = PriorityPathPlanner.Build(wp, (_, _) => true, (_, _) => null);
            plan.Path.Should().Equal(new Pt2(5, 5));
            plan.Infeasible.Should().BeFalse();
        }
    }
}
