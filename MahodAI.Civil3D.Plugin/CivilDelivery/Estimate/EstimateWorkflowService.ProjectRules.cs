using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using C = MahodAI.CivilDelivery.Estimate.ProjectRuleRecordContext;
using S = MahodAI.CivilDelivery.Estimate.ProjectRuleProposalScope;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// L05 (Codex B520 / ED1EB42C / 0BF95EE5 / 04647DC5): which unmapped groups may reach the generic proposal rankers at all.
/// Only a group the project's quantity rules leave proven-unchanged (GenericUnchanged over its whole case-insensitive scope)
/// is eligible; a group the rules compute, exclude, did not classify, or could not bind with proven lineage gets guidance
/// instead — never a catalog code for its raw measured sum. A project without embedded rules keeps the generic path. The
/// context approves nothing, prices nothing, changes no record and never touches the profile.
/// </summary>
public sealed partial class EstimateWorkflowService
{
    internal enum ProjectRuleContextState { NotApplicable, Bound, Pending, Stale }

    /// <summary>The prepared context: for Bound, the binder's snapshot, the scope captured right after Bind on the same scan
    /// records, and the current stamp of the second, independent observation.</summary>
    internal sealed record ProjectRuleContext(ProjectRuleContextState State, string Reason, C.Snapshot? Snapshot,
        S.Prepared? Scope = null, C.Stamp? Current = null)
    {
        internal static ProjectRuleContext NotApplicable(string reason) => new(ProjectRuleContextState.NotApplicable, reason, null);
        internal static ProjectRuleContext Pending(string reason) => new(ProjectRuleContextState.Pending, reason, null);
        internal static ProjectRuleContext Stale(string reason) => new(ProjectRuleContextState.Stale, reason, null);
    }

    /// <summary>A group that gets no generic proposal, with why and what to do instead (<see cref="Label"/> replaces the
    /// proposal in the catalog column). <see cref="Governed"/> is true for every review: none is eligible.</summary>
    internal sealed record ProjectRuleReview(string RuleKey, string? Layer, int ObjectCount, int BoundCount, int PendingCount,
        int GenericCount, IReadOnlyList<string> Lines, string Message, bool Governed, string Label = RuleLabel, string State = "");

    internal const string RuleLabel = "לפי כללי הפרויקט";
    private const string NoneKey = "(none)";
    private const string PricedByRules = "הכמויות לתמחור בפרויקט זה מחושבות ב'כתב כמויות לפי כללים'.";

    /// <summary>The rules engine run over the active scan and the latest scans of the other drawing roles — the exact
    /// preparation <see cref="ExportBoqRules"/> prices from, without the corridor measurement and without a workbook.</summary>
    private sealed record BoqPlan(BoqRuleset Rules, BoqRulesSourceResolver.ObservedResolution Observed,
        BoqInputSet Input, BoqEngineResult Result);

    private static BoqRulesSourceResolver.ActiveScan ActiveScanOf(ScanResult scan) =>
        new(scan.RunId, scan.SourceDrawing, scan.SourceDrawingHash ?? "", scan.Records, scan.Findings)
        {
            ScannedAtUtc = scan.ScannedAtUtc,
            Units = ScanUnitEvidence.OfScan(scan.PhysicalUnitsContract, scan.PhysicalUnits),
            PhysicalUnitConfigurationHash = scan.PhysicalUnitConfigurationHash,
            HatchDiagnostics = scan.HatchDiagnostics is { Sources.Count: > 0 } diagnostics
                ? System.Text.Json.JsonSerializer.SerializeToElement(diagnostics, SectionsWorkflowService.Json)
                : null,
        };

    private static BoqPlan PrepareBoqPlan(ScanResult scan, BoqRuleset rules, IEnumerable<string>? warningsBeforeRun = null)
    {
        var observed = BoqRulesSourceResolver.ResolveObserved(rules, ActiveScanOf(scan), SectionsWorkflowService.RunsRoot);
        var input = BoqNeutralRecordAdapter.Build(rules, observed.Resolution.Scans);
        input.Warnings.AddRange(observed.Resolution.Notes);
        if (warningsBeforeRun != null) input.Warnings.AddRange(warningsBeforeRun);
        return new BoqPlan(rules, observed, input, BoqRulesEngine.Run(rules, input));
    }

