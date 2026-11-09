using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>The pavement report is optional: without it (or where it says nothing) a road type's default structures price
/// the area — and every row, note and zone says it is a default.</summary>
public sealed class PavementDefaultsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcd-pave-defaults-" + Guid.NewGuid().ToString("N"));
    public PavementDefaultsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private static PavementDefaultClass Collector => PavementDefaults.Resolve(PavementDefaults.DefaultId)!;
    private static readonly RoadSurfaceKind[] Paved =
        { RoadSurfaceKind.Road, RoadSurfaceKind.PublicTransport, RoadSurfaceKind.Shoulder, RoadSurfaceKind.Sidewalk, RoadSurfaceKind.BikePath, RoadSurfaceKind.Milling };

    private static PavementReportPlan.ZoneArea Area(RoadSurfaceKind s, double m2, PavementReportPlan.Zone? zone = null) =>
        new(zone ?? PavementReportPlan.Zone.NoReport, s, m2);

    [Fact]
    public void Every_road_type_has_a_structure_for_every_paved_surface_with_sane_layers()
    {
        Assert.True(PavementDefaults.Classes.Count >= 8);
        Assert.Equal(PavementDefaults.Classes.Count, PavementDefaults.Classes.Select(c => c.Id).Distinct().Count());
        Assert.Equal(PavementDefaults.Classes.Count, PavementDefaults.Classes.Select(c => c.Name).Distinct().Count());
        foreach (var c in PavementDefaults.Classes)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Source));
            foreach (var s in Paved)
            {
                var structure = c.StructureFor(s);
                Assert.True(structure != null, $"{c.Id}: no structure for {s}");
                Assert.All(structure!.Layers, l => Assert.InRange(l.ThicknessCm, 1, 80));
                Assert.Equal(structure.Layers.Sum(l => l.ThicknessCm), structure.TotalCm, 6);
            }
            Assert.Null(c.StructureFor(RoadSurfaceKind.Island));      // an island keeps its own area row
        }
    }

    [Fact]
    public void The_quoted_structures_are_the_reports_own_layers()
    {
        // ראש העין 4312-1 §7.3.1 (מאסף, W18 3M): 4 + 5 + 6 asphalt on 40 cm מצע א'.
        Assert.Equal(new[] { 4d, 5, 6, 40 }, Collector.StructureFor(RoadSurfaceKind.Road)!.Layers.Select(l => l.ThicknessCm));
        // 1560-25 §5.2.1 structure 1 = 31 cm full-depth asphalt; also the bus lane.
        var arterial = PavementDefaults.Resolve("urban-arterial")!;
        Assert.Equal(31, arterial.StructureFor(RoadSurfaceKind.Road)!.TotalCm, 6);
        Assert.Equal(31, Collector.StructureFor(RoadSurfaceKind.PublicTransport)!.TotalCm, 6);
        // 1560-25 §5.2.4 bike path 3 + 5 + 30; ראש העין §7.4 sidewalk 6 + 4 + 20.
        Assert.Equal(new[] { 3d, 5, 30 }, Collector.StructureFor(RoadSurfaceKind.BikePath)!.Layers.Select(l => l.ThicknessCm));
        Assert.Equal(new[] { 6d, 4, 20 }, Collector.StructureFor(RoadSurfaceKind.Sidewalk)!.Layers.Select(l => l.ThicknessCm));
        // A paved shoulder takes the carriageway's structure (ראש העין §7.5).
        Assert.Same(Collector.StructureFor(RoadSurfaceKind.Road), Collector.StructureFor(RoadSurfaceKind.Shoulder));
    }

    [Fact]
    public void Every_default_layer_finds_a_priced_item_in_the_NTI_book()
    {
        var offer = Assert.Single(BundledPriceBooks.Offers(_dir, new MahodAI.CivilDelivery.Shared.ProjectProfile()));
        var catalog = PriceBookXlsxLoader.Load(offer.Entry.Path, "TEST NTI");
        var search = new InfrastructureCatalogSearch(catalog);
        var missing = new List<string>();
        foreach (var c in PavementDefaults.Classes)
        foreach (var s in Paved)
        foreach (var layer in PavementReportPlan.Layers(c.StructureFor(s)!, PavementDefaults.Coats))
        {
            var row = RoadPavement.DerivedGroups(new[] { new RoadPavement.LayerQuantity(s, layer, 100, c.StructureName) { Origin = PavementReportPlan.Origin.Default } }).Single();
            var item = search.Find(row).FirstOrDefault(o => o.IsRecommended);
            // Sand bedding (51.03.0075) has no price in the NTI book: it is found, just not priced.
            if (item == null || (item.Price is not > 0 && layer.Name != "שכבת חול")) missing.Add($"{c.Id}/{s}: {layer.Display} → {item?.Code ?? "none"}");
        }
        Assert.Empty(missing);
    }

    [Theory]
    [InlineData(4, "51.04.1530")]
    [InlineData(5, "51.04.1539")]
    [InlineData(15, "51.04.1554")]
    [InlineData(17, "51.04.1560")]
    public void Milling_is_priced_by_its_depth_band(double depth, string code)
    {
        var offer = Assert.Single(BundledPriceBooks.Offers(_dir, new MahodAI.CivilDelivery.Shared.ProjectProfile()));
        var search = new InfrastructureCatalogSearch(PriceBookXlsxLoader.Load(offer.Entry.Path, "TEST NTI"));
        var layer = new PavementLayer("קרצוף אספלט", depth, "מ״ר", PavementReportPlan.MillingHint(depth));
        var row = RoadPavement.DerivedGroups(new[] { new RoadPavement.LayerQuantity(RoadSurfaceKind.Milling, layer, 100, "x") }).Single();
        Assert.Equal(code, search.Find(row).First(o => o.IsRecommended).Code);
    }

    [Fact]
    public void Without_a_report_the_defaults_price_everything_and_say_so()
    {
        var plan = PavementReportPlan.Quantities(PavementReportSet.Empty,
            new[] { Area(RoadSurfaceKind.Road, 1000), Area(RoadSurfaceKind.Sidewalk, 300), Area(RoadSurfaceKind.Island, 50) }, defaults: Collector);
        Assert.Equal(1300, plan.DefaultArea, 6);
        Assert.Equal(0, plan.UnpricedArea, 6);
        Assert.Same(Collector, plan.Defaults);
        Assert.All(plan.Layers, l => Assert.True(l.IsDefault));
        Assert.All(plan.Zones, z => Assert.Equal(PavementReportPlan.Origin.Default, z.Origin));
        Assert.Contains(plan.Zones, z => z.Zone == "כל השטח" && z.AreaSquareMetres == 1000);
        Assert.Contains(plan.Notes, n => n.StartsWith(PavementReportPlan.DefaultsNote) && n.Contains("רחוב מאסף") && n.Contains("לא צורף דו״ח"));
        var base40 = plan.Layers.Single(l => l.Surface == RoadSurfaceKind.Road && l.Layer.ByVolume);
        Assert.Equal(1000 * 0.40, base40.Quantity, 6);
        var rows = RoadPavement.DerivedGroups(plan.Layers);
        Assert.All(rows, r => Assert.Contains("ברירת מחדל", r.Layer));
        Assert.All(rows, r => Assert.Contains("ברירת מחדל · רחוב מאסף — ערך טיפוסי, לא מדו״ח מבנה", r.Evidence));
        Assert.Equal(new HashSet<RoadSurfaceKind> { RoadSurfaceKind.Road, RoadSurfaceKind.Sidewalk }, plan.Priced);
    }

    [Fact]
    public void With_defaults_switched_off_nothing_is_priced_as_before()
    {
        var plan = PavementReportPlan.Quantities(PavementReportSet.Empty, new[] { Area(RoadSurfaceKind.Road, 1000) });
        Assert.Empty(plan.Layers); Assert.Empty(plan.Priced);
        Assert.Equal(1000, plan.UnpricedArea, 6);
        Assert.Contains(plan.Notes, n => n.Contains("לא נבחרו ערכי ברירת מחדל"));
    }

    [Fact]
    public void A_hand_typed_structure_beats_the_defaults()
    {
        var manual = new[] { new PavementStructure(RoadSurfaceKind.Sidewalk, new[] { new PavementLayer("ריצוף בטון", 8, "מ״ר", "אבן משתלבת 8") }) };
        var plan = PavementReportPlan.Quantities(PavementReportSet.Empty, new[] { Area(RoadSurfaceKind.Road, 1000), Area(RoadSurfaceKind.Sidewalk, 300) }, manual, Collector);
        var sidewalk = Assert.Single(plan.Layers, l => l.Surface == RoadSurfaceKind.Sidewalk);
        Assert.Equal(PavementReportPlan.Origin.Manual, sidewalk.Origin);
        Assert.Equal("מבנה ידני", sidewalk.StructureName);
        Assert.Equal(1000, plan.DefaultArea, 6);
        // The hand-typed row keeps its pre-defaults BOQ label, so saved price choices still find it.
        Assert.Equal("מדרכה · ריצוף בטון 8 ס״מ", RoadPavement.DerivedGroups(new[] { sidewalk }).Single().Layer);
    }

    [Fact]
    public void With_a_report_only_the_area_it_does_not_cover_takes_defaults()
    {
        var layer = new PavementReportLayer(5, "תא\"צ 19 גיר PG70-10", "asphalt", 19, "PG70-10", "גיר");
        var road = new PavementReportStructure("S1", "1", "מבנה 1", null, "road", "full", null, new[] { layer }, 5, 5, true, 12);
        var segment = new PavementReportSegment("1", "1000", "1010", "רחוב", "1", null);
        var content = new PavementReportContent(new PavementReportDocument(null, null, null, null, null, null), new[] { road }, new[] { segment },
            new PavementReportCoats(true, true, null), Array.Empty<string>(), Array.Empty<string>());
        var set = new PavementReportSet(new[] { new AttachedPavementReport("r.pdf", "AB", DateTime.UtcNow, "m", "p", 1, content, Array.Empty<string>()) }, new Dictionary<string, string>());
        var plan = PavementReportPlan.Quantities(set, new[]
        {
            Area(RoadSurfaceKind.Road, 800, new PavementReportPlan.Zone(0, segment)),
            Area(RoadSurfaceKind.Road, 200, PavementReportPlan.Zone.Outside),
            Area(RoadSurfaceKind.Sidewalk, 100, new PavementReportPlan.Zone(0, segment)),   // the report has no sidewalk
        }, defaults: Collector);
        Assert.Equal(800, plan.Layers.Where(l => l.Origin == PavementReportPlan.Origin.Report).Select(l => l.AreaSquareMetres).Distinct().Single(), 6);
        Assert.Equal(300, plan.DefaultArea, 6);
        Assert.Equal(0, plan.UnpricedArea, 6);
        Assert.Contains(plan.Notes, n => n.StartsWith(PavementReportPlan.DefaultsNote) && n.Contains("שאין להם מבנה בדו״ח"));
        Assert.Contains(plan.Zones, z => z.Zone == "מחוץ למקטעי הדו״ח" && z.IsDefault && z.AreaSquareMetres == 200);
        // Report rows and default rows of one surface are separate BOQ lines.
        var rows = RoadPavement.DerivedGroups(plan.Layers);
        Assert.Equal(rows.Count, rows.Select(r => r.Layer).Distinct().Count());
    }

    [Fact]
    public void The_choice_is_saved_per_scope_and_resolves_safely()
    {
        Assert.Equal(PavementDefaults.DefaultId, PavementDefaults.Resolve(null)!.Id);
        Assert.Null(PavementDefaults.Resolve(PavementDefaults.NoneId));
        Assert.Equal(PavementDefaults.DefaultId, PavementDefaults.Resolve("road-type-of-a-future-version")!.Id);
        var path = Path.Combine(_dir, "x.pavement-default.json");
        Assert.Null(PavementDefaults.LoadChoice(path));
        PavementDefaults.SaveChoice(path, "freeway");
        Assert.Equal("freeway", PavementDefaults.LoadChoice(path));
        File.WriteAllText(path, "{ not json");
        Assert.Null(PavementDefaults.LoadChoice(path));
    }
}
