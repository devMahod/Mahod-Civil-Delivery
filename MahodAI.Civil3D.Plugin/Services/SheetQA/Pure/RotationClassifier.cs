using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.Civil3D.Plugin.Services.SheetQA.Pure
{
    /// <summary>
    /// Flags text that cannot be read in the sheet's own orientation.
    ///
    /// A label rotated a little off horizontal is normal draughting (labels follow the line
    /// they annotate). A label rotated past vertical is upside down: the reader has to turn
    /// the sheet. The band is therefore (95°, 265°) on the APPARENT sheet angle — the angle
    /// after any xref/viewport transform, not the entity's stored rotation. 90° and 270°
    /// exactly are the normal "read from the right/left edge" conventions and stay legal,
    /// with 5° of slack around them.
    /// </summary>
    public static class RotationClassifier
    {
        public const double UnreadableFromDeg = 95.0;
        public const double UnreadableToDeg = 265.0;

        /// <summary>True when text at this apparent sheet angle reads upside down.</summary>
        public static bool IsUnreadable(double rotationDeg)
        {
            var normalized = Normalize(rotationDeg);
            return normalized > UnreadableFromDeg && normalized < UnreadableToDeg;
        }

        /// <summary>Folds any angle (negative, or several turns) into [0, 360).</summary>
        public static double Normalize(double degrees)
        {
            var d = degrees % 360.0;
            if (d < 0) d += 360.0;
            return d;
        }

        /// <summary>Converts radians to a normalised degree value in [0, 360).</summary>
        public static double NormalizeRadians(double radians) =>
            Normalize(radians * 180.0 / Math.PI);

        public static List<SheetTextRecord> FindUnreadable(IReadOnlyList<SheetTextRecord> texts)
        {
            if (texts == null) return new List<SheetTextRecord>();
            return texts.Where(t => IsUnreadable(t.RotationDeg)).ToList();
        }

        /// <summary>
        /// Text at roughly 180° is flat-out mirrored and always wrong; angles nearer the
        /// vertical limits are often a line-following label that merely tipped over.
        /// </summary>
        public static string SeverityFor(double rotationDeg)
        {
            var d = Normalize(rotationDeg);
            var distanceFrom180 = Math.Abs(180.0 - d);
            return distanceFrom180 <= 30 ? VisualFindingSeverities.High
                : distanceFrom180 <= 60 ? VisualFindingSeverities.Medium
                : VisualFindingSeverities.Low;
        }
    }
}
