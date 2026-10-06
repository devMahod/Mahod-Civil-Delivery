using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests
{
    public class SectionSurfaceStyleContractLogicTests
    {
        [Theory]
        [InlineData(true, 1, "ByAci", "Continuous", true)]
        [InlineData(false, 1, "ByAci", "Continuous", false)]
        [InlineData(true, 2, "ByAci", "Continuous", false)]
        [InlineData(true, 1, "ByColor", "Continuous", false)]
        [InlineData(true, 1, "ByAci", "MHD-DASHED2", false)]
        public void DesignContract_IsExact(
            bool ownedDescription, short color, string method, string linetype, bool expected)
        {
            var spec = SectionSurfaceStyleContractLogic.Design;
            SectionSurfaceStyleContractLogic.TryValidateLive(
                    spec,
                    spec.Name,
                    ownedDescription ? spec.Description : "foreign",
                    color,
                    method,
                    true,
                    linetype,
                    out _)
                .Should().Be(expected);
        }

        [Fact]
        public void ExistingAndDesign_HaveDistinctOwnedContracts()
        {
            SectionSurfaceStyleContractLogic.Existing.Name.Should().Be("MHD-EXISTING-V3");
            SectionSurfaceStyleContractLogic.Design.Name.Should().Be("MHD-DESIGN-V3");
            SectionSurfaceStyleContractLogic.Existing.Name.Should()
                .NotBe(SectionSurfaceStyleContractLogic.Design.Name);
            SectionSurfaceStyleContractLogic.Existing.Description.Should()
                .NotBe(SectionSurfaceStyleContractLogic.Design.Description);
            SectionSurfaceStyleContractLogic.Existing.Linetype.Should().Be("MHD-DASHED2");
            SectionSurfaceStyleContractLogic.Design.Linetype.Should().Be("Continuous");
        }
    }
}
