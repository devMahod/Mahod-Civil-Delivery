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
    /// Parameter-validation tests for add_alignment_spiral. The safety contract
    /// (arc removal rolled back when AddFreeSCS fails) is enforced by
    /// ToolExecutor's abort-on-fail: every failure path in this tool returns
    /// ToolResult.Fail, never a success-ish result.
    /// </summary>
    public class AddAlignmentSpiralToolTests
    {
        [Fact]
        public void Tool_HasExpectedMetadata()
        {
            var tool = new AddAlignmentSpiralTool();
            tool.Name.Should().Be("add_alignment_spiral");
            tool.Category.Should().Be(ToolCategories.Modification);
        }

        [Fact]
        public void Tool_IsRegistered()
        {
            ToolRegistry.Instance.HasTool("add_alignment_spiral").Should().BeTrue();
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_MissingCurveElementIndex_ReturnsInvalidParameters()
        {
            var tool = new AddAlignmentSpiralTool();
            var parameters = JsonDocument.Parse(
                """{"alignment_name": "A1", "length": 50}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.InvalidParameters);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-10")]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_NonPositiveLength_ReturnsInvalidParameters(string length)
        {
            var tool = new AddAlignmentSpiralTool();
            var parameters = JsonDocument.Parse(
                $$"""{"alignment_name": "A1", "curve_element_index": 2, "length": {{length}}}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.InvalidParameters);
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_InvalidSpiralType_ReturnsInvalidParameters()
        {
            var tool = new AddAlignmentSpiralTool();
            var parameters = JsonDocument.Parse(
                """{"alignment_name": "A1", "curve_element_index": 2, "length": 50, "spiral_type": "sideways"}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.InvalidParameters);
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_NullCivilDoc_ReturnsFail()
        {
            var tool = new AddAlignmentSpiralTool();
            var parameters = JsonDocument.Parse(
                """{"alignment_name": "A1", "curve_element_index": 2, "length": 50}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.ExecutionFailed);
        }

        [Fact(Skip = "Happy path requires AutoCAD + live Civil 3D document")]
        [Trait("Category", "RequiresCivil3D")]
        public void Happy_FailedScsInsertion_RollsBackArcRemovalViaExecutorAbort() { }
    }
}
