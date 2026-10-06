using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>Display lookup only. A found view is not an APPLY or VERIFY result.</summary>
    public static class ManagedSectionViewLookupPolicy
    {
        public enum State { Found, NotApplicable, WrongDrawing, IncompleteIdentity, Unreadable, Missing, Ambiguous, OwnershipMismatch }
        public sealed record Result(State Status, string? SectionViewHandle, string Reason)
        {
            public bool Found => Status == State.Found;
        }
        public sealed record Candidate(
            string Handle, bool IsSectionView, OwnershipMetadata Ownership, string? ParentSampleLineHandle = null);

        public static Result Resolve(
            string? expectedDrawing, string? currentDrawing, string? profileId,
            string? logicalKey, string? fingerprint, string? sourceClHandle, string? sourceClHash,
            bool readable, IEnumerable<Candidate> candidates)
        {
            if (string.IsNullOrWhiteSpace(expectedDrawing) ||
                !string.Equals(expectedDrawing, currentDrawing, StringComparison.OrdinalIgnoreCase))
                return new(State.WrongDrawing, null, "התכנון אינו שייך לשרטוט הפעיל; יש לתכנן מחדש בשרטוט הנכון.");
            if (new[] { profileId, logicalKey, fingerprint, sourceClHandle, sourceClHash }.Any(string.IsNullOrWhiteSpace))
                return new(State.IncompleteIdentity, null, "חסרה זהות בעלות מלאה לאיתור החתך הקיים; יש לתכנן מחדש.");
            if (!readable)
                return new(State.Unreadable, null, "לא ניתן לקרוא את כל מלאי החתכים ובעלותם; לא נבחר חתך להצגה.");

            var matches = candidates.Where(candidate => string.Equals(
                candidate.Ownership.LogicalKey, logicalKey, StringComparison.Ordinal)).ToList();
            if (matches.Count == 0)
                return new(State.Missing, null, "תצוגת החתך המנוהלת לא נמצאה בשרטוט הנוכחי; אין בכך הוכחה שהחתך מעולם לא נוצר.");
            if (matches.Any(candidate =>
                string.IsNullOrWhiteSpace(candidate.Handle) ||
                !string.Equals(candidate.Ownership.Feature, "sections", StringComparison.Ordinal) ||
                !string.Equals(candidate.Ownership.Role, candidate.IsSectionView ? "section-view" : "sample-line", StringComparison.Ordinal) ||
                !string.Equals(candidate.Ownership.ProjectProfileId, profileId, StringComparison.Ordinal) ||
                !string.Equals(candidate.Ownership.InputFingerprint, fingerprint, StringComparison.Ordinal) ||
                !string.Equals(candidate.Ownership.SourceClHandle, sourceClHandle, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(candidate.Ownership.SourceClDrawingHash, sourceClHash, StringComparison.OrdinalIgnoreCase)))
                return new(State.OwnershipMismatch, null, "נמצא אובייקט עם מפתח החתך אך בעלות או מקור שונים; לא נבחר חתך להצגה.");

            var views = matches.Where(candidate => candidate.IsSectionView).ToList();
            var lines = matches.Where(candidate => !candidate.IsSectionView).ToList();
            if (views.Count > 1 || lines.Count > 1)
                return new(State.Ambiguous, null, "נמצא יותר מזוג מנוהל אחד עבור החתך; יש לפתור את הכפילות לפני ההצגה.");
            if (views.Count != 1 || lines.Count != 1)
                return new(State.Missing, null, "לא נמצא זוג מלא של קו דגימה ותצוגת חתך בבעלות הכלי.");
            if (!string.Equals(views[0].ParentSampleLineHandle, lines[0].Handle, StringComparison.OrdinalIgnoreCase))
                return new(State.OwnershipMismatch, null, "תצוגת החתך אינה מקושרת לקו הדגימה בעל אותה זהות.");
            return new(State.Found, views[0].Handle, "נמצא חתך מנוהל יחיד לפי בעלות ומקור; ההצגה אינה אימות הנדסי.");
        }
    }
}
