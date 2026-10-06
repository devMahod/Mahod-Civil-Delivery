using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Estimate.BoqRulesV2
{
    /// <summary>
    /// A bill-of-quantities ruleset as DATA (schema mahod-boq-rules/2): parameters with their sources, sign sizes,
    /// chapters and BoQ lines built from parts (layers / block layers / sign numbers, measure kind, parameter). The
    /// lane extensions (source_roles, layer_sets, ownership, crosswalk_geometry, long_objects, sign_rules, pricebook,
    /// workbook) are optional: a file without them still loads, with the generic defaults documented per property.
    /// Version 2.1 keys ("one physical object once"): top-level <c>measurement</c> (length_basis "plan", tolerance_m,
    /// piece_m, parallel_sin, texts); per length part <c>object_width_m</c> / <c>object_width_note</c>; per count part
    /// <c>same_point_m</c>; the part kind <c>line_sum</c> (<c>lines</c>, <c>decided_by</c>) and a line-level
    /// <c>control</c> (label, src, layers, kind, object_width_m). A 2.0 file (without them) keeps the 2.0 behaviour.
    /// Version 2.3 keys (v4, 30.09.2026): top-level <c>not_included</c> [{id, title, reason}]; line-level <c>label</c>,
    /// <c>review</c>, <c>drawing_note</c>; part-level <c>blocks</c> (block-name filter) and <c>alt</c> {value, label}; the part
    /// kind <c>count_of</c> {line}. Lane keys for the v4 texts: sign_rules (guide-sign notes, combined labels, layer notes,
    /// poles), control_reasons, pricebook.descr_fix, crosswalk_geometry (assembly labels / reasons / constants) and workbook.
    /// Unknown keys (e.g. reference keys the engine does not read yet) are ignored.
    /// No project layer name lives in code; every name below is read from the file.
    /// </summary>
    public sealed class BoqRuleset
    {
        public const string EmbeddedResource6422 = "MahodAI.CivilDelivery.Estimate.BoqRulesV2.rulesets.6422_v2.json";

        public static readonly IReadOnlyCollection<string> Kinds = new[]
        {
            "length", "area", "count", "count_x", "hatch", "crosswalk_geo", "sign_area", "sign_block_area", "line_sum", "count_of",
        };

        public string Schema { get; private init; } = "";
        public string Project { get; private init; } = "";
        public string Version { get; private init; } = "";
        public string Sha256 { get; private init; } = "";
        public IReadOnlyList<KeyValuePair<string, string>> Sources { get; private init; } = Array.Empty<KeyValuePair<string, string>>();
        public IReadOnlyList<BoqParameter> Parameters { get; private init; } = Array.Empty<BoqParameter>();
        public BoqSignSizes SignSizes { get; private init; } = new();
        public IReadOnlyList<BoqChapter> Chapters { get; private init; } = Array.Empty<BoqChapter>();
        public IReadOnlyList<BoqLine> Lines { get; private init; } = Array.Empty<BoqLine>();
        public string? ExistingLayersPrefix { get; private init; }
        public IReadOnlyList<string> Notes { get; private init; } = Array.Empty<string>();
        /// <summary>Plan-length settings of the one-object-once rule (the reference constants when the section is absent).</summary>
        public BoqMeasurement Measurement { get; private init; } = new();
        /// <summary>
        /// Rules 2.8 (BOQ-N1): approved physical footprints of block definitions. Two placements of such a block at the same
        /// point are two objects only when both transforms are proven, the definition matches the approved signature and the
        /// placed bodies are positively separated (<see cref="BoqRulesEngine"/>); absent for every other block.
        /// </summary>
        public IReadOnlyList<BoqPhysicalFootprint> PhysicalFootprints { get; private init; } = Array.Empty<BoqPhysicalFootprint>();

        // ---- lane extensions (optional) ----
        /// <summary>Parameter id holding the road class 1..3 (default "road_class").</summary>
        public string RoadClassParam { get; private init; } = "road_class";
        public IReadOnlyList<BoqSourceRole> SourceRoles { get; private init; } = Array.Empty<BoqSourceRole>();
        /// <summary>Ordered ownership / duplicate rules. Absent: only the existing-layer prefix rule (if the prefix is set).</summary>
        public IReadOnlyList<BoqOwnershipRule> Ownership { get; private init; } = Array.Empty<BoqOwnershipRule>();
        /// <summary>Crossing (811) geometry settings. Absent: crosswalk_geo parts measure nothing and say so.</summary>
        public BoqCrosswalkConfig? Crosswalk { get; private init; }
        public string LongObjectReason { get; private init; } =
            "לבדיקה: עצם באורך {length} מטר בשכבת {layer} — ארוך מהמותר לחלק הזה; לא נכלל";
        /// <summary>Block numbers in this range are marking numbers, never signs (default 801–821, the national marking table).</summary>
        public (int From, int To)? SignExcludedNumberRange { get; private init; } = (801, 821);
        public string SignBlockAreaAbsentNote { get; private init; } = "נבדקו כל שמות הבלוקים בקובץ {src} — אין בלוקים עם מספרים {from}–{to}";
        /// <summary>
        /// Rules 2.3 (v4) sign texts and guide-sign search: the calculation-detail and BoQ-row notes of a sign_block_area part,
        /// the combined sign labels (505/506), the per-layer notes of sign blocks and the pole parameter (poles vs signs).
        /// </summary>
        public BoqSignTexts SignTexts { get; private init; } = new();
        public BoqPricebook Pricebook { get; private init; } = new();
        public BoqWorkbookText Workbook { get; private init; } = new();
        /// <summary>Chapters of the example that the draft does not measure (rules 2.3 top-level "not_included").</summary>
        public IReadOnlyList<BoqNotIncluded> NotIncluded { get; private init; } = Array.Empty<BoqNotIncluded>();
        /// <summary>Lane key "control_reasons": exclusion reason per line id of its control records ("{ref}" = the line's item).</summary>
        public IReadOnlyDictionary<string, string> ControlReasons { get; private init; } = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Lane key "array_members" (rules v4.1): member blocks inside path arrays counted at their world points; null = off.</summary>
        public BoqArrayMembersConfig? ArrayMembers { get; private init; }
        /// <summary>Lane key "note_figures" (rules v4.1): the figures quoted in the notes, traceable on the one-object-once sheet; null = off.</summary>
        public BoqNoteFiguresConfig? NoteFigures { get; private init; }
        /// <summary>Lane key "ha_overlap" (rules v4.4/4.5): overlap and unmeasured-area ESTIMATES of the area hatches; null = off.</summary>
        public BoqHaOverlapConfig? HaOverlap { get; private init; }
        /// <summary>Lane key "example_items" (v4.3): items of the example in measured chapters that this draft did not measure
        /// or check in the drawing — printed after the not-included chapters; null = none.</summary>
        public BoqExampleItems? ExampleItems { get; private init; }

        /// <summary>The line with this id, or null.</summary>
        public BoqLine? Line(string? id) => id == null ? null : Lines.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.Ordinal));

        /// <summary>
        /// The reference REF(): the price-list item the engineer knows (51.06.0010, "51.32.1852/1862" for an urban/rural
        /// line), or the line's own label when it has no item. An unknown id is returned as is.
        /// </summary>
        public string Ref(string? lineId) => Line(lineId)?.Ref() ?? lineId ?? "";

        public IEnumerable<BoqPart> AllParts => Lines.SelectMany(line => line.Parts);

        public BoqParameter? Parameter(string id) =>
            Parameters.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

        public static BoqRuleset LoadEmbedded6422()
        {
            using var stream = typeof(BoqRuleset).Assembly.GetManifestResourceStream(EmbeddedResource6422)
                ?? throw new InvalidDataException("The embedded BoQ ruleset is missing: " + EmbeddedResource6422);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            return Parse(reader.ReadToEnd());
        }

        public static BoqRuleset LoadFile(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

        public static BoqRuleset Parse(string json)
        {
            ArgumentNullException.ThrowIfNull(json);
            var text = json.Length > 0 && json[0] == '﻿' ? json[1..] : json;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A BoQ ruleset must be a JSON object.");

            var sources = new List<KeyValuePair<string, string>>();
            if (root.TryGetProperty("sources", out var src) && src.ValueKind == JsonValueKind.Object)
                foreach (var p in src.EnumerateObject())
                    sources.Add(new(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText()));

            var parameters = new List<BoqParameter>();
            if (root.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var p in ps.EnumerateArray())
                {
                    var id = Str(p, "id") ?? throw new InvalidDataException("A parameter has no id.");
                    var value = Num(p, "value") ?? throw new InvalidDataException($"Parameter '{id}' has no numeric value.");
                    parameters.Add(new BoqParameter(id, Str(p, "label") ?? id, value, Str(p, "status") ?? "", Str(p, "src") ?? ""));
                }

            var chapters = new List<BoqChapter>();
            if (root.TryGetProperty("chapters", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var c in cs.EnumerateArray())
                    chapters.Add(new BoqChapter(Str(c, "id") ?? "", Str(c, "title") ?? ""));

            var lines = new List<BoqLine>();
            if (root.TryGetProperty("boq", out var boq) && boq.ValueKind == JsonValueKind.Array)
                foreach (var l in boq.EnumerateArray())
                    lines.Add(ParseLine(l));

            var layerSets = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            if (root.TryGetProperty("layer_sets", out var sets) && sets.ValueKind == JsonValueKind.Object)
                foreach (var s in sets.EnumerateObject())
                    layerSets[s.Name] = StrList(s.Value);

            var existingPrefix = Str(root, "existing_layers_prefix");
            var ownership = new List<BoqOwnershipRule>();
            if (root.TryGetProperty("ownership", out var own) && own.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in own.EnumerateArray())
                    ownership.Add(ParseOwnership(o, layerSets));
            }
            else if (!string.IsNullOrEmpty(existingPrefix))
            {
                // A ruleset without an ownership section keeps the one generic rule the schema always had.
                ownership.Add(new BoqOwnershipRule
                {
                    Id = "existing_layers_prefix",
                    LayerPrefix = existingPrefix,
                    Reason = $"שכבת {existingPrefix} = מדידה / קיים — לא עבודה חדשה",
                });
            }

            var roles = new List<BoqSourceRole>();
            if (root.TryGetProperty("source_roles", out var rs) && rs.ValueKind == JsonValueKind.Array)
                foreach (var r in rs.EnumerateArray())
                    roles.Add(new BoqSourceRole(Str(r, "id") ?? throw new InvalidDataException("A source role has no id."),
                        Str(r, "file_pattern") ?? "", Str(r, "label")) { Contribution = Str(r, "contribution") });

            BoqCrosswalkConfig? crosswalk = null;
            if (root.TryGetProperty("crosswalk_geometry", out var cw) && cw.ValueKind == JsonValueKind.Object)
                crosswalk = ParseCrosswalk(cw);

            string longReason = "לבדיקה: עצם באורך {length} מטר בשכבת {layer} — ארוך מהמותר לחלק הזה; לא נכלל";
            if (root.TryGetProperty("long_objects", out var lo) && lo.ValueKind == JsonValueKind.Object && Str(lo, "reason") is { } lr)
                longReason = lr;

            (int, int)? excluded = (801, 821);
            var blockAbsent = "נבדקו כל שמות הבלוקים בקובץ {src} — אין בלוקים עם מספרים {from}–{to}";
            var signTexts = new BoqSignTexts();
            if (root.TryGetProperty("sign_rules", out var sr) && sr.ValueKind == JsonValueKind.Object)
            {
                if (sr.TryGetProperty("exclude_number_range", out var er))
                    excluded = er.ValueKind == JsonValueKind.Array && er.GetArrayLength() == 2
                        ? (er[0].GetInt32(), er[1].GetInt32()) : null;
                if (Str(sr, "block_area_absent_note") is { } note) blockAbsent = note;
                signTexts = ParseSignTexts(sr, signTexts);
            }

            var ruleset = new BoqRuleset
            {
                Schema = Str(root, "schema") ?? "",
                Project = Str(root, "project") ?? "",
                Version = Str(root, "version") ?? "",
                Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant(),
                Sources = sources,
                Parameters = parameters,
                SignSizes = ParseSignSizes(root),
                Chapters = chapters,
                Lines = lines,
                ExistingLayersPrefix = existingPrefix,
                Notes = root.TryGetProperty("notes", out var notes) ? StrList(notes) : Array.Empty<string>(),
                Measurement = ParseMeasurement(root),
                PhysicalFootprints = ParsePhysicalFootprints(root),
                RoadClassParam = Str(root, "road_class_param") ?? "road_class",
                SourceRoles = roles,
                Ownership = ownership,
                Crosswalk = crosswalk,
                LongObjectReason = longReason,
                SignExcludedNumberRange = excluded,
                SignBlockAreaAbsentNote = blockAbsent,
                SignTexts = signTexts,
                Pricebook = ParsePricebook(root),
                Workbook = ParseWorkbook(root),
                NotIncluded = ParseNotIncluded(root),
                ControlReasons = ParseStringMap(root, "control_reasons"),
                ArrayMembers = ParseArrayMembers(root),
                NoteFigures = ParseNoteFigures(root),
                HaOverlap = ParseHaOverlap(root),
                ExampleItems = ParseExampleItems(root),
            };
            ruleset.Validate();
            return ruleset;
        }

        /// <summary>Fails closed on a ruleset the engine cannot evaluate exactly as written.</summary>
        private void Validate()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var allLineIds = Lines.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
            var parameterIds = Parameters.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var line in Lines)
            {
                if (string.IsNullOrWhiteSpace(line.Id) || !ids.Add(line.Id))
                    throw new InvalidDataException($"BoQ line ids must be unique and non-empty ('{line.Id}').");
                if (line.Parts.Count == 0) throw new InvalidDataException($"BoQ line {line.Id} has no parts.");
                foreach (var part in line.Parts)
                {
                    if (!Kinds.Contains(part.Kind))
                        throw new InvalidDataException($"BoQ line {line.Id}: unsupported part kind '{part.Kind}'.");
                    if (part.Param != null && !parameterIds.Contains(part.Param))
                        throw new InvalidDataException($"BoQ line {line.Id}: parameter '{part.Param}' is not defined.");
                    if (part.Kind == "line_sum")
                    {
                        // The quantity is the sum of other lines' rounded quantities: it reads no drawing object.
                        if (part.Lines.Count == 0)
                            throw new InvalidDataException($"BoQ line {line.Id}: the line_sum part '{part.Label}' names no lines.");
                        foreach (var id in part.Lines)
                        {
                            if (string.Equals(id, line.Id, StringComparison.Ordinal) || !allLineIds.Contains(id))
                                throw new InvalidDataException($"BoQ line {line.Id}: line_sum refers to '{id}', which is not another line of the ruleset.");
                            // The engine and the workbook read a referenced line that is already complete: an earlier line
                            // with no line_sum of its own (review 30/09 — a later one gave 0 in Excel).
                            var ordered = Lines.ToList();
                            var referenced = ordered.FindIndex(l => string.Equals(l.Id, id, StringComparison.Ordinal));
                            if (referenced > ordered.IndexOf(line) || ordered[referenced].Parts.Any(p => p.Kind == "line_sum"))
                                throw new InvalidDataException($"BoQ line {line.Id}: line_sum refers to '{id}', which must be an earlier line without a line_sum.");
                        }
                        if (part.Param != null)
                            throw new InvalidDataException($"BoQ line {line.Id}: a line_sum part takes no parameter.");
                        continue;
                    }
                    if (part.Kind == "count_of")
                    {
                        // Count source objects from an earlier count/count_x line, not a derived quantity.
                        // Engine Base and the workbook's base cells have this shared meaning only for those kinds.
                        var ordered = Lines.ToList();
                        var referenced = part.Of == null ? -1 : ordered.FindIndex(l => string.Equals(l.Id, part.Of, StringComparison.Ordinal));
                        if (referenced < 0 || referenced >= ordered.IndexOf(line))
                            throw new InvalidDataException($"BoQ line {line.Id}: count_of refers to '{part.Of}', which must be an earlier line of the ruleset.");
                        if (part.Param != null)
                            throw new InvalidDataException($"BoQ line {line.Id}: a count_of part takes no parameter.");
                        var countedSource = ordered[referenced];
                        if (countedSource.Parts.Count == 0 || countedSource.Parts.Any(p => p.Kind is not ("count" or "count_x")))
                            throw new InvalidDataException(
                                $"BoQ line {line.Id}: count_of refers to '{part.Of}', whose parts must all be count or count_x. " +
                                "count_of counts source objects, not area, length or a derived quantity. " +
                                "Reference the original earlier object-count line directly (not count_of, line_sum or sign_area), or define an explicit count/count_x source line.");
                        continue;
                    }
                    if (part.Src.Count == 0)
                        throw new InvalidDataException($"BoQ line {line.Id}: part '{part.Label}' names no source file role.");
                    if (part.ObjectWidth is { } width && (!double.IsFinite(width) || width < 0))
                        throw new InvalidDataException($"BoQ line {line.Id}: object_width_m must be a length ≥ 0.");
                    if (part.SamePoint is { } same && (!double.IsFinite(same) || same < 0))
                        throw new InvalidDataException($"BoQ line {line.Id}: same_point_m must be a length ≥ 0.");
                }
                if (line.Control is { } control)
                {
                    if (control.Src.Count == 0 || control.Layers.Count == 0)
                        throw new InvalidDataException($"BoQ line {line.Id}: the control needs its source roles and layers.");
                    if (!double.IsFinite(control.ObjectWidth) || control.ObjectWidth < 0)
                        throw new InvalidDataException($"BoQ line {line.Id}: the control's object_width_m must be a length ≥ 0.");
                }
            }
            var m = Measurement;
            if (!string.Equals(m.LengthBasis, "plan", StringComparison.Ordinal))
                throw new InvalidDataException($"measurement.length_basis '{m.LengthBasis}' is not supported (only \"plan\").");
            if (!double.IsFinite(m.ToleranceM) || m.ToleranceM < 0 || !double.IsFinite(m.PieceM) || m.PieceM <= 0 ||
                !double.IsFinite(m.ParallelSin) || m.ParallelSin < 0 || m.ParallelSin > 1)
                throw new InvalidDataException("measurement: tolerance_m ≥ 0, piece_m > 0 and 0 ≤ parallel_sin ≤ 1 are required.");
            if (Crosswalk != null)
            {
                foreach (var id in new[] { Crosswalk.StandardWidthParam, Crosswalk.FillParam })
                    if (!parameterIds.Contains(id))
                        throw new InvalidDataException($"crosswalk_geometry refers to undefined parameter '{id}'.");
            }
            if (AllParts.Any(p => p.Kind == "sign_area") && !parameterIds.Contains(RoadClassParam))
                throw new InvalidDataException($"Sign areas need the road-class parameter '{RoadClassParam}'.");
            if (Lines.Any(l => l.ItemUrban != null) && !parameterIds.Contains(RoadClassParam))
                throw new InvalidDataException($"Urban/rural items need the road-class parameter '{RoadClassParam}'.");
        }

        private static BoqLine ParseLine(JsonElement l)
        {
            var parts = new List<BoqPart>();
            if (l.TryGetProperty("parts", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var p in ps.EnumerateArray())
                {
                    (int, int)? range = null;
                    if (p.TryGetProperty("sign_range", out var r) && r.ValueKind == JsonValueKind.Array && r.GetArrayLength() == 2)
                        range = (r[0].GetInt32(), r[1].GetInt32());
                    BoqPartAlternative? alt = null;
                    if (p.TryGetProperty("alt", out var a) && a.ValueKind == JsonValueKind.Object && Num(a, "value") is { } altValue)
                        alt = new BoqPartAlternative(altValue, Str(a, "label") ?? "");
                    parts.Add(new BoqPart
                    {
                        Label = Str(p, "label") ?? "",
                        Kind = Str(p, "kind") ?? "",
                        Src = StrList(p, "src"),
                        Layers = StrList(p, "layers"),
                        BlockLayers = StrList(p, "block_layers"),
                        Etypes = StrList(p, "etypes"),
                        Signs = StrList(p, "signs"),
                        SignRange = range,
                        Param = Str(p, "param"),
                        MaxLength = Num(p, "max_len"),
                        Confirm = Str(p, "confirm"),
                        Assumption = Str(p, "assumption"),
                        OpenOnly = Bool(p, "open_only"),
                        ClosedOnly = Bool(p, "closed_only"),
                        ObjectWidth = Num(p, "object_width_m"),
                        ObjectWidthNote = Str(p, "object_width_note"),
                        SamePoint = Num(p, "same_point_m"),
                        Lines = StrList(p, "lines"),
                        DecidedBy = Str(p, "decided_by"),
                        Blocks = StrList(p, "blocks"),
                        Of = Str(p, "line"),
                        Alt = alt,
                        Brief = Str(p, "brief"),
                    });
                }
            var id = Str(l, "id") ?? "";
            return new BoqLine
            {
                Id = id,
                Chapter = Str(l, "chapter") ?? "",
                Item = Str(l, "item"),
                ItemUrban = Str(l, "item_urban"),
                ItemRural = Str(l, "item_rural"),
                Unit = Str(l, "unit") ?? "",
                Confirm = Str(l, "confirm"),
                Assumption = Str(l, "assumption"),
                Label = Str(l, "label"),
                Review = Str(l, "review"),
                DrawingNote = Str(l, "drawing_note"),
                HeldNoteLayers = StrList(l, "held_note_layers"),
                Parts = parts,
                Control = ParseControl(l, id),
            };
        }

        // Port of rules v4.1 in progress (interrupted 30.09.2026 11:20): the lane keys are not read yet, so both stay off.
        private static BoqArrayMembersConfig? ParseArrayMembers(JsonElement root)
        {
            if (!root.TryGetProperty("array_members", out var a) || a.ValueKind != JsonValueKind.Object) return null;
            var d = new BoqArrayMembersConfig();
            return new BoqArrayMembersConfig
            {
                MemberBlock = Str(a, "member_block") ?? d.MemberBlock,
                MemberReason = Str(a, "member_reason") ?? d.MemberReason,
                ArrayReason = Str(a, "array_reason") ?? d.ArrayReason,
                ArrayReasonNoLine = Str(a, "array_reason_no_line") ?? d.ArrayReasonNoLine,
                PathLineSuffix = Str(a, "path_line_suffix") ?? d.PathLineSuffix,
            };
        }

        private static BoqNoteFiguresConfig? ParseNoteFigures(JsonElement root)
        {
            if (!root.TryGetProperty("note_figures", out var n) || n.ValueKind != JsonValueKind.Object) return null;
            var d = new BoqNoteFiguresConfig();
            IReadOnlyList<string> List(string name, IReadOnlyList<string> fallback) => n.TryGetProperty(name, out var v) ? StrList(v) : fallback;
            var pair = n.TryGetProperty("pair_m", out var pm) && pm.ValueKind == JsonValueKind.Array && pm.GetArrayLength() == 2
                ? (pm[0].GetDouble(), pm[1].GetDouble()) : (d.PairLoM, d.PairHiM);
            return new BoqNoteFiguresConfig
            {
                Src = Str(n, "src") ?? d.Src,
                LoweredLayers = List("lowered_layers", d.LoweredLayers),
                C1Layers = List("c1_layers", d.C1Layers),
                C2Layers = List("c2_layers", d.C2Layers),
                StepM = Num(n, "step_m") ?? d.StepM,
                ReachM = Num(n, "reach_m") ?? d.ReachM,
                MaxAngleRad = Num(n, "max_angle_rad") ?? d.MaxAngleRad,
                LoweredWidthM = Num(n, "lowered_width_m") ?? d.LoweredWidthM,
                PairLoM = pair.Item1,
                PairHiM = pair.Item2,
                PairWidthM = Num(n, "pair_width_m") ?? d.PairWidthM,
                MarkSrc = Str(n, "mark_src") ?? d.MarkSrc,
                LineTypes = List("line_types", d.LineTypes),
                FrameLayer = Str(n, "frame_layer") ?? d.FrameLayer,
                FrameMinM = Num(n, "frame_min_m") ?? d.FrameMinM,
                FrameWidthM = Num(n, "frame_width_m") ?? d.FrameWidthM,
                FrameAltWidthM = Num(n, "frame_alt_width_m") ?? d.FrameAltWidthM,
                LongRectLayer = Str(n, "long_rect_layer") ?? d.LongRectLayer,
            };
        }

        private static BoqExampleItems? ParseExampleItems(JsonElement root)
        {
            if (!root.TryGetProperty("example_items", out var e) || e.ValueKind != JsonValueKind.Object) return null;
            var rows = new List<BoqExampleItemRow>();
            if (e.TryGetProperty("rows", out var rs) && rs.ValueKind == JsonValueKind.Array)
                foreach (var r in rs.EnumerateArray())
                    rows.Add(new BoqExampleItemRow(Str(r, "chapter") ?? "", Str(r, "items") ?? "", Str(r, "status") ?? "", Str(r, "action") ?? ""));
            return new BoqExampleItems(Str(e, "title") ?? "", rows);
        }

        private static BoqHaOverlapConfig? ParseHaOverlap(JsonElement root)
        {
            if (!root.TryGetProperty("ha_overlap", out var h) || h.ValueKind != JsonValueKind.Object) return null;
            var d = new BoqHaOverlapConfig();
            return new BoqHaOverlapConfig
            {
                Layers = h.TryGetProperty("layers", out var l) ? StrList(l) : d.Layers,
                MeasuredStates = h.TryGetProperty("measured_states", out var m) ? StrList(m) : d.MeasuredStates,
                MinBetweenM2 = Num(h, "min_between_m2") ?? d.MinBetweenM2,
                AgreeAbsM2 = Num(h, "agree_abs_m2") ?? d.AgreeAbsM2,
                AgreeRel = Num(h, "agree_rel") ?? d.AgreeRel,
            };
        }

        private static IReadOnlyList<BoqNotIncluded> ParseNotIncluded(JsonElement root)
        {
            var list = new List<BoqNotIncluded>();
            if (root.TryGetProperty("not_included", out var ni) && ni.ValueKind == JsonValueKind.Array)
                foreach (var e in ni.EnumerateArray())
                    list.Add(new BoqNotIncluded(Str(e, "id") ?? "", Str(e, "title") ?? "", Str(e, "reason") ?? ""));
            return list;
        }

        private static IReadOnlyDictionary<string, string> ParseStringMap(JsonElement e, string name)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (e.TryGetProperty(name, out var m) && m.ValueKind == JsonValueKind.Object)
                foreach (var p in m.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) map[p.Name] = p.Value.GetString()!;
            return map;
        }

        private static BoqSignTexts ParseSignTexts(JsonElement sr, BoqSignTexts defaults)
        {
            var combined = new List<BoqSignCombinedLabel>();
            if (sr.TryGetProperty("combined_labels", out var cl) && cl.ValueKind == JsonValueKind.Array)
                foreach (var e in cl.EnumerateArray())
                    combined.Add(new BoqSignCombinedLabel(Str(e, "number") ?? "", Str(e, "block_contains") ?? "", Str(e, "label") ?? ""));
            var layerNotes = new List<BoqSignLayerNote>();
            if (sr.TryGetProperty("layer_notes", out var ln) && ln.ValueKind == JsonValueKind.Array)
                foreach (var e in ln.EnumerateArray())
                    layerNotes.Add(new BoqSignLayerNote(Str(e, "layer") ?? "", Str(e, "note") ?? ""));
            return defaults with
            {
                DetailAbsent = Str(sr, "detail_absent_note") ?? defaults.DetailAbsent,
                DetailFound = Str(sr, "detail_found_note") ?? defaults.DetailFound,
                DetailFactorAbsent = Str(sr, "detail_factor_absent") ?? defaults.DetailFactorAbsent,
                DetailFactorFound = Str(sr, "detail_factor_found") ?? defaults.DetailFactorFound,
                BoqAbsent = Str(sr, "boq_absent_note") ?? defaults.BoqAbsent,
                BoqFound = Str(sr, "boq_found_note") ?? defaults.BoqFound,
                CombinedLabels = combined,
                LayerNotes = layerNotes,
                PolesParam = Str(sr, "poles_param") ?? defaults.PolesParam,
                PolesDetailNote = Str(sr, "poles_detail_note") ?? defaults.PolesDetailNote,
                PolesBoqNote = Str(sr, "poles_boq_note") ?? defaults.PolesBoqNote,
            };
        }

        private static BoqLineControl? ParseControl(JsonElement l, string lineId)
        {
            if (!l.TryGetProperty("control", out var c) || c.ValueKind != JsonValueKind.Object) return null;
            return new BoqLineControl
            {
                Label = Str(c, "label") ?? "",
                Src = StrList(c, "src"),
                Layers = StrList(c, "layers"),
                Kind = Str(c, "kind") ?? "length",
                ObjectWidth = Num(c, "object_width_m")
                    ?? throw new InvalidDataException($"BoQ line {lineId}: the control needs object_width_m."),
                Reason = Str(c, "reason"),
            };
        }

        private static BoqMeasurement ParseMeasurement(JsonElement root)
        {
            if (!root.TryGetProperty("measurement", out var m) || m.ValueKind != JsonValueKind.Object) return new BoqMeasurement();
            return new BoqMeasurement
            {
                Present = true,
                LengthBasis = Str(m, "length_basis") ?? "plan",
                LengthRule = Str(m, "length_rule"),
                CountRule = Str(m, "count_rule"),
                ToleranceM = Num(m, "tolerance_m") ?? BoqObjectMeasure.ToleranceM,
                PieceM = Num(m, "piece_m") ?? BoqObjectMeasure.PieceM,
                ParallelSin = Num(m, "parallel_sin") ?? BoqObjectMeasure.ParallelSin,
            };
        }

        private static IReadOnlyList<BoqPhysicalFootprint> ParsePhysicalFootprints(JsonElement root)
        {
            if (!root.TryGetProperty("physical_footprints", out var list) || list.ValueKind != JsonValueKind.Array)
                return Array.Empty<BoqPhysicalFootprint>();
            static double[] Numbers(JsonElement e, string name, int count, string block)
            {
                if (!e.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != count ||
                    a.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number || !double.IsFinite(x.GetDouble())))
                    throw new InvalidDataException($"physical_footprints '{block}': {name} needs {count} finite numbers.");
                return a.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            }
            var result = new List<BoqPhysicalFootprint>();
            foreach (var f in list.EnumerateArray())
            {
                var block = Str(f, "block") ?? throw new InvalidDataException("physical_footprints: every footprint needs a block.");
                if (!f.TryGetProperty("definition_signature", out var sig) || sig.ValueKind != JsonValueKind.Object ||
                    !sig.TryGetProperty("dxf_counts", out var counts) || counts.ValueKind != JsonValueKind.Object ||
                    !f.TryGetProperty("same_transform", out var same) || same.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"physical_footprints '{block}': definition_signature.dxf_counts and same_transform are required.");
                var body = Numbers(f, "body_envelope", 4, block);
                if (!(body[0] < body[2] && body[1] < body[3]))
                    throw new InvalidDataException($"physical_footprints '{block}': body_envelope must be [x0, y0, x1, y1] with x0 < x1, y0 < y1.");
                result.Add(new BoqPhysicalFootprint
                {
                    Block = block,
                    Decision = Str(f, "decision") ?? "",
                    Signature = new BoqDefinitionSignature(
                        counts.EnumerateObject().ToDictionary(c => c.Name, c => c.Value.GetInt32(), StringComparer.Ordinal),
                        Numbers(sig, "envelope", 4, block), Numbers(sig, "base_point", 3, block),
                        (int)(Num(sig, "units_code") ?? throw new InvalidDataException($"physical_footprints '{block}': units_code is required.")),
                        Str(sig, "geometry_digest") is { Length: 64 } digest && digest.All(Uri.IsHexDigit)
                            ? digest.ToLowerInvariant()
                            : throw new InvalidDataException($"physical_footprints '{block}': geometry_digest (SHA-256 hex) is required.")),
                    BodyEnvelope = body,
                    SeparationMinM = Num(f, "separation_min_m") is { } gap && gap >= 0
                        ? gap : throw new InvalidDataException($"physical_footprints '{block}': separation_min_m ≥ 0 is required."),
                    SameTranslationM = Num(same, "translation_m") ?? throw new InvalidDataException($"physical_footprints '{block}': same_transform.translation_m."),
                    SameRotationRad = Num(same, "rotation_rad") ?? throw new InvalidDataException($"physical_footprints '{block}': same_transform.rotation_rad."),
                    SameScaleRel = Num(same, "scale_rel") ?? throw new InvalidDataException($"physical_footprints '{block}': same_transform.scale_rel."),
                });
            }
            return result;
        }

        private static BoqOwnershipRule ParseOwnership(JsonElement o, IReadOnlyDictionary<string, IReadOnlyList<string>> sets)
        {
            var layers = new List<string>(StrList(o, "layers"));
            if (Str(o, "layer_set") is { } set)
            {
                if (!sets.TryGetValue(set, out var members))
                    throw new InvalidDataException($"Ownership rule refers to an undefined layer set '{set}'.");
                layers.AddRange(members);
            }
            string? owner = null;
            var sameHandle = false;
            if (o.TryGetProperty("duplicate_of", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                owner = Str(d, "owner") ?? throw new InvalidDataException("duplicate_of needs an owner role.");
                sameHandle = Bool(d, "same_handle");
            }
            return new BoqOwnershipRule
            {
                Id = Str(o, "id") ?? "",
                Src = StrList(o, "src"),
                Layers = layers.Distinct(StringComparer.Ordinal).ToList(),
                LayerPrefix = Str(o, "layer_prefix"),
                Etypes = StrList(o, "etypes"),
                Kinds = StrList(o, "kinds"),
                Blocks = StrList(o, "blocks"),
                LayersFromPartKind = Str(o, "layers_from_part_kind"),
                DuplicateOwner = owner,
                DuplicateSameHandle = sameHandle,
                Reason = Str(o, "reason") ?? throw new InvalidDataException("An ownership rule needs a reason."),
            };
        }

        private static BoqCrosswalkConfig ParseCrosswalk(JsonElement c)
        {
            var widths = new Dictionary<string, double>(StringComparer.Ordinal);
            if (c.TryGetProperty("standard_width_by_layer", out var w) && w.ValueKind == JsonValueKind.Object)
                foreach (var p in w.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number) widths[p.Name] = p.Value.GetDouble();
            var config = new BoqCrosswalkConfig
            {
                Src = StrList(c, "src"),
                YellowLayers = StrList(c, "yellow_layers"),
                WhiteLayers = StrList(c, "white_layers"),
                HatchLayers = StrList(c, "hatch_layers"),
                BikeLayers = StrList(c, "bike_layers"),
                StandardWidthByLayer = widths,
                DefaultStandardWidth = Num(c, "default_standard_width") ?? 3.0,
                LinkDistance = Num(c, "link_m") ?? 6.0,
                NearDistance = Num(c, "near_m") ?? 3.5,
                MinMeasuredWidth = Num(c, "min_measured_width_m") ?? 1.0,
                AngleBin = Num(c, "angle_bin_rad") ?? 0.05,
                ParallelTolerance = Num(c, "parallel_tolerance_rad") ?? 0.1,
                EdgeMerge = Num(c, "edge_merge_m") ?? 0.05,
                BandInside = Num(c, "band_inside_m") ?? 0.02,
                BandAlong = Num(c, "band_along_m") ?? 1.0,
                DashedLinetypeScale = Num(c, "dashed_linetype_scale") ?? 0.5,
                EdgeOfDashed = Num(c, "edge_of_dashed_m") ?? 2.7,
                StubLength = Num(c, "stub_m") ?? 0.10,
                EdgeOverlap = Num(c, "edge_overlap") ?? 0.5,
                StandardWidthParam = Str(c, "standard_width_param") ?? throw new InvalidDataException("crosswalk_geometry needs standard_width_param."),
                FillParam = Str(c, "fill_param") ?? throw new InvalidDataException("crosswalk_geometry needs fill_param."),
                Note = Str(c, "note"),
            };
            if (c.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
                config = config with
                {
                    LabelMeasured = Str(labels, "measured") ?? config.LabelMeasured,
                    LabelHatch = Str(labels, "hatch") ?? config.LabelHatch,
                    LabelAssembly = Str(labels, "hatch_assembly") ?? config.LabelAssembly,
                    LabelStandard = Str(labels, "standard") ?? config.LabelStandard,
                    LabelStandardSolid = Str(labels, "standard_solid") ?? config.LabelStandardSolid,
                    LabelYellowWithWhite = Str(labels, "yellow_with_white") ?? config.LabelYellowWithWhite,
                    LabelYellowOnly = Str(labels, "yellow_only") ?? config.LabelYellowOnly,
                    LabelPedestrianBand = Str(labels, "pedestrian_band") ?? config.LabelPedestrianBand,
                    LabelBikeOnly = Str(labels, "bike_only") ?? config.LabelBikeOnly,
                };
            if (c.TryGetProperty("reasons", out var reasons) && reasons.ValueKind == JsonValueKind.Object)
                config = config with
                {
                    ReasonOnYellow = Str(reasons, "on_yellow") ?? config.ReasonOnYellow,
                    ReasonAtHatch = Str(reasons, "at_hatch") ?? config.ReasonAtHatch,
                    ReasonAssemblyEdge = Str(reasons, "assembly_edge") ?? config.ReasonAssemblyEdge,
                    ReasonAssemblyBoundary = Str(reasons, "assembly_boundary") ?? config.ReasonAssemblyBoundary,
                    ReasonNoise = Str(reasons, "noise") ?? config.ReasonNoise,
                    MissingHatchWhat = Str(reasons, "missing_hatch") ?? config.MissingHatchWhat,
                };
            if (config.Src.Count == 0) throw new InvalidDataException("crosswalk_geometry needs the source role(s) holding the geometry.");
            return config;
        }

        private static BoqSignSizes ParseSignSizes(JsonElement root)
        {
            var circle = new Dictionary<string, int[]>(StringComparer.Ordinal);
            var triangle = new Dictionary<string, int[]>(StringComparer.Ordinal);
            var rect = new Dictionary<string, (int W, int H)[]>(StringComparer.Ordinal);
            if (root.TryGetProperty("sign_sizes_cm", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                if (s.TryGetProperty("circle_d", out var c) && c.ValueKind == JsonValueKind.Object)
                    foreach (var p in c.EnumerateObject()) circle[p.Name] = Triple(p.Value, p.Name);
                if (s.TryGetProperty("triangle_a", out var t) && t.ValueKind == JsonValueKind.Object)
                    foreach (var p in t.EnumerateObject()) triangle[p.Name] = Triple(p.Value, p.Name);
                if (s.TryGetProperty("rect_wh", out var r) && r.ValueKind == JsonValueKind.Object)
                    foreach (var p in r.EnumerateObject())
                    {
                        if (p.Value.ValueKind != JsonValueKind.Array || p.Value.GetArrayLength() != 3)
                            throw new InvalidDataException($"Sign {p.Name}: rect_wh needs three [w,h] sizes.");
                        rect[p.Name] = p.Value.EnumerateArray()
                            .Select(wh => (wh[0].GetInt32(), wh[1].GetInt32())).ToArray();
                    }
            }
            return new BoqSignSizes { Circle = circle, Triangle = triangle, Rect = rect };

            static int[] Triple(JsonElement e, string name)
            {
                if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 3)
                    throw new InvalidDataException($"Sign {name}: three sizes (urban, non-urban single, non-urban dual) are required.");
                return e.EnumerateArray().Select(v => v.GetInt32()).ToArray();
            }
        }

        private static BoqPricebook ParsePricebook(JsonElement root)
        {
            if (!root.TryGetProperty("pricebook", out var pb) || pb.ValueKind != JsonValueKind.Object) return new BoqPricebook();
            var items = new Dictionary<string, BoqPricebookItem>(StringComparer.Ordinal);
            if (pb.TryGetProperty("items", out var its) && its.ValueKind == JsonValueKind.Object)
                foreach (var p in its.EnumerateObject())
                {
                    decimal? price = null;
                    if (p.Value.TryGetProperty("base_price", out var bp) && bp.ValueKind == JsonValueKind.Number) price = bp.GetDecimal();
                    items[p.Name] = new BoqPricebookItem(p.Name, Str(p.Value, "descr") ?? "", Str(p.Value, "unit") ?? "", price);
                }
            // Rules 2.3: spaces lost in the catalog text are restored when the description is shown (reference DESCR_FIX);
            // only a missing space, nothing else. [["27בקוטר", "27 בקוטר"], ...]
            var fixes = new List<(string From, string To)>();
            if (pb.TryGetProperty("descr_fix", out var df) && df.ValueKind == JsonValueKind.Array)
                foreach (var pair in df.EnumerateArray())
                    if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2 &&
                        pair[0].ValueKind == JsonValueKind.String && pair[1].ValueKind == JsonValueKind.String)
                        fixes.Add((pair[0].GetString()!, pair[1].GetString()!));
            return new BoqPricebook
            {
                Id = Str(pb, "id") ?? "",
                Name = Str(pb, "name") ?? "",
                Edition = Str(pb, "edition") ?? "",
                SourceSha256 = Str(pb, "source_sha256") ?? "",
                Note = Str(pb, "note") ?? "",
                Items = items,
                DescriptionFixes = fixes,
            };
        }

        private static BoqWorkbookText ParseWorkbook(JsonElement root)
        {
            if (!root.TryGetProperty("workbook", out var wb) || wb.ValueKind != JsonValueKind.Object) return new BoqWorkbookText();
            var explanation = new List<(string, bool)>();
            if (wb.TryGetProperty("explanation", out var ex) && ex.ValueKind == JsonValueKind.Array)
                foreach (var e in ex.EnumerateArray())
                    explanation.Add(e.ValueKind == JsonValueKind.String ? (e.GetString()!, false) : (Str(e, "text") ?? "", Bool(e, "bold")));
            var defaults = new BoqWorkbookText();
            var unclassified = new List<BoqUnclassifiedNote>();
            if (wb.TryGetProperty("unclassified_notes", out var un) && un.ValueKind == JsonValueKind.Array)
                foreach (var e in un.EnumerateArray())
                    unclassified.Add(new BoqUnclassifiedNote(Str(e, "src"), Str(e, "layer"), Str(e, "kind"), Str(e, "types_contain"), Str(e, "note") ?? ""));
            return new BoqWorkbookText
            {
                Title = Str(wb, "title") ?? defaults.Title,
                Subtitle = Str(wb, "subtitle") ?? defaults.Subtitle,
                Explanation = explanation.Count > 0 ? explanation : defaults.Explanation,
                ExplanationComplete = explanation.Count > 0 && Bool(wb, "explanation_complete"),
                Legend = wb.TryGetProperty("legend", out var lg) ? StrList(lg) : defaults.Legend,
                TotalLabel = Str(wb, "total_label") ?? defaults.TotalLabel,
                ParamsFooter = Str(wb, "params_footer") ?? defaults.ParamsFooter,
                SignsNote = Str(wb, "signs_note"),
                ObjectsNotes = wb.TryGetProperty("objects_notes", out var on) ? StrList(on) : defaults.ObjectsNotes,
                NotIncludedTitle = Str(wb, "not_included_title") ?? defaults.NotIncludedTitle,
                CountOfDetail = Str(wb, "count_of_detail") ?? defaults.CountOfDetail,
                UnclassifiedNotes = unclassified,
            };
        }

        // ---- JSON helpers ----
        internal static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        internal static double? Num(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        internal static bool Bool(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        internal static IReadOnlyList<string> StrList(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? StrList(v) : Array.Empty<string>();

        internal static IReadOnlyList<string> StrList(JsonElement v) =>
            v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : Array.Empty<string>();
    }

    public sealed record BoqParameter(string Id, string Label, double Value, string Status, string Source);

    public sealed record BoqChapter(string Id, string Title);

    public sealed record BoqSourceRole(string Id, string FilePattern, string? Label)
    {
        /// <summary>What this drawing contributes, for the sources sheet (lane key "contribution", rules v4.1).</summary>
        public string? Contribution { get; init; }

        /// <summary>Case-insensitive substring match on the drawing file name (e.g. "-GM-").</summary>
        public bool Matches(string? drawingPath)
        {
            if (string.IsNullOrWhiteSpace(FilePattern) || string.IsNullOrWhiteSpace(drawingPath)) return false;
            return Path.GetFileName(drawingPath).Contains(FilePattern, StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class BoqLine
    {
        public required string Id { get; init; }
        public required string Chapter { get; init; }
        public string? Item { get; init; }
        public string? ItemUrban { get; init; }
        public string? ItemRural { get; init; }
        public required string Unit { get; init; }
        public string? Confirm { get; init; }
        /// <summary>A jointly decided estimating assumption (item, factor or scope) with its reason and alternative: status
        /// 'הנחת אומדן' and a 'הנחת אומדן:' note — not a measurement; a 'confirm' on the same line still wins ('לאישור').</summary>
        public string? Assumption { get; init; }
        /// <summary>Rules 2.3: the line's own name (a line without an item shows it instead of its part labels).</summary>
        public string? Label { get; init; }
        /// <summary>Rules 2.3: objects not counted that need a check — status "· לבדיקה" and a "לבדיקה:" note on the BoQ row.</summary>
        public string? Review { get; init; }
        /// <summary>Rules 2.3: a drawing fact shown on the BoQ row (never a quantity).</summary>
        public string? DrawingNote { get; init; }
        /// <summary>Rules 2.7 (M1): layers whose held-back objects are named in this line's note (not only in 'לא נכלל').</summary>
        public IReadOnlyList<string> HeldNoteLayers { get; init; } = Array.Empty<string>();
        public IReadOnlyList<BoqPart> Parts { get; init; } = Array.Empty<BoqPart>();
        /// <summary>Drawing objects shown next to the line for comparison only (never a quantity); null when none.</summary>
        public BoqLineControl? Control { get; init; }

        /// <summary>The item for a road class: urban (1) or rural (2, 3) when the line has both, else the single item.</summary>
        public string? ItemFor(int roadClass) => ItemUrban != null ? (roadClass == 1 ? ItemUrban : ItemRural) : Item;

        /// <summary>True when the line has a price-list item (single, or urban/rural).</summary>
        public bool HasItem => !string.IsNullOrEmpty(Item) || ItemUrban != null;

        /// <summary>
        /// The reference REF(line): "urban/rural-last-4" (51.32.1852/1862) for an urban/rural line, else the item, else the
        /// line label, else its part labels joined by " / ".
        /// </summary>
        public string Ref()
        {
            if (ItemUrban != null)
            {
                var rural = ItemRural ?? "";
                return ItemUrban + "/" + (rural.Length > 4 ? rural[^4..] : rural);
            }
            if (!string.IsNullOrEmpty(Item)) return Item!;
            if (!string.IsNullOrEmpty(Label)) return Label!;
            return string.Join(" / ", Parts.Select(p => p.Label));
        }
    }

    /// <summary>A chapter of the example that the draft does not measure: named on the BoQ sheet with its reason, no quantity.</summary>
    public sealed record BoqNotIncluded(string Id, string Title, string Reason);

    /// <summary>A part's alternative factor shown beside its quantity for approval (rules 2.3 "alt").</summary>
    public sealed record BoqPartAlternative(double Value, string Label);

    /// <summary>A sign number shown with another one it covers (block "505(506)" → "505/506").</summary>
    public sealed record BoqSignCombinedLabel(string Number, string BlockContains, string Label);

    /// <summary>A note for sign blocks drawn on a given layer ("{n}" = their count, "{layer}" = the layer).</summary>
    public sealed record BoqSignLayerNote(string Layer, string Note);

    /// <summary>
    /// A note on an unclassified group: matched by file, layer and measure kind, or by file, kind and a text contained in the
    /// group's block / entity types. "{poles}" = the pole count, "{ref:LINE}" = that line's item.
    /// </summary>
    public sealed record BoqUnclassifiedNote(string? Src, string? Layer, string? Kind, string? TypesContain, string Note);

    /// <summary>
    /// Rules 2.3 (v4) sign texts. "{from}", "{to}" = the sign range, "{hits}" = the files with names in the range, as the
    /// reference prints a dict ({'SM': 2}), "{n}" = a count, "{signs}" / "{poles}" = the sign and pole counts.
    /// </summary>
    public sealed record BoqSignTexts
    {
        public string DetailAbsent { get; init; } = "נבדקו שמות הבלוקים והשכבות בכל הקבצים — לא נמצאו מספרים {from}–{to}; טקסטים בשרטוט וקבצים מופנים (XREF) לא נבדקו";
        public string DetailFound { get; init; } = "נמצאו שמות עם מספרים {from}–{to}: {hits} — לבדיקה";
        public string DetailFactorAbsent { get; init; } = "אין שלטים {from}–{to} בשרטוט — אין שטח למדידה";
        public string DetailFactorFound { get; init; } = "שטח השלט מהתכנית — למדוד";
        public string BoqAbsent { get; init; } = "כמות 0: נבדקו שמות הבלוקים והשכבות בכל הקבצים — לא נמצאו שלטים {from}–{to}; טקסטים בשרטוט וקבצים מופנים (XREF) לא נבדקו";
        public string BoqFound { get; init; } = "כמות 0: נמצאו שמות עם מספרים {from}–{to} ({hits}) — לבדיקה";
        public IReadOnlyList<BoqSignCombinedLabel> CombinedLabels { get; init; } = Array.Empty<BoqSignCombinedLabel>();
        public IReadOnlyList<BoqSignLayerNote> LayerNotes { get; init; } = Array.Empty<BoqSignLayerNote>();
        /// <summary>The parameter of the pole part(s) (count_x); null = no poles-vs-signs note.</summary>
        public string? PolesParam { get; init; }
        public string PolesDetailNote { get; init; } = "בשרטוט {signs} תמרורים על {poles} עמודים";
        public string PolesBoqNote { get; init; } = "בשרטוט {signs} תמרורים על {poles} עמודים — לאישור";
    }

    /// <summary>
    /// A line-level control (rules 2.1): the still-unclaimed records of these roles / layers / kind are measured one object
    /// once (object_length at <see cref="ObjectWidth"/>) and reported beside the line, then excluded — never a quantity.
    /// <see cref="Reason"/> (optional key "reason", "{line}" = the line id) overrides the generated exclusion reason.
    /// </summary>
    public sealed class BoqLineControl
    {
        public required string Label { get; init; }
        public IReadOnlyList<string> Src { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Layers { get; init; } = Array.Empty<string>();
        public string Kind { get; init; } = "length";
        public double ObjectWidth { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>The ruleset's "measurement" section (rules 2.1). Defaults are the reference constants of boq_geometry.py.</summary>
    public sealed class BoqMeasurement
    {
        /// <summary>True when the ruleset carries the section.</summary>
        public bool Present { get; init; }
        public string LengthBasis { get; init; } = "plan";
        public string? LengthRule { get; init; }
        public string? CountRule { get; init; }
        public double ToleranceM { get; init; } = BoqObjectMeasure.ToleranceM;
        public double PieceM { get; init; } = BoqObjectMeasure.PieceM;
        public double ParallelSin { get; init; } = BoqObjectMeasure.ParallelSin;
    }

    /// <summary>
    /// A block definition as read: DXF entity counts, plan envelope (block units), base point, units code and the canonical
    /// geometry digest of its entities (boq_geometry.definition_digest; null when an entity type has no descriptor).
    /// </summary>
    public sealed record BoqDefinitionSignature(IReadOnlyDictionary<string, int> DxfCounts, double[] Envelope, double[] BasePoint, int UnitsCode,
        string? GeometryDigest = null)
    {
        /// <summary>
        /// Same geometry digest, counts, units and base point (1e-9), envelope within 0.1% of its size — as
        /// build_boq_v7.signature_matches. Without a digest nothing matches: a body moved inside the same counts and envelope
        /// is not the approved one (Codex 00:47).
        /// </summary>
        public bool Matches(BoqDefinitionSignature approved)
        {
            if (string.IsNullOrEmpty(GeometryDigest) || !string.Equals(GeometryDigest, approved.GeometryDigest, StringComparison.Ordinal))
                return false;
            if (UnitsCode != approved.UnitsCode || DxfCounts.Count != approved.DxfCounts.Count ||
                approved.DxfCounts.Any(c => !DxfCounts.TryGetValue(c.Key, out var n) || n != c.Value))
                return false;
            if (BasePoint.Length != 3 || approved.BasePoint.Length != 3 || Envelope.Length != 4 || approved.Envelope.Length != 4) return false;
            if (BasePoint.Zip(approved.BasePoint).Any(p => Math.Abs(p.First - p.Second) > 1e-9)) return false;
            var size = Math.Max(approved.Envelope[2] - approved.Envelope[0], approved.Envelope[3] - approved.Envelope[1]);
            return Envelope.Zip(approved.Envelope).All(p => Math.Abs(p.First - p.Second) <= 1e-3 * size);
        }
    }

    /// <summary>
    /// Rules 2.8 physical_footprints entry: the approved body of one block definition (block units), bound to the definition
    /// signature it was decided on, with the positive separation and same-transform tolerances.
    /// </summary>
    public sealed class BoqPhysicalFootprint
    {
        public required string Block { get; init; }
        public string Decision { get; init; } = "";
        public required BoqDefinitionSignature Signature { get; init; }
        public required double[] BodyEnvelope { get; init; }
        public double SeparationMinM { get; init; }
        public double SameTranslationM { get; init; }
        public double SameRotationRad { get; init; }
        public double SameScaleRel { get; init; }
    }

    public sealed class BoqPart
    {
        public required string Label { get; init; }
        public required string Kind { get; init; }
        public IReadOnlyList<string> Src { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Layers { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> BlockLayers { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Etypes { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Signs { get; init; } = Array.Empty<string>();
        public (int From, int To)? SignRange { get; init; }
        public string? Param { get; init; }
        public double? MaxLength { get; init; }
        public string? Confirm { get; init; }
        /// <summary>A decided estimating assumption of this part (e.g. a factor from the engineer's own sheet): note only.</summary>
        public string? Assumption { get; init; }
        public bool OpenOnly { get; init; }
        public bool ClosedOnly { get; init; }
        /// <summary>
        /// Length parts (rules 2.1): the largest spacing between the parallel lines of ONE object; the part's quantity is
        /// then the plan length of its objects, each counted once. Null (a 2.0 ruleset): the sum of the record quantities.
        /// </summary>
        public double? ObjectWidth { get; init; }
        public string? ObjectWidthNote { get; init; }
        /// <summary>Count parts (rules 2.1): the same block (or layer) within this distance of a kept one in the same line is counted once.</summary>
        public double? SamePoint { get; init; }
        /// <summary>line_sum parts: the lines whose rounded quantities are summed.</summary>
        public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
        /// <summary>line_sum parts: who decided the rule (shown in the workbook).</summary>
        public string? DecidedBy { get; init; }
        /// <summary>Rules 2.3: block-name filter (exact names) of a layer-selected part; empty = every block.</summary>
        public IReadOnlyList<string> Blocks { get; init; } = Array.Empty<string>();
        /// <summary>Rules 2.3 count_of parts (key "line"): the line whose objects this part counts again (pole caps = poles).</summary>
        public string? Of { get; init; }
        /// <summary>Rules 2.3: an alternative factor shown beside the quantity for approval (never used for the quantity).</summary>
        public BoqPartAlternative? Alt { get; init; }
        /// <summary>Rules 2.7: the short text of this part in the BoQ row note (the full method stays on 'פירוט חישוב').</summary>
        public string? Brief { get; init; }
    }

    /// <summary>
    /// One ownership / duplicate rule. A record matches when every present filter matches; the first matching rule (in
    /// file order) excludes it with <see cref="Reason"/> ("{src}" is replaced by the record's source role).
    /// duplicate_of: the record is excluded only when a record of the owner role has the same kind, the same
    /// 0.1-rounded extents and the same 0.1-rounded quantity (and, with same_handle, any owner record has its handle).
    /// </summary>
    public sealed class BoqOwnershipRule
    {
        public required string Id { get; init; }
        public IReadOnlyList<string> Src { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Layers { get; init; } = Array.Empty<string>();
        public string? LayerPrefix { get; init; }
        public IReadOnlyList<string> Etypes { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Kinds { get; init; } = Array.Empty<string>();
        /// <summary>Block names (exact); an ownership rule with blocks only matches block records of those names.</summary>
        public IReadOnlyList<string> Blocks { get; init; } = Array.Empty<string>();
        public string? LayersFromPartKind { get; init; }
        public string? DuplicateOwner { get; init; }
        public bool DuplicateSameHandle { get; init; }
        public required string Reason { get; init; }
    }

    public sealed record BoqCrosswalkConfig
    {
        public IReadOnlyList<string> Src { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> YellowLayers { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> WhiteLayers { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> HatchLayers { get; init; } = Array.Empty<string>();
        /// <summary>
        /// Bike-crossing square-row layers (812). A measured yellow outline whose band between two consecutive edges
        /// holds an 812 row is a bike crossing beside the zebra: only the pedestrian band is zebra paint.
        /// </summary>
        public IReadOnlyList<string> BikeLayers { get; init; } = Array.Empty<string>();
        /// <summary>Parallel yellow edges closer than this are one drawn edge.</summary>
        public double EdgeMerge { get; init; } = 0.05;
        /// <summary>An 812 row must lie this far inside a band to mark it as a bike band.</summary>
        public double BandInside { get; init; } = 0.02;
        /// <summary>Along the crossing, an 812 row may lie this far beyond the yellow edges.</summary>
        public double BandAlong { get; init; } = 1.0;
        public IReadOnlyDictionary<string, double> StandardWidthByLayer { get; init; } = new Dictionary<string, double>();
        public double DefaultStandardWidth { get; init; } = 3.0;
        public double LinkDistance { get; init; } = 6.0;
        public double NearDistance { get; init; } = 3.5;
        public double MinMeasuredWidth { get; init; } = 1.0;
        public double AngleBin { get; init; } = 0.05;
        public double ParallelTolerance { get; init; } = 0.1;
        /// <summary>
        /// Rules 2.3 (v4): a white crossing line with exactly this linetype scale is the crossing's dashed line (DASHED ×0.5 =
        /// the 50/50 stripe pattern). A hatch cluster with such a line is one assembly: its dashed line × standard width plus
        /// the measured hatch area.
        /// </summary>
        public double DashedLinetypeScale { get; init; } = 0.5;
        /// <summary>A continuous white line this close (across) to an assembly's dashed line, parallel and along it, is its edge.</summary>
        public double EdgeOfDashed { get; init; } = 2.7;
        /// <summary>The edge line must overlap the dashed line along its direction by this share of the shorter one.</summary>
        public double EdgeOverlap { get; init; } = 0.5;
        public string StandardWidthParam { get; init; } = "";
        public string FillParam { get; init; } = "";
        public string? Note { get; init; }
        public string LabelMeasured { get; init; } = "קווי שוליים צהובים (רוחב נמדד)";
        public string LabelHatch { get; init; } = "הצללה בלבד (שטח מדוד)";
        public string LabelAssembly { get; init; } = "מעבר עם הצללה ליד קו העצירה: זברה לפי הקו המקווקו (רוחב תקני) + שטח ההצללה";
        public string LabelStandard { get; init; } = "קו לבן מקווקו בלבד (רוחב תקני)";
        public string LabelStandardSolid { get; init; } = "קו לבן בלבד (רוחב תקני)";
        /// <summary>v4.1: a white-only group that contains a lone yellow line (reference sm_geometry, CW4-151).</summary>
        public string LabelYellowWithWhite { get; init; } = "קו צהוב בודד + קו לבן (רוחב תקני)";
        public string LabelYellowOnly { get; init; } = "קו צהוב בודד (רוחב תקני)";
        /// <summary>v4.1: in a white-only / lone-yellow group a handle whose every piece is shorter than this is noise (STUB_M).</summary>
        public double StubLength { get; init; } = 0.10;
        public string LabelPedestrianBand { get; init; } = "קווי שוליים צהובים — רצועת הולכי רגל (רוחב נמדד)";
        public string LabelBikeOnly { get; init; } = "מעבר אופניים בלבד — נספר בסעיף 812";
        public string ReasonOnYellow { get; init; } = "מעבר חציה: אותו מעבר משורטט פעמיים (קו לבן / הצללה על מעבר עם קווי שוליים) — נספר פעם אחת";
        public string ReasonAtHatch { get; init; } = "מעבר חציה: קו לבן במעבר שנספר לפי ההצללה שלו";
        public string ReasonAssemblyEdge { get; init; } = "מעבר חציה: קו רציף לאורך מעבר עם הצללה, ליד הקו המקווקו של אותו מעבר — חלק מאותו מעבר, לא נספר שוב";
        public string ReasonAssemblyBoundary { get; init; } = "מעבר חציה: גבול ההצללה (או עותק שלו) — השטח נספר פעם אחת";
        public string ReasonNoise { get; init; } = "מעבר חציה: קטע קו קצר מ-5 ס\"מ — רעש שרטוט, לא מעבר";
        public string MissingHatchWhat { get; init; } = "הצללה ששטחה לא הוחזר וגם לא חושב מהגבול";

        public IReadOnlySet<string> AllLayers =>
            YellowLayers.Concat(WhiteLayers).Concat(HatchLayers).ToHashSet(StringComparer.Ordinal);
    }

    public sealed class BoqSignSizes
    {
        public IReadOnlyDictionary<string, int[]> Circle { get; init; } = new Dictionary<string, int[]>();
        public IReadOnlyDictionary<string, int[]> Triangle { get; init; } = new Dictionary<string, int[]>();
        public IReadOnlyDictionary<string, (int W, int H)[]> Rect { get; init; } = new Dictionary<string, (int W, int H)[]>();

        public bool Knows(string number) => Circle.ContainsKey(number) || Triangle.ContainsKey(number) || Rect.ContainsKey(number);

        /// <summary>Sign face area in m² for road class 1..3 (table 3): circle π(d/2)², triangle √3/4·a², rectangle w·h.</summary>
        public double AreaM2(string number, int roadClass)
        {
            var k = Math.Clamp(roadClass, 1, 3) - 1;
            if (Circle.TryGetValue(number, out var d)) return Math.PI * Math.Pow(d[k] / 200.0, 2);
            if (Triangle.TryGetValue(number, out var a)) return Math.Sqrt(3) / 4 * Math.Pow(a[k] / 100.0, 2);
            if (Rect.TryGetValue(number, out var wh)) return wh[k].W * wh[k].H / 10000.0;
            throw new InvalidDataException($"Sign {number} has no size in the ruleset (sign_sizes_cm).");
        }
    }

    public sealed record BoqPricebookItem(string Code, string Description, string Unit, decimal? BasePrice);

    public sealed class BoqPricebook
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Edition { get; init; } = "";
        public string SourceSha256 { get; init; } = "";
        public string Note { get; init; } = "";
        public IReadOnlyDictionary<string, BoqPricebookItem> Items { get; init; } = new Dictionary<string, BoqPricebookItem>();
        /// <summary>Missing spaces restored in catalog descriptions (reference DESCR_FIX), in order.</summary>
        public IReadOnlyList<(string From, string To)> DescriptionFixes { get; init; } = Array.Empty<(string, string)>();

        /// <summary>The catalog description with <see cref="DescriptionFixes"/> applied (reference descr_of).</summary>
        public string Describe(BoqPricebookItem item)
        {
            var text = item.Description;
            foreach (var (lost, restored) in DescriptionFixes) text = text.Replace(lost, restored, StringComparison.Ordinal);
            return text;
        }
    }

    public sealed class BoqWorkbookText
    {
        /// <summary>"{project}" and "{date}" are replaced when the workbook is written.</summary>
        public string Title { get; init; } = "כתב כמויות — פרויקט {project} — טיוטה לפי כללים, {date}";
        public string Subtitle { get; init; } = "טיוטה — המיפוי והמחירים לאישור; לא אומדן מאושר.";
        public IReadOnlyList<(string Text, bool Bold)> Explanation { get; init; } = new List<(string, bool)>
        {
            ("איך בנוי הקובץ", true),
            ("• \"כתב כמויות\" — מספר סעיף, תאור מהמחירון, יחידה, כמות, מחיר, סה\"כ. עמודת כמות אחת.", false),
            ("• \"פירוט חישוב\" — מאיפה כל כמות: קבצים, שכבות, כמות מדודה, והמקדם.", false),
            ("• \"פרמטרים\" — כל ההנחות במקום אחד (תאים צהובים); שינוי מעדכן הכול.", false),
            ("• \"לא נכלל\" — כפילויות וחלופות שנוכו, עם הסיבה. \"לא סווג\" — שכבות בלי כלל. \"חסרים\" — עצמים שלא נמדדו.", false),
        };
        public IReadOnlyList<string> Legend { get; init; } = new[]
        {
            "מקרא מצב: מוכן = סעיף, כמות ושיטה לפי הכללים · לאישור = הנחה מסומנת בהערה · סעיף לבחירה = הכמות מדודה, סעיף המחירון תלוי בהחלטה · חלקי = יש עצמים שלא נמדדו.",
            "כל כמות מחושבת בגיליון \"פירוט חישוב\" מכמות מדודה × מקדם מגיליון \"פרמטרים\".",
        };
        public string TotalLabel { get; init; } = "סה\"כ טיוטת אומדן — השורות עם מחיר (ללא מע\"מ, ללא הנחות/תוספות). מחיר שיוקלד בשורת \"סעיף לבחירה\" נכנס לסה\"כ";
        /// <summary>True when the ruleset's explanation is complete: it is written as is, without the generated bullets.</summary>
        public bool ExplanationComplete { get; init; }
        /// <summary>The note under the parameters table.</summary>
        public string ParamsFooter { get; init; } = "תאים צהובים ניתנים לשינוי; כל הכמויות, המחירים והסיכומים מתעדכנים מיד.";
        /// <summary>The note under the signs table; null = built from the ruleset's signs_size source.</summary>
        public string? SignsNote { get; init; }
        /// <summary>The paragraphs under the one-object-once sheet; empty = generated from the result.</summary>
        public IReadOnlyList<string> ObjectsNotes { get; init; } = Array.Empty<string>();
        public string NotIncludedTitle { get; init; } = "פרקים מהדוגמה שלא נכללו בטיוטה זו";
        /// <summary>The calculation-detail text of a count_of part ("{ref}" = the counted line's item).</summary>
        public string CountOfDetail { get; init; } = "מספר העצמים בסעיף {ref}";
        public IReadOnlyList<BoqUnclassifiedNote> UnclassifiedNotes { get; init; } = Array.Empty<BoqUnclassifiedNote>();
    }
    /// <summary>
    /// Lane key "array_members" (rules v4.1, review MARK-V4-01): the texts of the bike symbols inside path arrays. {near} = the
    /// distance to the model-space symbol it duplicates, {n} = symbols in the array, {line}/{layer} = the path line.
    /// </summary>
    public sealed record BoqArrayMembersConfig
    {
        public string MemberBlock { get; init; } = "BIKE";
        public string MemberReason { get; init; } = "סמל אופניים בתוך מערך (Array), בקצה המערך — {near} מ' מסמל אופניים בודד; אותו מיקום, נספר פעם אחת";
        public string ArrayReason { get; init; } = "מערך (Array) של {n} סמלי אופניים לאורך הקו {line} בשכבת {layer} — הסמלים שבתוכו נספרו אחד-אחד בסעיף 51.32.1942 (804)";
        public string ArrayReasonNoLine { get; init; } = "מערך (Array) של {n} סמלי אופניים לאורך קו שלא זוהה — הסמלים שבתוכו נספרו אחד-אחד בסעיף 51.32.1942 (804)";
        public string PathLineSuffix { get; init; } = " · לאורכו מערך של {n} סמלי אופניים (כ-20 מ' בין מיקומים) — לבדיקה אם זה קו נתיב אופניים (804) ששייך לסעיף 51.32.1852";
    }

    /// <summary>
    /// Lane key "note_figures" (reference compute_note_figures.py, v4.5): the lowered curb (GM HW-EVEN_MUN) cut into pieces of
    /// at most <see cref="StepM"/>; a piece is beside an item when a chord of the item's layers runs parallel (&lt;
    /// <see cref="MaxAngleRad"/>) within <see cref="ReachM"/> and the piece's midpoint projects inside that chord; the stone
    /// possibly in both curb items = C1 pieces with a C2 line <see cref="PairLoM"/>–<see cref="PairHiM"/> beside them; the 815
    /// island frame = its lines longer than <see cref="FrameMinM"/>; the 808 long rectangles = every line of their layer.
    /// </summary>
    public sealed record BoqNoteFiguresConfig
    {
        public string Src { get; init; } = "GM";
        public IReadOnlyList<string> LoweredLayers { get; init; } = new[] { "HW-EVEN_MUN" };
        public IReadOnlyList<string> C1Layers { get; init; } = new[] { "HW-CURB", "HW-CURB2" };
        public IReadOnlyList<string> C2Layers { get; init; } = new[] { "TR-ISLAND-CURBSTONE", "TR-INNER-ISLAND-CURBSTONE", "HW-CURB-ILND", "HW-CURB-ILND-IN" };
        public double StepM { get; init; } = 0.25;
        public double ReachM { get; init; } = 0.35;
        public double MaxAngleRad { get; init; } = 0.2;
        public double LoweredWidthM { get; init; } = 0.02;
        public double PairLoM { get; init; } = 0.165;
        public double PairHiM { get; init; } = 0.265;
        public double PairWidthM { get; init; } = 0.23;
        public string MarkSrc { get; init; } = "SM";
        public IReadOnlyList<string> LineTypes { get; init; } = new[] { "LINE", "POLYLINE", "POLYLINE2D" };
        public string FrameLayer { get; init; } = "TR-MARK-WHT-815";
        public double FrameMinM { get; init; } = 10.0;
        public double FrameWidthM { get; init; } = 0.20;
        public double FrameAltWidthM { get; init; } = 0.10;
        public string LongRectLayer { get; init; } = "TR-MARK-WHT-3-1.5-808";
    }

    /// <summary>
    /// Lane key "ha_overlap" (reference compute_ha_overlap.py, v4.5): the area hatches of these layers — boundary polygons
    /// (from the read, never Civil's measurement) summed / united per layer and intersected between layers, the boundary
    /// area of the hatches whose state is not measured, and how many polygons agree with Civil's own area. ESTIMATES only:
    /// nothing here changes a quantity.
    /// </summary>
    /// <summary>Lane key "example_items": a title row and one row per chapter (items joined with '; ', status, action).</summary>
    public sealed record BoqExampleItems(string Title, IReadOnlyList<BoqExampleItemRow> Rows);

    public sealed record BoqExampleItemRow(string Chapter, string Items, string Status, string Action);

    public sealed record BoqHaOverlapConfig
    {
        public IReadOnlyList<string> Layers { get; init; } = new[] { "HW-HTCH-ROAD", "HW-HTCH-NATAZ", "HW_HA_SIDEWALK", "HACTH-GINUN", "PL-BIKE", "HW-HATCH-ILND" };
        public IReadOnlyList<string> MeasuredStates { get; init; } = new[] { "direct", "strict_boundary_recovered" };
        public double MinBetweenM2 { get; init; } = 0.5;
        public double AgreeAbsM2 { get; init; } = 0.5;
        public double AgreeRel { get; init; } = 0.01;
    }
}
