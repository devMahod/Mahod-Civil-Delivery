using System;
using System.Collections.Generic;
using System.Linq;
using Key = MahodAI.CivilDelivery.Estimate.CorridorBotSurfaceLogic.RegionKey;
using Schedule = MahodAI.CivilDelivery.Estimate.CorridorBotSurfaceLogic.Schedule;

namespace MahodAI.CivilDelivery.Estimate.CorridorBoq;

public sealed record CorridorMaterialIssue(string Code, Key? Key, double? StationM, string Detail);
public sealed record CorridorMaterialThickness(double MinM, double MaxM);
public sealed record CorridorMaterialInterface(string UpperCode, string LowerCode, string Status,
    double LowerWidth, double TestedWidth, double MaxGapM, double MaxPenetrationM, IReadOnlyList<string> Details);
public sealed record CorridorMaterialSweepCluster(double FromM, double ToM, double RepresentativeM,
    IReadOnlyList<double> OriginalAbscissaeM);
public sealed record CorridorMaterialSweepEvidence(double BoundM, double MaxShiftM,
    IReadOnlyList<CorridorMaterialSweepCluster> Clusters)
{
    public double VertexIdentityBoundXM { get; init; }
    public double VertexIdentityBoundZM { get; init; }
    public double VertexIdentityMaxShiftXM { get; init; }
    public double VertexIdentityMaxShiftZM { get; init; }
}
public sealed record CorridorMaterialStationResult(Key Key, double StationM, bool Complete, string Status,
    IReadOnlyDictionary<string, double> CodeAreas, IReadOnlyDictionary<string, double> CodeWidths,
    double? AsphaltWidth, IReadOnlyDictionary<string, CorridorMaterialThickness> Thicknesses,
    IReadOnlyList<CorridorMaterialInterface> Interfaces)
{
    public CorridorMaterialSweepEvidence? SweepNormalization { get; init; }
}
public sealed record CorridorMaterialPair(Key Key, double FromM, double ToM,
    IReadOnlyDictionary<string, double> CodeVolumes, IReadOnlyDictionary<string, double> CodePlanAreas,
    double AsphaltPlanArea);
public sealed record CorridorMaterialRegionResult(Key Key, bool Complete, string InterfaceStatus,
    IReadOnlyDictionary<string, double> CodeVolumes, IReadOnlyDictionary<string, double> CodePlanAreas,
    double? AsphaltPlanArea);
public sealed record CorridorMaterialResult(bool Complete, string InterfaceStatus,
    IReadOnlyDictionary<string, double> CodeVolumes, IReadOnlyDictionary<string, double> CodePlanAreas,
    double? AsphaltPlanArea, IReadOnlyList<CorridorMaterialRegionResult> Regions,
    IReadOnlyList<CorridorMaterialStationResult> Stations, IReadOnlyList<CorridorMaterialPair> Pairs,
    IReadOnlyList<CorridorMaterialIssue> Issues)
{
    public string Method => CorridorMaterialLogic.Method;
    public string PlanAreaMetric => CorridorMaterialLogic.PlanAreaMetric;
    public string Status => Complete ? "measured-unapproved" : "partial-unapproved";
}

