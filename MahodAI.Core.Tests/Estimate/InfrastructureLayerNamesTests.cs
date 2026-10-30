using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class InfrastructureLayerNamesTests
{
    [Theory]
    [InlineData("- M6091 HASHMAL", "חשמל")]
    [InlineData("M6091HASHMAL_EX", "חשמל")]
    [InlineData("HashmalNewDN110", "חשמל")]
    [InlineData("CHASHMAL-UG", "חשמל")]
    [InlineData("KHASHMAL_250", "חשמל")]
    [InlineData("חשמל_חדש", "חשמל")]
    [InlineData("MAYIM110", "מים")]
    [InlineData("W-DE-8", "מים")]
    [InlineData("UT-W-EX", "מים")]
    [InlineData("UT-S-EX", "ביוב")]
    [InlineData("BIYUV315", "ביוב")]
    [InlineData("NIKOUZ_80", "ניקוז")]
    [InlineData("C-STRM-PIPE", "ניקוז")]
    [InlineData("DR-PIPE-INVR", "ניקוז")]
    [InlineData("TIKSHORET_NEW", "תקשורת")]
    [InlineData("BEZEK_EX", "תקשורת")]
    [InlineData("- M4015 HOT", "תקשורת")]
    [InlineData("TAURA_NEW", "תאורה")]
    [InlineData("RAMZORIM_01", "רמזורים")]
    [InlineData("HASHKAY", "השקיה")]
    [InlineData("HASHKAYA_32", "השקיה")]
    [InlineData("GAZ-NEW", "גז")]
    public void Engineering_spellings_and_numeric_suffixes_supply_topics(string layer, string expected) =>
        Assert.Equal(expected, InfrastructureBoq.Classify(layer, "unclassified.dwg").Topic);

    [Theory]
    [InlineData("DRAWING")]
    [InlineData("WATERPROOFING")]
    [InlineData("GASOLINE")]
    [InlineData("SELECTRICISH")]
    [InlineData("HOT")]
    [InlineData("LIGHT RAIL")]
    [InlineData("S-COLS")]
    [InlineData("TR-NOTE2")]
    [InlineData("0_2D MIGR")]
    [InlineData("HW-CURB")]
    [InlineData("M4404")]
    public void Unrelated_words_and_previous_drawing_symbols_do_not_invent_utility_topics(string layer) =>
        Assert.Equal(InfrastructureBoq.Unknown, InfrastructureBoq.Classify(layer, "984-HW-CIVIL.dwg").Topic);

    [Theory]
    [InlineData("ניקוז מים", "ניקוז")]
    [InlineData("WASTE_WATER", "ביוב")]
    [InlineData("STORM WATER", "ניקוז")]
    [InlineData("מים להשקיה", "השקיה")]
    [InlineData("HOT WATER", "מים")]
    public void Compound_meaning_supersedes_its_generic_water_word(string layer, string expected) =>
        Assert.Equal(expected, InfrastructureBoq.Classify(layer, "model.dwg").Topic);

    [Fact]
    public void Leaf_names_win_over_source_names_but_real_leaf_conflicts_are_kept_open()
    {
        Assert.Equal("חשמל", InfrastructureBoq.Classify("WATER-XREF|HASHMAL", "water.dwg").Topic);
        Assert.Equal("מים", InfrastructureBoq.Classify("ELEC-XREF|MAYIM", "electric.dwg").Topic);
        Assert.Equal("ביוב", InfrastructureBoq.Classify("pipe", "C:/WATER/models/BIYUV.dwg").Topic);
        Assert.Equal(InfrastructureBoq.Unknown, InfrastructureBoq.Classify("MAYIM-HASHMAL", "water.dwg").Topic);
        Assert.Equal(InfrastructureBoq.Unknown, InfrastructureBoq.Classify("GAS-WT", "water.dwg").Topic);
        Assert.Equal(InfrastructureBoq.Unknown, InfrastructureBoq.Classify("pipe", "C:/WATER/models/project.dwg").Topic);
        Assert.Contains("HASHMAL", InfrastructureBoq.Classify("HASHMAL-NEW", "").Evidence);
    }

    [Fact]
    public void Transliterated_search_and_layer_topics_retrieve_items_without_mapping_them()
    {
        var catalog = new CatalogSnapshot { SnapshotId = "test", FileHash = "test" };
        foreach (var (code, description) in new[] { ("08.10.0010", "כבל חשמל"), ("41.01.0010", "צינור פוליאתילן"), ("57.02.0010", "קו מים"), ("51.05.0010", "צינור ניקוז") })
            catalog.Items.Add(code, new CatalogItem { Code = code, Description = description, UnitRaw = "מטר" });
        var search = new InfrastructureCatalogSearch(catalog);
        InfrastructureGroup Group(string name) => InfrastructureBoq.Group(new[] { new InfrastructureLine("model.dwg", "A", name, "Line", 10, false,
            InfrastructureBoq.Classify(name, "").Topic, InfrastructureBoq.Classify(name, "").Evidence, "OUTPUT") }).Single();
        var electric = Group("HASHMAL1");
        Assert.Equal("08.10.0010", Assert.Single(search.Find(electric, "chashmal")).Code);
        var irrigation = Group("HASHKAY");
        Assert.Equal("41.01.0010", Assert.Single(search.Find(irrigation)).Code);
        Assert.False(irrigation.Approved); Assert.Null(irrigation.Price);
        Assert.Equal("51.05.0010", Assert.Single(search.Find(Group("C-STRM-PIPE"))).Code);
    }
}
