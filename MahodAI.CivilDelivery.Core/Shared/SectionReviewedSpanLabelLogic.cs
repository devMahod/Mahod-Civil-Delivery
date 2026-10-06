using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// A new, explicitly reviewed name may supersede source names at one exact measured
/// interval. Historical manual-profile suggestions keep their old conflict rules.
/// Geometry and all original evidence remain in the caller's canonical digest.
/// </summary>
public static class SectionReviewedSpanLabelLogic
{
    public const string Source = "engineer-reviewed-span-v1";
    private sealed record Evidence(double From, double To, string ApprovedBy, DateTime ApprovedAtUtc);

    public static SectionProjectionLogic.SpanLabelOverride Create(
        double from, double to, string label, string approvedBy, DateTime approvedAtUtc)
    {
        if (!double.IsFinite(from) || !double.IsFinite(to) || to <= from ||
            string.IsNullOrWhiteSpace(label) || label.Trim().Length > 80 ||
            string.IsNullOrWhiteSpace(approvedBy) || approvedAtUtc.Kind != DateTimeKind.Utc || approvedAtUtc == default)
            throw new ArgumentException("An exact finite span, reviewed name and UTC approver provenance are required.");
        return new((from + to) / 2, label.Trim(), Source,
            JsonSerializer.Serialize(new Evidence(from, to, approvedBy.Trim(), approvedAtUtc)));
    }

    public static bool IsExactReview(SectionProjectionLogic.SpanLabelOverride item, double from, double to)
    {
        if (item.Source != Source || string.IsNullOrWhiteSpace(item.Evidence) || item.Evidence.Length > 4096)
            return false;
        try
        {
            var evidence = JsonSerializer.Deserialize<Evidence>(item.Evidence);
            return evidence != null && double.IsFinite(from) && double.IsFinite(to) && to > from &&
                evidence.From == from && evidence.To == to && item.Offset == (from + to) / 2 &&
                !string.IsNullOrWhiteSpace(evidence.ApprovedBy) && evidence.ApprovedAtUtc.Kind == DateTimeKind.Utc && evidence.ApprovedAtUtc != default &&
                !string.IsNullOrWhiteSpace(item.Label) && item.Label.Length <= 80;
        }
        catch (JsonException) { return false; }
    }

    public static IReadOnlyList<SectionProjectionLogic.SpanLabelOverride> ForNameResolution(
        IReadOnlyList<SectionProjectionLogic.SpanLabelOverride> allEvidence,
        IReadOnlyList<(double From, double To, double Width)> widths)
    {
        if (!allEvidence.Any(item => item.Source == Source)) return allEvidence;
        var reviewedWidths = widths.Where(width =>
            allEvidence.Any(item => IsExactReview(item, width.From, width.To))).ToArray();
        // Keep order and all untouched intervals exactly as before. Multiple conflicting
        // explicit reviews remain a conflict; malformed review tokens have no authority.
        return allEvidence.Where(item => item.Source == Source
            ? reviewedWidths.Any(width => IsExactReview(item, width.From, width.To))
            : !reviewedWidths.Any(width => item.Offset > width.From + 1e-9 &&
                                          item.Offset < width.To - 1e-9)).ToArray();
    }
}
