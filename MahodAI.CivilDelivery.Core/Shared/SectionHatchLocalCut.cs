using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Local cut coverage for ONE closed, single-loop, Outer, line/circular-arc HATCH
/// whose only global defect is an analytically identified self-intersection
/// (b7 contract CODEX_B7_CUT_CONTRACT_HE, SHA 5368B2AA). It answers one question for
/// one finite WCS segment: Full / Partial / None inside the fill, or Unknown.
/// It never produces a polygon, an area, a quantity or a repaired loop; the source
/// stays globally invalid and its failure stays recorded.
/// Double arithmetic with declared bounds, not an interval certificate. Every
/// vertex, contact, tangent, collinear run, overlap, self-intersection near the
/// segment or ray disagreement is Unknown; no root is dropped by an angle threshold
/// (a near-parallel crossing widens its uncertainty zone instead). Even-odd parity
/// and non-zero winding must agree: that agreement is an explicit local operating
/// interpretation of Outer for a self-intersecting loop, not Autodesk Hatch.Area
/// and not an engineering naming decision.
/// </summary>
public static class SectionHatchLocalCut
{
    public const string Method = "hatch-local-cut-v1";
    /// <summary>Declared bound on the double evaluation error of a local coordinate,
    /// signed distance or root position while every value stays inside <see cref="FrameLimitM"/>.</summary>
    public const double NumericBoundM = 1e-9;
    /// <summary>Bounded local frame for <see cref="NumericBoundM"/>; larger extents are refused.</summary>
    public const double FrameLimitM = 1e5;
    // Numerical-domain gates, not physical uncertainty assigned to binary64 input.
    // Strictly below 2^20 the binary64 ULP is at most 2^-33 < N/8.
    public const double CoordinateUlpBudgetM = NumericBoundM / 8;
    public const double MapRoundingBudgetM = NumericBoundM / 8;
    public const double FrameRoundingBudgetM = NumericBoundM / 4;
    /// <summary>Existing endpoint recognition contract: joins and arc endpoints are uncertain within it.</summary>
    public const double RecognitionToleranceM = SectionHatchBoundaryGeometry.MicrometricEndpointToleranceM;
    /// <summary>Existing span coverage tolerance: a span endpoint may meet the boundary within it,
    /// and a non-crossing boundary within it of the segment is a contact.</summary>
    public const double CoverageToleranceM = 1e-8;
    /// <summary>A span endpoint may meet the boundary: the endpoint itself within
    /// <see cref="CoverageToleranceM"/> plus the contact band of a crossing there.</summary>
    public const double EndSlackM = 2 * CoverageToleranceM;
    public const int MaximumEdges = 512;
    // Fixed ray directions in the segment frame, none along the segment itself.
    private static readonly double[] RayAngles = { 0.7, 1.9, 2.9, 4.1, 5.3 };
    private const int MinimumAgreeingRays = 3;
    private const double Tau = 2 * Math.PI;
    private const double N = NumericBoundM;
    private const double R = RecognitionToleranceM;
    private const double C = CoverageToleranceM;

    public enum EdgeKind { Line = 1, Arc = 2 }

    /// <summary>One native boundary edge as read, in the hatch OCS. Arc fields are the
    /// native CircularArc2d scalars; the start/end points are its native getters.</summary>
    public sealed record RawEdge(EdgeKind Kind, double StartX, double StartY, double EndX, double EndY)
    {
        public double CenterX { get; init; } = double.NaN;
        public double CenterY { get; init; } = double.NaN;
        public double Radius { get; init; } = double.NaN;
        public double StartAngle { get; init; } = double.NaN;
        public double EndAngle { get; init; } = double.NaN;
        public bool Clockwise { get; init; }
        public double ReferenceX { get; init; } = double.NaN;
        public double ReferenceY { get; init; } = double.NaN;
    }

    /// <summary>Complete native inventory of the hatch's only loop plus the full
    /// OCS→host chain. <paramref name="TransformRowMajor"/> is the 4x4 block/XREF matrix.</summary>
    public sealed record RawLoop(
        IReadOnlyList<RawEdge> Edges, int NativeEdgeCount, int DeclaredLoopCount, int LoopIndex,
        bool CaptureComplete, bool IsPolyline, string LoopFlags, string HatchStyle,
        double NormalX, double NormalY, double NormalZ, double Elevation,
        IReadOnlyList<double> TransformRowMajor, string SourceIdentity);

    public abstract record Edge(P2 Start, P2 End);
    public sealed record LineEdge(P2 Start, P2 End) : Edge(Start, End);
    /// <summary>Host WCS arc: travel from Start by <paramref name="Sweep"/> radians,
    /// clockwise or counter-clockwise, on the circle (Center, Radius).</summary>
    public sealed record ArcEdge(P2 Start, P2 End, P2 Center, double Radius, bool Clockwise, double Sweep)
        : Edge(Start, End);
    public sealed record SelfRoot(int EdgeA, int EdgeB, P2 Point, double UncertaintyM);

    /// <summary>An eligible source. Only <see cref="TryCreate"/> can build one.</summary>
    public sealed class Source
    {
        internal Source(IReadOnlyList<Edge> edges, IReadOnlyList<SelfRoot> roots,
            string identity, int loopIndex, string canonicalSha256, double[] bounds)
        {
            Edges = edges; SelfIntersections = roots; SourceIdentity = identity;
            LoopIndex = loopIndex; CanonicalSha256 = canonicalSha256; Bounds = bounds;
        }
        public IReadOnlyList<Edge> Edges { get; }
        public IReadOnlyList<SelfRoot> SelfIntersections { get; }
        public string SourceIdentity { get; }
        public int LoopIndex { get; }
        /// <summary>Binds every raw scalar, flag, style, plane and the full transform.</summary>
        public string CanonicalSha256 { get; }
        /// <summary>Conservative host WCS envelope [minX, minY, maxX, maxY].</summary>
        public double[] Bounds { get; }
    }

