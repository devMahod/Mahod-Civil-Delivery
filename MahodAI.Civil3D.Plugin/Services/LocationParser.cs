using System.Globalization;
using System.Text.RegularExpressions;

namespace MahodAI.Civil3D.Plugin.Services
{
    /// <summary>
    /// Parses a finding's "location" string (alignment + station range) such as
    /// <c>"ציר Second Street — תחנה 0+035.05 עד 0+175.48 — עקומה 2"</c> into its
    /// alignment name and station(s). Shared by the chat table renderer (clickable
    /// location cells) and the on-drawing problem overlay (pin placement).
    /// </summary>
    public static class LocationParser
    {
        // Captures: alignment name, start station, optional end station.
        private static readonly Regex LocationRegex = new Regex(
            @"ציר\s+(?<align>.+?)\s*[—–\-]?\s*תחנה\s+(?<s1>\d[\d.,+]*)(?:\s*(?:עד|to|[—–\-])\s*(?<s2>\d[\d.,+]*))?",
            RegexOptions.Compiled);

        /// <summary>
        /// Attempts to extract the alignment name and station(s) from a location string.
        /// Returns false when the text isn't a recognizable alignment/station location.
        /// </summary>
        public static bool TryParse(string? text, out string alignment, out double stationStart, out double? stationEnd)
        {
            alignment = string.Empty;
            stationStart = 0;
            stationEnd = null;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            var m = LocationRegex.Match(text);
            if (!m.Success || !TryParseStation(m.Groups["s1"].Value, out stationStart))
                return false;

            alignment = m.Groups["align"].Value.Trim();
            if (m.Groups["s2"].Success && TryParseStation(m.Groups["s2"].Value, out double s2))
                stationEnd = s2;

            return !string.IsNullOrEmpty(alignment);
        }

        /// <summary>
        /// Parses a Civil 3D station string (e.g. "0+035.05" or "1+234.56") into meters.
        /// The "+" separator and thousands commas are stripped; the remaining value is the
        /// distance along the alignment.
        /// </summary>
        public static bool TryParseStation(string? s, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string cleaned = s.Replace("+", "").Replace(",", "").Trim();
            return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
