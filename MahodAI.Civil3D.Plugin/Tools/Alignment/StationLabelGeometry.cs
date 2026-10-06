using System;
using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Tools.Alignment
{
    /// <summary>
    /// Pure (host-free) math for station labelling — the part of SetStationLabelsTool that
    /// doesn't touch AutoCAD, so it can be unit-tested: which stations get a label, the
    /// Israeli chainage text (K+SSS), and keeping label text upright.
    /// </summary>
    public static class StationLabelGeometry
    {
        /// <summary>
        /// Stations to label between <paramref name="start"/> and <paramref name="end"/>:
        /// the multiples of <paramref name="interval"/> in range, with the exact start and
        /// end stations always included (so the road's ends are annotated). Mirrors
        /// Al_SetElevations' "label where station % interval == 0" plus the endpoints.
        /// </summary>
        public static List<double> BuildStationList(double start, double end, double interval)
        {
            var list = new List<double>();
            if (end < start || interval <= 0)
            {
                if (end >= start) list.Add(start);
                return list;
            }

            list.Add(start);
            double first = Math.Ceiling(start / interval) * interval;
            for (double s = first; s <= end + 1e-6; s += interval)
            {
                if (s <= start + 1e-6) continue;     // start already added
                if (s >= end - 1e-6) break;           // end added below
                list.Add(s);
            }
            if (Math.Abs(end - start) > 1e-6) list.Add(end);
            return list;
        }

        /// <summary>
        /// Israeli chainage text in K+SSS form (e.g. 1250.5 → "1+250.50", 50 → "0+050",
        /// 1250 → "1+250"). Whole metres print without a fraction.
        /// </summary>
        public static string FormatChainage(double station)
        {
            if (station < 0) station = 0;
            int km = (int)Math.Floor(station / 1000.0);
            double m = station - km * 1000.0;
            return Math.Abs(m - Math.Round(m)) < 1e-6
                ? $"{km}+{(int)Math.Round(m):000}"
                : $"{km}+{m:000.00}";
        }

        /// <summary>
        /// Section index for 'Add Section' mode: how many <paramref name="interval"/> steps
        /// from <paramref name="start"/> the station sits at (rounded).
        /// </summary>
        public static int SectionIndex(double station, double start, double interval)
        {
            if (interval <= 0) return 0;
            return (int)Math.Round((station - start) / interval);
        }

        /// <summary>
        /// Rotation (radians) that keeps label text upright: text reads along the tangent
        /// <paramref name="bearing"/>, flipped 180° when it would otherwise point left
        /// (beyond ±90°) so it never renders upside-down.
        /// </summary>
        public static double UprightRotation(double bearing)
        {
            double d = bearing;
            while (d <= -Math.PI) d += 2 * Math.PI;
            while (d > Math.PI) d -= 2 * Math.PI;
            if (d > Math.PI / 2 || d < -Math.PI / 2) d += Math.PI;
            while (d > Math.PI) d -= 2 * Math.PI;
            return d;
        }
    }
}
