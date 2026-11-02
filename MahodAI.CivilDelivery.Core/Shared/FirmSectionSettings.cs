using System;
using System.Collections.Generic;
using System.Globalization;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// The settings Civil Delivery hands the firm's cross-section program (MahodCivilNet's
/// <c>Sc_CreateMultipleSectionView</c>, called headless) when it draws ONE typical cross-section at a
/// station the engineer picked on the plan. Pure, so the exact string every run sends to the firm's
/// code is testable without Civil 3D.
/// </summary>
/// <remarks>
/// The firm's code is configured by a flat <c>key=value|key=value</c> string (its <c>Cfg.FromString</c>),
/// with no escaping: a name holding <c>|</c> or <c>=</c> would silently corrupt the whole string, and the
/// firm's code answers a bad string with a modal MessageBox that blocks Civil 3D. Such names are refused
/// here, before the firm's code is reached.
/// <para>
/// <see cref="FirmDefaults"/> is the MahodAI plugin wrapper's real firm setting (794CORR-DD, corridor
/// T_Aznar1), copied verbatim: 1:200, 900×1300 sheets, band step 2 m, by links. <see cref="ForStation"/>
/// then overrides the run-specific keys for a single station: start = end = the station, with the
/// interval from <see cref="IntervalFor"/> so that the firm's station list holds that very station, draws
/// exactly one section; <c>cbRemoveOld=0</c> keeps the sections of the earlier picks
/// (the defaults delete the old ones, which is right for a whole-corridor run and wrong here);
/// <c>cbSplitByPages=0</c> keeps the firm's code from paginating a single block.
/// </para>
/// <para>
/// Numbers are written with the invariant culture on purpose. The plugin wrapper formats with the
/// current culture; here the string must be the same on every engineer's machine and in the tests,
/// and a decimal-comma locale would otherwise write <c>1234,5</c>.
/// </para>
/// </remarks>
public static class FirmSectionSettings
{
    /// <summary>A real firm setting (794CORR-DD, corridor T_Aznar1); the run-specific keys are set by <see cref="ForStation"/>.</summary>
    public const string FirmDefaults =
        "txtMoveLoBands=0|cbDrawHatch=0|cbCustomHeight=0|txtVertCoef=1|cbAddDistance=0|txtHorOffs=0|cbSAByLayers=0|" +
        "txtBandHeight=0.75|txtBandStep=2|cbVertElevMM=0|cbCustomWidth=0|cbAdditional=0|cbSplitByPages=1|txtGridY=5|" +
        "txtGridX=2|cbGrid=0|txtMoveUpBands=0|cbSettingType=0|txtRiseLower=0|txtVertGap=10|txtHorGap=10|" +
        "cmbStationFormat=31+25 (3125.00)|txtExcluded=-|txtLabelPaperHeight=1|txtIntRadius=10|cbIntersection=0|" +
        "txtMaxStatInterval=50|cbByLinks=1|cmbStationType=Raw Station|txtMinGrade2=2|cbVertLabel=1|" +
        "txtSlopePrecision=1|txtMinGrade1=0|cbRemoveOld=1|txtScale=200|cmbPaperSize=900*1300|cmbTitlePlace=Center|" +
        "txtAlignMove=0|cbCustomDatum=0";

    /// <summary>
    /// Where MahodCivilNet keeps a project's own section settings: an Xrecord under this key in the drawing's named
    /// objects dictionary, one <c>CreateMultipleSectionView_key=value</c> entry per field, saved by the firm's dialog
    /// on every run (6403-HW-DESING, 2026-11-02: 203 fields — the project's band sizes, station format, the
    /// neighbouring corridor surfaces it draws into each section, the utility layers it projects).
    /// </summary>
    public const string StoredSettingsKey = "CreateMultipleSectionView";

    /// <summary>The prefix the firm's dialog writes before every stored field.</summary>
    public const string StoredFieldPrefix = StoredSettingsKey + "_";

