using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

/// <summary>The concrete elements a road/structures plan carries. A plan has their length, area or count; their section
/// (height, thickness, pile length…) is typed in by the engineer or, until then, a typical value marked as a default.</summary>
public enum ConcreteKind { None, RetainingWall, Footing, Pile, HeadBeam, Slab, Column }

/// <summary>The section of one concrete element. A null field is not typed in: the drawing or the typical value fills it.</summary>
public sealed record ConcreteSizes
{
    public double? HeightM { get; init; }
    public double? ThicknessCm { get; init; }
    public double? WidthM { get; init; }
    public double? FootingWidthM { get; init; }
    public double? FootingDepthCm { get; init; }
    public double? LengthM { get; init; }
    public double? DiameterCm { get; init; }
    public double? SpacingM { get; init; }
    public double? LeanCm { get; init; }
    public double? RebarKgPerM3 { get; init; }
    /// <summary>The plan quantity (length, area or count) when the engineer corrects what the drawing gave.</summary>
    public double? Basis { get; init; }
    public bool IsEmpty => this == new ConcreteSizes();
}

/// <summary>
/// Concrete from the plan (owner's task parked 2026-11-06, closed 2026-11-09). A 2D plan has no height or thickness, so
/// every element's section comes from the engineer, else from the drawing itself (a wall drawn as a closed outline gives its
/// thickness; a pile block "pile 60" its diameter), else from a typical value that every row, note and export marks as a
/// default. Concrete, lean concrete, piles and rebar become BOQ rows aimed at the NTI chapter 02 / 23 items; the plan's
/// own wall or pile line is then not priced a second time (the 2026-11-06 audit found walls priced as wall drains).
/// </summary>
public static class StructureConcrete
{
    public const string DerivedSource = "בטון — חישוב מידות";
    public const string DerivedPart = "בטון";
    public const string Topic = "בטון";
    public const string DefaultMark = "ברירת מחדל";

    public static string KindName(ConcreteKind k) => k switch
    {
        ConcreteKind.RetainingWall => "קיר תומך", ConcreteKind.Footing => "יסוד / רגל קיר", ConcreteKind.Pile => "כלונסאות",
        ConcreteKind.HeadBeam => "קורת ראש", ConcreteKind.Slab => "מרצף / משטח בטון", ConcreteKind.Column => "עמודי בטון", _ => "— לא בטון"
    };

    /// <summary>The plan quantity each kind is computed from.</summary>
    public static string BasisUnit(ConcreteKind k) => k switch
    {
        ConcreteKind.RetainingWall or ConcreteKind.HeadBeam => "מטר",
        ConcreteKind.Pile or ConcreteKind.Column => "יח׳",
        ConcreteKind.Footing or ConcreteKind.Slab => "מ״ר",
        _ => ""
    };

    // ───────────── detection ─────────────

    private static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    // Structures discipline prefixes of the firm convention (S-/ST- structures, BR- bridges) and the road walls (HW-WALL).
    private const string Structural = @"^(?:S|ST|STR|BR|HW)[-_]";
    private static readonly (Regex Pattern, ConcreteKind Kind)[] LayerRules =
    {
        (R(@"PILE|KLONS|KALONS|CALONS|כלונס"), ConcreteKind.Pile),
        (R(@"HEAD-?BEAM|CAP-?BEAM|PILE-?CAP|KORAT-?ROSH|קורת ראש"), ConcreteKind.HeadBeam),
        (R(Structural + @"(?:FOUND|FOOT|FTG|YESOD|BASIS|RAFSODA|RAFT)|יסוד|רפסודה"), ConcreteKind.Footing),
        (R(Structural + @"(?:WALL|KIR|RETAIN)|RETAINING|KIR-?TOMECH|קיר תומך"), ConcreteKind.RetainingWall),
        (R(Structural + @"(?:SLAB|DECK|TIKRA|MIRZAF|FLOOR)|מרצף|תקרה"), ConcreteKind.Slab),
        (R(Structural + @"(?:COLN|COLUMN|COL|AMUD)(?:-|$)"), ConcreteKind.Column),
    };
    // Drafting on structural layers: help lines, notes, hatches of sections, dimensions — never a concrete element.
    private static readonly Regex Drafting = R(@"(?:^|-)(?:HELP|NOTE|TEXT|TX|DIM|HATCH|HTCH|HA|SECT|CS|GRID|CL|AXIS|MISK|BLOCKS|LBL)(?:-|$|\d)");
    private static readonly Regex Number = new(@"(?<!\d)(\d{2,3})(?!\d)", RegexOptions.CultureInvariant);