/// <summary>
/// Pure candidate; no Civil calls or pricing. Caller proves drawing/version/units and inventory.
/// ReadSucceeded means ALL shapes were inventoried successfully, including codes absent at a station.
/// Shapes must preserve actual straight boundary links, not extents. Coordinates are metres in a
/// common same-assembly offset/elevation frame. Ground/MK status must NOT replace material read status.
/// Volumes are average-end native cross-section areas after boundary/area validation. Plan areas
/// integrate union of actual horizontal shape intervals over station; NOT actual XY footprint.
/// Complete describes quantities over the DECLARED schedules, never approval or inferred scope.
/// Interface status and vertical (NOT normal) thickness are separate from quantity completeness.
/// No interpolation through failed stations, between regions or beyond supplied boundaries.
/// </summary>
public static class CorridorMaterialLogic
{
    public const string Method = "corridor-material-boundary-aea/v1-candidate";
    public const string PlanAreaMetric = "station-integral-of-offset-interval-union; not actual XY footprint";
    public const double NumericalToleranceM = 1e-6; // Numerical evidence tolerance; not engineering approval.
    private const double Eps = NumericalToleranceM;
    private sealed record Pt(double X, double Z);
    private sealed record Edge(Pt A, Pt B);
    private sealed record Ring(string Code, double Area, List<Pt> Points, List<Edge> Edges, double Lo, double Hi,
        double IdentityBoundX, double IdentityBoundZ, double IdentityShiftX, double IdentityShiftZ,
        IReadOnlyList<double> OriginalAbscissae);
    private sealed class GeometryUnknown : Exception { public GeometryUnknown(string s) : base(s) { } }
    private sealed class InterfaceAccumulator
    {
        public string Upper = "", Lower = "";
        public double LowerWidth, TestedWidth, Gap, Penetration;
        public bool Unknown;
        public readonly List<string> Details = new();
        public CorridorMaterialInterface Result() => new(Upper, Lower,
            Unknown ? "unknown" : LowerWidth == 0 ? "not_applicable" : Details.Count > 0 ? "failed" : "consistent",
            LowerWidth, TestedWidth, Gap, Penetration, Details);
    }

