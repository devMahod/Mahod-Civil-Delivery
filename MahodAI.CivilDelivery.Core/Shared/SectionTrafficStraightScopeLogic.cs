using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// A deliberately bounded automatic-traffic contract. Only native straight
/// alignment segments supplied by the host may establish the local parallel
/// projection used by labels, source tracks and direction. This does not infer
/// lane continuity through curves, spirals, junctions or changing lane widths.
/// No published/raw station conversion or sampling can substitute for a segment.
/// </summary>
public static class SectionTrafficStraightScopeLogic
{
    public const double BoundaryToleranceM = 1e-6;
    public sealed record Segment(P2 Start, P2 End, string SourceKey);
    public sealed record Resolution(
        IReadOnlyList<TrafficDirectionEvidenceLogic.ArrowEvidence> Evidence,
        string? SegmentKey, int RejectedNearCutCount, string? Reason, Segment? ProvenSegment = null)
    {
        public bool HasProvenSegment => ProvenSegment != null && SegmentKey == ProvenSegment.SourceKey;
        public string? CanonicalEvidence { get; init; }
    }

    /// <summary>
    /// Stable, inspectable PLAN proof. Diagnostic text/counts and inventory order
    /// are deliberately absent. Native endpoints/identity and refusal state are
    /// engineering inputs; reversing a line's endpoint order does not change them.
    /// </summary>
    public static string CanonicalEvidenceFor(string? alignmentName, string? alignmentHandle, Resolution scope)
    {
        var segment = scope.ProvenSegment;
        var start = segment?.Start;
        var end = segment?.End;
        if (start.HasValue && end.HasValue && (start.Value.X > end.Value.X ||
            (start.Value.X == end.Value.X && start.Value.Y > end.Value.Y)))
            (start, end) = (end, start);
        return JsonSerializer.Serialize(new
        {
            contract = "traffic-native-straight-scope-v1",
            alignment = alignmentName?.Trim().ToUpperInvariant(),
            alignment_handle = alignmentHandle?.ToUpperInvariant(),
            state = scope.HasProvenSegment ? "proven" : "refused",
            straight_segment = segment == null ? null : new
            {
                identity = segment.SourceKey,
                start = new[] { start!.Value.X, start.Value.Y },
                end = new[] { end!.Value.X, end.Value.Y },
            },
            refusal = scope.HasProvenSegment ? null : scope.Reason?.Split(':')[0],
        });
    }

    public static Resolution Refuse(string reason) =>
        new(Array.Empty<TrafficDirectionEvidenceLogic.ArrowEvidence>(), null, 0, reason);

    public static Resolution Resolve(SectionCutFrame frame, double alignmentHeadingRadians,
        IReadOnlyList<Segment> nativeStraightSegments,
        IReadOnlyList<TrafficDirectionEvidenceLogic.ArrowEvidence> arrows)
    {
        if (frame == null || !double.IsFinite(alignmentHeadingRadians) || nativeStraightSegments == null ||
            nativeStraightSegments.Any(segment => !Valid(segment)))
            return Refuse("invalid-native-straight-segment-evidence");

        var tx = Math.Cos(alignmentHeadingRadians);
        var ty = Math.Sin(alignmentHeadingRadians);
        var matches = nativeStraightSegments.Where(segment =>
        {
            var (ux, uy, _) = Axis(segment);
            return Math.Abs(ux * ty - uy * tx) <= 1e-7 &&
                Math.Abs((frame.Origin.X - segment.Start.X) * uy -
                         (frame.Origin.Y - segment.Start.Y) * ux) <= BoundaryToleranceM &&
                InteriorFoot(segment, frame.Origin) &&
                // On a skewed cut the two ends have different longitudinal feet.
                // They must not silently extend this proof past a curve/PI.
                InteriorFoot(segment, frame.EndpointA) && InteriorFoot(segment, frame.EndpointB);
        }).ToArray();
        if (matches.Length != 1)
            return Refuse(matches.Length == 0 ? "cut-not-contained-in-one-native-straight-segment" :
                "ambiguous-native-straight-segment");

        var admitted = new List<TrafficDirectionEvidenceLogic.ArrowEvidence>();
        var rejectedNearCut = 0;
        foreach (var arrow in arrows ?? Array.Empty<TrafficDirectionEvidenceLogic.ArrowEvidence>())
        {
            if (arrow == null || !double.IsFinite(arrow.X) || !double.IsFinite(arrow.Y)) continue;
            var point = new P2(arrow.X, arrow.Y);
            if (InteriorFoot(matches[0], point)) admitted.Add(arrow);
            else if (TrafficDirectionEvidenceLogic.ClassifySource(arrow.Layer, arrow.BlockName) !=
                     TrafficDirectionEvidenceLogic.SourceClass.Unapproved && NearCut(frame, point))
                rejectedNearCut++;
        }
        return new(admitted, matches[0].SourceKey, rejectedNearCut,
            rejectedNearCut == 0 ? null : "nearby-arrows-outside-proven-straight-segment", matches[0]);
    }

    private static bool Valid(Segment segment)
    {
        if (segment == null || string.IsNullOrWhiteSpace(segment.SourceKey) ||
            !Finite(segment.Start) || !Finite(segment.End)) return false;
        var dx = segment.End.X - segment.Start.X;
        var dy = segment.End.Y - segment.Start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return double.IsFinite(length) && length > 2 * BoundaryToleranceM;
    }

    private static (double X, double Y, double Length) Axis(Segment segment)
    {
        var dx = segment.End.X - segment.Start.X;
        var dy = segment.End.Y - segment.Start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return (dx / length, dy / length, length);
    }

    private static bool InteriorFoot(Segment segment, P2 point)
    {
        var (ux, uy, length) = Axis(segment);
        var along = (point.X - segment.Start.X) * ux + (point.Y - segment.Start.Y) * uy;
        return double.IsFinite(along) && along > BoundaryToleranceM && along < length - BoundaryToleranceM;
    }

    private static bool NearCut(SectionCutFrame frame, P2 point)
    {
        var nearest = frame.PointAt(Math.Clamp(frame.OffsetOf(point), frame.MinOffset, frame.MaxOffset));
        var dx = point.X - nearest.X;
        var dy = point.Y - nearest.Y;
        return Math.Sqrt(dx * dx + dy * dy) <= TrafficDirectionEvidenceLogic.DefaultMaxSearchDistanceM;
    }

    private static bool Finite(P2 point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}
