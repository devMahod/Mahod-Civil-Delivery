using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using Xunit;
using C = MahodAI.CivilDelivery.Estimate.ProjectRuleRecordContext;
using W = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.EstimateWorkflowService;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// L05 consumer (04647 scope gate): only a group the project rules leave proven-unchanged reaches the generic rankers. A
/// group the rules compute, exclude or did not classify, a group with unproven lineage (also through a case sibling) and
/// every group of an unverified or stale context get guidance instead of a raw-sum proposal. Nothing here approves, prices
/// or writes. (Producer/receipt: BoqRulesV2ResolveObservedTests; scope adapter: ProjectRuleProposalScopeTests.)
/// </summary>
public sealed class ProjectRuleProposalContextTests
{
    private static NeutralQuantityRecord Record(string id, string layer, string? mapped = null, string? key = null) => new()
    {
        RecordId = id, ProjectProfileId = "6422", RunId = "run",
        Source = new QuantitySource { Drawing = "d", DrawingHash = new string('a', 64), Handle = id, EntityType = "LINE", Layer = layer },
        Measurement = new QuantityMeasurement { Kind = "length", Method = "line-length", RawValue = 5, Unit = "מטר" },
        Classification = new QuantityClassification { RuleKey = key ?? $"layer:{layer}|length", CandidateCatalogCode = mapped },
    };

    private static readonly C.Stamp Captured = new("6422", "run", "d", new string('a', 64), new string('b', 64), new string('c', 64),
        new string('d', 64), new string('e', 64), "snap", new string('f', 64), new string('1', 64), new string('2', 64), new string('3', 64));

    /// <summary>A Bound context exactly as Prepare builds it: guidance per record, then the scope captured on the same records.</summary>
    private static W.ProjectRuleContext Bound(IReadOnlyList<NeutralQuantityRecord> records, Func<NeutralQuantityRecord, C.State> state,
        C.Stamp? current = null, string message = "m")
    {
        var guidance = records.Select(r =>
        {
            var s = state(r);
            return new C.Guidance(r.RecordId, r.Classification.RuleKey, r.Measurement.RawValue, r.Measurement.Unit,
                r.Classification.CandidateCatalogCode, s, s.ToString(), message, Role: "SM",
                LineId: s == C.State.RuleReview ? "C1" : null, PartIndex: s == C.State.RuleReview ? 0 : null,
                RuleCatalogCode: s == C.State.RuleReview ? "51.06.0010" : null, RuleUnit: s == C.State.RuleReview ? "מ'" : null,
                PartKind: s == C.State.RuleReview ? "length" : null);
        }).ToList();
        var snapshot = new C.Snapshot(Captured, guidance, "digest");
        return new W.ProjectRuleContext(W.ProjectRuleContextState.Bound, "", snapshot,
            ProjectRuleProposalScope.Capture(snapshot, records), current ?? Captured);
    }

    [Fact]
    public void AGroupTheRulesComputeGetsTheRuleLineInsteadOfAProposal()
    {
        var records = new[] { Record("a1", "HW-CURB"), Record("a2", "HW-CURB") };
        var review = W.ProjectRuleReviews(records, Bound(records, _ => C.State.RuleReview)).Should().ContainSingle().Subject;
        review.Governed.Should().BeTrue();
        review.Label.Should().Be(W.RuleLabel);
        review.BoundCount.Should().Be(2);
        review.Lines.Should().ContainSingle().Which.Should().Contain("51.06.0010");
        review.Message.Should().Contain("לפי כללי הפרויקט").And.Contain("אינו הכמות לתמחור");
    }

    [Fact]
    public void ExcludedObjectsSayWhyTheyAreNotCounted()
    {
        var records = new[] { Record("b1", "TR-MARK-WHT-811") };
        var review = W.ProjectRuleReviews(records, Bound(records, _ => C.State.ExcludedReview, message: "בקרה בלבד")).Single();
        review.Governed.Should().BeTrue();
        review.Label.Should().Be("לא נספר לפי הכללים");
        review.Message.Should().Contain("אינם נספרים").And.Contain("בקרה בלבד");
    }

