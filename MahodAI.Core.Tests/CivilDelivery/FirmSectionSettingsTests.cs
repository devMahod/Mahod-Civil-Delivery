using System.Globalization;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.CivilDelivery;

public sealed class FirmSectionSettingsTests
{
    private static Dictionary<string, string> Parse(string settings) =>
        settings.Split('|').Select(entry => entry.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

    private static IReadOnlyList<(string Key, string Value)> Overrides(
        string corridor = "T_Aznar1", string alignment = "Aznar", string ground = "EG",
        string top = "T_Aznar1 Top", string bottom = "T_Aznar1 Bot",
        double station = 420, double left = 30, double right = 25, double interval = 420) =>
        FirmSectionSettings.ForStation(corridor, alignment, ground, top, bottom, station, left, right, interval);

    /// <summary>
    /// The firm's station list, step for step as MahodCivilNet builds it (<c>CAlignment.GetStations</c> with both
    /// intervals equal, then <c>createStation</c>), and the station filter of <c>ProcessCorridor</c>.
    /// </summary>
    private static List<double> FirmStationsInRange(
        double referenceStation, double endingStation, double interval, double start, double end,
        IReadOnlyList<(double Back, double Ahead)>? equations = null)
    {
        var steps = new List<(double Raw, double Station)> { (referenceStation, referenceStation) };
        foreach (var (back, ahead) in equations ?? Array.Empty<(double, double)>())
        {
            if (steps[^1].Raw == back) steps.RemoveAt(steps.Count - 1);
            steps.Add((back, ahead));
        }
        var raws = new List<double>();
        for (var at = steps[0].Raw; at < endingStation + interval; at += interval)
        {
            double? raw = null;
            foreach (var step in steps)
            {
                if (at < step.Raw) continue;
                var shown = Math.Round(step.Station + at - step.Raw, 2);
                var rest = shown - (int)(shown / interval) * interval;
                raw = at - rest;
            }
            if (raw != null) raws.Add(raw.Value);
        }
        return raws.Where(raw => raw + 0.01 >= start && raw - 0.01 <= end).ToList();
    }

    [Fact]
    public void DefaultsCarryTheKeysTheFirmWrapperReliesOn()
    {
        var defaults = Parse(FirmSectionSettings.FirmDefaults);

        defaults["txtScale"].Should().Be("200");
        defaults["cmbPaperSize"].Should().Be("900*1300");
        defaults["cbByLinks"].Should().Be("1");
        defaults["txtBandStep"].Should().Be("2");
        defaults["cmbStationType"].Should().Be("Raw Station");
        defaults["cmbStationFormat"].Should().Be("31+25 (3125.00)");
        defaults["txtExcluded"].Should().Be("-");
        defaults["cbRemoveOld"].Should().Be("1", "the whole-corridor defaults delete the old sections; ForStation turns that off");
        defaults["cbSplitByPages"].Should().Be("1");
        defaults.Keys.Should().NotContain(new[]
        {
            "cmbCorridor", "cmbBaseAlign", "cmbSurfBase", "cmbSurfElev", "cmbSurfTop1", "cmbSurfBot1",
            "txtStationInterval", "txtStartStation", "txtEndStation", "txtLeftSide", "txtRightSide",
        }, "the run-specific keys are set per station, never baked into the defaults");
        FirmSectionSettings.FirmDefaults.Split('|').Should().OnlyContain(entry =>
            entry.Count(c => c == '=') == 1 && !entry.StartsWith("="), "the firm's string has no escaping");
        FirmSectionSettings.MaxSections.Should().Be(2000);
    }

    [Fact]
    public void ForStationOverridesEveryRunSpecificKeyInTheWrapperOrder()
    {
        var overrides = Overrides();

        overrides.Should().Equal(
            ("cmbCorridor", "T_Aznar1"),
            ("cmbBaseAlign", "Aznar"),
            ("cmbSurfBase", "EG"),
            ("cmbSurfElev", "EG"),
            ("cmbSurfTop1", "T_Aznar1 Top"),
            ("cmbSurfBot1", "T_Aznar1 Bot"),
            ("txtStationInterval", "420"),
            ("txtStartStation", "420"),
            ("txtEndStation", "420"),
            ("txtLeftSide", "30"),
            ("txtRightSide", "25"),
            ("cbRemoveOld", "0"),
            ("cbSplitByPages", "0"));
    }

    [Fact]
    public void NumbersAreInvariantWhateverTheMachineCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            FirmSectionSettings.FormatNumber(420).Should().Be("420");
            FirmSectionSettings.FormatNumber(1234.5).Should().Be("1234.5");
            FirmSectionSettings.FormatNumber(1234.5678).Should().Be("1234.568");

            var overrides = Overrides(station: 1234.5, left: 12.25, right: 7.5);
            overrides.Should().Contain(("txtStartStation", "1234.5"));
            overrides.Should().Contain(("txtEndStation", "1234.5"));
            overrides.Should().Contain(("txtLeftSide", "12.25"));
            overrides.Should().Contain(("txtRightSide", "7.5"));
            string.Join("|", overrides.Select(o => $"{o.Key}={o.Value}")).Should().NotContain(",");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("A|B")]
    [InlineData("A=B")]
    [InlineData("|")]
    [InlineData("=")]
    public void ANameWithASeparatorIsRefusedInEveryPosition(string bad)
    {
        FirmSectionSettings.IsValidName(bad).Should().BeFalse();

        ((Action)(() => Overrides(corridor: bad))).Should().Throw<ArgumentException>().WithParameterName("corridor");
        ((Action)(() => Overrides(alignment: bad))).Should().Throw<ArgumentException>().WithParameterName("alignment");
        ((Action)(() => Overrides(ground: bad))).Should().Throw<ArgumentException>().WithParameterName("existingGround");
        ((Action)(() => Overrides(top: bad))).Should().Throw<ArgumentException>().WithParameterName("topSurface");
        ((Action)(() => Overrides(bottom: bad))).Should().Throw<ArgumentException>().WithParameterName("bottomSurface");
    }

