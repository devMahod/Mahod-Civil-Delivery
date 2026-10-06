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
    public class BatchFreezeLayersToolTests
    {
        [Fact]
        public void Tool_HasExpectedMetadata()
        {
            var tool = new BatchFreezeLayersTool();
            tool.Name.Should().Be("batch_freeze_layers");
            tool.Category.Should().Be(ToolCategories.Modification);
            tool.ParameterSchema.Should().NotBeNull();
        }

        [Fact]
        public void Tool_IsRegistered()
        {
            ToolRegistry.Instance.HasTool("batch_freeze_layers").Should().BeTrue();
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_EmptyLayerNames_ReturnsFail()
        {
            var tool = new BatchFreezeLayersTool();
            var parameters = JsonDocument.Parse("""{"layer_names": []}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.InvalidParameters);
        }

        [Fact(Skip = "Happy path requires AutoCAD HostApplicationServices.WorkingDatabase")]
        [Trait("Category", "RequiresCivil3D")]
        public void Happy_FreezesAllLayers() { }
    }

    public class BatchThawLayersToolTests
    {
        [Fact]
        public void Tool_HasExpectedMetadata()
        {
            var tool = new BatchThawLayersTool();
            tool.Name.Should().Be("batch_thaw_layers");
            tool.Category.Should().Be(ToolCategories.Modification);
            tool.ParameterSchema.Should().NotBeNull();
        }

        [Fact]
        public void Tool_IsRegistered()
        {
            ToolRegistry.Instance.HasTool("batch_thaw_layers").Should().BeTrue();
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_MissingLayerNames_ReturnsFail()
        {
            var tool = new BatchThawLayersTool();
            var parameters = JsonDocument.Parse("""{}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.InvalidParameters);
        }

        [Fact(Skip = "Happy path requires AutoCAD HostApplicationServices.WorkingDatabase")]
        [Trait("Category", "RequiresCivil3D")]
        public void Happy_ThawsAllLayers() { }
    }
}
