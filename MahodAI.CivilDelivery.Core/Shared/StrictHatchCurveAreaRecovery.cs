using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Bounded, Autodesk-free area interpretation of one complete line/circular-arc
/// loop. Never tessellates, repairs, reorders or changes the supplied geometry.
/// Unseparated contacts and numerical intervals are refusals, not zero areas.
/// This is not a native Hatch.Area or an engineering approval.
/// </summary>
public static class StrictHatchCurveAreaRecovery
{
    public const string Contract = "single-simple-line-arc-family-enclosure-4ulp-1nm-v1";
    public const string DisjointContract = "disjoint-simple-line-arc-family-enclosure-4ulp-1nm-v1";
    public const string ExplicitFrameContract = "single-simple-line-arc-explicit-frame-enclosure-4ulp-1nm-v1";
    public const string ExplicitFrameDisjointContract = "disjoint-simple-line-arc-explicit-frame-enclosure-4ulp-1nm-v1";
    public const int MaximumEdges = 512;
    public const int MaximumLoops = 32;
    public const double MaximumJoinGapHostMetres = 1e-9;

    public readonly record struct Point(double X, double Y);
    public abstract record Edge(Point Start, Point End);
    public sealed record LineEdge(Point Start, Point End) : Edge(Start, End);
    public enum FrameOrigin { NativeReferenceVector = 1, OriginalDwgOcsAngles = 2 }
    /// <summary>Explicit original source evidence, never a frame inferred from
    /// rounded endpoints. Evidence authenticity/source identity remains the
    /// caller's responsibility. The scalar vector is retained without rewriting.</summary>
    public sealed record OriginalArcFrame(Point Vector, FrameOrigin Origin, string EvidenceIdentity);
    public sealed record ArcEdge(Point Start, Point End, Point Center, double Radius,
        double StartAngle, double EndAngle, bool Clockwise) : Edge(Start, End)
    {
        public OriginalArcFrame? OriginalFrame { get; init; }
    }
    public sealed record Input(IReadOnlyList<Edge> Edges, int DeclaredLoopCount,
        bool CaptureComplete, string LoopFlags, string HatchStyle,
        double NormalX, double NormalY, double NormalZ, double Elevation,
        double SourceUnitsToMetres, double MaximumLinearScale);
    public sealed record Proof(double AreaSourceUnitsSquared,
        double AbsoluteErrorBoundSourceUnitsSquared, int EdgeCount, int ArcCount,
        int RecognizedJoinCount, string BoundaryContract)
    {
        public int LoopCount { get; init; } = 1;
        public int ExplicitFrameArcCount { get; init; }
    }

    /// <summary>
    /// Separate complete-hatch contract for strictly AABB-disjoint components.
    /// It never infers holes, discards failed loops or treats overlapping boxes as
    /// a union. Original DeclaredLoopCount must be present on EVERY input. Only a
    /// private diagnostic copy is reduced to one for the unchanged single reader.
    /// </summary>
    public static bool TryRecoverDisjointLoops(IReadOnlyList<Input> loops, out Proof? proof, out string refusal)
    {
        proof = null; refusal = string.Empty;
        try
        {
            Require(loops != null && loops.Count >= 1 && loops.Count <= MaximumLoops,
                "complete disjoint-loop count must be between one and 32");
            var count = loops.Count;
            var copy = new Input[count];
            var edgeCount = 0;
            for (var i = 0; i < count; i++)
            {
                var item = loops[i];
                Require(item != null && item.Edges != null && item.CaptureComplete && item.DeclaredLoopCount == count,
                    "all original loops/counts and complete captures must be retained");
                Require(item.Edges.Count >= 2 && item.Edges.Count <= MaximumEdges - edgeCount,
                    "total original hatch edge count exceeds the 512-edge budget or has an incomplete loop");
                copy[i] = item with { Edges = item.Edges.ToArray() };
                edgeCount += item.Edges.Count;
            }
            if (count == 1) return TryRecover(copy[0], out proof, out refusal);
            var first = copy[0];
            foreach (var item in copy)
                Require(item.HatchStyle == first.HatchStyle && item.NormalX == first.NormalX &&
                    item.NormalY == first.NormalY && item.NormalZ == first.NormalZ && item.Elevation == first.Elevation &&
                    item.SourceUnitsToMetres == first.SourceUnitsToMetres && item.MaximumLinearScale == first.MaximumLinearScale,
                    "one original hatch must have identical plane/style/units/full-transform metadata for every loop");

            var proofs = new List<Proof>();
            var bounds = new List<Box>();
            for (var i = 0; i < copy.Length; i++)
            {
                if (!TryRecover(copy[i] with { DeclaredLoopCount = 1 }, out var single, out var reason))
                    throw new Refusal($"original loop {i} is not proved: {reason}");
                proofs.Add(single!);
                var box = DomainBounds(copy[i].Edges[0]);
                foreach (var edge in copy[i].Edges.Skip(1))
                {
                    var edgeBox = DomainBounds(edge);
                    box = new(I.Hull(box.X, edgeBox.X), I.Hull(box.Y, edgeBox.Y));
                }
                for (var j = 0; j < bounds.Count; j++)
                    Require(!box.Overlaps(bounds[j]),
                        $"original loops {j}/{i} are not strictly AABB-disjoint; overlap/containment/contact is unsupported");
                bounds.Add(box);
            }
            // Every supported fill style fills each strictly separated simple
            // component exactly once. There is no nested loop to interpret.
            var area = I.Exact(0);
            foreach (var single in proofs)
                area += I.Exact(single.AreaSourceUnitsSquared) + new I(
                    -single.AbsoluteErrorBoundSourceUnitsSquared, single.AbsoluteErrorBoundSourceUnitsSquared);
            Require(area.Lo > 0 && double.IsFinite(area.Hi), "summed area is not finite and strictly positive");
            var midpoint = area.Lo + (area.Hi - area.Lo) / 2;
            var error = Math.BitIncrement(Math.Max(midpoint - area.Lo, area.Hi - midpoint));
            var explicitFrames = proofs.Sum(item => item.ExplicitFrameArcCount);
            proof = new(midpoint, error, edgeCount, proofs.Sum(item => item.ArcCount),
                proofs.Sum(item => item.RecognizedJoinCount), explicitFrames > 0 ? ExplicitFrameDisjointContract : DisjointContract)
                { LoopCount = count, ExplicitFrameArcCount = explicitFrames };
            return true;
        }
        catch (Refusal ex) { refusal = ex.Message; return false; }
        catch (ArithmeticException ex) { refusal = "unresolved disjoint-loop numerical interval: " + ex.Message; return false; }
    }

