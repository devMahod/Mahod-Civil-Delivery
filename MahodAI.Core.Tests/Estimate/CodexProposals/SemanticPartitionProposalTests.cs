// Adopted from Codex proposal tests (Work/codex-accept-92), 2026-09-27; unchanged below this line. Adaptations: System.IO using; output falls back to AdoptedTestOutput.Root.
using System.IO;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Decision = MahodAI.CivilDelivery.Shared.ProjectProfile.EstimateProfile.FamilyDecision;

namespace Codex.PartialProposal;

// Synthetic TEST ONLY. Tests the proposed Core seam, not the unchanged builder/service/UI.
public sealed class SemanticPartitionProposalTests
{
    private const string Family = "bus-shelters", Source = "TEST-ONLY-GM-MODEL", Layer = "QX971";
    private const string Approver = "CODEX SYNTHETIC TEST ONLY";
    private static readonly EngineerBoqLibrary Library = EngineerBoqLibrary.RoadsV1;
    private static readonly DateTime At = new(2026, 9, 27, 18, 30, 0, DateTimeKind.Utc);
    private static readonly string[] Selected = { "S1", "S2", "S3" };

    [Fact]
    public void ProvenPartitionSurvivesYamlWithoutChangingOtherRecordsOrDecision()
    {
        var records = Records();
        var before = JsonSerializer.Serialize(records);
        var groups = Groups(records);
        var unrelated = Existing(groups.Single(g => g.LayerLeaf == "VQ204"));
        var unrelatedBefore = JsonSerializer.Serialize(unrelated);
        var decision = Approve(groups.Single(g => g.LayerLeaf == Layer));
        Assert.Empty(FamilyDecisionPolicy.StructuralProblems(decision));
        var selector = Assert.Single(decision.Selectors);
        Assert.Equal(Layer, selector.LayerLeaf);
        Assert.Contains(selector.EvidenceMatch, m => m.Key == EvidenceKeys.BlockAttributes && m.Text == "BUS SHELTER");
        var loaded = RoundTrip(nameof(ProvenPartitionSurvivesYamlWithoutChangingOtherRecordsOrDecision), unrelated, decision);
        var result = FamilyDecisionPolicy.ResolvePartitions(loaded.Estimate.FamilyDecisions, groups, Library);
        AssertApplied(result, "S1", "S2", "S3", "C1");
        AssertCoverage(records, result);
        Assert.Equal(3d, result.Where(p => p.Resolution.DecisionId == decision.DecisionId && p.Resolution.IsApplied)
            .Sum(p => p.Group.Records.Sum(r => r.Measurement.RawValue)));
        Assert.Equal(FamilyDecisionState.NotCovered, For(result, "U1").Resolution.State);
        Assert.Equal(unrelatedBefore, JsonSerializer.Serialize(loaded.Estimate.FamilyDecisions.Single(d => d.DecisionId == unrelated.DecisionId)));
        Assert.Equal(unrelated.DecisionId, For(result, "C1").Resolution.DecisionId);
        Assert.Equal(before, JsonSerializer.Serialize(records));
        Assert.All(loaded.Estimate.FamilyDecisions, d => Assert.Empty(d.ItemApprovals));
        Assert.All(records, r => { Assert.Null(r.Classification.MappingApprovedBy); Assert.Null(r.Classification.CandidateCatalogCode); });
    }

