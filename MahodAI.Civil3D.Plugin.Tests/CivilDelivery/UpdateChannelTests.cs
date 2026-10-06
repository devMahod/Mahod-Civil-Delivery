using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// "בדוק עדכונים" must be boring and safe: it tells the engineer what changed, it never
    /// installs anything by itself, and it refuses anything it cannot verify.
    /// </summary>
    public class UpdateChannelTests
    {
        private const string Channel = "https://updates.mahod.co.il/civil-delivery";

        private static string PluginSourceDir =>
            typeof(UpdateChannelTests).Assembly
                .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Manifest(string version, string? sha = "abc", string? file = "Setup.exe") =>
            $$"""
            { "schema_version": 1, "version": "{{version}}", "released_utc": "2026-09-01T08:00:00Z",
              "setup_file": "{{file}}", "sha256": "{{sha}}", "notes": ["מיפוי אוטומטי לצנרת", "תיקון"] }
            """;

        [Fact]
        public void NewerVersion_IsOffered_WithItsReleaseNotes()
        {
            var r = UpdateChannel.Evaluate("1.0.0", Channel, Manifest("1.1.0"));
            r.State.Should().Be(UpdateChannel.State.UpdateAvailable);
            r.CanDownload.Should().BeTrue();
            r.Headline.Should().Contain("1.1.0").And.Contain("1.0.0");
            r.Detail.Should().Contain("מיפוי אוטומטי לצנרת", "she decides with the notes in front of her");
        }

        [Theory]
        [InlineData("1.0.0")]   // same
        [InlineData("0.9.9")]   // older than installed
        public void SameOrOlderVersion_IsNotOffered(string published)
        {
            var r = UpdateChannel.Evaluate("1.0.0", Channel, Manifest(published));
            r.State.Should().Be(UpdateChannel.State.UpToDate);
            r.CanDownload.Should().BeFalse();
        }

        [Fact]
        public void NoChannel_SaysSo_InsteadOfFailingSilently()
        {
            var r = UpdateChannel.Evaluate("1.0.0", null, null);
            r.State.Should().Be(UpdateChannel.State.NotConfigured);
            r.CanDownload.Should().BeFalse();
            r.Headline.Should().NotBeNullOrWhiteSpace();
        }

        [Fact]
        public void UnreachableChannel_IsReported_AndTheInstalledToolKeepsWorking()
        {
            var r = UpdateChannel.Evaluate("1.0.0", Channel, null);
            r.State.Should().Be(UpdateChannel.State.Unreachable);
            r.Detail.Should().Contain("ממשיך לעבוד");
            r.CanDownload.Should().BeFalse();
        }

        [Theory]
        [InlineData("not json at all")]
        [InlineData("{ }")]                                   // no version
        [InlineData("{ \"schema_version\": 99, \"version\": \"2.0.0\" }")]  // newer schema
        public void AnythingItCannotTrust_IsRefused(string json)
        {
            UpdateChannel.Evaluate("1.0.0", Channel, json)
                .CanDownload.Should().BeFalse("an update this build cannot understand is never offered");
        }

        [Fact]
        public void ManifestWithoutHashOrFile_IsNeverDownloadable()
        {
            UpdateChannel.Evaluate("1.0.0", Channel, Manifest("1.2.0", sha: ""))
                .CanDownload.Should().BeFalse("nothing is downloaded that cannot be verified");
            UpdateChannel.Evaluate("1.0.0", Channel, Manifest("1.2.0", file: ""))
                .CanDownload.Should().BeFalse();
        }

        [Theory]
        [InlineData("1.10.0", "1.9.0", 1)]
        [InlineData("1.0.0", "1.0.0", 0)]
        [InlineData("2.0.0", "10.0.0", -1)]
        [InlineData("1.0.1", "1.0", 1)]
        public void VersionCompare_IsNumeric_NotAlphabetical(string a, string b, int expected)
        {
            Math.Sign(UpdateChannel.CompareVersions(a, b)).Should().Be(expected);
        }

        [Fact]
        public void UnparsableVersion_NeverTriggersAnUpdate()
        {
            UpdateChannel.Evaluate("1.0.0", Channel, Manifest("banana"))
                .State.Should().Be(UpdateChannel.State.UpToDate,
                    "a version this build cannot parse must not be treated as newer");
        }

        [Fact]
        public void Locations_AreBuiltUnderTheChannelRoot()
        {
            UpdateChannel.ManifestLocation(Channel).Should().Be(Channel + "/latest.json");
            UpdateChannel.ManifestLocation(Channel + "/").Should().Be(Channel + "/latest.json");

            var m = new UpdateChannel.Manifest { SetupFile = "Mahod_Civil_Delivery_Setup_1.1.0.exe" };
            UpdateChannel.SetupLocation(Channel, m)
                .Should().Be(Channel + "/Mahod_Civil_Delivery_Setup_1.1.0.exe");
        }

        [Fact]
        public void ChannelSettings_AreReadWhicheverWayTheFileIsWritten()
        {
            // Caught live 2026-08-20: the settings file said {"channel": …} while the class
            // property is Channel, and System.Text.Json matched case-sensitively — so a
            // configured channel silently read as "not configured".
            var service = File.ReadAllText(Path.Combine(PluginSourceDir, "CivilDelivery", "Support", "UpdateService.cs"));
            service.Should().Contain("PropertyNameCaseInsensitive = true");
            service.Should().Contain("JsonPropertyName(\"channel\")");
            service.Should().Contain("Deserialize<ChannelSettings>(File.ReadAllText(ChannelSettingsPath), SettingsJson)");
        }

        [Fact]
        public void TheProductNeverRunsAnInstallerByItself()
        {
            var dir = Path.Combine(PluginSourceDir, "CivilDelivery");
            var service = File.ReadAllText(Path.Combine(dir, "Support", "UpdateService.cs"));
            var ui = File.ReadAllText(Path.Combine(dir, "UI", "CivilDeliveryControl.xaml.cs"));

            service.Should().NotContain("Process.Start", "the downloaded setup is never executed for her");
            service.Should().Contain("ArtifactHash.Sha256OfFile", "downloads are verified");
            service.Should().Contain("File.Delete(target)", "a file failing verification is destroyed, not offered");
            service.Should().Contain("https://", "the channel is https-only");

            // The check is SHA-256 integrity, NOT authenticity: the installer carries no
            // Authenticode signature, so no string the engineer reads may promise one.
            // (Comments may — and do — spell the limitation out; only literals are policed.)
            // Only the strings the engineer reads are policed; the comments may (and do)
            // spell out that this is integrity, not authenticity.
            var literals = System.Text.RegularExpressions.Regex.Matches(service, "\"[^\"\\n]*\"")
                .Select(m => m.Value)
                .ToList();
            var userText = string.Join("\n", literals);
            userText.Should().NotContain("\u05d7\u05ea\u05d9\u05de\u05d4", "SHA-256 is a fingerprint, not a signature");
            userText.Should().NotContain("Authenticode", "this build verifies no signature");
            userText.Should().Contain("\u05d8\u05d1\u05d9\u05e2\u05ea \u05d4-SHA-256 \u05e9\u05e4\u05d5\u05e8\u05e1\u05de\u05d4");
            service.Should().Contain("INTEGRITY check, not authenticity",
                "the limitation is documented where the check is implemented");
            // A button whose only outcome is "not configured" reads as an unfinished product.
            ui.Should().Contain("BtnCheckUpdates.Visibility = UpdateService.ReadChannel() == null");
            ui.Should().Contain("OnCheckUpdates", "the button exists");
            ui.Should().Contain("UpdateService.Download(channel!, result.Manifest!)");
        }
    }
}