    public static CorridorMaterialResult Measure(IReadOnlyList<Schedule> schedules,
        IReadOnlyList<CorridorShapeStation> stations, CorridorBoqRuleset rules, double maxStationGapM)
    {
        var issues = new List<CorridorMaterialIssue>();
        var measured = new List<CorridorMaterialStationResult>();
        var pairs = new List<CorridorMaterialPair>();
        var regions = new List<CorridorMaterialRegionResult>();
        bool complete = true;
        void Fail(string code, Key? key, double? st, string detail)
        { complete = false; issues.Add(new(code, key, st, detail)); }
        if (schedules is null || stations is null || rules?.Codes is null)
        {
            Fail("invalid-input", null, null, "Schedules, material observations and rules are required.");
            return Finish(false, regions, measured, pairs, issues);
        }
        bool gapValid = Finite(maxStationGapM) && maxStationGapM > 0;
        if (!gapValid) Fail("invalid-max-gap", null, null, "Finite positive maximum station gap required.");
        if (schedules.Count == 0) Fail("no-schedule", null, null, "No declared material measurement regions.");
        var codeNames = rules.Codes.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (codeNames.Any(string.IsNullOrWhiteSpace))
            Fail("invalid-rules", null, null, "Material rule codes cannot be blank.");
        var asphalt = rules.Codes.Values.Where(r => r is not null && r.Kind == "asphalt").ToList();
        bool orderValid = asphalt.All(r => r.Order.HasValue && r.Order > 0 && rules.Codes.ContainsKey(r.Code))
                          && asphalt.Select(r => r.Order).Distinct().Count() == asphalt.Count;
        var asphaltCodes = asphalt.Select(r => r.Code).ToHashSet(StringComparer.Ordinal);
        var ordered = orderValid ? asphalt.OrderBy(r => r.Order).Select(r => r.Code).ToArray() : Array.Empty<string>();
        if (!orderValid) issues.Add(new("asphalt-order-unknown", null, null, "Asphalt rule order missing or ambiguous; no layer-order approval."));
        var badKeys = schedules.Where(s => s is not null).GroupBy(s => s.Key).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet();
        foreach (var key in badKeys) Fail("duplicate-region", key, null, "Ambiguous region identity; no integration.");
        for (int i = 0; i < schedules.Count; i++)
            for (int j = i + 1; j < schedules.Count; j++)
            {
                var a = schedules[i]; var b = schedules[j];
                if (a?.Key is null || b?.Key is null || a.Key == b.Key ||
                    a.Key.CorridorId != b.Key.CorridorId || a.Key.BaselineId != b.Key.BaselineId ||
                    !Finite(a.StartM, a.EndM, b.StartM, b.EndM)) continue;
                if (Math.Min(a.EndM, b.EndM) > Math.Max(a.StartM, b.StartM))
                {
                    badKeys.Add(a.Key); badKeys.Add(b.Key);
                    Fail("overlapping-regions", a.Key, null, $"Interior overlaps region {b.Key.RegionId}; neither integrated.");
                }
            }
        foreach (var input in stations)
            if (input is null || !schedules.Any(s => s?.Key is not null && s.StationsM is not null &&
                    s.Key == InputKey(input) && s.StationsM.Contains(input.StationM)))
                Fail("unexpected-station", input is null ? null : InputKey(input), input is null ? null : FiniteOrNull(input.StationM),
                    "Observation has no exact declared schedule identity; never integrated.");
        foreach (var schedule in schedules)
        {
            if (schedule is null)
            { Fail("invalid-schedule", null, null, "Null schedule."); continue; }
            var key = schedule.Key;
            int pairStart = pairs.Count, rowStart = measured.Count;
            bool regionComplete = true;
            void RegionFail(string code, double? st, string detail)
            { regionComplete = false; Fail(code, key, st, detail); }
            bool valid = ValidKey(key) && !badKeys.Contains(key) && Finite(schedule.StartM, schedule.EndM)
                && schedule.EndM > schedule.StartM && schedule.StationsM is not null && schedule.StationsM.Count >= 2
                && schedule.StationsM.All(Finite) && !schedule.StationsM.Zip(schedule.StationsM.Skip(1), (a,b) => b <= a).Any(x => x)
                && schedule.StationsM[0] == schedule.StartM && schedule.StationsM[^1] == schedule.EndM;
            if (!valid)
            {
                RegionFail("invalid-schedule", null, "Require unique region, finite increasing stations and both actual region boundaries (at least two stations).");
            }
            else
            {
                foreach (var st in schedule.StationsM!)
                {
                    var matches = stations.Where(r => r is not null && InputKey(r) == key && r.StationM == st).ToList();
                    CorridorMaterialStationResult result;
                    if (matches.Count != 1)
                    {
                        RegionFail(matches.Count == 0 ? "missing-station" : "duplicate-station", st, "Exactly one material observation per scheduled station required.");
                        result = FailedStation(key, st);
                    }
                    else
                    {
                        result = MeasureStation(matches[0], codeNames, asphaltCodes, ordered, orderValid, issues);
                        if (!result.Complete) { complete = false; regionComplete = false; }
                    }
                    measured.Add(result);
                }
                var rows = measured.Skip(rowStart).ToList();
                for (int i = 1; i < rows.Count; i++)
                {
                    var a = rows[i - 1]; var b = rows[i];
                    double gap = b.StationM - a.StationM;
                    if (!gapValid || !Finite(gap) || gap > maxStationGapM)
                    { RegionFail("unintegrated-gap", b.StationM, "Consecutive station interval exceeds declared maximum."); continue; }
                    if (!a.Complete || !b.Complete) continue;
                    var names = a.CodeAreas.Keys.Union(b.CodeAreas.Keys, StringComparer.Ordinal).ToArray();
                    var volumes = names.ToDictionary(c => c, c => Average(a.CodeAreas.GetValueOrDefault(c), b.CodeAreas.GetValueOrDefault(c), gap), StringComparer.Ordinal);
                    var areas = names.ToDictionary(c => c, c => Average(a.CodeWidths.GetValueOrDefault(c), b.CodeWidths.GetValueOrDefault(c), gap), StringComparer.Ordinal);
                    var footprint = Average(a.AsphaltWidth!.Value, b.AsphaltWidth!.Value, gap);
                    if (!volumes.Values.All(Finite) || !areas.Values.All(Finite) || !Finite(footprint))
                    { RegionFail("integration-overflow", b.StationM, "Nonfinite interval quantity; not emitted."); continue; }
                    pairs.Add(new(key, a.StationM, b.StationM, volumes, areas, footprint));
                }
            }
            var ownPairs = pairs.Skip(pairStart).ToList();
            var ownRows = measured.Skip(rowStart).ToList();
            var regionVolumes = SumCodes(ownPairs, false);
            var regionAreas = SumCodes(ownPairs, true);
            double? regionAsphalt = ownPairs.Count == 0 ? null : ownPairs.Sum(p => p.AsphaltPlanArea);
            bool volumeFinite = regionVolumes.Values.All(Finite), areaFinite = regionAreas.Values.All(Finite);
            bool asphaltFinite = !regionAsphalt.HasValue || Finite(regionAsphalt.Value);
            if (!volumeFinite || !areaFinite || !asphaltFinite)
            {
                RegionFail("region-aggregate-overflow", null, "Nonfinite region aggregate not emitted; finite pair receipts retained.");
                if (!volumeFinite) regionVolumes.Clear();
                if (!areaFinite) regionAreas.Clear();
                if (!asphaltFinite) regionAsphalt = null;
            }
            bool rc = regionComplete && ownPairs.Count > 0;
            var iface = InterfaceStatus(ownRows, rc, orderValid);
            regions.Add(new(key, rc, iface, regionVolumes, regionAreas, regionAsphalt));
        }
        return Finish(complete && regions.Count > 0 && regions.All(r => r.Complete), regions, measured, pairs, issues);
    }