    public enum Coverage { Full, Partial, None, Unknown }
    public enum PartKind { Inside, Outside, Zone, Undecided }
    public sealed record Part(double From, double To, PartKind Kind);
    public sealed record Proof(bool Decided, Coverage Coverage, string? Reason,
        IReadOnlyList<Part> Parts, double LengthM, string Evidence);

    // ------------------------------------------------------------------ eligibility

    public static bool TryCreate(RawLoop raw, out Source? source, out string refusal)
    {
        source = null; refusal = string.Empty;
        try
        {
            source = Create(raw);
            return true;
        }
        catch (Refusal error) { refusal = error.Message; return false; }
        catch (ArithmeticException error) { refusal = "numerical failure: " + error.Message; return false; }
    }

    private static Source Create(RawLoop raw)
    {
        Require(raw != null && raw.Edges != null, "missing loop inventory");
        Require(raw!.CaptureComplete && !raw.IsPolyline, "the native edge loop was not captured completely");
        Require(raw.DeclaredLoopCount == 1 && raw.LoopIndex == 0, "exactly one hatch loop is required; holes/multiple loops are unsupported");
        Require(raw.NativeEdgeCount == raw.Edges.Count, "captured edge count differs from the native edge count");
        Require(raw.Edges.Count >= 2 && raw.Edges.Count <= MaximumEdges, "edge count outside the bounded contract");
        Require(raw.HatchStyle == "Outer", "only the Outer fill style is supported");
        var allowedFlags = new[] { "Default", "External", "Derived", "Outermost" };
        Require(!string.IsNullOrWhiteSpace(raw.LoopFlags) && raw.LoopFlags.Split(',')
            .All(flag => allowedFlags.Contains(flag.Trim(), StringComparer.Ordinal)),
            "loop flags do not prove a closed supported boundary");
        Require(raw.NormalX == 0 && raw.NormalY == 0 && raw.NormalZ == 1 && double.IsFinite(raw.Elevation),
            "the hatch plane must be exactly the WCS XY plane");
        Require(!string.IsNullOrWhiteSpace(raw.SourceIdentity), "source identity is missing");
        var m = raw.TransformRowMajor;
        Require(m != null && m.Count == 16 && m.All(double.IsFinite), "the full transform is missing or non-finite");
        Require(m![12] == 0 && m[13] == 0 && m[14] == 0 && m[15] == 1, "the transform is not affine");
        // Exact orientation-preserving similarity in plan; circles stay circles.
        Require(m[0] == m[5] && m[1] == -m[4], "the plan transform is not an exactly proven similarity");
        // Bound actual arithmetic (including large operands whose final sums cancel).
        // Inputs are exact as supplied; only the operations below introduce error.
        foreach (var value in m) NumericDomain(value);
        NumericDomain(raw.Elevation);
        var scaleValue = Root(Add(Mul(Exact(m[0]), Exact(m[0])), Mul(Exact(m[4]), Exact(m[4]))));
        var scale = scaleValue.Value;
        Require(scale > 0 && double.IsFinite(scale), "the plan transform has no finite positive scale");
        var tx = Add(Mul(Exact(m[2]), Exact(raw.Elevation)), Exact(m[3]));
        var ty = Add(Mul(Exact(m[6]), Exact(raw.Elevation)), Exact(m[7]));
        NumericDomain(tx.Value); NumericDomain(ty.Value);
        P2 Map(double x, double y)
        {
            var px = Add(Add(Mul(Exact(m[0]), Exact(x)), Mul(Exact(m[1]), Exact(y))), tx);
            var py = Add(Add(Mul(Exact(m[4]), Exact(x)), Mul(Exact(m[5]), Exact(y))), ty);
            Require(px.Error <= MapRoundingBudgetM && py.Error <= MapRoundingBudgetM,
                "raw-to-WCS rounding exceeds the declared numeric budget");
            NumericDomain(px.Value); NumericDomain(py.Value);
            return new P2(px.Value, py.Value);
        }

        var edges = new List<Edge>();
        for (var i = 0; i < raw.Edges.Count; i++)
        {
            var e = raw.Edges[i];
            Require(e != null && Enum.IsDefined(e.Kind), $"edge {i} is missing or of an unsupported kind");
            Require(double.IsFinite(e!.StartX) && double.IsFinite(e.StartY) && double.IsFinite(e.EndX) && double.IsFinite(e.EndY),
                $"edge {i} has non-finite endpoints");
            var start = Map(e.StartX, e.StartY);
            var end = Map(e.EndX, e.EndY);
            if (e.Kind == EdgeKind.Line)
            {
                Require(Distance(start, end) > R, $"line edge {i} is degenerate");
                edges.Add(new LineEdge(start, end));
                continue;
            }
            Require(double.IsFinite(e.CenterX) && double.IsFinite(e.CenterY) && double.IsFinite(e.Radius) && e.Radius > 0 &&
                double.IsFinite(e.StartAngle) && double.IsFinite(e.EndAngle) &&
                double.IsFinite(e.ReferenceX) && double.IsFinite(e.ReferenceY) && (e.ReferenceX != 0 || e.ReferenceY != 0),
                $"arc edge {i} has invalid native parameters");
            var sweep = e.EndAngle - e.StartAngle;
            Require(sweep > 0 && sweep < Tau, $"arc edge {i} has a zero, reversed or full-circle sweep");
            var radiusValue = Mul(Exact(e.Radius), scaleValue);
            Require(radiusValue.Error <= MapRoundingBudgetM, "radius transform rounding exceeds the declared numeric budget");
            var radius = radiusValue.Value;
            Require(radius <= FrameLimitM && radius * sweep > R, $"arc edge {i} is degenerate or exceeds the bounded frame");
            // The native endpoints must lie where centre/radius/reference/angles put
            // them, within the existing recognition tolerance. Nothing is snapped.
            var reference = Math.Atan2(e.ReferenceY, e.ReferenceX);
            var direction = e.Clockwise ? -1 : 1;
            P2 At(double angle) => new(e.CenterX + e.Radius * Math.Cos(reference + direction * angle),
                e.CenterY + e.Radius * Math.Sin(reference + direction * angle));
            var predictedStart = At(e.StartAngle); var predictedEnd = At(e.EndAngle);
            Require(Distance(predictedStart, new(e.StartX, e.StartY)) * scale <= R &&
                Distance(predictedEnd, new(e.EndX, e.EndY)) * scale <= R,
                $"arc edge {i} endpoints disagree with its centre/radius/angles beyond the recognition tolerance");
            edges.Add(new ArcEdge(start, end, Map(e.CenterX, e.CenterY), radius, e.Clockwise, sweep));
        }
        for (var i = 0; i < edges.Count; i++)
            Require(Distance(edges[i].End, edges[(i + 1) % edges.Count].Start) <= R,
                $"join {i}->{(i + 1) % edges.Count} exceeds the endpoint recognition tolerance; no gap is bridged");

        var bounds = WcsBounds(edges);
        Require(bounds[2] - bounds[0] <= FrameLimitM && bounds[3] - bounds[1] <= FrameLimitM,
            "source extent exceeds the bounded numeric frame");
        var roots = SelfIntersections(edges);
        Require(roots.Count > 0, "no analytically identified self-intersection; the failure is not the supported kind");

        var canonical = new StringBuilder();
        canonical.Append(FormattableString.Invariant(
            $"{Method}|source|{raw.SourceIdentity}|loop={raw.LoopIndex}|style={raw.HatchStyle}|flags={raw.LoopFlags}|polyline={raw.IsPolyline}|declared={raw.DeclaredLoopCount}|native={raw.NativeEdgeCount}|normal={raw.NormalX:R},{raw.NormalY:R},{raw.NormalZ:R}|elevation={raw.Elevation:R}|m="));
        canonical.Append(string.Join(",", m.Select(value => value.ToString("R", CultureInfo.InvariantCulture))));
        foreach (var e in raw.Edges)
            canonical.Append(FormattableString.Invariant(
                $"\n{e.Kind}|{e.StartX:R}|{e.StartY:R}|{e.EndX:R}|{e.EndY:R}|{e.CenterX:R}|{e.CenterY:R}|{e.Radius:R}|{e.StartAngle:R}|{e.EndAngle:R}|{e.Clockwise}|{e.ReferenceX:R}|{e.ReferenceY:R}"));
        return new Source(edges.AsReadOnly(), roots, raw.SourceIdentity, raw.LoopIndex,
            ArtifactHash.Sha256OfText(canonical.ToString()), bounds);
    }

