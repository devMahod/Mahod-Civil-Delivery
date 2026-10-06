using System.IO;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// Family decisions inside the engineer draft, under the review fixes: contradicting new evidence, role changes, the
/// precedence of an approved item, strict count blocks and the drawn-width re-read. Records are SYNTHETIC.
/// </summary>
public sealed class FamilyDecisionDraftIntegrationTests
{
    private const string Gm = "6422-GM-MODEL-NATAZ";
    private static int _handle;

    private static NeutralQuantityRecord Rec(string layer, string kind, string method, double value, string unit,
        IReadOnlyDictionary<string, string>? parameters = null, string? block = null, QuantityClassification? classification = null)
    {
        var p = new Dictionary<string, string> { ["ev_schema"] = "mahod-evidence/1", ["ev_schema_status"] = "read" };
        if (parameters != null) foreach (var pair in parameters) p[pair.Key] = pair.Value;
        if (block != null) p["cad_block_name_effective"] = block;
        var handle = System.Threading.Interlocked.Increment(ref _handle).ToString("X");
        return new NeutralQuantityRecord
        {
            RecordId = $"fdi-{handle}", ProjectProfileId = "SYNTHETIC", RunId = "SYNTHETIC-RUN",
            Source = new QuantitySource { Drawing = "synthetic.dwg", DrawingHash = new string('a', 64), Handle = handle, EntityType = "X",
                Layer = $"{Gm}|{layer}", Xref = Gm },
            Measurement = new QuantityMeasurement { Kind = kind, Method = method, RawValue = value, Unit = unit, Parameters = p },
            Classification = classification ?? new QuantityClassification(),
        };
    }

    private static Dictionary<string, string> Legend(string text) => new()
    {
        ["ev_legend_row"] = $"{{\"text\":\"{text}\",\"match\":\"linetype\",\"handle\":\"L1\",\"verified\":false}}",
        ["ev_legend_row_status"] = "read",
    };

    private static CatalogSnapshot Catalog()
    {
        var catalog = new CatalogSnapshot { SnapshotId = "SYNTHETIC", FileHash = new string('b', 64) };
        foreach (var (code, unit, price) in new[] { ("U51.06.1900", "מטר", 90m), ("U51.06.2460", "מטר", 150m), ("U51.33.2330", "מטר", 300m) })
        {
            catalog.Items[code] = new CatalogItem { Code = code, Description = "SYNTHETIC " + code, UnitRaw = unit };
            catalog.Prices[code] = new PriceRecord { Code = code, Price = price, PriceBookId = "SYNTHETIC" };
        }
        return catalog;
    }

    private static EngineerBoqDraft Draft(IReadOnlyList<NeutralQuantityRecord> records, IReadOnlyList<ProjectProfile.EstimateProfile.FamilyDecision>? decisions,
        CatalogSnapshot? catalog = null) =>
        EngineerBoqDraftBuilder.Build(records, Array.Empty<DeliveryFinding>(), catalog ?? Catalog(), new Dictionary<string, string>(), EngineerBoqLibrary.RoadsV1,
            new EngineerDraftContext("SYNTHETIC", "SYNTHETIC", "SYNTHETIC-RUN", "synthetic.dwg", "SYNTHETIC", Array.Empty<string>(),
                FamilyDecisions: decisions, Classifier: LocalFamilyClassifier.Instance));

    private static ProjectProfile.EstimateProfile.FamilyDecision Approve(EngineerBoqDraft draft, string family, IReadOnlyList<string> keys, params string[] layers) =>
        FamilyDecisionPolicy.CreateApproval(family, draft.RecognitionGroups.Where(g => layers.Contains(g.LayerLeaf)).ToList(), keys,
            EngineerBoqLibrary.RoadsV1, "SYNTHETIC engineer", "SYNTHETIC reason", new DateTime(2026, 9, 27, 15, 0, 0, DateTimeKind.Utc));

    private static decimal Total(EngineerBoqDraft d) =>
        EngineerBoqDraftExcelWriter.TotalAt(d, EngineerBoqDraftExcelWriter.BoqRows(d), EngineerBoqDraftExcelWriter.DefaultParameter(d), null);

