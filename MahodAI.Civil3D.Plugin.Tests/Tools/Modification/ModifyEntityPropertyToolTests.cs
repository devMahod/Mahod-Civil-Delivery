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
    public class ModifyEntityPropertyToolTests
    {
        [Fact]
        public void Tool_HasExpectedMetadata()
        {
            var tool = new ModifyEntityPropertyTool();
            tool.Name.Should().Be("modify_entity_property");
            tool.Category.Should().Be(ToolCategories.Modification);
            tool.ParameterSchema.Should().NotBeNull();
        }

        [Fact]
        public void Tool_IsRegistered()
        {
            ToolRegistry.Instance.HasTool("modify_entity_property").Should().BeTrue();
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_RejectsHandleProperty_Even_WithValue()
        {
            var tool = new ModifyEntityPropertyTool();
            var parameters = JsonDocument.Parse("""
                {"entity_handle": "A0", "property_name": "Handle", "value": "DEADBEEF"}
            """).RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.NotSupported);
            result.Error.Message.Should().Contain("Handle");
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_RejectsUnwhitelistedProperty()
        {
            var tool = new ModifyEntityPropertyTool();
            var parameters = JsonDocument.Parse("""
                {"entity_handle": "A0", "property_name": "SomeRandomProp", "value": 1}
            """).RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.NotSupported);
            result.Error.Message.Should().Contain("Layer");
            result.Error.Message.Should().Contain("ColorIndex");
        }

        [Fact(Skip = "Happy path requires AutoCAD HostApplicationServices.WorkingDatabase")]
        [Trait("Category", "RequiresCivil3D")]
        public void Happy_SetsWhitelistedProperty() { }
    }
}
