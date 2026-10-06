using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Estimate.BoqRulesV2
{
    /// <summary>
    /// One normalised host measurement: the source role (drawing tag from the ruleset, e.g. "GM"), layer, entity type,
    /// measure kind and native quantity (SI), the source handle, whether the object is a closed polyline measured both
    /// ways, the effective block name and the extents [minx, miny, maxx, maxy] (null when not measured).
    /// </summary>
    public sealed record BoqRecord(
        string Src, string Layer, string Etype, string Kind, double Qty, string Handle,
        bool Closed, string Block, IReadOnlyList<double>? Bbox)
    {
        /// <summary>The golden bucket key: "SRC:HANDLE:kind".</summary>
        public string BucketKey => Src + ":" + Handle + ":" + Kind;
    }

    public readonly record struct BoqPoint(double X, double Y)
    {
        public static double Distance(BoqPoint a, BoqPoint b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    public readonly record struct BoqSegment(BoqPoint A, BoqPoint B)
    {
        public BoqPoint Mid => new((A.X + B.X) / 2, (A.Y + B.Y) / 2);
        public double Length => BoqPoint.Distance(A, B);
    }

    /// <summary>
    /// Plan geometry of one entity: the segments of a line-like entity, or the boundary centroid of a hatch. In
    /// <see cref="BoqInputSet.Geometry"/> it only classifies (crossings, long objects); in
    /// <see cref="BoqInputSet.LengthGeometry"/> its segments are the plan chords the one-object-once rule measures.
    /// <see cref="Layer"/> may be null when the producer could not name it; such entities are ignored and reported.
    /// <see cref="SegmentsComplete"/> is false when the producer bounded the vertex list; the long-object rule then uses
    /// <see cref="FallbackLength"/> (the native measured length) instead of the chords.
    /// </summary>
    public sealed record BoqEntityGeometry(
        string Src, string Handle, string? Layer, bool IsHatch,
        IReadOnlyList<BoqSegment> Segments, BoqPoint? HatchCentroid,
        bool SegmentsComplete = true, double? FallbackLength = null)
    {
        /// <summary>
        /// Rules 2.3 (v4): the entity's own linetype scale (golden key sm_geometry.linetype_scale_by_handle; live evidence
        /// cad_entity_linetype_scale). A crossing line with the dashed scale is the crossing's dashed line. Null = unknown.
        /// </summary>
        public double? LinetypeScale { get; init; }

        /// <summary>
        /// Rules 2.3 (v4): the stored vertices of a polyline (never of a LINE), for "a closed polyline with the hatch's own
        /// vertices" (the hatch boundary drawn as a polyline). Null for a LINE or when unknown.
        /// </summary>
        public IReadOnlyList<BoqPoint>? Vertices { get; init; }

        /// <summary>
        /// Rules 2.3 (v4): a hatch's boundary points (line-edge endpoints and polyline-loop vertices; golden key
        /// hatch_boundary_points, live evidence cad_hatch_boundary_points). Null when unknown.
        /// </summary>
        public IReadOnlyList<BoqPoint>? HatchBoundaryPoints { get; init; }
    }

    /// <summary>
    /// A host line whose native length is exactly zero (start = end, a proven zero-length geometry): nothing to measure and
    /// no quantity changes. Rules 2.3 (v4) list it once in 'לא נכלל' as information, never as a missing object.
    /// </summary>
    public sealed record BoqZeroLength(string Src, string? Layer, string Handle, string Kind = "length");

    /// <summary>Hatch area totals per layer for a role whose hatches are billed as layer totals ("hatch" parts).</summary>
    public sealed record BoqHatchLayerTotal(
        string Src, string Layer, int DirectCount, decimal DirectArea,
        int RecoveredCount, decimal RecoveredArea, int UnresolvedCount,
        IReadOnlyList<string>? UnresolvedHandles = null);

    /// <summary>
    /// v4.5: an area hatch of the overlap layers — Civil's state for it ("direct" = its area was measured) and that area.
    /// </summary>
    public sealed record BoqHaHatch(string Handle, string Layer, string State, double? NativeAreaM2);

    /// <summary>v4.5: the boundary polygon of an area hatch as WKT (read-only geometry, arcs discretised; holes as rings).</summary>
    public sealed record BoqHaPolygon(string Handle, string Layer, string Wkt);

    /// <summary>A host object that could not be measured (its quantity is unknown, not zero).</summary>
    public sealed record BoqUnmeasured(string Src, string? Layer, string Handle, string What, string Action);

    /// <summary>
    /// One provenance row of the sources sheet. A drawing row names its source role (<see cref="Role"/>) and carries the
    /// file name, the scan date, the run id and the drawing fingerprint separately, so the sheet can print the reference
    /// layout (the drawings first, the run ids and fingerprints at the bottom); a row without a role is printed as is.
    /// </summary>
    public sealed record BoqSourceRow(string Label, string Value)
    {
        public string? Role { get; init; }
        public string? FileName { get; init; }
        public DateTime? MeasuredAtUtc { get; init; }
        public string? RunId { get; init; }
        public string? DrawingSha256 { get; init; }
    }

    /// <summary>
    /// Rules v4.1 (review MARK-V4-01, 30.09.2026): an array insert (an anonymous path array such as *U269) whose items
    /// carry member blocks of their own (bike symbols). Every member is an ordinary count record of <see cref="Src"/> with
    /// the handle "ARRAY/ITEM/MEMBER" and its world point in <see cref="BoqInputSet.CountPoints"/>; the insert itself is
    /// the count record with <see cref="Handle"/>. <see cref="FirstItemPoint"/> (the array's first item, world) finds the
    /// path line the array follows; null when unknown.
    /// </summary>
    public sealed record BoqArrayInsert(string Src, string Handle)
    {
        public string Block { get; init; } = "";
        public BoqPoint? FirstItemPoint { get; init; }
        /// <summary>The line the array follows (its first item sits on the line's first vertex) and that line's layer; null = not found.</summary>
        public string? PathLine { get; init; }
        public string? PathLayer { get; init; }
        /// <summary>The member records' handles, in item order.</summary>
        public IReadOnlyList<string> MemberHandles { get; init; } = Array.Empty<string>();
    }

    /// <summary>An INSERT placement: plan insertion point (metres), rotation (radians), scale factors and extrusion normal.</summary>
    public sealed record BoqInsertTransform(double X, double Y, double Rotation, double[] Scale, double[] Normal);

    /// <summary>Everything the engine reads. Producers: the golden JSON reader and the neutral-record adapter.</summary>
    public sealed class BoqInputSet
    {
        public List<BoqRecord> Records { get; } = new();
        /// <summary>
        /// Rotation (radians) of count records by (source role, handle): two blocks of the same line at the same point in
        /// opposite directions (π apart) are named as a possible back-to-back pair (rules v4.1). Absent = unknown.
        /// </summary>
        public Dictionary<(string Src, string Handle), double> CountRotations { get; } = new();
        /// <summary>
        /// Rules 2.8 (BOQ-N1): the full INSERT transform of count records whose block has an approved physical footprint —
        /// insertion point (metres, plan), rotation, scale factors and normal. Absent = the transform is not proven.
        /// </summary>
        public Dictionary<(string Src, string Handle), BoqInsertTransform> CountTransforms { get; } = new();
        /// <summary>
        /// Rules 2.8: the definition signature of an approved-footprint block as read from each source file; a null value
        /// means the definition could not be read completely (then it cannot match the approved one).
        /// </summary>
        public Dictionary<(string Src, string Block), BoqDefinitionSignature?> BlockSignatures { get; } = new();
        /// <summary>Array inserts whose member blocks are count records of their own (rules v4.1).</summary>
        public List<BoqArrayInsert> Arrays { get; } = new();
        /// <summary>An array member within this distance of a model-space symbol of the same block is that symbol (the array's end).</summary>
        public double ArraySameLocationM { get; set; } = 5.0;
        /// <summary>
        /// The crossings sheet's sentence on where the grouping / length / width geometry came from; null = the ruleset's
        /// text (the reference's offline read). The live adapter names the live read instead.
        /// </summary>
        public string? CrossingSourceNote { get; set; }
        /// <summary>Classification geometry (crossings, long objects). Never a quantity.</summary>
        public List<BoqEntityGeometry> Geometry { get; } = new();
        /// <summary>
        /// Plan chords (metres, Z ignored) of line-like records by (source role, handle), for the "one physical object once"
        /// length rule: a closed polyline includes its closing chord; arcs arrive tessellated. A record without an entry
        /// (or with <see cref="BoqEntityGeometry.SegmentsComplete"/> false) has no plan geometry and keeps its measured
        /// quantity, with a note.
        /// </summary>
        public Dictionary<(string Src, string Handle), BoqEntityGeometry> LengthGeometry { get; } = new();
        /// <summary>Insertion point (metres) of count records by (source role, handle), for the same-block-same-point rule.</summary>
        public Dictionary<(string Src, string Handle), BoqPoint> CountPoints { get; } = new();
        /// <summary>Where <see cref="LengthGeometry"/> / <see cref="CountPoints"/> came from (shown in the workbook).</summary>
        public string ObjectGeometryReader { get; set; } = "";
        public List<BoqHatchLayerTotal> HatchLayers { get; } = new();
        /// <summary>v4.5: every area hatch of the overlap layers with its Civil state, in read order.</summary>
        public List<BoqHaHatch> HaHatches { get; } = new();
        /// <summary>v4.5: the boundary polygons that could be built, in read order (a hatch without one is "not built").</summary>
        public List<BoqHaPolygon> HaHatchPolygons { get; } = new();
        /// <summary>v4.5: how many area hatches of those layers the read holds (built or not).</summary>
        public int? HaHatchesInRead { get; set; }
        /// <summary>Strictly recovered hatch areas (m²) by handle for hatches whose native area failed; null = not recovered.</summary>
        public Dictionary<string, double?> StrictHatchRecoveries { get; } = new(StringComparer.Ordinal);
        public List<BoqUnmeasured> Unmeasured { get; } = new();
        /// <summary>Proven zero-length lines (information in 'לא נכלל', never missing).</summary>
        public List<BoqZeroLength> ZeroLength { get; } = new();
        public List<BoqSourceRow> SourceRows { get; } = new();
        public string GeometryReader { get; set; } = "";
        public List<string> Warnings { get; } = new();
    }

    /// <summary>Insertion-ordered counter (Python collections.Counter semantics used by the reference).</summary>
    public sealed class BoqCounter
    {
        private readonly List<string> _order = new();
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public void Add(string key, int n = 1)
        {
            if (!_counts.ContainsKey(key)) { _order.Add(key); _counts[key] = 0; }
            _counts[key] += n;
        }

        /// <summary>
        /// Decrements a key; a key that reaches zero (or below) is removed, as the reference's "+counter" drops non-positive
        /// entries. Absent keys are ignored.
        /// </summary>
        public void Subtract(string key, int n = 1)
        {
            if (!_counts.TryGetValue(key, out var count)) return;
            count -= n;
            if (count > 0)
            {
                _counts[key] = count;
                return;
            }
            _counts.Remove(key);
            _order.Remove(key);
        }

        public bool ContainsKey(string key) => _counts.ContainsKey(key);

        public int this[string key] => _counts.TryGetValue(key, out var n) ? n : 0;
        public int Count => _order.Count;
        public IEnumerable<string> Keys => _order;
        public IEnumerable<KeyValuePair<string, int>> Items => _order.Select(k => new KeyValuePair<string, int>(k, _counts[k]));

        /// <summary>Most common first; ties keep insertion order (stable), like Counter.most_common.</summary>
        public IReadOnlyList<KeyValuePair<string, int>> MostCommon(int? take = null)
        {
            var ordered = Items.OrderByDescending(pair => pair.Value).ToList();
            return take is { } n ? ordered.Take(n).ToList() : ordered;
        }

        public void AddAll(BoqCounter other)
        {
            foreach (var pair in other.Items) Add(pair.Key, pair.Value);
        }
    }
}
