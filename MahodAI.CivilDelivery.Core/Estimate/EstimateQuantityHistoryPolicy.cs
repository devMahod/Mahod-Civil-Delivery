using System;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>Historical measurements are display-only and never grant estimate authority.</summary>
public static class EstimateQuantityHistoryPolicy
{
    public static bool CanRetain(string? sourceDrawing, string? activeDrawing, int recordCount) =>
        recordCount > 0 && !string.IsNullOrWhiteSpace(sourceDrawing) &&
        !string.IsNullOrWhiteSpace(activeDrawing) &&
        string.Equals(sourceDrawing, activeDrawing, StringComparison.OrdinalIgnoreCase);

    public static EstimateQuantityPresentationPolicy.State Presentation(string reason) => new(
        "מדידה קודמת",
        "מדידה היסטורית — לא עדכנית. לעיון בלבד; אין לאשר שיוך, לבנות או לייצא על סמך המדידות האלה. " +
        "השיוכים המוצגים שייכים לסריקה הקודמת. נדרשת סריקה חדשה.\n" + reason,
        null, "review", false, true, false);
}
