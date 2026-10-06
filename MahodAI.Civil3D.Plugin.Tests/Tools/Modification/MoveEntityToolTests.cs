using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools;
using MahodAI.Civil3D.Plugin.Tools.Modification;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Tools.Modification
{
    public class MoveEntityToolTests
    {
        [Fact]
        public void Tool_HasExpectedMetadata()
        {
            var tool = new MoveEntityTool();
            tool.Name.Should().Be("move_entity");
            tool.Category.Should().Be(ToolCategories.Modification);
            tool.ParameterSchema.Should().NotBeNull();
        }

        [Fact]
        public void Tool_IsRegistered()
        {
            ToolRegistry.Instance.HasTool("move_entity").Should().BeTrue();
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_MissingIdentifier_ReturnsFail()
        {
            var tool = new MoveEntityTool();
            var parameters = JsonDocument.Parse("""{"delta_x": 1, "delta_y": 2}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.InvalidParameters);
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_MissingDeltas_ReturnsFail()
        {
            var tool = new MoveEntityTool();
            var parameters = JsonDocument.Parse("""{"entity_handle": "A0"}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.InvalidParameters);
        }

        [Fact(Skip = "Happy path requires AutoCAD HostApplicationServices.WorkingDatabase")]
        [Trait("Category", "RequiresCivil3D")]
        public void Happy_MovesEntity_ByDelta() { }
    }
}
