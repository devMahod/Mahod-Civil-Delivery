using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Estimate.BoqRulesV2
{
    /// <summary>One physical crossing (a cluster of crossing objects), as the reference sm_geometry.crossings() builds it.</summary>
    public sealed class BoqCrossing
    {
        public required string Kind { get; init; }
        /// <summary>measured | hatch | hatch-assembly | standard</summary>
        public required string WidthSource { get; init; }
        public double? Length { get; init; }
        public double? Width { get; init; }
        /// <summary>Every member handle, sorted (hatches, lines at them, dashed / boundary / edge lines of an assembly).</summary>
        public required IReadOnlyList<string> Handles { get; init; }
        public IReadOnlyList<string> HatchHandles { get; init; } = Array.Empty<string>();
        /// <summary>
        /// Rules 2.3 (v4): hatch handle → the closed polyline drawn with the hatch's own vertices (its boundary). When the
        /// hatch area was not returned, the polyline's area is the hatch area ("area from the boundary", as in HA).
        /// </summary>
        public IReadOnlyDictionary<string, string> Boundary { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Rules 2.3 (v4): continuous lines along an assembly's dashed line (members, never counted again).</summary>
        public IReadOnlyList<string> EdgeHandles { get; init; } = Array.Empty<string>();
        /// <summary>Distinct objects (standard, hatch, assembly) or segments (measured yellow crossings), as the reference's n.</summary>
        public required int N { get; init; }
        /// <summary>Hatch crossings and assemblies: the measured hatch area (m²) the engine found for the hatch handles.</summary>
        public double? HatchM2 { get; internal set; }
        /// <summary>Hatches of this crossing whose area is unknown (neither returned, recovered nor from the boundary).</summary>
        public int MissingHatches { get; internal set; }
        /// <summary>Hatches of this crossing measured by their boundary polyline's area.</summary>
        public int Fallback { get; internal set; }
        /// <summary>Measured yellow crossings with a bike band: the full yellow outline width (m).</summary>
        public double? OutlineWidth { get; init; }
        /// <summary>Measured yellow crossings with a bike band: the width of the bike band(s), paid in part 812 (m).</summary>
        public double? BikeBandWidth { get; init; }
        /// <summary>Outline width minus the pedestrian width: bike band(s) plus the unpainted gap (m).</summary>
        public double? RemovedWidth { get; init; }
    }

    /// <summary>
    /// Crossing (811) union algorithm and the long-object rule, ported from the reference sm_geometry.py (v4, 30.09.2026).
    /// Uses SEGMENT ENDPOINTS (segment midpoints for clustering, endpoint projections for the extent), never bounding boxes.
    /// <list type="bullet">
    /// <item>Yellow edge lines cluster by midpoint (link distance). A cluster whose parallel lines span at least the
    /// minimum width is a crossing measured as length × measured width; otherwise its lines behave as single lines.</item>
    /// <item>White lines and hatches within the near distance of a measured yellow crossing are the same crossing drawn
    /// again (excluded).</item>
    /// <item>Hatches elsewhere cluster by boundary centroid. With a dashed white line (linetype scale 0.5) near the cluster,
    /// it is ONE assembly: zebra = the dashed line's length × the standard width, plus the measured hatch area; the other
    /// lines near it are its boundary (or a copy), and a continuous line along the dashed one is its edge — members, never
    /// counted again. Without a dashed line the cluster is a hatch-only crossing measured by its hatch area.</item>
    /// <item>Remaining white lines cluster by midpoint; each cluster is length × a standard width ("dashed" or "solid" by
    /// the linetype scale); a cluster shorter than 5 cm is drafting noise.</item>
    /// </list>
    /// </summary>
    public static class BoqCrosswalkGeometry
    {
        public const string RoleYellowEdge = "yellow-edge";
        public const string RoleWhiteOnYellow = "white-on-yellow";
        public const string RoleHatchOnYellow = "hatch-on-yellow";
        public const string RoleHatchStripe = "hatch-stripe";
        public const string RoleWhiteAtHatch = "white-at-hatch";
        public const string RoleWhiteOnly = "white-only";
        public const string RoleAssemblyHatch = "assembly-hatch";
        public const string RoleAssemblyDashed = "assembly-dashed";
        public const string RoleAssemblyBoundary = "assembly-boundary";
        public const string RoleAssemblyEdge = "assembly-edge";
        public const string RoleNoise = "noise";

        private readonly record struct Line(string Handle, string Layer, BoqSegment Seg);
        private readonly record struct Hat(string Handle, BoqPoint Centroid);

        public sealed record Result(IReadOnlyList<BoqCrossing> Crossings, IReadOnlyDictionary<string, string> Roles, int IgnoredWithoutLayer);

        public static Result Compute(BoqCrosswalkConfig config, IEnumerable<BoqEntityGeometry> entities)
        {
            ArgumentNullException.ThrowIfNull(config);
            var yellowLayers = config.YellowLayers.ToHashSet(StringComparer.Ordinal);
            var whiteLayers = config.WhiteLayers.ToHashSet(StringComparer.Ordinal);
            var hatchLayers = config.HatchLayers.ToHashSet(StringComparer.Ordinal);
            var yel = new List<Line>();
            var wht = new List<Line>();
            var hat = new List<Hat>();
            var hatPoints = new Dictionary<string, HashSet<(double, double)>>(StringComparer.Ordinal);
            var byHandle = new Dictionary<string, BoqEntityGeometry>(StringComparer.Ordinal);
            var bikeLayers = config.BikeLayers.ToHashSet(StringComparer.Ordinal);
            var bike = new List<BoqSegment>();
            var ignored = 0;
            foreach (var entity in entities)
            {
                if (entity.Layer == null) { ignored++; continue; }
                byHandle[entity.Handle] = entity;
                if (entity.IsHatch)
                {
                    if (hatchLayers.Contains(entity.Layer) && entity.HatchCentroid is { } centroid)
                    {
                        hat.Add(new Hat(entity.Handle, centroid));
                        if (entity.HatchBoundaryPoints is { Count: > 0 } points) hatPoints[entity.Handle] = RoundedSet(points);
                    }
                    continue;
                }
                if (bikeLayers.Contains(entity.Layer)) bike.AddRange(entity.Segments);
                if (yellowLayers.Contains(entity.Layer))
                    foreach (var s in entity.Segments) yel.Add(new Line(entity.Handle, entity.Layer, s));
                else if (whiteLayers.Contains(entity.Layer))
                    foreach (var s in entity.Segments) wht.Add(new Line(entity.Handle, entity.Layer, s));
            }
            bool Dashed(string handle) =>
                byHandle.TryGetValue(handle, out var e) && e.LinetypeScale is { } scale && scale == config.DashedLinetypeScale;

            var roles = new Dictionary<string, string>(StringComparer.Ordinal);
            var output = new List<BoqCrossing>();
            var yellowCrossingSegments = new List<BoqSegment>();
            foreach (var cluster in Clusters(yel, item => item.Seg.Mid, config.LinkDistance))
            {
                var (length, width) = Extent(cluster.Select(item => item.Seg).ToList(), config.AngleBin, config.ParallelTolerance);
                if (width >= config.MinMeasuredWidth)
                {
                    yellowCrossingSegments.AddRange(cluster.Select(item => item.Seg));
                    var split = Bands(cluster.Select(item => item.Seg).ToList(), bike, config);
                    output.Add(split is not { } bands
                        ? new BoqCrossing
                        {
                            Kind = config.LabelMeasured, WidthSource = "measured", Length = length, Width = width,
                            Handles = SortedDistinct(cluster.Select(item => item.Handle)), N = cluster.Count,
                        }
                        // A bike crossing beside the zebra is one yellow outline around two bands: only the pedestrian
                        // band is zebra paint; the bike band is paid in part 812 and the gap between them is unpainted.
                        : new BoqCrossing
                        {
                            Kind = bands.Pedestrian > 0 ? config.LabelPedestrianBand : config.LabelBikeOnly,
                            WidthSource = "measured", Length = length, Width = bands.Pedestrian,
                            OutlineWidth = width, BikeBandWidth = bands.Bike, RemovedWidth = bands.Removed,
                            Handles = SortedDistinct(cluster.Select(item => item.Handle)), N = cluster.Count,
                        });
                    foreach (var item in cluster) roles[item.Handle] = RoleYellowEdge;
                }
                else
                {
                    // A lone yellow line behaves like a white single line.
                    wht.AddRange(cluster);
                }
            }

            bool NearYellow(BoqPoint p) =>
                yellowCrossingSegments.Count > 0 && yellowCrossingSegments.Min(s => PointSegmentDistance(p, s)) <= config.NearDistance;

            var freeWhite = new List<Line>();
            foreach (var item in wht)
            {
                if (NearYellow(item.Seg.Mid)) roles.TryAdd(item.Handle, RoleWhiteOnYellow);
                else freeWhite.Add(item);
            }
            var freeHatch = new List<Hat>();
            foreach (var item in hat)
            {
                if (NearYellow(item.Centroid)) roles[item.Handle] = RoleHatchOnYellow;
                else freeHatch.Add(item);
            }
            foreach (var cluster in Clusters(freeHatch, item => item.Centroid, config.LinkDistance))
            {
                var hatchHandles = cluster.Select(item => item.Handle).ToList();
                var at = freeWhite.Where(w => cluster.Min(c => BoqPoint.Distance(w.Seg.Mid, c.Centroid)) <= config.NearDistance).ToList();
                var atHandles = at.Select(w => w.Handle).ToHashSet(StringComparer.Ordinal);
                var dashed = at.Where(w => Dashed(w.Handle)).ToList();
                var dashedHandles = dashed.Select(w => w.Handle).ToHashSet(StringComparer.Ordinal);
                // The closed polyline drawn with the hatch's own vertices is its boundary. The reference iterates a set, so a
                // hatch with two such polylines (copies) keeps an arbitrary one; here the last in ordinal order.
                var boundary = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var h2 in atHandles.Where(h => !dashedHandles.Contains(h)).OrderBy(h => h, StringComparer.Ordinal))
                {
                    if (!byHandle.TryGetValue(h2, out var polyline) || polyline.Vertices is not { Count: > 0 } vertices) continue;
                    var set = RoundedSet(vertices);
                    foreach (var h in hatchHandles)
                        if (hatPoints.TryGetValue(h, out var points) && set.SetEquals(points))
                            boundary[h] = h2;
                }
                if (dashed.Count == 0)
                {
                    foreach (var h in hatchHandles) roles[h] = RoleHatchStripe;
                    foreach (var h2 in atHandles) roles[h2] = RoleWhiteAtHatch;
                    var members = SortedDistinct(hatchHandles.Concat(atHandles));
                    output.Add(new BoqCrossing
                    {
                        Kind = config.LabelHatch, WidthSource = "hatch",
                        Handles = members, HatchHandles = hatchHandles, Boundary = boundary, N = members.Count,
                    });
                    continue;
                }

                var dsegs = dashed.Select(w => w.Seg).ToList();
                var (length, _) = Extent(dsegs, config.AngleBin, config.ParallelTolerance);
                var longest = dsegs[0];
                foreach (var s in dsegs)
                    if (s.Length > longest.Length) longest = s; // Python max(): the first of equal length
                var dl = longest.Length;
                double ux = 1.0, uy = 0.0;
                if (dl > 0)
                {
                    ux = (longest.B.X - longest.A.X) / dl;
                    uy = (longest.B.Y - longest.A.Y) / dl;
                }
                double nx = -uy, ny = ux;
                var dOff = 0.0;
                foreach (var s in dsegs) dOff += s.Mid.X * nx + s.Mid.Y * ny;
                dOff /= dsegs.Count;
                var along = dsegs.SelectMany(s => new[] { s.A, s.B }).Select(p => p.X * ux + p.Y * uy).ToList();
                double d0 = along.Min(), d1 = along.Max();
                var dMid = longest.Mid;
                var edges = new HashSet<string>(StringComparer.Ordinal);
                foreach (var w in freeWhite)
                {
                    var sg = w.Seg;
                    if (atHandles.Contains(w.Handle) || Dashed(w.Handle) || sg.Length < MinSegment) continue;
                    var ang = Math.Abs(Math.Atan2(sg.B.Y - sg.A.Y, sg.B.X - sg.A.X) - Math.Atan2(uy, ux)) % Math.PI;
                    if (Math.Min(ang, Math.PI - ang) > config.ParallelTolerance || BoqPoint.Distance(sg.Mid, dMid) > config.LinkDistance) continue;
                    var off = sg.Mid.X * nx + sg.Mid.Y * ny;
                    var a0 = sg.A.X * ux + sg.A.Y * uy;
                    var a1 = sg.B.X * ux + sg.B.Y * uy;
                    double lo = Math.Min(a0, a1), hi = Math.Max(a0, a1);
                    var overlap = Math.Min(hi, d1) - Math.Max(lo, d0);
                    if (Math.Abs(off - dOff) <= config.EdgeOfDashed && overlap >= config.EdgeOverlap * Math.Min(hi - lo, d1 - d0))
                        edges.Add(w.Handle);
                }
                foreach (var h in hatchHandles) roles[h] = RoleAssemblyHatch;
                foreach (var h2 in atHandles) roles[h2] = dashedHandles.Contains(h2) ? RoleAssemblyDashed : RoleAssemblyBoundary;
                foreach (var h2 in edges) roles[h2] = RoleAssemblyEdge;
                var assembly = SortedDistinct(hatchHandles.Concat(atHandles).Concat(edges));
                output.Add(new BoqCrossing
                {
                    Kind = config.LabelAssembly, WidthSource = "hatch-assembly", Length = length,
                    Width = StandardWidth(config, dashed), Handles = assembly, HatchHandles = hatchHandles,
                    Boundary = boundary, EdgeHandles = SortedDistinct(edges), N = assembly.Count,
                });
            }
            var rest = freeWhite.Where(w => !(roles.TryGetValue(w.Handle, out var role) &&
                                              role is RoleWhiteAtHatch or RoleAssemblyDashed or RoleAssemblyBoundary or RoleAssemblyEdge)).ToList();
            foreach (var group in Clusters(rest, item => item.Seg.Mid, config.LinkDistance))
            {
                // v4.1 (CW4-151): a handle whose every piece is a stub shorter than StubLength is noise (a 0.087 m yellow stub
                // stretched crossing #151 by ~1 m); the group is measured without it.
                var longHandles = group.Where(item => item.Seg.Length >= config.StubLength).Select(item => item.Handle).ToHashSet(StringComparer.Ordinal);
                foreach (var item in group)
                    if (!longHandles.Contains(item.Handle)) roles[item.Handle] = RoleNoise;
                var cluster = group.Where(item => longHandles.Contains(item.Handle)).ToList();
                var (length, _) = cluster.Count > 0 ? Extent(cluster.Select(item => item.Seg).ToList(), config.AngleBin, config.ParallelTolerance) : (0.0, 0.0);
                if (length < MinSegment)
                {
                    // A cluster of pieces shorter than 5 cm (e.g. a 3.7 cm line): drafting noise, no crossing.
                    foreach (var item in cluster) roles[item.Handle] = RoleNoise;
                    continue;
                }
                var handles = SortedDistinct(cluster.Select(item => item.Handle));
                var yellow = cluster.Where(item => yellowLayers.Contains(item.Layer)).Select(item => item.Handle).ToHashSet(StringComparer.Ordinal);
                var dashedLine = handles.Where(h => !yellow.Contains(h)).All(h => !byHandle.ContainsKey(h) || Dashed(h));
                var kind = yellow.Count > 0
                    ? (yellow.Count < handles.Count ? config.LabelYellowWithWhite : config.LabelYellowOnly)
                    : dashedLine ? config.LabelStandard : config.LabelStandardSolid;
                output.Add(new BoqCrossing
                {
                    Kind = kind, WidthSource = "standard",
                    Length = length, Width = StandardWidth(config, cluster), Handles = handles, N = handles.Count,
                });
                foreach (var item in cluster) roles[item.Handle] = RoleWhiteOnly;
            }
            return new Result(output, roles, ignored);
        }

        /// <summary>The standard width of lines: the layer's own when every line gives the same one, else the default.</summary>
        private static double StandardWidth(BoqCrosswalkConfig config, IEnumerable<Line> lines)
        {
            var widths = lines.Select(item => config.StandardWidthByLayer.TryGetValue(item.Layer, out var w) ? w : config.DefaultStandardWidth)
                .Distinct().ToList();
            return widths.Count == 1 ? widths[0] : config.DefaultStandardWidth;
        }

        /// <summary>Points rounded to 0.01 exactly as Python's round(v, 2) (the reference compares these sets).</summary>
        private static HashSet<(double, double)> RoundedSet(IEnumerable<BoqPoint> points) =>
            points.Select(p => (BoqRulesEngine.PythonRound(p.X, 2) + 0.0, BoqRulesEngine.PythonRound(p.Y, 2) + 0.0)).ToHashSet();

        /// <summary>
        /// Handles (per source role) of line-like objects on the parts' max_len layers whose drawn length exceeds the
        /// part's limit: sum of chord lengths, or the native length when the producer bounded the vertex list.
        /// </summary>
        public static IReadOnlyDictionary<(string Src, string Handle), (double Length, string Layer)> LongObjects(
            BoqRuleset rules, IEnumerable<BoqEntityGeometry> entities)
        {
            var limits = new Dictionary<(string Src, string Layer), double>();
            foreach (var part in rules.AllParts.Where(p => p.MaxLength is > 0))
                foreach (var src in part.Src)
                    foreach (var layer in part.Layers)
                    {
                        var key = (src, layer);
                        limits[key] = limits.TryGetValue(key, out var existing) ? Math.Min(existing, part.MaxLength!.Value) : part.MaxLength!.Value;
                    }
            var result = new Dictionary<(string Src, string Handle), (double Length, string Layer)>();
            if (limits.Count == 0) return result;
            foreach (var entity in entities)
            {
                if (entity.IsHatch || entity.Layer == null || !limits.TryGetValue((entity.Src, entity.Layer), out var limit)) continue;
                var length = entity.SegmentsComplete ? entity.Segments.Sum(s => s.Length) : entity.FallbackLength ?? 0.0;
                if (length > limit) result[(entity.Src, entity.Handle)] = (length, entity.Layer);
            }
            return result;
        }

        // ------------------------------------------------------------------ helpers (reference semantics)

        /// <summary>Connected components under "distance &lt; link", explored with a LIFO stack in item order.</summary>
        internal static List<List<T>> Clusters<T>(IReadOnlyList<T> items, Func<T, BoqPoint> key, double link)
        {
            var keys = items.Select(key).ToArray();
            var used = new bool[items.Count];
            var output = new List<List<T>>();
            for (var i = 0; i < items.Count; i++)
            {
                if (used[i]) continue;
                var stack = new Stack<int>();
                var component = new List<T>();
                stack.Push(i);
                used[i] = true;
                while (stack.Count > 0)
                {
                    var k = stack.Pop();
                    component.Add(items[k]);
                    for (var j = 0; j < items.Count; j++)
                    {
                        if (used[j] || !(BoqPoint.Distance(keys[k], keys[j]) < link)) continue;
                        used[j] = true;
                        stack.Push(j);
                    }
                }
                output.Add(component);
            }
            return output;
        }

        /// <summary>Shorter segments are drafting noise without a meaningful direction (reference MIN_SEGMENT_M).</summary>
        public const double MinSegment = 0.05;

        /// <summary>
        /// Dominant direction (length-weighted angle bins, first maximum in insertion order), the along-extent of the
        /// parallel segments' endpoints and the across-extent of their midpoints.
        /// </summary>
        internal static (double Along, double Across) Extent(IReadOnlyList<BoqSegment> segments, double bin, double tolerance)
        {
            segments = segments.Where(s => s.Length >= MinSegment).ToList();
            if (segments.Count == 0) return (0, 0);
            var order = new List<int>();
            var weights = new Dictionary<int, double>();
            foreach (var s in segments)
            {
                var key = (int)Math.Round(Angle(s) / bin, MidpointRounding.ToEven);
                if (!weights.ContainsKey(key)) { order.Add(key); weights[key] = 0.0; }
                weights[key] += s.Length;
            }
            var best = order[0];
            foreach (var key in order)
                if (weights[key] > weights[best]) best = key;
            var dominant = best * bin;
            double ux = Math.Cos(dominant), uy = Math.Sin(dominant), nx = -Math.Sin(dominant), ny = Math.Cos(dominant);
            var parallel = segments.Where(s =>
            {
                var d = Math.Abs(Angle(s) - dominant);
                return Math.Min(d, Math.PI - d) < tolerance;
            }).ToList();
            if (parallel.Count == 0) return (0, 0);
            var offsets = parallel.Select(s => s.Mid.X * nx + s.Mid.Y * ny).ToList();
            var along = parallel.SelectMany(s => new[] { s.A, s.B }).Select(p => p.X * ux + p.Y * uy).ToList();
            return (along.Max() - along.Min(), offsets.Max() - offsets.Min());
        }

        /// <summary>
        /// The reference sm_geometry._bands: the bands between consecutive parallel edges of a measured yellow cluster
        /// (edges within EdgeMerge are one edge, averaged in offset order). A band holding the midpoint of a parallel
        /// bike-row segment strictly inside it (and along the crossing) is a bike band. Null when no band is a bike band;
        /// otherwise the pedestrian width (non-bike bands at least MinMeasuredWidth wide), the bike width and the removed
        /// width (outline edges span minus pedestrian).
        /// </summary>
        internal static (double Pedestrian, double Bike, double Removed)? Bands(
            IReadOnlyList<BoqSegment> segments, IReadOnlyList<BoqSegment> bikeRows, BoqCrosswalkConfig config)
        {
            segments = segments.Where(s => s.Length >= MinSegment).ToList();
            if (segments.Count == 0 || bikeRows.Count == 0) return null;
            var order = new List<int>();
            var weights = new Dictionary<int, double>();
            foreach (var s in segments)
            {
                var key = (int)Math.Round(Angle(s) / config.AngleBin, MidpointRounding.ToEven);
                if (!weights.ContainsKey(key)) { order.Add(key); weights[key] = 0.0; }
                weights[key] += s.Length;
            }
            var best = order[0];
            foreach (var key in order)
                if (weights[key] > weights[best]) best = key;
            var dominant = best * config.AngleBin;
            double ux = Math.Cos(dominant), uy = Math.Sin(dominant), nx = -Math.Sin(dominant), ny = Math.Cos(dominant);
            bool Parallel(BoqSegment s)
            {
                var d = Math.Abs(Angle(s) - dominant);
                return Math.Min(d, Math.PI - d) < config.ParallelTolerance;
            }
            var parallel = segments.Where(Parallel).ToList();
            if (parallel.Count == 0) return null;
            var offsets = parallel.Select(s => s.Mid.X * nx + s.Mid.Y * ny).OrderBy(o => o).ToList();
            var groups = new List<List<double>>();
            foreach (var o in offsets)
            {
                if (groups.Count > 0 && o - groups[^1][^1] < config.EdgeMerge) groups[^1].Add(o);
                else groups.Add(new List<double> { o });
            }
            var edges = groups.Select(g => g.Sum() / g.Count).ToList();
            var along = parallel.SelectMany(s => new[] { s.A, s.B }).Select(p => p.X * ux + p.Y * uy).ToList();
            double lowAlong = along.Min() - config.BandAlong, highAlong = along.Max() + config.BandAlong;
            var rows = bikeRows.Where(Parallel)
                .Select(s => (Offset: s.Mid.X * nx + s.Mid.Y * ny, Along: s.Mid.X * ux + s.Mid.Y * uy)).ToList();
            var bands = new List<(double Width, bool Bike)>();
            for (var i = 0; i + 1 < edges.Count; i++)
            {
                double lo = edges[i], hi = edges[i + 1];
                var isBike = rows.Any(r => lo + config.BandInside < r.Offset && r.Offset < hi - config.BandInside &&
                                           lowAlong <= r.Along && r.Along <= highAlong);
                bands.Add((hi - lo, isBike));
            }
            if (!bands.Any(b => b.Bike)) return null;
            double pedestrian = 0, bikeWidth = 0, total = 0;
            foreach (var (bandWidth, isBike) in bands)
            {
                if (!isBike && bandWidth >= config.MinMeasuredWidth) pedestrian += bandWidth;
                if (isBike) bikeWidth += bandWidth;
                total += bandWidth;
            }
            return (pedestrian, bikeWidth, total - pedestrian);
        }

        /// <summary>Direction in [0, π] exactly as Python's math.atan2(dy, dx) % math.pi.</summary>
        internal static double Angle(BoqSegment s) => PythonMod(Math.Atan2(s.B.Y - s.A.Y, s.B.X - s.A.X), Math.PI);

        internal static double PythonMod(double x, double m)
        {
            var r = x % m; // C# % on doubles is C fmod, as Python's float % starts from
            if (r != 0)
            {
                if ((m < 0) != (r < 0)) r += m;
            }
            else r = m < 0 ? -0.0 : 0.0;
            return r;
        }

        internal static double PointSegmentDistance(BoqPoint p, BoqSegment s)
        {
            var dx = s.B.X - s.A.X;
            var dy = s.B.Y - s.A.Y;
            var l2 = dx * dx + dy * dy;
            var t = l2 == 0 ? 0.0 : Math.Max(0.0, Math.Min(1.0, ((p.X - s.A.X) * dx + (p.Y - s.A.Y) * dy) / l2));
            return BoqPoint.Distance(p, new BoqPoint(s.A.X + t * dx, s.A.Y + t * dy));
        }

        private static IReadOnlyList<string> SortedDistinct(IEnumerable<string> handles) =>
            handles.Distinct(StringComparer.Ordinal).OrderBy(h => h, StringComparer.Ordinal).ToList();
    }
}