    /// <summary>
    /// The project's stored settings as the firm's <c>Cfg.FromString</c> takes them (prefix removed), or null when
    /// <paramref name="stored"/> holds none of the firm's fields. The office's sections are drawn with these, so a
    /// typical section drawn with them is the office's section; <see cref="FirmDefaults"/> (another project's setting)
    /// is only the fallback for a drawing the firm's program never ran in. Entries without a key are dropped.
    /// </summary>
    public static string? FromStored(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        var entries = new List<string>();
        foreach (var raw in stored.Split(EntrySeparator))
        {
            var entry = raw.StartsWith(StoredFieldPrefix, StringComparison.Ordinal) ? raw.Substring(StoredFieldPrefix.Length) : raw;
            var cut = entry.IndexOf(ValueSeparator);
            if (cut <= 0) continue;
            entries.Add(entry);
        }
        return entries.Count == 0 || !entries.Exists(e => e.StartsWith("cbByLinks=", StringComparison.Ordinal) ||
                                                           e.StartsWith("txtBandStep=", StringComparison.Ordinal) ||
                                                           e.StartsWith("cmbCorridor=", StringComparison.Ordinal))
            ? null
            : string.Join(EntrySeparator.ToString(), entries);
    }

    /// <summary>One field of a settings string (stored or plain); null when absent.</summary>
    public static string? Field(string? settings, string key)
    {
        if (string.IsNullOrEmpty(settings)) return null;
        foreach (var raw in settings.Split(EntrySeparator))
        {
            var entry = raw.StartsWith(StoredFieldPrefix, StringComparison.Ordinal) ? raw.Substring(StoredFieldPrefix.Length) : raw;
            if (entry.StartsWith(key + ValueSeparator, StringComparison.Ordinal)) return entry.Substring(key.Length + 1);
        }
        return null;
    }

    /// <summary>
    /// The surfaces the office's last run used, when that run was on <paramref name="corridor"/>: the firm's dialog
    /// keeps them per run, and a temporary corridor is often cut against the main road's finished surfaces
    /// (6403: RD-40-ST-01 against "RD-40 Surface - TOP-2.00" / "BOT-IN"). Nulls when the stored run was another corridor.
    /// </summary>
    public static (string? Top, string? Bottom, string? Ground) StoredSurfacesFor(string? settings, string corridor)
    {
        if (!string.Equals(Field(settings, "cmbCorridor"), corridor, StringComparison.OrdinalIgnoreCase)) return (null, null, null);
        static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
        return (NonEmpty(Field(settings, "cmbSurfTop1")), NonEmpty(Field(settings, "cmbSurfBot1")), NonEmpty(Field(settings, "cmbSurfBase")));
    }

    /// <summary>The firm tool's own limit on sections per run; beyond it ProcessCorridor shows a modal box.</summary>
    public const int MaxSections = 2000;

    /// <summary>The entry separator of the firm's settings string.</summary>
    public const char EntrySeparator = '|';

    /// <summary>The key/value separator of the firm's settings string.</summary>
    public const char ValueSeparator = '=';