    private static CorridorMaterialResult Finish(bool complete, List<CorridorMaterialRegionResult> regions,
        List<CorridorMaterialStationResult> rows, List<CorridorMaterialPair> pairs, List<CorridorMaterialIssue> issues)
    {
        var volumes = SumCodes(pairs, false); var areas = SumCodes(pairs, true);
        double? asphalt = pairs.Count == 0 ? null : pairs.Sum(p => p.AsphaltPlanArea);
        if (!volumes.Values.All(Finite) || !areas.Values.All(Finite) || (asphalt.HasValue && !Finite(asphalt.Value)))
        {
            complete = false; volumes.Clear(); areas.Clear(); asphalt = null;
            issues.Add(new("aggregate-overflow", null, null, "Nonfinite aggregate not emitted; finite pair receipts retained."));
        }
        var statuses = regions.Select(r => r.InterfaceStatus).ToList();
        var status = !complete || statuses.Contains("unknown") ? "unknown" : statuses.Contains("failed") ? "failed"
            : statuses.Contains("consistent") ? "consistent" : "not_applicable";
        return new(complete && pairs.Count > 0, status, volumes, areas, asphalt, regions, rows, pairs, issues);
    }

    private static CorridorMaterialStationResult MeasureStation(CorridorShapeStation input, string[] expectedCodes,
        HashSet<string> asphaltCodes, string[] order, bool orderValid, List<CorridorMaterialIssue> issues)
    {
        var key = InputKey(input); var st = input.StationM;
        CorridorMaterialStationResult Fail(string code, string detail)
        { issues.Add(new(code, key, st, detail)); return FailedStation(key, st); }
        if (!input.ReadSucceeded || !string.IsNullOrEmpty(input.ReadFailure))
            return Fail("material-read-failed", input.ReadFailure ?? "Material inventory incomplete; no zero inferred.");
        if (input.Shapes is null) return Fail("missing-shape-inventory", "Read-success without shape inventory cannot prove empty codes.");
        try
        {
            var shapes = input.Shapes.Select(BuildRing).ToList();
            var codes = expectedCodes.Union(shapes.Select(s => s.Code), StringComparer.Ordinal).ToArray();
            foreach (var code in shapes.Select(s => s.Code).Distinct().Except(expectedCodes))
                issues.Add(new("unmapped-material-code", key, st, $"Measured code {code} has no mapping; no price or item inferred."));
            var sweep = Breakpoints(shapes);
            var xs = sweep.Values;
            var thickness = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            var stats = order.Zip(order.Skip(1), (a,b) => new InterfaceAccumulator { Upper = a, Lower = b }).ToList();
            bool anyAmbiguous = false;
            for (int i = 1; i < xs.Count; i++)
            {
                double left = xs[i - 1], right = xs[i], mid = left + (right - left) / 2;
                var active = shapes.Where(s => s.Lo < mid && mid < s.Hi)
                    .GroupBy(s => s.Code).ToDictionary(g => g.Key, g => g.Select(s => SlabEdges(s, mid)).ToList(), StringComparer.Ordinal);
                foreach (var (code, pieces) in active)
                {
                    // Native areas can be summed only if same-code polygon interiors are disjoint.
                    for (int p = 0; p < pieces.Count; p++)
                        for (int q = p + 1; q < pieces.Count; q++)
                            if (Math.Min(Z(pieces[p].Top, mid), Z(pieces[q].Top, mid)) >
                                Math.Max(Z(pieces[p].Bottom, mid), Z(pieces[q].Bottom, mid)))
                                throw new GeometryUnknown("same-code polygon interiors overlap; native areas may double count");
                    if (pieces.Count > 1 && asphaltCodes.Contains(code)) anyAmbiguous = true;
                    foreach (var (bottom, top) in pieces)
                    {
                        var values = new[] { left, mid, right }.Select(x => Z(top, x) - Z(bottom, x)).ToArray();
                        if (values.Any(v => !Finite(v) || v < -Eps)) throw new GeometryUnknown("invalid vertical thickness");
                        if (!thickness.TryGetValue(code, out var list)) thickness[code] = list = new();
                        list.AddRange(values);
                    }
                }
                foreach (var p in stats)
                {
                    if (!active.TryGetValue(p.Lower, out var lower)) continue;
                    p.LowerWidth += right - left;
                    active.TryGetValue(p.Upper, out var upper);
                    if (lower.Count != 1 || (upper is not null && upper.Count != 1))
                    { p.Unknown = true; p.Details.Add($"Ambiguous vertical material branches on [{left:R},{right:R}]."); continue; }
                    if (upper is null)
                    { p.Details.Add($"Lower material has no upper material on [{left:R},{right:R}]."); continue; }
                    p.TestedWidth += right - left;
                    var residual = new[] { left, mid, right }.Select(x => Z(upper[0].Bottom, x) - Z(lower[0].Top, x)).ToArray();
                    if (residual.Any(v => !Finite(v))) throw new GeometryUnknown("interface arithmetic overflow");
                    p.Gap = Math.Max(p.Gap, residual.Max()); p.Penetration = Math.Max(p.Penetration, -residual.Min());
                    if (residual.Any(v => Math.Abs(v) > Eps)) p.Details.Add($"Noncoincident interface on [{left:R},{right:R}].");
                }
            }
            var interfaces = stats.Select(p => p.Result()).ToList();
            if (anyAmbiguous || !orderValid)
                interfaces.Add(new("", "", "unknown", 0, 0, 0, 0, new[] { "Asphalt branch/order ambiguous; no interface certification." }));
            foreach (var p in interfaces.Where(p => p.Status is "unknown" or "failed"))
                issues.Add(new("asphalt-interface-" + p.Status, key, st, $"{p.UpperCode} bottom / {p.LowerCode} top: {string.Join(" ", p.Details)}"));
            var areas = codes.ToDictionary(c => c, c => shapes.Where(s => s.Code == c).Sum(s => s.Area), StringComparer.Ordinal);
            var widths = codes.ToDictionary(c => c, c => UnionWidth(shapes.Where(s => s.Code == c)), StringComparer.Ordinal);
            double footprint = UnionWidth(shapes.Where(s => asphaltCodes.Contains(s.Code)));
            if (!areas.Values.All(Finite) || !widths.Values.All(Finite) || !Finite(footprint))
                throw new GeometryUnknown("station quantity overflow");
            return new(key, st, true, shapes.Count == 0 ? "no_applicable_geometry" : "valid_geometry", areas, widths, footprint,
                thickness.ToDictionary(kv => kv.Key, kv => new CorridorMaterialThickness(kv.Value.Min(), kv.Value.Max()), StringComparer.Ordinal), interfaces)
                { SweepNormalization = sweep.Evidence };
        }
        catch (GeometryUnknown e) { return Fail("unsupported-material-geometry", e.Message); }
    }