    public static bool TryRecover(Input input, out Proof? proof, out string refusal)
    {
        proof = null;
        refusal = string.Empty;
        try
        {
            var original = ValidateOriginal(input);
            var edges = original.Edges;
            var interpreted = original.Interpreted;
            var arcs = original.Arcs;
            var joins = original.Joins;

            for (var i = 0; i < edges.Length; i++)
                for (var j = i + 1; j < edges.Length; j++)
                {
                    var shared = new List<Box>();
                    if (j == i + 1) shared.Add(JoinBox(edges[i].End, edges[j].Start));
                    if (i == 0 && j == edges.Length - 1) shared.Add(JoinBox(edges[j].End, edges[i].Start));
                    CheckPair(edges[i], edges[j], shared, i, j);
                }

            // Translation to an actual input endpoint avoids large WCS cancellation.
            // Green's arc term uses the original directed sweep, not a polygon or
            // atan-derived replacement. Endpoint rounding and permitted joins are
            // included in the outward-rounded area enclosure.
            var originalPoints = edges.SelectMany(edge => new[] { edge.Start, edge.End }).ToArray();
            var middleX = originalPoints.Min(point => point.X) / 2 + originalPoints.Max(point => point.X) / 2;
            var middleY = originalPoints.Min(point => point.Y) / 2 + originalPoints.Max(point => point.Y) / 2;
            // Translation alone changes no source scalar/domain. Choose an actual
            // recorded endpoint near the envelope centre to reduce interval
            // cancellation, instead of an arbitrary far-away first endpoint.
            var origin = originalPoints.OrderBy(point =>
                Math.Abs(point.X - middleX) + Math.Abs(point.Y - middleY)).First();
            var sum = I.Exact(0);
            for (var i = 0; i < edges.Length; i++)
            {
                var edge = edges[i];
                var a = interpreted[i].Start - Exact(origin);
                var b = interpreted[i].End - Exact(origin);
                if (edge is LineEdge) sum += Cross(a, b);
                else
                {
                    var arc = (ArcEdge)edge;
                    var radius = I.Exact(arc.Radius);
                    var sweep = Sweep(arc);
                    var (sin, _) = SinCos(sweep);
                    // SAME circle family, algebraically identical Green integral:
                    // C×(B-A)+r²*t = A×B+r²*(t-sin(t)). This avoids multiplying
                    // endpoint uncertainty by a distant circle centre and retains
                    // the original radius/sweep and complete endpoint family.
                    // Taylor enclosures account for sin error; no accuracy or
                    // closure threshold is relaxed to obtain a smaller enclosure.
                    sum += Cross(a, b) + radius * radius * (sweep - sin);
                }
            }
            // Possible sub-ULP join interpretation is not an invented bridge:
            // enclose its maximal Green contribution instead of adding geometry.
            for (var i = 0; i < edges.Length; i++)
            {
                var a = interpreted[i].End - Exact(origin);
                var b = interpreted[(i + 1) % edges.Length].Start - Exact(origin);
                var term = Cross(a, b);
                var magnitude = Math.Max(Math.Abs(term.Lo), Math.Abs(term.Hi));
                sum += new I(-magnitude, magnitude);
            }
            var signed = sum / I.Exact(2);
            Require(!signed.ContainsZero, "area orientation or positivity is numerically unresolved");
            var area = signed.Lo > 0 ? signed : -signed;
            Require(area.Lo > 0 && double.IsFinite(area.Hi), "non-finite or non-positive bounded area");
            var midpoint = area.Lo + (area.Hi - area.Lo) / 2;
            var error = Math.BitIncrement(Math.Max(midpoint - area.Lo, area.Hi - midpoint));
            Require(Positive(midpoint) && double.IsFinite(error) && error < midpoint,
                "area error does not separate a positive measurement");
            var explicitFrames = edges.OfType<ArcEdge>().Count(arc => arc.OriginalFrame != null);
            proof = new(midpoint, error, edges.Length, arcs, joins, explicitFrames > 0 ? ExplicitFrameContract : Contract)
                { ExplicitFrameArcCount = explicitFrames };
            return true;
        }
        catch (Refusal ex) { refusal = ex.Message; return false; }
        catch (ArithmeticException ex) { refusal = "unresolved numerical interval: " + ex.Message; return false; }
    }

    /// <summary>
    /// Preflight facts only: original capture/plane/flags/units, every original
    /// edge and arc frame/family, and every original join. Does NOT test pairwise
    /// topology, simplicity, fill meaning or area. Not a quantity/section proof.
    /// Public only because the native adapter resides in a separate assembly.
    /// </summary>
    public sealed record OriginalValidation(int EdgeCount, int ArcCount,
        int ExplicitFrameArcCount, int RecognizedJoinCount)
    {
        public string ValidationContract => "original-line-arc-frame-and-joins-only-v1";
        public bool TopologyOrAreaProven => false;
    }

    public static bool TryValidateOriginalGeometry(Input input,
        out OriginalValidation? validation, out string refusal)
    {
        validation = null; refusal = string.Empty;
        try
        {
            var original = ValidateOriginal(input);
            validation = new(original.Edges.Length, original.Arcs,
                original.Edges.OfType<ArcEdge>().Count(arc => arc.OriginalFrame != null), original.Joins);
            return true;
        }
        catch (Refusal ex) { refusal = ex.Message; return false; }
        catch (ArithmeticException ex) { refusal = "unresolved numerical interval: " + ex.Message; return false; }
    }

