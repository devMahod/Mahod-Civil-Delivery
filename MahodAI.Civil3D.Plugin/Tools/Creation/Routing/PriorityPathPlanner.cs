using System;
using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Tools.Creation.Routing
{
    /// <summary>
    /// Plans a centerline that follows an engineer-drawn priority path (e.g. the blue
    /// "box" polyline) as the FIRST priority over the surface, rerouting only the spans
    /// the surface won't allow.
    ///
    /// For each consecutive pair of drawn vertices: if the straight segment is buildable
    /// (caller-supplied <c>chordViable</c>) the drawn segment is honored verbatim — so a
    /// straight drawn path yields a straight road. Where a segment is NOT buildable
    /// (off-surface / through a no-build zone), that span alone is A*-rerouted across
    /// walkable terrain (<c>subRoute</c>) and recorded so the engineer is told which
    /// station range was changed. Only when a span has no route at all (the surface
    /// genuinely cannot host the road there) does the plan come back infeasible, naming
    /// the offending span — the engineer's "tell me when it's impossible" case.
    ///
    /// Pure geometry/orchestration (no AutoCAD host types): the surface tests and the A*
    /// search are injected as delegates, so the branching logic is unit-testable.
    /// </summary>
    public static class PriorityPathPlanner
    {
        public sealed class Plan
        {
            /// <summary>Concatenated PIs of the planned centerline (drawn segments + rerouted spans), endpoints included. Empty when <see cref="Infeasible"/>.</summary>
            public List<Pt2> Path { get; } = new();

            /// <summary>Indices (into the drawn-vertex spans) of the spans that had to be rerouted off the drawn line.</summary>
            public List<int> ReroutedSpanIndices { get; } = new();

            /// <summary>True when a span could not be routed at all — the road cannot follow the drawn path there and no detour exists.</summary>
            public bool Infeasible { get; init; }

            /// <summary>Zero-based index of the first unroutable span (the i-th drawn segment), or -1.</summary>
            public int InfeasibleSpanIndex { get; init; } = -1;

            public Pt2 InfeasibleFrom { get; init; }
            public Pt2 InfeasibleTo { get; init; }
        }

        /// <param name="waypoints">Ordered drawn-polyline vertices (≥2), already snapped to walkable cells by the caller.</param>
        /// <param name="chordViable">Returns true iff the straight segment a→b is buildable (on the walkable mask at clearance).</param>
        /// <param name="subRoute">A* path a→b as PIs (including both endpoints) when the straight chord is not viable; null when no route exists.</param>
        public static Plan Build(
            IReadOnlyList<Pt2> waypoints,
            Func<Pt2, Pt2, bool> chordViable,
            Func<Pt2, Pt2, IReadOnlyList<Pt2>?> subRoute)
        {
            if (waypoints == null) throw new ArgumentNullException(nameof(waypoints));
            if (chordViable == null) throw new ArgumentNullException(nameof(chordViable));
            if (subRoute == null) throw new ArgumentNullException(nameof(subRoute));

            var plan = new Plan();
            if (waypoints.Count < 2)
            {
                foreach (var w in waypoints) plan.Path.Add(w);
                return plan;
            }

            plan.Path.Add(waypoints[0]);
            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                Pt2 a = waypoints[i], b = waypoints[i + 1];

                if (chordViable(a, b))
                {
                    // The drawn segment is buildable — honor it verbatim (first priority).
                    plan.Path.Add(b);
                    continue;
                }

                // Drawn segment leaves the surface — reroute just this span.
                var detour = subRoute(a, b);
                if (detour == null || detour.Count < 2)
                {
                    return new Plan
                    {
                        Infeasible = true,
                        InfeasibleSpanIndex = i,
                        InfeasibleFrom = a,
                        InfeasibleTo = b,
                    };
                }

                plan.ReroutedSpanIndices.Add(i);
                // detour[0] == a == plan.Path[^1] — skip it to avoid a duplicate vertex.
                for (int k = 1; k < detour.Count; k++) plan.Path.Add(detour[k]);
            }

            return plan;
        }
    }
}
