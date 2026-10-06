using System;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>
/// Location evidence for an unsuccessful measurement, not a recovered quantity or
/// a proof of affected output rows. Callers must supply the actual traversal source.
/// </summary>
public static class MeasurementFailureProvenance
{
    public static DeliveryFinding Create(string? projectProfileId, string title, string? reason,
        ProvenanceRef? source = null, string? measurementKind = null)
    {
        var detail = reason ?? string.Empty;
        if (source != null)
        {
            // Keep this readable in consumers that show only Message. Structured
            // SourceRefs retain the exact strings, without escaping or truncation.
            detail += (detail.Length == 0 ? "" : Environment.NewLine) +
                $"measurement-kind={Display(measurementKind)}; " +
                $"source={Display(source.SourcePathOrUri)}; sha256={Display(source.DrawingChecksum)}; " +
                $"handle={Display(source.SourceHandle)}; " +
                $"xref={(source.SourceKind == "drawing" && source.XrefPath == null ? "(host)" : Display(source.XrefPath))}; " +
                $"layer={Display(source.Layer)}; entity={Display(source.EntityType)}; " +
                $"method={Display(source.MeasurementMethod)}";
        }

        return new DeliveryFinding
        {
            Code = EstimateFindingCodes.MeasurementFailed,
            Domain = "estimate",
            Severity = FindingSeverity.Error,
            Title = title,
            Message = detail,
            ProjectProfileId = projectProfileId,
            RunId = source?.RunId,
            SourceRefs = source == null ? new() : new() { source },
            RecommendedAction = source == null
                ? "בדוק את שגיאת הקריאה ואת זהות המקור החסרה. כשל מדידה אינו הוכחה שהגאומטריה פגומה; " +
                  "יש להבחין בין מגבלת קורא לבין פגם מוכח לפני שינוי מקור. לא נקבעו כמות או החרגה."
                : "אתר את קובץ המקור ואת מזהה העצם המדויקים ופתח את פרטי הממצא. " +
                  "כשל מדידה אינו הוכחה שהגאומטריה פגומה: מגבלת קורא או דיוק דורשת פתרון קריאה, " +
                  "לא שינוי גאומטריה כדי לעקוף אותה. לאחר פתרון הסיבה יש לסרוק מחדש; לא נקבעו כמות או החרגה.",
        };
    }

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "(not recorded)" : value;
}
