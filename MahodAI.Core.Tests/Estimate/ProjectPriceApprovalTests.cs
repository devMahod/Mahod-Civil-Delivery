using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MahodAI.Core.Tests.Estimate;

public sealed class ProjectPriceApprovalTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTime When = new(2026, 9, 7, 11, 0, 0, DateTimeKind.Utc);
    private static CatalogSnapshot Catalog(string hash = Hash, string description = "Synthetic item") => new()
    {
        SnapshotId = "FIXTURE-ONLY", FileHash = hash,
        Items = { ["TEST.1"] = new() { Code = "TEST.1", Description = description, UnitRaw = "m" } },
    };
    private static ProjectProfile Profile() => new()
    {
        ProfileId = "PRICE-FIXTURE", ProjectName = "SIMULATION ONLY",
        Estimate = new()
        {
            Catalog = new() { CatalogFile = "fixture.xlsx", CatalogFileHash = Hash },
            Pricing = new() { PriceBookSnapshotId = "FIXTURE-ONLY", PriceBookHash = Hash },
            PriceBooks = { new() { Id = "FIXTURE-ONLY", File = "fixture.xlsx", FileHash = Hash } },
        },
    };
    private static ProjectPriceApprovalPolicy.Approval Approve(ProjectProfile profile, CatalogSnapshot catalog) =>
        ProjectPriceApprovalPolicy.Approve(ProjectPriceApprovalPolicy.Capture(profile, catalog, "TEST.1", "m"),
            117.50m, "SIMULATION quote Q-001", "Synthetic test decision", "FIXTURE APPROVER", When, true)!;

    [Fact]
    public void CancellationNeverProducesAnApprovalOrMutatesProfile()
    {
        var profile = Profile(); var catalog = Catalog();
        var before = EstimateTraceIdentity.EffectiveProfileHash(profile);
        var context = ProjectPriceApprovalPolicy.Capture(profile, catalog, "TEST.1", "m");
        ProjectPriceApprovalPolicy.Approve(context, null, null, null, null, default, false).Should().BeNull();
        EstimateTraceIdentity.EffectiveProfileHash(profile).Should().Be(before);
        profile.Estimate.ProjectOverrides.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public void NonPositivePriceCannotBeApproved(int price)
    {
        var context = ProjectPriceApprovalPolicy.Capture(Profile(), Catalog(), "TEST.1", "m");
        Action act = () => ProjectPriceApprovalPolicy.Approve(context, price, "source", "reason", "name", When, true);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EveryEvidenceFieldAndUtcAreRequiredAndHeaderInjectionIsRejected()
    {
        foreach (var fields in new[] { ("", "reason", "name"), ("source", "", "name"), ("source", "reason", ""), ("source", "reason", "name\nprofile_id: other") })
            ProjectPriceApprovalPolicy.Validate(1m, fields.Item1, fields.Item2, fields.Item3, When).Should().NotBeEmpty();
        ProjectPriceApprovalPolicy.Validate(1m, "source", "reason", "name", DateTime.SpecifyKind(When, DateTimeKind.Unspecified)).Should().NotBeEmpty();
        ProjectPriceApprovalPolicy.Validate(decimal.MaxValue, "source", "reason", "name", When).Should().BeEmpty(); // decimal is finite, never parsed as floating point
    }

    [Fact]
    public void WrongUnitUnknownItemOrChangedItemCannotReuseApprovalContext()
    {
        var profile = Profile(); var catalog = Catalog();
        Action wrongUnit = () => ProjectPriceApprovalPolicy.Capture(profile, catalog, "TEST.1", "m2");
        wrongUnit.Should().Throw<InvalidOperationException>();
        Action absent = () => ProjectPriceApprovalPolicy.Capture(profile, catalog, "NOT-REAL", "m");
        absent.Should().Throw<InvalidOperationException>();
        var approved = Approve(profile, catalog);
        catalog = Catalog(description: "Different item");
        Action changed = () => ProjectPriceApprovalPolicy.RequireUnchanged(approved.Context, profile, catalog);
        changed.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void InMemoryProfileMutationAndWrongCatalogRejectOriginalContext()
    {
        var profile = Profile(); var catalog = Catalog(); var approved = Approve(profile, catalog);
        profile.ProjectName = "Changed while modal was open";
        Action changed = () => ProjectPriceApprovalPolicy.RequireUnchanged(approved.Context, profile, catalog);
        changed.Should().Throw<InvalidOperationException>();
        profile = Profile(); catalog = Catalog(new string('b', 64));
        Action wrongBook = () => ProjectPriceApprovalPolicy.RequireUnchanged(approved.Context, profile, catalog);
        wrongBook.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CatalogPinsAreAllOrNoneAndExactWhileLegacyContractRemainsCompatible()
    {
        var catalog = Catalog(); var item = catalog.Items["TEST.1"];
        var value = new ProjectProfile.EstimateProfile.PriceOverride { ItemCode = "TEST.1" };
        ProjectPriceApprovalPolicy.BindingMatches(value, catalog, item).Should().BeTrue();
        value.ApprovedCatalogId = catalog.SnapshotId;
        ProjectPriceApprovalPolicy.BindingMatches(value, catalog, item).Should().BeFalse();
        value.ApprovedCatalogHash = Hash; value.ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(item); value.ExpectedUnit = "m";
        ProjectPriceApprovalPolicy.BindingMatches(value, catalog, item).Should().BeTrue();
        value.ExpectedUnit = "m2";
        ProjectPriceApprovalPolicy.BindingMatches(value, catalog, item).Should().BeFalse();
        value.ExpectedUnit = "m"; catalog = Catalog(new string('b', 64));
        ProjectPriceApprovalPolicy.BindingMatches(value, catalog, item).Should().BeFalse();
    }

    [Fact]
    public void WriterPersistsExactScopedPricePinsAndBackupThenRejectsConcurrentSave()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mahod-price-approval-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "profile.yaml");
        var profile = Profile(); var catalog = Catalog();
        profile.Estimate.ProjectOverrides.Add(new() { ItemCode = "UNRELATED", Price = 9m, Source = "other", Reason = "keep", ApprovedBy = "other", ApprovedAtUtc = When });
        var serializer = new SerializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build();
        var original = serializer.Serialize(profile); File.WriteAllText(path, original);
        var expected = ProjectProfileWriter.CaptureExpectedState(path, ArtifactHash.Sha256OfText(original), path);
        var approved = Approve(profile, catalog);
        var saved = ProjectPriceApprovalWriter.Save(profile, catalog, approved, path, expected);
        File.ReadAllText(saved.BackupPath).Should().Be(original);
        ArtifactHash.Sha256OfText(File.ReadAllText(path)).Should().Be(saved.NewHash);
        var reloaded = ProjectProfileLoader.LoadFromFile(path).Profile!;
        var written = reloaded.Estimate.ProjectOverrides.Single(value => value.ItemCode == "TEST.1");
        written.Price.Should().Be(117.50m); written.Source.Should().Be(approved.Source);
        written.Reason.Should().Be(approved.Reason); written.ApprovedBy.Should().Be(approved.ApprovedBy); written.ApprovedAtUtc.Should().Be(When);
        written.ApprovedCatalogHash.Should().Be(Hash); written.ApprovedCatalogId.Should().Be(catalog.SnapshotId);
        written.ApprovedCatalogItemFingerprint.Should().Be(CatalogIdentity.ItemFingerprint(catalog.Items["TEST.1"]));
        written.ExpectedUnit.Should().Be("m");
        reloaded.Estimate.ProjectOverrides.Single(value => value.ItemCode == "UNRELATED").Price.Should().Be(9m);

        var next = Approve(profile, catalog); var inMemoryBefore = EstimateTraceIdentity.EffectiveProfileHash(profile);
        var bytesBefore = File.ReadAllText(path);
        Action stale = () => ProjectPriceApprovalWriter.Save(profile, catalog, next, path, expected);
        stale.Should().Throw<InvalidOperationException>();
        File.ReadAllText(path).Should().Be(bytesBefore);
        EstimateTraceIdentity.EffectiveProfileHash(profile).Should().Be(inMemoryBefore);
    }

    [Fact]
    public void TargetFailureRestoresAllPreviousOverrideObjectsAndProvenance()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mahod-price-approval-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.yaml"); var target = Path.Combine(directory, "wrong-target.yaml");
        var profile = Profile(); var catalog = Catalog();
        var previous = new ProjectProfile.EstimateProfile.PriceOverride { ItemCode = "TEST.1", Price = 12m, Source = "old", Reason = "old", ApprovedBy = "old", ApprovedAtUtc = When };
        profile.Estimate.ProjectOverrides.Add(previous);
        File.WriteAllText(source, new SerializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build().Serialize(profile));
        var expected = ProjectProfileWriter.CaptureExpectedState(source, ArtifactHash.Sha256OfText(File.ReadAllText(source)), source);
        var approved = Approve(profile, catalog); var before = EstimateTraceIdentity.EffectiveProfileHash(profile);
        Action fail = () => ProjectPriceApprovalWriter.Save(profile, catalog, approved, target, expected);
        fail.Should().Throw<InvalidOperationException>();
        profile.Estimate.ProjectOverrides.Should().ContainSingle().Which.Should().BeSameAs(previous);
        EstimateTraceIdentity.EffectiveProfileHash(profile).Should().Be(before); File.Exists(target).Should().BeFalse();
    }
}
