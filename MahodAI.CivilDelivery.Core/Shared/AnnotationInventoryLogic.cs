using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Host-free completeness proof for the dedicated section-annotation layer.
    /// Registry-only verification cannot see an orphan whose handle was removed from
    /// the registry; exact two-way set/cardinality comparison can.
    /// </summary>
    public static class AnnotationInventoryLogic
    {
        public enum OwnershipState
        {
            LegacyAbsent,
            Valid,
            Invalid,
        }

        public sealed record Registered(
            string Handle, string RegistryKey, string Fingerprint);

        public sealed record LayerEntity(
            string Handle, string Fingerprint, OwnershipState Ownership);

        public sealed record Verdict(IReadOnlyList<string> Problems)
        {
            public bool IsValid => Problems.Count == 0;
        }

        public enum HandleState { Live, Missing, Erased, Unreadable }
        public sealed record HandleLookup(string Handle, HandleState State);
        public sealed record RecoveryVerdict(
            IReadOnlyList<Registered> DeadEntries, IReadOnlyList<string> Problems)
        {
            public bool IsValid => Problems.Count == 0;
        }

        public sealed record RepairScopeVerdict(
            IReadOnlyList<string> RequiredRecordIds, IReadOnlyList<string> OutsideTargetRecordIds)
        {
            public bool CanProceed => OutsideTargetRecordIds.Count == 0;
            public bool RequiresBatch => !CanProceed && RequiredRecordIds.Count > 1;
        }

        public static RepairScopeVerdict EvaluateRepairScope(
            IEnumerable<string> requiredRepairRecordIds, IEnumerable<string> targetRecordIds)
        {
            var required = requiredRepairRecordIds.Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
            var targets = targetRecordIds.ToHashSet(StringComparer.Ordinal);
            return new RepairScopeVerdict(required, required.Where(id => !targets.Contains(id)).ToList());
        }

        /// <summary>
        /// Separate from strict verification: only a uniquely registered, SHA-pinned
        /// missing/erased handle for an explicitly eligible target may be pruned.
        /// Absence from a layer is never proof of absence from the database.
        /// </summary>
        public static RecoveryVerdict EvaluateRecovery(
            IEnumerable<Registered> registeredEntries, IEnumerable<LayerEntity> layerEntities,
            IEnumerable<HandleLookup> handleLookups, IEnumerable<string> eligibleRegistryKeys)
        {
            var registered = registeredEntries.ToList();
            var layer = layerEntities.ToList();
            var lookups = handleLookups.GroupBy(x => x.Handle, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var eligible = eligibleRegistryKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var duplicates = registered.GroupBy(x => x.Handle, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() != 1).Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var layerHandles = layer.Select(x => x.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dead = registered.Where(entry =>
                !duplicates.Contains(entry.Handle) && !layerHandles.Contains(entry.Handle) &&
                long.TryParse(entry.Handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var handle) && handle > 0 &&
                eligible.Contains(entry.RegistryKey) && entry.Fingerprint.Length == 64 &&
                entry.Fingerprint.All(Uri.IsHexDigit) &&
                lookups.TryGetValue(entry.Handle, out var matches) && matches.Count == 1 &&
                matches[0].State is HandleState.Missing or HandleState.Erased).ToList();
            var remaining = registered.Except(dead).ToList();
            var strict = Evaluate(remaining, layer);
            // No partial cleanup when another registry/layer problem remains.
            return new RecoveryVerdict(strict.IsValid ? dead : Array.Empty<Registered>(), strict.Problems);
        }

        public static Verdict Evaluate(
            IEnumerable<Registered> registeredEntries,
            IEnumerable<LayerEntity> layerEntities)
        {
            var registered = registeredEntries?.ToList() ??
                throw new ArgumentNullException(nameof(registeredEntries));
            var layer = layerEntities?.ToList() ??
                throw new ArgumentNullException(nameof(layerEntities));
            var problems = new List<string>();

            foreach (var duplicate in registered
                         .GroupBy(item => item.Handle, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() != 1))
                problems.Add($"registered handle {duplicate.Key} appears {duplicate.Count()} times");
            foreach (var duplicate in layer
                         .GroupBy(item => item.Handle, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() != 1))
                problems.Add($"layer handle {duplicate.Key} appears {duplicate.Count()} times");

            var registeredByHandle = registered
                .GroupBy(item => item.Handle, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(),
                    StringComparer.OrdinalIgnoreCase);
            var layerByHandle = layer
                .GroupBy(item => item.Handle, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var entity in layerByHandle.Values)
            {
                if (!registeredByHandle.TryGetValue(entity.Handle, out var proof))
                {
                    problems.Add($"owned layer entity {entity.Handle} is unregistered");
                    continue;
                }
                if (!string.Equals(entity.Fingerprint, proof.Fingerprint,
                        StringComparison.OrdinalIgnoreCase))
                    problems.Add($"owned layer entity {entity.Handle} fingerprint differs");
                if (entity.Ownership == OwnershipState.Invalid)
                    problems.Add($"owned layer entity {entity.Handle} ownership differs");
            }

            foreach (var proof in registeredByHandle.Values.Where(item =>
                         !layerByHandle.ContainsKey(item.Handle)))
                problems.Add($"registered entity {proof.Handle} is absent from the owned layer");

            problems.Sort(StringComparer.Ordinal);
            return new Verdict(problems);
        }
    }
}
