using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>Bounded readable pages, never an aggregation of station areas into a BOQ.</summary>
public static class MaterialSectionAreaReviewPolicy
{
    public const int PageSize = 50;
    public sealed record Page(string Text, int Offset, int Count, int Total, bool HasPrevious, bool HasNext);

    public static Page Read(IReadOnlyList<MaterialSectionAreaObservation> observations, int requestedOffset)
    {
        var lastPage = observations.Count == 0 ? 0 : (observations.Count - 1) / PageSize * PageSize;
        var offset = Math.Clamp(requestedOffset / PageSize * PageSize, 0, lastPage);
        var count = Math.Min(PageSize, observations.Count - offset);
        var text = new StringBuilder(MaterialSectionAreaEvidence.ScopeNotice);
        var range = $"{(count == 0 ? 0 : offset + 1)}–{offset + count}";
        text.AppendLine($"\n\nתצפיות {Bidi.Ltr(range)} מתוך {observations.Count}; אין סכום שטחים.");
        for (var index = offset; index < offset + count; index++)
        {
            var row = observations[index];
            text.AppendLine($"\n{index + 1}. קורידור: {row.CorridorName}; ציר/סדרה: {row.BaselineSeries}; קוד: {row.ShapeCode}");
            text.AppendLine($"תחנה: {row.Station.ToString("G17", CultureInfo.InvariantCulture)} {row.StationUnit}; " +
                $"שטח חתך: {row.Area.ToString("G17", CultureInfo.InvariantCulture)} {row.AreaUnit}; צורות: {row.ShapeCount}");
            text.AppendLine($"יחידות: {(row.MetricUnitsProven ? "מטריות מוכחות" : "לא מטריות — נדרשת בדיקה")}; " +
                $"כיסוי: {(row.CorridorCoverageProven ? "סדרת תחנות מוכחת" : "חלקי/לא מוכח")}; מקור יחידה: {row.SourceUnits}");
            text.AppendLine($"מקור: {row.DrawingPath}\nSHA-256: {row.DrawingHash}\n" +
                $"עצם: {row.CorridorHandle}; ריצה: {row.RunId}; שיטה: {row.MeasurementMethod}");
        }
        if (observations.Count == 0) text.AppendLine("לא תועדו תצפיות שטח. אין פירוש הדבר ששטחי החומר הם אפס.");
        return new Page(text.ToString().TrimEnd(), offset, count, observations.Count, offset > 0, offset + count < observations.Count);
    }
}
