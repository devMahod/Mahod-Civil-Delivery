using System;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// The export notice under the estimate buttons (measurement draft, rules bill, corridor bill, priced draft, or the failure
/// of one of them) belongs to the drawing whose command wrote it, not to a scan: a corridor export runs without one
/// (Codex review 01.10, 00:22 — a scan-based reset missed it). A drawing switch or an explicit profile change hides it;
/// the exported file and the profile's decisions stay where they are, and returning to the owning drawing does not bring
/// an old notice back. A new result or failure always replaces the previous notice, so no stale success outlives a failure.
/// </summary>
internal sealed class DrawingScopedNotice
{
    public string Text { get; private set; } = string.Empty;
    public string? OwnerDrawing { get; private set; }

    public void Set(string text, string? ownerDrawing)
    {
        Text = text ?? string.Empty;
        OwnerDrawing = Text.Length == 0 ? null : ownerDrawing;
    }

    /// <summary>Clears the notice unless the active drawing is its known owner; an unknown owner or drawing clears it.</summary>
    /// <returns>true when a visible notice was cleared.</returns>
    public bool ClearUnlessOwnedBy(string? activeDrawing)
    {
        if (Text.Length == 0) return false;
        if (OwnerDrawing != null && activeDrawing != null &&
            string.Equals(OwnerDrawing, activeDrawing, StringComparison.OrdinalIgnoreCase))
            return false;
        Clear();
        return true;
    }

    /// <summary>
    /// The owning drawing is closing (Codex review 01.10, 00:45): clear, so a reopen of the same path starts clean. Closing
    /// any other drawing never touches a valid notice.
    /// </summary>
    /// <returns>true when a visible notice was cleared.</returns>
    public bool ClearIfOwnedBy(string? closingDrawing)
    {
        if (Text.Length == 0 || OwnerDrawing == null || closingDrawing == null ||
            !string.Equals(OwnerDrawing, closingDrawing, StringComparison.OrdinalIgnoreCase))
            return false;
        Clear();
        return true;
    }

    public void Clear()
    {
        Text = string.Empty;
        OwnerDrawing = null;
    }
}
