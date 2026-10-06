// Adopted from Codex proposal tests (Work/codex-accept-92), 2026-09-27; unchanged below this line. Adaptations: System.IO using; output falls back to AdoptedTestOutput.Root.
using System.IO;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Decision = MahodAI.CivilDelivery.Shared.ProjectProfile.EstimateProfile.FamilyDecision;

namespace Codex.PartialProposalV2;

// Synthetic tests of the actual Build output, not a replay of the v1 policy tests.
public sealed class PartitionBuilderProposalTests
{
    private const string Family = "bus-shelters", Source = "TEST-ONLY-GM-MODEL", Layer = "QX971";
    private const string Approver = "CODEX SYNTHETIC TEST ONLY";
    private static readonly EngineerBoqLibrary Library = EngineerBoqLibrary.RoadsV1;
    private static readonly DateTime At = new(2026, 9, 27, 18, 30, 0, DateTimeKind.Utc);
    private static readonly string[] Selected = { "S1", "S2", "S3" };

    [Fact]
    public void RealBuildKeepsThreeApprovedOneResidualAndUnrelatedContributionAfterReload()
    {
        var records = Records();
        var groups = Groups(records);
        var unrelated = Existing(groups.Single(g => g.LayerLeaf == "VQ204"));
        var baseline = Build(records, new[] { unrelated });
        var baselineOther = Assert.Single(baseline.Elements.SelectMany(e => e.Sources).Where(s => s.Group.Layer == "VQ204"));
        var partition = Approve(groups.Single(g => g.LayerLeaf == Layer));
        var loaded = RoundTrip(nameof(RealBuildKeepsThreeApprovedOneResidualAndUnrelatedContributionAfterReload), unrelated, partition);
        var stateBefore = JsonSerializer.Serialize(new { records, loaded.Estimate.FamilyDecisions });
        var findings = new[] { new DeliveryFinding { FindingId = "TEST-OVERLAP", Code = EstimateFindingCodes.OverlapRisk,
            Domain = "estimate", Severity = FindingSeverity.Warning, Title = "1 overlapping pair",
            AffectedRecordIds = new List<string> { "S1", "S2" } } };
        var draft = Build(records, loaded.Estimate.FamilyDecisions, findings);
        var bus = Assert.Single(draft.Elements.Where(e => e.Rule.Id == Family));
        Assert.Equal(4d, bus.IncludedQuantity); // 3 newly assigned + 1 unchanged C1.
        var main = Assert.Single(bus.Sources.Where(s => s.Group.Layer == Layer));
        Assert.True(main.Included);
        Assert.Equal(3d, main.Group.Quantity);
        Assert.Equal(3, main.Group.Count);
        Assert.Equal(1, main.OverlapPairs);
        var residual = Assert.Single(draft.UnmappedDesign);
        Assert.Equal(1d, residual.Quantity);
        Assert.Equal(1, residual.Count);
        Assert.Equal(0, residual.OverlapPairs);
        Assert.NotEqual(main.Group.GroupId, residual.GroupId);
        var other = Assert.Single(bus.Sources.Where(s => s.Group.Layer == "VQ204"));
        Assert.Equal(JsonSerializer.Serialize(baselineOther), JsonSerializer.Serialize(other));
        Assert.Equal(new[] { "U1" }, draft.RecognitionProposals.SelectMany(p => p.RecordIds).ToArray());
        Assert.Equal(stateBefore, JsonSerializer.Serialize(new { records, loaded.Estimate.FamilyDecisions }));
        Assert.All(loaded.Estimate.FamilyDecisions, d => Assert.Empty(d.ItemApprovals));
        AssertBuildCoverage(records, draft);
        Observe(nameof(RealBuildKeepsThreeApprovedOneResidualAndUnrelatedContributionAfterReload), draft);
    }

