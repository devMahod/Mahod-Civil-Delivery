using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Estimate.BoqRulesV2
{
    public enum BoqBucket { None, Item, Excluded, Unclassified }

    /// <summary>One part of a BoQ line: the records it claimed, its measured base and its quantity at the parameters.</summary>
    public sealed class BoqPartResult
    {
        public required BoqLine Line { get; init; }
        public required int Index { get; init; }
        public required BoqPart Part { get; init; }
        public string LineId => Line.Id;
        public int Objects { get; internal set; }
        public double Base { get; internal set; }
        public int Missing { get; internal set; }
        public List<string> Notes { get; } = new();
        public BoqCounter Layers { get; } = new();
        public BoqCounter Srcs { get; } = new();
        /// <summary>Sign numbers (sign parts) or block names (block-count parts).</summary>
        public BoqCounter Signs { get; } = new();
        public IReadOnlyList<BoqCrossing>? Crossings { get; internal set; }
        public double? ParameterValue { get; internal set; }
        public double Quantity { get; internal set; }

        // ---- one physical object once (length parts with object_width_m; null otherwise) ----
        /// <summary>The sum of the record quantities, every line as drawn (as measured before 2.1; 3D where Z was set).</summary>
        public double? DrawnSum { get; internal set; }
        /// <summary>The plan length of every chord (no object merged) plus the quantities of records without plan geometry.</summary>
        public double? PlanSum { get; internal set; }
        /// <summary>Length counted at each multiplicity m (m parallel lines of one object at that place).</summary>
        public IReadOnlyDictionary<int, double>? ByMultiplicity { get; internal set; }
        public double? Width { get; internal set; }
        public string? WidthNote { get; internal set; }
        /// <summary>Records measured by their original quantity because they have no complete plan geometry.</summary>
        public int NoGeometry { get; internal set; }
    }

    /// <summary>A count record removed because the same block (or layer) of the same line is already counted at that point.</summary>
    public sealed record BoqDedupRow(string LineId, string PartLabel, string Src, string Layer, string Block, string Handle, string Kept);

    /// <summary>Rules 2.8: an approved-footprint block counted again at the same point — its body is positively separated from every twin.</summary>
    public sealed record BoqDistinctRow(string LineId, string PartLabel, string Src, string Handle, IReadOnlyList<string> Twins, double BodyGapM);

    /// <summary>Rules 2.8: an approved-footprint block counted once because its separate body is not proven (never silently).</summary>
    public sealed record BoqFootprintReviewRow(string LineId, string PartLabel, string Src, string Handle, string Kept, string Reason);

    /// <summary>A line control: its objects, their one-object-once plan length and their drawn sum. Never a quantity.</summary>
    public sealed record BoqControlResult(string LineId, string Label, int Objects, double Length, double Drawn);

    public sealed class BoqLineResult
    {
        public required BoqLine Line { get; init; }
        public string? Item { get; init; }
        public required IReadOnlyList<BoqPartResult> Parts { get; init; }
        public double Quantity => Parts.Sum(p => p.Quantity);
        public int Missing => Parts.Sum(p => p.Missing);
    }

    /// <summary>An object whose quantity is unknown. <see cref="LineId"/> is the owning line's id ("—" when no line owns it).</summary>
    public sealed record BoqMissingRow(string LineId, string Src, string Layer, string Handle, string What, string Action);

    public sealed record BoqGroup(string Reason, string Src, string Layer, string Kind, int Count, double Quantity,
        IReadOnlyList<KeyValuePair<string, int>> Types)
    {
        /// <summary>v4.4 unclassified: plan length of a length group (chords; a record without chords keeps its as-drawn
        /// length), else the quantity.</summary>
        public double? Plan { get; init; }
        /// <summary>v4.4 unclassified: records whose as-drawn length exceeds the plan by more than 1 m (a wrong Z).</summary>
        public int ZCount { get; init; }
        /// <summary>The record with the largest such excess (its handle), or null.</summary>
        public string? ZExampleHandle { get; init; }
        /// <summary>The sum of those excesses (m).</summary>
        public double ZExcess { get; init; }
    }

    public sealed class BoqEngineResult
    {
        public required BoqRuleset Rules { get; init; }
        public required BoqInputSet Input { get; init; }
        public required IReadOnlyDictionary<string, double> Parameters { get; init; }
        public required int RoadClass { get; init; }
        public required IReadOnlyList<BoqPartResult> Parts { get; init; }
        public required IReadOnlyList<BoqLineResult> Lines { get; init; }
        public required IReadOnlyList<BoqCrossing> Crossings { get; init; }
        public required IReadOnlyDictionary<string, string> Roles { get; init; }
        public required BoqBucket[] Buckets { get; init; }
        public required string?[] Reasons { get; init; }
        /// <summary>Records held back as longer than their part's max_len (their measured quantity); null otherwise.</summary>
        public double?[] HeldLong { get; init; } = Array.Empty<double?>();
        /// <summary>
        /// Rows appended after the grouped exclusions (reference EXTRA_EXCL): proven zero-length lines — information, never
        /// missing, never a quantity.
        /// </summary>
        public IReadOnlyList<BoqGroup> ExtraExcluded { get; init; } = Array.Empty<BoqGroup>();
        /// <summary>Files whose block or layer names carry a number of the guide-sign range (reference GUIDE_HITS), by file.</summary>
        public BoqCounter GuideHits { get; init; } = new();
        /// <summary>The shown label of each counted sign number ("505/506" when block 505(506) covers both).</summary>
        public IReadOnlyDictionary<string, string> SignLabels { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Counted poles (the count_x parts of sign_rules.poles_param) and counted signs.</summary>
        public int PolesCount { get; init; }
        public int SignsTotal { get; init; }

        /// <summary>The reference REF() of a line id: its item ("51.32.1852/1862" for an urban/rural line) or label.</summary>
        public string Ref(string lineId) => lineId == "—" ? lineId : Rules.Ref(lineId);

        /// <summary>
        /// Records of this line's part layers held back as too long for the part ('לבדיקה'): count, measured length and
        /// layers. The BoQ row shows them so a held-back quantity is never silent (review 30/09, 812 bike rows).
        /// </summary>
        public (int Count, double Length, IReadOnlyList<string> Layers)? HeldLongObjects(BoqLine line)
        {
            var keys = line.Parts.SelectMany(p => p.Src.SelectMany(src => p.Layers.Select(layer => (src, layer))))
                .ToHashSet();
            var count = 0;
            var length = 0.0;
            var layers = new SortedSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < HeldLong.Length; i++)
            {
                if (HeldLong[i] is not { } held) continue;
                var r = Input.Records[i];
                if (!keys.Contains((r.Src, r.Layer))) continue;
                count++;
                length += held;
                layers.Add(r.Layer);
            }
            return count == 0 ? null : (count, length, layers.ToList());
        }
        public required (string Line, int Part)?[] Items { get; init; }
        public required IReadOnlyList<BoqMissingRow> Missing { get; init; }
        public required BoqCounter SignCounts { get; init; }
        public required IReadOnlyList<string> Warnings { get; init; }
        public required IReadOnlyDictionary<(string Src, string Handle), (double Length, string Layer)> LongObjects { get; init; }
        /// <summary>v4.1: SM/DR curb pieces that are not a geometric copy of a GM curb, by layer (count, length) — לבדיקה on the curb rows.</summary>
        public IReadOnlyDictionary<string, (int Count, double Length)> CurbUnique { get; init; } = new Dictionary<string, (int, double)>();
        public IReadOnlyList<string> CurbSources { get; init; } = System.Array.Empty<string>();
        /// <summary>v4.5: the figures quoted in the notes (lane "note_figures"); null when the ruleset has none.</summary>
        public BoqNoteFigures? NoteFigures { get; init; }
        /// <summary>v4.5: overlap / unmeasured-area estimates of the area hatches (lane "ha_overlap"); null when off or no polygons.</summary>
        public BoqHaOverlap? HaOverlap { get; init; }
        /// <summary>Count records removed as the same block at the same point (line order, part order, record order).</summary>
        public required IReadOnlyList<BoqDedupRow> Dedup { get; init; }
        /// <summary>Rules 2.8: approved-footprint placements counted as separate objects (line, part, record order).</summary>
        public IReadOnlyList<BoqDistinctRow> Distinct { get; init; } = Array.Empty<BoqDistinctRow>();
        /// <summary>Rules 2.8: approved-footprint placements counted once for review (separate body not proven).</summary>
        public IReadOnlyList<BoqFootprintReviewRow> FootprintReview { get; init; } = Array.Empty<BoqFootprintReviewRow>();
        /// <summary>Line controls by line id.</summary>
        public required IReadOnlyDictionary<string, BoqControlResult> Controls { get; init; }

        /// <summary>"item:LINE:PART" | "excl" | "uncl" — the golden record_bucket_by_handle value.</summary>
        public string BucketLabel(int index) => Buckets[index] switch
        {
            BoqBucket.Item => $"item:{Items[index]!.Value.Line}:{Items[index]!.Value.Part.ToString(CultureInfo.InvariantCulture)}",
            BoqBucket.Excluded => "excl",
            _ => "uncl",
        };

        /// <summary>"SRC/item|excl|uncl" → records.</summary>
        public IReadOnlyDictionary<string, int> BucketCounts()
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < Buckets.Length; i++)
            {
                var key = Input.Records[i].Src + "/" + (Buckets[i] switch { BoqBucket.Item => "item", BoqBucket.Excluded => "excl", _ => "uncl" });
                counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
            }
            return counts;
        }

        /// <summary>
        /// Excluded records grouped by (reason, file, layer, kind); ordered by reason, file, then larger quantity first;
        /// then the <see cref="ExtraExcluded"/> rows (reference erows = sorted groups + EXTRA_EXCL).
        /// </summary>
        public IReadOnlyList<BoqGroup> ExcludedGroups()
        {
            var groups = new Dictionary<(string, string, string, string), (int N, double Q)>();
            var order = new List<(string, string, string, string)>();
            for (var i = 0; i < Buckets.Length; i++)
            {
                if (Buckets[i] != BoqBucket.Excluded) continue;
                var r = Input.Records[i];
                var key = (Reasons[i] ?? "", r.Src, r.Layer, r.Kind);
                if (!groups.TryGetValue(key, out var g)) { order.Add(key); g = (0, 0.0); }
                groups[key] = (g.N + 1, g.Q + r.Qty);
            }
            // v4.4: the reference order (build_boq_v7 _ekey) over the grouped rows AND the extra rows together: by reason, then
            // file, larger quantity first; the held-back long objects ('לבדיקה: עצם באורך N …') by N as a number.
            return order
                .Select(k => new BoqGroup(k.Item1, k.Item2, k.Item3, k.Item4, groups[k].N, groups[k].Q, Array.Empty<KeyValuePair<string, int>>()))
                .Concat(ExtraExcluded)
                .OrderBy(g => g, ExcludedOrder.Instance).ToList();
        }

        /// <summary>
        /// Unclassified records grouped by (file, layer, kind), ordered by file then larger quantity first, followed by
        /// the hatch-total layers no hatch part uses. Types: every block / entity type of the group, most common first.
        /// </summary>
        public IReadOnlyList<BoqGroup> UnclassifiedGroups()
        {
            var groups = new Dictionary<(string, string, string), (int N, double Q, BoqCounter Types)>();
            var planOf = new Dictionary<(string, string, string), (double Plan, int Count, double Excess, string? Example, double ExampleExcess)>();
            var order = new List<(string, string, string)>();
            for (var i = 0; i < Buckets.Length; i++)
            {
                if (Buckets[i] != BoqBucket.Unclassified) continue;
                var r = Input.Records[i];
                var key = (r.Src, r.Layer, r.Kind);
                if (!groups.TryGetValue(key, out var g)) { order.Add(key); g = (0, 0.0, new BoqCounter()); }
                g.Types.Add(string.IsNullOrEmpty(r.Block) ? r.Etype : r.Block);
                groups[key] = (g.N + 1, g.Q + r.Qty, g.Types);
                // v4.4: plan length from the record's chords (a record without chords keeps its as-drawn length), and the
                // records whose as-drawn length is more than 1 m longer than the plan (a wrong Z).
                var plan = r.Qty;
                if (r.Kind == "length" && Input.LengthGeometry.TryGetValue((r.Src, r.Handle), out var geo) && geo.Segments.Count > 0)
                    plan = geo.Segments.Sum(s => s.Length);
                planOf.TryGetValue(key, out var z);
                z.Plan += plan;
                if (r.Kind == "length" && r.Qty - plan > 1.0)
                {
                    z.Count++;
                    z.Excess += r.Qty - plan;
                    // the first record with the largest excess (copies of one object tie: 1 µm, not the last bit of a sum)
                    if (z.Example == null || r.Qty - plan > z.ExampleExcess + 1e-6) (z.Example, z.ExampleExcess) = (r.Handle, r.Qty - plan);
                }
                planOf[key] = z;
            }
            var rows = order
                .Select(k => new BoqGroup("", k.Item1, k.Item2, k.Item3, groups[k].N, groups[k].Q, groups[k].Types.MostCommon())
                {
                    Plan = planOf[k].Plan, ZCount = planOf[k].Count, ZExampleHandle = planOf[k].Example, ZExcess = planOf[k].Excess,
                })
                .ToList();
            var usedHatchLayers = Rules.AllParts.Where(p => p.Kind == "hatch").SelectMany(p => p.Layers).ToHashSet(StringComparer.Ordinal);
            foreach (var t in Input.HatchLayers.Where(t => !usedHatchLayers.Contains(t.Layer)))
                rows.Add(new BoqGroup("", t.Src, t.Layer, "area", t.DirectCount, (double)t.DirectArea,
                    new[] { new KeyValuePair<string, int>("HATCH", t.DirectCount) }) { Plan = (double)t.DirectArea });
            // v4.4: the reference sorts again after appending the hatch-total rows (file, then larger quantity first; stable).
            return rows.OrderBy(g => g.Src, StringComparer.Ordinal).ThenByDescending(g => g.Quantity).ToList();
        }
    }

    /// <summary>
    /// The pure BoQ rules engine (port of the reference build_boq_v7.py selection and export_golden.py quantities, v4 of
    /// 30.09.2026): ownership/duplicate rules → crossing roles (assemblies of a hatch near the stop line: dashed line +
    /// hatch, boundary and edge lines are members) → long objects → parts in rules order (the first matching part wins;
    /// a part may filter block names) → one physical object once (plan length of every length part with object_width_m)
    /// → the same block at the same point counted once per line → line controls (measured, reported, excluded) → proven
    /// zero-length lines (information) → known unmeasured objects (missing, once per file and handle) → unclassified.
    /// Quantities are measured base × parameter; crossings and signs are computed from their own tables; a count_of part
    /// is the count of another line; a line_sum part is the sum of its lines' quantities rounded to 0.01 (the workbook
    /// sums rounded cells). Deterministic; no I/O; no CAD dependency.
    /// </summary>
    public static class BoqRulesEngine
    {
        public const string MissingAction = "לפתוח את המקור ב-Civil: LIST / Properties ובדיקת לולאות הגבול";
        private static readonly Regex SignNumberPattern = new("(?<![0-9])([0-9]{3})(?![0-9])", RegexOptions.CultureInvariant);

        public static string? SignNumber(string? block)
        {
            var match = SignNumberPattern.Match(block ?? "");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>Every three-digit number of a text (reference re.findall(r'(?&lt;!\d)(\d{3})(?!\d)', …)).</summary>
        internal static IEnumerable<int> ThreeDigitNumbers(string text) =>
            SignNumberPattern.Matches(text).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));

        public static BoqEngineResult Run(BoqRuleset rules, BoqInputSet input, IReadOnlyDictionary<string, double>? parameterOverrides = null)
        {
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(input);
            var parameters = rules.Parameters.ToDictionary(p => p.Id, p => p.Value, StringComparer.Ordinal);
            if (parameterOverrides != null)
                foreach (var pair in parameterOverrides)
                {
                    if (!parameters.ContainsKey(pair.Key)) throw new ArgumentException($"Unknown parameter '{pair.Key}'.");
                    parameters[pair.Key] = pair.Value;
                }
            var road = parameters.TryGetValue(rules.RoadClassParam, out var rc) ? (int)rc : 1;
            var warnings = new List<string>(input.Warnings);
            var recs = input.Records;
            var n = recs.Count;
            var bucket = new BoqBucket[n];
            var heldLong = new double?[n];
            var reason = new string?[n];
            var items = new (string Line, int Part)?[n];

            void Exclude(int i, string why)
            {
                if (bucket[i] != BoqBucket.None) return;
                bucket[i] = BoqBucket.Excluded;
                reason[i] = why;
            }

            // 1. ownership / duplicate rules, in file order.
            ApplyOwnership(rules, recs, bucket, Exclude);

            // 2. crossings: one physical crossing counted once.
            var crossings = (IReadOnlyList<BoqCrossing>)Array.Empty<BoqCrossing>();
            var roles = (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal);
            var hasCrossingPart = rules.AllParts.Any(p => p.Kind == "crosswalk_geo");
            var cw = rules.Crosswalk;
            if (hasCrossingPart && cw == null)
                warnings.Add("בכללים חסר crosswalk_geometry — מעברי חציה לא חושבו (0), העצמים נשארים 'לא סווג'.");
            if (hasCrossingPart && cw != null)
            {
                var geo = BoqCrosswalkGeometry.Compute(cw, input.Geometry.Where(g => cw.Src.Contains(g.Src)));
                crossings = geo.Crossings;
                roles = geo.Roles;
                if (geo.IgnoredWithoutLayer > 0)
                    warnings.Add($"{geo.IgnoredWithoutLayer} עצמי גאומטריה ללא שכבה ידועה לא השתתפו בזיהוי מעברי החציה.");
                var crossingLayers = cw.AllLayers;
                for (var i = 0; i < n; i++)
                {
                    var r = recs[i];
                    if (bucket[i] != BoqBucket.None || !cw.Src.Contains(r.Src) || !crossingLayers.Contains(r.Layer)) continue;
                    if (!roles.TryGetValue(r.Handle, out var role)) continue;
                    switch (role)
                    {
                        case BoqCrosswalkGeometry.RoleWhiteOnYellow or BoqCrosswalkGeometry.RoleHatchOnYellow:
                            Exclude(i, cw.ReasonOnYellow);
                            break;
                        case BoqCrosswalkGeometry.RoleWhiteAtHatch:
                            Exclude(i, cw.ReasonAtHatch);
                            break;
                        case BoqCrosswalkGeometry.RoleAssemblyEdge:
                            Exclude(i, cw.ReasonAssemblyEdge);
                            break;
                        case BoqCrosswalkGeometry.RoleAssemblyBoundary:
                            Exclude(i, cw.ReasonAssemblyBoundary);
                            break;
                        case BoqCrosswalkGeometry.RoleNoise:
                            Exclude(i, cw.ReasonNoise);
                            break;
                    }
                }
            }

            // 3. objects longer than a part's max_len on its layers are not that part's objects. The reason and the BoQ
            //    row quote the record's measured length (v4: 2,601 m, not the chord sum of the classification geometry).
            var longObjects = BoqCrosswalkGeometry.LongObjects(rules, input.Geometry);
            for (var i = 0; i < n; i++)
            {
                var r = recs[i];
                if (bucket[i] == BoqBucket.None && longObjects.ContainsKey((r.Src, r.Handle)))
                {
                    Exclude(i, rules.LongObjectReason
                        .Replace("{length}", r.Qty.ToString("F0", CultureInfo.InvariantCulture))
                        .Replace("{layer}", r.Layer).Replace("{handle}", r.Handle));
                    heldLong[i] = r.Qty; // held back for review: the BoQ row says so, it is not silent
                }
            }

            // 3b. v4.1 (MARK-V4-01): bike symbols inside path arrays are counted at their world points (their member records);
            //     a member within the same-location distance of a model-space symbol is that symbol (the array's end); the
            //     array insert is no symbol; a held-long line that carries an array says so.
            if (input.Arrays.Count > 0 && rules.ArrayMembers is { } am)
            {
                var members = input.Arrays.SelectMany(a => a.MemberHandles.Select(h => (a.Src, h))).ToHashSet();
                // Model symbols per file: a member is compared only with symbols of its own drawing (reference _MS_BIKE = the SM
                // model space) — a symbol of another file at the same point is no evidence that the member is that object.
                var modelSymbols = new Dictionary<string, List<BoqPoint>>(StringComparer.Ordinal);
                for (var i = 0; i < n; i++)
                {
                    var r = recs[i];
                    if (r.Kind == "count" && r.Block == am.MemberBlock && !members.Contains((r.Src, r.Handle)) &&
                        input.CountPoints.TryGetValue((r.Src, r.Handle), out var p))
                    {
                        if (!modelSymbols.TryGetValue(r.Src, out var list)) modelSymbols[r.Src] = list = new List<BoqPoint>();
                        list.Add(p);
                    }
                }
                // The container is the array insert itself, by (file, handle): an anonymous block name is no array identity.
                var arrayByHandle = input.Arrays.GroupBy(a => (a.Src, a.Handle)).ToDictionary(g => g.Key, g => g.First());
                var pathLines = input.Arrays.Where(a => a.PathLine != null).ToDictionary(a => (a.Src, a.PathLine!), a => a.MemberHandles.Count);
                for (var i = 0; i < n; i++)
                {
                    var r = recs[i];
                    if (members.Contains((r.Src, r.Handle)) && input.CountPoints.TryGetValue((r.Src, r.Handle), out var p) &&
                        modelSymbols.TryGetValue(r.Src, out var own) && own.Count > 0)
                    {
                        var near = own.Min(q => BoqPoint.Distance(p, q));
                        if (near <= input.ArraySameLocationM)
                            Exclude(i, am.MemberReason.Replace("{near}", near.ToString("F1", CultureInfo.InvariantCulture)));
                    }
                    else if (r.Kind == "count" && arrayByHandle.TryGetValue((r.Src, r.Handle), out var arr))
                        Exclude(i, (arr.PathLine != null ? am.ArrayReason : am.ArrayReasonNoLine)
                            .Replace("{n}", arr.MemberHandles.Count.ToString(CultureInfo.InvariantCulture))
                            .Replace("{line}", arr.PathLine ?? "").Replace("{layer}", arr.PathLayer ?? ""));
                    else if (heldLong[i] != null && pathLines.TryGetValue((r.Src, r.Handle), out var count) && reason[i] is { } held)
                        reason[i] = held + am.PathLineSuffix.Replace("{n}", count.ToString(CultureInfo.InvariantCulture));
                }
            }

            // 4. parts, in rules order: the first matching part wins.
            var parts = new List<BoqPartResult>();
            var missing = new List<BoqMissingRow>();
            foreach (var line in rules.Lines)
            {
                var lineRef = line.Ref();
                for (var pi = 0; pi < line.Parts.Count; pi++)
                {
                    var part = line.Parts[pi];
                    var P = new BoqPartResult { Line = line, Index = pi, Part = part };
                    parts.Add(P);
                    void Claim(int i)
                    {
                        bucket[i] = BoqBucket.Item;
                        items[i] = (line.Id, P.Index);
                    }

                    switch (part.Kind)
                    {
                        case "crosswalk_geo":
                            SelectCrossings(part, P, recs, bucket, reason, items, roles, crossings, input, cw, missing);
                            continue;
                        case "hatch":
                            SelectHatchTotals(part, P, input, missing);
                            continue;
                        case "line_sum":
                            // Reads no drawing object; its quantity is summed from other lines below.
                            if (!string.IsNullOrEmpty(part.DecidedBy)) P.Notes.Add(part.DecidedBy);
                            continue;
                        case "count_of":
                            continue; // reads no drawing object: the count of another line, below
                    }

                    var layers = part.Layers.ToHashSet(StringComparer.Ordinal);
                    var blockLayers = part.BlockLayers.ToHashSet(StringComparer.Ordinal);
                    var blocks = part.Blocks.ToHashSet(StringComparer.Ordinal);
                    var etypes = part.Etypes.ToHashSet(StringComparer.Ordinal);
                    for (var i = 0; i < n; i++)
                    {
                        var r = recs[i];
                        if (bucket[i] != BoqBucket.None || !part.Src.Contains(r.Src)) continue;
                        if (part.Kind is "sign_area" or "sign_block_area")
                        {
                            if (r.Kind != "count") continue;
                            var num = SignNumber(r.Block);
                            if (num == null) continue;
                            var value = int.Parse(num, CultureInfo.InvariantCulture);
                            if (rules.SignExcludedNumberRange is { } ex && ex.From <= value && value <= ex.To) continue;
                            var ok = part.Signs.Contains(num) || (part.SignRange is { } range && range.From <= value && value <= range.To);
                            if (!ok) continue;
                            Claim(i);
                            P.Objects++;
                            P.Signs.Add(num);
                            P.Layers.Add(r.Layer);
                            P.Srcs.Add(r.Src);
                            continue;
                        }
                        if (part.Kind == "count_x" || (part.Kind == "count" && blockLayers.Count > 0))
                        {
                            if (!blockLayers.Contains(r.Layer) || r.Kind != "count") continue;
                            Claim(i);
                            P.Objects++;
                            P.Base += r.Qty;
                            P.Layers.Add(r.Layer);
                            P.Srcs.Add(r.Src);
                            P.Signs.Add(r.Block);
                            continue;
                        }
                        if (layers.Count > 0 && !layers.Contains(r.Layer)) continue;
                        if (blocks.Count > 0 && !blocks.Contains(r.Block)) continue;
                        if (etypes.Count > 0 && !etypes.Contains(r.Etype)) continue;
                        if (part.OpenOnly && r.Closed) continue;
                        if (part.ClosedOnly && !r.Closed) continue;
                        var want = part.Kind; // length | area | count (validated)
                        if (r.Kind == want)
                        {
                            Claim(i);
                            P.Objects++;
                            P.Base += r.Qty;
                            P.Layers.Add(r.Layer);
                            P.Srcs.Add(r.Src);
                        }
                        else if (want == "length" && r.Kind == "area" && r.Closed)
                            Exclude(i, $"{lineRef}: פוליליין סגור נמדד באורך (היקף); חלופת השטח לא נכללת");
                        else if (want == "area" && r.Kind == "length" && r.Closed)
                            Exclude(i, $"{lineRef}: פוליליין סגור נמדד בשטח; חלופת האורך לא נכללת");
                        else if (want == "count")
                            Exclude(i, $"{lineRef}: סעיף בספירה — אורך/שטח של העצם אינו כמות");
                    }
                }
            }

            // Records claimed by each part, in record order (read by the one-object-once steps below).
            var claimed = new Dictionary<(string Line, int Part), List<int>>();
            for (var i = 0; i < n; i++)
            {
                if (items[i] is not { } it) continue;
                if (!claimed.TryGetValue(it, out var list)) claimed[it] = list = new List<int>();
                list.Add(i);
            }
            IReadOnlyList<int> ClaimedBy(BoqPartResult part) =>
                claimed.TryGetValue((part.LineId, part.Index), out var found) ? found : (IReadOnlyList<int>)Array.Empty<int>();

            // 4b. one physical object once: the plan length of every length part's objects (boq_geometry.object_length).
            foreach (var P in parts)
            {
                if (P.Part.Kind != "length" || P.Part.ObjectWidth is not { } width) continue;
                var chords = new List<BoqSegment>();
                var noGeometry = 0;
                var noGeometryQty = 0.0;
                foreach (var i in ClaimedBy(P))
                {
                    var r = recs[i];
                    if (input.LengthGeometry.TryGetValue((r.Src, r.Handle), out var g) && g.SegmentsComplete) chords.AddRange(g.Segments);
                    else
                    {
                        noGeometry++;
                        noGeometryQty += r.Qty;
                    }
                }
                var measured = BoqObjectMeasure.ObjectLength(chords, width, rules.Measurement);
                P.DrawnSum = P.Base;
                P.PlanSum = measured.Raw + noGeometryQty;
                P.Base = measured.Length + noGeometryQty;
                P.ByMultiplicity = measured.ByMultiplicity;
                P.Width = width;
                P.WidthNote = P.Part.ObjectWidthNote ?? "";
                P.NoGeometry = noGeometry;
                // v4: the note quotes the plan length (the 3D drawn sum stays on the one-object-once sheet).
                if (Math.Abs(P.PlanSum.Value - P.Base) > 0.005)
                    P.Notes.Insert(0, $"נמדדו {P.Objects} קווים שאורכם בתוכנית {F1(P.PlanSum.Value)} מ'; עצם אחד נספר פעם אחת: {F1(P.Base)} מ' ({P.WidthNote})");
                if (noGeometry > 0)
                    P.Notes.Add($"{noGeometry} עצמים בלי גאומטריה בתוכנית נמדדו לפי האורך המקורי");
            }

            // 4c. the same block at the same point counts once per BoQ line (any part, any file), in part and record order.
            // Rules 2.8 (BOQ-N1, build_boq_v7.py): a block with an approved physical footprint counts again only when both
            // placements are proven, its definition matches the approved signature and its body is positively separated from
            // every twin already kept; the same transform counts once; anything else counts once and is flagged for review.
            var dedup = new List<BoqDedupRow>();
            var distinct = new List<BoqDistinctRow>();
            var footprintReview = new List<BoqFootprintReviewRow>();
            var footprints = rules.PhysicalFootprints.ToDictionary(f => f.Block, StringComparer.Ordinal);
            foreach (var line in rules.Lines)
            {
                var seen = new List<(string Key, double X, double Y, string Id, double? Rot, BoqInsertTransform? Transform, BoqPoint[]? Body)>();
                foreach (var P in parts.Where(p => ReferenceEquals(p.Line, line)))
                {
                    if (P.Part.Kind is not ("count" or "count_x") || P.Part.SamePoint is not { } same) continue;
                    foreach (var i in ClaimedBy(P))
                    {
                        if (bucket[i] != BoqBucket.Item) continue;
                        var r = recs[i];
                        if (!input.CountPoints.TryGetValue((r.Src, r.Handle), out var pt)) continue;
                        var key = string.IsNullOrEmpty(r.Block) ? r.Layer : r.Block;
                        double? rot = input.CountRotations.TryGetValue((r.Src, r.Handle), out var rot1) ? rot1 : null;
                        // An array member (handle "ARRAY/ITEM/MEMBER") has no INSERT of its own: never an approved placement.
                        var fp = !string.IsNullOrEmpty(r.Block) && !r.Handle.Contains('/') &&
                                 footprints.TryGetValue(r.Block, out var approved) ? approved : null;
                        var (tf, body, why) = fp == null ? (null, null, null) : PlacedBody(fp, r, input);
                        var twins = seen.Where(s => string.Equals(s.Key, key, StringComparison.Ordinal) &&
                                                    double.Hypot(s.X - pt.X, s.Y - pt.Y) <= same).ToList();
                        if (twins.Count == 0)
                        {
                            seen.Add((key, pt.X, pt.Y, $"{r.Src}:{r.Handle}", rot, tf, body));
                            continue;
                        }
                        string kept;
                        var flip = false;
                        var review = false;
                        if (fp != null)
                        {
                            var sameTransform = twins.Where(s => tf != null && s.Transform != null && SameTransform(tf, s.Transform, fp)).ToList();
                            var gaps = twins.Select(s => body != null && s.Body != null ? BodyGap(body, s.Body) : (double?)null).ToList();
                            if (sameTransform.Count == 0 && gaps.All(g => g is { } v && v > fp.SeparationMinM))
                            {
                                seen.Add((key, pt.X, pt.Y, $"{r.Src}:{r.Handle}", rot, tf, body));
                                distinct.Add(new BoqDistinctRow(line.Id, P.Part.Label, r.Src, r.Handle, twins.Select(s => s.Id).ToList(),
                                    gaps.Min(g => g!.Value)));
                                continue;
                            }
                            var twin = sameTransform.Count > 0 ? sameTransform[0] : twins[0];
                            kept = twin.Id;
                            bucket[i] = BoqBucket.Excluded;
                            if (sameTransform.Count > 0)
                                reason[i] = $"{line.Ref()}: אותו בלוק באותה נקודה כמו {kept} — נספר פעם אחת";
                            else
                            {
                                why ??= twin.Body == null ? "גוף ההצבה השכנה לא הוכח" : "גופי המתקנים חופפים או צמודים";
                                var gapCm = (double.Hypot(twin.X - pt.X, twin.Y - pt.Y) * 100).ToString("F1", CultureInfo.InvariantCulture);
                                reason[i] = $"{line.Ref()}: בלוק זהה {gapCm} ס\"מ מ-{kept} — לא הוכח גוף נפרד ({why}); נספר פעם אחת — לבדיקה";
                                footprintReview.Add(new BoqFootprintReviewRow(line.Id, P.Part.Label, r.Src, r.Handle, kept, why));
                                review = true;
                            }
                        }
                        else
                        {
                            var twin = twins[0];
                            kept = twin.Id;
                            flip = rot is { } a && twin.Rot is { } b && Math.Abs(Math.Abs(a - b) - Math.PI) < 0.05;
                            var gap = double.Hypot(twin.X - pt.X, twin.Y - pt.Y);
                            bucket[i] = BoqBucket.Excluded;
                            reason[i] = flip
                                ? $"{line.Ref()}: בלוק זהה {(gap * 100).ToString("F1", CultureInfo.InvariantCulture)} ס\"מ מ-{kept} בכיוון הפוך — נספר פעם אחת; לבדיקה אם זוג גב-אל-גב (ראו הערת השורה)"
                                : $"{line.Ref()}: אותו בלוק באותה נקודה כמו {kept} — נספר פעם אחת";
                        }
                        if (flip) kept += " (בכיוון הפוך — לבדיקה)";
                        else if (review) kept += " (גוף נפרד לא הוכח — לבדיקה)";
                        items[i] = null;
                        P.Objects--;
                        P.Base -= r.Qty;
                        P.Layers.Subtract(r.Layer);
                        P.Srcs.Subtract(r.Src);
                        if (P.Signs[r.Block] > 0) P.Signs.Subtract(r.Block);
                        dedup.Add(new BoqDedupRow(line.Id, P.Part.Label, r.Src, r.Layer, key, r.Handle, kept));
                    }
                }
            }

            // 4d. line controls: the free records they name are measured one object once, reported, then excluded.
            var controls = new Dictionary<string, BoqControlResult>(StringComparer.Ordinal);
            foreach (var line in rules.Lines)
            {
                if (line.Control is not { } control) continue;
                var src = control.Src.ToHashSet(StringComparer.Ordinal);
                var layers = control.Layers.ToHashSet(StringComparer.Ordinal);
                var mine = new List<int>();
                for (var i = 0; i < n; i++)
                {
                    var r = recs[i];
                    if (bucket[i] == BoqBucket.None && src.Contains(r.Src) && layers.Contains(r.Layer) &&
                        string.Equals(r.Kind, control.Kind, StringComparison.Ordinal))
                        mine.Add(i);
                }
                var chords = new List<BoqSegment>();
                foreach (var i in mine)
                    if (input.LengthGeometry.TryGetValue((recs[i].Src, recs[i].Handle), out var g) && g.SegmentsComplete)
                        chords.AddRange(g.Segments);
                var measured = BoqObjectMeasure.ObjectLength(chords, control.ObjectWidth, rules.Measurement);
                var drawn = 0.0;
                foreach (var i in mine) drawn += recs[i].Qty;
                controls[line.Id] = new BoqControlResult(line.Id, control.Label, mine.Count, measured.Length, drawn);
                var why = ControlReason(rules, line, road);
                foreach (var i in mine) Exclude(i, why);
            }

            // 5a. proven zero-length lines: nothing to measure and nothing missing — one information row in 'לא נכלל'
            //     (v4: the GM line 10A971), named by the item of the first part that owns its file and layer.
            BoqPartResult? OwnerOf(string src, string? layer) => layer == null ? null : parts.FirstOrDefault(p =>
                p.Part.Src.Contains(src) && (p.Part.Layers.Contains(layer) || p.Part.BlockLayers.Contains(layer)));
            var extraExcluded = new List<BoqGroup>();
            foreach (var z in input.ZeroLength)
            {
                var owner = OwnerOf(z.Src, z.Layer);
                var text = $"קו באורך אפס (נקודת התחלה = נקודת סיום, מזהה {z.Handle}) — אין אורך למדידה; אינו משנה את הכמות";
                extraExcluded.Add(new BoqGroup(owner != null ? owner.Line.Ref() + ": " + text : text, z.Src, z.Layer ?? "", z.Kind, 1, 0.0,
                    Array.Empty<KeyValuePair<string, int>>()));
            }

            // 5b. known unmeasured objects count as missing in the first part that owns their file and layer — once per file
            //     and handle (1.3.9 listed three SM hatches twice). A crossing hatch is the crossing part's own business: it is
            //     measured there (returned, recovered or from its boundary), listed there as missing, or — on a yellow
            //     crossing — the same crossing drawn twice (the reference never lists it).
            var listed = missing.Select(m => (m.Src, m.Handle)).ToHashSet();
            var crossingHatches = crossings.SelectMany(c => c.HatchHandles).ToHashSet(StringComparer.Ordinal);
            var crossingSrc = cw?.Src.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            foreach (var u in input.Unmeasured)
            {
                if (crossingSrc.Contains(u.Src) && (crossingHatches.Contains(u.Handle) || roles.ContainsKey(u.Handle))) continue;
                if (!listed.Add((u.Src, u.Handle))) continue;
                var owner = OwnerOf(u.Src, u.Layer);
                if (owner != null) owner.Missing++;
                missing.Add(new BoqMissingRow(owner?.LineId ?? "—", u.Src, u.Layer ?? "", u.Handle, u.What, u.Action));
            }

            // 6. everything else is unclassified (visible, never silently dropped).
            for (var i = 0; i < n; i++)
                if (bucket[i] == BoqBucket.None) bucket[i] = BoqBucket.Unclassified;

            // 6b. v4.1 (WI4-01 / GRD-1): a DR copy of a GM object states what really happened to the GM copy.
            var copyRule = rules.Ownership.FirstOrDefault(o => o.DuplicateOwner != null && o.DuplicateSameHandle);
            if (copyRule != null)
            {
                var ownerIndex = new Dictionary<(string Handle, string Kind), int>();
                for (var i = 0; i < n; i++)
                    if (recs[i].Src == copyRule.DuplicateOwner) ownerIndex[(recs[i].Handle, recs[i].Kind)] = i;  // last wins, as the reference dict
                for (var i = 0; i < n; i++)
                {
                    if (bucket[i] != BoqBucket.Excluded || reason[i] != copyRule.Reason) continue;
                    string outcome;
                    if (!ownerIndex.TryGetValue((recs[i].Handle, recs[i].Kind), out var g)) outcome = "העותק ב-GM לא נמצא ברשומות — לבדיקה";
                    else if (bucket[g] == BoqBucket.Item && items[g] is { } it && rules.Lines.FirstOrDefault(l => l.Id == it.Line) is { } owner)
                        outcome = owner.HasItem ? $"העותק ב-GM נספר בסעיף {owner.Ref()}" : $"העותק ב-GM נספר בשורה \"{owner.Ref()}\"";
                    else if (bucket[g] == BoqBucket.Unclassified) outcome = $"גם העותק ב-GM לא סווג — ראו \"לא סווג\" (GM, {recs[g].Layer})";
                    else if (reason[g] is { } gr && gr.Contains("פוליליין סגור נמדד באורך", StringComparison.Ordinal))
                        outcome = $"העצם ב-GM נמדד באורך (היקף) בסעיף {gr.Split(':')[0]}; חלופת השטח לא נכללת";
                    else if (reason[g] is { } gd && gd.Contains("אותו בלוק באותה נקודה", StringComparison.Ordinal))
                        outcome = $"גם העותק ב-GM הוסר ככפול (אותו בלוק באותה נקודה כמו בקובץ {(gd.Contains("כמו ", StringComparison.Ordinal) ? gd.Split("כמו ")[1].Split(':')[0] : "אחר")}) — נספר פעם אחת";
                    else outcome = "גם העותק ב-GM לא נכלל: " + reason[g];
                    reason[i] = "עותק ב-DR של עצם מקובץ GM (אותו מזהה ואותה גאומטריה) — לא נספר מ-DR; " + outcome;
                }
            }

            // 6c. v4.1 (WI4-05): SM/DR curbs held back for GM are compared with the GM curb chords (≤2 cm): a geometric copy says
            //     so; a sliver under 10 cm is drawing noise; what is left stays לבדיקה and is named on the curb rows.
            var curbRule = rules.Ownership.FirstOrDefault(o => o.Id == "curbs_owned_by_gm");
            var curbUnique = new Dictionary<string, (int Count, double Length)>(StringComparer.Ordinal);
            var curbSrc = new SortedSet<string>(StringComparer.Ordinal);
            if (curbRule != null)
            {
                var curbLayers = curbRule.Layers.ToHashSet(StringComparer.Ordinal);
                var gmChords = new List<BoqSegment>();
                for (var i = 0; i < n; i++)
                    if (recs[i].Src == "GM" && curbLayers.Contains(recs[i].Layer) &&
                        input.LengthGeometry.TryGetValue((recs[i].Src, recs[i].Handle), out var gg))
                        gmChords.AddRange(gg.Segments);
                var grid = new Dictionary<(long, long), List<BoqSegment>>();
                foreach (var c in gmChords)
                {
                    var key = ((long)Math.Round((c.A.X + c.B.X) / 2), (long)Math.Round((c.A.Y + c.B.Y) / 2));
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<BoqSegment>();
                    list.Add(c);
                }
                bool InGm(BoqSegment c)
                {
                    var mx = (long)Math.Round((c.A.X + c.B.X) / 2);
                    var my = (long)Math.Round((c.A.Y + c.B.Y) / 2);
                    for (var dx = -1; dx <= 1; dx++)
                        for (var dy = -1; dy <= 1; dy++)
                            if (grid.TryGetValue((mx + dx, my + dy), out var list))
                                foreach (var g in list)
                                    if ((BoqPoint.Distance(c.A, g.A) <= 0.02 && BoqPoint.Distance(c.B, g.B) <= 0.02) ||
                                        (BoqPoint.Distance(c.A, g.B) <= 0.02 && BoqPoint.Distance(c.B, g.A) <= 0.02)) return true;
                    return false;
                }
                var prefix = curbRule.Reason.Split("{src}")[0];
                for (var i = 0; i < n; i++)
                {
                    var r = recs[i];
                    if (bucket[i] != BoqBucket.Excluded || reason[i] is not { } why || !why.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var has = input.LengthGeometry.TryGetValue((r.Src, r.Handle), out var geo) && geo.Segments.Count > 0;
                    if (r.Kind == "length" && r.Qty < 0.10)
                        reason[i] = $"קטע אבן קצר מ-10 ס\"מ בקובץ {r.Src} — רעש שרטוט";
                    else if (has && geo!.Segments.All(InGm))
                        reason[i] = $"אבן שפה בקובץ {r.Src} שזהה גאומטרית לעצם בקובץ GM — נספרת פעם אחת, ב-GM";
                    else
                    {
                        curbUnique.TryGetValue(r.Layer, out var cu);
                        curbUnique[r.Layer] = (cu.Count + 1, cu.Length + (r.Kind == "length" ? r.Qty : 0.0));
                        curbSrc.Add(r.Src);
                    }
                }
            }

            // 7. quantities at the parameters.
            var signCounts = new BoqCounter();
            foreach (var P in parts)
            {
                switch (P.Part.Kind)
                {
                    case "crosswalk_geo":
                        // v4: an assembly = its dashed line × standard width × painted share + the measured hatch area.
                        P.Quantity = cw == null ? 0.0 : (P.Crossings ?? Array.Empty<BoqCrossing>()).Sum(c => c.WidthSource switch
                        {
                            "hatch" => c.HatchM2 ?? 0.0,
                            "hatch-assembly" => (c.Length ?? 0.0) * parameters[cw.StandardWidthParam] * parameters[cw.FillParam] + (c.HatchM2 ?? 0.0),
                            "measured" => (c.Length ?? 0.0) * (c.Width ?? 0.0) * parameters[cw.FillParam],
                            _ => (c.Length ?? 0.0) * parameters[cw.StandardWidthParam] * parameters[cw.FillParam],
                        });
                        break;
                    case "sign_area":
                        signCounts.AddAll(P.Signs);
                        P.Quantity = P.Signs.Items.Sum(pair => pair.Value * rules.SignSizes.AreaM2(pair.Key, road));
                        break;
                    case "sign_block_area":
                        // The plan area of a guide sign is not in the block name: found blocks stay visible as
                        // unmeasured (never a silent zero). With none found the part is a checked, empty zero.
                        P.Quantity = 0.0;
                        if (P.Objects > 0)
                        {
                            P.Missing += P.Objects;
                            P.Notes.Add($"נמצאו {P.Objects} בלוקים בטווח — שטח השלט לא חושב; למדוד בתכנית");
                        }
                        break;
                    case "line_sum":
                    case "count_of":
                        break; // below, once every other part has its quantity
                    default:
                        P.ParameterValue = P.Part.Param != null ? parameters[P.Part.Param] : null;
                        P.Quantity = P.Base * (P.ParameterValue ?? 1.0);
                        break;
                }
            }
            // count_of: the objects of the referenced line (the sum of its parts' bases), as export_golden (base stays 0).
            foreach (var P in parts.Where(p => p.Part.Kind == "count_of"))
                P.Quantity = parts.Where(other => string.Equals(other.LineId, P.Part.Of, StringComparison.Ordinal)).Sum(other => other.Base);
            // line_sum: Σ over the referenced lines of ROUND(line quantity, 2) — the workbook sums the rounded cells.
            foreach (var P in parts.Where(p => p.Part.Kind == "line_sum"))
            {
                var total = 0.0;
                foreach (var id in P.Part.Lines)
                {
                    var lineQuantity = 0.0;
                    foreach (var other in parts)
                        if (string.Equals(other.LineId, id, StringComparison.Ordinal)) lineQuantity += other.Quantity;
                    total += Math.Round(lineQuantity, 2, MidpointRounding.AwayFromZero);
                }
                P.Base = total;
                P.Quantity = total;
            }
            var lines = rules.Lines.Select(line => new BoqLineResult
            {
                Line = line,
                Item = line.ItemFor(road),
                Parts = parts.Where(p => ReferenceEquals(p.Line, line)).ToList(),
            }).ToList();

            // Signs: the shown label of each number; poles vs signs; guide-sign names in every file (block AND layer).
            var signLabels = SignLabels(rules, signCounts, recs, bucket, items);
            var polesParam = rules.SignTexts.PolesParam;
            var poles = polesParam == null ? 0 : parts.Where(p => p.Part.Kind == "count_x" && p.Part.Param == polesParam).Sum(p => p.Objects);
            var guideHits = new BoqCounter();
            if (rules.AllParts.FirstOrDefault(p => p.Kind == "sign_block_area")?.SignRange is { } guide)
                foreach (var r in recs)
                    if (ThreeDigitNumbers((r.Block ?? "") + " " + r.Layer).Any(v => guide.From <= v && v <= guide.To))
                        guideHits.Add(r.Src);

            return new BoqEngineResult
            {
                Rules = rules,
                Input = input,
                Parameters = parameters,
                RoadClass = road,
                Parts = parts,
                Lines = lines,
                Crossings = crossings,
                Roles = roles,
                Buckets = bucket,
                Reasons = reason,
                HeldLong = heldLong,
                ExtraExcluded = extraExcluded,
                GuideHits = guideHits,
                SignLabels = signLabels,
                PolesCount = poles,
                SignsTotal = signCounts.Items.Sum(pair => pair.Value),
                Items = items,
                Missing = missing,
                SignCounts = signCounts,
                Warnings = warnings,
                LongObjects = longObjects,
                CurbUnique = curbUnique,
                CurbSources = curbSrc.ToList(),
                NoteFigures = BoqNoteAnalysis.NoteFigures(rules, input),
                HaOverlap = BoqNoteAnalysis.HaOverlap(rules, input),
                Dedup = dedup,
                Distinct = distinct,
                FootprintReview = footprintReview,
                Controls = controls,
            };
        }

        /// <summary>
        /// The label of each counted sign number: the ruleset's combined label (505 → "505/506") when a block counted as
        /// that number contains the other number in its name (block "505(506)"), else the number itself.
        /// </summary>
        private static IReadOnlyDictionary<string, string> SignLabels(BoqRuleset rules, BoqCounter signCounts,
            IReadOnlyList<BoqRecord> recs, BoqBucket[] bucket, (string Line, int Part)?[] items)
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            var blocksByNumber = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            for (var i = 0; i < recs.Count; i++)
            {
                if (bucket[i] != BoqBucket.Item || items[i] is not { } it) continue;
                var line = rules.Line(it.Line);
                if (line == null || line.Parts[it.Part].Kind != "sign_area") continue;
                var num = SignNumber(recs[i].Block);
                if (num == null) continue;
                if (!blocksByNumber.TryGetValue(num, out var set)) blocksByNumber[num] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(recs[i].Block);
            }
            foreach (var num in signCounts.Keys)
            {
                var combined = rules.SignTexts.CombinedLabels.FirstOrDefault(c => c.Number == num &&
                    blocksByNumber.TryGetValue(num, out var blocks) && blocks.Any(b => b.Contains(c.BlockContains, StringComparison.Ordinal)));
                labels[num] = combined?.Label ?? num;
            }
            return labels;
        }

        /// <summary>Python's "{:,.1f}" (thousands separator, one decimal).</summary>
        /// <summary>
        /// The proven placement of an approved-footprint block: its transform and the plan corners of the approved body
        /// (build_boq_v7.placed_body); a Hebrew reason when the transform or the definition is not proven.
        /// </summary>
        internal static (BoqInsertTransform? Transform, BoqPoint[]? Body, string? Why) PlacedBody(
            BoqPhysicalFootprint fp, BoqRecord r, BoqInputSet input)
        {
            if (!input.CountTransforms.TryGetValue((r.Src, r.Handle), out var t) || t.Scale.Length != 3 || t.Normal.Length != 3 ||
                !double.IsFinite(t.X) || !double.IsFinite(t.Y) || !double.IsFinite(t.Rotation) ||
                !t.Scale.Concat(t.Normal).All(double.IsFinite))
                return (null, null, "טרנספורם חסר");
            if (Math.Abs(t.Normal[2] - 1) > 1e-6 || t.Scale.Min() <= 0)
                return (null, null, "normal שאינו +Z או scale שלילי");
            if (!input.BlockSignatures.TryGetValue((r.Src, fp.Block), out var sig) || sig == null || !sig.Matches(fp.Signature))
                return (null, null, "הגדרת הבלוק שונה מההגדרה המאושרת");
            var b = fp.Signature.BasePoint;
            var (c, s) = (Math.Cos(t.Rotation), Math.Sin(t.Rotation));
            var e = fp.BodyEnvelope;
            var corners = new[] { (e[0], e[1]), (e[2], e[1]), (e[2], e[3]), (e[0], e[3]) }
                .Select(p => new BoqPoint(t.X + c * (p.Item1 - b[0]) * t.Scale[0] - s * (p.Item2 - b[1]) * t.Scale[1],
                                          t.Y + s * (p.Item1 - b[0]) * t.Scale[0] + c * (p.Item2 - b[1]) * t.Scale[1]))
                .ToArray();
            return (t, corners, null);
        }

        /// <summary>Translation, rotation (wrapped) and scale within the footprint's same-transform tolerances.</summary>
        internal static bool SameTransform(BoqInsertTransform a, BoqInsertTransform b, BoqPhysicalFootprint fp)
        {
            var d = (a.Rotation - b.Rotation + Math.PI) % (2 * Math.PI);
            if (d < 0) d += 2 * Math.PI;
            return double.Hypot(a.X - b.X, a.Y - b.Y) <= fp.SameTranslationM && Math.Abs(d - Math.PI) <= fp.SameRotationRad &&
                   a.Scale.Zip(b.Scale).All(p => Math.Abs(p.First - p.Second) <= fp.SameScaleRel * Math.Max(Math.Abs(p.First), Math.Abs(p.Second)));
        }

        /// <summary>Plan distance between two convex quadrilaterals; 0 when they touch or overlap (separating axis first).</summary>
        internal static double BodyGap(IReadOnlyList<BoqPoint> a, IReadOnlyList<BoqPoint> b)
        {
            var separated = false;
            foreach (var poly in new[] { a, b })
            {
                for (var i = 0; i < 4 && !separated; i++)
                {
                    var (p, q) = (poly[i], poly[(i + 1) % 4]);
                    var (nx, ny) = (q.Y - p.Y, p.X - q.X);
                    var pa = a.Select(v => nx * v.X + ny * v.Y).ToList();
                    var pb = b.Select(v => nx * v.X + ny * v.Y).ToList();
                    separated = pa.Max() < pb.Min() || pb.Max() < pa.Min();
                }
                if (separated) break;
            }
            if (!separated) return 0.0;
            static double Seg(BoqPoint p, BoqPoint q, BoqPoint r)
            {
                var (dx, dy) = (q.X - p.X, q.Y - p.Y);
                var t = Math.Max(0.0, Math.Min(1.0, ((r.X - p.X) * dx + (r.Y - p.Y) * dy) / (dx * dx + dy * dy)));
                return double.Hypot(p.X + t * dx - r.X, p.Y + t * dy - r.Y);
            }
            var fromA = Enumerable.Range(0, 4).SelectMany(i => b.Select(q => Seg(a[i], a[(i + 1) % 4], q))).Min();
            var fromB = Enumerable.Range(0, 4).SelectMany(i => a.Select(q => Seg(b[i], b[(i + 1) % 4], q))).Min();
            return Math.Min(fromA, fromB);
        }

        internal static string F1(double value) => value.ToString("#,##0.0", CultureInfo.InvariantCulture);

        /// <summary>
        /// The exclusion reason of a line's control records: the ruleset's control.reason when given ("{line}" = the line
        /// id), else the lane's control_reasons text of the line ("{ref}" = its item), else built from the line's own data —
        /// the items its line_sum adds up and who decided it.
        /// </summary>
        internal static string ControlReason(BoqRuleset rules, BoqLine line, int roadClass)
        {
            if (line.Control?.Reason is { Length: > 0 } custom) return custom.Replace("{line}", line.Id).Replace("{ref}", line.Ref());
            if (rules.ControlReasons.TryGetValue(line.Id, out var text) && text.Length > 0) return text.Replace("{ref}", line.Ref());
            var sum = line.Parts.FirstOrDefault(p => p.Kind == "line_sum");
            if (sum == null) return $"{line.Ref()}: בקרה בלבד — לא נכנס לכמות";
            var items = sum.Lines.Select(id => rules.Lines.FirstOrDefault(l => l.Id == id)?.ItemFor(roadClass) ?? id);
            return $"{line.Ref()}: בקרה בלבד — הכמות היא סכום הסעיפים {string.Join(" + ", items)}" +
                   (string.IsNullOrWhiteSpace(sum.DecidedBy) ? "" : $" ({sum.DecidedBy})");
        }

        private static void ApplyOwnership(BoqRuleset rules, IReadOnlyList<BoqRecord> recs, BoqBucket[] bucket, Action<int, string> exclude)
        {
            // Geometric identity of owner records is computed once, over ALL owner records (before any exclusion).
            var ownerKeys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var ownerHandles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var owner in rules.Ownership.Select(o => o.DuplicateOwner).Where(o => o != null).Distinct())
            {
                ownerKeys[owner!] = recs.Where(r => r.Src == owner).Select(GeometryKey).ToHashSet(StringComparer.Ordinal);
                ownerHandles[owner!] = recs.Where(r => r.Src == owner).Select(r => r.Handle).ToHashSet(StringComparer.Ordinal);
            }
            foreach (var rule in rules.Ownership)
            {
                var src = rule.Src.ToHashSet(StringComparer.Ordinal);
                var layers = rule.Layers.ToHashSet(StringComparer.Ordinal);
                if (rule.LayersFromPartKind != null)
                    layers.UnionWith(rules.AllParts.Where(p => p.Kind == rule.LayersFromPartKind).SelectMany(p => p.Layers));
                var layerFilter = rule.Layers.Count > 0 || rule.LayersFromPartKind != null;
                var etypes = rule.Etypes.ToHashSet(StringComparer.Ordinal);
                var kinds = rule.Kinds.ToHashSet(StringComparer.Ordinal);
                var blocks = rule.Blocks.ToHashSet(StringComparer.Ordinal);
                for (var i = 0; i < recs.Count; i++)
                {
                    if (bucket[i] != BoqBucket.None) continue;
                    var r = recs[i];
                    if (src.Count > 0 && !src.Contains(r.Src)) continue;
                    if (layerFilter && !layers.Contains(r.Layer)) continue;
                    if (rule.LayerPrefix != null && !r.Layer.StartsWith(rule.LayerPrefix, StringComparison.Ordinal)) continue;
                    if (etypes.Count > 0 && !etypes.Contains(r.Etype)) continue;
                    if (kinds.Count > 0 && !kinds.Contains(r.Kind)) continue;
                    if (blocks.Count > 0 && !blocks.Contains(r.Block)) continue;
                    if (rule.DuplicateOwner != null)
                    {
                        if (rule.DuplicateSameHandle && !ownerHandles[rule.DuplicateOwner].Contains(r.Handle)) continue;
                        if (!ownerKeys[rule.DuplicateOwner].Contains(GeometryKey(r))) continue;
                    }
                    exclude(i, rule.Reason.Replace("{src}", r.Src));
                }
            }
        }

        /// <summary>(kind, extents rounded to 0.1, quantity rounded to 0.1) — the reference duplicate key.</summary>
        internal static string GeometryKey(BoqRecord r)
        {
            var bbox = r.Bbox == null ? "" : string.Join(",", r.Bbox.Take(4).Select(v => Round1(v)));
            return r.Kind + "|" + bbox + "|" + Round1(r.Qty);
        }

        // "+ 0.0" folds -0.0 into 0.0, as Python's tuple equality does.
        private static string Round1(double v) =>
            (PythonRound1(v) + 0.0).ToString("R", CultureInfo.InvariantCulture);

        /// <summary>
        /// Python's round(x, 1): the EXACT binary value rounded half-to-even to one decimal, returned as the double nearest
        /// to that decimal. Math.Round(x, 1) scales by 10 first, so a value stored just below .x5 (203647.15 is
        /// 203647.1499999999941…) can round up (203647.2) where the reference rounds down (203647.1).
        /// </summary>
        internal static double PythonRound1(double value) => PythonRound(value, 1);

        /// <summary>Python's round(x, digits) for 0 ≤ digits ≤ 6: the exact binary value rounded half-to-even (see above).</summary>
        internal static double PythonRound(double value, int digits)
        {
            if (digits < 0 || digits > 6) throw new ArgumentOutOfRangeException(nameof(digits));
            if (!double.IsFinite(value) || value == 0.0) return value;
            if (Math.Abs(value) >= 1e14 / Math.Pow(10, digits - 1)) return Math.Round(value, digits, MidpointRounding.ToEven); // never a drawing coordinate
            var bits = BitConverter.DoubleToInt64Bits(Math.Abs(value));
            var biased = (int)(bits >> 52);
            var mantissa = bits & 0xFFFFFFFFFFFFFL;
            if (biased == 0) biased = 1; else mantissa |= 1L << 52;
            var shift = 1075 - biased; // |value| = mantissa / 2^shift exactly
            if (shift <= 0) return value; // an integer: nothing below the decimals
            var scale = System.Numerics.BigInteger.Pow(10, digits);
            var denominator = System.Numerics.BigInteger.One << shift;
            var quotient = System.Numerics.BigInteger.DivRem(new System.Numerics.BigInteger(mantissa) * scale, denominator, out var remainder);
            var twice = remainder * 2;
            if (twice > denominator || (twice == denominator && !quotient.IsEven)) quotient += System.Numerics.BigInteger.One;
            // |quotient| < 1e15 < 2^53: exact; the division is correctly rounded.
            var rounded = (double)(long)quotient / Math.Pow(10, digits);
            return value < 0 ? -rounded : rounded;
        }

        /// <summary>
        /// The crossing part (v4): claims the records of the crossing roles (yellow edges, white-only lines, hatch-only
        /// stripes, an assembly's dashed line and hatch); then per hatch crossing / assembly the hatch area: returned,
        /// strictly recovered, or — hatch area missing — the area of the closed polyline drawn with the hatch's own vertices
        /// (that polyline's area record becomes the item's, excluded as boundary before); otherwise missing, never zero.
        /// </summary>
        private static void SelectCrossings(BoqPart part, BoqPartResult P, IReadOnlyList<BoqRecord> recs, BoqBucket[] bucket,
            string?[] reason, (string Line, int Part)?[] items, IReadOnlyDictionary<string, string> roles,
            IReadOnlyList<BoqCrossing> crossings, BoqInputSet input, BoqCrosswalkConfig? cw, List<BoqMissingRow> missing)
        {
            var layers = part.Layers.ToHashSet(StringComparer.Ordinal);
            var hatchArea = new Dictionary<string, double>(StringComparer.Ordinal);
            var areaRecord = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                if (!part.Src.Contains(r.Src) || r.Kind != "area") continue;
                if (r.Etype == "HATCH") hatchArea[r.Handle] = r.Qty;
                else areaRecord[r.Handle] = i;
            }
            for (var i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                if (bucket[i] != BoqBucket.None || !part.Src.Contains(r.Src) || !layers.Contains(r.Layer)) continue;
                if (!roles.TryGetValue(r.Handle, out var role) ||
                    role is not (BoqCrosswalkGeometry.RoleYellowEdge or BoqCrosswalkGeometry.RoleWhiteOnly or BoqCrosswalkGeometry.RoleHatchStripe
                        or BoqCrosswalkGeometry.RoleAssemblyDashed or BoqCrosswalkGeometry.RoleAssemblyHatch))
                    continue;
                bucket[i] = BoqBucket.Item;
                items[i] = (P.LineId, P.Index);
                P.Objects++;
                P.Layers.Add(r.Layer);
                P.Srcs.Add(r.Src);
            }
            foreach (var c in crossings.Where(c => c.WidthSource is "hatch" or "hatch-assembly"))
            {
                double got = 0.0;
                int miss = 0, fallback = 0;
                foreach (var h in c.HatchHandles)
                {
                    var boundaryIndex = c.Boundary.TryGetValue(h, out var b) && areaRecord.TryGetValue(b, out var bi) ? bi : -1;
                    if (hatchArea.TryGetValue(h, out var area) && area != 0.0) got += area;
                    else if (input.StrictHatchRecoveries.TryGetValue(h, out var recovered) && recovered is { } value && value != 0.0) got += value;
                    else if (boundaryIndex >= 0 && recs[boundaryIndex].Qty != 0.0)
                    {
                        // The closed polyline with the hatch's own vertices: the same "area from the boundary" rule as HA.
                        var br = recs[boundaryIndex];
                        got += br.Qty;
                        fallback++;
                        bucket[boundaryIndex] = BoqBucket.Item;
                        reason[boundaryIndex] = null;
                        items[boundaryIndex] = (P.LineId, P.Index);
                        P.Objects++;
                        P.Layers.Add(br.Layer);
                        P.Srcs.Add(br.Src);
                    }
                    else
                    {
                        miss++;
                        missing.Add(new BoqMissingRow(P.LineId, part.Src.FirstOrDefault() ?? "", cw?.HatchLayers.FirstOrDefault() ?? "", h,
                            cw?.MissingHatchWhat ?? "", MissingAction));
                    }
                }
                c.HatchM2 = got;
                c.MissingHatches = miss;
                c.Fallback = fallback;
                P.Missing += miss;
            }
            P.Crossings = crossings;
        }

        private static void SelectHatchTotals(BoqPart part, BoqPartResult P, BoqInputSet input, List<BoqMissingRow> missing)
        {
            foreach (var t in input.HatchLayers)
            {
                if (!part.Src.Contains(t.Src) || !part.Layers.Contains(t.Layer)) continue;
                P.Base += (double)(t.DirectArea + t.RecoveredArea);
                P.Objects += t.DirectCount + t.RecoveredCount;
                P.Layers.Add(t.Layer);
                P.Srcs.Add(t.Src);
                P.Missing += t.UnresolvedCount;
                if (t.RecoveredCount > 0) P.Notes.Add(BoqRulesWorkbookWriter.CountHe(t.RecoveredCount, "הצללה אחת ששטחה חושב מהגבול", "הצללות ששטחן חושב מהגבול"));
                if (t.UnresolvedCount <= 0) continue;
                const string what = "הצללה ששטחה לא הוחזר — השטח לא ידוע (לא אפס)";
                if (t.UnresolvedHandles is { Count: > 0 } handles)
                    foreach (var h in handles) missing.Add(new BoqMissingRow(P.LineId, t.Src, t.Layer, h, what, MissingAction));
                else
                    missing.Add(new BoqMissingRow(P.LineId, t.Src, t.Layer,
                        $"{t.UnresolvedCount} הצללות (ללא פירוט Handle במקור)", what, MissingAction));
            }
        }
    }
}
