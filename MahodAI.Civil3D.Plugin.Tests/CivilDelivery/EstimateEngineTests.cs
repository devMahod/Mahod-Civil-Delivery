using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.Tools.CivilDelivery;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public static class EstimateFixtures
    {
        public static string RepoRoot()
        {
            var pluginSrc = typeof(EstimateFixtures).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;
            return Path.GetFullPath(Path.Combine(pluginSrc, ".."));
        }

        public static string PriceBookPath =>
            Path.Combine(RepoRoot(), "fixtures", "civil-delivery", "estimate", "nti-urban-082025.xlsx");

        private static CatalogSnapshot? _snapshot;
        public static CatalogSnapshot Snapshot() =>
            _snapshot ??= PriceBookXlsxLoader.Load(PriceBookPath, "nti-urban-082025");

        public static NeutralQuantityRecord Record(
            string id, string code, double value, string unit, string kind = "length",
            string handle = "AB12", string? xref = null, string? ruleKey = "kerb",
            double[]? bbox = null, string drawingHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", string layer = "KERB",
            string? civilIdentity = null, double? stationFrom = null, double? stationTo = null,
            string? method = null) => new()
        {
            RecordId = id,
            ProjectProfileId = "6422",
            RunId = "test-run",
            Source = new QuantitySource
            {
                Drawing = "PD.dwg",
                DrawingPath = @"C:\test\PD.dwg",
                DrawingHash = drawingHash,
                Handle = handle,
                EntityType = "LWPOLYLINE",
                Layer = layer,
                Xref = xref,
                CivilIdentity = civilIdentity,
                StationFrom = stationFrom,
                StationTo = stationTo,
            },
            Measurement = new QuantityMeasurement
            {
                Kind = kind,
                Method = method ?? "polyline-length",
                RawValue = value,
                Unit = unit,
                GeometryEvidence = bbox,
            },
            Classification = ClassificationFor(code, ruleKey, unit),
        };

        private static QuantityClassification ClassificationFor(
            string code, string? ruleKey, string unit)
        {
            if (string.IsNullOrWhiteSpace(code))
                return new QuantityClassification { RuleKey = ruleKey };
            var snapshot = Snapshot();
            var item = snapshot.Items.TryGetValue(code, out var actual)
                ? actual
                : new CatalogItem { Code = code, Description = "test item", UnitRaw = unit };
            return new QuantityClassification
            {
                CandidateCatalogCode = code,
                RuleKey = ruleKey,
                ApprovedCatalogId = snapshot.SnapshotId,
                ApprovedCatalogHash = snapshot.FileHash,
                ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(item),
                MappingApprovedBy = "test-engineer",
                MappingApprovedAtUtc = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
            };
        }

        public static ProjectProfile Profile()
        {
            var profile = new ProjectProfile { ProfileId = "6422" };
            profile.Estimate.QuantitySources.SourceScopePolicy =
                EstimatePreflightPolicy.DiscoverAllSourceScopePolicy;
            profile.Estimate.QuantitySources.XrefPolicy =
                EstimatePreflightPolicy.IncludeXrefsPolicy;
            var snapshot = Snapshot();
            profile.Estimate.Catalog.CatalogFile = PriceBookPath;
            profile.Estimate.Catalog.CatalogFileHash = snapshot.FileHash;
            profile.Estimate.Pricing.PriceBookSnapshotId = snapshot.SnapshotId;
            profile.Estimate.Pricing.PriceBookHash = snapshot.FileHash;
            profile.Estimate.PriceBooks.Add(new ProjectProfile.EstimateProfile.PriceBookEntry
            {
                Id = snapshot.SnapshotId,
                File = PriceBookPath,
                FileHash = snapshot.FileHash,
            });
            return profile;
        }

        public static CatalogSnapshot SnapshotWithPrice(
            string code, string unit, decimal price) => new()
        {
            SnapshotId = Snapshot().SnapshotId,
            FileHash = Snapshot().FileHash,
            Items =
            {
                [code] = new CatalogItem
                {
                    Code = code,
                    Description = "test item",
                    UnitRaw = unit,
                },
            },
            Prices =
            {
                [code] = new PriceRecord
                {
                    Code = code,
                    Price = price,
                    PriceBookId = Snapshot().SnapshotId,
                    SourceHash = Snapshot().FileHash,
                },
            },
        };

        public static void BindApprovalToActiveCatalog(
            ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule rule)
        {
            var snapshot = Snapshot();
            rule.ApprovedCatalogId = snapshot.SnapshotId;
            rule.ApprovedCatalogHash = snapshot.FileHash;
            rule.ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(
                snapshot.Items[rule.CandidateCatalogCode!]);
            rule.ApprovedBy ??= "test-engineer";
            rule.ApprovedAtUtc ??= new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);
        }
    }

    public class PriceBookLoaderTests
    {
        [Fact]
        public void RealPriceBook_LoadsFullCatalog()
        {
            var snapshot = EstimateFixtures.Snapshot();

            // Verified against the source with openpyxl: 9,082 U-coded rows total =
            // 8,615 real items + 467 chapter-note rows (unit 'הערה') that the loader
            // rightly excludes.
            snapshot.Items.Count.Should().Be(8615);
            snapshot.FileHash.Should().Be("90da59809602c4127fcf0b91c3f037c2b98d7b93288beba2c851e3a3550f313c",
                "snapshot identity is the exact source file hash");
            snapshot.PublicationNote.Should().Contain("2025");
        }

        [Fact]
        public void KnownItem_HasExactCatalogPrice()
        {
            var snapshot = EstimateFixtures.Snapshot();

            snapshot.Items.Should().ContainKey("U51.04.0080");
            snapshot.Items["U51.04.0080"].Unit.Canonical.Should().Be("m2");
            snapshot.Prices["U51.04.0080"].Price.Should().BeApproximately(45.1136m, 0.0001m);
        }

        [Fact]
        public void DashPrice_IsMissingNeverZero()
        {
            var snapshot = EstimateFixtures.Snapshot();

            snapshot.Prices.Should().ContainKey("U02.01.0055");
            snapshot.Prices["U02.01.0055"].IsMissing.Should().BeTrue();
            snapshot.Prices["U02.01.0055"].Price.Should().BeNull("'-' means MISSING_PRICE, not 0");
        }

        [Fact]
        public void ChapterNoteRows_AreNotItems()
        {
            var snapshot = EstimateFixtures.Snapshot();
            // U02.01.0005 is a chapter-note row (unit 'הערה', price 0) in the source book.
            snapshot.Items.Should().NotContainKey("U02.01.0005");
        }
    }

    public class UnitTests
    {
        [Theory]
        [InlineData("מ\"ר", "m2", UnitDimension.Area)]
        [InlineData("מ״ר", "m2", UnitDimension.Area)]
        [InlineData(" מ\"ר ", "m2", UnitDimension.Area)]
        [InlineData("מ\"ק", "m3", UnitDimension.Volume)]
        [InlineData("מטר", "m", UnitDimension.Length)]
        [InlineData("מ'", "m", UnitDimension.Length)]
        [InlineData("יח'", "unit", UnitDimension.Count)]
        [InlineData("קומפ'", "comp", UnitDimension.Compound)]
        [InlineData("טון", "ton", UnitDimension.Mass)]
        [InlineData("דונם", "dunam", UnitDimension.Area)]
        [InlineData("הערה", "note", UnitDimension.Note)]
        public void Parse_HandlesHebrewSpellings(string raw, string canonical, UnitDimension dim)
        {
            var u = Units.Parse(raw);
            u.Canonical.Should().Be(canonical);
            u.Dimension.Should().Be(dim);
        }

        [Fact]
        public void SameDimensionDifferentUnit_IsNotSameUnit()
        {
            var dunam = Units.Parse("דונם");
            var m2 = Units.Parse("מ\"ר");
            dunam.Dimension.Should().Be(m2.Dimension);
            dunam.SameUnit(m2).Should().BeFalse("dunam→m² conversion must never be silent");
        }
    }

    public class AdjustmentEngineTests
    {
        private static ProjectProfile.EstimateProfile.AdjustmentRule Rule(
            string id, double factor, string status, string? approver = null, int order = 0) => new()
        {
            RuleId = id,
            Factor = factor,
            Status = status,
            ApprovedBy = approver,
            ApprovedAtUtc = approver != null ? DateTime.UtcNow : null,
            Scope = approver != null ? "record:r1" : null,
            Order = order,
        };

        [Fact]
        public void UnconfirmedCandidate_IsNeverApplied()
        {
            var outcome = AdjustmentEngine.Apply(100.0,
                approved: Array.Empty<ProjectProfile.EstimateProfile.AdjustmentRule>(),
                candidates: new[] { Rule("0.90-candidate", 0.90, "UNCONFIRMED") },
                recordId: "r1");

            outcome.BoqValue.Should().Be(100.0, "the 0.90 stays UNVERIFIED_PROJECT_RULE");
            outcome.Steps.Should().BeEmpty();
            outcome.Findings.Should().ContainSingle(f =>
                f.Code == EstimateFindingCodes.AdjustmentUnverified &&
                f.Severity == FindingSeverity.Warning);
        }

        [Fact]
        public void ConfirmedApproved_AppliesInOrder()
        {
            var outcome = AdjustmentEngine.Apply(100.0,
                approved: new[]
                {
                    Rule("second", 1.10, "CONFIRMED", "nataly", order: 2),
                    Rule("first", 0.90, "CONFIRMED", "nataly", order: 1),
                },
                candidates: Array.Empty<ProjectProfile.EstimateProfile.AdjustmentRule>(),
                recordId: "r1");

            outcome.Steps.Should().HaveCount(2);
            outcome.Steps[0].RuleId.Should().Be("first");
            outcome.Steps[0].Output.Should().BeApproximately(90.0, 1e-9);
            outcome.Steps[1].Output.Should().BeApproximately(99.0, 1e-9);
            outcome.BoqValue.Should().BeApproximately(99.0, 1e-9);
        }

        [Fact]
        public void ConfirmedWithoutApprover_IsErrorAndNotApplied()
        {
            var outcome = AdjustmentEngine.Apply(100.0,
                approved: new[] { Rule("bad", 0.5, "CONFIRMED", approver: null) },
                candidates: Array.Empty<ProjectProfile.EstimateProfile.AdjustmentRule>(),
                recordId: "r1");

            outcome.BoqValue.Should().Be(100.0);
            outcome.Findings.Should().ContainSingle(f =>
                f.Code == EstimateFindingCodes.AdjustmentUnverified &&
                f.Severity == FindingSeverity.Error);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("all")]
        [InlineData("layer:KERB")]
        public void ConfirmedAdjustment_WithAbsentOrUnsupportedScope_IsRejected(string? scope)
        {
            var rule = Rule("unsafe-global", 0.9, "CONFIRMED", "nataly");
            rule.Scope = scope;

            var outcome = AdjustmentEngine.Apply(100, new[] { rule },
                Array.Empty<ProjectProfile.EstimateProfile.AdjustmentRule>(),
                "r1", "layer:KERB|length", "U51.01.0250");

            outcome.BoqValue.Should().Be(100);
            outcome.Steps.Should().BeEmpty();
            outcome.Findings.Should().ContainSingle(f =>
                f.Code == EstimateFindingCodes.AdjustmentUnverified &&
                f.Severity == FindingSeverity.Error);
        }

        [Fact]
        public void ExplicitRuleScope_AppliesOnlyToTheMatchingRuleKey()
        {
            var rule = Rule("kerb-only", 0.9, "CONFIRMED", "nataly");
            rule.Scope = "rule:layer:KERB|length";

            var matching = AdjustmentEngine.Apply(100, new[] { rule },
                Array.Empty<ProjectProfile.EstimateProfile.AdjustmentRule>(),
                "r1", "layer:KERB|length", "U51.01.0250");
            var other = AdjustmentEngine.Apply(100, new[] { rule },
                Array.Empty<ProjectProfile.EstimateProfile.AdjustmentRule>(),
                "r2", "layer:PAVING|area", "U51.01.0090");

            matching.BoqValue.Should().Be(90);
            matching.Steps.Should().ContainSingle();
            other.BoqValue.Should().Be(100);
            other.Steps.Should().BeEmpty();
            other.Findings.Should().BeEmpty("a valid scope for another record is not malformed");
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-0.5)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void NonPositiveOrNonFiniteApprovedFactor_IsBlocked(double factor)
        {
            var outcome = AdjustmentEngine.Apply(100,
                new[] { Rule("bad-factor", factor, "CONFIRMED", "nataly") },
                Array.Empty<ProjectProfile.EstimateProfile.AdjustmentRule>(), "r1");

            outcome.BoqValue.Should().Be(100);
            outcome.Steps.Should().BeEmpty();
            outcome.Findings.Should().ContainSingle(f =>
                f.Code == EstimateFindingCodes.AdjustmentUnverified &&
                f.Severity == FindingSeverity.Error);
        }

        [Fact]
        public void ConflictingApprovedAdjustments_AreBothBlocked_NotAppliedByOrder()
        {
            var a = Rule("a", 0.9, "CONFIRMED", "nataly", order: 1);
            var b = Rule("b", 1.1, "CONFIRMED", "nataly", order: 1);

            var outcome = AdjustmentEngine.Apply(100, new[] { a, b },
                Array.Empty<ProjectProfile.EstimateProfile.AdjustmentRule>(), "r1");

            outcome.BoqValue.Should().Be(100);
            outcome.Steps.Should().BeEmpty();
            outcome.Findings.Should().HaveCount(2);
        }
    }

    public class DuplicateRiskDetectorTests
    {
        [Fact]
        public void FullModelWorkingCopy_DoesNotNeedAnAllPairsScan()
        {
            // 07/09 18:22: the 74,881-record scan of the 6422 working copy sat in the
            // pair loop for more than 40 minutes. Records with distinct values must not
            // be compared, and the one real host+XREF pair must still be found.
            var records = new List<NeutralQuantityRecord>();
            for (var i = 0; i < 40000; i++)
                records.Add(EstimateFixtures.Record($"r{i}", "", 10.0 + i * 0.01, "מטר",
                    handle: $"H{i:X}", xref: i % 2 == 0 ? "UT-3D" : null,
                    ruleKey: i % 7 == 0 ? null : $"rule-{i % 5}",
                    bbox: new[] { i * 1.0, 0.0, i + 0.5, 0.5 }));
            records.Add(EstimateFixtures.Record("host-dup", "", 10.0 + 124 * 0.01, "מטר",
                handle: "H7C", xref: null, ruleKey: "rule-4",
                bbox: new[] { 124.0, 0.0, 124.5, 0.5 }));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var findings = DuplicateRiskDetector.Detect(records);
            watch.Stop();

            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            findings.Where(f => f.Code == EstimateFindingCodes.XrefDoubleCountRisk).Should().ContainSingle()
                .Which.AffectedRecordIds.Should().BeEquivalentTo(new[] { "host-dup", "r124" });
            findings.Should().NotContain(f => f.Code == EstimateFindingCodes.DuplicateSource);
        }

        [Fact]
        public void SameHandleTwice_IsFlagged()
        {
            var records = new List<NeutralQuantityRecord>
            {
                EstimateFixtures.Record("r1", "U51.01.0250", 100, "מטר", handle: "AAAA"),
                EstimateFixtures.Record("r2", "U51.01.0250", 100, "מטר", handle: "AAAA"),
            };
            var findings = DuplicateRiskDetector.Detect(records);
            findings.Should().ContainSingle(f => f.Code == EstimateFindingCodes.DuplicateSource);
        }

        [Fact]
        public void HostPlusXref_IsDoubleCountRisk()
        {
            var records = new List<NeutralQuantityRecord>
            {
                EstimateFixtures.Record("r1", "U51.01.0250", 100, "מטר", handle: "CCCC", xref: null,
                    bbox: new double[] { 0, 0, 100, 0 }),
                EstimateFixtures.Record("r2", "U51.01.0250", 100, "מטר", handle: "BR1:CCCC", xref: "UT-3D.dwg",
                    bbox: new double[] { 0, 0, 100, 0 }, drawingHash: "hash-b"),
            };
            var findings = DuplicateRiskDetector.Detect(records);
            findings.Should().Contain(f => f.Code == EstimateFindingCodes.XrefDoubleCountRisk);
        }

        [Fact]
        public void CoincidentHandleTextAcrossHostAndXref_IsNotEvidenceOfDuplication()
        {
            var records = new List<NeutralQuantityRecord>
            {
                EstimateFixtures.Record("r1", "U51.01.0250", 100, "מטר", handle: "CCCC"),
                EstimateFixtures.Record("r2", "U51.01.0250", 101, "מטר", handle: "BR1/CCCC",
                    xref: "UT-3D.dwg", drawingHash: new string('b', 64)),
            };

            DuplicateRiskDetector.Detect(records)
                .Should().NotContain(f => f.Code == EstimateFindingCodes.XrefDoubleCountRisk);
        }

        [Fact]
        public void SameExternalEntityInsertedThroughTwoXrefPaths_IsDoubleCountRisk()
        {
            var sourceHash = new string('b', 64);
            var records = new List<NeutralQuantityRecord>
            {
                EstimateFixtures.Record("r1", "U51.01.0250", 100, "מטר",
                    handle: "A1/CCCC", xref: "SITE-A",
                    bbox: new double[] { 0, 0, 100, 0 }, drawingHash: sourceHash),
                EstimateFixtures.Record("r2", "U51.01.0250", 100, "מטר",
                    handle: "B2/CCCC", xref: "SITE-B",
                    bbox: new double[] { 0, 0, 100, 0 }, drawingHash: sourceHash),
            };

            DuplicateRiskDetector.Detect(records).Should().ContainSingle(f =>
                    f.Code == EstimateFindingCodes.XrefDoubleCountRisk)
                .Which.AffectedRecordIds.Should().BeEquivalentTo("r1", "r2");
        }

        [Fact]
        public void DifferentEntitiesFromSameXrefFile_AreNotTreatedAsRepeatedInsertion()
        {
            var sourceHash = new string('b', 64);
            var records = new List<NeutralQuantityRecord>
            {
                EstimateFixtures.Record("r1", "U51.01.0250", 100, "מטר",
                    handle: "A1/CCCC", xref: "SITE-A",
                    bbox: new double[] { 0, 0, 100, 0 }, drawingHash: sourceHash),
                EstimateFixtures.Record("r2", "U51.01.0250", 100, "מטר",
                    handle: "B2/DDDD", xref: "SITE-B",
                    bbox: new double[] { 0, 0, 100, 0 }, drawingHash: sourceHash),
            };

            DuplicateRiskDetector.Detect(records)
                .Should().NotContain(f => f.Code == EstimateFindingCodes.XrefDoubleCountRisk);
        }

        [Fact]
        public void OverlappingSameRuleAreas_AreFlagged()
        {
            var records = new List<NeutralQuantityRecord>
            {
                EstimateFixtures.Record("r1", "U51.01.0090", 500, "מ\"ר", kind: "area", handle: "H1",
                    ruleKey: "milling", bbox: new double[] { 0, 0, 10, 10 }),
                EstimateFixtures.Record("r2", "U51.01.0090", 300, "מ\"ר", kind: "area", handle: "H2",
                    ruleKey: "milling", bbox: new double[] { 5, 5, 15, 15 }),
                EstimateFixtures.Record("r3", "U51.01.0090", 300, "מ\"ר", kind: "area", handle: "H3",
                    ruleKey: "milling", bbox: new double[] { 100, 100, 110, 110 }),
            };
            var findings = DuplicateRiskDetector.Detect(records);
            findings.Should().ContainSingle(f => f.Code == EstimateFindingCodes.OverlapRisk)
                .Which.AffectedRecordIds.Should().BeEquivalentTo(new[] { "r1", "r2" });
        }

        [Fact]
        public void OverlappingAreasAcrossRules_SameApprovedCatalogCodeAndUnit_AreFlagged()
        {
            var records = new List<NeutralQuantityRecord>
            {
                EstimateFixtures.Record("r1", "U51.01.0090", 500, "מ\"ר", kind: "area", handle: "H1",
                    ruleKey: "layer:PAVE-A|area", bbox: new double[] { 0, 0, 10, 10 }),
                EstimateFixtures.Record("r2", "U51.01.0090", 300, "מ\"ר", kind: "area", handle: "H2",
                    ruleKey: "layer:PAVE-B|area", bbox: new double[] { 5, 5, 15, 15 }),
            };

            var finding = DuplicateRiskDetector.Detect(records).Should().ContainSingle(f =>
                f.Code == EstimateFindingCodes.CrossSourceDuplicateRisk).Subject;

            finding.AffectedRecordIds.Should().BeEquivalentTo(new[] { "r1", "r2" });
            finding.Title.Should().Contain("U51.01.0090").And.Contain("m2");
            finding.Message.Should().Contain("layer:PAVE-A|area")
                .And.Contain("layer:PAVE-B|area");
        }

        [Fact]
        public void CrossRuleOverlap_DifferentCatalogOrUnboundSuggestion_IsNotClaimedAsSameBoqWork()
        {
            var different = new List<NeutralQuantityRecord>
            {
                EstimateFixtures.Record("r1", "U51.01.0090", 500, "מ\"ר", kind: "area", handle: "H1",
                    ruleKey: "layer:PAVE-A|area", bbox: new double[] { 0, 0, 10, 10 }),
                EstimateFixtures.Record("r2", "U99.01.0002", 300, "מ\"ר", kind: "area", handle: "H2",
                    ruleKey: "layer:PAVE-B|area", bbox: new double[] { 5, 5, 15, 15 }),
            };
            var unbound = EstimateFixtures.Record(
                "r3", "U51.01.0090", 300, "מ\"ר", kind: "area", handle: "H3",
                ruleKey: "layer:PAVE-C|area", bbox: new double[] { 5, 5, 15, 15 });
            unbound.Classification.ApprovedCatalogId = null;
            unbound.Classification.ApprovedCatalogHash = null;
            unbound.Classification.ApprovedCatalogItemFingerprint = null;
            different.Add(unbound);

            DuplicateRiskDetector.Detect(different).Should().NotContain(f =>
                f.Code == EstimateFindingCodes.CrossSourceDuplicateRisk);
        }

        [Fact]
        public void SameCorridorHandle_DifferentMaterialOrRun_IsNotADuplicate()
        {
            var records = new List<NeutralQuantityRecord>
            {
                EstimateFixtures.Record("maza", "U51.03.0010", 100, "מ\"ק", kind: "volume",
                    handle: "C001", layer: "corridor:MAZA", civilIdentity: "3000",
                    stationFrom: 0, stationTo: 100, method: "corridor-qto-avg-end-area"),
                EstimateFixtures.Record("asphalt", "U51.03.0010", 20, "מ\"ק", kind: "volume",
                    handle: "C001", layer: "corridor:ASF-5-19-70", civilIdentity: "3000",
                    stationFrom: 0, stationTo: 100, method: "corridor-qto-avg-end-area"),
                EstimateFixtures.Record("maza-run-2", "U51.03.0010", 75, "מ\"ק", kind: "volume",
                    handle: "C001", layer: "corridor:MAZA", civilIdentity: "3000",
                    stationFrom: 200, stationTo: 300, method: "corridor-qto-avg-end-area"),
            };

            DuplicateRiskDetector.Detect(records)
                .Should().NotContain(f => f.Code == EstimateFindingCodes.DuplicateSource);
        }
    }

    public class EstimateBuilderTests
    {
        [Fact]
        public void MappedPricedLine_GetsRealTotal()
        {
            // U51.01.0250 = kerb demolition, unit מטר, price 10.52324 in 08/2025.
            var result = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("r1", "U51.01.0250", 250.0, "מטר") },
                EstimateFixtures.Snapshot(), EstimateFixtures.Profile());

            var line = result.Lines.Single();
            line.PriceStatus.Should().Be(PriceStatus.Priced);
            line.IncludedInTotals.Should().BeTrue();
            line.BoqQuantity.Should().Be(250.0, "no confirmed adjustments configured");
            line.Total.Should().Be(Math.Round(250.0m * line.Price!.Value, 2));
            result.CleanTotal.Should().Be(line.Total!.Value);
            EstimatePreflightPolicy.CanExport(result).Should().BeTrue();
        }

        [Fact]
        public void MissingPrice_ExcludedFromTotals_NeverZero()
        {
            // U40.02.2500 (smart shelter) is '-' in the 08/2025 book — the exact case
            // from the Judgment-2 evidence.
            var result = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("r1", "U40.02.2500", 6, "יח'", kind: "count") },
                EstimateFixtures.Snapshot(), EstimateFixtures.Profile());

            var line = result.Lines.Single();
            line.PriceStatus.Should().Be(PriceStatus.MissingPrice);
            line.Price.Should().BeNull();
            line.Total.Should().BeNull("a missing price must never become 0");
            line.IncludedInTotals.Should().BeFalse();
            line.Findings.Should().Contain(f => f.Code == EstimateFindingCodes.MissingPrice);
            result.CleanTotal.Should().Be(0);
            result.ExcludedLineCount.Should().Be(1);
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse(
                "a partial estimate with MISSING_PRICE is an audit draft, not an export");
        }

        [Fact]
        public void PositivePriceFromAnotherSnapshot_IsUnverifiedAndNeverBecomesMoney()
        {
            const string code = "U51.01.0250";
            var snapshot = EstimateFixtures.SnapshotWithPrice(code, "מטר", 42.50m);
            snapshot.Prices[code] = new PriceRecord
            {
                Code = code,
                Price = 42.50m,
                PriceBookId = snapshot.SnapshotId,
                SourceHash = new string('b', 64),
            };

            var record = EstimateFixtures.Record("r1", code, 10, "מטר");
            record.Classification.ApprovedCatalogItemFingerprint =
                CatalogIdentity.ItemFingerprint(snapshot.Items[code]);
            var result = EstimateBuilder.Build(
                new[] { record }, snapshot, EstimateFixtures.Profile());

            var line = result.Lines.Should().ContainSingle().Subject;
            line.Price.Should().BeNull();
            line.Total.Should().BeNull();
            line.IncludedInTotals.Should().BeFalse();
            line.Findings.Should().Contain(f =>
                f.Code == EstimateFindingCodes.PriceSourceUnverified &&
                f.Severity == FindingSeverity.Error);
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
        }

        [Fact]
        public void UnitMismatch_IsReviewAndExcluded()
        {
            // Measured in meters against an m² catalog item.
            var result = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("r1", "U51.01.0090", 100, "מטר") },
                EstimateFixtures.Snapshot(), EstimateFixtures.Profile());

            var line = result.Lines.Single();
            line.Status.Should().Be(DeliveryStatus.ReviewRequired);
            line.IncludedInTotals.Should().BeFalse();
            line.Findings.Should().Contain(f => f.Code == EstimateFindingCodes.UnitMismatch);
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
        }

        [Fact]
        public void UnmappedRecord_IsVisibleReviewLine()
        {
            var result = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("r1", code: null!, 42, "מטר") },
                EstimateFixtures.Snapshot(), EstimateFixtures.Profile());

            var line = result.Lines.Single();
            line.PriceStatus.Should().Be(PriceStatus.Unmapped);
            line.Status.Should().Be(DeliveryStatus.ReviewRequired);
            line.Findings.Should().Contain(f => f.Code == EstimateFindingCodes.Unmapped);
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
        }

        [Fact]
        public void UnknownUnit_IsAClosedExportGate()
        {
            var result = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("r1", "U51.01.0250", 42, "furlong") },
                EstimateFixtures.Snapshot(), EstimateFixtures.Profile());

            result.Lines.Should().ContainSingle().Which.Findings
                .Should().Contain(f => f.Code == EstimateFindingCodes.UnitUnknown);
            EstimatePreflightPolicy.ExportBlockingReasons(result)
                .Should().Contain(EstimateFindingCodes.UnitUnknown);
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
        }

        [Fact]
        public void AnyUnresolvedReviewFinding_BlocksEvenWhenItsCodeWasNotWhitelisted()
        {
            var review = new DeliveryFinding
            {
                Code = "EST-FUTURE-MONEY-REVIEW",
                Domain = "estimate",
                Severity = FindingSeverity.ReviewRequired,
                Title = "future review gate",
            };
            var result = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("r1", "U51.01.0250", 42, "מטר") },
                EstimateFixtures.Snapshot(), EstimateFixtures.Profile(),
                preflightFindings: new[] { review });

            result.Status.Should().Be(DeliveryStatus.ReviewRequired);
            EstimatePreflightPolicy.BlockingFindings(result)
                .Should().Contain(f => f.Code == review.Code);
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
        }

        [Fact]
        public void LegacyNeutralRecordWithCodeButNoCatalogBinding_IsUnmapped()
        {
            var record = EstimateFixtures.Record("legacy", "U51.01.0250", 42, "מטר");
            record.Classification.ApprovedCatalogId = null;
            record.Classification.ApprovedCatalogHash = null;
            record.Classification.ApprovedCatalogItemFingerprint = null;

            var result = EstimateBuilder.Build(
                new[] { record }, EstimateFixtures.Snapshot(), EstimateFixtures.Profile());

            var line = result.Lines.Should().ContainSingle().Subject;
            line.PriceStatus.Should().Be(PriceStatus.Unmapped);
            line.IncludedInTotals.Should().BeFalse();
            line.Findings.Should().ContainSingle(f => f.Code == EstimateFindingCodes.Unmapped);
        }

        [Fact]
        public void NullSourceAndXrefPolicies_AreExplicitGlobalBlockers()
        {
            var profile = new ProjectProfile { ProfileId = "6422" };

            var findings = EstimatePreflightPolicy.ValidateSourcePolicies(profile);

            findings.Should().Contain(f =>
                f.Code == EstimateFindingCodes.SourceScopePolicyUnapproved &&
                f.Severity == FindingSeverity.ReviewRequired);
            findings.Should().Contain(f =>
                f.Code == EstimateFindingCodes.XrefPolicyUnapproved &&
                f.Severity == FindingSeverity.ReviewRequired);
            findings.Should().OnlyContain(f => EstimatePreflightPolicy.IsBlocking(f));

            var result = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("r1", "U51.01.0250", 42, "מטר") },
                EstimateFixtures.Snapshot(), profile);
            result.Status.Should().Be(DeliveryStatus.ReviewRequired);
            result.Lines.Should().OnlyContain(l => !l.IncludedInTotals && l.Total == null);
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
        }

        [Fact]
        public void HostOnly_IsRejectedBecauseCompleteEstimateRequiresXrefs()
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.QuantitySources.XrefPolicy = EstimatePreflightPolicy.HostOnlyXrefPolicy;

            EstimatePreflightPolicy.ValidateSourcePolicies(profile)
                .Should().ContainSingle(f =>
                    f.Code == EstimateFindingCodes.XrefPolicyUnapproved &&
                    f.Severity == FindingSeverity.Error);
        }

        [Fact]
        public void RulesOnlySourcePolicy_IsRejectedAsDataLossRisk()
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.QuantitySources.SourceScopePolicy =
                EstimatePreflightPolicy.RulesOnlySourceScopePolicy;

            var finding = EstimatePreflightPolicy.ValidateSourcePolicies(profile)
                .Should().ContainSingle(f =>
                    f.Code == EstimateFindingCodes.SourceScopePolicyUnapproved).Subject;
            finding.Severity.Should().Be(FindingSeverity.Error);
            finding.Title.Should().Contain("rules-only");
            finding.Message.Should().Contain("תמיד סורק את כל הישויות הנתמכות");
        }

        [Fact]
        public void ApprovedOverride_WinsOverSnapshot_WithFullAudit()
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.ProjectOverrides.Add(new ProjectProfile.EstimateProfile.PriceOverride
            {
                ItemCode = "U40.02.2500",
                Price = 30000m,
                Source = "הצעת ספק תחנות חכמות 07/2026",
                Reason = "אין מחיר מחירון לסככה חכמה",
                ApprovedBy = "nataly",
                ApprovedAtUtc = DateTime.UtcNow,
            });

            var result = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("r1", "U40.02.2500", 6, "יח'", kind: "count") },
                EstimateFixtures.Snapshot(), profile);

            var line = result.Lines.Single();
            line.PriceStatus.Should().Be(PriceStatus.ProjectOverride);
            line.Price.Should().Be(30000m);
            line.IncludedInTotals.Should().BeTrue();
            line.Total.Should().Be(180000m);
        }

        [Fact]
        public void OverrideWithoutApprovalTimestamp_FailsClosedWithoutCatalogFallback()
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.ProjectOverrides.Add(new ProjectProfile.EstimateProfile.PriceOverride
            {
                ItemCode = "U51.01.0250",
                Price = 999m,
                Source = "project quote",
                Reason = "project-specific price",
                ApprovedBy = "nataly",
                ApprovedAtUtc = null,
            });

            var result = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("r1", "U51.01.0250", 10, "מטר") },
                EstimateFixtures.Snapshot(), profile);

            var line = result.Lines.Should().ContainSingle().Subject;
            line.Price.Should().BeNull(
                "an incomplete override must not apply and must not silently fall back to the catalog");
            line.Findings.Should().ContainSingle(f =>
                f.Code == EstimateFindingCodes.OverrideIncomplete &&
                f.Severity == FindingSeverity.Error);
            line.Status.Should().Be(DeliveryStatus.Failed);
            line.IncludedInTotals.Should().BeFalse();
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-25")]
        public void NonPositiveApprovedOverride_IsRejectedWithoutCatalogFallback(string price)
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.ProjectOverrides.Add(new ProjectProfile.EstimateProfile.PriceOverride
            {
                ItemCode = "U51.01.0250",
                Price = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture),
                Source = "project quote",
                Reason = "project-specific price",
                ApprovedBy = "nataly",
                ApprovedAtUtc = DateTime.UtcNow,
            });

            var line = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("r1", "U51.01.0250", 10, "מטר") },
                EstimateFixtures.Snapshot(), profile).Lines.Single();

            line.Price.Should().BeNull();
            line.IncludedInTotals.Should().BeFalse();
            line.Findings.Should().Contain(f => f.Code == EstimateFindingCodes.OverrideIncomplete);
        }

        [Fact]
        public void DuplicateApprovedOverrides_AreAmbiguousAndProduceNoMoney()
        {
            var profile = EstimateFixtures.Profile();
            foreach (var price in new[] { 95m, 125m })
                profile.Estimate.ProjectOverrides.Add(new ProjectProfile.EstimateProfile.PriceOverride
                {
                    ItemCode = "U51.01.0250", Price = price, Source = "quote",
                    Reason = "project price", ApprovedBy = "nataly", ApprovedAtUtc = DateTime.UtcNow,
                });

            var result = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("r1", "U51.01.0250", 10, "מטר") },
                EstimateFixtures.Snapshot(), profile);

            result.Lines.Single().Price.Should().BeNull();
            result.Lines.Single().IncludedInTotals.Should().BeFalse();
            result.Findings.Should().Contain(f =>
                f.Code == EstimateFindingCodes.ConfigurationAmbiguous);
        }

        [Fact]
        public void CandidateAdjustment_SurfacesButDoesNotChangeQuantity()
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.CandidateAdjustments.Add(new ProjectProfile.EstimateProfile.AdjustmentRule
            {
                RuleId = "quantity-0.90-early-design",
                Factor = 0.90,
                Status = "UNCONFIRMED",
            });

            var result = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("r1", "U51.01.0250", 100, "מטר") },
                EstimateFixtures.Snapshot(), profile);

            var line = result.Lines.Single();
            line.BoqQuantity.Should().Be(100.0);
            line.Adjustments.Should().BeEmpty();
            line.Findings.Should().Contain(f => f.Code == EstimateFindingCodes.AdjustmentUnverified);
            line.IncludedInTotals.Should().BeTrue("a pending candidate is a review item, not a blocker");
        }

        [Fact]
        public void DuplicatePreflight_TravelsIntoResult_AndExcludesAffectedMoney()
        {
            var records = new[]
            {
                EstimateFixtures.Record("r1", "U51.01.0250", 100, "מטר", handle: "A"),
                EstimateFixtures.Record("r2", "U51.01.0250", 50, "מטר", handle: "B"),
                EstimateFixtures.Record("safe", "U51.01.0250", 25, "מטר", handle: "C"),
            };
            var duplicate = new DeliveryFinding
            {
                Code = EstimateFindingCodes.DuplicateSource,
                Domain = "estimate",
                Severity = FindingSeverity.ReviewRequired,
                Title = "duplicate",
                AffectedRecordIds = { "r1", "r2" },
            };

            var result = EstimateBuilder.Build(records, EstimateFixtures.Snapshot(),
                EstimateFixtures.Profile(), preflightFindings: new[] { duplicate });

            result.Findings.Should().Contain(f => f.FindingId == duplicate.FindingId);
            result.Lines.Where(l => l.RecordId is "r1" or "r2")
                .Should().OnlyContain(l => !l.IncludedInTotals &&
                                          l.Status == DeliveryStatus.ReviewRequired &&
                                          l.Total == null);
            result.Lines.Single(l => l.RecordId == "safe").IncludedInTotals.Should().BeTrue();
            result.CleanTotal.Should().Be(result.Lines.Single(l => l.RecordId == "safe").Total);
            result.Status.Should().Be(DeliveryStatus.ReviewRequired);
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
        }

        [Fact]
        public void GlobalStaleCorridorFinding_BlocksExportAndSurvivesBuild()
        {
            var stale = new DeliveryFinding
            {
                Code = EstimatePreflightPolicy.CorridorOutOfDateCode,
                Domain = "estimate",
                Severity = FindingSeverity.ReviewRequired,
                Title = "corridor is out of date",
            };

            var result = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("safe", "U51.01.0250", 25, "מטר") },
                EstimateFixtures.Snapshot(), EstimateFixtures.Profile(),
                preflightFindings: new[] { stale });

            result.Findings.Should().Contain(f => f.FindingId == stale.FindingId);
            result.Status.Should().Be(DeliveryStatus.ReviewRequired);
            result.Lines.Should().OnlyContain(l => !l.IncludedInTotals && l.Total == null);
            result.CleanTotal.Should().Be(0m);
            EstimatePreflightPolicy.BlockingFindings(result).Should().ContainSingle();
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
        }

        [Fact]
        public void SuppliedPreflight_CannotSuppressFreshDuplicateDetection()
        {
            var records = new[]
            {
                EstimateFixtures.Record("r1", "U51.01.0250", 10, "מטר", handle: "DUP"),
                EstimateFixtures.Record("r2", "U51.01.0250", 10, "מטר", handle: "DUP"),
            };
            var benign = new DeliveryFinding
            {
                Code = EstimateFindingCodes.SourceMissing,
                Domain = "estimate",
                Severity = FindingSeverity.Info,
                Title = "benign supplied finding",
            };

            var result = EstimateBuilder.Build(records, EstimateFixtures.Snapshot(),
                EstimateFixtures.Profile(), preflightFindings: new[] { benign });

            result.Findings.Should().Contain(f => f.FindingId == benign.FindingId);
            result.Findings.Should().ContainSingle(f => f.Code == EstimateFindingCodes.DuplicateSource);
            result.Lines.Should().OnlyContain(l => !l.IncludedInTotals);
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
        }

        [Fact]
        public void CorridorQtoFailure_IsAGlobalErrorAndBlocksExport()
        {
            var failed = new DeliveryFinding
            {
                Code = EstimatePreflightPolicy.CorridorQtoFailedCode,
                Domain = "estimate",
                Severity = FindingSeverity.Error,
                Title = "corridor QTO failed",
            };

            var result = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("safe", "U51.01.0250", 25, "מטר") },
                EstimateFixtures.Snapshot(), EstimateFixtures.Profile(),
                preflightFindings: new[] { failed });

            result.Status.Should().Be(DeliveryStatus.Failed);
            result.Lines.Should().OnlyContain(l => !l.IncludedInTotals && l.Total == null);
            result.CleanTotal.Should().Be(0m);
            EstimatePreflightPolicy.BlockingFindings(result).Should().ContainSingle()
                .Which.Code.Should().Be(EstimatePreflightPolicy.CorridorQtoFailedCode);
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
        }

        [Fact]
        public void ScanAssembly_PreservesModelRecordsAndGlobalModelBlockers()
        {
            var drawing = EstimateFixtures.Record(
                "drawing", "U51.01.0250", 10, "מטר", handle: "D");
            drawing.Status = DeliveryStatus.Ready;
            var model = EstimateFixtures.Record(
                "model", null!, 20, "מ\"ק", kind: "volume", handle: "C",
                layer: "corridor:Base", civilIdentity: "C1/BL1",
                stationFrom: 0, stationTo: 10, method: "corridor-qto-avg-end-area");
            model.Status = DeliveryStatus.ReviewRequired;
            var blocker = new DeliveryFinding
            {
                Code = EstimatePreflightPolicy.CorridorQtoFailedCode,
                Domain = "estimate",
                Severity = FindingSeverity.Error,
                Title = "model QTO incomplete",
            };

            var scan = EstimateWorkflowService.AssembleScan(
                "run", "6422", @"C:\\drawings\\A.dwg", "profile-hash",
                new[] { drawing }, Array.Empty<DeliveryFinding>(), 1, false,
                new[] { model }, new[] { blocker });

            scan.Records.Select(r => r.RecordId).Should().Equal("drawing", "model");
            scan.Findings.Should().Contain(f => f.FindingId == blocker.FindingId);
            scan.Status.Should().Be(DeliveryStatus.Failed);
            scan.ProjectProfileId.Should().Be("6422");
            scan.SourceDrawing.Should().Be(@"C:\\drawings\\A.dwg");
        }

        [Fact]
        public void EstimateRunManifest_HashesEachInputDrawingOnce_ForTensOfThousandsOfRecords()
        {
            // 07/09 18:25 -> 18:56: the 74,900-record scan of the 6422 working copy
            // supplied one manifest input per record and the host DWG was hashed for
            // every one of them. Same manifest, one read per distinct file.
            var tmp = Path.Combine(Path.GetTempPath(), "mcd-estimate-manifest", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var drawingPath = Path.Combine(tmp, "WORK.dwg");
            var bytes = new byte[8 * 1024 * 1024];
            new Random(6422).NextBytes(bytes);
            File.WriteAllBytes(drawingPath, bytes);
            var drawingHash = ArtifactHash.Sha256OfFile(drawingPath);
            try
            {
                var records = new List<NeutralQuantityRecord>();
                for (var i = 0; i < 30000; i++)
                {
                    records.Add(new NeutralQuantityRecord
                    {
                        RecordId = $"r{i}",
                        ProjectProfileId = "6422",
                        RunId = "estimate-extract-scale",
                        Source = new QuantitySource
                        {
                            Drawing = "WORK.dwg",
                            DrawingPath = drawingPath,
                            DrawingHash = drawingHash,
                            Handle = $"H{i:X}",
                            EntityType = "LWPOLYLINE",
                            Layer = "KERB",
                        },
                        Measurement = new QuantityMeasurement
                        {
                            Kind = "length",
                            Method = "polyline-length",
                            RawValue = 10.0 + i * 0.01,
                            Unit = "מטר",
                        },
                        Classification = new QuantityClassification { RuleKey = "kerb" },
                    });
                }
                var scan = EstimateWorkflowService.AssembleScan(
                    "estimate-extract-scale", "6422", drawingPath, "profile-hash",
                    records, Array.Empty<DeliveryFinding>(), records.Count, true,
                    sourceDrawingHash: drawingHash);
                var inputs = EstimateWorkflowService.ManifestInputs(scan).ToList();
                inputs.Should().HaveCount(1, "every record names the same saved drawing and hash");

                var findings = EstimateWorkflowService.ManifestFindings(scan);
                var counts = EstimateWorkflowService.ScanManifestCounts(scan, findings);
                var identity = new PluginRuntimeIdentity(
                    "1.3.39.0", "7df13b589764a2e5ae8bfc495e0df90a3b63d390",
                    "1.2.49", new string('a', 64));
                var repeated = Enumerable.Range(0, 30000)
                    .Select(_ => new RunManifestInput(drawingPath, drawingHash))
                    .ToList();

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var manifest = RuntimeRunManifestService.Compose(
                    scan.RunId, "estimate", "extract",
                    scan.ProjectProfileId, scan.ProjectProfileHash,
                    scan.Status, counts, findings, repeated,
                    new[] { "neutral_quantity_records.json" }, identity, "26.0");
                watch.Stop();

                watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15),
                    "30,000 inputs of one 8 MB file must read it once, not 30,000 times");
                manifest.InputDrawings.Should().Equal(drawingPath);
                manifest.InputHashes.Should().Equal(drawingHash);
                manifest.InputHashesByPath[drawingPath].Should().Be(drawingHash);
                var conflicting = () => RuntimeRunManifestService.Compose(
                    scan.RunId, "estimate", "extract",
                    scan.ProjectProfileId, scan.ProjectProfileHash,
                    scan.Status, counts, findings,
                    new[] { new RunManifestInput(drawingPath, drawingHash), new RunManifestInput(drawingPath, new string('c', 64)) },
                    new[] { "neutral_quantity_records.json" }, identity, "26.0");
                conflicting.Should().Throw<InvalidDataException>("a changed input is still refused after the first proof");
            }
            finally
            {
                try { Directory.Delete(tmp, recursive: true); } catch { }
            }
        }

        [Fact]
        public void EstimateRunManifest_ProjectsIdentityInputsCountsAndAllFindings()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "mcd-estimate-manifest", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var drawingPath = Path.Combine(tmp, "A.dwg");
            File.WriteAllText(drawingPath, "drawing-bytes");

            try
            {
                var recordFinding = new DeliveryFinding
                {
                    Code = EstimateFindingCodes.Unmapped,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "record finding",
                };
                var globalFinding = new DeliveryFinding
                {
                    Code = EstimatePreflightPolicy.CorridorQtoFailedCode,
                    Domain = "estimate",
                    Severity = FindingSeverity.Error,
                    Title = "global blocker",
                };
                var record = EstimateFixtures.Record(
                    "drawing", null!, 12, "מטר", drawingHash: new string('b', 64));
                record.Findings.Add(recordFinding);
                record.Status = DeliveryStatus.ReviewRequired;
                var scan = EstimateWorkflowService.AssembleScan(
                    "estimate-extract-test", "6422", drawingPath, "profile-hash",
                    new[] { record }, new[] { globalFinding }, 77, true);
                var findings = EstimateWorkflowService.ManifestFindings(scan);
                var counts = EstimateWorkflowService.ScanManifestCounts(scan, findings);
                var identity = new PluginRuntimeIdentity(
                    "1.3.4.0", "7df13b589764a2e5ae8bfc495e0df90a3b63d390",
                    "1.2.14", new string('a', 64));

                var manifest = RuntimeRunManifestService.Compose(
                    scan.RunId, "estimate", "extract",
                    scan.ProjectProfileId, scan.ProjectProfileHash,
                    scan.Status, counts, findings,
                    EstimateWorkflowService.ManifestInputs(scan),
                    new[] { "neutral_quantity_records.json", "quantity_preflight.json" },
                    identity, "26.0");

                manifest.Feature.Should().Be("estimate");
                manifest.Operation.Should().Be("extract");
                manifest.ResultStatus.Should().Be(DeliveryStatus.Failed);
                manifest.ProjectProfileId.Should().Be("6422");
                manifest.ProjectProfileHash.Should().Be("profile-hash");
                manifest.PluginBuildVersion.Should().Be("1.3.4.0");
                manifest.PluginPackageRevision.Should().Be("1.2.14");
                manifest.PluginAssemblySha256.Should().Be(new string('a', 64));
                manifest.InputDrawings.Should().Contain(drawingPath).And.Contain(@"C:\test\PD.dwg");
                manifest.InputHashes.Should().Contain(ArtifactHash.Sha256OfFile(drawingPath))
                    .And.Contain(new string('b', 64));
                manifest.InputHashesByPath[drawingPath].Should().Be(
                    ArtifactHash.Sha256OfFile(drawingPath),
                    "the host drawing path must be bound to the host drawing bytes, not a record hash");
                manifest.InputHashesByPath[@"C:\test\PD.dwg"].Should().Be(new string('b', 64));
                manifest.RecordCounts["scanned_entities"].Should().Be(77);
                manifest.RecordCounts["neutral_records"].Should().Be(1);
                manifest.RecordCounts["findings"].Should().Be(2);
                manifest.RecordCounts["blocking_findings"].Should().Be(2,
                    "every unresolved ReviewRequired/Error finding fails closed, including UNMAPPED");
                manifest.FindingCounts[recordFinding.Code].Should().Be(1);
                manifest.FindingCounts[globalFinding.Code].Should().Be(1);
                manifest.Artifacts.Should().HaveCount(2);
            }
            finally
            {
                Directory.Delete(tmp, recursive: true);
            }
        }

        [Fact]
        public void EarthworksAdapter_UsesSharedFailClosedSurfaceSelection()
        {
            var source = File.ReadAllText(Path.Combine(EstimateFixtures.RepoRoot(),
                "MahodAI.Civil3D.Plugin", "CivilDelivery", "Estimate", "CorridorQuantityService.cs"));

            source.Should().Contain("SectionSourceSelectionLogic.Select(")
                .And.Contain("EstimatePreflightPolicy.EarthworksSourceUnverifiedCode")
                .And.NotContain("byName.Keys.FirstOrDefault");

            var finding = new DeliveryFinding
            {
                Code = EstimatePreflightPolicy.EarthworksSourceUnverifiedCode,
                Domain = "estimate",
                Severity = FindingSeverity.ReviewRequired,
                Title = "ambiguous earthworks source",
            };
            EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();
        }

        [Theory]
        [InlineData(EstimatePreflightPolicy.CorridorMaterialReadFailedCode)]
        [InlineData(EstimatePreflightPolicy.EarthworksQtoFailedCode)]
        [InlineData(EstimateFindingCodes.MeasurementFailed)]
        public void PartialModelQuantityException_IsAGlobalExportBlocker(string code)
        {
            var finding = new DeliveryFinding
            {
                Code = code,
                Domain = "estimate",
                Severity = FindingSeverity.Error,
                Title = "partial model quantity failure",
            };

            EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();

            var result = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("r", "U51.01.0250", 10, "מטר") },
                EstimateFixtures.Snapshot(), EstimateFixtures.Profile(),
                preflightFindings: new[] { finding });
            result.Status.Should().Be(DeliveryStatus.Failed);
            result.Lines.Should().OnlyContain(l => !l.IncludedInTotals && l.Total == null);
            result.CleanTotal.Should().Be(0m);
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
        }

        [Fact]
        public void SupportedEntityMeasurementFailures_AggregateToOneGlobalErrorBlocker()
        {
            var extraction = new CivilQuantityExtractionService.ExtractionResult();
            extraction.Findings.AddRange(new[]
            {
                new DeliveryFinding
                {
                    Code = EstimateFindingCodes.MeasurementFailed,
                    Domain = "estimate",
                    Severity = FindingSeverity.Warning,
                    Title = "line A failed",
                },
                new DeliveryFinding
                {
                    Code = EstimateFindingCodes.MeasurementFailed,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "hatch B failed",
                },
            });

            CivilQuantityExtractionService.ConsolidateMeasurementFailures(extraction, "6422");

            var finding = extraction.Findings.Should().ContainSingle().Subject;
            finding.Severity.Should().Be(FindingSeverity.Error);
            finding.AffectedRecordIds.Should().BeEmpty();
            finding.Message.Should().Contain("line A failed").And.Contain("hatch B failed");
            EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();
        }

        [Fact]
        public void CleanTotal_UsesTheSameGroupedRoundingAsTheWorkbook()
        {
            const string code = "U99.01.0001";
            var result = EstimateBuilder.Build(
                new[]
                {
                    EstimateFixtures.Record("r1", code, 0.6172, "מטר", handle: "A"),
                    EstimateFixtures.Record("r2", code, 0.6173, "מטר", handle: "B"),
                },
                EstimateFixtures.SnapshotWithPrice(code, "מטר", 1m),
                EstimateFixtures.Profile());

            result.Lines.Sum(l => l.Total ?? 0m).Should().Be(1.24m,
                "per-object cents are retained only as trace evidence");
            var group = EstimateBoqSemantics.GroupLines(result.Lines).Should().ContainSingle().Subject;
            group.Quantity.Should().Be(1.2345m);
            group.Total.Should().Be(1.23m);
            result.CleanTotal.Should().Be(1.23m,
                "the audit total is the printed grouped BOQ total, not the sum of object rounding");
        }

        [Fact]
        public void MidpointQuantity_IsRoundedAwayFromZeroExactlyOnce()
        {
            const string code = "U99.01.0001";
            var result = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("r1", code, 1.23445, "מטר") },
                EstimateFixtures.SnapshotWithPrice(code, "מטר", 1m),
                EstimateFixtures.Profile());

            result.Lines.Should().ContainSingle().Which.BoqQuantity.Should().Be(1.2345);
            var group = EstimateBoqSemantics.GroupLines(result.Lines).Should().ContainSingle().Subject;
            group.Quantity.Should().Be(1.2345m);
            group.Total.Should().Be(1.23m);
            result.CleanTotal.Should().Be(1.23m);
        }
    }

    public class EstimateSessionScopeTests
    {
        private static EstimateWorkflowService.ScanResult Scan(
            string runId = "run-a", string? profileSource = null) =>
            EstimateWorkflowService.AssembleScan(
                runId, "6422", @"C:\\drawings\\A.dwg", "hash-a",
                Array.Empty<NeutralQuantityRecord>(), Array.Empty<DeliveryFinding>(),
                0, false, profileSource: profileSource);

#if !MAHOD_CD_STANDALONE // EstimateToolSessionScope belongs to MahodAI's chat tools, which the separate plugin does not carry
        [Fact]
        public void ScopeRejectsDrawingProfileAndHashSwitches()
        {
            var scan = Scan();

            EstimateToolSessionScope.ValidateScope(
                scan, @"C:\\drawings\\A.dwg", "6422", "hash-a").Should().BeNull();
            EstimateToolSessionScope.ValidateScope(
                scan, @"C:\\drawings\\B.dwg", "6422", "hash-a").Should().NotBeNull();
            EstimateToolSessionScope.ValidateScope(
                scan, @"C:\\drawings\\A.dwg", "other", "hash-a").Should().NotBeNull();
            EstimateToolSessionScope.ValidateScope(
                scan, @"C:\\drawings\\A.dwg", "6422", "hash-b").Should().NotBeNull();
        }
#endif

        [Fact]
        public void NewScanClearsEstimate_AndMismatchedRunCannotBeStored()
        {
            CivilDeliverySession.ClearEstimateContext();
            var profile = EstimateFixtures.Profile();
            var first = Scan("run-a");
            CivilDeliverySession.SetScan(
                first, profile, "hash-a", @"C:\test\estimate-profile.yaml");
            CivilDeliverySession.SetEstimate(new EstimateResult
            {
                RunId = "run-a",
                ProjectProfileId = "6422",
                PriceBookId = "book",
                PriceBookHash = "hash",
            });

            CivilDeliverySession.SetScan(
                Scan("run-b"), profile, "hash-a", @"C:\test\estimate-profile.yaml");
            CivilDeliverySession.GetEstimateContext().Estimate.Should().BeNull();
            var act = () => CivilDeliverySession.SetEstimate(new EstimateResult
            {
                RunId = "run-a",
                ProjectProfileId = "6422",
                PriceBookId = "book",
                PriceBookHash = "hash",
            });
            act.Should().Throw<InvalidOperationException>();
            CivilDeliverySession.ClearEstimateContext();
        }

        [Fact]
        public void AbsoluteProfileSource_IsPreservedForExactReload()
        {
            var dir = Path.Combine(Path.GetTempPath(), "mcd-profile-scope", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "custom-profile.yaml");
            File.WriteAllText(path, "profile_id: 6422");
            try
            {
                var resolved = EstimateWorkflowService.ResolveProfileSource(path);
                resolved.Should().Be(Path.GetFullPath(path));
                Scan(profileSource: resolved).ProfileSource.Should().Be(resolved);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void EstimateScan_DoesNotReplacePendingSectionsProfileContext()
        {
            var sectionProfile = new ProjectProfile { ProfileId = "sections-profile" };
            var plan = new SectionPlan
            {
                RunId = "sections-run",
                ProjectProfileId = sectionProfile.ProfileId,
                ProjectProfileHash = "sections-hash",
                SourceDrawing = @"C:\\drawings\\A.dwg",
            };
            CivilDeliverySession.SetPlan(
                plan, sectionProfile, "sections-hash", @"C:\test\sections-profile.yaml");

            var estimateProfile = EstimateFixtures.Profile();
            CivilDeliverySession.SetScan(
                Scan(), estimateProfile, "hash-a", @"C:\test\estimate-profile.yaml");

            var sections = CivilDeliverySession.GetSectionsContext();
            sections.Plan.Should().BeSameAs(plan);
            sections.Profile.Should().BeSameAs(sectionProfile);
            sections.ProfileHash.Should().Be("sections-hash");
            sections.ProfileWriteTarget.Should().Be(@"C:\test\sections-profile.yaml");
            CivilDeliverySession.ClearEstimateContext();
        }
    }

    public class EstimateExportBoundarySourceTests
    {
        [Fact]
        public void DirectCommand_ChecksFinalGateBeforeCallingExport()
        {
            var source = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "Commands", "MhdEstimateCommand.cs"));

            var gate = source.IndexOf("EstimatePreflightPolicy.CanExport(estimate)",
                StringComparison.Ordinal);
            var export = source.IndexOf(
                "Workflow.Export(doc, scan, estimate, profile)", StringComparison.Ordinal);
            gate.Should().BeGreaterThan(0);
            export.Should().BeGreaterThan(gate,
                "the direct command must return before invoking the shared writer");
        }

        [Fact]
        public void IncludeXrefsPolicy_UsesRecursiveVerifiedTraversal()
        {
            var source = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "Estimate", "CivilQuantityExtractionService.cs"));

            source.Should().Contain("EstimatePreflightPolicy.ValidateSourcePolicies(profile)");
            source.Should().Contain("void TraverseReference(")
                .And.Contain("EstimateFindingCodes.XrefTraversalUnresolved")
                .And.Contain("EstimateFindingCodes.XrefTransformInvalid")
                .And.Contain("result.ExternalSources.Add(")
                .And.Contain("LoadedSnapshotFailure(")
                .And.Contain("TryInspectActiveSpatialClip(")
                .And.Contain("ACAD_FILTER")
                .And.Contain("SPATIAL")
                .And.Contain("if (!isExternal)")
                .And.Contain("member is BlockReference nestedReference")
                .And.Contain("if (countOrdinaryReference)")
                .And.Contain("countOrdinaryReference: false")
                .And.Contain("activeAncestorClipHandle: effectiveClipHandle")
                .And.Contain("IsExternalReferenceExcludedByOverlay(")
                .And.Contain("hasExternalOverlayAncestor: hasExternalOverlayAncestor")
                .And.Contain("hasExternalOverlayAncestor ||")
                .And.Contain("composedTransform = outerTransform * reference.BlockTransform");

            source.IndexOf("IsExternalReferenceExcludedByOverlay(", StringComparison.Ordinal)
                .Should().BeLessThan(
                    source.IndexOf("if (definition.IsUnloaded || !definition.IsResolved)",
                        StringComparison.Ordinal),
                    "a nested external branch excluded by Overlay is not visible and must not be blocked as unresolved");
        }

        [Fact]
        public void PaletteScopeApproval_IsExplicitAndInvalidatesEveryStaleEstimateObject()
        {
            var xaml = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "UI", "CivilDeliveryControl.xaml"));
            var source = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "UI", "CivilDeliveryControl.xaml.cs"));

            xaml.Should().Contain("x:Name=\"BtnApproveEstimateScope\"")
                .And.Contain("Click=\"OnApproveEstimateScope\"")
                .And.Contain("אישור היקף המקורות; אינו מאשר את המדידה, השיוכים או המחירים");

            var start = source.IndexOf("private void OnApproveEstimateScope", StringComparison.Ordinal);
            var end = source.IndexOf("private void OnScan", start, StringComparison.Ordinal);
            start.Should().BeGreaterThan(0);
            end.Should().BeGreaterThan(start);
            var handler = source.Substring(start, end - start);
            handler.Should().Contain("new EstimateSourceReviewDialog(selectionInventory, decisionScope.Profile.Estimate.SourceSelection, inventory.Text, scopeText)")
                .And.Contain("EstimateSourceSelectionPolicy.InventoryHash(currentInventory)")
                .And.Contain("EstimateSourceSelectionPolicy.Approve(currentInventory, dialog.Choices, approver, DateTime.UtcNow)")
                .And.Contain("if (CivilModalHost.ShowFromPalette(dialog) != true) return;")
                .And.Contain("EstimateSourceInventoryService.Capture(decisionScope.Document)")
                .And.Contain("if (!inventory.IsComplete)")
                .And.Contain("RequireProfileDecisionScope(decisionScope)")
                .And.Contain("ApproveCompleteDiscoveryScope")
                .And.Contain("InvalidateEstimateEvidence(")
                .And.Contain("ReloadProfile();");
            handler.IndexOf("if (CivilModalHost.ShowFromPalette(dialog) != true) return;", StringComparison.Ordinal)
                .Should().BeLessThan(handler.IndexOf("ApproveCompleteDiscoveryScope", StringComparison.Ordinal));
            var consentXaml = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "UI", "EstimateSourceReviewDialog.xaml"));
            consentXaml.Should().Contain("Click=\"OnApprove\" IsDefault=\"False\"")
                .And.Contain("IsCancel=\"True\"")
                .And.Contain("IsReadOnly=\"True\"")
                .And.Contain("VerticalScrollBarVisibility=\"Auto\"");
        }

        [Fact]
        public void PaletteIgnoredRuleDecision_RequiresConfirmationAndInvalidatesOldExport()
        {
            var source = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "UI", "CivilDeliveryControl.xaml.cs"));

            var start = source.IndexOf("private void OnToggleRelevance", StringComparison.Ordinal);
            var end = source.IndexOf("private void OnApproveMapping", start, StringComparison.Ordinal);
            start.Should().BeGreaterThan(0);
            end.Should().BeGreaterThan(start);
            var handler = source.Substring(start, end - start);
            handler.Should().Contain("MessageBoxButton.YesNo")
                .And.Contain("MessageBoxResult.Yes")
                .And.Contain("QuantityExclusionDecisionDialog")
                .And.Contain("dialog.EngineeringReason")
                .And.Contain("SaveIgnoredRuleDecision")
                .And.Contain("InvalidateEstimateEvidence")
                .And.Contain("ReloadProfile();");

            var dialogSource = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "UI", "QuantityExclusionDecisionDialog.xaml.cs"));
            dialogSource.Should().Contain("!string.IsNullOrWhiteSpace(ReasonBox.Text)")
                .And.Contain("!string.IsNullOrWhiteSpace(ApproverBox.Text)");
        }

        [Fact]
        public void PaletteCatalogSummary_SeparatesApprovedRulesFromUnapprovedSuggestions()
        {
            var source = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "UI", "CivilDeliveryControl.xaml.cs"));

            source.Should().Contain("EstimateWorkflowService.QuantityRuleApprovals(_profile)")
                .And.Contain("חוקי כמויות מאושרים: {quantityRuleApprovals.Approved}")
                .And.Contain("הצעות מיפוי לא מאושרות: {quantityRuleApprovals.Unapproved}")
                .And.Contain("EstimateWorkflowService.ApprovedCatalogCodeForDisplay(",
                    "a stale/default candidate code must not receive the green approved grid state");
        }

        [Fact]
        public void PaletteApprovesTheSupplierLayerLeaf_NotTheXrefQualifiedAttachmentName()
        {
            var source = File.ReadAllText(Path.Combine(
                EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery",
                "UI", "CivilDeliveryControl.xaml.cs"));
            var start = source.IndexOf("private void OnApproveMapping", StringComparison.Ordinal);
            var end = source.IndexOf("private void InvalidateEstimateEvidence", start,
                StringComparison.Ordinal);
            var handler = source.Substring(start, end - start);

            handler.Should().Contain("SectionProjectionLogic.LayerLeaf(sample.Source.Layer)")
                .And.Contain("LayerPattern: string.IsNullOrWhiteSpace(mappingLayer)");
        }
    }

    public class EstimateSourceSnapshotPolicyTests
    {
        private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        [Fact]
        public void DirtyDrawing_IsRejectedBeforeAnyHashCanRepresentIt()
        {
            EstimateSourceSnapshotPolicy.InitialFailure(HashA, 1)
                .Should().Contain("unsaved changes");
        }

        [Theory]
        [InlineData(null, "rev-1", "rev-1")]
        [InlineData(0, "rev-1", "rev-2")]
        public void MissingDbmodOrChangedLiveRevision_InvalidatesExport(
            int? dbmod, string scannedRevision, string currentRevision)
        {
            EstimateSourceSnapshotPolicy.FreshnessFailure(
                    HashA, scannedRevision, HashA, currentRevision, dbmod)
                .Should().NotBeNull();
        }

        [Fact]
        public void ChangedSavedBytes_InvalidatesExport()
        {
            EstimateSourceSnapshotPolicy.FreshnessFailure(HashA, "rev", HashB, "rev", 0)
                .Should().Contain("bytes changed");
        }
    }

    public class EstimateExcelWriterTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcd-xlsx-tests", Guid.NewGuid().ToString("N"));

        public EstimateExcelWriterTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static DocumentFormat.OpenXml.Packaging.WorksheetPart PricedWorksheet(
            DocumentFormat.OpenXml.Packaging.WorkbookPart workbook)
        {
            var sheets = workbook.Workbook.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().ToList();
            sheets.Select(sheet => sheet.Name!.Value).Should().Equal(
                "כתב כמויות", "לא מתומחר", "ממצאים", "עקבה", "זהות ראיות");
            foreach (var sheet in sheets)
            {
                var part = (DocumentFormat.OpenXml.Packaging.WorksheetPart)workbook.GetPartById(sheet.Id!.Value!);
                part.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.SheetView>().Single()
                    .RightToLeft!.Value.Should().BeTrue("every Hebrew export worksheet is RTL");
            }
            return (DocumentFormat.OpenXml.Packaging.WorksheetPart)workbook.GetPartById(
                sheets.Single(sheet => sheet.Name!.Value == "כתב כמויות").Id!.Value!);
        }

        private EstimateResult BuildSample()
        {
            return EstimateBuilder.Build(new[]
            {
                EstimateFixtures.Record("r1", "U51.01.0250", 250.0, "מטר"),
                EstimateFixtures.Record("r2", "U51.06.1900", 6, "מטר", handle: "H2",
                    layer: "KERB-NEW"),
                EstimateFixtures.Record("r3", "U51.01.0250", 42, "מטר", handle: "H3"),
            }, EstimateFixtures.Snapshot(), EstimateFixtures.Profile());
        }

        private EstimateResult BuildUnsafeSample() => EstimateBuilder.Build(new[]
        {
            EstimateFixtures.Record("r1", "U51.01.0250", 250.0, "מטר"),
            EstimateFixtures.Record("r2", "U40.02.2500", 6, "יח'", kind: "count", handle: "H2",
                layer: "BUS-SHELTER"),
            EstimateFixtures.Record("r3", null!, 42, "מטר", handle: "H3"),
        }, EstimateFixtures.Snapshot(), EstimateFixtures.Profile());

        [Fact]
        public void Write_ProducesRtlWorkbookWithFormulasAndAudit()
        {
            var estimate = BuildSample();
            var written = EstimateExcelWriter.Write(estimate, _dir, "אומדן-בדיקה");

            File.Exists(written.XlsxPath).Should().BeTrue();
            File.Exists(written.AuditPath).Should().BeTrue();
            File.Exists(written.ManifestPath).Should().BeTrue();
            written.XlsxHash.Should().Be(ArtifactHash.Sha256OfFile(written.XlsxPath));
            written.AuditHash.Should().Be(ArtifactHash.Sha256OfFile(written.AuditPath));
            written.ManifestHash.Should().Be(ArtifactHash.Sha256OfFile(written.ManifestPath));

            using var doc = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(written.XlsxPath, false);
            var wb = doc.WorkbookPart!;
            var ws = PricedWorksheet(wb);
            var view = ws.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.SheetView>().First();
            view.RightToLeft!.Value.Should().BeTrue("Hebrew estimate sheets are RTL");

            var formulas = ws.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.CellFormula>()
                .Select(f => f.Text).ToList();
            formulas.Should().Contain(f => f.StartsWith("ROUND("), "line totals are real formulas");
            formulas.Should().Contain(f => f.StartsWith("SUM("), "chapter/grand totals are real formulas");

            var texts = ws.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Text>()
                .Select(t => t.Text).ToList();
            texts.Should().Contain(t => t.Contains("תקין"), "only final-ready lines reach the workbook");
            texts.Should().Contain(EstimatePreflightPolicy.NeutralRecordScopeNotice,
                "BuildSample supplies neutral records, not a verified live CAD snapshot");

            var audit = File.ReadAllText(written.AuditPath);
            audit.Should().Contain("\"price_book_hash\"");
            audit.Should().Contain("\"source_scope_policy\": \"discover-all\"")
                .And.Contain("\"xref_policy\": \"include-xrefs\"")
                .And.Contain(EstimatePreflightPolicy.NeutralRecordScopeNotice);
            audit.Should().Contain("\"boq_groups\"", "the audit exposes the exact printed grouping");
            audit.Should().Contain("U51.01.0250")
                .And.Contain("\"project_profile_effective_hash\"")
                .And.Contain("\"source_drawing_hash\"")
                .And.Contain("\"mapping_approved_by\": \"test-engineer\"")
                .And.Contain("\"workbook_sha256\": \"" + written.XlsxHash + "\"");
            var manifest = File.ReadAllText(written.ManifestPath);
            manifest.Should().Contain("\"sha256\": \"" + written.XlsxHash + "\"")
                .And.Contain("\"sha256\": \"" + written.AuditHash + "\"")
                .And.Contain(Path.GetFileName(written.XlsxPath))
                .And.Contain(Path.GetFileName(written.AuditPath));
        }

        [Fact]
        public void Write_PreservesAuditedExclusionInWorkbookAndAuditJson()
        {
            var estimate = BuildSample();
            estimate.Exclusions.Add(new EstimateExclusion
            {
                RuleKey = "layer:HELPER|length",
                Reason = "קו עזר גרפי בלבד",
                ApprovedBy = "nataly",
                ApprovedAtUtc = new DateTime(2026, 8, 31, 15, 0, 0, DateTimeKind.Utc),
                Sources =
                {
                    new EstimateExclusionSource
                    {
                        RecordId = "excluded-1",
                        Drawing = "PD.dwg",
                        DrawingHash = new string('d', 64),
                        Handle = "AB12",
                        Layer = "HELPER",
                        MeasurementKind = "length",
                        Unit = "מטר",
                        RawValue = 40,
                    },
                },
            });

            var written = EstimateExcelWriter.Write(estimate, _dir, "אומדן-החרגות");
            using var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(
                written.XlsxPath, false);
            var workbook = document.WorkbookPart!;
            var worksheet = PricedWorksheet(workbook);
            var workbookText = string.Join("\n", worksheet.Worksheet
                .Descendants<DocumentFormat.OpenXml.Spreadsheet.Text>()
                .Select(t => t.Text));

            workbookText.Should().Contain("נספח ביקורת — כמויות שהוחרגו בהחלטה הנדסית")
                .And.Contain("layer:HELPER|length")
                .And.Contain("קו עזר גרפי בלבד")
                .And.Contain("PD.dwg#AB12@dddddddddddd")
                .And.Contain("nataly")
                .And.Contain("2026-08-31 15:00:00 UTC");

            var audit = File.ReadAllText(written.AuditPath);
            audit.Should().Contain("\"exclusions\"")
                .And.Contain("\"rule_key\": \"layer:HELPER|length\"")
                .And.Contain("\"reason\": \"קו עזר גרפי בלבד\"")
                .And.Contain("\"approved_by\": \"nataly\"")
                .And.Contain("\"drawing_hash\": \"" + new string('d', 64) + "\"")
                .And.Contain("\"handle\": \"AB12\"");
        }

        [Fact]
        public void Write_AggregatesDrawingObjectsIntoOneBoqRowPerCatalogItem()
        {
            // The first real export (6422, 2026-08-19) printed 437 rows for ONE kerb
            // item - one per polyline - and SUM() over 437 cells. An estimate lists each
            // catalog item once with the summed quantity; the audit keeps every object.
            var records = Enumerable.Range(1, 437)
                .Select(i => EstimateFixtures.Record($"r{i}", "U51.06.1900", 84.44, "מטר", handle: $"H{i}"))
                .ToList();
            var estimate = EstimateBuilder.Build(records, EstimateFixtures.Snapshot(), EstimateFixtures.Profile());
            estimate.Lines.Should().HaveCount(437, "per-object lines stay for traceability");

            var written = EstimateExcelWriter.Write(estimate, _dir, "אומדן-מצטבר");

            using var doc = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(written.XlsxPath, false);
            var wb = doc.WorkbookPart!;
            var ws = PricedWorksheet(wb);

            var itemRows = ws.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>()
                .Where(r => r.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>()
                    .Any(c => c.InlineString?.Text?.Text == "U51.06.1900"))
                .ToList();
            itemRows.Should().HaveCount(1, "one BOQ row per catalog item");

            var qty = itemRows[0].Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>()
                .First(c => c.CellReference!.Value!.StartsWith("E")).CellValue!.Text;
            double.Parse(qty, System.Globalization.CultureInfo.InvariantCulture)
                .Should().BeApproximately(437 * 84.44, 0.01, "quantities are summed");

            var desc = itemRows[0].Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>()
                .First(c => c.CellReference!.Value!.StartsWith("C")).InlineString!.Text!.Text;
            desc.Should().Contain("437 עצמים", "the row says how many drawing objects it sums");

            var sums = ws.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.CellFormula>()
                .Select(f => f.Text).Where(f => f.StartsWith("SUM(")).ToList();
            sums.Should().NotBeEmpty();
            sums.Should().OnlyContain(f => f.Length < 80, "SUM uses ranges, not hundreds of cell references");

            File.ReadAllText(written.AuditPath).Should().Contain("\"r437\"", "the audit still lists every object");
        }

        [Fact]
        public void Workbook_ReadsLikeAFinalEstimate_WithPricedChaptersAndNoUnresolvedAppendix()
        {
            var estimate = BuildSample();
            var written = EstimateExcelWriter.Write(estimate, _dir, "אומדן-סדר",
                new EstimateExcelWriter.WriteOptions(ProjectTitle: "נת\"צ מודיעין", PriceBookLabel: "נתיבי ישראל 08/2025", DrawingName: "6422-TEST.dwg"));

            using var doc = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(written.XlsxPath, false);
            var wb = doc.WorkbookPart!;
            var ws = PricedWorksheet(wb);
            var sst = wb.SharedStringTablePart?.SharedStringTable;
            string CellText(DocumentFormat.OpenXml.Spreadsheet.Cell c)
            {
                if (c.DataType?.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString && sst != null)
                    return sst.ElementAt(int.Parse(c.CellValue!.Text)).InnerText;
                return c.InnerText;
            }
            var rows = ws.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>().ToList();
            var colC = rows.Select(r => r.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>()
                .FirstOrDefault(c => c.CellReference!.Value!.StartsWith("C")))
                .Select(c => c == null ? "" : CellText(c)).ToList();

            // header context
            var all = string.Join("\n", rows.SelectMany(r => r.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>()).Select(CellText));
            all.Should().Contain("נת\"צ מודיעין").And.Contain("נתיבי ישראל 08/2025").And.Contain("6422-TEST.dwg");

            // Final export contains priced chapters only; unresolved rows are stopped
            // at the export boundary rather than printed as an apparent deliverable.
            int firstChapter = colC.FindIndex(t => t.StartsWith("פרק "));
            int appendix = colC.FindIndex(t =>
                t.StartsWith("נספח — שכבות שנמדדו ללא שיוך", StringComparison.Ordinal));
            firstChapter.Should().BeGreaterThan(0);
            appendix.Should().Be(-1);

            // Quantity carries four visible decimals; price/total keep the monetary style.
            var styles = wb.WorkbookStylesPart!.Stylesheet;
            var xfs = styles.CellFormats!.Elements<DocumentFormat.OpenXml.Spreadsheet.CellFormat>().ToList();
            var qtyCell = rows.SelectMany(r => r.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>())
                .First(c => c.CellReference!.Value!.StartsWith("E") && c.DataType == null && c.CellValue != null);
            var xf = xfs[(int)qtyCell.StyleIndex!.Value];
            xf.NumberFormatId!.Value.Should().Be(164u, "#,##0.0000 preserves the measured BOQ quantity");
        }

        [Fact]
        public void WorkbookQuantityAndAuditTotal_UseTheCanonicalGroupedSemantics()
        {
            const string code = "U99.01.0001";
            var estimate = EstimateBuilder.Build(
                new[]
                {
                    EstimateFixtures.Record("r1", code, 0.6172, "מטר", handle: "A"),
                    EstimateFixtures.Record("r2", code, 0.6173, "מטר", handle: "B"),
                },
                EstimateFixtures.SnapshotWithPrice(code, "מטר", 1m),
                EstimateFixtures.Profile());

            var written = EstimateExcelWriter.Write(estimate, _dir, "canonical-total");
            using var doc = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(written.XlsxPath, false);
            var wb = doc.WorkbookPart!;
            var ws = PricedWorksheet(wb);
            var itemRow = ws.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>()
                .Single(r => r.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>()
                    .Any(c => c.InlineString?.Text?.Text == code));
            var quantity = itemRow.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>()
                .Single(c => c.CellReference!.Value!.StartsWith("E"));
            var total = itemRow.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>()
                .Single(c => c.CellReference!.Value!.StartsWith("G"));

            quantity.CellValue!.Text.Should().Be("1.2345");
            var rowNumber = itemRow.RowIndex!.Value;
            total.CellFormula!.Text.Should().Be($"ROUND(E{rowNumber}*F{rowNumber},2)");
            estimate.CleanTotal.Should().Be(1.23m);
            File.ReadAllText(written.AuditPath).Should().Contain("\"clean_total\": 1.23");
        }

        [Fact]
        public void WorkbookMidpointQuantity_UsesAwayFromZeroFourDecimalValue()
        {
            const string code = "U99.01.0001";
            var estimate = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("r1", code, 1.23445, "מטר") },
                EstimateFixtures.SnapshotWithPrice(code, "מטר", 1m),
                EstimateFixtures.Profile());

            var written = EstimateExcelWriter.Write(estimate, _dir, "midpoint-away");
            using var doc = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(written.XlsxPath, false);
            var wb = doc.WorkbookPart!;
            var ws = PricedWorksheet(wb);
            var itemRow = ws.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>()
                .Single(r => r.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>()
                    .Any(c => c.InlineString?.Text?.Text == code));

            itemRow.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>()
                .Single(c => c.CellReference!.Value!.StartsWith("E"))
                .CellValue!.Text.Should().Be("1.2345");
            estimate.CleanTotal.Should().Be(1.23m);
        }

        [Fact]
        public void Write_FailsClosed_WhenAuditTotalNoLongerMatchesWorkbookGrouping()
        {
            var estimate = BuildSample();
            estimate.CleanTotal += 0.01m;

            var act = () => EstimateExcelWriter.Write(estimate, _dir, "inconsistent");

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*inconsistent with workbook grouping*");
            Directory.GetFiles(_dir, "inconsistent*").Should().BeEmpty();
        }

        [Fact]
        public void Write_FailsClosed_ForGlobalQuantityBlocker()
        {
            var blocker = new DeliveryFinding
            {
                Code = EstimatePreflightPolicy.CorridorOutOfDateCode,
                Domain = "estimate",
                Severity = FindingSeverity.ReviewRequired,
                Title = "stale corridor",
            };
            var estimate = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("r1", "U51.01.0250", 10, "מטר") },
                EstimateFixtures.Snapshot(), EstimateFixtures.Profile(),
                preflightFindings: new[] { blocker });

            var act = () => EstimateExcelWriter.Write(estimate, _dir, "blocked");

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*EST-CORRIDOR-OUT-OF-DATE*");
            Directory.GetFiles(_dir, "blocked*").Should().BeEmpty();
        }

        [Fact]
        public void Write_FailsClosed_ForUnmappedMissingPriceAndExcludedLines()
        {
            var estimate = BuildUnsafeSample();

            var act = () => EstimateExcelWriter.Write(estimate, _dir, "partial");

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*EST-MISSING-PRICE*")
                .WithMessage("*EST-UNMAPPED*");
            Directory.GetFiles(_dir, "partial*").Should().BeEmpty();
        }

        [Fact]
        public void Write_NeverOverwritesExistingFile()
        {
            var estimate = BuildSample();
            var first = EstimateExcelWriter.Write(estimate, _dir, "אומדן");
            var second = EstimateExcelWriter.Write(estimate, _dir, "אומדן");

            second.XlsxPath.Should().NotBe(first.XlsxPath);
            File.Exists(first.XlsxPath).Should().BeTrue();
            File.Exists(second.XlsxPath).Should().BeTrue();
        }

        [Fact]
        public void WorkbookTreatsExternalTextAsInlineStrings_NotExcelFormulas()
        {
            const string code = "U99.01.0001";
            const string hostile = "=HYPERLINK(\"https://invalid.example\",\"click\")";
            var snapshot = EstimateFixtures.SnapshotWithPrice(code, "מטר", 2m);
            snapshot.Items[code] = new CatalogItem
            {
                Code = code,
                Description = hostile,
                UnitRaw = "מטר",
            };
            var record = EstimateFixtures.Record("formula-text", code, 2, "מטר");
            record.Classification.ApprovedCatalogItemFingerprint =
                CatalogIdentity.ItemFingerprint(snapshot.Items[code]);
            var estimate = EstimateBuilder.Build(
                new[] { record }, snapshot, EstimateFixtures.Profile());

            var written = EstimateExcelWriter.Write(
                estimate, _dir, "formula-injection",
                new EstimateExcelWriter.WriteOptions(ProjectTitle: hostile));
            using var document =
                DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(
                    written.XlsxPath, false);
            PricedWorksheet(document.WorkbookPart!); // exact sheet order/names and RTL on every sheet
            var worksheets = document.WorkbookPart!.WorksheetParts.Select(part => part.Worksheet).ToList();

            worksheets.SelectMany(worksheet => worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.CellFormula>())
                .Select(formula => formula.Text)
                .Should().OnlyContain(formula =>
                    formula.StartsWith("ROUND(", StringComparison.Ordinal) ||
                    formula.StartsWith("SUM(", StringComparison.Ordinal));
            worksheets.SelectMany(worksheet => worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Text>())
                .Select(text => text.Text)
                .Should().Contain(text => text.Contains(hostile, StringComparison.Ordinal));
        }

        [Fact]
        public void FailedPackageMember_IsRemovedInsteadOfRemainingAsFinalWorkbook()
        {
            var path = Path.Combine(_dir, "partial.xlsx");
            File.WriteAllText(path, "not a complete package");

            EstimateExcelWriter.WithdrawCommittedFile(path, "unit-test");

            File.Exists(path).Should().BeFalse();
            Directory.GetFiles(_dir, "partial.xlsx.FAILED-*").Should().BeEmpty();
        }

        [Fact]
        public void LockedFailedPackageMember_SurfacesCleanupFailure()
        {
            var path = Path.Combine(_dir, "locked-partial.xlsx");
            File.WriteAllText(path, "not a complete package");
            using var lockStream = new FileStream(
                path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

            var act = () => EstimateExcelWriter.WithdrawCommittedFile(path, "unit-test");

            act.Should().Throw<IOException>()
                .WithMessage("*Could not withdraw or quarantine*");
            File.Exists(path).Should().BeTrue(
                "an undeletable final-looking partial must be reported, never silently ignored");
        }
    }
}
