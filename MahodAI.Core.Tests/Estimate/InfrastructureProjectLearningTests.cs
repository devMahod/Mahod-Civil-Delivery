using System.IO;
using System.IO.Compression;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class InfrastructureProjectLearningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcd-local-books-" + Guid.NewGuid().ToString("N"));
    public InfrastructureProjectLearningTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);
    private static InfrastructureGroup Group(string layer = "W-DE-DN110") => new()
    { Source = "TEST ONLY.dwg", Layer = layer, Topic = "מים", Count = 1, LengthMetres = 10, Evidence = "TEST ONLY" };
    private static CatalogSnapshot Catalog(string hash, decimal price) => new()
    {
        SnapshotId = "TEST ONLY " + hash, FileHash = hash,
        Items = new() { ["01.01.0010"] = new() { Code = "01.01.0010", Description = "TEST ONLY צינור מים PVC בקוטר 110 מ״מ", UnitRaw = "מטר" } },
        Prices = new() { ["01.01.0010"] = new() { Code = "01.01.0010", Price = price, PriceBookId = hash, SourceHash = hash } }
    };

    [Theory]
    [InlineData("SAWAGE-else", "ביוב")]
    [InlineData("w-fire", "מים")]
    [InlineData("w-drink", "מים")]
    [InlineData("MIKOROT", "מים")]
    [InlineData("0-HASHMALI", "חשמל")]
    [InlineData("M4015 PARTENER", "תקשורת")]
    [InlineData("HOT_new", "תקשורת")]
    [InlineData("HOT_EX", "תקשורת")]
    [InlineData("TKSORET", "תקשורת")]
    [InlineData("EL-TIKSHHORET-RAZIF", "תקשורת")]
    [InlineData("EL-TAALA-TIKSHORET", "תקשורת")]
    [InlineData("drenag", "ניקוז")]
    public void Additional_engineering_spellings_remain_hints(string layer, string expected) =>
        Assert.Equal(expected, InfrastructureLayerNames.Classify(layer, "model.dwg").Topic);

    [Fact]
    public void Numeric_names_need_their_own_description_and_cannot_become_global_numeric_rules()
    {
        Assert.Equal("מים", InfrastructureLayerNames.Classify("4601", "model.dwg", "קוי מים").Topic);
        Assert.Equal("ביוב", InfrastructureLayerNames.Classify("4801", "model.dwg", "שכבת קוי ביוב-גרביטציה").Topic);
        Assert.Equal("חשמל", InfrastructureLayerNames.Classify("unclassified", "model.dwg", "כבל תת קרקעי מתח נמוך").Topic);
        Assert.Equal(InfrastructureBoq.Unknown, InfrastructureLayerNames.Classify("4601", "model.dwg").Topic);
        Assert.Equal("ביוב", InfrastructureLayerNames.Classify("BIUV", "water.dwg", "מים").Topic);
        Assert.Equal(InfrastructureBoq.Unknown, InfrastructureLayerNames.Classify("TIKSORET_HASHMAL", "model.dwg").Topic);
    }

    [Theory]
    [InlineData("HW-CS-TABL")]
    [InlineData("HW-PR-01-ELEV")]
    [InlineData("C-TOPO")]
    [InlineData("0")]
    [InlineData("ST-CONC-4")]
    [InlineData("RR-ALGN")]
    public void A_utility_filename_does_not_turn_unrelated_geometry_into_pipes(string layer) =>
        Assert.Equal(InfrastructureBoq.Unknown, InfrastructureLayerNames.Classify(layer, "WATER-MODEL.dwg").Topic);

    [Fact]
    public void Text_and_manhole_outlines_require_an_explicit_search_instead_of_automatic_metre_items()
    {
        var s = new InfrastructureCatalogSearch(Catalog("a", 1));
        var g = Group("WATER-TXT");
        Assert.Empty(s.Find(g)); Assert.NotEmpty(s.Find(g, "צינור"));
        Assert.Empty(s.Find(Group("DR-MNHL")));
        Assert.Empty(s.Find(Group("DR-PR-01")));
        Assert.Empty(s.Find(Group("DR-PIPE-T")));
        Assert.False(g.Approved);
    }

    [Fact]
    public void Publisher_chapters_and_materials_drive_retrieval_without_cross_publisher_number_assumptions()
    {
        var c = Catalog("a", 1);
        c.Items["01.01.0020"] = new() { Code = "01.01.0020", Description = "TEST ONLY צינור H.D.P.E בקוטר 110 מ״מ", UnitRaw = "מטר" };
        c.Items["01.01.0030"] = new() { Code = "01.01.0030", Description = "TEST ONLY שוחה", UnitRaw = "יח'" };
        var chapters = c.Items.Keys.ToDictionary(k => k, _ => "מערכות מים וביוב > צנרת");
        var search = new InfrastructureCatalogSearch(new InfrastructurePriceBook("TEST publisher", "TEST edition", c, chapters));
        Assert.Contains("ביוב", InfrastructureLayerNames.TopicsInText("מערכות מים וביוב"));
        Assert.Contains("תאורה", InfrastructureLayerNames.TopicsInText("חשמל ותאורה"));
        Assert.Equal("01.01.0020", search.Find(Group("WATER-HDPE-DN110")).First().Code);
        Assert.Equal("01.01.0010", Assert.Single(search.Find(Group(), "צינור P.V.C 110")).Code);
        Assert.DoesNotContain(search.Find(Group()), x => x.Code == "01.01.0030");
        Assert.Equal(560, InfrastructureCatalogSearch.LayerDiameter("Kav Maim-560mm")!.Value.Value);
        Assert.Null(InfrastructureCatalogSearch.LayerDiameter("-maim-400")!.Value.Unit);
    }

    [Fact]
    public void Historical_usage_is_description_and_unit_context_never_a_copied_price_or_layer_decision()
    {
        var c = Catalog("a", 15);
        var usage = new InfrastructureCatalogUsage(new[] { new InfrastructureCatalogUsage.Example(c.Items.Values.Single().Description, "מטר", new[] { "TEST-A", "TEST-A", "TEST-B" }) });
        Assert.Equal(2, usage.Projects(c.Items.Values.Single()));
        var g = Group();
        var result = new InfrastructureCatalogSearch(c, usage, usesNtiChapters: false).Find(g).Single();
        Assert.Equal(15m, result.Price); Assert.Contains("2 פרויקטים", result.Reason);
        Assert.False(g.Approved); Assert.Null(g.Price);
        var wrongUnit = new InfrastructureCatalogUsage(new[] { new InfrastructureCatalogUsage.Example(c.Items.Values.Single().Description, "יח'", new[] { "TEST-C" }) });
        Assert.Equal(0, wrongUnit.Projects(c.Items.Values.Single()));
    }

    [Fact]
    public void A_specific_subchapter_wins_over_a_combined_parent_before_historical_usage_is_applied()
    {
        var c = Catalog("a", 1);
        c.Items["01.01.0010"] = new() { Code = "01.01.0010", Description = "TEST ONLY צינור PVC בקוטר 110 מ״מ", UnitRaw = "מטר" };
        c.Items["01.02.0010"] = new() { Code = "01.02.0010", Description = "TEST ONLY צינור פוליאתילן בקוטר 110 מ״מ", UnitRaw = "מטר" };
        var chapters = new Dictionary<string,string>
        {
            ["01.01.0010"] = "מערכות ביוב ואספקת מים > אספקת מים",
            ["01.02.0010"] = "מערכות ביוב ואספקת מים > מערכות ביוב"
        };
        var usage = new InfrastructureCatalogUsage(new[] { new InfrastructureCatalogUsage.Example(c.Items["01.02.0010"].Description, "מטר", new[] { "TEST-A", "TEST-B" }) });
        var s = new InfrastructureCatalogSearch(new InfrastructurePriceBook("TEST publisher", "TEST edition", c, chapters), usage);
        Assert.Equal("01.01.0010", Assert.Single(s.Find(Group())).Code);
        Assert.Equal("01.02.0010", Assert.Single(s.Find(Group(), "צינור ביוב")).Code);
        var sewer = Group(); sewer.Topic = "ביוב";
        Assert.Equal("01.02.0010", Assert.Single(s.Find(sewer)).Code);
    }

    [Fact]
    public void A_cost_deduction_does_not_outrank_installing_a_pipe_even_when_it_appears_in_project_history()
    {
        var c = Catalog("a", 1);
        c.Items["00.01.0010"] = new() { Code = "00.01.0010", Description = "TEST ONLY הפחתת עלויות חפירה של צינורות קוי מים", UnitRaw = "מטר" };
        var usage = new InfrastructureCatalogUsage(new[] { new InfrastructureCatalogUsage.Example(c.Items["00.01.0010"].Description, "מטר", new[] { "TEST-A", "TEST-B" }) });
        var s = new InfrastructureCatalogSearch(c, usage);
        Assert.Equal("01.01.0010", s.Find(Group()).First().Code);
        Assert.Contains("הפחתה", Assert.Single(s.Find(Group(), "הפחתת")).Reason);
    }

    [Fact]
    public void An_explicit_manual_query_topic_outranks_the_automatically_recognized_layer_topic()
    {
        var c = Catalog("a", 1);
        c.Items.Clear();
        c.Items["57.03.0010"] = new() { Code = "57.03.0010", Description = "TEST ONLY צינור PVC בקוטר 315 מ״מ", UnitRaw = "מטר" };
        c.Items["18.08.0010"] = new() { Code = "18.08.0010", Description = "TEST ONLY עטיפת חול לצינורות בקוטר 315 מ״מ", UnitRaw = "מטר" };
        var chapters = new Dictionary<string,string> { ["57.03.0010"] = "מערכות מים וביוב > מערכות ביוב", ["18.08.0010"] = "תעלות לצנרת מים וביוב" };
        var s = new InfrastructureCatalogSearch(new InfrastructurePriceBook("TEST NTI", "TEST edition", c, chapters, true));
        var result = s.Find(Group(), "צינור ביוב 315");
        Assert.Equal("57.03.0010", result.First().Code);
        Assert.Contains("ביוב", result.First().Reason); Assert.DoesNotContain(" · מים", result.First().Reason);
        Assert.Equal("מים", Group().Topic);
    }

    [Fact]
    public void Two_publishers_with_the_same_code_preserve_prices_on_switch_restore_and_export()
    {
        var a = Catalog("a", 10); var b = Catalog("b", 25);
        var g1 = Group(); var g2 = Group("SW-DN110"); g2.Topic = "ביוב";
        g1.Assign(a, "01.01.0010"); g2.Assign(b, "01.01.0010");
        var path = Path.Combine(_dir, "review.json");
        InfrastructureReviewStore.Save(path, "drawing", new[] { a, b }, new[] { g1, g2 }, new HashSet<InfrastructureGroup>());
        var fresh = new[] { Group(), Group("SW-DN110") };
        Assert.Equal(2, InfrastructureReviewStore.Restore(path, "drawing", new[] { a, b }, fresh, new HashSet<InfrastructureGroup>()));
        Assert.Equal("ביוב", fresh[1].Topic); Assert.Equal(100m, fresh[0].Total); Assert.Equal(250m, fresh[1].Total);
        var workbook = Path.Combine(_dir, "TEST.xlsx");
        InfrastructureBoq.Export(workbook, fresh, Array.Empty<InfrastructureLine>(), new[] { a, b }, Array.Empty<string>(), "TEST ONLY.dwg", "drawing", "TEST ONLY");
        using var zip = ZipFile.OpenRead(workbook);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var xml = reader.ReadToEnd(); Assert.Contains("TEST ONLY a", xml); Assert.Contains("TEST ONLY b", xml);
        Assert.Contains("F6*G6", xml); Assert.Contains("F7*G7", xml);
        Assert.Throws<InvalidOperationException>(() => InfrastructureBoq.Export(Path.Combine(_dir, "bad.xlsx"), fresh, Array.Empty<InfrastructureLine>(), a, Array.Empty<string>(), "TEST", "drawing", "TEST"));
    }

    [Fact]
    public void Invalid_local_books_are_reported_without_losing_other_publishers()
    {
        var row = new InfrastructurePriceBooks.Row("01.01.0010", "TEST ONLY", "מטר", null, "TEST chapter");
        var good = new InfrastructurePriceBooks.Document(1, "TEST publisher", "TEST edition", "TEST", "1", DateTime.UtcNow, new[] { row }, UsesNtiChapters: true);
        File.WriteAllText(Path.Combine(_dir, "good.pricebook.json"), JsonSerializer.Serialize(good));
        File.WriteAllText(Path.Combine(_dir, "duplicate.pricebook.json"), JsonSerializer.Serialize(good with { Items = new[] { row, row } }));
        var errors = new List<string>(); var books = InfrastructurePriceBooks.Load(_dir, errors);
        Assert.Single(books); Assert.Single(errors); Assert.Null(books[0].Catalog.Prices[row.Code].Price);
        Assert.Equal("TEST chapter", books[0].Chapters[row.Code]);
        Assert.True(books[0].UsesNtiChapters);
    }
}