    /// <summary>
    /// Prepares the project-rule context for the scan's proposals. NotApplicable: the project has no embedded quantity
    /// rules (the generic path is unchanged). Pending: a source, receipt or record could not be proven — no group gets a
    /// generic proposal and the reason is shown. Stale: the evidence changed between the two observations (as Pending).
    /// Bound: the binder's guidance per record and the proposal scope captured from it. The current observation is read
    /// again from disk, never copied from the captured one.
    /// </summary>
    internal ProjectRuleContext PrepareProjectRuleContext(ScanResult scan, CatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(snapshot);
        BoqRuleset rules;
        try { rules = BoqRuleset.LoadEmbedded6422(); }
        catch (Exception ex) { return ProjectRuleContext.Pending("כללי הכמויות של הפרויקט לא נטענו: " + ex.Message); }
        if (!string.Equals(scan.ProjectProfileId, rules.Project, StringComparison.Ordinal))
            return ProjectRuleContext.NotApplicable($"לפרויקט {scan.ProjectProfileId} אין כללי כמויות מוטמעים; ההצעות הכלליות לא השתנו.");
        try
        {
            var proof = RequirePublishedScanEvidence(scan);
            if ((BoqNeutralRecordAdapter.LegacyEvidenceRefusal(scan.Records) ?? BoqNeutralRecordAdapter.UnitRefusal(scan.Records) ??
                 BoqNeutralRecordAdapter.ScanUnitRefusal(ScanUnitEvidence.OfScan(scan.PhysicalUnitsContract, scan.PhysicalUnits), scan.Records)) is { } refusal)
                return ProjectRuleContext.Pending(refusal);
            var plan = PrepareBoqPlan(scan, rules);
            if (plan.Observed.Receipt is not { } receipt || plan.Observed.PendingReasons.Count > 0)
                return ProjectRuleContext.Pending(PendingText(plan.Observed));
            var captured = Stamp(scan, snapshot, rules, proof.Hash, plan.Observed, receipt, C.OutcomeDigest(plan.Result), out var sources);
            var current = CurrentStamp(scan, snapshot, rules, captured.OutcomeSha256);
            if (current == null)
                return ProjectRuleContext.Stale("מקורות הכללים השתנו בזמן ההכנה; יש לסרוק מחדש.");
            var bound = C.Bind(new C.Request(captured, current, sources, scan.Records, plan.Result));
            if (bound.Records.Count > 0 && bound.Records.All(r => r.ReasonCode == "stale_context"))
                return ProjectRuleContext.Stale("מקורות הכללים השתנו בזמן ההכנה; יש לסרוק מחדש.");
            // The scope is captured immediately after Bind, on the same complete records (04647: Capture).
            return new ProjectRuleContext(ProjectRuleContextState.Bound, "", bound, S.Capture(bound, scan.Records), current);
        }
        catch (Exception ex)
        {
            return ProjectRuleContext.Pending("הקשר כללי הפרויקט לא הוכן: " + ex.Message);
        }
    }

    /// <summary>Observes the published scan and every source again and rebuilds the stamp (null when a source can no longer
    /// be proven). <paramref name="outcomeSha256"/> is the captured result's digest: the result object itself is not rerun.</summary>
    private static C.Stamp? CurrentStamp(ScanResult scan, CatalogSnapshot snapshot, BoqRuleset rules, string outcomeSha256)
    {
        var proof = RequirePublishedScanEvidence(scan);
        var observed = BoqRulesSourceResolver.ResolveObserved(rules, ActiveScanOf(scan), SectionsWorkflowService.RunsRoot);
        if (observed.Receipt is not { } receipt || observed.PendingReasons.Count > 0) return null;
        return Stamp(scan, snapshot, rules, proof.Hash, observed, receipt, outcomeSha256, out _);
    }

