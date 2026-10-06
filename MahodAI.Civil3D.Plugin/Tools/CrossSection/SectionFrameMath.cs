using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.Civil3D.Plugin.Tools.CrossSection
{
    /// <summary>
    /// Pure geometry for a cross-section sheet frame. No AutoCAD types — this
    /// file belongs to the Core test lane.
    ///
    /// A cross-section drawn on a sheet is a little coordinate system: an offset
    /// zero (the section centreline), an elevation datum, and a scale. Everything
    /// else — the Offset/Elevation table, the dimension chains, the height labels
    /// — is an annotation of that system. Decode it once and every question
    /// ("what elevation is this vertex?", "where does offset 4.22 sit?") becomes
    /// arithmetic.
    ///
    /// The scale is MEASURED from the drawing's own elevation ruler, never
    /// assumed. On the RD4 sheets it is 1:1 both ways, but a sheet at 1:100/1:50
    /// decodes correctly through the same code.
    /// </summary>
    public sealed class SectionFrameMath
    {
        /// <summary>Drawing X of offset zero (the section centreline).</summary>
        public double CentrelineX { get; }

        /// <summary>Drawing Y of elevation zero.</summary>
        public double Datum { get; }

        /// <summary>Drawing units per metre of elevation.</summary>
        public double VerticalScale { get; }

        /// <summary>Drawing units per metre of offset.</summary>
        public double HorizontalScale { get; }

        public SectionFrameMath(double centrelineX, double datum, double verticalScale, double horizontalScale = 1.0)
        {
            if (Math.Abs(verticalScale) < 1e-9)
                throw new ArgumentOutOfRangeException(nameof(verticalScale), "Vertical scale cannot be zero.");
            if (Math.Abs(horizontalScale) < 1e-9)
                throw new ArgumentOutOfRangeException(nameof(horizontalScale), "Horizontal scale cannot be zero.");

            CentrelineX = centrelineX;
            Datum = datum;
            VerticalScale = verticalScale;
            HorizontalScale = horizontalScale;
        }

        /// <summary>
        /// Builds a frame from the sheet's own annotation.
        /// </summary>
        /// <param name="centrelineX">X of the section centreline entity.</param>
        /// <param name="gridTopY">Y of the top edge of the Offset/Elevation table box.</param>
        /// <param name="rulerLabels">
        /// The left-hand elevation ruler: (elevation value, drawing Y) for each
        /// labelled mark. At least two are required — their spacing IS the
        /// vertical scale. The lowest one sits on <paramref name="gridTopY"/>.
        /// </param>
        public static SectionFrameMath FromRuler(
            double centrelineX,
            double gridTopY,
            IReadOnlyList<(double Elevation, double Y)> rulerLabels,
            double horizontalScale = 1.0)
        {
            if (rulerLabels == null) throw new ArgumentNullException(nameof(rulerLabels));
            if (rulerLabels.Count < 2)
                throw new ArgumentException("At least two ruler labels are needed to measure the vertical scale.", nameof(rulerLabels));

            var ordered = rulerLabels.OrderBy(r => r.Y).ToList();
            var lo = ordered[0];
            var hi = ordered[ordered.Count - 1];

            double deltaElev = hi.Elevation - lo.Elevation;
            if (Math.Abs(deltaElev) < 1e-9)
                throw new ArgumentException("Ruler labels all carry the same elevation; scale cannot be measured.", nameof(rulerLabels));

            double vScale = (hi.Y - lo.Y) / deltaElev;

            // The lowest labelled mark coincides with the top of the table box,
            // which is what ties the ruler to the frame.
            double datum = gridTopY - lo.Elevation * vScale;

            return new SectionFrameMath(centrelineX, datum, vScale, horizontalScale);
        }

        /// <summary>Elevation (m) of a drawing Y.</summary>
        public double ElevationAt(double y) => (y - Datum) / VerticalScale;

        /// <summary>Drawing Y of an elevation (m).</summary>
        public double YFor(double elevation) => Datum + elevation * VerticalScale;

        /// <summary>Offset (m, positive right) of a drawing X.</summary>
        public double OffsetAt(double x) => (x - CentrelineX) / HorizontalScale;

        /// <summary>Drawing X of an offset (m, positive right).</summary>
        public double XFor(double offset) => CentrelineX + offset * HorizontalScale;

        /// <summary>
        /// Round-trip check used by the audit: a frame that does not survive its
        /// own conversion is mis-decoded and every downstream number would be
        /// wrong. Cheap insurance against a bad ruler read.
        /// </summary>
        public bool RoundTripsWithin(double tolerance = 1e-6)
        {
            foreach (var probe in new[] { -50.0, 0.0, 12.34, 55.0 })
            {
                if (Math.Abs(OffsetAt(XFor(probe)) - probe) > tolerance) return false;
                if (Math.Abs(ElevationAt(YFor(probe)) - probe) > tolerance) return false;
            }
            return true;
        }

        /// <summary>
        /// House number format for a distance. Values below 1.00 drop the leading
        /// zero — the sheets read ".48", never "0.48". Getting this wrong is
        /// visible on every drawing, so it lives here rather than at each call site.
        /// </summary>
        public static string FormatDistance(double metres)
        {
            string s = Math.Round(metres, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            if (s.StartsWith("0.", StringComparison.Ordinal)) return s.Substring(1);
            if (s.StartsWith("-0.", StringComparison.Ordinal)) return "-" + s.Substring(2);
            return s;
        }

        /// <summary>House number format for an elevation — always two decimals, leading digits kept.</summary>
        public static string FormatElevation(double metres) =>
            Math.Round(metres, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Collapses ordinates that are the same point drafted twice. Dimension
        /// chains carry degenerate ~1 mm ticks at vertical faces; treating those
        /// as separate spans invents a 0.001 m dimension.
        /// </summary>
        public static List<double> Dedupe(IEnumerable<double> ordinates, double tolerance = 0.01)
        {
            var result = new List<double>();
            foreach (var x in ordinates.OrderBy(v => v))
            {
                if (result.Count == 0 || x - result[result.Count - 1] > tolerance)
                    result.Add(x);
            }
            return result;
        }
    }
}