    [Fact]
    public void NewReadEvidenceThatContradictsTheApprovalStopsItButIrrelevantEvidenceAndDeliberateOverridesDoNot()
    {
        // Codex 16:57: A and B approved on the legend; A's attributes were unavailable then and now say "fence".
        Dictionary<string, string> Unavailable() => new(Legend("אבן שפה לכביש")) { ["ev_block_attributes_status"] = "unavailable:Exception" };
        var scan1 = new[] { Rec("aa1", "length", "polyline-length+xref-transform", 100, "מטר", Unavailable()),
                            Rec("bb2", "length", "polyline-length+xref-transform", 50, "מטר", Legend("אבן שפה לכביש")) };
        var first = Draft(scan1, null);
        var decision = Approve(first, "curb-road", new[] { "ev_legend_row" }, "aa1", "bb2");
        decision.Selectors.Should().OnlyContain(s => s.LocalVerdictAtApproval == "curb-road");

        var contradicted = new Dictionary<string, string>(Legend("אבן שפה לכביש"))
        {
            ["ev_block_attributes"] = "[{\"tag\":\"TYPE\",\"value\":\"גדר להולכי רגל\"}]", ["ev_block_attributes_status"] = "read",
        };
        var scan2 = new[] { Rec("aa1", "length", "polyline-length+xref-transform", 100, "מטר", contradicted),
                            Rec("bb2", "length", "polyline-length+xref-transform", 50, "מטר", Legend("אבן שפה לכביש")) };
        var second = Draft(scan2, new[] { decision });
        var a = second.FamilyResolutions.Single(r => second.RecognitionGroups.Single(g => g.GroupId == r.GroupId).LayerLeaf == "aa1");
        a.State.Should().Be(FamilyDecisionState.Stale);
        a.StaleReason.Should().Be(FamilyDecisionPolicy.StaleContradicted);
        second.FamilyResolutions.Should().ContainSingle(r => r.State == FamilyDecisionState.Applied);
        Total(second).Should().Be(50m * 90m, "only B stays priced");
        second.Warnings.Should().Contain(w => w.Topic == "החלטת משפחה לא בתוקף" && w.Message.Contains("ראיה חדשה"));

        // Evidence that does not change what the drawing says is not a contradiction.
        var colour = new Dictionary<string, string>(Legend("אבן שפה לכביש")) { ["ev_color_effective"] = "255,0,0", ["ev_color_effective_status"] = "read" };
        var third = Draft(new[] { Rec("aa1", "length", "polyline-length+xref-transform", 100, "מטר", colour), scan2[1] }, new[] { decision });
        third.FamilyResolutions.Should().OnlyContain(r => r.State == FamilyDecisionState.Applied);

        // The engineer deliberately chose another family than the one the evidence suggested: that choice holds.
        var overrideDecision = Approve(first, "curb-island", Array.Empty<string>(), "bb2");
        overrideDecision.Selectors.Single().LocalVerdictAtApproval.Should().Be("curb-road");
        var fourth = Draft(scan1, new[] { overrideDecision });
        fourth.FamilyResolutions.Should().ContainSingle(r => r.State == FamilyDecisionState.Applied && r.FamilyId == "curb-island");
        FamilyDecisionPolicy.IsContradicted("curb-island", "curb-road", "curb-road").Should().BeFalse();
        FamilyDecisionPolicy.IsContradicted("curb-road", "curb-road", "abstained").Should().BeTrue();
        FamilyDecisionPolicy.IsContradicted("curb-road", null, "abstained").Should().BeFalse("a legacy decision is only contradicted by a different family");
    }