    private static Ring BuildRing(CorridorShapeSample shape)
    {
        if (shape is null || string.IsNullOrWhiteSpace(shape.Code) || !Finite(shape.Area) || shape.Area <= 0 || shape.Links is null || shape.Links.Count < 3)
            throw new GeometryUnknown("Missing code/positive native area/closed boundary links.");
        if (shape.Links.Any(l => l is null || !Finite(l.X1,l.Z1,l.X2,l.Z2)))
            throw new GeometryUnknown("Nonfinite boundary link.");
        // Coordinate identity is roundoff-scale, NOT the 1e-6 interface evidence tolerance.
        double identityX = RoundoffBound(shape.Links.SelectMany(l => new[] { l.X1,l.X2 }));
        double identityZ = RoundoffBound(shape.Links.SelectMany(l => new[] { l.Z1,l.Z2 }));
        double identityShiftX = 0, identityShiftZ = 0;
        var vertices = new List<Pt>(); var ids = new List<(int A, int B)>();
        var bounds = new List<(double MinX,double MaxX,double MinZ,double MaxZ)>();
        int Vertex(Pt p)
        {
            var matches = Enumerable.Range(0, vertices.Count).Where(i =>
                Math.Max(bounds[i].MaxX,p.X)-Math.Min(bounds[i].MinX,p.X) <= identityX &&
                Math.Max(bounds[i].MaxZ,p.Z)-Math.Min(bounds[i].MinZ,p.Z) <= identityZ).ToList();
            if (matches.Count > 1) throw new GeometryUnknown("Ambiguous vertex tolerance cluster.");
            if (matches.Count == 1)
            {
                int i=matches[0];var b=bounds[i];
                bounds[i]=(Math.Min(b.MinX,p.X),Math.Max(b.MaxX,p.X),Math.Min(b.MinZ,p.Z),Math.Max(b.MaxZ,p.Z));
                identityShiftX=Math.Max(identityShiftX,Math.Abs(vertices[i].X-p.X));
                identityShiftZ=Math.Max(identityShiftZ,Math.Abs(vertices[i].Z-p.Z));
                return i;
            }
            vertices.Add(p);bounds.Add((p.X,p.X,p.Z,p.Z));return vertices.Count - 1;
        }
        foreach (var link in shape.Links)
        {
            if (link is null || !Finite(link.X1, link.Z1, link.X2, link.Z2)) throw new GeometryUnknown("Nonfinite boundary link.");
            // Native CalculatedShape boundaries may repeat the closure vertex as a point-link.
            // Exact equality only: an identity segment contributes no edge, area or interval.
            // Near-but-distinct endpoints still go through the existing topology/refusal checks.
            if (link.X1 == link.X2 && link.Z1 == link.Z2) continue;
            int a = Vertex(new(link.X1, link.Z1)), b = Vertex(new(link.X2, link.Z2));
            if (a == b) throw new GeometryUnknown("Zero-length boundary link.");
            var edge = (Math.Min(a,b), Math.Max(a,b));
            if (ids.Contains(edge)) throw new GeometryUnknown("Duplicate boundary link.");
            ids.Add(edge);
        }
        if (ids.Count < 3) throw new GeometryUnknown("Fewer than three nonzero boundary links.");
        var adjacent = Enumerable.Range(0, vertices.Count).ToDictionary(i => i, _ => new List<int>());
        foreach (var (a,b) in ids) { adjacent[a].Add(b); adjacent[b].Add(a); }
        if (adjacent.Values.Any(v => v.Count != 2)) throw new GeometryUnknown("Open/branching boundary.");
        var visited = new List<int>(); int previous = -1, current = 0;
        while (!visited.Contains(current))
        {
            visited.Add(current); int next = adjacent[current].First(i => i != previous);
            previous = current; current = next;
        }
        if (current != 0 || visited.Count != vertices.Count) throw new GeometryUnknown("Disconnected/multiple rings.");
        var points = visited.Select(i => vertices[i]).ToList();
        var edges = points.Zip(points.Skip(1).Append(points[0]), (a,b) => new Edge(a,b)).ToList();
        for (int i = 0; i < edges.Count; i++)
            for (int j = i + 1; j < edges.Count; j++)
            {
                if (j == i + 1 || (i == 0 && j == edges.Count - 1)) continue;
                var a = edges[i]; var b = edges[j];
                if (Crossing(a,b) is not null || OnSegment(a.A,b) || OnSegment(a.B,b) || OnSegment(b.A,a) || OnSegment(b.B,a))
                    throw new GeometryUnknown("Self-crossing/touching boundary.");
            }
        // Translate before shoelace to avoid loss of significance with absolute elevations.
        var origin = points[0];
        double area = Math.Abs(edges.Sum(e => Cross(Sub(e.A, origin), Sub(e.B, origin)))) / 2;
        if (!Finite(area) || Math.Abs(area - shape.Area) > Math.Max(Eps * Eps * points.Count, 1e-6 * shape.Area))
            throw new GeometryUnknown("Native area does not match reconstructed polygon area.");
        var originalAbscissae = shape.Links.SelectMany(l => new[] { l.X1,l.X2 }).ToArray();
        // Widths use the supplied native boundary extrema, never normalized query coordinates.
        double lo = originalAbscissae.Min(), hi = originalAbscissae.Max();
        if (!Finite(hi-lo) || hi-lo <= Eps) throw new GeometryUnknown("No resolvable horizontal interval.");
        return new(shape.Code, shape.Area, points, edges, lo, hi, identityX, identityZ, identityShiftX, identityShiftZ, originalAbscissae);
    }

