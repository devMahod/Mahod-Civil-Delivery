using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Xunit.Abstractions;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// The production proposal-group assembly carries recognition evidence to the existing catalog search. SYNTHETIC
/// records on a randomly named layer; the price list is the repository's NTI fixture. Offline: nothing is published,
/// approved or priced, and no AI is called.
/// </summary>
public sealed class MappingEvidenceBridgeWiringTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private static string RepoRoot => Path.GetFullPath(Path.Combine(typeof(MappingEvidenceBridgeWiringTests)
        .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "MahodPluginSourceDir").Value!, ".."));
    private static CatalogSnapshot Catalog() => PriceBookXlsxLoader.Load(
        Path.Combine(RepoRoot, "fixtures", "civil-delivery", "estimate", "nti-urban-082025.xlsx"), "nti-urban-082025");

    private static NeutralQuantityRecord Record(params (string Key, string Value)[] evidence)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["block_name"] = "QZ-77A", ["cad_block_name_effective"] = "QZ-77A",
            [EvidenceKeys.Schema] = EvidenceKeys.SchemaV1, [EvidenceKeys.Schema + EvidenceKeys.StatusSuffix] = "read",
        };
        foreach (var (key, value) in evidence)
        {
            parameters[key] = value;
            parameters[key + EvidenceKeys.StatusSuffix] = "read";
        }
        return new NeutralQuantityRecord
        {
            RecordId = "SYNTHETIC-" + Guid.NewGuid().ToString("N"), ProjectProfileId = "TEST-ONLY", RunId = "TEST-ONLY",
            Source = new QuantitySource { Drawing = "TEST-ONLY.dwg", DrawingPath = @"C:\TEST-ONLY\drawing.dwg",
                DrawingHash = new string('a', 64), Handle = "A1", EntityType = "BLOCKREFERENCE", Layer = "asdasd23423" },
            Measurement = new QuantityMeasurement { Kind = "count", Method = "block-count", RawValue = 1, Unit = "יח'",
                Parameters = parameters },
            Classification = new QuantityClassification { RuleKey = "layer:asdasd23423|count|block:QZ-77A" },
            Status = DeliveryStatus.ReviewRequired,
        };
    }

    private static (string, string) Attribute(string value) =>
        (EvidenceKeys.BlockAttributes, $"[{{\"tag\":\"DESC\",\"value\":\"{value}\",\"invisible\":false}}]");

    [Fact]
    public void ProductionReviewGroupRetainsDetachedVerifiedEvidenceForSemanticRequest()
    {
        var records = new List<NeutralQuantityRecord> { Record(Attribute("BUS SHELTER")), Record(Attribute("BUS SHELTER")) };
        var scan = new EstimateWorkflowService.ScanResult
        {
            RunId = "TEST-ONLY", ProjectProfileId = "TEST-ONLY", Records = records,
            ProfileSource = "C:/TEST-ONLY/profile.yaml",
            SourceDrawing = "C:/TEST-ONLY/drawing.dwg", SourceDrawingHash = new string('a', 64),
        };
        var row = new QuantityRowViewModel
        {
            RuleKey = records[0].Classification.RuleKey!, Layer = "asdasd23423", EntityType = "BLOCKREFERENCE",
            Method = "count", Unit = "יח'", ObjectCount = 2, Quantity = 2, MappingState = "לבדיקה",
        };
        var review = Assert.Single(CivilDeliveryControl.BuildMappingReviewGroups(scan, new[] { row },
            Array.Empty<MappingProposal>(), new ProjectProfile { ProfileId = "TEST-ONLY" }));
        var subject = Assert.Single(review.RecognitionEvidence!.Subjects);
        Assert.Equal("BUS SHELTER", subject.Value);
        Assert.Equal(2, subject.RecordCount);
        Assert.True(((IList<CatalogEvidenceBridge.Subject>)review.RecognitionEvidence.Subjects).IsReadOnly);
        records[0].Measurement.Parameters[EvidenceKeys.BlockAttributes] = Attribute("SEWER").Item2;
        Assert.Equal("BUS SHELTER", Assert.Single(review.RecognitionEvidence.Subjects).Value);
    }

    [Fact]
    public void AnAttributeOnARandomLayerReachesUnapprovedCandidatesOfTheRealPriceList()
    {
        var records = new[] { Record(Attribute("BUS SHELTER")), Record(Attribute("BUS SHELTER")) };
        var before = records.Select(r => JsonSerializer.Serialize(r, Json)).ToList();
        var group = Assert.Single(EstimateWorkflowService.BuildMappingProposalGroups(records, MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.RoadsV1));
        Assert.NotNull(group.RecognitionEvidence);
        Assert.Equal("bus-shelters", group.RecognitionEvidence!.RecognisedFamily);
        Assert.Single(group.RecognitionEvidence.Subjects);

        var catalog = Catalog();
        var proposals = MappingProposalEngine.Propose(new[] { group }, catalog);
        output.WriteLine("proposals=" + string.Join(" || ", proposals.Select(p => p.ProposedCode + ":" + p.Score + ":" + p.CatalogDescription)));
        output.WriteLine("family=" + string.Join(",", group.RecognitionEvidence.FamilyCandidateCodes.Select(code =>
            code + "=" + (catalog.Items.TryGetValue(code, out var item) ? item.UnitRaw + ":" + item.Description : "missing"))));
        Assert.NotEmpty(proposals);
        Assert.All(proposals, p =>
        {
            Assert.Equal("PROPOSED_UNAPPROVED", p.Status);
            Assert.Equal("יח'", p.MeasuredUnit);
            Assert.True(catalog.Items.ContainsKey(p.ProposedCode), "only existing catalog items are proposed");
            Assert.Contains(p.Reasons, reason => reason.Contains("לא אישור"));
        });
        Assert.Contains(proposals, p => p.Reasons.Any(reason => reason.Contains("ev_block_attributes")));
        // The recognised family's own item is priced per "קומפ'" and the measurement is in "יח'": the exact-unit
        // filter keeps it out rather than converting silently. It stays for the engineer to choose manually.
        Assert.Contains("U40.02.2340", group.RecognitionEvidence.FamilyCandidateCodes);
        Assert.DoesNotContain(proposals, p => p.ProposedCode == "U40.02.2340");
        Assert.Null(MappingProposalEngine.EvidenceRefusal(group));
        Assert.Equal(before, records.Select(r => JsonSerializer.Serialize(r, Json)).ToList());
        Assert.All(records, r => Assert.Null(r.Classification.MappingApprovedBy));

        // Control: the same group without the bridge (the b2 contract) finds nothing on this layer and block name.
        var withoutBridge = group with { RecognitionEvidence = null };
        Assert.Empty(MappingProposalEngine.Propose(new[] { withoutBridge }, catalog));
    }

    [Fact]
    public void ContradictingObjectEvidenceIsARefusalTheEngineerSees()
    {
        (string, string) property = (EvidenceKeys.BlockProps, "[{\"name\":\"TYPE\",\"value\":\"SEWER\"}]");
        var records = new[] { Record(Attribute("WATER"), property), Record(Attribute("WATER"), property) };
        var group = Assert.Single(EstimateWorkflowService.BuildMappingProposalGroups(records, MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.RoadsV1));
        Assert.True(group.RecognitionEvidence!.Contradicted);
        Assert.Empty(MappingProposalEngine.Propose(new[] { group }, Catalog()));
        var refusal = MappingProposalEngine.EvidenceRefusal(group);
        Assert.NotNull(refusal);
        Assert.Contains("סותרות", refusal);
        Assert.Equal(2, group.ObjectCount);
    }

    [Fact]
    public void AnUnapprovedProfileRuleIsStoppedByContradictingEvidenceLikeTheSearch()
    {
        (string, string) property = (EvidenceKeys.BlockProps, "[{\"name\":\"TYPE\",\"value\":\"SEWER\"}]");
        var contradicted = Assert.Single(EstimateWorkflowService.BuildMappingProposalGroups(
            new[] { Record(Attribute("WATER"), property), Record(Attribute("WATER"), property) }, MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.RoadsV1));
        var agreeing = Assert.Single(EstimateWorkflowService.BuildMappingProposalGroups(
            new[] { Record(Attribute("BUS SHELTER")), Record(Attribute("BUS SHELTER")) }, MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.RoadsV1));
        var profile = new ProjectProfile { ProfileId = "TEST-ONLY-CURATED" };
        profile.Estimate.QuantitySources.Rules.Add(new()
        {
            RuleKey = contradicted.RuleKey, LayerPattern = "asdasd23423", MeasurementKind = "count", ExpectedUnit = "יח'",
            CandidateCatalogCode = "U40.02.3000",
        });
        var catalog = Catalog();

        // Control: the same unapproved exact named-count rule does propose for a group whose evidence agrees.
        Assert.Contains(EstimateWorkflowService.CuratedRuleProposals(new[] { agreeing }, catalog, profile),
            p => p.ProposedCode == "U40.02.3000" && p.Status == "PROPOSED_UNAPPROVED");
        Assert.NotNull(MappingProposalEngine.EvidenceRefusal(contradicted));
        Assert.Empty(EstimateWorkflowService.CuratedRuleProposals(new[] { contradicted }, catalog, profile));
    }
}
