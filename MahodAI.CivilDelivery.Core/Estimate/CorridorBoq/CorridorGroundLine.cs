using System;
using System.Collections.Generic;

namespace MahodAI.CivilDelivery.Estimate.CorridorBoq;

/// <summary>
/// The existing ground on one section line, ordered by offset (live WEST r8, Codex 17:23). The line runs between the two
/// requested plan points of the Bot's outer offsets. Its ends are those offsets exactly (the metre values the Bot carries,
/// bit for bit), and an end exists only where the surface elevation was measured at that exact point — an end off the
/// surface gets no point and is never extended. Interior samples take their offset from the projection parameter on the line.
/// A sample is the requested end itself only within numeric noise (64 ULP of the coordinate magnitude); any other sample,
/// however close to an end, stays a real breakpoint. Samples outside the line are not part of it.
/// </summary>
public static class CorridorGroundLine
{
    public sealed record Sample(double X, double Y, double Z);
    public sealed record GroundPoint(double OffsetM, double X, double Y, double Z);

    /// <summary>What happened at the line ends, for the evidence and the raw receipt.</summary>
    public sealed class Counters
    {
        /// <summary>Ends measured at the exact requested point.</summary>
        public int Measured { get; set; }
        /// <summary>Samples that were a requested end within numeric noise (replaced by the exact end).</summary>
        public int Replaced { get; set; }
        public double MaxEndShiftM { get; set; }
        /// <summary>Measured ends the sampling did not return (no sample within numeric noise).</summary>
        public int Added { get; set; }
        public double MaxAddedGapM { get; set; }
        /// <summary>Ends off the surface: no point, the ground stops short (a coverage gap the kernel reports).</summary>
        public int OffSurface { get; set; }
        /// <summary>Samples outside the line (beyond an end by more than numeric noise).</summary>
        public int Outside { get; set; }
    }

    /// <summary>Numeric-noise radius of plan coordinates of this magnitude: 64 ULP.</summary>
    public static double NoiseRadius(double x1, double y1, double x2, double y2)
    {
        var magnitude = Math.Max(1.0, Math.Max(Math.Max(Math.Abs(x1), Math.Abs(y1)), Math.Max(Math.Abs(x2), Math.Abs(y2))));
        return 64 * (Math.BitIncrement(magnitude) - magnitude);
    }

    /// <param name="x1">Plan point of the 'from' offset (drawing units).</param>
    /// <param name="fromM">The 'from' offset in metres (the Bot's value).</param>
    /// <param name="linearToMetres">Drawing unit → metres.</param>
    /// <param name="zFrom">Surface elevation measured at exactly (x1, y1), or null when that point is off the surface.</param>
    public static List<GroundPoint> Order(double x1, double y1, double x2, double y2, double fromM, double toM, double linearToMetres,
        IEnumerable<Sample> samples, double? zFrom, double? zTo, Counters counters)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(counters);
        var line = new List<GroundPoint>();
        double dx = x2 - x1, dy = y2 - y1, len2 = dx * dx + dy * dy;
        if (!double.IsFinite(len2) || len2 <= 0 || !(toM > fromM) || !double.IsFinite(linearToMetres) || linearToMetres <= 0)
            return line;
        var noise = NoiseRadius(x1, y1, x2, y2);
        var nearFrom = double.PositiveInfinity;
        var nearTo = double.PositiveInfinity;
        var interior = new SortedDictionary<double, GroundPoint>();
        foreach (var p in samples)
        {
            if (p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z)) continue;
            var toStart = Math.Sqrt((p.X - x1) * (p.X - x1) + (p.Y - y1) * (p.Y - y1));
            var toEnd = Math.Sqrt((p.X - x2) * (p.X - x2) + (p.Y - y2) * (p.Y - y2));
            nearFrom = Math.Min(nearFrom, toStart);
            nearTo = Math.Min(nearTo, toEnd);
            if (toStart <= noise || toEnd <= noise)
            {
                counters.Replaced++;
                counters.MaxEndShiftM = Math.Max(counters.MaxEndShiftM, Math.Min(toStart, toEnd) * linearToMetres);
                continue;
            }
            var t = ((p.X - x1) * dx + (p.Y - y1) * dy) / len2;
            var offsetM = fromM + t * (toM - fromM);
            if (!(offsetM > fromM && offsetM < toM)) { counters.Outside++; continue; }
            interior.TryAdd(offsetM, new GroundPoint(offsetM, p.X, p.Y, p.Z));
        }
        void End(double offsetM, double x, double y, double? z, double near)
        {
            if (z is not { } zz || !double.IsFinite(zz)) { counters.OffSurface++; return; }
            counters.Measured++;
            if (near > noise)
            {
                counters.Added++;
                if (double.IsFinite(near)) counters.MaxAddedGapM = Math.Max(counters.MaxAddedGapM, near * linearToMetres);
            }
            line.Add(new GroundPoint(offsetM, x, y, zz));
        }
        End(fromM, x1, y1, zFrom, nearFrom);
        line.AddRange(interior.Values);
        End(toM, x2, y2, zTo, nearTo);
        return line;
    }
}
