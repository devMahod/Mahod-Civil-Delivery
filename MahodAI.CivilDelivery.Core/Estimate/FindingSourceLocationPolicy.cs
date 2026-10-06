using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>Read-only navigation identity. Never creates a quantity or resolves a finding.</summary>
public static class FindingSourceLocationPolicy
{
    public sealed record Target(string Code, string Title, ProvenanceRef Source)
    {
        public IReadOnlyList<FindingSourceContextPolicy.Context> Contexts { get; init; } = Array.Empty<FindingSourceContextPolicy.Context>();
        public string Display => $"{Source.SourceHandle ?? "ללא handle"} · {Source.Layer ?? "ללא שכבה"} · {Source.MeasurementMethod} · {Source.SourcePathOrUri ?? "ללא נתיב"}";
        public string? UnavailableReason => Validate(Source);
    }

    public static IReadOnlyList<Target> Collect(IEnumerable<EstimateReviewPolicy.Issue> issues) =>
        issues.SelectMany(issue => issue.Sources.Select(source => new Target(issue.Code, issue.Title, source)
                { Contexts = new[] { FindingSourceContextPolicy.Capture(issue, source) } }))
            .GroupBy(target => string.Join("\u001f", target.Code, target.Source.SourceKind,
                target.Source.SourcePathOrUri, target.Source.DrawingChecksum, target.Source.SourceHandle,
                target.Source.XrefPath, target.Source.EntityType, target.Source.Layer, target.Source.MeasurementMethod))
            .Select(group => group.First() with { Contexts = group.SelectMany(target => target.Contexts).Distinct().ToArray() })
            .ToArray();

    public static string? Validate(ProvenanceRef source)
    {
        if (source.SourceKind is not ("drawing" or "xref" or "civil-model")) return "המקור אינו עצם DWG מתועד";
        // Only a whole host Corridor has a supported Civil locator route. A Civil
        // context string is evidence, never permission to guess a child/parent ID.
        if (source.SourceKind == "civil-model" &&
            (!string.Equals(source.EntityType, "Corridor", StringComparison.OrdinalIgnoreCase) ||
             source.MeasurementMethod != "civil-model-quantity" ||
             !string.IsNullOrWhiteSpace(source.XrefPath) || source.XrefTransform != null ||
             source.SourceHandle?.Contains('/') == true))
            return "איתור Civil נתמך רק עבור Corridor שלם במארח, ללא XREF או תת-ישות";
        if (!IsDriveQualifiedDrawingPath(source.SourcePathOrUri)) return "נדרש נתיב DWG מלא עם אות כונן; רשת נבדקת לפני גישה";
        if (!CatalogIdentity.IsValidSha256(source.DrawingChecksum)) return "לא תועד SHA-256 תקין למקור";
        var parts = source.SourceHandle?.Split('/');
        if (parts == null || parts.Length == 0 || parts.Any(part => part.Length == 0 ||
            !long.TryParse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) || value <= 0))
            return "לא תועדה שרשרת handles תקינה";
        if (source.SourceKind == "xref" && (parts.Length < 2 || string.IsNullOrWhiteSpace(source.XrefPath)))
            return "חסרה שרשרת XREF; אין לחפש את handle העלה בשרטוט המארח";
        if (!string.IsNullOrWhiteSpace(source.SourceSubentityPath)) return "איתור תת-ישות אינו נתמך במסלול זה";
        return null;
    }

    // Syntax only; a drive letter may be mapped to a server. Native I/O must also call LocalDriveFailure.
    public static bool IsDriveQualifiedDrawingPath(string? path) => !string.IsNullOrWhiteSpace(path) &&
        path.Length >= 4 && char.IsLetter(path[0]) && path[1] == ':' &&
        (path[2] == '\\' || path[2] == '/') &&
        string.Equals(Path.GetExtension(path), ".dwg", StringComparison.OrdinalIgnoreCase);

    public static bool SamePath(string? left, string? right)
    {
        if (!IsDriveQualifiedDrawingPath(left) || !IsDriveQualifiedDrawingPath(right)) return false;
        try { return string.Equals(Path.GetFullPath(left!), Path.GetFullPath(right!), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public static string? LocalDriveFailure(string? path, Func<string, DriveType> getDriveType)
    {
        if (!IsDriveQualifiedDrawingPath(path)) return "נתיב רשת, device או נתיב DWG לא מלא אינו מורשה לאיתור";
        try
        {
            // Query the drive mapping only. Never probe the source with Exists/Open to discover its type.
            var kind = getDriveType(path![..3]);
            return kind is DriveType.Fixed or DriveType.Removable or DriveType.Ram
                ? null : "כונן רשת או כונן לא מזוהה אינו מורשה; לא בוצעה גישה לקובץ המקור";
        }
        catch { return "לא ניתן לאמת שהכונן מקומי; לא בוצעה גישה לקובץ המקור"; }
    }

    public static string? IdentityFailure(ProvenanceRef source, string actualPath, string actualHash, string? actualXref)
    {
        var invalid = Validate(source);
        if (invalid != null) return invalid;
        if (!SamePath(source.SourcePathOrUri, actualPath)) return "נתיב העצם הטעון שונה מהמקור המתועד";
        if (!string.Equals(source.DrawingChecksum, actualHash, StringComparison.OrdinalIgnoreCase))
            return "SHA-256 המקור השתנה; נדרשת סריקה חדשה";
        if (!string.Equals(source.XrefPath ?? "", actualXref ?? "", StringComparison.Ordinal))
            return "שרשרת XREF אינה תואמת למקור המתועד";
        return null;
    }
}