    private sealed record ValidatedOriginal(Edge[] Edges,
        (Box Start, Box End)[] Interpreted, int Arcs, int Joins);

    // Extracted without changing expressions, order, thresholds or messages.
    // Both full recovery and the narrow retrace preflight call this SAME block.
    private static ValidatedOriginal ValidateOriginal(Input input)
    {
        Require(input != null && input.Edges != null, "missing boundary input");
        Require(input!.CaptureComplete && input.DeclaredLoopCount == 1,
            "exactly one complete original loop is required; holes and partial captures are unsupported");
        Require(input.Edges.Count >= 2 && input.Edges.Count <= MaximumEdges,
            "boundary edge count is outside the bounded reader contract");
        var allowedFlags = new[] { "Default", "External", "Derived", "Outermost" };
        Require(!string.IsNullOrWhiteSpace(input.LoopFlags) && input.LoopFlags.Split(',')
            .All(flag => allowedFlags.Contains(flag.Trim(), StringComparer.Ordinal)),
            "loop flags do not prove a supported boundary");
        Require(input.HatchStyle is "Normal" or "Outer" or "Ignore",
            "unknown hatch fill style");
        Require(input.NormalX == 0 && input.NormalY == 0 && Math.Abs(input.NormalZ) == 1 &&
            double.IsFinite(input.Elevation), "finite exactly horizontal hatch plane is required");
        Require(Positive(input.SourceUnitsToMetres) && Positive(input.MaximumLinearScale),
            "source units and full transform maximum linear scale are required");
        var hostScale = input.SourceUnitsToMetres * input.MaximumLinearScale;
        Require(Positive(hostScale), "source-to-host scale is non-finite");
        // Copy the list so a caller cannot swap edge identities mid-proof.
        var edges = input.Edges.ToArray();
        var interpreted = new (Box Start, Box End)[edges.Length];
        var joins = 0;
        var arcs = 0;
        for (var i = 0; i < edges.Length; i++)
        {
            var edge = edges[i];
            Require(edge != null && Finite(edge.Start) && Finite(edge.End), "non-finite edge endpoints");
            Require(edge.Start != edge.End, "zero-length/full-circle edge requires a different contract");
            Require(edge is LineEdge or ArcEdge, "unsupported original curve type");
            if (edge is ArcEdge arc) { interpreted[i] = ValidateArc(arc); arcs++; }
            else interpreted[i] = (Exact(edge.Start), Exact(edge.End));
            var next = edges[(i + 1) % edges.Length];
            Require(next != null && Finite(next.Start), "missing following edge");
            Require(RecognizeJoin(edge.End, next!.Start, hostScale),
                $"directed join {i} is not within four coordinate ULPs and one host nanometre");
            if (edge.End != next.Start) joins++;
        }
        Require(arcs > 0, "straight-only input belongs to the existing linear recovery contract");
        for (var i = 0; i < edges.Length; i++)
        {
            var next = (i + 1) % edges.Length;
            var delta = interpreted[i].End - interpreted[next].Start;
            Require((Dot(delta, delta).Sqrt * I.Exact(hostScale)).Hi <= MaximumJoinGapHostMetres,
                $"all compatible circular endpoint join {i} possibilities must stay within one host nanometre");
        }
        return new(edges, interpreted, arcs, joins);
    }

    private static (Box Start, Box End) ValidateArc(ArcEdge arc)
    {
        Require(Finite(arc.Center) && Positive(arc.Radius) && double.IsFinite(arc.StartAngle) &&
            double.IsFinite(arc.EndAngle) && arc.EndAngle > arc.StartAngle, "invalid original circular-arc parameters");
        var sweep = Sweep(arc);
        Require(sweep.Abs.Hi < 2 * Math.PI && sweep.Abs.Lo > 0,
            "zero, full-circle or excessive circular sweep is unsupported");
        if (arc.OriginalFrame != null) return ValidateExplicitFrame(arc);
        var center = Exact(arc.Center);
        var radial = Exact(arc.Start) - center;
        var length = Dot(radial, radial).Sqrt;
        var start = radial / length * I.Exact(arc.Radius);
        // Common rotation follows the recorded start radial direction. No missing
        // ReferenceVector is invented. Taylor remainder explicitly encloses sin/cos.
        var (sin, cos) = SinCos(sweep);
        var predicted = new Box(start.X * cos - start.Y * sin, start.X * sin + start.Y * cos);
        var actualStart = center + start;
        var actualEnd = center + predicted;
        // Existence certificate only: one SAME radius-consistent candidate arc
        // fits both original endpoint boxes. It is NOT claimed to be the unknown
        // native reference rotation, and is not used alone for area/topology.
        Require(Uncertain(arc.Start).Contains(actualStart) && Uncertain(arc.End).Contains(actualEnd),
            "same-arc radius/sweep endpoints are not proved inside original four-ULP bounds");

        // Enclose EVERY compatible reference rotation: every actual point on the
        // recorded radius which lies in StartBox is unchanged by this radial
        // normalization. Therefore rotating the whole normalized StartBox is a
        // superset of every compatible endpoint, including correlations lost by
        // interval arithmetic. Intersection with each original endpoint box can
        // only remove incompatible possibilities. The candidate above proves the
        // family nonempty. Area uses these family enclosures; topology deliberately
        // uses the original (larger) endpoint boxes, never just the candidate.
        var startBox = Uncertain(arc.Start);
        var familyRadial = startBox - center;
        familyRadial = familyRadial / Dot(familyRadial, familyRadial).Sqrt * I.Exact(arc.Radius);
        var familyStart = center + familyRadial;
        var familyEnd = center + new Box(familyRadial.X * cos - familyRadial.Y * sin,
            familyRadial.X * sin + familyRadial.Y * cos);
        return (familyStart.Intersection(startBox), familyEnd.Intersection(Uncertain(arc.End)));
    }