    [Fact]
    public void ExplicitSemanticRuleAdmitsNewMatchingMemberButNotChangedMeaning()
    {
        var original = Records();
        var originalGroups = Groups(original);
        var decision = Approve(originalGroups.Single(g => g.LayerLeaf == Layer));
        var unrelated = Existing(originalGroups.Single(g => g.LayerLeaf == "VQ204"));
        var loaded = RoundTrip(nameof(ExplicitSemanticRuleAdmitsNewMatchingMemberButNotChangedMeaning), unrelated, decision);
        var history = JsonSerializer.Serialize(loaded.Estimate.FamilyDecisions);

        var appended = Records().Append(Record("N1", Layer, "BUS SHELTER")).ToArray();
        var appendedBefore = JsonSerializer.Serialize(appended);
        var expanded = FamilyDecisionPolicy.ResolvePartitions(loaded.Estimate.FamilyDecisions, Groups(appended), Library);
        AssertApplied(expanded, "S1", "S2", "S3", "N1", "C1"); // Explicit reusable semantic rule, not record-bound.
        AssertCoverage(appended, expanded);
        Assert.Equal(6d, appended.Sum(r => r.Measurement.RawValue));
        Assert.Equal(appendedBefore, JsonSerializer.Serialize(appended));

        var changed = Records();
        changed[0] = Record("S1", Layer, "BENCH");
        var changedBefore = JsonSerializer.Serialize(changed);
        var after = FamilyDecisionPolicy.ResolvePartitions(loaded.Estimate.FamilyDecisions, Groups(changed), Library);
        AssertApplied(after, "S2", "S3", "C1");
        Assert.False(For(after, "S1").Resolution.IsApplied);
        Assert.False(For(after, "U1").Resolution.IsApplied);
        Assert.Equal(unrelated.DecisionId, For(after, "C1").Resolution.DecisionId);
        AssertCoverage(changed, after);
        Assert.Equal(changedBefore, JsonSerializer.Serialize(changed));
        Assert.Equal(history, JsonSerializer.Serialize(loaded.Estimate.FamilyDecisions));
    }

    [Fact]
    public void PartitioningDoesNotReinterpretExistingLiteralApprovalFingerprint()
    {
        var records = Records();
        var groups = Groups(records);
        var whole = groups.Single(g => g.LayerLeaf == Layer);
        // A deliberately recorded prior manual override in a SYNTHETIC profile. This tests
        // preservation of intent, not a recommendation to classify a real mixed layer this way.
        var oldLiteral = Existing(whole);
        var newPartition = Approve(whole);
        var unrelated = Existing(groups.Single(g => g.LayerLeaf == "VQ204"));
        var decisions = new[] { oldLiteral, unrelated, newPartition };
        var before = JsonSerializer.Serialize(new { records, decisions });
        var baseline = Assert.Single(FamilyDecisionPolicy.Resolve(new[] { oldLiteral }, new[] { whole }, Library));
        Assert.True(baseline.IsApplied);
        var result = FamilyDecisionPolicy.ResolvePartitions(decisions, groups, Library);
        Assert.Equal(newPartition.DecisionId, For(result, "S1").Resolution.DecisionId);
        var residual = For(result, "U1");
        Assert.Equal(oldLiteral.DecisionId, residual.Resolution.DecisionId);
        Assert.True(residual.Resolution.IsApplied);
        Assert.Equal(whole.Records.Count, residual.ValidationScope.Records.Count);
        Assert.Equal(1, residual.Group.Records.Count);
        Assert.Equal(unrelated.DecisionId, For(result, "C1").Resolution.DecisionId);
        AssertCoverage(records, result);
        Assert.Equal(before, JsonSerializer.Serialize(new { records, decisions }));
    }

    [Fact]
    public void PreviewIsPureAndCannotApproveArbitrarySubsetOrUnavailableEvidence()
    {
        var records = Records();
        var whole = Groups(records).Single(g => g.LayerLeaf == Layer);
        var before = JsonSerializer.Serialize(records);
        var preview = FamilyDecisionPolicy.EvidenceMatchesForPartition(whole, Selected, Family, Library);
        Assert.NotEmpty(preview);
        Assert.Equal(before, JsonSerializer.Serialize(records));
        Assert.Throws<InvalidOperationException>(() => FamilyDecisionPolicy.CreatePartitionApproval(Family, whole,
            new[] { "S1", "S2" }, Library, Approver, "TEST", At));
        Assert.Throws<InvalidOperationException>(() => FamilyDecisionPolicy.CreatePartitionApproval(Family, whole,
            new[] { "S1", "OUTSIDER" }, Library, Approver, "TEST", At));
        foreach (var record in records.Where(r => Selected.Contains(r.RecordId)))
            record.Measurement.Parameters[EvidenceKeys.BlockAttributes + EvidenceKeys.StatusSuffix] = "unavailable:TEST";
        var unavailableBefore = JsonSerializer.Serialize(records);
        Assert.Throws<InvalidOperationException>(() => Approve(Groups(records).Single(g => g.LayerLeaf == Layer)));
        Assert.Equal(unavailableBefore, JsonSerializer.Serialize(records));
        // Preview purity is NOT an execution of dialog Cancel or a profile-write dispatch seam.
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
