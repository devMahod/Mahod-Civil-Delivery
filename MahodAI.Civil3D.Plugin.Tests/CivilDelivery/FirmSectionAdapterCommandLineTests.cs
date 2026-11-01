using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// The firm's section program reports a station it could not draw only on the command line (2026-10-28), so the
/// adapter keeps the lines printed during its call: what the history holds after it, past what it held before.
/// </summary>
public sealed class FirmSectionAdapterCommandLineTests
{
    [Fact]
    public void NewLines_AreTheLinesPastTheEarlierHistory()
    {
        var before = new[] { "Command: MCD_SMOKE_TYPICAL", "alignment:" };
        var after = new[] { "Command: MCD_SMOKE_TYPICAL", "alignment:", "Station creation error: 100 - Object reference not set" };

        FirmSectionAdapter.NewLines(before, after).Should().Equal("Station creation error: 100 - Object reference not set");
    }

    [Fact]
    public void NewLines_FollowAHistoryThatScrolled()
    {
        var before = new[] { "a", "b", "c", "d" };
        var after = new[] { "c", "d", "e", "f" };

        FirmSectionAdapter.NewLines(before, after).Should().Equal("e", "f");
    }

    [Fact]
    public void NewLines_AreEmptyWhenNothingWasPrinted_AndEverythingWithoutAnEarlierHistory()
    {
        var lines = new[] { "a", "b" };

        FirmSectionAdapter.NewLines(lines, lines).Should().BeEmpty();
        FirmSectionAdapter.NewLines(System.Array.Empty<string>(), lines).Should().Equal("a", "b");
    }
}
