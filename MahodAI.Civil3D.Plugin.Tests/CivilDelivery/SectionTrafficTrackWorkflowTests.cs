using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using DataGrid = System.Windows.Controls.DataGrid;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SectionTrafficTrackWorkflowTests
{
    private static readonly DateTime At = new(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc);
    // These bounds are from native PLAN59/STA42676. Arrow positions BELOW are
    // synthetic: that artifact retained only digests, not the source arrow list.
    private const double From = -0.6444394570903852, To = 9.314069968312523;

    [Fact]
    public void SourceAuthorityBindsBytesAndLiveRevision_NotFileRelocationOrHostSave()
    {
        SectionExternalSourceEvidence Source(string path, char hash, string revision) => new()
        {
            SourcePath = path, SourceName = "SM.dwg", SourceChain = "SM", Sha256 = new string(hash, 64),
            RequiresLiveDatabase = true, LiveDatabaseRevision = revision
        };
        var before = SectionTrafficPlanContract.SourceAuthorityDigest(new[] { Source("C:/original/SM.dwg", 'a', "1") });
        SectionTrafficPlanContract.SourceAuthorityDigest(new[] { Source("C:/relocated/SM.dwg", 'a', "1") }).Should().Be(before);
        SectionTrafficPlanContract.SourceAuthorityDigest(new[] { Source("C:/original/SM.dwg", 'b', "1") }).Should().NotBe(before);
        SectionTrafficPlanContract.SourceAuthorityDigest(new[] { Source("C:/original/SM.dwg", 'a', "2") }).Should().NotBe(before);
        Action unreadable = () => SectionTrafficPlanContract.SourceAuthorityDigest(new[] { Source("C:/original/SM.dwg", 'a', "") });
        unreadable.Should().Throw<InvalidOperationException>();
    }

    private static SectionPlanRecord Record()
    {
        var record = new SectionPlanRecord
        {
            RecordId = "cl-7C8F", SectionId = "STA42676", SelectedAlignment = "600", Station = 42676,
            Cl = new() { RecordId = "cl-7C8F", SourceDrawing = "fixture-host.dwg", SourceDrawingHash = new string('a', 64),
                SourceHandle = "7C8F", SourceEntityType = "LINE", SourceLayer = "CL",
                SourceEndpoints = new[] { -13d, 0, 13, 0 }, WcsEndpoints = new[] { -13d, 0, 13, 0 } },
            SelectedCrossing = new() { AlignmentName = "600", Point = new[] { 0d, 0 }, TangentDeg = 90 },
            PresentationCoverage = new() { VehicleStripCount = 1, OfficeCarStripCount = 1, Complete = true },
        };
        record.PresentationCoverage.ResolvedSpans.Add(new()
        {
            FromOffsetM = From, ToOffsetM = To, WidthM = To - From, Label = "נתיבי נסיעה",
            LeftKind = "island", RightKind = "curb", EvidenceSource = "traffic-arrow", EvidenceDigest = new string('b', 64)
        });
        SectionCutFrame.TryCreate(new(-13, 0), new(13, 0), new(0, 0), 90, out var frame).Should().BeTrue();
        var arrows = new[]
        {
            new TrafficDirectionEvidenceLogic.ArrowEvidence(2, 1, Math.PI / 2, "BL-TR-ARRW", "TR-ARW", "SM", "BD91EF/A1"),
            new TrafficDirectionEvidenceLogic.ArrowEvidence(6, 1, -Math.PI / 2, "BL-TR-ARRW", "TR-ARW", "SM", "BD91EF/A2")
        };
        var tracks = SectionTrafficTrackLogic.Resolve(frame!, Math.PI / 2, From, To, arrows);
        tracks.IsResolved.Should().BeTrue(tracks.Error);
        record.CompositeTrafficEnvelopes.Add(new() { FromOffsetM = From, ToOffsetM = To, SourceTracks = tracks.Tracks.ToList() });
        foreach (var track in tracks.Tracks) record.TrafficDirections.Add(Direction(record, track));
        return record;
    }

    private static SectionTrafficDirectionPlan Direction(SectionPlanRecord record, SectionTrafficTrackLogic.Track track)
    {
        var result = SectionVehicleDirectionPlanner.Resolve(record.Cl.SourceDrawingHash, record.Cl.SourceHandle,
            record.SelectedAlignment, track.OffsetM, track.OffsetM, 0, Math.PI / 2, track.Arrows,
            Array.Empty<ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision>(),
            laneFromOffsetM: From, laneToOffsetM: To, trackEvidenceDigest: track.EvidenceDigest,
            laneCutFrame: SectionCutGeometry.RequireFrame(record));
        return new()
        {
            FromOffsetM = From, ToOffsetM = To, LaneMidOffsetM = track.OffsetM, TrackEvidenceDigest = track.EvidenceDigest,
            StripLabel = "נתיבי נסיעה", StripKind = "road", EvidenceMode = "motor", State = "resolved",
            Flow = SectionVehicleDirectionPlanner.FlowToken(result.Flow), OfficeCarView = result.OfficeCarView!.Value.ToString().ToLowerInvariant(),
            DirectionSource = result.DirectionSource, DirectionDigest = result.DirectionDigest, Reason = result.Reason
        };
    }

    [Fact]
    public void FullTrackContract_RoundTripsAndRequiresTwoInstancesWithoutChangingOneEnvelope()
    {
        var record = Record();
        var copy = JsonSerializer.Deserialize<SectionPlanRecord>(JsonSerializer.Serialize(record))!;
        SectionTrafficPlanContract.Validate(copy).Should().BeNull();
        SectionTrafficPlanContract.VehicleInstances(copy).Should().Be(2);
        SectionTrafficPlanContract.OfficeCarInstances(copy).Should().Be(2);
        copy.PresentationCoverage.VehicleStripCount.Should().Be(1);
        copy.PresentationCoverage.ResolvedSpans.Should().ContainSingle();
        copy.PresentationCoverage.DimensionMarks.Should().BeEmpty("source tracks are not new boundary marks");
        copy.TrafficDirections.Select(row => row.Flow).Should().BeEquivalentTo(new[] { "along-alignment", "against-alignment" });
        SectionPlanLogic.ComputeFingerprint(copy).Should().Be(SectionPlanLogic.ComputeFingerprint(record));
    }

    [Theory]
    [InlineData("missing-direction")]
    [InlineData("duplicate-direction")]
    [InlineData("missing-track")]
    [InlineData("missing-envelope")]
    [InlineData("changed-source")]
    public void ApplyAndVerifySharedContractRejectsOmissionDuplicationAndChangedEvidence(string mutation)
    {
        var record = Record(); var before = SectionPlanLogic.ComputeFingerprint(record);
        switch (mutation)
        {
            case "missing-direction": record.TrafficDirections.RemoveAt(1); break;
            case "duplicate-direction": record.TrafficDirections.Add(record.TrafficDirections[0]); break;
            case "missing-track": record.CompositeTrafficEnvelopes[0].SourceTracks.RemoveAt(1); break;
            case "missing-envelope": record.CompositeTrafficEnvelopes.Clear(); break;
            case "changed-source":
                var track = record.CompositeTrafficEnvelopes[0].SourceTracks[0];
                record.CompositeTrafficEnvelopes[0].SourceTracks[0] = track with
                    { Arrows = new[] { track.Arrows[0] with { HeadingRadians = 0.1 } } };
                break;
        }
        SectionTrafficPlanContract.Validate(record).Should().NotBeNull();
        SectionPlanLogic.ComputeFingerprint(record).Should().NotBe(before);
    }

    [Fact]
    public void ExplicitTrackEditPreservesLegacyAuthority_AndReplanUsesOnlyExactTrack()
    {
        var record = Record(); var profile = new ProjectProfile(); var row = record.TrafficDirections[0];
        var old = new ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision
        {
            SourceDrawingHash = record.Cl.SourceDrawingHash, SourceHandle = record.Cl.SourceHandle,
            AlignmentName = record.SelectedAlignment, LaneMidOffsetM = row.LaneMidOffsetM,
            Flow = "along-alignment", ApprovedBy = "Earlier explicit approver", ApprovedAtUtc = At.AddDays(-1)
        };
        profile.Sections.Decisions.TrafficDirections.Add(old); var oldJson = JsonSerializer.Serialize(old);
        var approval = new SectionDecisionProfileService.TrafficDirectionBatchApproval(record, row,
            TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment);
        SectionDecisionProfileService.ApproveEditedTrafficDirectionsBatch(profile, new[] { record }, new[] { approval }, "Explicit test approver", At)
            .Should().Be(1);
        profile.Sections.Decisions.TrafficDirections.Should().HaveCount(2);
        JsonSerializer.Serialize(old).Should().Be(oldJson);
        var current = profile.Sections.Decisions.TrafficDirections.Single(item => item.TrackEvidenceDigest != null);
        current.FromOffsetM.Should().Be(From); current.ToOffsetM.Should().Be(To);
        current.LaneMidOffsetM.Should().Be(row.LaneMidOffsetM);
        current.TrackEvidenceDigest.Should().Be(row.TrackEvidenceDigest);
        var track = record.CompositeTrafficEnvelopes[0].SourceTracks[0];
        var replay = SectionVehicleDirectionPlanner.Resolve(record.Cl.SourceDrawingHash, record.Cl.SourceHandle, record.SelectedAlignment,
            track.OffsetM, track.OffsetM, 0, Math.PI / 2, track.Arrows, profile.Sections.Decisions.TrafficDirections,
            laneFromOffsetM: From, laneToOffsetM: To, trackEvidenceDigest: track.EvidenceDigest,
            laneCutFrame: SectionCutGeometry.RequireFrame(record));
        replay.DirectionSource.Should().Be("manual"); replay.Flow.Should().Be(TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment);
        var profileCopy = JsonSerializer.Deserialize<ProjectProfile>(JsonSerializer.Serialize(profile))!;
        profileCopy.Sections.Decisions.TrafficDirections.Single(item => item.TrackEvidenceDigest != null)
            .TrackEvidenceDigest.Should().Be(track.EvidenceDigest);
    }

    [Fact]
    public void StaleApprovalOrMalformedTrackBatchIsAtomic()
    {
        var record = Record(); var profile = new ProjectProfile(); var before = JsonSerializer.Serialize(profile);
        var stale = JsonSerializer.Deserialize<SectionTrafficDirectionPlan>(JsonSerializer.Serialize(record.TrafficDirections[0])
            .Replace(record.TrafficDirections[0].TrackEvidenceDigest!, new string('c', 64)))!;
        var approval = new SectionDecisionProfileService.TrafficDirectionBatchApproval(record, stale, TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment);
        Action save = () => SectionDecisionProfileService.ApproveEditedTrafficDirectionsBatch(profile, new[] { record }, new[] { approval }, "Reviewer", At);
        save.Should().Throw<InvalidOperationException>();
        JsonSerializer.Serialize(profile).Should().Be(before);
    }

    [Fact]
    public void LegacyPlanRemainsReadableWithOneVehicleAndNoInventedTrack()
    {
        var record = Record(); record.CompositeTrafficEnvelopes.Clear(); record.TrafficDirections.Clear();
        record.TrafficDirections.Add(new()
        {
            FromOffsetM = From, ToOffsetM = To, LaneMidOffsetM = (From + To) / 2, StripLabel = "נתיבי נסיעה",
            StripKind = "road", EvidenceMode = "motor", State = "resolved", Flow = "along-alignment", OfficeCarView = "rear",
            DirectionSource = "arrow", DirectionDigest = new string('c', 64), Reason = "historical midpoint contract"
        });
        SectionTrafficPlanContract.Validate(record).Should().BeNull();
        SectionTrafficPlanContract.VehicleInstances(record).Should().Be(1);
        JsonSerializer.Deserialize<SectionPlanRecord>(JsonSerializer.Serialize(record))!.CompositeTrafficEnvelopes.Should().BeEmpty();
    }

    [Fact]
    public void MixedNewContractCannotSilentlyLeaveSecondCompositeStripOnLegacyMidpoint()
    {
        var record = Record(); record.PresentationCoverage.VehicleStripCount = 2;
        record.PresentationCoverage.OfficeCarStripCount = 2;
        record.PresentationCoverage.ResolvedSpans.Add(new()
        {
            FromOffsetM = -12, ToOffsetM = -5, WidthM = 7, Label = "נתיבי נסיעה", LeftKind = "curb", RightKind = "curb",
            EvidenceSource = "traffic-arrow", EvidenceDigest = new string('d', 64)
        });
        record.TrafficDirections.Add(new()
        {
            FromOffsetM = -12, ToOffsetM = -5, LaneMidOffsetM = -8.5, StripLabel = "נתיבי נסיעה", StripKind = "road",
            EvidenceMode = "motor", State = "resolved", Flow = "along-alignment", OfficeCarView = "rear",
            DirectionSource = "arrow", DirectionDigest = new string('d', 64), Reason = "legacy midpoint is not complete source-track coverage"
        });
        record.TrafficDirections.Count.Should().Be(SectionTrafficPlanContract.VehicleInstances(record));
        SectionTrafficPlanContract.Validate(record).Should().Be("composite-strip-without-exact-source-track-envelope");
    }

    [Fact]
    public void ProductionConsumersRequireSharedTrackContractAndMandatoryLiveSources()
    {
        var source = GetType().Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "MahodPluginSourceDir").Value!;
        string Service(string file) => File.ReadAllText(Path.Combine(source, "CivilDelivery", "Sections", "Services", file));
        Service("SectionDecorationService.cs").Should().Contain("SectionTrafficPlanContract.Validate(record)")
            .And.Contain("trackEnvelope.SourceTracks.Select(track => track.OffsetM)")
            .And.Contain("SectionVehicleDirectionPlanner.TryRestoreResolved(");
        Service("SectionVerifyService.cs").Should().Contain("SectionTrafficPlanContract.Validate(planRecord)")
            .And.Contain("CheckLiveSources(").And.Contain("sourceTrackContractError == null");
    }

    [Fact]
    public void CompiledEditorShowsBothSourceTracks_ExplicitChoiceOnly_CancelLeavesPlanAndProfileUntouched()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var record = Record(); var before = JsonSerializer.Serialize(record);
                var dialog = new TrafficDirectionDecisionDialog(new[] { record }, true);
                var grid = (DataGrid)dialog.FindName("DirectionsGrid"); grid.Items.Count.Should().Be(2);
                dialog.Approvals.Should().BeEmpty();
                var row = (TrafficDirectionDecisionDialog.DirectionRow)grid.Items[0];
                row.Strip.Should().Contain("מסלול חץ");
                row.SelectedChoice = row.Choices.Single(choice => choice.Flow == TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment);
                dialog.Approvals.Should().ContainSingle().Which.Direction.TrackEvidenceDigest.Should().Be(record.TrafficDirections[0].TrackEvidenceDigest);
                dialog.Close(); JsonSerializer.Serialize(record).Should().Be(before);
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(TimeSpan.FromSeconds(20)).Should().BeTrue();
        if (failure != null) throw new InvalidOperationException("Unshown linked production editor test failed.", failure);
    }
}