    private static (List<double> Values, CorridorMaterialSweepEvidence Evidence) Breakpoints(List<Ring> shapes)
    {
        var values = shapes.SelectMany(s => s.OriginalAbscissae).ToList();
        var edges = shapes.SelectMany(s => s.Edges).ToList();
        for (int i = 0; i < edges.Count; i++)
            for (int j = i + 1; j < edges.Count; j++)
            { var p = Crossing(edges[i], edges[j]); if (p is not null) values.Add(p.X); }
        var distinct = values.Distinct().OrderBy(x => x).ToArray();
        if (distinct.Any(v => !Finite(v))) throw new GeometryUnknown("Breakpoint overflow.");
        double bound = RoundoffBound(distinct);
        var result = new List<double>();
        var clusters = new List<CorridorMaterialSweepCluster>();
        for (int i = 0; i < distinct.Length;)
        {
            double first = distinct[i]; int end = i + 1;
            // Diameter is bounded from FIRST, never chained from a moving neighbour.
            while (end < distinct.Length && distinct[end] - first <= bound) end++;
            if (result.Count > 0 && first-result[^1] <= Eps)
                throw new GeometryUnknown("Near-distinct breakpoints beyond roundoff bound; not merged.");
            result.Add(first); // Query abscissae only: original ring coordinates/areas/union intervals stay unchanged.
            if (end > i + 1) clusters.Add(new(first, distinct[end-1], first, distinct[i..end]));
            i = end;
        }
        return (result, new(bound, clusters.Select(c => c.ToM-c.RepresentativeM).DefaultIfEmpty().Max(), clusters)
        {
            VertexIdentityBoundXM=shapes.Select(s=>s.IdentityBoundX).DefaultIfEmpty().Max(),
            VertexIdentityBoundZM=shapes.Select(s=>s.IdentityBoundZ).DefaultIfEmpty().Max(),
            VertexIdentityMaxShiftXM=shapes.Select(s=>s.IdentityShiftX).DefaultIfEmpty().Max(),
            VertexIdentityMaxShiftZM=shapes.Select(s=>s.IdentityShiftZ).DefaultIfEmpty().Max(),
        });
    }

