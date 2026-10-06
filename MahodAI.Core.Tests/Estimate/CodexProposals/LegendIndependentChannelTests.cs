// Adopted from Codex proposal tests (Work/codex-accept-92), 2026-09-27; unchanged below this line. Adaptations: System.IO using; output falls back to AdoptedTestOutput.Root.
using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Evidence;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Xunit;

namespace Codex.LegendProposal;

// One synthetic Core discriminator; no CAD reader, profile, UI or engineering approval.
public sealed class LegendIndependentChannelTests
{
    private const string Subject = "BUS SHELTER", Source = "TEST-ONLY-GM-MODEL", Layer = "QX971";
    private static readonly EngineerBoqLibrary Library = EngineerBoqLibrary.RoadsV1;
    private static string ShelterCode => Library.Rules.Single(rule => rule.Id == "bus-shelters").Emits.Single().Code;

    [Fact]
    public void IncompleteLegendCannotSupplyCandidatesOrSuppressCompleteIndependentAttributes()
    {
        var style = new Dictionary<string, string>
        {
            [EvidenceKeys.ColorEffective] = EffectiveColorPolicy.AciToRgbText(1)!,
            [EvidenceKeys.ColorEffective + EvidenceKeys.StatusSuffix] = "read",
            [EvidenceKeys.EntityLinetype] = "DASHED",
        };
        var completeLegend = LegendEvidence.ForRecord(
            new[] { new LegendEntry(Subject, 1, "dashed", "A1", "MIKRA", true) }, null, style);
        Assert.Equal("read", completeLegend.Status);
        var incompleteLegend = LegendEvidence.ApplyReadCompleteness(completeLegend, complete: false);
        Assert.Equal("unavailable:legend-read-incomplete", incompleteLegend.Status);
        Assert.Null(incompleteLegend.Json);

        // Same TEST BUS SHELTER count catalog/records as the existing bridge control.
        // Baseline has attributes only; the second case adds the exact helper output.
        var baseline = Records(attributes: true, legend: null);
        var independent = Records(attributes: true, legend: incompleteLegend);
        var legendOnly = Records(attributes: false, legend: incompleteLegend);
        var catalog = Catalog();
        var before = JsonSerializer.Serialize(new { baseline, independent, legendOnly, catalog, style });
        var baselineGroup = Group(baseline);
        var independentGroup = Group(independent);
        var legendGroup = Group(legendOnly);
        var baselineCandidates = MappingProposalEngine.Propose(new[] { baselineGroup }, catalog);
        var independentCandidates = MappingProposalEngine.Propose(new[] { independentGroup }, catalog);
        var legendCandidates = MappingProposalEngine.Propose(new[] { legendGroup }, catalog);

        AssertSafeCandidates(baselineCandidates, baselineGroup, catalog);
        AssertSafeCandidates(independentCandidates, independentGroup, catalog);
        Assert.Equal(baselineCandidates.Select(p => p.ProposedCode).OrderBy(x => x).ToArray(),
            independentCandidates.Select(p => p.ProposedCode).OrderBy(x => x).ToArray());
        Assert.Null(MappingProposalEngine.EvidenceRefusal(independentGroup));
        Assert.False(independentGroup.RecognitionEvidence!.Contradicted);
        Assert.Equal("bus-shelters", independentGroup.RecognitionEvidence.RecognisedFamily);
        var subject = Assert.Single(independentGroup.RecognitionEvidence.Subjects,
            value => value.Key == EvidenceKeys.BlockAttributes);
        Assert.Equal(Subject, subject.Value);
        Assert.Equal(2, subject.RecordCount);
        Assert.DoesNotContain(independentGroup.RecognitionEvidence.Subjects, value => value.Key == EvidenceKeys.LegendRow);
        Assert.All(independentCandidates, candidate => Assert.Contains(candidate.Reasons,
            reason => reason.Contains(subject.Citation, StringComparison.Ordinal)));

        Assert.Empty(legendGroup.RecognitionEvidence!.Subjects);
        Assert.Empty(legendGroup.RecognitionEvidence.FamilyCandidateCodes);
        Assert.Empty(legendCandidates);
        foreach (var group in new[] { baselineGroup, independentGroup, legendGroup })
        {
            Assert.Equal(2, group.ObjectCount);
            Assert.Equal(2d, group.TotalQuantity);
            Assert.Equal(Source + "|" + Layer, group.Layer);
            Assert.Empty(group.CadMetadata!); // No legacy CAD name supplies the answer.
        }
        Assert.Equal(before, JsonSerializer.Serialize(new { baseline, independent, legendOnly, catalog, style }));
        Assert.All(baseline.Concat(independent).Concat(legendOnly), record =>
        {
            Assert.Null(record.Classification.CandidateCatalogCode);
            Assert.Null(record.Classification.MappingApprovedBy);
            Assert.Null(record.Classification.MappingApprovedAtUtc);
            Assert.Null(record.Classification.ApprovedCatalogId);
            Assert.Null(record.Classification.ApprovedCatalogHash);
            Assert.Null(record.Classification.ApprovedCatalogItemFingerprint);
        });
    }

