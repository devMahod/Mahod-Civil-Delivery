using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Decision = MahodAI.CivilDelivery.Shared.ProjectProfile.EstimateProfile.FamilyDecision;

namespace MahodAI.Core.Tests.Estimate.Recognition;

/// <summary>
/// The scope of a partition rule (int-b review, 28.09): it is fingerprinted on its cited conjunction only, so a per-instance
/// attribute beside it never makes it stale; and the uncovered rest of a proven partition is approvable when the other records of
/// that partition are already covered as the same family, without changing their decision. Records are SYNTHETIC, TEST ONLY.
/// </summary>
public sealed class FamilyPartitionScopeTests
{
    private const string Family = "bus-shelters", Source = "TEST-ONLY-GM-MODEL", Layer = "QX971", Approver = "TEST ONLY APPROVER";
    private static readonly EngineerBoqLibrary Library = EngineerBoqLibrary.RoadsV1;
    private static readonly DateTime At = new(2026, 9, 28, 1, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Shelters = { "TEST-S1", "TEST-S2", "TEST-S3" };

    private static NeutralQuantityRecord Counted(string id, params (string Tag, string Value)[] attributes) => new()
    {
        RecordId = id, ProjectProfileId = "TEST-ONLY-PARTITION", RunId = "TEST-ONLY-RUN",
        Source = new QuantitySource { Drawing = "TEST-NOT-A-DRAWING.dwg", DrawingHash = new string('a', 64),
            Handle = id, Xref = Source, EntityType = "BlockReference", Layer = Source + "|" + Layer },
        Measurement = new QuantityMeasurement
        {
            Kind = "count", Method = "block-count", Unit = "unit", RawValue = 1,
            Parameters = new Dictionary<string, string>
            {
                [EvidenceKeys.Schema] = EvidenceKeys.SchemaV1,
                [EvidenceKeys.Schema + EvidenceKeys.StatusSuffix] = "read",
                [EvidenceKeys.BlockNameEffective] = "TEST_OBJECT",
                [EvidenceKeys.BlockAttributes + EvidenceKeys.StatusSuffix] = "read",
                [EvidenceKeys.BlockAttributes] = JsonSerializer.Serialize(new { synthetic_injection = true, source = new { handle = id },
                    observation = new { attributes = attributes.Select(a => new { tag = a.Tag, value = a.Value }).ToArray() } }),
            },
        },
        Classification = new QuantityClassification(),
    };

    private static RecognitionGroupInput Whole(NeutralQuantityRecord[] records) =>
        EngineerBoqDraftBuilder.RecognitionGroups(records, Library).Single();

    private static IReadOnlyList<FamilyDecisionPolicy.PartitionResolution> Resolve(IReadOnlyList<Decision> decisions,
        NeutralQuantityRecord[] records) =>
        FamilyDecisionPolicy.ResolvePartitions(decisions, EngineerBoqDraftBuilder.RecognitionGroups(records, Library), Library);

    private static IReadOnlyList<string> Applied(IReadOnlyList<Decision> decisions, NeutralQuantityRecord[] records) =>
        Resolve(decisions, records).Where(p => p.Resolution.IsApplied).SelectMany(p => p.Group.Records)
            .Select(r => r.RecordId).OrderBy(id => id, StringComparer.Ordinal).ToList();

    [Fact]
    public void APerInstanceAttributeBesideTheConjunctionNeverMakesThePartitionRuleStale()
    {
        NeutralQuantityRecord Shelter(string id, string number) => Counted(id, ("TEST_CLASS", "BUS SHELTER"), ("NO", number));
        var records = new[] { Shelter("TEST-S1", "1"), Shelter("TEST-S2", "2"), Shelter("TEST-S3", "3"),
            Counted("TEST-U1", ("TEST_CLASS", "BENCH"), ("NO", "4")) };
        var before = JsonSerializer.Serialize(records);
        var decision = FamilyDecisionPolicy.CreatePartitionApproval(Family, Whole(records), Shelters, Library, Approver,
            "TEST ONLY: proven partition", At);
        decision.Selectors.Single().EvidenceMatch.Should().ContainSingle()
            .Which.Text.Should().Be("BUS SHELTER", "a per-instance number is never part of the conjunction");
        var decisions = new[] { decision };
        Applied(decisions, records).Should().Equal(Shelters);

        // A new, a renumbered and a removed member keep the rule: its conjunction is unchanged.
        Applied(decisions, records.Append(Shelter("TEST-N1", "5")).ToArray())
            .Should().Equal("TEST-N1", "TEST-S1", "TEST-S2", "TEST-S3");
        var renumbered = records.ToArray();
        renumbered[1] = Shelter("TEST-S2", "22");
        Applied(decisions, renumbered).Should().Equal(Shelters);
        Applied(decisions, records.Where(r => r.RecordId != "TEST-S3").ToArray()).Should().Equal("TEST-S1", "TEST-S2");
        Applied(decisions, new[] { records[0], records[3] }).Should().Equal("TEST-S1");

        // The cited text read under another attribute is another statement: the partition goes back to review, nothing is
        // silently covered.
        var moved = records.Append(Counted("TEST-N2", ("DESCRIPTION", "BUS SHELTER"), ("NO", "6"))).ToArray();
        var withMoved = Resolve(decisions, moved).Single(p => p.Group.Records.Any(r => r.RecordId == "TEST-N2"));
        withMoved.Resolution.State.Should().Be(FamilyDecisionState.Stale);
        withMoved.Resolution.StaleReason.Should().Be(FamilyDecisionPolicy.StaleEvidence);
        // So does a changed meaning.
        var changed = records.ToArray();
        changed[0] = Counted("TEST-S1", ("TEST_CLASS", "BENCH"), ("NO", "1"));
        Applied(decisions, changed).Should().Equal("TEST-S2", "TEST-S3");
        JsonSerializer.Serialize(records).Should().Be(before);
    }

    [Fact]
    public void TheUncoveredRestOfAProvenPartitionIsApprovableWhenTheRestIsCoveredAsTheSameFamily()
    {
        NeutralQuantityRecord Shelter(string id, string colour) => Counted(id, ("TEST_CLASS", "BUS SHELTER"), ("COLOUR", colour));
        var records = new[] { Shelter("TEST-S1", "RED"), Shelter("TEST-S2", "RED"), Shelter("TEST-S3", "RED"),
            Counted("TEST-U1", ("TEST_CLASS", "BENCH"), ("COLOUR", "RED")) };
        var earlier = FamilyDecisionPolicy.CreatePartitionApproval(Family, Whole(records), Shelters, Library, Approver,
            "TEST ONLY: proven partition", At);
        earlier.Selectors.Single().EvidenceMatch.Select(m => m.Text).Should().BeEquivalentTo(new[] { "BUS SHELTER", "RED" },
            "the incidental colour shared by the three is part of the earlier conjunction");

        // A new shelter without the incidental colour is not covered by the earlier rule.
        var appended = records.Append(Shelter("TEST-N1", "BLUE")).ToArray();
        var before = JsonSerializer.Serialize(appended);
        Applied(new[] { earlier }, appended).Should().Equal(Shelters);
        var whole = Whole(appended);
        var covered = Resolve(new[] { earlier }, appended)
            .Where(p => p.Resolution.IsApplied && p.Resolution.FamilyId == Family)
            .SelectMany(p => p.Group.Records).Select(r => r.RecordId).ToHashSet(StringComparer.Ordinal);
        covered.Should().BeEquivalentTo(Shelters);

        // Without the covered records it is less than the proven partition and refused, as before.
        FluentActions.Invoking(() => FamilyDecisionPolicy.EvidenceMatchesForPartition(whole, new[] { "TEST-N1" }, Family, Library))
            .Should().Throw<InvalidOperationException>();
        // With them, it is the uncovered rest of the proven partition.
        FamilyDecisionPolicy.EvidenceMatchesForPartition(whole, new[] { "TEST-N1" }, Family, Library, covered)
            .Select(m => m.Text).Should().BeEquivalentTo(new[] { "BUS SHELTER", "BLUE" });
        // A covered record is never approved again, and covered records that do not complete the partition do not help.
        FluentActions.Invoking(() => FamilyDecisionPolicy.EvidenceMatchesForPartition(whole, new[] { "TEST-N1", "TEST-S1" }, Family,
            Library, covered)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => FamilyDecisionPolicy.EvidenceMatchesForPartition(whole, new[] { "TEST-N1" }, Family, Library,
            new HashSet<string>(new[] { "TEST-S1" }, StringComparer.Ordinal))).Should().Throw<InvalidOperationException>();

        var later = FamilyDecisionPolicy.CreatePartitionApproval(Family, whole, new[] { "TEST-N1" }, Library, Approver,
            "TEST ONLY: the new shelter", At.AddMinutes(5), covered);
        var both = new[] { earlier, later };
        Applied(both, appended).Should().Equal("TEST-N1", "TEST-S1", "TEST-S2", "TEST-S3");
        var result = Resolve(both, appended);
        result.Single(p => p.Group.Records.Any(r => r.RecordId == "TEST-S1")).Resolution.DecisionId
            .Should().Be(earlier.DecisionId, "the earlier decision keeps its records");
        result.Single(p => p.Group.Records.Any(r => r.RecordId == "TEST-N1")).Resolution.DecisionId.Should().Be(later.DecisionId);
        result.Single(p => p.Group.Records.Any(r => r.RecordId == "TEST-U1")).Resolution.IsApplied.Should().BeFalse();
        result.SelectMany(p => p.Group.Records).Select(r => r.RecordId).Should().OnlyHaveUniqueItems()
            .And.BeEquivalentTo(appended.Select(r => r.RecordId));
        JsonSerializer.Serialize(appended).Should().Be(before);
    }
}
