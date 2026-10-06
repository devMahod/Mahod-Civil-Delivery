using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Whether drawing/XREF linework was completely inspected for projected utilities.
    /// This is separate from the number of crossings in one section: zero crossings is
    /// not evidence that the drawing contains no utility geometry.
    /// </summary>
    public enum UtilityProjectionScanState
    {
        NotRun,
        Complete,
        Blocked,
        Disabled,
    }

    /// <summary>
    /// The geometry and classification behind projecting drawing linework into a
    /// created section view: utilities that live as polylines (usually inside the
    /// UT XREF), curbs and lane lines from the plan layout, and the right-of-way.
    ///
    /// Civil can only SAMPLE its own objects (surfaces, pipe networks, corridors)
    /// into a section — a fact 6422 ran into head-on: the water/sewer/power lines
    /// arrive through UT-3D.dwg as plain polylines, the tool reported "no systems in
    /// the drawing", and the engineer rightly pointed at her old drafted sections
    /// where the systems are visible (meeting recording, 2026-08-30). Those old
    /// sections are static drafting; this projection is the honest dynamic
    /// equivalent: intersect each system line with the sample line, mark the section
    /// at the true offset (and true depth when the line carries elevations), and
    /// label it in Hebrew.
    ///
    /// Everything here is pure so it can be proven by unit tests; the plug-in layer
    /// only walks the drawing and draws what this class computed.
    /// </summary>
    public static class SectionProjectionLogic
    {
        /// <summary>A plan-space point (drawing metres).</summary>
        public readonly record struct P2(double X, double Y);

        /// <summary>A polyline vertex; Z is 0 for flat 2D drafting.</summary>
        public readonly record struct V3(double X, double Y, double Z);

        /// <summary>One place a projected line crosses the sample line.</summary>
        public sealed record Crossing(
            double Offset,        // signed section offset, metres; negative = left of the axis
            double? Elevation,    // null when the source polyline carries no real Z
            string Layer,
            string? Xref,
            ProjectionRuleMatch Rule,
            string? SourceHandle = null,
            double? WcsX = null,
            double? WcsY = null)
        {
            public string? SourceDrawingPath { get; init; }
            public string? SourceDrawingHash { get; init; }
        }

        /// <summary>The rule that classified a layer, resolved to display facts.</summary>
        public sealed record ProjectionRuleMatch(string Kind, string Label, short ColorIndex);

        /// <summary>Below this absolute Z a polyline is treated as flat drafting, not elevation data.</summary>
        public const double FlatZToleranceM = 0.01;

        // ------------------------------------------------------------ intersection

        /// <summary>
        /// Intersects segment a→b with every edge of a polyline and returns, per hit,
        /// the parameter t along a→b (metres from a) and the interpolated Z of the
        /// POLYLINE at the hit (null when the polyline is flat there).
        /// </summary>
        public static List<(double T, double? Z)> IntersectPolyline(
            P2 a, P2 b, IReadOnlyList<V3> verts, bool closed)
        {
            var hits = new List<(double, double?)>();
            if (verts.Count < 2) return hits;

            int edges = closed ? verts.Count : verts.Count - 1;
            for (int i = 0; i < edges; i++)
            {
                var p = verts[i];
                var q = verts[(i + 1) % verts.Count];
                var hit = IntersectSegments(a, b, new P2(p.X, p.Y), new P2(q.X, q.Y));
                if (hit is not { } h) continue;

                double? z = null;
                var zp = p.Z;
                var zq = q.Z;
                if (Math.Abs(zp) > FlatZToleranceM || Math.Abs(zq) > FlatZToleranceM)
                    z = zp + (zq - zp) * h.U;

                hits.Add((h.T, z));
            }

            // Two edges meeting exactly on the sample line produce twin hits; merge them.
            hits.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            var merged = new List<(double, double?)>();
            foreach (var h in hits)
            {
                if (merged.Count > 0 && Math.Abs(merged[^1].Item1 - h.Item1) < 0.02)
                    continue;
                merged.Add(h);
            }
            return merged;
        }

        /// <summary>
        /// Proper segment×segment intersection. Returns (t metres along a→b,
        /// u fraction along p→q), or null when they do not cross.
        /// </summary>
        public static (double T, double U)? IntersectSegments(P2 a, P2 b, P2 p, P2 q)
        {
            double rx = b.X - a.X, ry = b.Y - a.Y;
            double sx = q.X - p.X, sy = q.Y - p.Y;
            double denom = rx * sy - ry * sx;
            if (Math.Abs(denom) < 1e-12) return null; // parallel

            double qx = p.X - a.X, qy = p.Y - a.Y;
            double tFrac = (qx * sy - qy * sx) / denom;
            double u = (qx * ry - qy * rx) / denom;
            if (tFrac < -1e-9 || tFrac > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9) return null;

            var len = Math.Sqrt(rx * rx + ry * ry);
            return (tFrac * len, Math.Clamp(u, 0, 1));
        }

        // ----------------------------------------------------------- offset signs

        /// <summary>
        /// The signed section offset of a point on the sample line.
        ///
        /// The sign convention must match the SectionView's axis, and guessing it from
        /// the drawing direction of the CL line is exactly the kind of guess that fails
        /// on someone else's machine. Civil states which end of the created sample line
        /// is its LEFT (SampleLineVertex.Side), so the caller passes that endpoint and
        /// the sign becomes a fact: toward the left endpoint = negative.
        /// </summary>
        public static double SignedOffset(P2 crossing, P2 point, P2 leftEndpoint)
        {
            var d = Math.Sqrt((point.X - crossing.X) * (point.X - crossing.X) +
                              (point.Y - crossing.Y) * (point.Y - crossing.Y));
            var dot = (point.X - crossing.X) * (leftEndpoint.X - crossing.X) +
                      (point.Y - crossing.Y) * (leftEndpoint.Y - crossing.Y);
            return dot > 0 ? -d : d;
        }

        /// <summary>Metres from a to the projection of point onto a→b (unclamped).</summary>
        public static double ParamAlong(P2 a, P2 b, P2 point)
        {
            double rx = b.X - a.X, ry = b.Y - a.Y;
            var len = Math.Sqrt(rx * rx + ry * ry);
            if (len < 1e-9) return 0;
            return ((point.X - a.X) * rx + (point.Y - a.Y) * ry) / len;
        }

        // -------------------------------------------------------------- width cap

        /// <summary>
        /// Trims the CL endpoints to at most <paramref name="maxHalfWidthM"/> on each
        /// side of the crossing. A CL drawn 88 m long makes an 88 m section that
        /// dwarfs the road (engineer feedback, 2026-08-30); the drawn line keeps
        /// deciding the DIRECTION and the cap only limits how far it reaches.
        /// </summary>
        public static (double Ax, double Ay, double Bx, double By, bool Trimmed) CapEndpoints(
            P2 crossing, P2 a, P2 b, double maxHalfWidthM)
        {
            if (maxHalfWidthM <= 0) return (a.X, a.Y, b.X, b.Y, false);

            var (ax, ay, ta) = CapOne(crossing, a, maxHalfWidthM);
            var (bx, by, tb) = CapOne(crossing, b, maxHalfWidthM);
            return (ax, ay, bx, by, ta || tb);

            static (double X, double Y, bool T) CapOne(P2 c, P2 e, double cap)
            {
                var dx = e.X - c.X; var dy = e.Y - c.Y;
                var d = Math.Sqrt(dx * dx + dy * dy);
                if (d <= cap || d < 1e-9) return (e.X, e.Y, false);
                var f = cap / d;
                return (c.X + dx * f, c.Y + dy * f, true);
            }
        }

        // ---------------------------------------------------------- classification

        /// <summary>
        /// Matches a layer (and the XREF it came from) against the profile's rules,
        /// falling back to the built-in Israeli utility layer dictionary. Returns null
        /// for layers that are nobody's business in a section.
        /// </summary>
        public static ProjectionRuleMatch? Classify(
            string layer,
            string? xrefName,
            IReadOnlyList<ProjectionRuleConfig> profileRules)
        {
            foreach (var rule in profileRules)
            {
                if (!string.IsNullOrEmpty(rule.LayerPattern) && !MatchesLayer(layer, rule.LayerPattern)) continue;
                if (!string.IsNullOrEmpty(rule.XrefPattern) &&
                    !MatchesXrefChain(xrefName, rule.XrefPattern)) continue;
                if (string.IsNullOrEmpty(rule.LayerPattern) && string.IsNullOrEmpty(rule.XrefPattern)) continue;

                return new ProjectionRuleMatch(
                    string.IsNullOrWhiteSpace(rule.Kind) ? "utility" : rule.Kind!,
                    string.IsNullOrWhiteSpace(rule.Label) ? AutoLabel(layer) : rule.Label!,
                    rule.ColorIndex ?? DefaultColor(layer));
            }

            // Built-in dictionary: only when the profile did not decide.
            foreach (var (pattern, label, kind, color) in BuiltInRules)
            {
                if (MatchesLayer(layer, pattern))
                    return new ProjectionRuleMatch(kind, label, color);
            }
            return null;
        }

        /// <summary>
        /// AutoCAD reports a layer owned by an XREF as <c>XREF|LAYER</c>.  Project
        /// profiles are deliberately written against the supplier's real layer name,
        /// so an exact rule such as <c>KAV_NETIVIM_NTZ</c> must also match the leaf of
        /// a qualified name.  The complete value is still tested first so a project
        /// may intentionally scope a layer pattern to an XREF-qualified name.
        /// </summary>
        internal static bool MatchesLayer(string layer, string pattern)
        {
            if (Wildcard(layer ?? string.Empty, pattern)) return true;
            var leaf = LayerLeaf(layer);
            return !string.Equals(leaf, layer, StringComparison.Ordinal) &&
                   Wildcard(leaf, pattern);
        }

        /// <summary>
        /// Returns the supplier-owned leaf of an AutoCAD dependent layer name.
        /// Nested XREFs may produce <c>OUTER|INNER|LAYER</c>; business rules and
        /// approved estimate mappings belong to <c>LAYER</c>, while the full qualified
        /// value remains available separately in provenance.
        /// </summary>
        public static string LayerLeaf(string? layer)
        {
            if (string.IsNullOrWhiteSpace(layer)) return string.Empty;
            var value = layer.Trim();
            var separator = value.LastIndexOf('|');
            return separator >= 0 && separator + 1 < value.Length
                ? value.Substring(separator + 1)
                : value;
        }

        /// <summary>
        /// Nested XREF collection retains the full readable chain (for example
        /// <c>BASE &gt; UT-3D</c>). A profile rule may intentionally name either the
        /// outer supplier model or the actual nested source, so test the complete
        /// chain and every exact chain component. Ordinary block names are not added
        /// to this chain and therefore cannot accidentally satisfy an XREF rule.
        /// </summary>
        internal static bool MatchesXrefChain(string? xrefChain, string pattern)
        {
            if (Wildcard(xrefChain ?? string.Empty, pattern)) return true;
            if (string.IsNullOrWhiteSpace(xrefChain)) return false;
            return xrefChain.Split('>')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .Any(part => Wildcard(part, pattern));
        }

        /// <summary>Serializable profile rule (mirrors ProjectProfile.Sections.Projection).</summary>
        public sealed record ProjectionRuleConfig(
            string? LayerPattern, string? XrefPattern, string? Label, string? Kind, short? ColorIndex)
        {
            public string? Origin { get; init; }
        }

        /// <summary>
        /// Office plan-mark conventions observed in Nataly's SM/GM drawings after the
        /// first managed project profile had already been approved. Installer upgrades
        /// preserve that profile, so these are runtime defaults rather than profile
        /// mutations. Explicit project rules retain their declared order and take
        /// precedence, including wildcard and XREF-scoped rules. Only the complete,
        /// unchanged historical gate-0 bundle with its original package provenance
        /// is recognized as inherited tool defaults, not as five engineer overrides.
        /// A convention from
        /// one supplier must never silently override a different project's choice.
        /// </summary>
        public static readonly IReadOnlyList<ProjectionRuleConfig> ObservedPlanMarkDefaults =
            new List<ProjectionRuleConfig>
            {
                // Exact audited leaf names from Natalie's GM/HA models. Keep these
                // narrow: similar hatch/survey layers are not proven section
                // boundaries. MatchesLayer also applies the exact rule to an
                // XREF-qualified leaf (for example GM|TR-INNER-GRDN-STONE).
                new("TR-INNER-ISLAND-CURBSTONE", null, "אי תנועה", "island", null),
                new("TR-INNER-GRDN-STONE", null, "גבול גינון", "garden", null),
                new("TR-GRDN-STONE", null, "גבול גינון", "garden", null),
                new("*CURB-ILND*", null, "אי תנועה", "island", null),
                // Exact supplier convention observed in Natalie's GM layer table.
                // It is a garden boundary, not an ordinary curb; two such boundaries
                // prove a garden span while one alone remains unresolved.
                new("*CURB-GRDN*", null, "גבול גינון", "garden", null),
                new("*MIDRACHA*", null, "מדרכה", "sidewalk", null),
                new("*SIDEWALK*", null, "מדרכה", "sidewalk", null),
                new("KAV_NETIVIM_NTZ", null, "נת\"צ", "strip", null),
                // Netivei Israel longitudinal lane separators in Natalie's SM model
                // (Table 4: 801 lane/axis line, 803 double solid, 808 rectangles for an
                // auxiliary lane). Without them STA-42676 showed one 6.36 m "lane"
                // where SM draws 3-1.5-808 at +2.70 between two lanes (live 29/09).
                // Transverse or in-junction marks (809 turn guides, 810 stop line,
                // 811/812 crossings, 815 island hatching) are deliberately absent.
                new("TR-MARK-WHT-801", null, "קו נתיב", "lane", null),
                new("TR-MARK-WHT-3-3-801", null, "קו נתיב", "lane", null),
                new("TR-MARK-WHT-801-250", null, "קו נתיב", "lane", null),
                new("TR-MARK-WHT-803", null, "קו הפרדה כפול", "lane", null),
                new("TR-MARK-WHT-808", null, "קו מלבנים", "lane", null),
                new("TR-MARK-WHT-808-3-3", null, "קו מלבנים", "lane", null),
                new("TR-MARK-WHT-3-1.5-808", null, "קו מלבנים", "lane", null),
                // Exact western-model/XREF convention observed in the offline DWG audit.
                new("ROW_2024-08", null, "זכות דרך", "row", null),
                new("*PGVUL*", null, "זכות דרך", "row", null),
            };

        public static List<ProjectionRuleConfig> WithObservedPlanMarkDefaults(
            IReadOnlyList<ProjectionRuleConfig> profileRules,
            ProjectProfile.ProfileProvenance? provenance = null)
        {
            if (IsKnownLegacyPlanMarkDefaultBundle(profileRules, provenance))
                return ObservedPlanMarkDefaults.Concat(profileRules).ToList();

            var merged = new List<ProjectionRuleConfig>(profileRules);
            var explicitPatterns = new HashSet<string>(
                profileRules.Select(r => r.LayerPattern ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);

            foreach (var fallback in ObservedPlanMarkDefaults)
            {
                if (!explicitPatterns.Contains(fallback.LayerPattern ?? string.Empty))
                    merged.Add(fallback);
            }
            return merged;
        }

        // Compatibility identity for the shipped Input Package v1, not a project-ID
        // heuristic or a hash of a user's mutable profile. Later approvals for ROW,
        // span labels, pricing, etc. do not retrospectively approve these five rules.
        // No partially matching bundle is upgraded; every changed/extra field or
        // explicit origin retains ordinary profile-first semantics. This does not
        // manufacture per-rule approval or modify the persisted profile.
        public static bool IsKnownLegacyPlanMarkDefaultBundle(
            IReadOnlyList<ProjectionRuleConfig> rules,
            ProjectProfile.ProfileProvenance? provenance)
        {
            if (provenance is null || rules.Count != 5 ||
                provenance.CreatedBy != "civil-delivery gate-0" ||
                provenance.CreatedAtUtc != new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc) ||
                provenance.CreatedAtUtc.Value.Kind != DateTimeKind.Utc ||
                (provenance.Source ?? string.Empty).Split('|')[0].Trim() !=
                    "Input Package v1 (Materials collected by Arthur, 2026-08-18)" ||
                provenance.SourceHashes is null ||
                !OriginalHash("CL.dwg", "eae8f807734b04bba2497570ec27da431ac7b39f96456e3d50e2ac56b38f1574") ||
                !OriginalHash("6422-HW-CS.dwg", "aaa6a6cd77698d84e672d4cf88d18699ee4f2656c3d0f8de70289e0d5c54d64e"))
                return false;

            var known = new ProjectionRuleConfig[]
            {
                new("*TR-ISLAND*", null, "אי תנועה", "island", null),
                new("*BIKE*", null, "שביל אופניים", "bike", null),
                new("END-MDR*", null, "מדרכה", "sidewalk", null),
                new("HW-TRWY*", null, "שפת מיסעה", "lane", null),
                new("*CURB*", null, "אבן שפה", "curb", null),
            };
            return rules.SequenceEqual(known);

            bool OriginalHash(string name, string expected) =>
                provenance.SourceHashes.TryGetValue(name, out var actual) &&
                string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The Israeli road-drawing utility layer prefixes seen on real Mahod projects
        /// (6422's host drawing and UT XREF: MAIM*, BIUV*, HASHMAL*, BEZEQ*, HOT*...).
        /// A weak default, deliberately narrow: unknown layers are NOT guessed into
        /// sections — the profile is where a project widens this.
        /// </summary>
        public static readonly IReadOnlyList<(string Pattern, string Label, string Kind, short Color)> BuiltInRules =
            new List<(string, string, string, short)>
            {
                // The 6422 UT XREF carries a second family of layer names - the municipal
                // utility-survey convention (MYA-4801-Biuv-line, MYA-4404-Teura-line...).
                // Prefix-only patterns missed all of them and the tool said "no systems"
                // while the engineer was looking at them (live, 30/08). Contains-patterns
                // for the distinctive tokens close that hole; short ambiguous tokens
                // (HOT, GAZ) stay prefix-anchored so C-ANNO-GAZEBO-like names cannot match.
                ("*BIUV*",    "ביוב",    "utility", 34),
                ("*TEURA*",   "תאורה",   "utility", 30),
                ("*TAURA*",   "תאורה",   "utility", 30),
                ("*BEZEQ*",   "בזק",     "utility", 6),
                ("*BZQ*",     "בזק",     "utility", 6),
                ("*MEKOROT*", "מקורות",  "utility", 5),
                ("*NIKUZ*",   "ניקוז",   "utility", 4),
                ("*HASHMAL*", "חשמל",    "utility", 1),
                ("RAMZOR*",   "רמזור",   "utility", 30),
                ("*MAIM*",    "מים",     "utility", 5),
                ("MAIM*",     "מים",     "utility", 5),   // blue
                ("*WATER*",   "מים",     "utility", 5),
                ("C-WATR-PIPE*", "מים",  "utility", 5),
                ("BIUV*",     "ביוב",    "utility", 34),  // brown
                ("*SEWER*",   "ביוב",    "utility", 34),
                ("C-SSWR-PIPE*", "ביוב", "utility", 34),
                ("NIKUZ*",    "ניקוז",   "utility", 4),   // cyan
                ("*DRAIN*",   "ניקוז",   "utility", 4),
                ("C-STRM-PIPE*", "ניקוז", "utility", 4),
                ("HASHMAL*",  "חשמל",    "utility", 1),   // red
                ("*ELEC*",    "חשמל",    "utility", 1),
                ("TEURA*",    "תאורה",   "utility", 30),   // yellow
                ("TAURA*",    "תאורה",   "utility", 30),
                ("*LIGHT*",   "תאורה",   "utility", 30),
                ("BEZEQ*",    "בזק",     "utility", 6),   // magenta
                ("TIKSHORET*","תקשורת",  "utility", 6),
                ("*TELECOM*", "תקשורת",  "utility", 6),
                ("S_PHONE*",  "בזק/תקשורת", "utility", 6),
                ("HOT*",      "HOT",     "utility", 6),
                ("GAZ*",      "גז",      "utility", 30),  // orange
                ("*GAS*",     "גז",      "utility", 30),
                ("MEKOROT*",  "מקורות",  "utility", 5),
                ("KOLHIN*",   "קולחין",  "utility", 92),
                ("CURB*",     "אבן שפה", "curb",    7),
                ("*ROW*",     "זכות דרך","row",     1),
                ("*PGVUL*",   "זכות דרך","row",     1),   // office convention (Nataly, 31/08)
                ("ZCHUT*",    "זכות דרך","row",     1),
            };

        /// <summary>A readable fallback label when no rule names one: the layer itself.</summary>
        public static string AutoLabel(string layer) => Bidi.Ltr(LayerLeaf(layer));

        private static short DefaultColor(string layer) => 7;

        public static bool Wildcard(string value, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            return WildcardRegex(pattern).IsMatch(value);
        }

        /// <summary>
        /// One constructed regex per pattern. The static Regex.IsMatch cache holds 15
        /// entries; a profile has more layer patterns than that, so every entity of a
        /// large XREF rebuilt the same expressions (live 29/09: project.collect took
        /// 98 s for 7,615 host entities plus XREF content, on every PLAN/APPLY/VERIFY).
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,
            System.Text.RegularExpressions.Regex> WildcardRegexes = new(StringComparer.Ordinal);

        private static System.Text.RegularExpressions.Regex WildcardRegex(string pattern) =>
            WildcardRegexes.GetOrAdd(pattern, key => new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(key)
                    .Replace("\\*", ".*").Replace("\\?", ".") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));

        // ------------------------------------------------------------- mark maths

        /// <summary>
        /// Widths between adjacent plan marks of the same kind — the "3.00  6.35"
        /// dimension row a drafted section carries between curb/lane lines. Marks
        /// closer than <paramref name="minWidthM"/> are drafting noise, wider than
        /// <paramref name="maxWidthM"/> are unrelated features.
        /// </summary>
        public static List<(double From, double To, double Width)> AdjacentWidths(
            IEnumerable<double> offsets, double minWidthM = 0.5, double maxWidthM = 30.0)
        {
            var sorted = offsets.OrderBy(o => o).ToList();
            var result = new List<(double, double, double)>();
            for (int i = 1; i < sorted.Count; i++)
            {
                var w = sorted[i] - sorted[i - 1];
                if (w >= minWidthM && w <= maxWidthM)
                    result.Add((sorted[i - 1], sorted[i], w));
            }
            return result;
        }

        /// <summary>
        /// Kinds of plan marks that participate in the width/offset rows.  ROW is a
        /// real outer dimension boundary, not merely a decorative vertical line.
        /// </summary>
        public static readonly IReadOnlyList<string> WidthMarkKinds =
            new[]
            {
                "curb", "lane", "sidewalk", "island", "bike", "garden",
                "parking", "shoulder", "row",
            };

        /// <summary>
        /// Engineer/source-backed name dropped inside one otherwise ambiguous span.
        /// Source participates in the coverage digest so a manual approval and an
        /// observed drawing mark cannot silently substitute for one another.
        /// </summary>
        public sealed record SpanLabelOverride(
            double Offset,
            string Label,
            string Source,
            string? Evidence = null);

        public sealed record UnresolvedSpan(
            double From,
            double To,
            double Width,
            string LeftKind,
            string RightKind,
            string Reason);

        /// <summary>
        /// One real width-participating source crossing. SourceIdentity is stable
        /// provenance (drawing/XREF/layer/handle/WCS digest), not a display label. It
        /// participates in the presentation digest so equal-looking geometry from a
        /// different source cannot silently replace PLAN evidence.
        /// </summary>
        public sealed record PresentationMark(
            double Offset,
            string Kind,
            string Label,
            string SourceIdentity)
        {
            public DimensionSourceEvidence? SourceEvidence { get; init; }
        }

        /// <summary>Every measured source remains evidence, even at an identical anchor.</summary>
        public sealed record DimensionSourceEvidence(
            string Identity, string Kind, string Label, string Layer, string? Xref,
            string? SourceHandle, string? SourceDrawingPath, string? SourceDrawingHash,
            double? WcsX, double? WcsY, double? Elevation);

        public sealed record DimensionBoundaryEvidence(
            double Offset, string GeometryKey, IReadOnlyList<DimensionSourceEvidence> Sources)
        {
            // Evidence only. This does not invent a new precedence or source-role rule.
            public bool HasSourceSemanticConflict => Sources.Select(s => (s.Kind, s.Label))
                .Distinct().Skip(1).Any();
        }

        /// <summary>
        /// An XREF mark is bound to the exact bytes of its external file. A mark drawn in
        /// the host drawing is not: the host file changes on every save, including the save
        /// of the section this tool just created, so its hash made every such section plan
        /// as "owned-fingerprint-mismatch" after save/reopen (live 29/09, STA-12145 and
        /// STA-42676 via CURB-EXST). A host mark stays bound by path, layer, handle and its
        /// exact WCS crossing, so an edit of that geometry still changes the evidence.
        /// </summary>
        public static string? SourceHashForEvidence(string? xrefChain, string? sourceDrawingHash) =>
            string.IsNullOrWhiteSpace(xrefChain) ? null : sourceDrawingHash;

        /// <summary>Coordinate identity is independent of names, handles and source copies.</summary>
        public static string DimensionGeometryKey(double offset) => "DIM:" +
            ArtifactHash.Sha256OfText("exact-cut-offset-v1|" +
                (offset == 0 ? 0.0 : offset).ToString("R", System.Globalization.CultureInfo.InvariantCulture));

        public static PresentationMark DimensionPresentationMark(Crossing crossing)
        {
            var source = new DimensionSourceEvidence(string.Empty,
                crossing.Rule.Kind, crossing.Rule.Label, crossing.Layer, crossing.Xref,
                crossing.SourceHandle, crossing.SourceDrawingPath, crossing.SourceDrawingHash,
                crossing.WcsX, crossing.WcsY, crossing.Elevation);
            var identity = "DIM-SRC:" + ArtifactHash.Sha256OfText(
                System.Text.Json.JsonSerializer.Serialize(source));
            return new PresentationMark(crossing.Offset, crossing.Rule.Kind,
                crossing.Rule.Label, identity) { SourceEvidence = source with { Identity = identity } };
        }

        public static string DimensionSourcesCanonical(IEnumerable<DimensionSourceEvidence> sources) =>
            System.Text.Json.JsonSerializer.Serialize(sources
                .Distinct().OrderBy(s => s.Identity, StringComparer.Ordinal)
                .ThenBy(s => System.Text.Json.JsonSerializer.Serialize(s), StringComparer.Ordinal));

        /// <summary>
        /// A PLAN-time, production-independent contract for the visible road-section
        /// furniture.  It is derived from source plan-mark crossings before APPLY, so
        /// "nothing was recognized" can never become a successful empty==empty VERIFY.
        /// </summary>
        public sealed record PresentationCoverageSummary(
            int PlanMarkCount,
            int DimensionMarkCount,
            int WidthSpanCount,
            int NamedStripCount,
            int VehicleStripCount,
            int OfficeCarStripCount,
            string EvidenceDigest)
        {
            /// <summary>
            /// True only when the chain reaches both proven boundaries and every
            /// consecutive mark pair is either a measured width or a narrow furniture
            /// gap (the two faces of one curb / island nose, under the 0.50 m noise
            /// floor). An oversized gap (an unrelated mark over 30 m away) breaks it.
            /// 1.2.22-1.2.33 also treated every narrow gap as a break, so no section of
            /// a real plan (6422: 0.05-0.49 m curb faces on all 28 sections) could reach
            /// Ready after its strips were named.
            /// </summary>
            public bool ContinuousWidthChain { get; init; }

            /// <summary>Consecutive mark pairs under the noise floor: covered, unnamed furniture.</summary>
            public int NarrowGapCount { get; init; }

            /// <summary>Consecutive mark pairs over the width ceiling: real holes in the chain.</summary>
            public int OversizedGapCount { get; init; }

            /// <summary>
            /// True only when both outer limits are proven by a two-sided ROW envelope
            /// or by outermost real plan marks on both sides of CL. CL sample extents
            /// alone are geometry, not presentation evidence.
            /// </summary>
            public bool OuterBoundariesProven { get; init; }

            public string BoundarySource { get; init; } = "unproven";
            public double? BoundaryFrom { get; init; }
            public double? BoundaryTo { get; init; }

            public bool IsComplete =>
                PlanMarkCount > 0 &&
                DimensionMarkCount >= 2 &&
                WidthSpanCount > 0 &&
                NamedStripCount == WidthSpanCount &&
                ContinuousWidthChain &&
                OuterBoundariesProven;
        }

        public sealed class PresentationAnalysis
        {
            public List<(double Offset, string Kind, string Label)> DimensionMarks { get; } = new();
            public List<DimensionBoundaryEvidence> DimensionBoundaries { get; } = new();
            public List<(double From, double To, double Width)> WidthSpans { get; } = new();
            public List<(double From, double To, string Label)> StripLabels { get; } = new();
            public List<UnresolvedSpan> UnresolvedSpans { get; } = new();
            /// <summary>Named strips that touch a bus-lane (נת"צ) line: kept, and listed for the engineer's review.</summary>
            public List<(double From, double To, string Label)> BusLaneEdgeStrips { get; } = new();
            public required PresentationCoverageSummary Summary { get; init; }
        }

        /// <summary>Widths under this are drafting noise / curb faces, never a named strip.</summary>
        public const double StripNoiseFloorM = 0.5;

        /// <summary>Widths over this are unrelated marks, never one strip.</summary>
        public const double StripWidthCeilingM = 30.0;

        /// <summary>
        /// Normalizes coincident marks, produces the full width chain and names every
        /// confidently identified strip.  The same method is called by PLAN and APPLY;
        /// its digest therefore detects renamed/moved source geometry before mutation.
        /// </summary>
        public static PresentationAnalysis AnalyzePresentationCoverage(
            IEnumerable<(double Offset, string Kind, string Label)> crossings,
            double? requiredLeftOffset = null,
            double? requiredRightOffset = null,
            double boundaryToleranceM = 0.25,
            IReadOnlyList<SpanLabelOverride>? approvedOverrides = null)
        {
            return AnalyzePresentationCoverage(
                crossings.Select(c => new PresentationMark(
                    c.Offset, c.Kind, c.Label,
                    FormattableString.Invariant(
                        $"legacy-mark|{c.Offset:F6}|{c.Kind}|{c.Label}"))),
                requiredLeftOffset, requiredRightOffset, boundaryToleranceM,
                approvedOverrides);
        }

        /// <summary>
        /// Provenance-aware overload used by PLAN and APPLY. The tuple overload stays
        /// available for callers that own only normalized semantic marks.
        /// </summary>
        public static PresentationAnalysis AnalyzePresentationCoverage(
            IEnumerable<PresentationMark> crossings,
            double? requiredLeftOffset = null,
            double? requiredRightOffset = null,
            double boundaryToleranceM = 0.25,
            IReadOnlyList<SpanLabelOverride>? approvedOverrides = null)
        {
            if (!double.IsFinite(boundaryToleranceM) || boundaryToleranceM < 0)
                throw new ArgumentOutOfRangeException(nameof(boundaryToleranceM));

            var all = crossings
                .Where(c => double.IsFinite(c.Offset) && !string.IsNullOrWhiteSpace(c.Kind))
                .Select(c => c with { Kind = c.Kind.Trim().ToLowerInvariant(),
                    Label = c.Label?.Trim() ?? string.Empty,
                    SourceIdentity = c.SourceIdentity?.Trim() ?? string.Empty })
                .OrderBy(c => c.Offset)
                .ThenBy(c => c.Kind, StringComparer.Ordinal)
                .ThenBy(c => c.Label, StringComparer.Ordinal)
                .ThenBy(c => c.SourceIdentity, StringComparer.Ordinal)
                .ToList();

            static int Priority(string kind) => kind switch
            {
                "row" => 7,
                "bike" => 5,
                "sidewalk" => 4,
                "island" => 3,
                "garden" => 3,
                "parking" => 3,
                "shoulder" => 3,
                "curb" => 2,
                "lane" => 1,
                _ => 0,
            };

            var sourceDimensions = all
                .Where(c => WidthMarkKinds.Contains(c.Kind, StringComparer.Ordinal))
                // A display bin must not erase a measured physical face. Exact copies
                // share one coordinate; all their provenance is retained below.
                .GroupBy(c => c.Offset)
                .Select(g => g.OrderByDescending(c => Priority(c.Kind))
                    .ThenBy(c => c.Kind, StringComparer.Ordinal)
                    .ThenBy(c => c.Label, StringComparer.Ordinal).First())
                .OrderBy(c => c.Offset)
                .ToList();

            var rowMarks = all
                .Where(c => c.Kind == "row")
                .OrderBy(c => c.Offset)
                .ToList();

            double? boundaryFrom = null;
            double? boundaryTo = null;
            var boundarySource = "unproven";
            var boundariesProven = false;

            if (rowMarks.Count > 0)
            {
                // Once a drawing claims ROW evidence, a lone/same-side ROW may not be
                // ignored in favour of the CL ends.  It is an actionable incomplete
                // boundary contract.
                var first = rowMarks[0].Offset;
                var last = rowMarks[^1].Offset;
                if (rowMarks.Count >= 2 && first < -boundaryToleranceM &&
                    last > boundaryToleranceM && last - first >= 0.5)
                {
                    boundaryFrom = first;
                    boundaryTo = last;
                    boundarySource = "row";
                    boundariesProven = true;
                }
                else
                {
                    boundarySource = "row-incomplete";
                }
            }
            else if (sourceDimensions.Any(mark => mark.Offset < -boundaryToleranceM) &&
                     sourceDimensions.Any(mark => mark.Offset > boundaryToleranceM))
            {
                // Actual outermost marks are the honest presentation envelope. The
                // CL extents remain available to the host for SampleLine/SectionView
                // geometry, but do not become synthetic exterior strips.
                boundaryFrom = sourceDimensions[0].Offset;
                boundaryTo = sourceDimensions[^1].Offset;
                boundarySource = "plan-mark-extents";
                boundariesProven = true;
            }
            else if (requiredLeftOffset is { } left && requiredRightOffset is { } right &&
                     double.IsFinite(left) && double.IsFinite(right) &&
                     left < -boundaryToleranceM && right > boundaryToleranceM &&
                     right - left >= 0.5)
            {
                // Diagnostic fallback only. Civil geometry still has these limits,
                // but they are not evidence for a visible dimension/strip boundary.
                boundaryFrom = left;
                boundaryTo = right;
                boundarySource = "cl-extents";
            }

            var dimensions = boundariesProven
                ? sourceDimensions.Where(c =>
                        c.Offset >= boundaryFrom!.Value - boundaryToleranceM &&
                        c.Offset <= boundaryTo!.Value + boundaryToleranceM)
                    .ToList()
                : sourceDimensions.ToList();

            // ROW and plan-mark envelopes are real source marks. Re-adding the ROW
            // endpoint only protects its priority at a coincident mark; CL extents
            // are deliberately never inserted as synthetic dimension anchors.
            if (boundariesProven && boundarySource == "row")
            {
                AddBoundaryAnchor(boundaryFrom!.Value);
                AddBoundaryAnchor(boundaryTo!.Value);
                dimensions = dimensions
                    .OrderBy(c => c.Offset)
                    .ThenByDescending(c => Priority(c.Kind))
                    .GroupBy(c => c.Offset)
                    .Select(g => g.First())
                    .OrderBy(c => c.Offset)
                    .ToList();
            }

            void AddBoundaryAnchor(double offset)
            {
                if (dimensions.Any(c => Math.Abs(c.Offset - offset) <= boundaryToleranceM))
                    return;
                dimensions.Add(new PresentationMark(offset, "row", "זכות דרך", "row-boundary"));
            }
            var overrides = all
                .Where(c => c.Kind == "strip" && c.Label.Length > 0)
                .GroupBy(c => (Math.Round(c.Offset, 1, MidpointRounding.AwayFromZero), c.Label))
                .Select(g => new SpanLabelOverride(
                    g.First().Offset, g.Key.Label, "source-mark"))
                .Concat((approvedOverrides ?? Array.Empty<SpanLabelOverride>())
                    .Where(item => double.IsFinite(item.Offset) &&
                                   !string.IsNullOrWhiteSpace(item.Label) &&
                                   !string.IsNullOrWhiteSpace(item.Source))
                    .Select(item => item with
                    {
                        Label = item.Label.Trim(),
                        Source = item.Source.Trim(),
                        Evidence = item.Evidence?.Trim(),
                    }))
                .GroupBy(item => (
                     Math.Round(item.Offset, 3, MidpointRounding.AwayFromZero),
                     item.Label,
                     item.Source,
                     item.Evidence))
                .Select(group => group.First())
                .OrderBy(item => item.Offset)
                 .ThenBy(item => item.Label, StringComparer.Ordinal)
                 .ThenBy(item => item.Source, StringComparer.Ordinal)
                 .ThenBy(item => item.Evidence, StringComparer.Ordinal)
                .ToList();

            var widths = AdjacentWidths(
                dimensions.Select(c => c.Offset), StripNoiseFloorM, StripWidthCeilingM);
            // Name authority is separate from geometry/evidence. Keep every original
            // override in the canonical digest below, including superseded source names.
            var nameOverrides = SectionReviewedSpanLabelLogic.ForNameResolution(overrides, widths);
            // Natalie examples contain legitimate 1.30-1.40 m sidewalk/garden spans;
            // use the same 0.50 m noise floor as the dimension chain.
            var labels = StripLabels(
                dimensions.Select(c => (c.Offset, c.Kind)),
                nameOverrides.Select(item => (item.Offset, item.Label)).ToList(),
                minWidthM: StripNoiseFloorM, maxWidthM: StripWidthCeilingM);
            // SEC-M5 (review of 1.3.9): a strip bounded by a bus-lane (נת"צ) marking
            // line may be the bus lane itself. Naming it "נתיב נסיעה" from the lane
            // kind alone decided an engineering question for the engineer. Such a
            // strip is never auto-named; it goes to «הגדר שמות רצועות» unless an
            // approved/source override already names it.
            var busLaneEdgeSpans = new HashSet<(double From, double To)>();
            labels = labels.Where(label =>
            {
                if (nameOverrides.Any(item =>
                        item.Offset > label.From + 1e-9 && item.Offset < label.To - 1e-9))
                    return true;
                var leftMark = dimensions.Single(mark => mark.Offset == label.From);
                var rightMark = dimensions.Single(mark => mark.Offset == label.To);
                if (!IsBusLaneLineMark(leftMark.Kind, leftMark.Label) &&
                    !IsBusLaneLineMark(rightMark.Kind, rightMark.Label))
                    return true;
                // 30.09 (Arthur): the strip keeps its name — a review note, never a block (it may be the bus lane itself).
                busLaneEdgeSpans.Add((label.From, label.To));
                return true;
            }).ToList();
            var unresolved = widths.Where(width => labels.All(label =>
                    Math.Abs(label.From - width.From) > 1e-9 ||
                    Math.Abs(label.To - width.To) > 1e-9))
                .Select(width =>
                {
                    var left = dimensions.Single(mark =>
                        mark.Offset == width.From);
                    var right = dimensions.Single(mark =>
                        mark.Offset == width.To);
                    var labelsInside = nameOverrides.Where(item =>
                            item.Offset > width.From + 1e-9 &&
                            item.Offset < width.To - 1e-9)
                        .Select(item => item.Label)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    var reason = labelsInside.Count > 1
                        ? "conflicting-strip-label-evidence"
                        : busLaneEdgeSpans.Contains((width.From, width.To))
                            ? BusLaneLineEdgeReason
                            : "no-confident-strip-label";
                    return new UnresolvedSpan(
                        width.From, width.To, width.Width,
                        left.Kind, right.Kind, reason);
                })
                .ToList();
            var vehicles = labels.Count(s =>
            {
                var spec = SectionFurnitureLogic.VehicleForStrip(s.Label);
                return spec != null && SectionFurnitureLogic.FitsStrip(spec, s.To - s.From);
            });
            var officeCars = labels.Count(s =>
            {
                var spec = SectionFurnitureLogic.VehicleForStrip(s.Label);
                return spec?.Key == SectionFurnitureLogic.Car.Key &&
                       SectionFurnitureLogic.FitsStrip(spec, s.To - s.From);
            });

            var chainReachesBoundaries = boundariesProven && dimensions.Count >= 2 &&
                Math.Abs(dimensions[0].Offset - boundaryFrom!.Value) <= boundaryToleranceM &&
                Math.Abs(dimensions[^1].Offset - boundaryTo!.Value) <= boundaryToleranceM;
            // Every consecutive mark pair is a measured width, a narrow furniture gap
            // (two faces of one curb / island nose under the noise floor: covered, never
            // named) or an oversized hole. Only holes and a chain that misses a proven
            // boundary break continuity; every width must still end exactly on a mark.
            var gaps = dimensions.Zip(dimensions.Skip(1),
                (left, right) => right.Offset - left.Offset).ToList();
            var narrowGaps = gaps.Count(gap => gap < StripNoiseFloorM);
            var oversizedGaps = gaps.Count(gap => gap > StripWidthCeilingM);
            var continuousWidthChain = chainReachesBoundaries &&
                widths.Count > 0 && oversizedGaps == 0 &&
                widths.Count + narrowGaps == gaps.Count &&
                widths.All(width => dimensions.Any(mark =>
                    Math.Abs(mark.Offset - width.To) <= 1e-9));

            var boundaryFromText = boundaryFrom?.ToString(
                "F6", System.Globalization.CultureInfo.InvariantCulture) ?? "?";
            var boundaryToText = boundaryTo?.ToString(
                "F6", System.Globalization.CultureInfo.InvariantCulture) ?? "?";
            var sourceGroups = all.Where(source => WidthMarkKinds.Contains(source.Kind, StringComparer.Ordinal))
                .GroupBy(source => source.Offset).ToDictionary(group => group.Key, group => group.ToArray());
            var boundaries = dimensions.Select(mark => new DimensionBoundaryEvidence(
                mark.Offset, DimensionGeometryKey(mark.Offset),
                (sourceGroups.TryGetValue(mark.Offset, out var sources) ? sources : Array.Empty<PresentationMark>())
                    .Select(source => source.SourceEvidence ?? new DimensionSourceEvidence(
                        source.SourceIdentity, source.Kind, source.Label, string.Empty,
                        null, null, null, null, null, null, null))
                    .Distinct().OrderBy(source => source.Identity, StringComparer.Ordinal).ToArray()))
                .ToList();
            var canonical = string.Join("\n",
                new[]
                {
                    "dimension-contract|exact-crossings-v1",
                    $"boundary|{boundarySource}|{boundaryFromText}|{boundaryToText}|" +
                    $"proven={boundariesProven}|continuous={continuousWidthChain}",
                }
                    .Concat(all.Select(c => FormattableString.Invariant(
                        $"mark|{c.Offset:F6}|{c.Kind}|{c.Label}|{c.SourceIdentity}")))
                    .Concat(dimensions.Select(c => FormattableString.Invariant(
                        $"dimension|{c.Offset:F6}|{c.Kind}|{c.Label}|{c.SourceIdentity}")))
                    .Concat(boundaries.Select(boundary =>
                        $"anchor|{boundary.GeometryKey}|{DimensionSourcesCanonical(boundary.Sources)}"))
                    .Concat(widths.Select(w => FormattableString.Invariant(
                        $"width|{w.From:F6}|{w.To:F6}|{w.Width:F6}")))
                    .Concat(overrides.Select(item => FormattableString.Invariant(
                        $"override|{item.Offset:F6}|{item.Label}|{item.Source}|{item.Evidence}")))
                    .Concat(labels.Select(s => FormattableString.Invariant(
                        $"strip|{s.From:F6}|{s.To:F6}|{s.Label}"))));
            var summary = new PresentationCoverageSummary(
                all.Count, dimensions.Count, widths.Count, labels.Count,
                vehicles, officeCars, ArtifactHash.Sha256OfText(canonical))
            {
                ContinuousWidthChain = continuousWidthChain,
                NarrowGapCount = narrowGaps,
                OversizedGapCount = oversizedGaps,
                OuterBoundariesProven = boundariesProven,
                BoundarySource = boundarySource,
                BoundaryFrom = boundaryFrom,
                BoundaryTo = boundaryTo,
            };
            var result = new PresentationAnalysis { Summary = summary };
            result.DimensionMarks.AddRange(
                dimensions.Select(mark => (mark.Offset, mark.Kind, mark.Label)));
            result.DimensionBoundaries.AddRange(boundaries);
            result.WidthSpans.AddRange(widths);
            result.StripLabels.AddRange(labels);
            result.UnresolvedSpans.AddRange(unresolved);
            result.BusLaneEdgeStrips.AddRange(labels.Where(label => busLaneEdgeSpans.Contains((label.From, label.To))));
            return result;
        }

        /// <summary>
        /// Version stamp of the drawn section furniture. Participates in the plan
        /// fingerprint so a tool update that changes only LAYOUT still re-dresses
        /// sections whose engineering inputs did not change — otherwise 25 existing
        /// sections keep the old look forever and only new ones show the fix.
        /// </summary>
        // v16 (b15): labels of one semantic band sit on shared rows (Codex 262F6FEF, replay on the native 42676 capture).
        // v17 (b32): 1.5 text-height word gap between labels on one row and 0.45h between rows (Arthur 04.10: the
        // STA-42676 strip names and widths read as one run); existing sections re-plan as an update and are re-dressed.
        // v18 (b33): one text height between the strip-name, overall-width and axis groups (Codex 04.10 03:45).
        public const string AnnotationLayoutVersion = "layout-v18-header-group-gaps";

        /// <summary>
        /// Number of straight chords needed to approximate a circular arc while
        /// bounding the maximum sagitta in transformed drawing metres.  The host uses
        /// the returned count with Curve.GetPointAtParameter, so bulged LWPOLYLINE and
        /// Arc entities retain their curved path instead of being replaced by one
        /// endpoint chord.
        /// </summary>
        public static int ArcTessellationSegmentCount(
            double radiusM,
            double sweepRadians,
            double maxSagittaM = 0.005,
            int maxSegments = 8192)
        {
            if (!double.IsFinite(radiusM) || radiusM <= 0)
                throw new ArgumentOutOfRangeException(nameof(radiusM));
            if (!double.IsFinite(sweepRadians) || sweepRadians <= 0)
                throw new ArgumentOutOfRangeException(nameof(sweepRadians));
            if (!double.IsFinite(maxSagittaM) || maxSagittaM <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxSagittaM));
            if (maxSegments < 1)
                throw new ArgumentOutOfRangeException(nameof(maxSegments));

            var ratio = Math.Clamp(maxSagittaM / radiusM, 0.0, 2.0);
            var maxAngle = 2.0 * Math.Acos(Math.Clamp(1.0 - ratio, -1.0, 1.0));
            // Even a sub-tolerance circle needs enough chords to retain its shape.
            maxAngle = Math.Min(Math.PI / 2.0,
                !double.IsFinite(maxAngle) || maxAngle <= 1e-9 ? Math.PI / 2.0 : maxAngle);
            var requiredDouble = Math.Max(1.0, Math.Ceiling(sweepRadians / maxAngle));
            if (!double.IsFinite(requiredDouble) || requiredDouble > maxSegments)
                throw new InvalidOperationException(
                    $"Arc tessellation requires {requiredDouble:F0} segments; safety cap is {maxSegments}.");
            var required = (int)requiredDouble;
            return required;
        }

        public static double BulgeSweepRadians(double bulge)
        {
            if (!double.IsFinite(bulge) || Math.Abs(bulge) <= 1e-12)
                throw new ArgumentOutOfRangeException(nameof(bulge));
            return 4.0 * Math.Atan(bulge);
        }

        public static double BulgeRadius(double chordLength, double bulge)
        {
            if (!double.IsFinite(chordLength) || chordLength <= 0)
                throw new ArgumentOutOfRangeException(nameof(chordLength));
            if (!double.IsFinite(bulge) || Math.Abs(bulge) <= 1e-12)
                throw new ArgumentOutOfRangeException(nameof(bulge));
            return Math.Abs(chordLength * (1.0 + bulge * bulge) / (4.0 * bulge));
        }

        /// <summary>
        /// Names the strip between two adjacent plan marks by the KINDS of its bounds —
        /// the נתיב נסיעה/מדרכה/שביל אופניים row of a drafted section (חתך 1039).
        /// Deliberately conservative: a curb-to-curb strip may be a bus lane (נת"צ),
        /// parking or a median — those return null and the engineer names them.
        /// </summary>
        /// <summary>Unresolved-span reason: the strip touches a bus-lane (נת"צ) marking line.</summary>
        public const string BusLaneLineEdgeReason = "bus-lane-line-edge";

        /// <summary>
        /// A lane-kind plan mark whose rule label names a bus lane ("קו נת\"צ" from the
        /// 6422 profile rule *TR-MARK-YLW-3-3*). Label text only; no geometry guess.
        /// </summary>
        public static bool IsBusLaneLineMark(string? kind, string? label)
        {
            if (!string.Equals((kind ?? string.Empty).Trim(), "lane", StringComparison.OrdinalIgnoreCase))
                return false;
            var text = label ?? string.Empty;
            return text.Contains("נת\"צ", StringComparison.Ordinal) ||
                   text.Contains("נת״צ", StringComparison.Ordinal) ||
                   text.Contains("נתצ", StringComparison.Ordinal);
        }

        public static string? StripLabel(string leftKind, string rightKind, double widthM)
        {
            if (leftKind == "island" && rightKind == "island") return "אי תנועה";
            if (leftKind == "garden" && rightKind == "garden") return "גינון";
            if (leftKind == "parking" && rightKind == "parking") return "חניה";
            if (leftKind == "shoulder" && rightKind == "shoulder") return "שול";
            if (leftKind == "sidewalk" && rightKind == "sidewalk") return "מדרכה";
            if (leftKind == "bike" || rightKind == "bike") return "שביל אופניים";
            if ((leftKind == "sidewalk" && rightKind is "curb" or "island") ||
                (rightKind == "sidewalk" && leftKind is "curb" or "island")) return "מדרכה";
            if ((leftKind == "lane" || rightKind == "lane") && widthM >= 2.5 && widthM <= 6.5)
                return "נתיב נסיעה";
            return null;
        }

        public static List<(double From, double To, string Label)> StripLabels(
            IEnumerable<(double Offset, string Kind)> marks,
            double minWidthM = 2.0, double maxWidthM = 30.0) =>
            StripLabels(marks, null, minWidthM, maxWidthM);

        /// <summary>
        /// As above, plus OVERRIDE marks: a plan layer that names its strip outright
        /// (a נת"צ marking line, for example) drops an override at its offset, and
        /// the strip containing that offset takes the override's label — including a
        /// curb-to-curb strip the kind table deliberately refuses to guess.
        /// </summary>
        public static List<(double From, double To, string Label)> StripLabels(
            IEnumerable<(double Offset, string Kind)> marks,
            IReadOnlyList<(double Offset, string Label)>? overrides,
            double minWidthM = 2.0, double maxWidthM = 30.0)
        {
            var sorted = marks.OrderBy(m => m.Offset).ToList();
            var result = new List<(double, double, string)>();
            for (int i = 1; i < sorted.Count; i++)
            {
                var from = sorted[i - 1].Offset;
                var to = sorted[i].Offset;
                var width = to - from;
                if (width < minWidthM || width > maxWidthM) continue;

                var overrideLabels = overrides?
                    .Where(o => o.Offset > from + 1e-9 && o.Offset < to - 1e-9 &&
                                !string.IsNullOrWhiteSpace(o.Label))
                    .Select(o => o.Label.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToList() ?? new List<string>();
                // Competing source/manual labels are engineering ambiguity.  Never
                // pick the first item merely because traversal order happened to put
                // it first.
                var label = overrideLabels.Count switch
                {
                    1 => overrideLabels[0],
                    > 1 => null,
                    _ => StripLabel(sorted[i - 1].Kind, sorted[i].Kind, width),
                };
                if (label != null) result.Add((from, to, label));
            }
            return result;
        }

        /// <summary>
        /// Groups crossings that landed within <paramref name="mergeM"/> of each other
        /// on the same layer — a duct bank drawn as five parallel lines is one label,
        /// not five overlapping ones.
        /// </summary>
        public static List<Crossing> MergeNearby(IEnumerable<Crossing> crossings, double mergeM = 0.6)
        {
            var result = new List<Crossing>();
            foreach (var group in crossings.GroupBy(c => (
                         Layer: c.Layer.ToUpperInvariant(),
                         Xref: (c.Xref ?? string.Empty).ToUpperInvariant(),
                         Kind: c.Rule.Kind,
                         Label: c.Rule.Label,
                         c.Rule.ColorIndex)))
            {
                var cluster = new List<Crossing>();
                Crossing? previous = null;
                foreach (var c in group.OrderBy(c => c.Offset))
                {
                    if (previous != null && Math.Abs(c.Offset - previous.Offset) >= mergeM)
                    {
                        result.Add(Choose(cluster));
                        cluster.Clear();
                    }
                    cluster.Add(c);
                    previous = c;
                }
                if (cluster.Count > 0) result.Add(Choose(cluster));
            }
            return result.OrderBy(c => c.Offset).ToList();

            // Choice is independent of CL direction: APPLY may receive Civil's left
            // side opposite to PLAN's drawn-line direction. Prefer real elevation,
            // then stable source/WCS identity instead of whichever crossing sorted first.
            static Crossing Choose(List<Crossing> cluster) => cluster
                .OrderByDescending(c => c.Elevation.HasValue)
                .ThenBy(c => c.SourceHandle ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.WcsX ?? double.MaxValue)
                .ThenBy(c => c.WcsY ?? double.MaxValue)
                .First();
        }

        /// <summary>Stable identity of one promised projected source crossing.</summary>
        public static string ProjectionEvidenceKey(Crossing crossing)
        {
            var x = crossing.WcsX ?? crossing.Offset;
            var y = crossing.WcsY ?? 0.0;
            var canonical = string.Join("|",
                crossing.Xref ?? "HOST",
                crossing.Layer,
                crossing.SourceHandle ?? "?",
                Math.Round(x, 3).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                Math.Round(y, 3).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                crossing.Elevation is { } z
                    ? Math.Round(z, 3).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    : "NO-Z",
                crossing.Rule.Kind,
                crossing.Rule.Label,
                crossing.Rule.ColorIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return "PRJ:" + ArtifactHash.Short(ArtifactHash.Sha256OfText(canonical));
        }

        /// <summary>
        /// Host-neutral description of one annotation entity drawn for a promised
        /// projection. APPLY stores its fingerprint and VERIFY recomputes it from the
        /// live Line/Circle/DBText, so an existing-but-moved or relabelled handle is
        /// not accepted as evidence.
        /// </summary>
        public sealed record ProjectionAnnotationSemantic(
            string EntityKind,
            IReadOnlyList<double> Geometry,
            short ColorIndex,
            int? TrueColorArgb,
            string? ColorMethod,
            string? Text,
            string? LineType,
            string? Layer);

        public static string ProjectionAnnotationFingerprint(ProjectionAnnotationSemantic semantic)
        {
            static string Field(string? value) => value == null ? "-1:" : $"{value.Length}:{value}";
            static string Number(double value)
            {
                var rounded = Math.Round(value, 6);
                if (Math.Abs(rounded) < 0.0000005) rounded = 0;
                return rounded.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            }

            var canonical = string.Join("|",
                Field(semantic.EntityKind),
                string.Join(",", semantic.Geometry.Select(Number)),
                semantic.ColorIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                semantic.TrueColorArgb?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NO-RGB",
                Field(semantic.ColorMethod), Field(semantic.Text), Field(semantic.LineType), Field(semantic.Layer));
            return ArtifactHash.Sha256OfText(canonical);
        }

        public sealed record ProjectionEvidenceComparison(
            IReadOnlyList<string> Missing,
            IReadOnlyList<string> Unexpected,
            IReadOnlyList<string> Unproven)
        {
            public bool IsExact => Missing.Count == 0 && Unexpected.Count == 0 && Unproven.Count == 0;
        }

        /// <summary>Exact planned keys versus live registry handle counts.</summary>
        public static ProjectionEvidenceComparison CompareProjectionEvidence(
            IEnumerable<string> expectedKeys,
            IReadOnlyDictionary<string, int> actualLiveHandleCounts)
        {
            var expected = new HashSet<string>(expectedKeys, StringComparer.Ordinal);
            var actual = new HashSet<string>(actualLiveHandleCounts.Keys, StringComparer.Ordinal);
            return new ProjectionEvidenceComparison(
                expected.Except(actual).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                actual.Except(expected).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                expected.Where(k => actualLiveHandleCounts.TryGetValue(k, out var n) && n <= 0)
                    .OrderBy(x => x, StringComparer.Ordinal).ToList());
        }

        /// <summary>
        /// One Hebrew sentence for the panel's systems column, honest about the source:
        /// sampled Civil objects and projected XREF linework are different facts.
        /// </summary>
        public static UtilityProjectionScanState ResolveProjectionScanState(
            bool projectionEnabled, bool scanBlocked) =>
            !projectionEnabled
                ? UtilityProjectionScanState.Disabled
                : scanBlocked
                    ? UtilityProjectionScanState.Blocked
                    : UtilityProjectionScanState.Complete;

        public static string SystemsSummary(
            IReadOnlyList<string> sampledUtilities,
            IReadOnlyList<string> projectedLabels,
            UtilityProjectionScanState projectionScanState,
            int drawingProjectionEntityCount,
            int sectionProjectionCrossingCount)
        {
            var projected = projectedLabels.Distinct().ToList();
            // The column is already titled "מערכות", so the cell carries only the list.
            // The old prefix "מוקרנות:" sat next to the מקורות (Mekorot) water label and
            // read as a typo of it (engineer feedback, 30/08).
            var parts = new List<string>();
            if (projected.Count > 0)
                parts.Add(string.Join(", ", projected));
            if (sampledUtilities.Count > 0)
                parts.Add("נדגמות: " + string.Join(", ", sampledUtilities));

            if (parts.Count > 0)
            {
                if (projectionScanState == UtilityProjectionScanState.Blocked)
                    parts.Add("סריקת XREF חסומה — הכיסוי חלקי");
                else if (projectionScanState == UtilityProjectionScanState.Disabled)
                    parts.Add("מערכות XREF לא נסרקו");
                else if (projectionScanState == UtilityProjectionScanState.NotRun)
                    parts.Add("בדיקת מערכות טרם הושלמה");
                else if (projected.Count == 0 && sectionProjectionCrossingCount > 0)
                    parts.Add("נמצאו חציות מערכת XREF ללא תווית — דרושה בדיקה");
                else if (projected.Count == 0 && drawingProjectionEntityCount > 0)
                    parts.Add("לא נמצאה חציית מערכת XREF בחתך זה");
                else if (projected.Count == 0)
                    parts.Add("לא נמצאו מערכות XREF בשרטוט");
                return string.Join(" · ", parts);
            }

            return projectionScanState switch
            {
                UtilityProjectionScanState.Blocked =>
                    "סריקת מערכות XREF חסומה — לא ניתן לקבוע חציות",
                UtilityProjectionScanState.Disabled => "מערכות XREF לא נסרקו",
                UtilityProjectionScanState.NotRun => "בדיקת מערכות טרם הושלמה",
                _ when sectionProjectionCrossingCount > 0 =>
                    "נמצאו חציות מערכת בחתך ללא תווית — דרושה בדיקה",
                _ when drawingProjectionEntityCount > 0 =>
                    "לא נמצאה חציית מערכת בחתך זה",
                _ => "לא נמצאו מערכות בשרטוט",
            };
        }
    }
}
