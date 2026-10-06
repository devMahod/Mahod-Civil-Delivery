using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>
/// A measured material SHAPE area at one station, not a plan area or a payable
/// BOQ quantity. Deliberately not a NeutralQuantityRecord: station areas must
/// never flow into pricing, be summed along a road, or be inferred from H/HL.
/// </summary>
public sealed record MaterialSectionAreaObservation(
    string RunId,
    string DrawingPath,
    string DrawingHash,
    string CorridorHandle,
    string CorridorName,
    string BaselineSeries,
    string ShapeCode,
    double Station,
    string StationUnit,
    double Area,
    string AreaUnit,
    int ShapeCount,
    string SourceUnits,
    bool MetricUnitsProven,
    bool CorridorCoverageProven)
{
    public string MeasurementMethod => "civil-calculated-shape-cross-section-area";
    public string QuantityMeaning => "cross-section-area-at-one-station-not-plan-area";
}

public static class MaterialSectionAreaEvidence
{
    public const string ScopeNotice =
        "שטחי חומר בחתכים לפי קוד ותחנה — ראיות מדידה, לא שטח תכניתי ולא כמות לתמחור. " +
        "אין לסכום שטחים מתחנות שונות. קודי החומר מוצגים כפי שנקראו; H1/H2/H3 אינם מוסקים מפרמטרי עובי.";

    public static IReadOnlyList<MaterialSectionAreaObservation> Capture(
        IEnumerable<CorridorQuantityLogic.ShapeSample> samples,
        string runId, string drawingPath, string drawingHash,
        string corridorHandle, string corridorName,
        double linearScale, bool metricUnitsProven, string sourceUnits,
        bool corridorCoverageProven)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(drawingPath) ||
            !CatalogIdentity.IsValidSha256(drawingHash) ||
            string.IsNullOrWhiteSpace(corridorHandle) || string.IsNullOrWhiteSpace(sourceUnits))
            throw new ArgumentException("Material area evidence requires an identified measured source.");
        if (!double.IsFinite(linearScale) || linearScale <= 0 ||
            (!metricUnitsProven && linearScale != 1))
            throw new ArgumentException("Material area unit conversion must be explicit and finite.");

        var input = samples.ToList();
        if (input.Any(s => s == null || !double.IsFinite(s.Station) ||
            !double.IsFinite(s.Area) || s.Area < 0 || string.IsNullOrWhiteSpace(s.Code) ||
            string.IsNullOrWhiteSpace(s.SeriesId)))
            throw new ArgumentException("Material area evidence contains an invalid station, code or area.");

        var result = new List<MaterialSectionAreaObservation>();
        // Exact station values, not rounded station labels. Two close but distinct
        // native stations must not silently become one area observation.
        foreach (var group in input.GroupBy(s => (
                     Series: s.SeriesId.Trim().ToUpperInvariant(),
                     Code: s.Code.Trim().ToUpperInvariant(), s.Station))
                 .OrderBy(g => g.Key.Series, StringComparer.Ordinal)
                 .ThenBy(g => g.Key.Station)
                 .ThenBy(g => g.Key.Code, StringComparer.Ordinal))
        {
            var first = group.First();
            var area = group.Sum(s => s.Area) * linearScale * linearScale;
            var station = first.Station * linearScale;
            if (!double.IsFinite(area) || !double.IsFinite(station))
                throw new ArgumentException("Material area evidence overflowed the declared unit scale.");
            result.Add(new MaterialSectionAreaObservation(
                runId, drawingPath, drawingHash, corridorHandle, corridorName,
                first.SeriesId.Trim(), first.Code.Trim(), station,
                metricUnitsProven ? "מטר" : "יחידת שרטוט",
                area, metricUnitsProven ? "מ\"ר" : "יחידת שרטוט²",
                group.Count(), sourceUnits, metricUnitsProven, corridorCoverageProven));
        }
        return result;
    }
}
