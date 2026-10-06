using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Autodesk-free identity/cardinality contract between a section PLAN and its APPLY
/// evidence. An exclusion is authoritative only when APPLY contains the exact row;
/// PLAN alone must never make an omitted decision green during VERIFY.
/// </summary>
public static class SectionRecordEvidenceLogic
{
    public sealed record Identity(string RecordId, string? LogicalKey);

    public sealed record Verdict(
        bool IsExact,
        IReadOnlyList<string> Missing,
        IReadOnlyList<string> Extra,
        IReadOnlyList<string> DuplicatePlanIds,
        IReadOnlyList<string> DuplicateApplyIds,
        IReadOnlyList<string> LogicalKeyMismatches);

    public static Verdict Compare(
        IEnumerable<Identity>? planRows,
        IEnumerable<Identity>? applyRows)
    {
        var plan = (planRows ?? Array.Empty<Identity>()).ToList();
        var apply = (applyRows ?? Array.Empty<Identity>()).ToList();
        var duplicatePlan = DuplicateIds(plan);
        var duplicateApply = DuplicateIds(apply);

        var planMap = UniqueMap(plan);
        var applyMap = UniqueMap(apply);
        var missing = planMap.Keys.Except(applyMap.Keys, StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal).ToList();
        var extra = applyMap.Keys.Except(planMap.Keys, StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal).ToList();
        var mismatches = planMap.Keys.Intersect(applyMap.Keys, StringComparer.Ordinal)
            .Where(id => !string.Equals(
                planMap[id].LogicalKey, applyMap[id].LogicalKey, StringComparison.Ordinal))
            .OrderBy(value => value, StringComparer.Ordinal).ToList();

        var invalidIds = plan.Concat(apply)
            .Where(row => string.IsNullOrWhiteSpace(row.RecordId))
            .Any();
        var exact = !invalidIds && plan.Count > 0 &&
                    duplicatePlan.Count == 0 && duplicateApply.Count == 0 &&
                    missing.Count == 0 && extra.Count == 0 && mismatches.Count == 0 &&
                    plan.Count == apply.Count;
        return new Verdict(
            exact, missing, extra, duplicatePlan, duplicateApply, mismatches);
    }

    private static List<string> DuplicateIds(IEnumerable<Identity> rows) => rows
        .GroupBy(row => row.RecordId ?? string.Empty, StringComparer.Ordinal)
        .Where(group => group.Count() != 1)
        .Select(group => group.Key)
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToList();

    private static Dictionary<string, Identity> UniqueMap(IEnumerable<Identity> rows) => rows
        .Where(row => !string.IsNullOrWhiteSpace(row.RecordId))
        .GroupBy(row => row.RecordId, StringComparer.Ordinal)
        .Where(group => group.Count() == 1)
        .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
}
