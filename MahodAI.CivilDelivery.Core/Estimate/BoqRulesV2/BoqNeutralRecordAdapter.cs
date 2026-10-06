using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MahodAI.CivilDelivery.Shared;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;

namespace MahodAI.CivilDelivery.Estimate.BoqRulesV2
{
    /// <summary>
    /// The Civil Delivery native input path: the neutral quantity records (and measurement-failure findings) of one scan
    /// per source role → the engine's <see cref="BoqInputSet"/>. The same normalisation the reference applied to the plugin
    /// runs: XREF records are not the host's; closed = the handle was measured both as area and length; roles billed by
    /// hatch-layer totals ("hatch" parts) have their HATCH records summed per layer (direct / strictly recovered /
    /// unresolved). Classification geometry comes from the bounded <c>cad_segments</c> / <c>cad_hatch_boundary_centroid</c>
    /// evidence, scaled from raw drawing units to metres with the entity's INSUNITS.
    /// Rules 2.1 ("one physical object once"): the length records of every role read by a length part with object_width_m
    /// or by a line control get their plan chords from the same complete <c>cad_segments</c> vertex chain (arcs arrive
    /// tessellated by the collector; a closed chain — <c>cad_segments_closed</c> — adds its closing chord); a record
    /// without complete segments keeps its measured quantity. Count records of parts with same_point_m get their point
    /// from <c>cad_insert_point</c> (raw drawing units) when present, else the centre of their extents (already metres).
    /// </summary>
    public static class BoqNeutralRecordAdapter
    {
        /// <summary>
        /// The insertion-point evidence key ("x,y", raw drawing units), the same name as QuantityGeometryEvidence.InsertPointKey
        /// that the collector writes; kept here as a literal so this adapter does not depend on that constant's presence.
        /// </summary>
        public const string InsertPointKey = "cad_insert_point";
        public const string WorldInsertPointKey = "cad_insert_point_world_m";
        public const string WorldInsertPointStatusKey = WorldInsertPointKey + "_status";

        /// <summary>Methods of the plugin's strict hatch-boundary recoveries (StrictHatchLinearAreaRecovery / Curve adapter).</summary>
        public static readonly IReadOnlySet<string> RecoveredHatchMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "hatch-linear-boundary-area", "hatch-line-arc-boundary-area", "hatch-exact-retrace-linear-area",
            "hatch-exact-retrace-mixed-line-area",
        };

        public sealed record SourceScan(
            string Role,
            string RunId,
            string DrawingPath,
            string DrawingHash,
            IReadOnlyList<NeutralQuantityRecord> Records,
            IReadOnlyList<DeliveryFinding> Findings,
            DateTime? ScannedAtUtc = null)
        {
            /// <summary>
            /// The scan's hatch_area_failure_diagnostics.json root (optional): hatches whose area failed have no record,
            /// but their captured boundary still places them for crossing classification, as the reference did.
            /// </summary>
            public System.Text.Json.JsonElement? HatchDiagnostics { get; init; }

            /// <summary>b24 (Codex 12:45): the scan's unit authority, identified from its run — required, never defaulted.</summary>
            public required ScanUnitEvidence Units { get; init; }
        }

        /// <summary>Why a scan's unit evidence refuses its records (Hebrew); null when it holds.</summary>
        public static string? ScanUnitRefusal(ScanUnitEvidence units, IReadOnlyList<NeutralQuantityRecord> records)
        {
            var host = records.Where(r => string.IsNullOrEmpty(r.Source.Xref)).ToList();
            var declaredMetres = host.Any(r => r.Measurement.Unit is "מטר" or "מ\"ר");
            return units.RecordsProblem(host.Select(r => (IReadOnlyDictionary<string, string>)r.Measurement.Parameters), declaredMetres);
        }

        /// <summary>
        /// A hatch whose area failed: its boundary centroid in raw drawing units (host, plan normal only), and (v4) the
        /// boundary points the centroid is the mean of, which find the hatch's boundary polyline.
        /// </summary>
        public sealed record FailedHatchCentroid(string Handle, string? Layer, double X, double Y)
        {
            public IReadOnlyList<(double X, double Y)> Points { get; init; } = Array.Empty<(double, double)>();
        }

