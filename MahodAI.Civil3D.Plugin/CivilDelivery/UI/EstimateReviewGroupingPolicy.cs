using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// Presentation-only index over captured issues. Grouping never changes findings,
/// quantities, approvals or export gates; every original issue stays available.
/// </summary>
internal static class EstimateReviewGroupingPolicy
{
    internal sealed class GroupSummary
    {
        internal GroupSummary(string key, string code, EstimateReviewPolicy.Issue first,
            string[] rules, string[] dimensions, List<EstimateReviewPolicy.Issue> issues)
        {
            Key = key;
            Stage = first.Stage;
            Code = code;
            RuleKeys = Array.AsReadOnly(rules);
            MeasurementDimensions = Array.AsReadOnly(dimensions);
            // Copy the list, not its evidence: potentially large source arrays stay shared.
            Issues = Array.AsReadOnly(issues.ToArray());
            BlockingCount = issues.Count(issue => issue.Blocking);
            var action = first.Action switch
            {
                EstimateReviewPolicy.Recovery.Mapping => "בדיקת שיוך",
                EstimateReviewPolicy.Recovery.Price => "בדיקת מחיר",
                EstimateReviewPolicy.Recovery.Sources => "בדיקת מקור",
                EstimateReviewPolicy.Recovery.Catalog => "בדיקת מחירון",
                _ => "בדיקת ממצא",
            };
            var target = rules.Length == 1 ? Short(rules[0], 110) :
                rules.Length > 1 ? $"{rules.Length:N0} קבוצות מדידה" : "ממצא כללי / מקור";
            var dimension = dimensions.Length == 1 ? Short(dimensions[0], 60) :
                dimensions.Length > 1 ? "מספר ממדי מדידה" : "ללא מדידה מזוהה";
            Caption = $"{action} · {target} · {dimension} · {Count:N0} ממצאים · {Short(Code, 70)}";
        }

        public string Key { get; }
        public string Stage { get; }
        public string Code { get; }
        public IReadOnlyList<string> RuleKeys { get; }
        public IReadOnlyList<string> MeasurementDimensions { get; }
        public int Count => Issues.Count;
        public int BlockingCount { get; }
        public IReadOnlyList<EstimateReviewPolicy.Issue> Issues { get; }
        public string Caption { get; }
    }

    private sealed record MeasurementIdentity(string RuleKey, string Kind, string Unit, string Dimension)
    {
        internal string Display => $"{Kind} / {Unit} ({Dimension})";
    }

    internal static IReadOnlyList<GroupSummary> Group(
        IReadOnlyList<EstimateReviewPolicy.Issue> issues,
        IEnumerable<NeutralQuantityRecord> records)
    {
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(records);
        // Build once: no per-issue scan over the 79k-record collection.
        var byId = new Dictionary<string, MeasurementIdentity>(StringComparer.Ordinal);
        var ambiguousIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var unit = Units.Parse(record.Measurement.Unit);
            var identity = new MeasurementIdentity(record.Classification.RuleKey ?? "",
                record.Measurement.Kind.Trim().ToLowerInvariant(),
                unit.Canonical == "?" ? "? " + record.Measurement.Unit : unit.Canonical,
                unit.Dimension.ToString());
            if (!byId.TryAdd(record.RecordId, identity) && byId[record.RecordId] != identity)
                ambiguousIds.Add(record.RecordId);
        }

        var indexed = new Dictionary<string, (EstimateReviewPolicy.Issue First, string Code, string[] Rules,
            string[] Dimensions, List<EstimateReviewPolicy.Issue> Issues)>(StringComparer.Ordinal);
        var keysInOrder = new List<string>();
        for (var index = 0; index < issues.Count; index++)
        {
            var issue = issues[index];
            var rules = issue.RuleKeys.Where(rule => !string.IsNullOrWhiteSpace(rule))
                .Distinct(StringComparer.Ordinal).OrderBy(rule => rule, StringComparer.Ordinal).ToArray();
            var ruleSet = rules.ToHashSet(StringComparer.Ordinal);
            var dimensions = new HashSet<string>(StringComparer.Ordinal);
            var identities = new HashSet<MeasurementIdentity>();
            var known = rules.Length > 0 && issue.RecordIds.Count > 0;
            foreach (var id in issue.RecordIds)
            {
                if (!byId.TryGetValue(id, out var identity) || ambiguousIds.Contains(id))
                {
                    known = false;
                    continue;
                }
                identities.Add(identity);
                dimensions.Add(identity.Display);
                if (!ruleSet.Contains(identity.RuleKey) || identity.Unit.StartsWith("?", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(identity.Kind)) known = false;
            }
            var orderedDimensions = dimensions.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            // The identity tuple retains the rule-to-dimension association (not just
            // two independent sets that could accidentally merge swapped dimensions).
            var orderedIdentities = identities.OrderBy(value => value.RuleKey, StringComparer.Ordinal)
                .ThenBy(value => value.Kind, StringComparer.Ordinal).ThenBy(value => value.Unit, StringComparer.Ordinal).ToArray();
            var code = CanonicalGroupCode(issue, known);
            // Global/no-record and ambiguous issues are deliberately independent:
            // e.g. two failed source files must not become one presumed root cause.
            var key = JsonSerializer.Serialize(new
            {
                issue.Stage, Code = code, Rules = rules, Identities = orderedIdentities,
                issue.Blocking, issue.Action, UnresolvedIdentity = known ? -1 : index,
            });
            if (!indexed.TryGetValue(key, out var group))
            {
                group = (issue, code, rules, orderedDimensions, new List<EstimateReviewPolicy.Issue>());
                indexed.Add(key, group);
                keysInOrder.Add(key);
            }
            group.Issues.Add(issue);
        }
        return Array.AsReadOnly(keysInOrder.Select(key =>
        {
            var group = indexed[key];
            return new GroupSummary(key, group.Code, group.First, group.Rules, group.Dimensions, group.Issues);
        }).ToArray());
    }

    private static string CanonicalGroupCode(EstimateReviewPolicy.Issue issue, bool known)
    {
        // Collect binds raw export reasons to exactly one emitted record. Normalize
        // presentation only, and only the actual builder's L0001... identity syntax;
        // detail retains the untouched line:<id>:<reason> code for audit/location.
        if (!known || issue.Stage != "ייצוא" || issue.RecordIds.Count != 1 ||
            issue.RuleKeys.Count != 1 || !issue.Code.StartsWith("line:", StringComparison.Ordinal))
            return issue.Code;
        var separator = issue.Code.IndexOf(':', 5);
        if (separator <= 5 || separator == issue.Code.Length - 1) return issue.Code;
        var lineId = issue.Code[5..separator];
        if (lineId.Length < 5 || lineId[0] != 'L' ||
            !lineId.Skip(1).All(character => character is >= '0' and <= '9')) return issue.Code;
        return issue.Code[(separator + 1)..];
    }

    private static string Short(string value, int maximum) => value.Length <= maximum
        ? value : value[..maximum] + "…";
}
