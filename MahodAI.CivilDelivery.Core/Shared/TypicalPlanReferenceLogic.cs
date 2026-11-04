using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Which plan references a typical section is cut from, and where their files are (2026-11-04, a coworker's 6403: the
/// sheet came out with the axis only - no lanes, no widths, no cars). The office keeps its big drawings light by
/// leaving the plan references UNLOADED (6403: the geometry plan HW-IC_4NATIV-GM-M44, the dimensions, CL, EV - all
/// unloaded in the saved file), and the lane lines live in the marking plan (HW-IC_00000-SM-M38), which 6403 does not
/// reference at all - in the office's packages it sits in the same folder as the geometry plan. The cut reads only
/// what is loaded, so it read nothing.
///
/// The tool now opens what the section is read from for the length of the run and puts the drawing back after: the
/// unloaded references that carry the road (geometry, marking, traffic stages, the tree survey), and the project's
/// marking plan from beside the geometry plan when no reference is one.
/// </summary>
public static class TypicalPlanReferenceLogic
{
    /// <summary>Whether a reference carries what a typical section is cut from, by the office's file naming.</summary>
    public static bool Carries(string? referenceName)
    {
        var name = (referenceName ?? string.Empty).ToUpperInvariant();
        if (name.Length == 0) return false;
        return IsGeometry(name) || IsMarking(name) || name.Contains("-TT-") || name.Contains("_TT_") || name.Contains("TREE") || name.Contains("עצים");
    }

    /// <summary>The geometry plan (GM): carriageway edges, shoulders, curbs, sidewalks, bike paths.</summary>
    public static bool IsGeometry(string? name) => Has(name, "-GM-") || Has(name, "_GM_") || Has(name, "-GM_");

    /// <summary>The marking plan (SM): the lane lines.</summary>
    public static bool IsMarking(string? name) => Has(name, "-SM-") || Has(name, "_SM_") || Has(name, "-SM_");

    private static bool Has(string? name, string part) =>
        (name ?? string.Empty).IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Where a reference's file may be, in order: its stored path (a relative one from the drawing's folder), then the
    /// same file name in the drawing's folder and its PD sub-folder (a copy sent with its references beside it).
    /// </summary>
    public static IReadOnlyList<string> Candidates(string? storedPath, string? hostFolder)
    {
        var result = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { path = Path.GetFullPath(path!); } catch { return; }
            if (!result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
        }
        if (string.IsNullOrWhiteSpace(storedPath)) return result;
        var stored = storedPath!.Trim();
        if (Path.IsPathRooted(stored)) Add(stored);
        else if (!string.IsNullOrWhiteSpace(hostFolder)) Add(Path.Combine(hostFolder!, stored));
        var file = Path.GetFileName(stored);
        if (!string.IsNullOrWhiteSpace(hostFolder) && file.Length > 0)
        {
            Add(Path.Combine(hostFolder!, file));
            Add(Path.Combine(hostFolder!, "PD", file));
        }
        return result;
    }

    /// <summary>The project prefix of an office file name: "HW-IC" of "HW-IC_4NATIV-GM-M44".</summary>
    public static string ProjectPrefix(string? fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        var cut = name.IndexOf('_');
        return cut > 0 ? name.Substring(0, cut) : string.Empty;
    }

    /// <summary>
    /// The marking plans to read when no reference of the drawing is one: the "*-SM-*" drawings of the same project
    /// (<see cref="ProjectPrefix"/>) in the folders of its geometry plans. Empty when a marking plan is referenced
    /// already, or none is found.
    /// </summary>
    public static IReadOnlyList<string> MarkingPlans(
        IEnumerable<(string Name, string? File)> references, Func<string, IEnumerable<string>> drawingsIn)
    {
        var list = references.ToList();
        if (list.Any(r => IsMarking(r.Name) || IsMarking(Path.GetFileName(r.File ?? string.Empty)))) return Array.Empty<string>();
        var found = new List<string>();
        foreach (var geometry in list.Where(r => IsGeometry(r.Name) && !string.IsNullOrWhiteSpace(r.File)))
        {
            var folder = Path.GetDirectoryName(geometry.File!);
            var prefix = ProjectPrefix(geometry.File);
            if (string.IsNullOrEmpty(folder) || prefix.Length == 0) continue;
            IEnumerable<string> files;
            try { files = drawingsIn(folder!).ToList(); }
            catch { continue; }
            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                if (!name.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) || !IsMarking(name)) continue;
                if (!string.Equals(ProjectPrefix(name), prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!found.Contains(file, StringComparer.OrdinalIgnoreCase)) found.Add(file);
            }
        }
        return found;
    }

    /// <summary>The run's note: what was opened for the cut, and that the drawing is as it was.</summary>
    public static string? OpenedNoteHe(IReadOnlyCollection<string> loaded, IReadOnlyCollection<string> attached)
    {
        if (loaded.Count == 0 && attached.Count == 0) return null;
        var parts = new List<string>();
        if (loaded.Count > 0) parts.Add($"נטענו לקריאה ההפניות שהיו כבויות בשרטוט ({string.Join(", ", loaded)})");
        if (attached.Count > 0) parts.Add($"נקראה תוכנית הסימון מהתיקייה של תוכנית הגיאומטריה ({string.Join(", ", attached)})");
        return string.Join("; ", parts) + " — כדי לקרוא מהן את הנתיבים, השוליים ואבני השפה. בסוף ההרצה השרטוט הוחזר כפי שהיה.";
    }

    /// <summary>The run's note on references whose file was not found anywhere: the section misses what they carry.</summary>
    public static string? NotFoundNoteHe(IReadOnlyCollection<string> notFound) =>
        notFound.Count == 0
            ? null
            : $"לא נמצא הקובץ של ההפניות {string.Join(", ", notFound)} — החתך הטיפוסי לא קרא מהן קווי כביש. יש לפתוח את השרטוט מתיקיית הפרויקט (או לתקן את הנתיב ב-External References) וליצור שוב.";

    /// <summary>The note when the plan has no lane lines at all: no marking plan referenced or found.</summary>
    public const string NoMarkingPlanHe =
        "בתנוחה אין תוכנית סימון (SM) — בלי קווי נתיב אי אפשר לחלק את המיסעה לנתיבים, והיא מסומנת כמיסעה אחת עם רוחבה. כדי לקבל נתיבים ורכבים יש לצרף לשרטוט את תוכנית הסימון של הפרויקט וליצור שוב.";
}
