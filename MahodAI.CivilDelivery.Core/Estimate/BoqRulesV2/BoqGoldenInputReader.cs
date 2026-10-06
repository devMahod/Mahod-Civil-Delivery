using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Estimate.BoqRulesV2
{
    /// <summary>
    /// Reads the frozen acceptance inputs (schema mahod-boq-golden-inputs/1, produced by the reference export_golden.py):
    /// normalised records, the SM classification geometry (segments by handle, hatch boundary points), the HA hatch layer
    /// totals, the SM strict hatch recoveries, the known unmeasured GM object and (rules 2.1, export_golden v7)
    /// object_geometry: the plan chords of every length record and the insertion point of every count record. The schema
    /// names its roles ("sm_*", "ha_*", "gm_*"); layer names come only from the file and the ruleset. v4 (30.09.2026):
    /// sm_geometry.linetype_scale_by_handle (dashed crossing lines), the hatch boundary points of every crossing hatch and
    /// the polyline vertices (segment endpoints of non-LINE records) that find a hatch's boundary polyline; the zero-length
    /// GM line is a zero-length object (information), not an unmeasured one.
    /// </summary>
    public static class BoqGoldenInputReader
    {
        public const string Schema = "mahod-boq-golden-inputs/1";

        /// <param name="rules">The ruleset; its crossing hatch layer names the hatches the export wrote without a record.</param>
        /// <param name="unmeasuredLayerHints">Layer of a known unmeasured handle, when the frozen file does not carry it.</param>
        public static BoqInputSet Read(string json, BoqRuleset rules, IReadOnlyDictionary<string, string>? unmeasuredLayerHints = null)
        {
            ArgumentNullException.ThrowIfNull(json);
            ArgumentNullException.ThrowIfNull(rules);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!string.Equals(BoqRuleset.Str(root, "schema"), Schema, StringComparison.Ordinal))
                throw new InvalidDataException("Not a " + Schema + " file.");
            var input = new BoqInputSet { GeometryReader = "golden inputs (" + Schema + ")" };

            foreach (var r in root.GetProperty("records").EnumerateArray())
            {
                IReadOnlyList<double>? bbox = null;
                if (r.TryGetProperty("bbox", out var b) && b.ValueKind == JsonValueKind.Array)
                    bbox = b.EnumerateArray().Select(v => v.GetDouble()).ToList();
                input.Records.Add(new BoqRecord(
                    BoqRuleset.Str(r, "src") ?? "",
                    BoqRuleset.Str(r, "layer") ?? "",
                    BoqRuleset.Str(r, "etype") ?? "",
                    BoqRuleset.Str(r, "kind") ?? "",
                    r.TryGetProperty("qty", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetDouble() : 0.0,
                    BoqRuleset.Str(r, "handle") ?? "",
                    BoqRuleset.Bool(r, "closed"),
                    BoqRuleset.Str(r, "block") ?? "",
                    bbox));
            }

            // Geometry is SM's (schema "sm_geometry"). Its layer is the SM record's layer for the same handle; a hatch
            // exported without a record (its area failed natively) was exported only from the crossing hatch layer.
            const string geometrySrc = "SM";
            var layerByHandle = new Dictionary<string, string>(StringComparer.Ordinal);
            var etypeByHandle = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in input.Records.Where(r => r.Src == geometrySrc))
            {
                layerByHandle.TryAdd(r.Handle, r.Layer);
                etypeByHandle.TryAdd(r.Handle, r.Etype);
            }
            var hatchLayer = rules.Crosswalk?.HatchLayers.FirstOrDefault();
            if (root.TryGetProperty("sm_geometry", out var geo) && geo.ValueKind == JsonValueKind.Object)
            {
                var reader = BoqRuleset.Str(geo, "reader");
                var sha = BoqRuleset.Str(geo, "sha256");
                input.GeometryReader = $"{reader} — {sha}";
                // v4 (export_golden 30.09.2026): the linetype scale of every crossing line (dashed ×0.5 vs continuous).
                var linetypeScale = new Dictionary<string, double>(StringComparer.Ordinal);
                if (geo.TryGetProperty("linetype_scale_by_handle", out var lts) && lts.ValueKind == JsonValueKind.Object)
                    foreach (var p in lts.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.Number) linetypeScale[p.Name] = p.Value.GetDouble();
                var unknownLines = 0;
                foreach (var entry in geo.GetProperty("segments_by_handle").EnumerateObject())
                {
                    var handle = entry.Name;
                    if (entry.Value.ValueKind == JsonValueKind.Object)
                    {
                        var points = entry.Value.TryGetProperty("hatch_boundary_points", out var pts) && pts.ValueKind == JsonValueKind.Array
                            ? pts.EnumerateArray().Select(p => new BoqPoint(p[0].GetDouble(), p[1].GetDouble())).ToList()
                            : new List<BoqPoint>();
                        BoqPoint? centroid = points.Count == 0 ? null
                            : new BoqPoint(points.Sum(p => p.X) / points.Count, points.Sum(p => p.Y) / points.Count);
                        var layer = layerByHandle.TryGetValue(handle, out var known) ? known : hatchLayer;
                        input.Geometry.Add(new BoqEntityGeometry(geometrySrc, handle, layer, true, Array.Empty<BoqSegment>(), centroid)
                        {
                            HatchBoundaryPoints = points.Count == 0 ? null : points,
                        });
                        continue;
                    }
                    var segments = new List<BoqSegment>();
                    foreach (var seg in entry.Value.EnumerateArray())
                        segments.Add(new BoqSegment(new BoqPoint(seg[0][0].GetDouble(), seg[0][1].GetDouble()),
                            new BoqPoint(seg[1][0].GetDouble(), seg[1][1].GetDouble())));
                    double? scale = linetypeScale.TryGetValue(handle, out var s) ? s : null;
                    if (!layerByHandle.TryGetValue(handle, out var lineLayer))
                    {
                        if (segments.Count > 0) unknownLines++;
                        input.Geometry.Add(new BoqEntityGeometry(geometrySrc, handle, null, false, segments, null) { LinetypeScale = scale });
                        continue;
                    }
                    // The stored vertices of a polyline are its consecutive segment endpoints (the reference's
                    // vertices); a LINE has none (it is never a hatch's boundary polyline).
                    IReadOnlyList<BoqPoint>? vertices = null;
                    if (segments.Count > 0 && etypeByHandle.TryGetValue(handle, out var etype) && !string.Equals(etype, "LINE", StringComparison.OrdinalIgnoreCase))
                        vertices = segments.Select(sg => sg.A).Append(segments[^1].B).ToList();
                    input.Geometry.Add(new BoqEntityGeometry(geometrySrc, handle, lineLayer, false, segments, null)
                    {
                        LinetypeScale = scale, Vertices = vertices,
                    });
                }
                if (unknownLines > 0)
                    input.Warnings.Add($"{unknownLines} קווים בגאומטריית SM ללא רשומה (שכבה לא ידועה) — לא השתתפו בסיווג.");
            }

            if (root.TryGetProperty("ha_hatch_layers", out var ha) && ha.ValueKind == JsonValueKind.Array)
                foreach (var l in ha.EnumerateArray())
                    input.HatchLayers.Add(new BoqHatchLayerTotal(
                        "HA",
                        BoqRuleset.Str(l, "layer") ?? "",
                        Int(l, "native_direct_count"),
                        Dec(l, "native_direct_area_m2"),
                        Int(l, "native_recovered_count"),
                        Dec(l, "native_recovered_area_m2"),
                        Int(l, "native_unresolved_count")));

            // v4.5: the area hatches of the overlap notes — Civil's state per hatch and the boundary polygons of the read.
            if (root.TryGetProperty("ha_hatch_rows", out var hr) && hr.ValueKind == JsonValueKind.Array)
                foreach (var r in hr.EnumerateArray())
                    input.HaHatches.Add(new BoqHaHatch(BoqRuleset.Str(r, "handle") ?? "", BoqRuleset.Str(r, "layer") ?? "",
                        BoqRuleset.Str(r, "state") ?? "", BoqRuleset.Num(r, "native_area_m2")));
            if (root.TryGetProperty("ha_hatch_polygons", out var hp) && hp.ValueKind == JsonValueKind.Object)
                foreach (var p in hp.EnumerateObject())
                    input.HaHatchPolygons.Add(new BoqHaPolygon(p.Name, BoqRuleset.Str(p.Value, "layer") ?? "", BoqRuleset.Str(p.Value, "wkt") ?? ""));
            if (root.TryGetProperty("ha_hatches_in_read", out var hn) && hn.ValueKind == JsonValueKind.Number)
                input.HaHatchesInRead = hn.GetInt32();

            if (root.TryGetProperty("sm_strict_recoveries", out var rec) && rec.ValueKind == JsonValueKind.Object)
                foreach (var p in rec.EnumerateObject())
                    input.StrictHatchRecoveries[p.Name] = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetDouble() : null;

            // Rules 2.1: the plan chords of every length-part / control record ("SRC:HANDLE" → [[x1,y1,x2,y2],...], metres,
            // already tessellated, a closed polyline with its closing chord) and the insertion point of every count record.
            // A record absent from the chords has no plan geometry (its measured quantity counts); an empty list is a
            // record whose geometry has no chord.
            if (root.TryGetProperty("object_geometry", out var og) && og.ValueKind == JsonValueKind.Object)
            {
                var reader = BoqRuleset.Str(og, "reader") ?? "";
                var shas = og.TryGetProperty("sha256", out var sh) && sh.ValueKind == JsonValueKind.Object
                    ? string.Join(", ", sh.EnumerateObject().Select(p => p.Name + " " + Short(p.Value.GetString())))
                    : Short(BoqRuleset.Str(og, "sha256"));
                input.ObjectGeometryReader = reader + (shas.Length > 0 ? " — " + shas : "");
                var layerOf = new Dictionary<(string, string), string>();
                foreach (var r in input.Records) layerOf.TryAdd((r.Src, r.Handle), r.Layer);
                if (og.TryGetProperty("length_chords_by_record", out var lc) && lc.ValueKind == JsonValueKind.Object)
                    foreach (var entry in lc.EnumerateObject())
                    {
                        if (!SplitRecordKey(entry.Name, out var src, out var handle) || entry.Value.ValueKind != JsonValueKind.Array) continue;
                        var chords = new List<BoqSegment>(entry.Value.GetArrayLength());
                        foreach (var c in entry.Value.EnumerateArray())
                            chords.Add(new BoqSegment(new BoqPoint(c[0].GetDouble(), c[1].GetDouble()), new BoqPoint(c[2].GetDouble(), c[3].GetDouble())));
                        layerOf.TryGetValue((src, handle), out var layer);
                        input.LengthGeometry[(src, handle)] = new BoqEntityGeometry(src, handle, layer, false, chords, null);
                    }
                if (og.TryGetProperty("count_points_by_record", out var cp) && cp.ValueKind == JsonValueKind.Object)
                    foreach (var entry in cp.EnumerateObject())
                    {
                        if (!SplitRecordKey(entry.Name, out var src, out var handle) ||
                            entry.Value.ValueKind != JsonValueKind.Array || entry.Value.GetArrayLength() < 2) continue;
                        input.CountPoints[(src, handle)] = new BoqPoint(entry.Value[0].GetDouble(), entry.Value[1].GetDouble());
                    }
                if (og.TryGetProperty("count_rotation_by_record", out var cr) && cr.ValueKind == JsonValueKind.Object)
                    foreach (var entry in cr.EnumerateObject())
                        if (SplitRecordKey(entry.Name, out var src, out var handle) && entry.Value.ValueKind == JsonValueKind.Number)
                            input.CountRotations[(src, handle)] = entry.Value.GetDouble();
                // Rules 2.8 (BOQ-N1): full INSERT transforms of approved-footprint blocks and each file's definition signature.
                if (og.TryGetProperty("count_transform_by_record", out var ct) && ct.ValueKind == JsonValueKind.Object)
                    foreach (var entry in ct.EnumerateObject())
                    {
                        var t = entry.Value;
                        if (!SplitRecordKey(entry.Name, out var src, out var handle) || t.ValueKind != JsonValueKind.Object ||
                            Numbers(t, "insert") is not { Length: >= 2 } insert || BoqRuleset.Num(t, "rotation") is not { } rotation ||
                            Numbers(t, "scale") is not { Length: 3 } scale || Numbers(t, "normal") is not { Length: 3 } normal) continue;
                        input.CountTransforms[(src, handle)] = new BoqInsertTransform(insert[0], insert[1], rotation, scale, normal);
                    }
                if (og.TryGetProperty("block_signatures", out var bs) && bs.ValueKind == JsonValueKind.Object)
                    foreach (var entry in bs.EnumerateObject())
                    {
                        var bar = entry.Name.IndexOf('|');
                        if (bar <= 0) continue;
                        var sig = entry.Value;
                        input.BlockSignatures[(entry.Name[..bar], entry.Name[(bar + 1)..])] =
                            sig.ValueKind == JsonValueKind.Object && sig.TryGetProperty("dxf_counts", out var counts) &&
                            counts.ValueKind == JsonValueKind.Object && Numbers(sig, "envelope") is { Length: 4 } envelope &&
                            Numbers(sig, "base_point") is { Length: 3 } basePoint && BoqRuleset.Num(sig, "units_code") is { } units
                                ? new BoqDefinitionSignature(counts.EnumerateObject().ToDictionary(c => c.Name, c => c.Value.GetInt32(), StringComparer.Ordinal),
                                    envelope, basePoint, (int)units, BoqRuleset.Str(sig, "geometry_digest"))
                                : null;
                    }
            }

            // v4: the zero-length GM line is not missing — nothing to measure, the quantity does not change; it is one
            // information row in 'לא נכלל' (reference EXTRA_EXCL).
            if (BoqRuleset.Str(root, "gm_unmeasured_zero_length") is { Length: > 0 } unmeasured)
            {
                string? layer = null;
                unmeasuredLayerHints?.TryGetValue(unmeasured, out layer);
                input.ZeroLength.Add(new BoqZeroLength("GM", layer, unmeasured));
            }

            // v4.1: path arrays whose items hold bike symbols; the members are ordinary count records with world points.
            if (root.TryGetProperty("arrays", out var arrays) && arrays.ValueKind == JsonValueKind.Array)
                foreach (var a in arrays.EnumerateArray())
                    input.Arrays.Add(new BoqArrayInsert(BoqRuleset.Str(a, "src") ?? "SM", BoqRuleset.Str(a, "handle") ?? "")
                    {
                        Block = BoqRuleset.Str(a, "block") ?? "",
                        MemberHandles = a.TryGetProperty("members", out var m) && m.ValueKind == JsonValueKind.Array
                            ? m.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>(),
                        PathLine = BoqRuleset.Str(a, "path_line"),
                        PathLayer = BoqRuleset.Str(a, "path_layer"),
                    });
            if (root.TryGetProperty("array_same_location_m", out var asl) && asl.ValueKind == JsonValueKind.Number)
                input.ArraySameLocationM = asl.GetDouble();

            input.SourceRows.Add(new BoqSourceRow("קלט", Schema + " (" + input.Records.Count.ToString(CultureInfo.InvariantCulture) + " רשומות)"));
            return input;
        }

        /// <summary>"SRC:HANDLE" → (SRC, HANDLE); the role never contains a colon.</summary>
        private static double[]? Numbers(JsonElement e, string name) =>
            e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array &&
            a.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number)
                ? a.EnumerateArray().Select(x => x.GetDouble()).ToArray() : null;

        internal static bool SplitRecordKey(string key, out string src, out string handle)
        {
            var colon = key.IndexOf(':');
            src = colon > 0 ? key[..colon] : "";
            handle = colon > 0 ? key[(colon + 1)..] : "";
            return colon > 0 && handle.Length > 0;
        }

        private static string Short(string? sha) => string.IsNullOrEmpty(sha) ? "" : sha.Length > 12 ? sha[..12] : sha;

        private static int Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        /// <summary>Areas are frozen as decimal strings (Python Decimal); numbers are accepted too.</summary>
        private static decimal Dec(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return 0m;
            return v.ValueKind switch
            {
                JsonValueKind.String => decimal.Parse(v.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture),
                JsonValueKind.Number => v.GetDecimal(),
                _ => 0m,
            };
        }
    }
}
