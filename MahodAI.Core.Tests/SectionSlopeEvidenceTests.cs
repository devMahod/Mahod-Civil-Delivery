using System;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests
{
    public class SectionSlopeEvidenceTests
    {
        [Fact]
        public void SlopeEvidence_InterpolatesInsideDesignChain_AndKeepsDirection()
        {
            var chain = new[]
            {
                (Offset: -5.0, Elevation: 100.00),
                (Offset:  0.0, Elevation: 100.10),
                (Offset:  5.0, Elevation:  99.90),
            };

            SectionFurnitureLogic.TrySlopeEvidence(
                    chain, -2.5, 2.5, out var evidence)
                .Should().BeTrue();
            evidence.Should().NotBeNull();
            evidence!.FromElevation.Should().BeApproximately(100.05, 1e-9);
            evidence.ToElevation.Should().BeApproximately(100.00, 1e-9);
            evidence.Percent.Should().BeApproximately(-1.0, 1e-9);
            SectionFurnitureLogic.FormatSlopePercent(evidence.Percent).Should().Be("-1.00%");
        }

        [Fact]
        public void SlopeEvidence_UsesStripInteriorAtVerticalCurbFaces()
        {
            var chain = new[]
            {
                (Offset: -2.0, Elevation: 100.00),
                (Offset:  0.0, Elevation: 100.00), // outside/left of curb
                (Offset:  0.0, Elevation: 100.20), // inside strip
                (Offset:  4.0, Elevation: 100.12), // inside strip
                (Offset:  4.0, Elevation:  99.80), // outside/right of curb
                (Offset:  6.0, Elevation:  99.80),
            };

            SectionFurnitureLogic.TrySlopeEvidence(chain, 0.0, 4.0, out var evidence)
                .Should().BeTrue();
            evidence!.FromElevation.Should().Be(100.20);
            evidence.ToElevation.Should().Be(100.12);
            evidence.Percent.Should().BeApproximately(-2.0, 1e-9);
        }

        [Fact]
        public void SlopeEvidence_FailsClosedOutsideChainOrOnMalformedSpan()
        {
            var chain = new[] { (Offset: 0.0, Elevation: 100.0), (Offset: 5.0, Elevation: 100.1) };

            SectionFurnitureLogic.TrySlopeEvidence(chain, -0.1, 4.0, out _).Should().BeFalse();
            SectionFurnitureLogic.TrySlopeEvidence(chain, 1.0, 5.1, out _).Should().BeFalse();
            SectionFurnitureLogic.TrySlopeEvidence(chain, 1.0, 1.4, out _).Should().BeFalse();
            SectionFurnitureLogic.TrySlopeEvidence(chain, 4.0, 1.0, out _).Should().BeFalse();
            SectionFurnitureLogic.TrySlopeEvidence(
                new[] { (0.0, 100.0), (5.0, double.NaN) }, 0.0, 5.0, out _).Should().BeFalse();
        }

        [Fact]
        public void SlopePercentFormatting_IsInvariantSignedAndSuppressesNegativeZero()
        {
            SectionFurnitureLogic.FormatSlopePercent(2.0).Should().Be("+2.00%");
            SectionFurnitureLogic.FormatSlopePercent(-2.0).Should().Be("-2.00%");
            SectionFurnitureLogic.FormatSlopePercent(-0.004).Should().Be("0.00%");
            Action nonFinite = () => SectionFurnitureLogic.FormatSlopePercent(double.PositiveInfinity);
            nonFinite.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