    // ------------------------------------------------------------------ segment proof

    /// <summary>
    /// Proves the fill along exactly the finite segment start→end. Full: every point
    /// farther than <see cref="EndSlackM"/> from both ends is decided inside AND farther
    /// than <see cref="CoverageToleranceM"/> from every boundary edge (running along the
    /// boundary is not coverage). Partial: decided inside and decided outside parts both
    /// exist (conflict evidence only). None: decided outside with no boundary event in
    /// the interior. Otherwise Unknown.
    /// </summary>
    public static Proof Prove(Source source, P2 start, P2 end)
    {
        ArgumentNullException.ThrowIfNull(source);
        try { return ProveCore(source, start, end); }
        catch (Refusal error)
        {
            return Finish(source, start, end, Distance(start, end), new(), new() { error.Message });
        }
    }

    private static Proof ProveCore(Source source, P2 start, P2 end)
    {
        ArgumentNullException.ThrowIfNull(source);
        var parts = new List<Part>();
        var unknown = new List<string>();
        var length = Distance(start, end);
        if (!double.IsFinite(start.X) || !double.IsFinite(start.Y) || !double.IsFinite(end.X) || !double.IsFinite(end.Y) ||
            !double.IsFinite(length) || length <= 2 * EndSlackM || length > FrameLimitM)
            return Finish(source, start, end, length, parts, new() { "degenerate, non-finite or unbounded segment" });
        var frame = new Frame(start, end, length);
        var local = source.Edges.Select(frame.Edge).ToList();
        if (local.Any(edge => !edge.Bounded))
            return Finish(source, start, end, length, parts, new() { "source lies outside the bounded numeric frame of this segment" });

        var zones = new List<(double Lo, double Hi)>();
        var contacts = new List<(double Lo, double Hi)>();
        var withEvent = new bool[local.Count];
        for (var i = 0; i < local.Count; i++)
        {
            foreach (var vertex in new[] { local[i].Start, local[i].End })
                if (DistanceToSegment(vertex, length) <= R + N)
                {
                    unknown.Add($"vertex of edge {i} within the recognition tolerance of the segment");
                    withEvent[i] = true;
                }
            withEvent[i] |= local[i] is LocalLine line
                ? LineEvents(i, line, length, zones, contacts, unknown)
                : ArcEvents(i, (LocalArc)local[i], length, zones, contacts, unknown);
        }
        for (var i = 0; i < local.Count; i++)
            if (!withEvent[i] && DistanceEdgeToSegment(local[i], length) <= C)
                unknown.Add($"edge {i} touches the segment within the coverage tolerance without crossing it");
        foreach (var root in source.SelfIntersections)
            if (DistanceToSegment(frame.Point(root.Point), length) <= R + root.UncertaintyM + N)
                unknown.Add($"self-intersection of edges {root.EdgeA}/{root.EdgeB} lies on the segment");
        if (unknown.Count > 0) return Finish(source, start, end, length, parts, unknown);

        var merged = new List<(double Lo, double Hi)>();
        foreach (var zone in zones.Select(z => (Lo: Math.Max(0, z.Lo), Hi: Math.Min(length, z.Hi)))
                     .Where(z => z.Lo <= z.Hi).OrderBy(z => z.Lo))
        {
            if (merged.Count > 0 && zone.Lo <= merged[^1].Hi)
                merged[^1] = (merged[^1].Lo, Math.Max(merged[^1].Hi, zone.Hi));
            else merged.Add(zone);
        }
        var cursor = 0.0;
        foreach (var zone in merged)
        {
            if (zone.Lo > cursor) parts.Add(Classify(local, cursor, zone.Lo));
            parts.Add(new Part(zone.Lo, zone.Hi, PartKind.Zone));
            cursor = Math.Max(cursor, zone.Hi);
        }
        if (cursor < length) parts.Add(Classify(local, cursor, length));
        if (!parts.Any(part => part.Kind is PartKind.Inside or PartKind.Outside))
            unknown.Add("no decided free interval on the segment");
        if (parts.Any(part => part.Kind == PartKind.Undecided))
            unknown.Add("a free interval is not decided by agreeing parity/winding rays");
        return Finish(source, start, end, length, parts, unknown, contacts);
    }

