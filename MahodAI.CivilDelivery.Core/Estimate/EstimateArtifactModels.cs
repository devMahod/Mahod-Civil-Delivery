using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// Compact, self-contained representation of <see cref="EstimateResult"/> for the
    /// on-disk estimate_result.json artifact.
    ///
    /// Findings that are already present in the result-level finding registry are
    /// represented on a line by finding id.  The canonical finding (including its
    /// affected_record_ids, evidence and resolution data) is serialized exactly once
    /// in <see cref="Findings"/>.  Line-local findings remain inline for backwards
    /// readability.  This avoids quadratic JSON when a group finding affects many
    /// records while retaining a bidirectional audit trail.
    /// </summary>
    public sealed class EstimateResultArtifact
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 2;
        [JsonPropertyName("run_id")] public required string RunId { get; init; }
        [JsonPropertyName("project_profile_id")] public required string ProjectProfileId { get; init; }
        [JsonPropertyName("project_profile_hash"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProjectProfileHash { get; init; }
        [JsonPropertyName("project_profile_hash_kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProjectProfileHashKind { get; init; }
        [JsonPropertyName("project_profile_effective_hash"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProjectProfileEffectiveHash { get; init; }
        [JsonPropertyName("price_book_id")] public string? PriceBookId { get; init; }
        [JsonPropertyName("price_book_hash")] public string? PriceBookHash { get; init; }
        [JsonPropertyName("source_scope_policy")] public string? SourceScopePolicy { get; init; }
        [JsonPropertyName("source_selection"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EstimateSourceSelection? SourceSelection { get; init; }
        [JsonPropertyName("xref_policy")] public string? XrefPolicy { get; init; }
        [JsonPropertyName("scope_notice")] public string? ScopeNotice { get; init; }
        [JsonPropertyName("source_snapshot_kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceSnapshotKind { get; init; }
        [JsonPropertyName("source_drawing_path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceDrawingPath { get; init; }
        [JsonPropertyName("source_drawing_hash"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceDrawingHash { get; init; }
        [JsonPropertyName("source_database_revision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceDatabaseRevision { get; init; }
        [JsonPropertyName("source_dbmod"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? SourceDbMod { get; init; }
        [JsonPropertyName("external_sources")] public List<EstimateExternalSource> ExternalSources { get; init; } = new();
        [JsonPropertyName("exclusions")] public List<EstimateExclusion> Exclusions { get; init; } = new();
        [JsonPropertyName("lines")] public List<EstimateLineArtifact> Lines { get; init; } = new();
        [JsonPropertyName("findings")] public List<DeliveryFinding> Findings { get; init; } = new();
        [JsonPropertyName("clean_total")] public decimal CleanTotal { get; init; }
        [JsonPropertyName("excluded_line_count")] public int ExcludedLineCount { get; init; }
        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DeliveryStatus Status { get; init; }

        public static EstimateResultArtifact From(EstimateResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            var canonicalFindings = result.Findings
                .GroupBy(f => f.FindingId, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
            var canonicalFindingIds = canonicalFindings
                .Select(f => f.FindingId)
                .ToHashSet(StringComparer.Ordinal);

            return new EstimateResultArtifact
            {
                RunId = result.RunId,
                ProjectProfileId = result.ProjectProfileId,
                ProjectProfileHash = result.ProjectProfileHash,
                ProjectProfileHashKind = result.ProjectProfileHashKind,
                ProjectProfileEffectiveHash = result.ProjectProfileEffectiveHash,
                PriceBookId = result.PriceBookId,
                PriceBookHash = result.PriceBookHash,
                SourceScopePolicy = result.SourceScopePolicy,
                SourceSelection = result.SourceSelection,
                XrefPolicy = result.XrefPolicy,
                ScopeNotice = result.ScopeNotice,
                SourceSnapshotKind = result.SourceSnapshotKind,
                SourceDrawingPath = result.SourceDrawingPath,
                SourceDrawingHash = result.SourceDrawingHash,
                SourceDatabaseRevision = result.SourceDatabaseRevision,
                SourceDbMod = result.SourceDbMod,
                ExternalSources = result.ExternalSources,
                Exclusions = result.Exclusions,
                Lines = result.Lines
                    .Select(line => EstimateLineArtifact.From(line, canonicalFindingIds))
                    .ToList(),
                Findings = canonicalFindings,
                CleanTotal = result.CleanTotal,
                ExcludedLineCount = result.ExcludedLineCount,
                Status = result.Status,
            };
        }
    }

    /// <summary>
    /// An estimate line as stored in the schema-v2 artifact.  Property names match
    /// EstimateLine's schema-v1 shape; finding_refs is the only additive field.
    /// </summary>
    public sealed class EstimateLineArtifact
    {
        [JsonPropertyName("line_id")] public required string LineId { get; init; }
        [JsonPropertyName("record_id")] public required string RecordId { get; init; }
        [JsonPropertyName("source_layer")] public string? SourceLayer { get; init; }
        [JsonPropertyName("source_drawing"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceDrawing { get; init; }
        [JsonPropertyName("source_drawing_path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceDrawingPath { get; init; }
        [JsonPropertyName("source_drawing_hash"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceDrawingHash { get; init; }
        [JsonPropertyName("source_database_revision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceDatabaseRevision { get; init; }
        [JsonPropertyName("source_handle"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceHandle { get; init; }
        [JsonPropertyName("source_entity_type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceEntityType { get; init; }
        [JsonPropertyName("source_xref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceXref { get; init; }
        [JsonPropertyName("source_civil_identity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceCivilIdentity { get; init; }
        [JsonPropertyName("source_station_from"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? SourceStationFrom { get; init; }
        [JsonPropertyName("source_station_to"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? SourceStationTo { get; init; }
        [JsonPropertyName("measurement_method"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MeasurementMethod { get; init; }
        [JsonPropertyName("source_measurement_status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DeliveryStatus? SourceMeasurementStatus { get; init; }
        [JsonPropertyName("source_measurement_kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceMeasurementKind { get; init; }
        [JsonPropertyName("rule_key")] public string? RuleKey { get; init; }
        [JsonPropertyName("mapping_approved_by"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MappingApprovedBy { get; init; }
        [JsonPropertyName("mapping_approved_at_utc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTime? MappingApprovedAtUtc { get; init; }
        [JsonPropertyName("approved_catalog_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ApprovedCatalogId { get; init; }
        [JsonPropertyName("approved_catalog_hash"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ApprovedCatalogHash { get; init; }
        [JsonPropertyName("approved_catalog_item_fingerprint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ApprovedCatalogItemFingerprint { get; init; }
        [JsonPropertyName("catalog_code")] public string? CatalogCode { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("unit")] public string? Unit { get; init; }
        [JsonPropertyName("raw_quantity")]
        [JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
        public double RawQuantity { get; init; }
        [JsonPropertyName("adjustments")] public List<AdjustmentStep> Adjustments { get; init; } = new();
        [JsonPropertyName("boq_quantity")] public double BoqQuantity { get; init; }
        [JsonPropertyName("price")] public decimal? Price { get; init; }
        [JsonPropertyName("price_status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public PriceStatus PriceStatus { get; init; }
        [JsonPropertyName("price_book_id")] public string? PriceBookId { get; init; }
        [JsonPropertyName("price_decision_source"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PriceDecisionSource { get; init; }
        [JsonPropertyName("price_decision_reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PriceDecisionReason { get; init; }
        [JsonPropertyName("price_approved_by"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PriceApprovedBy { get; init; }
        [JsonPropertyName("price_approved_at_utc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTime? PriceApprovedAtUtc { get; init; }
        [JsonPropertyName("total")] public decimal? Total { get; init; }
        [JsonPropertyName("included_in_totals")] public bool IncludedInTotals { get; init; }
        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DeliveryStatus Status { get; init; }
        [JsonPropertyName("findings")] public List<DeliveryFinding> Findings { get; init; } = new();
        [JsonPropertyName("finding_refs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? FindingRefs { get; init; }

        internal static EstimateLineArtifact From(
            EstimateLine line, IReadOnlySet<string> canonicalFindingIds)
        {
            var inline = line.Findings
                .Where(f => !canonicalFindingIds.Contains(f.FindingId))
                .ToList();
            var references = line.Findings
                .Where(f => canonicalFindingIds.Contains(f.FindingId))
                .Select(f => f.FindingId)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return new EstimateLineArtifact
            {
                LineId = line.LineId,
                RecordId = line.RecordId,
                SourceLayer = line.SourceLayer,
                SourceDrawing = line.SourceDrawing,
                SourceDrawingPath = line.SourceDrawingPath,
                SourceDrawingHash = line.SourceDrawingHash,
                SourceDatabaseRevision = line.SourceDatabaseRevision,
                SourceHandle = line.SourceHandle,
                SourceEntityType = line.SourceEntityType,
                SourceXref = line.SourceXref,
                SourceCivilIdentity = line.SourceCivilIdentity,
                SourceStationFrom = line.SourceStationFrom,
                SourceStationTo = line.SourceStationTo,
                MeasurementMethod = line.MeasurementMethod,
                SourceMeasurementStatus = line.SourceMeasurementStatus,
                SourceMeasurementKind = line.SourceMeasurementKind,
                RuleKey = line.RuleKey,
                MappingApprovedBy = line.MappingApprovedBy,
                MappingApprovedAtUtc = line.MappingApprovedAtUtc,
                ApprovedCatalogId = line.ApprovedCatalogId,
                ApprovedCatalogHash = line.ApprovedCatalogHash,
                ApprovedCatalogItemFingerprint = line.ApprovedCatalogItemFingerprint,
                CatalogCode = line.CatalogCode,
                Description = line.Description,
                Unit = line.Unit,
                RawQuantity = line.RawQuantity,
                Adjustments = line.Adjustments,
                BoqQuantity = line.BoqQuantity,
                Price = line.Price,
                PriceStatus = line.PriceStatus,
                PriceBookId = line.PriceBookId,
                PriceDecisionSource = line.PriceDecisionSource,
                PriceDecisionReason = line.PriceDecisionReason,
                PriceApprovedBy = line.PriceApprovedBy,
                PriceApprovedAtUtc = line.PriceApprovedAtUtc,
                Total = line.Total,
                IncludedInTotals = line.IncludedInTotals,
                Status = line.Status,
                Findings = inline,
                FindingRefs = references.Count == 0 ? null : references,
            };
        }
    }
}
