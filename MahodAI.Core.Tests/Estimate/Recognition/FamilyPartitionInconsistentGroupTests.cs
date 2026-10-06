using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.Recognition;

/// <summary>
/// A saved partition rule must not stop every later draft of a scan that also holds a measured group whose records do not
/// share one set of facts (here an unknown drawing unit, as an unsupported INSUNITS source produces): that group is resolved
/// whole, where no family decision can apply, and the partition still applies. Records are SYNTHETIC, TEST ONLY.
/// </summary>
public sealed class FamilyPartitionInconsistentGroupTests
{
    private const string Family = "bus-shelters", Source = "TEST-ONLY-GM-MODEL", Layer = "QX971";
    private static readonly EngineerBoqLibrary Library = EngineerBoqLibrary.RoadsV1;
    private static readonly DateTime At = new(2026, 9, 27, 18, 30, 0, DateTimeKind.Utc);

    private static NeutralQuantityRecord Counted(string id, string subject) => new()
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
                [EvidenceKeys.BlockAttributes] = JsonSerializer.Serialize(new { synthetic_injection = true,
                    source = new { handle = id }, observation = new { attributes = new[] { new { tag = "TEST_CLASS", value = subject } } } }),
            },
        },
        Classification = new QuantityClassification(),
    };

    /// <summary>A line measured in drawing units of an unsupported INSUNITS source: its unit is not one known unit.</summary>
    private static NeutralQuantityRecord Unitless(string id) => new()
    {
        RecordId = id, ProjectProfileId = "TEST-ONLY-PARTITION", RunId = "TEST-ONLY-RUN",
        Source = new QuantitySource { Drawing = "TEST-NOT-A-DRAWING.dwg", DrawingHash = new string('a', 64),
            Handle = id, Xref = Source, EntityType = "Polyline", Layer = Source + "|QX972" },
        Measurement = new QuantityMeasurement
        {
            Kind = "length", Method = "polyline-length", Unit = "יחידת שרטוט", RawValue = 12.5,
            Parameters = new Dictionary<string, string>(),
        },
        Classification = new QuantityClassification(),
    };

    [Fact]
    public void AGroupWithAnUnknownUnitIsResolvedWholeAndTheDraftStillAppliesThePartition()
    {
        var records = new[]
        {
            Counted("TEST-S1", "BUS SHELTER"), Counted("TEST-S2", "BUS SHELTER"), Counted("TEST-U1", "BUS SHELTER"),
            Counted("TEST-U2", "BENCH"), Unitless("TEST-X1"),
        };
        var before = JsonSerializer.Serialize(records);
        var groups = EngineerBoqDraftBuilder.RecognitionGroups(records, Library);
        var unitless = groups.Single(g => g.LayerLeaf == "QX972");
        FamilyDecisionPolicy.GroupFactsProblem(unitless).Should().NotBeNull("an unknown unit is never one proven unit");
        var decision = FamilyDecisionPolicy.CreatePartitionApproval(Family, groups.Single(g => g.LayerLeaf == Layer),
            new[] { "TEST-S1", "TEST-S2", "TEST-U1" }, Library, "TEST ONLY APPROVER", "TEST ONLY: proven partition", At);

        var partitions = FamilyDecisionPolicy.ResolvePartitions(new[] { decision }, groups, Library);
        var passThrough = partitions.Single(p => p.WholeGroup.GroupId == unitless.GroupId);
        passThrough.Group.Should().BeSameAs(unitless, "a group with inconsistent facts is never partitioned");
        passThrough.ValidationScope.Should().BeSameAs(unitless);
        passThrough.Resolution.IsApplied.Should().BeFalse();
        var applied = partitions.Where(p => p.Resolution.IsApplied).SelectMany(p => p.Group.Records).Select(r => r.RecordId).ToList();
        applied.Should().NotBeEmpty();
        applied.Should().BeEquivalentTo(new[] { "TEST-S1", "TEST-S2", "TEST-U1" });
        partitions.SelectMany(p => p.Group.Records).Select(r => r.RecordId)
            .Should().BeEquivalentTo(records.Select(r => r.RecordId), "every record is accounted once");

        var draft = EngineerBoqDraftBuilder.Build(records, Array.Empty<DeliveryFinding>(),
            new CatalogSnapshot { SnapshotId = "TEST-ONLY-NO-PRICES", FileHash = new string('b', 64) },
            new Dictionary<string, string>(), Library,
            new EngineerDraftContext("TEST ONLY", "TEST-ONLY-PARTITION", "TEST-ONLY-RUN", "TEST-NOT-A-DRAWING.dwg",
                "TEST NO PRICES SUPPLIED", Array.Empty<string>(), FamilyDecisions: new[] { decision }, Classifier: LocalFamilyClassifier.Instance));
        draft.AccountedRecords.Should().Be(records.Length);
        draft.Elements.Single(e => e.Rule.Id == Family).IncludedQuantity.Should().Be(3d);
        draft.RecognitionGroups.Should().Contain(g => g.GroupId == unitless.GroupId);
        draft.FamilyResolutions.Should().NotContain(r => r.GroupId == unitless.GroupId && r.IsApplied);
        JsonSerializer.Serialize(records).Should().Be(before);
    }
}
