using System;
using System.Collections.Generic;
using System.Globalization;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// How long a linetype dash actually LOOKS in model space. AutoCAD multiplies the
    /// pattern by LTSCALE and by the entity's LinetypeScale, and — when MSLTSCALE is
    /// on (the template default since 2008) — divides by the current annotation scale
    /// value (CANNOSCALEVALUE, e.g. 0.01 for 1:100). A protected 0.50 m pattern drawn
    /// with LinetypeScale = 1/LTSCALE therefore renders as 50 m at 1:100 and as a
    /// visually continuous line at 1:1000, while every table/entity read-back stays
    /// green (Claude review of 1.2.28). The entity scale must cancel all three.
    /// </summary>
    public static class AnnotationLinetypeDisplayLogic
    {
        public const string Version = "ltdisplay-v1";

        /// <summary>Drawing state that decides the visible dash length.</summary>
        public sealed record DrawingState(
            double Ltscale,
            bool Msltscale,
            string? ScaleName,
            double PaperUnits,
            double DrawingUnits,
            double CannoscaleValue);

        /// <summary>Empty list = usable; otherwise every reason the state cannot be drawn against.</summary>
        public static IReadOnlyList<string> Validate(DrawingState s)
        {
            ArgumentNullException.ThrowIfNull(s);
            var errors = new List<string>();
            if (!double.IsFinite(s.Ltscale) || s.Ltscale <= 1e-9)
                errors.Add($"ltscale={R(s.Ltscale)} (expected finite > 0)");
            if (!s.Msltscale) return errors;

            // With MSLTSCALE on, the annotation scale participates and must be sane.
            if (!double.IsFinite(s.PaperUnits) || s.PaperUnits <= 0)
                errors.Add($"cannoscale-paper-units={R(s.PaperUnits)} (expected > 0)");
            if (!double.IsFinite(s.DrawingUnits) || s.DrawingUnits <= 0)
                errors.Add($"cannoscale-drawing-units={R(s.DrawingUnits)} (expected > 0)");
            if (!double.IsFinite(s.CannoscaleValue) || s.CannoscaleValue <= 0)
                errors.Add($"cannoscale-value={R(s.CannoscaleValue)} (expected > 0)");
            else if (double.IsFinite(s.PaperUnits) && double.IsFinite(s.DrawingUnits) &&
                     s.PaperUnits > 0 && s.DrawingUnits > 0)
            {
                // CANNOSCALEVALUE is paper units per drawing unit (0.01 for 1:100). The
                // host value must agree with its own units; if the API ever reported
                // the inverse, the contract fails closed instead of drawing 5000 m dashes.
                var expected = s.PaperUnits / s.DrawingUnits;
                if (Math.Abs(s.CannoscaleValue - expected) > 1e-9 * Math.Max(1.0, expected))
                    errors.Add($"cannoscale-value={R(s.CannoscaleValue)} inconsistent with paper/drawing={R(expected)}");
            }
            return errors;
        }

        /// <summary>
        /// Entity LinetypeScale that makes the protected pattern render at its nominal
        /// length regardless of LTSCALE, MSLTSCALE and the annotation scale.
        /// </summary>
        public static double EntityScale(DrawingState s)
        {
            var errors = Validate(s);
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "Annotation linetype display state is unusable: " + string.Join("; ", errors));
            return s.Msltscale ? s.CannoscaleValue / s.Ltscale : 1.0 / s.Ltscale;
        }

        /// <summary>What AutoCAD shows for a pattern length under this state and entity scale.</summary>
        public static double VisibleLength(double patternLength, DrawingState s, double entityScale)
        {
            var errors = Validate(s);
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "Annotation linetype display state is unusable: " + string.Join("; ", errors));
            var annotationFactor = s.Msltscale ? 1.0 / s.CannoscaleValue : 1.0;
            return patternLength * s.Ltscale * entityScale * annotationFactor;
        }

        /// <summary>Canonical, culture-invariant evidence string; APPLY records it, VERIFY re-reads and compares.</summary>
        public static string Describe(DrawingState s)
        {
            ArgumentNullException.ThrowIfNull(s);
            var scale = Validate(s).Count == 0 ? R(EntityScale(s)) : "(invalid)";
            // With MSLTSCALE off the annotation scale does not touch the dash length, so
            // it must not be part of the contract: changing CANNOSCALE later would
            // otherwise fail VERIFY for a section that still renders identically.
            var annotation = s.Msltscale
                ? ";cannoscale=" + (string.IsNullOrWhiteSpace(s.ScaleName) ? "(none)" : s.ScaleName.Trim()) +
                  ";paper=" + R(s.PaperUnits) +
                  ";drawing=" + R(s.DrawingUnits) +
                  ";value=" + R(s.CannoscaleValue)
                : ";cannoscale=(n/a);paper=(n/a);drawing=(n/a);value=(n/a)";
            return Version +
                   ";ltscale=" + R(s.Ltscale) +
                   ";msltscale=" + (s.Msltscale ? "1" : "0") +
                   annotation +
                   ";entity_scale=" + scale;
        }

        private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
