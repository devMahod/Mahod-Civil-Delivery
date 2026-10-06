using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools;
using MahodAI.Civil3D.Plugin.Tools.View;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Tools.View
{
    public class ZoomToObjectToolTests
    {
        [Fact]
        public void Tool_HasExpectedMetadata()
        {
            var tool = new ZoomToObjectTool();
            tool.Name.Should().Be("zoom_to_object");
            tool.Category.Should().Be(ToolCategories.Utility);
            tool.ParameterSchema.Should().NotBeNull();
            var schema = tool.ParameterSchema!.Value.GetRawText();
            schema.Should().Contain("alignment");
            schema.Should().Contain("pipe_network");
        }

        [Fact]
        public void Tool_IsRegistered()
        {
            ToolRegistry.Instance.HasTool("zoom_to_object").Should().BeTrue();
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_MissingIdentifiers_ReturnsFail()
        {
            var tool = new ZoomToObjectTool();
            var parameters = JsonDocument.Parse("""{}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.InvalidParameters);
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_ObjectNameWithoutType_ReturnsFail()
        {
            var tool = new ZoomToObjectTool();
            var parameters = JsonDocument.Parse("""{"object_name": "Align-A"}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.InvalidParameters);
        }

        [Fact(Skip = "Happy path requires AutoCAD HostApplicationServices.WorkingDatabase")]
        [Trait("Category", "RequiresCivil3D")]
        public void Happy_ZoomsToExtents() { }
    }
}
