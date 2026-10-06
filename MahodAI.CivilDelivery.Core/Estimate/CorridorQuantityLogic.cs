using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// The quantity mathematics behind corridor take-off, kept pure so every number
    /// is provable without a Civil host. Two inputs, both read live from Civil:
    ///
    ///   * per-station SHAPE AREAS from the corridor's applied assemblies
    ///     (CalculatedShape.Area per material code — Pave1/Pave2/Base/SubBase...),
    ///     integrated to volumes by the average-end-area method, the same method a
    ///     road engineer's quantity sheet uses;
    ///   * per-station CUT/FILL AREAS between the existing-ground section and the
    ///     design section, integrated the same way.
    ///
    /// These are volume calculations from measured section areas, not estimates
    /// derived from H/HL thickness parameters. They do not establish material
    /// identity or plan/surface area per layer; those require their own explicit
    /// mapping and measurement definition. The September review requested layer
    /// areas, which must not be represented as fulfilled by this volume routine.
    /// </summary>
    public static class CorridorQuantityLogic
    {
        /// <summary>
        /// One material shape sampled at one corridor station. <paramref name="SeriesId"/>
        /// identifies the baseline (or another independent station axis). Equal station
        /// numbers from different baselines must never be integrated together.
        /// </summary>
        public sealed record ShapeSample(
            double Station, string Code, double Area, string SeriesId = "");

        /// <summary>Integrated volume of one material code along a run of stations.</summary>
        public sealed record MaterialVolume(
            string Code, double VolumeM3, double StationFrom, double StationTo,
            int StationCount, double MaxGapM, string SeriesId = "");

        /// <summary>The complete station schedule Civil exposed for one baseline.</summary>
        public sealed record StationSeries(string SeriesId, IReadOnlyList<double> Stations);

        /// <summary>A Civil read that omitted part of a model quantity.</summary>
        public sealed record QuantityReadFailure(string Stage, string Context, string Message);

        /// <summary>
        /// Average-end-area integration per material code:
        /// V = Σ (A(i) + A(i+1)) / 2 × (st(i+1) − st(i)).
        /// Multiple shapes of the same code at one station (left+right lanes) sum
        /// first. A gap wider than <paramref name="maxGapM"/> is never bridged;
        /// runs are reported separately. The gap alone does not prove that the
        /// material stops: MaterialCoverageIssues must separately reject missing
        /// coverage before the remaining runs can be considered complete.
        /// </summary>
        public static List<MaterialVolume> AverageEndArea(
            IEnumerable<ShapeSample> samples, double maxGapM = 50.0)
        {
            var result = new List<MaterialVolume>();
            var groups = samples.GroupBy(s => new
            {
                Series = (s.SeriesId ?? string.Empty).ToUpperInvariant(),
                Code = (s.Code ?? string.Empty).ToUpperInvariant(),
            });
            foreach (var byCode in groups)
            {
                var first = byCode.First();
                var stations = byCode
                    .GroupBy(s => Math.Round(s.Station, 4))
                    .Select(g => (Station: g.Key, Area: g.Sum(s => s.Area)))
                    .OrderBy(s => s.Station)
                    .ToList();

                int runStart = 0;
                double volume = 0, maxGap = 0;
                for (int i = 1; i <= stations.Count; i++)
                {
                    var gap = i < stations.Count ? stations[i].Station - stations[i - 1].Station : double.MaxValue;
                    if (gap <= maxGapM)
                    {
                        volume += (stations[i - 1].Area + stations[i].Area) / 2.0 * gap;
                        maxGap = Math.Max(maxGap, gap);
                        continue;
                    }

                    // run [runStart .. i-1] closes
                    if (i - runStart >= 2 && volume > 1e-9)
                        result.Add(new MaterialVolume(first.Code, volume,
                            stations[runStart].Station, stations[i - 1].Station, i - runStart, maxGap,
                            first.SeriesId ?? string.Empty));
                    runStart = i;
                    volume = 0; maxGap = 0;
                }
            }
            return result
                .OrderBy(v => v.SeriesId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Code, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.StationFrom)
                .ToList();
        }

        /// <summary>
        /// Fail-closed completeness audit for corridor material series. Average-end-
        /// area intentionally refuses to bridge wide gaps, but silently returning the
        /// remaining runs would still look like a complete quantity. This method names
        /// singleton series, unreadable/empty baselines, missing scheduled samples and
        /// starts/stops without an explicit zero/boundary observation.
        /// </summary>
        public static List<string> MaterialCoverageIssues(
            IReadOnlyList<ShapeSample> samples,
            IReadOnlyList<StationSeries> expectedSeries,
            double maxGap)
        {
            var issues = new List<string>();
            if (!double.IsFinite(maxGap) || maxGap <= 0)
            {
                issues.Add("material coverage max gap is not a finite positive value");
                return issues;
            }
            if (expectedSeries.Count == 0)
            {
                issues.Add("corridor has no readable baseline station series for material coverage");
                return issues;
            }

            var expectedById = expectedSeries
                .GroupBy(s => s.SeriesId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.SelectMany(s => s.Stations ?? Array.Empty<double>())
                        .Where(double.IsFinite)
                        .Select(s => Math.Round(s, 4))
                        .Distinct()
                        .OrderBy(s => s)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var unexpected in samples
                         .Select(s => s.SeriesId ?? string.Empty)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(id => !expectedById.ContainsKey(id)))
                issues.Add($"material series '{unexpected}' has samples but no readable baseline station schedule");

            foreach (var (seriesId, expectedStations) in expectedById
                         .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (expectedStations.Count < 2)
                {
                    issues.Add($"material series '{seriesId}' has fewer than two readable corridor stations");
                    continue;
                }

                var seriesSamples = samples.Where(s => string.Equals(
                        s.SeriesId ?? string.Empty, seriesId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (seriesSamples.Count == 0)
                {
                    issues.Add($"material series '{seriesId}' has no readable material shapes");
                    continue;
                }

                foreach (var invalid in seriesSamples.Where(s =>
                             !double.IsFinite(s.Station) || !double.IsFinite(s.Area) ||
                             s.Area < 0 || string.IsNullOrWhiteSpace(s.Code)))
                    issues.Add($"material series '{seriesId}' contains an invalid station/code/area observation");

                foreach (var byCode in seriesSamples
                             .Where(s => double.IsFinite(s.Station) && double.IsFinite(s.Area) &&
                                         s.Area >= 0 && !string.IsNullOrWhiteSpace(s.Code))
                             .GroupBy(s => s.Code.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    var observations = byCode
                        .GroupBy(s => Math.Round(s.Station, 4))
                        .Select(g => (Station: g.Key, Area: g.Sum(x => x.Area)))
                        .OrderBy(x => x.Station)
                        .ToList();

                    // Boundary evidence is independently useful even when only one
                    // station was readable. Report it before the singleton early exit
                    // so remediation names every missing end condition instead of
                    // hiding both behind one generic coverage issue.
                    var beginsInside = observations[0].Station > expectedStations[0] &&
                                       observations[0].Area > 1e-9;
                    var endsInside = observations[^1].Station < expectedStations[^1] &&
                                     observations[^1].Area > 1e-9;
                    if (beginsInside)
                        issues.Add($"material series '{seriesId}', code '{byCode.Key}' starts inside the baseline without a zero/boundary sample");
                    if (endsInside)
                        issues.Add($"material series '{seriesId}', code '{byCode.Key}' ends inside the baseline without a zero/boundary sample");

                    if (observations.Count < 2)
                    {
                        issues.Add($"material series '{seriesId}', code '{byCode.Key}' has a singleton station");
                        continue;
                    }

                    for (var i = 1; i < observations.Count; i++)
                    {
                        var gap = observations[i].Station - observations[i - 1].Station;
                        if (gap > maxGap)
                            issues.Add($"material series '{seriesId}', code '{byCode.Key}' has an unintegrated gap ({gap:F3} drawing units)");
                    }

                    var observedStations = observations.Select(x => x.Station)
                        .ToHashSet();
                    var interiorMissing = expectedStations.Count(station =>
                        station > observations[0].Station &&
                        station < observations[^1].Station &&
                        !observedStations.Contains(station));
                    if (interiorMissing > 0)
                        issues.Add($"material series '{seriesId}', code '{byCode.Key}' is missing {interiorMissing} scheduled station sample(s) inside its observed span");

                }
            }

            return issues.Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Converts swallowed/continued Civil read errors into one deterministic global
        /// blocker. Partial model quantities may remain as evidence, but can never be
        /// priced or exported while this finding is present.
        /// </summary>
        public static DeliveryFinding? BlockingReadFinding(
            IEnumerable<QuantityReadFailure> failures,
            string code,
            string projectProfileId,
            string title)
        {
            var materialized = failures
                .Where(f => f != null)
                .OrderBy(f => f.Stage, StringComparer.Ordinal)
                .ThenBy(f => f.Context, StringComparer.Ordinal)
                .ToList();
            if (materialized.Count == 0) return null;

            var byStage = materialized
                .GroupBy(f => f.Stage, StringComparer.Ordinal)
                .Select(g => $"{g.Key}={g.Count()}");
            var examples = materialized.Take(5)
                .Select(f => $"{f.Stage} [{f.Context}]: {f.Message}");
            return new DeliveryFinding
            {
                Code = code,
                Domain = "estimate",
                Severity = FindingSeverity.Error,
                Title = title,
                Message = string.Join("; ", byStage) + ". " + string.Join(" | ", examples),
                RecommendedAction = "Repair the reported Civil model reads and run a fresh quantity scan.",
                ProjectProfileId = projectProfileId,
            };
        }

        /// <summary>
        /// Earthworks may use only a Sections-owned sample-line group for this profile.
        /// A matching MCD-* name alone is not ownership and can otherwise double-count.
        /// </summary>
        public static bool IsTrustedSectionGroup(
            OwnershipMetadata? ownership, string projectProfileId) =>
            ownership != null &&
            string.Equals(ownership.Feature, "sections", StringComparison.Ordinal) &&
            string.Equals(ownership.Role, "sample-line-group", StringComparison.Ordinal) &&
            string.Equals(ownership.ProjectProfileId, projectProfileId, StringComparison.Ordinal);

        /// <summary>
        /// Validates Civil section points before they may become earthworks evidence.
        /// The caller has already proved that the sample-line group is owned by the
        /// Sections workflow; <paramref name="maxHalfWidthDrawingUnits"/> is that
        /// workflow's configured swath cap, converted to drawing units.
        ///
        /// No point is filtered or repaired. A non-finite coordinate, a point outside
        /// the owned swath, or a chain without a real offset span rejects the complete
        /// surface chain. This is intentionally stricter than presentation rendering:
        /// partial visual geometry may be harmless, while a silently clipped chain can
        /// change priced cut/fill volumes.
        /// </summary>
        public static bool TryNormalizeOwnedSectionPoints(
            IReadOnlyList<(double X, double Y, double Z)> raw,
            double maxHalfWidthDrawingUnits,
            double boundaryToleranceDrawingUnits,
            out List<(double Offset, double Elevation)> points,
            out string failure)
        {
            points = new List<(double Offset, double Elevation)>();
            failure = string.Empty;

            if (!double.IsFinite(maxHalfWidthDrawingUnits) || maxHalfWidthDrawingUnits <= 0)
            {
                failure = "The owned section swath cap is missing or invalid.";
                return false;
            }
            if (!double.IsFinite(boundaryToleranceDrawingUnits) ||
                boundaryToleranceDrawingUnits < 0 ||
                boundaryToleranceDrawingUnits > maxHalfWidthDrawingUnits)
            {
                failure = "The section-point boundary tolerance is invalid.";
                return false;
            }
            if (raw == null || raw.Count < 2)
            {
                failure = "The selected surface section has fewer than two points.";
                return false;
            }

            var bound = maxHalfWidthDrawingUnits + boundaryToleranceDrawingUnits;
            for (var i = 0; i < raw.Count; i++)
            {
                var point = raw[i];
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                    !double.IsFinite(point.Z))
                {
                    failure = $"Section point {i} contains a non-finite coordinate.";
                    return false;
                }
                if (point.X < -bound || point.X > bound)
                {
                    failure = $"Section point {i} offset {point.X:G17} is outside the " +
                              $"owned +/-{maxHalfWidthDrawingUnits:G17} drawing-unit swath.";
                    return false;
                }
            }

            points = raw
                .Select(point => (Offset: point.X, Elevation: point.Y))
                .OrderBy(point => point.Offset)
                .ToList();
            if (points[^1].Offset - points[0].Offset <= 1e-9)
            {
                points.Clear();
                failure = "The selected surface section has no measurable offset span.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Cut and fill areas between the existing-ground polyline and the design
        /// polyline of ONE cross-section, both given as (offset, elevation) chains.
        /// Cut = existing ABOVE design (material to remove); fill = design ABOVE
        /// existing. Evaluated exactly on the overlapping offset domain, with the
        /// crossing points of the two chains inserted so no sliver is miscounted.
        /// </summary>
        public static (double CutArea, double FillArea) CutFill(
            IReadOnlyList<(double Offset, double Elevation)> existing,
            IReadOnlyList<(double Offset, double Elevation)> design)
        {
            var eg = Normalize(existing);
            var ds = Normalize(design);
            if (eg.Count < 2 || ds.Count < 2) return (0, 0);

            var lo = Math.Max(eg[0].Offset, ds[0].Offset);
            var hi = Math.Min(eg[^1].Offset, ds[^1].Offset);
            if (hi - lo < 1e-9) return (0, 0);

            // Breakpoints: every vertex of either chain inside the domain.
            var xs = eg.Select(p => p.Offset).Concat(ds.Select(p => p.Offset))
                .Where(x => x >= lo - 1e-9 && x <= hi + 1e-9)
                .Append(lo).Append(hi)
                .Distinct().OrderBy(x => x).ToList();

            double cut = 0, fill = 0;
            for (int i = 1; i < xs.Count; i++)
            {
                var x0 = xs[i - 1]; var x1 = xs[i];
                var w = x1 - x0;
                if (w < 1e-12) continue;
                var d0 = ElevationAt(eg, x0)!.Value - ElevationAt(ds, x0)!.Value;
                var d1 = ElevationAt(eg, x1)!.Value - ElevationAt(ds, x1)!.Value;

                if (d0 >= 0 && d1 >= 0) cut += (d0 + d1) / 2.0 * w;
                else if (d0 <= 0 && d1 <= 0) fill += (-d0 - d1) / 2.0 * w;
                else
                {
                    // sign change: split at the crossing of the two straight segments
                    var t = d0 / (d0 - d1);              // in (0,1) by construction
                    var xw = w * t;
                    if (d0 > 0) { cut += d0 / 2.0 * xw; fill += -d1 / 2.0 * (w - xw); }
                    else { fill += -d0 / 2.0 * xw; cut += d1 / 2.0 * (w - xw); }
                }
            }
            return (cut, fill);
        }

        /// <summary>Linear interpolation on an offset-sorted chain; null outside its domain.</summary>
        public static double? ElevationAt(
            IReadOnlyList<(double Offset, double Elevation)> chain, double offset)
        {
            if (chain.Count == 0) return null;
            if (offset < chain[0].Offset - 1e-9 || offset > chain[^1].Offset + 1e-9) return null;
            for (int i = 1; i < chain.Count; i++)
            {
                if (offset > chain[i].Offset + 1e-9) continue;
                var (x0, y0) = chain[i - 1];
                var (x1, y1) = chain[i];
                var span = x1 - x0;
                return span < 1e-12 ? y1 : y0 + (y1 - y0) * (offset - x0) / span;
            }
            return chain[^1].Elevation;
        }

        private static List<(double Offset, double Elevation)> Normalize(
            IReadOnlyList<(double Offset, double Elevation)> chain) =>
            chain.OrderBy(p => p.Offset).ToList();
    }
}
