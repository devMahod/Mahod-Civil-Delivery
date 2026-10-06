using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Geometry;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

/// <summary>Pure PLAN/placement/VERIFY agreement; track positions never become dimension boundaries.</summary>
internal static class SectionTrafficPlanContract
{
    internal static string SourceAuthorityDigest(IReadOnlyList<SectionExternalSourceEvidence> sources)
    {
        if (sources.Any(source => !SectionVehicleDirectionPlanner.IsSha256(source.Sha256) ||
            (source.RequiresLiveDatabase && string.IsNullOrWhiteSpace(source.LiveDatabaseRevision))))
            throw new InvalidOperationException("Source-track authorities are incomplete.");
        // File relocation with identical bytes does not change a source track;
        // changed bytes/live database DO invalidate an explicit track decision.
        // Host bytes are intentionally absent: saving created annotations must
        // not invalidate unchanged source geometry and its exact handle paths.
        return ArtifactHash.Sha256OfText(JsonSerializer.Serialize(sources.Select(source => new
        {
            sha256 = source.Sha256.ToLowerInvariant(), source.SourceChain,
            source.RequiresLiveDatabase, source.LiveDatabaseRevision
        }).OrderBy(source => source.SourceChain, StringComparer.Ordinal).ThenBy(source => source.sha256, StringComparer.Ordinal).ToArray()));
    }

    internal static int VehicleInstances(SectionPlanRecord record) =>
        record.PresentationCoverage.VehicleStripCount +
        record.CompositeTrafficEnvelopes.Sum(envelope => envelope.SourceTracks.Count - 1);

    internal static int OfficeCarInstances(SectionPlanRecord record) =>
        record.PresentationCoverage.OfficeCarStripCount +
        record.CompositeTrafficEnvelopes.Sum(envelope => envelope.SourceTracks.Count - 1);

    internal static string? Validate(SectionPlanRecord record)
    {
        // Historical plans remain readable under their original one-per-strip
        // contract. New PLANs explicitly capture every composite envelope.
        if (record.CompositeTrafficEnvelopes.Count == 0)
            return record.TrafficDirections.Any(row => row.TrackEvidenceDigest != null)
                ? "track-direction-without-source-envelope" : null;
        if (record.PresentationCoverage.ResolvedSpans.Where(span => span.Label == "נתיבי נסיעה")
            .Any(span => record.CompositeTrafficEnvelopes.Count(envelope =>
                envelope.FromOffsetM == span.FromOffsetM && envelope.ToOffsetM == span.ToOffsetM) != 1))
            return "composite-strip-without-exact-source-track-envelope";
        var frame = SectionCutGeometry.RequireFrame(record);
        foreach (var envelope in record.CompositeTrafficEnvelopes)
        {
            if (record.CompositeTrafficEnvelopes.Count(other => other.FromOffsetM == envelope.FromOffsetM &&
                other.ToOffsetM == envelope.ToOffsetM) != 1 ||
                record.PresentationCoverage.ResolvedSpans.Count(span => span.FromOffsetM == envelope.FromOffsetM &&
                    span.ToOffsetM == envelope.ToOffsetM && span.Label == "נתיבי נסיעה") != 1)
                return "track-envelope-does-not-match-one-resolved-strip";
            var recomputed = SectionTrafficTrackLogic.Resolve(frame,
                record.SelectedCrossing!.TangentDeg * Math.PI / 180,
                envelope.FromOffsetM, envelope.ToOffsetM,
                envelope.SourceTracks.SelectMany(track => track.Arrows).ToArray(),
                SourceAuthorityDigest(envelope.SourceAuthorities));
            if (!recomputed.IsResolved) return recomputed.Error;
            if (recomputed.Tracks.Count != envelope.SourceTracks.Count || recomputed.Tracks.Any(expected =>
                envelope.SourceTracks.Count(actual => actual.OffsetM == expected.OffsetM &&
                    actual.EvidenceDigest == expected.EvidenceDigest) != 1))
                return "source-track-evidence-does-not-recompute";
            var directions = record.TrafficDirections.Where(row => row.FromOffsetM == envelope.FromOffsetM &&
                row.ToOffsetM == envelope.ToOffsetM).ToArray();
            if (directions.Length != recomputed.Tracks.Count || recomputed.Tracks.Any(track =>
                directions.Count(row => row.TrackEvidenceDigest == track.EvidenceDigest &&
                    row.LaneMidOffsetM == track.OffsetM && row.StripLabel == "נתיבי נסיעה" &&
                    row.StripKind == "road" && row.EvidenceMode == "motor") != 1))
                return "source-tracks-and-direction-rows-are-not-one-to-one";
        }
        if (record.TrafficDirections.Count != VehicleInstances(record) ||
            record.TrafficDirections.Where(row => row.TrackEvidenceDigest != null).Any(row =>
                record.CompositeTrafficEnvelopes.SelectMany(envelope => envelope.SourceTracks)
                    .Count(track => track.EvidenceDigest == row.TrackEvidenceDigest) != 1))
            return "source-track-direction-coverage-mismatch";
        return null;
    }
}
