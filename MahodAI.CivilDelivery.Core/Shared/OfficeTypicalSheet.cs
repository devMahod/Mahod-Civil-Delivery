using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// The office's typical-section sheet as data (2026-11-02, owner: "exactly the same as the separate sheet" - the office's
/// MHD-HW-000-IC_00000-DD-500X, drawn in its NTS drawing): the texts in the office's own encoding, the titles, the
/// pavement-layer callouts read from the corridor's shape codes, and the office's symbols. Pure logic; the plugin draws.
/// </summary>
public static class OfficeTypicalSheet
{
    // ------------------------------------------------------------------------------------------- text encoding

    // The office's Hebrew SHX fonts (miryl, mirym; 0-miryl here) are keyboard-mapped and draw right to left: "auk" is
    // "שול", and a run of digits or Latin is stored reversed ("04" shows 40, "01-07GP" shows PG70-10).
    private static readonly Dictionary<char, char> Keys = new()
    {
        ['א'] = 't', ['ב'] = 'c', ['ג'] = 'd', ['ד'] = 's', ['ה'] = 'v', ['ו'] = 'u', ['ז'] = 'z', ['ח'] = 'j',
        ['ט'] = 'y', ['י'] = 'h', ['כ'] = 'f', ['ך'] = 'l', ['ל'] = 'k', ['מ'] = 'n', ['ם'] = 'o', ['נ'] = 'b',
        ['ן'] = 'i', ['ס'] = 'x', ['ע'] = 'g', ['פ'] = 'p', ['ף'] = ';', ['צ'] = 'm', ['ץ'] = '.', ['ק'] = 'e',
        ['ר'] = 'r', ['ש'] = 'a', ['ת'] = ',', ['\''] = 'w', ['׳'] = 'w', ['״'] = '"',
    };

    private static readonly Regex LtrRun = new(@"[0-9A-Za-z][0-9A-Za-z.,:/%+\-]*[0-9A-Za-z%]|[0-9A-Za-z]", RegexOptions.Compiled);

    /// <summary>
    /// A line written in logical order (as one reads it) in the office's keyboard-mapped right-to-left SHX encoding:
    /// Hebrew letters to their keys, every run of digits or Latin reversed so the font draws it the right way round.
    /// The office's own texts decode back to their reading this way ("nxgv jsav (fukk tdu\"n)-fcha 04").
    /// </summary>
    public static string ShxHebrew(string logical)
    {
        if (string.IsNullOrEmpty(logical)) return logical ?? string.Empty;
        var reversed = LtrRun.Replace(logical, m =>
        {
            var chars = m.Value.ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        });
        var sb = new StringBuilder(reversed.Length);
        foreach (var ch in reversed) sb.Append(Keys.TryGetValue(ch, out var k) ? k : ch);
        return sb.ToString();
    }

    /// <summary>True when the text has a Hebrew letter (it goes in the Hebrew style; numbers and Latin go LTR).</summary>
    public static bool HasHebrew(string text) => text.Any(c => c >= '֐' && c <= '׿');

    // ------------------------------------------------------------------------------------------------- titles

