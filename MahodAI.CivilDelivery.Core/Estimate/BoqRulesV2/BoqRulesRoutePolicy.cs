using System;
using System.Linq;

namespace MahodAI.CivilDelivery.Estimate.BoqRulesV2;

/// <summary>
/// Presentation applicability only. A matching project id does not make every
/// drawing a source of that project's rules. Reuse the resolver's source-role
/// filename contract; do not infer Civil materials or suppress standard review.
/// Freshness, source evidence and export gates remain owned by the workflow.
/// </summary>
public static class BoqRulesRoutePolicy
{
    public static bool AppliesTo(BoqRuleset rules, string? profileId, string? scanDrawingPath)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return !string.IsNullOrWhiteSpace(profileId) &&
            string.Equals(profileId, rules.Project, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(scanDrawingPath) &&
            rules.SourceRoles.Count(role => role.Matches(scanDrawingPath)) == 1;
    }
}