    /// <summary>The kind a group's layer (or, for a placed block, its block name) names, if any.</summary>
    public static ConcreteKind Detect(InfrastructureGroup g)
    {
        if (g.Source == DerivedSource || g.Source == RoadPavement.DerivedSource || g.Role == LayerRole.Annotation) return ConcreteKind.None;
        var leaf = FirmLayerConventions.Canonical(g.Layer);
        var block = g.GroupPart.StartsWith("בלוק ", StringComparison.Ordinal) ? g.GroupPart[5..] : "";
        // A pile or column symbol counts by its block name, whatever helper layer it sits on (S-BLOCKS "pile 60-pl").
        if (block.Length > 0)
        {
            if (Regex.IsMatch(block, @"PILE|KLONS|כלונס", RegexOptions.IgnoreCase)) return ConcreteKind.Pile;
            if (Regex.IsMatch(block, @"^(?:COL|COLN|COLUMN)(?:[-_ ]?\d|$)", RegexOptions.IgnoreCase) && Regex.IsMatch(leaf, Structural, RegexOptions.IgnoreCase)) return ConcreteKind.Column;
            if (Drafting.IsMatch(leaf)) return ConcreteKind.None;
        }
        else if (Drafting.IsMatch(leaf)) return ConcreteKind.None;
        foreach (var (pattern, kind) in LayerRules)
            if (pattern.IsMatch(leaf)) return kind == ConcreteKind.Column && block.Length == 0 && g.CountUnits <= 0 && g.ClosedCount <= 0 ? ConcreteKind.None : kind;
        return ConcreteKind.None;
    }

