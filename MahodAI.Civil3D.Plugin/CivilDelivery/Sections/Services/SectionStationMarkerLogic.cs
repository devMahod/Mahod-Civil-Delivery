using System;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Geometry;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// "Station markers" CL mode (1.4.1, project 984; agreed with Codex 06.10 11:17). Some CL drawings mark each section
    /// station with a short tick across the axis (an exploded Civil <c>AeccTickLine</c>, 0.5 m) instead of a line drawn
    /// across the road, so the drawn length says nothing about the section reach. Only an engineer can switch this mode
    /// on, together with a positive finite half-width; the tool never infers it from line length and never defaults or
    /// derives the width. Each tick keeps deciding the DIRECTION; it is extended symmetrically from its single alignment
    /// crossing to ±half-width. A degenerate direction or a missing width blocks the record for review.
    /// In the ordinary mode a short line is only a warning that points to this mode.
    /// </summary>
    internal static class SectionStationMarkerLogic
    {
        public const string ModeStationMarkers = ProjectProfile.SectionsProfile.ClProfile.ModeStationMarkers;

        /// <summary>A drawn CL shorter than this is reported (warning only) in the ordinary mode.</summary>
        public const double ShortLineWarningM = 2.0;

        /// <summary>A half-width above this is refused as a likely unit mistake (cm or mm typed as metres).</summary>
        public const double MaxHalfWidthM = ProjectProfile.SectionsProfile.ClProfile.StationMarkerMaxHalfWidthM;

        internal static bool IsActive(ProjectProfile.SectionsProfile.ClProfile cl) =>
            string.Equals(cl.Mode, ModeStationMarkers, StringComparison.OrdinalIgnoreCase);

        /// <summary>Null when the value is usable; otherwise the Hebrew reason it is not.</summary>
        internal static string? ValidateHalfWidth(double? halfWidth)
        {
            if (halfWidth is not { } w || !double.IsFinite(w) || w <= 0)
                return "מצב 'סימוני תחנות' פעיל אך חצי-רוחב החתך לא הוגדר כמספר חיובי";
            if (w > MaxHalfWidthM)
                return $"חצי-רוחב {w:F1} מ' חורג מ-{MaxHalfWidthM:F0} מ' — כנראה טעות יחידות";
            return null;
        }

        /// <summary>
        /// Extends a station tick a→b to ±<paramref name="halfWidth"/> from <paramref name="crossing"/> along the tick's
        /// own direction. False (with a Hebrew reason) when the direction is degenerate or the width is invalid.
        /// </summary>
        internal static bool TryExtend(Pt2 a, Pt2 b, Pt2 crossing, double? halfWidth,
            out Pt2 extendedA, out Pt2 extendedB, out string? reason)
        {
            extendedA = a; extendedB = b;
            reason = ValidateHalfWidth(halfWidth);
            if (reason != null) return false;
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (!double.IsFinite(length) || length < 1e-6 ||
                !double.IsFinite(crossing.X) || !double.IsFinite(crossing.Y))
            {
                reason = "כיוון סימון התחנה מנוון — אי אפשר להרחיב אותו לחתך";
                return false;
            }
            var w = halfWidth!.Value;
            var ux = dx / length; var uy = dy / length;
            extendedA = new Pt2(crossing.X - ux * w, crossing.Y - uy * w);
            extendedB = new Pt2(crossing.X + ux * w, crossing.Y + uy * w);
            return true;
        }

        internal static DeliveryFinding ExtendedFinding(string recordId, double drawnLength, double halfWidth, string? approvedBy) => new()
        {
            Code = SectionFindingCodes.LayoutUnresolved,
            Domain = SectionPlanLogic.Domain,
            Severity = FindingSeverity.Info,
            Title = $"סימון תחנה ({drawnLength:F2} מ') הורחב ל-±{halfWidth:F1} מ' לפי הגדרת הפרויקט",
            Message = "station-markers mode: the drawn tick decides the direction; the engineer-entered half-width " +
                      $"decides the reach (approved by: {approvedBy ?? "—"}).",
            AffectedRecordIds = { recordId },
        };

        internal static DeliveryFinding ShortLineWarning(string recordId, double drawnLength) => new()
        {
            Code = SectionFindingCodes.LayoutUnresolved,
            Domain = SectionPlanLogic.Domain,
            Severity = FindingSeverity.Warning,
            Title = $"קו CL קצר ({drawnLength:F2} מ') — רוחב החתך ייקבע לפי אורכו",
            Message = "אם הקווים הם סימוני תחנה (טיקים) ולא קווי חתך לרוחב הדרך, יש לבחור בהגדרת הפרויקט " +
                      "את המצב 'סימוני תחנות' ולהזין חצי-רוחב לכל צד.",
            AffectedRecordIds = { recordId },
        };
    }
}
