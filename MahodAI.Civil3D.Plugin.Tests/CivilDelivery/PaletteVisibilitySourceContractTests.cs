using System.IO;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Live 30.09.2026 (1.3.7): the first MCD_CIVIL_DELIVERY of a session printed "נפתח" and showed
    /// nothing, the second "toggled" the invisible panel closed and printed "נפתח" again, and only the
    /// third call showed the panel. The command must decide by what is drawn, retry an undrawn first
    /// show once, and say what it did.
    /// </summary>
    public class PaletteVisibilitySourceContractTests
    {
        private static string PluginFile(params string[] path) => Path.Combine(
            EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", Path.Combine(path));

        private static string Between(string text, string start, string end)
        {
            var from = text.IndexOf(start, System.StringComparison.Ordinal);
            from.Should().BeGreaterThanOrEqualTo(0, $"'{start}' must exist");
            var to = text.IndexOf(end, from + start.Length, System.StringComparison.Ordinal);
            to.Should().BeGreaterThan(from, $"'{end}' must follow '{start}'");
            return text.Substring(from, to - from);
        }

        [Fact]
        public void Toggle_DecidesByTheDrawnPanel_NotByThePaletteFlag()
        {
            var palette = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryPalette.cs"));
            var toggle = Between(palette, "public static bool Toggle()", "internal static bool IsShownOnScreen(");
            toggle.Should().Contain("if (IsShownOnScreen(out var state))")
                .And.NotContain("_paletteSet.Visible", "the flag reported Visible for a panel that was not drawn");

            var check = Between(palette, "internal static bool IsShownOnScreen(", "private static bool OnSomeMonitor(");
            check.Should().Contain("IsWindowVisible(_host.Handle)", "a hidden parent hides the panel")
                .And.Contain("GetWindowRect(_host.Handle, out var r)")
                .And.Contain("(rolled up)")
                .And.Contain("OnSomeMonitor(rect, 100, 100)", "a panel on no monitor is not shown");
        }

        [Fact]
        public void FirstShow_IsRetriedOnceOnIdle_AndLogged()
        {
            var palette = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryPalette.cs"));
            var idle = Between(palette, "void Handler(object? sender, EventArgs e)", "Application.Idle += Handler;");
            idle.Should().Contain("Application.Idle -= Handler;", "the retry runs once, not on every idle");
            idle.IndexOf("if (IsShownOnScreen(out var state))", System.StringComparison.Ordinal)
                .Should().BeLessThan(idle.IndexOf("_paletteSet!.Visible = false;", System.StringComparison.Ordinal),
                    "a drawn panel is never hidden and re-shown");
            idle.Should().Contain("_paletteSet.Visible = true;")
                .And.Contain("MahodLogger.Info(\"palette shown: \" + state);")
                .And.Contain("palette not on screen after show");

            var onScreen = Between(palette, "internal static void EnsureOnScreen()", "private static void DeferEnsureUsableSize()");
            onScreen.Should().Contain("OnSomeMonitor(titleBar, 200, 30)")
                .And.NotContain("SystemInformation.VirtualScreen", "the virtual box includes gaps between monitors");
        }

        [Fact]
        public void Command_ReportsWhetherItOpenedOrClosedThePanel()
        {
            var command = File.ReadAllText(PluginFile("CivilDelivery", "Commands", "MhdCivilDeliveryCommand.cs"));
            command.Should().Contain("var shown = CivilDeliveryPalette.Toggle();")
                .And.Contain("\"\\nMahod Civil Delivery נפתח.\\n\"")
                .And.Contain("Mahod Civil Delivery נסגר.")
                .And.Contain("הפאנל יופיע בעוד כמה שניות", "the slow first opening is announced");
            command.IndexOf("var firstOpen = CivilDeliveryPalette.IsFirstOpen;", System.StringComparison.Ordinal)
                .Should().BeLessThan(command.IndexOf("var shown = CivilDeliveryPalette.Toggle();", System.StringComparison.Ordinal),
                    "first-open is read before the toggle creates the palette");
        }

        [Fact]
        public void RepeatedCommandWhileOpening_KeepsThePanel_AndTheLoadTimeIsLogged()
        {
            // Live 30.09.2026 (1.3.8): one click opened the panel, but it painted seconds later; a second
            // command in that window used to close it again.
            var palette = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryPalette.cs"));
            var toggle = Between(palette, "public static bool Toggle()", "public static bool IsFirstOpen");
            toggle.IndexOf("DateTime.UtcNow - _openedAtUtc < RepeatKeepsOpen", System.StringComparison.Ordinal)
                .Should().BeLessThan(toggle.IndexOf("Hide();", System.StringComparison.Ordinal));
            toggle.Should().Contain("_openedAtUtc = DateTime.UtcNow;");
            MahodAI.Civil3D.Plugin.CivilDelivery.UI.CivilDeliveryPalette.RepeatKeepsOpen
                .Should().BeGreaterThanOrEqualTo(System.TimeSpan.FromSeconds(15)).And.BeLessThanOrEqualTo(System.TimeSpan.FromSeconds(60));

            var control = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
            control.Should().Contain("MahodLogger.Info($\"palette load {loadTimer.ElapsedMilliseconds} ms\");");
        }
    }
}