    [Fact]
    public void UnclassifiedObjectsPointToTheUnclassifiedSheetWithoutAProposal()
    {
        var records = new[] { Record("u1", "pcell") };
        var review = W.ProjectRuleReviews(records, Bound(records, _ => C.State.UnclassifiedReview)).Single();
        review.Governed.Should().BeTrue();
        review.Label.Should().Be("לא סווג בכללים");
        review.Message.Should().Contain("לא סווג").And.Contain("החלטה הנדסית מפורשת");
    }

    [Fact]
    public void APendingOnlyGroupGetsNoGenericProposal()
    {
        // Codex 23:25 (BABB60AB §1): unproven lineage is not a note next to a proposal — it is not eligible at all.
        var records = new[] { Record("c1", "X"), Record("c2", "X") };
        var review = W.ProjectRuleReviews(records, Bound(records, _ => C.State.PendingLineage)).Single();
        review.Governed.Should().BeTrue();
        review.Label.Should().Be("לא הוצע — הצמדה לא מוכחת");
        review.State.Should().Be("pending_lineage");
        review.Message.Should().Contain("XREF").And.Contain("ל-2 מתוך 2");
    }

    [Fact]
    public void AMixedGroupWithAPendingMemberStaysPending()
    {
        var records = new[] { Record("d1", "M"), Record("d2", "M"), Record("d3", "M") };
        var states = new Dictionary<string, C.State> { ["d1"] = C.State.RuleReview, ["d2"] = C.State.PendingLineage, ["d3"] = C.State.GenericUnchanged };
        var review = W.ProjectRuleReviews(records, Bound(records, r => states[r.RecordId])).Single();
        review.Governed.Should().BeTrue();
        (review.BoundCount, review.PendingCount, review.GenericCount).Should().Be((1, 1, 1));
        review.Message.Should().Contain("ל-1 מתוך 3").And.Contain("51.06.0010");
    }

    [Fact]
    public void OnlyAProvenGenericGroupStaysOnTheGenericPath()
    {
        // The useful positive: a consistent GenericUnchanged group gets no review, so it reaches the rankers as before.
        var generic = new[] { Record("e1", "G"), Record("e2", "G") };
        W.ProjectRuleReviews(generic, Bound(generic, _ => C.State.GenericUnchanged)).Should().BeEmpty();
        // A group the engineer already mapped is not a proposal group.
        var mapped = new[] { Record("f1", "H", mapped: "51.06.0010") };
        W.ProjectRuleReviews(mapped, Bound(mapped, _ => C.State.RuleReview)).Should().BeEmpty();
        // A project without embedded rules keeps the generic path.
        W.ProjectRuleReviews(generic, W.ProjectRuleContext.NotApplicable("r")).Should().BeEmpty();
    }

    [Fact]
    public void ACaseSiblingWithUnprovenLineageClosesTheGenericGroupToo()
    {
        var records = new[] { Record("g1", "A", key: "layer:A|length"), Record("g2", "a", key: "LAYER:A|length") };
        var reviews = W.ProjectRuleReviews(records,
            Bound(records, r => r.RecordId == "g1" ? C.State.GenericUnchanged : C.State.PendingLineage));
        reviews.Select(r => r.RuleKey).Should().BeEquivalentTo("layer:A|length", "LAYER:A|length");
        reviews.Should().OnlyContain(r => r.Governed);
    }

    [Fact]
    public void AnUnverifiedOrStaleContextGivesEveryGroupAReviewAndNoProposal()
    {
        var records = new[] { Record("h1", "G"), Record("h2", "K"), Record("h3", "K", mapped: "51.06.0010") };
        foreach (var context in new[] { W.ProjectRuleContext.Pending("סיבה"), W.ProjectRuleContext.Stale("סיבה") })
        {
            var reviews = W.ProjectRuleReviews(records, context);
            reviews.Select(r => r.RuleKey).Should().BeEquivalentTo("layer:G|length", "layer:K|length");
            reviews.Should().OnlyContain(r => r.Governed && r.Label == "לא הוצע — הקשר לא אומת" && r.Message == "סיבה");
        }
        // Bound but observed again with a different stamp: the captured guidance is not trusted.
        var stale = Bound(records, _ => C.State.GenericUnchanged, current: Captured with { CatalogSha256 = new string('9', 64) });
        W.ProjectRuleReviews(records, stale).Should().HaveCount(2).And.OnlyContain(r => r.Label == "לא הוצע — הקשר לא אומת");
        // ...and a record that changed after the capture.
        var fresh = Bound(records, _ => C.State.GenericUnchanged);
        var changed = records.Select(r => r.RecordId == "h1" ? Record("h1", "G2") : r).ToArray();
        W.ProjectRuleReviews(changed, fresh).Should().OnlyContain(r => r.Label == "לא הוצע — הקשר לא אומת");
    }

