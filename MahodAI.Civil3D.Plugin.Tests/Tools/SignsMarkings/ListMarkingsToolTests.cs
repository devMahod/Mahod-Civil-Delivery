using System;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools;
using MahodAI.Civil3D.Plugin.Tools.SignsMarkings;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Tools.SignsMarkings
{
    /// <summary>
    /// Sanity tests for <see cref="ListMarkingsTool"/>. The full happy path requires a live
    /// AutoCAD session (HostApplicationServices.WorkingDatabase + transaction), so most
    /// end-to-end checks are marked skip.
    /// </summary>
    public class ListMarkingsToolTests
    {
        [Fact]
        public void Tool_IsConstructable_AndHasMetadata()
        {
            var tool = new ListMarkingsTool();
            tool.Name.Should().Be("list_markings");
            tool.Description.Should().NotBeNullOrWhiteSpace();
            tool.Category.Should().Be("SignsMarkings");
            tool.Timeout.Should().BeGreaterThan(TimeSpan.Zero);
        }

        [Fact]
        public void Tool_IsRegisteredInRegistry()
        {
            var registry = ToolRegistry.Instance;
            registry.HasTool("list_markings").Should().BeTrue();
            var tool = registry.GetTool("list_markings");
            tool.Should().BeOfType<ListMarkingsTool>();
        }

        [Fact(Skip = "Happy path requires AutoCAD HostApplicationServices.WorkingDatabase")]
        [Trait("Category", "RequiresCivil3D")]
        public void Happy_Returns_MarkingList_WhenDrawingHasMarkings()
        {
            // End-to-end covered by integration run in Civil 3D 2026 Debug profile.
        }
    }
}
