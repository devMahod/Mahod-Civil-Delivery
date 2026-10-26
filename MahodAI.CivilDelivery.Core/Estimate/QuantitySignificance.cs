using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// Sorts a discovered scan so the engineer sees the decisions that matter first, and
    /// flags the groups that are plainly drawing furniture rather than construction work.
    ///
    /// Measured on the real 6422 model (2026-08-19): the scan produced 192 groups, of which
    /// 135 were station-named layers ("2000+120-2+W", "2000-DES+110+W") — the section/station
    /// geometry itself — and one "_Hidden" hatch reported 8.6 million m². Meanwhile 99.4% of
    /// the measured quantity sat in the top 20 groups. Presenting all 192 in discovery order
    /// makes a working tool look broken.
    ///
    /// Nothing here deletes or hides a quantity: a flagged group stays visible and measured.
    /// Unresolved flagged groups deliberately block export until the engineer either approves
    /// an exact mapping or records an audited "not a construction quantity" decision (see the
    /// profile's ignored-rule list, which only she can add to).
    /// </summary>
    public static class QuantitySignificance
    {
        /// <summary>A station-named layer: 2000+120+W, 2000-DES+110+W, 750+260819_101600.</summary>
        private static readonly Regex StationLayer = new(
            @"^\s*\d{2,4}(-[A-Za-z]+)*\s*\+\s*\d", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Layers AutoCAD or a template owns, never a construction item.</summary>
        private static readonly string[] SystemLayers = { "defpoints", "0-ezer", "_hidden", "pl-cont" };

        /// <summary>
        /// Layers this product draws on. The estimate once measured the tool's own
        /// section annotations — "MHD-SECT-ANNO · 242 lines · 1,758 m" sat in the
        /// quantity table like construction work (live, 30/08). A tool must never
        /// price its own drawings.
        /// </summary>
        private static readonly string[] ToolLayerPrefixes = { "MHD-", "MCD-", "MCDV-" };

        /// <summary>
        /// Helper/annotation layers seen on the real 6422 model that sat in the pending
        /// list as if they needed pricing (HELP-750+LINK-R+W, HW-CS-TABL, MPI_Symbol —
        /// engineer feedback, 31/08). Deliberately narrow patterns.
        /// </summary>
        private static readonly string[] HelperLayerPatterns = { "HELP*", "*TABL", "MPI_*", "*SYMBOL*" };

        public enum Kind
        {
            /// <summary>
            /// A non-finite or non-positive raw measurement.  This is a measurement
            /// failure, never drawing noise that an ignore decision may hide.
            /// </summary>
            InvalidMeasurement,

            /// <summary>A plausible construction quantity awaiting an engineer decision.</summary>
            Quantity,

            /// <summary>Section/station geometry: the CL lines and their labels, not work to price.</summary>
            StationGeometry,

            /// <summary>Template, helper or hidden layers; contours.</summary>
            Auxiliary,

            /// <summary>A handful of objects reporting an implausible amount — almost certainly not a quantity.</summary>
            ImplausibleMagnitude,

            /// <summary>
            /// A surveyed existing utility line (water, sewer, power…). Existing
            /// infrastructure is what the road is designed AROUND, not construction
            /// work to price — half the "needs mapping" backlog was MEKOROT/BIUV/MAIM
            /// survey layers (engineer feedback, 31/08). Still measured, still visible,
            /// still mappable by the engineer for protection/relocation items.
            /// </summary>
            ExistingUtility,
        }

        public sealed record Group(string RuleKey, string? Layer, string Unit, double Quantity, int ObjectCount);

        public sealed record Verdict(Kind Kind, string Reason)
        {
            public bool IsLikelyQuantity => Kind == Kind.Quantity;
        }

        /// <summary>An area no site produces: a hatch covering the whole sheet, a stray boundary.</summary>
        public const double ImplausibleAreaM2 = 500_000;

        /// <summary>A length no corridor produces from a couple of objects.</summary>
        public const double ImplausibleLengthM = 100_000;

        /// <summary>
        /// A single synthetic/model record above this volume is a source-selection
        /// incident until proved otherwise. The 6422 failure produced about 20M m³.
        /// </summary>
        public const double ImplausibleVolumeM3 = 5_000_000;

        /// <summary>Survey conventions and existing-state markers on a layer name.</summary>
        private static bool LooksExisting(string layer) =>
            layer.StartsWith("MYA-", StringComparison.OrdinalIgnoreCase) ||
            layer.Contains("EXST", StringComparison.OrdinalIgnoreCase) ||
            layer.EndsWith("-EX", StringComparison.OrdinalIgnoreCase) ||
            layer.Contains("-EX-", StringComparison.OrdinalIgnoreCase) ||
            layer.Contains("קיים", StringComparison.Ordinal);

        /// <summary>
        /// The minimum invariant for a measured BOQ contribution.  Keep it here so
        /// classification, exclusion decisions and the build boundary agree: NaN,
        /// infinities, zero and negative values are measurement failures.
        /// </summary>
        public static bool IsValidMeasurement(double value) =>
            double.IsFinite(value) && value > 0;

        public static bool IsValidMeasurement(double value, int objectCount) =>
            IsValidMeasurement(value) && objectCount > 0;

        public static Verdict Classify(Group g)
        {
            // This must precede every layer/name heuristic.  A broken geometry value
            // on a station/helper/tool layer is still a broken measurement; calling it
            // noise would let an exclusion decision erase the failure.
            if (!IsValidMeasurement(g.Quantity, g.ObjectCount))
                return new Verdict(Kind.InvalidMeasurement,
                    "כמות לא סופית/לא חיובית או קבוצה ללא עצמים — כשל מדידה שאינו ניתן להחרגה");

            // AutoCAD qualifies dependent layers as XREF|LAYER. Classification is
            // about the supplier layer, not the attachment name. The full value stays
            // in provenance, but XREF|0 and XREF|MHD-* must remain drawing furniture.
            var layer = SectionProjectionLogic.LayerLeaf(g.Layer);

            // Real 6422 evidence (31/08): 3,889 INSERT records on CURB-EXST were all
            // the survey marker block S_POINT_E.  Layer-only classification made them
            // look like 3,889 countable kerb units.  The current discovery key carries
            // the effective block name (|count|block:S_POINT_E), so use that stable,
            // auditable subject identity rather than inferring from the layer.  Keep the
            // group visible and engineer-overridable; only suppress automatic proposals.
            if (IsSurveyPointMarker(g))
                return new Verdict(Kind.Auxiliary,
                    "בלוק נקודת מדידה S_POINT_E — סימון סקר, לא יחידת עבודה לתמחור");

            if (StationLayer.IsMatch(layer))
                return new Verdict(Kind.StationGeometry, "שכבה בשם תחנה — גיאומטריית חתך/סימון, לא עבודה לתמחור");

            if (ToolLayerPrefixes.Any(pfx => layer.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)))
                return new Verdict(Kind.Auxiliary, "סימון של Mahod Civil Delivery — הכלי אינו מתמחר את השרטוטים של עצמו");

            // The same utility dictionary that projects systems into the sections also
            // recognises them here — but recognising a WATER layer does not say whether
            // it is existing infrastructure or a designed pipe to build: BIUV315 in the
            // design model is priced work (it matched a catalog item on the real 6422).
            // Only an explicit survey convention (MYA-*) or an existing marker
            // (-EXST/-EX/קיים) makes it existing; everything else stays a quantity and
            // the engineer decides — exactly the open question we sent her (-EXST:
            // מפרקים או משאירים?).
            var utility = Shared.SectionProjectionLogic.Classify(
                layer, null, Array.Empty<Shared.SectionProjectionLogic.ProjectionRuleConfig>());
            if (utility is { Kind: "utility" } && LooksExisting(layer))
                return new Verdict(Kind.ExistingUtility,
                    $"תשתית קיימת ({utility.Label}) — לא פריט עבודה; ניתן למפות ידנית לסעיפי הגנה/העתקה");

            if (HelperLayerPatterns.Any(p => Shared.SectionProjectionLogic.Wildcard(layer, p)))
                return new Verdict(Kind.Auxiliary, "שכבת עזר/טבלה/סמלים — לא פריט בנייה");

            // Geometry left on layer "0" is drafting residue by every CAD convention.
            if (layer == "0")
                return new Verdict(Kind.Auxiliary, "שכבת ברירת המחדל של AutoCAD — שרטוט עזר, לא עבודה לתמחור");

            if (SystemLayers.Any(s => layer.Equals(s, StringComparison.OrdinalIgnoreCase)) ||
                layer.StartsWith("_", StringComparison.Ordinal))
                return new Verdict(Kind.Auxiliary, "שכבת עזר/תבנית — לא פריט בנייה");

            var unit = Units.Parse(g.Unit).Canonical;
            // A multi-million cubic-metre result is a source-selection/width/station
            // incident until an engineer explicitly approves it.  Do not restrict
            // this guard to a handful of records: the bad 6422 surface sampling can
            // spread the same inflated total over many station records.
            if (unit == "m3" && g.Quantity > ImplausibleVolumeM3)
                return new Verdict(Kind.ImplausibleMagnitude,
                    $"{g.ObjectCount} רשומות מדווחות {g.Quantity:N0} מ\"ק — יש לאמת משטחים, רוחב ותחנות לפני תמחור");

            if (g.ObjectCount <= 3)
            {
                if (unit == "m2" && g.Quantity > ImplausibleAreaM2)
                    return new Verdict(Kind.ImplausibleMagnitude,
                        $"{g.ObjectCount} עצמים מדווחים {g.Quantity:N0} מ\"ר — כמעט בוודאי גבול/האצ' ולא שטח עבודה");
                if (unit == "m" && g.Quantity > ImplausibleLengthM)
                    return new Verdict(Kind.ImplausibleMagnitude,
                        $"{g.ObjectCount} עצמים מדווחים {g.Quantity:N0} מטר — כמעט בוודאי לא אורך עבודה");
            }

            return new Verdict(Kind.Quantity, "");
        }

        /// <summary>
        /// Native76 measured 2,330 S_POINT_E+ inserts on qualified S_PAVEMENT_UP,
        /// but the word PAVE alone proposed tree grilles. That source identity is
        /// insufficient construction-subject evidence. Abstain from suggestions,
        /// without classifying these records as auxiliary or authorizing exclusion.
        /// This is deliberately not a rule for all S_POINT_* blocks/survey layers.
        /// </summary>
        internal static bool RequiresExplicitSurveyMarkerSubjectReview(Group group)
        {
            if (string.IsNullOrWhiteSpace(group.RuleKey) || string.IsNullOrWhiteSpace(group.Layer) ||
                Units.Parse(group.Unit).Canonical != "unit") return false;
            var parts = group.RuleKey.Split('|');
            if (parts.Length != 3 ||
                !parts[0].Equals("layer:S_PAVEMENT_UP", StringComparison.OrdinalIgnoreCase) ||
                !parts[1].Equals("count", StringComparison.OrdinalIgnoreCase) ||
                !parts[2].StartsWith("block:", StringComparison.OrdinalIgnoreCase)) return false;
            string block;
            try { block = Uri.UnescapeDataString(parts[2]["block:".Length..]); }
            catch (UriFormatException) { return false; }
            var layerSeparator = group.Layer.LastIndexOf('|');
            var blockSeparator = block.LastIndexOf('|');
            if (layerSeparator <= 0 || blockSeparator <= 0 ||
                !group.Layer[(layerSeparator + 1)..].Equals("S_PAVEMENT_UP", StringComparison.OrdinalIgnoreCase) ||
                !block[(blockSeparator + 1)..].Equals("S_POINT_E+", StringComparison.OrdinalIgnoreCase)) return false;
            var sourceNamespace = group.Layer[..layerSeparator];
            return sourceNamespace.Equals(block[..blockSeparator], StringComparison.OrdinalIgnoreCase) &&
                sourceNamespace.Split('|').All(part => !string.IsNullOrWhiteSpace(part) && part == part.Trim());
        }

        private static bool IsSurveyPointMarker(Group group)
        {
            var ruleKey = group.RuleKey;
            if (string.IsNullOrWhiteSpace(ruleKey)) return false;
            var parts = ruleKey.Split('|');
            if (!parts.Any(part => part.Equals("count", StringComparison.OrdinalIgnoreCase)))
                return false;

            var encoded = parts.FirstOrDefault(part =>
                part.StartsWith("block:", StringComparison.OrdinalIgnoreCase));
            if (encoded == null) return false;

            string blockName;
            try { blockName = Uri.UnescapeDataString(encoded.Substring("block:".Length)); }
            catch (UriFormatException) { return false; }
            if (blockName.Equals("S_POINT_E", StringComparison.OrdinalIgnoreCase)) return true;

            // Dependent symbols retain their actual XREF namespace, e.g. the native71
            // S_CURB group uses SP-MEDVA|S_POINT_E. Do not strip an arbitrary pipe:
            // the complete (possibly nested) prefix must also identify this group's
            // qualified source layer, whose leaf must agree with its discovery key.
            // This only proposes the existing reviewed noise workflow; it does not
            // ignore a record, add an approval or normalize any persisted identity.
            if (parts.Length != 3 || !parts[0].StartsWith("layer:", StringComparison.Ordinal) ||
                !parts[1].Equals("count", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(group.Layer)) return false;
            var layer = group.Layer;
            var blockSeparator = blockName.LastIndexOf('|');
            var layerSeparator = layer.LastIndexOf('|');
            if (blockSeparator <= 0 || layerSeparator <= 0 || layerSeparator == layer.Length - 1 ||
                !string.Equals(blockName[..blockSeparator], layer[..layerSeparator], StringComparison.OrdinalIgnoreCase) ||
                blockName[..blockSeparator].Split('|').Any(part => string.IsNullOrWhiteSpace(part) || part != part.Trim()) ||
                !string.Equals(parts[0]["layer:".Length..], SectionProjectionLogic.LayerLeaf(layer), StringComparison.OrdinalIgnoreCase))
                return false;
            return SectionProjectionLogic.LayerLeaf(blockName).Equals("S_POINT_E", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reading order: real quantities first, biggest within each unit, then station
        /// geometry and helper layers. Units are ordered the way a BOQ reads: length, area,
        /// count — quantities in different units cannot be compared to each other.
        /// </summary>
        public static IReadOnlyList<Group> Order(IEnumerable<Group> groups)
        {
            static int UnitRank(string unit) => Units.Parse(unit).Canonical switch
            {
                "m" => 0,
                "m2" => 1,
                "unit" => 2,
                _ => 3,
            };

            return groups
                .OrderBy(g => Classify(g).IsLikelyQuantity ? 0 : 1)
                .ThenBy(g => UnitRank(g.Unit))
                .ThenByDescending(g => g.Quantity)
                .ThenBy(g => g.RuleKey, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Converts the classifier from a cosmetic reading-order hint into a monetary
        /// preflight gate. Suspicious groups remain measured and visible, but export is
        /// blocked until an engineer either approves an exact mapping or explicitly
        /// marks the rule as not a construction quantity.
        /// </summary>
        public static IReadOnlyList<DeliveryFinding> DetectReviewFindings(
            IReadOnlyList<NeutralQuantityRecord> records,
            IEnumerable<string>? ignoredRuleKeys = null)
        {
            var ignored = new HashSet<string>(
                ignoredRuleKeys ?? Array.Empty<string>(), StringComparer.Ordinal);
            var findings = new List<DeliveryFinding>();
            var groups = records
                .GroupBy(r => r.Classification.RuleKey ?? "(none)", StringComparer.Ordinal)
                .ToList();

            // One closed boundary is deliberately emitted as two review alternatives.
            // Mapping both does not make both true: the complete, exact source-handle
            // set still represents one physical subject.  Keep this monetary gate even
            // after both groups carry catalog codes; only an audited ignore decision
            // for one sibling resolves it.
            foreach (var pair in ClosedPolylineAlternativePolicy.FindExactPairs(records))
            {
                if (ignored.Contains(pair.FirstRuleKey) || ignored.Contains(pair.SecondRuleKey))
                    continue;
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.MixedDimensionLayer,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "אותם פוליליינים סגורים מייצגים שתי חלופות מדידה",
                    Message = $"{pair.FirstRuleKey} ({pair.FirstKind}) <> " +
                              $"{pair.SecondRuleKey} ({pair.SecondKind}); exact source set",
                    RecommendedAction =
                        "יש לאשר מיפוי לחלופה הנכונה ולהחריג את החלופה האחות בהחלטה מתועדת; אין לתמחר את שתיהן.",
                    AffectedRecordIds = pair.RecordIds.ToList(),
                });
            }

            foreach (var group in groups)
            {
                var invalid = group
                    .Where(r => !IsValidMeasurement(r.Measurement.RawValue))
                    .ToList();
                if (invalid.Count > 0)
                {
                    findings.Add(new DeliveryFinding
                    {
                        Code = EstimateFindingCodes.MeasurementFailed,
                        Domain = "estimate",
                        Severity = FindingSeverity.Error,
                        Title = $"בקבוצה '{Bidi.Ltr(group.First().Source.Layer)}' נמצאה מדידה לא תקינה",
                        Message = "NaN, Infinity, אפס או כמות שלילית הם כשל מדידה ואינם ניתנים להחרגה או לתמחור.",
                        RecommendedAction = "יש לתקן את גיאומטריית המקור או את כלל המדידה ולהריץ סריקת כמויות חדשה.",
                        AffectedRecordIds = invalid.Select(r => r.RecordId)
                            .OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    });
                    continue;
                }

                if (ignored.Contains(group.Key)) continue;
                if (group.All(r => !string.IsNullOrWhiteSpace(r.Classification.CandidateCatalogCode)))
                    continue; // an explicit approved rule is the engineer's decision

                var first = group.First();
                var quantity = group.Sum(r => r.Measurement.RawValue);
                var verdict = Classify(new Group(
                    group.Key, first.Source.Layer, first.Measurement.Unit, quantity, group.Count()));
                if (verdict.IsLikelyQuantity) continue;

                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.QuantitySignificanceReview,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = $"הקבוצה '{Bidi.Ltr(first.Source.Layer)}' אינה כמות בנייה מוכחת",
                    Message = verdict.Reason,
                    RecommendedAction = "יש לבדוק את העצמים בשרטוט ולבחור במפורש: אשר מיפוי מתאים או סמן 'לא רלוונטי'.",
                    AffectedRecordIds = group.Select(r => r.RecordId).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                });
            }

            // A layer measured both as length and area was a concrete source of noise
            // on 6422 (CURB-EXST plus CURB-EXST-AREA). Do not guess which dimension is
            // procurement truth. Only unresolved records are gated; explicit approvals
            // for distinct physical subjects remain authoritative. Exact same-source
            // closed-polyline siblings were already gated above.
            foreach (var layerGroup in records
                         .Where(r => !string.IsNullOrWhiteSpace(r.Source.Layer))
                         .Where(r => !ignored.Contains(r.Classification.RuleKey ?? ""))
                         .GroupBy(r => r.Source.Layer!, StringComparer.OrdinalIgnoreCase))
            {
                var dimensions = layerGroup.Select(r => Units.Parse(r.Measurement.Unit).Dimension)
                    .Where(d => d != UnitDimension.Unknown)
                    .Distinct()
                    .ToList();
                if (dimensions.Count <= 1) continue;

                var layerRecords = layerGroup.ToList();
                var identities = layerRecords.Select(ExactObjectIdentity).ToList();
                var complete = identities.All(identity => identity != null) &&
                    HasConsistentSourceHashes(layerRecords);
                var mixedObjects = complete
                    ? layerRecords.Select((record, index) => (record, identity: identities[index]!))
                        .GroupBy(item => item.identity, StringComparer.OrdinalIgnoreCase)
                        .Where(group => group.Select(item => Units.Parse(item.record.Measurement.Unit).Dimension)
                            .Distinct().Count() > 1)
                        .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // A catalog decision does not make both dimensional alternatives of
                // one exact object additive, including a closed boundary embedded in
                // a larger length rule. Unknown identities retain the historic gate.
                if (complete && layerRecords.All(r =>
                        !string.IsNullOrWhiteSpace(r.Classification.CandidateCatalogCode)) &&
                    mixedObjects.Count == 0)
                    continue;

                // Only a proven, approved independent open length may leave this
                // layer-level gate. Preserve path/hash/full insertion-handle chain:
                // a leaf handle or a layer name alone never proves independence.
                var affected = layerRecords.Where((record, index) => !complete ||
                        string.IsNullOrWhiteSpace(record.Classification.CandidateCatalogCode) ||
                        !IsKnownOpenLength(record) || mixedObjects.Contains(identities[index]!))
                    .Select(r => r.RecordId)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
                if (affected.Count == 0) continue;
                if (findings.Any(finding =>
                        finding.Code == EstimateFindingCodes.MixedDimensionLayer &&
                        finding.AffectedRecordIds.OrderBy(id => id, StringComparer.Ordinal)
                            .SequenceEqual(affected, StringComparer.Ordinal)))
                    continue;

                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.MixedDimensionLayer,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = $"השכבה '{Bidi.Ltr(layerGroup.Key)}' נמדדה ביותר מממד אחד",
                    Message = "נמצאו באותה שכבה מדידות מסוגים שונים: " +
                              string.Join(", ", layerGroup.Select(r => $"{r.Measurement.Kind}/{r.Measurement.Unit}").Distinct()),
                    RecommendedAction = "יש להציג את העצמים, לאשר כל rule_key מתאים או לסמן את המדידה הנגזרת כרעש.",
                    AffectedRecordIds = affected,
                });
            }

            return findings;
        }

        /// <summary>Re-evaluate decision-derived gates; retain every original source/geometry finding.</summary>
        public static IReadOnlyList<DeliveryFinding> RecomputeDecisionFindings(
            IReadOnlyList<NeutralQuantityRecord> records, IEnumerable<DeliveryFinding> originalFindings,
            IEnumerable<string>? ignoredRuleKeys = null) => originalFindings
            .Where(finding => finding.Code != EstimateFindingCodes.QuantitySignificanceReview &&
                              finding.Code != EstimateFindingCodes.MixedDimensionLayer)
            .Concat(DetectReviewFindings(records, ignoredRuleKeys)).ToList();

        private static bool HasConsistentSourceHashes(IEnumerable<NeutralQuantityRecord> records)
        {
            // Two snapshots of one canonical drawing path cannot prove independent
            // objects. Do not turn conflicting source evidence into separate keys.
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                string path;
                try { path = Path.GetFullPath(record.Source.DrawingPath ?? record.Source.Drawing); }
                catch { return false; }
                var hash = record.Source.DrawingHash;
                if (hashes.TryGetValue(path, out var existing) &&
                    !string.Equals(existing, hash, StringComparison.OrdinalIgnoreCase)) return false;
                hashes[path] = hash;
            }
            return true;
        }

        private static string? ExactObjectIdentity(NeutralQuantityRecord record)
        {
            var source = record.Source;
            var path = source.DrawingPath ?? source.Drawing;
            if (!FindingSourceLocationPolicy.IsDriveQualifiedDrawingPath(path) ||
                !CatalogIdentity.IsValidSha256(source.DrawingHash) ||
                string.IsNullOrWhiteSpace(source.EntityType)) return null;
            var handles = source.Handle?.Split('/');
            if (handles == null || handles.Any(handle => !ulong.TryParse(handle,
                    NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number) || number == 0))
                return null;
            var xref = !string.IsNullOrWhiteSpace(source.Xref);
            if (xref ? handles.Length < 2 : handles.Length != 1) return null;
            if (MeasurementBaseMethod(record) == null) return null;
            try { return string.Join("|", Path.GetFullPath(path!), source.DrawingHash, source.Handle, source.Xref ?? ""); }
            catch { return null; }
        }

        private static string? MeasurementBaseMethod(NeutralQuantityRecord record)
        {
            var method = record.Measurement.Method;
            if (string.IsNullOrWhiteSpace(method)) return null;
            const string suffix = "+xref-transform";
            if (!string.IsNullOrWhiteSpace(record.Source.Xref))
            {
                if (!method.EndsWith(suffix, StringComparison.Ordinal)) return null;
                method = method[..^suffix.Length];
            }
            else if (method.Contains('+')) return null;
            var type = record.Source.EntityType.ToUpperInvariant();
            var dimension = Units.Parse(record.Measurement.Unit).Dimension;
            var length = record.Measurement.Kind == "length" && dimension == UnitDimension.Length;
            var area = record.Measurement.Kind == "area" && dimension == UnitDimension.Area;
            return (type, method) switch
            {
                ("LINE", "line-length") or ("ARC", "arc-length") or
                ("POLYLINE", "polyline-length") or ("POLYLINE2D", "polyline2d-length") or
                ("POLYLINE3D", "polyline3d-length") or ("SPLINE", "spline-length") or
                ("POLYLINE", "closed-polyline-perimeter") or ("POLYLINE2D", "closed-polyline2d-perimeter")
                    when length => method,
                ("POLYLINE", "closed-polyline-area") or ("POLYLINE2D", "closed-polyline2d-area")
                    when area => method,
                _ => null,
            };
        }

        private static bool IsKnownOpenLength(NeutralQuantityRecord record) =>
            MeasurementBaseMethod(record) is "line-length" or "arc-length" or "polyline-length" or
                "polyline2d-length" or "polyline3d-length" or "spline-length";
    }
}