    [Fact]
    public void RealBuildRescanUsesDeclaredMeaningRuleWithoutMergingResidual()
    {
        var records = Records();
        var groups = Groups(records);
        var decisions = new[] { Existing(groups.Single(g => g.LayerLeaf == "VQ204")), Approve(groups.Single(g => g.LayerLeaf == Layer)) };
        var saved = RoundTrip(nameof(RealBuildRescanUsesDeclaredMeaningRuleWithoutMergingResidual), decisions);
        var history = JsonSerializer.Serialize(saved.Estimate.FamilyDecisions);
        var appended = Records().Append(Record("N1", Layer, "BUS SHELTER")).ToArray();
        var expanded = Build(appended, saved.Estimate.FamilyDecisions);
        Assert.Equal(5d, Assert.Single(expanded.Elements.Where(e => e.Rule.Id == Family)).IncludedQuantity);
        Assert.Equal(1d, Assert.Single(expanded.UnmappedDesign).Quantity);
        AssertBuildCoverage(appended, expanded);
        var changed = Records();
        changed[0] = Record("S1", Layer, "BENCH");
        var after = Build(changed, saved.Estimate.FamilyDecisions);
        Assert.Equal(3d, Assert.Single(after.Elements.Where(e => e.Rule.Id == Family)).IncludedQuantity);
        Assert.Equal(2d, Assert.Single(after.UnmappedDesign).Quantity);
        Assert.Equal(new[] { "S1", "U1" }, after.RecognitionProposals.SelectMany(p => p.RecordIds).OrderBy(x => x));
        Assert.Equal(history, JsonSerializer.Serialize(saved.Estimate.FamilyDecisions));
        AssertBuildCoverage(changed, after);
        Observe(nameof(RealBuildRescanUsesDeclaredMeaningRuleWithoutMergingResidual), after);
    }

    [Fact]
    public void RealBuildKeepsLegacyWholeFingerprintAndVerdictWhenPartitionIsAdded()
    {
        var records = Records();
        var groups = Groups(records);
        var whole = groups.Single(g => g.LayerLeaf == Layer);
        // A synthetic pre-existing manual override. Preservation test, not an engineering recommendation.
        var oldLiteral = Existing(whole);
        var unrelated = Existing(groups.Single(g => g.LayerLeaf == "VQ204"));
        var before = Build(records, new[] { oldLiteral, unrelated });
        var newPartition = Approve(whole);
        var decisions = new[] { oldLiteral, unrelated, newPartition };
        var state = JsonSerializer.Serialize(new { records, decisions });
        var after = Build(records, decisions);
        Assert.Equal(5d, Assert.Single(before.Elements.Where(e => e.Rule.Id == Family)).IncludedQuantity);
        Assert.Equal(5d, Assert.Single(after.Elements.Where(e => e.Rule.Id == Family)).IncludedQuantity);
        Assert.Empty(after.UnmappedDesign);
        Assert.All(after.FamilyResolutions, r => Assert.True(r.IsApplied, r.StaleReason));
        var residual = Assert.Single(after.FamilyDecisionPartitions.Where(p => p.Group.Records.Any(r => r.RecordId == "U1")));
        Assert.Equal(oldLiteral.DecisionId, residual.Resolution.DecisionId);
        Assert.Equal(4, residual.ValidationScope.Records.Count);
        Assert.Single(residual.Group.Records);
        Assert.Equal(JsonSerializer.Serialize(before.WholeRecognitionGroups), JsonSerializer.Serialize(after.WholeRecognitionGroups));
        Assert.Equal(state, JsonSerializer.Serialize(new { records, decisions }));
        AssertBuildCoverage(records, after);
        Observe(nameof(RealBuildKeepsLegacyWholeFingerprintAndVerdictWhenPartitionIsAdded), after);
    }

    [Fact]
    public void UnsplitRealBuildRetainsOriginalGroupIdsAndLiteralApprovalPath()
    {
        var records = Records();
        var originalGroups = Groups(records);
        var decision = Existing(originalGroups.Single(g => g.LayerLeaf == "VQ204"));
        var unapproved = Build(records, Array.Empty<Decision>());
        var literal = Build(records, new[] { decision });
        Assert.Empty(literal.FamilyDecisionPartitions);
        Assert.Equal(originalGroups.Select(g => g.GroupId), literal.RecognitionGroups.Select(g => g.GroupId));
        Assert.Equal(originalGroups.Select(g => g.GroupId), literal.WholeRecognitionGroups.Select(g => g.GroupId));
        var residual = Assert.Single(literal.UnmappedDesign);
        Assert.Equal(4d, residual.Quantity);
        Assert.Equal(unapproved.UnmappedDesign.Single(g => g.Layer == Layer).GroupId, residual.GroupId);
        var source = Assert.Single(Assert.Single(literal.Elements).Sources);
        Assert.Equal(string.Join("|", Source, "VQ204", "TEST_OBJECT", "count", "unit", "block", ""), source.Group.GroupId);
        Assert.Equal(1d, source.Group.Quantity);
        AssertBuildCoverage(records, literal);
        Observe(nameof(UnsplitRealBuildRetainsOriginalGroupIdsAndLiteralApprovalPath), literal);
    }

