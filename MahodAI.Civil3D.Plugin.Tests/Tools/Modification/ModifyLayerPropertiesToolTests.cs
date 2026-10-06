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
    public class ModifyLayerPropertiesToolTests
    {
        [Fact]
        public void Tool_HasExpectedMetadata()
        {
            var tool = new ModifyLayerPropertiesTool();
            tool.Name.Should().Be("modify_layer_properties");
            tool.Category.Should().Be(ToolCategories.Modification);
            tool.ParameterSchema.Should().NotBeNull();
            var schemaJson = tool.ParameterSchema!.Value.GetRawText();
            schemaJson.Should().Contain("layer_name");
            schemaJson.Should().Contain("color");
            schemaJson.Should().Contain("lineweight");
        }

        [Fact]
        public void Tool_IsRegistered()
        {
            ToolRegistry.Instance.HasTool("modify_layer_properties").Should().BeTrue();
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_MissingLayerName_Throws()
        {
            var tool = new ModifyLayerPropertiesTool();
            var parameters = JsonDocument.Parse("""{}""").RootElement;
            Func<Task> act = async () => await tool.ExecuteAsync(
                null!, null!, parameters, new ToolCache(), CancellationToken.None);
            await act.Should().ThrowAsync<ArgumentException>();
        }

        [Fact(Skip = "Happy path requires AutoCAD HostApplicationServices.WorkingDatabase")]
        [Trait("Category", "RequiresCivil3D")]
        public void Happy_AppliesSuppliedFields() { }
    }
}
