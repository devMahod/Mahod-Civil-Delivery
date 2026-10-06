using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// How the engineer draft uses recognition: proposals for groups nothing covers, never priced, and the groups
/// that recognition and family decisions share. Records are SYNTHETIC.
/// </summary>
public sealed class EngineerDraftRecognitionTests
{
    private const string Gm = "6422-GM-MODEL-NATAZ";
    private const string Sm = "6422-SM-MODEL-NATAZ";
    private static int _handle;

    private static NeutralQuantityRecord Rec(string? xref, string layer, string kind, string method, double value, string unit,
        string? block = null, double? width = null, string? drawingHash = null)
    {
        var parameters = new Dictionary<string, string>();
        if (block != null) parameters["cad_block_name_effective"] = block;
        if (width != null)
        {
            parameters["cad_polyline_constant_width_raw"] = width.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            parameters["cad_entity_database_insunits"] = "Meters";
        }
        var handle = System.Threading.Interlocked.Increment(ref _handle).ToString("X");
        return new NeutralQuantityRecord
        {
            RecordId = $"r-{handle}", ProjectProfileId = "SYNTHETIC", RunId = "SYNTHETIC-RUN",
            Source = new QuantitySource
            {
                Drawing = "synthetic.dwg", DrawingHash = drawingHash ?? new string('a', 64), Handle = handle, EntityType = "X",
                Layer = xref == null ? layer : $"{xref}|{layer}", Xref = xref,
            },
            Measurement = new QuantityMeasurement { Kind = kind, Method = method, RawValue = value, Unit = unit, Parameters = parameters },
            Classification = new QuantityClassification(),
        };
    }

    private static CatalogSnapshot Catalog()
    {
        var catalog = new CatalogSnapshot { SnapshotId = "SYNTHETIC", FileHash = new string('b', 64) };
        catalog.Items["U51.06.1900"] = new CatalogItem { Code = "U51.06.1900", Description = "SYNTHETIC road curb", UnitRaw = "מטר" };
        catalog.Prices["U51.06.1900"] = new PriceRecord { Code = "U51.06.1900", Price = 90m, PriceBookId = "SYNTHETIC" };
        return catalog;
    }

    /// <summary>Records what it was asked and proposes one family for every record, citing nothing real.</summary>
    private sealed class FakeClassifier : IFamilyClassifier
    {
        public List<RecognitionGroupInput> Seen { get; } = new();
        public string Identity => "fake/1";

        public IReadOnlyList<RecognitionProposal> Classify(RecognitionGroupInput group, EngineerBoqLibrary library, CatalogSnapshot? catalog)
        {
            Seen.Add(group);
            return new[]
            {
                new RecognitionProposal(group.GroupId, group.Records.Select(r => r.RecordId).ToList(), RecognitionStatus.Proposed, "curb-road",
                    new[] { "U51.06.1900" }, new[] { new RecognitionEvidenceRef("ev_legend_row", group.Records.Select(r => r.RecordId).ToList()) },
                    new[] { "שורת מקרא (לא מאומתת): אבן שפה לכביש" }, new[] { "התאמה למשפחה 'אבן שפה לכביש'" },
                    new[] { new RecognitionAlternative("curb-island", "גם אבן שפה") }, Array.Empty<string>(), RecognitionProposal.OriginLocal),
            };
        }
    }

    private static EngineerBoqDraft Build(IReadOnlyList<NeutralQuantityRecord> records, IFamilyClassifier? classifier) =>
        EngineerBoqDraftBuilder.Build(records, Array.Empty<DeliveryFinding>(), Catalog(), new Dictionary<string, string>(), EngineerBoqLibrary.RoadsV1,
            new EngineerDraftContext("SYNTHETIC", "SYNTHETIC", "SYNTHETIC-RUN", "synthetic.dwg", "SYNTHETIC", Array.Empty<string>(), Classifier: classifier));