    /// <summary>
    /// The overrides to apply, in this order, after <c>Cfg.FromString(FirmDefaults)</c> so that the firm's
    /// code draws exactly one section at <paramref name="station"/>.
    /// </summary>
    /// <param name="station">The raw station (the defaults set <c>cmbStationType=Raw Station</c>).</param>
    /// <param name="interval">The firm's station interval, from <see cref="IntervalFor"/>.</param>
    /// <exception cref="ArgumentException">A name holds <c>|</c> or <c>=</c>, is empty, a number is not finite,
    /// or the interval is not positive (the firm's code answers that with a modal box).</exception>
    public static IReadOnlyList<(string Key, string Value)> ForStation(
        string corridor,
        string alignment,
        string existingGround,
        string topSurface,
        string bottomSurface,
        double station,
        double leftM,
        double rightM,
        double interval)
    {
        RequireValidName(corridor, nameof(corridor));
        RequireValidName(alignment, nameof(alignment));
        RequireValidName(existingGround, nameof(existingGround));
        RequireValidName(topSurface, nameof(topSurface));
        RequireValidName(bottomSurface, nameof(bottomSurface));
        RequireFinite(station, nameof(station));
        RequireFinite(leftM, nameof(leftM));
        RequireFinite(rightM, nameof(rightM));
        RequireFinite(interval, nameof(interval));
        if (interval <= 0)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The interval must be positive.");

        var stationText = FormatNumber(station);
        return new[]
        {
            ("cmbCorridor", corridor),
            ("cmbBaseAlign", alignment),
            ("cmbSurfBase", existingGround),
            ("cmbSurfElev", existingGround),
            ("cmbSurfTop1", topSurface),
            ("cmbSurfBot1", bottomSurface),
            ("txtStationInterval", FormatNumber(interval)),
            ("txtStartStation", stationText),
            ("txtEndStation", stationText),
            ("txtLeftSide", FormatNumber(leftM)),
            ("txtRightSide", FormatNumber(rightM)),
            ("cbRemoveOld", "0"),
            ("cbSplitByPages", "0"),
        };
    }

    /// <summary>
    /// The interval that puts exactly this station in the firm's station list. The firm's code lists stations from
    /// the alignment start in steps of the interval and moves each one down to a multiple of the interval, counted on
    /// the displayed station rounded to centimetres (MahodCivilNet <c>CAlignment.createStation</c>). An interval of
    /// 1 m therefore reaches whole metres only and passes a corridor station such as 103.46 by. With the station itself
    /// as the interval its one multiple in range is the station; station zero, a multiple of anything, takes 1 m.
    /// A station rounded past the alignment's end (a corridor station at the very end, 691.806 → 691.81) steps back
    /// one centimetre: past the end the alignment has no point to cut at.
    /// </summary>
    /// <param name="displayStation">The station as displayed (<see cref="DisplayStation"/>).</param>
    /// <param name="displayEndingStation">The alignment's ending station as displayed.</param>
    public static double IntervalFor(double displayStation, double displayEndingStation)
    {
        RequireFinite(displayStation, nameof(displayStation));
        var interval = Math.Abs(Math.Round(displayStation, 2));
        if (displayStation > 0 && interval > displayEndingStation) interval = Math.Round(interval - 0.01, 2);
        return interval >= 0.01 ? interval : 1.0;
    }

    /// <summary>
    /// The displayed station the firm's code computes for a raw station: from the last station equation, in the
    /// alignment's order, whose back raw station it has reached (ahead station + the distance past it), else the raw
    /// station itself.
    /// </summary>
    public static double DisplayStation(
        double rawStation, IReadOnlyList<(double RawStationBack, double StationAhead)>? equations)
    {
        var display = rawStation;
        if (equations == null) return display;
        foreach (var (back, ahead) in equations)
            if (rawStation >= back) display = ahead + rawStation - back;
        return display;
    }

    /// <summary>A number as the firm's settings string carries it: up to three decimals, invariant culture.</summary>
    public static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>True when the name can travel in the firm's settings string without corrupting it.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOf(EntrySeparator) < 0
        && name.IndexOf(ValueSeparator) < 0;

    private static void RequireValidName(string name, string paramName)
    {
        if (name == null) throw new ArgumentNullException(paramName);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("The name is empty.", paramName);
        if (!IsValidName(name))
            throw new ArgumentException(FormattableString.Invariant(
                $"The name '{name}' contains '{EntrySeparator}' or '{ValueSeparator}', which the firm's settings string cannot carry."), paramName);
    }

    private static void RequireFinite(double value, string paramName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(paramName, value, "The value must be a finite number.");
    }
}
