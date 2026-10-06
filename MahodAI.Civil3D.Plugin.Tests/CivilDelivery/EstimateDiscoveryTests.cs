using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Discovery mode contract: an unconfigured estimate profile must produce real
    /// measured quantities marked UNMAPPED — never a misleading "0 quantities".
    /// </summary>
    public class EstimateDiscoveryContractTests
    {
        private static NeutralQuantityRecord Discovered(
            string handle, string layer, double value, string unit, string kind)
        {
            var record = new NeutralQuantityRecord
            {
                RecordId = $"q-disc-{handle}",
                ProjectProfileId = "6422",
                RunId = "run-disc",
                Source = new QuantitySource
                {
                    Drawing = "PD.dwg",
                    DrawingHash = "hash",
                    Handle = handle,
                    EntityType = "LWPOLYLINE",
                    Layer = layer,
                },
                Measurement = new QuantityMeasurement
                {
                    Kind = kind,
                    Method = "polyline-length",
                    RawValue = value,
                    Unit = unit,
                },
                Classification = new QuantityClassification
                {
                    RuleKey = $"layer:{layer}|{kind}",
                    CandidateCatalogCode = null,
                    Tags = { "discovered" },
                },
                Status = DeliveryStatus.ReviewRequired,
            };
            record.Findings.Add(new DeliveryFinding
            {
                Code = EstimateFindingCodes.Unmapped,
                Domain = "estimate",
                Severity = FindingSeverity.ReviewRequired,
                Title = "no catalog mapping",
            });
            return record;
        }

        [Fact]
        public void DiscoveredRecords_BecomeVisibleReviewLines_NotZero()
        {
            var records = new[]
            {
                Discovered("A1", "KERB", 250.0, "מטר", "length"),
                Discovered("A2", "KERB", 130.0, "מטר", "length"),
            };

            var result = EstimateBuilder.Build(records, EstimateFixtures.Snapshot(), EstimateFixtures.Profile());

            result.Lines.Should().HaveCount(2);
            result.Lines.Should().OnlyContain(l => l.PriceStatus == PriceStatus.Unmapped);
            result.Lines.Should().OnlyContain(l => l.Status == DeliveryStatus.ReviewRequired);
            result.Lines.Select(l => l.BoqQuantity).Should().BeEquivalentTo(new[] { 250.0, 130.0 },
                "the measured quantity survives even without a mapping");
            result.ExcludedLineCount.Should().Be(2);
            result.CleanTotal.Should().Be(0, "unmapped work contributes no priced total");
            result.Status.Should().Be(DeliveryStatus.ReviewRequired);
        }

        [Fact]
        public void DiscoveredRecords_GroupByRuleKeyForApproval()
        {
            var records = new[]
            {
                Discovered("A1", "KERB", 250.0, "מטר", "length"),
                Discovered("A2", "KERB", 130.0, "מטר", "length"),
                Discovered("B1", "MILLING", 900.0, "מ\"ר", "area"),
            };

            var groups = records.GroupBy(r => r.Classification.RuleKey).ToList();

            groups.Should().HaveCount(2);
            groups.Single(g => g.Key == "layer:KERB|length").Sum(r => r.Measurement.RawValue)
                .Should().Be(380.0);
        }

        [Fact]
        public void CountRuleKey_SeparatesDistinctBlockNamesOnTheSameLayer()
        {
            static QuantityMeasurement Count(string blockName) => new()
            {
                Kind = "count",
                Method = "block-count",
                RawValue = 1,
                Unit = "יח'",
                Parameters = { ["block_name"] = blockName },
            };

            var bench = CivilQuantityExtractionService.BuildDiscoveryRuleKey(
                "FURNITURE", Count(" Bench A "));
            var pole = CivilQuantityExtractionService.BuildDiscoveryRuleKey(
                "FURNITURE", Count("Light Pole"));
            var xrefBench = CivilQuantityExtractionService.BuildDiscoveryRuleKey(
                "OUTER|INNER|FURNITURE", Count("Bench A"));

            bench.Should().Be("layer:FURNITURE|count|block:BENCH_A");
            pole.Should().Be("layer:FURNITURE|count|block:LIGHT_POLE");
            xrefBench.Should().Be(bench,
                "an approval is owned by the supplier layer, not an attachment name");
            bench.Should().NotBe(pole);
        }

        [Fact]
        public void ApprovedCountRule_DoesNotFallBackToAnotherBlockOnTheSameLayer()
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.QuantitySources.Rules.Add(
                new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
                {
                    RuleKey = "layer:FURNITURE|count|block:BENCH_A",
                    LayerPattern = "FURNITURE",
                    MeasurementKind = "count",
                    CandidateCatalogCode = "U40.02.1265",
                    ExpectedUnit = "יח'",
                    ApprovedBy = "nataly",
                    ApprovedAtUtc = DateTime.UtcNow,
                });

            CivilQuantityExtractionService.FindApprovedRule(
                    profile, "layer:FURNITURE|count|block:LIGHT_POLE", "FURNITURE", "count")
                .Should().BeNull("one named block approval must not price every block on its layer");
        }

        [Fact]
        public void UnsupportedEntityCoverage_IsOneAggregateReviewBlocker()
        {
            var finding = CivilQuantityExtractionService.UnsupportedEntityCoverageFinding(
                new Dictionary<string, int>
                {
                    ["SOLID3D"] = 4,
                    ["CUSTOMENTITY"] = 2,
                }, "6422");

            finding.Should().NotBeNull();
            finding!.Code.Should().Be(EstimateFindingCodes.UnsupportedEntityCoverage);
            finding.Severity.Should().Be(FindingSeverity.ReviewRequired);
            finding.Message.Should().Contain("SOLID3D=4").And.Contain("CUSTOMENTITY=2");
            EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();
        }

        [Theory]
        [InlineData("DBText")]
        [InlineData("MText")]
        [InlineData("RotatedDimension")]
        [InlineData("MLeader")]
        [InlineData("Alignment")]
        [InlineData("TinSurface")]
        public void KnownAnnotationAndPresentationTypes_AreInformationalCoverage(string type)
        {
            CivilQuantityExtractionService.IsKnownNonQuantityEntityType(type).Should().BeTrue();
        }

        [Theory]
        [InlineData("ProfileDataBandLabelGroup")]
        [InlineData("AlignmentStationLabelGroup")]
        [InlineData("HorizontalGeometryBandLabelGroup")]
        [InlineData("VerticalGeometryBandLabelGroup")]
        [InlineData("ProfileCrestCurveLabelGroup")]
        [InlineData("ProfileLineLabelGroup")]
        [InlineData("ProfilePVILabelGroup")]
        [InlineData("ProfileSagCurveLabelGroup")]
        [InlineData("SuperelevationView")]
        [InlineData("AlignmentGeometryPointLabelGroup")]
        [InlineData("AlignmentStationEquationLabelGroup")]
        [InlineData("SampleLineGroup")]
        [InlineData("StationElevationLabel")]
        public void ConfirmedCivilLabelViewAndControllerTypes_UseExactInformationalWhitelist(string type)
        {
            CivilQuantityExtractionService.IsKnownNonQuantityEntityType(type).Should().BeTrue();
            CivilQuantityExtractionService.IsKnownNonQuantityEntityType(" " + type.ToLowerInvariant() + " ")
                .Should().BeTrue();
            CivilQuantityExtractionService.IsKnownNonQuantityEntityType("Custom" + type).Should().BeFalse();
            CivilQuantityExtractionService.IsKnownNonQuantityEntityType(type + "Custom").Should().BeFalse();
        }

        [Theory]
        [InlineData("SUBASSEMBLY")]
        [InlineData("DBPOINT")]
        [InlineData("ASSEMBLY")]
        [InlineData("ENTITY")]
        [InlineData("SITE")]
        [InlineData("UnknownLabelGroup")]
        [InlineData("UnknownView")]
        [InlineData("UnknownLabel")]
        public void UnconfirmedConstructionOrUnknownTypes_RemainCoverageBlockers(string type)
        {
            CivilQuantityExtractionService.IsKnownNonQuantityEntityType(type).Should().BeFalse();
            var finding = CivilQuantityExtractionService.UnsupportedEntityCoverageFinding(
                new Dictionary<string, int> { [type] = 1 }, "6422");
            EstimatePreflightPolicy.IsBlocking(finding!).Should().BeTrue();
        }

        [Fact]
        public void Native53September9Inventory_Reports287ConfirmedAnnotationsAndKeeps435UnknownBlocking()
        {
            // Exact unsupported-type inventory in the frozen native scan
            // estimate-extract-20260909-092731-3aab4b94/quantity_preflight.json.
            // Classifying presentation/controller objects is not an engineering
            // mapping, scope exclusion, measurement approval or deletion.
            var inventory = new Dictionary<string, int>
            {
                ["SUBASSEMBLY"] = 282, ["DBPOINT"] = 132,
                ["PROFILEDATABANDLABELGROUP"] = 100, ["ALIGNMENTSTATIONLABELGROUP"] = 38,
                ["HORIZONTALGEOMETRYBANDLABELGROUP"] = 20, ["VERTICALGEOMETRYBANDLABELGROUP"] = 20,
                ["ASSEMBLY"] = 19, ["PROFILECRESTCURVELABELGROUP"] = 18,
                ["PROFILELINELABELGROUP"] = 18, ["PROFILEPVILABELGROUP"] = 18,
                ["PROFILESAGCURVELABELGROUP"] = 18, ["SUPERELEVATIONVIEW"] = 14,
                ["ALIGNMENTGEOMETRYPOINTLABELGROUP"] = 10, ["ALIGNMENTSTATIONEQUATIONLABELGROUP"] = 10,
                ["SAMPLELINEGROUP"] = 2, ["ENTITY"] = 1, ["SITE"] = 1,
                ["STATIONELEVATIONLABEL"] = 1,
            };
            var known = inventory.Where(pair => CivilQuantityExtractionService.IsKnownNonQuantityEntityType(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var unknown = inventory.Where(pair => !CivilQuantityExtractionService.IsKnownNonQuantityEntityType(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            inventory.Values.Sum().Should().Be(722);
            known.Values.Sum().Should().Be(287);
            unknown.Values.Sum().Should().Be(435);
            unknown.Should().BeEquivalentTo(new Dictionary<string, int>
            {
                ["SUBASSEMBLY"] = 282, ["DBPOINT"] = 132, ["ASSEMBLY"] = 19, ["ENTITY"] = 1, ["SITE"] = 1,
            });
            var info = CivilQuantityExtractionService.KnownNonQuantityCoverageFinding(known, "6422")!;
            var blocker = CivilQuantityExtractionService.UnsupportedEntityCoverageFinding(unknown, "6422")!;
            info.Severity.Should().Be(FindingSeverity.Info);
            EstimatePreflightPolicy.IsBlocking(info).Should().BeFalse();
            blocker.Severity.Should().Be(FindingSeverity.ReviewRequired);
            EstimatePreflightPolicy.IsBlocking(blocker).Should().BeTrue();
            foreach (var pair in known) info.Message.Should().Contain($"{pair.Key}={pair.Value}");
            foreach (var pair in unknown) blocker.Message.Should().Contain($"{pair.Key}={pair.Value}");
        }

        [Fact]
        public void CommonConstructionGeometry_HasNaturalMeasurementAdapters()
        {
            var source = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "Estimate", "CivilQuantityExtractionService.cs"));

            source.Should().Contain("case Circle circle:")
                .And.Contain("case Spline spline:")
                .And.Contain("case AcadRegion region:")
                .And.Contain("case CivilDb.FeatureLine featureLine:")
                .And.Contain("case CivilDb.Pipe pipe:")
                .And.Contain("case CivilDb.Structure structure:")
                .And.Contain("featureLine.Length2D")
                .And.Contain("pipe.Length2D");
        }

        [Fact]
        public void ApprovedRule_OverridesBroadPresentationLayerFilter()
        {
            var source = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "Estimate", "CivilQuantityExtractionService.cs"));
            // Capture the same native layer before measurement, both for filtering
            // and for failure provenance; an approved rule still overrides the filter.
            source.Should().MatchRegex(
                    @"var sourceLayer = ent\.Layer;\s*if \(IsCivilPresentationLayer\(sourceLayer\) &&\s*!HasExplicitApprovedRuleForEntity\(profile, ent\)\)")
                .And.Contain("IsExplicitlyApproved(profile, rule)");
            source.Should().MatchRegex(
                @"ProvenanceRef FailureSource\(string operation\) => new\(\)\s*\{[^}]*Layer = sourceLayer,");
        }

        [Fact]
        public void ClosedPolyline_RequiresOneApprovedMeaningAndOneIgnoredAlternative()
        {
            var profile = EstimateFixtures.Profile();
            var measurements = new[]
            {
                new QuantityMeasurement { Kind = "area", Method = "area", RawValue = 20, Unit = "מ\"ר" },
                new QuantityMeasurement { Kind = "length", Method = "perimeter", RawValue = 18, Unit = "מטר" },
            };
            var unresolved = CivilQuantityExtractionService.ClosedPolylineChoice(
                profile, "PAVING", measurements);
            unresolved.IsResolved.Should().BeFalse();

            profile.Estimate.QuantitySources.Rules.Add(new()
            {
                RuleKey = unresolved.AreaRuleKey,
                LayerPattern = "PAVING",
                EntityType = "LWPOLYLINE",
                MeasurementKind = "area",
                CandidateCatalogCode = "U51.01.0090",
                ExpectedUnit = "מ\"ר",
                ApprovedBy = "nataly",
                ApprovedAtUtc = DateTime.UtcNow,
            });
            EstimateFixtures.BindApprovalToActiveCatalog(
                profile.Estimate.QuantitySources.Rules[^1]);
            CivilQuantityExtractionService.ClosedPolylineChoice(profile, "PAVING", measurements)
                .IsResolved.Should().BeFalse("approval alone must not silently discard the perimeter alternative");

            profile.Estimate.IgnoredRuleDecisions.Add(new()
            {
                RuleKey = unresolved.LengthRuleKey,
                Reason = "הפוליליין מייצג שטח ריצוף; ההיקף אינו סעיף ביצוע נוסף",
                ApprovedBy = "nataly",
                ApprovedAtUtc = DateTime.UtcNow,
            });
            CivilQuantityExtractionService.ClosedPolylineChoice(profile, "PAVING", measurements)
                .IsResolved.Should().BeTrue();

            profile.Estimate.QuantitySources.Rules.Add(new()
            {
                RuleKey = unresolved.LengthRuleKey,
                LayerPattern = "PAVING",
                EntityType = "LWPOLYLINE",
                MeasurementKind = "length",
                CandidateCatalogCode = "U51.01.0250",
                ExpectedUnit = "מטר",
                ApprovedBy = "legacy",
                ApprovedAtUtc = DateTime.UtcNow.AddDays(-1),
            });
            EstimateFixtures.BindApprovalToActiveCatalog(
                profile.Estimate.QuantitySources.Rules[^1]);
            CivilQuantityExtractionService.ClosedPolylineChoice(profile, "PAVING", measurements)
                .IsResolved.Should().BeTrue(
                    "the audited exclusion remains authoritative over a stale legacy sibling mapping after reload");
        }

        [Fact]
        public void KnownNonQuantityCoverage_IsInfoAndNeverAnExportBlocker()
        {
            var finding = CivilQuantityExtractionService.KnownNonQuantityCoverageFinding(
                new Dictionary<string, int> { ["DBTEXT"] = 20, ["MLEADER"] = 3 }, "6422");

            finding.Should().NotBeNull();
            finding!.Code.Should().Be(EstimateFindingCodes.NonQuantityEntityTypesSkipped);
            finding.Severity.Should().Be(FindingSeverity.Info);
            finding.Message.Should().Contain("DBTEXT=20").And.Contain("MLEADER=3");
            EstimatePreflightPolicy.IsBlocking(finding).Should().BeFalse();
        }

        [Fact]
        public void DrawingSourceHash_IsHashOfFileBytes_NotThePathString()
        {
            var dir = Path.Combine(Path.GetTempPath(), "mcd-drawing-hash", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var first = Path.Combine(dir, "a.dwg");
            var second = Path.Combine(dir, "renamed.dwg");
            try
            {
                File.WriteAllBytes(first, new byte[] { 1, 2, 3, 4, 5 });
                File.WriteAllBytes(second, new byte[] { 1, 2, 3, 4, 5 });

                var firstHash = CivilQuantityExtractionService.HashDrawingFile(first);
                var secondHash = CivilQuantityExtractionService.HashDrawingFile(second);
                firstHash.Should().MatchRegex("^[0-9a-f]{64}$");
                firstHash.Should().Be(secondHash, "renaming identical source bytes does not change provenance identity");
                firstHash.Should().NotBe(ArtifactHash.Sha256OfText(first),
                    "a path digest is not source evidence");

                File.WriteAllBytes(second, new byte[] { 1, 2, 3, 4, 6 });
                CivilQuantityExtractionService.HashDrawingFile(second).Should().NotBe(firstHash);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void MissingDrawingBytes_AreAnExplicitGlobalSourceIdentityBlocker()
        {
            CivilQuantityExtractionService.HashDrawingFile(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dwg"))
                .Should().BeEmpty();

            var finding = CivilQuantityExtractionService.DrawingSourceHashFinding(
                "(unsaved)", string.Empty, "6422");

            finding.Should().NotBeNull();
            finding!.Code.Should().Be(EstimateFindingCodes.SourceMissing);
            finding.Severity.Should().Be(FindingSeverity.Error);
            finding.AffectedRecordIds.Should().BeEmpty("missing source identity blocks the whole scan");
            EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();
        }

        [Fact]
        public void EarthworksSectionReader_UsesOwnedPointBounds_NotUnsupportedSectionExtents()
        {
            var source = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "Estimate", "CorridorQuantityService.cs"));

            source.Should().Contain("TryNormalizeOwnedSectionPoints")
                .And.Contain("section-point-bounds")
                .And.Contain("MaxHalfWidthM")
                .And.NotContain("s.LeftOffset")
                .And.NotContain("s.RightOffset")
                .And.NotContain("s.MinmumElevation")
                .And.NotContain("s.MaximumElevation");

            source.IndexOf("IsTrustedSectionGroup", StringComparison.Ordinal)
                .Should().BeLessThan(
                    source.IndexOf("var (eg, ds, sourceSelection) = SurfaceChains", StringComparison.Ordinal),
                    "earthworks point bounds are authority only after MCD ownership is proven");
            source.Should().Contain("section-read")
                .And.Contain("section-enumeration",
                    "real SectionPoints/object enumeration failures must remain export blockers");
        }
    }

    public class MappingApprovalTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "mcd-mapping-tests", Guid.NewGuid().ToString("N"));

        public MappingApprovalTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static EstimateWorkflowService.MappingApproval Approval(
            string code, string unit, string kind = "length") =>
            new("layer:KERB|" + kind, code, "KERB", "LWPOLYLINE", kind, unit);

        private static EstimateWorkflowService.ScanResult Scan(
            ProjectProfile profile, params NeutralQuantityRecord[] records) => new()
        {
            RunId = "decision-scan",
            ProjectProfileId = profile.ProfileId,
            ProfileSource = "test-profile.yaml",
            SourceDrawing = "PD.dwg",
            ProjectProfileEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(profile),
            DiscoveryMode = true,
            Records = records.ToList(),
            Status = DeliveryStatus.ReviewRequired,
        };

        private static EstimateWorkflowService.IgnoredRuleDecisionRequest NoiseRequest(
            params NeutralQuantityRecord[] records)
        {
            var key = records[0].Classification.RuleKey!;
            var verdict = QuantitySignificance.Classify(new QuantitySignificance.Group(
                key,
                records[0].Source.Layer,
                records[0].Measurement.Unit,
                records.Sum(record => record.Measurement.RawValue),
                records.Length));
            return new EstimateWorkflowService.IgnoredRuleDecisionRequest(key, verdict.Reason);
        }

        [Fact]
        public void SaveApprovedMappings_RejectsUnknownCatalogCode()
        {
            var profile = EstimateFixtures.Profile();
            var act = () => new EstimateWorkflowService().SaveApprovedMappings(
                profile, EstimateFixtures.Snapshot(),
                new[] { Approval("U99.99.9999", "מטר") }, "nataly", targetPath: Path.Combine(_dir, "never-written.yaml"));

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*does not exist in snapshot*");
        }

        [Fact]
        public void SaveApprovedMappings_RejectsUnitMismatch()
        {
            // U51.01.0090 is an m² item; approving it for a length measurement is a
            // silent unit conversion and must be refused.
            var profile = EstimateFixtures.Profile();
            var act = () => new EstimateWorkflowService().SaveApprovedMappings(
                profile, EstimateFixtures.Snapshot(),
                new[] { Approval("U51.01.0090", "מטר") }, "nataly", targetPath: Path.Combine(_dir, "never-written.yaml"));

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*Unit mismatch*");
        }

        [Fact]
        public void SaveApprovedMappings_RequiresApprover()
        {
            var profile = EstimateFixtures.Profile();
            var act = () => new EstimateWorkflowService().SaveApprovedMappings(
                profile, EstimateFixtures.Snapshot(),
                new[] { Approval("U51.01.0250", "מטר") }, "   ", targetPath: Path.Combine(_dir, "never-written.yaml"));

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void SaveApprovedMappings_RejectsDuplicateRuleKeysBeforeMutation()
        {
            var profile = EstimateFixtures.Profile();
            var approvals = new[]
            {
                Approval("U51.01.0250", "מטר"),
                Approval("U51.06.1900", "מטר"),
            };

            var act = () => new EstimateWorkflowService().SaveApprovedMappings(
                profile, EstimateFixtures.Snapshot(), approvals, "nataly",
                targetPath: Path.Combine(_dir, "never-written.yaml"));

            act.Should().Throw<InvalidOperationException>().WithMessage("*duplicate rule keys*");
            profile.Estimate.QuantitySources.Rules.Should().BeEmpty();
        }

        [Fact]
        public void SaveApprovedMappings_RejectsAuditedIgnoredRuleBeforeMutation()
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.IgnoredRuleDecisions.Add(
                new ProjectProfile.EstimateProfile.IgnoredRuleDecision
                {
                    RuleKey = "layer:KERB|length",
                    Reason = "not construction work",
                    ApprovedBy = "nataly",
                    ApprovedAtUtc = DateTime.UtcNow,
                });
            var path = Path.Combine(_dir, "ignored-mapping-must-not-write.yaml");

            var act = () => new EstimateWorkflowService().SaveApprovedMappings(
                profile, EstimateFixtures.Snapshot(),
                new[] { Approval("U51.01.0250", "מטר") }, "nataly", targetPath: path);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*Return it to the estimate before approving*");
            profile.Estimate.QuantitySources.Rules.Should().BeEmpty();
            File.Exists(path).Should().BeFalse();
        }

        [Fact]
        public void FailedDecisionEvidenceRestore_ReinstatesExactPreviousProfileBytes()
        {
            var profile = EstimateFixtures.Profile();
            var path = Path.Combine(_dir, "decision-rollback.yaml");
            var original = ProfileCasTest.Save(
                profile, path, "initial test profile", "nataly");
            var originalText = File.ReadAllText(path);

            var decision = new EstimateWorkflowService().SaveApprovedMappings(
                profile, EstimateFixtures.Snapshot(),
                new[] { Approval("U51.01.0250", "מטר") }, "nataly", targetPath: path);
            ArtifactHash.Sha256OfText(File.ReadAllText(path)).Should().Be(decision.NewHash);

            EstimateWorkflowService.RestoreProfileAfterFailedDecisionEvidence(decision);

            File.ReadAllText(path).Should().Be(originalText);
            ArtifactHash.Sha256OfText(File.ReadAllText(path)).Should().Be(original.NewHash);
            ProjectProfileLoader.LoadFromFile(path).Profile!.Estimate.QuantitySources.Rules
                .Should().BeEmpty();
        }

        [Fact]
        public void FailedDecisionEvidenceRestore_RefusesToOverwriteNewerProfileBytes()
        {
            var profile = EstimateFixtures.Profile();
            var path = Path.Combine(_dir, "decision-rollback-race.yaml");
            ProfileCasTest.Save(profile, path, "initial test profile", "nataly");
            var decision = new EstimateWorkflowService().SaveApprovedMappings(
                profile, EstimateFixtures.Snapshot(),
                new[] { Approval("U51.01.0250", "מטר") }, "nataly", targetPath: path);
            File.AppendAllText(path, "\n# concurrent newer bytes\n");

            var act = () => EstimateWorkflowService
                .RestoreProfileAfterFailedDecisionEvidence(decision);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*changed after the decision save*");
            File.ReadAllText(path).Should().Contain("concurrent newer bytes");
        }

        [Fact]
        public void ApproveCompleteDiscoveryScope_PersistsHostAndXrefPoliciesAndApprovalTrail()
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.QuantitySources.SourceScopePolicy = null;
            profile.Estimate.QuantitySources.XrefPolicy = null;
            var path = Path.Combine(_dir, "scope-approval.yaml");

            var saved = new EstimateWorkflowService().ApproveCompleteDiscoveryScope(
                profile, "nataly", targetPath: path);

            saved.Path.Should().Be(path);
            var loaded = ProjectProfileLoader.LoadFromFile(path);
            loaded.IsUsable.Should().BeTrue();
            var persisted = loaded.Profile!;
            EstimateWorkflowService.IsCompleteDiscoveryScopeApproved(persisted).Should().BeTrue();
            persisted.Estimate.QuantitySources.SourceScopePolicy
                .Should().Be(EstimatePreflightPolicy.DiscoverAllSourceScopePolicy);
            persisted.Estimate.QuantitySources.XrefPolicy
                .Should().Be(EstimatePreflightPolicy.IncludeXrefsPolicy);
            persisted.Provenance.ApprovedBy.Should().Be("nataly");
            persisted.Provenance.ApprovedAtUtc.Should().NotBeNull();
            persisted.Provenance.Source.Should().Contain("recursive host+XREF");
            EstimatePreflightPolicy.ValidateSourcePolicies(persisted).Should().BeEmpty();
        }

        [Fact]
        public void ApproveCompleteDiscoveryScope_RequiresNamedApproverAndWritesNothing()
        {
            var profile = new ProjectProfile { ProfileId = "6422" };
            var path = Path.Combine(_dir, "scope-never-written.yaml");

            var act = () => new EstimateWorkflowService().ApproveCompleteDiscoveryScope(
                profile, "  ", targetPath: path);

            act.Should().Throw<ArgumentException>();
            File.Exists(path).Should().BeFalse();
            profile.Estimate.QuantitySources.SourceScopePolicy.Should().BeNull();
            profile.Estimate.QuantitySources.XrefPolicy.Should().BeNull();
        }

        [Fact]
        public void QuantityRuleSummaryAndGridCode_CountOnlyNamedTimestampedApprovals()
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.QuantitySources.Rules.Add(
                new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
                {
                    RuleKey = "layer:DEFAULT|length",
                    LayerPattern = "DEFAULT",
                    MeasurementKind = "length",
                    CandidateCatalogCode = "U51.01.0250",
                    ExpectedUnit = "מטר",
                });
            profile.Estimate.QuantitySources.Rules.Add(
                new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
                {
                    RuleKey = "layer:KERB|length",
                    LayerPattern = "KERB",
                    MeasurementKind = "length",
                    CandidateCatalogCode = "U51.01.0250",
                    ExpectedUnit = "מטר",
                    ApprovedBy = "nataly",
                    ApprovedAtUtc = DateTime.UtcNow,
                });
            EstimateFixtures.BindApprovalToActiveCatalog(
                profile.Estimate.QuantitySources.Rules[^1]);

            var summary = EstimateWorkflowService.QuantityRuleApprovals(profile);
            summary.Approved.Should().Be(1);
            summary.Unapproved.Should().Be(1);
            EstimateWorkflowService.ApprovedCatalogCodeForDisplay(
                    profile, "layer:DEFAULT|length", "DEFAULT", "length", "U51.01.0250")
                .Should().BeNull("a shipped candidate must not look approved in the grid");
            EstimateWorkflowService.ApprovedCatalogCodeForDisplay(
                    profile, "layer:KERB|length", "KERB", "length", "U51.01.0250")
                .Should().Be("U51.01.0250");
        }

        [Fact]
        public void ExplicitlyIgnoredRule_CanExportOnlyTheRemainingWork_AndStaysNamedInAudit()
        {
            const string ignoredKey = "layer:HELPER|length";
            var profile = EstimateFixtures.Profile();
            var approvedAt = new DateTime(2026, 8, 31, 14, 30, 0, DateTimeKind.Utc);
            profile.Estimate.IgnoredRuleDecisions.Add(new()
            {
                RuleKey = ignoredKey,
                Reason = "קו עזר גרפי בלבד; אינו רכיב לביצוע",
                ApprovedBy = "nataly",
                ApprovedAtUtc = approvedAt,
            });
            var safe = EstimateFixtures.Record(
                "priced", "U51.01.0250", 25, "מטר", handle: "A", ruleKey: "layer:KERB|length");
            var ignored = EstimateFixtures.Record(
                "ignored", null!, 40, "מטר", handle: "B", ruleKey: ignoredKey, layer: "HELPER");
            ignored.Status = DeliveryStatus.ReviewRequired;
            ignored.Findings.Add(new DeliveryFinding
            {
                Code = EstimateFindingCodes.Unmapped,
                Domain = "estimate",
                Severity = FindingSeverity.ReviewRequired,
                Title = "ignored discovery record",
                AffectedRecordIds = { ignored.RecordId },
            });

            var application = EstimateWorkflowService.ApplyIgnoredRules(
                new[] { safe, ignored }, profile);
            var result = EstimateBuilder.Build(
                application.PricedRecords, EstimateFixtures.Snapshot(), profile);
            result.Exclusions.AddRange(application.Exclusions);
            result.Findings.AddRange(application.AuditFindings);

            result.Lines.Should().ContainSingle().Which.RecordId.Should().Be("priced");
            result.ExcludedLineCount.Should().Be(0,
                "an explicitly ignored group is outside the priced line set, not a hidden partial line");
            EstimatePreflightPolicy.CanExport(result).Should().BeTrue();
            var finding = result.Findings.Should().ContainSingle(f =>
                f.Code == EstimateFindingCodes.NotAQuantity).Subject;
            finding.Severity.Should().Be(FindingSeverity.Info);
            finding.Message.Should().Contain(ignoredKey)
                .And.Contain("HELPER")
                .And.Contain("40.00");
            var exclusion = result.Exclusions.Should().ContainSingle().Subject;
            exclusion.RuleKey.Should().Be(ignoredKey);
            exclusion.Reason.Should().Be("קו עזר גרפי בלבד; אינו רכיב לביצוע");
            exclusion.ApprovedBy.Should().Be("nataly");
            exclusion.ApprovedAtUtc.Should().Be(approvedAt);
            var source = exclusion.Sources.Should().ContainSingle().Subject;
            source.RecordId.Should().Be(ignored.RecordId);
            source.Drawing.Should().Be(ignored.Source.Drawing);
            source.DrawingHash.Should().Be(ignored.Source.DrawingHash);
            source.Handle.Should().Be("B");
            source.Layer.Should().Be("HELPER");
            source.RawValue.Should().Be(40);
        }

        [Fact]
        public void LegacyBareIgnoredRule_IsNotAllowedToRemoveMeasuredWork()
        {
            const string key = "layer:HELPER|length";
            var profile = EstimateFixtures.Profile();
            profile.Estimate.IgnoredRuleKeys.Add(key);
            var record = EstimateFixtures.Record(
                "legacy", null!, 12, "מטר", handle: "B", ruleKey: key, layer: "HELPER");

            var application = EstimateWorkflowService.ApplyIgnoredRules(
                new[] { record }, profile);

            application.PricedRecords.Should().ContainSingle().Which.Should().BeSameAs(record);
            application.Exclusions.Should().BeEmpty();
            application.AuditFindings.Should().ContainSingle(f =>
                f.Severity == FindingSeverity.ReviewRequired &&
                f.AffectedRecordIds.Contains(record.RecordId));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(0d)]
        [InlineData(-1d)]
        public void AuditedIgnoreDecision_CannotHideAnInvalidMeasurement(double invalidValue)
        {
            const string key = "layer:2000+120+W|length";
            var profile = EstimateFixtures.Profile();
            profile.Estimate.IgnoredRuleDecisions.Add(new()
            {
                RuleKey = key,
                Reason = "station annotation",
                ApprovedBy = "nataly",
                ApprovedAtUtc = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
            });
            var invalid = EstimateFixtures.Record(
                "invalid", null!, invalidValue, "מטר", handle: "S1",
                ruleKey: key, layer: "2000+120+W");
            var validSibling = EstimateFixtures.Record(
                "valid-sibling", null!, 12, "מטר", handle: "S2",
                ruleKey: key, layer: "2000+120+W");

            var application = EstimateWorkflowService.ApplyIgnoredRules(
                new[] { invalid, validSibling }, profile);

            application.PricedRecords.Should().BeEquivalentTo(
                new[] { invalid, validSibling },
                "one invalid raw value invalidates the group-wide exclusion");
            application.Exclusions.Should().BeEmpty();
            application.AuditFindings.Should().ContainSingle(f =>
                f.Code == EstimateFindingCodes.MeasurementFailed &&
                f.Severity == FindingSeverity.Error &&
                f.AffectedRecordIds.Contains(invalid.RecordId));

            var result = EstimateBuilder.Build(
                application.PricedRecords,
                EstimateFixtures.Snapshot(),
                profile,
                preflightFindings: application.AuditFindings);
            result.Lines.Single(line => line.RecordId == invalid.RecordId)
                .Status.Should().Be(DeliveryStatus.Failed);
            result.CleanTotal.Should().Be(0m);
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
        }

        [Fact]
        public void SaveIgnoredRuleDecision_PersistsAuditedAuthority_AndRevocation()
        {
            const string key = "layer:HELPER|length";
            var workflow = new EstimateWorkflowService();
            var profile = EstimateFixtures.Profile();
            profile.Estimate.IgnoredRuleKeys.Add(key);
            var path = Path.Combine(_dir, "ignored-decision.yaml");
            var record = EstimateFixtures.Record(
                "helper", null!, 12, "מטר", ruleKey: key, layer: "HELPER");
            var scan = Scan(profile, record);

            workflow.SaveIgnoredRuleDecision(
                profile, scan, key, true, "קו עזר גרפי בלבד", "nataly", path);

            profile.Estimate.IgnoredRuleKeys.Should().NotContain(key,
                "legacy bare keys must not remain a second authority");
            var savedDecision = EstimateWorkflowService.ApprovedIgnoredRuleDecision(profile, key);
            savedDecision.Should().NotBeNull();
            savedDecision!.Reason.Should().Be("קו עזר גרפי בלבד");
            savedDecision.ApprovedBy.Should().Be("nataly");
            savedDecision.ApprovedAtUtc.Should().NotBeNull();

            var loaded = ProjectProfileLoader.LoadFromFile(path);
            loaded.IsUsable.Should().BeTrue(string.Join("; ", loaded.Findings.Select(f => f.Title)));
            EstimateWorkflowService.ApprovedIgnoredRuleDecision(loaded.Profile, key)
                .Should().NotBeNull();

            workflow.SaveIgnoredRuleDecision(
                profile, scan, key, false, null, "nataly", path);
            EstimateWorkflowService.ApprovedIgnoredRuleDecision(profile, key).Should().BeNull();
            ProjectProfileLoader.LoadFromFile(path).Profile!.Estimate.IgnoredRuleDecisions
                .Should().NotContain(d => d.RuleKey == key);
        }

        [Fact]
        public void SaveIgnoredRuleDecision_RequiresReason_AndRollsBackOnWriteFailure()
        {
            const string key = "layer:HELPER|length";
            var workflow = new EstimateWorkflowService();
            var profile = EstimateFixtures.Profile();
            var record = EstimateFixtures.Record(
                "helper", null!, 12, "מטר", ruleKey: key, layer: "HELPER");
            var scan = Scan(profile, record);

            var noReason = () => workflow.SaveIgnoredRuleDecision(
                profile, scan, key, true, "  ", "nataly", Path.Combine(_dir, "never.yaml"));
            noReason.Should().Throw<ArgumentException>();
            profile.Estimate.IgnoredRuleDecisions.Should().BeEmpty();

            var directoryAsTarget = Path.Combine(_dir, "cannot-replace-directory");
            Directory.CreateDirectory(directoryAsTarget);
            var writeFailure = () => workflow.SaveIgnoredRuleDecision(
                profile, scan, key, true, "קו עזר", "nataly", directoryAsTarget);
            writeFailure.Should().Throw<Exception>();
            profile.Estimate.IgnoredRuleDecisions.Should().BeEmpty(
                "a failed atomic save cannot leave the in-memory gate approved");
            profile.Estimate.IgnoredRuleKeys.Should().BeEmpty();
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(0d)]
        [InlineData(-1d)]
        public void SaveIgnoredRuleDecision_RejectsInvalidRawBeforeMutation(double invalidValue)
        {
            const string key = "layer:2000+120+W|length";
            var profile = EstimateFixtures.Profile();
            var invalid = EstimateFixtures.Record(
                "invalid", null!, invalidValue, "מטר", handle: "S1",
                ruleKey: key, layer: "2000+120+W");
            var scan = Scan(profile, invalid);
            var path = Path.Combine(_dir, "invalid-manual-must-not-exist.yaml");

            var act = () => new EstimateWorkflowService().SaveIgnoredRuleDecision(
                profile, scan, key, true, "station annotation", "nataly", path);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*invalid raw measurement*");
            profile.Estimate.IgnoredRuleDecisions.Should().BeEmpty();
            profile.Estimate.IgnoredRuleKeys.Should().BeEmpty();
            File.Exists(path).Should().BeFalse();
        }

        [Fact]
        public void SaveIgnoredRuleDecision_RequiresExactCurrentScanScope()
        {
            const string key = "layer:HELPER|length";
            var workflow = new EstimateWorkflowService();
            var path = Path.Combine(_dir, "scope-mismatch-must-not-exist.yaml");

            var profile = EstimateFixtures.Profile();
            var other = EstimateFixtures.Record(
                "other", null!, 5, "מטר",
                ruleKey: "layer:OTHER|length", layer: "OTHER");
            var missingExactGroup = () => workflow.SaveIgnoredRuleDecision(
                profile, Scan(profile, other), key, true, "helper", "nataly", path);
            missingExactGroup.Should().Throw<InvalidOperationException>()
                .WithMessage("*not present as an exact group*");

            var matching = EstimateFixtures.Record(
                "helper", null!, 5, "מטר", ruleKey: key, layer: "HELPER");
            var matchingScan = Scan(profile, matching);
            var wrongProfile = EstimateFixtures.Profile();
            wrongProfile.ProfileId = "other-profile";
            var profileMismatch = () => workflow.SaveIgnoredRuleDecision(
                wrongProfile, matchingScan, key, true, "helper", "nataly", path);
            profileMismatch.Should().Throw<InvalidOperationException>()
                .WithMessage("*different project profile*");

            profile.ProjectName = "changed after scan";
            var staleProfile = () => workflow.SaveIgnoredRuleDecision(
                profile, matchingScan, key, true, "helper", "nataly", path);
            staleProfile.Should().Throw<InvalidOperationException>()
                .WithMessage("*profile changed after the quantity scan*");

            profile.Estimate.IgnoredRuleDecisions.Should().BeEmpty();
            wrongProfile.Estimate.IgnoredRuleDecisions.Should().BeEmpty();
            File.Exists(path).Should().BeFalse();
        }

        [Fact]
        public void SaveIgnoredRuleDecisions_PersistsOneAtomicAuditedNoiseBatch()
        {
            var profile = EstimateFixtures.Profile();
            var station = EstimateFixtures.Record(
                "station", null!, 38, "מטר", handle: "S1",
                ruleKey: "layer:2000+120+W|length", layer: "2000+120+W");
            var auxiliary = EstimateFixtures.Record(
                "auxiliary", null!, 12, "מטר", handle: "A1",
                ruleKey: "layer:0-EZER|length", layer: "0-EZER");
            profile.Estimate.IgnoredRuleKeys.Add(station.Classification.RuleKey!);
            profile.Estimate.IgnoredRuleKeys.Add(auxiliary.Classification.RuleKey!);
            var scan = Scan(profile, station, auxiliary);
            var path = Path.Combine(_dir, "ignored-noise-batch.yaml");

            new EstimateWorkflowService().SaveIgnoredRuleDecisions(
                profile,
                scan,
                new[] { NoiseRequest(station), NoiseRequest(auxiliary) },
                "nataly",
                path);

            profile.Estimate.IgnoredRuleKeys.Should().BeEmpty(
                "legacy bare keys cannot remain a second exclusion authority");
            profile.Estimate.IgnoredRuleDecisions.Should().HaveCount(2);
            profile.Estimate.IgnoredRuleDecisions.Should().OnlyContain(decision =>
                decision.ApprovedBy == "nataly" &&
                !string.IsNullOrWhiteSpace(decision.Reason) &&
                decision.ApprovedAtUtc != null);
            profile.Estimate.IgnoredRuleDecisions
                .Select(decision => decision.ApprovedAtUtc)
                .Distinct().Should().ContainSingle("the batch is one engineering act");

            var loaded = ProjectProfileLoader.LoadFromFile(path);
            loaded.IsUsable.Should().BeTrue(string.Join("; ", loaded.Findings.Select(f => f.Title)));
            loaded.Profile!.Estimate.IgnoredRuleDecisions.Should().HaveCount(2);
        }

        [Fact]
        public void SaveIgnoredRuleDecisions_RejectsUnsafeGroupBeforeAnyMutation()
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.IgnoredRuleDecisions.Add(new()
            {
                RuleKey = "layer:PREEXISTING|length",
                Reason = "existing audited choice",
                ApprovedBy = "nataly",
                ApprovedAtUtc = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
            });
            var station = EstimateFixtures.Record(
                "station", null!, 38, "מטר", handle: "S1",
                ruleKey: "layer:2000+120+W|length", layer: "2000+120+W");
            var existingUtility = EstimateFixtures.Record(
                "utility", null!, 80, "מטר", handle: "U1",
                ruleKey: "layer:MYA-4602-Water-line|length", layer: "MYA-4602-Water-line");
            var scan = Scan(profile, station, existingUtility);
            var path = Path.Combine(_dir, "unsafe-batch-must-not-exist.yaml");

            var act = () => new EstimateWorkflowService().SaveIgnoredRuleDecisions(
                profile,
                scan,
                new[] { NoiseRequest(station), NoiseRequest(existingUtility) },
                "nataly",
                path);

            act.Should().Throw<InvalidOperationException>().WithMessage("*ExistingUtility*");
            profile.Estimate.IgnoredRuleDecisions.Should().ContainSingle()
                .Which.RuleKey.Should().Be("layer:PREEXISTING|length");
            File.Exists(path).Should().BeFalse();
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(0d)]
        [InlineData(-1d)]
        public void SaveIgnoredRuleDecisions_RejectsInvalidRawBeforeMutation(double invalidValue)
        {
            var profile = EstimateFixtures.Profile();
            var invalidStation = EstimateFixtures.Record(
                "invalid-station", null!, invalidValue, "מטר", handle: "S1",
                ruleKey: "layer:2000+120+W|length", layer: "2000+120+W");
            var scan = Scan(profile, invalidStation);
            var request = NoiseRequest(invalidStation);
            var path = Path.Combine(_dir, "invalid-noise-must-not-exist.yaml");

            var act = () => new EstimateWorkflowService().SaveIgnoredRuleDecisions(
                profile, scan, new[] { request }, "nataly", path);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*invalid raw measurement*");
            profile.Estimate.IgnoredRuleDecisions.Should().BeEmpty();
            profile.Estimate.IgnoredRuleKeys.Should().BeEmpty();
            File.Exists(path).Should().BeFalse();
        }

        [Fact]
        public void SaveIgnoredRuleDecisions_RejectsPartialScopeAndMissingExactRuleKeys()
        {
            var profile = EstimateFixtures.Profile();
            var station = EstimateFixtures.Record(
                "station", null!, 38, "מטר", handle: "S1",
                ruleKey: "layer:2000+120+W|length", layer: "2000+120+W");
            var request = NoiseRequest(station);

            var partialScan = Scan(profile, station);
            partialScan = new EstimateWorkflowService.ScanResult
            {
                RunId = partialScan.RunId,
                ProjectProfileId = partialScan.ProjectProfileId,
                ProfileSource = partialScan.ProfileSource,
                SourceDrawing = partialScan.SourceDrawing,
                ProjectProfileEffectiveHash = partialScan.ProjectProfileEffectiveHash,
                DiscoveryMode = false,
                Records = partialScan.Records,
                Status = partialScan.Status,
            };
            var partial = () => new EstimateWorkflowService().SaveIgnoredRuleDecisions(
                profile, partialScan, new[] { request }, "nataly",
                Path.Combine(_dir, "partial-scope-must-not-exist.yaml"));
            partial.Should().Throw<InvalidOperationException>().WithMessage("*discover-all*");

            var missingKey = EstimateFixtures.Record(
                "missing-key", null!, 38, "מטר", handle: "S2",
                ruleKey: null, layer: "2000+120+W");
            var exactKeyScan = Scan(profile, missingKey);
            var verdict = QuantitySignificance.Classify(new QuantitySignificance.Group(
                "(none)", missingKey.Source.Layer, missingKey.Measurement.Unit,
                missingKey.Measurement.RawValue, 1));
            var noExactKey = () => new EstimateWorkflowService().SaveIgnoredRuleDecisions(
                profile, exactKeyScan,
                new[] { new EstimateWorkflowService.IgnoredRuleDecisionRequest("(none)", verdict.Reason) },
                "nataly", Path.Combine(_dir, "missing-key-must-not-exist.yaml"));
            noExactKey.Should().Throw<InvalidOperationException>().WithMessage("*exact classification rule key*");
            profile.Estimate.IgnoredRuleDecisions.Should().BeEmpty();
        }

        [Fact]
        public void SaveIgnoredRuleDecisions_RequiresExactReasonAndRollsBackTheWholeWrite()
        {
            var profile = EstimateFixtures.Profile();
            var station = EstimateFixtures.Record(
                "station", null!, 38, "מטר", handle: "S1",
                ruleKey: "layer:2000+120+W|length", layer: "2000+120+W");
            var scan = Scan(profile, station);
            var request = NoiseRequest(station);

            var wrongReason = () => new EstimateWorkflowService().SaveIgnoredRuleDecisions(
                profile,
                scan,
                new[] { request with { Reason = "generic noise" } },
                "nataly",
                Path.Combine(_dir, "wrong-reason-must-not-exist.yaml"));
            wrongReason.Should().Throw<InvalidOperationException>().WithMessage("*classifier evidence*");
            profile.Estimate.IgnoredRuleDecisions.Should().BeEmpty();

            profile.Estimate.IgnoredRuleKeys.Add(request.RuleKey);
            scan = Scan(profile, station);
            var directoryAsTarget = Path.Combine(_dir, "cannot-replace-batch-directory");
            Directory.CreateDirectory(directoryAsTarget);
            var writeFailure = () => new EstimateWorkflowService().SaveIgnoredRuleDecisions(
                profile, scan, new[] { request }, "nataly", directoryAsTarget);

            writeFailure.Should().Throw<Exception>();
            profile.Estimate.IgnoredRuleDecisions.Should().BeEmpty();
            profile.Estimate.IgnoredRuleKeys.Should().Equal(new[] { request.RuleKey },
                "a failed atomic write restores legacy and audited lists exactly");
        }

        [Fact]
        public void LegacyOrDefaultRule_WithoutPerRuleApproval_IsNeverPriceable()
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.QuantitySources.Rules.Add(
                new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
                {
                    RuleKey = "mahod-default:kerb",
                    LayerPattern = "KERB*",
                    MeasurementKind = "length",
                    CandidateCatalogCode = "U51.01.0250",
                    ExpectedUnit = "מטר",
                });

            CivilQuantityExtractionService.FindApprovedRule(
                    profile, "layer:KERB|length", "KERB", "length")
                .Should().BeNull("a catalog suggestion is not an engineer approval");
        }

        [Fact]
        public void Rule_IsPriceableOnlyWithApproverAndTimestamp()
        {
            var profile = EstimateFixtures.Profile();
            var rule = new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
            {
                RuleKey = "layer:KERB|length",
                LayerPattern = "KERB",
                MeasurementKind = "length",
                CandidateCatalogCode = "U51.01.0250",
                ExpectedUnit = "מטר",
                ApprovedBy = "nataly",
            };
            profile.Estimate.QuantitySources.Rules.Add(rule);

            CivilQuantityExtractionService.FindApprovedRule(
                    profile, rule.RuleKey, "KERB", "length")
                .Should().BeNull("an approver without a timestamp is incomplete evidence");

            rule.ApprovedAtUtc = DateTime.UtcNow;
            CivilQuantityExtractionService.FindApprovedRule(
                    profile, rule.RuleKey, "KERB", "length")
                .Should().BeNull("a legacy approval without active catalog id/hash/fingerprint is not current");

            EstimateFixtures.BindApprovalToActiveCatalog(rule);
            CivilQuantityExtractionService.FindApprovedRule(
                    profile, rule.RuleKey, "KERB", "length")
                .Should().BeSameAs(rule);
        }

        [Fact]
        public void ApprovedLeafLayerRuleClassifiesAnXrefQualifiedEntity()
        {
            var profile = EstimateFixtures.Profile();
            var rule = new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
            {
                RuleKey = "office:kerb",
                LayerPattern = "HW-CURB",
                MeasurementKind = "length",
                CandidateCatalogCode = "U51.01.0250",
                ExpectedUnit = "מטר",
                ApprovedBy = "nataly",
                ApprovedAtUtc = DateTime.UtcNow,
            };
            EstimateFixtures.BindApprovalToActiveCatalog(rule);
            profile.Estimate.QuantitySources.Rules.Add(rule);

            CivilQuantityExtractionService.ResolveApprovedRule(
                    profile, "layer:HW-CURB|length",
                    "6422-SM-MODEL-NATAZ|HW-CURB", "length")
                .Rule.Should().BeSameAs(rule);
        }

        [Fact]
        public void MultipleApprovedQuantityRulesMatchingOneEntity_AreAmbiguous()
        {
            var profile = EstimateFixtures.Profile();
            foreach (var key in new[] { "kerb-exact", "kerb-wildcard" })
            {
                var rule = new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
                {
                    RuleKey = key,
                    LayerPattern = key == "kerb-exact" ? "KERB" : "K*",
                    MeasurementKind = "length",
                    CandidateCatalogCode = "U51.01.0250",
                    ExpectedUnit = "מטר",
                    ApprovedBy = "nataly",
                    ApprovedAtUtc = DateTime.UtcNow,
                };
                EstimateFixtures.BindApprovalToActiveCatalog(rule);
                profile.Estimate.QuantitySources.Rules.Add(rule);
            }

            var resolution = CivilQuantityExtractionService.ResolveApprovedRule(
                profile, "layer:KERB|length", "KERB", "length");

            resolution.IsAmbiguous.Should().BeTrue();
            resolution.Rule.Should().BeNull("list order is never mapping authority");
            resolution.Matches.Should().HaveCount(2);
        }

        [Fact]
        public void ApprovedMapping_TurnsDiscoveredQuantityIntoPricedLine()
        {
            var profile = EstimateFixtures.Profile();
            var snapshot = EstimateFixtures.Snapshot();

            // The rule the engineer approves for the discovered kerb group.
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

            // A record extracted under that approved rule now carries the code.
            var mapped = EstimateFixtures.Record("r1", "U51.01.0250", 380.0, "מטר");
            var result = EstimateBuilder.Build(new[] { mapped }, snapshot, profile);

            var line = result.Lines.Single();
            line.PriceStatus.Should().Be(PriceStatus.Priced);
            line.IncludedInTotals.Should().BeTrue();
            line.BoqQuantity.Should().Be(380.0);
            result.CleanTotal.Should().BeGreaterThan(0);
        }

        [Fact]
        public void SaveApprovedMappings_PersistsRuleAndReloads()
        {
            var profile = EstimateFixtures.Profile();
            profile.Sections.Cl.LayerPatterns.Add("CL-SEC");

            // NEVER the live profile: a temp path, so the test cannot touch an
            // engineer's configuration (it did, on 2026-08-19).
            var tempProfile = Path.Combine(Path.GetTempPath(), "mcd_test_" + Guid.NewGuid().ToString("N"), "project-profile.yaml");
            var saved = new EstimateWorkflowService().SaveApprovedMappings(
                profile, EstimateFixtures.Snapshot(),
                new[] { Approval("U51.01.0250", "מטר") }, "nataly", targetPath: tempProfile);

            saved.Path.Should().Be(tempProfile, "the test must not write anywhere near the live profile");
            File.Exists(saved.Path).Should().BeTrue();
            try
            {
                var reloaded = ProjectProfileLoader.LoadFromFile(saved.Path);
                reloaded.IsUsable.Should().BeTrue();
                var rule = reloaded.Profile!.Estimate.QuantitySources.Rules
                    .Should().ContainSingle().Subject;
                rule.CandidateCatalogCode.Should().Be("U51.01.0250");
                rule.ExpectedUnit.Should().Be("מטר");
                rule.RuleKey.Should().Be("layer:KERB|length");
                rule.ApprovedBy.Should().Be("nataly");
                rule.ApprovedAtUtc.Should().NotBeNull();
                rule.ApprovedCatalogId.Should().Be(EstimateFixtures.Snapshot().SnapshotId);
                rule.ApprovedCatalogHash.Should().Be(EstimateFixtures.Snapshot().FileHash);
                rule.ApprovedCatalogItemFingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
                reloaded.Profile.Provenance.ApprovedBy.Should().Be("nataly");
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(saved.Path)!, true); } catch { }
            }
        }

        [Fact]
        public void SaveApprovedMappings_UpdatesExistingRuleInsteadOfDuplicating()
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.QuantitySources.Rules.Add(
                new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
                {
                    RuleKey = "layer:KERB|length",
                    CandidateCatalogCode = "U51.01.0240",
                    ExpectedUnit = "מטר",
                });

            var tempProfile = Path.Combine(Path.GetTempPath(), "mcd_test_" + Guid.NewGuid().ToString("N"), "project-profile.yaml");
            var saved = new EstimateWorkflowService().SaveApprovedMappings(
                profile, EstimateFixtures.Snapshot(),
                new[] { Approval("U51.01.0250", "מטר") }, "nataly", targetPath: tempProfile);

            try
            {
                profile.Estimate.QuantitySources.Rules.Should().ContainSingle();
                profile.Estimate.QuantitySources.Rules[0].CandidateCatalogCode.Should().Be("U51.01.0250");
                profile.Estimate.QuantitySources.Rules[0].ApprovedBy.Should().Be("nataly");
                profile.Estimate.QuantitySources.Rules[0].ApprovedAtUtc.Should().NotBeNull();
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(saved.Path)!, true); } catch { }
            }
        }

        [Fact]
        public void MappingApproval_BecomesUnapprovedWhenActiveCatalogIdentityChanges()
        {
            var profile = EstimateFixtures.Profile();
            var tempProfile = Path.Combine(_dir, "catalog-bound-mapping.yaml");
            new EstimateWorkflowService().SaveApprovedMappings(
                profile, EstimateFixtures.Snapshot(),
                new[] { Approval("U51.01.0250", "מטר") }, "nataly", targetPath: tempProfile);
            var rule = profile.Estimate.QuantitySources.Rules.Single();
            CivilQuantityExtractionService.IsExplicitlyApproved(profile, rule).Should().BeTrue();

            profile.Estimate.PriceBooks.Add(new ProjectProfile.EstimateProfile.PriceBookEntry
            {
                Id = "other-edition",
                File = EstimateFixtures.PriceBookPath,
                FileHash = EstimateFixtures.Snapshot().FileHash,
            });
            PriceBookRegistry.MakeActive(profile, "other-edition");

            CivilQuantityExtractionService.IsExplicitlyApproved(profile, rule).Should().BeFalse(
                "approval is for the exact snapshot id+hash, not merely a catalog code");
            CivilQuantityExtractionService.FindApprovedRule(
                    profile, rule.RuleKey!, "KERB", "length")
                .Should().BeNull();
        }

        [Fact]
        public void XrefDiscoveryContract_IsRecursiveTransformedHashVerifiedAndFailClosed()
        {
            var source = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "Estimate", "CivilQuantityExtractionService.cs"));

            source.Should().Contain("void TraverseReference(")
                .And.Contain("outerTransform * reference.BlockTransform")
                .And.Contain("definitionStack.Add(definition.ObjectId)")
                .And.Contain("definition.IsUnloaded || !definition.IsResolved")
                .And.Contain("ClInstructionReader.ResolvePath(")
                .And.Contain("ClInstructionReader.HashFileShared(resolved)")
                .And.Contain("TryReadDwgSnapshotIdentity(resolved")
                .And.Contain("XrefQuantityPolicy.LoadedSnapshotFailure(")
                .And.Contain("ValidateExternalTransform(composedTransform)")
                .And.Contain("EstimateFindingCodes.XrefTransformInvalid")
                .And.Contain("ReferenceHandlePath = currentHandle")
                .And.Contain("DrawingPath = source.DrawingPath")
                .And.Contain("DrawingHash = source.DrawingHash")
                .And.Contain("Xref = source.XrefChain");
        }

        [Fact]
        public void XrefInstanceIdentity_UsesTopLevelAndNestedReferenceHandles()
        {
            CivilQuantityExtractionService.AppendReferenceHandlePath(null, "10")
                .Should().Be("10");
            CivilQuantityExtractionService.AppendReferenceHandlePath(null, "11")
                .Should().Be("11");
            CivilQuantityExtractionService.AppendReferenceHandlePath("10", "20")
                .Should().Be("10/20");
        }

        [Fact]
        public void OrdinaryBlockInsideXref_IsMeasuredOnceAndOnlyExternalReferenceIsExpanded()
        {
            var source = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "Estimate", "CivilQuantityExtractionService.cs"));

            source.Should().Contain("if (!isExternal)")
                .And.Contain("if (countOrdinaryReference)")
                .And.Contain("Consider(reference, parentSource, outerTransform, currentHandle")
                .And.Contain("if (member is BlockReference nestedReference)")
                .And.Contain("countOrdinaryReference: false")
                .And.Contain("if (nestedObject is BlockReference nestedReference)")
                .And.Contain("countOrdinaryReference: true")
                .And.Contain("if (nestedObject is Entity nestedEntity)")
                .And.Contain("Consider(nestedEntity, source, composedTransform,")
                .And.NotContain("ExtractFromXref(")
                .And.NotContain("MeasureEntity(");
        }

        [Fact]
        public void ExternalSourceTraceComparison_IsOrderIndependentAndInstanceSensitive()
        {
            var one = new EstimateExternalSource
            {
                DrawingPath = Path.Combine(_dir, "one.dwg"),
                DrawingHash = new string('a', 64),
                XrefChain = "ONE",
                ReferenceHandlePath = "10",
            };
            var two = new EstimateExternalSource
            {
                DrawingPath = Path.Combine(_dir, "two.dwg"),
                DrawingHash = new string('b', 64),
                XrefChain = "ONE > TWO",
                ReferenceHandlePath = "10/20",
            };

            EstimateWorkflowService.ExternalSourcesEqual(
                new[] { one, two }, new[] { two, one }).Should().BeTrue();
            EstimateWorkflowService.ExternalSourcesEqual(
                new[] { one }, new[] { one, two }).Should().BeFalse();
        }
    }
}