    private static readonly Regex RoadNumber = new(@"^(?:ROAD|RD|R|KVISH)[-_ ]?(\d+)(?:[-_ ].*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RampNumber = new(@"^(?:RAMP|RAMPA)[-_ ]?(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"כביש 40" for ROAD-40 / RD-40, "רמפה מס' 13" for RAMP-13, the alignment's own name otherwise.</summary>
    public static string RoadName(string alignmentName)
    {
        var name = alignmentName?.Trim() ?? string.Empty;
        var ramp = RampNumber.Match(name);
        if (ramp.Success) return "רמפה מס' " + ramp.Groups[1].Value;
        var road = RoadNumber.Match(name);
        return road.Success ? "כביש " + road.Groups[1].Value : name;
    }

    /// <summary>
    /// The office's road title: "מסעה חדשה (כולל אגו"מ)-כביש 40" - a new carriageway with or without the granular
    /// sub-base (AGUM) its corridor builds.
    /// </summary>
    public static string RoadTitle(string alignmentName, bool withAgum) =>
        (withAgum ? "מסעה חדשה (כולל אגו\"מ)-" : "מסעה חדשה (ללא אגו\"מ)-") + RoadName(alignmentName);

    /// <summary>
    /// The section's number on the office's sheets: the station over the firm's 20 m interval, rounded down, as its
    /// section program names them (6403: "ROAD-3933 - 3961 (79220.00)", "RAMP-13 - 1300 (26017.90)"; the plan labels
    /// ROAD-40 79700 "3985").
    /// </summary>
    public static long? SectionNumber(double station, double intervalM = 20.0)
    {
        if (!double.IsFinite(station) || intervalM <= 0) return null;
        return (long)Math.Floor(station / intervalM + 1e-6);
    }

    /// <summary>"חת' 3985" ("תחנה ..." for a station no number can be given).</summary>
    public static string SectionLabel(double station, double intervalM = 20.0) =>
        SectionNumber(station, intervalM) is { } n
            ? "חת' " + n.ToString(CultureInfo.InvariantCulture)
            : "תחנה " + station.ToString("0.00", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------------------------- pavement callouts

    /// <summary>One row of the layer callout: the line above the leader's shelf and the line below it.</summary>
    public sealed record Callout(string Code, double ThicknessM, string Up, string? Down, int Count = 1);

    private static readonly Regex Asphalt = new(@"^(KIRZUF-)?ASF(-TAMA)?-(\d+(?:\.\d+)?)-(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The thickness a shape code states (ASF-5-70 is 5 cm), or null when the code states none.</summary>
    public static double? CodedThicknessM(string code)
    {
        var m = Asphalt.Match(code ?? string.Empty);
        return m.Success ? double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) / 100.0 : null;
    }

    private static string Cm(double m) => Math.Round(m * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>
    /// The callout rows of one pavement structure, top down, in the office's words for each layer its corridor codes
    /// (6403 RD-40: ASF-TAMA-5-70, ASF-5-70, ASF-5-68, ASF-6-68, AGUM, MAZA-A, MAZ-A, MAZA-B). The thickness is the
    /// shape's own, measured; the binder grade is the one the code states. Two consecutive equal layers of one material
    /// are one row, as the office writes "2x12 ס"מ מצע סוג א'". Nothing the drawing does not say is added (aggregate
    /// size, mineral, the tack coats).
    /// </summary>
    public static IReadOnlyList<Callout> Describe(IEnumerable<(string Code, double ThicknessM)> stackTopDown)
    {
        var rows = new List<Callout>();
        foreach (var (code, thickness) in stackTopDown)
        {
            var (material, down) = Material(code);
            var last = rows.Count > 0 ? rows[^1] : null;
            if (last != null && Material(last.Code).Material == material && Math.Abs(last.ThicknessM - thickness) < 0.005 && !material.Contains("אספלט"))
            {
                rows[^1] = last with { Count = last.Count + 1, Up = $"{last.Count + 1}x{Cm(thickness)} ס\"מ {material}" };
                continue;
            }
            rows.Add(new Callout(code, thickness, $"{Cm(thickness)} ס\"מ {material}", down));
        }
        return rows;
    }

    private static (string Material, string? Down) Material(string code)
    {
        var c = (code ?? string.Empty).Trim();
        var asphalt = Asphalt.Match(c);
        if (asphalt.Success)
        {
            var kind = asphalt.Groups[2].Success ? "שכבת אספלט מסוג תאמ\"א" : "שכבת אספלט תא\"צ";
            if (asphalt.Groups[1].Success) kind += " (לאחר קרצוף)";
            return (kind, $"ביטומן מסוג PG{asphalt.Groups[4].Value}-10");
        }
        var u = c.ToUpperInvariant();
        if (u == "AGUM") return ("אגו\"מ", "מהודק בהידוק מבוקר ל-100%");
        if (u is "MAZA-A" or "MAZ-A") return ("מצע סוג א'", "מהודק בהידוק מבוקר לדרגת צפיפות 100%");
        if (u is "MAZA-B" or "MAZ-B") return ("מצע סוג ב'", "מהודק בהידוק מבוקר לדרגת צפיפות 100%");
        if (u is "ASF-B" or "ASF-BIKE") return ("שכבת אספלט לשביל אופניים", null);
        return (c, null);
    }

    /// <summary>"סה"כ עובי מבנה" over "84 ס"מ": the structure's total, the sum of its measured layers.</summary>
    public static (string Line1, string Line2, string Line3) Total(double totalM) => ("סה\"כ עובי", "מבנה", Cm(totalM) + " ס\"מ");

    /// <summary>
    /// The number of the first new sheet: one past the highest "&lt;base&gt;-NN" layout already in the drawing, so a second
    /// run adds MHD-TYP-02 beside MHD-TYP-01 (2026-11-03; it made "MHD-TYP-01_2").
    /// </summary>
    public static int FirstFreeSheetNumber(IEnumerable<string> layoutNames, string baseName)
    {
        var pattern = new Regex("^" + Regex.Escape(baseName) + @"-(\d+)(?:_\d+)?$", RegexOptions.IgnoreCase);
        var highest = 0;
        foreach (var name in layoutNames)
        {
            var match = pattern.Match(name ?? "");
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                highest = Math.Max(highest, n);
        }
        return highest + 1;
    }

    // ------------------------------------------------------------------------------------------------ symbols

    /// <summary>One primitive of an office symbol, in block units (see <see cref="TypicalSymbolData"/>).</summary>
    public sealed record SymbolPrim(string Type, int Color, double[] P, bool Closed, double Width, string? Pattern, double[][]? Loops);

    /// <summary>The primitives of an office symbol by its name in <see cref="TypicalSymbolData.All"/>.</summary>
    public static IReadOnlyList<SymbolPrim> Symbol(string name)
    {
        if (!TypicalSymbolData.All.TryGetValue(name, out var data)) throw new ArgumentException("no symbol " + name, nameof(name));
        using var doc = JsonDocument.Parse(data.Json);
        var list = new List<SymbolPrim>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            static double[] Arr(JsonElement a) => a.EnumerateArray().Select(v => v.GetDouble()).ToArray();
            list.Add(new SymbolPrim(
                e.GetProperty("t").GetString()!,
                e.TryGetProperty("c", out var c) ? c.GetInt32() : 0,
                e.TryGetProperty("p", out var p) ? Arr(p) : Array.Empty<double>(),
                e.TryGetProperty("cl", out var cl) && cl.GetInt32() == 1,
                e.TryGetProperty("w", out var w) ? w.GetDouble() : 0,
                e.TryGetProperty("pat", out var pat) ? pat.GetString() : null,
                e.TryGetProperty("loops", out var loops) ? loops.EnumerateArray().Select(Arr).ToArray() : null));
        }
        return list;
    }
}
