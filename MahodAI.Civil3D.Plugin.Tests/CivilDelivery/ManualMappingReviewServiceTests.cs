using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Choice = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.EstimateWorkflowService.ReviewedMappingChoice;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Temporary, synthetic human decisions only; never approves the live 6422 profile.</summary>
public sealed class ManualMappingReviewServiceTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Ordinary = "layer:FIXTURE-CURB|length";
    private const string Area = "layer:FIXTURE-BOUNDARY|area";
    private const string Perimeter = "layer:FIXTURE-BOUNDARY|length";
    private const string Unselected = "layer:FIXTURE-OTHER|length";
    private const string Approver = "SYNTHETIC TEST REVIEWER";

    private static CatalogSnapshot Catalog(string hash = Hash) => new()
    {
        SnapshotId = "MANUAL-MAPPING-SIMULATION-ONLY", FileHash = hash,
        Items =
        {
            ["TEST.LENGTH"] = new() { Code = "TEST.LENGTH", Description = "Synthetic length item", UnitRaw = "m" },
            ["TEST.AREA"] = new() { Code = "TEST.AREA", Description = "Synthetic area item", UnitRaw = "m2" },
        },
        // Deliberately no prices: mapping approval is not price approval.
    };

    private static NeutralQuantityRecord Record(string id, string key = Ordinary,
        string kind = "length", string unit = "m", string layer = "FIXTURE-CURB",
        string entityType = "LINE", string? handle = null, string? method = null,
        string profileId = "6422", double value = 37.25)
    {
        var basis = EstimateFixtures.Record(id, null!, value, unit, kind,
            handle: handle ?? id, ruleKey: key, layer: layer, method: method ?? "polyline-length");
        return new NeutralQuantityRecord
        {
            RecordId = basis.RecordId, ProjectProfileId = profileId, RunId = "SIMULATION-ONLY-MANUAL-REVIEW",
            Source = new()
            {
                Drawing = "SIMULATION-ONLY.dwg", DrawingPath = basis.Source.DrawingPath,
                DrawingHash = basis.Source.DrawingHash, Handle = basis.Source.Handle,
                EntityType = entityType, Layer = layer,
            },
            Measurement = basis.Measurement, Classification = basis.Classification,
            Status = DeliveryStatus.ReviewRequired,
            Findings =
            {
                new DeliveryFinding
                {
                    Code = EstimateFindingCodes.Unmapped, Domain = "estimate", Severity = FindingSeverity.ReviewRequired,
                    Title = "Synthetic mapping undecided", ProjectProfileId = profileId, AffectedRecordIds = { id },
                },
            },
        };
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "mahod-manual-review-tests-" + Guid.NewGuid().ToString("N"));
        internal string TargetPath => Path.Combine(DirectoryPath, "simulation-only-profile.yaml");
        internal readonly CatalogSnapshot Snapshot = Catalog();
        internal readonly ProjectProfile Profile;
        internal readonly List<NeutralQuantityRecord> Records;
        internal readonly DeliveryFinding GlobalFinding = new()
        {
            Code = EstimateFindingCodes.MeasurementFailed, Domain = "estimate", Severity = FindingSeverity.Error,
            Title = "Synthetic unrelated geometry failure remains unresolved", ProjectProfileId = "6422",
        };
        internal EstimateWorkflowService.ScanResult Scan;
        internal ProjectProfileWriter.ExpectedProfileState State => Scan.ProfileWriteState!;

        internal Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            Profile = new ProjectProfile { ProfileId = "6422", ProjectName = "SIMULATION ONLY - NOT NATIVE APPROVAL" };
            Profile.Estimate.Catalog.CatalogFile = "SIMULATION-ONLY-CATALOG.xlsx";
            Profile.Estimate.Catalog.CatalogFileHash = Snapshot.FileHash;
            Profile.Estimate.Pricing.PriceBookSnapshotId = Snapshot.SnapshotId;
            Profile.Estimate.Pricing.PriceBookHash = Snapshot.FileHash;
            Profile.Estimate.PriceBooks.Add(new()
            {
                Id = Snapshot.SnapshotId, File = "SIMULATION-ONLY-CATALOG.xlsx", FileHash = Snapshot.FileHash,
            });
            Records = new()
            {
                Record("ordinary-line"),
                Record("ordinary-polyline", unit: "מטר", entityType: "LWPOLYLINE", value: 60),
                // 240/128 mirror recorded HW-CS-TABL values only as a synthetic fixture.
                Record("closed-area", Area, "area", "m2", "FIXTURE-BOUNDARY", "LWPOLYLINE", "BOUNDARY", "closed-polyline-area", value: 240),
                Record("closed-perimeter", Perimeter, "length", "m", "FIXTURE-BOUNDARY", "LWPOLYLINE", "BOUNDARY", "closed-polyline-perimeter", value: 128),
                Record("other", Unselected, layer: "FIXTURE-OTHER", value: 12.5),
            };
            Scan = NewScan();
        }

        internal EstimateWorkflowService.ScanResult NewScan() => new()
        {
            RunId = "SIMULATION-ONLY-MANUAL-REVIEW", ProjectProfileId = Profile.ProfileId,
            ProfileSource = TargetPath, SourceDrawing = @"C:\test\SIMULATION-ONLY.dwg",
            SourceDrawingHash = Hash, SourceDbMod = 0, DatabaseRevision = "SIMULATION-REVISION",
            ProjectProfileHash = Hash, ProjectProfileEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(Profile),
            ProfileWriteState = ProfileCasTest.For(Profile, TargetPath), Records = Records,
            Findings = { GlobalFinding }, Status = DeliveryStatus.Failed, DiscoveryMode = true,
        };

        internal ProjectProfileWriter.SaveResult Save(IReadOnlyList<Choice> choices,
            string approvedBy = Approver, CatalogSnapshot? catalog = null,
            ProjectProfileWriter.ExpectedProfileState? state = null) =>
            new EstimateWorkflowService().SaveReviewedMappings(Profile, catalog ?? Snapshot, Scan,
                choices, approvedBy, TargetPath, state ?? State);

        internal void Reject(IReadOnlyList<Choice> choices, string approvedBy = Approver,
            CatalogSnapshot? catalog = null, ProjectProfileWriter.ExpectedProfileState? state = null)
        {
            var profileBefore = JsonSerializer.Serialize(Profile);
            var scanBefore = JsonSerializer.Serialize(Scan);
            var filesBefore = Directory.GetFiles(DirectoryPath, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal).ToDictionary(path => path, File.ReadAllBytes);
            Action act = () => Save(choices, approvedBy, catalog, state);
            act.Should().Throw<Exception>().Where(error =>
                typeof(ArgumentException).IsInstanceOfType(error) || typeof(InvalidOperationException).IsInstanceOfType(error),
                "a rejected human selection must fail through a guarded contract, not a null-reference failure");
            JsonSerializer.Serialize(Profile).Should().Be(profileBefore);
            JsonSerializer.Serialize(Scan).Should().Be(scanBefore);
            var filesAfter = Directory.GetFiles(DirectoryPath, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            filesAfter.Should().Equal(filesBefore.Keys);
            foreach (var pair in filesBefore) File.ReadAllBytes(pair.Key).Should().Equal(pair.Value);
        }

        public void Dispose()
        {
            // This directory is created uniquely by this fixture, never a product profile directory.
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    [Fact]
    public void MultiGroupReviewSavesOnlySelectedMappingsAndExplicitSiblingInOneVersion()
    {
        using var f = new Fixture();
        var originalRecords = JsonSerializer.Serialize(f.Records);
        var originalFinding = JsonSerializer.Serialize(f.GlobalFinding);
        var version = f.Profile.Provenance.Version;
        var saved = f.Save(new[] { new Choice(Ordinary, "TEST.LENGTH"), new Choice(Area, "TEST.AREA", Perimeter) });

        saved.NewVersion.Should().Be(version + 1);
        File.Exists(saved.Path).Should().BeTrue(); saved.NewHash.Should().HaveLength(64);
        f.Profile.Estimate.QuantitySources.Rules.Select(rule => rule.RuleKey).Should().BeEquivalentTo(Ordinary, Area);
        var ordinary = f.Profile.Estimate.QuantitySources.Rules.Single(rule => rule.RuleKey == Ordinary);
        ordinary.EntityType.Should().Be("*", "LINE and LWPOLYLINE share this exact measured group");
        CivilQuantityExtractionService.ResolveApprovedRule(f.Profile, Ordinary, "FIXTURE-CURB", "length")
            .Rule.Should().BeSameAs(ordinary, "full discovery resolves the exact group independently of its entity types");
        ordinary.LayerPattern.Should().Be("FIXTURE-CURB"); ordinary.MeasurementKind.Should().Be("length");
        Units.Parse(ordinary.ExpectedUnit).Canonical.Should().Be("m");
        foreach (var rule in f.Profile.Estimate.QuantitySources.Rules)
        {
            rule.ApprovedBy.Should().Be(Approver); rule.ApprovedAtUtc.Should().NotBeNull();
            rule.ApprovedAtUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
            rule.ApprovedCatalogId.Should().Be(f.Snapshot.SnapshotId);
            rule.ApprovedCatalogHash.Should().Be(f.Snapshot.FileHash);
            rule.ApprovedCatalogItemFingerprint.Should().Be(CatalogIdentity.ItemFingerprint(f.Snapshot.Items[rule.CandidateCatalogCode!]));
        }
        var ignored = f.Profile.Estimate.IgnoredRuleDecisions.Should().ContainSingle().Which;
        ignored.RuleKey.Should().Be(Perimeter); ignored.Reason.Should().Contain(Area);
        ignored.ApprovedBy.Should().Be(Approver); ignored.ApprovedAtUtc.Should().NotBeNull();
        f.Snapshot.Prices.Should().BeEmpty(); f.Profile.Estimate.ProjectOverrides.Should().BeEmpty();
        JsonSerializer.Serialize(f.Records).Should().Be(originalRecords);
        JsonSerializer.Serialize(f.GlobalFinding).Should().Be(originalFinding);
        EstimatePreflightPolicy.IsBlocking(f.GlobalFinding).Should().BeTrue();
        var loaded = ProjectProfileLoader.LoadFromFile(saved.Path).Profile;
        loaded.Should().NotBeNull();
        loaded!.Estimate.QuantitySources.Rules.Select(rule => rule.RuleKey).Should().BeEquivalentTo(Ordinary, Area);
        loaded.Estimate.IgnoredRuleDecisions.Should().ContainSingle().Which.RuleKey.Should().Be(Perimeter);
    }

    [Fact]
    public void MissingPriceAndUnselectedClosedGroupsDoNotPreventExplicitOrdinaryMapping()
    {
        using var f = new Fixture();
        f.Save(new[] { new Choice(Ordinary, "TEST.LENGTH") });
        f.Profile.Estimate.QuantitySources.Rules.Should().ContainSingle().Which.RuleKey.Should().Be(Ordinary);
        f.Profile.Estimate.IgnoredRuleDecisions.Should().BeEmpty();
        f.Snapshot.Prices.Should().BeEmpty(); f.Scan.Status.Should().Be(DeliveryStatus.Failed);
    }

    [Fact]
    public void ReturnedExactAlternativeCanReplaceApprovedSiblingOnlyWithExplicitAtomicExclusion()
    {
        using var f = new Fixture();
        AddApprovedPerimeter(f);
        f.Profile.Estimate.IgnoredRuleDecisions.Add(new()
        {
            RuleKey = Area, Reason = "Synthetic earlier perimeter choice", ApprovedBy = Approver,
            ApprovedAtUtc = new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc),
        });
        f.Scan = f.NewScan();
        var returned = new EstimateWorkflowService().SaveIgnoredRuleDecision(f.Profile, f.Scan,
            Area, false, null, Approver, f.TargetPath, f.State);
        f.Scan = EstimateWorkflowService.RebaseAfterProfileDecision(f.Scan, f.Profile, returned);

        var saved = f.Save(new[] { new Choice(Area, "TEST.AREA", Perimeter) });
        saved.NewVersion.Should().Be(returned.NewVersion + 1);
        f.Profile.Estimate.QuantitySources.Rules.Should().Contain(rule =>
            rule.RuleKey == Area && rule.CandidateCatalogCode == "TEST.AREA");
        f.Profile.Estimate.IgnoredRuleDecisions.Should().ContainSingle().Which.RuleKey.Should().Be(Perimeter);
        var rebased = EstimateWorkflowService.RebaseAfterProfileDecisions(f.Scan, f.Profile, saved, new[] { Area });
        var application = EstimateWorkflowService.ApplyIgnoredRules(rebased.Records, f.Profile);
        application.PricedRecords.Should().NotContain(record => record.Classification.RuleKey == Perimeter);
        application.PricedRecords.Should().Contain(record => record.Classification.RuleKey == Area);
        rebased.Findings.Should().Contain(f.GlobalFinding);
    }

    [Fact]
    public void TheMappingAndItsExcludedAlternativeCarryTheSameTypedApproverThroughReload()
    {
        // b24 (Codex 11:18): the palette passes one confirmed approver to both halves of this single action.
        using var f = new Fixture();
        const string typed = "SYNTHETIC SESSION APPROVER";
        var saved = new EstimateWorkflowService().SaveApprovedClosedPolylineMapping(
            f.Profile, f.Snapshot, f.Scan,
            new EstimateWorkflowService.MappingApproval(Area, "TEST.AREA", "FIXTURE-BOUNDARY", "LWPOLYLINE", "area", "m2"),
            Perimeter, typed, f.TargetPath, f.State);
        var loaded = ProjectProfileLoader.LoadFromFile(saved.Path).Profile!;
        loaded.Estimate.QuantitySources.Rules.Single(rule => rule.RuleKey == Area).ApprovedBy.Should().Be(typed);
        loaded.Estimate.IgnoredRuleDecisions.Single(decision => decision.RuleKey == Perimeter).ApprovedBy.Should().Be(typed);
        loaded.Provenance.ApprovedBy.Should().Be(typed);
    }

    [Fact]
    public void ExistingSingleMappingRouteStillRefusesReplacingAnApprovedSibling()
    {
        using var f = new Fixture();
        AddApprovedPerimeter(f);
        f.Scan = f.NewScan();
        var before = JsonSerializer.Serialize(f.Profile);
        Action act = () => new EstimateWorkflowService().SaveApprovedClosedPolylineMapping(
            f.Profile, f.Snapshot, f.Scan,
            new EstimateWorkflowService.MappingApproval(Area, "TEST.AREA", "FIXTURE-BOUNDARY", "LWPOLYLINE", "area", "m2"),
            Perimeter, Approver, f.TargetPath, f.State);
        act.Should().Throw<InvalidOperationException>().WithMessage("*already mapped*");
        JsonSerializer.Serialize(f.Profile).Should().Be(before);
        File.Exists(f.TargetPath).Should().BeFalse();
    }

    private static void AddApprovedPerimeter(Fixture f)
    {
        var stamp = new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc);
        var fingerprint = CatalogIdentity.ItemFingerprint(f.Snapshot.Items["TEST.LENGTH"]);
        f.Profile.Estimate.QuantitySources.Rules.Add(new()
        {
            RuleKey = Perimeter, LayerPattern = "FIXTURE-BOUNDARY", EntityType = "LWPOLYLINE",
            MeasurementKind = "length", ExpectedUnit = "m", CandidateCatalogCode = "TEST.LENGTH",
            ApprovedBy = Approver, ApprovedAtUtc = stamp, ApprovedCatalogId = f.Snapshot.SnapshotId,
            ApprovedCatalogHash = f.Snapshot.FileHash, ApprovedCatalogItemFingerprint = fingerprint,
        });
        var classification = f.Records.Single(record => record.Classification.RuleKey == Perimeter).Classification;
        classification.CandidateCatalogCode = "TEST.LENGTH";
        classification.ApprovedCatalogId = f.Snapshot.SnapshotId;
        classification.ApprovedCatalogHash = f.Snapshot.FileHash;
        classification.ApprovedCatalogItemFingerprint = fingerprint;
        classification.MappingApprovedBy = Approver;
        classification.MappingApprovedAtUtc = stamp;
    }

    [Fact]
    public void RebaseClassifiesBothEntityTypesAndRetainsAllMeasurementsAndGlobalFinding()
    {
        using var f = new Fixture();
        var saved = f.Save(new[] { new Choice(Ordinary, "TEST.LENGTH"), new Choice(Area, "TEST.AREA", Perimeter) });
        var rebased = EstimateWorkflowService.RebaseAfterProfileDecisions(f.Scan, f.Profile, saved, new[] { Ordinary, Area });
        rebased.Records.Select(record => record.RecordId).Should().Equal(f.Records.Select(record => record.RecordId));
        for (var i = 0; i < f.Records.Count; i++)
        {
            JsonSerializer.Serialize(rebased.Records[i].Measurement).Should().Be(JsonSerializer.Serialize(f.Records[i].Measurement));
            JsonSerializer.Serialize(rebased.Records[i].Source).Should().Be(JsonSerializer.Serialize(f.Records[i].Source));
        }
        rebased.Records.Where(record => record.Classification.RuleKey == Ordinary)
            .Should().HaveCount(2).And.OnlyContain(record => CatalogIdentity.IsClassificationCurrent(record.Classification, f.Snapshot));
        rebased.Records.Single(record => record.Classification.RuleKey == Unselected).Classification.CandidateCatalogCode.Should().BeNull();
        rebased.Findings.Should().Contain(f.GlobalFinding); EstimatePreflightPolicy.IsBlocking(f.GlobalFinding).Should().BeTrue();
        rebased.SourceDrawingHash.Should().Be(f.Scan.SourceDrawingHash);
        rebased.DatabaseRevision.Should().Be(f.Scan.DatabaseRevision);
        rebased.ProjectProfileHash.Should().Be(saved.NewHash);
        f.Scan.Records.Should().OnlyContain(record => record.Classification.CandidateCatalogCode == null);
    }

    [Theory]
    [InlineData("unknown-code")]
    [InlineData("wrong-unit")]
    [InlineData("missing-group")]
    public void BadLastChoiceCannotPartiallySaveEarlierMappingOrSiblingExclusion(string failure)
    {
        using var f = new Fixture();
        f.Reject(new[]
        {
            new Choice(Area, "TEST.AREA", Perimeter),
            new Choice(failure == "missing-group" ? "MISSING-GROUP" : Ordinary,
                failure == "unknown-code" ? "NOT-IN-CATALOG" : failure == "wrong-unit" ? "TEST.AREA" : "TEST.LENGTH"),
        });
        File.Exists(f.TargetPath).Should().BeFalse();
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("missing-name")]
    [InlineData("missing-key")]
    [InlineData("missing-code")]
    public void IncompleteOrDuplicateHumanDecisionsNeverWrite(string failure)
    {
        using var f = new Fixture();
        var choices = failure == "empty" ? Array.Empty<Choice>() : failure == "duplicate"
            ? new[] { new Choice(Ordinary, "TEST.LENGTH"), new Choice(Ordinary, "TEST.LENGTH") }
            : new[] { new Choice(failure == "missing-key" ? " " : Ordinary, failure == "missing-code" ? " " : "TEST.LENGTH") };
        f.Reject(choices, failure == "missing-name" ? " " : Approver);
    }

    [Theory]
    [InlineData("unit")]
    [InlineData("kind")]
    [InlineData("layer")]
    [InlineData("profile")]
    public void EveryRecordMustAgreeWithTheSelectedGroupContract(string failure)
    {
        using var f = new Fixture();
        f.Records[1] = Record("ordinary-polyline", kind: failure == "kind" ? "area" : "length",
            unit: failure == "unit" ? "m2" : "m", layer: failure == "layer" ? "DIFFERENT-LAYER" : "FIXTURE-CURB",
            profileId: failure == "profile" ? "DIFFERENT-PROFILE" : "6422");
        f.Reject(new[] { new Choice(Ordinary, "TEST.LENGTH") });
    }

    [Fact]
    public void IgnoredGroupCannotBeSilentlyReturnedAndMapped()
    {
        using var f = new Fixture();
        f.Profile.Estimate.IgnoredRuleDecisions.Add(new()
        {
            RuleKey = Ordinary, Reason = "Synthetic existing exclusion", ApprovedBy = Approver,
            ApprovedAtUtc = new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc),
        });
        f.Scan = f.NewScan();
        f.Reject(new[] { new Choice(Ordinary, "TEST.LENGTH") });
    }

    [Fact]
    public void MixedPresentationTypesCannotLoseTheirExplicitNativeTypeScope()
    {
        using var f = new Fixture();
        f.Records[0] = Record("ordinary-line", layer: "FIXTURE_LABEL", entityType: "LINE");
        f.Records[1] = Record("ordinary-polyline", layer: "FIXTURE_LABEL", entityType: "LWPOLYLINE");
        f.Reject(new[] { new Choice(Ordinary, "TEST.LENGTH") });
    }

    [Theory]
    [InlineData("missing-exclusion")]
    [InlineData("wrong-sibling")]
    [InlineData("self-sibling")]
    [InlineData("both-selected")]
    public void ClosedBoundaryRequiresOneExplicitExactAlternativeChoice(string failure)
    {
        using var f = new Fixture();
        var alternative = failure == "missing-exclusion" ? null : failure == "wrong-sibling" ? Unselected : failure == "self-sibling" ? Area : Perimeter;
        var choices = new List<Choice> { new(Area, "TEST.AREA", alternative) };
        if (failure == "both-selected") choices.Add(new(Perimeter, "TEST.LENGTH", Area));
        f.Reject(choices);
    }

    [Theory]
    [InlineData("open-line")]
    [InlineData("hatch")]
    public void MixedSourceRuleCannotBeFalselyExcludedAsWholeClosedSibling(string additionalSource)
    {
        using var f = new Fixture();
        f.Records.Add(additionalSource == "hatch"
            ? Record("unrelated-hatch", Area, "area", "m2", "FIXTURE-BOUNDARY", "HATCH", method: "hatch-area")
            : Record("unrelated-open", Perimeter, layer: "FIXTURE-BOUNDARY", method: "polyline-length"));
        f.Reject(new[] { new Choice(Area, "TEST.AREA", Perimeter) });
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("catalog")]
    [InlineData("absent-original-cas")]
    [InlineData("different-cas")]
    public void ChangedContextCannotBeReplacedAtTheSaveBoundary(string change)
    {
        using var f = new Fixture();
        var original = f.State;
        if (change == "profile") f.Profile.ProjectName = "Changed after original review context";
        if (change == "absent-original-cas") f.Scan.ProfileWriteState = null;
        f.Reject(new[] { new Choice(Ordinary, "TEST.LENGTH") },
            catalog: change == "catalog" ? Catalog(new string('b', 64)) : null,
            state: change == "different-cas" ? original with { SourceHash = new string('c', 64) } : original);
    }

    [Fact]
    public void ConcurrentDurableProfileEditIsPreservedAndRecapturedCasCannotReplaceOriginal()
    {
        using var f = new Fixture();
        ProfileCasTest.Save(f.Profile, f.TargetPath, "Synthetic initial profile", Approver);
        f.Scan = f.NewScan();
        var original = f.State;
        var other = ProjectProfileLoader.LoadFromFile(f.TargetPath).Profile!;
        other.ProjectName = "Synthetic concurrent edit must survive";
        ProfileCasTest.Save(other, f.TargetPath, "Synthetic concurrent change", "OTHER TEST REVIEWER");
        f.Reject(new[] { new Choice(Ordinary, "TEST.LENGTH") }, state: original);
        var recaptured = ProfileCasTest.For(f.Profile, f.TargetPath);
        f.Reject(new[] { new Choice(Ordinary, "TEST.LENGTH") }, state: recaptured);
        ProjectProfileLoader.LoadFromFile(f.TargetPath).Profile!.ProjectName.Should().Be(other.ProjectName);
    }

    [Fact]
    public void ValueEqualCopyOfOriginalCasRemainsValid()
    {
        using var f = new Fixture();
        f.Save(new[] { new Choice(Ordinary, "TEST.LENGTH") }, state: f.State with { });
        f.Profile.Estimate.QuantitySources.Rules.Should().ContainSingle();
    }
}
