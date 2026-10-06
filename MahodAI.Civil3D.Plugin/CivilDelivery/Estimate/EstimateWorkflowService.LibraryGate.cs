using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

public sealed partial class EstimateWorkflowService
{
    internal const string LibraryNoItemLabel = "לפי הספרייה: להחלטה, ללא סעיף";
    internal const string LibraryMixedLabel = "לפי הספרייה: קבוצה מעורבת";

    /// <summary>
    /// b19 (live b18 finding 2, Codex 03:31): the active library's disposition of every unmapped proposal group, read from
    /// the engineer draft built with the same library, records and price list (LibraryProposalGate). Writes nothing.
    /// </summary>
    internal static IReadOnlyDictionary<string, LibraryProposalDisposition> LibraryDispositions(
        ScanResult scan, CatalogSnapshot snapshot, ProjectProfile profile)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(profile);
        RequireTrustedUnitEvidence(scan, "שער ספריית הסעיפים");
        var draft = EngineerBoqDraftBuilder.Build(scan.Records, scan.Findings, snapshot, new Dictionary<string, string>(),
            EngineerBoqLibrary.For(profile), DraftContext(profile, scan, snapshot));
        return LibraryProposalGate.Evaluate(draft, scan.Records);
    }

    /// <summary>The visible review of each group the library holds: no heuristic, curated or assistant proposal.</summary>
    internal static IReadOnlyList<ProjectRuleReview> LibraryReviews(
        IReadOnlyDictionary<string, LibraryProposalDisposition> dispositions, IEnumerable<MappingProposalEngine.DiscoveredGroup> groups) =>
        groups.Where(group => dispositions.ContainsKey(group.RuleKey))
            .Select(group =>
            {
                var disposition = dispositions[group.RuleKey];
                var mixed = disposition.State == LibraryProposalState.Mixed;
                return new ProjectRuleReview(group.RuleKey, group.Layer, group.ObjectCount, 0, 0, 0, disposition.Elements,
                    disposition.Message, true, mixed ? LibraryMixedLabel : LibraryNoItemLabel,
                    mixed ? "library_mixed" : "library_no_item");
            })
            .ToList();

    /// <summary>
    /// The complete review map of a scan, as published with its proposals: the L05 reviews, then the library's reviews for
    /// the unmapped groups L05 leaves open. Used again after a decision rebases the scan (Codex 04:04): a group mapped by
    /// that decision is no longer an unmapped group and loses its review; a held group keeps its reason.
    /// </summary>
    internal static IReadOnlyList<ProjectRuleReview> ReviewsWithLibrary(
        ScanResult scan, CatalogSnapshot snapshot, ProjectProfile profile, ProjectRuleContext context)
    {
        var reviews = ProjectRuleReviews(scan.Records, context);
        var governed = reviews.Where(r => r.Governed).Select(r => r.RuleKey).ToHashSet(StringComparer.Ordinal);
        var open = BuildMappingProposalGroups(scan.Records, EngineerBoqLibrary.For(profile), profile.Estimate.FamilyDecisions)
            .Where(g => !governed.Contains(g.RuleKey)).ToList();
        if (open.Count == 0) return reviews;
        var library = LibraryReviews(LibraryDispositions(scan, snapshot, profile), open);
        return library.Count == 0 ? reviews : reviews.Concat(library).ToList();
    }

    /// <summary>True for a review the library gives (not an L05 project-rules review).</summary>
    internal static bool IsLibraryReview(ProjectRuleReview review) => review.State.StartsWith("library_", StringComparison.Ordinal);

    /// <summary>Refuses a batch whose candidates include a group the library holds now (re-read before the durable decision).</summary>
    internal static void RequireNoLibraryHeldCandidates(
        IReadOnlyDictionary<string, LibraryProposalDisposition> dispositions, IEnumerable<string> ruleKeys)
    {
        var held = ruleKeys.Select(key => (Key: key, Refusal: LibraryProposalGate.Refusal(dispositions, key)))
            .Where(x => x.Refusal != null).ToList();
        if (held.Count > 0)
            throw new InvalidOperationException(
                $"{held.Count} מההצעות שייכות כעת לרכיב שהספרייה מגדירה להחלטה ללא סעיף — לא נשמר אף מיפוי. " + held[0].Refusal);
    }
}