        /// <summary>
        /// Centroids of failed-area hatches from the failure diagnostics, by the collector's own rule
        /// (QuantitySegmentEvidenceReader.ReadHatchCentroid): the mean of polyline-loop vertices and of line-edge
        /// endpoints. XREF hatches, non-plan normals and loops without line work are skipped. Never a quantity.
        /// </summary>
        public static IReadOnlyList<FailedHatchCentroid> FailedHatchCentroids(System.Text.Json.JsonElement root)
        {
            var result = new List<FailedHatchCentroid>();
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !root.TryGetProperty("Sources", out var sources) || sources.ValueKind != System.Text.Json.JsonValueKind.Array)
                return result;
            foreach (var item in sources.EnumerateArray())
            {
                if (!item.TryGetProperty("Source", out var source) || !item.TryGetProperty("Boundary", out var boundary)) continue;
                var handle = JsonText(source, "source_handle");
                if (string.IsNullOrWhiteSpace(handle) || !string.IsNullOrEmpty(JsonText(source, "xref_path"))) continue;
                if (!(JsonText(boundary, "Header") ?? "").Contains("normal=0,0,1", StringComparison.Ordinal)) continue;
                if (!boundary.TryGetProperty("Loops", out var loops) || loops.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                double sx = 0, sy = 0;
                var n = 0;
                var points = new List<(double X, double Y)>();
                foreach (var loop in loops.EnumerateArray())
                {
                    if (!loop.TryGetProperty("Evidence", out var evidence) || evidence.ValueKind != System.Text.Json.JsonValueKind.Object ||
                        !evidence.TryGetProperty("Items", out var items) || items.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                    foreach (var entry in items.EnumerateArray())
                    {
                        var text = entry.GetString() ?? "";
                        if (text.StartsWith("vertex=", StringComparison.Ordinal))
                        {
                            var end = text.IndexOf(';');
                            if (TryPoint(end < 0 ? text[7..] : text[7..end], out var x, out var y)) { sx += x; sy += y; n++; points.Add((x, y)); }
                        }
                        else if (text.StartsWith("line=", StringComparison.Ordinal))
                        {
                            var parts = text[5..].Split(" -> ");
                            if (parts.Length == 2 && TryPoint(parts[0], out var x1, out var y1) && TryPoint(parts[1], out var x2, out var y2))
                            {
                                sx += x1 + x2; sy += y1 + y2; n += 2;
                                points.Add((x1, y1));
                                points.Add((x2, y2));
                            }
                        }
                    }
                }
                if (n > 0 && double.IsFinite(sx) && double.IsFinite(sy))
                    result.Add(new FailedHatchCentroid(handle!, JsonText(source, "layer"), sx / n, sy / n) { Points = points });
            }
            return result;

            static string? JsonText(System.Text.Json.JsonElement e, string name) =>
                e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
                v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

            static bool TryPoint(string text, out double x, out double y)
            {
                x = y = 0;
                var xy = text.Split(',');
                return xy.Length == 2 &&
                       double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                       double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
                       double.IsFinite(x) && double.IsFinite(y);
            }
        }

        /// <summary>
        /// A failed hatch emits no record, so it has no place in the scan order; the reference reads it in drawing order.
        /// It goes right after this file's geometry with the nearest smaller handle (AutoCAD appends new objects with
        /// increasing handles), else at the end — only the order of crossings in the sheet depends on it, never a quantity.
        /// </summary>
        internal static void InsertInHandleOrder(List<BoqEntityGeometry> geometry, string role, BoqEntityGeometry item)
        {
            if (!long.TryParse(item.Handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) { geometry.Add(item); return; }
            // the nearest predecessor by handle value (not merely the last smaller one: later objects can have old handles)
            var at = -1;
            var best = long.MinValue;
            for (var i = 0; i < geometry.Count; i++)
                if (geometry[i].Src == role && long.TryParse(geometry[i].Handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
                    g < h && g > best)
                    (at, best) = (i, g);
            if (at < 0 || at == geometry.Count - 1) geometry.Add(item);
            else geometry.Insert(at + 1, item);
        }

        /// <summary>
        /// v4.5: the boundary loops of every failed hatch in the failure diagnostics (lines, circular arcs, polyline vertices
        /// with bulges) as rings — the unmeasured-area estimate. A hatch with any other edge is left out (not built).
        /// </summary>
        public static IReadOnlyDictionary<string, List<List<(double X, double Y)>>> FailedHatchLoops(System.Text.Json.JsonElement root)
        {
            var result = new Dictionary<string, List<List<(double X, double Y)>>>(StringComparer.Ordinal);
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !root.TryGetProperty("Sources", out var sources) || sources.ValueKind != System.Text.Json.JsonValueKind.Array)
                return result;
            foreach (var item in sources.EnumerateArray())
            {
                if (!item.TryGetProperty("Source", out var source) || !item.TryGetProperty("Boundary", out var boundary)) continue;
                var handle = source.TryGetProperty("source_handle", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.String ? h.GetString() : null;
                var xref = source.TryGetProperty("xref_path", out var x) && x.ValueKind == System.Text.Json.JsonValueKind.String ? x.GetString() : null;
                if (string.IsNullOrWhiteSpace(handle) || !string.IsNullOrEmpty(xref)) continue;
                if (!boundary.TryGetProperty("Loops", out var loops) || loops.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                List<List<(double X, double Y)>>? rings = new();
                foreach (var loop in loops.EnumerateArray())
                {
                    if (!loop.TryGetProperty("Evidence", out var evidence) || !evidence.TryGetProperty("Items", out var items) ||
                        items.ValueKind != System.Text.Json.JsonValueKind.Array) { rings = null; break; }
                    var texts = items.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                    var ring = texts.Count > 0 && texts.All(t => t.StartsWith("vertex=", StringComparison.Ordinal))
                        ? PolylineRing(texts) : QuantityHatchLoops.RingFromDiagnosticItems(texts);
                    if (ring == null || ring.Count < 3) { rings = null; break; }
                    rings.Add(ring);
                }
                if (rings is { Count: > 0 }) result[handle!] = rings;
            }
            return result;

            static List<(double X, double Y)>? PolylineRing(List<string> texts)
            {
                var vertices = new List<(double X, double Y, double Bulge)>();
                foreach (var t in texts)
                {
                    var fields = t.Split(';').Select(f => f.Trim().Split('=', 2)).Where(f => f.Length == 2)
                        .ToDictionary(f => f[0], f => f[1], StringComparer.Ordinal);
                    if (!fields.TryGetValue("vertex", out var v) || !QuantityGeometryEvidence.TryParsePoint(v, out var px, out var py)) return null;
                    var bulge = fields.TryGetValue("bulge", out var b) && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var bb) ? bb : 0.0;
                    vertices.Add((px, py, bulge));
                }
                var ring = new List<(double X, double Y)>();
                for (var j = 0; j < vertices.Count; j++)
                {
                    var a = vertices[j];
                    var c = vertices[(j + 1) % vertices.Count];
                    if (j == vertices.Count - 1 && Math.Abs(a.X - c.X) < 1e-12 && Math.Abs(a.Y - c.Y) < 1e-12) break;
                    QuantityHatchLoops.Append(ring, QuantityHatchLoops.BulgePoints((a.X, a.Y), (c.X, c.Y), a.Bulge));
                }
                return ring;
            }
        }

        /// <summary>
        /// A hatch's rings as one polygon, as the reference builds it: each ring made valid, the rings combined by symmetric
        /// difference (holes cut out); metres. Null when nothing with area is left.
        /// </summary>
        internal static string? HatchPolygonWkt(IReadOnlyList<List<(double X, double Y)>> rings, double metresPerUnit)
        {
            var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
            // A repaired ring can come back with stray lines / points: only its polygons have area, and OverlayNG refuses
            // mixed-dimension input — keep the polygonal part (same area as the reference's make_valid + XOR).
            Geometry Polygonal(Geometry g) => factory.BuildGeometry(PolygonExtracter.GetPolygons(g));
            try
            {
                Geometry? shape = null;
                foreach (var ring in rings)
                {
                    var coords = ring.Select(p => new Coordinate(p.X * metresPerUnit, p.Y * metresPerUnit)).ToList();
                    if (!coords[0].Equals2D(coords[^1])) coords.Add(coords[0].Copy());
                    if (coords.Count < 4) continue;  // a two-edge sliver (a, b, a) has no area: the reference's make_valid drops it too
                    var fixedRing = Polygonal(GeometryFixer.Fix(factory.CreatePolygon(coords.ToArray())));
                    shape = shape == null ? fixedRing
                        : fixedRing.IsEmpty ? shape
                        : shape.IsEmpty ? fixedRing
                        : Polygonal(OverlayNGRobust.Overlay(shape, fixedRing, SpatialFunction.SymDifference));
                }
                return shape == null || shape.IsEmpty || !(shape.Area > 0) ? null : shape.AsText();
            }
            catch (Exception)
            {
                return null;  // an estimate only: a hatch whose rings cannot be combined is "not built", never a failed export
            }
        }

        public static BoqInputSet Build(BoqRuleset rules, IReadOnlyList<SourceScan> scans)
        {
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(scans);
            var input = new BoqInputSet
            {
                GeometryReader = "קריאה ישירה של השרטוט ב-Civil 3D, בלי לשנות אותו: קטעי הקווים ומרכזי ההצללות " +
                                 $"(יחידות השרטוט מומרות למטר; עד {QuantityGeometryEvidence.MaxVertices} קודקודים לעצם)",
                ObjectGeometryReader = "קריאה ישירה של השרטוט ב-Civil 3D, בלי לשנות אותו: קווים פתוחים וסגורים באורך בתוכנית " +
                                       $"(יחידות השרטוט מומרות למטר; עד {QuantityGeometryEvidence.MaxVertices} קודקודים לעצם); " +
                                       "נקודת בלוק: נקודת ההכנסה או מרכז התיחום",
            };
            var hatchTotalRoles = rules.AllParts.Where(p => p.Kind == "hatch").SelectMany(p => p.Src).ToHashSet(StringComparer.Ordinal);
            // v4.5: the area hatches of the overlap / unmeasured-area estimates (lane "ha_overlap"), with their boundary rings.
            var haLayers = (rules.HaOverlap?.Layers ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
            var haInRead = 0;
            var geometryRoles = new HashSet<string>(StringComparer.Ordinal);
            if (rules.Crosswalk != null) geometryRoles.UnionWith(rules.Crosswalk.Src);
            geometryRoles.UnionWith(rules.AllParts.Where(p => p.MaxLength is > 0).SelectMany(p => p.Src));
            // Rules 2.1: roles whose length records are measured one object once (length parts with a width, line controls).
            var lengthRoles = rules.AllParts.Where(p => p.Kind == "length" && p.ObjectWidth != null).SelectMany(p => p.Src)
                .Concat(rules.Lines.Where(l => l.Control != null).SelectMany(l => l.Control!.Src))
                .ToHashSet(StringComparer.Ordinal);
            geometryRoles.UnionWith(lengthRoles);
            // Count records that the same-block-same-point rule reads (the reference's count layers: block layers, else layers).
            var countParts = rules.AllParts.Where(p => p.Kind is ("count" or "count_x") && p.SamePoint != null).ToList();
            var countRoles = countParts.SelectMany(p => p.Src).ToHashSet(StringComparer.Ordinal);
            var countLayers = countParts.SelectMany(p => p.BlockLayers.Count > 0 ? p.BlockLayers : p.Layers).ToHashSet(StringComparer.Ordinal);

            foreach (var scan in scans)
            {
                var role = scan.Role;
                foreach (var scope in scan.Findings.Where(f => f.Code == EstimateSourceSelectionPolicy.ExcludedScopeCode))
                    input.Warnings.Add($"{role} [{scan.RunId}]: {scope.Message ?? scope.Title} " +
                        "בחירת המקורות קשורה לשרטוט זה בלבד; סריקות תפקידים אחרות והקורידורים הם מקורות נפרדים.");
                var host = scan.Records.Where(r => string.IsNullOrEmpty(r.Source.Xref)).ToList();
                var kindsByHandle = host.GroupBy(r => r.Source.Handle, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Select(r => r.Measurement.Kind).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
                var totals = new Dictionary<string, (int Direct, decimal DirectArea, int Recovered, decimal RecoveredArea, List<string> Unresolved)>(StringComparer.Ordinal);
                var totalOrder = new List<string>();
                var seenGeometry = new HashSet<string>(StringComparer.Ordinal);
                var unitWarnings = 0;
                var objectUnitWarnings = 0;
                var countPointWarnings = 0;
                var declaredMetres = host.Any(r => r.Measurement.Unit is "מטר" or "מ\"ר");
                // b24 (Codex 12:45): every host factor comes from the scan's verified unit evidence.
                var unitEvidence = scan.Units;
                // A scan whose unit evidence does not hold gives no plan geometry at all — not just for its bad records.
                var unitProblem = ScanUnitRefusal(unitEvidence, scan.Records);
                Func<IReadOnlyDictionary<string, string>, double?> factorOf = unitProblem != null
                    ? _ => null
                    : p => unitEvidence.RecordMetresPerUnit(p, declaredMetres);
                if (unitProblem != null)
                    input.Warnings.Add($"{role}: {unitProblem} (גאומטריה במטרים לא נבנתה מהסריקה הזו)");
                var lengthByHandle = host.Where(r => r.Measurement.Kind == "length")
                    .GroupBy(r => r.Source.Handle, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Max(r => r.Measurement.RawValue), StringComparer.Ordinal);

                // v4.5: symbols inside associative-array items (collector "cad_array_handle"). Only the ruleset's member
                // block is a record (reference: the BIKE symbols); without "array_members" none is. The array inserts
                // ("cad_array") become BoqInputSet.Arrays below.
                var memberBlock = rules.ArrayMembers?.MemberBlock;
                var members = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var record in host)
                {
                    var s = record.Source;
                    var m = record.Measurement;
                    var layer = s.Layer ?? "";
                    var etype = (s.EntityType ?? "").ToUpperInvariant();
                    if (m.Parameters.TryGetValue("cad_array_handle", out var arrayHandle))
                    {
                        m.Parameters.TryGetValue("cad_block_name_effective", out var memberEffective);
                        m.Parameters.TryGetValue("block_name", out var memberRaw);
                        var name = !string.IsNullOrWhiteSpace(memberEffective) ? memberEffective! : memberRaw ?? "";
                        if (memberBlock == null || m.Kind != "count" || !string.Equals(name, memberBlock, StringComparison.Ordinal)) continue;
                        if (!members.TryGetValue(arrayHandle, out var list)) members[arrayHandle] = list = new List<string>();
                        list.Add(s.Handle);
                    }
                    if (hatchTotalRoles.Contains(role) && etype == "HATCH" && m.Kind == "area")
                    {
                        if (!totals.TryGetValue(layer, out var t)) { totalOrder.Add(layer); t = (0, 0m, 0, 0m, new List<string>()); }
                        // An unknown recovery must never silently become direct native Area.
                        if (m.Method != "hatch-area" && !RecoveredHatchMethods.Contains(m.Method))
                        {
                            if (!t.Unresolved.Contains(s.Handle)) t.Unresolved.Add(s.Handle);
                            totals[layer] = t;
                            if (haLayers.Contains(layer))
                            {
                                haInRead++;
                                input.HaHatches.Add(new BoqHaHatch(s.Handle, layer, "unresolved", null));
                            }
                            input.Warnings.Add("שיטת שטח HATCH אינה מוכרת; השטח נשאר חסר: " +
                                role + "/" + s.Handle + " — " + m.Method);
                            continue;
                        }
                        var area = (decimal)m.RawValue;
                        totals[layer] = RecoveredHatchMethods.Contains(m.Method)
                            ? (t.Direct, t.DirectArea, t.Recovered + 1, t.RecoveredArea + area, t.Unresolved)
                            : (t.Direct + 1, t.DirectArea + area, t.Recovered, t.RecoveredArea, t.Unresolved);
                        if (haLayers.Contains(layer))
                        {
                            haInRead++;
                            var recovered = RecoveredHatchMethods.Contains(m.Method);
                            input.HaHatches.Add(new BoqHaHatch(s.Handle, layer, recovered ? "strict_boundary_recovered" : "direct",
                                recovered ? null : m.RawValue));
                            // rings of a host model-space hatch only (a transformed one would need its block's transform)
                            if (!m.Method.Contains("transform", StringComparison.Ordinal) &&
                                m.Parameters.TryGetValue(QuantityGeometryEvidence.HatchLoopsStatusKey, out var loopStatus) &&
                                loopStatus == QuantityGeometryEvidence.StatusComplete &&
                                m.Parameters.TryGetValue(QuantityGeometryEvidence.HatchLoopsKey, out var loopText) &&
                                QuantityHatchLoops.TryParse(loopText, out var rings) &&
                                factorOf(m.Parameters) is { } hk &&
                                HatchPolygonWkt(rings, hk) is { } wkt)
                                input.HaHatchPolygons.Add(new BoqHaPolygon(s.Handle, layer, wkt));
                        }
                        continue;
                    }
                    // v4.5: a crossing hatch whose area Civil recovered from its straight boundary is that hatch's strict
                    // recovery (reference: golden sm_strict_recoveries — the same areas), not a record of its own; its
                    // boundary still places it among the crossings below.
                    if (geometryRoles.Contains(role) && etype == "HATCH" && m.Kind == "area" && RecoveredHatchMethods.Contains(m.Method))
                    {
                        input.StrictHatchRecoveries[s.Handle] = m.RawValue;
                        if (unitProblem == null && seenGeometry.Add(s.Handle))
                        {
                            if (TryGeometry(role, record, layer, etype, lengthByHandle, factorOf, out var recovered, out var recoveredWarning))
                                input.Geometry.Add(recovered!);
                            if (recoveredWarning != null && unitWarnings++ == 0) input.Warnings.Add(recoveredWarning + " (ההודעה מוצגת פעם אחת לקובץ)");
                        }
                        continue;
                    }
                    var kinds = kindsByHandle[s.Handle];
                    m.Parameters.TryGetValue("cad_block_name_effective", out var effective);
                    m.Parameters.TryGetValue("block_name", out var raw);
                    var bbox = m.GeometryEvidence is { Length: >= 4 } g ? g.Take(4).ToList() : null;
                    input.Records.Add(new BoqRecord(role, layer, etype, m.Kind, m.RawValue, s.Handle,
                        kinds.Contains("area") && kinds.Contains("length"),
                        !string.IsNullOrWhiteSpace(effective) ? effective! : raw ?? "", bbox));

                    // A scan whose unit evidence does not hold contributes no classification geometry either (Codex 13:21).
                    if (unitProblem == null && geometryRoles.Contains(role) && seenGeometry.Add(s.Handle))
                    {
                        if (TryGeometry(role, record, layer, etype, lengthByHandle, factorOf, out var geometry, out var warning))
                            input.Geometry.Add(geometry!);
                        if (warning != null && unitWarnings++ == 0) input.Warnings.Add(warning + " (ההודעה מוצגת פעם אחת לקובץ)");
                    }
                    if (m.Kind == "length" && lengthRoles.Contains(role) && !input.LengthGeometry.ContainsKey((role, s.Handle)))
                    {
                        if (TryLengthGeometry(role, record, layer, factorOf, out var chords, out var warning))
                            input.LengthGeometry[(role, s.Handle)] = chords!;
                        if (warning != null && objectUnitWarnings++ == 0) input.Warnings.Add(warning + " (ההודעה מוצגת פעם אחת לקובץ)");
                    }
                    if (m.Kind == "count" && countRoles.Contains(role) && countLayers.Contains(layer) &&
                        !input.CountPoints.ContainsKey((role, s.Handle)))
                    {
                        if (TryCountPoint(role, record, bbox, factorOf, out var point, out var warning))
                            input.CountPoints[(role, s.Handle)] = point;
                        if (warning != null && countPointWarnings++ == 0) input.Warnings.Add(warning + " (ההודעה מוצגת פעם אחת לקובץ)");
                        // v4.1: rotation of a host model-space block (back-to-back pairs in the same-point rule).
                        if (m.Parameters.TryGetValue("cad_rotation_rad", out var rotationText) &&
                            double.TryParse(rotationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var rotation) && double.IsFinite(rotation))
                            input.CountRotations[(role, s.Handle)] = rotation;
                        // Rules 2.8 (BOQ-N1): the rest of the host INSERT's transform (scale in metres per block unit) and its
                        // definition signature, for approved physical footprints; missing evidence leaves the transform unproven.
                        var blockName = !string.IsNullOrWhiteSpace(effective) ? effective! : raw ?? "";
                        if (input.CountPoints.TryGetValue((role, s.Handle), out var placedAt) && input.CountRotations.TryGetValue((role, s.Handle), out var turned) &&
                            TryTriple(m.Parameters, QuantityGeometryEvidence.InsertScaleKey, out var scale) &&
                            TryTriple(m.Parameters, QuantityGeometryEvidence.InsertNormalKey, out var normal) &&
                            factorOf(m.Parameters) is { } perUnit)
                            input.CountTransforms[(role, s.Handle)] = new BoqInsertTransform(placedAt.X, placedAt.Y, turned, scale.Select(v => v * perUnit).ToArray(), normal);
                        if (blockName.Length > 0 && !input.BlockSignatures.ContainsKey((role, blockName)) &&
                            m.Parameters.TryGetValue(QuantityGeometryEvidence.InsertBlockSignatureKey, out var signatureText))
                            input.BlockSignatures[(role, blockName)] = ParseSignature(signatureText);
                    }
                }

                // v4.5: the associative arrays of this file — the array insert, its first item's world point, the member
                // symbols, and the polyline it follows (a polyline whose first vertex is the first item's point, to the cm).
                foreach (var record in host.Where(r => r.Measurement.Kind == "count" &&
                                                       r.Measurement.Parameters.TryGetValue("cad_array", out var a) && a == "associative"))
                {
                    var p = record.Measurement.Parameters;
                    p.TryGetValue("cad_block_name_effective", out var effective);
                    p.TryGetValue("block_name", out var raw);
                    BoqPoint? first = p.TryGetValue("cad_array_first_item_world_m", out var firstText) &&
                                      QuantityGeometryEvidence.TryParsePoint(firstText, out var fx, out var fy) ? new BoqPoint(fx, fy) : null;
                    string? pathLine = null, pathLayer = null;
                    if (first is { } f0)
                    {
                        var key = (Math.Round(f0.X, 2), Math.Round(f0.Y, 2));
                        foreach (var candidate in host)
                        {
                            var et = (candidate.Source.EntityType ?? "").ToUpperInvariant();
                            if (candidate.Measurement.Kind != "length" || et is not ("POLYLINE" or "POLYLINE2D")) continue;
                            if (!input.LengthGeometry.TryGetValue((role, candidate.Source.Handle), out var geo) || geo.Segments.Count == 0) continue;
                            var start = geo.Segments[0].A;
                            if ((Math.Round(start.X, 2), Math.Round(start.Y, 2)) == key)
                                (pathLine, pathLayer) = (candidate.Source.Handle, candidate.Source.Layer);  // the last one wins, as the reference dict
                        }
                    }
                    members.TryGetValue(record.Source.Handle, out var memberHandles);
                    input.Arrays.Add(new BoqArrayInsert(role, record.Source.Handle)
                    {
                        Block = !string.IsNullOrWhiteSpace(effective) ? effective! : raw ?? "",
                        FirstItemPoint = first,
                        PathLine = pathLine,
                        PathLayer = pathLayer,
                        MemberHandles = (memberHandles ?? new List<string>()).OrderBy(h => h, StringComparer.Ordinal).ToList(),
                    });
                }

                // Failed-area hatches emit no record, but their captured boundary still places them for crossing
                // classification (the reference read every hatch's boundary). Scaled like the scan's own records.
                if (unitProblem == null && geometryRoles.Contains(role) && scan.HatchDiagnostics is { } diagnostics)
                {
                    if (unitProblem == null && unitEvidence.ScanMetresPerUnit(
                            host.Select(r => (IReadOnlyDictionary<string, string>)r.Measurement.Parameters), declaredMetres) is { } k)
                        foreach (var hatch in FailedHatchCentroids(diagnostics))
                            if (seenGeometry.Add(hatch.Handle))
                                InsertInHandleOrder(input.Geometry, role, new BoqEntityGeometry(role, hatch.Handle, hatch.Layer, true,
                                    Array.Empty<BoqSegment>(), new BoqPoint(hatch.X * k, hatch.Y * k))
                                {
                                    // v4: the boundary points find the closed polyline drawn with the hatch's vertices,
                                    // whose area stands in for the failed hatch area (live SM 8AB16 / 8AB18 / 204E66).
                                    HatchBoundaryPoints = hatch.Points.Count == 0 ? null
                                        : hatch.Points.Select(p => new BoqPoint(p.X * k, p.Y * k)).ToList(),
                                });
                }

                // Unmeasured host objects: measurement failures that emitted no record (a failed hatch area, a proven
                // zero-length line). Failures of emitted records (evidence only) are not unmeasured objects.
                var emitted = host.Select(r => r.Source.Handle).ToHashSet(StringComparer.Ordinal);
                var reported = new HashSet<string>(StringComparer.Ordinal);
                IReadOnlyDictionary<string, List<List<(double X, double Y)>>>? failedLoops = null;
                foreach (var finding in scan.Findings.Where(f => f.Code == EstimateFindingCodes.MeasurementFailed && f.AffectedRecordIds.Count == 0))
                {
                    foreach (var source in finding.SourceRefs)
                    {
                        if (!string.IsNullOrEmpty(source.XrefPath) || string.IsNullOrWhiteSpace(source.SourceHandle)) continue;
                        var handle = source.SourceHandle!;
                        if (emitted.Contains(handle) || !reported.Add(handle)) continue;
                        var layer = source.Layer ?? "";
                        var isHatch = string.Equals(source.EntityType, "Hatch", StringComparison.OrdinalIgnoreCase);
                        if (isHatch && hatchTotalRoles.Contains(role))
                        {
                            if (!totals.TryGetValue(layer, out var t)) { totalOrder.Add(layer); t = (0, 0m, 0, 0m, new List<string>()); }
                            t.Unresolved.Add(handle);
                            totals[layer] = t;
                            if (haLayers.Contains(layer))
                            {
                                haInRead++;
                                input.HaHatches.Add(new BoqHaHatch(handle, layer, "unresolved", null));
                                failedLoops ??= scan.HatchDiagnostics is { } fd ? FailedHatchLoops(fd) : new Dictionary<string, List<List<(double X, double Y)>>>();
                                if (failedLoops.TryGetValue(handle, out var failedRings) &&
                                    unitProblem == null && unitEvidence.ScanMetresPerUnit(
                                        host.Select(r => (IReadOnlyDictionary<string, string>)r.Measurement.Parameters), declaredMetres) is { } fk &&
                                    HatchPolygonWkt(failedRings, fk) is { } failedWkt)
                                    input.HaHatchPolygons.Add(new BoqHaPolygon(handle, layer, failedWkt));
                            }
                            continue;
                        }
                        // v4: a proven zero-length line (start = end) is not missing — nothing to measure and the quantity
                        // does not change; the workbook lists it once in 'לא נכלל' (reference: GM 10A971).
                        if (!isHatch && (finding.Message ?? "").Contains("proof=" + ZeroLengthGeometryProof.Contract, StringComparison.Ordinal))
                        {
                            input.ZeroLength.Add(new BoqZeroLength(role, source.Layer, handle));
                            continue;
                        }
                        input.Unmeasured.Add(new BoqUnmeasured(role, source.Layer, handle,
                            isHatch ? "הצללה ששטחה לא הוחזר וגם לא חושב מהגבול" : UnmeasuredWhat(source.MeasurementMethod),
                            "לפתוח את המקור ב-Civil: LIST / Properties, לתקן ולסרוק מחדש"));
                    }
                }

                foreach (var layer in totalOrder)
                {
                    var t = totals[layer];
                    input.HatchLayers.Add(new BoqHatchLayerTotal(role, layer, t.Direct, t.DirectArea, t.Recovered, t.RecoveredArea,
                        t.Unresolved.Count, t.Unresolved));
                }
                input.SourceRows.Add(new BoqSourceRow(role,
                    $"{Path.GetFileName(scan.DrawingPath)} — {scan.RunId} — SHA-256 {scan.DrawingHash}" +
                    (scan.ScannedAtUtc is { } at ? " — " + at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : ""))
                {
                    Role = role,
                    FileName = Path.GetFileName(scan.DrawingPath),
                    RunId = string.IsNullOrWhiteSpace(scan.RunId) ? null : scan.RunId,
                    DrawingSha256 = string.IsNullOrWhiteSpace(scan.DrawingHash) ? null : scan.DrawingHash,
                    MeasuredAtUtc = scan.ScannedAtUtc,
                });
            }
            if (haInRead > 0) input.HaHatchesInRead = haInRead;
            // Every HA polygon above is built here from Civil 3D's own boundary loops (scan records and failure
            // diagnostics, arcs cut by QuantityHatchLoops); the receipt is bound to this exact input (writer 09 note).
            if (input.HaHatchPolygons.Count > 0)
                input.SourceRows.Add(new BoqSourceRow("שיטת קריאת גבולות הצללות HA",
                    $"גבולות ההצללות כפי ש-Civil 3D החזיר אותן (קשתות מחולקות ל-{QuantityHatchLoops.PiecesPerRadian:0} קטעים לרדיאן)")
                {
                    DrawingSha256 = BoqRulesWorkbookWriter.ContextEvidenceFingerprint(input),
                });
            return input;
        }

        /// <summary>
        /// Metres per raw drawing unit for the INSUNITS names the plugin records (UnitsValue.ToString()); null when not a
        /// length unit. The same factors as PhysicalDrawingUnitPolicy.ExplicitUnit, so every unit the quantity scan accepts
        /// also gives plan geometry (review 30/09: Yards, Dekameters, Microns… fell back to "no geometry").
        /// </summary>
        public static double? MetresPerUnit(string? insunits) => insunits switch
        {
            "Inches" => 0.0254,
            "Feet" => 0.3048,
            "Miles" => 1609.344,
            "Millimeters" => 0.001,
            "Centimeters" => 0.01,
            "Meters" => 1.0,
            "Kilometers" => 1000.0,
            "MicroInches" => 0.0000000254,
            "Mils" => 0.0000254,
            "Yards" => 0.9144,
            "Angstroms" => 1e-10,
            "Nanometers" => 1e-9,
            "Microns" => 1e-6,
            "Decimeters" => 0.1,
            "Dekameters" => 10.0,
            "Hectometers" => 100.0,
            "Gigameters" => 1e9,
            "SurveyFeet" or "USSurveyFeet" => 1200.0 / 3937.0,
            "USSurveyInch" => 100.0 / 3937.0,
            "USSurveyYard" => 3600.0 / 3937.0,
            "USSurveyMile" => 6_336_000.0 / 3937.0,
            _ => null,
        };

        /// <summary>
        /// Metres per raw unit for one scan: the INSUNITS factor, or 1.0 for a Unitless ("Undefined") host whose records are
        /// labelled in metres — that label only comes from an approved "drawn in metres" declaration, whose factor is 1.0
        /// (review 30/09: such a host lost its plan geometry, so pipes ×5 and stone faces ×2 came back).
        /// </summary>
        internal static double? ScanMetresPerUnit(string? insunits, bool declaredMetres) =>
            MetresPerUnit(insunits) ?? (declaredMetres && insunits == "Undefined" ? 1.0 : null);

        /// <summary>Hebrew reason when a scan's records carry raw drawing units (no physical unit), else null.</summary>
        public static string? UnitRefusal(IReadOnlyList<NeutralQuantityRecord> records) =>
            records.Any(r => string.IsNullOrEmpty(r.Source.Xref) &&
                             (r.Measurement.Unit ?? "").StartsWith("יחידת שרטוט", StringComparison.Ordinal))
                ? "יחידות השרטוט אינן מאושרות (אורכים ושטחים ביחידות שרטוט ולא במטרים) — יש להגדיר יחידות פיזיות ולסרוק מחדש"
                : null;

        /// <summary>
        /// Hebrew reason when a scan was written before the plan measuring chain existed (Civil Delivery before 1.3.4): its
        /// cad_segments chain is the old tessellation, so one-object-once lengths and crossing roles would be wrong.
        /// </summary>
        public static string? LegacyEvidenceRefusal(IReadOnlyList<NeutralQuantityRecord> records) =>
            records.Any(r => string.IsNullOrEmpty(r.Source.Xref) && r.Measurement.Kind == "length" &&
                             r.Measurement.Parameters.TryGetValue(QuantityGeometryEvidence.SegmentsStatusKey, out var status) &&
                             status == QuantityGeometryEvidence.StatusComplete &&
                             !r.Measurement.Parameters.ContainsKey(QuantityGeometryEvidence.PlanChainStatusKey))
                ? "הסריקה נעשתה בגרסה קודמת של הכלי (לפני 1.3.4), שמדדה קשתות אחרת — יש לסרוק מחדש"
                : null;

        private static bool TryGeometry(string role, NeutralQuantityRecord record, string layer, string etype,
            IReadOnlyDictionary<string, double> lengthByHandle, Func<IReadOnlyDictionary<string, string>, double?> factorOf,
            out BoqEntityGeometry? geometry, out string? warning)
        {
            geometry = null;
            warning = null;
            var p = record.Measurement.Parameters;
            var handle = record.Source.Handle;
            p.TryGetValue("cad_entity_database_insunits", out var units);
            var scale = factorOf(p);
            if (p.ContainsKey(QuantityGeometryEvidence.HatchCentroidKey) || etype == "HATCH")
            {
                if (!p.TryGetValue(QuantityGeometryEvidence.HatchCentroidKey, out var c) ||
                    !QuantityGeometryEvidence.TryParsePoint(c, out var x, out var y)) return false;
                if (scale == null) { warning = UnitWarning(role, handle, units); scale = 1.0; }
                var hk = scale.Value;
                // v4: the boundary points (collector 1.3.10+) find the hatch's boundary polyline; absent in older scans.
                IReadOnlyList<BoqPoint>? boundaryPoints = null;
                if (p.TryGetValue(QuantityGeometryEvidence.HatchBoundaryPointsKey, out var pointsText) &&
                    QuantityGeometryEvidence.TryParseVertices(pointsText, out var hatchPoints))
                    boundaryPoints = hatchPoints.Select(v => new BoqPoint(v.X * hk, v.Y * hk)).ToList();
                geometry = new BoqEntityGeometry(role, handle, layer, true, Array.Empty<BoqSegment>(),
                    new BoqPoint(x * hk, y * hk)) { HatchBoundaryPoints = boundaryPoints };
                return true;
            }
            // v4: the crossing's dashed line is told by the entity's own linetype scale (DASHED ×0.5).
            double? linetypeScale = p.TryGetValue(QuantityGeometryEvidence.LinetypeScaleKey, out var ltsText) &&
                                    double.TryParse(ltsText, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out var lts) &&
                                    double.IsFinite(lts) ? lts : null;
            // Classification (crossings, over-long stop lines) reads line work only, as the reference did: arcs,
            // circles and 3D polylines now carry chains for the one-object length rule, not for classification.
            if (etype is not ("LINE" or "POLYLINE" or "LWPOLYLINE" or "POLYLINE2D")) return false;
            if (!p.TryGetValue(QuantityGeometryEvidence.SegmentsStatusKey, out var status)) return false;
            lengthByHandle.TryGetValue(handle, out var nativeLength);
            if (status != QuantityGeometryEvidence.StatusComplete)
            {
                // Bounded out: no chords, but the native length still decides "longer than the part allows".
                geometry = new BoqEntityGeometry(role, handle, layer, false, Array.Empty<BoqSegment>(), null,
                    SegmentsComplete: false, FallbackLength: nativeLength) { LinetypeScale = linetypeScale };
                return true;
            }
            if (!p.TryGetValue(QuantityGeometryEvidence.SegmentsKey, out var text) ||
                !QuantityGeometryEvidence.TryParseVertices(text, out var vertices)) return false;
            if (scale == null) { warning = UnitWarning(role, handle, units); scale = 1.0; }
            var k = scale.Value;
            var segments = new List<BoqSegment>(Math.Max(0, vertices.Count - 1));
            // Consecutive vertices only, like the reference (a closed polyline's closing chord is not added).
            for (var i = 0; i + 1 < vertices.Count; i++)
                segments.Add(new BoqSegment(new BoqPoint(vertices[i].X * k, vertices[i].Y * k),
                    new BoqPoint(vertices[i + 1].X * k, vertices[i + 1].Y * k)));
            geometry = new BoqEntityGeometry(role, handle, layer, false, segments, null)
            {
                LinetypeScale = linetypeScale,
                // v4: a polyline's stored vertices (never a LINE's) — a closed one with a hatch's vertices is its boundary.
                Vertices = etype == "LINE" || segments.Count == 0 ? null : vertices.Select(v => new BoqPoint(v.X * k, v.Y * k)).ToList(),
            };
            return true;
        }

        /// <summary>
        /// What is missing for an object whose measurement failed, in Hebrew only (1.3.9 printed "(line-length)"): the
        /// measure the failed method would have given.
        /// </summary>
        internal static string UnmeasuredWhat(string? method)
        {
            var m = (method ?? "").ToLowerInvariant();
            var measure = m.Contains("area") ? "שטח"
                : m.Contains("length") || m.Contains("perimeter") || m.Contains("circumference") ? "אורך"
                : m.Contains("count") ? "ספירה"
                : null;
            return measure == null ? "עצם שלא נמדד" : $"עצם שלא נמדד ({measure})";
        }

        private static string UnitWarning(string role, string handle, string? units) =>
            $"{role}:{handle}: יחידות השרטוט '{units ?? "?"}' אינן יחידת אורך מוכרת — הגאומטריה לסיווג נקראה כמטרים.";

        /// <summary>
        /// Plan chords (metres) of one length record for the one-object-once rule: the complete cad_segments vertex chain,
        /// closed chains with their closing chord. Units are never guessed here — this geometry sets a quantity: without a
        /// known INSUNITS the record keeps its measured length.
        /// </summary>
        private static bool TryLengthGeometry(string role, NeutralQuantityRecord record, string layer,
            Func<IReadOnlyDictionary<string, string>, double?> factorOf,
            out BoqEntityGeometry? geometry, out string? warning)
        {
            geometry = null;
            warning = null;
            var p = record.Measurement.Parameters;
            // The measuring chain (arcs tessellated). A scan written before it existed is refused upstream
            // (LegacyEvidenceRefusal); its raw chain is never used as a measuring chain.
            var statusKey = QuantityGeometryEvidence.PlanChainStatusKey;
            var chainKey = QuantityGeometryEvidence.PlanChainKey;
            var closedKey = QuantityGeometryEvidence.PlanChainClosedKey;
            if (!p.TryGetValue(statusKey, out var status) || status != QuantityGeometryEvidence.StatusComplete ||
                !p.TryGetValue(chainKey, out var text) ||
                !QuantityGeometryEvidence.TryParseVertices(text, out var vertices)) return false;
            p.TryGetValue("cad_entity_database_insunits", out var units);
            if (factorOf(p) is not { } k)
            {
                warning = $"{role}:{record.Source.Handle}: יחידות השרטוט '{units ?? "?"}' אינן יחידת אורך מוכרת — " +
                          "האורך נלקח מהמדידה המקורית (בלי מיזוג קווים מקבילים של אותו עצם).";
                return false;
            }
            var closed = p.TryGetValue(closedKey, out var closedText) &&
                         string.Equals(closedText, "true", StringComparison.OrdinalIgnoreCase);
            var scaled = new List<(double X, double Y)>(vertices.Count);
            foreach (var (x, y) in vertices) scaled.Add((x * k, y * k));
            geometry = new BoqEntityGeometry(role, record.Source.Handle, layer, false, BoqObjectMeasure.VertexChainChords(scaled, closed), null);
            return true;
        }

        /// <summary>Three finite round-trip numbers "a,b,c" (a scale or a normal of the scan).</summary>
        private static bool TryTriple(IReadOnlyDictionary<string, string> p, string key, out double[] values)
        {
            values = Array.Empty<double>();
            if (!p.TryGetValue(key, out var text)) return false;
            var parts = text.Split(',');
            var parsed = new double[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]) || !double.IsFinite(parsed[i]))
                    return false;
            if (parsed.Length != 3) return false;
            values = parsed;
            return true;
        }

        /// <summary>The scan's block definition signature (JSON); null when it is malformed — then it matches no approved footprint.</summary>
        internal static BoqDefinitionSignature? ParseSignature(string json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                double[]? Numbers(string name) =>
                    root.TryGetProperty(name, out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Array &&
                    a.EnumerateArray().All(x => x.ValueKind == System.Text.Json.JsonValueKind.Number)
                        ? a.EnumerateArray().Select(x => x.GetDouble()).ToArray() : null;
                if (!root.TryGetProperty("dxf_counts", out var counts) || counts.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    Numbers("envelope") is not { Length: 4 } envelope || Numbers("base_point") is not { Length: 3 } basePoint ||
                    !root.TryGetProperty("units_code", out var units) || units.ValueKind != System.Text.Json.JsonValueKind.Number)
                    return null;
                return new BoqDefinitionSignature(counts.EnumerateObject().ToDictionary(c => c.Name, c => c.Value.GetInt32(), StringComparer.Ordinal),
                    envelope, basePoint, units.GetInt32(),
                    root.TryGetProperty("geometry_digest", out var digest) && digest.ValueKind == System.Text.Json.JsonValueKind.String ? digest.GetString() : null);
            }
            catch (System.Text.Json.JsonException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (FormatException) { return null; }
        }

        /// <summary>
        /// The point of one count record for the same-block-same-point rule: cad_insert_point (raw drawing units → metres)
        /// when present and the units are known, else the centre of the record's extents (already metres).
        /// </summary>
        private static bool TryCountPoint(string role, NeutralQuantityRecord record, IReadOnlyList<double>? bbox,
            Func<IReadOnlyDictionary<string, string>, double?> factorOf,
            out BoqPoint point, out string? warning)
        {
            point = default;
            warning = null;
            var p = record.Measurement.Parameters;
            if (p.ContainsKey(WorldInsertPointKey) || p.ContainsKey(WorldInsertPointStatusKey))
            {
                if (p.TryGetValue(WorldInsertPointStatusKey, out var status) && status == "complete" &&
                    p.TryGetValue(WorldInsertPointKey, out var worldText) &&
                    QuantityGeometryEvidence.TryParsePoint(worldText, out var worldX, out var worldY))
                {
                    // Already host-world metres: do not apply the source INSUNITS
                    // scale or the ancestor transform again.
                    point = new BoqPoint(worldX, worldY);
                    return true;
                }
                warning = $"{role}:{record.Source.Handle}: נקודת עולם של הבלוק אינה מאומתת — נדרשת סריקה חדשה; הספירה נשמרה ללא איחוד לפי נקודה.";
                return false;
            }
            if (record.Measurement.Method.Contains("xref-transform", StringComparison.Ordinal) ||
                record.Measurement.Method.Contains("block-transform", StringComparison.Ordinal))
            {
                warning = $"{role}:{record.Source.Handle}: סריקה ישנה עם נקודת בלוק מקומית — נדרשת סריקה חדשה; הספירה נשמרה ללא איחוד לפי נקודה.";
                return false;
            }
            if (p.TryGetValue(InsertPointKey, out var text) && QuantityGeometryEvidence.TryParsePoint(text, out var x, out var y))
            {
                p.TryGetValue("cad_entity_database_insunits", out var units);
                if (factorOf(p) is { } k)
                {
                    point = new BoqPoint(x * k, y * k);
                    return true;
                }
                warning = $"{role}:{record.Source.Handle}: יחידות השרטוט '{units ?? "?"}' אינן יחידת אורך מוכרת — " +
                          "נקודת הבלוק נלקחה ממרכז התיחום.";
            }
            if (bbox is { Count: >= 4 } b && double.IsFinite(b[0]) && double.IsFinite(b[1]) && double.IsFinite(b[2]) && double.IsFinite(b[3]))
            {
                point = new BoqPoint((b[0] + b[2]) / 2, (b[1] + b[3]) / 2);
                return true;
            }
            return false;
        }
    }
}