    [Fact]
    public void UncoveredGroupsAndUnapprovedAssumptionRecipesAreReviewedAndProposalsAreNeverPriced()
    {
        var records = new[]
        {
            Rec(Gm, "HW-CURB", "length", "polyline-length+xref-transform", 100, "מטר"),
            Rec(Gm, "asdasd23423", "length", "polyline-length+xref-transform", 30, "מטר"),
            Rec(Gm, "asdasd23423", "length", "polyline-length+xref-transform", 20, "מטר"),
            Rec("6422-SP-MEDVA-ALL-2026-MHD", "zz-survey", "length", "polyline-length+xref-transform", 70, "מטר"),
        };
        var classifier = new FakeClassifier();
        var plain = Build(records, null);
        var draft = Build(records, classifier);

        // Eligibility (Codex D465A971, root-approved 29.09): besides the uncovered design group, a group matched only by
        // an unapproved Assumption recipe (HW-CURB -> curb-road) is offered for recognition review. The survey source is
        // still not classified, and review never adds a second contribution or a price (totals below are unchanged).
        classifier.Seen.Select(g => (g.LayerLeaf, g.Records.Count))
            .Should().BeEquivalentTo(new[] { ("asdasd23423", 2), ("HW-CURB", 1) });
        draft.RecognitionProposals.Should().HaveCount(2)
            .And.ContainSingle(p => p.FamilyId == "curb-road" && p.RecordIds.Count == 2);
        draft.ClassifierIdentity.Should().Be("fake/1");
        plain.RecognitionProposals.Should().BeEmpty();
        decimal Total(EngineerBoqDraft d) => EngineerBoqDraftExcelWriter.TotalAt(d, EngineerBoqDraftExcelWriter.BoqRows(d), EngineerBoqDraftExcelWriter.DefaultParameter(d), null);
        Total(draft).Should().Be(Total(plain)).And.Be(9000m);
        draft.UnmappedDesign.Should().ContainSingle(g => g.Layer == "asdasd23423");
        draft.AccountedRecords.Should().Be(records.Length);

        var path = Path.Combine(Path.GetTempPath(), "mhd-engineer-draft-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            EngineerBoqDraftExcelWriter.Write(draft, path);
            using var zip = ZipFile.OpenRead(path);
            var names = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open()).Descendants()
                .Where(e => e.Name.LocalName == "sheet").Select(e => (string)e.Attribute("name")!).ToList();
            names.Should().Contain(EngineerBoqDraftExcelWriter.RecognitionSheet);
            var index = names.IndexOf(EngineerBoqDraftExcelWriter.RecognitionSheet) + 1;
            var text = string.Concat(XDocument.Load(zip.GetEntry($"xl/worksheets/sheet{index}.xml")!.Open()).Descendants()
                .Where(e => e.Name.LocalName == "t").Select(e => e.Value));
            text.Should().Contain("הצעה — לא מאושרת").And.Contain("אבן שפה לכביש").And.Contain("U51.06.1900");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static NeutralQuantityRecord Legend(string layer, double metres, string legendText, string drawingHash = "")
    {
        var record = Rec(Gm, layer, "length", "polyline-length+xref-transform", metres, "מטר",
            drawingHash: drawingHash.Length > 0 ? drawingHash : null);
        record.Measurement.Parameters["ev_schema"] = "mahod-evidence/1";
        record.Measurement.Parameters["ev_schema_status"] = "read";
        record.Measurement.Parameters["ev_legend_row"] =
            $"{{\"text\":\"{legendText}\",\"match\":\"linetype\",\"handle\":\"L1\",\"verified\":false}}";
        record.Measurement.Parameters["ev_legend_row_status"] = "read";
        return record;
    }

    private static EngineerBoqDraft BuildWith(IReadOnlyList<NeutralQuantityRecord> records,
        IReadOnlyList<ProjectProfile.EstimateProfile.FamilyDecision>? decisions) =>
        EngineerBoqDraftBuilder.Build(records, Array.Empty<DeliveryFinding>(), Catalog(), new Dictionary<string, string>(), EngineerBoqLibrary.RoadsV1,
            new EngineerDraftContext("SYNTHETIC", "SYNTHETIC", "SYNTHETIC-RUN", "synthetic.dwg", "SYNTHETIC", Array.Empty<string>(),
                FamilyDecisions: decisions, Classifier: LocalFamilyClassifier.Instance));

    private static decimal Total(EngineerBoqDraft draft) =>
        EngineerBoqDraftExcelWriter.TotalAt(draft, EngineerBoqDraftExcelWriter.BoqRows(draft), EngineerBoqDraftExcelWriter.DefaultParameter(draft), null);

    [Fact]
    public void RandomlyNamedLayersAreRecognisedApprovedOnceAndKeptAcrossARescan()
    {
        // Two meaningless layer names; the drawing's legend (matched by line type) says what they are.
        var scan1 = new[] { Legend("asdasd23423", 100, "אבן שפה לכביש"), Legend("qwe_77", 50, "אבן שפה לכביש") };
        var first = BuildWith(scan1, null);
        Total(first).Should().Be(0m, "recognition proposes, it never prices");
        first.RecognitionProposals.Should().HaveCount(2).And.OnlyContain(p => p.Status == RecognitionStatus.Proposed && p.FamilyId == "curb-road");

        // The engineer approves the family once for both groups (the dialog's batch → the policy's decision).
        var candidates = first.RecognitionProposals.Select(p => first.RecognitionGroups.Single(g => g.GroupId == p.GroupId)).ToList();
        var decision = FamilyDecisionPolicy.CreateApproval("curb-road", candidates, new[] { "ev_legend_row" }, EngineerBoqLibrary.RoadsV1,
            "SYNTHETIC engineer", "שורת מקרא 'אבן שפה לכביש'", new DateTime(2026, 9, 27, 14, 0, 0, DateTimeKind.Utc));
        decision.Selectors.Should().HaveCount(2, "one decision covers both groups");
        decision.ItemApprovals.Should().BeEmpty("a family approval never approves items");
        var decisions = new[] { decision };

        var second = BuildWith(scan1, decisions);
        second.FamilyResolutions.Should().HaveCount(2).And.OnlyContain(r => r.State == FamilyDecisionState.Applied && r.FamilyId == "curb-road");
        second.Elements.Single(e => e.Rule.Id == "curb-road").IncludedQuantity.Should().Be(150);
        Total(second).Should().Be(150m * 90m);
        second.RecognitionProposals.Should().BeEmpty("decided groups are no longer proposals");
        second.Warnings.Should().Contain(w => w.Topic == "שיוך לפי החלטות משפחה");

        // A rescan: new record ids and handles, a re-saved drawing, a longer curb. The decision still holds.
        var scan2 = new[]
        {
            Legend("asdasd23423", 120, "אבן שפה לכביש", new string('c', 64)), Legend("qwe_77", 50, "אבן שפה לכביש", new string('c', 64)),
        };
        var third = BuildWith(scan2, decisions);
        third.FamilyResolutions.Should().OnlyContain(r => r.State == FamilyDecisionState.Applied);
        Total(third).Should().Be(170m * 90m);

        // The legend of one layer now says something else: only that selector goes stale; the other stays applied.
        var scan3 = new[] { Legend("asdasd23423", 120, "גדר להולכי רגל"), Legend("qwe_77", 50, "אבן שפה לכביש") };
        var fourth = BuildWith(scan3, decisions);
        fourth.FamilyResolutions.Should().Contain(r => r.State == FamilyDecisionState.Stale && r.StaleReason == "evidence")
            .And.Contain(r => r.State == FamilyDecisionState.Applied);
        Total(fourth).Should().Be(50m * 90m);
        fourth.Warnings.Should().Contain(w => w.Topic == "החלטת משפחה לא בתוקף" && w.AffectsTotal);
        fourth.UnmappedDesign.Should().ContainSingle(g => g.Layer == "asdasd23423");
        fourth.AccountedRecords.Should().Be(scan3.Length);
    }

    [Fact]
    public void RecognitionGroupsIgnoreDrawnWidthAndSplitCountsByBlock()
    {
        var records = new[]
        {
            Rec(Sm, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 10, "מטר", width: 0.5),
            Rec(Sm, "tr-mark-wht-810", "length", "polyline-length+xref-transform", 10, "מטר", width: 0.1),
            Rec(Sm, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 10, "מטר"),
            Rec(Sm, "Q1", "count", "block-count+xref-transform", 1, "יח'", block: "A"),
            Rec(Sm, "Q1", "count", "block-count+xref-transform", 1, "יח'", block: "B"),
            Rec(Sm, "Q1", "count", "block-count+xref-transform", 0, "יח'", block: "B"),
        };
        var groups = EngineerBoqDraftBuilder.RecognitionGroups(records, EngineerBoqLibrary.RoadsV1);

        groups.Should().HaveCount(3);
        groups.Single(g => g.Kind == "length").Records.Should().HaveCount(3, "one group before any width re-read, layer case-insensitive");
        groups.Where(g => g.Kind == "count").Select(g => g.Block).Should().BeEquivalentTo(new[] { "A", "B" });
        groups.Sum(g => g.Records.Count).Should().Be(5, "an invalid measurement is never grouped");
        groups.Select(g => g.GroupId).Should().OnlyHaveUniqueItems().And.BeInAscendingOrder(StringComparer.Ordinal);
        EngineerBoqDraftBuilder.RecognitionGroups(records.Reverse().ToList(), EngineerBoqLibrary.RoadsV1).Select(g => g.GroupId)
            .Should().Equal(groups.Select(g => g.GroupId));
    }
}
