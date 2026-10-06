using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>Region boundaries are quantity discontinuities, even when a gap is shorter
/// than the maximum sampling interval. No stations or zero-area samples are invented.</summary>
public static class CorridorRegionQuantityPolicy
{
    public sealed record Region(string SeriesId, double StartStation, double EndStation);

    /// <summary>A fully read, valid station collection has missing coverage. This is
    /// not a native/API read failure and does not authorize any inferred samples.</summary>
    public sealed class CoverageIncompleteException : InvalidOperationException
    {
        public CoverageIncompleteException(string message) : base(message) { }
    }

    public static string SeriesId(string baselineSeriesId, int regionOrdinal) =>
        !string.IsNullOrWhiteSpace(baselineSeriesId) && regionOrdinal > 0
            ? $"{baselineSeriesId}/region-{regionOrdinal:D3}"
            : throw new ArgumentException("Region identity requires a baseline and a positive ordinal.");

    public static void RequireNonOverlappingRegions(IReadOnlyList<Region> regions)
    {
        if (regions.Count == 0) throw new ArgumentException("Baseline has no readable regions.");
        if (regions.Any(r => r == null || string.IsNullOrWhiteSpace(r.SeriesId) ||
                !double.IsFinite(r.StartStation) || !double.IsFinite(r.EndStation) ||
                Station(r.EndStation) <= Station(r.StartStation)))
            throw new ArgumentException("Region identity or station range is invalid.");
        if (regions.Select(r => r.SeriesId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != regions.Count)
            throw new ArgumentException("Region series identity is duplicated.");
        var ordered = regions.OrderBy(r => r.StartStation).ToList();
        for (var i = 1; i < ordered.Count; i++)
            if (Station(ordered[i].StartStation) < Station(ordered[i - 1].EndStation))
                throw new ArgumentException("Baseline regions overlap; quantities cannot be integrated safely.");
    }

    /// <summary>Validate observed native region assemblies against the baseline schedule
    /// and exact region endpoints. A shared endpoint in TWO regions is allowed; a repeated
    /// station within ONE region is not a second cross section to sum.</summary>
    public static CorridorQuantityLogic.StationSeries RequireSchedule(Region region,
        IReadOnlyList<double> baselineStations, IReadOnlyList<double> observedStations)
    {
        RequireNonOverlappingRegions(new[] { region });
        if (baselineStations.Any(s => !double.IsFinite(s)) || observedStations.Any(s => !double.IsFinite(s)))
            throw new ArgumentException("Region station schedule contains non-finite values.");
        var start = Station(region.StartStation);
        var end = Station(region.EndStation);
        var observed = observedStations.Select(Station).OrderBy(s => s).ToList();
        if (observed.Count != observed.Distinct().Count())
            throw new ArgumentException("Duplicate applied-assembly station inside one region.");
        // Invalid observations must not be downgraded to a completeness finding,
        // even if this region also lacks an endpoint or a second observation.
        if (observed.Any(s => s < start || s > end))
            throw new ArgumentException("Applied-assembly station is outside its region range.");
        if (observed.Count < 2 || observed[0] != start || observed[^1] != end)
            throw new CoverageIncompleteException(FormattableString.Invariant(
                $"Region '{region.SeriesId}' has incomplete endpoint coverage: start={start:R}, end={end:R}, observed_count={observed.Count}, first={(observed.Count == 0 ? "none" : observed[0].ToString("R", System.Globalization.CultureInfo.InvariantCulture))}, last={(observed.Count == 0 ? "none" : observed[^1].ToString("R", System.Globalization.CultureInfo.InvariantCulture))}; station comparison rounds to 4 drawing-unit decimals. No missing sample or volume was inferred."));
        var expected = baselineStations.Select(Station).Where(s => s >= start && s <= end)
            .Append(start).Append(end).Distinct().ToHashSet();
        if (expected.Any(s => !observed.Contains(s)))
            throw new CoverageIncompleteException("Region is missing a scheduled applied assembly. No missing sample or volume was inferred.");
        return new CorridorQuantityLogic.StationSeries(region.SeriesId, observed);
    }

    private static double Station(double value) => Math.Round(value, 4);
}
