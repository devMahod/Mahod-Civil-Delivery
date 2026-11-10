using System;
using System.IO;
using System.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// Live 2026-11-10, 130X road plan: every barrier layer was auto-assigned 19.05.2005 (pedestrian-bridge railing, ₪1,183/m),
/// ₪41.7M of a ₪111.8M bill, and lighting poles a cable guard channel. Barriers are NTI 51.33 / 51.36 by the layer's own
/// containment level and working width; reference lines beside a barrier are not barrier.
/// </summary>
public sealed class FirmItemHintsTests
{
    private static InfrastructureGroup G(string layer, string unit = "מטר", string topic = "מסגרות וגדרות") => new()
    {
        Source = @"C:\x\6422-GM-466-ALBX-MHD.dwg", Layer = "6422-GM-466-ALBX-MHD|" + layer, Topic = topic, Count = 3,
        LengthMetres = unit == "מטר" ? 500 : 0, CountUnits = unit == "יח׳" ? 12 : 0, MeasurementUnit = unit, Evidence = "",
        Role = FirmLayerConventions.Match(layer)?.Role ?? LayerRole.Quantity,
    };

    [Theory]
    [InlineData("TR-BARI-FLEX-H-W3", "51.33.1525")]      // H1, W3 (₪204)
    [InlineData("TR-BARI-FLEX-H-01", "51.33.1513")]      // H1, width not named: first H1 item, the engineer checks W
    [InlineData("HW-BARI-H2-W4", "51.33.1551")]          // H2 items carry no working width → H2 alone
    [InlineData("TR-BARI-RIGD-H-01", "51.36.0200")]      // concrete barrier cast in place
    [InlineData("TR-BARI-FLEX-N1W2", null)]               // N1 is not in the book: no automatic item
    [InlineData("TR-BARI-FLEX-N2W4_DS", null)]            // double-sided N2 W4 has no item: never priced as single-sided
    public void A_barrier_gets_its_own_nti_item_or_none(string layer, string? code)
    {
        var recommended = Search().Find(G(layer)).FirstOrDefault(o => o.IsRecommended);
        Assert.Equal(code, recommended?.Code);
        Assert.DoesNotContain(Search().Find(G(layer)), o => o.IsRecommended && o.Code.StartsWith("19.05", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("TR-BARI-EOP")]
    [InlineData("TR-BARI-EOP-W3")]
    [InlineData("TR-BARI-EOP-1")]
    [InlineData("TR-BARI-WW-1.30")]
    [InlineData("TR-BARI-W3-1.00")]
    [InlineData("TR-BARI-RIGD-H-01-1")]
    public void Reference_lines_beside_a_barrier_are_listed_not_priced(string layer)
    {
        var meaning = FirmLayerConventions.Match(layer);
        Assert.NotNull(meaning);
        Assert.Equal(LayerRole.Boundary, meaning!.Role);
        Assert.False(G(layer).IsPayItem);
    }

    [Theory]
    [InlineData("TR-BARI-FLEX-H-01")]
    [InlineData("TR-BARI-RIGD-H-01")]
    [InlineData("TR-BARI-FLEX-N2W4_DS")]
    [InlineData("HW-BARI-H2-W4")]
    public void The_barrier_itself_stays_a_pay_item(string layer) => Assert.True(G(layer).IsPayItem);

    [Fact]
    public void Barrier_blocks_are_counted_but_not_guessed()
    {
        var row = G("TR-BARI-EQ-BL", "יח׳");
        Assert.DoesNotContain(Search().Find(row), o => o.IsRecommended);
    }

    [Fact]
    public void A_lighting_pole_block_is_a_lighting_pole()
    {
        var row = G("EL-LT-POLE-BL", "יח׳", "תאורה");
        var item = Search().Find(row).First(o => o.IsRecommended);
        Assert.StartsWith("08.10.", item.Code, StringComparison.Ordinal);
        Assert.StartsWith("עמוד תאורה", item.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pedestrian_guide_fence_is_a_guide_fence() =>
        Assert.Equal("51.33.5530", Search().Find(G("HW-FENC-PDST")).First(o => o.IsRecommended).Code);

    [Theory]
    [InlineData("MAAKE 2")]
    [InlineData("GADER")]
    [InlineData("LA-FENC")]
    public void A_railing_or_fence_named_without_its_kind_is_not_guessed(string layer) =>
        Assert.DoesNotContain(Search().Find(G(layer)), o => o.IsRecommended);

    [Fact]
    public void A_lighting_pole_block_under_the_electric_topic_is_still_a_pole()
    {
        var row = G("BL-EL-LT-POLE-AMOD-1", "יח׳", "חשמל");
        Assert.StartsWith("עמוד תאורה", Search().Find(row).First(o => o.IsRecommended).Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Layers_without_a_firm_rule_keep_the_ordinary_search() =>
        Assert.Null(FirmItemHints.For("6422-DR-MODEL-NATAZ|DR-PIPE-80", "מטר"));

    private static CatalogSnapshot? _catalog;
    private static CatalogSnapshot Catalog()
    {
        if (_catalog != null) return _catalog;
        var dir = Path.Combine(Path.GetTempPath(), "mcd-barrier-book-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var offer = Assert.Single(BundledPriceBooks.Offers(dir, new MahodAI.CivilDelivery.Shared.ProjectProfile()));
        return _catalog = PriceBookXlsxLoader.Load(offer.Entry.Path, "TEST NTI");
    }
    private static InfrastructureCatalogSearch Search() => new(Catalog());
}