    [Fact]
    public void AnEmptyNameOrANonFiniteNumberIsRefused()
    {
        FirmSectionSettings.IsValidName("").Should().BeFalse();
        FirmSectionSettings.IsValidName(null).Should().BeFalse();
        FirmSectionSettings.IsValidName("T_Aznar1 Top").Should().BeTrue();

        ((Action)(() => Overrides(top: " "))).Should().Throw<ArgumentException>();
        ((Action)(() => Overrides(station: double.NaN))).Should().Throw<ArgumentException>().WithParameterName("station");
        ((Action)(() => Overrides(left: double.PositiveInfinity))).Should().Throw<ArgumentException>().WithParameterName("leftM");
        ((Action)(() => Overrides(interval: 0))).Should().Throw<ArgumentException>().WithParameterName("interval");
        ((Action)(() => Overrides(interval: -1))).Should().Throw<ArgumentException>().WithParameterName("interval");
    }

    [Theory]
    [InlineData(103.4567, 103.46)]
    [InlineData(420, 420)]
    [InlineData(-37.5, 37.5)]
    [InlineData(0, 1)]
    [InlineData(0.004, 1)]
    [InlineData(691.806, 691.8)]
    public void IntervalFor_IsTheStationItselfInCentimetres_NeverPastTheEnd_AndOneMetreAtZero(double station, double expected)
    {
        FirmSectionSettings.IntervalFor(station, displayEndingStation: 691.806).Should().Be(expected);
    }

    [Fact]
    public void DisplayStation_FollowsTheLastEquationReached()
    {
        var equations = new[] { (300.0, 1000.0), (500.0, 2000.0) };
        FirmSectionSettings.DisplayStation(120, equations).Should().Be(120);
        FirmSectionSettings.DisplayStation(412.37, equations).Should().BeApproximately(1112.37, 1e-9);
        FirmSectionSettings.DisplayStation(650, equations).Should().BeApproximately(2150, 1e-9);
        FirmSectionSettings.DisplayStation(77, null).Should().Be(77);
    }