    [Fact]
    public void AnUnverifiedContextNeverPublishesAProposal()
    {
        var proposal = new MappingProposal { RuleKey = "layer:G|length", MeasurementKind = "length", MeasuredUnit = "מטר", ProposedCode = "51.06.0025" };
        var act = () => W.RequireCurrentProjectRuleContext(null!, null!, W.ProjectRuleContext.Pending("r"), new[] { proposal });
        act.Should().Throw<InvalidOperationException>();
        W.RequireCurrentProjectRuleContext(null!, null!, W.ProjectRuleContext.Pending("r"), Array.Empty<MappingProposal>());
        W.RequireCurrentProjectRuleContext(null!, null!, W.ProjectRuleContext.NotApplicable("r"), new[] { proposal });
    }

    [Fact]
    public void TheAssistantSeesTheWholeCaseClosureNotTheMappedRepresentative()
    {
        // Codex 23:59: r1 (mapped) represents the closure in the review window; r2 (other case, unmapped) has unproven lineage.
        var records = new[]
        {
            Record("r1", "HW-CURB", mapped: "51.06.0010", key: "layer:HW-CURB|length"),
            Record("r2", "HW-CURB", key: "LAYER:HW-CURB|length"),
        };
        var context = Bound(records, r => r.RecordId == "r1" ? C.State.GenericUnchanged : C.State.PendingLineage);
        var evaluation = ProjectRuleProposalScope.Evaluate(context.Scope!, context.Current!, records);
        W.ProjectRuleReviews(records, context).Select(r => r.RuleKey).Should().Equal("LAYER:HW-CURB|length");
        W.ClosureAssistRefusal(evaluation, "layer:HW-CURB|length").Should().NotBeNull("the review dictionary has no entry for the mapped key");
        W.ClosureAssistRefusal(evaluation, "LAYER:HW-CURB|length").Should().NotBeNull();

        // A proven-generic closure with an unmapped member keeps the assistant, also when asked through a mapped sibling.
        var generic = new[] { Record("g1", "G", mapped: "51.06.0010", key: "layer:G|length"), Record("g2", "G", key: "LAYER:G|length") };
        var open = Bound(generic, _ => C.State.GenericUnchanged);
        W.ClosureAssistRefusal(ProjectRuleProposalScope.Evaluate(open.Scope!, open.Current!, generic), "layer:G|length").Should().BeNull();
        // ...but not when the context changed after the capture.
        var changed = generic.Select(r => r.RecordId == "g2" ? Record("g2", "G2", key: "LAYER:G|length") : r).ToArray();
        W.ClosureAssistRefusal(ProjectRuleProposalScope.Evaluate(open.Scope!, open.Current!, changed), "layer:G|length").Should().NotBeNull();
    }

    [Fact]
    public void TheAssistantGateRefusesWithoutAProvenContext()
    {
        W.GenericAssistRefusal(null!, null!, null, "k").Should().NotBeNull();
        W.GenericAssistRefusal(null!, null!, W.ProjectRuleContext.Pending("סיבה"), "k").Should().Be("סיבה");
        W.GenericAssistRefusal(null!, null!, W.ProjectRuleContext.Stale("ישן"), "k").Should().Be("ישן");
        W.GenericAssistRefusal(null!, null!, W.ProjectRuleContext.NotApplicable("r"), "k").Should().BeNull();
    }

