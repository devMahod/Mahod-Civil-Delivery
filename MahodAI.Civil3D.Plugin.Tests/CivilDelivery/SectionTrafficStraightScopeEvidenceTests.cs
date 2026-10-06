using System;
using System.Text.Json;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Segment = MahodAI.CivilDelivery.Shared.SectionTrafficStraightScopeLogic.Segment;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SectionTrafficStraightScopeEvidenceTests
{
    private static SectionPlanRecord Record(string? proof = null) => new()
    {
        RecordId = "cl-A", SelectedAlignment = "axis", Station = 100,
        Cl = new()
        {
            RecordId = "cl-A", SourceDrawing = "synthetic-host.dwg", SourceDrawingHash = new string('a', 64),
            SourceHandle = "A", SourceEntityType = "LINE", SourceLayer = "CL",
            SourceEndpoints = new[] { -10d, 0, 10, 0 }, WcsEndpoints = new[] { -10d, 0, 10, 0 }
        },
        TrafficStraightScopeEvidence = proof,
    };
    private static SectionTrafficStraightScopeLogic.Resolution Scope(Segment[] segments)
    {
        Assert.True(SectionCutFrame.TryCreate(new(-10, 0), new(10, 0), new(0, 0), 90, out var frame));
        return SectionTrafficStraightScopeLogic.Resolve(frame!, Math.PI / 2, segments,
            Array.Empty<TrafficDirectionEvidenceLogic.ArrowEvidence>());
    }
    private static string Proof(SectionTrafficStraightScopeLogic.Resolution scope, string handle = "AB") =>
        SectionTrafficStraightScopeLogic.CanonicalEvidenceFor("axis", handle, scope);

    [Fact]
    public void NativeEndpointProofRoundTrips_AndHistoricalPlanRemainsAbsent()
    {
        var scope = Scope(new[] { new Segment(new(0, -100), new(0, 100), "AB/1") });
        var record = Record(Proof(scope));
        var copy = JsonSerializer.Deserialize<SectionPlanRecord>(JsonSerializer.Serialize(record))!;
        Assert.Equal(record.TrafficStraightScopeEvidence, copy.TrafficStraightScopeEvidence);
        using var proof = JsonDocument.Parse(copy.TrafficStraightScopeEvidence!);
        Assert.Equal("traffic-native-straight-scope-v1", proof.RootElement.GetProperty("contract").GetString());
        Assert.Equal(-100, proof.RootElement.GetProperty("straight_segment").GetProperty("start")[1].GetDouble());
        Assert.Equal(SectionPlanLogic.ComputeFingerprint(record), SectionPlanLogic.ComputeFingerprint(copy));
        var legacy = Record();
        Assert.DoesNotContain("traffic_straight_scope_evidence", JsonSerializer.Serialize(legacy));
        var legacyCopy = JsonSerializer.Deserialize<SectionPlanRecord>(JsonSerializer.Serialize(legacy))!;
        Assert.Equal(SectionPlanLogic.ComputeFingerprint(legacy), SectionPlanLogic.ComputeFingerprint(legacyCopy));
        Assert.NotEqual(SectionPlanLogic.ComputeFingerprint(legacy), SectionPlanLogic.ComputeFingerprint(record));
    }

    [Fact]
    public void AxisGeometryIdentityAndRefusalChangeFingerprint_ButOrderAndDiagnosticsDoNot()
    {
        var line = new Segment(new(0, -100), new(0, 100), "AB/1");
        var unrelated = new Segment(new(50, -100), new(50, 100), "AB/2");
        var first = Scope(new[] { line, unrelated });
        var canonical = Proof(first);
        var fingerprint = SectionPlanLogic.ComputeFingerprint(Record(canonical));
        var reordered = Scope(new[] { unrelated, line with { Start = line.End, End = line.Start } });
        Assert.Equal(canonical, Proof(reordered));
        Assert.Equal(canonical, Proof(first with { Reason = "different diagnostic text", RejectedNearCutCount = 9 }));
        Assert.Equal(fingerprint, SectionPlanLogic.ComputeFingerprint(Record(Proof(reordered))));
        var changed = Scope(new[] { line with { End = new(0, 101) } });
        Assert.NotEqual(fingerprint, SectionPlanLogic.ComputeFingerprint(Record(Proof(changed))));
        Assert.NotEqual(fingerprint, SectionPlanLogic.ComputeFingerprint(Record(Proof(first, "AC"))));
        var refused = SectionTrafficStraightScopeLogic.Refuse("cut-not-contained-in-one-native-straight-segment");
        Assert.NotEqual(fingerprint, SectionPlanLogic.ComputeFingerprint(Record(Proof(refused))));
        using var proof = JsonDocument.Parse(Proof(refused));
        Assert.Equal("refused", proof.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, proof.RootElement.GetProperty("straight_segment").ValueKind);
        Assert.Equal(Proof(SectionTrafficStraightScopeLogic.Refuse("native-straight-scope-unreadable:FirstException")),
            Proof(SectionTrafficStraightScopeLogic.Refuse("native-straight-scope-unreadable:SecondException")));
    }
}
