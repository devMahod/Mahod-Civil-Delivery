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
    /// <summary>
    /// Parameter-validation and contract tests for remove_alignment_curve.
    /// Happy paths (geometry writes) require a live Civil 3D document; the
    /// deflection / PI-intersection math is covered by
    /// <see cref="CurveRemovalGeometryTests"/>.
    /// </summary>
    public class RemoveAlignmentCurveToolTests
    {
        [Fact]
        public void Tool_HasExpectedMetadata()
        {
            var tool = new RemoveAlignmentCurveTool();
            tool.Name.Should().Be("remove_alignment_curve");
            tool.Category.Should().Be(ToolCategories.Modification);
            tool.Description.Should().Contain("max_deflection_deg",
                "the agent reads the description to learn the deflection safety guard");
            tool.Description.Should().Contain("target_station",
                "the agent reads the description to learn stale-index re-resolution");
            tool.Description.Should().Contain("Spiral-Curve-Spiral",
                "the agent must know a full SCS group is removable as one unit");
        }

        [Fact]
        public void Tool_IsRegistered()
        {
            ToolRegistry.Instance.HasTool("remove_alignment_curve").Should().BeTrue();
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_MissingAlignmentName_Throws()
        {
            var tool = new RemoveAlignmentCurveTool();
            var parameters = JsonDocument.Parse("""{}""").RootElement;
            Func<Task> act = async () => await tool.ExecuteAsync(
                null!, null!, parameters, new ToolCache(), CancellationToken.None);
            await act.Should().ThrowAsync<ArgumentException>();
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_MissingElementIndex_ReturnsInvalidParameters()
        {
            var tool = new RemoveAlignmentCurveTool();
            var parameters = JsonDocument.Parse(
                """{"alignment_name": "A1"}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.InvalidParameters);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-0.5")]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_NonPositiveMaxDeflection_ReturnsInvalidParameters(string maxDeflection)
        {
            var tool = new RemoveAlignmentCurveTool();
            var parameters = JsonDocument.Parse(
                $$"""{"alignment_name": "A1", "element_index": 2, "max_deflection_deg": {{maxDeflection}}}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.InvalidParameters);
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_NullCivilDoc_ReturnsFail()
        {
            var tool = new RemoveAlignmentCurveTool();
            var parameters = JsonDocument.Parse(
                """{"alignment_name": "A1", "element_index": 2}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.ExecutionFailed);
            result.Error.Message.Should().Contain("Civil 3D");
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_OptionalParameters_AreAcceptedWithoutSchemaError()
        {
            // target_station and max_deflection_deg must not be rejected as
            // unknown/invalid parameters — the agent's fix flow sends both.
            var tool = new RemoveAlignmentCurveTool();
            var parameters = JsonDocument.Parse(
                """{"alignment_name": "A1", "element_index": 2, "target_station": 530.25, "max_deflection_deg": 1.0}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            // Fails on the null civilDoc — NOT on parameter validation.
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.ExecutionFailed);
            result.Error.Message.Should().Contain("Civil 3D");
        }

        [Fact(Skip = "Happy path requires AutoCAD + live Civil 3D document")]
        [Trait("Category", "RequiresCivil3D")]
        public void Happy_RemovesCurveAndExtendsTangentsToPi_NetEntityCountMinusOne() { }
    }
}
