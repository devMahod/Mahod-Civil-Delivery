using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Tools
{
    public class ToolResultTransactionTests
    {
        [Fact]
        public void ReadOnly_SucceedsButCannotCommitProductionTransaction()
        {
            var result = ToolResult.ReadOnly(new { value = 42 });

            result.Success.Should().BeTrue();
            result.Outcome.Should().Be(ToolOutcome.Succeeded);
            result.IsReadOnly.Should().BeTrue();
            result.MayCommitProductionObject.Should().BeFalse();
        }

        [Fact]
        public void Ok_RemainsCommittableForExistingMutationTools()
        {
            var result = ToolResult.Ok(new { changed = true });

            result.Success.Should().BeTrue();
            result.IsReadOnly.Should().BeFalse();
            result.MayCommitProductionObject.Should().BeTrue();
        }
    }
}
