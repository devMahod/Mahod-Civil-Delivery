using System;
using System.Text.Json;
using System.Threading;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools;
using MahodAI.Civil3D.Plugin.Tools.SignsMarkings;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Tools.SignsMarkings
{
    public class GetSignRequirementsToolTests
    {
        [Fact]
        public void Tool_IsConstructable_AndHasMetadata()
        {
            var tool = new GetSignRequirementsTool();
            tool.Name.Should().Be("get_sign_requirements");
            tool.Description.Should().NotBeNullOrWhiteSpace();
            tool.Category.Should().Be("SignsMarkings");
        }

        [Fact]
        public void Tool_IsRegisteredInRegistry()
        {
            ToolRegistry.Instance.HasTool("get_sign_requirements").Should().BeTrue();
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async System.Threading.Tasks.Task Execute_FailsClosed_RulesetUnavailable_NotEmptyPass()
        {
            // P0-05: no sourced ruleset is wired, so the tool must FAIL cleanly rather than
            // return an empty-but-Ok payload that reads as "no requirements for this sign".
            var tool = new GetSignRequirementsTool();
            var parameters = JsonDocument.Parse("""{"sign_code": "401"}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Outcome.Should().Be(ToolOutcome.Failed);
            result.MayCommitProductionObject.Should().BeFalse();
            result.Error!.Code.Should().Be("STANDARD_RULESET_UNAVAILABLE");
            result.Error.Message.Should().Contain("401");
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async System.Threading.Tasks.Task Execute_WithoutSignCode_StillFailsClosed()
        {
            var tool = new GetSignRequirementsTool();
            var parameters = JsonDocument.Parse("""{}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be("STANDARD_RULESET_UNAVAILABLE");
        }
    }
}
