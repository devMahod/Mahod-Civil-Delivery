using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>Physical dimension of a unit. Dimension equality is NOT unit equality.</summary>
    public enum UnitDimension { Length, Area, Volume, Count, Mass, Compound, Note, Unknown }

    /// <summary>Canonical unit with its dimension. No silent conversion, ever (plan §8.5).</summary>
    public sealed record UnitInfo(string Canonical, UnitDimension Dimension, string Raw)
    {
        public bool SameUnit(UnitInfo other) =>
            Canonical != "?" && string.Equals(Canonical, other.Canonical, StringComparison.Ordinal);
    }

    public static class Units
    {
        /// <summary>
        /// Parses Hebrew/engineering unit spellings (מ"ר, מ״ר, מ"ק, מטר, יח', קומפ',
        /// טון, ק"ג, דונם…). Unknown text is preserved, never coerced.
        /// </summary>
        public static UnitInfo Parse(string? raw)
        {
            var t = (raw ?? string.Empty).Trim()
                .Replace("״", "\"").Replace("''", "\"").Replace("`", "'")
                .TrimEnd('.');

            return t switch
            {
                "מ\"ר" or "מר" or "m2" or "m²" => new UnitInfo("m2", UnitDimension.Area, raw ?? ""),
                "מ\"ק" or "מק" or "m3" or "m³" => new UnitInfo("m3", UnitDimension.Volume, raw ?? ""),
                "מטר" or "מ'" or "מ" or "m" or "מא" or "מ\"א" => new UnitInfo("m", UnitDimension.Length, raw ?? ""),
                "יח'" or "יח" or "יחידה" or "unit" or "יח\"" => new UnitInfo("unit", UnitDimension.Count, raw ?? ""),
                "קומפ'" or "קומפ" or "קומפלט" => new UnitInfo("comp", UnitDimension.Compound, raw ?? ""),
                "טון" or "ton" => new UnitInfo("ton", UnitDimension.Mass, raw ?? ""),
                "ק\"ג" or "קג" or "kg" => new UnitInfo("kg", UnitDimension.Mass, raw ?? ""),
                "דונם" => new UnitInfo("dunam", UnitDimension.Area, raw ?? ""),
                "הערה" => new UnitInfo("note", UnitDimension.Note, raw ?? ""),
                "" => new UnitInfo("?", UnitDimension.Unknown, raw ?? ""),
                _ => new UnitInfo("?", UnitDimension.Unknown, raw ?? ""),
            };
        }
    }

    // ------------------------------------------------------------------ catalog

    /// <summary>CatalogItem != PriceRecord (plan §8.9).</summary>
    public sealed class CatalogItem
    {
        public required string Code { get; init; }
        public required string Description { get; init; }
        public required string UnitRaw { get; init; }
        [JsonIgnore] public UnitInfo Unit => Units.Parse(UnitRaw);

        /// <summary>"51" from U51.04.0080 (NTI) or from 68.052.0010 (Dekel) - any single-letter publisher prefix is ignored.</summary>
        public string Chapter => ChapterOf(Code);

        /// <summary>"51.04" from U51.04.0080; "68.052" from 68.052.0010.</summary>
        public string SubChapter
        {
            get
            {
                var parts = Bare(Code).Split('.');
                return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : Chapter;
            }
        }

        /// <summary>Chapter for any supported code shape; empty when the code has none.</summary>
        public static string ChapterOf(string code)
        {
            var parts = Bare(code).Split('.');
            return parts.Length >= 1 && parts[0].Length > 0 ? parts[0] : "";
        }

        /// <summary>Strips a single leading publisher letter (NTI "U"); digits are untouched.</summary>
        private static string Bare(string code) =>
            code.Length > 1 && char.IsLetter(code[0]) ? code[1..] : code;
    }

    public sealed class PriceRecord
    {
        public required string Code { get; init; }
        public decimal? Price { get; init; }
        public bool IsMissing => Price is null;
        public required string PriceBookId { get; init; }
        public string? SourceHash { get; init; }
    }

    /// <summary>A loaded, versioned price-book snapshot (identity = file hash, plan §8.10).</summary>
    public sealed class CatalogSnapshot
    {
        public required string SnapshotId { get; init; }
        public required string FileHash { get; init; }
        public string? PublicationNote { get; init; }
        public Dictionary<string, CatalogItem> Items { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PriceRecord> Prices { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Library code → item code of this list, from the engineer's approved edition links (EditionLinkPolicy).
        /// Empty when nothing is linked. Not part of the list's identity (<see cref="SnapshotId"/>, <see cref="FileHash"/>).
        /// </summary>
        public IReadOnlyDictionary<string, string> LibraryAliases { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>This list's code for a library code: itself when listed, else an approved link's listed item, else null.</summary>
        public string? ResolveLibraryCode(string? code) =>
            string.IsNullOrWhiteSpace(code) ? null
            : Items.ContainsKey(code) ? code
            : LibraryAliases.TryGetValue(code, out var linked) && Items.ContainsKey(linked) ? linked
            : null;
    }

    // ---------------------------------------------------------- neutral records

    /// <summary>Neutral Quantity Record — the serialized boundary (directive §20). No CAD ObjectIds.</summary>
    public sealed class NeutralQuantityRecord
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 1;
        [JsonPropertyName("record_id")] public required string RecordId { get; init; }
        [JsonPropertyName("project_profile_id")] public required string ProjectProfileId { get; init; }
        [JsonPropertyName("run_id")] public required string RunId { get; init; }

        [JsonPropertyName("source")] public required QuantitySource Source { get; init; }
        [JsonPropertyName("measurement")] public required QuantityMeasurement Measurement { get; init; }
        [JsonPropertyName("classification")] public QuantityClassification Classification { get; init; } = new();

        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DeliveryStatus Status { get; set; } = DeliveryStatus.Discovered;

        [JsonPropertyName("findings")] public List<DeliveryFinding> Findings { get; init; } = new();
        [JsonPropertyName("provenance")] public ProvenanceRef? Provenance { get; init; }
    }

    public sealed class QuantitySource
    {
        [JsonPropertyName("drawing")] public required string Drawing { get; init; }
        /// <summary>Canonical source path when the producer has one; Drawing remains the display name.</summary>
        [JsonPropertyName("drawing_path")] public string? DrawingPath { get; init; }
        [JsonPropertyName("drawing_hash")] public required string DrawingHash { get; init; }
        [JsonPropertyName("handle")] public required string Handle { get; init; }
        [JsonPropertyName("entity_type")] public required string EntityType { get; init; }
        [JsonPropertyName("layer")] public string? Layer { get; init; }
        [JsonPropertyName("xref")] public string? Xref { get; init; }
        [JsonPropertyName("civil_identity")] public string? CivilIdentity { get; init; }
        [JsonPropertyName("station_from")] public double? StationFrom { get; init; }
        [JsonPropertyName("station_to")] public double? StationTo { get; init; }
    }

    /// <summary>
    /// Immutable file identity for an external drawing that participated in an
    /// estimate scan.  One source file may appear through several insertion chains;
    /// the chain and reference-handle path keep those intentional instances
    /// auditable while path+hash provide the freshness boundary.
    /// </summary>
    public sealed class EstimateExternalSource
    {
        [JsonPropertyName("drawing_path")] public required string DrawingPath { get; init; }
        [JsonPropertyName("drawing_hash")] public required string DrawingHash { get; init; }
        [JsonPropertyName("xref_chain")] public required string XrefChain { get; init; }
        [JsonPropertyName("reference_handle_path")] public required string ReferenceHandlePath { get; init; }
    }

    public sealed class QuantityMeasurement
    {
        /// <summary>length | area | count | volume</summary>
        [JsonPropertyName("kind")] public required string Kind { get; init; }
        /// <summary>e.g. polyline-length | hatch-area | block-count | corridor-qto</summary>
        [JsonPropertyName("method")] public required string Method { get; init; }
        [JsonPropertyName("raw_value")]
        [JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
        public required double RawValue { get; init; }
        [JsonPropertyName("unit")] public required string Unit { get; init; }
        /// <summary>[minx, miny, maxx, maxy] where meaningful — drives overlap risk checks.</summary>
        [JsonPropertyName("geometry_evidence")] public double[]? GeometryEvidence { get; init; }
        [JsonPropertyName("parameters")] public Dictionary<string, string> Parameters { get; init; } = new();
    }

    /// <summary>
    /// Identity policy for the two measurements emitted from one closed polyline.
    /// Area and perimeter are alternatives, not two automatically additive BOQ
    /// quantities.  A pair is recognized only when the complete immutable source set
    /// is equal; sharing a layer name is deliberately insufficient.
    /// </summary>
    public static class ClosedPolylineAlternativePolicy
    {
        public sealed record Pair(
            string FirstRuleKey,
            string SecondRuleKey,
            string FirstKind,
            string SecondKind,
            IReadOnlyList<string> RecordIds)
        {
            public string AlternativeOf(string ruleKey)
            {
                if (string.Equals(FirstRuleKey, ruleKey, StringComparison.Ordinal))
                    return SecondRuleKey;
                if (string.Equals(SecondRuleKey, ruleKey, StringComparison.Ordinal))
                    return FirstRuleKey;
                throw new ArgumentException("The rule key does not belong to this closed-polyline pair.",
                    nameof(ruleKey));
            }
        }

        public static bool IsClosedPolylineMeasurement(NeutralQuantityRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            return (string.Equals(record.Measurement.Kind, "area", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(record.Measurement.Kind, "length", StringComparison.OrdinalIgnoreCase)) &&
                   record.Measurement.Method.StartsWith(
                       "closed-polyline", StringComparison.OrdinalIgnoreCase);
        }

        public static IReadOnlyList<Pair> FindExactPairs(
            IReadOnlyList<NeutralQuantityRecord> records)
        {
            ArgumentNullException.ThrowIfNull(records);
            var groups = records
                .Where(record => !string.IsNullOrWhiteSpace(record.Classification.RuleKey))
                .GroupBy(record => record.Classification.RuleKey!, StringComparer.Ordinal)
                .Select(group => group.ToList())
                // An ignore decision is persisted at rule-key scope.  Therefore a
                // rule containing even one hatch/line/other subject is not an exact
                // closed-polyline sibling and must never be auto-excluded wholesale.
                .Where(group => group.Count > 0 && group.All(IsClosedPolylineMeasurement))
                .ToList();
            var pairs = new List<Pair>();

            for (var leftIndex = 0; leftIndex < groups.Count; leftIndex++)
            {
                var left = groups[leftIndex];
                var leftKind = SingleKind(left);
                if (leftKind == null) continue;
                var leftSources = left.Select(SourceKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (leftSources.Count != left.Count) continue;

                for (var rightIndex = leftIndex + 1; rightIndex < groups.Count; rightIndex++)
                {
                    var right = groups[rightIndex];
                    var rightKind = SingleKind(right);
                    if (rightKind == null || string.Equals(leftKind, rightKind,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    if (left.Count != right.Count) continue;

                    var rightSources = right.Select(SourceKey)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (rightSources.Count != right.Count || !leftSources.SetEquals(rightSources))
                        continue;

                    pairs.Add(new Pair(
                        left[0].Classification.RuleKey!,
                        right[0].Classification.RuleKey!,
                        leftKind,
                        rightKind,
                        left.Concat(right).Select(record => record.RecordId)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(id => id, StringComparer.Ordinal)
                            .ToList()));
                }
            }

            return pairs;
        }

        /// <summary>
        /// Exact pairs safe for an automatic rule-level exclusion. If one rule key has
        /// more than one exact counterpart, the engineering choice is ambiguous and
        /// remains a review gate instead of guessing which sibling to ignore.
        /// </summary>
        public static IReadOnlyList<Pair> FindUnambiguousExactPairs(
            IReadOnlyList<NeutralQuantityRecord> records)
        {
            var pairs = FindExactPairs(records);
            var occurrences = pairs
                .SelectMany(pair => new[] { pair.FirstRuleKey, pair.SecondRuleKey })
                .GroupBy(ruleKey => ruleKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            return pairs.Where(pair =>
                    occurrences[pair.FirstRuleKey] == 1 &&
                    occurrences[pair.SecondRuleKey] == 1)
                .ToList();
        }

        private static string? SingleKind(IReadOnlyList<NeutralQuantityRecord> records)
        {
            var kinds = records.Select(record => record.Measurement.Kind)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return kinds.Count == 1 ? kinds[0] : null;
        }

        private static string SourceKey(NeutralQuantityRecord record) => string.Join("\u001f",
            record.Source.DrawingPath ?? record.Source.Drawing,
            record.Source.DrawingHash,
            record.Source.Xref ?? string.Empty,
            record.Source.Handle,
            record.Source.Layer ?? string.Empty);
    }

    public sealed class QuantityClassification
    {
        [JsonPropertyName("source_class")] public string? SourceClass { get; set; }
        [JsonPropertyName("rule_key")] public string? RuleKey { get; set; }
        [JsonPropertyName("candidate_catalog_code")] public string? CandidateCatalogCode { get; set; }
        [JsonPropertyName("approved_catalog_id")] public string? ApprovedCatalogId { get; set; }
        [JsonPropertyName("approved_catalog_hash")] public string? ApprovedCatalogHash { get; set; }
        [JsonPropertyName("approved_catalog_item_fingerprint")] public string? ApprovedCatalogItemFingerprint { get; set; }
        [JsonPropertyName("mapping_approved_by")] public string? MappingApprovedBy { get; set; }
        [JsonPropertyName("mapping_approved_at_utc")] public DateTime? MappingApprovedAtUtc { get; set; }
        [JsonPropertyName("tags")] public List<string> Tags { get; init; } = new();
    }

    // ------------------------------------------------------------ estimate out

    public sealed class AdjustmentStep
    {
        [JsonPropertyName("rule_id")] public required string RuleId { get; init; }
        [JsonPropertyName("factor")]
        [JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
        public double? Factor { get; init; }
        [JsonPropertyName("input")]
        [JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
        public double Input { get; init; }
        [JsonPropertyName("output")]
        [JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
        public double Output { get; init; }
        [JsonPropertyName("reason")] public string? Reason { get; init; }
        [JsonPropertyName("source")] public string? Source { get; init; }
        [JsonPropertyName("approved_by")] public string? ApprovedBy { get; init; }
        [JsonPropertyName("approved_at_utc")] public DateTime? ApprovedAtUtc { get; init; }
        [JsonPropertyName("order")] public int Order { get; init; }
    }

    public enum PriceStatus { Priced, MissingPrice, ProjectOverride, Unmapped }

    public sealed class EstimateExclusionSource
    {
        [JsonPropertyName("record_id")] public required string RecordId { get; init; }
        [JsonPropertyName("drawing")] public required string Drawing { get; init; }
        [JsonPropertyName("drawing_path")] public string? DrawingPath { get; init; }
        [JsonPropertyName("drawing_hash")] public required string DrawingHash { get; init; }
        [JsonPropertyName("handle")] public required string Handle { get; init; }
        [JsonPropertyName("xref")] public string? Xref { get; init; }
        [JsonPropertyName("entity_type")] public string? EntityType { get; init; }
        [JsonPropertyName("layer")] public string? Layer { get; init; }
        [JsonPropertyName("measurement_method")] public string? MeasurementMethod { get; init; }
        [JsonPropertyName("measurement_kind")] public required string MeasurementKind { get; init; }
        [JsonPropertyName("unit")] public required string Unit { get; init; }
        [JsonPropertyName("raw_value")] public double RawValue { get; init; }
    }

    public sealed class EstimateExclusion
    {
        [JsonPropertyName("rule_key")] public required string RuleKey { get; init; }
        [JsonPropertyName("reason")] public required string Reason { get; init; }
        [JsonPropertyName("approved_by")] public required string ApprovedBy { get; init; }
        [JsonPropertyName("approved_at_utc")] public required DateTime ApprovedAtUtc { get; init; }
        [JsonPropertyName("sources")] public List<EstimateExclusionSource> Sources { get; init; } = new();
    }

    public sealed class EstimateLine
    {
        [JsonPropertyName("line_id")] public required string LineId { get; init; }
        [JsonPropertyName("record_id")] public required string RecordId { get; init; }
        /// <summary>Drawing layer the quantity came from; how unmapped lines are grouped on the sheet.</summary>
        [JsonPropertyName("source_layer")] public string? SourceLayer { get; set; }
        [JsonPropertyName("source_drawing")] public string? SourceDrawing { get; set; }
        [JsonPropertyName("source_drawing_path")] public string? SourceDrawingPath { get; set; }
        [JsonPropertyName("source_drawing_hash")] public string? SourceDrawingHash { get; set; }
        [JsonPropertyName("source_database_revision")] public string? SourceDatabaseRevision { get; set; }
        [JsonPropertyName("source_handle")] public string? SourceHandle { get; set; }
        [JsonPropertyName("source_entity_type")] public string? SourceEntityType { get; set; }
        [JsonPropertyName("source_xref")] public string? SourceXref { get; set; }
        [JsonPropertyName("source_civil_identity")] public string? SourceCivilIdentity { get; set; }
        [JsonPropertyName("source_station_from")] public double? SourceStationFrom { get; set; }
        [JsonPropertyName("source_station_to")] public double? SourceStationTo { get; set; }
        [JsonPropertyName("measurement_method")] public string? MeasurementMethod { get; set; }
        /// <summary>Original measurement evidence, separate from the later pricing status. Missing legacy evidence remains unknown.</summary>
        [JsonPropertyName("source_measurement_status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DeliveryStatus? SourceMeasurementStatus { get; set; }
        [JsonPropertyName("source_measurement_kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceMeasurementKind { get; set; }
        /// <summary>layer:XXX|kind - the approval key.</summary>
        [JsonPropertyName("rule_key")] public string? RuleKey { get; set; }
        [JsonPropertyName("mapping_approved_by")] public string? MappingApprovedBy { get; set; }
        [JsonPropertyName("mapping_approved_at_utc")] public DateTime? MappingApprovedAtUtc { get; set; }
        [JsonPropertyName("approved_catalog_id")] public string? ApprovedCatalogId { get; set; }
        [JsonPropertyName("approved_catalog_hash")] public string? ApprovedCatalogHash { get; set; }
        [JsonPropertyName("approved_catalog_item_fingerprint")] public string? ApprovedCatalogItemFingerprint { get; set; }
        [JsonPropertyName("catalog_code")] public string? CatalogCode { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("unit")] public string? Unit { get; set; }
        [JsonPropertyName("raw_quantity")]
        [JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
        public double RawQuantity { get; set; }
        [JsonPropertyName("adjustments")] public List<AdjustmentStep> Adjustments { get; init; } = new();
        [JsonPropertyName("boq_quantity")] public double BoqQuantity { get; set; }
        [JsonPropertyName("price")] public decimal? Price { get; set; }
        [JsonPropertyName("price_status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public PriceStatus PriceStatus { get; set; } = PriceStatus.Unmapped;
        [JsonPropertyName("price_book_id")] public string? PriceBookId { get; set; }
        [JsonPropertyName("price_decision_source")] public string? PriceDecisionSource { get; set; }
        [JsonPropertyName("price_decision_reason")] public string? PriceDecisionReason { get; set; }
        [JsonPropertyName("price_approved_by")] public string? PriceApprovedBy { get; set; }
        [JsonPropertyName("price_approved_at_utc")] public DateTime? PriceApprovedAtUtc { get; set; }
        [JsonPropertyName("total")] public decimal? Total { get; set; }
        [JsonPropertyName("included_in_totals")] public bool IncludedInTotals { get; set; }
        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DeliveryStatus Status { get; set; } = DeliveryStatus.Discovered;
        [JsonPropertyName("findings")] public List<DeliveryFinding> Findings { get; init; } = new();
    }

    public sealed class EstimateResult
    {
        [JsonPropertyName("run_id")] public required string RunId { get; init; }
        [JsonPropertyName("project_profile_id")] public required string ProjectProfileId { get; init; }
        [JsonPropertyName("project_profile_hash")] public string? ProjectProfileHash { get; init; }
        [JsonPropertyName("project_profile_hash_kind")] public string? ProjectProfileHashKind { get; init; }
        [JsonPropertyName("project_profile_effective_hash")] public string? ProjectProfileEffectiveHash { get; init; }
        [JsonPropertyName("price_book_id")] public string? PriceBookId { get; init; }
        [JsonPropertyName("price_book_hash")] public string? PriceBookHash { get; init; }
        [JsonPropertyName("source_scope_policy")] public string? SourceScopePolicy { get; init; }
        [JsonPropertyName("source_selection"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EstimateSourceSelection? SourceSelection { get; init; }
        [JsonPropertyName("xref_policy")] public string? XrefPolicy { get; init; }
        [JsonPropertyName("scope_notice")] public string? ScopeNotice { get; init; }
        [JsonPropertyName("source_snapshot_kind")] public string? SourceSnapshotKind { get; init; }
        [JsonPropertyName("source_drawing_path")] public string? SourceDrawingPath { get; init; }
        [JsonPropertyName("source_drawing_hash")] public string? SourceDrawingHash { get; init; }
        [JsonPropertyName("source_database_revision")] public string? SourceDatabaseRevision { get; init; }
        [JsonPropertyName("source_dbmod")] public int? SourceDbMod { get; init; }
        [JsonPropertyName("external_sources")] public List<EstimateExternalSource> ExternalSources { get; init; } = new();
        [JsonPropertyName("exclusions")] public List<EstimateExclusion> Exclusions { get; init; } = new();
        [JsonPropertyName("lines")] public List<EstimateLine> Lines { get; init; } = new();
        [JsonPropertyName("findings")] public List<DeliveryFinding> Findings { get; init; } = new();
        [JsonPropertyName("clean_total")] public decimal CleanTotal { get; set; }
        [JsonPropertyName("excluded_line_count")] public int ExcludedLineCount { get; set; }
        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DeliveryStatus Status { get; set; } = DeliveryStatus.Discovered;
    }

    /// <summary>
    /// Immutable identity supplied at the Build boundary. Civil uses a saved-live
    /// snapshot; headless producers receive an explicitly labelled neutral-record
    /// snapshot instead of pretending that a DWG session was observed.
    /// </summary>
    public sealed record EstimateBuildContext(
        string ProjectProfileHash,
        string ProjectProfileHashKind,
        string ProjectProfileEffectiveHash,
        string SourceSnapshotKind,
        string SourceDrawingPath,
        string SourceDrawingHash,
        string SourceDatabaseRevision,
        int? SourceDbMod)
    {
        public const string CivilLiveSaved = "civil-live-saved";
        public const string NeutralRecordSet = "neutral-record-set";
        public const string SourceFileHash = "source-file-sha256";
        public const string EffectiveProfileHash = "effective-profile-json-sha256";
        public IReadOnlyList<EstimateExternalSource> ExternalSources { get; init; } =
            Array.Empty<EstimateExternalSource>();
    }

    /// <summary>Deterministic effective-configuration identity and headless trace fallback.</summary>
    public static class EstimateTraceIdentity
    {
        private static readonly JsonSerializerOptions CanonicalJson = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };

        public static string EffectiveProfileHash(ProjectProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            return ArtifactHash.Sha256OfText(JsonSerializer.Serialize(profile, CanonicalJson));
        }

        public static EstimateBuildContext InferHeadless(
            IReadOnlyList<NeutralQuantityRecord> records, ProjectProfile profile)
        {
            ArgumentNullException.ThrowIfNull(records);
            ArgumentNullException.ThrowIfNull(profile);
            var effective = EffectiveProfileHash(profile);
            var recordJson = JsonSerializer.Serialize(records, CanonicalJson);
            var recordHash = ArtifactHash.Sha256OfText(recordJson);
            var paths = records.Select(r => r.Source.DrawingPath ?? r.Source.Drawing)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var hashes = records.Select(r => r.Source.DrawingHash)
                .Where(CatalogIdentity.IsValidSha256)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new EstimateBuildContext(
                effective,
                EstimateBuildContext.EffectiveProfileHash,
                effective,
                EstimateBuildContext.NeutralRecordSet,
                paths.Count == 1 ? paths[0] : "(multiple neutral sources)",
                hashes.Count == 1 ? hashes[0] : string.Empty,
                "neutral-records:" + recordHash,
                null);
        }
    }

    /// <summary>Pure fail-closed rules shared by the Civil adapter and host-free tests.</summary>
    public static class EstimateSourceSnapshotPolicy
    {
        public enum ScanSourceAction
        {
            Ready,
            SaveAndScan,
            SaveAsAndScan,
            Blocked,
        }

        /// <summary>
        /// Engineer-facing interpretation of the low-level DWG snapshot gate.  The
        /// estimate still refuses unsaved bytes, but the palette can now distinguish
        /// the normal, recoverable "please save" case from an identity failure that a
        /// save cannot safely repair.
        /// </summary>
        public sealed record ScanSourceReadiness(
            ScanSourceAction Action,
            string Title,
            string Detail)
        {
            public bool IsReady => Action == ScanSourceAction.Ready;
            public bool CanSaveAndResume =>
                Action == ScanSourceAction.SaveAndScan || Action == ScanSourceAction.SaveAsAndScan;
        }

        public static ScanSourceReadiness ForScan(
            string? drawingPath,
            string? drawingHash,
            int? dbMod,
            string? captureFailure = null)
        {
            var hasPath = !string.IsNullOrWhiteSpace(drawingPath);
            if (dbMod == null)
                return new ScanSourceReadiness(
                    ScanSourceAction.Blocked,
                    "לא ניתן לבדוק אם השרטוט שמור",
                    "יש לסיים פקודה פעילה ב-Civil, לוודא שהשרטוט הפעיל פתוח ולנסות שוב. " +
                    (string.IsNullOrWhiteSpace(captureFailure) ? string.Empty : captureFailure));

            if (dbMod.Value != 0)
                return new ScanSourceReadiness(
                    hasPath ? ScanSourceAction.SaveAndScan : ScanSourceAction.SaveAsAndScan,
                    hasPath ? "יש שינויים שלא נשמרו" : "יש לשמור את השרטוט בשם",
                    $"DBMOD={dbMod.Value}. האומדן חייב לקשור כל כמות ל-SHA-256 של קובץ DWG שמור.");

            if (!hasPath)
                return new ScanSourceReadiness(
                    ScanSourceAction.SaveAsAndScan,
                    "השרטוט עדיין לא נשמר לקובץ DWG",
                    "יש לבחור שם ומיקום לקובץ לפני סריקת כמויות.");

            if (!string.IsNullOrWhiteSpace(captureFailure))
                return new ScanSourceReadiness(
                    ScanSourceAction.Blocked,
                    "לא ניתן לאמת את קובץ המקור",
                    "ודא שה-DWG נגיש לקריאה, שאין פקודה פעילה ושהשרטוט הנכון פעיל. " + captureFailure);

            if (!CatalogIdentity.IsValidSha256(drawingHash))
                return new ScanSourceReadiness(
                    ScanSourceAction.Blocked,
                    "לא ניתן לחשב זהות SHA-256 ל-DWG השמור",
                    "ודא שיש הרשאת קריאה לקובץ ושאינו נעול, ואז נסה שוב.");

            return new ScanSourceReadiness(
                ScanSourceAction.Ready,
                "ה-DWG שמור ומוכן לסריקה",
                "קובץ המקור אומת וייקשר לכל רשומת כמות.");
        }

        public static string? InitialFailure(string? drawingHash, int? dbMod, string? dbModError = null)
        {
            if (dbMod == null)
                return "DBMOD could not be read" + (string.IsNullOrWhiteSpace(dbModError) ? "" : $": {dbModError}");
            if (dbMod.Value != 0)
                return $"the active drawing has unsaved changes (DBMOD={dbMod.Value})";
            if (!CatalogIdentity.IsValidSha256(drawingHash))
                return "the saved DWG bytes do not have a valid SHA-256";
            return null;
        }

        public static string? FreshnessFailure(
            string? scannedHash, string? scannedRevision,
            string? currentHash, string? currentRevision,
            int? currentDbMod, string? dbModError = null, int? scannedDbMod = null)
        {
            var source = InitialFailure(currentHash, currentDbMod, dbModError);
            // Autodesk DBMOD bit 16 is view-only. It may follow our own locate/zoom
            // after a genuinely saved scan; it is not permission to start a scan
            // from dirty geometry, clear DBMOD, or replace the captured revision.
            if (currentDbMod == 16 && scannedDbMod == 0)
                source = !string.IsNullOrWhiteSpace(dbModError) ? dbModError :
                    !CatalogIdentity.IsValidSha256(currentHash)
                        ? "the saved DWG bytes do not have a valid SHA-256" : null;
            if (source != null) return source;
            if (!CatalogIdentity.IsValidSha256(scannedHash) ||
                !string.Equals(scannedHash, currentHash, StringComparison.OrdinalIgnoreCase))
                return "the saved DWG bytes changed since the quantity scan";
            if (string.IsNullOrWhiteSpace(scannedRevision) ||
                !string.Equals(scannedRevision, currentRevision, StringComparison.Ordinal))
                return "the live drawing database changed since the quantity scan";
            return null;
        }

        /// <summary>Permission to read disk evidence, not approval of a dirty initial scan.</summary>
        public static bool CanReadSavedDrawingHash(int? dbMod) => dbMod is 0 or 16;

        /// <summary>
        /// Narrow UI save-prompt hint only. It does not verify disk/XREF/profile
        /// hashes; the action must still perform the full freshness check.
        /// </summary>
        public static bool IsViewOnlySavedScanRevisionMatch(int? scannedDbMod, string? scannedRevision,
            int? currentDbMod, string? currentRevision) => scannedDbMod == 0 && currentDbMod == 16 &&
            !string.IsNullOrWhiteSpace(scannedRevision) &&
            string.Equals(scannedRevision, currentRevision, StringComparison.Ordinal);
    }
}
