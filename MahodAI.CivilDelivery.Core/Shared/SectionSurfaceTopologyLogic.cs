namespace MahodAI.CivilDelivery.Shared;

/// <summary>Reads Civil SectionPoint segment topology without inventing lines through gaps.</summary>
public static class SectionSurfaceTopologyLogic
{
    public readonly record struct Point(double X, double Y, double Z, int SegmentTo);

    /// <summary>
    /// SegmentTo is the native index of the next connected point, or -1 when no
    /// outgoing segment exists. A terminal point with an incoming edge is real;
    /// a point with neither incoming nor outgoing edges is not surface geometry.
    /// The current annotation/verification contract supports one continuous chain
    /// only. Multiple components, branching, cycles and malformed links fail closed.
    /// The complete chain is returned left to right, preserving vertical-face sides.
    /// </summary>
    public static IReadOnlyList<Point> RequireSingleChain(IReadOnlyList<Point> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var incoming = new int[points.Count];
        var participating = new bool[points.Count];
        var edgeCount = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
                throw Invalid($"point {i} is non-finite");
            var next = point.SegmentTo;
            if (next < -1 || next >= points.Count || next == i)
                throw Invalid($"point {i} has invalid SegmentTo={next}");
            if (next == -1) continue;
            if (++incoming[next] > 1)
                throw Invalid($"point {next} has multiple incoming segments");
            participating[i] = participating[next] = true;
            edgeCount++;
        }
        if (edgeCount == 0) throw Invalid("no connected surface segment");

        var starts = Enumerable.Range(0, points.Count)
            .Where(i => participating[i] && incoming[i] == 0).ToList();
        if (starts.Count != 1)
            throw Invalid($"expected one continuous chain, found {starts.Count} starts (gap or cycle)");

        var chain = new List<Point>();
        var visited = new HashSet<int>();
        var index = starts[0];
        var direction = 0;
        while (index != -1)
        {
            if (!visited.Add(index)) throw Invalid($"cycle at point {index}");
            var point = points[index];
            if (chain.Count > 0)
            {
                var step = Math.Sign(point.X - chain[^1].X);
                // Equal offsets retain native vertical faces. Sorting a reversing
                // chain would change its segment geometry, so do not normalize it.
                if (step != 0 && direction != 0 && step != direction)
                    throw Invalid($"offset direction reverses at point {index}");
                if (step != 0) direction = step;
            }
            chain.Add(point);
            index = point.SegmentTo;
        }
        if (visited.Count != participating.Count(value => value))
            throw Invalid("disconnected segment component or cycle");
        // Reverse the whole traversal, including equal-offset points. Stable sorting
        // alone would reverse horizontal direction but not vertical-face ordering,
        // changing the adjacent segments and their one-sided slope endpoints.
        if (direction < 0) chain.Reverse();
        return chain;
    }

    private static InvalidOperationException Invalid(string reason) =>
        new($"Native Section surface topology is unproven: {reason}.");
}
