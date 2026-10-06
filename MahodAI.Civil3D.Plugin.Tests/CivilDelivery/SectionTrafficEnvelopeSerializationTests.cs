using System.IO;
using System.Text;
using System.Text.Json;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Serialization/provenance regression only; not native traffic geometry acceptance.</summary>
public sealed class SectionTrafficEnvelopeSerializationTests
{
    private static readonly JsonSerializerOptions Json = SectionsWorkflowService.Json;
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Live52RebuildTheory]
    [InlineData(Live52RebuildTheoryAttribute.Plan)]
    [InlineData("sections-plan-20260907-142318-ee95fdb3")]
    public void Historical52AndProducer48Plans_RoundTripAll28RecordsWithoutChangingAnyByte(string run)
    {
        var path = Path.Combine(Live52RebuildTheoryAttribute.Runs, run, "section_plan.json");
        var bytes = File.ReadAllBytes(path);
        var plan = JsonSerializer.Deserialize<SectionPlan>(bytes, Json)!;
        Assert.Equal(28, plan.Records.Count);
        Assert.DoesNotContain("composite_traffic_envelopes", Encoding.UTF8.GetString(bytes));
        // Ordinary caller reads must not turn an absent historical property into [].
        Assert.All(plan.Records, record => Assert.Empty(record.CompositeTrafficEnvelopes));
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan, Json)));
        SectionsWorkflowService.RequirePlanEvidence(plan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsentAndExplicitEmptyArray_RetainTheirDistinctWireShapes(bool explicitlyEmpty)
    {
        var initial = JsonSerializer.Serialize(MinimalRecord(), Json);
        if (explicitlyEmpty)
        {
            // Start from a correctly ordered current wire object, including explicit [].
            var record = MinimalRecord(explicitEmpty: true);
            initial = JsonSerializer.Serialize(record, Json);
        }
        Assert.Equal(explicitlyEmpty, initial.Contains("\"composite_traffic_envelopes\": []", StringComparison.Ordinal));
        var loaded = JsonSerializer.Deserialize<SectionPlanRecord>(initial, Json)!;
        Assert.Empty(loaded.CompositeTrafficEnvelopes);
        Assert.Equal(initial, JsonSerializer.Serialize(loaded, Json));
    }

    [Fact]
    public void PopulatedEnvelopeAndExplicitTrackDirectionDigest_RoundTripEverySourceField()
    {
        var record = MinimalRecord();
        record.CompositeTrafficEnvelopes.Add(Envelope());
        record.TrafficDirections.Add(new SectionTrafficDirectionPlan
        {
            TrackEvidenceDigest = Digest, FromOffsetM = -6, ToOffsetM = 6, LaneMidOffsetM = -2,
            StripLabel = "מיסעה", StripKind = "road", EvidenceMode = "motor", State = "resolved",
            Flow = "against-alignment", OfficeCarView = "front", DirectionSource = "arrow",
            DirectionDigest = Digest, Reason = "synthetic serialization fixture"
        });
        var wire = JsonSerializer.Serialize(record, Json);
        var loaded = JsonSerializer.Deserialize<SectionPlanRecord>(wire, Json)!;
        Assert.Equal(wire, JsonSerializer.Serialize(loaded, Json));
        var envelope = Assert.Single(loaded.CompositeTrafficEnvelopes);
        Assert.Equal(-6, envelope.FromOffsetM);
        Assert.Equal(6, envelope.ToOffsetM);
        var track = Assert.Single(envelope.SourceTracks);
        Assert.Equal(-2, track.OffsetM);
        Assert.Equal(Digest, track.EvidenceDigest);
        Assert.Equal("SM/ABC", Assert.Single(track.Arrows).HandlePath);
        Assert.Equal(Digest, Assert.Single(loaded.TrafficDirections).TrackEvidenceDigest);
        Assert.Equal(Digest, Assert.Single(envelope.SourceAuthorities).Sha256);
        envelope.SourceTracks[0] = track with { OffsetM = -1.5 };
        Assert.NotEqual(ArtifactHash.Sha256OfText(wire),
            ArtifactHash.Sha256OfText(JsonSerializer.Serialize(loaded, Json)));
    }

    [Live52RebuildTheory]
    [InlineData("added-envelope")]
    [InlineData("added-track-digest")]
    public void NewSourceEvidenceCannotBeSmuggledIntoAnOldPublishedPlan(string mutation)
    {
        var path = Path.Combine(Live52RebuildTheoryAttribute.Runs,
            Live52RebuildTheoryAttribute.Plan, "section_plan.json");
        var originalHash = ArtifactHash.Sha256OfFile(path);
        var plan = JsonSerializer.Deserialize<SectionPlan>(File.ReadAllText(path), Json)!;
        SectionsWorkflowService.RequirePlanEvidence(plan);
        if (mutation == "added-envelope") plan.Records[0].CompositeTrafficEnvelopes.Add(Envelope());
        else
        {
            var target = plan.Records.First(record => record.TrafficDirections.Count != 0);
            var wire = JsonSerializer.Serialize(target.TrafficDirections[0], Json);
            // Populate only the new field through its actual serializer contract.
            wire = wire.Insert(1, "\"track_evidence_digest\":\"" + Digest + "\",");
            target.TrafficDirections[0] = JsonSerializer.Deserialize<SectionTrafficDirectionPlan>(wire, Json)!;
        }
        Assert.Throws<InvalidDataException>(() => SectionsWorkflowService.RequirePlanEvidence(plan));
        Assert.Equal(originalHash, ArtifactHash.Sha256OfFile(path));
    }

    [Fact]
    public void ExplicitNullEnvelope_IsNotSilentlyReclassifiedAsHistoricalAbsence()
    {
        var wire = JsonSerializer.Serialize(MinimalRecord(explicitEmpty: true), Json)
            .Replace("\"composite_traffic_envelopes\": []", "\"composite_traffic_envelopes\": null", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SectionPlanRecord>(wire, Json));
    }

    private static SectionPlanRecord MinimalRecord(bool explicitEmpty = false)
    {
        var cl = new ClSourceRecord
        {
            RecordId = "synthetic", SourceDrawing = "local.dwg", SourceDrawingHash = Digest,
            SourceHandle = "ABC", SourceEntityType = "Line", SourceLayer = "CL",
            SourceEndpoints = new[] { 0d, 0, 1, 0 }, WcsEndpoints = new[] { 0d, 0, 1, 0 }
        };
        return explicitEmpty
            ? new SectionPlanRecord { RecordId = "synthetic", Cl = cl, SerializedCompositeTrafficEnvelopes = new() }
            : new SectionPlanRecord { RecordId = "synthetic", Cl = cl };
    }

    private static SectionCompositeTrafficEnvelopePlan Envelope() => new()
    {
        FromOffsetM = -6, ToOffsetM = 6,
        SourceAuthorities = new List<SectionExternalSourceEvidence>
        {
            new() { SourcePath = "local-sm.dwg", SourceName = "SM", Sha256 = Digest, Roles = new() { "traffic" } }
        },
        SourceTracks = new List<SectionTrafficTrackLogic.Track>
        {
            new(-2, Digest, new[] { new TrafficDirectionEvidenceLogic.ArrowEvidence(
                1, 2, 3, "TR-MARK-ARW-BL", "ARROW-A", "SM", "SM/ABC") })
        }
    };
}
