using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class RecoveredCurveFindingImpactTests
{
    [Theory]
    [InlineData("A/10/ABC", false)]
    [InlineData("A/10/123", true)]
    [InlineData("A/10", true)]
    public void RecoveredCurvedArea_IsIndependentOfAnotherObjectFailure_ButNotItsOwnOrParent(string failedHandle, bool blocks)
    {
        var record = Record();
        var finding = new DeliveryFinding
        {
            Code = EstimateFindingCodes.MeasurementFailed, Domain = "estimate",
            Severity = FindingSeverity.Error, Title = "TEST ONLY source failure",
            SourceRefs = { new ProvenanceRef
            {
                SourceKind = "xref", SourcePathOrUri = record.Source.DrawingPath,
                DrawingChecksum = record.Source.DrawingHash, SourceHandle = failedHandle,
                XrefPath = "HA", EntityType = "HATCH", MeasurementMethod = "hatch-area",
            } },
        };
        EstimateFindingImpactPolicy.BlocksRecord(finding, record).Should().Be(blocks);
        EstimateFindingImpactPolicy.CreateIndex(new[] { finding }).BlocksRecord(record).Should().Be(blocks);
        // The original finding remains blocking project completeness in all cases.
        EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();
    }

    [Fact]
    public void RecoveredArea_DoesNotBypassAnUnapprovedSourcePolicy()
    {
        var finding = new DeliveryFinding
        {
            Code = EstimateFindingCodes.SourceScopePolicyUnapproved, Domain = "estimate",
            Severity = FindingSeverity.Error, Title = "source approval missing",
        };
        EstimateFindingImpactPolicy.BlocksRecord(finding, Record()).Should().BeTrue();
    }

    private static NeutralQuantityRecord Record() => new()
    {
        RecordId = "TEST-RECOVERED-CURVE", RunId = "TEST-SCAN", ProjectProfileId = "TEST-ONLY",
        Source = new QuantitySource
        {
            Drawing = "HA.dwg", DrawingPath = @"C:\TEST-ONLY\HA.dwg", DrawingHash = new string('a', 64),
            Handle = "A/10/123", EntityType = "HATCH", Xref = "HA", Layer = "TEST-LAYER",
        },
        Measurement = new QuantityMeasurement
        {
            Kind = "area", Method = "hatch-line-arc-boundary-area+xref-transform", RawValue = 40, Unit = "m2",
        },
    };
}
