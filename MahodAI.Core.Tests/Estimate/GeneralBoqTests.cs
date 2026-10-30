using System.IO;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class GeneralBoqTests
{
    private static InfrastructureLine Line(string part = "", double area = 0, double count = 0) =>
        new("TEST ONLY.dwg", Guid.NewGuid().ToString(), "K-RIZUF", "Polyline", count > 0 ? 0 : 14, true, "ריצופים", "TEST ONLY", "TEST")
        { AreaSquareMetres = area, CountUnits = count, GroupPart = part };
    private static CatalogSnapshot Book() => new()
    {
        SnapshotId = "TEST ONLY", FileHash = "TEST HASH",
        Items = new() {
            ["40.01.0001"] = new() { Code="40.01.0001",Description="ריצוף משתלב",UnitRaw="מ״ר",ChapterPath="ריצופים ועבודות פיתוח > ריצוף" },
            ["24.01.0001"] = new() { Code="24.01.0001",Description="פירוק ריצוף",UnitRaw="מ״ר",ChapterPath="פירוקים והריסות > פירוקים" },
            ["40.01.0002"] = new() { Code="40.01.0002",Description="אבן שפה",UnitRaw="מטר",ChapterPath="ריצופים ועבודות פיתוח > ריצוף" },
            ["40.01.0003"] = new() { Code="40.01.0003",Description="עץ לשתילה",UnitRaw="יח׳",ChapterPath="גינון > עצים" },
            ["40.01.0004"] = new() { Code="40.01.0004",Description="אספלט לפי משקל",UnitRaw="טון",ChapterPath="כבישים" } },
        Prices = new() { ["40.01.0001"] = new() { Code="40.01.0001",Price=100,PriceBookId="TEST ONLY" }, ["40.01.0003"] = new() { Code="40.01.0003",Price=99,PriceBookId="TEST ONLY" }, ["40.01.0004"] = new() { Code="40.01.0004",Price=200,PriceBookId="TEST ONLY" } }
    };
    [Fact]
    public void Paving_uses_measured_area_and_never_perimeter_for_a_square_metre_price()
    {
        var g = Assert.Single(InfrastructureBoq.Group(new[] { Line(area:12) }));
        Assert.Equal("m2",Units.Parse(g.MeasurementUnit).Canonical); Assert.Equal(12,g.Quantity);
        Assert.Throws<InvalidOperationException>(()=>g.Assign(Book(),"40.01.0002"));
        g.Assign(Book(),"40.01.0001");Assert.Equal(1200m,g.Total);
        g.MeasurementUnit="מטר";Assert.False(g.Approved);Assert.Equal(14,g.Quantity);
    }
    [Fact]
    public void Identical_blocks_group_by_name_and_layer_without_counting_their_outline()
    {
        var groups=InfrastructureBoq.Group(new[]{Line("בלוק TREE",count:1),Line("בלוק TREE",count:1),Line("בלוק OTHER",count:1)});
        Assert.Equal(2,groups.Count);var tree=groups.Single(g=>g.GroupPart=="בלוק TREE");
        Assert.Equal(0,tree.LengthMetres);Assert.Equal(2,tree.Quantity);Assert.Equal("unit",Units.Parse(tree.MeasurementUnit).Canonical);
        tree.Assign(Book(),"40.01.0003");Assert.Equal(198m,tree.Total);
    }
    [Fact]
    public void A_weight_item_needs_an_explicit_manual_quantity_and_cannot_use_area_as_mass()
    {
        var g=Assert.Single(InfrastructureBoq.Group(new[]{Line(area:20)}));g.MeasurementUnit="טון";
        Assert.Equal(0,g.Quantity);Assert.Throws<InvalidOperationException>(()=>g.Assign(Book(),"40.01.0004"));
        g.ManualQuantity=3;g.Assign(Book(),"40.01.0004");Assert.Equal(600m,g.Total);Assert.Contains("ידנית",g.QuantityOrigin);
        g.MeasurementUnit="מ״ר";Assert.Null(g.ManualQuantity);Assert.False(g.Approved);Assert.Equal(20,g.Quantity);
    }
    [Fact]
    public void Chapter_selection_uses_the_publishers_own_folders_and_matches_only_the_selected_unit()
    {
        var c=Book();var book=new InfrastructurePriceBook("TEST publisher","TEST edition",c,new Dictionary<string,string>());
        var category=BoqCatalogCategories.For(book).Single(x=>x.Code=="24");Assert.Contains("פירוקים",category.Label);
        var g=Assert.Single(InfrastructureBoq.Group(new[]{Line(area:12)}));g.Topic=InfrastructureBoq.Unknown;
        var options=new InfrastructureCatalogSearch(book).Find(g,category:category);
        Assert.Equal("24.01.0001",Assert.Single(options).Code);Assert.False(g.Approved);
    }
    [Fact]
    public void A_measured_hatch_is_an_area_source_not_an_auxiliary_pipe_outline()
    {
        var g=Assert.Single(InfrastructureBoq.Group(new[]{Line("שטחים",area:12) with { Layer="RIZUF-HATCH" }}));
        var options=new InfrastructureCatalogSearch(Book()).Find(g);
        Assert.Contains(options,o=>o.Code=="40.01.0001");Assert.All(options,o=>Assert.Equal("m2",Units.Parse(o.Unit).Canonical));
    }
    [Theory]
    [InlineData("K-RIZUF","ריצופים")]
    [InlineData("40-hatch ritzuf","ריצופים")]
    [InlineData("HW-ASFL-DESG","כבישים ואספלט")]
    [InlineData("ASF","כבישים ואספלט")]
    [InlineData("HW-CURB","אבני שפה")]
    [InlineData("WATERPROOFING","איטום")]
    [InlineData("ST-CONC-4","בטון")]
    [InlineData("1EVEN_SAFA","אבני שפה")]
    [InlineData("1GADER-RESHET","מסגרות וגדרות")]
    [InlineData("PERUK-RIZUF","לא מסווג")]
    public void Broad_domain_aliases_keep_conflicting_meanings_open(string layer,string topic) =>
        Assert.Equal(topic,InfrastructureBoq.Classify(layer,"TEST ONLY.dwg").Topic);
    [Fact]
    public void A_typed_review_restores_area_but_not_changed_geometry()
    {
        var dir=Path.Combine(Path.GetTempPath(),"general-boq-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        try
        {
            var g=Assert.Single(InfrastructureBoq.Group(new[]{Line(area:12)}));g.Assign(Book(),"40.01.0001");var path=Path.Combine(dir,"review.json");
            InfrastructureReviewStore.Save(path,"DRAWING",Book(),new[]{g},new HashSet<InfrastructureGroup>());
            var fresh=Assert.Single(InfrastructureBoq.Group(new[]{Line(area:12)}));
            Assert.Equal(1,InfrastructureReviewStore.Restore(path,"DRAWING",Book(),new[]{fresh},new HashSet<InfrastructureGroup>()));Assert.Equal(1200m,fresh.Total);
            var changed=Assert.Single(InfrastructureBoq.Group(new[]{Line(area:13)}));
            Assert.Equal(0,InfrastructureReviewStore.Restore(path,"DRAWING",Book(),new[]{changed},new HashSet<InfrastructureGroup>()));
        }
        finally {Directory.Delete(dir,true);}
    }
    [Fact]
    public void Official_textual_chapter_headings_are_retained_without_turning_them_into_items()
    {
        var dir=Path.Combine(Path.GetTempPath(),"general-book-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        try
        {
            var w=new MiniXlsx.Workbook();
            w.Rows.Add(new MiniXlsx.OutRow(1).Text("A","סעיף",0).Text("B","תיאור",0).Text("C","יחידה",0).Text("D","מחיר",0));
            w.Rows.Add(new MiniXlsx.OutRow(2).Text("A","פרק 51 - סלילה",0));
            w.Rows.Add(new MiniXlsx.OutRow(3).Text("A","תת פרק 51.01 - עבודות הכנה ופירוק",0));
            w.Rows.Add(new MiniXlsx.OutRow(4).Text("A","51.01.0010",0).Text("B","פירוק ריצוף",0).Text("C","מ״ר",0).Number("D",20m,0));
            var path=Path.Combine(dir,"book.xlsx");MiniXlsx.Write(w,path);var c=PriceBookXlsxLoader.Load(path,"TEST ONLY");
            Assert.Single(c.Items);Assert.Equal("סלילה > עבודות הכנה ופירוק",c.Items["51.01.0010"].ChapterPath);
            Assert.Contains(BoqCatalogCategories.For(new("TEST","TEST",c,new Dictionary<string,string>())),f=>f.Code=="51.01"&&f.Title.Contains("פירוק"));
        }
        finally{Directory.Delete(dir,true);}
    }
}