    private static Proof Finish(Source source, P2 start, P2 end, double length, List<Part> parts, List<string> unknown,
        IReadOnlyList<(double Lo, double Hi)>? contacts = null)
    {
        var decided = unknown.Count == 0 && parts.Count > 0;
        var coverage = Coverage.Unknown;
        if (decided)
        {
            var lo = EndSlackM; var hi = length - EndSlackM;
            var kinds = parts.Where(part => Math.Min(part.To, hi) > Math.Max(part.From, lo))
                .Select(part => part.Kind).Distinct().ToList();
            var touching = (contacts ?? Array.Empty<(double, double)>()).Any(band => Math.Min(band.Hi, hi) > Math.Max(band.Lo, lo));
            if (kinds.Count == 1 && kinds[0] == PartKind.Inside && !touching) coverage = Coverage.Full;
            else if (kinds.Contains(PartKind.Inside) && kinds.Contains(PartKind.Outside)) coverage = Coverage.Partial;
            else if (kinds.Count == 1 && kinds[0] == PartKind.Outside) coverage = Coverage.None;
        }
        var reason = unknown.Count == 0 ? null : string.Join("; ", unknown.Distinct());
        var canonical = FormattableString.Invariant(
            $"{Method}|proof|{source.SourceIdentity}|{source.CanonicalSha256}|segment={start.X:R},{start.Y:R},{end.X:R},{end.Y:R}|decided={decided}|coverage={coverage}|reason={reason}|parts=") +
            string.Join(";", parts.Select(part => FormattableString.Invariant($"{part.Kind}:{part.From:R}:{part.To:R}")));
        return new Proof(decided, coverage, reason, parts.AsReadOnly(), length, ArtifactHash.Sha256OfText(canonical));
    }

    private static bool LineEvents(int index, LocalLine line, double length, List<(double, double)> zones,
        List<(double, double)> contacts, List<string> unknown)
    {
        var (t0, d0) = (line.Start.X, line.Start.Y);
        var (t1, d1) = (line.End.X, line.End.Y);
        if (Math.Abs(d0) <= N && Math.Abs(d1) <= N)
        {
            if (Math.Max(t0, t1) + N < 0 || Math.Min(t0, t1) - N > length) return false;
            unknown.Add($"line edge {index} is collinear with the segment");
            return true;
        }
        if ((d0 > N && d1 > N) || (d0 < -N && d1 < -N)) return false;
        // The sub-edge within N of the segment's line contains the true crossing;
        // a shallow angle widens this zone instead of dismissing the root.
        var zone = NearLineZone(t0, d0, t1, d1);
        if (zone.Hi < 0 || zone.Lo > length) return false;
        if (d0 * d1 < 0 && Math.Abs(d0) > N && Math.Abs(d1) > N)
        {
            zones.Add(zone);
            // Where this crossing edge runs within the coverage tolerance of the cut.
            contacts.Add(NearLineZone(t0, d0, t1, d1, C));
            return true;
        }
        unknown.Add($"line edge {index} meets the segment line at its endpoint");
        return true;
    }

    private static bool ArcEvents(int index, LocalArc arc, double length, List<(double, double)> zones,
        List<(double, double)> contacts, List<string> unknown)
    {
        var (tc, dc, r) = (arc.Center.X, arc.Center.Y, arc.Radius);
        var gap = Math.Abs(dc) - r;
        if (gap > N) return false;
        if (gap >= -N)
        {
            var w = Math.Sqrt(Math.Max(0, r * r - Math.Pow(Math.Max(0, Math.Abs(dc) - N), 2))) + N;
            if (tc + w < 0 || tc - w > length) return false;
            var angle = Math.Atan2(-dc, 0);
            if (arc.Membership(angle, (w + R + N) / r) == Location.Outside) return false;
            unknown.Add($"arc edge {index} is tangent to the segment");
            return true;
        }
        var half = Math.Sqrt((r - Math.Abs(dc)) * (r + Math.Abs(dc)));
        var any = false;
        foreach (var root in new[] { tc - half, tc + half })
        {
            var delta = N * r / half + N;
            if (root + delta < 0 || root - delta > length) continue;
            var location = arc.Membership(Math.Atan2(-dc, root - tc), (delta + R + N) / r);
            if (location == Location.Outside) continue;
            any = true;
            if (location == Location.Ambiguous) { unknown.Add($"arc edge {index} meets the segment at its endpoint"); continue; }
            zones.Add((root - delta, root + delta));
            // The circle's band within the coverage tolerance of the cut around this root.
            var outer = Math.Sqrt(Math.Max(0, r * r - Math.Pow(Math.Max(0, Math.Abs(dc) - C), 2)));
            var inner = Math.Abs(dc) + C < r ? Math.Sqrt(r * r - Math.Pow(Math.Abs(dc) + C, 2)) : 0;
            contacts.Add(inner == 0 ? (tc - outer - N, tc + outer + N)
                : root > tc ? (tc + inner - N, tc + outer + N) : (tc - outer - N, tc - inner + N));
        }
        return any;
    }

