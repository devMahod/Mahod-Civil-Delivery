using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>Fail-closed authority boundary for measured-work exclusions.</summary>
    public static class IgnoredRulePolicy
    {
        public static bool IsApproved(
            ProjectProfile.EstimateProfile.IgnoredRuleDecision? decision) =>
            decision != null &&
            !string.IsNullOrWhiteSpace(decision.RuleKey) &&
            !string.IsNullOrWhiteSpace(decision.Reason) &&
            !string.IsNullOrWhiteSpace(decision.ApprovedBy) &&
            decision.ApprovedAtUtc != null;

        public static IReadOnlyDictionary<string, ProjectProfile.EstimateProfile.IgnoredRuleDecision>
            ApprovedDecisions(ProjectProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            return profile.Estimate.IgnoredRuleDecisions
                .Where(IsApproved)
                .GroupBy(d => d.RuleKey!.Trim(), StringComparer.Ordinal)
                // Duplicate approvals are deterministic: latest decision wins, but
                // callers can still audit every persisted entry in the profile.
                .ToDictionary(g => g.Key,
                    g => g.OrderByDescending(d => d.ApprovedAtUtc).First(),
                    StringComparer.Ordinal);
        }

        public static IReadOnlyCollection<string> ApprovedKeys(ProjectProfile profile) =>
            ApprovedDecisions(profile).Keys.ToList();
    }
}
