using System;
using System.Collections.Generic;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;
using static MahodAI.CivilDelivery.Shared.SectionHatchBoundaryGeometry;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Reads a polyline's individual OCS spans without asking a whole malformed
/// native Curve to evaluate its parameters. No source geometry is modified.
/// </summary>
public static class SectionPolylineSegmentSampling
{
    public sealed record Result(IReadOnlyList<P2> Points, int? SkippedExactClosingSegmentIndex)
    {
        public int? SkippedNativeCoincidentClosingSegmentIndex { get; init; }
    }
    public sealed record Frame(V3 Origin, V3 XAxis, V3 YAxis, V3 ZAxis);
    public sealed record SourceReadResult(IReadOnlyList<BulgePoint> Vertices,
        int? UnreadClosingSegmentIndex, int? UnreadOpenTerminalVertexIndex)
    {
        public NativeCoincidentClosingCertificate? NativeCoincidentClosing { get; init; }
    }

    // Only ReadSource may issue this evidence, bound to its immutable vertex list.
    // It is not a tolerance-based permission for arbitrary Sample callers.
    public sealed class NativeCoincidentClosingCertificate
    {
        internal NativeCoincidentClosingCertificate(IReadOnlyList<BulgePoint> vertices, double maximumLinearScale)
        { Vertices = vertices; MaximumLinearScale = maximumLinearScale; }
        internal IReadOnlyList<BulgePoint> Vertices { get; }
        public double MaximumLinearScale { get; }
        public int SegmentIndex => Vertices.Count - 1;
    }

    /// <summary>
    /// Reads and validates every endpoint before touching any native bulge getter.
    /// Unread bulges belong to an open terminal vertex (no outgoing segment), an
    /// exactly coincident finite closing span, or a bounded non-exact closing span
    /// explicitly confirmed as Coincident by the native adapter. NaN represents
    /// unread data, not a measured/repaired zero; the reason is returned explicitly.
    /// Live getter exceptions propagate unchanged and no partial result is returned.
    /// </summary>
    public static SourceReadResult ReadSource(int count, bool closed,
        Func<int, P2> readPoint, Func<int, double> readBulge,
        Func<int, bool>? confirmNativeCoincident = null, double maximumLinearScale = 1)
    {
        ArgumentNullException.ThrowIfNull(readPoint);
        ArgumentNullException.ThrowIfNull(readBulge);
        if (count < 2) throw new ArgumentOutOfRangeException(nameof(count));
        if (!double.IsFinite(maximumLinearScale) || maximumLinearScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumLinearScale));
        var points = new P2[count];
        for (var index = 0; index < count; index++)
        {
            points[index] = readPoint(index);
            if (!double.IsFinite(points[index].X) || !double.IsFinite(points[index].Y))
                throw new ArgumentException($"Polyline endpoint {index} is non-finite.");
        }