    private static EngineerBoqDraft Build(NeutralQuantityRecord[] records, IReadOnlyList<Decision> decisions,
        IReadOnlyList<DeliveryFinding>? findings = null) =>
        EngineerBoqDraftBuilder.Build(records, findings ?? Array.Empty<DeliveryFinding>(),
            new CatalogSnapshot { SnapshotId = "TEST-ONLY-NO-PRICES", FileHash = new string('b', 64) },
            new Dictionary<string, string>(), Library,
            new EngineerDraftContext("TEST ONLY", "TEST-ONLY-PARTITION", "TEST-ONLY-RUN", "TEST-NOT-A-DRAWING.dwg",
                "TEST NO PRICES SUPPLIED", Array.Empty<string>(), FamilyDecisions: decisions, Classifier: LocalFamilyClassifier.Instance));

    private static void AssertBuildCoverage(NeutralQuantityRecord[] records, EngineerBoqDraft draft)
    {
        var buckets = draft.Elements.SelectMany(e => e.Sources).Select(s => s.Group)
            .Concat(draft.UnmappedDesign).Concat(draft.NotUsedAlternatives).Concat(draft.Existing).Concat(draft.Utilities)
            .Concat(draft.DraftingAids).Concat(draft.CorridorVolumes).Concat(draft.ExcludedByDecision).ToList();
        Assert.Equal(records.Length, draft.AccountedRecords);
        Assert.Equal(records.Length, buckets.Sum(g => g.Count));
        Assert.Equal(records.Sum(r => r.Measurement.RawValue), buckets.Sum(g => g.Quantity));
        Assert.Equal(buckets.Count, buckets.Select(g => g.GroupId).Distinct().Count());
        Assert.Equal(records.Select(r => r.RecordId).OrderBy(x => x),
            draft.RecognitionGroups.SelectMany(g => g.Records).Select(r => r.RecordId).OrderBy(x => x));
        Assert.All(records, r => { Assert.Null(r.Classification.MappingApprovedBy); Assert.Null(r.Classification.CandidateCatalogCode); });
    }

