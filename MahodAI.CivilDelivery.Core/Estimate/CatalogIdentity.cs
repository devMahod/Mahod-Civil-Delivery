using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// One fail-closed identity contract for price books and mapping approvals.
    /// A filename or friendly edition is not identity: every participating field must
    /// name the same registered snapshot and the same 64-hex SHA-256.
    /// </summary>
    public static class CatalogIdentity
    {
        private static readonly Regex Sha256Pattern =
            new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public sealed record ActiveProfileIdentity(
            string SnapshotId,
            string FileHash,
            string CatalogFile,
            ProjectProfile.EstimateProfile.PriceBookEntry RegistryEntry);

        public static bool IsValidSha256(string? value) =>
            value != null && Sha256Pattern.IsMatch(value.Trim());

        /// <summary>Deterministic identity of the item the engineer actually saw.</summary>
        public static string ItemFingerprint(CatalogItem item)
        {
            ArgumentNullException.ThrowIfNull(item);
            var canonical = string.Join("\n", new[]
            {
                item.Code.Trim().ToUpperInvariant(),
                NormalizeText(item.Description),
                Units.Parse(item.UnitRaw).Canonical,
                NormalizeText(item.UnitRaw),
            });
            return ArtifactHash.Sha256OfText(canonical);
        }

        public static IReadOnlyList<string> ValidateActiveProfile(ProjectProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            var errors = new List<string>();
            var catalog = profile.Estimate.Catalog;
            var pricing = profile.Estimate.Pricing;
            var snapshotId = pricing?.PriceBookSnapshotId?.Trim();
            var catalogFile = catalog.CatalogFile?.Trim();
            var catalogHash = catalog.CatalogFileHash?.Trim();
            var pricingHash = pricing?.PriceBookHash?.Trim();

            if (string.IsNullOrWhiteSpace(snapshotId))
                errors.Add("pricing.price_book_snapshot_id is missing");
            if (string.IsNullOrWhiteSpace(catalogFile))
                errors.Add("catalog.catalog_file is missing");
            ValidateHash("catalog.catalog_file_hash", catalogHash, errors);
            ValidateHash("pricing.price_book_hash", pricingHash, errors);

            var matches = string.IsNullOrWhiteSpace(snapshotId)
                ? new List<ProjectProfile.EstimateProfile.PriceBookEntry>()
                : profile.Estimate.PriceBooks.Where(e => string.Equals(
                    e.Id?.Trim(), snapshotId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
            {
                errors.Add(matches.Count == 0
                    ? $"active registry entry '{snapshotId ?? "?"}' is missing"
                    : $"active registry id '{snapshotId}' is duplicated ({matches.Count} entries)");
                return errors;
            }

            var entry = matches[0];
            var entryFile = entry.File?.Trim();
            var entryHash = entry.FileHash?.Trim();
            if (string.IsNullOrWhiteSpace(entry.Id) ||
                !string.Equals(entry.Id.Trim(), snapshotId, StringComparison.OrdinalIgnoreCase))
                errors.Add("active registry id does not equal pricing snapshot id");
            if (string.IsNullOrWhiteSpace(entryFile))
                errors.Add("active registry file is missing");
            ValidateHash("active registry file_hash", entryHash, errors);

            if (!string.IsNullOrWhiteSpace(catalogFile) &&
                !string.IsNullOrWhiteSpace(entryFile) &&
                !SameLogicalFile(catalogFile, entryFile))
                errors.Add($"catalog file '{catalogFile}' does not equal active registry file '{entryFile}'");

            var hashes = new[] { catalogHash, pricingHash, entryHash }
                .Where(IsValidSha256)
                .Select(h => h!.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (hashes.Count > 1)
                errors.Add("catalog, pricing and active registry hashes do not match");
            return errors;
        }

        public static bool TryGetActiveProfileIdentity(
            ProjectProfile profile,
            out ActiveProfileIdentity? identity,
            out IReadOnlyList<string> errors)
        {
            errors = ValidateActiveProfile(profile);
            if (errors.Count != 0)
            {
                identity = null;
                return false;
            }

            var snapshotId = profile.Estimate.Pricing.PriceBookSnapshotId!.Trim();
            var entry = profile.Estimate.PriceBooks.Single(e => string.Equals(
                e.Id?.Trim(), snapshotId, StringComparison.OrdinalIgnoreCase));
            identity = new ActiveProfileIdentity(
                snapshotId,
                profile.Estimate.Catalog.CatalogFileHash!.Trim().ToLowerInvariant(),
                profile.Estimate.Catalog.CatalogFile!.Trim(),
                entry);
            return true;
        }

        public static bool SnapshotMatches(ActiveProfileIdentity identity, CatalogSnapshot snapshot) =>
            string.Equals(identity.SnapshotId, snapshot.SnapshotId, StringComparison.OrdinalIgnoreCase) &&
            IsValidSha256(snapshot.FileHash) &&
            string.Equals(identity.FileHash, snapshot.FileHash, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A price record is authority only when it names the exact immutable catalog
        /// snapshot that contains it. Dictionary membership and a positive decimal are
        /// not enough: a mixed/corrupt snapshot must never price a line.
        /// </summary>
        public static bool PriceRecordMatchesSnapshot(
            CatalogSnapshot snapshot, string catalogCode, PriceRecord priceRecord)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(priceRecord);
            return priceRecord.Price is > 0 &&
                   string.Equals(priceRecord.Code?.Trim(), catalogCode?.Trim(),
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(priceRecord.PriceBookId?.Trim(), snapshot.SnapshotId?.Trim(),
                       StringComparison.OrdinalIgnoreCase) &&
                   IsValidSha256(priceRecord.SourceHash) &&
                   string.Equals(priceRecord.SourceHash?.Trim(), snapshot.FileHash?.Trim(),
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reconciles every exportable line back to the freshly loaded catalog bytes.
        /// The build artifact is immutable evidence, but it is not permission to reuse
        /// a stale/tampered decimal: export must prove item, price, id and SHA again.
        /// Project overrides retain their profile-bound price authority, while their
        /// underlying catalog mapping is still checked against the active snapshot.
        /// </summary>
        public static IReadOnlyList<string> ValidateEstimatePricing(
            EstimateResult estimate, CatalogSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(estimate);
            ArgumentNullException.ThrowIfNull(snapshot);
            var errors = new List<string>();

            if (!string.Equals(estimate.PriceBookId, snapshot.SnapshotId,
                    StringComparison.OrdinalIgnoreCase) ||
                !IsValidSha256(estimate.PriceBookHash) ||
                !IsValidSha256(snapshot.FileHash) ||
                !string.Equals(estimate.PriceBookHash, snapshot.FileHash,
                    StringComparison.OrdinalIgnoreCase))
                errors.Add("estimate catalog id/hash does not equal the verified snapshot");

            foreach (var line in estimate.Lines.Where(line =>
                         line.PriceStatus is PriceStatus.Priced or PriceStatus.ProjectOverride))
            {
                var prefix = $"line {line.LineId}";
                var code = line.CatalogCode?.Trim();
                if (string.IsNullOrWhiteSpace(code) ||
                    !snapshot.Items.TryGetValue(code, out var item))
                {
                    errors.Add($"{prefix}: catalog item is absent from the verified snapshot");
                    continue;
                }

                if (!string.Equals(line.ApprovedCatalogId, snapshot.SnapshotId,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(line.ApprovedCatalogHash, snapshot.FileHash,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(line.ApprovedCatalogItemFingerprint,
                        ItemFingerprint(item), StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{prefix}: approved catalog item binding is not current");

                if (line.PriceStatus != PriceStatus.Priced) continue;

                if (!snapshot.Prices.TryGetValue(code, out var priceRecord) ||
                    !PriceRecordMatchesSnapshot(snapshot, item.Code, priceRecord))
                {
                    errors.Add($"{prefix}: verified snapshot has no matching authoritative price record");
                    continue;
                }

                if (line.Price != priceRecord.Price)
                    errors.Add($"{prefix}: exported price differs from the verified snapshot");
                if (!string.Equals(line.PriceBookId, snapshot.SnapshotId,
                        StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{prefix}: price-book id differs from the verified snapshot");
                if (!string.Equals(line.PriceDecisionSource, snapshot.FileHash,
                        StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{prefix}: price source hash differs from the verified snapshot");
            }

            return errors.Distinct(StringComparer.Ordinal).ToList();
        }

        public static bool IsApprovalCurrent(
            ProjectProfile profile,
            ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule rule)
        {
            if (rule == null ||
                string.IsNullOrWhiteSpace(rule.CandidateCatalogCode) ||
                string.IsNullOrWhiteSpace(rule.ApprovedBy) ||
                rule.ApprovedAtUtc == null ||
                string.IsNullOrWhiteSpace(rule.ApprovedCatalogId) ||
                !IsValidSha256(rule.ApprovedCatalogHash) ||
                !IsValidSha256(rule.ApprovedCatalogItemFingerprint))
                return false;

            return TryGetActiveProfileIdentity(profile, out var active, out _) &&
                   active != null &&
                   string.Equals(rule.ApprovedCatalogId.Trim(), active.SnapshotId,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(rule.ApprovedCatalogHash!.Trim(), active.FileHash,
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The neutral record carries the exact approval identity used at extraction.
        /// This stops a stale scan (or a legacy producer that supplied only a code)
        /// from being priced against a different snapshot at the build boundary.
        /// </summary>
        public static bool IsClassificationCurrent(
            QuantityClassification classification, CatalogSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(classification);
            ArgumentNullException.ThrowIfNull(snapshot);
            if (string.IsNullOrWhiteSpace(classification.CandidateCatalogCode) ||
                string.IsNullOrWhiteSpace(classification.ApprovedCatalogId) ||
                string.IsNullOrWhiteSpace(classification.MappingApprovedBy) ||
                classification.MappingApprovedAtUtc == null ||
                !IsValidSha256(classification.ApprovedCatalogHash) ||
                !IsValidSha256(classification.ApprovedCatalogItemFingerprint) ||
                !string.Equals(classification.ApprovedCatalogId.Trim(), snapshot.SnapshotId,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(classification.ApprovedCatalogHash!.Trim(), snapshot.FileHash,
                    StringComparison.OrdinalIgnoreCase) ||
                !snapshot.Items.TryGetValue(classification.CandidateCatalogCode, out var item))
                return false;

            return string.Equals(classification.ApprovedCatalogItemFingerprint!.Trim(),
                ItemFingerprint(item), StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateHash(string field, string? value, List<string> errors)
        {
            if (!IsValidSha256(value))
                errors.Add($"{field} must be a 64-hex SHA-256");
        }

        private static bool SameLogicalFile(string first, string second)
        {
            static string Normalize(string value)
            {
                var normalized = value.Trim().Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                while (normalized.StartsWith("." + Path.DirectorySeparatorChar,
                           StringComparison.Ordinal))
                    normalized = normalized[2..];
                return normalized.TrimEnd(Path.DirectorySeparatorChar);
            }

            return string.Equals(Normalize(first), Normalize(second),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeText(string? value) =>
            string.Join(" ", (value ?? string.Empty)
                .Replace("\r", " ").Replace("\n", " ").Replace("\t", " ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