        var vertices = new BulgePoint[count];
        int? unreadClosing = null, unreadOpenTerminal = null;
        var nativeCoincidentClosing = false;
        for (var index = 0; index < count; index++)
        {
            var point = points[index];
            double bulge;
            if (!closed && index == count - 1)
            {
                unreadOpenTerminal = index;
                bulge = double.NaN;
            }
            else if (IsExactClosingSegment(closed, index, count, point, points[0]))
            {
                unreadClosing = index;
                bulge = double.NaN;
            }
            else if (confirmNativeCoincident != null &&
                     IsBoundedNativeClosingCandidate(closed, index, count, point, points[0], maximumLinearScale) &&
                     confirmNativeCoincident(index))
            {
                nativeCoincidentClosing = true;
                bulge = double.NaN; // Explicitly unread; never a fabricated live zero bulge.
            }
            else
            {
                bulge = readBulge(index);
                if (!double.IsFinite(bulge))
                    throw new ArgumentException($"Polyline segment {index} has a non-finite live bulge.");
            }
            vertices[index] = new BulgePoint(point.X, point.Y, bulge);
        }
        var frozen = Array.AsReadOnly(vertices);
        return new SourceReadResult(frozen, unreadClosing, unreadOpenTerminal)
        {
            NativeCoincidentClosing = nativeCoincidentClosing
                ? new NativeCoincidentClosingCertificate(frozen, maximumLinearScale) : null,
        };
    }

    public static Result Sample(IReadOnlyList<BulgePoint> source, bool closed,
        double maximumLinearScale = 1, double maxSagittaM = .005, int maximumVertices = int.MaxValue,
        NativeCoincidentClosingCertificate? nativeCoincidentClosing = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 2 || source.Count > maximumVertices || maximumVertices < 2 ||
            !double.IsFinite(maximumLinearScale) || maximumLinearScale <= 0 ||
            !double.IsFinite(maxSagittaM) || maxSagittaM <= 0)
            throw new ArgumentException("Invalid bounded polyline sampling inputs.");
        foreach (var vertex in source)
            if (!double.IsFinite(vertex.X) || !double.IsFinite(vertex.Y))
                throw new ArgumentException("Polyline endpoint is non-finite.");

        if (nativeCoincidentClosing != null &&
            (!ReferenceEquals(source, nativeCoincidentClosing.Vertices) ||
             maximumLinearScale != nativeCoincidentClosing.MaximumLinearScale ||
             !double.IsNaN(source[^1].Bulge) ||
             !IsBoundedNativeClosingCandidate(closed, source.Count - 1, source.Count,
                 new(source[^1].X, source[^1].Y), new(source[0].X, source[0].Y), maximumLinearScale)))
            throw new ArgumentException("Native coincident closing evidence does not match this exact source read.");

        var points = new List<P2> { new(source[0].X, source[0].Y) };
        int? skipped = null;
        int? nativeSkipped = null;
        var spanCount = closed ? source.Count : source.Count - 1;
        for (var index = 0; index < spanCount; index++)
        {
            var a = source[index]; var b = source[(index + 1) % source.Count];
            // Exact equality, never a tolerance. This last closed span has no
            // extent and its bulge is unused. Keep the preceding curved span
            // ending at this very same point; do not remove any live arc.
            if (IsExactClosingSegment(closed, index, source.Count, new(a.X, a.Y), new(b.X, b.Y)))
            {
                skipped = index;
                continue;
            }
            if (nativeCoincidentClosing != null && index == nativeCoincidentClosing.SegmentIndex)
            {
                nativeSkipped = index;
                continue; // Keep the literal last endpoint emitted by the preceding live span.
            }
            if (!double.IsFinite(a.Bulge))
                throw new ArgumentException($"Polyline segment {index} has a non-finite bulge on a non-skippable span.");
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var chord = Math.Sqrt(dx * dx + dy * dy);
            if (!double.IsFinite(chord)) throw new ArgumentException($"Polyline segment {index} chord is non-finite.");
            var isArc = Math.Abs(a.Bulge) > 1e-12;
            var count = isArc ? ArcTessellationSegmentCount(BulgeRadius(chord, a.Bulge) * maximumLinearScale,
                Math.Abs(BulgeSweepRadians(a.Bulge)), maxSagittaM) : 1;
            if (count > maximumVertices - points.Count)
                throw new InvalidOperationException("Polyline tessellation exceeds the bounded reader limit.");
            for (var sample = 1; sample <= count; sample++)
                points.Add(sample == count ? new P2(b.X, b.Y) : PointOnBulge(a, b, (double)sample / count));
        }
        return new Result(points.AsReadOnly(), skipped) { SkippedNativeCoincidentClosingSegmentIndex = nativeSkipped };
    }

    /// <summary>
    /// The native adapter supplies the basis of XREF * PlaneToWorld(normal).
    /// Keeping elevation as the OCS Z coordinate preserves tilted planes and
    /// nonuniform/mirrored/sheared insert transforms without planar flattening.
    /// </summary>
    public static IReadOnlyList<V3> ToWcs(Result sampled, double elevation, Frame frame)
    {
        ArgumentNullException.ThrowIfNull(sampled); ArgumentNullException.ThrowIfNull(frame);
        if (!double.IsFinite(elevation) || !Finite(frame.Origin) || !Finite(frame.XAxis) ||
            !Finite(frame.YAxis) || !Finite(frame.ZAxis))
            throw new ArgumentException("Polyline OCS/elevation/transform is non-finite.");
        var result = new List<V3>(sampled.Points.Count);
        foreach (var p in sampled.Points)
        {
            var value = new V3(
                frame.Origin.X + p.X * frame.XAxis.X + p.Y * frame.YAxis.X + elevation * frame.ZAxis.X,
                frame.Origin.Y + p.X * frame.XAxis.Y + p.Y * frame.YAxis.Y + elevation * frame.ZAxis.Y,
                frame.Origin.Z + p.X * frame.XAxis.Z + p.Y * frame.YAxis.Z + elevation * frame.ZAxis.Z);
            if (!Finite(value)) throw new ArgumentException("Transformed polyline sample is non-finite.");
            result.Add(value);
        }
        if (sampled.SkippedNativeCoincidentClosingSegmentIndex != null)
        {
            // Recheck the actual transformed doubles too: a conservative linear
            // norm does not account for coordinate rounding after translation.
            if (result.Count < 2) throw new ArgumentException("Native coincident closing output is incomplete.");
            var first = result[0]; var last = result[^1];
            var dx = first.X - last.X; var dy = first.Y - last.Y; var dz = first.Z - last.Z;
            var gap = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (!double.IsFinite(gap) || gap > 1e-9)
                throw new ArgumentException("Native coincident closing output exceeds the 1e-9m world gap bound.");
        }
        return result;
    }

    private static bool Finite(V3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static bool IsBoundedNativeClosingCandidate(bool closed, int index, int count,
        P2 from, P2 to, double maximumLinearScale)
    {
        if (!closed || count < 2 || index < 0 || index != count - 1 ||
            !double.IsFinite(from.X) || !double.IsFinite(from.Y) ||
            !double.IsFinite(to.X) || !double.IsFinite(to.Y) ||
            !double.IsFinite(maximumLinearScale) || maximumLinearScale <= 0 ||
            (from.X == to.X && from.Y == to.Y)) return false;
        var dx = Math.Abs(from.X - to.X); var dy = Math.Abs(from.Y - to.Y);
        if (!WithinFourUlps(from.X, to.X, dx) || !WithinFourUlps(from.Y, to.Y, dy)) return false;
        // The adapter supplies the conservative full-XREF operator-norm bound in
        // the existing metre geometry pipeline. Never waive a world-space gap.
        var worldX = dx * maximumLinearScale; var worldY = dy * maximumLinearScale;
        return double.IsFinite(worldX) && double.IsFinite(worldY) &&
               Math.Sqrt(worldX * worldX + worldY * worldY) <= 1e-9;
    }

    private static bool WithinFourUlps(double a, double b, double delta)
    {
        static double Ulp(double value) => Math.Max(Math.Abs(Math.BitIncrement(value) - value),
            Math.Abs(value - Math.BitDecrement(value)));
        var bound = 4 * Math.Max(Ulp(a), Ulp(b));
        return double.IsFinite(delta) && double.IsFinite(bound) && delta <= bound;
    }

    private static bool IsExactClosingSegment(bool closed, int index, int count, P2 from, P2 to) =>
        closed && index == count - 1 && double.IsFinite(from.X) && double.IsFinite(from.Y) &&
        double.IsFinite(to.X) && double.IsFinite(to.Y) && from.X == to.X && from.Y == to.Y;
}
