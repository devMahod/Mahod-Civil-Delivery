using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public class LegacyPriceBookTests
{
    [Fact]
    public void LegacyExcel_PreservesHebrewNumericPricesAndRegistryIdentity()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcd-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "source.xls");
            File.Copy(Path.Combine(EstimateFixtures.RepoRoot(), "MahodAI.Core.Tests", "Fixtures", "legacy-price-book.xls"), file);
            var bytes = File.ReadAllBytes(file);
            PriceBookWorkbook.ReadSheetNames(bytes).Should().Equal("מחירון", "אחר");
            var row = PriceBookWorkbook.ReadSheet(bytes, "מחירון")[1];
            row[1].Text.Should().Be("אבן שפה");
            row[3].IsNumeric.Should().BeTrue();
            row[3].Text.Should().Be("42.5");
            PriceBookWorkbook.ReadSheet(bytes, "אחר").Single().Single().Text.Should().Be("אחר");
            var inspection = PriceBookXlsxLoader.Inspect(file);
            inspection.IsUsable.Should().BeTrue();
            var profile = new ProjectProfile();
            var registered = PriceBookRegistry.Register(profile, Path.Combine(dir, "project"), file,
                "test", id: "legacy", makeActive: true, expectedInspectionHash: inspection.FileHash);
            registered.Entry.File.Should().Be("legacy.xls");
            PriceBookXlsxLoader.Inspect(registered.StoredPath).FileHash.Should().Be(inspection.FileHash);
            var mapping = new PriceBookXlsxLoader.ColumnMapping(inspection.FileHash, "מחירון", 1, "A", "B", "C", "D");
            PriceBookXlsxLoader.Inspect(registered.StoredPath, mapping).ItemCount.Should().Be(1);
            var missingSheet = () => PriceBookWorkbook.ReadSheet(bytes, "missing");
            missingSheet.Should().Throw<InvalidOperationException>();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
