using System;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate
{
    public sealed class CatalogPriceReconciliationTests
    {
        private const string SnapshotId = "book-2026";
        private const string Code = "U51.01.0250";
        private static readonly string Hash = new('a', 64);

        [Fact]
        public void PricedLine_MustReconcileDecimalIdHashAndItemToVerifiedSnapshot()
        {
            var (estimate, snapshot, line) = ValidPricedEstimate();
            CatalogIdentity.ValidateEstimatePricing(estimate, snapshot).Should().BeEmpty();

            line.Price = 999.99m;
            line.PriceBookId = "other-book";
            line.PriceDecisionSource = new string('b', 64);
            line.ApprovedCatalogItemFingerprint = new string('c', 64);

            var errors = CatalogIdentity.ValidateEstimatePricing(estimate, snapshot);

            errors.Should().Contain(error => error.Contains("exported price differs", StringComparison.Ordinal));
            errors.Should().Contain(error => error.Contains("price-book id differs", StringComparison.Ordinal));
            errors.Should().Contain(error => error.Contains("price source hash differs", StringComparison.Ordinal));
            errors.Should().Contain(error => error.Contains("item binding is not current", StringComparison.Ordinal));
        }

        [Fact]
        public void PricedLine_RejectsPriceRecordFromAnotherSnapshotEvenWhenDecimalMatches()
        {
            var (estimate, snapshot, _) = ValidPricedEstimate();
            snapshot.Prices[Code] = new PriceRecord
            {
                Code = Code,
                Price = 42.50m,
                PriceBookId = SnapshotId,
                SourceHash = new string('b', 64),
            };

            CatalogIdentity.ValidateEstimatePricing(estimate, snapshot)
                .Should().ContainSingle(error =>
                    error.Contains("no matching authoritative price record", StringComparison.Ordinal));
        }

        [Fact]
        public void ProjectOverride_KeepsProfilePriceAuthorityButStillRequiresCurrentCatalogItemBinding()
        {
            var (estimate, snapshot, line) = ValidPricedEstimate();
            snapshot.Prices.Clear();
            line.PriceStatus = PriceStatus.ProjectOverride;
            line.Price = 55m;
            line.PriceBookId = "override:tender-17";
            line.PriceDecisionSource = "tender-17";

            CatalogIdentity.ValidateEstimatePricing(estimate, snapshot).Should().BeEmpty();

            line.ApprovedCatalogHash = new string('b', 64);
            CatalogIdentity.ValidateEstimatePricing(estimate, snapshot)
                .Should().ContainSingle(error =>
                    error.Contains("item binding is not current", StringComparison.Ordinal));
        }

        private static (EstimateResult Estimate, CatalogSnapshot Snapshot, EstimateLine Line)
            ValidPricedEstimate()
        {
            var item = new CatalogItem
            {
                Code = Code,
                Description = "אבן שפה",
                UnitRaw = "מטר",
            };
            var snapshot = new CatalogSnapshot
            {
                SnapshotId = SnapshotId,
                FileHash = Hash,
                Items = { [Code] = item },
                Prices =
                {
                    [Code] = new PriceRecord
                    {
                        Code = Code,
                        Price = 42.50m,
                        PriceBookId = SnapshotId,
                        SourceHash = Hash,
                    },
                },
            };
            var line = new EstimateLine
            {
                LineId = "L0001",
                RecordId = "R0001",
                CatalogCode = Code,
                ApprovedCatalogId = SnapshotId,
                ApprovedCatalogHash = Hash,
                ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(item),
                Price = 42.50m,
                PriceStatus = PriceStatus.Priced,
                PriceBookId = SnapshotId,
                PriceDecisionSource = Hash,
            };
            var estimate = new EstimateResult
            {
                RunId = "run-price-reconcile",
                ProjectProfileId = "6422",
                PriceBookId = SnapshotId,
                PriceBookHash = Hash,
                Lines = { line },
            };
            return (estimate, snapshot, line);
        }
    }
}