    /// <summary>A layer the concrete screen lists: a detected element, or any structures-discipline layer with a quantity
    /// (ST-CONC-1, S-GIRDER…) that the engineer may mark as one.</summary>
    public static bool MaybeConcrete(InfrastructureGroup g) =>
        Detect(g) != ConcreteKind.None ||
        (g.Role != LayerRole.Annotation && g.Source != DerivedSource && g.Source != RoadPavement.DerivedSource &&
         Regex.IsMatch(FirmLayerConventions.Canonical(g.Layer), @"^(?:S|ST|STR|BR)[-_]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
         !Drafting.IsMatch(FirmLayerConventions.Canonical(g.Layer)) &&
         (g.LengthMetres > 0 || g.AreaSquareMetres > 0 || g.CountUnits > 0));

    // ───────────── one element ─────────────

    /// <summary>What the plan gives for a group: length, area, count and, where it can, the section.</summary>
    public sealed record Measured(double LengthM, double AreaM2, int Count, bool Outlines, double? ThicknessCm, double? DiameterCm, string Note);

    public static Measured Measure(InfrastructureGroup g, ConcreteKind kind)
    {
        var count = g.CountUnits > 0 ? (int)Math.Round(g.CountUnits) : g.ClosedCount;
        var outlines = g.Count > 0 && g.ClosedCount == g.Count && g.AreaSquareMetres > 0;
        double? diameter = null;
        var named = Number.Matches(g.GroupPart.Length > 0 ? g.GroupPart : g.LayerName).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .FirstOrDefault(v => v is >= 25 and <= 250);
        if (named > 0) diameter = named;
        else if (outlines && g.CountUnits <= 0 && kind is ConcreteKind.Pile or ConcreteKind.Column)
        {
            // Circles: N closed shapes of area A each, perimeter π·d each.
            var d = 2 * Math.Sqrt(g.AreaSquareMetres / g.ClosedCount / Math.PI);
            if (Math.Abs(Math.PI * d * g.ClosedCount - g.LengthMetres) < 0.05 * g.LengthMetres && d is >= 0.25 and <= 2.5) diameter = Math.Round(d * 100);
        }
        var length = g.LengthMetres; double? thickness = null; var note = "";
        var area = g.AreaSquareMetres;
        if (!outlines && g.Faces is { IsFaces: true } f && kind is ConcreteKind.RetainingWall or ConcreteKind.HeadBeam or ConcreteKind.Footing)
        {
            length = f.AxisM; thickness = Math.Round(f.WidthM * 100);
            note = $"שני קווי פנים: ציר {f.AxisM:N2} מ׳ מתוך {f.DrawnM:N2} מ׳ קווים, רוחב {f.WidthM:0.##} מ׳";
            if (kind == ConcreteKind.Footing) area = f.AxisM * f.WidthM;
        }
        else if (outlines && kind is ConcreteKind.RetainingWall or ConcreteKind.HeadBeam)
        {
            // A wall drawn as its two faces closed into an outline (per outline a long rectangle): axis + thickness = half the
            // perimeter and axis × thickness = area, so axis = (s + √(s² − 4A)) / 2 with s the half perimeter, summed per outline.
            var n = Math.Max(1, g.ClosedCount);
            var s = g.LengthMetres / 2 / n; var a = g.AreaSquareMetres / n;
            var root = s * s - 4 * a;
            length = root >= 0 ? n * (s + Math.Sqrt(root)) / 2 : g.LengthMetres / 2;
            var t = g.AreaSquareMetres / length * 100;
            if (t is >= 10 and <= 300) thickness = Math.Round(t);
            note = "קו חיצוני סגור: ציר ועובי מההיקף והשטח";
        }
        return new(length, kind == ConcreteKind.Footing && !outlines && g.Faces is not { IsFaces: true } ? 0 : area, count, outlines, thickness, diameter, note);
    }

    /// <summary>One resolved section field: its value and where it came from.</summary>
    public sealed record Field(string Name, double Value, string Unit, string Origin)
    {
        public bool IsDefault => Origin == DefaultMark;
        public string Display => $"{Name} {Value.ToString("0.##", CultureInfo.InvariantCulture)} {Unit}" + (Origin.Length > 0 ? $" ({Origin})" : "");
    }

    /// <summary>One concrete pay quantity: m³ of concrete or lean concrete, metres of pile, tons of rebar.</summary>
    public sealed record Quantity(string Name, double Amount, string Unit, string SearchHint, string Formula);

    /// <summary>One concrete element of the plan with its section and its pay quantities.</summary>
    public sealed class Element
    {
        public required InfrastructureGroup Group { get; init; }
        public required ConcreteKind Kind { get; init; }
        public required ConcreteKind DetectedKind { get; init; }
        public required Measured Plan { get; init; }
        public required IReadOnlyList<Field> Fields { get; init; }
        public required IReadOnlyList<Quantity> Quantities { get; init; }
        public required double BasisValue { get; init; }
        public required string BasisOrigin { get; init; }
        public double ConcreteM3 { get; init; }
        public double RebarTon { get; init; }
        /// <summary>No section typed and defaults switched off: listed, not priced.</summary>
        public bool Unpriced { get; init; }
        /// <summary>Priced, but without its rebar row: no ratio typed and defaults switched off.</summary>
        public bool RebarMissing => !Unpriced && Fields.Any(f => f.Name == "זיון" && f.Origin == "חסר");
        public string Key => KeyOf(Group);
        public string KindName => StructureConcrete.KindName(Kind);
        public string LayerName => Group.ShortLayer;
        public string BasisDisplay => $"{BasisValue:N2} {BasisUnit(Kind)}" + (BasisOrigin.Length > 0 ? $" ({BasisOrigin})" : "");
        public bool UsesDefaults => Fields.Any(f => f.IsDefault);
        public string Origin => Unpriced ? "לא תומחר" : UsesDefaults ? DefaultMark : RebarMissing ? "ללא זיון" : "ידני / מהשרטוט";
        public string Section => string.Join(" · ", Fields.Select(f => f.Display));
        public string DefaultFields => string.Join(", ", Fields.Where(f => f.IsDefault).Select(f => f.Name));
    }

    public static string KeyOf(InfrastructureGroup g) => g.Source + "\n" + g.Layer + "\n" + g.GroupPart;

    /// <summary>Typical values (the source each one leans on is in <see cref="DefaultsSource"/>).</summary>
    public static class Typical
    {
        public const double WallHeightM = 3.0, WallStemCm = 30, LeanCm = 5;
        public const double FootingDepthCm = 50, FootingWidthM = 1.8;
        public const double PileLengthM = 12, PileDiameterCm = 60, PileSpacingM = 2.5;
        public const double HeadBeamWidthM = 0.8, HeadBeamHeightM = 0.8;
        public const double SlabCm = 25, ColumnHeightM = 5, ColumnDiameterCm = 60;
        public static double WallFootingWidthM(double h) => Math.Round(0.6 * h, 2);
        public static double WallFootingDepthCm(double h) => Math.Max(40, Math.Round(h * 10 / 5) * 5);
        public static double RebarKgPerM3(ConcreteKind k) => k switch
        {
            ConcreteKind.RetainingWall => 110, ConcreteKind.Footing => 90, ConcreteKind.Pile => 120,
            ConcreteKind.HeadBeam => 130, ConcreteKind.Slab => 100, ConcreteKind.Column => 150, _ => 100
        };
    }

    public const string DefaultsSource =
        "ערכים טיפוסיים לתכנון מוקדם, לא תכנון הפרויקט: קיר תומך — גובה 3 מ׳, עובי גזע 30 ס״מ, רוחב רגל ≈ 0.6H ועובי ≈ H/10 (לפחות 40 ס״מ); " +
        "יסוד — עובי 50 ס״מ; כלונס — קוטר 60 ס״מ (או מהבלוק/העיגול), אורך 12 מ׳, מרווח 2.5 מ׳ בקו כלונסאות; קורת ראש 0.8×0.8 מ׳; מרצף 25 ס״מ; " +
        "עמוד — גובה 5 מ׳; בטון רזה 5 ס״מ מתחת ליסודות ולמרצפים; זיון (ק״ג למ״ק): קיר 110, יסוד 90, כלונס 120, קורה 130, מרצף 100, עמוד 150 " +
        "— לפי ממוצעים מקובלים (One Click LCA, UK: קירות וראשי כלונסאות 110–150, יסודות 90–130, מרצפים 95–135); כלונס ועמוד מוסקים.";

    /// <summary>Resolves the section and pay quantities of one element. <paramref name="separateFooting"/>: the drawing has
    /// its own footing layer, so a wall does not add a footing of its own by default.</summary>
    public static Element Compute(InfrastructureGroup g, ConcreteKind kind, ConcreteSizes typed, bool useDefaults, bool separateFooting)
    {
        var plan = Measure(g, kind);
        var fields = new List<Field>();
        var missing = new HashSet<string>(StringComparer.Ordinal);
        double V(string name, string unit, double? value, double? fromDrawing, double typical, string drawingOrigin = "מהשרטוט")
        {
            if (value is { } v && double.IsFinite(v) && v >= 0) { fields.Add(new(name, v, unit, "")); return v; }
            if (fromDrawing is { } d) { fields.Add(new(name, d, unit, drawingOrigin)); return d; }
            if (!useDefaults) { missing.Add(name); fields.Add(new(name, 0, unit, "חסר")); return 0; }
            fields.Add(new(name, typical, unit, DefaultMark)); return typical;
        }
        var basisOrigin = typed.Basis is > 0 ? "הוזן ידנית" : plan.Note.Length > 0 ? plan.Note : "";
        var q = new List<Quantity>();
        double concrete = 0, rebar = 0, basis;
        var name = KindName(kind);
        switch (kind)
        {
            case ConcreteKind.RetainingWall:
            {
                basis = typed.Basis ?? plan.LengthM;
                var h = V("גובה", "מ׳", typed.HeightM, null, Typical.WallHeightM);
                var t = V("עובי גזע", "ס״מ", typed.ThicknessCm, plan.ThicknessCm, Typical.WallStemCm);
                var b = V("רוחב רגל", "מ׳", typed.FootingWidthM, separateFooting ? 0 : null, Typical.WallFootingWidthM(h), "יסוד בשכבה נפרדת");
                var d = b > 0 ? V("עובי רגל", "ס״מ", typed.FootingDepthCm, null, Typical.WallFootingDepthCm(h)) : 0;
                var lean = b > 0 ? V("בטון רזה", "ס״מ", typed.LeanCm, null, Typical.LeanCm) : 0;
                var ratio = V("זיון", "ק״ג/מ״ק", typed.RebarKgPerM3, null, Typical.RebarKgPerM3(kind));
                var stem = basis * h * t / 100; var foot = basis * b * d / 100;
                q.Add(new("גזע קיר תומך", stem, "מ״ק", "^קירות תומכים מבטון", $"{basis:N2} מ׳ × {h:0.##} מ׳ × {t / 100:0.##} מ׳"));
                if (foot > 0) q.Add(new("רגל קיר תומך", foot, "מ״ק", "^יסודות עוברים ורגלי קירות תומכים", $"{basis:N2} מ׳ × {b:0.##} מ׳ × {d / 100:0.##} מ׳"));
                if (lean > 0) q.Add(new("בטון רזה מתחת לרגל", basis * b * lean / 100, "מ״ק", "^בטון רזה יצוק מתחת לאלמנטים", $"{basis:N2} מ׳ × {b:0.##} מ׳ × {lean / 100:0.##} מ׳"));
                concrete = stem + foot; rebar = concrete * ratio / 1000;
                break;
            }
            case ConcreteKind.Footing:
            {
                var byArea = plan.AreaM2 > 0;
                basis = typed.Basis ?? (byArea ? plan.AreaM2 : 0);
                var w = byArea || typed.Basis is > 0 ? 1 : V("רוחב", "מ׳", typed.WidthM, null, Typical.FootingWidthM);
                if (!byArea && typed.Basis is not > 0) { basis = plan.LengthM * w; basisOrigin = $"קו יסוד {plan.LengthM:N2} מ׳ × רוחב"; }
                var d = V("עובי", "ס״מ", typed.ThicknessCm, null, Typical.FootingDepthCm);
                var lean = V("בטון רזה", "ס״מ", typed.LeanCm, null, Typical.LeanCm);
                var ratio = V("זיון", "ק״ג/מ״ק", typed.RebarKgPerM3, null, Typical.RebarKgPerM3(kind));
                concrete = basis * d / 100;
                q.Add(new("יסוד", concrete, "מ״ק", "^יסודות עוברים ורגלי קירות תומכים", $"{basis:N2} מ״ר × {d / 100:0.##} מ׳"));
                if (lean > 0) q.Add(new("בטון רזה מתחת ליסוד", basis * lean / 100, "מ״ק", "^בטון רזה יצוק מתחת לאלמנטים", $"{basis:N2} מ״ר × {lean / 100:0.##} מ׳"));
                rebar = concrete * ratio / 1000;
                break;
            }
            case ConcreteKind.Pile:
            {
                var len = V("אורך", "מ׳", typed.LengthM, null, Typical.PileLengthM);
                var dia = V("קוטר", "ס״מ", typed.DiameterCm, plan.DiameterCm, Typical.PileDiameterCm);
                if (typed.Basis is > 0) basis = Math.Round(typed.Basis.Value);
                else if (plan.Count > 0) basis = plan.Count;
                else
                {
                    var s = V("מרווח", "מ׳", typed.SpacingM, null, Typical.PileSpacingM);
                    basis = s > 0 && plan.LengthM > 0 ? Math.Floor(plan.LengthM / s) + 1 : 0;
                    basisOrigin = $"קו כלונסאות {plan.LengthM:N2} מ׳ ÷ מרווח + 1";
                }
                var ratio = V("זיון", "ק״ג/מ״ק", typed.RebarKgPerM3, null, Typical.RebarKgPerM3(kind));
                var band = PileItem(dia, len);
                q.Add(new($"כלונס קוטר {band.DiameterCm:0} ס״מ", basis * len, "מטר", band.Hint, $"{basis:0} כלונסאות × {len:0.##} מ׳"));
                concrete = basis * len * Math.PI * Math.Pow(dia / 200, 2);
                rebar = concrete * ratio / 1000;
                break;
            }
            case ConcreteKind.HeadBeam:
            {
                basis = typed.Basis ?? plan.LengthM;
                var w = V("רוחב", "מ׳", typed.WidthM, plan.ThicknessCm / 100, Typical.HeadBeamWidthM);
                var h = V("גובה", "מ׳", typed.HeightM, null, Typical.HeadBeamHeightM);
                var ratio = V("זיון", "ק״ג/מ״ק", typed.RebarKgPerM3, null, Typical.RebarKgPerM3(kind));
                concrete = basis * w * h;
                q.Add(new("קורת ראש", concrete, "מ״ק", "^קורות ראש מבטון · של כלונסאות", $"{basis:N2} מ׳ × {w:0.##} מ׳ × {h:0.##} מ׳"));
                rebar = concrete * ratio / 1000;
                break;
            }
            case ConcreteKind.Slab:
            {
                basis = typed.Basis ?? plan.AreaM2;
                var t = V("עובי", "ס״מ", typed.ThicknessCm, null, Typical.SlabCm);
                var lean = V("בטון רזה", "ס״מ", typed.LeanCm, null, Typical.LeanCm);
                var ratio = V("זיון", "ק״ג/מ״ק", typed.RebarKgPerM3, null, Typical.RebarKgPerM3(kind));
                concrete = basis * t / 100;
                q.Add(new("מרצף / משטח בטון", concrete, "מ״ק", "^מרצפי בטון ומשטחי בטון", $"{basis:N2} מ״ר × {t / 100:0.##} מ׳"));
                if (lean > 0) q.Add(new("בטון רזה מתחת למרצף", basis * lean / 100, "מ״ק", "^בטון רזה יצוק מתחת לאלמנטים", $"{basis:N2} מ״ר × {lean / 100:0.##} מ׳"));
                rebar = concrete * ratio / 1000;
                break;
            }
            case ConcreteKind.Column:
            {
                basis = typed.Basis is > 0 ? Math.Round(typed.Basis.Value) : plan.Count;
                var h = V("גובה", "מ׳", typed.HeightM, null, Typical.ColumnHeightM);
                var dia = V("קוטר", "ס״מ", typed.DiameterCm, plan.DiameterCm, Typical.ColumnDiameterCm);
                var ratio = V("זיון", "ק״ג/מ״ק", typed.RebarKgPerM3, null, Typical.RebarKgPerM3(kind));
                concrete = basis * h * Math.PI * Math.Pow(dia / 200, 2);
                q.Add(new($"עמודי בטון קוטר {dia:0} ס״מ", concrete, "מ״ק", ColumnHint(dia), $"{basis:0} עמודים × {h:0.##} מ׳ × π·{dia / 100:0.##}²/4"));
                rebar = concrete * ratio / 1000;
                break;
            }
            default:
                basis = 0;
                break;
        }
        if (rebar > 0) q.Add(new($"זיון {name}", rebar, "טון", "^מוטות פלדה מצולעים רתיכים", $"{concrete:N2} מ״ק × {fields.Last(f => f.Name == "זיון").Value:0} ק״ג/מ״ק"));
        // Defaults off: a missing size of the concrete itself leaves the element unpriced; a missing rebar ratio only drops
        // the rebar row (live 2026-11-09: a wall with its height typed and its thickness drawn lost its concrete too).
        var unpriced = missing.Any(m => m != "זיון") || basis <= 0;
        return new Element
        {
            Group = g, Kind = kind, DetectedKind = Detect(g), Plan = plan, Fields = fields, BasisValue = basis, BasisOrigin = basisOrigin,
            Quantities = unpriced ? Array.Empty<Quantity>() : q.Where(x => x.Amount > 0).ToArray(),
            ConcreteM3 = unpriced ? 0 : concrete, RebarTon = unpriced ? 0 : rebar, Unpriced = unpriced,
        };
    }

    // ───────────── NTI items by section ─────────────

    /// <summary>NTI 23.01 drilled-pile diameters (cm) and the depth bands each is priced in (upper bounds in metres).</summary>
    private static readonly (double D, double[] Bands)[] PileBands =
    {
        (30, new[] { 10d }), (40, new[] { 10d }), (45, new[] { 10d }), (50, new[] { 10d, 15 }), (60, new[] { 10d, 15 }),
        (70, new[] { 15d, 20 }), (80, new[] { 15d, 20 }), (90, new[] { 20d, 30, 40 }), (100, new[] { 20d, 30, 40 }), (110, new[] { 20d, 30, 40 }),
        (120, new[] { 20d, 30, 40 }), (130, new[] { 20d, 30, 40 }), (140, new[] { 20d, 30, 40 }), (150, new[] { 20d, 30, 40 }), (160, new[] { 20d, 30, 40 }),
        (170, new[] { 20d, 30, 40 }), (180, new[] { 20d, 30, 40 }), (200, new[] { 20d, 30, 40 }),
    };

    /// <summary>The NTI pile item's wording for a diameter (rounded up to a priced one) and a depth: "עד 10.00", "מעל 10.01 … ועד", "מעל 15.00".</summary>
    public static (double DiameterCm, string Hint) PileItem(double diameterCm, double lengthM)
    {
        var (d, bands) = PileBands.FirstOrDefault(p => p.D >= diameterCm - 0.5);
        if (d == 0) (d, bands) = PileBands[^1];
        var size = $"^כלונס אנכי יצוק באתר · בקוטר {d:0}";
        var i = Array.FindIndex(bands, b => lengthM <= b + 1e-6);
        string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        // Ø30–45 are priced only "עד 10.00": a longer one still takes that item (its evidence states the length).
        if (i == 0 || bands.Length == 1) return (d, $"{size} · בעומק עד {F(bands[0])}");
        // "מעל 10.01 מ' ועד 15.00" (Ø50–80) and "מ- 20.01 מ' ועד 30" (Ø90+): the lower bound is the band's own number.
        if (i > 0) return (d, $"{size} · {F(bands[i - 1] + 0.01)}");
        return (d, $"{size} · מעל {F(bands[^1])}");
    }

    /// <summary>NTI 02.01.0141–0171 round columns by diameter band.</summary>
    public static string ColumnHint(double diameterCm) => "^עמודי בטון ב-40 בחתך עגול · " + (diameterCm switch
    {
        <= 72.5 => "בין 40-70", <= 112.5 => "בין 75-110", <= 152.5 => "בין 115-150", _ => "בין 155-190"
    });

    // ───────────── the plan ─────────────

    public sealed record Plan(IReadOnlyList<Element> Elements)
    {
        public double ConcreteM3 => Elements.Sum(e => e.ConcreteM3);
        public double RebarTon => Elements.Sum(e => e.RebarTon);
        public int Defaults => Elements.Count(e => !e.Unpriced && e.UsesDefaults);
        public int Unpriced => Elements.Count(e => e.Unpriced);
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    }

    /// <summary>Every new concrete element of the active groups: detected from the layer, or set by the engineer.</summary>
    public static Plan Build(IEnumerable<InfrastructureGroup> groups, Settings settings)
    {
        var candidates = groups.Where(g => g.Disposition == InfrastructureDisposition.New && g.Source != DerivedSource && g.Source != RoadPavement.DerivedSource)
            .Select(g => (Group: g, Kind: settings.KindFor(g)))
            .Where(x => x.Kind != ConcreteKind.None && (x.Group.LengthMetres > 0 || x.Group.AreaSquareMetres > 0 || x.Group.CountUnits > 0 || x.Group.ClosedCount > 0))
            .ToArray();
        var separateFooting = candidates.Any(c => c.Kind == ConcreteKind.Footing);
        var elements = candidates.Select(c => Compute(c.Group, c.Kind, settings.SizesFor(c.Group), settings.UseDefaults, separateFooting))
            .OrderBy(e => (int)e.Kind).ThenBy(e => e.LayerName, StringComparer.Ordinal).ToArray();
        var notes = new List<string>();
        if (elements.Any(e => !e.Unpriced && e.UsesDefaults))
            notes.Add($"⚠ {DefaultMark}: {elements.Count(e => !e.Unpriced && e.UsesDefaults)} אלמנטי בטון חושבו בחלקם לפי מידות טיפוסיות (לא מתוכניות הקונסטרוקציה) — יש לאמת מול תכנון הקונסטרוקטור. " + DefaultsSource);
        if (elements.Any(e => e.Unpriced))
            notes.Add($"{elements.Count(e => e.Unpriced)} אלמנטי בטון לא תומחרו: חסרות מידות (ערכי ברירת המחדל כבויים) או שאין כמות בשרטוט.");
        if (elements.Any(e => e.RebarMissing))
            notes.Add($"{elements.Count(e => e.RebarMissing)} אלמנטי בטון תומחרו ללא זיון: לא הוזן יחס זיון (ערכי ברירת המחדל כבויים).");
        return new Plan(elements) { Notes = notes };
    }

    /// <summary>The elements' pay quantities as BOQ rows, priced, auto-assigned and exported like any measured layer.</summary>
    public static IReadOnlyList<InfrastructureGroup> DerivedGroups(Plan plan) => plan.Elements
        .SelectMany(e => e.Quantities.Select(q => new InfrastructureGroup
        {
            Source = DerivedSource,
            Layer = $"{e.KindName} · {e.LayerName} · {q.Name}" + (e.UsesDefaults ? " · " + DefaultMark : ""),
            Topic = Topic,
            Count = 1, LengthMetres = q.Unit == "מטר" ? q.Amount : 0,
            VolumeCubicMetres = q.Unit == "מ״ק" ? q.Amount : 0,
            GroupPart = DerivedPart,
            MeasurementUnit = q.Unit == "טון" ? "טון" : q.Unit,
            WeightTons = q.Unit == "טון" ? q.Amount : 0,
            Evidence = $"{q.Formula} — {e.Section}" + (e.UsesDefaults ? " · " + DefaultMark + " — ערך טיפוסי, לא מתוכנית הקונסטרוקציה; יש לאמת מול הקונסטרוקטור" : ""),
            Element = q.SearchHint,
            Surface = RoadSurfaceKind.None,
        }))
        .ToArray();

    /// <summary>Marks the plan rows the concrete rows price, so a wall line is not also priced by its length.</summary>
    public static void MarkPricedByConcrete(IEnumerable<InfrastructureGroup> groups, Plan plan)
    {
        var priced = plan.Elements.Where(e => !e.Unpriced).Select(e => e.Group).ToHashSet();
        foreach (var g in groups.Where(g => g.Source != DerivedSource)) g.PricedByConcrete = priced.Contains(g);
    }

    // ───────────── per-drawing settings ─────────────

    public sealed record Entry(string Key, ConcreteKind? Kind, ConcreteSizes Sizes);

    /// <summary>The engineer's sections and kind corrections for one drawing (a marked area shares them: a wall is as high in a part of the drawing as in all of it).</summary>
    public sealed class Settings
    {
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        public bool UseDefaults { get; set; } = true;
        public IEnumerable<Entry> Entries => _entries.Values;
        public ConcreteKind KindFor(InfrastructureGroup g) => _entries.TryGetValue(KeyOf(g), out var e) && e.Kind is { } k ? k : Detect(g);
        public ConcreteSizes SizesFor(InfrastructureGroup g) => _entries.TryGetValue(KeyOf(g), out var e) ? e.Sizes : new ConcreteSizes();
        public void SetKind(InfrastructureGroup g, ConcreteKind kind)
        {
            var key = KeyOf(g); var old = _entries.GetValueOrDefault(key);
            _entries[key] = new(key, kind == Detect(g) ? null : kind, old?.Sizes ?? new ConcreteSizes());
            Tidy(key);
        }
        public void SetSizes(InfrastructureGroup g, ConcreteSizes sizes)
        {
            var key = KeyOf(g); var old = _entries.GetValueOrDefault(key);
            _entries[key] = new(key, old?.Kind, sizes);
            Tidy(key);
        }
        private void Tidy(string key) { if (_entries[key] is { Kind: null } e && e.Sizes.IsEmpty) _entries.Remove(key); }
        internal void Put(Entry e) => _entries[e.Key] = e;
    }

    private sealed record Stored(int SchemaVersion, bool UseDefaults, Entry[] Entries);

    public static Settings Load(string path)
    {
        var settings = new Settings();
        try
        {
            if (!File.Exists(path)) return settings;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            if (stored is not { SchemaVersion: 1 } || stored.Entries == null) return settings;
            settings.UseDefaults = stored.UseDefaults;
            foreach (var e in stored.Entries.Where(e => e?.Key != null && e.Sizes != null)) settings.Put(e);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return settings;
    }

    public static void Save(string path, Settings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored(1, settings.UseDefaults, settings.Entries.ToArray()))); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
