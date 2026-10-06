using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Turns audited plan-arrow evidence into a strip name without inventing lane
/// boundaries.  A normal-width envelope may be one lane.  A wider carriageway is
/// named in the plural only when two laterally distinct motor-arrow tracks prove
/// that it is composite; repeated arrows along one lane do not meet that bar.
/// </summary>
public static class SectionTrafficSpanLabelLogic
{
    public sealed record Evidence(
        double Offset,
        TrafficDirectionEvidenceLogic.SourceClass SourceClass,
        string StableIdentity);

    public sealed record Resolution(
        string? Label,
        int MotorTrackCount,
        string Reason)
    {
        public bool IsResolved => !string.IsNullOrWhiteSpace(Label);
    }

    public const double MinimumVehicleWidthM = 0.5;
    public const double MaximumSingleLaneWidthM = 6.5;
    public const double MaximumCompositeCarriagewayWidthM = 12.0;
    public const double DistinctLaneTrackSeparationM = 1.75;

    public static Resolution Resolve(double spanWidthM, IReadOnlyCollection<Evidence> evidence)
    {
        if (!double.IsFinite(spanWidthM) || spanWidthM < MinimumVehicleWidthM)
            return new Resolution(null, 0, "span-outside-vehicle-range");

        var usable = (evidence ?? Array.Empty<Evidence>())
            .Where(item => double.IsFinite(item.Offset) &&
                           !string.IsNullOrWhiteSpace(item.StableIdentity))
            .GroupBy(item => item.StableIdentity, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Offset)
            .ToList();
        var classes = usable.Select(item => item.SourceClass).Distinct().ToList();
        if (classes.Count != 1)
            return new Resolution(null, 0,
                classes.Count == 0 ? "no-approved-arrow-evidence" : "mixed-arrow-classes");

        if (classes[0] == TrafficDirectionEvidenceLogic.SourceClass.ExcludedBikeArrow)
        {
            return spanWidthM <= MaximumSingleLaneWidthM
                ? new Resolution("שביל אופניים", 0, "audited-bike-arrow")
                : new Resolution(null, 0, "bike-envelope-too-wide");
        }
        if (classes[0] != TrafficDirectionEvidenceLogic.SourceClass.ApprovedTrafficArrow)
            return new Resolution(null, 0, "source-class-not-approved");

        var trackOffsets = new List<double>();
        foreach (var item in usable)
        {
            if (trackOffsets.Count == 0 ||
                item.Offset - trackOffsets[^1] >= DistinctLaneTrackSeparationM)
                trackOffsets.Add(item.Offset);
        }

        if (spanWidthM <= MaximumSingleLaneWidthM)
            return new Resolution("נתיב נסיעה", trackOffsets.Count,
                "audited-motor-arrow");
        if (spanWidthM <= MaximumCompositeCarriagewayWidthM && trackOffsets.Count >= 2)
            return new Resolution("נתיבי נסיעה", trackOffsets.Count,
                "two-distinct-motor-arrow-tracks");

        return new Resolution(null, trackOffsets.Count,
            spanWidthM > MaximumCompositeCarriagewayWidthM
                ? "carriageway-envelope-too-wide"
                : "wide-envelope-needs-two-distinct-arrow-tracks");
    }
}
