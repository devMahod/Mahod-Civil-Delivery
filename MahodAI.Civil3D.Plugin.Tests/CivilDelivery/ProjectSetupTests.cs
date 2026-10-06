using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.Tools.CivilDelivery;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public class ProjectSetupServiceTests
    {
        private static ProjectProfile EmptyProfile() => new() { ProfileId = "6422" };

        [Fact]
        public void NeedsSetup_TrueWhenClLayersUnconfigured()
        {
            var p = EmptyProfile();
            ProjectSetupService.NeedsSetup(p).Should().BeTrue(
                "an unconfigured profile must route to setup, not to an empty section result");
        }

        [Fact]
        public void NeedsSetup_FalseOnceLayersAndSourcesExist()
        {
            var p = EmptyProfile();
            p.Sections.Cl.LayerPatterns.Add("CL-SECTIONS");
            p.Sections.Sources.SampledSourceRules.Add(
                new ProjectProfile.SectionsProfile.SourcesProfile.SourceRule
                { Name = "EG", Kind = "surface", Required = true });

            ProjectSetupService.NeedsSetup(p).Should().BeFalse();
        }

        [Fact]
        public void ApplySelection_WritesExactlyWhatTheEngineerChose()
        {
            var p = EmptyProfile();
            var selection = new ProjectSetupSelection
            {
                ClLayers = { "CL-SEC", "CL-SEC-2" },
                ClSourceFiles = { "CL.dwg" },
                AllowedAlignments = { "NATZ-MAIN" },
                SampledSources = { ["EG"] = "surface", ["CORR-1"] = "corridor" },
                IntersectionToleranceM = 0.25,
                ApprovedBy = "nataly",
            };

            ProjectSetupService.ApplySelection(p, selection);

            p.Sections.Cl.LayerPatterns.Should().BeEquivalentTo(new[] { "CL-SEC", "CL-SEC-2" });
            p.Sections.Cl.SourceFiles.Should().BeEquivalentTo(new[] { "CL.dwg" });
            p.Sections.Alignments.AllowedNames.Should().BeEquivalentTo(new[] { "NATZ-MAIN" });
            p.Sections.Cl.IntersectionToleranceM.Should().Be(0.25);
            p.Sections.Sources.SampledSourceRules.Should().HaveCount(2);
            p.Sections.Sources.SampledSourceRules.Should().OnlyContain(r => r.Required);
            p.Sections.Sources.SampledSourceRules.Select(r => r.Kind)
                .Should().BeEquivalentTo(new[] { "surface", "corridor" });
        }

        [Fact]
        public void ApplySelection_EmptySnapshotClearsEveryStaleConfiguredValue()
        {
            var p = EmptyProfile();
            p.Sections.Cl.LayerPatterns.Add("OLD-CL");
            p.Sections.Cl.SourceFiles.Add("old-cl.dwg");
            p.Sections.Alignments.AllowedNames.Add("OLD-ALIGNMENT");
            p.Sections.Sources.SampledSourceRules.Add(
                new ProjectProfile.SectionsProfile.SourcesProfile.SourceRule
                { Name = "OLD-SURFACE", Kind = "surface", Required = true });
            p.Sections.Cl.IntersectionToleranceM = 0.75;

            ProjectSetupService.ApplySelection(p, new ProjectSetupSelection());

            p.Sections.Cl.LayerPatterns.Should().BeEmpty();
            p.Sections.Cl.SourceFiles.Should().BeEmpty();
            p.Sections.Alignments.AllowedNames.Should().BeEmpty();
            p.Sections.Sources.SampledSourceRules.Should().BeEmpty();
            p.Sections.Cl.IntersectionToleranceM.Should().BeNull();
            ProjectSetupService.NeedsSetup(p).Should().BeTrue(
                "a complete empty snapshot must not inherit choices from an earlier drawing");
        }

        [Fact]
        public void ApplySelection_IgnoresNonPositiveTolerance()
        {
            var p = EmptyProfile();
            ProjectSetupService.ApplySelection(p, new ProjectSetupSelection
            {
                ClLayers = { "X" },
                IntersectionToleranceM = 0,
            });
            p.Sections.Cl.IntersectionToleranceM.Should().BeNull(
                "0 means exact geometric intersection, not a configured tolerance");
        }

        [Fact]
        public void ApplySelection_ClearsOutOfRangeToleranceInsteadOfKeepingTheOldOne()
        {
            var p = EmptyProfile();
            p.Sections.Cl.IntersectionToleranceM = 0.25;

            ProjectSetupService.ApplySelection(p, new ProjectSetupSelection
            {
                ClLayers = { "X" },
                IntersectionToleranceM = 5.01,
            });

            p.Sections.Cl.IntersectionToleranceM.Should().BeNull();
        }

        [Fact]
        public void ApplySelection_DeduplicatesLayersCaseInsensitively()
        {
            var p = EmptyProfile();
            ProjectSetupService.ApplySelection(p, new ProjectSetupSelection
            {
                ClLayers = { "CL-SEC", " cl-sec ", "CL-SEC" },
            });
            p.Sections.Cl.LayerPatterns.Should().ContainSingle().Which.Should().Be("CL-SEC");
        }

        [Fact]
        public void Save_RejectsOutOfScanChoiceBeforeMutation()
        {
            var p = EmptyProfile();
            p.Sections.Cl.LayerPatterns.Add("OLD-CL");
            var scan = SetupScan(p.ProfileId, "REAL-CL");
            var selection = new ProjectSetupSelection
            {
                ClLayers = { "INVENTED-CL" },
                ApprovedBy = "nataly",
            };

            var act = () => ProjectSetupService.SaveCoreForContractTests(
                p, selection, scan, Path.Combine(Path.GetTempPath(), "unused-profile.yaml"));

            act.Should().Throw<InvalidOperationException>();
            p.Sections.Cl.LayerPatterns.Should().Equal("OLD-CL");
        }

        [Fact]
        public void Save_WriteFailureRestoresCompleteSetupSnapshot()
        {
            var p = EmptyProfile();
            p.Sections.Cl.LayerPatterns.Add("OLD-CL");
            p.Sections.Cl.SourceFiles.Add("old.dwg");
            p.Sections.Alignments.AllowedNames.Add("OLD-ALIGN");
            p.Sections.Sources.SampledSourceRules.Add(new()
            {
                Name = "OLD-SURFACE", Kind = "surface", NamePattern = "OLD-*", Required = true,
            });
            p.Sections.Cl.IntersectionToleranceM = 0.5;
            var scan = SetupScan(p.ProfileId, "NEW-CL", "NEW-ALIGN", "NEW-SURFACE");
            var selection = new ProjectSetupSelection
            {
                ClLayers = { "NEW-CL" },
                ClSourceFiles = { "new.dwg" },
                AllowedAlignments = { "NEW-ALIGN" },
                SampledSources = { ["NEW-SURFACE"] = "surface" },
                IntersectionToleranceM = 0.25,
                ApprovedBy = "nataly",
            };
            var existingDirectory = Path.Combine(
                Path.GetTempPath(), "mcd-setup-write-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(existingDirectory);
            try
            {
                var act = () => ProjectSetupService.SaveCoreForContractTests(
                    p, selection, scan, existingDirectory);
                act.Should().Throw<Exception>();
                p.Sections.Cl.LayerPatterns.Should().Equal("OLD-CL");
                p.Sections.Cl.SourceFiles.Should().Equal("old.dwg");
                p.Sections.Alignments.AllowedNames.Should().Equal("OLD-ALIGN");
                p.Sections.Sources.SampledSourceRules.Should().ContainSingle(rule =>
                    rule.Name == "OLD-SURFACE" && rule.NamePattern == "OLD-*");
                p.Sections.Cl.IntersectionToleranceM.Should().Be(0.5);
            }
            finally
            {
                Directory.Delete(existingDirectory, true);
            }
        }

        [Fact]
        public void Save_RejectsIncompleteDiscoveryBeforeProfileMutation()
        {
            var p = EmptyProfile();
            p.Sections.Cl.LayerPatterns.Add("OLD-CL");
            var scan = SetupScan(p.ProfileId, "NEW-CL");
            scan.ScanComplete = false;
            scan.Findings.Add(new DeliveryFinding
            {
                Code = SectionFindingCodes.SetupScanIncomplete,
                Domain = "sections",
                Severity = FindingSeverity.Error,
                Title = "read failed",
            });

            var act = () => ProjectSetupService.SaveCoreForContractTests(
                p, new ProjectSetupSelection
                {
                    ClLayers = { "NEW-CL" },
                    ApprovedBy = "nataly",
                }, scan, Path.Combine(Path.GetTempPath(), "unused-incomplete-profile.yaml"));

            act.Should().Throw<InvalidOperationException>();
            p.Sections.Cl.LayerPatterns.Should().Equal("OLD-CL");
        }

        [Fact]
        public void Save_RejectsNoClSelectionBeforeMutation()
        {
            var p = EmptyProfile();
            p.Sections.Cl.LayerPatterns.Add("OLD-CL");
            p.Sections.Sources.SampledSourceRules.Add(new()
            {
                Name = "OLD-SURFACE", Kind = "surface", Required = true,
            });
            var scan = SetupScan(p.ProfileId, "NEW-CL", source: "NEW-SURFACE");
            var selection = new ProjectSetupSelection
            {
                SampledSources = { ["NEW-SURFACE"] = "surface" },
                ApprovedBy = "nataly",
            };

            var act = () => ProjectSetupService.SaveCoreForContractTests(
                p, selection, scan,
                Path.Combine(Path.GetTempPath(), "unused-no-cl-profile.yaml"));

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*CL layer*");
            p.Sections.Cl.LayerPatterns.Should().Equal("OLD-CL");
            p.Sections.Sources.SampledSourceRules.Should().ContainSingle(rule =>
                rule.Name == "OLD-SURFACE");
        }

        [Fact]
        public void UnknownDrawingSeed_CanBecomeOneConfiguredProfileWithout6422Bleed()
        {
            var selectionIdentity = ActiveProjectProfileService.SelectForDrawing(
                @"C:\Projects\Greenfield\Site.dwg", null, null, null);
            var generated = ActiveProjectProfileService.CreateUnconfiguredResult(
                selectionIdentity);
            var profile = generated.Profile!;
            var target = Path.Combine(
                Path.GetTempPath(), "mcd-generated-setup-" + Guid.NewGuid().ToString("N"),
                "project-profile.yaml");
            try
            {
                var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(
                    profile, generated.ProfileHash!, target);
                ProjectProfileWriter.RequireGeneratedSourceUnchanged(profile, expected);
                var scan = SetupScan(profile.ProfileId, "CL-SITE", "SITE-ALIGN", "SITE-EG");
                var approved = new ProjectSetupSelection
                {
                    ClLayers = { "CL-SITE" },
                    AllowedAlignments = { "SITE-ALIGN" },
                    SampledSources = { ["SITE-EG"] = "surface" },
                    ApprovedBy = "nataly",
                };

                ProjectSetupService.SaveCoreForContractTests(
                    profile, approved, scan, target, expected);

                var reloaded = ProjectProfileLoader.LoadFromFile(target);
                reloaded.IsUsable.Should().BeTrue();
                reloaded.Profile!.ProfileId.Should().Be(profile.ProfileId);
                reloaded.Profile.ProfileId.Should().NotBe("6422");
                reloaded.Profile.Sections.Cl.LayerPatterns.Should().Equal("CL-SITE");
                reloaded.Profile.Sections.Sources.SampledSourceRules
                    .Should().ContainSingle(rule => rule.Name == "SITE-EG");
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(target)!, true); } catch { }
            }
        }

        [Fact]
        public void SetupSessionSnapshotCannotBeCrossedWithLaterSectionProfile()
        {
            var setupProfile = new ProjectProfile { ProfileId = "setup-a" };
            var setupScan = SetupScan(setupProfile.ProfileId, "CL-A");
            var setupPath = Path.Combine(Path.GetTempPath(), "setup-a.yaml");
            CivilDeliverySession.SetSetupScan(setupScan, setupProfile, "hash-a", setupPath);

            var sectionProfile = new ProjectProfile { ProfileId = "sections-b" };
            CivilDeliverySession.SetPlan(new SectionPlan
            {
                RunId = "plan-b",
                ProjectProfileId = sectionProfile.ProfileId,
            }, sectionProfile, "hash-b", Path.Combine(Path.GetTempPath(), "sections-b.yaml"));

            var setup = CivilDeliverySession.GetSetupContext();
            setup.Scan.Should().BeSameAs(setupScan);
            setup.Profile.Should().BeSameAs(setupProfile);
            setup.ProfileHash.Should().Be("hash-a");
            setup.ProfileWriteTarget.Should().Be(setupPath);
        }

        private static ProjectSetupScan SetupScan(
            string profileId, string layer, string? alignment = null, string? source = null)
        {
            var scan = new ProjectSetupScan
            {
                RunId = Guid.NewGuid().ToString("N"),
                ProjectProfileId = profileId,
            };
            scan.ClLayerCandidates.Add(new ClLayerCandidate { Layer = layer });
            if (alignment != null)
                scan.Alignments.Add(new AlignmentCandidateSummary
                {
                    Name = alignment,
                    StartStation = 0,
                    EndStation = 1,
                    Length = 1,
                });
            if (source != null)
                scan.Sources.Add(new SourceCandidateSummary
                {
                    Name = source,
                    Kind = "surface",
                });
            return scan;
        }
    }

    public class ProjectProfileWriterTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "mcd-profile-tests", Guid.NewGuid().ToString("N"));

        public ProjectProfileWriterTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private ProjectProfile Configured()
        {
            var p = new ProjectProfile { ProfileId = "6422", ProjectName = "נת\"צ מודיעין" };
            p.Sections.Cl.LayerPatterns.Add("CL-SEC");
            p.Sections.Cl.SourceFiles.Add("CL.dwg");
            p.Sections.Alignments.AllowedNames.Add("NATZ-MAIN");
            p.Sections.Sources.SampledSourceRules.Add(
                new ProjectProfile.SectionsProfile.SourcesProfile.SourceRule
                { Name = "EG", Kind = "surface", NamePattern = "EG", Required = true });
            return p;
        }

        [Fact]
        public void Save_RoundTripsThroughTheRealLoader()
        {
            var path = Path.Combine(_dir, "project-profile.yaml");
            var profile = Configured();
            var saved = ProfileCasTest.Save(profile, path, "setup test", "nataly");

            saved.NewVersion.Should().Be(2, "version bumps on every approved change");
            File.Exists(path).Should().BeTrue();

            var reloaded = ProjectProfileLoader.LoadFromFile(path);
            reloaded.IsUsable.Should().BeTrue(because: string.Join("; ",
                reloaded.Findings.Select(f => f.Code)));
            reloaded.Profile!.ProfileId.Should().Be("6422");
            reloaded.Profile.Sections.Cl.LayerPatterns.Should().BeEquivalentTo(new[] { "CL-SEC" });
            reloaded.Profile.Sections.Alignments.AllowedNames.Should().BeEquivalentTo(new[] { "NATZ-MAIN" });
            reloaded.Profile.Sections.Sources.SampledSourceRules.Should().ContainSingle(r => r.Name == "EG");
            reloaded.Profile.Provenance.ApprovedBy.Should().Be("nataly");
        }

        [Fact]
        public void Save_RecordsProvenanceAndHashes()
        {
            var path = Path.Combine(_dir, "project-profile.yaml");
            var profile = Configured();
            var saved = ProfileCasTest.Save(profile, path, "chose CL layers", "arthur",
                new Dictionary<string, string> { ["CL.dwg"] = "abc123" });

            var text = File.ReadAllText(path);
            text.Should().Contain("chose CL layers");
            text.Should().Contain("arthur");
            text.Should().Contain("abc123");
            saved.NewHash.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public void Save_RoundTripsExactCrossingAndExplicitExclusionDecisions()
        {
            var p = Configured();
            var at = new DateTime(2026, 9, 1, 9, 30, 0, DateTimeKind.Utc);
            p.Sections.Decisions.Crossings.Add(new
                ProjectProfile.SectionsProfile.DecisionsProfile.CrossingDecision
                {
                    SourceDrawingHash = "sha-cl",
                    SourceHandle = "A1",
                    AlignmentName = "MAIN",
                    Station = 1086.125,
                    ApprovedBy = "nataly",
                    ApprovedAtUtc = at,
                });
            p.Sections.Decisions.Exclusions.Add(new
                ProjectProfile.SectionsProfile.DecisionsProfile.ExclusionDecision
                {
                    SourceDrawingHash = "sha-cl",
                    SourceHandle = "A2",
                    FindingCode = SectionFindingCodes.ClNoIntersection,
                    Reason = "outside this delivery",
                    ApprovedBy = "nataly",
                    ApprovedAtUtc = at,
                });
            var path = Path.Combine(_dir, "project-profile.yaml");

            ProfileCasTest.Save(p, path, "section decisions", "nataly");
            var loaded = ProjectProfileLoader.LoadFromFile(path);

            loaded.IsUsable.Should().BeTrue(because: string.Join("; ", loaded.Findings.Select(f => f.Code)));
            loaded.Profile!.Sections.Decisions.Crossings.Should().ContainSingle(d =>
                d.SourceHandle == "A1" && d.AlignmentName == "MAIN" && d.Station == 1086.125);
            loaded.Profile.Sections.Decisions.Exclusions.Should().ContainSingle(d =>
                d.SourceHandle == "A2" && d.Reason == "outside this delivery" &&
                d.ApprovedBy == "nataly" && d.ApprovedAtUtc == at);
        }

        [Fact]
        public void Save_BacksUpThePreviousFile()
        {
            var path = Path.Combine(_dir, "project-profile.yaml");
            var firstProfile = Configured();
            ProfileCasTest.Save(firstProfile, path, "first", "arthur");
            var secondProfile = Configured();
            var second = ProfileCasTest.Save(secondProfile, path, "second", "arthur");

            second.BackupPath.Should().NotBeNullOrEmpty();
            File.Exists(second.BackupPath).Should().BeTrue("the previous approved configuration is never lost");
        }

        [Fact]
        public void Save_StaleExpectedStateDoesNotOverwriteTargetOrMutateProvenance()
        {
            var path = Path.Combine(_dir, "cas-project-profile.yaml");
            var initialProfile = Configured();
            var initial = ProfileCasTest.Save(
                initialProfile, path, "initial", "arthur");
            var expected = ProjectProfileWriter.CaptureExpectedState(
                path, initial.NewHash, path);

            // Represents a different workflow publishing a newer approved profile
            // after this workflow captured its decision basis.
            File.AppendAllText(path, "\n# concurrent newer decision\n");
            var concurrentBytes = File.ReadAllText(path);
            var staleProfile = Configured();
            var beforeVersion = staleProfile.Provenance.Version;
            var beforeApprover = staleProfile.Provenance.ApprovedBy;

            var act = () => ProjectProfileWriter.Save(
                staleProfile, path, "stale decision", "nataly",
                expectedState: expected);

            act.Should().Throw<InvalidOperationException>();
            File.ReadAllText(path).Should().Be(concurrentBytes);
            staleProfile.Provenance.Version.Should().Be(beforeVersion);
            staleProfile.Provenance.ApprovedBy.Should().Be(beforeApprover);
        }

        [Fact]
        public void ConcurrentPublishersWithSameExpectedHash_ExactlyOneWins()
        {
            var path = Path.Combine(_dir, "concurrent-project-profile.yaml");
            var baseline = Configured();
            var initial = ProfileCasTest.Save(baseline, path, "initial", "arthur");
            var expected = ProjectProfileWriter.CaptureExpectedState(
                path, initial.NewHash, path);
            var first = Configured();
            first.ProjectName = "candidate-a";
            var second = Configured();
            second.ProjectName = "candidate-b";
            using var start = new ManualResetEventSlim(false);

            Task<Exception?> Attempt(ProjectProfile candidate, string approver) =>
                Task.Run(() =>
                {
                    start.Wait();
                    try
                    {
                        ProjectProfileWriter.Save(
                            candidate, path, "concurrent decision", approver, expected);
                        return null;
                    }
                    catch (Exception ex) { return ex; }
                });

            var attempts = new[] { Attempt(first, "a"), Attempt(second, "b") };
            start.Set();
            Task.WaitAll(attempts);

            attempts.Count(task => task.Result == null).Should().Be(1);
            attempts.Count(task => task.Result is InvalidOperationException).Should().Be(1);
            ProjectProfileLoader.LoadFromFile(path).Profile!.ProjectName
                .Should().BeOneOf("candidate-a", "candidate-b");
        }

        [Fact]
        public void FailedEvidenceRestore_RefusesToOverwriteNewerConcurrentSave()
        {
            var path = Path.Combine(_dir, "restore-race-profile.yaml");
            var baseline = Configured();
            ProfileCasTest.Save(baseline, path, "initial", "arthur");
            var decision = Configured();
            var decisionSave = ProfileCasTest.Save(
                decision, path, "decision awaiting evidence", "nataly");
            var newer = Configured();
            newer.ProjectName = "newer-authority";
            ProfileCasTest.Save(newer, path, "newer concurrent decision", "other");
            var newerBytes = File.ReadAllText(path);

            var act = () => ProjectProfileWriter.RestoreAfterFailedPublication(decisionSave);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*changed after the decision save*");
            File.ReadAllText(path).Should().Be(newerBytes);
        }

        [Fact]
        public void Save_RefusesRuntimeTargetThatAppearedAfterRepositorySourceWasLoaded()
        {
            var source = Path.Combine(_dir, "repo-profile.yaml");
            var target = Path.Combine(_dir, "runtime", "project-profile.yaml");
            var sourceProfile = Configured();
            var sourceSave = ProfileCasTest.Save(
                sourceProfile, source, "repository default", "arthur");
            var expected = ProjectProfileWriter.CaptureExpectedState(
                source, sourceSave.NewHash, target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "newer runtime profile");

            var act = () => ProjectProfileWriter.Save(
                Configured(), target, "stale first runtime decision", "nataly",
                expectedState: expected);

            act.Should().Throw<InvalidOperationException>();
            File.ReadAllText(target).Should().Be("newer runtime profile");
        }

        [Fact]
        public void GeneratedSeed_RefusesConcurrentRuntimeTargetAppearance()
        {
            var target = Path.Combine(_dir, "generated", "project-profile.yaml");
            var seed = new ProjectProfile
            {
                ProfileId = "drawing-site-safe",
                ProjectName = "Site",
            };
            var seedHash = ArtifactHash.Sha256OfText(
                JsonSerializer.Serialize(seed, SectionsWorkflowService.Json));
            var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(
                seed, seedHash, target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "concurrent approved profile");

            var act = () => ProjectProfileWriter.Save(
                seed, target, "generated setup", "nataly",
                expectedState: expected);

            act.Should().Throw<InvalidOperationException>();
            File.ReadAllText(target).Should().Be("concurrent approved profile");
        }

        [Fact]
        public void GeneratedSeed_MutationAfterDiscoveryIsRejectedBeforeApproval()
        {
            var target = Path.Combine(_dir, "generated-stale", "project-profile.yaml");
            var seed = new ProjectProfile
            {
                ProfileId = "drawing-site-safe",
                ProjectName = "Site",
            };
            var seedHash = ArtifactHash.Sha256OfText(
                JsonSerializer.Serialize(seed, SectionsWorkflowService.Json));
            var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(
                seed, seedHash, target);
            seed.Sections.Cl.LayerPatterns.Add("MUTATED-BEHIND-REVIEW");

            var act = () => ProjectProfileWriter.RequireGeneratedSourceUnchanged(
                seed, expected);

            act.Should().Throw<InvalidOperationException>();
            File.Exists(target).Should().BeFalse();
        }

        [Fact]
        public void Save_RefusesWithoutApprover()
        {
            var path = Path.Combine(_dir, "project-profile.yaml");
            var profile = Configured();
            var act = () => ProjectProfileWriter.Save(
                profile, path, "no approver", "  ", ProfileCasTest.For(profile, path));
            act.Should().Throw<ArgumentException>("configuration is an engineering decision, not a default");
        }

        [Fact]
        public void SavedProfile_ProducesNoNeedForSetup()
        {
            var path = Path.Combine(_dir, "project-profile.yaml");
            var profile = Configured();
            ProfileCasTest.Save(profile, path, "setup", "nataly");

            var reloaded = ProjectProfileLoader.LoadFromFile(path);
            ProjectSetupService.NeedsSetup(reloaded.Profile!).Should().BeFalse(
                "after setup the product must go straight to PLAN");
        }
    }

    public class StageLogTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "mcd-stagelog-tests", Guid.NewGuid().ToString("N"));

        public StageLogTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void OpenStage_IsFlushedBeforeTheRiskyCall()
        {
            string path;
            using (var log = new StageLog("hang-test", _dir))
            {
                path = log.Path_;
                log.Begin("civil.sampleline_create", "MCD-1086");

                // Simulates reading the log from OUTSIDE while the process is blocked
                // inside the Civil API call that never returns.
                var live = File.ReadAllText(path);
                live.Should().Contain("BEGIN civil.sampleline_create");
                live.Should().NotContain("END   civil.sampleline_create");
                log.OpenStage.Should().Be("civil.sampleline_create");
            }
        }

        [Fact]
        public void Dispose_MarksAnUnclosedStageAsAborted()
        {
            string path;
            using (var log = new StageLog("abort-test", _dir))
            {
                path = log.Path_;
                log.Begin("civil.hanging_stage");
            }
            File.ReadAllText(path).Should().Contain("ABORT stage still open at dispose: civil.hanging_stage");
        }

        [Fact]
        public void Step_RecordsFailureWithException()
        {
            using var log = new StageLog("fail-test", _dir);
            var act = () => log.Step("civil.boom", () => throw new InvalidOperationException("nope"));
            act.Should().Throw<InvalidOperationException>();

            File.ReadAllText(log.Path_).Should().Contain("FAIL  civil.boom | InvalidOperationException: nope");
        }

        [Fact]
        public void Log_IsReadableWhileOpen()
        {
            using var log = new StageLog("share-test", _dir);
            log.Info("first");
            using var reader = new FileStream(log.Path_, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            reader.Length.Should().BeGreaterThan(0, "the log must stay readable while Civil holds it");
        }
    }
}
