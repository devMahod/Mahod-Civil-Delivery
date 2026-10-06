using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

public sealed partial class EstimateWorkflowService
{
    /// <summary>
    /// An explicit human choice, not an automatically accepted proposal. Measurement
    /// scope is deliberately absent: it is derived from the original scan, never
    /// trusted from editable dialog fields. A closed sibling exclusion is independent
    /// consent and must name its exact measured alternative.
    /// </summary>
    public sealed record ReviewedMappingChoice(
        string RuleKey, string CatalogCode, string? ExcludedAlternativeRuleKey = null);

    /// <summary>
    /// Saves the entire manually reviewed selection in one existing atomic profile
    /// write. This route does not loosen ProvenBatchMappingCandidates, approve a
    /// proposal automatically, require a price, or dismiss source/geometry findings.
    /// The host must retain the original document/profile/scan/catalog decision scope
    /// across its modal and call RequireFreshForDecision immediately before saving.
    /// </summary>
    public ProjectProfileWriter.SaveResult SaveReviewedMappings(
        ProjectProfile profile,
        CatalogSnapshot snapshot,
        ScanResult scan,
        IReadOnlyList<ReviewedMappingChoice> choices,
        string approvedBy,
        string targetPath,
        ProjectProfileWriter.ExpectedProfileState expectedProfileState)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(expectedProfileState);
        RequireProfileWriteTarget(targetPath);
        if (choices.Count == 0)
            throw new ArgumentException("No reviewed mappings were selected.", nameof(choices));
        if (string.IsNullOrWhiteSpace(approvedBy))
            throw new ArgumentException("An explicit batch approver is required.", nameof(approvedBy));
        if (scan.ProfileWriteState == null || scan.ProfileWriteState != expectedProfileState)
            throw new InvalidOperationException(
                "The reviewed mappings require the original scan's profile CAS evidence.");
        if (!string.Equals(scan.ProjectProfileId, profile.ProfileId, StringComparison.Ordinal) ||
            !string.Equals(scan.ProjectProfileEffectiveHash,
                EstimateTraceIdentity.EffectiveProfileHash(profile), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The reviewed mappings do not belong to the current project-profile evidence.");
        ProjectProfileWriter.RequireExpectedStateUnchanged(profile, expectedProfileState);

        if (choices.Any(choice => choice == null || string.IsNullOrWhiteSpace(choice.RuleKey) ||
                                  string.IsNullOrWhiteSpace(choice.CatalogCode)))
            throw new InvalidOperationException("Every reviewed choice requires a rule key and catalog code.");
        if (choices.GroupBy(choice => choice.RuleKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() != 1))
            throw new InvalidOperationException("The reviewed mapping batch contains duplicate rule keys.");

        // Index once for a large drawing; never rescan all measured rows per choice.
        var groups = ManualMappingCaseScope.Collect(scan.Records, profile)
            .ToDictionary(group => group.RuleKey, StringComparer.OrdinalIgnoreCase);
        var pairs = ClosedPolylineAlternativePolicy.FindUnambiguousExactPairs(scan.Records);
        var alternatives = pairs.SelectMany(pair => new[]
            {
                (Selected: pair.FirstRuleKey, Alternative: pair.SecondRuleKey),
                (Selected: pair.SecondRuleKey, Alternative: pair.FirstRuleKey),
            }).ToDictionary(pair => pair.Selected, pair => pair.Alternative, StringComparer.Ordinal);
        var selectedKeys = choices.Select(choice => choice.RuleKey).ToHashSet(StringComparer.Ordinal);
        var approvals = new List<MappingApproval>();
        var exclusions = new List<ClosedPolylineAlternativeExclusion>();
        foreach (var choice in choices)
        {
            if (!groups.TryGetValue(choice.RuleKey, out var scope) ||
                !scope.RuleKeys.Contains(choice.RuleKey, StringComparer.Ordinal) || scope.Records.Count == 0)
                throw new InvalidOperationException($"Reviewed rule '{choice.RuleKey}' is absent from the scan.");
            if (scope.Refusal != null) throw new InvalidOperationException(scope.Refusal);
            // The editor shows this complete scope, not just the case-sensitive row.
            var records = scope.Records;
            if (records.Any(record => !string.Equals(record.ProjectProfileId, scan.ProjectProfileId,
                    StringComparison.Ordinal)))
                throw new InvalidOperationException($"Reviewed rule '{choice.RuleKey}' contains another project's records.");
            if (scope.RuleKeys.Any(key => ApprovedIgnoredRuleDecision(profile, key) != null))
                throw new InvalidOperationException(
                    $"Return excluded rule '{choice.RuleKey}' to the estimate before mapping it.");
            // A case-only duplicate existing rule would otherwise be appended by the
            // historical writer and leave competing approved rules after a rescan.
            var existingRules = profile.Estimate.QuantitySources.Rules.Where(rule =>
                string.Equals(rule.RuleKey, choice.RuleKey, StringComparison.OrdinalIgnoreCase)).ToList();
            if (existingRules.Count > 1 || (existingRules.Count == 1 &&
                !string.Equals(existingRules[0].RuleKey, choice.RuleKey, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Reviewed rule '{choice.RuleKey}' is not one exact profile rule identity.");

            var layers = records.Select(record => SectionProjectionLogic.LayerLeaf(record.Source.Layer))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var kinds = records.Select(record => record.Measurement.Kind)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var entityTypes = records.Select(record => record.Source.EntityType)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (layers.Count != 1 || string.IsNullOrWhiteSpace(layers[0]) ||
                layers[0].IndexOfAny(new[] { '*', '?' }) >= 0 ||
                kinds.Count != 1 || string.IsNullOrWhiteSpace(kinds[0]) ||
                entityTypes.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException(
                    $"Reviewed rule '{choice.RuleKey}' does not have one exact layer and measurement-kind scope.");
            // Discovery classifies an exact rule key independently of entity type.
            // However, presentation-layer exceptions use an explicit native type
            // predicate. A mixed-type marker must not erase that narrower contract.
            if (entityTypes.Count > 1 && CivilQuantityExtractionService.IsCivilPresentationLayer(layers[0]))
                throw new InvalidOperationException(
                    $"Reviewed presentation rule '{choice.RuleKey}' has mixed entity types; retain its explicit individual type scope.");
            var code = choice.CatalogCode.Trim().ToUpperInvariant();
            if (!snapshot.Items.TryGetValue(code, out var item))
                throw new InvalidOperationException($"Catalog code '{code}' does not exist in the reviewed catalog.");
            if (records.Any(record => !Units.Parse(record.Measurement.Unit).SameUnit(item.Unit)))
                throw new InvalidOperationException(
                    $"Unit mismatch in reviewed rule '{choice.RuleKey}'; every measured record must match '{code}'.");

            if (alternatives.TryGetValue(choice.RuleKey, out var alternative))
            {
                if (!string.Equals(choice.ExcludedAlternativeRuleKey, alternative, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Reviewed closed-polyline rule '{choice.RuleKey}' requires explicit exclusion of exact alternative '{alternative}'.");
                if (selectedKeys.Contains(alternative))
                    throw new InvalidOperationException("Both closed-polyline siblings cannot be selected in one review.");
                exclusions.Add(new ClosedPolylineAlternativeExclusion(choice.RuleKey, alternative));
            }
            else if (!string.IsNullOrWhiteSpace(choice.ExcludedAlternativeRuleKey))
                throw new InvalidOperationException(
                    $"Rule '{choice.RuleKey}' has no unique exact whole-group closed-polyline sibling to exclude.");

            // '*' records a mixed discovery group; it is not a new permission for
            // the native presentation-layer exception predicate.
            approvals.Add(new MappingApproval(choice.RuleKey, item.Code, layers[0],
                entityTypes.Count == 1 ? entityTypes[0] : "*", kinds[0], records[0].Measurement.Unit));
        }

        // Only exclusions proven above from the complete exact source sets may be
        // treated as withdrawn dimensions. Arbitrary UI keys never waive overlap.
        RequireNoReviewedClosedSourceOverlap(profile, scan, selectedKeys,
            exclusions.Select(exclusion => exclusion.AlternativeRuleKey).ToHashSet(StringComparer.Ordinal));

        // The existing writer validates catalog identity and every choice before its
        // single replace; failures restore rules AND sibling decisions. The caller
        // then uses PublishProfileDecisionOrRestore so publication failure withdraws
        // the exact just-saved profile rather than leaving untraceable approvals.
        return SaveMappingsWithClosedPolylineExclusionsCore(
            profile, snapshot, scan, approvals, exclusions, approvedBy, targetPath, expectedProfileState,
            allowReplacingMappedAlternative: true);
    }

    private static void RequireNoReviewedClosedSourceOverlap(
        ProjectProfile profile, ScanResult scan, IReadOnlySet<string> selectedKeys,
        IReadOnlySet<string> validatedAlternativeExclusions)
    {
        // A closed object can share its length rule with unrelated OPEN objects.
        // Such groups are correctly not exact whole-group siblings: excluding the
        // length rule would also discard the open objects. Nevertheless, approving
        // both dimensions of the common closed object is still double meaning, even
        // if the other side was approved in an earlier review. Index only recorded
        // closed-source identities, preserving drawing/hash/insertion-chain/handle
        // identity exactly as ClosedPolylineAlternativePolicy does.
        var ignoredKeys = IgnoredRulePolicy.ApprovedKeys(profile).ToHashSet(StringComparer.Ordinal);
        ignoredKeys.UnionWith(validatedAlternativeExclusions);
        var mappedKeys = scan.Records.Where(record => !string.IsNullOrWhiteSpace(record.Classification.RuleKey))
            .GroupBy(record => record.Classification.RuleKey!, StringComparer.Ordinal)
            .Where(group => !selectedKeys.Contains(group.Key) && !ignoredKeys.Contains(group.Key) &&
                            group.Any(record => HasApprovedCatalogMapping(profile, record)))
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var sources = new Dictionary<string, Dictionary<string, (string RuleKey, bool Selected)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in scan.Records)
        {
            var key = record.Classification.RuleKey;
            if (key == null || ignoredKeys.Contains(key) ||
                (!selectedKeys.Contains(key) && !mappedKeys.Contains(key)) ||
                !ClosedPolylineAlternativePolicy.IsClosedPolylineMeasurement(record))
                continue;
            var selected = selectedKeys.Contains(key);
            var source = string.Join("\u001f", record.Source.DrawingPath ?? record.Source.Drawing,
                record.Source.DrawingHash, record.Source.Xref ?? string.Empty,
                record.Source.Handle, record.Source.Layer ?? string.Empty);
            if (!sources.TryGetValue(source, out var dimensions))
                sources.Add(source, dimensions = new Dictionary<string, (string RuleKey, bool Selected)>(StringComparer.OrdinalIgnoreCase));
            foreach (var previous in dimensions)
                if (!string.Equals(previous.Key, record.Measurement.Kind, StringComparison.OrdinalIgnoreCase) &&
                    (selected || previous.Value.Selected))
                    throw new InvalidOperationException(
                        $"Reviewed rules '{previous.Value.RuleKey}' and '{key}' include both area and perimeter of closed source '{record.Source.Handle}'. " +
                        "Resolve that measured alternative without excluding unrelated open objects; no mapping was saved.");
            // Retain both previously mapped dimensions, and preserve a selected
            // occurrence when a same-kind source repeats before its opposite side.
            if (!dimensions.TryGetValue(record.Measurement.Kind, out var sameKind) || selected)
                dimensions[record.Measurement.Kind] = (key, selected || sameKind.Selected);
        }
    }
}