    private static C.Stamp Stamp(ScanResult scan, CatalogSnapshot snapshot, BoqRuleset rules, string scanSha256,
        BoqRulesSourceResolver.ObservedResolution observed, BoqSourceResolutionReceipt.Receipt receipt, string outcomeSha256,
        out List<C.Source> sources)
    {
        sources = observed.Resolution.Scans.Select(s =>
        {
            var read = observed.Sources.Single(o => o.Role == s.Role && o.RunId == s.RunId);
            return new C.Source(new C.SourceIdentity(s.Role, s.RunId, s.DrawingPath, s.DrawingHash, read.RecordsSha256,
                C.RecordDigest(s.Records), read.ManifestSha256, C.AuxiliaryDigest(s)), s);
        }).ToList();
        return new C.Stamp(rules.Project, scan.RunId, scan.SourceDrawing, scan.SourceDrawingHash ?? "", scanSha256,
            scan.ProjectProfileHash ?? "", scan.ProjectProfileEffectiveHash ?? "", rules.Sha256, snapshot.SnapshotId,
            snapshot.FileHash, C.SourceSetDigest(sources.Select(x => x.Identity)), receipt.Sha256, outcomeSha256);
    }

    /// <summary>
    /// Re-proves the context right before proposals are published (also after an AI read transaction): a Bound context is
    /// observed again from disk and its scope evaluated against that real current stamp and the scan's records; every
    /// published proposal must still belong to an eligible group. A Pending/Stale context may publish no proposal at all.
    /// </summary>
    internal static void RequireCurrentProjectRuleContext(ScanResult scan, CatalogSnapshot snapshot, ProjectRuleContext context,
        IReadOnlyList<MappingProposal> proposals)
    {
        if (context.State == ProjectRuleContextState.NotApplicable) return;
        if (context.State != ProjectRuleContextState.Bound || context.Snapshot is not { } bound || context.Scope is not { } scope)
        {
            if (proposals.Count > 0)
                throw new InvalidOperationException("הקשר כללי הפרויקט לא אומת ובכל זאת הוכנו הצעות כלליות — לא פורסמו.");
            return;
        }
        var rules = BoqRuleset.LoadEmbedded6422();
        var current = CurrentStamp(scan, snapshot, rules, bound.Stamp.OutcomeSha256);
        if (current == null || current != bound.Stamp)
            throw new InvalidOperationException("הקשר כללי הפרויקט התיישן לפני הפרסום — מקור, פרופיל, כללים או מחירון השתנו. יש לסרוק מחדש.");
        var evaluation = S.Evaluate(scope, current, scan.Records);
        if (!evaluation.ContextMatches)
            throw new InvalidOperationException("הקשר כללי הפרויקט התיישן לפני הפרסום — " + evaluation.ContextMessage);
        var eligible = EligibleKeys(evaluation);
        if (proposals.Any(p => !eligible.Contains(p.RuleKey)))
            throw new InvalidOperationException("הוכנה הצעה כללית לקבוצה שכללי הפרויקט אינם משאירים במסלול הכללי — לא פורסמה.");
    }

    /// <summary>
    /// Why the generic mapping assistant may not propose a code for <paramref name="ruleKey"/>, or null when it may (Codex
    /// 23:59). The gate is the same 04647 scope as the rankers, over the whole case-insensitive closure of the key (a mapped
    /// representative cannot open a group whose sibling has unproven lineage), on a context re-proven now: a Bound context is
    /// observed again from disk and evaluated against the scan's records. Call it before the assistant and again after it
    /// returns. NotApplicable keeps the assistant; no context, Pending or Stale refuses.
    /// </summary>
    internal static string? GenericAssistRefusal(ScanResult scan, CatalogSnapshot snapshot, ProjectRuleContext? context, string ruleKey)
    {
        if (context == null)
            return "הקשר כללי הכמויות של הפרויקט לא הוכן לסריקה זו — העזרה החכמה אינה מציעה סעיף. ניתן לבחור סעיף ידנית.";
        if (context.State == ProjectRuleContextState.NotApplicable) return null;
        if (context.State != ProjectRuleContextState.Bound || context.Snapshot is not { } bound || context.Scope is not { } scope)
            return string.IsNullOrWhiteSpace(context.Reason) ? "הקשר כללי הכמויות של הפרויקט לא אומת." : context.Reason;
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(snapshot);
        var current = CurrentStamp(scan, snapshot, BoqRuleset.LoadEmbedded6422(), bound.Stamp.OutcomeSha256);
        if (current == null || current != bound.Stamp)
            return "הקשר כללי הפרויקט התיישן — מקור, פרופיל, כללים או מחירון השתנו. יש לסרוק מחדש לפני הצעה.";
        return ClosureAssistRefusal(S.Evaluate(scope, current, scan.Records), ruleKey);
    }

