using System;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class ProjectPriceGroupContextTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTime When = new(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc);

    private static CatalogSnapshot Catalog() => new()
    {
        SnapshotId = "GROUP-CONTEXT-FIXTURE", FileHash = Hash,
        Items =
        {
            ["TEST.1"] = new() { Code = "TEST.1", Description = "Synthetic first item", UnitRaw = "m" },
            ["TEST.2"] = new() { Code = "TEST.2", Description = "Synthetic second item", UnitRaw = "m" },
        },
        // Missing catalog prices are precisely why a pre-build price action is useful.
    };

    private static ProjectProfile Profile() => new()
    {
        ProfileId = "PRICE-GROUP-FIXTURE", ProjectName = "SIMULATION ONLY",
        Estimate = new()
        {
            Catalog = new() { CatalogFile = "fixture.xlsx", CatalogFileHash = Hash },
            Pricing = new() { PriceBookSnapshotId = "GROUP-CONTEXT-FIXTURE", PriceBookHash = Hash },
            PriceBooks = { new() { Id = "GROUP-CONTEXT-FIXTURE", File = "fixture.xlsx", FileHash = Hash } },
        },
    };

    private static NeutralQuantityRecord Record(CatalogSnapshot catalog, string id = "r1",
        string code = "TEST.1", string unit = "m") => new()
    {
        RecordId = id, ProjectProfileId = "PRICE-GROUP-FIXTURE", RunId = "synthetic-price-group",
        Source = new() { Drawing = "SIMULATION.dwg", DrawingHash = Hash, Handle = id, EntityType = "LINE", Layer = "TEST-LAYER" },
        Measurement = new() { Kind = "length", Method = "line-length", RawValue = 37.25, Unit = unit },
        Classification = new()
        {
            RuleKey = "layer:TEST-LAYER|length", CandidateCatalogCode = code,
            ApprovedCatalogId = catalog.SnapshotId, ApprovedCatalogHash = catalog.FileHash,
            ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(catalog.Items[code]),
            MappingApprovedBy = "FIXTURE APPROVER", MappingApprovedAtUtc = When,
        },
        Status = DeliveryStatus.Ready,
    };

    [Fact]
    public void MissingCatalogPriceIsReachableBeforeBuildWithoutWritingPricesOrMappings()
    {
        var profile = Profile(); var catalog = Catalog();
        var records = new[] { Record(catalog), Record(catalog, "r2", unit: "מטר") };
        var profileBefore = EstimateTraceIdentity.EffectiveProfileHash(profile);
        var recordsBefore = JsonSerializer.Serialize(records);

        var context = ProjectPriceApprovalPolicy.CaptureForRecords(profile, catalog, "TEST.1", records);

        context.Should().Be(ProjectPriceApprovalPolicy.Capture(profile, catalog, "TEST.1", "m"));
        catalog.Prices.Should().BeEmpty(); profile.Estimate.ProjectOverrides.Should().BeEmpty();
        profile.Estimate.QuantitySources.Rules.Should().BeEmpty(); // current-profile mapping checks belong to the host
        EstimateTraceIdentity.EffectiveProfileHash(profile).Should().Be(profileBefore);
        JsonSerializer.Serialize(records).Should().Be(recordsBefore);
    }

    [Fact]
    public void ValidExistingProjectOverrideRemainsEditableAndIsNotReplacedByCapture()
    {
        var profile = Profile(); var catalog = Catalog();
        var previous = new ProjectProfile.EstimateProfile.PriceOverride
        {
            ItemCode = "TEST.1", Price = 117.50m, Source = "FIXTURE Q-001", Reason = "Synthetic prior decision",
            ApprovedBy = "FIXTURE APPROVER", ApprovedAtUtc = When,
            ApprovedCatalogId = catalog.SnapshotId, ApprovedCatalogHash = catalog.FileHash,
            ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(catalog.Items["TEST.1"]), ExpectedUnit = "m",
        };
        profile.Estimate.ProjectOverrides.Add(previous);
        var before = EstimateTraceIdentity.EffectiveProfileHash(profile);
        ProjectPriceApprovalPolicy.BindingMatches(previous, catalog, catalog.Items["TEST.1"]).Should().BeTrue();

        var context = ProjectPriceApprovalPolicy.CaptureForRecords(profile, catalog, "TEST.1", new[] { Record(catalog) });

        context.ItemCode.Should().Be("TEST.1");
        profile.Estimate.ProjectOverrides.Should().ContainSingle().Which.Should().BeSameAs(previous);
        previous.Price.Should().Be(117.50m); catalog.Prices.Should().BeEmpty();
        EstimateTraceIdentity.EffectiveProfileHash(profile).Should().Be(before);
    }

    [Fact]
    public void EmptyGroupCannotCapturePriceContext()
    {
        Action act = () => ProjectPriceApprovalPolicy.CaptureForRecords(Profile(), Catalog(), "TEST.1", Array.Empty<NeutralQuantityRecord>());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void IndividuallyCurrentButDifferentCatalogCodesCannotBecomeOnePriceGroup()
    {
        var catalog = Catalog(); var records = new[] { Record(catalog), Record(catalog, "r2", "TEST.2") };
        foreach (var record in records) CatalogIdentity.IsClassificationCurrent(record.Classification, catalog).Should().BeTrue();
        Action act = () => ProjectPriceApprovalPolicy.CaptureForRecords(Profile(), catalog, "TEST.1", records);
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("m2")]
    [InlineData("unknown-unit")]
    public void EveryRecordUnitMustMatchTheItemNotOnlyTheFirst(string secondUnit)
    {
        var catalog = Catalog(); var records = new[] { Record(catalog), Record(catalog, "r2", unit: secondUnit) };
        Action act = () => ProjectPriceApprovalPolicy.CaptureForRecords(Profile(), catalog, "TEST.1", records);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void UniformlyWrongUnitStillCannotCapturePriceContext()
    {
        var catalog = Catalog();
        Action act = () => ProjectPriceApprovalPolicy.CaptureForRecords(Profile(), catalog, "TEST.1",
            new[] { Record(catalog, unit: "m2"), Record(catalog, "r2", unit: "m2") });
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("catalog-id")]
    [InlineData("catalog-hash")]
    [InlineData("item-fingerprint")]
    [InlineData("missing-approver")]
    [InlineData("missing-time")]
    [InlineData("missing-code")]
    public void StaleOrUnapprovedClassificationInAnyRecordCannotCapture(string change)
    {
        var catalog = Catalog(); var records = new[] { Record(catalog), Record(catalog, "r2") };
        var classification = records[1].Classification;
        switch (change)
        {
            case "catalog-id": classification.ApprovedCatalogId = "ANOTHER-CATALOG"; break;
            case "catalog-hash": classification.ApprovedCatalogHash = new string('b', 64); break;
            case "item-fingerprint": classification.ApprovedCatalogItemFingerprint = new string('c', 64); break;
            case "missing-approver": classification.MappingApprovedBy = null; break;
            case "missing-time": classification.MappingApprovedAtUtc = null; break;
            case "missing-code": classification.CandidateCatalogCode = null; break;
        }
        var before = JsonSerializer.Serialize(records);
        Action act = () => ProjectPriceApprovalPolicy.CaptureForRecords(Profile(), catalog, "TEST.1", records);
        act.Should().Throw<InvalidOperationException>();
        JsonSerializer.Serialize(records).Should().Be(before);
    }

    [Fact]
    public void CurrentRecordPinsDoNotOverrideDifferentActiveProfileCatalog()
    {
        var profile = Profile(); var catalog = Catalog();
        profile.Estimate.Pricing.PriceBookHash = new string('b', 64);
        Action act = () => ProjectPriceApprovalPolicy.CaptureForRecords(profile, catalog, "TEST.1", new[] { Record(catalog) });
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void PriceContextDoesNotResolveOrApproveMeasurementFindings()
    {
        var catalog = Catalog(); var record = Record(catalog);
        record.Status = DeliveryStatus.Failed;
        var finding = new DeliveryFinding
        {
            Code = EstimateFindingCodes.MeasurementFailed, Domain = "estimate", Severity = FindingSeverity.Error,
            Title = "Synthetic source issue", AffectedRecordIds = { record.RecordId },
        };
        record.Findings.Add(finding);
        ProjectPriceApprovalPolicy.CaptureForRecords(Profile(), catalog, "TEST.1", new[] { record }).ItemCode.Should().Be("TEST.1");
        record.Status.Should().Be(DeliveryStatus.Failed); finding.ResolvedAtUtc.Should().BeNull();
        EstimatePreflightPolicy.IsBlocking(finding).Should().BeTrue();
    }
}
