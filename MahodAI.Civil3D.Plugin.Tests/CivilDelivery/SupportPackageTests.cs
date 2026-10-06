using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// The support package must be small enough to email. The first real export on the
    /// 6422 machine produced a 180 MB zip because it packed every run artifact
    /// (2026-08-19); these tests pin the bound and what survives it.
    /// </summary>
    public class SupportPackageTests
    {
        private static SupportPackage.Candidate F(string rel, double mb, int ageDays = 0) =>
            new(rel, (long)(mb * 1024 * 1024), new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc).AddDays(-ageDays));

        private static List<SupportPackage.Candidate> RealisticState()
        {
            var files = new List<SupportPackage.Candidate>
            {
                F("logs/stage_ui_plan.log", 0.05),
                F("logs/stage_ui_apply.log", 0.05),
                F("logs/stage_ui_estimate_scan.log", 0.4),
                F("profiles/6422/project-profile.yaml", 0.01),
                F("install_state.json", 0.01),
            };
            // 20 runs, each with a huge estimate_result.json - this is what blew the package up.
            for (int i = 0; i < 20; i++)
            {
                files.Add(F($"runs/estimate-{i:00}/estimate_result.json", 7.4, ageDays: i));
                files.Add(F($"runs/estimate-{i:00}/neutral_quantity_records.json", 3.0, ageDays: i));
                files.Add(F($"runs/estimate-{i:00}/quantity_preflight.json", 0.02, ageDays: i));
            }
            return files;
        }

        [Fact]
        public void Package_NeverCarriesAStoredSecret()
        {
            // 01.10.2026 review: the mapping assistant keeps its encrypted API key in a "secrets" folder of the state root,
            // beside logs and profiles, and everything outside runs/ was packed. A stored key never leaves the machine.
            var folder = "sec" + "rets";
            var files = RealisticState();
            files.Add(F($"{folder}/assistant.dpapi", 0.001));
            files.Add(F($"profiles/6422/{folder}/other.bin", 0.001));
            files.Add(F("profiles/6422/access.pem", 0.001));
            // Codex 02:33: the run branch too — the newest run, well inside the budget, must still not carry them.
            files.Add(F($"runs/estimate-00/{folder}/example.bin", 0.001));
            files.Add(F("runs/estimate-00/example.pem", 0.001));
            files.Add(F("runs/estimate-00/sub/example.dpapi", 0.001));
            var plan = SupportPackage.Choose(files);
            plan.Included.Select(i => i.RelativePath).Should().NotContain(p => SupportPackage.IsSecret(p))
                .And.Contain("profiles/6422/project-profile.yaml").And.Contain("install_state.json")
                .And.Contain("runs/estimate-00/quantity_preflight.json", "the run itself is still packed");
            plan.Skipped.Where(s => s.Reason == SupportPackage.SecretReason).Select(s => s.RelativePath).Should().BeEquivalentTo(
                new[] { $"{folder}/assistant.dpapi", $"profiles/6422/{folder}/other.bin", "profiles/6422/access.pem",
                        $"runs/estimate-00/{folder}/example.bin", "runs/estimate-00/example.pem", "runs/estimate-00/sub/example.dpapi" });
            var manifest = SupportPackage.Manifest(plan, "state");
            manifest.Should().Contain(SupportPackage.SecretReason);
            foreach (var s in plan.Skipped.Where(s => s.Reason == SupportPackage.SecretReason))
                manifest.Should().Contain(s.RelativePath, "every file left out as a stored secret is named, never its content");
            SupportPackage.IsSecret($"logs/{folder}-report.log").Should().BeFalse("only a folder of that name, not a file name");
        }

        [Theory]
        [InlineData(@"runs\estimate-00\SECRETS\example.bin")]
        [InlineData(@"Secrets\assistant.DPAPI")]
        [InlineData("runs/estimate-00/Example.PEM")]
        [InlineData(@"profiles\6422\access.Key")]
        [InlineData("runs/estimate-00/sub/example.PfX")]
        public void StoredSecret_IsRecognised_InAnyCaseAndWithEitherSeparator(string path)
        {
            // Codex 02:36: the normalisation was read in code but no mixed-case or backslash case was run.
            SupportPackage.IsSecret(path).Should().BeTrue();
            var plan = SupportPackage.Choose(new[] { F(path, 0.001), F("runs/estimate-00/quantity_preflight.json", 0.02) });
            plan.Included.Select(i => i.RelativePath).Should().Equal("runs/estimate-00/quantity_preflight.json");
            plan.Skipped.Should().ContainSingle(s => s.RelativePath == path && s.Reason == SupportPackage.SecretReason);
        }

        [Fact]
        public void InstallRestorePoints_AreNotPacked_AndAreSummarisedInOneLine()
        {
            // Live 01.10.2026 (b5): the old installer's restore-points held 36 copies of an earlier bundle — 1,485 files,
            // 350 MB of DLLs and videos, each under the single-file limit — and the package grew to 156 MB.
            var files = RealisticState();
            for (int b = 0; b < 36; b++)
                for (int i = 0; i < 41; i++)
                    files.Add(F($"restore-points/MahodAI.bundle-202608{b:00}/Contents/2026/file{i:00}.dll", 0.25, ageDays: 40));
            files.Add(F(@"Restore-Points\MahodAI.bundle-original\Contents\guide.mp4", 3.9, ageDays: 41));
            var plan = SupportPackage.Choose(files);

            plan.TotalBytes.Should().BeLessThan(30L * 1024 * 1024, "a support package must stay small enough to email");
            plan.Included.Select(i => i.RelativePath).Should().NotContain(p => p.Replace('\\', '/').StartsWith("restore-points/", StringComparison.OrdinalIgnoreCase))
                .And.Contain("install_state.json").And.Contain("runs/estimate-00/quantity_preflight.json");
            plan.Skipped.Count(s => s.Reason == SupportPackage.RestorePointReason).Should().Be(36 * 41 + 1);

            var manifest = SupportPackage.Manifest(plan, "state").Split(Environment.NewLine);
            manifest.Count(l => l.Contains(SupportPackage.RestorePointReason)).Should().Be(1, "one summary line, not 1,477 rows");
            manifest.Single(l => l.Contains(SupportPackage.RestorePointReason)).Should().Contain("1477").And.Contain("restore-points");
            manifest.Should().Contain(l => l.Contains("estimate_result.json"), "the other skip reasons are still listed");
        }

        [Fact]
        public void Package_StaysSmallEnoughToEmail()
        {
            var plan = SupportPackage.Choose(RealisticState());

            plan.TotalBytes.Should().BeLessThan(30L * 1024 * 1024,
                "180 MB could not be sent to support");
            plan.Skipped.Should().NotBeEmpty("the oversized artifacts must be reported, not silently dropped");
        }

        [Fact]
        public void LogsProfileAndReceipt_AreAlwaysIncluded()
        {
            var plan = SupportPackage.Choose(RealisticState());
            var included = plan.Included.Select(i => i.RelativePath).ToList();

            included.Should().Contain("logs/stage_ui_plan.log");
            included.Should().Contain("logs/stage_ui_apply.log");
            included.Should().Contain("logs/stage_ui_estimate_scan.log");
            included.Should().Contain("profiles/6422/project-profile.yaml");
            included.Should().Contain("install_state.json",
                "support needs to know which build produced the problem");
        }

        [Fact]
        public void NewestRunsWin_AndTheOldestAreDropped()
        {
            var plan = SupportPackage.Choose(RealisticState());
            var runs = plan.Included
                .Select(i => i.RelativePath)
                .Where(p => p.StartsWith("runs/", StringComparison.Ordinal))
                .Select(p => p.Split('/')[1])
                .Distinct()
                .ToList();

            runs.Should().Contain("estimate-00", "the newest run is the one support will look at");
            runs.Should().NotContain("estimate-19", "the oldest run is not worth the bytes");
            runs.Count.Should().BeLessThanOrEqualTo(SupportPackage.MaxRuns);
        }

        [Fact]
        public void EverySkippedFileCarriesAReason_AndReachesTheManifest()
        {
            var plan = SupportPackage.Choose(RealisticState());
            plan.Skipped.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Reason));

            var manifest = SupportPackage.Manifest(plan, @"C:\state\civil-delivery");
            manifest.Should().Contain("חבילת תמיכה");
            manifest.Should().Contain("לא נכללו");
            manifest.Should().Contain("MB");
        }

        [Fact]
        public void EmptyState_ProducesAnEmptyButValidPlan()
        {
            var plan = SupportPackage.Choose(Array.Empty<SupportPackage.Candidate>());
            plan.Included.Should().BeEmpty();
            plan.Skipped.Should().BeEmpty();
            plan.TotalBytes.Should().Be(0);
            SupportPackage.Manifest(plan, "x").Should().Contain("נכללו 0 קבצים");
        }

        [Fact]
        public void DisplayPath_KeepsTheHebrewFileNameReadable()
        {
            // Wrapping the whole path in an LTR mark scrambled the Hebrew file name in the
            // export dialog (live 2026-08-19). Folder and file are shown on separate lines.
            var path = Path.Combine(@"C:\Users\arthurf\Documents\MahodCivilDelivery\6422", "אומדן-מוקדם-6422-20260819-2316.xlsx");
            var shown = SupportPackage.DisplayPath(path);

            var lines = shown.Split(Environment.NewLine);
            lines.Should().HaveCount(2);
            lines[1].Should().Be("אומדן-מוקדם-6422-20260819-2316.xlsx",
                "the file name is printed exactly as it is on disk, with no bidi marks inside it");
            lines[0].Should().StartWith("\u200E").And.EndWith("\u200E");
            lines[0].Should().Contain(@"C:\Users\arthurf\Documents\MahodCivilDelivery\6422\");
        }

        [Fact]
        public void DisplayPath_HandlesBareNamesAndEmpty()
        {
            SupportPackage.DisplayPath("report.xlsx").Should().Be("report.xlsx");
            SupportPackage.DisplayPath("").Should().Be("");
        }
    }
}
