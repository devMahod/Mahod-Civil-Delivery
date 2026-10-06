using System;
using System.Globalization;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionVerificationRecoveryPolicy;

namespace MahodAI.Core.Tests.CivilDelivery;

public sealed class SectionVerificationDiagnosticTests
{
    [Fact]
    public void CoverageRejectionReportsBothIntervalsCountsAndSignedDeltas()
    {
        var error = SurfaceMismatch(
            new[] { new Sample(-2, 10), new Sample(0, 10), new Sample(4, 10) },
            new[] { new Sample(-3, 10), new Sample(3, 10) });
        Assert.Contains("source count=3 interval=[-2,4]", error);
        Assert.Contains("section count=2 interval=[-3,3]", error);
        Assert.Contains("delta(source-section)=[1,1]", error);
        Assert.Contains("offset-tolerance=0.0005", error);
    }

    [Theory]
    [InlineData("equal", SurfaceMismatchKind.None)]
    [InlineData("interval", SurfaceMismatchKind.CutIntervalMismatch)]
    [InlineData("elevation", SurfaceMismatchKind.ElevationMismatch)]
    [InlineData("missing-source", SurfaceMismatchKind.IncompleteOrAmbiguousChain)]
    [InlineData("ambiguous-source", SurfaceMismatchKind.IncompleteOrAmbiguousChain)]
    [InlineData("missing-section", SurfaceMismatchKind.IncompleteOrAmbiguousChain)]
    [InlineData("invalid-tolerance", SurfaceMismatchKind.InvalidTolerance)]
    public void TypedComparisonSeparatesValidGeometryFromUnprovenChains(string scenario, SurfaceMismatchKind expected)
    {
        var source = scenario == "missing-source" ? Array.Empty<Sample>() :
            scenario == "ambiguous-source" ? new[] { new Sample(0, 10), new Sample(0, 11) } :
            new[] { new Sample(0, scenario == "elevation" ? 11 : 10), new Sample(2, 10) };
        var section = scenario == "missing-section" ? Array.Empty<Sample>() :
            new[] { new Sample(scenario == "interval" ? -1 : 0, 10), new Sample(2, 10) };
        var tolerance = scenario == "invalid-tolerance" ? double.NaN : 0.005;
        var error = SurfaceMismatch(source, section, out var kind, tolerance);
        Assert.Equal(expected, kind);
        Assert.Equal(expected == SurfaceMismatchKind.None, error == null);
        Assert.Equal(SurfaceMismatch(source, section, tolerance), error);
    }

    [Fact]
    public void MissingChainReportsUnavailableInsteadOfInventedZeroCoverage()
    {
        var error = SurfaceMismatch(Array.Empty<Sample>(), new[] { new Sample(-3, 10), new Sample(3, 10) });
        Assert.Contains("source count=0 interval=unavailable", error);
        Assert.Contains("source-valid=False; section-valid=True", error);
    }

    [Fact]
    public void ElevationRejectionIncludesMeasuredDeltaAndOriginalTolerance()
    {
        var error = SurfaceMismatch(new[] { new Sample(-3, 11), new Sample(3, 10) },
            new[] { new Sample(-3, 10), new Sample(3, 10) });
        Assert.Contains("offset -3: source=11, section=10, delta(source-section)=1", error);
        Assert.Contains("elevation-tolerance=0.005", error);
    }

    [Theory]
    [InlineData(0.0005, 0.005, true)]
    [InlineData(0.0005001, 0.005, false)]
    [InlineData(0.0005, 0.0050001, false)]
    public void ExistingCoverageAndElevationBoundarySemanticsRemainExact(
        double offsetDelta, double elevationDelta, bool accepted)
    {
        var error = SurfaceMismatch(new[] { new Sample(0, elevationDelta), new Sample(2, elevationDelta) },
            new[] { new Sample(offsetDelta, 0), new Sample(2, 0) });
        Assert.Equal(accepted, error == null);
    }

    [Fact]
    public void StoredLiveSlopeDiagnosticHasOffsetsElevationsPercentDeltasAndTolerances()
    {
        var stored = new SectionAnnotationContractLogic.SlopeAnnotationEvidence(-2, 2, 100, 101, 25, "ABC");
        var live = new SectionFurnitureLogic.SlopeEvidence(-2, 2, 100.25, 101.5, 31.25);
        var error = DescribeMeasuredSlopeFailure(stored, live, Array.Empty<(double, double)>(), "");
        Assert.Contains("handle=ABC; stored offsets=[-2,2], elevations=[100,101], percent=25", error);
        Assert.Contains("live offsets=[-2,2], elevations=[100.25,101.5], percent=31.25", error);
        Assert.Contains("delta(stored-live) offsets=[0,0], elevations=[-0.25,-0.5], percent=-6.25", error);
        Assert.Contains("elevation-tolerance=0.000005; percent-tolerance=0.000005 percentage-points", error);
    }

    [Fact]
    public void SlopeParseFailureRetainsExactParserCauseAndDoesNotClaimLiveSampling()
    {
        Assert.False(SectionAnnotationContractLogic.TryParseSlopeReference("malformed", out var stored, out var cause));
        var error = DescribeMeasuredSlopeFailure(stored, null, Array.Empty<(double, double)>(), cause);
        Assert.Contains("stored=unavailable; live=not evaluated; parse-error=" + cause, error);
    }

    [Fact]
    public void UnavailableSlopeReportsRequestedOffsetsActualDomainAndSamplingCause()
    {
        var stored = new SectionAnnotationContractLogic.SlopeAnnotationEvidence(-2, 2, 100, 101, 25, "ABC");
        var design = new[] { (Offset: -1.0, Elevation: 100.0), (Offset: 2.0, Elevation: 101.0) };
        Assert.False(SectionFurnitureLogic.TrySlopeEvidence(design, stored.FromOffset, stored.ToOffset, out var live));
        var error = DescribeMeasuredSlopeFailure(stored, live, design, "");
        Assert.Contains("stored offsets=[-2,2]", error);
        Assert.Contains("live=unavailable: requested endpoint outside design interval", error);
        Assert.Contains("design count=2 interval=[-1,2]", error);
    }

    [Fact]
    public void DiagnosticNumbersRemainInvariantUnderCommaDecimalCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var error = SurfaceMismatch(new[] { new Sample(-2.25, 10), new Sample(3.5, 10) },
                new[] { new Sample(-3, 10), new Sample(3, 10) });
            Assert.Contains("interval=[-2.25,3.5]", error);
            Assert.Contains("delta(source-section)=[0.75,0.5]", error);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
