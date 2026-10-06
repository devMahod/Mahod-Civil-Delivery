using System.Collections.Generic;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public class SectionSurfaceSelectionTests
    {
        private static readonly string[] Real6422Surfaces =
        {
            "MK", "MK-EAST", "2000-DESIGN", "2000-DESIGN-FINAL", "700D@Top@01",
            "700 top- (1)", "700 bot- (2)", "1000D@Top@01", "3000-DESIGN-FINAL",
            "600-DESIGN-FINAL",
        };

        [Fact]
        public void ExactExistingGroundAndAlignmentFinalAreTheOnlySelectedPair()
        {
            var selected = SectionSourceSelectionLogic.Select(
                Real6422Surfaces, "2000", new[] { "MK", "MK*" });

            selected.IsReady.Should().BeTrue();
            selected.ExistingGround.Name.Should().Be("MK",
                "the literal configured surface outranks the broader MK* wildcard");
            selected.Design.Name.Should().Be("2000-DESIGN-FINAL");
        }

        [Fact]
        public void AlignmentScopedCorridorDesignTopConventionIsSelected()
        {
            var selected = SectionSourceSelectionLogic.Select(
                Real6422Surfaces, "1000", new[] { "MK" });

            selected.IsReady.Should().BeTrue();
            selected.Design.State.Should().Be(SectionSourceSelectionLogic.ChoiceState.Selected);
            selected.Design.Name.Should().Be("1000D@Top@01");
        }

        [Fact]
        public void ExplicitCorridorDesignTopOutranksGenericTopAndBotNames()
        {
            var selected = SectionSourceSelectionLogic.Select(
                Real6422Surfaces, "700", new[] { "MK" });

            selected.Design.State.Should().Be(SectionSourceSelectionLogic.ChoiceState.Selected);
            selected.Design.Name.Should().Be("700D@Top@01");
            selected.IsReady.Should().BeTrue();
        }

        [Fact]
        public void MultipleExplicitCorridorDesignTopSurfacesAreAmbiguous()
        {
            var selected = SectionSourceSelectionLogic.Select(
                new[] { "MK", "700D@Top@01", "700D@Top@02", "700 top- (1)" },
                "700", new[] { "MK" });

            selected.Design.State.Should().Be(SectionSourceSelectionLogic.ChoiceState.Ambiguous);
            selected.Design.Name.Should().BeNull();
            selected.Design.Candidates.Should().Equal("700D@Top@01", "700D@Top@02");
            selected.IsReady.Should().BeFalse();
        }

        [Fact]
        public void GenericAlignmentScopedTopNameAloneRemainsReviewEvidence()
        {
            var selected = SectionSourceSelectionLogic.Select(
                new[] { "MK", "700 top- (1)", "700 bot- (2)" },
                "700", new[] { "MK" });

            selected.Design.State.Should().Be(SectionSourceSelectionLogic.ChoiceState.Missing);
            selected.Design.Name.Should().BeNull();
            selected.Design.Candidates.Should().Contain("700 top- (1)");
            selected.IsReady.Should().BeFalse();
        }

        [Fact]
        public void ForeignDesignIsMissingRatherThanFallback()
        {
            var selected = SectionSourceSelectionLogic.Select(
                new[] { "MK", "2000-DESIGN-FINAL" }, "600", new[] { "MK" });

            selected.Design.State.Should().Be(SectionSourceSelectionLogic.ChoiceState.Missing);
            selected.Design.Name.Should().BeNull();
            selected.IsReady.Should().BeFalse();
        }

        [Fact]
        public void NumericAlignmentPrefixDoesNotClaimALongerAxisNumber()
        {
            var selected = SectionSourceSelectionLogic.Select(
                new[] { "MK", "7000-DESIGN-FINAL" }, "700", new[] { "MK" });

            selected.Design.State.Should().Be(SectionSourceSelectionLogic.ChoiceState.Missing);
        }

        [Fact]
        public void WildcardOnlyExistingGroundMustBeUnique()
        {
            var selected = SectionSourceSelectionLogic.Select(
                new[] { "MK", "MK-EAST", "2000-DESIGN-FINAL" }, "2000", new[] { "MK*" });

            selected.ExistingGround.State.Should().Be(SectionSourceSelectionLogic.ChoiceState.Ambiguous);
            selected.IsReady.Should().BeFalse();
        }
    }
}
