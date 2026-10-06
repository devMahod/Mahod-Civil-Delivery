using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class EstimateReferenceProjectScopeTests
{
    [Theory]
    [InlineData("6422", true)]
    [InlineData("6422-copy", false)]
    [InlineData("FRESH-PROJECT", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ProjectSpecificReferenceNeverRanksUnrelatedProjects(string? project, bool expected) =>
        EstimateWorkflowService.UsesBundled6422Reference(project).Should().Be(expected);
}
