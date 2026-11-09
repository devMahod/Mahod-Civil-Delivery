using System;
using System.Collections.Generic;
using System.Linq;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Pure geometry and checks of one pick (2026-10). A click lands near the road, not on the axis: the host projects
    /// it onto the alignment (station, axis point, tangent) and this logic builds the marker chord through the axis
    /// point, perpendicular to the tangent, ±half-width — so a PLAN-style intersection finds the crossing exactly at
    /// the axis point and the firm's section program cuts at the same station. Nothing here invents a width: the
    /// half-width comes from the engineer, typed at the prompt or approved earlier in the profile.
    /// </summary>
    public static class TypicalSectionPickLogic
    {
        /// <summary>What a click (or the cursor, while it moves) would mark.</summary>
        public enum PickState
        {
            /// <summary>A section can be marked here.</summary>
            Ready,
            /// <summary>The point does not project onto any allowed alignment.</summary>
            NoAlignment,
            /// <summary>The point is farther from the axis than the half-width: it is not on this road.</summary>
            TooFar,
            /// <summary>A section is already marked at this station.</summary>
            Duplicate,
        }

        /// <summary>
        /// The verdict the live preview colours by (green when Ready, red otherwise) and the commit refuses by: one
        /// rule for both, so what the engineer sees while moving is exactly what a click does. The reason is null
        /// when Ready, otherwise the Hebrew line the command prints.
        /// </summary>
        public static (PickState State, string? Reason) Assess(
            string? alignmentName, double station, double offsetFromAxisM, double halfWidthM, bool duplicate)
        {
            if (string.IsNullOrWhiteSpace(alignmentName) || !double.IsFinite(station) || !double.IsFinite(offsetFromAxisM))
                return (PickState.NoAlignment, "הנקודה אינה מול תוואי מותר — יש ללחוץ קרוב יותר לציר הכביש.");
            if (Math.Abs(offsetFromAxisM) > halfWidthM)
                return (PickState.TooFar, FormattableString.Invariant(
                    $"הנקודה רחוקה {Math.Abs(offsetFromAxisM):0.0} מ' מציר {alignmentName}, מעבר לחצי-הרוחב — יש ללחוץ קרוב יותר."));
            if (duplicate)
                return (PickState.Duplicate, FormattableString.Invariant(
                    $"בתחנה {station:0.00} של {alignmentName} כבר מסומן חתך — הלחיצה לא נוספה."));
            return (PickState.Ready, null);
        }

        /// <summary>Null when the value is usable; otherwise the Hebrew reason it is not.</summary>
        public static string? ValidateHalfWidth(double halfWidthM)
        {
            if (!double.IsFinite(halfWidthM) || halfWidthM <= 0)
                return "חצי-רוחב החתך חייב להיות מספר חיובי";
            if (halfWidthM > TypicalSectionContracts.MaxHalfWidthM)
                return FormattableString.Invariant(
                    $"חצי-רוחב {halfWidthM:F1} מ' חורג מ-{TypicalSectionContracts.MaxHalfWidthM:F0} מ' — כנראה טעות יחידות");
            return null;
        }

        /// <summary>The value the prompt offers: the profile's approved half-width when usable, else the default.</summary>
        public static double DefaultHalfWidth(double? profileHalfWidthM) =>
            profileHalfWidthM is { } w && ValidateHalfWidth(w) == null ? w : TypicalSectionContracts.DefaultHalfWidthM;

        /// <summary>
        /// Marker chord ends: the axis point ± the left normal of the tangent × half-width. A lies on the left of the
        /// direction of travel, B on the right; the section frame decides left/right by station anyway.
        /// </summary>
        public static (P2 A, P2 B) Endpoints(P2 axisPoint, double tangentDeg, double halfWidthM)
        {
            if (ValidateHalfWidth(halfWidthM) is { } reason)
                throw new ArgumentOutOfRangeException(nameof(halfWidthM), reason);
            if (!double.IsFinite(tangentDeg) || !double.IsFinite(axisPoint.X) || !double.IsFinite(axisPoint.Y))
                throw new ArgumentOutOfRangeException(nameof(tangentDeg), "The axis point and tangent must be finite.");
            var t = tangentDeg * Math.PI / 180.0;
            var nx = -Math.Sin(t);
            var ny = Math.Cos(t);
            return (new P2(axisPoint.X + nx * halfWidthM, axisPoint.Y + ny * halfWidthM),
                    new P2(axisPoint.X - nx * halfWidthM, axisPoint.Y - ny * halfWidthM));
        }

        /// <summary>
        /// The corridor station nearest the picked one, or null when the corridor computed none. The firm's section
        /// program reads the road at a station from the corridor's applied assembly there (MahodCivilNet
        /// <c>getLinkSample</c>), and the corridor has one only at its own stations: its frequency, geometry and added
        /// stations. So a pick moves to the nearest of them, and the line the engineer sees is where the section is cut.
        /// </summary>
        public static double? SnapToCorridorStation(double station, IReadOnlyList<double>? corridorStations)
        {
            if (!double.IsFinite(station) || corridorStations == null) return null;
            double? best = null;
            foreach (var candidate in corridorStations)
            {
                if (!double.IsFinite(candidate)) continue;
                if (best == null || Math.Abs(candidate - station) < Math.Abs(best.Value - station)) best = candidate;
            }
            return best;
        }

        /// <summary>True when another pick already sits within the minimum spacing of this axis point.</summary>
        public static bool TooClose(IEnumerable<P2> existingAxisPoints, P2 axisPoint,
            double minSpacingM = TypicalSectionContracts.MinPickSpacingM) =>
            existingAxisPoints.Any(p => Distance(p, axisPoint) < minSpacingM);

        public static double Distance(P2 a, P2 b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// What the pick command says in a popup when the drawing gives it no road to mark on (2026-11-09, owner, after
        /// GM-halufa_A_new2: "an appropriate message should pop up if the user tries to do this without a centreline
        /// alignment"). <see cref="CanPickAxis"/>: the drawing has no Civil alignment at all, so the engineer may show
        /// the centre line on the plan; with alignments that the profile does not allow, the profile is what to change.
        /// <see cref="Summary"/> is the one line the command line and the palette keep.
        /// </summary>
        public sealed record NoAxisNotice(string Title, string Body, bool CanPickAxis, string Summary);

        /// <summary>Lists at most this many alignment names in the notice, then "ועוד N".</summary>
        public const int NoticeNamesShown = 5;

        public static NoAxisNotice NoAxis(IReadOnlyList<string>? drawingAlignments, IReadOnlyList<string>? allowedNames)
        {
            var inDrawing = (drawingAlignments ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (inDrawing.Count == 0)
            {
                return new NoAxisNotice(
                    "לא נמצא ציר לכביש",
                    "חתך טיפוסי נמדד מציר הכביש, ובשרטוט הזה אין ציר: אין תוואי Civil 3D, ואין קו ציר בשכבות AX-*, CL או CENTERLINE." +
                    "\n\nאפשר לבחור עכשיו בשרטוט את קו הציר של הכביש - קו, פוליליין או קשת, גם בתוך XREF. " +
                    "הכלי יעתיק אותו לשכבת ציר בשם שתקליד (למשל 43 תיתן AX-43), וימצא אותו לבד בפעם הבאה. " +
                    "התחנות נמדדות מתחילת הקו שנבחר." +
                    "\n\nאפשר גם להעתיק בעצמך את קו הציר לשכבה ששמה מתחיל ב-AX (למשל AX-43) ולהריץ שוב.",
                    true,
                    "לא נמצא ציר לכביש (תוואי Civil 3D או קו בשכבות AX-*, CL, CENTERLINE).");
            }

            var allowed = (allowedNames ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            return new NoAxisNotice(
                "אין תוואי מותר בשרטוט",
                $"בשרטוט יש {inDrawing.Count} תוואים ({NamesOf(inDrawing)}), אבל אף אחד מהם אינו ברשימת התוואים המותרים בפרופיל הפרויקט" +
                (allowed.Count > 0 ? $" ({NamesOf(allowed)})." : ".") +
                "\n\nאפשר לשנות את התוואים המותרים בלשונית 'פרויקט' > 'הגדרת פרויקט', ולהריץ שוב.",
                false,
                "לא נמצא תוואי מתאים בשרטוט (לפי התוואים המותרים בפרופיל הפרויקט).");

            static string NamesOf(IReadOnlyList<string> names) =>
                string.Join(", ", names.Take(NoticeNamesShown)) +
                (names.Count > NoticeNamesShown ? $" ועוד {names.Count - NoticeNamesShown}" : string.Empty);
        }

        /// <summary>A readable rotation for the marker text: the chord direction folded into (−90°, 90°].</summary>
        public static double ReadableTextRotationDeg(double chordDirectionDeg)
        {
            var d = chordDirectionDeg % 360.0;
            if (d < 0) d += 360.0;
            if (d > 90.0 && d <= 270.0) d -= 180.0;
            else if (d > 270.0) d -= 360.0;
            return d;
        }
    }
}
