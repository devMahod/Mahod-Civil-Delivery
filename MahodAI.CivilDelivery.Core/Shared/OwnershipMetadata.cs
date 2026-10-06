using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Ownership/provenance metadata persisted on every tool-created Civil object
    /// (ExtensionDictionary/Xrecord as authoritative store, plan §7.12). Names are
    /// never the identity — the logical key is.
    /// </summary>
    public sealed class OwnershipMetadata
    {
        public const string Owner = "MahodCivilDelivery";
        public const string DictionaryKey = "MAHOD_CIVIL_DELIVERY";
        public const int SchemaVersion = 1;

        public required string Feature { get; init; }          // "sections" | "estimate"
        public required string Role { get; init; }             // e.g. "sample-line-group" | "sample-line" | "section-view"
        public required string ProjectProfileId { get; init; }
        public required string RunId { get; init; }
        public string? SourceClDrawingHash { get; init; }
        public string? SourceClHandle { get; init; }
        public required string LogicalKey { get; init; }
        public required string InputFingerprint { get; init; }
        public required string CreatedByToolVersion { get; init; }
        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

        /// <summary>Flat string-pair representation, storage-mechanism agnostic (Xrecord/JSON).</summary>
        public IReadOnlyList<KeyValuePair<string, string>> ToPairs() => new[]
        {
            new KeyValuePair<string, string>("schema_version", SchemaVersion.ToString()),
            new KeyValuePair<string, string>("owner", Owner),
            new KeyValuePair<string, string>("feature", Feature),
            new KeyValuePair<string, string>("role", Role),
            new KeyValuePair<string, string>("project_profile_id", ProjectProfileId),
            new KeyValuePair<string, string>("run_id", RunId),
            new KeyValuePair<string, string>("source_cl_drawing_hash", SourceClDrawingHash ?? string.Empty),
            new KeyValuePair<string, string>("source_cl_handle", SourceClHandle ?? string.Empty),
            new KeyValuePair<string, string>("logical_key", LogicalKey),
            new KeyValuePair<string, string>("input_fingerprint", InputFingerprint),
            new KeyValuePair<string, string>("created_by_tool_version", CreatedByToolVersion),
            new KeyValuePair<string, string>("created_at_utc", CreatedAtUtc.ToString("O")),
        };

        public static OwnershipMetadata? FromPairs(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            var map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            if (!map.TryGetValue("owner", out var owner) || owner != Owner) return null;

            // The dictionary key itself is an authority boundary: once an object says
            // it is ours, incomplete/corrupt provenance must never be downgraded to
            // "unowned".  Callers use null to mean foreign/manual and are consequently
            // allowed to leave or adopt the object.  Throw for every malformed OUR
            // record so PLAN/APPLY/VERIFY fail closed instead.
            var requiredKeys = new[]
            {
                "schema_version", "owner", "feature", "role", "project_profile_id",
                "run_id", "source_cl_drawing_hash", "source_cl_handle", "logical_key",
                "input_fingerprint", "created_by_tool_version", "created_at_utc",
            };
            var missing = requiredKeys.Where(key => !map.ContainsKey(key)).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException(
                    "Tool ownership metadata is missing required fields: " +
                    string.Join(", ", missing));
            if (map.Count != requiredKeys.Length ||
                map.Keys.Any(key => !requiredKeys.Contains(key, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "Tool ownership metadata contains unexpected fields for schema 1.");
            if (!int.TryParse(map["schema_version"], out var schema) ||
                schema != SchemaVersion)
                throw new InvalidOperationException(
                    $"Unsupported tool ownership schema '{map["schema_version"]}'.");

            foreach (var key in new[]
                     {
                         "feature", "role", "project_profile_id", "run_id", "logical_key",
                         "input_fingerprint", "created_by_tool_version",
                     })
            {
                if (string.IsNullOrWhiteSpace(map[key]))
                    throw new InvalidOperationException(
                        $"Tool ownership metadata field '{key}' is empty.");
            }

            if (!DateTime.TryParse(map["created_at_utc"],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var createdAt) ||
                createdAt.Kind != DateTimeKind.Utc)
                throw new InvalidOperationException(
                    "Tool ownership metadata created_at_utc is not a valid UTC round-trip timestamp.");

            return new OwnershipMetadata
            {
                Feature = map["feature"],
                Role = map["role"],
                ProjectProfileId = map["project_profile_id"],
                RunId = map["run_id"],
                SourceClDrawingHash = NullIfEmpty(map.GetValueOrDefault("source_cl_drawing_hash")),
                SourceClHandle = NullIfEmpty(map.GetValueOrDefault("source_cl_handle")),
                LogicalKey = map["logical_key"],
                InputFingerprint = map["input_fingerprint"],
                CreatedByToolVersion = map["created_by_tool_version"],
                CreatedAtUtc = createdAt,
            };
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>
    /// Stable logical keys and input fingerprints (plan §7.12). Same inputs → same
    /// key on every rerun; the key survives renames and reruns because it is built
    /// only from source identity, never from generated object names.
    /// </summary>
    public static class LogicalKeys
    {
        /// <summary>
        /// profile + stable source entity handle + selected alignment + generated
        /// object role. The CL file content hash is deliberately NOT object identity:
        /// saving the same DWG changes its bytes, and v1 consequently treated every
        /// existing section as a different object. The hash remains in the input
        /// fingerprint and provenance so a changed/re-saved source still triggers an
        /// UPDATE without creating a duplicate identity.
        /// </summary>
        public static string ForSectionObject(
            string projectProfileId,
            string clDrawingHash,
            string clHandle,
            string alignmentName,
            string role)
        {
            _ = clDrawingHash; // retained in the signature for source/binary compatibility with v1 callers
            var canonical = string.Join("|",
                "v2",
                projectProfileId?.Trim() ?? string.Empty,
                (clHandle ?? string.Empty).ToUpperInvariant(),
                alignmentName?.Trim() ?? string.Empty,
                role?.Trim().ToLowerInvariant() ?? string.Empty);
            return $"MCD:{ArtifactHash.Short(ArtifactHash.Sha256OfText(canonical))}";
        }

        /// <summary>
        /// Deterministic fingerprint over the engineering inputs that, when changed,
        /// require an UPDATE/REPLACE of the generated object. Serialized with sorted
        /// keys and invariant formatting so it is stable across runs and cultures.
        /// </summary>
        public static string Fingerprint(IReadOnlyDictionary<string, object?> inputs)
        {
            var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var kv in inputs) sorted[kv.Key] = Canonicalize(kv.Value);
            var json = JsonSerializer.Serialize(sorted);
            return ArtifactHash.Sha256OfText(json);
        }

        private static object? Canonicalize(object? value) => value switch
        {
            null => null,
            double d => Math.Round(d, 6).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            float f => Math.Round(f, 6).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DateTime dt => dt.ToUniversalTime().ToString("O"),
            IEnumerable<object?> seq => seq.Select(Canonicalize).ToArray(),
            _ => value.ToString(),
        };
    }
}
