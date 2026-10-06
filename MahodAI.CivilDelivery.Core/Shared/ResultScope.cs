using System;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Binds a result to the drawing and profile it was produced from.
    ///
    /// Civil 3D keeps several drawings open in one session, so a plan computed in
    /// drawing A must never be applied, shown or exported against drawing B. The same
    /// applies after the engineer edits the profile: the plan on screen no longer
    /// describes what would happen. Both cases are "stale", and both are refused
    /// rather than silently acted upon.
    /// </summary>
    public sealed record ResultScope(string? DrawingPath, string? ProfileHash)
    {
        public static ResultScope For(string? drawingPath, string? profileHash) =>
            new(drawingPath, profileHash);

        /// <summary>True when this result still describes the given drawing + profile.</summary>
        public bool Matches(string? drawingPath, string? profileHash) =>
            string.Equals(DrawingPath, drawingPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ProfileHash, profileHash, StringComparison.Ordinal);

        /// <summary>Why the result is stale, in words an engineer can act on.</summary>
        public string? StaleReason(string? drawingPath, string? profileHash)
        {
            if (!string.Equals(DrawingPath, drawingPath, StringComparison.OrdinalIgnoreCase))
                return "התוצאה שייכת לשרטוט אחר — יש להריץ תכנון מחדש";
            if (!string.Equals(ProfileHash, profileHash, StringComparison.Ordinal))
                return "פרופיל הפרויקט השתנה מאז התכנון — יש להריץ תכנון מחדש";
            return null;
        }
    }
}