    [Theory]
    [InlineData(0.0, 691.806, 100.0)]
    [InlineData(0.0, 691.806, 103.4567)]
    [InlineData(0.0, 691.806, 250.005)]
    [InlineData(0.0, 691.806, 691.806)]
    [InlineData(0.0, 691.806, 0.0)]
    [InlineData(50.0, 900.0, 60.5)]
    [InlineData(1250.0, 4800.0, 3127.41)]
    public void TheFirmsStationListHoldsExactlyThePickedStation(double reference, double ending, double station)
    {
        var start = double.Parse(FirmSectionSettings.FormatNumber(station), CultureInfo.InvariantCulture);
        var interval = FirmSectionSettings.IntervalFor(FirmSectionSettings.DisplayStation(station, null), ending);

        var hits = FirmStationsInRange(reference, ending, interval, start, start);

        hits.Should().ContainSingle("one call draws one section");
        hits[0].Should().BeApproximately(station, 0.0099, "the firm matches the corridor's assembly within 1 cm");
        hits[0].Should().BeLessThanOrEqualTo(ending, "past the end the alignment has no point to cut at");
    }

    [Fact]
    public void TheFirmsStationListHoldsAPickPastAStationEquation()
    {
        var equations = new[] { (300.0, 1000.0) };
        const double station = 412.37;
        var interval = FirmSectionSettings.IntervalFor(
            FirmSectionSettings.DisplayStation(station, equations), FirmSectionSettings.DisplayStation(691.806, equations));

        var hits = FirmStationsInRange(0, 691.806, interval, station, station, equations);

        hits.Should().ContainSingle();
        hits[0].Should().BeApproximately(station, 0.0099);
    }

    [Fact]
    public void OneMetreSteps_ListWholeMetresOnly()
    {
        FirmStationsInRange(0, 691.806, 1, 103.457, 103.457).Should().BeEmpty("a corridor station between whole metres is never listed");
        FirmStationsInRange(0, 691.806, 1, 100, 100).Should().Equal(100.0);
    }

    // The office's own settings as the firm's dialog saved them in 6403-HW-DESING (2026-11-02), shortened.
    private const string Stored6403 =
        "CreateMultipleSectionView_txtAlignMove=0|CreateMultipleSectionView_cmbCorridor=RD-40-ST-01|" +
        "CreateMultipleSectionView_cmbBaseAlign=40-st-01-1|CreateMultipleSectionView_cbSAByLayers=1|" +
        "CreateMultipleSectionView_txtBandHeight=1|CreateMultipleSectionView_txtBandStep=2|" +
        "CreateMultipleSectionView_lvSurf_RD-40+Top=1|CreateMultipleSectionView_cmbSurfBase=MK-MISAA|" +
        "CreateMultipleSectionView_cmbSurfElev=|CreateMultipleSectionView_cmbSurfTop1=RD-40 Surface - TOP-2.00|" +
        "CreateMultipleSectionView_cmbSurfBot1=RD-40 Surface - BOT-IN|CreateMultipleSectionView_cbByLinks=1||junk";

    [Fact]
    public void StoredSettings_AreTheFirmsFieldsWithoutTheDialogPrefix()
    {
        var settings = FirmSectionSettings.FromStored(Stored6403);

        settings.Should().NotBeNull();
        var fields = Parse(settings!);
        fields["cbSAByLayers"].Should().Be("1");
        fields["lvSurf_RD-40+Top"].Should().Be("1");
        fields["cmbSurfElev"].Should().Be("", "an empty field is still the office's choice");
        fields.Keys.Should().NotContain(k => k.StartsWith(FirmSectionSettings.StoredFieldPrefix, StringComparison.Ordinal));
        fields.Keys.Should().NotContain("junk");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CalcSectionVolumes3_cmbCorridor=RD-40")]
    public void NoFirmFields_IsNoStoredSetting(string? stored) =>
        FirmSectionSettings.FromStored(stored?.Replace("CalcSectionVolumes3_cmbCorridor", "other")).Should().BeNull();

    [Fact]
    public void StoredSurfaces_ApplyOnlyToTheCorridorTheOfficeLastRan()
    {
        var settings = FirmSectionSettings.FromStored(Stored6403);

        FirmSectionSettings.StoredSurfacesFor(settings, "rd-40-st-01")
            .Should().Be(("RD-40 Surface - TOP-2.00", "RD-40 Surface - BOT-IN", "MK-MISAA"));
        FirmSectionSettings.StoredSurfacesFor(settings, "RD-3933-ZMANI").Should().Be(((string?)null, (string?)null, (string?)null));
        FirmSectionSettings.Field(Stored6403, "cmbSurfBase").Should().Be("MK-MISAA", "the prefixed form reads too");
    }
}