    private static (Box Start, Box End) ValidateExplicitFrame(ArcEdge arc)
    {
        var frame = arc.OriginalFrame!;
        Require(frame.Origin is FrameOrigin.NativeReferenceVector or FrameOrigin.OriginalDwgOcsAngles &&
            !string.IsNullOrWhiteSpace(frame.EvidenceIdentity), "explicit arc frame must identify its original evidence/convention");
        Require(Finite(frame.Vector) && frame.Vector != new Point(0, 0), "explicit original reference direction is non-finite/zero");
        if (frame.Origin == FrameOrigin.OriginalDwgOcsAngles)
            Require(frame.Vector == new Point(1, 0), "original DWG OCS angles require their declared positive-X basis");
        Require((Math.Abs(frame.Vector.X) == 1 && frame.Vector.Y == 0) ||
            (Math.Abs(frame.Vector.Y) == 1 && frame.Vector.X == 0),
            "explicit original frame supports only exact unit axes; arbitrary directions are unsupported");
        var direction = Exact(frame.Vector); // no normalization of any source scalar
        Box At(double angle)
        {
            var (sin, cos) = SinCos(I.Exact(arc.Clockwise ? -angle : angle));
            var rotated = new Box(direction.X * cos - direction.Y * sin,
                direction.X * sin + direction.Y * cos);
            return Exact(arc.Center) + rotated * I.Exact(arc.Radius);
        }
        var start = At(arc.StartAngle); var end = At(arc.EndAngle);
        // No endpoint is overwritten and no inferred frame is substituted.
        // The SAME explicit radius/frame/angles must fit both recorded boxes.
        // Existing topology continues using the wider original endpoint boxes.
        Require(Uncertain(arc.Start).Contains(start) && Uncertain(arc.End).Contains(end),
            "explicit original frame endpoints are not proved inside original four-ULP bounds");
        return (start, end);
    }

    private static (I Sin, I Cos) SinCosReduced(I angle)
    {
        if (angle.Lo == 0 && angle.Hi == 0) return (I.Exact(0), I.Exact(1));
        // The chosen integer quadrant need not itself be an exact predicate:
        // subtraction encloses k*pi/2, so every resulting rotation is enclosed.
        // Math.PI is below true pi; BitIncrement(Math.PI) is above it. No angle
        // scalar is reduced/rewritten in the input, and no native vector inferred.
        var midpoint = angle.Lo / 2 + angle.Hi / 2;
        var quadrantDouble = Math.Round(midpoint / (Math.PI / 2));
        Require(Math.Abs(quadrantDouble) <= 4096, "original angle exceeds bounded quadrant arithmetic");
        var quadrant = (int)quadrantDouble;
        var halfPi = new I(Math.PI, Math.BitIncrement(Math.PI)) / I.Exact(2);
        var reduced = quadrant == 0 ? angle : angle - I.Exact(quadrant) * halfPi;
        Require(reduced.Abs.Hi <= Math.PI / 2, "original-angle reduction interval unresolved");
        var (sin, cos) = SinCosTaylor(reduced);
        return ((quadrant % 4 + 4) % 4) switch
        {
            0 => (sin, cos), 1 => (cos, -sin), 2 => (-sin, -cos), _ => (-cos, sin)
        };
    }

    private static I Sweep(ArcEdge arc)
    {
        var value = I.Exact(arc.EndAngle) - I.Exact(arc.StartAngle);
        return arc.Clockwise ? -value : value;
    }

    private static void CheckPair(Edge a, Edge b, IReadOnlyList<Box> shared, int ai, int bi)
    {
        string tag = $"edges {ai}/{bi}: ";
        // A gapped/rounded join is not a common geometric origin for a half-plane
        // shortcut. In particular near-tangent minor arcs can cross micrometres
        // away despite sub-ULP endpoint differences. Check actual roots below.
        // Endpoint uncertainty is checked independently from root rounding. A
        // nominal root just outside a domain is not a certificate of separation.
        foreach (var p in new[] { a.Start, a.End })
            CheckEndpointContact(p, b, shared, tag);
        foreach (var p in new[] { b.Start, b.End })
            CheckEndpointContact(p, a, shared, tag);

        try { CheckPairDouble(a, b, shared, tag); }
        catch (Refusal) { CheckPairExact(a, b, shared, tag); }
        catch (ArithmeticException) { CheckPairExact(a, b, shared, tag); }
    }

    private static void CheckPairDouble(Edge a, Edge b, IReadOnlyList<Box> shared, string tag)
    {

        if (a is LineEdge la && b is LineEdge lb)
        {
            var p = Exact(la.Start); var u = Exact(la.End) - p;
            var q = Exact(lb.Start); var v = Exact(lb.End) - q;
            var determinant = Cross(u, v);
            if (determinant.ContainsZero)
            {
                // Overlapping axis-aligned boxes do not imply collinearity.
                // Separation must hold for all four-ULP endpoint boxes; shared
                // join tolerance is never a reason to accept a retrace.
                Require(!LineBounds(la).Overlaps(LineBounds(lb)) || LineDomainsStrictlySeparated(la, lb),
                    tag + "parallel/collinear contact or overlap is unresolved");
                return;
            }
            var t = Cross(q - p, v) / determinant;
            var s = Cross(q - p, u) / determinant;
            if (OutsideUnit(t) || OutsideUnit(s)) return;
            CheckRoot(p + u * t, a, b, shared, tag);
            return;
        }
        if (a is ArcEdge && b is LineEdge) { CheckPairDouble(b, a, shared, tag); return; }
        if (a is LineEdge line && b is ArcEdge circle)
        {
            var p = Exact(line.Start); var u = Exact(line.End) - p;
            var w = p - Exact(circle.Center);
            var aa = Dot(u, u); var bb = I.Exact(2) * Dot(w, u);
            var cc = Dot(w, w) - I.Exact(circle.Radius) * I.Exact(circle.Radius);
            var disc = bb * bb - I.Exact(4) * aa * cc;
            if (disc.Hi < 0) return;
            Require(disc.Lo > 0, tag + "line/arc tangency or double root is unresolved");
            var root = disc.Sqrt;
            foreach (var sign in new[] { -1, 1 })
            {
                var t = (-bb + I.Exact(sign) * root) / (I.Exact(2) * aa);
                if (OutsideUnit(t)) continue;
                CheckRoot(p + u * t, a, b, shared, tag);
            }
            return;
        }
        var ac = (ArcEdge)a; var bc = (ArcEdge)b;
        var delta = Exact(bc.Center) - Exact(ac.Center);
        var distance2 = Dot(delta, delta);
        var ra = I.Exact(ac.Radius); var rb = I.Exact(bc.Radius);
        var radiusSum = ra + rb; var radiusDiff = ra - rb;
        if (distance2.Lo > (radiusSum * radiusSum).Hi || distance2.Hi < (radiusDiff * radiusDiff).Lo) return;
        Require(distance2.Lo > 0, tag + "concentric/coincident circular arcs are unsupported");
        var distance = distance2.Sqrt;
        var along = (ra * ra - rb * rb + distance2) / (I.Exact(2) * distance);
        var height2 = ra * ra - along * along;
        if (height2.Hi < 0) return;
        Require(height2.Lo > 0, tag + "arc/arc tangency or overlap is unresolved");
        var direction = delta / distance;
        var foot = Exact(ac.Center) + direction * along;
        var side = new Box(-direction.Y, direction.X) * height2.Sqrt;
        CheckRoot(foot + side, a, b, shared, tag);
        CheckRoot(foot - side, a, b, shared, tag);
    }

