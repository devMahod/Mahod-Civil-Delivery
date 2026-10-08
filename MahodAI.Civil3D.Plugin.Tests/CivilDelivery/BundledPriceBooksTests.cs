using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public class BundledPriceBooksTests
{
    [Fact]
    public void BundledNti_IsAvailableWithoutDownloads_AndRegistersExactEdition()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcd-bundled-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = new ProjectProfile();
            var offer = BundledPriceBooks.Offers(dir, profile).Single();
            profile.Estimate.PriceBooks.Should().BeEmpty();
            var inspection = PriceBookXlsxLoader.Inspect(offer.Entry.Path);
            inspection.IsUsable.Should().BeTrue();
            inspection.ItemCount.Should().Be(9783);
            inspection.FileHash.Should().BeEquivalentTo(BundledPriceBooks.Sha256);
            File.WriteAllText(offer.Entry.Path, "damaged extracted copy");
            offer = BundledPriceBooks.Offers(dir, profile).Single();
            ArtifactHash.Sha256OfFile(offer.Entry.Path).Should().BeEquivalentTo(BundledPriceBooks.Sha256);
            var registered = PriceBookRegistry.Register(profile, Path.Combine(dir, "project"), offer.Entry.Path,
                "test", makeActive: true, expectedInspectionHash: offer.Entry.Sha256,
                publisher: offer.Entry.Publisher, edition: offer.Entry.Edition);
            registered.MadeActive.Should().BeTrue();
            registered.Entry.FileHash.Should().BeEquivalentTo(BundledPriceBooks.Sha256);
            PriceBookXlsxLoader.Inspect(registered.StoredPath).ItemCount.Should().Be(9783);
            BundledPriceBooks.Offers(dir, profile).Should().BeEmpty();
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
