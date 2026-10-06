using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// The "מדריך למשתמש" button (b15, plan B5): visible only in the standalone tool, it opens a temporary copy of the
    /// running bundle's guide and never the installed file or any other guide. Source contract only; the native
    /// open/upgrade check belongs to the packaging acceptance.
    /// </summary>
    public class UserGuideButtonSourceContractTests
    {
        private static string Root => typeof(UserGuideButtonSourceContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "MahodPluginSourceDir").Value!;

        private static string Ui(string name) => File.ReadAllText(Path.Combine(Root, "CivilDelivery", "UI", name));

        [Fact]
        public void TheButtonIsCollapsedInMarkupAndMadeVisibleOnlyForTheStandaloneTool()
        {
            var xaml = Ui("CivilDeliveryControl.xaml");
            xaml.Should().Contain("x:Name=\"BtnUserGuide\"").And.Contain("Click=\"OnOpenUserGuide\" Visibility=\"Collapsed\"");
            var code = Ui("CivilDeliveryControl.xaml.cs");
            var show = code.IndexOf("BtnUserGuide.Visibility = Visibility.Visible;", StringComparison.Ordinal);
            show.Should().BePositive();
            var gate = code.LastIndexOf("#if MAHOD_CD_STANDALONE && MAHOD_CD_GUIDE", show, StringComparison.Ordinal);
            gate.Should().BePositive("only a build that stages the approved guide PDF shows the button (review GUIDE-1)");
            code[..show].LastIndexOf("#endif", StringComparison.Ordinal).Should().BeLessThan(gate);
        }

        [Fact]
        public void TheHandlerOpensOnlyTheVerifiedTemporaryCopy()
        {
            var guide = Ui("CivilDeliveryControl.Guide.cs");
            guide.Should().Contain("CivilDeliveryGuidePath.Resolve(assemblyLocation)")
                .And.Contain("CivilDeliveryGuideTempCopy.Prepare(guide)")
                .And.Contain("new System.Diagnostics.ProcessStartInfo(copy.TempPath!)");
            guide.Split("ProcessStartInfo(", StringSplitOptions.None).Length.Should().Be(2, "exactly one open request");
            guide.Should().NotContain("ProcessStartInfo(guide.GuidePath", "the installed guide itself is never opened");
            var prepare = guide.IndexOf("CivilDeliveryGuideTempCopy.Prepare(guide)", StringComparison.Ordinal);
            var open = guide.IndexOf("ProcessStartInfo(copy.TempPath!)", StringComparison.Ordinal);
            prepare.Should().BeLessThan(open);
            guide.IndexOf("State.Ready)", prepare, StringComparison.Ordinal).Should().BeLessThan(open,
                "a failed copy returns before any open request");
        }

        [Fact]
        public void TheReviewedHelpersAreIntegratedUnchanged()
        {
            var runtime = Path.Combine(Root, "..", "MahodCivilDelivery", "Mahod.CivilDelivery", "Runtime");
            Sha(Path.Combine(runtime, "CivilDeliveryGuidePath.cs"))
                .Should().Be("182B0B5E29B9A1731861E9AB4D06B205FB0A0AE3C365C02943BC5B7C05767A23");
            Sha(Path.Combine(runtime, "CivilDeliveryGuideTempCopy.cs"))
                .Should().Be("331C872B8AC456CEA5FA02BA81729BDE19FE6050C9DE279843D84AF7433E6DEC");
        }

        private static string Sha(string path) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
    }
}
