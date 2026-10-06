using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

/// <summary>Explicit session-only association; never clones, approves or writes a profile or drawing.</summary>
internal static class ExistingProjectProfileSelection
{
    internal sealed record Preview(string Path, string Hash, string ProfileId, string ProjectName,
        string Version, string Decisions);
    internal sealed record Binding(string DrawingPath, string ProfilePath, string ProfileId);
    private static readonly ConditionalWeakTable<object, Binding> Bindings = new();
    private static readonly object Sync = new();

    internal static Binding? Current(object documentToken)
    {
        lock (Sync) return Bindings.TryGetValue(documentToken, out var current) ? current : null;
    }

    internal static string DrawingPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("יש לשמור את השרטוט בשם ובנתיב לפני בחירת פרופיל קיים.");
        return Path.GetFullPath(path);
    }

    internal static string? Resolve(object documentToken, string? drawingPath, string? explicitSelector)
    {
        if (!string.IsNullOrWhiteSpace(explicitSelector)) return explicitSelector;
        var binding = Current(documentToken);
        if (binding == null) return null;
        if (string.IsNullOrWhiteSpace(drawingPath) || !Path.IsPathFullyQualified(drawingPath) ||
            !string.Equals(binding.DrawingPath, Path.GetFullPath(drawingPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("נתיב השרטוט השתנה לאחר בחירת הפרופיל. בחר מחדש פרופיל קיים לשרטוט הזה; לא הועברו החלטות אוטומטית.");
        return binding.ProfilePath;
    }

    internal static Preview Inspect(string path)
    {
        var full = RequireLocalProjectFile(path);
        var loaded = ProjectProfileLoader.LoadFromFile(full);
        if (!loaded.IsUsable || loaded.Profile == null || !CatalogIdentity.IsValidSha256(loaded.ProfileHash))
            throw new InvalidOperationException("הפרופיל אינו תקין ולא נבחר:\n" +
                string.Join("\n", loaded.Findings.Select(finding => finding.Title + ": " + finding.Message)));
        var profile = loaded.Profile;
        var rules = profile.Estimate.QuantitySources.Rules;
        var namedRules = rules.Count(rule => !string.IsNullOrWhiteSpace(rule.ApprovedBy) && rule.ApprovedAtUtc != null);
        var decisions = $"כללי שיוך: {rules.Count}; עם חתימת מאשר: {namedRules}\n" +
            $"מחירי פרויקט: {profile.Estimate.ProjectOverrides.Count}; מקדמים מאושרים: {profile.Estimate.ApprovedAdjustments.Count}\n" +
            $"החלטות החרגה: {profile.Estimate.IgnoredRuleDecisions.Count}\n" +
            $"מחירון: {profile.Estimate.Pricing.PriceBookSnapshotId ?? "לא הוגדר"}\n" +
            $"קובץ מחירון: {profile.Estimate.Catalog.CatalogFile ?? "לא הוגדר"}\n" +
            $"מדיניות מקורות: {profile.Estimate.QuantitySources.SourceScopePolicy ?? "טרם הוגדרה"}\n" +
            $"מדיניות XREF: {profile.Estimate.QuantitySources.XrefPolicy ?? "טרם הוגדרה"}\n" +
            $"מקורות CL: {(profile.Sections.Cl.SourceFiles.Count == 0 ? "אין קובצי CL מוגדרים" : string.Join("; ", profile.Sections.Cl.SourceFiles))}\n" +
            "אלו החלטות קיימות, לא תוצאות מהשרטוט הנוכחי. יחידות, מקור, מחירון וסמכות ההחלטות ייבדקו במסלול הרגיל; החלטה לא תקפה לא נעשית תקפה באמצעות הבחירה.";
        return new(full, loaded.ProfileHash!, profile.ProfileId, profile.ProjectName,
            profile.Provenance.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), decisions);
    }

    internal static void RequireUnchanged(Preview preview)
    {
        var current = Inspect(preview.Path);
        if (!string.Equals(current.Hash, preview.Hash, StringComparison.OrdinalIgnoreCase) || current.ProfileId != preview.ProfileId)
            throw new InvalidOperationException("קובץ הפרופיל השתנה מאז הצגתו. לא הוחלפה הבחירה; פתח ובדוק אותו מחדש.");
    }

    internal static void RequireProfileIdentity(Binding binding, string? loadedProfileId)
    {
        if (loadedProfileId != null && !string.Equals(binding.ProfileId, loadedProfileId, StringComparison.Ordinal))
            throw new InvalidOperationException("זהות הפרויקט בקובץ הנבחר השתנתה. בחר מחדש פרופיל לאחר בדיקתו; לא נטען פרויקט חלופי.");
    }

    internal static void Bind(object documentToken, string drawingPath, Preview preview, Binding? expectedPrior)
    {
        var canonicalDrawing = DrawingPath(drawingPath);
        RequireUnchanged(preview);
        lock (Sync)
        {
            var current = Bindings.TryGetValue(documentToken, out var found) ? found : null;
            if (!ReferenceEquals(current, expectedPrior))
                throw new InvalidOperationException("בחירת הפרופיל של המסמך השתנתה בזמן הבדיקה; הבחירה לא הוחלפה.");
            Bindings.Remove(documentToken);
            Bindings.Add(documentToken, new(canonicalDrawing, preview.Path, preview.ProfileId));
        }
    }

    private static string RequireLocalProjectFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            (Path.GetExtension(path).ToLowerInvariant() is not ".yaml" and not ".yml"))
            throw new InvalidOperationException("בחר קובץ פרופיל YAML מקומי, בנתיב מלא.");
        var full = Path.GetFullPath(path);
        if (new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            throw new InvalidOperationException("בחר עותק מקומי של פרופיל הפרויקט; נתיב רשת אינו נבחר כאן.");
        for (var ancestor = full; !string.IsNullOrWhiteSpace(ancestor); ancestor = Path.GetDirectoryName(ancestor))
        {
            if (ancestor.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("פרופיל מתוך חבילת תוכנה אינו יעד עריכה. בחר פרופיל עבודה מקומי קיים.");
            if ((File.Exists(ancestor) || Directory.Exists(ancestor)) &&
                (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("קישור קבצים או תיקיות אינו יעד פרופיל נתמך; בחר את הקובץ המקומי עצמו.");
        }
        if (!File.Exists(full)) throw new FileNotFoundException("קובץ הפרופיל לא נמצא; לא נבחר פרופיל חלופי.", full);
        if ((File.GetAttributes(full) & FileAttributes.ReadOnly) != 0)
            throw new InvalidOperationException("הפרופיל לקריאה בלבד. בחר פרופיל עבודה מקומי שניתן לערוך.");
        return full;
    }
}
