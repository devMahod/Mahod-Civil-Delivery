using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// Reads persisted estimate_result.json artifacts into the runtime model.
    /// Schema v1 is the original inline-finding shape (with or without an explicit
    /// schema_version). Schema v2 keeps canonical findings at the root and restores
    /// each line's complete finding list through finding_refs.
    /// </summary>
    public static class EstimateResultArtifactReader
    {
        public const int LegacySchemaVersion = 1;
        public const int CompactSchemaVersion = 2;

        public static EstimateResult Read(string json, JsonSerializerOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(json);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Estimate artifact root must be a JSON object.");

            var schemaVersion = ReadSchemaVersion(document.RootElement);
            return schemaVersion switch
            {
                LegacySchemaVersion => JsonSerializer.Deserialize<EstimateResult>(json, options)
                    ?? throw new JsonException("Estimate schema v1 artifact deserialized to null."),
                CompactSchemaVersion => Rehydrate(
                    JsonSerializer.Deserialize<EstimateResultArtifact>(json, options)
                    ?? throw new JsonException("Estimate schema v2 artifact deserialized to null.")),
                _ => throw new JsonException($"Unsupported estimate schema_version {schemaVersion}.")
            };
        }

        public static EstimateResult Rehydrate(EstimateResultArtifact artifact)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            if (artifact.SchemaVersion != CompactSchemaVersion)
                throw new JsonException(
                    $"Cannot rehydrate estimate schema_version {artifact.SchemaVersion} as schema v2.");
            if (artifact.Findings is null)
                throw new JsonException("Estimate schema v2 root findings must be an array.");
            if (artifact.Lines is null)
                throw new JsonException("Estimate schema v2 lines must be an array.");

            var canonicalById = new Dictionary<string, DeliveryFinding>(StringComparer.Ordinal);
            foreach (var finding in artifact.Findings)
            {
                if (finding is null || string.IsNullOrWhiteSpace(finding.FindingId))
                    throw new JsonException("Estimate schema v2 contains a root finding without finding_id.");
                if (!canonicalById.TryAdd(finding.FindingId, finding))
                    throw new JsonException(
                        $"Estimate schema v2 contains duplicate root finding_id '{finding.FindingId}'.");
            }

            return new EstimateResult
            {
                RunId = artifact.RunId,
                ProjectProfileId = artifact.ProjectProfileId,
                ProjectProfileHash = artifact.ProjectProfileHash,
                ProjectProfileHashKind = artifact.ProjectProfileHashKind,
                ProjectProfileEffectiveHash = artifact.ProjectProfileEffectiveHash,
                PriceBookId = artifact.PriceBookId,
                PriceBookHash = artifact.PriceBookHash,
                SourceScopePolicy = artifact.SourceScopePolicy,
                SourceSelection = artifact.SourceSelection,
                XrefPolicy = artifact.XrefPolicy,
                ScopeNotice = artifact.ScopeNotice,
                SourceSnapshotKind = artifact.SourceSnapshotKind,
                SourceDrawingPath = artifact.SourceDrawingPath,
                SourceDrawingHash = artifact.SourceDrawingHash,
                SourceDatabaseRevision = artifact.SourceDatabaseRevision,
                SourceDbMod = artifact.SourceDbMod,
                ExternalSources = artifact.ExternalSources ?? new List<EstimateExternalSource>(),
                Exclusions = artifact.Exclusions ?? new List<EstimateExclusion>(),
                Lines = artifact.Lines
                    .Select(line => RehydrateLine(line, canonicalById))
                    .ToList(),
                Findings = artifact.Findings.ToList(),
                CleanTotal = artifact.CleanTotal,
                ExcludedLineCount = artifact.ExcludedLineCount,
                Status = artifact.Status,
            };
        }

        private static int ReadSchemaVersion(JsonElement root)
        {
            if (!root.TryGetProperty("schema_version", out var value))
                return LegacySchemaVersion;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var version))
                throw new JsonException("Estimate schema_version must be an integer.");
            return version;
        }

        private static EstimateLine RehydrateLine(
            EstimateLineArtifact line,
            IReadOnlyDictionary<string, DeliveryFinding> canonicalById)
        {
            if (line is null)
                throw new JsonException("Estimate schema v2 contains a null line.");
            if (line.Findings is null)
                throw new JsonException($"Estimate line '{line.LineId}' findings must be an array.");

            var findings = new List<DeliveryFinding>();
            var findingIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var finding in line.Findings)
            {
                if (finding is null || string.IsNullOrWhiteSpace(finding.FindingId))
                    throw new JsonException(
                        $"Estimate line '{line.LineId}' contains an inline finding without finding_id.");
                if (!findingIds.Add(finding.FindingId))
                    throw new JsonException(
                        $"Estimate line '{line.LineId}' contains duplicate finding_id '{finding.FindingId}'.");
                findings.Add(finding);
            }

            foreach (var findingRef in line.FindingRefs ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(findingRef) ||
                    !canonicalById.TryGetValue(findingRef, out var canonical))
                {
                    throw new JsonException(
                        $"Estimate line '{line.LineId}' references unknown finding_id '{findingRef}'.");
                }
                if (!findingIds.Add(findingRef))
                    throw new JsonException(
                        $"Estimate line '{line.LineId}' contains duplicate finding_id '{findingRef}'.");
                findings.Add(canonical);
            }

            return new EstimateLine
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
                Adjustments = line.Adjustments ?? new List<AdjustmentStep>(),
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
                Findings = findings,
            };
        }
    }
}