    private static void CheckPairExact(Edge a, Edge b, IReadOnlyList<Box> shared, string tag)
    {
        // Only ill-conditioned double predicates reach this bounded fallback.
        // Coefficients are exact rationals of the ORIGINAL binary doubles; no
        // decimal-roundtrip loss, point movement or tolerance is introduced.
        // Square roots are rational enclosures <2^-160 source units wide.
        if (a is ArcEdge && b is LineEdge) { CheckPairExact(b, a, shared, tag); return; }
        if (a is LineEdge la && b is LineEdge lb)
        {
            var p = RV.From(la.Start); var u = RV.From(la.End) - p;
            var q = RV.From(lb.Start); var v = RV.From(lb.End) - q;
            var det = RV.Cross(u, v);
            if (det.Sign == 0)
            {
                Require(!LineBounds(la).Overlaps(LineBounds(lb)) || LineDomainsStrictlySeparated(la, lb),
                    tag + "parallel/collinear contact or overlap is unresolved");
                return;
            }
            var t = RV.Cross(q - p, v) / det;
            var s = RV.Cross(q - p, u) / det;
            if (t < R.Zero || t > R.One || s < R.Zero || s > R.One) return;
            CheckRoot(RVI.From(p + u * t), a, b, shared, tag);
            return;
        }
        if (a is LineEdge line && b is ArcEdge arc)
        {
            var p = RV.From(line.Start); var u = RV.From(line.End) - p;
            var w = p - RV.From(arc.Center);
            var aa = RV.Dot(u, u); var bb = R.Two * RV.Dot(w, u);
            var radius = R.From(arc.Radius);
            var cc = RV.Dot(w, w) - radius * radius;
            var discriminant = bb * bb - R.From(4) * aa * cc;
            if (discriminant.Sign < 0) return;
            var root = RI.Sqrt(discriminant);
            foreach (var sign in discriminant.Sign == 0 ? new[] { 1 } : new[] { -1, 1 })
            {
                var t = (RI.Exact(-bb) + RI.Exact(R.From(sign)) * root) / RI.Exact(R.Two * aa);
                if (t.Hi < R.Zero || t.Lo > R.One) continue;
                CheckRoot(RVI.From(p) + RVI.From(u) * t, a, b, shared, tag);
            }
            return;
        }
        var first = (ArcEdge)a; var second = (ArcEdge)b;
        var center = RV.From(first.Center);
        var d = RV.From(second.Center) - center;
        var d2 = RV.Dot(d, d);
        var r1 = R.From(first.Radius); var r2 = R.From(second.Radius);
        var sum = r1 + r2; var difference = r1 - r2;
        if (d2 > sum * sum || d2 < difference * difference) return;
        Require(d2.Sign > 0, tag + "concentric/coincident circular arcs are unsupported");
        // foot = C1 + d*k; side = perpendicular(d)*sqrt(r1²/d²-k²).
        // This eliminates nested square roots and all unstable subtractions of
        // near-equal double radii from the coefficient calculation.
        var k = (r1 * r1 - r2 * r2 + d2) / (R.Two * d2);
        var heightRatio2 = r1 * r1 / d2 - k * k;
        if (heightRatio2.Sign < 0) return;
        var foot = center + d * k;
        var side = RVI.From(new RV(-d.Y, d.X)) * RI.Sqrt(heightRatio2);
        CheckRoot(RVI.From(foot) + side, a, b, shared, tag);
        if (heightRatio2.Sign != 0) CheckRoot(RVI.From(foot) - side, a, b, shared, tag);
    }

    internal static bool LineDomainsStrictlySeparated(LineEdge a, LineEdge b)
    {
        // Each possible B endpoint must lie on the same STRICT side of every
        // possible supporting line of A. Convexity then separates the complete
        // segments. Interval dependency can only cause a conservative refusal;
        // no endpoint movement, collinearity tolerance or area change is used.
        var start = Uncertain(a.Start);
        var direction = Uncertain(a.End) - start;
        var first = Cross(direction, Uncertain(b.Start) - start);
        var second = Cross(direction, Uncertain(b.End) - start);
        return first.Lo > 0 && second.Lo > 0 || first.Hi < 0 && second.Hi < 0;
    }