    private static Part Classify(IReadOnlyList<LocalEdge> edges, double from, double to)
    {
        var mid = new P2(from / 2 + to / 2, 0);
        if (edges.Any(edge => DistanceEdgeToPoint(edge, mid) <= N)) return new Part(from, to, PartKind.Undecided);
        var votes = new List<(int Parity, int Winding)>();
        foreach (var angle in RayAngles)
            if (CastRay(edges, mid, angle) is { } vote) votes.Add(vote);
        if (votes.Count < MinimumAgreeingRays || votes.Distinct().Count() != 1)
            return new Part(from, to, PartKind.Undecided);
        var (parity, winding) = votes[0];
        if ((parity == 1) != (winding != 0)) return new Part(from, to, PartKind.Undecided);
        return new Part(from, to, parity == 1 ? PartKind.Inside : PartKind.Outside);
    }

    /// <summary>Half-line crossings from <paramref name="origin"/>; null rejects the ray.</summary>
    private static (int Parity, int Winding)? CastRay(IReadOnlyList<LocalEdge> edges, P2 origin, double angle)
    {
        var v = new P2(Math.Cos(angle), Math.Sin(angle));
        (double Along, double Side) Ray(P2 p) =>
            ((p.X - origin.X) * v.X + (p.Y - origin.Y) * v.Y, v.X * (p.Y - origin.Y) - v.Y * (p.X - origin.X));
        var parity = 0; var winding = 0;
        foreach (var edge in edges)
            foreach (var vertex in new[] { edge.Start, edge.End })
            {
                var (a, s) = Ray(vertex);
                if ((a >= 0 ? Math.Abs(s) : Math.Sqrt(a * a + s * s)) <= R + N) return null;
            }
        foreach (var edge in edges)
        {
            if (edge is LocalLine line)
            {
                var (a0, s0) = Ray(line.Start); var (a1, s1) = Ray(line.End);
                if (Math.Abs(s0) <= N && Math.Abs(s1) <= N)
                {
                    if (Math.Max(a0, a1) + N < 0) continue;
                    return null;
                }
                if ((s0 > N && s1 > N) || (s0 < -N && s1 < -N)) continue;
                var zone = NearLineZone(a0, s0, a1, s1);
                if (zone.Hi < 0) continue;
                if (zone.Lo <= 0 || !(s0 * s1 < 0 && Math.Abs(s0) > N && Math.Abs(s1) > N)) return null;
                parity ^= 1;
                winding += s0 < 0 ? 1 : -1;
                continue;
            }
            var arc = (LocalArc)edge;
            var (ca, cs) = Ray(arc.Center);
            var r = arc.Radius;
            var gap = Math.Abs(cs) - r;
            if (gap > N) continue;
            if (gap >= -N)
            {
                var w = Math.Sqrt(Math.Max(0, r * r - Math.Pow(Math.Max(0, Math.Abs(cs) - N), 2))) + N;
                if (ca + w < 0) continue;
                var foot = new P2(origin.X + ca * v.X, origin.Y + ca * v.Y);
                var tangentAngle = Math.Atan2(foot.Y - arc.Center.Y, foot.X - arc.Center.X);
                if (arc.Membership(tangentAngle, (w + R + N) / r) == Location.Outside) continue;
                return null;
            }
            var half = Math.Sqrt((r - Math.Abs(cs)) * (r + Math.Abs(cs)));
            foreach (var along in new[] { ca - half, ca + half })
            {
                var delta = N * r / half + N;
                if (along + delta < 0) continue;
                var point = new P2(origin.X + along * v.X, origin.Y + along * v.Y);
                var phi = Math.Atan2(point.Y - arc.Center.Y, point.X - arc.Center.X);
                var location = arc.Membership(phi, (delta + R + N) / r);
                if (location == Location.Outside) continue;
                if (location == Location.Ambiguous || along - delta <= 0) return null;
                var tangent = arc.Tangent(phi);
                parity ^= 1;
                winding += v.X * tangent.Y - v.Y * tangent.X > 0 ? 1 : -1;
            }
        }
        return (parity, winding);
    }

    /// <summary>Range along a line edge's projection where its signed distance is within
    /// <paramref name="tolerance"/> (default the numeric bound), padded by N.</summary>
    private static (double Lo, double Hi) NearLineZone(double t0, double d0, double t1, double d1, double tolerance = N)
    {
        double lo, hi;
        var delta = d1 - d0;
        if (delta == 0) { lo = 0; hi = 1; }
        else
        {
            var a = (-tolerance - d0) / delta; var b = (tolerance - d0) / delta;
            lo = Math.Max(0, Math.Min(a, b)); hi = Math.Min(1, Math.Max(a, b));
            if (lo > hi) { lo = 0; hi = 1; }
        }
        var x = t0 + lo * (t1 - t0); var y = t0 + hi * (t1 - t0);
        return (Math.Min(x, y) - N, Math.Max(x, y) + N);
    }

    // ------------------------------------------------------------------ self-intersections