    /// <summary>The closure part of <see cref="GenericAssistRefusal"/>: every exact key equal to <paramref name="ruleKey"/>
    /// ignoring case must be GenericUnchanged over its full scope, and at least one must still have an unmapped member.</summary>
    internal static string? ClosureAssistRefusal(S.Evaluation evaluation, string ruleKey)
    {
        if (!evaluation.ContextMatches) return evaluation.ContextMessage ?? "הקשר כללי הפרויקט התיישן.";
        var closure = evaluation.Groups.Where(g => string.Equals(g.RuleKey ?? NoneKey, ruleKey, StringComparison.OrdinalIgnoreCase)).ToList();
        if (closure.Count > 0 && closure.All(g => g.State == S.Disposition.GenericUnchanged) && closure.Any(g => g.GenericProposalEligible))
            return null;
        var reason = closure.Select(g => g.Message).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)) ?? "לא נמצאה הנחיית כללים לקבוצה.";
        return "לפי כללי הפרויקט העזרה החכמה אינה מציעה סעיף לסכום הגולמי של קבוצה זו: " + reason;
    }

    /// <summary>Proposal-group keys (null RuleKey shown as "(none)") whose every scope group is eligible.</summary>
    private static HashSet<string> EligibleKeys(S.Evaluation evaluation) =>
        evaluation.Groups.GroupBy(g => g.RuleKey ?? NoneKey, StringComparer.Ordinal)
            .Where(g => g.All(x => x.GenericProposalEligible)).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

    private static string PendingText(BoqRulesSourceResolver.ObservedResolution observed)
    {
        var reasons = observed.PendingReasons.Select(r => r.Split(':')[0] switch
        {
            "missing_role" or "rejected_role" => $"אין סריקה תקפה לקובץ {r.Split(':').ElementAtOrDefault(1)}",
            "inventory_unreadable_run" => "תיקיית ריצה לא נקראה",
            "active_drawing_changed" or "active_drawing_unavailable" => "השרטוט הפעיל השתנה או אינו נגיש מאז הסריקה",
            "active_published_records_differ" => "הרשומות שפורסמו אינן זהות לסריקה הפעילה",
            "active_manifest_unreadable" => "מניפסט הסריקה הפעילה לא נקרא",
            _ => "ראיות המקור אינן שלמות",
        }).Distinct().ToList();
        return "לא הוצעו סעיפים כלליים: הקשר כללי הכמויות של הפרויקט לא אומת — " + string.Join("; ", reasons) +
               ". " + PricedByRules + " אפשר עדיין לבחור סעיף ידנית ('בחר סעיף').";
    }

    /// <summary>
    /// The guidance for every unmapped proposal group that is not eligible for the generic rankers (04647 Evaluate over the
    /// complete records, the context's current stamp). NotApplicable: none. Pending/Stale: every group, with the reason.
    /// </summary>
    internal static IReadOnlyList<ProjectRuleReview> ProjectRuleReviews(IReadOnlyList<NeutralQuantityRecord> records, ProjectRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(context);
        if (context.State == ProjectRuleContextState.NotApplicable) return Array.Empty<ProjectRuleReview>();
        var unmapped = records.Where(r => string.IsNullOrWhiteSpace(r.Classification.CandidateCatalogCode))
            .GroupBy(r => r.Classification.RuleKey ?? NoneKey).ToList();
        ProjectRuleReview Unverified(IGrouping<string, NeutralQuantityRecord> group, string reason) =>
            new(group.Key, group.First().Source.Layer, group.Count(), 0, group.Count(), 0, Array.Empty<string>(),
                reason, true, "לא הוצע — הקשר לא אומת", "context_unverified");
        if (context.State != ProjectRuleContextState.Bound || context.Snapshot is not { } bound || context.Scope is not { } scope ||
            context.Current is not { } current)
        {
            var reason = string.IsNullOrWhiteSpace(context.Reason) ? "הקשר כללי הכמויות של הפרויקט לא אומת." : context.Reason;
            return unmapped.Select(g => Unverified(g, reason)).ToList();
        }
        var evaluation = S.Evaluate(scope, current, records);
        if (!evaluation.ContextMatches)
            return unmapped.Select(g => Unverified(g, evaluation.ContextMessage ?? "הקשר כללי הפרויקט התיישן.")).ToList();
        var byKey = evaluation.Groups.ToLookup(g => g.RuleKey ?? NoneKey, StringComparer.Ordinal);
        var byId = bound.Records.ToDictionary(g => g.RecordId, StringComparer.Ordinal);
        var reviews = new List<ProjectRuleReview>();
        foreach (var group in unmapped)
        {
            var scopes = byKey[group.Key].ToList();
            var scoped = scopes.Count == 1 ? scopes[0] : null;
            if (scoped?.GenericProposalEligible == true) continue;
            var guidance = group.Select(r => byId.GetValueOrDefault(r.RecordId)).ToList();
            var rule = guidance.Where(g => g?.State == C.State.RuleReview).Select(g => g!).ToList();
            var excluded = guidance.Where(g => g?.State == C.State.ExcludedReview).Select(g => g!).ToList();
            var unclassified = guidance.Count(g => g?.State == C.State.UnclassifiedReview);
            var pending = guidance.Where(g => g == null || g.State == C.State.PendingLineage).ToList();
            var generic = guidance.Count(g => g?.State == C.State.GenericUnchanged);
            var lines = rule.Select(g => $"{g.LineId} · {g.RuleCatalogCode} ({g.RuleUnit})").Distinct(StringComparer.Ordinal).ToList();
            var linesText = lines.Count == 0 ? "" : $" ({string.Join("; ", lines.Take(3).Select(Bidi.Ltr))})";
            string message, label;
            switch (scoped?.State)
            {
                case S.Disposition.RuleReview:
                    label = RuleLabel;
                    message = $"לפי כללי הפרויקט הקבוצה משתתפת בחישוב כמות פיזית{linesText}. " +
                              "הסכום שנמדד כאן אינו הכמות לתמחור — הכמות מחושבת ב'כתב כמויות לפי כללים'.";
                    break;
                case S.Disposition.RecordReview when excluded.Count > 0:
                    label = "לא נספר לפי הכללים";
                    message = "לפי כללי הפרויקט העצמים האלה אינם נספרים בכמות: " +
                              string.Join("; ", excluded.Select(g => g.Message).Distinct().Take(2)) + ". הסכום שנמדד כאן אינו כמות לתמחור.";
                    break;
                case S.Disposition.RecordReview:
                    label = "לא סווג בכללים";
                    message = "כללי הפרויקט לא סיווגו את העצמים האלה — הם מופיעים בגיליון 'לא סווג' של כתב הכמויות לפי כללים. " +
                              "לא הוצע סעיף לסכום הגולמי; אם הם נדרשים בכמויות, השיוך הוא החלטה הנדסית מפורשת.";
                    break;
                case S.Disposition.MixedReview:
                    label = "לבדיקה — כללים שונים";
                    message = $"בקבוצה יש תוצאות כללים, יחידות או חלקים שונים{linesText}; אין הצעה משותפת לסכום הגולמי. " + PricedByRules;
                    break;
                case S.Disposition.PendingContext when scoped.ReasonCode == "pending_lineage":
                    label = "לא הוצע — הצמדה לא מוכחת";
                    message = $"ל-{pending.Count} מתוך {guidance.Count} עצמים אין הצמדה מוכחת לחישוב הכללים (למשל עצמים מקובץ מופנה XREF " +
                              $"או רשומה שאינה חד-חד-ערכית){linesText}. לא הוצע סעיף לסכום הגולמי. " + PricedByRules;
                    break;
                default:
                    label = "לא הוצע — לבדיקה";
                    message = (scoped?.Message ?? "לא נמצאה הנחיית כללים לקבוצה.") + " " + PricedByRules;
                    break;
            }
            reviews.Add(new ProjectRuleReview(group.Key, group.First().Source.Layer, guidance.Count, rule.Count + excluded.Count,
                pending.Count, generic + unclassified, lines, message, true, label, scoped?.ReasonCode ?? "no_scope_group"));
        }
        return reviews;
    }
}
