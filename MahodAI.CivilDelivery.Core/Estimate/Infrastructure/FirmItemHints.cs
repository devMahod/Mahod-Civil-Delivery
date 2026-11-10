using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

/// <summary>
/// Firm layers whose NAME carries the pay item's specification. Each hint is "^lead · phrase · …" (whole words, see
/// <see cref="InfrastructureCatalogSearch"/>); alternatives are tried in order and the first one the price book has wins.
/// A layer with hints but no matching item gets NO automatic item — it is listed for the engineer, never guessed.
///
/// Live 2026-11-10 (130X road plan): every barrier layer took 19.05.2005 "pedestrian-bridge railing, steel with stainless
/// nets" at ₪1,183/m — ₪41.7M of a ₪111.8M bill — because "מעקה" was the only word the search had; lighting poles took
/// 08.10.0817, a cable guard channel. Road safety barriers are NTI 51.33 (steel, by containment level N2/H1/H2/H4b and
/// working width W1–W7) and 51.36 (concrete).
/// </summary>
public static class FirmItemHints
{
    /// <summary>Null: no firm rule, the ordinary topic search decides. Empty: known element, no automatic item.</summary>
    public static IReadOnlyList<string>? For(string layer, string unit)
    {
        var raw = layer.Split('|').Last().ToUpperInvariant();
        if (raw.LastIndexOf("$0$", StringComparison.Ordinal) is var bound and >= 0) raw = raw[(bound + 3)..];   // a bound XREF
        var dims = Units.Parse(unit).Dimension;
        if (Regex.IsMatch(raw, @"(?:^|[-_])BARI(?:[-_]|$)|^H\d+W\d+(?:[-_]|$)"))
        {
            if (dims != UnitDimension.Length) return Array.Empty<string>();   // terminals / posts as blocks: the engineer chooses
            return Barrier(raw);
        }
        // A pedestrian guide fence (HW-FENC-PDST) is NTI's "גדר הולכה"; it took 51.33.3580, painting an existing fence.
        if (Regex.IsMatch(raw, @"FENC(?:E)?[-_]?(?:PDST|PED)") && dims == UnitDimension.Length) return new[] { "^גדר הולכה" };
        // A railing or fence named without its kind (MAAKE 2, GADER) took a pedestrian-BRIDGE railing at ₪1,183/m: the
        // name does not say which railing or which fence, so the engineer chooses.
        if (Regex.IsMatch(raw, @"(?:^|[-_ ])(?:MAAKE|MAAKEH|GADER|GDER|FENC|FENCE)(?:[-_ \d]|$)")) return Array.Empty<string>();
        if (Regex.IsMatch(raw, @"^(?:UT-|EL-|BL-EL-)?(?:EL-)?LT-POLE|(?:^|[-_])POLE(?:[-_]|$)") && dims == UnitDimension.Count
            && raw.Contains("LT", StringComparison.Ordinal))
            return new[] { "^עמוד תאורה" };
        return null;
    }

    private static IReadOnlyList<string> Barrier(string raw)
    {
        if (Regex.IsMatch(raw, @"RIGD|RIGID|CONC|BETON|NJ(?:[-_]|$)|STEP"))
            return new[] { "^מעקה בטון יצוק באתר בחתך א סימטרי", "^מעקה בטון יצוק באתר" };
        var level = Regex.Match(raw, @"(?<![A-Z])(N[12]|H[1-4]B?|H)(?=W\d|[-_]|$)") is { Success: true } m
            ? m.Groups[1].Value : "";
        if (level == "H") level = "H1";   // "FLEX-H-01": the firm writes H for the normal H1 containment
        if (level == "H4") level = "H4B";
        var width = Regex.Match(raw, @"W([1-8])(?![\d.])") is { Success: true } w ? "W" + w.Groups[1].Value : "";
        var doubleSided = Regex.IsMatch(raw, @"(?:[-_])DS(?:[-_]|$)|DOUBLE|DDSP");
        if (level.Length == 0) return Array.Empty<string>();
        var lv = "ברמת תפקוד " + level.ToLowerInvariant();
        var wd = width.Length > 0 ? " · ברוחב פעיל " + width.ToLowerInvariant() : "";
        var hints = new List<string>();
        if (doubleSided)
        {
            // A double-sided barrier is its own item; a single-sided item would price half of it.
            hints.Add("^מעקה בטיחות מפלדה דו צדדי · " + lv + wd);
            hints.Add("^מעקה בטיחות דו צדדי מפלדה · " + lv + wd);
            if (wd.Length > 0) hints.Add("^מעקה בטיחות מפלדה דו צדדי · " + lv);
            return hints;
        }
        hints.Add("^מעקה בטיחות מפלדה ברמת · " + lv + wd);
        if (wd.Length > 0) hints.Add("^מעקה בטיחות מפלדה ברמת · " + lv);
        return hints;
    }
}