    private static (Edge Bottom, Edge Top) SlabEdges(Ring shape, double mid)
    {
        var edges = shape.Edges.Where(e => Math.Min(e.A.X,e.B.X) < mid && mid < Math.Max(e.A.X,e.B.X)).OrderBy(e => Z(e,mid)).ToList();
        if (edges.Count != 2) throw new GeometryUnknown("Disconnected/ambiguous vertical intervals in one shape.");
        return (edges[0], edges[1]);
    }
    private static double UnionWidth(IEnumerable<Ring> shapes)
    {
        var spans = shapes.OrderBy(s => s.Lo).ToList();
        if (spans.Count == 0) return 0;
        double lo = spans[0].Lo, hi = spans[0].Hi, sum = 0;
        foreach (var span in spans.Skip(1))
            if (span.Lo <= hi) hi = Math.Max(hi, span.Hi);
            else { sum += hi-lo; lo = span.Lo; hi = span.Hi; }
        return sum + hi-lo;
    }
    private static Pt? Crossing(Edge a, Edge b)
    {
        var r = Sub(a.B,a.A); var s = Sub(b.B,b.A); double den = Cross(r,s);
        if (!Finite(den)) throw new GeometryUnknown("Intersection overflow.");
        if (Math.Abs(den) <= Eps*Eps) return null;
        var ca = Sub(b.A,a.A); double t = Cross(ca,s)/den, u = Cross(ca,r)/den;
        double et = Eps/Math.Max(Length(r),Eps), eu = Eps/Math.Max(Length(s),Eps);
        return et < t && t < 1-et && eu < u && u < 1-eu ? new(a.A.X+t*r.X,a.A.Z+t*r.Z) : null;
    }
    private static bool OnSegment(Pt p, Edge e)
    {
        var d = Sub(e.B,e.A); double length = Length(d);
        return length > Eps && Math.Abs(Cross(Sub(p,e.A),d)) <= Eps*length &&
            Math.Min(e.A.X,e.B.X)-Eps <= p.X && p.X <= Math.Max(e.A.X,e.B.X)+Eps &&
            Math.Min(e.A.Z,e.B.Z)-Eps <= p.Z && p.Z <= Math.Max(e.A.Z,e.B.Z)+Eps;
    }
    private static Pt Sub(Pt a, Pt b) => new(a.X-b.X,a.Z-b.Z);
    private static double Cross(Pt a, Pt b) => a.X*b.Z-a.Z*b.X;
    private static double Length(Pt p) => Math.Sqrt(p.X*p.X+p.Z*p.Z);
    private static double RoundoffBound(IEnumerable<double> values)
    {
        double scale=Math.Max(1,values.Select(Math.Abs).DefaultIfEmpty().Max());
        double bound=8*(double.BitIncrement(scale)-scale);
        if (!Finite(bound)) throw new GeometryUnknown("Coordinate resolution overflow.");
        return bound;
    }
    private static double Z(Edge e, double x)
    {
        // Same segment evaluated from either native traversal direction must use one formula.
        if (e.A.X > e.B.X) e = new(e.B, e.A);
        return e.A.Z+(e.B.Z-e.A.Z)*(x-e.A.X)/(e.B.X-e.A.X);
    }
    private static bool Finite(double x) => double.IsFinite(x);
    private static bool Finite(params double[] values) => values.All(double.IsFinite);
    private static double? FiniteOrNull(double x) => Finite(x) ? x : null;
    private static bool ValidKey(Key? key) => key is not null && !string.IsNullOrWhiteSpace(key.CorridorId) &&
        !string.IsNullOrWhiteSpace(key.BaselineId) && !string.IsNullOrWhiteSpace(key.RegionId);
    private static Key InputKey(CorridorShapeStation s) => new(s.CorridorId,s.BaselineId,s.RegionId);
    private static double Average(double a, double b, double gap) => (a/2+b/2)*gap;
    private static CorridorMaterialStationResult FailedStation(Key key, double station) =>
        new(key,station,false,"unknown",new Dictionary<string,double>(),new Dictionary<string,double>(),null,
            new Dictionary<string,CorridorMaterialThickness>(),Array.Empty<CorridorMaterialInterface>());
    private static string InterfaceStatus(List<CorridorMaterialStationResult> rows, bool complete, bool orderValid)
    {
        var status = rows.SelectMany(r => r.Interfaces).Select(p => p.Status).ToList();
        return !complete || !orderValid || status.Contains("unknown") ? "unknown" : status.Contains("failed") ? "failed"
            : status.Contains("consistent") ? "consistent" : "not_applicable";
    }
    private static Dictionary<string,double> SumCodes(IEnumerable<CorridorMaterialPair> pairs, bool area)
    {
        var result = new Dictionary<string,double>(StringComparer.Ordinal);
        foreach (var pair in pairs)
            foreach (var (code,value) in area ? pair.CodePlanAreas : pair.CodeVolumes)
                result[code] = result.GetValueOrDefault(code)+value;
        return result;
    }
}
