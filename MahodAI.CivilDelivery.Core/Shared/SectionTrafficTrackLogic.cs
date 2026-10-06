using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>Source-arrow tracks inside an unchanged carriageway envelope, not inferred lanes.</summary>
public static class SectionTrafficTrackLogic
{
    public sealed record Track(double OffsetM, string EvidenceDigest,
        IReadOnlyList<TrafficDirectionEvidenceLogic.ArrowEvidence> Arrows);
    public sealed record Resolution(IReadOnlyList<Track> Tracks, string? Error)
    {
        public bool IsResolved => Error == null;
    }

    public static Resolution Resolve(SectionCutFrame frame, double alignmentHeadingRadians,
        double from, double to, IReadOnlyList<TrafficDirectionEvidenceLogic.ArrowEvidence> arrows,
        string? sourceAuthorityDigest = null)
    {
        if (frame == null || !double.IsFinite(alignmentHeadingRadians) ||
            !double.IsFinite(from) || !double.IsFinite(to) || to <= from ||
            (sourceAuthorityDigest != null && !SectionVehicleDirectionPlanner.IsSha256(sourceAuthorityDigest)))
            return Fail("invalid-track-envelope");
        var admitted = new List<(TrafficDirectionEvidenceLogic.ArrowEvidence Arrow, double Offset, double Along)>();
        foreach (var arrow in arrows ?? Array.Empty<TrafficDirectionEvidenceLogic.ArrowEvidence>())
        {
            if (arrow == null) return Fail("missing-track-source");
            var sourceClass = TrafficDirectionEvidenceLogic.ClassifySource(arrow.Layer, arrow.BlockName);
            if (sourceClass == TrafficDirectionEvidenceLogic.SourceClass.Unapproved) continue;
            if (!double.IsFinite(arrow.X) || !double.IsFinite(arrow.Y) || !double.IsFinite(arrow.HeadingRadians))
                return Fail("nonfinite-track-source");
            var offset = frame.OffsetAtAlignmentProjection(new(arrow.X, arrow.Y));
            var onCut = frame.PointAt(offset);
            var along = (arrow.X - onCut.X) * Math.Cos(alignmentHeadingRadians) +
                        (arrow.Y - onCut.Y) * Math.Sin(alignmentHeadingRadians);
            if (Math.Abs(along) > TrafficDirectionEvidenceLogic.DefaultMaxSearchDistanceM ||
                offset <= from + 0.05 || offset >= to - 0.05) continue;
            if (sourceClass != TrafficDirectionEvidenceLogic.SourceClass.ApprovedTrafficArrow)
                return Fail("mixed-track-source-classes");
            // The collector explicitly uses null Source for host-owned arrows;
            // the host PLAN authority plus handle scopes that identity. An empty
            // external chain is not a substitute for readable provenance.
            if ((arrow.Source != null && string.IsNullOrWhiteSpace(arrow.Source)) || string.IsNullOrWhiteSpace(arrow.HandlePath))
                return Fail("missing-track-source-identity");
            admitted.Add((arrow, offset, along));
        }
        var unique = new List<(TrafficDirectionEvidenceLogic.ArrowEvidence Arrow, double Offset, double Along)>();
        foreach (var group in admitted.GroupBy(item => Identity(item.Arrow), StringComparer.Ordinal))
        {
            if (group.Select(item => Canonical(item.Arrow)).Distinct(StringComparer.Ordinal).Count() != 1)
                return Fail("conflicting-track-source-identity");
            unique.Add(group.First());
        }
        var groups = new List<List<(TrafficDirectionEvidenceLogic.ArrowEvidence Arrow, double Offset, double Along)>>();
        foreach (var item in unique.OrderBy(item => item.Offset).ThenBy(item => Identity(item.Arrow), StringComparer.Ordinal))
        {
            // Preserve the existing source-label separation rule. This does NOT
            // claim lane widths, delimit lanes, or prove that vehicles fit.
            if (groups.Count == 0 || item.Offset - groups[^1][0].Offset >=
                SectionTrafficSpanLabelLogic.DistinctLaneTrackSeparationM)
                groups.Add(new());
            groups[^1].Add(item);
        }
        var tracks = groups.Select(group =>
        {
            // Use an actual source offset, never the envelope midpoint or an
            // invented evenly-spaced lane centre. Repeated arrows remain evidence.
            var representative = group.OrderBy(item => Math.Abs(item.Along))
                .ThenBy(item => Identity(item.Arrow), StringComparer.Ordinal).First();
            var sources = group.Select(item => item.Arrow).OrderBy(Canonical, StringComparer.Ordinal).ToArray();
            var digest = ArtifactHash.Sha256OfText(JsonSerializer.Serialize(new
            {
                contract = "source-arrow-track-v1", from, to, offset = representative.Offset,
                source_authority_digest = sourceAuthorityDigest ?? ArtifactHash.Sha256OfText("[]"),
                sources = sources.Select(Canonical).ToArray()
            }));
            return new Track(representative.Offset, digest, sources);
        }).OrderBy(track => track.OffsetM).ToArray();
        if (tracks.Length < 2) return new(tracks, "composite-envelope-needs-two-source-tracks");
        var width = SectionFurnitureLogic.MinimumStripWidthM(SectionFurnitureLogic.Car);
        if (tracks.Any(track => track.OffsetM - width / 2 < from || track.OffsetM + width / 2 > to))
            return new(tracks, "source-track-vehicle-crosses-envelope");
        if (tracks.Zip(tracks.Skip(1), (a, b) => b.OffsetM - a.OffsetM).Any(gap => gap < width))
            return new(tracks, "source-track-vehicles-overlap-or-lack-clearance");
        return new(tracks, null);
    }

    private static Resolution Fail(string error) => new(Array.Empty<Track>(), error);
    private static string Identity(TrafficDirectionEvidenceLogic.ArrowEvidence arrow) =>
        JsonSerializer.Serialize(new[] { arrow.Source, arrow.HandlePath });
    private static string Canonical(TrafficDirectionEvidenceLogic.ArrowEvidence arrow) => JsonSerializer.Serialize(arrow);
}