    private static void CheckEndpointContact(Point point, Edge other, IReadOnlyList<Box> shared, string tag)
    {
        var box = Uncertain(point);
        if (shared.Any(join => join.Contains(box))) return;
        if (other is LineEdge line)
        {
            var a = Uncertain(line.Start); var u = Uncertain(line.End) - a;
            var w = box - a;
            if (!Cross(u, w).ContainsZero) return;
            var projection = Dot(w, u); var length2 = Dot(u, u);
            if (projection.Hi < 0 || projection.Lo > length2.Hi) return;
            throw new Refusal(tag + "non-shared endpoint touches or overlaps a line within source rounding bounds");
        }
        var arc = (ArcEdge)other;
        var v = box - Exact(arc.Center);
        var radius2 = I.Exact(arc.Radius) * I.Exact(arc.Radius);
        if (!Dot(v, v).Overlaps(radius2)) return;
        if (Membership(box, other) == Location.Outside) return;
        throw new Refusal(tag + "non-shared endpoint touches a circular arc within source rounding bounds");
    }

    private enum Location { Outside, Inside, Uncertain }
    private static void CheckRoot(RVI root, Edge a, Edge b, IReadOnlyList<Box> shared, string tag)
    {
        // Preserve exact separation at a representable bound. Converting first
        // could round an outside rational root onto that bound. Bounds themselves
        // still contain ALL compatible four-ULP endpoint rotations, not literals.
        if (Beyond(root, DomainBounds(a)) || Beyond(root, DomainBounds(b))) return;
        CheckRoot(root.Box, a, b, shared, tag);
    }
    private static bool Beyond(RVI root, Box bound) =>
        root.X.Hi < R.From(bound.X.Lo) || root.X.Lo > R.From(bound.X.Hi) ||
        root.Y.Hi < R.From(bound.Y.Lo) || root.Y.Lo > R.From(bound.Y.Hi);
    private static void CheckRoot(Box root, Edge a, Edge b, IReadOnlyList<Box> shared, string tag)
    {
        var first = Membership(root, a); var second = Membership(root, b);
        if (first == Location.Outside || second == Location.Outside) return;
        // The entire root enclosure must fit an allowed original join box.
        if (shared.Any(join => join.Contains(root))) return;
        throw new Refusal(tag + (first == Location.Inside && second == Location.Inside
            ? "interior curve intersection" : "unseparated curve contact near an endpoint"));
    }

    private static Location Membership(Box point, Edge edge)
    {
        if (!point.Overlaps(DomainBounds(edge))) return Location.Outside;
        return MembershipRaw(point, edge);
    }

    private static Box DomainBounds(Edge edge)
    {
        if (edge is LineEdge line) return LineBounds(line);
        var arc = (ArcEdge)edge;
        var a = Uncertain(arc.Start); var b = Uncertain(arc.End);
        var bounds = new Box(I.Hull(a.X, b.X), I.Hull(a.Y, b.Y));
        var center = Exact(arc.Center); var radius = I.Exact(arc.Radius);
        // Every coordinate extremum of a finite circular arc is an endpoint or
        // cardinal point. Retain a cardinal unless RAW sector membership proves
        // it outside every compatible arc; calling Membership here would recurse.
        var cardinals = new[]
        {
            new Box(center.X + radius, center.Y), new Box(center.X - radius, center.Y),
            new Box(center.X, center.Y + radius), new Box(center.X, center.Y - radius),
        };
        foreach (var cardinal in cardinals)
            if (MembershipRaw(cardinal, arc) != Location.Outside)
                bounds = new(I.Hull(bounds.X, cardinal.X), I.Hull(bounds.Y, cardinal.Y));
        return bounds;
    }

    private static Location MembershipRaw(Box point, Edge edge)
    {
        if (edge is LineEdge line)
        {
            var u = Exact(line.End) - Exact(line.Start);
            var t = Dot(point - Exact(line.Start), u) / Dot(u, u);
            return OutsideUnit(t) ? Location.Outside :
                t.Lo > 0 && t.Hi < 1 ? Location.Inside : Location.Uncertain;
        }
        var arc = (ArcEdge)edge;
        var start = Uncertain(arc.Start) - Exact(arc.Center);
        var end = Uncertain(arc.End) - Exact(arc.Center);
        var v = point - Exact(arc.Center);
        var sign = I.Exact(arc.Clockwise ? -1 : 1);
        var left = sign * Cross(start, v); var right = sign * Cross(v, end);
        var classification = ClassifySweep(arc.StartAngle, arc.EndAngle);
        if (classification < 0)
        {
            if (left.Hi < 0 || right.Hi < 0) return Location.Outside;
            if (left.Lo > 0 && right.Lo > 0) return Location.Inside;
        }
        else if (classification > 0)
        {
            if (left.Hi < 0 && right.Hi < 0) return Location.Outside;
            if (left.Lo > 0 || right.Lo > 0) return Location.Inside;
        }
        else
        {
            // The rounded angle difference cannot choose minor versus major.
            // Only conclusions shared by BOTH possible sectors are admissible.
            if (left.Hi < 0 && right.Hi < 0) return Location.Outside;
            if (left.Lo > 0 && right.Lo > 0) return Location.Inside;
        }
        return Location.Uncertain;
    }

    internal static int ClassifySweep(double startAngle, double endAngle)
    {
        var absolute = (I.Exact(endAngle) - I.Exact(startAngle)).Abs;
        // These adjacent binary64 constants enclose mathematical pi. Math.PI is
        // the lower one; subtraction itself is also outward-enclosed.
        if (absolute.Hi <= Math.PI) return -1;
        if (absolute.Lo > Math.BitIncrement(Math.PI)) return 1;
        return 0;
    }