    private static void Observe(string name, EngineerBoqDraft draft)
    {
        var output = Environment.GetEnvironmentVariable("CODEX_ACCEPT92_OUTPUT") ?? Codex.AdoptedTestOutput.Root;
        var observations = new { draft.RecordCount, draft.AccountedRecords, draft.FamilyResolutions,
            contributions = draft.Elements.SelectMany(e => e.Sources.Select(s => new { family = e.Rule.Id, s.Group, s.Included, s.Note })),
            unresolved = draft.UnmappedDesign,
            coverage = draft.RecognitionGroups.Select(g => new { g.GroupId, ids = g.Records.Select(r => r.RecordId) }) };
        File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static NeutralQuantityRecord[] Records() => new[]
    {
        Record("S1", Layer, "BUS SHELTER"), Record("S2", Layer, "BUS SHELTER"),
        Record("S3", Layer, "BUS SHELTER"), Record("U1", Layer, "BENCH"),
        Record("C1", "VQ204", "BUS SHELTER"),
    };

    private static NeutralQuantityRecord Record(string id, string layer, string subject) => new()
    {
        RecordId = id, ProjectProfileId = "TEST-ONLY-PARTITION", RunId = "TEST-ONLY-RUN",
        Source = new QuantitySource { Drawing = "TEST-NOT-A-DRAWING.dwg", DrawingHash = new string('a', 64),
            Handle = id, Xref = Source, EntityType = "BlockReference", Layer = Source + "|" + layer },
        Measurement = new QuantityMeasurement
        {
            Kind = "count", Method = "block-count", Unit = "unit", RawValue = 1,
            Parameters = new Dictionary<string, string>
            {
                [EvidenceKeys.Schema] = EvidenceKeys.SchemaV1,
                [EvidenceKeys.Schema + EvidenceKeys.StatusSuffix] = "read",
                [EvidenceKeys.BlockNameEffective] = "TEST_OBJECT",
                [EvidenceKeys.BlockAttributes + EvidenceKeys.StatusSuffix] = "read",
                [EvidenceKeys.BlockAttributes] = JsonSerializer.Serialize(new { synthetic_injection = true,
                    source = new { handle = id }, observation = new { attributes = new[] { new { tag = "TEST_CLASS", value = subject } } } }),
            },
        },
        Classification = new QuantityClassification(),
    };

    private static IReadOnlyList<RecognitionGroupInput> Groups(NeutralQuantityRecord[] records) =>
        EngineerBoqDraftBuilder.RecognitionGroups(records, Library);
    private static Decision Approve(RecognitionGroupInput whole) => FamilyDecisionPolicy.CreatePartitionApproval(
        Family, whole, Selected, Library, Approver, "TEST: reusable meaning on this source and exact layer", At);
    private static Decision Existing(RecognitionGroupInput whole) => FamilyDecisionPolicy.CreateApproval(
        Family, new[] { whole }, new[] { EvidenceKeys.BlockAttributes }, Library, Approver, "TEST: existing family-only decision", At.AddMinutes(-5));
    private static FamilyDecisionPolicy.PartitionResolution For(IReadOnlyList<FamilyDecisionPolicy.PartitionResolution> result, string id) =>
        Assert.Single(result.Where(p => p.Group.Records.Any(r => r.RecordId == id)));
    private static void AssertApplied(IReadOnlyList<FamilyDecisionPolicy.PartitionResolution> result, params string[] ids) =>
        Assert.Equal(ids.OrderBy(x => x), result.Where(p => p.Resolution.IsApplied)
            .SelectMany(p => p.Group.Records).Select(r => r.RecordId).OrderBy(x => x));
    private static void AssertCoverage(NeutralQuantityRecord[] records, IReadOnlyList<FamilyDecisionPolicy.PartitionResolution> result)
    {
        var flattened = result.SelectMany(p => p.Group.Records).ToList();
        Assert.Equal(records.Length, flattened.Count);
        Assert.Equal(records.Select(r => r.RecordId).OrderBy(x => x), flattened.Select(r => r.RecordId).OrderBy(x => x));
        Assert.Equal(records.Sum(r => r.Measurement.RawValue), flattened.Sum(r => r.Measurement.RawValue));
        Assert.All(flattened, r => Assert.Same(records.Single(original => original.RecordId == r.RecordId), r));
    }

    private static ProjectProfile RoundTrip(string test, params Decision[] decisions)
    {
        var output = Environment.GetEnvironmentVariable("CODEX_ACCEPT92_OUTPUT") ?? Codex.AdoptedTestOutput.Root;
        if (!Path.IsPathFullyQualified(output)) throw new InvalidOperationException("Output root must be absolute");
        var directory = Path.Combine(output, test);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "TEST-profile.yaml");
        var profile = new ProjectProfile { ProfileId = "TEST-ONLY-PARTITION", ProjectName = "TEST ONLY", ProjectStage = "KEEP-UNRELATED-STAGE" };
        profile.Estimate.FamilyDecisions.AddRange(decisions);
        var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(profile, EstimateTraceIdentity.EffectiveProfileHash(profile), file);
        ProjectProfileWriter.Save(profile, file, "TEST partial semantic approval", Approver, expected);
        var loaded = ProjectProfileLoader.LoadFromFile(file);
        Assert.True(loaded.IsUsable, string.Join("; ", loaded.Findings.Select(f => f.Code + ": " + f.Message)));
        Assert.Equal("KEEP-UNRELATED-STAGE", loaded.Profile!.ProjectStage);
        Assert.Equal(EstimateTraceIdentity.EffectiveProfileHash(profile), EstimateTraceIdentity.EffectiveProfileHash(loaded.Profile));
        return loaded.Profile;
    }
}
