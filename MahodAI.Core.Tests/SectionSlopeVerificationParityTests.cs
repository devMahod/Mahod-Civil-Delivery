using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Evidence = MahodAI.CivilDelivery.Shared.SectionAnnotationContractLogic.SlopeAnnotationEvidence;

namespace MahodAI.Core.Tests;

public sealed class SectionSlopeVerificationParityTests
{
    private static (double Offset, double Elevation)[] Curb(double shift = 0) =>
    [(-2 + shift, 100), (shift, 100), (shift, 100.2),
        (4 + shift, 100.12), (4 + shift, 99.8), (6 + shift, 99.8)];

    private static Evidence ApplyAndPersist(
        IReadOnlyList<(double Offset, double Elevation)> design, double from, double to)
    {
        Assert.True(SectionFurnitureLogic.TrySlopeEvidence(design, from, to, out var applied));
        var stored = SectionAnnotationContractLogic.FormatSlopeReference(applied!, "ABC");
        Assert.True(SectionAnnotationContractLogic.TryParseSlopeReference(stored, out var parsed, out var error), error);
        return parsed!;
    }

    [Fact]
    public void VerticalCurb_ReproducesOldVerifierMismatch_AndUsesApplyInterior()
    {
        var chain = Curb();
        var stored = ApplyAndPersist(chain, 0, 4);
        var oldFrom = CorridorQuantityLogic.ElevationAt(chain, 0)!.Value;
        var oldTo = CorridorQuantityLogic.ElevationAt(chain, 4)!.Value;
        Assert.Equal(3, (oldTo - oldFrom) * 100 / 4, 8);
        Assert.Equal(-2, stored.Percent, 8);
        Assert.True(SectionAnnotationContractLogic.TryVerifySlopeEvidence(chain, 0, 4, stored, out var live));
        Assert.Equal(100.2, live!.FromElevation);
        Assert.Equal(100.12, live.ToElevation);
    }

    [Theory]
    [InlineData(0.0000004)]
    [InlineData(-0.0000004)]
    public void PersistedOffsetRoundingCannotSampleOppositeSideOfCurb(double shift)
    {
        var chain = Curb(shift);
        var stored = ApplyAndPersist(chain, shift, 4 + shift);
        Assert.Equal(0, stored.FromOffset);
        Assert.True(SectionAnnotationContractLogic.TryVerifySlopeEvidence(chain,
            shift, 4 + shift, stored, out var live));
        Assert.Equal(shift, live!.FromOffset);
        Assert.Equal(-2, live.Percent, 8);
    }

    [Fact]
    public void SlopingAndFlatInterpolatedSpansRoundTripThroughActualEvidenceFormat()
    {
        (double Offset, double Elevation)[] chain = [(-5, 100), (0, 100.1), (5, 99.9)];
        foreach (var span in new[] { (-2.5, 2.5), (-4.1234564, -1.2345674), (1.1, 4.4) })
        {
            var stored = ApplyAndPersist(chain, span.Item1, span.Item2);
            Assert.True(SectionAnnotationContractLogic.TryVerifySlopeEvidence(chain,
                span.Item1, span.Item2, stored, out _));
        }
        chain = [(0, 100), (5, 100)];
        Assert.True(SectionAnnotationContractLogic.TryVerifySlopeEvidence(chain,
            0, 5, ApplyAndPersist(chain, 0, 5), out var flat));
        Assert.Equal(0, flat!.Percent);
    }

    [Fact]
    public void DescendingNativeChainNormalizesBeforeApplyAndVerify()
    {
        var ascending = Curb();
        var descending = ascending.Reverse().Select((p, i) =>
            new SectionSurfaceTopologyLogic.Point(p.Offset, p.Elevation, 0,
                i == ascending.Length - 1 ? -1 : i + 1)).ToArray();
        var ordered = SectionSurfaceTopologyLogic.RequireSingleChain(descending);
        var normalized = SectionFurnitureLogic.NormalizeSectionPoints(
            ordered.Select(p => (p.X, p.Y, p.Z)).ToList(), -2, 6, 90, 110);
        var stored = ApplyAndPersist(normalized, 0, 4);
        Assert.True(SectionAnnotationContractLogic.TryVerifySlopeEvidence(normalized, 0, 4, stored, out _));
        Assert.Equal(-2, stored.Percent, 8);
    }

