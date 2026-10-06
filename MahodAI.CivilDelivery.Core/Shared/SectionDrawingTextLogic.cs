using System;
using System.Globalization;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// The exact Hebrew wording the tool writes INTO the section drawing (review of
    /// 1.3.9, 30/09). APPLY, VERIFY and the PLAN preview use these strings; nothing
    /// here is an engineering value — each text only says what the proven source is.
    /// </summary>
    public static class SectionDrawingTextLogic
    {
        /// <summary>
        /// SEC-M2: the one stated elevation is the proven existing ground AT THE AXIS.
        /// "רום קיים 285.35" at the grid corner read as a datum-line elevation. Whether
        /// the single reference should be existing or design stays Natali's decision.
        /// </summary>
        public const string DatumPrefix = "רום קרקע קיימת בציר: ";

        /// <summary>SEC-m3: key to the two surface lines, same wording as the palette legend.</summary>
        public const string LegendSurfaces = "קו ירוק מקווקו — קרקע קיימת · קו אדום רציף — משטח תכנון";

        /// <summary>
        /// SEC-M3: the utility marker is a location marker, not a pipe diameter, and the
        /// source does not say whether its Z is top, centre or invert.
        /// </summary>
        public const string LegendUtilities = "עיגול מערכת = מיקום בלבד, לא קוטר · הרום מקובץ המקור, סוגו לא ידוע";

        public const string OverallWidthPrefix = "רוחב כולל ";

        /// <summary>Radius of the utility location marker (drawing units). Symbol size only.</summary>
        public const double UtilityMarkerRadius = 0.15;

        public static string DatumText(double elevation) =>
            DatumPrefix + elevation.ToString("F2", CultureInfo.InvariantCulture);

        public static bool IsDatumText(string? text) =>
            text != null && text.StartsWith(DatumPrefix, StringComparison.Ordinal);

        public static string OverallWidthText(double width) =>
            OverallWidthPrefix + width.ToString("F2", CultureInfo.InvariantCulture);

        public static string GapWidthText(double width) =>
            width.ToString("F2", CultureInfo.InvariantCulture);

        /// <summary>"חשמל רום 292.11" — says it is an elevation; the legend says which kind is unknown.</summary>
        public static string UtilityLabel(string system, double? elevation) =>
            elevation is { } z && double.IsFinite(z)
                ? (system ?? string.Empty).Trim() + " רום " + z.ToString("F2", CultureInfo.InvariantCulture)
                : (system ?? string.Empty).Trim() + " (עומק לא מוגדר)";

        /// <summary>Israeli chainage format: 12145.43 → "12+145.43".</summary>
        public static string? Chainage(double? station)
        {
            if (station is not { } value || !double.IsFinite(value)) return null;
            var rounded = Math.Round(Math.Abs(value), 2, MidpointRounding.AwayFromZero);
            var km = Math.Floor(rounded / 1000.0);
            var rest = Math.Round(rounded - km * 1000.0, 2, MidpointRounding.AwayFromZero);
            if (rest >= 1000.0)
            {
                km += 1;
                rest -= 1000.0;
            }
            return (value < 0 ? "-" : string.Empty) +
                   km.ToString("0", CultureInfo.InvariantCulture) + "+" +
                   rest.ToString("000.00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// SEC-m3: the drawn title names the road and the chainage, not only the CL id:
        /// "חתך STA-12145 · תוואי 2000 · 12+145.43". Values come from PLAN only.
        /// </summary>
        public static string Title(string sectionId, string? alignment, double? station)
        {
            var title = "חתך " + (sectionId ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(alignment))
                title += " · תוואי " + alignment.Trim();
            if (Chainage(station) is { } chainage)
                title += " · " + chainage;
            return title;
        }
    }
}