    private static bool RecognizeJoin(Point a, Point b, double hostScale)
    {
        if (!Finite(a) || !Finite(b) || !NearUlps(a.X, b.X) || !NearUlps(a.Y, b.Y)) return false;
        var d = Exact(a) - Exact(b);
        return (Dot(d, d).Sqrt * I.Exact(hostScale)).Hi <= MaximumJoinGapHostMetres;
    }
    private static bool NearUlps(double a, double b)
    {
        if (a == b) return true;
        return Math.Abs(a - b) <= 4 * Math.Max(Ulp(a), Ulp(b));
    }
    private static Box JoinBox(Point a, Point b)
    {
        var aa = Uncertain(a); var bb = Uncertain(b);
        return new(I.Hull(aa.X, bb.X), I.Hull(aa.Y, bb.Y));
    }
    private static double Ulp(double value) => Math.Max(
        Math.Abs(Math.BitIncrement(value) - value), Math.Abs(value - Math.BitDecrement(value)));
    private static I Coordinate(double value)
    {
        var lo = value; var hi = value;
        for (var i = 0; i < 4; i++) { lo = Math.BitDecrement(lo); hi = Math.BitIncrement(hi); }
        return new(lo, hi);
    }
    private static Box Exact(Point p) => new(I.Exact(p.X), I.Exact(p.Y));
    private static Box Uncertain(Point p) => new(Coordinate(p.X), Coordinate(p.Y));
    private static Box LineBounds(LineEdge line) => new(
        I.Hull(Coordinate(line.Start.X), Coordinate(line.End.X)),
        I.Hull(Coordinate(line.Start.Y), Coordinate(line.End.Y)));
    private static I Cross(Box a, Box b) => a.X * b.Y - a.Y * b.X;
    private static I Dot(Box a, Box b) => a.X * b.X + a.Y * b.Y;
    private static bool OutsideUnit(I value) => value.Hi < 0 || value.Lo > 1;
    private static bool Positive(double x) => double.IsFinite(x) && x > 0;
    private static bool Finite(Point p) => double.IsFinite(p.X) && double.IsFinite(p.Y);
    private static void Require([DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw new Refusal(message); }
    private sealed class Refusal(string message) : System.Exception(message);

    private static (I Sin, I Cos) SinCos(I x) => SinCosReduced(x);

    // Taylor polynomials at zero after outward quadrant reduction. All additions/products/divisions
    // round outward and the Lagrange remainder uses |sin|,|cos|<=1. No assertion
    // about platform Math.Sin/Math.Cos accuracy is needed. Degrees 49/48 are bounded.
    private static (I Sin, I Cos) SinCosTaylor(I x)
    {
        var square = x * x;
        var sin = x; var st = x;
        var cos = I.Exact(1); var ct = I.Exact(1);
        for (var k = 1; k <= 24; k++)
        {
            st = -st * square / I.Exact((2 * k) * (2 * k + 1)); sin += st;
            ct = -ct * square / I.Exact((2 * k - 1) * (2 * k)); cos += ct;
        }
        var sr = Remainder(x.Abs.Hi, 50); var cr = Remainder(x.Abs.Hi, 49);
        return (sin + new I(-sr, sr), cos + new I(-cr, cr));
    }
    private static double Remainder(double x, int degree)
    {
        var value = I.Exact(1);
        for (var i = 1; i <= degree; i++) value = value * I.Exact(x) / I.Exact(i);
        return value.Hi;
    }

    private readonly record struct RV(R X, R Y)
    {
        public static RV From(Point p) => new(R.From(p.X), R.From(p.Y));
        public Box Box => new(X.Enclosure, Y.Enclosure);
        public static RV operator +(RV a, RV b) => new(a.X + b.X, a.Y + b.Y);
        public static RV operator -(RV a, RV b) => new(a.X - b.X, a.Y - b.Y);
        public static RV operator *(RV a, R b) => new(a.X * b, a.Y * b);
        public static R Cross(RV a, RV b) => a.X * b.Y - a.Y * b.X;
        public static R Dot(RV a, RV b) => a.X * b.X + a.Y * b.Y;
    }
    private readonly record struct RVI(RI X, RI Y)
    {
        public static RVI From(RV p) => new(RI.Exact(p.X), RI.Exact(p.Y));
        public Box Box => new(X.Enclosure, Y.Enclosure);
        public static RVI operator +(RVI a, RVI b) => new(a.X + b.X, a.Y + b.Y);
        public static RVI operator -(RVI a, RVI b) => new(a.X - b.X, a.Y - b.Y);
        public static RVI operator *(RVI a, RI b) => new(a.X * b, a.Y * b);
    }
    private readonly record struct RI(R Lo, R Hi)
    {
        public static RI Exact(R value) => new(value, value);
        public I Enclosure => new(Lo.Enclosure.Lo, Hi.Enclosure.Hi);
        public static RI operator +(RI a, RI b) => new(a.Lo + b.Lo, a.Hi + b.Hi);
        public static RI operator -(RI a, RI b) => new(a.Lo - b.Hi, a.Hi - b.Lo);
        public static RI operator *(RI a, RI b)
        {
            var values = new[] { a.Lo * b.Lo, a.Lo * b.Hi, a.Hi * b.Lo, a.Hi * b.Hi };
            return new(values.Min(), values.Max());
        }
        public static RI operator /(RI a, RI b)
        {
            if (b.Lo.Sign <= 0 && b.Hi.Sign >= 0) throw new ArithmeticException("exact division interval contains zero");
            return a * new RI(R.One / b.Hi, R.One / b.Lo);
        }
        public static RI Sqrt(R value)
        {
            if (value.Sign < 0) throw new ArithmeticException("negative exact square root");
            if (value.Sign == 0) return Exact(R.Zero);
            var scale = BigInteger.One << 160;
            var integer = (value.Numerator << 320) / value.Denominator;
            var root = IntegerSqrt(integer);
            var lo = new R(root, scale);
            return lo * lo == value ? Exact(lo) : new(lo, new R(root + 1, scale));
        }
        private static BigInteger IntegerSqrt(BigInteger value)
        {
            if (value.Sign <= 0) return BigInteger.Zero;
            var next = BigInteger.One << checked((int)((value.GetBitLength() + 1) / 2));
            while (true)
            {
                var current = next;
                next = (current + value / current) >> 1;
                if (next >= current) return current;
            }
        }
    }
    private readonly struct R : IComparable<R>, IEquatable<R>
    {
        public BigInteger Numerator { get; }
        public BigInteger Denominator { get; }
        public int Sign => Numerator.Sign;
        public static R Zero => new(0, 1);
        public static R One => new(1, 1);
        public static R Two => new(2, 1);
        public R(BigInteger numerator, BigInteger denominator)
        {
            if (denominator.IsZero) throw new ArithmeticException("zero exact denominator");
            if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
            var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
            Numerator = numerator / gcd; Denominator = denominator / gcd;
        }
        public static R From(double value)
        {
            if (!double.IsFinite(value)) throw new ArithmeticException("non-finite exact scalar");
            var bits = BitConverter.DoubleToInt64Bits(value);
            var exponent = (int)((bits >> 52) & 0x7ff);
            var mantissa = new BigInteger(bits & 0x000fffffffffffffL);
            if (exponent != 0) mantissa += BigInteger.One << 52;
            if (bits < 0) mantissa = -mantissa;
            var shift = exponent == 0 ? -1074 : exponent - 1075;
            return shift >= 0 ? new(mantissa << shift, 1) : new(mantissa, BigInteger.One << -shift);
        }
        public I Enclosure
        {
            get
            {
                if (Sign == 0) return I.Exact(0);
                var n = BigInteger.Abs(Numerator);
                var ns = Math.Max(0, checked((int)n.GetBitLength()) - 60);
                var ds = Math.Max(0, checked((int)Denominator.GetBitLength()) - 60);
                var approximate = Math.ScaleB((double)(n >> ns) / (double)(Denominator >> ds), ns - ds);
                if (Sign < 0) approximate = -approximate;
                if (!double.IsFinite(approximate)) throw new ArithmeticException("exact root is outside finite double range");
                var lower = approximate;
                // The initial 60-bit estimate is within a few ULPs. Compare exact
                // rational values to direct rounding; never trust the estimate.
                for (var i = 0; From(lower) > this; i++)
                {
                    if (i == 16) throw new ArithmeticException("exact lower conversion not separated");
                    lower = Math.BitDecrement(lower);
                }
                var upper = lower;
                for (var i = 0; From(upper) < this; i++)
                {
                    if (i == 16) throw new ArithmeticException("exact upper conversion not separated");
                    upper = Math.BitIncrement(upper);
                }
                return new(lower, upper);
            }
        }
        public int CompareTo(R other) => (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);
        public bool Equals(R other) => Numerator == other.Numerator && Denominator == other.Denominator;
        public override bool Equals(object? obj) => obj is R other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);
        public static R operator +(R a, R b) => new(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator);
        public static R operator -(R a) => new(-a.Numerator, a.Denominator);
        public static R operator -(R a, R b) => a + -b;
        public static R operator *(R a, R b) => new(a.Numerator * b.Numerator, a.Denominator * b.Denominator);
        public static R operator /(R a, R b) => new(a.Numerator * b.Denominator, a.Denominator * b.Numerator);
        public static bool operator <(R a, R b) => a.CompareTo(b) < 0;
        public static bool operator >(R a, R b) => a.CompareTo(b) > 0;
        public static bool operator <=(R a, R b) => a.CompareTo(b) <= 0;
        public static bool operator >=(R a, R b) => a.CompareTo(b) >= 0;
        public static bool operator ==(R a, R b) => a.Equals(b);
        public static bool operator !=(R a, R b) => !a.Equals(b);
    }