    [Fact]
    public void ChangedGeometryAndTamperedEvidenceStillFailOriginalTolerances()
    {
        var chain = Curb();
        var stored = ApplyAndPersist(chain, 0, 4);
        var changed = Curb();
        changed[2] = (0, 100.21);
        Assert.False(SectionAnnotationContractLogic.TryVerifySlopeEvidence(changed, 0, 4, stored, out _));
        foreach (var invalid in new[]
        {
            stored with { FromElevation = stored.FromElevation + 0.00001 },
            stored with { ToElevation = stored.ToElevation + 0.00001 },
            stored with { Percent = stored.Percent + 0.00001 },
            stored with { FromOffset = 0.000001 },
            stored with { ToOffset = 4.000001 },
            stored with { FromElevation = double.NaN },
            stored with { ToElevation = double.PositiveInfinity },
            stored with { Percent = double.NaN },
            stored with { FromOffset = double.NaN },
        })
            Assert.False(SectionAnnotationContractLogic.TryVerifySlopeEvidence(chain, 0, 4, invalid, out _));
    }

    [Fact]
    public void MinimumSpanSurvivesF6AndBinarySubtractionButShortPlanCannotVerify()
    {
        (double Offset, double Elevation)[] chain = [(0, 100), (1, 100)];
        const double from = 0.2;
        const double to = 0.7000001;
        var stored = ApplyAndPersist(chain, from, to);
        Assert.True(stored.ToOffset - stored.FromOffset < 0.5); // binary 0.7 - 0.2
        Assert.True(SectionAnnotationContractLogic.TryVerifySlopeEvidence(chain, from, to, stored, out _));
        Assert.False(SectionAnnotationContractLogic.TryVerifySlopeEvidence(chain, from, 0.6999999, stored, out _));
        Assert.False(SectionAnnotationContractLogic.TryParseSlopeReference(
            "design-surface-grade|from=0.200000|to=0.699990|z_from=100.000000|z_to=100.000000|percent=0.000000|handle=ABC",
            out _, out _));
    }

    [Fact]
    public void UnavailableMalformedOrUnprovenLiveGeometryCannotVerify()
    {
        var chain = Curb();
        var stored = ApplyAndPersist(chain, 0, 4);
        Assert.False(SectionAnnotationContractLogic.TryVerifySlopeEvidence(null, 0, 4, stored, out _));
        Assert.False(SectionAnnotationContractLogic.TryVerifySlopeEvidence(chain, 0, 4, null, out _));
        Assert.False(SectionAnnotationContractLogic.TryVerifySlopeEvidence(chain, double.NaN, 4, stored, out _));
        Assert.False(SectionAnnotationContractLogic.TryVerifySlopeEvidence([], 0, 4, stored, out _));
        Assert.False(SectionAnnotationContractLogic.TryVerifySlopeEvidence(chain, 4, 0,
            stored with { FromOffset = 4, ToOffset = 0 }, out _));
        Assert.False(SectionAnnotationContractLogic.TryVerifySlopeEvidence(chain, 0, 0.4,
            stored with { ToOffset = 0.4 }, out _));
        Assert.False(SectionAnnotationContractLogic.TryVerifySlopeEvidence(chain, -3, 4,
            stored with { FromOffset = -3 }, out _));
        chain[2] = (0, double.NaN);
        Assert.False(SectionAnnotationContractLogic.TryVerifySlopeEvidence(chain, 0, 4, stored, out _));
    }
}
