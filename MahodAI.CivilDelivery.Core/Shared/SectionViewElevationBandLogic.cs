using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Presentation-only elevation band of a managed SectionView (SEC-B3, review of
    /// 1.3.9, 30/09).  1.3.9 took the existing-ground range, added a fixed ±5 m, then
    /// 2 m below and ceil(bus arrow) + 1 m above, whatever the section actually drew:
    /// a road with about 1 m of relief became a composition taller than the road is
    /// wide ("למה צריך גובה כזה לחתכים", "סתם גבוה").
    ///
    /// The band is now exactly what the section draws:
    ///   * low end  — the lower of the sampled EG/FG minimum and the lowest projected
    ///                utility that carries a PROVEN elevation (never an invented depth);
    ///   * high end — the sampled EG/FG maximum (and any higher proven utility), plus
    ///                the direction-arrow headroom only when arrows are drawn, for the
    ///                tallest strip kind actually present;
    ///   * one small drafting margin on each side, rounded outward to 0.1 m.
    /// Nothing here is an engineering value: no depth, width, height or datum is
    /// derived — only the visible frame around proven content.
    /// </summary>
    public static class SectionViewElevationBandLogic
    {
        /// <summary>Drafting air around the drawn content. Presentation constant only.</summary>
        public const double DraftingMarginM = 0.5;

        /// <summary>The pinned range is rounded outward to this step.</summary>
        public const double RoundingStepM = 0.1;

        public const string EvidencePrefix = "content-band-v1";

        public sealed record Inputs(
            double ExistingMin,
            double ExistingMax,
            double? DesignMin,
            double? DesignMax,
            double? LowestUtility,
            double? HighestUtility,
            double ArrowHeadroomM);

        public sealed record Band(double Min, double Max, bool Capped, Inputs Inputs)
        {
            /// <summary>Round-trip evidence stored on the APPLY record and re-derived by VERIFY.</summary>
            public string Evidence => string.Join("|", new[]
            {
                EvidencePrefix,
                "eg=" + R(Inputs.ExistingMin) + ".." + R(Inputs.ExistingMax),
                "fg=" + (Inputs.DesignMin is { } dMin && Inputs.DesignMax is { } dMax
                    ? R(dMin) + ".." + R(dMax) : "-"),
                "ut=" + (Inputs.LowestUtility is { } uMin && Inputs.HighestUtility is { } uMax
                    ? R(uMin) + ".." + R(uMax) : "-"),
                "headroom=" + R(Inputs.ArrowHeadroomM),
                "margin=" + R(DraftingMarginM),
                "min=" + R(Min),
                "max=" + R(Max),
                "capped=" + (Capped ? "true" : "false"),
            });
        }

        /// <summary>
        /// Tallest headroom among the strips that will carry a visible direction arrow
        /// ("road" | "bus" | "bike", the PLAN traffic-direction strip kinds). No arrow
        /// means no headroom. An unknown kind takes the tallest (bus) headroom so an
        /// arrow can never be clipped by the tightened band.
        /// </summary>
        public static double ArrowHeadroomFor(IEnumerable<string?> stripKinds)
        {
            var headroom = 0.0;
            foreach (var kind in stripKinds ?? Array.Empty<string?>())
            {
                var value = (kind ?? string.Empty).Trim().ToLowerInvariant() switch
                {
                    "road" => SectionTrafficDirectionAnnotationLogic.RequiredHeadroomM(
                        SectionTrafficDirectionAnnotationLogic.StripKind.Road),
                    "bike" => SectionTrafficDirectionAnnotationLogic.RequiredHeadroomM(
                        SectionTrafficDirectionAnnotationLogic.StripKind.Bike),
                    _ => SectionTrafficDirectionAnnotationLogic.RequiredHeadroomM(
                        SectionTrafficDirectionAnnotationLogic.StripKind.Bus),
                };
                headroom = Math.Max(headroom, value);
            }
            return headroom;
        }

        public static bool TryCompute(Inputs inputs, double maxSpanM,
            out Band? band, out string error)
        {
            band = null;
            error = string.Empty;
            if (inputs == null)
            {
                error = "elevation band inputs are missing";
                return false;
            }
            if (!Finite(inputs.ExistingMin) || !Finite(inputs.ExistingMax) ||
                inputs.ExistingMax < inputs.ExistingMin)
            {
                error = "existing-ground sampled range is not finite/ordered";
                return false;
            }
            if (inputs.DesignMin.HasValue != inputs.DesignMax.HasValue ||
                (inputs.DesignMin is { } dLo && inputs.DesignMax is { } dHi &&
                 (!Finite(dLo) || !Finite(dHi) || dHi < dLo)))
            {
                error = "design sampled range is not finite/ordered";
                return false;
            }
            if (inputs.LowestUtility.HasValue != inputs.HighestUtility.HasValue ||
                (inputs.LowestUtility is { } uLo && inputs.HighestUtility is { } uHi &&
                 (!Finite(uLo) || !Finite(uHi) || uHi < uLo)))
            {
                error = "projected utility elevation range is not finite/ordered";
                return false;
            }
            if (!Finite(inputs.ArrowHeadroomM) || inputs.ArrowHeadroomM < 0)
            {
                error = "direction-arrow headroom is invalid";
                return false;
            }
            if (!Finite(maxSpanM) || maxSpanM <= 2 * DraftingMarginM)
            {
                error = "maximum view span is invalid";
                return false;
            }

            var groundMin = Math.Min(inputs.ExistingMin, inputs.DesignMin ?? inputs.ExistingMin);
            var groundMax = Math.Max(inputs.ExistingMax, inputs.DesignMax ?? inputs.ExistingMax);
            var contentMin = Math.Min(groundMin, inputs.LowestUtility ?? groundMin);
            // Arrows stand on sampled design ground, which never exceeds groundMax.
            var contentMax = Math.Max(groundMax + inputs.ArrowHeadroomM,
                inputs.HighestUtility ?? groundMax);

            var min = FloorToStep(contentMin - DraftingMarginM);
            var max = CeilToStep(contentMax + DraftingMarginM);
            var capped = false;
            if (max - min > maxSpanM)
            {
                // Same ceiling as 1.3.9: a road cross-section is never taller than
                // this. Content outside the capped band then fails closed in APPLY
                // (utility out of range / arrow headroom), it is never hidden.
                var mid = (groundMin + groundMax) / 2.0;
                min = FloorToStep(mid - maxSpanM / 2.0);
                max = min + maxSpanM;
                capped = true;
            }
            band = new Band(min, max, capped, inputs);
            return true;
        }

        /// <summary>
        /// Parses stored evidence and re-derives it: a band whose stored min/max no
        /// longer follow from its own inputs is rejected (tamper/legacy guard).
        /// </summary>
        public static bool TryParseAndRecompute(string? evidence, double maxSpanM,
            out Band? band, out string error)
        {
            band = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(evidence))
            {
                error = "no content-band evidence (legacy APPLY record; re-apply the section)";
                return false;
            }
            var parts = evidence.Split('|');
            if (parts.Length != 9 || !string.Equals(parts[0], EvidencePrefix, StringComparison.Ordinal))
            {
                error = "content-band evidence has an unknown shape";
                return false;
            }
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in parts.Skip(1))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0 || !fields.TryAdd(part[..eq], part[(eq + 1)..]))
                {
                    error = "content-band evidence field is malformed: " + part;
                    return false;
                }
            }
            if (!TryRange(fields, "eg", allowNone: false, out var egLo, out var egHi) ||
                !TryRange(fields, "fg", allowNone: true, out var fgLo, out var fgHi) ||
                !TryRange(fields, "ut", allowNone: true, out var utLo, out var utHi) ||
                !TryNumber(fields, "headroom", out var headroom) ||
                !TryNumber(fields, "margin", out var margin) ||
                !TryNumber(fields, "min", out var storedMin) ||
                !TryNumber(fields, "max", out var storedMax) ||
                !fields.TryGetValue("capped", out var cappedText) ||
                (cappedText != "true" && cappedText != "false"))
            {
                error = "content-band evidence values are unreadable";
                return false;
            }
            if (Math.Abs(margin - DraftingMarginM) > 1e-9)
            {
                error = "content-band evidence was produced with another drafting margin";
                return false;
            }
            var inputs = new Inputs(egLo!.Value, egHi!.Value, fgLo, fgHi, utLo, utHi, headroom);
            if (!TryCompute(inputs, maxSpanM, out var recomputed, out error) || recomputed == null)
                return false;
            if (Math.Abs(recomputed.Min - storedMin) > 1e-6 ||
                Math.Abs(recomputed.Max - storedMax) > 1e-6 ||
                recomputed.Capped != (cappedText == "true"))
            {
                error = FormattableString.Invariant(
                    $"stored band {storedMin:F3}..{storedMax:F3} does not follow from its content (expected {recomputed.Min:F3}..{recomputed.Max:F3})");
                return false;
            }
            band = recomputed;
            return true;
        }

        private static bool TryRange(IReadOnlyDictionary<string, string> fields, string key,
            bool allowNone, out double? lo, out double? hi)
        {
            lo = null;
            hi = null;
            if (!fields.TryGetValue(key, out var text)) return false;
            if (text == "-") return allowNone;
            var split = text.Split(new[] { ".." }, StringSplitOptions.None);
            if (split.Length != 2 ||
                !double.TryParse(split[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ||
                !double.TryParse(split[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ||
                !Finite(a) || !Finite(b) || b < a)
                return false;
            lo = a;
            hi = b;
            return true;
        }

        private static bool TryNumber(IReadOnlyDictionary<string, string> fields, string key,
            out double value)
        {
            value = double.NaN;
            return fields.TryGetValue(key, out var text) &&
                   double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                   Finite(value);
        }

        private static double FloorToStep(double value) =>
            Math.Round(Math.Floor(value / RoundingStepM + 1e-9) * RoundingStepM, 6,
                MidpointRounding.AwayFromZero);

        private static double CeilToStep(double value) =>
            Math.Round(Math.Ceiling(value / RoundingStepM - 1e-9) * RoundingStepM, 6,
                MidpointRounding.AwayFromZero);

        private static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static bool Finite(double value) => double.IsFinite(value);
    }
}