    private static List<SelfRoot> SelfIntersections(IReadOnlyList<Edge> edges)
    {
        var origin = edges[0].Start;
        var frame = new Frame(origin, new P2(origin.X + 1, origin.Y), 1);
        var local = edges.Select(frame.Edge).ToList();
        Require(local.All(edge => edge.Bounded), "source extent exceeds the bounded numeric frame");
        var roots = new List<SelfRoot>();
        var n = local.Count;
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                var joints = new List<P2>();
                if (j == i + 1) joints.Add(local[i].End);
                if (i == 0 && j == n - 1) joints.Add(local[j].End);
                foreach (var (point, uncertainty) in PairRoots(i, j, local[i], local[j]))
                {
                    if (joints.Any(joint => Distance(joint, point) <= R + uncertainty + N)) continue;
                    roots.Add(new SelfRoot(i, j, frame.Wcs(point), uncertainty));
                }
            }
        return roots;
    }

    private static IEnumerable<(P2 Point, double Uncertainty)> PairRoots(int i, int j, LocalEdge a, LocalEdge b)
    {
        if (a is LocalArc && b is LocalLine) (a, b) = (b, a);
        if (a is LocalLine la && b is LocalLine lb) return LineLineRoots(i, j, la, lb);
        if (a is LocalLine line && b is LocalArc arc) return LineArcRoots(line, arc);
        return ArcArcRoots(i, j, (LocalArc)a, (LocalArc)b);
    }

    private static IEnumerable<(P2, double)> LineLineRoots(int i, int j, LocalLine a, LocalLine b)
    {
        var frame = new Frame(a.Start, a.End, Distance(a.Start, a.End));
        var (b0, b1) = (frame.Point(b.Start), frame.Point(b.End));
        var length = frame.Length;
        if (Math.Abs(b0.Y) <= R + N && Math.Abs(b1.Y) <= R + N)
        {
            var overlap = Math.Min(length, Math.Max(b0.X, b1.X)) - Math.Max(0, Math.Min(b0.X, b1.X));
            Require(overlap <= R + N, $"edges {i}/{j} overlap collinearly; overlap is unresolved");
            if (overlap < -(R + N)) yield break;
            // Collinear contact at a single vertex.
            yield return (DistanceToSegment(b0, length) <= DistanceToSegment(b1, length) ? b.Start : b.End, R + N);
            yield break;
        }
        foreach (var root in SegmentLineRoots(b0, b1, length))
            yield return (frame.Wcs(root.Point), root.Uncertainty);
        // A's endpoints against B, for contacts where an A vertex touches B's interior.
        var back = new Frame(b.Start, b.End, Distance(b.Start, b.End));
        foreach (var vertex in new[] { a.Start, a.End })
        {
            var p = back.Point(vertex);
            if (Math.Abs(p.Y) <= R + N && p.X >= -(R + N) && p.X <= back.Length + R + N) yield return (vertex, R + N);
        }
    }

    /// <summary>Crossings of edge b0→b1 (in a frame where the other edge is [0,length] on the X axis).</summary>
    private static IEnumerable<(P2 Point, double Uncertainty)> SegmentLineRoots(P2 b0, P2 b1, double length)
    {
        if ((b0.Y > N && b1.Y > N) || (b0.Y < -N && b1.Y < -N))
        {
            // No crossing; a vertex within the recognition tolerance is still a contact.
            foreach (var p in new[] { b0, b1 })
                if (Math.Abs(p.Y) <= R + N && p.X >= -(R + N) && p.X <= length + R + N) yield return (p, R + N);
            yield break;
        }
        var zone = NearLineZone(b0.X, b0.Y, b1.X, b1.Y);
        if (zone.Hi < -(R + N) || zone.Lo > length + R + N) yield break;
        var crossing = b0.Y * b1.Y < 0 && Math.Abs(b0.Y) > N && Math.Abs(b1.Y) > N;
        var x = crossing ? b0.X + (b1.X - b0.X) * b0.Y / (b0.Y - b1.Y) : (Math.Abs(b0.Y) <= Math.Abs(b1.Y) ? b0.X : b1.X);
        yield return (new P2(x, 0), (zone.Hi - zone.Lo) / 2 + N);
    }

    private static IEnumerable<(P2, double)> LineArcRoots(LocalLine line, LocalArc arc)
    {
        var length = Distance(line.Start, line.End);
        var frame = new Frame(line.Start, line.End, length);
        var center = frame.Point(arc.Center);
        var r = arc.Radius;
        var gap = Math.Abs(center.Y) - r;
        if (gap > N)
        {
            // Arc endpoints touching the line interior are contacts.
            foreach (var vertex in new[] { arc.Start, arc.End })
            {
                var p = frame.Point(vertex);
                if (Math.Abs(p.Y) <= R + N && p.X >= -(R + N) && p.X <= length + R + N) yield return (vertex, R + N);
            }
            yield break;
        }
        var candidates = new List<(double X, double Delta)>();
        if (gap >= -N)
        {
            var w = Math.Sqrt(Math.Max(0, r * r - Math.Pow(Math.Max(0, Math.Abs(center.Y) - N), 2))) + N;
            candidates.Add((center.X, w));
        }
        else
        {
            var half = Math.Sqrt((r - Math.Abs(center.Y)) * (r + Math.Abs(center.Y)));
            candidates.Add((center.X - half, N * r / half + N));
            candidates.Add((center.X + half, N * r / half + N));
        }
        foreach (var (x, delta) in candidates)
        {
            if (x + delta < -(R + N) || x - delta > length + R + N) continue;
            var wcsPoint = frame.Wcs(new P2(x, 0));
            var phi = Math.Atan2(wcsPoint.Y - arc.Center.Y, wcsPoint.X - arc.Center.X);
            if (arc.Membership(phi, (delta + R + N) / r) == Location.Outside) continue;
            yield return (wcsPoint, delta + N);
        }
    }

    private static IEnumerable<(P2, double)> ArcArcRoots(int i, int j, LocalArc a, LocalArc b)
    {
        var d = Distance(a.Center, b.Center);
        var (r1, r2) = (a.Radius, b.Radius);
        if (d <= N)
        {
            if (Math.Abs(r1 - r2) > N) yield break;
            Require(!a.AngularOverlap(b, (R + N) / Math.Min(r1, r2)),
                $"edges {i}/{j} are coincident circular arcs with overlapping sweeps; overlap is unresolved");
            // Coincident circles meeting only at endpoints: report each touching endpoint.
            foreach (var vertex in new[] { b.Start, b.End })
                if (a.Membership(Math.Atan2(vertex.Y - a.Center.Y, vertex.X - a.Center.X), (R + N) / r1) != Location.Outside)
                    yield return (vertex, R + N);
            yield break;
        }
        if (d > r1 + r2 + N || d < Math.Abs(r1 - r2) - N) yield break;
        var ux = (b.Center.X - a.Center.X) / d; var uy = (b.Center.Y - a.Center.Y) / d;
        var along = (r1 * r1 - r2 * r2 + d * d) / (2 * d);
        var h2 = r1 * r1 - along * along;
        var tangent = Math.Abs(d - (r1 + r2)) <= N || Math.Abs(d - Math.Abs(r1 - r2)) <= N || h2 <= 0;
        var h = tangent ? 0 : Math.Sqrt(h2);
        var delta = tangent ? Math.Sqrt(2 * Math.Max(r1, r2) * N) + N : N * r1 * r2 / (d * h) + N;
        var foot = new P2(a.Center.X + ux * along, a.Center.Y + uy * along);
        foreach (var sign in tangent ? new[] { 0 } : new[] { -1, 1 })
        {
            var point = new P2(foot.X - sign * uy * h, foot.Y + sign * ux * h);
            var la = a.Membership(Math.Atan2(point.Y - a.Center.Y, point.X - a.Center.X), (delta + R + N) / r1);
            var lb = b.Membership(Math.Atan2(point.Y - b.Center.Y, point.X - b.Center.X), (delta + R + N) / r2);
            if (la == Location.Outside || lb == Location.Outside) continue;
            yield return (point, delta + N);
        }
    }

    // ------------------------------------------------------------------ local geometry

    private enum Location { Inside, Outside, Ambiguous }

    private abstract record LocalEdge(P2 Start, P2 End, bool Bounded);
    private sealed record LocalLine(P2 Start, P2 End, bool Bounded) : LocalEdge(Start, End, Bounded);
    private sealed record LocalArc(P2 Start, P2 End, P2 Center, double Radius, bool Clockwise, double Sweep, bool Bounded)
        : LocalEdge(Start, End, Bounded)
    {
        private double StartAngle => Math.Atan2(Start.Y - Center.Y, Start.X - Center.X);

        /// <summary>Is a point at polar angle phi on this arc? Ambiguous within margin of an end.</summary>
        public Location Membership(double phi, double margin)
        {
            if (!double.IsFinite(margin) || margin >= Math.PI) return Location.Ambiguous;
            var offset = Mod(Clockwise ? StartAngle - phi : phi - StartAngle);
            if (offset <= margin || Tau - offset <= margin || Math.Abs(offset - Sweep) <= margin) return Location.Ambiguous;
            return offset < Sweep ? Location.Inside : Location.Outside;
        }

        public bool AngularOverlap(LocalArc other, double margin)
        {
            // Sample-free: two sweeps on one circle overlap iff an interior point of
            // either lies strictly inside the other (checked at both ends and middles).
            foreach (var (arc, probe) in new[] { (this, other), (other, this) })
                foreach (var fraction in new[] { 0.0, 0.5, 1.0 })
                {
                    var phi = probe.StartAngle + (probe.Clockwise ? -1 : 1) * probe.Sweep * fraction;
                    if (arc.Membership(phi, margin) == Location.Inside) return true;
                }
            return false;
        }

        public P2 Tangent(double phi) => Clockwise ? new(Math.Sin(phi), -Math.Cos(phi)) : new(-Math.Sin(phi), Math.Cos(phi));
    }

    /// <summary>Translation to the segment start and rotation onto +X: (along, signed side).</summary>
    private sealed class Frame
    {
        private readonly P2 origin; private readonly double ux; private readonly double uy;
        private readonly Number uxValue; private readonly Number uyValue;
        public double Length { get; }
        public Frame(P2 start, P2 end, double length)
        {
            origin = start; Length = length;
            var dx = Sub(Exact(end.X), Exact(start.X));
            var dy = Sub(Exact(end.Y), Exact(start.Y));
            var norm = Root(Add(Mul(dx, dx), Mul(dy, dy)));
            // Squared distances are not coordinates: bounded arithmetic below uses
            // a separate finite check on these products, while operands stay gated.
            uxValue = Divide(dx, norm); uyValue = Divide(dy, norm);
            ux = uxValue.Value; uy = uyValue.Value;
        }
        public P2 Point(P2 p)
        {
            var dx = Sub(Exact(p.X), Exact(origin.X));
            var dy = Sub(Exact(p.Y), Exact(origin.Y));
            var x = Add(Mul(dx, uxValue), Mul(dy, uyValue));
            var y = Sub(Mul(uxValue, dy), Mul(uyValue, dx));
            Require(x.Error <= FrameRoundingBudgetM && y.Error <= FrameRoundingBudgetM,
                "frame arithmetic exceeds the declared numeric budget");
            NumericDomain(x.Value); NumericDomain(y.Value);
            return new(x.Value, y.Value);
        }
        public P2 Wcs(P2 p) => new(origin.X + p.X * ux - p.Y * uy, origin.Y + p.X * uy + p.Y * ux);
        public LocalEdge Edge(Edge edge)
        {
            var start = Point(edge.Start); var end = Point(edge.End);
            if (edge is LineEdge) return new LocalLine(start, end, InFrame(start) && InFrame(end));
            var arc = (ArcEdge)edge;
            var center = Point(arc.Center);
            return new LocalArc(start, end, center, arc.Radius, arc.Clockwise, arc.Sweep,
                InFrame(start) && InFrame(end) && InFrame(center) && arc.Radius <= FrameLimitM);
        }
        private static bool InFrame(P2 p) => double.IsFinite(p.X) && double.IsFinite(p.Y) &&
            Math.Abs(p.X) <= FrameLimitM && Math.Abs(p.Y) <= FrameLimitM;
    }

    private static double DistanceToSegment(P2 p, double length) =>
        p.X >= 0 && p.X <= length ? Math.Abs(p.Y)
        : Math.Sqrt(Math.Pow(p.X < 0 ? p.X : p.X - length, 2) + p.Y * p.Y);

    private static double DistancePointToLine(P2 p, P2 a, P2 b)
    {
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        var l2 = dx * dx + dy * dy;
        var t = l2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
        return Distance(p, new P2(a.X + t * dx, a.Y + t * dy));
    }

    private static double DistanceEdgeToPoint(LocalEdge edge, P2 p)
    {
        if (edge is LocalLine line) return DistancePointToLine(p, line.Start, line.End);
        var arc = (LocalArc)edge;
        var phi = Math.Atan2(p.Y - arc.Center.Y, p.X - arc.Center.X);
        var endpoints = Math.Min(Distance(p, arc.Start), Distance(p, arc.End));
        return arc.Membership(phi, 0) == Location.Inside
            ? Math.Min(endpoints, Math.Abs(Distance(p, arc.Center) - arc.Radius)) : endpoints;
    }

    /// <summary>True distance between a non-crossing edge and the segment [0,length] on X.</summary>
    private static double DistanceEdgeToSegment(LocalEdge edge, double length)
    {
        var a = new P2(0, 0); var b = new P2(length, 0);
        var candidates = new List<double>
        {
            DistanceToSegment(edge.Start, length), DistanceToSegment(edge.End, length),
            DistanceEdgeToPoint(edge, a), DistanceEdgeToPoint(edge, b),
        };
        if (edge is LocalArc arc && arc.Center.X >= 0 && arc.Center.X <= length)
            foreach (var phi in new[] { Math.PI / 2, -Math.PI / 2 })
                if (arc.Membership(phi, 0) == Location.Inside)
                    candidates.Add(Math.Abs(arc.Center.Y + Math.Sin(phi) * arc.Radius));
        return candidates.Min();
    }

    private static double[] WcsBounds(IReadOnlyList<Edge> edges)
    {
        var xs = new List<double>(); var ys = new List<double>();
        foreach (var edge in edges)
        {
            xs.Add(edge.Start.X); xs.Add(edge.End.X); ys.Add(edge.Start.Y); ys.Add(edge.End.Y);
            if (edge is not ArcEdge arc) continue;
            var startAngle = Math.Atan2(arc.Start.Y - arc.Center.Y, arc.Start.X - arc.Center.X);
            foreach (var cardinal in new[] { 0, Math.PI / 2, Math.PI, -Math.PI / 2 })
            {
                var offset = Mod(arc.Clockwise ? startAngle - cardinal : cardinal - startAngle);
                if (offset > arc.Sweep) continue;
                xs.Add(arc.Center.X + arc.Radius * Math.Cos(cardinal));
                ys.Add(arc.Center.Y + arc.Radius * Math.Sin(cardinal));
            }
        }
        // Pad by recognition + numeric bounds so no endpoint/cardinal rounding escapes.
        var pad = R + N;
        return new[] { xs.Min() - pad, ys.Min() - pad, xs.Max() + pad, ys.Max() + pad };
    }

    private static double Mod(double angle)
    {
        var value = angle % Tau;
        return value < 0 ? value + Tau : value;
    }

    private static double Distance(P2 a, P2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // Conservative first-order-plus-product error propagation for basic binary64
    // operations. No error is attached to an input merely because it has an ULP.
    // This is a domain guard for Map/Frame, not a certificate for trig/root topology.
    private readonly record struct Number(double Value, double Error);
    private static void NumericDomain(double value)
    {
        Require(double.IsFinite(value) && Ulp(value) <= CoordinateUlpBudgetM,
            "coordinate/operand exceeds the declared numeric precision domain");
    }
    private static double Ulp(double value)
    {
        var a = Math.Abs(value);
        return Math.Max(Math.BitIncrement(a) - a, a - Math.BitDecrement(a));
    }
    private static double Up(double value) => value == 0 ? 0 : Math.BitIncrement(value);
    private static double RoundBound(double value) => Math.Max(double.Epsilon, Up(Ulp(value) / 2));
    private static Number Exact(double value) { NumericDomain(value); return new(value, 0); }
    private static Number Finite(double value, double error)
    {
        Require(double.IsFinite(value) && double.IsFinite(error), "non-finite numeric operation");
        return new(value, Up(error));
    }
    private static Number Add(Number a, Number b)
    {
        var value = a.Value + b.Value;
        var rounding = a.Value == 0 || b.Value == 0 ? 0 : RoundBound(value);
        return Finite(value, Up(a.Error + b.Error) + rounding);
    }
    private static Number Sub(Number a, Number b) => Add(a, new(-b.Value, b.Error));
    private static Number Mul(Number a, Number b)
    {
        var value = a.Value * b.Value;
        var rounding = a.Value is 0 or 1 or -1 || b.Value is 0 or 1 or -1 ? 0 : RoundBound(value);
        var error = Up(Up(Math.Abs(a.Value) * b.Error) + Up(Math.Abs(b.Value) * a.Error));
        return Finite(value, Up(error + Up(a.Error * b.Error)) + rounding);
    }
    private static Number Root(Number a)
    {
        Require(a.Value > a.Error && a.Error >= 0, "uncertain or degenerate numeric norm");
        var value = Math.Sqrt(a.Value);
        var low = Math.BitDecrement(a.Value - a.Error);
        Require(low > 0, "uncertain numeric norm");
        var propagation = a.Error == 0 ? 0 : Up(a.Error / Math.Sqrt(low));
        var rounding = a.Error == 0 && value * value == a.Value && value is 1 ? 0 : RoundBound(value);
        return Finite(value, propagation + rounding);
    }
    private static Number Divide(Number a, Number b)
    {
        var denominator = Math.BitDecrement(Math.Abs(b.Value) - b.Error);
        Require(denominator > 0, "uncertain numeric divisor");
        var value = a.Value / b.Value;
        var rounding = a.Value == 0 || b.Value is 1 or -1 ? 0 : RoundBound(value);
        var propagated = Up(Up(a.Error + Up(Up(Math.Abs(value) + rounding) * b.Error)) / denominator);
        return Finite(value, propagated + rounding);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Refusal(message); }
    private sealed class Refusal(string message) : Exception(message);
}
