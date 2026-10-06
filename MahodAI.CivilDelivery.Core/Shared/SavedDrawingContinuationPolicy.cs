namespace MahodAI.CivilDelivery.Shared
{
    public enum SavedDrawingContinuationDecision
    {
        NoPending,
        DrawingChanged,
        SaveIncomplete,
        Resume,
        /// <summary>The explicit save completed but the drawing is dirty again; one more explicit save is requested.</summary>
        SaveAgain,
    }

    /// <summary>
    /// Decides whether a consumed, explicit save request can continue. The caller
    /// supplies a fresh source-readiness check, not the state before QSAVE, and
    /// compares the exact originating document/database rather than its filename.
    /// </summary>
    public static class SavedDrawingContinuationPolicy
    {
        /// <summary>An old queued command cannot consume a cancelled/newer request.</summary>
        public static bool CanConsume(string? pendingToken, string? incomingToken, bool hasPending) =>
            hasPending && !string.IsNullOrWhiteSpace(pendingToken) &&
            !string.IsNullOrWhiteSpace(incomingToken) &&
            string.Equals(pendingToken, incomingToken, System.StringComparison.Ordinal);

        public static SavedDrawingContinuationDecision Decide(
            bool hasPending, bool sameDocument, bool sourceReady) =>
            Decide(hasPending, sameDocument, sourceReady,
                canSaveAndResume: false, savesRequested: MaxExplicitSaves);

        /// <summary>Explicit saves one request may issue before it fails closed.</summary>
        public const int MaxExplicitSaves = 2;

        /// <summary>
        /// Civil can re-dirty the database while QSAVE completes: live 07/09 17:29 the
        /// first save after a committed APPLY wrote the DWG and DBMOD read 1 again; the
        /// second explicit save read 0. A recoverable "please save" readiness after a
        /// completed explicit save is therefore answered with exactly one more explicit
        /// save. An identity failure a save cannot repair, or a second dirty read-back,
        /// stays SaveIncomplete.
        /// </summary>
        public static SavedDrawingContinuationDecision Decide(
            bool hasPending, bool sameDocument, bool sourceReady,
            bool canSaveAndResume, int savesRequested)
        {
            if (!hasPending) return SavedDrawingContinuationDecision.NoPending;
            if (!sameDocument) return SavedDrawingContinuationDecision.DrawingChanged;
            if (sourceReady) return SavedDrawingContinuationDecision.Resume;
            return canSaveAndResume && savesRequested >= 1 && savesRequested < MaxExplicitSaves
                ? SavedDrawingContinuationDecision.SaveAgain
                : SavedDrawingContinuationDecision.SaveIncomplete;
        }
    }
}