    private readonly record struct Box(I X, I Y)
    {
        public bool Contains(Box other) => X.Contains(other.X) && Y.Contains(other.Y);
        public bool Overlaps(Box other) => X.Overlaps(other.X) && Y.Overlaps(other.Y);
        public Box Intersection(Box other) => new(X.Intersection(other.X), Y.Intersection(other.Y));
        public static Box operator +(Box a, Box b) => new(a.X + b.X, a.Y + b.Y);
        public static Box operator -(Box a, Box b) => new(a.X - b.X, a.Y - b.Y);
        public static Box operator *(Box a, I b) => new(a.X * b, a.Y * b);
        public static Box operator /(Box a, I b) => new(a.X / b, a.Y / b);
    }
    private readonly record struct I
    {
        public double Lo { get; }
        public double Hi { get; }
        public I(double lo, double hi)
        {
            if (!double.IsFinite(lo) || !double.IsFinite(hi) || lo > hi)
                throw new ArithmeticException("non-finite/inverted interval");
            Lo = lo; Hi = hi;
        }
        public static I Exact(double value) => new(value, value);
        public static I Hull(I a, I b) => new(Math.Min(a.Lo, b.Lo), Math.Max(a.Hi, b.Hi));
        public bool ContainsZero => Lo <= 0 && Hi >= 0;
        public bool Contains(I other) => Lo <= other.Lo && Hi >= other.Hi;
        public bool Overlaps(I other) => Lo <= other.Hi && other.Lo <= Hi;
        public I Intersection(I other) => new(Math.Max(Lo, other.Lo), Math.Min(Hi, other.Hi));
        public I Abs => Lo >= 0 ? this : Hi <= 0 ? -this : new(0, Math.Max(-Lo, Hi));
        public I Sqrt
        {
            get
            {
                if (Hi < 0) throw new ArithmeticException("negative square root");
                return new(Lo <= 0 ? 0 : Math.BitDecrement(Math.Sqrt(Lo)),
                    Math.BitIncrement(Math.Sqrt(Hi)));
            }
        }
        public static I operator +(I a, I b) => new(Down(a.Lo + b.Lo), Up(a.Hi + b.Hi));
        public static I operator -(I a) => new(-a.Hi, -a.Lo);
        public static I operator -(I a, I b) => a + -b;
        public static I operator *(I a, I b)
        {
            var values = new[] { a.Lo * b.Lo, a.Lo * b.Hi, a.Hi * b.Lo, a.Hi * b.Hi };
            return new(Down(values.Min()), Up(values.Max()));
        }
        public static I operator /(I a, I b)
        {
            if (b.ContainsZero) throw new ArithmeticException("division interval contains zero");
            return a * new I(Down(1 / b.Hi), Up(1 / b.Lo));
        }
        private static double Down(double x) => Math.BitDecrement(x);
        private static double Up(double x) => Math.BitIncrement(x);
    }
}