    private static NeutralQuantityRecord[] Records(bool attributes, EvidenceValue? legend) => new[] { "A", "B" }.Select(id =>
    {
        var parameters = new Dictionary<string, string>
        {
            [EvidenceKeys.Schema] = EvidenceKeys.SchemaV1,
            [EvidenceKeys.Schema + EvidenceKeys.StatusSuffix] = "read",
        };
        if (attributes)
        {
            parameters[EvidenceKeys.BlockAttributes] = JsonSerializer.Serialize(new
            {
                synthetic_injection = true, source = new { handle = id },
                observation = new { attributes = new[] { new { tag = "TEST_CLASS", value = Subject } } },
            });
            parameters[EvidenceKeys.BlockAttributes + EvidenceKeys.StatusSuffix] = "read";
        }
        if (legend is { } legendValue)
        {
            parameters[EvidenceKeys.LegendRow + EvidenceKeys.StatusSuffix] = legendValue.Status;
            if (legendValue.Json != null) parameters[EvidenceKeys.LegendRow] = legendValue.Json;
        }
        return new NeutralQuantityRecord
        {
            RecordId = "TEST-ONLY-" + id, ProjectProfileId = "TEST-ONLY-NO-PROFILE", RunId = "TEST-ONLY-RUN",
            Source = new QuantitySource { Drawing = "TEST-ONLY-NOT-A-REAL-DRAWING.dwg", DrawingHash = new string('a', 64),
                Handle = id, EntityType = "BlockReference", Layer = Source + "|" + Layer, Xref = Source },
            Measurement = new QuantityMeasurement { Kind = "count", Method = "block-count", RawValue = 1, Unit = "unit", Parameters = parameters },
            Classification = new QuantityClassification { RuleKey = "TEST-ONLY-QX971-COUNT" },
        };
    }).ToArray();

    private static MappingProposalEngine.DiscoveredGroup Group(NeutralQuantityRecord[] records) => new(
        records[0].Classification.RuleKey!, records[0].Source.Layer, "count", "unit", records.Length,
        records.Sum(record => record.Measurement.RawValue), QuantityCadMetadataPolicy.Summarize(records.Select(record => record.Measurement)),
        CatalogEvidenceBridge.For(records, Library));

    private static CatalogSnapshot Catalog()
    {
        var catalog = new CatalogSnapshot { SnapshotId = "TEST-ONLY-CATALOG", FileHash = new string('b', 64),
            PublicationNote = "TEST ONLY: fictional descriptions, units and prices; no real engineering specification." };
        foreach (var code in new[] { ShelterCode, "TEST-SHELTER-1", "TEST-SHELTER-2", "TEST-SHELTER-3", "TEST-SHELTER-4" })
        {
            catalog.Items.Add(code, new CatalogItem { Code = code, Description = "סככה לתחנת אוטובוס — TEST ONLY", UnitRaw = "unit" });
            catalog.Prices.Add(code, new PriceRecord { Code = code, Price = 101m, PriceBookId = catalog.SnapshotId, SourceHash = catalog.FileHash });
        }
        catalog.Items.Add("TEST-WRONG-UNIT", new CatalogItem { Code = "TEST-WRONG-UNIT", Description = "סככה לתחנת אוטובוס — TEST ONLY", UnitRaw = "m2" });
        catalog.Items.Add("TEST-OTHER-SUBJECT", new CatalogItem { Code = "TEST-OTHER-SUBJECT", Description = "ספסל — TEST ONLY", UnitRaw = "unit" });
        return catalog;
    }

    private static void AssertSafeCandidates(IReadOnlyList<MappingProposal> candidates,
        MappingProposalEngine.DiscoveredGroup group, CatalogSnapshot catalog)
    {
        Assert.Equal(3, candidates.Count);
        Assert.Equal(3, candidates.Select(candidate => candidate.ProposedCode).Distinct().Count());
        Assert.Contains(candidates, candidate => candidate.ProposedCode == ShelterCode);
        Assert.All(candidates, candidate =>
        {
            Assert.True(catalog.Items.TryGetValue(candidate.ProposedCode, out var item));
            Assert.True(item!.Unit.SameUnit(Units.Parse(group.MeasuredUnit)));
            Assert.Equal("PROPOSED_UNAPPROVED", candidate.Status);
            Assert.Equal(group.RuleKey, candidate.RuleKey);
            Assert.Equal(group.Layer, candidate.Layer);
            Assert.Equal(group.ObjectCount, candidate.ObjectCount);
            Assert.Equal(group.TotalQuantity, candidate.TotalQuantity);
            Assert.DoesNotContain(candidate.ProposedCode, new[] { "TEST-WRONG-UNIT", "TEST-OTHER-SUBJECT" });
        });
    }
}
