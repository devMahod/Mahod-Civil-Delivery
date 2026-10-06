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
    /// Parameter-validation and contract tests for modify_alignment_curve_radius.
    /// Happy paths (geometry writes) require a live Civil 3D document; the
    /// exact_only decision logic is covered by <see cref="RadiusAdjustmentSearchTests"/>.
    /// </summary>
    public class ModifyAlignmentCurveRadiusToolTests
    {
        [Fact]
        public void Tool_HasExpectedMetadata()
        {
            var tool = new ModifyAlignmentCurveRadiusTool();
            tool.Name.Should().Be("modify_alignment_curve_radius");
            tool.Category.Should().Be(ToolCategories.Modification);
            tool.Description.Should().Contain("exact_only",
                "the agent reads the description to learn the no-partial-fix switch");
        }

        [Fact]
        public void Tool_IsRegistered()
        {
            ToolRegistry.Instance.HasTool("modify_alignment_curve_radius").Should().BeTrue();
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_MissingAlignmentName_Throws()
        {
            var tool = new ModifyAlignmentCurveRadiusTool();
            var parameters = JsonDocument.Parse("""{}""").RootElement;
            Func<Task> act = async () => await tool.ExecuteAsync(
                null!, null!, parameters, new ToolCache(), CancellationToken.None);
            await act.Should().ThrowAsync<ArgumentException>();
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_MissingElementIndex_ReturnsInvalidParameters()
        {
            var tool = new ModifyAlignmentCurveRadiusTool();
            var parameters = JsonDocument.Parse(
                """{"alignment_name": "A1", "new_radius": 900}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.InvalidParameters);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-5")]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_NonPositiveRadius_ReturnsInvalidParameters(string radius)
        {
            var tool = new ModifyAlignmentCurveRadiusTool();
            var parameters = JsonDocument.Parse(
                $$"""{"alignment_name": "A1", "element_index": 2, "new_radius": {{radius}}}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.InvalidParameters);
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_NullCivilDoc_ReturnsFail()
        {
            var tool = new ModifyAlignmentCurveRadiusTool();
            var parameters = JsonDocument.Parse(
                """{"alignment_name": "A1", "element_index": 2, "new_radius": 900}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.ExecutionFailed);
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_ExactOnlyParameter_IsAcceptedWithoutSchemaError()
        {
            // exact_only must not be rejected as an unknown/invalid parameter —
            // back-compat contract: default false, opt-in true from the fix flow.
            var tool = new ModifyAlignmentCurveRadiusTool();
            var parameters = JsonDocument.Parse(
                """{"alignment_name": "A1", "element_index": 2, "new_radius": 900, "exact_only": true}""").RootElement;
            var result = await tool.ExecuteAsync(null!, null!, parameters, new ToolCache(), CancellationToken.None);
            // Fails on the null civilDoc — NOT on parameter validation.
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.ExecutionFailed);
            result.Error.Message.Should().Contain("Civil 3D");
        }

        [Fact(Skip = "Happy path requires AutoCAD + live Civil 3D document")]
        [Trait("Category", "RequiresCivil3D")]
        public void Happy_ExactOnlyRejection_LeavesGeometryUntouched_AndReportsAchievableRadius() { }
    }
}
