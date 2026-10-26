using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// A catalog code SUGGESTED for a discovered quantity group. It is evidence, not
    /// a decision: nothing downstream reads a proposal, and only
    /// <c>SaveApprovedMappings</c> (which re-validates code + unit) can turn one into
    /// a rule. This exists so the engineer starts from ranked candidates instead of
    /// searching 8,615 catalog rows by hand.
    /// </summary>
    public sealed class MappingProposal
    {
        [JsonPropertyName("rule_key")]
        public required string RuleKey { get; init; }

        [JsonPropertyName("layer")]
        public string? Layer { get; init; }

        [JsonPropertyName("measurement_kind")]
        public required string MeasurementKind { get; init; }

        [JsonPropertyName("measured_unit")]
        public required string MeasuredUnit { get; init; }

        [JsonPropertyName("object_count")]
        public int ObjectCount { get; init; }

        [JsonPropertyName("total_quantity")]
        public double TotalQuantity { get; init; }

        [JsonPropertyName("proposed_code")]
        public required string ProposedCode { get; init; }

        [JsonPropertyName("catalog_description")]
        public string? CatalogDescription { get; init; }

        [JsonPropertyName("catalog_unit")]
        public string? CatalogUnit { get; init; }

        [JsonPropertyName("score")]
        public int Score { get; set; }

        [JsonPropertyName("reasons")]
        public List<string> Reasons { get; init; } = new();

        /// <summary>
        /// Machine-readable provenance for review/batch gates.  It never changes the
        /// proposal's unapproved status and is not authority to price a line.
        /// </summary>
        [JsonPropertyName("evidence_kind")]
        public string EvidenceKind { get; init; } = "heuristic";

        /// <summary>Always PROPOSED_UNAPPROVED — a proposal never becomes fact by itself.</summary>
        [JsonPropertyName("status")]
        public string Status => "PROPOSED_UNAPPROVED";
    }

    /// <summary>
    /// Ranks catalog candidates for discovered quantity groups.
    ///
    /// Hard rules kept from the locked plan: a proposal whose catalog unit differs
    /// from the measured unit is never produced (that would be a silent conversion),
    /// and the engine never writes to the profile. Reference codes seen in a
    /// comparable delivered estimate are a ranking signal only.
    /// </summary>
    public static class MappingProposalEngine
    {
        /// <summary>How many suggestions to return per discovered group.</summary>
        public const int MaxProposalsPerGroup = 3;

        /// <summary>
        /// Hebrew has no spaces inside words, so "מים" happily matches inside
        /// "מקומיים" — which is exactly how a water layer was offered galvanised
        /// threaded rods (live, 31/08). A keyword hit requires word boundaries.
        /// </summary>
        internal static bool ContainsWord(string text, string keyword)
        {
            var idx = 0;
            while ((idx = text.IndexOf(keyword, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var beforeOk = idx == 0 || !IsHebrewOrLatin(text[idx - 1]);
                var end = idx + keyword.Length;
                var afterOk = end >= text.Length || !IsHebrewOrLatin(text[end]);
                if (beforeOk && afterOk) return true;
                idx = end;
            }
            return false;

            static bool IsHebrewOrLatin(char c) =>
                (c >= 'א' && c <= 'ת') || char.IsAsciiLetter(c);
        }

        /// <summary>Wording that prices removal/maintenance of something that exists.</summary>
        private static readonly string[] DemolitionWords =
            { "פירוק", "פרוק", "סתימת", "ניקוי", "העתק", "התאמת גובה", "אחזק", "שיקום", "תיקון" };

        private static bool LayerImpliesRemoval(string? layer)
        {
            var l = layer ?? "";
            return l.Contains("EXST", StringComparison.OrdinalIgnoreCase) ||
                   l.Contains("EXIST", StringComparison.OrdinalIgnoreCase) ||
                   l.EndsWith("-EX", StringComparison.OrdinalIgnoreCase) ||
                   l.Contains("-EX-", StringComparison.OrdinalIgnoreCase) ||
                   l.Contains("DEMO", StringComparison.OrdinalIgnoreCase) ||
                   l.Contains("PIRUK", StringComparison.OrdinalIgnoreCase) ||
                   l.Contains("KAYAM", StringComparison.OrdinalIgnoreCase) ||
                   l.Contains("קיים", StringComparison.Ordinal);
        }

        /// <summary>Catalog phrases a token must never buy, even on a genuine word hit.</summary>
        private static readonly Dictionary<string, string[]> NegativeHints = new(StringComparer.OrdinalIgnoreCase)
        {
            // A bike LANE may legitimately be represented by its dedicated edge
            // kerb item (U51.06.2930).  The live regression was bicycle PARKING
            // furniture, not kerbs in general, so keep the guard subject-specific.
            ["BIKE-LANE"] = new[] { "מתקן חנייה", "מתקני חנייה" },
            ["BIKE"] = new[] { "מתקן חנייה", "מתקני חנייה" },
            ["LIGHT"] = new[] { "רמזור" },
        };

        /// <summary>
        /// Hard semantic guard shared by heuristic and curated/profile proposals.
        /// A legacy default rule is useful evidence, but it may not bypass the exact
        /// regressions that the heuristic route rejects.
        /// </summary>
        public static bool IsSemanticallyCompatible(string? layer, string description,
            string? additionalElectricalEvidence = null)
        {
            // Supplement, never replace, the actual source name. Explicit engineer
            // context may establish a signal role but cannot erase a source HV/LV conflict.
            if (!ElectricalProposalCompatibility.IsCompatible(
                string.Join("\n", new[] { layer, additionalElectricalEvidence }.Where(value => !string.IsNullOrWhiteSpace(value))),
                description)) return false;
            var layerImpliesRemoval = LayerImpliesRemoval(layer);
            var itemIsRemovalOrMaintenance = DemolitionWords.Any(w =>
                description.Contains(w, StringComparison.Ordinal));
            if (!layerImpliesRemoval && itemIsRemovalOrMaintenance)
                return false;
            if (layerImpliesRemoval && !itemIsRemovalOrMaintenance)
                return false;

            // Actual full 6422 scan: TR-MARK-WHT-812-BIKE was offered the
            // cycleway KERB by the legacy *BIKE* profile rule (ahead of genuine
            // marking candidates). A marking's bike modifier does not change its
            // subject into an edge. Apply this to curated proposals too, and read
            // source leaves independently so an XREF name cannot invent the role.
            var isRoadMarking = HasRoadMarkingSubject(layer);
            // U51.32.0700 genuinely prices painting kerbs, not supplying them.
            // Keep that positive work subject; incidental "כולל סימון/צביעת"
            // later in a physical-kerb item must not bypass the guard.
            var isKerbPainting = description.TrimStart().StartsWith("צביעת אבני שפה", StringComparison.Ordinal) ||
                description.TrimStart().StartsWith("צביעת אבן שפה", StringComparison.Ordinal);
            if (isRoadMarking && !isKerbPainting &&
                (description.Contains("אבן שפה", StringComparison.Ordinal) ||
                 description.Contains("אבני שפה", StringComparison.Ordinal)))
                return false;

            // Native July-2026 catalog regression: "סימון" bought cable markers /
            // HDPE pipes, and "כולל אבן שפה" bought a slotted drain. Match the work's
            // primary subject, not an incidental component later in the wording.
            // These guards also apply to legacy/profile and family candidates.
            if (isRoadMarking && !IsRoadMarkingCatalogSubject(description, layerImpliesRemoval))
                return false;
            if (HasKerbSubject(layer) && !isRoadMarking && !isKerbPainting &&
                !IsKerbCatalogSubject(description, layer, layerImpliesRemoval))
                return false;

            // Recovered SM drawing: TR-MARK-ARW-BL / arrow-D is road-arrow
            // marking evidence, not an electrical marker post or cable manhole.
            // A count of INSERTs still cannot become the paint catalog's m2.
            if ((layer ?? string.Empty).Contains("TR-MARK-ARW", StringComparison.OrdinalIgnoreCase) &&
                !description.Contains("צביעת שטחים", StringComparison.Ordinal) &&
                !description.Contains("סימון חיצ", StringComparison.Ordinal) &&
                !description.Contains("סימון חץ", StringComparison.Ordinal))
                return false;

            var subjectBlocked = SubjectRequirements.Any(sr =>
                (layer ?? string.Empty).Contains(sr.Key, StringComparison.OrdinalIgnoreCase) &&
                !sr.Value.Any(w => description.Contains(w, StringComparison.Ordinal)));
            if (subjectBlocked) return false;

            return !NegativeHints.Any(h =>
                (layer ?? string.Empty).Contains(h.Key, StringComparison.OrdinalIgnoreCase) &&
                h.Value.Any(bad => description.Contains(bad, StringComparison.Ordinal)));
        }

        private static IEnumerable<string> SubjectLeaves(string? context) =>
            (context ?? string.Empty).Split('\n').Select(SectionProjectionLogic.LayerLeaf);

        // 1.4.1 (984, 06.10; Arthur: "SIMUN אומר שזה סימון כבישים"): the Mahod layer word SIMUN is road marking.
        // Live b34: 2519-simun818 was offered 08.10.0121 (cable warning plates) because "סימון" matched an
        // electrical item; as a road-marking subject the catalog guard below now keeps it to marking work.
        private static readonly string[] RoadMarkingWords =
        {
            "ROAD-MARKING", "ROAD MARKING", "SIMUN", "SIMUNE", "SIMUNIM",
            "סימון כביש", "סימוני כביש", "סימון כבישים", "סימוני כבישים", "סימון דרך", "סימוני דרך",
        };

        private static bool HasRoadMarkingSubject(string? context) => SubjectLeaves(context).Any(name =>
            name.Equals("TR-MARK", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("TR-MARK-", StringComparison.OrdinalIgnoreCase) ||
            RoadMarkingWords.Any(word => ContainsWord(name, word)));

        private static bool HasKerbSubject(string? context) => SubjectLeaves(context).Any(name =>
            new[] { "CURB", "KERB", "SHAPA", "אבן שפה", "אבני שפה" }.Any(word => ContainsWord(name, word)));

        private static string PrimaryCatalogSubject(string description, bool removal) => Regex.Replace(
            description.TrimStart(), removal
                ? @"^(?:(?:אספקה|אספקת|והתקנה|והתקנת|התקנה|התקנת|הנחת|בניית|של|פירוק(?:\s+זהיר)?|פרוק(?:\s+זהיר)?|והובלת|הובלת)\s+){0,6}"
                : @"^(?:(?:אספקה|אספקת|והתקנה|והתקנת|התקנה|התקנת|הנחת|בניית|של)\s+){0,6}", "");

        private static bool StartsWithCatalogSubject(string description, string subject) =>
            description.StartsWith(subject, StringComparison.Ordinal) &&
            ContainsWord(description[..Math.Min(description.Length, subject.Length + 1)], subject);

        private static bool IsRoadMarkingCatalogSubject(string description, bool removal)
        {
            var primary = PrimaryCatalogSubject(description, removal);
            // A road-marking number may precede the subject (e.g. תמרור 804 -סימון).
            // The number is not inferred from arbitrary layer suffixes.
            primary = Regex.Replace(primary, @"^תמרור\s+\d+(?:\s*ו[-־]?\d+)?\s*[-־–:]\s*", "");
            if (removal)
                primary = Regex.Replace(primary, @"^(?:הסרה|מחיקה|הסתרה|העלמה)(?:\s+(?:מכנית|ארעית|זמנית))?\s+(?:של\s*)?", "");
            return new[] { "קו ניתוב", "קוניתוב", "קן ניתוב", "צביעת שטחים", "סימון שטחים",
                    "סימון כביש", "סימון דרך", "סימון שביל", "סימון אופניים", "סימון סמל אופניים",
                    "סימון חץ", "סימון חיצים", "צביעת אבני שפה", "צביעת אבן שפה" }
                .Any(subject => StartsWithCatalogSubject(primary, subject));
        }

        private static bool IsKerbCatalogSubject(string description, string? context, bool removal)
        {
            var primary = PrimaryCatalogSubject(description, removal);
            if (new[] { "אבן שפה", "אבני שפה" }.Any(subject => StartsWithCatalogSubject(primary, subject)))
                return true;
            // A real combined kerb-drain remains discoverable only when the object
            // itself also identifies drainage. An XREF's name cannot provide it.
            return SubjectLeaves(context).Any(name => new[] { "DRAIN", "NIKUZ", "נקז", "ניקוז" }
                    .Any(word => ContainsWord(name, word))) &&
                new[] { "נקז", "תעלת ניקוז" }.Any(subject => StartsWithCatalogSubject(primary, subject));
        }

        /// <summary>
        /// A layer whose name contains the KEY may only be offered items whose
        /// wording contains one of the SUBJECT words. "אטמי מים" reached a water
        /// PIPE layer because "מים" is genuinely a whole word there — the subject
        /// (seal vs pipe) was wrong, not the match (live, 31/08). When no catalog
        /// item passes, the row honestly stays "דרוש מיפוי".
        /// </summary>
        private static readonly Dictionary<string, string[]> SubjectRequirements = new(StringComparer.OrdinalIgnoreCase)
        {
            // TR-SIGN-STAG-BL / BIKE in the recovered SM file is a symbol role.
            // A bicycle rack mentioning its incidental sign is not a road sign.
            ["TR-SIGN"] = new[] { "תמרור" },
            // The 6422 SM drawings use HW-CURB-ILND.  It also matches the legacy
            // broad HW-CURB* default, so require explicit island wording before a
            // profile rule may outrank the heuristic candidates.
            ["CURB-ILND"] = new[] { "אי תנועה", "עטרה" },
            ["TR-ISLAND"] = new[] { "אי תנועה", "עטרה" },
            // GRDN is a proven 6422 layer suffix. A broad HW-CURB* profile
            // default must not present a road kerb as the smart answer for a
            // landscape/garden edge; no matching catalog wording means UNMAPPED.
            ["CURB-GRDN"] = new[] { "גן", "גינון" },
            ["CURB-GARDEN"] = new[] { "גן", "גינון" },
            ["BIKE-LANE"] = new[] { "אופניים" },
            ["CURB"] = new[] { "שפה" },
            ["KERB"] = new[] { "שפה" },
            ["MAIM"] = new[] { "צינור", "צינורות", "קו מים", "קווי מים", "שוחה", "שוחות", "מגוף", "הנחת" },
            ["WATER"] = new[] { "צינור", "צינורות", "קו מים", "קווי מים", "שוחה", "שוחות", "מגוף", "הנחת" },
            ["MEKOROT"] = new[] { "צינור", "צינורות", "קו מים", "קווי מים", "שוחה", "שוחות", "מגוף", "הנחת" },
            ["BIUV"] = new[] { "צינור", "צינורות", "קו ביוב", "קווי ביוב", "שוחה", "שוחות", "הנחת" },
        };

        /// <summary>
        /// Hebrew/engineering keywords that connect a drawing layer to catalog wording.
        /// Deliberately small and explicit: a guessy synonym table would manufacture
        /// confidence the evidence does not support.
        /// </summary>
        private static readonly Dictionary<string, string[]> LayerKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            // Both construct forms appear in the catalog: "אבן שפה" and "אבני שפה".
            ["KERB"] = new[] { "אבן שפה", "אבני שפה" },
            ["CURB"] = new[] { "אבן שפה", "אבני שפה" },
            ["SHAPA"] = new[] { "אבן שפה", "אבני שפה" },
            ["ASPHALT"] = new[] { "אספלט", "תא\"צ", "מסעה" },
            ["ASF"] = new[] { "אספלט", "תא\"צ" },
            ["MISA"] = new[] { "מסעה" },
            ["ROAD"] = new[] { "מסעה", "אספלט" },
            ["MILL"] = new[] { "קרצוף" },
            ["KIRTZUF"] = new[] { "קרצוף" },
            ["SIDEWALK"] = new[] { "מדרכ" },
            ["MIDRACHA"] = new[] { "מדרכ" },
            ["RITZUF"] = new[] { "ריצוף", "מרוצפ" },
            ["PAVE"] = new[] { "ריצוף", "מרוצפ" },
            ["PAVING_UNIT"] = new[] { "יחידת ריצוף", "יחידות ריצוף" },
            ["MARK"] = new[] { "סימון", "צביעת" },
            ["SIMUN"] = new[] { "סימון", "צביעת" },
            ["SIGN"] = new[] { "תמרור", "שלט" },
            ["TAMROR"] = new[] { "תמרור" },
            ["BASE"] = new[] { "מצע" },
            ["MATZA"] = new[] { "מצע" },
            ["EXCAV"] = new[] { "חפירה", "עבודות עפר" },
            ["FILL"] = new[] { "מילוי" },
            ["DEMO"] = new[] { "פירוק" },
            ["PIRUK"] = new[] { "פירוק" },
            ["SHELTER"] = new[] { "סככה", "תחנת אוטובוס" },
            ["SOCHECHA"] = new[] { "סככה" },
            // Never the bare "תחנת": it also buys "תחנת גלאי מהירות", a pumping or a transformer station (live: an
            // object whose own property said "505 BUS stop" was offered a speed-detector station). "אוטובוס" alone
            // keeps sign, shelter and full-stop items equal: the evidence does not say which of them the object is.
            ["BUS"] = new[] { "אוטובוס" },
            ["TREE"] = new[] { "עץ", "נטיע" },
            ["LIGHT"] = new[] { "תאורה", "עמוד" },
            ["POLE"] = new[] { "עמוד" },
            ["CABINET"] = new[] { "ארון", "ארונות" },
            // Structures and furniture.
            // Specific phrases first: a phrase hit outranks a bare-word hit (see scoring).
            ["WALL"] = new[] { "קיר תומך", "קיר כובד", "קיר" },
            ["KIR"] = new[] { "קיר תומך", "קיר כובד", "קיר" },
            ["RETAIN"] = new[] { "קיר תומך", "קיר כובד" },
            ["FENCE"] = new[] { "גדר" },
            ["GADER"] = new[] { "גדר" },
            ["GUARDRAIL"] = new[] { "מעקה" },
            ["GUARD"] = new[] { "מעקה" },
            ["RAIL"] = new[] { "מעקה" },
            ["BARRIER"] = new[] { "מעקה", "מחסום" },
            ["BOLLARD"] = new[] { "עמוד חסימה", "עמודון" },
            ["BENCH"] = new[] { "ספסל" },
            ["STAIR"] = new[] { "מדרגות" },
            ["RAMP"] = new[] { "רמפה", "כבש" },
            ["ISLAND"] = new[] { "אי תנועה" },
            ["CROSS"] = new[] { "מעבר חצי" },
            ["ZEBRA"] = new[] { "מעבר חצי" },
            ["BIKE"] = new[] { "אופניים" },
            ["SHOULDER"] = new[] { "שול" },
            // Earthworks, materials, landscape.
            ["CONC"] = new[] { "בטון" },
            ["BETON"] = new[] { "בטון" },
            ["GRAVEL"] = new[] { "מצע" },
            ["AGG"] = new[] { "מצע" },
            ["TOPSOIL"] = new[] { "אדמת גן" },
            ["GRASS"] = new[] { "דשא", "גינון" },
            ["GARDEN"] = new[] { "גינון" },
            ["GINUN"] = new[] { "גינון" },
            ["LANDSCAPE"] = new[] { "גינון" },
            ["GEOTEX"] = new[] { "יריעה" },
            // Wet and dry utilities - the 6422 model names its layers this way
            // (MAIM, BIUV315, HASHMAL, BEZEQ, NIKUZ...).
            ["MAIM"] = new[] { "מים" },
            ["WATER"] = new[] { "מים" },
            ["BIUV"] = new[] { "ביוב" },
            ["SEWER"] = new[] { "ביוב" },
            ["NIKUZ"] = new[] { "ניקוז" },
            ["DRAIN"] = new[] { "ניקוז" },
            ["STORM"] = new[] { "ניקוז" },
            ["INLET"] = new[] { "קולטן" },
            ["KOLTAN"] = new[] { "קולטן" },
            ["CULVERT"] = new[] { "מעביר" },
            ["MAAVIR"] = new[] { "מעביר" },
            ["CHANNEL"] = new[] { "תעלה" },
            ["TAALA"] = new[] { "תעלה" },
            ["HASHMAL"] = new[] { "חשמל" },
            ["ELEC"] = new[] { "חשמל" },
            ["ELECTRIC"] = new[] { "חשמל" },
            ["ELECTRICITY"] = new[] { "חשמל" },
            ["BEZEQ"] = new[] { "תקשורת", "בזק" },
            ["BEZEK"] = new[] { "תקשורת", "בזק" },
            ["TELECOM"] = new[] { "תקשורת" },
            ["COMM"] = new[] { "תקשורת" },
            ["TIKSHORET"] = new[] { "תקשורת" },
            ["GAS"] = new[] { "גז" },
            ["GAZ"] = new[] { "גז" },
            ["PIPE"] = new[] { "צינור" },
            ["TZINOR"] = new[] { "צינור" },
            ["MANHOLE"] = new[] { "שוחה", "תא בקרה" },
            ["SHUHA"] = new[] { "שוחה" },
            ["SHOHA"] = new[] { "שוחה" },
            // "Existing" markers: on an early-design BRT corridor an existing element
            // is as likely to be demolished as kept, so demolition wording ranks too.
            ["EXST"] = new[] { "פירוק", "קיים" },
            ["EXIST"] = new[] { "פירוק", "קיים" },
            ["-EX"] = new[] { "פירוק", "קיים" },
            ["KAYAM"] = new[] { "פירוק", "קיים" },
        };

        /// <summary>Words that describe the state of an element, not the element itself.</summary>
        private static readonly HashSet<string> ModifierWords = new(StringComparer.OrdinalIgnoreCase) { "פירוק", "קיים" };

        /// <summary>
        /// A raw CAD identifier is subject evidence only when it contains known
        /// engineering vocabulary, not merely an alphabetic token. Reuse the
        /// proposal vocabulary without its permissive substring matching or its
        /// fallback that accepts arbitrary Hebrew words. State-only tokens do not
        /// identify a subject. Numeric suffixes (BIUV315) remain valid boundaries.
        /// This is a retrieval gate, never an engineering approval.
        /// </summary>
        internal static bool HasKnownSubjectVocabulary(string? text) =>
            !string.IsNullOrWhiteSpace(text) &&
            (LayerKeywords.Any(entry => entry.Value.Any(word => !ModifierWords.Contains(word)) &&
                (ContainsWord(text, entry.Key) || entry.Value.Any(word =>
                    !ModifierWords.Contains(word) && ContainsWord(text, word)))) ||
             SemanticSubjectAliases.Any(word => ContainsWord(text, word)));

        // Full subject nouns for the legacy stem hints above, plus the English
        // description used by independently named edge blocks. Unknown vocabulary
        // remains available through an explicit engineer description instead.
        private static readonly string[] SemanticSubjectAliases =
            { "edging", "pavement", "מדרכה", "מדרכות", "נטיעה", "נטיעות", "עצים", "שפה", "שפות" };

        /// <param name="RecognitionEvidence">
        /// Readable object-bound CAD evidence and the local recognition outcome for the group
        /// (<see cref="Recognition.CatalogEvidenceBridge.For"/>). Search terms and a stop signal only: never approval.
        /// </param>
        public sealed record DiscoveredGroup(
            string RuleKey,
            string? Layer,
            string MeasurementKind,
            string MeasuredUnit,
            int ObjectCount,
            double TotalQuantity,
            IReadOnlyList<QuantityCadMetadataPolicy.FieldSummary>? CadMetadata = null,
            Recognition.CatalogEvidenceBridge.Evidence? RecognitionEvidence = null);

        private sealed record MetadataSubject(string Key, string Value, IReadOnlyList<string> Keywords, string? Citation = null)
        {
            public bool FromEvidence => Citation != null;
        }

        /// <summary>
        /// Why no catalog item is proposed automatically for <paramref name="group"/> because its own CAD evidence
        /// contradicts itself; null when the evidence does not. Shown to the engineer: the group is still measured and
        /// can be mapped manually.
        /// </summary>
        public static string? EvidenceRefusal(DiscoveredGroup group) =>
            group.RecognitionEvidence is { Contradicted: true } evidence
                ? "ראיות ה-CAD של הקבוצה סותרות זו את זו — לא הוצעו סעיפים אוטומטית, גם אם שם בלוק או שכבה מתאימים. " +
                  string.Join(" ", evidence.Contradictions) + " ניתן לבדוק ולשייך ידנית; המדידות לא הוחרגו."
                : null;

        // Object-bound evidence subjects (identical on every record) as search subjects with their citation.
        private static IReadOnlyList<MetadataSubject> EvidenceSubjects(DiscoveredGroup group) =>
            (group.RecognitionEvidence?.Subjects ?? Array.Empty<Recognition.CatalogEvidenceBridge.Subject>())
                .Select(subject => new MetadataSubject(subject.Key, subject.Value,
                    KeywordsFor(SectionProjectionLogic.LayerLeaf(subject.Value)), subject.Citation))
                .Where(subject => subject.Keywords.Any(keyword => !ModifierWords.Contains(keyword)))
                .ToList();

        private static readonly HashSet<string> SubjectMetadataKeys = new(StringComparer.Ordinal)
        {
            "cad_block_name_effective", "cad_block_name_raw",
            "cad_entity_linetype", "cad_layer_linetype",
        };

        private static IReadOnlyList<MetadataSubject> MetadataSubjects(
            DiscoveredGroup group, out bool hasUncertainMetadata)
        {
            var fields = (group.CadMetadata ?? Array.Empty<QuantityCadMetadataPolicy.FieldSummary>())
                .Where(field => SubjectMetadataKeys.Contains(field.Key)).ToList();
            // A dynamic block's raw anonymous name is not an alternative subject.
            // If effective-name coverage is incomplete, do not borrow a raw name.
            if (fields.Any(field => field.Key == "cad_block_name_effective"))
                fields.RemoveAll(field => field.Key == "cad_block_name_raw");
            hasUncertainMetadata = fields.Any(field => field.IsMixed ||
                field.RecordCount != group.ObjectCount) ||
                fields.GroupBy(field => field.Key, StringComparer.Ordinal).Any(grouping => grouping.Count() != 1);
            if (hasUncertainMetadata) return Array.Empty<MetadataSubject>();

            return fields.Select(field => new MetadataSubject(
                    field.Key, field.Values[0], KeywordsFor(SectionProjectionLogic.LayerLeaf(field.Values[0]))))
                .Where(subject => subject.Keywords.Any(keyword => !ModifierWords.Contains(keyword)))
                .ToList();
        }

        // Only explicit role-prefixed identifiers are ranking evidence. A color
        // index, width or arbitrary drawing number is never a sign/marking code.
        private static readonly Regex MetadataRoleNumber = new(
            @"(?:^|[^A-Z0-9])(?:SIGN|TAMROR|MARK(?:ING)?|SIMUN|תמרור|סימון)[_\s-]*(?<number>[0-9]{2,4})(?![0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Proposal generation is for plausible construction quantities only. Station
        /// geometry, helper/system layers, existing survey utilities and implausible
        /// magnitudes remain visible for explicit review, but an automatic catalog
        /// suggestion would turn drawing furniture into apparent procurement evidence.
        /// </summary>
        public static bool IsProposalEligible(DiscoveredGroup group) =>
            QuantitySignificance.Classify(new QuantitySignificance.Group(
                group.RuleKey, group.Layer, group.MeasuredUnit,
                group.TotalQuantity, group.ObjectCount)).IsLikelyQuantity &&
            !QuantitySignificance.RequiresExplicitSurveyMarkerSubjectReview(new QuantitySignificance.Group(
                group.RuleKey, group.Layer, group.MeasuredUnit, group.TotalQuantity, group.ObjectCount));

        // These are existing retrieval subjects with a countable asset identity,
        // not material/state/location words. This list does not choose a catalog
        // specification or establish that a survey symbol is construction work.
        private static readonly string[] CountedAssetRoles =
        {
            "KERB", "CURB", "SHAPA", "SIGN", "TAMROR", "SHELTER", "SOCHECHA", "TREE",
            "LIGHT", "POLE", "CABINET", "BOLLARD", "BENCH", "INLET", "KOLTAN",
            "MANHOLE", "SHUHA", "SHOHA", "PIPE", "TZINOR", "PAVING_UNIT",
        };
        private static readonly string[] LinearAssetRoles =
        {
            "KERB", "CURB", "SHAPA", "FENCE", "GADER", "GUARDRAIL", "GUARD", "RAIL",
            "BARRIER", "PIPE", "TZINOR", "CHANNEL", "TAALA", "DRAIN", "NIKUZ",
        };
        private enum AssetIdentityScope { None, Counted, Linear }

        private static AssetIdentityScope MeasurementSubjectScope(DiscoveredGroup group)
        {
            var layer = SectionProjectionLogic.LayerLeaf(group.Layer);
            var wall = new[] { "WALL", "KIR", "RETAIN", "קיר" }.Any(token => ContainsWord(layer, token));
            var paving = new[] { "PAVE", "PAVED", "PAVEMENT", "PAVING", "RITZUF", "ריצוף" }
                .Any(token => ContainsWord(layer, token));
            var unit = Units.Parse(group.MeasuredUnit).Canonical;
            if ((wall || paving) && unit == "unit" &&
                group.MeasurementKind.Equals("count", StringComparison.OrdinalIgnoreCase))
                return AssetIdentityScope.Counted;
            if (paving && unit == "m" && group.MeasurementKind.Equals("length", StringComparison.OrdinalIgnoreCase))
                return AssetIdentityScope.Linear;
            return AssetIdentityScope.None;
        }

        private static IReadOnlyList<IReadOnlyList<string>> MeasuredAssetIdentities(
            DiscoveredGroup group, string? additionalExplicitSubjectEvidence)
        {
            var scope = MeasurementSubjectScope(group);
            var roles = scope == AssetIdentityScope.Counted ? CountedAssetRoles : LinearAssetRoles;
            var names = new List<string> { SectionProjectionLogic.LayerLeaf(group.Layer) };
            var metadata = MetadataSubjects(group, out var uncertain);
            if (!uncertain)
                names.AddRange(metadata.Where(subject => scope == AssetIdentityScope.Linear ||
                    subject.Key is "cad_block_name_effective" or "cad_block_name_raw")
                    .Select(subject => SectionProjectionLogic.LayerLeaf(subject.Value)));
            if (CountRuleKeySubject(group) is { } blockSubject)
                names.Add(SectionProjectionLogic.LayerLeaf(blockSubject));
            names.AddRange(EvidenceSubjects(group).Select(subject => subject.Value));
            if (!string.IsNullOrWhiteSpace(additionalExplicitSubjectEvidence))
                names.Add(additionalExplicitSubjectEvidence);
            return names.Select(name => (IReadOnlyList<string>)roles
                    .Where(role => ContainsWord(name, role) || LayerKeywords[role].Any(word => ContainsWord(name, word)))
                    .SelectMany(role => LayerKeywords[role]).Distinct(StringComparer.Ordinal).ToArray())
                .Where(words => words.Count > 0).ToArray();
        }

        // Older discovered groups may predate observational CAD metadata. Their
        // count key still names the measured block (BuildDiscoveryRuleKey). Read
        // only that exact, canonical identity; an XREF name is never an asset.
        // Existing/mixed block metadata takes precedence, not a fallback around it.
        private static string? CountRuleKeySubject(DiscoveredGroup group)
        {
            if (MeasurementSubjectScope(group) != AssetIdentityScope.Counted ||
                string.IsNullOrWhiteSpace(group.Layer) || string.IsNullOrWhiteSpace(group.RuleKey) ||
                (group.CadMetadata ?? Array.Empty<QuantityCadMetadataPolicy.FieldSummary>())
                    .Any(field => field.Key is "cad_block_name_effective" or "cad_block_name_raw")) return null;
            MetadataSubjects(group, out var uncertain);
            if (uncertain) return null;
            var parts = group.RuleKey.Split('|');
            if (parts.Length != 3 || !parts[0].StartsWith("layer:", StringComparison.Ordinal) ||
                !parts[1].Equals("count", StringComparison.OrdinalIgnoreCase) ||
                !parts[2].StartsWith("block:", StringComparison.Ordinal) ||
                !parts[0]["layer:".Length..].Equals(SectionProjectionLogic.LayerLeaf(group.Layer),
                    StringComparison.OrdinalIgnoreCase)) return null;
            var encoded = parts[2]["block:".Length..];
            string block;
            try { block = Uri.UnescapeDataString(encoded); }
            catch (UriFormatException) { return null; }
            // Roundtrip rejects malformed escapes, literal delimiters and double
            // decoding. Normalization must match the discovery writer exactly.
            if (!Uri.EscapeDataString(block).Equals(encoded, StringComparison.OrdinalIgnoreCase) ||
                !NormalizeBlockName(block).Equals(block, StringComparison.OrdinalIgnoreCase)) return null;
            var layerParts = group.Layer.Split('|');
            var blockParts = block.Split('|');
            if (layerParts.Length != blockParts.Length ||
                layerParts.Concat(blockParts).Any(part => string.IsNullOrWhiteSpace(part) || part != part.Trim()) ||
                blockParts.Any(part => part.Contains('%') || part.Any(char.IsControl)) ||
                !NormalizeBlockName(string.Join("|", layerParts.Take(layerParts.Length - 1)))
                    .Equals(string.Join("|", blockParts.Take(blockParts.Length - 1)), StringComparison.OrdinalIgnoreCase))
                return null;
            return block;

            static string NormalizeBlockName(string name) => string.Join("_", name.Trim()
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        }

        /// <summary>
        /// Wall length/area and paving area can retain their ordinary proposals.
        /// Wall/paving symbols and paving lines need a counted/linear asset identity:
        /// a mounting location or surrounding paving in catalog text is insufficient.
        /// Explicit engineer meaning can supply retrieval evidence, never approval.
        /// </summary>
        public static bool HasAutomaticMeasurementSubjectEvidence(DiscoveredGroup group,
            string? additionalExplicitSubjectEvidence = null) =>
            MeasurementSubjectScope(group) == AssetIdentityScope.None ||
            MeasuredAssetIdentities(group, additionalExplicitSubjectEvidence).Count > 0;

        public static bool IsAutomaticMeasurementSubjectCompatible(DiscoveredGroup group, string description,
            string? additionalExplicitSubjectEvidence = null)
        {
            if (MeasurementSubjectScope(group) == AssetIdentityScope.None) return true;
            var identities = MeasuredAssetIdentities(group, additionalExplicitSubjectEvidence);
            // Match the actual catalog subject after ordinary procurement wording,
            // not a later "mounted on wall" / "opening in paving" clause. Unknown
            // primary wording stays available through explicit manual/rule review.
            var primary = Regex.Replace(description.TrimStart(),
                @"^(?:(?:אספקה|אספקת|והתקנה|והתקנת|התקנה|התקנת|הנחת|נטיעת|בניית|של)\s+){0,5}", "");
            var withoutClassifier = Regex.Replace(primary, @"^(?:גוף|גופי|יחידת|יחידות|אביזר|אביזרי)\s+", "");
            return identities.Count > 0 && identities.All(words => words.Any(word =>
                StartsWithSubject(primary, word) || StartsWithSubject(withoutClassifier, word)));

            static bool StartsWithSubject(string text, string word) =>
                text.StartsWith(word, StringComparison.Ordinal) &&
                ContainsWord(text[..Math.Min(text.Length, word.Length + 1)], word);
        }

        public const string CountSubjectReviewRequired =
            "שם שכבת קיר או ריצוף אינו מזהה פריט נספר או קווי. נדרשת זהות פריט במקור או הסבר הנדסי מפורש, " +
            "או כלל מדידה מדויק. ניתן לבדוק ולשייך ידנית; המדידות לא הוחרגו.";

        /// <summary>
        /// Builds ranked proposals for each unmapped group.
        /// </summary>
        /// <param name="referenceCodes">
        /// Catalog codes seen in a comparable delivered estimate. Used only to boost
        /// ranking — never to assert that an item belongs in this project.
        /// </param>
        public static List<MappingProposal> Propose(
            IEnumerable<DiscoveredGroup> groups,
            CatalogSnapshot snapshot,
            IReadOnlyCollection<string>? referenceCodes = null)
        {
            var reference = new HashSet<string>(
                referenceCodes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var results = new List<MappingProposal>();

            foreach (var group in groups)
            {
                // Contradicting object evidence is a stop, not a tie for a layer or block name to break.
                if (EvidenceRefusal(group) != null) continue;
                var bridge = group.RecognitionEvidence;
                // Library recipe codes as this list's own codes: listed, or an engineer-approved edition link.
                var familyCodes = new HashSet<string>((bridge?.FamilyCandidateCodes ?? Array.Empty<string>())
                    .Select(code => snapshot.ResolveLibraryCode(code) ?? code), StringComparer.OrdinalIgnoreCase);
                if (!IsProposalEligible(group) ||
                    (!HasAutomaticMeasurementSubjectEvidence(group) && familyCodes.Count == 0)) continue;

                var measured = Units.Parse(group.MeasuredUnit);
                if (measured.Canonical == "?") continue; // unknown unit: never guess a code

                var layerKeywords = KeywordsFor(
                    MahodAI.CivilDelivery.Shared.SectionProjectionLogic.LayerLeaf(group.Layer));
                var metadataSubjects = MetadataSubjects(group, out var hasUncertainMetadata);
                var evidenceSubjects = EvidenceSubjects(group);
                var evidenceKeywords = evidenceSubjects.SelectMany(subject => subject.Keywords)
                    .Where(keyword => !ModifierWords.Contains(keyword)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var ruleBlockSubject = CountRuleKeySubject(group);
                var requiresAssetIdentity = MeasurementSubjectScope(group) != AssetIdentityScope.None;
                var keywords = layerKeywords.Concat(metadataSubjects.SelectMany(subject => subject.Keywords))
                    .Concat(evidenceSubjects.SelectMany(subject => subject.Keywords))
                    .Concat(ruleBlockSubject == null ? Array.Empty<string>() :
                        KeywordsFor(SectionProjectionLogic.LayerLeaf(ruleBlockSubject)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var semanticContext = string.Join("\n", new[] { group.Layer ?? string.Empty }
                    .Concat(metadataSubjects.Select(subject => subject.Value))
                    .Concat(evidenceSubjects.Select(subject => subject.Value))
                    .Concat(ruleBlockSubject == null ? Array.Empty<string>() : new[] { ruleBlockSubject }));
                if (LayerImpliesRemoval(group.Layer) ||
                    LayerImpliesRemoval(ruleBlockSubject) ||
                    metadataSubjects.Any(subject => LayerImpliesRemoval(subject.Value)) ||
                    evidenceSubjects.Any(subject => LayerImpliesRemoval(subject.Value)))
                    semanticContext += " EXST"; // Preserve a per-name trailing -EX marker after joining.
                var roadMarkingSubject = HasRoadMarkingSubject(semanticContext);
                var kerbSubject = HasKerbSubject(semanticContext);
                var roleNumbers = metadataSubjects.SelectMany(subject =>
                        MetadataRoleNumber.Matches(subject.Value).Cast<Match>())
                    .Select(match => match.Groups["number"].Value).Distinct(StringComparer.Ordinal).ToList();

                var candidates = new List<MappingProposal>();
                foreach (var item in snapshot.Items.Values)
                {
                    // HARD filter: identical unit only. A different unit would be a
                    // silent dimensional conversion, which the locked plan forbids.
                    if (!measured.SameUnit(item.Unit)) continue;
                    // An item of the family local recognition proposed from cited evidence is a candidate on that
                    // evidence; every other safety filter below still applies to it.
                    var fromFamily = familyCodes.Contains(item.Code);
                    if (!fromFamily && !IsAutomaticMeasurementSubjectCompatible(group, item.Description)) continue;

                    var reasons = new List<string>();
                    int score = 0;

                    var descriptionHits = keywords
                        .Where(k => ContainsWord(item.Description, k))
                        .ToList();
                    // Textual evidence is mandatory: a layer named "2000+217" must not be
                    // offered kerb demolition just because a past estimate used that code
                    // (seen live 2026-08-19: "? U51.01.0250" on every station-named layer).
                    if (descriptionHits.Count == 0 && !fromFamily) continue;
                    // What the objects say about themselves outranks a layer name: an item that names none of it
                    // is not proposed from it.
                    if (!fromFamily && evidenceKeywords.Count > 0 &&
                        !evidenceKeywords.Any(keyword => ContainsWord(item.Description, keyword)))
                        continue;

                    // Evidence names are constraints as well as search terms. Do not
                    // choose one arbitrary subject when complete fields disagree,
                    // e.g. a BENCH block using a WATER line type. Layer evidence is
                    // also retained instead of being overwritten by a block name.
                    if (!requiresAssetIdentity && metadataSubjects.Any(subject => !subject.Keywords.Any(keyword =>
                            !ModifierWords.Contains(keyword) && ContainsWord(item.Description, keyword))))
                        continue;
                    if (!requiresAssetIdentity && metadataSubjects.Count > 0 &&
                        layerKeywords.Any(keyword => !ModifierWords.Contains(keyword)) &&
                        !layerKeywords.Any(keyword => !ModifierWords.Contains(keyword) &&
                            ContainsWord(item.Description, keyword)))
                        continue;

                    // Demolition/maintenance wording is proposed only to layers that SAY
                    // they are existing or demolition. "פירוק אבני שפה" offered for a NEW
                    // curb layer, and "סתימת קו ביוב" for a sewer line, both reached the
                    // engineer's screen (live at her desk, 31/08).
                    // Demolition direction, subject and per-token exclusions are shared
                    // with curated/profile proposals; legacy rules do not bypass safety.
                    if (!IsSemanticallyCompatible(semanticContext, item.Description))
                        continue;

                    var primaryHits = descriptionHits.Where(k => !ModifierWords.Contains(k)).ToList();
                    var modifierHits = descriptionHits.Count - primaryHits.Count;
                    // State-only evidence (for example "-EX") does not identify what
                    // the object IS. Exact live regression: 1000+225-EX+W is a station
                    // layer, not a road-marking/removal quantity.
                    if (primaryHits.Count == 0 && !fromFamily) continue;
                    // The subject token (WALL → קיר) outweighs the "existing" modifier
                    // (-EX → פירוק): a wall layer proposes walls first, demolition of
                    // something else only after.
                    score += primaryHits.Count > 0 ? 50 + (primaryHits.Count - 1) * 10 : 0;
                    score += modifierHits * 15;
                    // A multi-word phrase ("קיר תומך") is stronger evidence than a bare word
                    // ("קיר" also matches "בקירות תמך" and "קיר לרצפה").
                    if (primaryHits.Any(k => k.Contains(' '))) score += 10;
                    var layerHits = layerKeywords.Where(keyword => ContainsWord(item.Description, keyword)).ToList();
                    if (layerHits.Count > 0)
                        reasons.Add($"שם השכבה '{group.Layer}' תואם לנוסח הסעיף: {string.Join(", ", layerHits)}");
                    foreach (var subject in metadataSubjects)
                    {
                        reasons.Add($"מאפיין CAD משותף לכל {group.ObjectCount} העצמים: " +
                            $"{subject.Key}='{subject.Value}' — רמז לחיפוש בלבד, לא אישור הנדסי");
                    }
                    foreach (var subject in evidenceSubjects.Where(subject =>
                                 subject.Keywords.Any(keyword => ContainsWord(item.Description, keyword))))
                    {
                        reasons.Add($"ראיית CAD קריאה של העצם: {subject.Citation} — רמז לחיפוש בלבד, לא אישור הנדסי");
                    }
                    if (fromFamily)
                    {
                        score += 40;
                        reasons.Add(bridge!.ConfirmedFamilyDecisionIds.Count > 0
                            ? $"החלטת משפחה שמורה תקפה לכל העצמים: '{bridge.RecognisedFamily}' " +
                              $"({string.Join(", ", bridge.ConfirmedFamilyDecisionIds)}), לפי {string.Join(", ", bridge.FamilyEvidenceKeys)}; " +
                              "פריטי המשפחה מוצעים לבדיקה קבוצתית בלבד — לא אישור סעיף, מחיר, עובי או מקדם; מתכון מרובה סעיפים אינו שיוך אוטומטי"
                            : $"זיהוי מקומי הציע לכל העצמים את המשפחה '{bridge.RecognisedFamily}' לפי " +
                              $"{string.Join(", ", bridge.FamilyEvidenceKeys)}; הסעיף הוא מפריטי המשפחה — הצעה בלבד, לא אישור");
                    }
                    if (hasUncertainMetadata)
                        reasons.Add("מאפייני CAD מעורבים או חסרים לא שימשו לבחירת הצעה לקבוצה; נדרשת בדיקה פרטנית");
                    if (ruleBlockSubject != null)
                        reasons.Add($"שם בלוק במפתח המדידה המאומת: '{ruleBlockSubject}' — רמז לחיפוש בלבד, לא אישור הנדסי");
                    if (ElectricalProposalCompatibility.MatchingVoltageReason(semanticContext, item.Description) is { } voltageReason)
                    {
                        score += 15;
                        reasons.Add(voltageReason);
                    }
                    else if (ElectricalProposalCompatibility.ReviewHint(semanticContext) is { } electricalHint)
                        reasons.Add(electricalHint);
                    if (semanticContext.Contains("TR-MARK-ARW", StringComparison.OrdinalIgnoreCase))
                        reasons.Add("מידות החץ, הגוון ומיקום היישום שבסעיף טרם אושרו; יש לבדוק גאומטריה ומפרט לפני בחירה");
                    if (roadMarkingSubject)
                        reasons.Add("נושא הסעיף הוא סימון דרך; רוחב/יחיד או כפול/חומר/גוון/מרקם אינם מאושרים מהשם או מרוחב CAD גולמי. יש לבדוק את המפרט לפני בחירה");
                    else if (kerbSubject)
                        reasons.Add("נושא הסעיף הוא אבן שפה; מידות/גוון/דגם אינם מאושרים משם השכבה. יש לבדוק את המפרט לפני בחירה");

                    foreach (var number in roleNumbers)
                    {
                        if (!Regex.IsMatch(item.Description,
                                @"(?<![0-9])" + Regex.Escape(number) + @"(?![0-9])")) continue;
                        score += 15;
                        reasons.Add($"מזהה מפורש בשם בלוק/סוג קו ({number}) מופיע גם בנוסח הסעיף; יש לבדוק התאמה");
                    }

                    if (reference.Contains(item.Code))
                    {
                        // "The office's example estimate used this exact item" is
                        // stronger evidence than one more keyword hit: U08.06.0185
                        // (road crossing that merely MENTIONS curbs) outranked
                        // U51.01.0250 (פירוק אבני שפה, the item their own estimate
                        // used) at the engineer's desk (31/08).
                        score += 60;
                        reasons.Add("הסעיף הופיע באומדן ייחוס של עבודה דומה");
                    }

                    score += 10;
                    reasons.Add($"יחידה זהה ({item.UnitRaw})");

                    if (snapshot.Prices.TryGetValue(item.Code, out var price) && !price.IsMissing)
                    {
                        score += 5;
                        reasons.Add("יש מחיר במהדורת המחירון הפעילה");
                    }
                    else
                    {
                        // An unpriced item is a weaker default than a priced one.
                        score -= 10;
                        reasons.Add("אין מחיר במהדורת המחירון הפעילה (MISSING_PRICE) — תידרש הצעת מחיר לפרויקט");
                    }

                    candidates.Add(new MappingProposal
                    {
                        RuleKey = group.RuleKey,
                        Layer = group.Layer,
                        MeasurementKind = group.MeasurementKind,
                        MeasuredUnit = group.MeasuredUnit,
                        ObjectCount = group.ObjectCount,
                        TotalQuantity = Math.Round(group.TotalQuantity, 3),
                        ProposedCode = item.Code,
                        CatalogDescription = Trim(item.Description, 120),
                        CatalogUnit = item.UnitRaw,
                        // Raw, uncapped: Math.Min(100, …) flattened a reference-
                        // estimate item (150) and a keyword-noise item (100) into a
                        // tie that ordinal code order broke the WRONG way (31/08).
                        Score = score,
                        EvidenceKind = metadataSubjects.Count > 0 ? "heuristic-cad-metadata" : "heuristic",
                        Reasons = reasons,
                    });
                }

                results.AddRange(candidates
                    .OrderByDescending(c => c.Score)
                    .ThenBy(c => c.ProposedCode, StringComparer.Ordinal)
                    .Take(MaxProposalsPerGroup));
            }

            return results;
        }

        private static List<string> KeywordsFor(string? layer)
        {
            var keywords = new List<string>();
            if (string.IsNullOrWhiteSpace(layer)) return keywords;

            foreach (var (token, words) in LayerKeywords)
            {
                // Electrical names use the same word boundary as AI evidence:
                // ELECTRIC is useful; SELECTRICISH is not an electrical subject.
                if (words.Contains("חשמל") ? ContainsWord(layer, token) :
                    layer.Contains(token, StringComparison.OrdinalIgnoreCase))
                    keywords.AddRange(words);
            }

            // July-2026 line-paint items can say only "קו ניתוב" (without the
            // generic word סימון). This is a subject synonym, not an inferred
            // width/material, and applies equally to complete object evidence.
            if (HasRoadMarkingSubject(layer))
                keywords.AddRange(new[] { "קו ניתוב", "קוניתוב", "קן ניתוב" });

            // Hebrew layer names can already carry the catalog wording directly.
            foreach (var part in layer.Split(new[] { '-', '_', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Length >= 3 && part.Any(c => c >= 0x0590 && c <= 0x05FF))
                    keywords.Add(part);
            }

            return keywords.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string Trim(string s, int n) =>
            string.IsNullOrEmpty(s) || s.Length <= n ? s : s[..(n - 1)] + "…";
    }
}