    [Fact]
    public void AGovernedRowShowsItsLabelInsteadOfAProposal()
    {
        var row = new QuantityRowViewModel
        {
            RuleKey = "layer:HW-CURB|length", Layer = "HW-CURB", EntityType = "LINE", Method = "line-length", ObjectCount = 2,
            Quantity = 10, Unit = "מטר", MappingState = "לפי כללי הפרויקט", ProjectRuleGoverned = true, ProjectRuleNote = "לפי כללי הפרויקט …",
        };
        row.CatalogCodeDisplay.Should().Be("לפי כללי הפרויקט");
        row.ProjectRuleLabel = "לא הוצע — הצמדה לא מוכחת";
        row.CatalogCodeDisplay.Should().Be("לא הוצע — הצמדה לא מוכחת");
        row.ProjectRuleGoverned = false;
        row.ProposedCode = "51.06.0025";
        row.CatalogCodeDisplay.Should().Contain("הצעה: ");
    }

    private static string PluginRoot => typeof(ProjectRuleProposalContextTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { PluginRoot }.Concat(parts).ToArray()));

    [Fact]
    public void EveryProposalCallerCarriesTheContextAndPublicationReprovesIt()
    {
        var service = Source("CivilDelivery", "Estimate", "EstimateWorkflowService.cs");
        // Eligibility before ranking: a group without generic eligibility never reaches the heuristic, curated or Top-3 step.
        var filter = service.IndexOf("var groups = allGroups.Where(g => !governed.Contains(g.RuleKey)).ToList();", StringComparison.Ordinal);
        filter.Should().BeGreaterThan(0);
        filter.Should().BeLessThan(service.IndexOf("MappingProposalEngine.Propose(groups, snapshot, reference.CatalogCodes)", StringComparison.Ordinal));
        filter.Should().BeLessThan(service.IndexOf("CuratedRuleProposals(groups, snapshot, profile)", StringComparison.Ordinal));
        service.Should().Contain("ScanResult scan, CatalogSnapshot snapshot, ProjectProfile profile, ProjectRuleContext context)")
            .And.Contain("PrepareProjectRuleContext(scan, snapshot)")
            .And.Contain("RequireCurrentProjectRuleContext(scan, snapshot, rulesContext, pending.Proposals);");
        Source("Tools", "CivilDelivery", "EstimateTools.cs").Should()
            .Contain("var rulesContext = workflow.PrepareProjectRuleContext(scan, catalog.Snapshot);")
            .And.Contain("scan, catalog.Snapshot, profile, rulesContext);")
            .And.Contain("pending.Scan, pending.Publication, pending.CatalogSnapshot);")
            .And.Contain("governed_by_project_rules");
        var review = Source("CivilDelivery", "UI", "CivilDeliveryControl.MappingReview.cs");
        review.Should().Contain("if (_projectRuleReviews.TryGetValue(group.RuleKey, out var ruleReview) && ruleReview.Governed)")
            .And.Contain("var rulesContext = contextAtStart ?? _estimate.PrepareProjectRuleContext(scan, catalog);");
        // The gate runs before the assistant and again before its answer is shown.
        var gate = "EstimateWorkflowService.GenericAssistRefusal(scan, catalog, rulesContext, group.RuleKey)";
        var first = review.IndexOf(gate, StringComparison.Ordinal);
        var call = review.IndexOf("await assistant.AssistAsync(", StringComparison.Ordinal);
        first.Should().BeGreaterThan(0).And.BeLessThan(call);
        review.IndexOf(gate, call, StringComparison.Ordinal).Should().BeGreaterThan(call);
        Source("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs").Should()
            .Contain("var proposal = ruleReview?.Governed == true ? null : _proposals.FirstOrDefault(p => p.RuleKey == group.Key);");
        var producer = Source("CivilDelivery", "Estimate", "EstimateWorkflowService.ProjectRules.cs");
        producer.Should().NotContain("ProjectProfileWriter").And.NotContain("SaveApprovedMappings")
            .And.NotContain("ResolveCorridor").And.NotContain("BoqRulesWorkbookWriter")
            .And.Contain("var current = CurrentStamp(scan, snapshot, rules, captured.OutcomeSha256);")
            .And.Contain("S.Capture(bound, scan.Records), current);")
            .And.Contain("var evaluation = S.Evaluate(scope, current, scan.Records);");
    }
}