    [Fact]
    public void ADecisionNeverCoversAGroupThatIsNoLongerDesignWork()
    {
        var records = new[] { Rec("zz9", "length", "polyline-length+xref-transform", 100, "מטר", Legend("אבן שפה לכביש")) };
        var decision = Approve(Draft(records, null), "curb-road", new[] { "ev_legend_row" }, "zz9");
        Total(Draft(records, new[] { decision })).Should().Be(9000m);

        // The same layer now measures an implausible magnitude: it is classified as a drafting aid, and the decision stops.
        var huge = new[] { Rec("zz9", "length", "polyline-length+xref-transform", 5_000_000, "מטר", Legend("אבן שפה לכביש")) };
        var draft = Draft(huge, new[] { decision });
        draft.DraftingAids.Should().Contain(g => g.Layer == "zz9");
        draft.FamilyResolutions.Should().ContainSingle(r => r.State == FamilyDecisionState.Stale && r.StaleReason == FamilyDecisionPolicy.StaleRole);
        Total(draft).Should().Be(0m);
    }

    [Fact]
    public void AnApprovedItemOutranksAFamilyDecision()
    {
        var catalog = Catalog();
        var records = new[] { Rec("zz7", "length", "polyline-length+xref-transform", 100, "מטר", Legend("אבן שפה לכביש")) };
        var decision = Approve(Draft(records, null, catalog), "curb-road", new[] { "ev_legend_row" }, "zz7");
        var approved = new QuantityClassification
        {
            RuleKey = "layer:zz7|length", CandidateCatalogCode = "U51.33.2330", ApprovedCatalogId = catalog.SnapshotId, ApprovedCatalogHash = catalog.FileHash,
            ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(catalog.Items["U51.33.2330"]), MappingApprovedBy = "SYNTHETIC engineer",
            MappingApprovedAtUtc = new DateTime(2026, 9, 27, 16, 0, 0, DateTimeKind.Utc),
        };
        var withItem = new[] { Rec("zz7", "length", "polyline-length+xref-transform", 100, "מטר", Legend("אבן שפה לכביש"), classification: approved) };
        var draft = Draft(withItem, new[] { decision }, catalog);

        draft.FamilyResolutions.Should().ContainSingle(r => r.StaleReason == FamilyDecisionPolicy.StaleApprovedItem);
        draft.Elements.Should().NotContain(e => e.Rule.Id == "curb-road");
        Total(draft).Should().Be(100m * 300m, "the approved item prices the layer, as it did before the family decision");
    }

    [Fact]
    public void ACountSelectorWithoutABlockNeverCoversBlocksInsertedLater()
    {
        var structures = new[] { Rec("C-STR-x9", "count", "structure-count", 1, "יח'", Legend("שוחת ניקוז")) };
        var decision = Approve(Draft(structures, null), "utility-drainage-structures", Array.Empty<string>(), "C-STR-x9");
        decision.Selectors.Single().BlockName.Should().BeNull();
        var later = structures.Append(Rec("C-STR-x9", "count", "block-count+xref-transform", 1, "יח'", block: "TREE_01")).ToArray();
        var draft = Draft(later, new[] { decision });
        draft.FamilyResolutions.Should().ContainSingle(r => r.State == FamilyDecisionState.Applied);
        draft.RecognitionGroups.Should().HaveCount(2);
        draft.Elements.Single(e => e.Rule.Id == "utility-drainage-structures").Sources.Should().ContainSingle(s => s.Group.Block == null);
    }

    [Fact]
    public void AMarkingLayerDecidedForAFamilyWithoutWidthKeepsItsLines()
    {
        // TR-MARK-* layers are re-read by drawn width only when the decided family splits by width.
        var p = new Dictionary<string, string>(Legend("צביעת אבני שפה"))
        {
            ["cad_polyline_constant_width_raw"] = "0.3", ["cad_entity_database_insunits"] = "Meters",
        };
        var records = new[] { Rec("TR-MARK-RED-1", "length", "polyline-length+xref-transform", 40, "מטר", p) };
        var first = Draft(records, null);
        var decision = Approve(first, "curb-painting", new[] { "ev_legend_row" }, "TR-MARK-RED-1");
        var draft = Draft(records, new[] { decision });
        draft.NotUsedAlternatives.Should().BeEmpty();
        draft.Elements.Should().ContainSingle(e => e.Rule.Id == "curb-painting" && e.IncludedQuantity == 40);
    }
}
