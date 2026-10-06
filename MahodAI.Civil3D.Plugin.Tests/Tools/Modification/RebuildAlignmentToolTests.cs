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
    public class RebuildAlignmentToolTests
    {
        [Fact]
        public void Tool_HasExpectedMetadata()
        {
            var tool = new RebuildAlignmentTool();
            tool.Name.Should().Be("rebuild_alignment");
            tool.Category.Should().Be(ToolCategories.Modification);
            tool.ParameterSchema.Should().NotBeNull();
        }

        [Fact]
        public void Tool_IsRegistered()
        {
            ToolRegistry.Instance.HasTool("rebuild_alignment").Should().BeTrue();
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_MissingAlignmentName_Throws()
        {
            var tool = new RebuildAlignmentTool();
            var parameters = JsonDocument.Parse("""{}""").RootElement;
            Func<Task> act = async () => await tool.ExecuteAsync(
                null!, null!, parameters, new ToolCache(), CancellationToken.None);
            await act.Should().ThrowAsync<ArgumentException>();
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_NullCivilDoc_ReturnsFail()
        {
            var tool = new RebuildAlignmentTool();
            var parameters = JsonDocument.Parse("""{"alignment_name": "A1"}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.ExecutionFailed);
        }

        [Fact(Skip = "Happy path requires AutoCAD + live Civil 3D document")]
        [Trait("Category", "RequiresCivil3D")]
        public void Happy_RebuildsDependentProfilesAndCorridors() { }
    }
}
