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
