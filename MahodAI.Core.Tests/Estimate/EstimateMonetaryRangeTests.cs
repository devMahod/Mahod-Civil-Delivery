using System.IO;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>Explicit numeric-boundary simulations; never real project prices or quantities.</summary>
public sealed class EstimateMonetaryRangeTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTime When = new(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CatalogAndApprovedOverrideMultiplicationOverflowReturnBlockingEvidence(bool projectOverride)
    {
        var fixture = Fixture(2, decimal.MaxValue, 1, sameCode: true, projectOverride);
        var result = EstimateBuilder.Build(fixture.Records, fixture.Catalog, fixture.Profile, "SIMULATION-OVERFLOW");
        RequireBlocked(result);
        result.Lines.Single().RawQuantity.Should().Be(2);
        result.Lines.Single().Price.Should().Be(decimal.MaxValue);
        result.Findings.Should().Contain(f => f.Code == EstimateBoqSemantics.MonetaryRangeExceededCode && f.Message.Contains("Record"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void IndividuallyRepresentableLinesCannotProduceOverflowingGroupOrGrandTotal(bool sameCode)
    {
        var price = decimal.MaxValue / 2m + 10m;
        var fixture = Fixture(1, price, 2, sameCode, projectOverride: false);
        var result = EstimateBuilder.Build(fixture.Records, fixture.Catalog, fixture.Profile, "SIMULATION-TOTAL-OVERFLOW");
        RequireBlocked(result);
        result.Lines.Should().HaveCount(2).And.OnlyContain(line => line.RawQuantity == 1 && line.Price == price);
        result.Findings.Should().Contain(f => f.Code == EstimateBoqSemantics.MonetaryRangeExceededCode && f.AffectedRecordIds.Count == 2);
    }

    [Fact]
    public void PreflightRetotalizationAlsoBlocksAnUnrepresentableRemainingSum()
    {
        var price = decimal.MaxValue / 2m + 10m;
        var result = new EstimateResult { RunId = "SIMULATION-PREFLIGHT", ProjectProfileId = "SIMULATION" };
        for (var i = 0; i < 3; i++) result.Lines.Add(new EstimateLine
        {
            LineId = "L" + i, RecordId = "r" + i, CatalogCode = "TEST." + i,
            RawQuantity = 1, BoqQuantity = 1, Unit = "m", Price = i == 2 ? 1 : price,
            Total = i == 2 ? 1 : price, IncludedInTotals = true, PriceStatus = PriceStatus.Priced, Status = DeliveryStatus.Ready,
        });
        EstimatePreflightPolicy.Apply(result, new[] { new DeliveryFinding
        {
            Code = "SIMULATION-BLOCK", Domain = "estimate", Severity = FindingSeverity.Error,
            Title = "Synthetic third-row exclusion", AffectedRecordIds = { "r2" },
        } });
        RequireBlocked(result);
    }

    [Fact]
    public void OrdinaryPricesStillReconcileAndRemainReady()
    {
        var fixture = Fixture(2, 10.25m, 2, sameCode: true, projectOverride: false);
        var result = EstimateBuilder.Build(fixture.Records, fixture.Catalog, fixture.Profile, "SIMULATION-CONTROL");
        result.Status.Should().Be(DeliveryStatus.Ready);
        result.CleanTotal.Should().Be(41m);
        EstimatePreflightPolicy.CanExport(result).Should().BeTrue();
    }

    private static void RequireBlocked(EstimateResult result)
    {
        result.Status.Should().Be(DeliveryStatus.Failed);
        result.CleanTotal.Should().Be(0m, "non-authoritative failed storage sentinel, not an estimate of zero");
        result.Lines.Should().OnlyContain(line => !line.IncludedInTotals && line.Total == null);
        result.Findings.Should().Contain(f => f.Code == EstimateBoqSemantics.MonetaryRangeExceededCode && f.Severity == FindingSeverity.Error);
        EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
        var path = Path.Combine(Path.GetTempPath(), "mahod-overflow-export-must-not-exist", Guid.NewGuid().ToString("N"));
        Action export = () => EstimateExcelWriter.Write(result, path, "MUST-NOT-EXIST");
        export.Should().Throw<InvalidOperationException>();
        Directory.Exists(path).Should().BeFalse();
    }

    private static (ProjectProfile Profile, CatalogSnapshot Catalog, List<NeutralQuantityRecord> Records) Fixture(
        double quantity, decimal price, int count, bool sameCode, bool projectOverride)
    {
        var catalog = new CatalogSnapshot { SnapshotId = "SYNTHETIC-BOUNDARY", FileHash = Hash };
        var profile = new ProjectProfile { ProfileId = "SIMULATION", ProjectName = "SIMULATION ONLY" };
        profile.Estimate.Catalog.CatalogFile = "synthetic.xlsx";
        profile.Estimate.Catalog.CatalogFileHash = Hash;
        profile.Estimate.Pricing.PriceBookSnapshotId = catalog.SnapshotId;
        profile.Estimate.Pricing.PriceBookHash = Hash;
        profile.Estimate.PriceBooks.Add(new() { Id = catalog.SnapshotId, File = "synthetic.xlsx", FileHash = Hash });
        profile.Estimate.QuantitySources.SourceScopePolicy = EstimatePreflightPolicy.DiscoverAllSourceScopePolicy;
        profile.Estimate.QuantitySources.XrefPolicy = EstimatePreflightPolicy.IncludeXrefsPolicy;
        profile.Estimate.Earthworks.Requested = false;
        profile.Estimate.Earthworks.DecidedBy = "SIMULATION";
        profile.Estimate.Earthworks.DecidedAtUtc = When;
        profile.Estimate.Earthworks.Reason = "No earthworks in numeric boundary simulation";
        var records = new List<NeutralQuantityRecord>();
        for (var i = 0; i < count; i++)
        {
            var code = "TEST." + (sameCode ? 0 : i);
            if (!catalog.Items.ContainsKey(code))
            {
                catalog.Items.Add(code, new() { Code = code, Description = "Synthetic numeric boundary item", UnitRaw = "m" });
                catalog.Prices.Add(code, new() { Code = code, Price = price, PriceBookId = catalog.SnapshotId, SourceHash = Hash });
                if (projectOverride) profile.Estimate.ProjectOverrides.Add(new()
                {
                    ItemCode = code, Price = price, Source = "SIMULATION", Reason = "Numeric boundary",
                    ApprovedBy = "SIMULATION", ApprovedAtUtc = When, ApprovedCatalogId = catalog.SnapshotId,
                    ApprovedCatalogHash = Hash, ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(catalog.Items[code]), ExpectedUnit = "m",
                });
            }
            records.Add(new()
            {
                RecordId = "r" + i, ProjectProfileId = profile.ProfileId, RunId = "SIMULATION",
                Source = new() { Drawing = "synthetic.json", DrawingPath = "synthetic.json", DrawingHash = Hash,
                    Handle = "SIM-" + i, EntityType = "SIMULATED-LINE", Layer = "NEW-QUANTITY-" + i },
                Measurement = new() { Kind = "length", Method = "synthetic-length", Unit = "m", RawValue = quantity },
                Classification = new() { RuleKey = "layer:NEW-QUANTITY-" + i + "|length", CandidateCatalogCode = code,
                    ApprovedCatalogId = catalog.SnapshotId, ApprovedCatalogHash = Hash,
                    ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(catalog.Items[code]),
                    MappingApprovedBy = "SIMULATION", MappingApprovedAtUtc = When },
            });
        }
        return (profile, catalog, records);
    }
}
