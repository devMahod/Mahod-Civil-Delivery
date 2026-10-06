using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// Money-affecting configuration must have one authority, never list-order
    /// semantics. This validator is called by both profile loading and Build so a
    /// caller cannot bypass it by constructing a ProjectProfile in memory.
    /// </summary>
    public static class EstimateConfigurationPolicy
    {
        public static IReadOnlyList<DeliveryFinding> Validate(ProjectProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            var findings = new List<DeliveryFinding>();

            void Add(string title, string message) => findings.Add(new DeliveryFinding
            {
                Code = EstimateFindingCodes.ConfigurationAmbiguous,
                Domain = "estimate",
                Severity = FindingSeverity.Error,
                Title = title,
                Message = message,
                ProjectProfileId = profile.ProfileId,
            });

            var rules = profile.Estimate.QuantitySources.Rules ?? new();
            foreach (var duplicate in rules
                         .Where(r => !string.IsNullOrWhiteSpace(r.RuleKey))
                         .GroupBy(r => r.RuleKey!.Trim(), StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
            {
                Add($"מפתח חוק כמות '{duplicate.Key}' הוגדר יותר מפעם אחת",
                    "Competing quantity mappings are never resolved by YAML/list order. Keep one rule key and one approval authority.");
            }

            var overrides = profile.Estimate.ProjectOverrides ?? new();
            foreach (var duplicate in overrides
                         .Where(o => !string.IsNullOrWhiteSpace(o.ItemCode))
                         .GroupBy(o => o.ItemCode!.Trim(), StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
            {
                Add($"לסעיף '{duplicate.Key}' הוגדר יותר ממחיר פרויקט אחד",
                    "A catalog item may have one approved project price only; list order is not price authority.");
            }

            var adjustments = profile.Estimate.ApprovedAdjustments ?? new();
            foreach (var duplicate in adjustments
                         .Where(a => !string.IsNullOrWhiteSpace(a.RuleId))
                         .GroupBy(a => a.RuleId!.Trim(), StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
            {
                Add($"מזהה המקדם '{duplicate.Key}' הוגדר יותר מפעם אחת",
                    "A duplicated approved adjustment would multiply the same authority twice and is blocked.");
            }

            foreach (var duplicate in adjustments
                         .Where(a => !string.IsNullOrWhiteSpace(a.Scope))
                         .GroupBy(a => (a.Order, Scope: a.Scope!.Trim()), new AdjustmentSlotComparer())
                         .Where(g => g.Count() > 1))
            {
                Add($"יותר ממקדם אחד תופס את סדר {duplicate.Key.Order} וההיקף '{duplicate.Key.Scope}'",
                    "Give every approved adjustment an explicit unique order within its scope.");
            }

            return findings;
        }

        public static bool IsConflictedAdjustment(
            ProjectProfile.EstimateProfile.AdjustmentRule candidate,
            IReadOnlyList<ProjectProfile.EstimateProfile.AdjustmentRule> all)
        {
            if (string.IsNullOrWhiteSpace(candidate.RuleId)) return true;
            var duplicateId = all.Count(a => string.Equals(
                a.RuleId?.Trim(), candidate.RuleId.Trim(), StringComparison.OrdinalIgnoreCase)) > 1;
            var duplicateSlot = !string.IsNullOrWhiteSpace(candidate.Scope) && all.Count(a =>
                a.Order == candidate.Order && string.Equals(
                    a.Scope?.Trim(), candidate.Scope.Trim(), StringComparison.OrdinalIgnoreCase)) > 1;
            return duplicateId || duplicateSlot;
        }

        private sealed class AdjustmentSlotComparer : IEqualityComparer<(int Order, string Scope)>
        {
            public bool Equals((int Order, string Scope) x, (int Order, string Scope) y) =>
                x.Order == y.Order && string.Equals(x.Scope, y.Scope, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((int Order, string Scope) obj) =>
                HashCode.Combine(obj.Order, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Scope));
        }
    }
}
