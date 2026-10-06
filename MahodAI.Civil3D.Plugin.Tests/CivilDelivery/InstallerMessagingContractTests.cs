using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// The engineer identifies a candidate by its PACKAGE revision (1.2.x). The 1.2.30
    /// install dialog led with the previous PLATFORM assembly version (1.3.x) and read
    /// as "installed the old version". Every operator-facing message must name the
    /// package first (final sweep, 1.2.31).
    /// </summary>
    public class InstallerMessagingContractTests
    {
        private static string PluginSourceDir =>
            typeof(InstallerMessagingContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Repo(params string[] parts) => File.ReadAllText(
            Path.Combine(new[] { PluginSourceDir, ".." }.Concat(parts).ToArray()));

        [Fact]
        public void InstallScript_NamesThePackageBeforeAnyPlatformNumber()
        {
            var script = Repo("installer", "Install-MahodCivilDelivery.ps1");

            script.Should().Contain("Incoming package          : Mahod Civil Delivery $packageRevision")
                .And.Contain("Installed package         : $priorPackageText")
                .And.Contain("Upgrading to Mahod Civil Delivery $packageRevision (from $priorPackageText;");
            // The prior package revision is read from the owned install state, never guessed.
            script.Should().Contain("$priorPackageRevision = [string](([System.IO.File]::ReadAllText(")
                .And.Contain(".package_revision)");

            // The Hebrew completion dialog opens with the package that was just installed
            // and names the replaced package explicitly.
            var report = script.Substring(script.IndexOf("$report = @()", StringComparison.Ordinal));
            report.IndexOf("הותקנה: Mahod Civil Delivery $packageRevision", StringComparison.Ordinal)
                .Should().BeGreaterThan(0).And.BeLessThan(
                    report.IndexOf("ההתקנה הושלמה בהצלחה", StringComparison.Ordinal));
            report.Should().Contain("הוחלפה התקנה קודמת: ")
                .And.Contain("הגרסה שנטענת עכשיו: Mahod Civil Delivery $packageRevision")
                .And.NotContain("נמצאה התקנה קודמת: ' + $(if ($existingVersion) { \"פלטפורמה $existingVersion\"");
        }

        [Fact]
        public void Bootstrap_ShowsThePackageRevisionInItsConfirmation()
        {
            var program = Repo("installer", "bootstrap", "Program.cs");
            program.Should().Contain("private static string PackageRevision =>")
                .And.Contain("Assembly.GetExecutingAssembly().GetName().Version")
                .And.Contain(".ToString(3)")
                .And.Contain("$\"להתקין את Mahod Civil Delivery {PackageRevision} עבור Civil 3D?")
                .And.Contain("$\"Mahod Civil Delivery {PackageRevision} — התקנה\"");
        }

        [Fact]
        public void GateDoc_ExplainsThatFirstPlanHasNoReadyRow_AndHowToNameSpans()
        {
            var doc = Repo("runtime-gate", "ARTHUR_10_MIN_GUI_GATE_HE.md");
            doc.Should().Contain("**אם אין אף שורה `Ready`**")
                .And.Contain("**שמות רצועות בחתך**")
                .And.Contain("**בדוק פרטי שורה**")
                .And.Contain("תיקון במקור וסריקה מחדש")
                .And.Contain("אין לתת שם מומצא");
            Repo("nataly", "docs", "04_KNOWN_LIMITATIONS_HE.md")
                .Should().Contain("**רצועות ללא שם ודאי.**");
        }
    }
}
