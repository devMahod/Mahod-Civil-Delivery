using System;
using System.Collections.Generic;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Family recognition without a loaded price list: families only, and never over an unverifiable item mapping.</summary>
public sealed class RecognitionCatalogTests
{
    private static NeutralQuantityRecord Record(QuantityClassification? classification = null) => new()
    {
        RecordId = Guid.NewGuid().ToString("N"),
        ProjectProfileId = "TEST-ONLY",
        RunId = "TEST-ONLY",
        Source = new QuantitySource { Drawing = "t.dwg", DrawingHash = new string('a', 64), Handle = "1", EntityType = "HATCH", Layer = "QZ987" },
        Measurement = new QuantityMeasurement { Kind = "area", Method = "hatch-area", RawValue = 10, Unit = "מ\"ר" },
        Classification = classification ?? new QuantityClassification { RuleKey = "layer:QZ987|area" },
    };

    [Fact]
    public void ALoadedPriceListIsUsedAsIs()
    {
        var loaded = new CatalogSnapshot { SnapshotId = "REAL", FileHash = new string('b', 64) };
        Assert.Same(loaded, EstimateWorkflowService.RecognitionCatalog(loaded, Array.Empty<string>(), new[] { Record() }));
    }

    [Fact]
    public void WithoutAPriceListFamiliesAreRecognisedAgainstAnEmptyNamedList()
    {
        var snapshot = EstimateWorkflowService.RecognitionCatalog(null, new[] { "no catalog" }, new[] { Record(), Record() });
        Assert.Equal(EstimateWorkflowService.NoCatalogSnapshotId, snapshot.SnapshotId);
        Assert.Empty(snapshot.Items);
        Assert.Empty(snapshot.Prices);
    }

    public static IEnumerable<object[]> Mapped()
    {
        yield return new object[] { new QuantityClassification { RuleKey = "k", CandidateCatalogCode = "U51.04.1820" } };
        yield return new object[] { new QuantityClassification { RuleKey = "k", ApprovedCatalogId = "CAT" } };
        yield return new object[] { new QuantityClassification { RuleKey = "k", MappingApprovedBy = "engineer" } };
    }

    [Theory]
    [MemberData(nameof(Mapped))]
    public void AnItemMappingThatCannotBeVerifiedKeepsTheRefusal(QuantityClassification mapped)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            EstimateWorkflowService.RecognitionCatalog(null, new[] { "catalog missing" }, new[] { Record(), Record(mapped) }));
        Assert.Contains("1 רשומות", error.Message);
        Assert.Contains("catalog missing", error.Message);
    }
}
