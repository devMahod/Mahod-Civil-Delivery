using System;
using System.IO;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public sealed class EstimateEarthworksDecisionTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "mcd-earthworks-decision-tests", Guid.NewGuid().ToString("N"));

        public EstimateEarthworksDecisionTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void DecisionState_IsResolvedOnlyWithCompleteAuthorityAndExclusionReason()
        {
            var profile = EstimateFixtures.Profile();

            EstimateWorkflowService.GetEarthworksDecision(profile).Status
                .Should().Be(EstimateWorkflowService.EarthworksDecisionStatus.Unresolved);

            profile.Estimate.Earthworks.Requested = true;
            EstimateWorkflowService.GetEarthworksDecision(profile).Status
                .Should().Be(EstimateWorkflowService.EarthworksDecisionStatus.Invalid,
                    "a boolean without named authority and UTC evidence is not a decision");

            profile.Estimate.Earthworks.DecidedBy = "nataly";
            profile.Estimate.Earthworks.DecidedAtUtc = DateTime.UtcNow;
            EstimateWorkflowService.GetEarthworksDecision(profile).Status
                .Should().Be(EstimateWorkflowService.EarthworksDecisionStatus.Included);

            profile.Estimate.Earthworks.Requested = false;
            EstimateWorkflowService.GetEarthworksDecision(profile).Status
                .Should().Be(EstimateWorkflowService.EarthworksDecisionStatus.Invalid,
                    "out-of-scope also requires an engineering reason");

            profile.Estimate.Earthworks.Reason = "אומדן מוקדם לעבודות פיתוח בלבד";
            var excluded = EstimateWorkflowService.GetEarthworksDecision(profile);
            excluded.Status.Should().Be(EstimateWorkflowService.EarthworksDecisionStatus.Excluded);
            excluded.DisplayText.Should().Contain("מחוץ להיקף").And.Contain("nataly");
        }

        [Fact]
        public void RuntimeCollector_SkipsEarthworksUntilTheExplicitDecisionIsResolved()
        {
            var source = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "Estimate", "CorridorQuantityService.cs"));
            var start = source.IndexOf(
                "var earthworksDecision = EstimateWorkflowService.GetEarthworksDecision(profile)",
                StringComparison.Ordinal);
            var end = source.IndexOf("private static", start, StringComparison.Ordinal);
            var earthworksPass = source.Substring(start, end - start);

            earthworksPass.Should().Contain("EarthworksDecisionStatus.Excluded")
                .And.Contain("EarthworksDecisionStatus.Included")
                .And.Contain("EarthworksSourceUnverifiedCode")
                .And.Contain("EarthworksNotAssessedCode")
                .And.Contain("עבודות עפר טרם נבדקו — לא אפס ולא הוחרגו");
        }

        [Fact]
        public void SaveDecision_PersistsExplicitIncludeWithApproverAndUtcTimestamp()
        {
            var profile = EstimateFixtures.Profile();
            var path = Path.Combine(_dir, "project-profile.yaml");
            var before = DateTime.UtcNow;

            var saved = new EstimateWorkflowService().SaveEarthworksDecision(
                profile, includeEarthworks: true, reason: null,
                approvedBy: "nataly", targetPath: path);

            var loaded = ProjectProfileLoader.LoadFromFile(path).Profile!;
            var decision = EstimateWorkflowService.GetEarthworksDecision(loaded);
            decision.Status.Should().Be(EstimateWorkflowService.EarthworksDecisionStatus.Included);
            decision.DecidedBy.Should().Be("nataly");
            decision.DecidedAtUtc.Should().NotBeNull();
            decision.DecidedAtUtc!.Value.ToUniversalTime().Should().BeOnOrAfter(before);
            loaded.Estimate.Earthworks.Requested.Should().BeTrue();
            saved.NewHash.Should().Be(ProjectProfileLoader.LoadFromFile(path).ProfileHash);
        }

        [Fact]
        public void SaveDecision_ExclusionRequiresReasonAndWritesNothing()
        {
            var profile = EstimateFixtures.Profile();
            var path = Path.Combine(_dir, "must-not-exist.yaml");

            var act = () => new EstimateWorkflowService().SaveEarthworksDecision(
                profile, includeEarthworks: false, reason: "  ",
                approvedBy: "nataly", targetPath: path);

            act.Should().Throw<ArgumentException>().WithMessage("*engineering reason*");
            File.Exists(path).Should().BeFalse();
            profile.Estimate.Earthworks.Requested.Should().BeNull();
            profile.Estimate.Earthworks.DecidedBy.Should().BeNull();
            profile.Estimate.Earthworks.DecidedAtUtc.Should().BeNull();
        }

        [Fact]
        public void SaveDecision_WriteFailureRollsBackDecisionAndProvenanceInMemory()
        {
            var profile = EstimateFixtures.Profile();
            profile.Provenance.Version = 7;
            profile.Provenance.ApprovedBy = "previous";
            profile.Provenance.Source = "previous source";
            profile.Estimate.Earthworks.Requested = true;
            profile.Estimate.Earthworks.DecidedBy = "previous";
            profile.Estimate.Earthworks.DecidedAtUtc = new DateTime(2026, 8, 1, 1, 2, 3, DateTimeKind.Utc);

            // A directory cannot become the profile file. The atomic writer reaches
            // the final move, fails, removes its temporary file and rolls everything back.
            var act = () => new EstimateWorkflowService().SaveEarthworksDecision(
                profile, includeEarthworks: false, reason: "מחוץ לחוזה הנוכחי",
                approvedBy: "nataly", targetPath: _dir);

            act.Should().Throw<Exception>();
            profile.Estimate.Earthworks.Requested.Should().BeTrue();
            profile.Estimate.Earthworks.DecidedBy.Should().Be("previous");
            profile.Estimate.Earthworks.Reason.Should().BeNull();
            profile.Provenance.Version.Should().Be(7);
            profile.Provenance.ApprovedBy.Should().Be("previous");
            profile.Provenance.Source.Should().Be("previous source");
            Directory.GetFiles(Path.GetDirectoryName(_dir)!, Path.GetFileName(_dir) + ".tmp-*")
                .Should().BeEmpty("failed atomic saves clean their same-directory temporary file");
        }

        [Fact]
        public void AtomicReplacement_PreservesExactPreviousFileAsBackup()
        {
            var path = Path.Combine(_dir, "existing-profile.yaml");
            const string previousBytes = "previous profile bytes\nthat must remain recoverable";
            File.WriteAllText(path, previousBytes);
            var profile = EstimateFixtures.Profile();

            var saved = new EstimateWorkflowService().SaveEarthworksDecision(
                profile, includeEarthworks: false,
                reason: "החוזה הנוכחי אינו כולל עבודות עפר",
                approvedBy: "nataly", targetPath: path);

            saved.BackupPath.Should().NotBeNullOrWhiteSpace();
            File.ReadAllText(saved.BackupPath).Should().Be(previousBytes);
            ProjectProfileLoader.LoadFromFile(path).Profile!.Estimate.Earthworks.Requested
                .Should().BeFalse();
            Directory.GetFiles(_dir, "*.tmp-*").Should().BeEmpty();
        }

        [Fact]
        public void CandidateCode_DoesNotRemoveWorkFromPendingMappingTotals()
        {
            var profile = EstimateFixtures.Profile();
            var record = EstimateFixtures.Record(
                "r1", "U51.01.0250", 12, "מטר",
                ruleKey: "layer:KERB|length", layer: "KERB");

            EstimateWorkflowService.HasApprovedCatalogMapping(profile, record).Should().BeFalse(
                "a candidate/default code is a suggestion until it has approver+timestamp");

            profile.Estimate.QuantitySources.Rules.Add(
                new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
                {
                    RuleKey = "layer:KERB|length",
                    LayerPattern = "KERB",
                    EntityType = "LWPOLYLINE",
                    MeasurementKind = "length",
                    CandidateCatalogCode = "U51.01.0250",
                    ExpectedUnit = "מטר",
                    ApprovedBy = "nataly",
                    ApprovedAtUtc = DateTime.UtcNow,
                });
            EstimateFixtures.BindApprovalToActiveCatalog(
                profile.Estimate.QuantitySources.Rules[^1]);

            EstimateWorkflowService.HasApprovedCatalogMapping(profile, record).Should().BeTrue();
        }

        [Fact]
        public void MappingBatch_ValidatesEverythingBeforeMutationAndRollsBackFailedAtomicWrite()
        {
            var valid = new EstimateWorkflowService.MappingApproval(
                "layer:KERB|length", "U51.01.0250", "KERB", "LWPOLYLINE", "length", "מטר");
            var invalid = valid with { RuleKey = "layer:OTHER|length", CatalogCode = "U99.99.9999" };

            var invalidProfile = EstimateFixtures.Profile();
            var invalidPath = Path.Combine(_dir, "invalid-batch.yaml");
            var invalidAct = () => new EstimateWorkflowService().SaveApprovedMappings(
                invalidProfile, EstimateFixtures.Snapshot(), new[] { valid, invalid },
                "nataly", targetPath: invalidPath);

            invalidAct.Should().Throw<InvalidOperationException>();
            invalidProfile.Estimate.QuantitySources.Rules.Should().BeEmpty(
                "the first valid approval is not applied before the full batch validates");
            File.Exists(invalidPath).Should().BeFalse();

            var writeFailureProfile = EstimateFixtures.Profile();
            var writeAct = () => new EstimateWorkflowService().SaveApprovedMappings(
                writeFailureProfile, EstimateFixtures.Snapshot(), new[] { valid },
                "nataly", targetPath: _dir);

            writeAct.Should().Throw<Exception>();
            writeFailureProfile.Estimate.QuantitySources.Rules.Should().BeEmpty(
                "a failed durable save must not leave a mapping approved in memory");
        }

        [Fact]
        public void PaletteExposesDecisionAndInvalidatesEveryDependentEstimateArtifact()
        {
            var root = EstimateFixtures.RepoRoot();
            var xaml = File.ReadAllText(Path.Combine(
                root, "MahodAI.Civil3D.Plugin", "CivilDelivery", "UI", "CivilDeliveryControl.xaml"));
            var source = File.ReadAllText(Path.Combine(
                root, "MahodAI.Civil3D.Plugin", "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));

            xaml.Should().Contain("BtnEarthworksDecision")
                .And.Contain("OnEarthworksDecision")
                .And.Contain("EarthworksDecisionState");

            var start = source.IndexOf("private void OnEarthworksDecision", StringComparison.Ordinal);
            var end = source.IndexOf("private void OnScan", start, StringComparison.Ordinal);
            start.Should().BeGreaterThan(0);
            end.Should().BeGreaterThan(start);
            var handler = source.Substring(start, end - start);
            handler.Should().Contain("SaveEarthworksDecision")
                .And.Contain("InvalidateEstimateEvidence(")
                .And.Contain("ReloadProfile();");

            var potentialStart = source.IndexOf("private string UnmappedPotentialText", StringComparison.Ordinal);
            var potentialEnd = source.IndexOf("private static Document", potentialStart, StringComparison.Ordinal);
            source.Substring(potentialStart, potentialEnd - potentialStart)
                .Should().Contain("HasApprovedCatalogMapping")
                .And.NotContain("string.IsNullOrEmpty(r.Classification.CandidateCatalogCode)");
        }
    }
}
