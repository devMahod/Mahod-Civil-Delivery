using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Xunit.Abstractions;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Real production group assembly plus proposal engines; offline, never publishes or approves.</summary>
public sealed class ProposalGroupSourceIdentityTests(ITestOutputHelper output)
{
    private const string Survey = "6422-SP-MEDVA-ALL-2026-MHD";
    private static readonly JsonSerializerOptions Json = new()
    { Converters = { new JsonStringEnumConverter() } };
    private static string RepoRoot => Path.GetFullPath(Path.Combine(typeof(ProposalGroupSourceIdentityTests)
        .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "MahodPluginSourceDir").Value!, ".."));
    private static CatalogSnapshot Catalog() => PriceBookXlsxLoader.Load(
        Path.Combine(RepoRoot, "fixtures", "civil-delivery", "estimate", "nti-urban-082025.xlsx"), "nti-urban-082025");

    [Fact]
    public void ExactNative75SurveyRecord_ServiceGroupMustNotProduceTheThreeKerbProposals()
    {
        var path = Path.Combine(RepoRoot, "fixtures", "civil-delivery", "estimate", "native75-qualified-survey-marker.json");
        Assert.Equal("ed16c727d87b6523a61a6e6dd798d187b473635e335c196d8e4467080536bde1", ArtifactHash.Sha256OfFile(path));
        var record = JsonSerializer.Deserialize<NeutralQuantityRecord>(File.ReadAllText(path), Json)!;
        Assert.Equal("estimate-extract-20260912-170748-af9cdebd", record.RunId);
        Assert.Equal("q-disc-336457-22540-count", record.RecordId);
        Assert.Equal(Survey + "|S_CURB", record.Source.Layer);
        Assert.Equal(Survey + "|S_POINT_E", record.Measurement.Parameters["block_name"]);
        var before = JsonSerializer.Serialize(record, Json);
        var groups = EstimateWorkflowService.BuildMappingProposalGroups(new[] { record }, MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.RoadsV1);
        var heuristic = MappingProposalEngine.Propose(groups, Catalog());
        output.WriteLine("Recorded first native75 group member, not a fresh native measurement or 3895-record replay. Layer=" +
            groups.Single().Layer + "; proposals=" + string.Join(",", heuristic.Select(p => p.ProposedCode)));
        Assert.Empty(heuristic);
        Assert.Empty(EstimateWorkflowService.CuratedRuleProposals(groups, Catalog(), CuratedProfile()));
        Assert.False(MappingProposalEngine.IsProposalEligible(groups.Single()));
        Assert.Equal(record.Source.Layer, groups.Single().Layer);
        Assert.Equal(before, JsonSerializer.Serialize(record, Json));
        Assert.Equal(1, record.Measurement.RawValue);
        Assert.Null(record.Classification.CandidateCatalogCode);
        Assert.Null(record.Classification.MappingApprovedBy);
        Assert.Equal(DeliveryStatus.ReviewRequired, record.Status);
        Assert.Contains(record.Findings, finding => finding.Code == EstimateFindingCodes.Unmapped);
    }

    [Theory]
    [InlineData("S_CURB", "S_POINT_E")]
    [InlineData(Survey + "|S_CURB", Survey + "|S_POINT_E")]
    [InlineData("PARENT|CHILD|S_CURB", "PARENT|CHILD|S_POINT_E")]
    public void HostAndQualifiedKnownMarkersRemainMeasuredButHaveNoAutomaticProposal(string layer, string block)
    {
        var record = Record(layer, block);
        var group = Assert.Single(EstimateWorkflowService.BuildMappingProposalGroups(new[] { record }, MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.RoadsV1));
        Assert.Equal(layer, group.Layer);
        Assert.Empty(MappingProposalEngine.Propose(new[] { group }, Catalog()));
        Assert.Empty(EstimateWorkflowService.CuratedRuleProposals(new[] { group }, Catalog(), CuratedProfile()));
        Assert.Equal(1, group.ObjectCount);
        Assert.Equal(1, group.TotalQuantity);
        Assert.Null(record.Classification.MappingApprovedBy);
    }

    [Theory]
    [InlineData("S_CURB", "KERB_UNIT")]
    [InlineData("DESIGN|S_CURB", "DESIGN|KERB_UNIT")]
    public void GenuineCurbStillReceivesOnlyUnapprovedCompatibleProposals(string layer, string block)
    {
        var record = Record(layer, block);
        var groups = EstimateWorkflowService.BuildMappingProposalGroups(new[] { record }, MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.RoadsV1);
        var proposals = MappingProposalEngine.Propose(groups, Catalog());
        Assert.NotEmpty(proposals);
        Assert.All(proposals, p =>
        {
            Assert.Equal("PROPOSED_UNAPPROVED", p.Status);
            Assert.Equal(layer, p.Layer);
            Assert.Equal("יח'", p.MeasuredUnit);
            Assert.Contains("שפה", p.CatalogDescription);
        });
        Assert.Null(record.Classification.CandidateCatalogCode);
        Assert.Null(record.Classification.MappingApprovedBy);
    }

    [Theory]
    [InlineData("ACTUAL|S_CURB", "OTHER|S_POINT_E")]
    [InlineData("S_CURB", "OTHER|S_POINT_E")]
    public void UnprovenNamespaceDoesNotBecomeAFalseAuxiliaryExclusion(string layer, string block)
    {
        var group = Assert.Single(EstimateWorkflowService.BuildMappingProposalGroups(new[] { Record(layer, block) }, MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.RoadsV1));
        Assert.Equal(layer, group.Layer);
        Assert.True(MappingProposalEngine.IsProposalEligible(group));
    }

    [Fact]
    public void ActualGroupingPreservesAllMeasurementsMetadataAndDoesNotReopenAlreadyMappedRecords()
    {
        var a = Record("SITE|S_CURB", "SITE|KERB_UNIT");
        var b = Record("SITE|S_CURB", "SITE|KERB_UNIT");
        b.Measurement.Parameters["cad_layer_linetype"] = "Dashed";
        var mapped = Record("MAPPED|S_CURB", "MAPPED|KERB_UNIT", "TEST-CODE");
        var inputs = new[] { a, b, mapped };
        var original = JsonSerializer.Serialize(inputs, Json);
        var group = Assert.Single(EstimateWorkflowService.BuildMappingProposalGroups(inputs, MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.RoadsV1));
        Assert.Equal("SITE|S_CURB", group.Layer);
        Assert.Equal(a.Classification.RuleKey, group.RuleKey);
        Assert.Equal(2, group.ObjectCount);
        Assert.Equal(2, group.TotalQuantity);
        var field = Assert.Single(group.CadMetadata!, f => f.Key == "cad_layer_linetype");
        Assert.True(field.IsMixed);
        Assert.Equal(2, field.RecordCount);
        Assert.Equal(new[] { "Continuous", "Dashed" }, field.Values);
        Assert.Equal(original, JsonSerializer.Serialize(inputs, Json));
    }

    [Fact]
    public void LiveServiceUsesTheTestedGroupAssemblyOnlyAfterPublishedScanValidation()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "MahodAI.Civil3D.Plugin", "CivilDelivery", "Estimate", "EstimateWorkflowService.cs"));
        var start = source.IndexOf("internal MappingProposalPublication ProposeMappingsUnpublished(", StringComparison.Ordinal);
        var end = source.IndexOf("internal static List<MappingProposalEngine.DiscoveredGroup> BuildMappingProposalGroups(", start, StringComparison.Ordinal);
        var method = source[start..end];
        Assert.True(method.IndexOf("RequirePublishedScanEvidence(scan)", StringComparison.Ordinal) <
            method.IndexOf("BuildMappingProposalGroups(scan.Records, MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.For(profile), profile.Estimate.FamilyDecisions)", StringComparison.Ordinal));
        Assert.Contains("MappingProposalEngine.Propose(groups, snapshot, reference.CatalogCodes)", method);
        Assert.Contains("CuratedRuleProposals(groups, snapshot, profile)", method);
        Assert.DoesNotContain("SaveApprovedMappings", method);
    }

    private static NeutralQuantityRecord Record(string layer, string block, string? code = null) => new()
    {
        RecordId = "SYNTHETIC-" + Guid.NewGuid().ToString("N"), ProjectProfileId = "TEST-ONLY", RunId = "TEST-ONLY",
        Source = new QuantitySource { Drawing = "TEST-ONLY.dwg", DrawingPath = @"C:\TEST-ONLY\drawing.dwg",
            DrawingHash = new string('a', 64), Handle = "A1", EntityType = "BLOCKREFERENCE", Layer = layer },
        Measurement = new QuantityMeasurement { Kind = "count", Method = "block-count", RawValue = 1, Unit = "יח'",
            Parameters = { ["block_name"] = block, ["cad_block_name_effective"] = block, ["cad_layer_linetype"] = "Continuous" } },
        Classification = new QuantityClassification { RuleKey = $"layer:{SectionProjectionLogic.LayerLeaf(layer)}|count|block:{Uri.EscapeDataString(block)}", CandidateCatalogCode = code },
        Status = DeliveryStatus.ReviewRequired,
    };

    private static ProjectProfile CuratedProfile()
    {
        var profile = new ProjectProfile { ProfileId = "TEST-ONLY-PROPOSAL" };
        profile.Estimate.QuantitySources.Rules.Add(new()
        { RuleKey = "curb-hint", LayerPattern = "*S_CURB", MeasurementKind = "count", ExpectedUnit = "יח'", CandidateCatalogCode = "U51.06.2020" });
        return profile;
    }
}
