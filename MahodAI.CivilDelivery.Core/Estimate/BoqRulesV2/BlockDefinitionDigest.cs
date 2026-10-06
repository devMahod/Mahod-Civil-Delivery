using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MahodAI.CivilDelivery.Estimate.BoqRulesV2
{
    /// <summary>
    /// Rules 2.8 (BOQ-N1; Codex 00:47 / 00:50, 01.10.2026): the canonical geometry digest of a block definition, bit for bit
    /// the reference's boq_geometry.definition_digest. An approved physical footprint is bound to it, so a body moved or
    /// changed inside the same entity counts and envelope keeps no old approval, while a copy of the same definition in
    /// another file (other handles) matches. Every number is the integer round-half-even(v × 10^6) of the same IEEE product
    /// (the documented canonicalisation tolerance, block units / radians — no decimal-format tie rule involved); angles and
    /// ellipse parameters modulo 2π (fmod, + 2π when negative, as Python's float %); the descriptors are sorted (a multiset).
    /// The offline read (golden) and the Civil collector build the same descriptors.
    /// </summary>
    public static class BlockDefinitionDigest
    {
        public static string Canon(double v) =>
            ((long)Math.Round(v * 1e6, MidpointRounding.ToEven)).ToString(CultureInfo.InvariantCulture);

        public static string Angle(double a)
        {
            var m = a % (2 * Math.PI);
            if (m < 0) m += 2 * Math.PI;
            return Canon(m);
        }

        private static string Join(IEnumerable<string> parts) => string.Join(",", parts);

        public static string Line(IReadOnlyList<double> start, IReadOnlyList<double> end) =>
            "LINE|" + Join(start.Concat(end).Select(Canon));

        public static string Arc(IReadOnlyList<double> center, double radius, double startAngle, double endAngle, IReadOnlyList<double> normal) =>
            "ARC|" + Join(center.Select(Canon).Append(Canon(radius)).Append(Angle(startAngle)).Append(Angle(endAngle)).Concat(normal.Select(Canon)));

        public static string Circle(IReadOnlyList<double> center, double radius, IReadOnlyList<double> normal) =>
            "CIRCLE|" + Join(center.Select(Canon).Append(Canon(radius)).Concat(normal.Select(Canon)));

        public static string Ellipse(IReadOnlyList<double> center, IReadOnlyList<double> majorAxis, double radiusRatio,
            double startParameter, double endParameter, IReadOnlyList<double> normal) =>
            "ELLIPSE|" + Join(center.Select(Canon).Concat(majorAxis.Select(Canon)).Append(Canon(radiusRatio))
                .Append(Angle(startParameter)).Append(Angle(endParameter)).Concat(normal.Select(Canon)));

        /// <summary>
        /// A zero-width polyline only (Codex 01:23): a width is geometry the descriptor does not carry, so any non-zero
        /// constant or vertex width makes the descriptor unknown (null) and the digest unproven.
        /// </summary>
        public static string? LwPolyline(bool closed, double elevation, IReadOnlyList<double> normal, double constantWidth,
            IReadOnlyList<(double X, double Y, double Bulge, double StartWidth, double EndWidth)> vertices)
        {
            if (constantWidth != 0 || vertices.Any(v => v.StartWidth != 0 || v.EndWidth != 0)) return null;
            return "LWPOLYLINE|" + (closed ? "1" : "0") + "|" + Canon(elevation) + "|" + Join(normal.Select(Canon)) + "|" +
                   string.Join(";", vertices.Select(v => $"{Canon(v.X)},{Canon(v.Y)},{Canon(v.Bulge)}"));
        }

        /// <summary>SHA-256 (hex, lower case) of the sorted descriptors; null when there are none or any is unknown (null).</summary>
        public static string? Digest(IEnumerable<string?> descriptors)
        {
            var list = descriptors.ToList();
            if (list.Count == 0 || list.Any(d => d == null)) return null;
            var text = string.Join("\n", list.OrderBy(d => d, StringComparer.Ordinal));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }
    }
}
