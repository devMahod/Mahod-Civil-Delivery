using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class InfrastructureBoqTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcd-infra-test-" + Guid.NewGuid().ToString("N"));
    public InfrastructureBoqTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);
    private CatalogSnapshot Catalog()
    {
        var book = new MiniXlsx.Workbook();
        book.Rows.Add(new MiniXlsx.OutRow(1).Text("A", "סעיף", 0).Text("B", "תיאור", 0).Text("C", "יחידה", 0).Text("D", "מחיר", 0));
        book.Rows.Add(new MiniXlsx.OutRow(2).Text("A", "57.01.0001", 0).Text("B", "TEST ONLY צינור", 0).Text("C", "מטר", 0).Number("D", 12m, 0));
        book.Rows.Add(new MiniXlsx.OutRow(3).Text("A", "57.01.0002", 0).Text("B", "TEST ONLY שוחה", 0).Text("C", "יח׳", 0).Number("D", 99m, 0));
        var path = Path.Combine(_dir, "test.xlsx"); MiniXlsx.Write(book, path); return PriceBookXlsxLoader.Load(path, "TEST ONLY");
    }
    private static InfrastructureLine Line(string source = "water.dwg", string layer = "WT-DN200", double length = 10) =>
        new(source, "A1", layer, "Polyline", length, false, "מים", "TEST", "BOQ_WATER");
    private static InfrastructureGroup Group() => InfrastructureBoq.Group(new[] { Line() }).Single();

    [Fact]
    public void Bundled_book_is_the_verified_official_download_including_the_corrected_rate()
    {
        var offer = Assert.Single(BundledPriceBooks.Offers(_dir, new MahodAI.CivilDelivery.Shared.ProjectProfile()));
        var catalog = PriceBookXlsxLoader.Load(offer.Entry.Path, "official-20261030");
        Assert.Equal(BundledPriceBooks.Sha256, catalog.FileHash.ToUpperInvariant());
        Assert.Equal(9783, catalog.Items.Count);
        Assert.Equal(41m, catalog.Prices["02.01.0769"].Price);
    }

    [Theory]
    [InlineData("-M4600 MAIM", "water.dwg", "מים")]
    [InlineData("-M6091 HASHMAL", "water.dwg", "חשמל")]
    [InlineData("pipe", "6422-UT-SW-DE-West-MHD.dwg", "ביוב")]
    [InlineData("GAS-WT", "water.dwg", "לא מסווג")]
    [InlineData("DRAWING", "unknown.dwg", "לא מסווג")]
    public void Topics_are_proposals_with_token_boundaries_and_conflicts_blocked(string layer, string source, string expected) =>
        Assert.Equal(expected, InfrastructureBoq.Classify(layer, source).Topic);

    [Fact]
    public void Grouping_preserves_sources_and_sums_occurrences()
    {
        var groups = InfrastructureBoq.Group(new[] { Line(length: 2), Line(length: 3), Line("other.dwg", length: 7) });
        Assert.Equal(2, groups.Count); Assert.Equal(5, groups.Single(g => g.Source == "water.dwg").LengthMetres);
        Assert.All(groups, g => { Assert.False(g.Approved); Assert.Null(g.Total); Assert.Equal("טרם נבדק", g.WorkStatus); });
    }
    [Fact]
    public void Mapping_needs_only_a_catalogue_item_and_compatible_metres_not_a_name_or_form()
    {
        var catalog = Catalog(); var g = Group();
        Assert.Throws<InvalidOperationException>(() => g.Assign(catalog, "57.01.0002"));
        Assert.Throws<InvalidOperationException>(() => g.Assign(catalog, "missing"));
        g.Assign(catalog, "57.01.0001"); Assert.Equal(120m, g.Total);
        Assert.Null(g.ApprovedBy); Assert.True(g.Approved);
        g.Specification = "TEST ONLY DN300"; Assert.False(g.Approved); Assert.Null(g.Price);
    }

    [Fact]
    public void Saved_choices_restore_only_for_the_same_drawing_catalogue_and_measurements()
    {
        var c = Catalog(); var g = Group(); g.Assign(c, "57.01.0001");
        var excluded = Group(); var groups = new[] { g, InfrastructureBoq.Group(new[] { Line("other.dwg") }).Single() };
        var path = Path.Combine(_dir, "review.json");
        InfrastructureReviewStore.Save(path, "DRAWING", c, groups, new System.Collections.Generic.HashSet<InfrastructureGroup> { groups[1] });
        var fresh = new[] { Group(), InfrastructureBoq.Group(new[] { Line("other.dwg") }).Single() };
        var restoredExcluded = new System.Collections.Generic.HashSet<InfrastructureGroup>();
        Assert.Equal(1, InfrastructureReviewStore.Restore(path, "DRAWING", c, fresh, restoredExcluded));
        Assert.Equal(120m, fresh[0].Total); Assert.Contains(fresh[1], restoredExcluded);
        Assert.Equal(0, InfrastructureReviewStore.Restore(path, "CHANGED", c, new[] { Group() }, restoredExcluded));
        Assert.Equal(0, InfrastructureReviewStore.Restore(path, "DRAWING", new CatalogSnapshot { SnapshotId = "new", FileHash = "different", Items = c.Items, Prices = c.Prices }, new[] { Group() }, restoredExcluded));
        var changed = InfrastructureBoq.Group(new[] { Line(length: 11) }).Single();
        Assert.Equal(0, InfrastructureReviewStore.Restore(path, "DRAWING", c, new[] { changed }, restoredExcluded));
        Assert.False(changed.Approved);
    }

    [Fact]
    public void Real_drawing_layers_get_ranked_official_items_and_hebrew_search_without_prior_approval()
    {
        var offer = Assert.Single(BundledPriceBooks.Offers(_dir, new MahodAI.CivilDelivery.Shared.ProjectProfile()));
        var c = PriceBookXlsxLoader.Load(offer.Entry.Path, "official"); var search = new InfrastructureCatalogSearch(c);
        var sewer = InfrastructureBoq.Group(new[] { Line(layer: "6422-UT-SW-DE-West-MHD|SW-DE-315") with { Topic = "ביוב" } }).Single();
        var suggestions = search.Find(sewer);
        Assert.NotEmpty(suggestions);
        Assert.All(suggestions.Take(5), x => { Assert.StartsWith("57.03", x.Code); Assert.Contains("315", x.Description); });
        Assert.False(sewer.Approved); Assert.Null(sewer.Price);
        var query = search.Find(sewer, "צנרת ביוב 315");
        Assert.NotEmpty(query); Assert.All(query, x => Assert.True(c.Items[x.Code].Unit.SameUnit(Units.Parse("מטר"))));
        var drain = InfrastructureBoq.Group(new[] { Line(layer: "6422-DR-WEST|DR-PIPE-40") with { Topic = "ניקוז" } }).Single();
        Assert.Contains(search.Find(drain).Take(8), x => x.Description.Contains("40 ס"));
        Assert.Contains("לא צוינה", InfrastructureCatalogSearch.LayerNote(drain));
        Assert.Null(InfrastructureCatalogSearch.LayerDiameter("DR-BL-TX"));
        Assert.Contains("מצב קיים", InfrastructureCatalogSearch.LayerNote(sewer = InfrastructureBoq.Group(new[] { Line(layer: "SW-EX") }).Single()));
        Assert.Contains("סימון עזר", InfrastructureCatalogSearch.LayerNote(InfrastructureBoq.Group(new[] { Line(layer: "DR-BL-TX") }).Single()));
        var exact = search.Find(drain, "51.05.0715"); Assert.Equal("51.05.0715", exact.First().Code);
    }

    [Theory]
    [InlineData("WT-DN200", 200, "מ״מ")]
    [InlineData("W-DE-8", 8, null)]
    [InlineData("SW-DE-315", 315, null)]
    [InlineData("DR-PIPE-40", 40, null)]
    public void Layer_diameter_never_invents_an_unspecified_unit(string layer, double value, string? unit)
    {
        var d = InfrastructureCatalogSearch.LayerDiameter(layer); Assert.NotNull(d);
        Assert.Equal(value, d.Value.Value); Assert.Equal(unit, d.Value.Unit);
    }
    [Fact]
    public void Existing_state_or_topic_change_withdraws_price()
    {
        var catalog = Catalog(); var g = Group(); g.Specification = "TEST"; g.WorkStatus = "תכנון לביצוע";
        g.Approve(catalog, "57.01.0001", "TEST"); g.WorkStatus = "מצב קיים"; Assert.False(g.Approved);
        g.WorkStatus = "תכנון לביצוע"; g.Approve(catalog, "57.01.0001", "TEST"); g.Topic = "גז"; Assert.False(g.Approved);
    }
    [Fact]
    public void Partial_export_keeps_unapproved_price_blank_and_caches_approved_total()
    {
        var catalog = Catalog(); var g = Group(); g.Specification = "TEST"; g.WorkStatus = "תכנון לביצוע"; g.Approve(catalog, "57.01.0001", "TEST");
        var path = Path.Combine(_dir, "output.xlsx");
        InfrastructureBoq.Export(path, new[] { g, Group() }, new[] { Line() }, catalog, Array.Empty<string>(), "TEST", "TESTHASH", "TEST");
        using var zip = ZipFile.OpenRead(path); using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var xml = XDocument.Load(stream); XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var cells = xml.Descendants(ns + "c").ToDictionary(c => (string)c.Attribute("r")!);
        Assert.Equal("F6*G6", cells["H6"].Element(ns + "f")!.Value);
        Assert.Equal("120", cells["H6"].Element(ns + "v")!.Value);
        Assert.False(cells.ContainsKey("G7")); Assert.False(cells.ContainsKey("H7"));
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet3.xml"));
    }
}
