using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// Firm layer conventions, ASMADE / existing detection, demolition pricing and the roads calculation.
/// Layer names are real ones from the 100 project drawings surveyed 2026-11-05 (docs/BOQ_LAYERS_HE.md).
/// </summary>
public sealed class InfrastructureLayersAndExistingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcd-layers-test-" + Guid.NewGuid().ToString("N"));
    public InfrastructureLayersAndExistingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private CatalogSnapshot Nti()
    {
        var offer = Assert.Single(BundledPriceBooks.Offers(_dir, new MahodAI.CivilDelivery.Shared.ProjectProfile()));
        return PriceBookXlsxLoader.Load(offer.Entry.Path, "TEST NTI");
    }

    private static InfrastructureLine Line(string layer, string source = "MHD-HW-000-IC_00000-DD-030X.dwg", double length = 0, double area = 0, double count = 0, string part = "")
    {
        var c = InfrastructureBoq.Classify(layer, source);
        return InfrastructureBoq.Describe(new InfrastructureLine(source, "A1", layer, "Polyline", length, area > 0, c.Topic, c.Evidence, "BOQ_" + c.Code)
        { AreaSquareMetres = area, CountUnits = count, GroupPart = part });
    }
    /// <summary>A structure typed in by the engineer (the 1.9 placeholder lists, now only ever entered by hand).</summary>
    private static readonly PavementStructure[] Typed =
    {
        new(RoadSurfaceKind.Road, new[]
        {
            new PavementLayer("שכבה נושאת תא״צ 19", 5, "מ״ר", "תא\"צ 19 בעובי 5"), new PavementLayer("שכבה מקשרת תא״צ 25", 7, "מ״ר", "תא\"צ 25 בעובי 7"),
            new PavementLayer("ריסוס מאחה", 0, "מ״ר", "ריסוס מאחה"), new PavementLayer("ריסוס יסוד", 0, "מ״ר", "ריסוס יסוד"), new PavementLayer("מצע סוג א׳", 30, "מ״ק", "מצע סוג א"),
        }),
        new(RoadSurfaceKind.BikePath, new[] { new PavementLayer("תא״צ 12.5", 4, "מ״ר", "תא\"צ 12.5 בעובי 4"), new PavementLayer("מצע סוג א׳", 20, "מ״ק", "מצע סוג א") }),
        new(RoadSurfaceKind.Sidewalk, new[] { new PavementLayer("ריצוף אבנים משתלבות", 6, "מ״ר", "אבן משתלבת 6"), new PavementLayer("מצע סוג א׳", 20, "מ״ק", "מצע סוג א") }),
    };

    private static InfrastructureGroup Group(string layer, string source = "MHD-HW-000-IC_00000-DD-030X.dwg", double length = 0, double area = 0, double count = 0, string part = "")
        => InfrastructureBoq.Group(new[] { Line(layer, source, length, area, count, part) }).Single();

    [Theory]
    [InlineData("HW-ROAD-HA", "כבישים ואספלט", "Road")]
    [InlineData("HW-ROAD-HA-40", "כבישים ואספלט", "Road")]
    [InlineData("HW-ASPH-H", "כבישים ואספלט", "Road")]
    [InlineData("0-HATCH-CVISH", "כבישים ואספלט", "Road")]
    [InlineData("HW-HTCH-NTZ", "כבישים ואספלט", "PublicTransport")]
    [InlineData("HW-BIKE-HA", "כבישים ואספלט", "BikePath")]
    [InlineData("HW-SDWK-H", "ריצופים", "Sidewalk")]
    [InlineData("2rizuf", "ריצופים", "Sidewalk")]
    [InlineData("HW-HA-KIRZUF", "כבישים ואספלט", "Milling")]
    [InlineData("0-HATCH-SHULIAIM", "כבישים ואספלט", "Shoulder")]
    [InlineData("CS_Pave1", "כבישים ואספלט", "StructureArea")]
    public void Pavement_hatch_layers_are_road_surfaces(string layer, string topic, string surface)
    {
        var meaning = FirmLayerConventions.Match(layer);
        Assert.NotNull(meaning);
        Assert.Equal(topic, meaning!.Topic);
        Assert.Equal(surface, meaning.Surface.ToString());
        Assert.Equal("מ״ר", meaning.Unit);
        Assert.Equal(topic, InfrastructureBoq.Classify(layer, "x.dwg").Topic);
    }

    [Theory]
    [InlineData("HW-CURB-GRDN", "אבני שפה", "מטר")]
    [InlineData("TR-CURB3_MIDRAHA-17", "אבני שפה", "מטר")]
    [InlineData("HW-BARI-FLEX-H1W3", "מסגרות וגדרות", "מטר")]
    [InlineData("H2W4 מעקה קיים AS MADE", "מסגרות וגדרות", "מטר")]
    [InlineData("HW-FENCE-EX", "מסגרות וגדרות", "מטר")]
    [InlineData("TR-MARK-WHT-801", "סימון ושילוט", "מטר")]
    [InlineData("TR-SIGN-POLE", "סימון ושילוט", "יח׳")]
    [InlineData("DR-MNHL-EX", "ניקוז", "יח׳")]
    [InlineData("DR-PIPE-CONC-01-EX", "ניקוז", "מטר")]
    [InlineData("UT-W-VLV", "מים", "מטר")]
    [InlineData("!UT-WATER-EX", "מים", "מטר")]
    [InlineData("UT-TC-BEZ", "תקשורת", "מטר")]
    [InlineData("UT-EL-HV", "חשמל", "מטר")]
    [InlineData("UT-LT-CABL", "תאורה", "מטר")]
    [InlineData("UT-G-EX", "גז", "מטר")]
    [InlineData("UT-IR-D", "השקיה", "מטר")]
    [InlineData("LA-TREE-BL", "גינון", "יח׳")]
    [InlineData("HW-IC_EXST_TO4-GM-M44$0$HW-CURB-GRDN", "אבני שפה", "מטר")]
    public void Firm_prefixes_name_the_element_and_its_unit(string layer, string topic, string unit)
    {
        var meaning = FirmLayerConventions.Match(layer);
        Assert.NotNull(meaning);
        Assert.Equal(topic, meaning!.Topic);
        Assert.Equal(unit, meaning.Unit);
        Assert.Equal(LayerRole.Quantity, meaning.Role);
    }

    [Theory]
    [InlineData("HW-CS-EX", "Annotation")]
    [InlineData("HW-CS-D", "Annotation")]
    [InlineData("HW-PR-EX-ELEV", "Annotation")]
    [InlineData("BEZEK-EX_Name", "Annotation")]
    [InlineData("KVISH-KAYAM_Elev", "Annotation")]
    [InlineData("UT-W-T", "Annotation")]
    [InlineData("HW-TYP-TEXT", "Annotation")]
    [InlineData("CL_Cont_Minor", "Annotation")]
    [InlineData("CL_Slope", "Annotation")]
    [InlineData("HW-TRWY", "Boundary")]
    [InlineData("HW-EOP", "Boundary")]
    [InlineData("HW-SHLD", "Boundary")]
    public void Drafting_and_edge_layers_are_not_pay_items(string layer, string role)
        => Assert.Equal(role, FirmLayerConventions.Match(layer)!.Role.ToString());

    [Theory]
    [InlineData("ASMADE-WATER-PIPE", "x.dwg", "ExistingKeep")]
    [InlineData("ASMADE_DR_PIPE_600", "x.dwg", "ExistingKeep")]
    [InlineData("AS-MADE CURB", "x.dwg", "ExistingKeep")]
    [InlineData("CASING-AS-MADE", "UT-6403-GAS-MHD.dwg", "ExistingKeep")]
    [InlineData("H2W4 מעקה קיים AS MADE", "x.dwg", "ExistingKeep")]
    [InlineData("DR-MNHL-EX", "x.dwg", "ExistingKeep")]
    [InlineData("Hashmal-LV-ex", "x.dwg", "ExistingKeep")]
    [InlineData("0-BEZEQ KAYAM", "x.dwg", "ExistingKeep")]
    [InlineData("HW-ROAD-HA", "HW-IC_HA-EXST_TO4-GM-M44.dwg", "ExistingKeep")]
    [InlineData("M1502 asf", "SR-3933-VSC-GIS-MADE-GDR573-MHD.dwg", "ExistingKeep")]
    [InlineData("HW-ROAD-HA", "ASMADE-ROAD-40.dwg", "ExistingKeep")]
    [InlineData("S_WALL_RETAIN", @".\PD\6422-SR-WEST-MHD.dwg", "ExistingKeep")]
    [InlineData("6422-SR-SURVEY-MD17615-MHD|S_WALL_BT", "host.dwg", "ExistingKeep")]
    [InlineData("6422-WALL-EX|S_WALL_BT", "host.dwg", "ExistingKeep")]
    [InlineData("6422-EV-NATAZ|HW-WALL-NEW", @".\PD\6422-EV-NATAZ.dwg", "New")]
    [InlineData("HW-ROAD-HA", "SRB-ROAD.dwg", "New")]
    [InlineData("UT-W-CNCL", "x.dwg", "ExistingDemolish")]
    [InlineData("UT-CANCEL-PIPE", "x.dwg", "ExistingDemolish")]
    [InlineData("DELEK BITUL", "x.dwg", "ExistingDemolish")]
    [InlineData("ASMADE-CURB-PIRUK", "x.dwg", "ExistingDemolish")]
    [InlineData("HW-ROAD-HA", "HW-IC_HATCH-GM-M44.dwg", "New")]
    [InlineData("TR-MARK-WHT-801", "x.dwg", "New")]
    [InlineData("HW-EXIT-RAMP", "x.dwg", "New")]
    public void Existing_state_is_read_from_the_layer_and_source_names(string layer, string source, string expectedName)
    {
        var expected = Enum.Parse<InfrastructureDisposition>(expectedName); // enums in InlineData drop tests from discovery
        var (disposition, evidence) = InfrastructureCondition.Detect(layer, source);
        Assert.Equal(expected, disposition);
        Assert.Equal(expected == InfrastructureDisposition.New, evidence.Length == 0);
    }

    [Fact]
    public void Manholes_drawn_as_closed_outlines_are_counted_not_measured_in_metres()
    {
        var outline = new InfrastructureLine("UT-DR.dwg", "A1", "DR-MNHL", "Circle", 3.77, true, "ניקוז", "TEST", "BOQ_DRAINAGE") { AreaSquareMetres = 1.13 };
        var group = InfrastructureBoq.Group(new[] { InfrastructureBoq.Describe(outline), InfrastructureBoq.Describe(outline with { Handle = "A2" }) }).Single();
        Assert.Equal("יח׳", group.MeasurementUnit);
        Assert.Equal(2, group.Quantity);
        var pipe = InfrastructureBoq.Describe(new InfrastructureLine("UT-DR.dwg", "A3", "DR-PIPE-CONC-01", "Polyline", 50, false, "ניקוז", "TEST", "BOQ_DRAINAGE"));
        Assert.Equal(0, pipe.CountUnits);
    }

    [Fact]
    public void A_layer_whose_subject_is_demolition_is_existing_for_demolition()
    {
        var g = Group("PIRUKIM-2025", length: 12);
        Assert.Equal(InfrastructureDisposition.ExistingDemolish, g.Disposition);
    }

    [Fact]
    public void Changing_the_disposition_withdraws_the_price_and_survives_save_and_restore()
    {
        var catalog = Nti();
        var g = Group("ASMADE-CURB", length: 80);
        Assert.Equal(InfrastructureDisposition.ExistingKeep, g.Disposition);
        Assert.Equal("אבני שפה", g.Topic);
        g.Disposition = InfrastructureDisposition.ExistingDemolish;
        var option = new InfrastructureCatalogSearch(catalog).Find(g).First(o => o.IsRecommended);
        g.Assign(catalog, option.Code);
        Assert.True(g.Approved);
        g.Disposition = InfrastructureDisposition.ExistingKeep;
        Assert.False(g.Approved);
        g.Disposition = InfrastructureDisposition.ExistingDemolish;
        g.Assign(catalog, option.Code);

        var path = Path.Combine(_dir, "review.json");
        InfrastructureReviewStore.Save(path, "H", new[] { catalog }, new[] { g }, new System.Collections.Generic.HashSet<InfrastructureGroup>());
        var fresh = Group("ASMADE-CURB", length: 80);
        Assert.Equal(1, InfrastructureReviewStore.Restore(path, "H", new[] { catalog }, new[] { fresh }, new System.Collections.Generic.HashSet<InfrastructureGroup>()));
        Assert.Equal(InfrastructureDisposition.ExistingDemolish, fresh.Disposition);
        Assert.Equal(option.Code, fresh.Code);
    }

    [Theory]
    [InlineData("ASMADE-CURB", 0d, 60d, 0d, "פירוק אבני שפה")]
    [InlineData("ASMADE-DR-MNHL", 0d, 0d, 4d, "פירוק שוחות")]
    [InlineData("ASMADE-ROAD-HA", 250d, 0d, 0d, "פירוק אספלט")]
    [InlineData("ASMADE-FENCE", 0d, 30d, 0d, "פירוק גדר")]
    [InlineData("ASMADE-DR-PIPE-600", 0d, 40d, 0d, "צינור ניקוז")]
    public void Demolition_rows_are_recommended_the_demolition_item_of_their_element(string layer, double area, double length, double count, string expected)
    {
        var g = Group(layer, length: length, area: area, count: count, part: area > 0 ? "שטחים" : "");
        g.Disposition = InfrastructureDisposition.ExistingDemolish;
        var recommended = new InfrastructureCatalogSearch(Nti()).Find(g).FirstOrDefault(o => o.IsRecommended);
        Assert.NotNull(recommended);
        Assert.True(recommended!.Description.Replace("  ", " ").Contains(expected), recommended.Code + " " + recommended.Description);
        Assert.StartsWith("51.01", recommended.Code);
    }

    [Fact]
    public void New_curbs_are_not_offered_a_demolition_item_and_existing_that_stays_is_never_auto_priced()
    {
        var search = new InfrastructureCatalogSearch(Nti());
        var curb = Group("HW-CURB-GRDN", length: 100);
        var recommended = search.Find(curb).First(o => o.IsRecommended);
        Assert.DoesNotContain("פירוק", recommended.Description);

        var keep = Group("ASMADE-CURB", length: 100);
        var batch = search.PlanAutomaticAssignment(new[] { curb, keep });
        Assert.Equal(1, batch.Apply());
        Assert.True(curb.Approved);
        Assert.False(keep.Approved);
    }

    /// <summary>Live 2026-11-06 (4NATIVIM, 18.5 km of curbs): every curb layer got 51.06.1830, a perforated drainage pipe.</summary>
    [Theory]
    [InlineData("HW-IC_4NATIV-GM-M44|HW-CURB-GRDN")]
    [InlineData("HW-IC_4NATIV-GM-M44|TR-CURB3_MIDRAHA-17")]
    [InlineData("HW-IC_4NATIV-GM-M44|TR-CURB2_MIFRADA-18")]
    [InlineData("TR-CURB-RED")]
    public void New_curbs_are_recommended_a_curb_item_never_a_pipe(string layer)
    {
        var curb = Group(layer, length: 6742.64);
        var recommended = new InfrastructureCatalogSearch(Nti()).Find(curb).First(o => o.IsRecommended);
        var text = InfrastructureCatalogSearch.Normalize(recommended.Description);   // "אבן-שפה" is written with a hyphen too
        Assert.True(text.Contains("אבן שפה") || text.Contains("אבני שפה"), recommended.Code + " " + text);
        Assert.DoesNotContain("צינור", text);
        Assert.StartsWith("51.06", recommended.Code);
    }

    [Fact]
    public void Utility_lines_still_get_their_pipe_item()
    {
        var search = new InfrastructureCatalogSearch(Nti());
        var water = search.Find(Group("UT-W-DN200", length: 100)).First(o => o.IsRecommended);
        var text = InfrastructureCatalogSearch.Normalize(water.Description);
        Assert.True(text.Contains("צינור") || text.Contains("קו מים"), water.Code + " " + text);
    }

    [Fact]
    public void Road_area_is_summed_per_surface_and_the_structure_gives_volumes()
    {
        var groups = InfrastructureBoq.Group(new[]
        {
            Line("HW-ROAD-HA", area: 1000, part: "שטחים"), Line("HW-ROAD-HA-40", area: 500, part: "שטחים"),
            Line("HW-SDWK-H", area: 200, part: "שטחים"), Line("ASMADE-ROAD-HA", area: 300, part: "שטחים"),
            Line("HW-CURB-GRDN", length: 90),
        });
        var surfaces = RoadPavement.Surfaces(groups);
        Assert.Equal(1500, surfaces.Single(s => s.Surface == RoadSurfaceKind.Road && s.Disposition == InfrastructureDisposition.New).AreaSquareMetres, 6);
        Assert.Equal(300, surfaces.Single(s => s.Surface == RoadSurfaceKind.Road && s.Disposition == InfrastructureDisposition.ExistingKeep).AreaSquareMetres, 6);
        Assert.Equal(200, surfaces.Single(s => s.Surface == RoadSurfaceKind.Sidewalk).AreaSquareMetres, 6);

        var layers = RoadPavement.Structure(surfaces, Typed);
        var subbase = layers.Single(l => l.Surface == RoadSurfaceKind.Road && l.Layer.Name.StartsWith("מצע"));
        Assert.Equal(1500 * 0.30, subbase.VolumeCubicMetres, 6);
        Assert.Equal(subbase.VolumeCubicMetres, subbase.Quantity, 6);
        var wearing = layers.First(l => l.Surface == RoadSurfaceKind.Road);
        Assert.Equal(1500, wearing.Quantity, 6);
        Assert.DoesNotContain(layers, l => l.AreaSquareMetres == 300); // existing road is not rebuilt by default

        var derived = RoadPavement.DerivedGroups(layers);
        var derivedSubbase = derived.Single(d => d.Layer.Contains("מצע") && d.Layer.StartsWith("מיסעה"));
        Assert.Equal("מ״ק", derivedSubbase.MeasurementUnit);
        Assert.Equal(450, derivedSubbase.Quantity, 6);
        var recommended = new InfrastructureCatalogSearch(Nti()).Find(derivedSubbase).First(o => o.IsRecommended);
        Assert.Contains("מצע סוג א", recommended.Description);
    }

    [Fact]
    public void The_engineer_structure_is_saved_per_drawing_and_merged_over_defaults()
    {
        var path = Path.Combine(_dir, "roads.json");
        var road = Typed.Single(s => s.Surface == RoadSurfaceKind.Road);
        var edited = road with { Layers = road.Layers.Select(l => l.Name.StartsWith("מצע") ? l with { ThicknessCm = 40 } : l).ToArray() };
        RoadPavement.Save(path, new[] { edited });
        var loaded = RoadPavement.Load(path);
        Assert.Equal(RoadPavement.Defaults.Count, loaded.Count);
        Assert.Equal(40, loaded.Single(s => s.Surface == RoadSurfaceKind.Road).Layers.Single(l => l.Name.StartsWith("מצע")).ThicknessCm);
        Assert.Same(RoadPavement.Defaults, RoadPavement.Load(Path.Combine(_dir, "missing.json")));
        // No placeholder structure exists any more: without a report or a typed structure nothing is priced.
        Assert.All(RoadPavement.Defaults, d => Assert.Empty(d.Layers));
    }

    [Fact]
    public void A_saved_file_from_1_10_does_not_bring_back_the_old_placeholder_structures()
    {
        // 1.9-1.10 saved every structure (placeholders included) whenever the engineer edited one surface.
        var path = Path.Combine(_dir, "roads-v1.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Structures = new[]
            {
                new { Surface = RoadSurfaceKind.Road, Layers = Typed[0].Layers.Select(l => new { l.Name, l.ThicknessCm, l.Unit, l.SearchHint }).ToArray() },
                new { Surface = RoadSurfaceKind.Sidewalk, Layers = new[] { new { Name = "ריצוף", ThicknessCm = 8d, Unit = "מ״ר", SearchHint = "אבן משתלבת 8" } } },
            },
        }));
        var loaded = RoadPavement.Load(path);
        Assert.Empty(loaded.Single(s => s.Surface == RoadSurfaceKind.Road).Layers);          // the untouched placeholder
        Assert.Equal(8, loaded.Single(s => s.Surface == RoadSurfaceKind.Sidewalk).Layers.Single().ThicknessCm);   // the engineer's edit
    }

    [Fact]
    public void Export_puts_new_work_demolition_existing_and_roads_on_their_own_sheets()
    {
        var catalog = Nti();
        var lines = new[]
        {
            Line("HW-CURB-GRDN", length: 100), Line("ASMADE-CURB", length: 60),
            Line("ASMADE-WATER-PIPE", length: 40), Line("HW-ROAD-HA", area: 1000, part: "שטחים"),
        };
        var groups = InfrastructureBoq.Group(lines).ToList();
        groups.Single(g => g.Layer == "ASMADE-CURB").Disposition = InfrastructureDisposition.ExistingDemolish;
        var search = new InfrastructureCatalogSearch(catalog);
        search.PlanAutomaticAssignment(groups).Apply();
        var surfaces = RoadPavement.Surfaces(groups);
        var structure = RoadPavement.Structure(surfaces, Typed);
        var report = new RoadPavement.Report(surfaces, structure, Array.Empty<InfrastructureGroup>());
        var path = Path.Combine(_dir, "boq.xlsx");
        InfrastructureBoq.Export(path, groups, lines, new[] { catalog }, Array.Empty<string>(), "d.dwg", "H", "TEST", report);

        using var zip = ZipFile.OpenRead(path);
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var names = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open()).Descendants(main + "sheet").Select(s => (string)s.Attribute("name")!).ToArray();
        Assert.Equal(new[] { InfrastructureBoq.NewWorkSheet, InfrastructureBoq.DemolitionSheet, InfrastructureBoq.ExistingSheet, InfrastructureBoq.RoadsSheet,
            "מדידות מקור", "בדיקות ומקור המחירון" }, names);
        var mainSheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ToString();
        Assert.Contains("HW-CURB-GRDN", mainSheet);
        var demolitionSheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet2.xml")!.Open()).ToString();
        Assert.Contains("ASMADE-CURB", demolitionSheet);
        Assert.DoesNotContain("HW-CURB-GRDN", demolitionSheet);
        Assert.Contains("ASMADE-WATER-PIPE", XDocument.Load(zip.GetEntry("xl/worksheets/sheet3.xml")!.Open()).ToString());
        Assert.Contains($"'{InfrastructureBoq.DemolitionSheet}'!H", mainSheet);

        var demolitionOnly = Path.Combine(_dir, "demolition.xlsx");
        InfrastructureBoq.ExportDemolition(demolitionOnly, groups, new[] { catalog }, Array.Empty<string>(), "d.dwg", "H", "TEST");
        using var dz = ZipFile.OpenRead(demolitionOnly);
        Assert.Contains("ASMADE-CURB", XDocument.Load(dz.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ToString());
    }

    [Fact]
    public void A_road_area_with_a_structure_is_priced_once_through_its_structure_rows()
    {
        // Live 2026-11-05: every road hatch was auto-priced "הסרת צמחיה" and the sidewalk hatch a paving item, on top
        // of their structure rows; the bike path's asphalt row got the acrylic coating of the marking chapter.
        var catalog = Nti();
        var groups = InfrastructureBoq.Group(new[]
        {
            Line("HW-ROAD-HA", area: 1000, part: "שטחים"), Line("HW-SDWK-H", area: 200, part: "שטחים"),
            Line("HW-BIKE-HA", area: 100, part: "שטחים"), Line("ASMADE-ROAD-HA", area: 300, part: "שטחים"),
            Line("HW-CURB-GRDN", length: 90),
        }).ToList();
        Assert.Equal(3, RoadPavement.MarkPricedByStructure(groups, Typed));
        var road = groups.Single(g => g.Layer == "HW-ROAD-HA");
        Assert.True(road.PricedByStructure);
        Assert.False(road.IsPayItem);
        Assert.Equal("לפי מבנה", road.MappingDisplay);
        Assert.False(groups.Single(g => g.Layer == "ASMADE-ROAD-HA").PricedByStructure); // existing keeps its own row

        var derived = RoadPavement.DerivedGroups(RoadPavement.Structure(RoadPavement.Surfaces(groups), Typed));
        var all = groups.Concat(derived).ToList();
        new InfrastructureCatalogSearch(catalog).PlanAutomaticAssignment(all).Apply();

        Assert.All(groups.Where(g => g.PricedByStructure), g => Assert.False(g.Approved, g.Layer));
        Assert.True(groups.Single(g => g.Layer == "HW-CURB-GRDN").Approved);
        Assert.All(derived, d => Assert.True(d.Approved, d.Layer));
        Assert.All(derived, d => Assert.False(d.Code!.StartsWith("51.01") || d.Code.StartsWith("51.32"), d.Layer + " " + d.Code + " " + d.Description));
        var bike = derived.Single(d => d.Layer.StartsWith("שביל אופניים") && d.MeasurementUnit == "מ״ר");
        Assert.StartsWith("51.04", bike.Code);
        Assert.Contains("12.5", bike.Description);
        Assert.NotNull(bike.Price);
        var paving = derived.Single(d => d.Layer.StartsWith("מדרכה") && d.MeasurementUnit == "מ״ר");
        Assert.Contains("משתלבת", paving.Description);
        Assert.Contains("/6", paving.Description);
        // The 6 cm stone chosen 2026-11-05, not the ₪91 "ריצוף באבן משתלבת" a layer-element bonus would prefer (2026-11-06).
        Assert.Equal("51.06.0451", paving.Code);

        // Approved before the structure existed: marking withdraws it, so the area cannot stay priced twice.
        var early = InfrastructureBoq.Group(new[] { Line("HW-ROAD-HA", area: 50, part: "שטחים") }).Single();
        var anyItem = new InfrastructureCatalogSearch(catalog).Find(early).First(o => o.IsRecommended);
        early.Assign(catalog, anyItem.Code);
        RoadPavement.MarkPricedByStructure(new[] { early }, Typed);
        Assert.False(early.Approved);

        // No structure for the surface (no report, nothing typed): the area is NOT priced as a bare area — it waits
        // for the pavement report (engineers' rule 2026-11-09: without the report the pavement cannot be calculated).
        var bare = Typed.Select(s => s.Surface == RoadSurfaceKind.Road ? s with { Layers = Array.Empty<PavementLayer>() } : s).ToArray();
        RoadPavement.MarkPricedByStructure(groups, bare);
        Assert.False(road.PricedByStructure);
        Assert.True(road.AwaitingPavementReport);
        Assert.False(road.IsPayItem);
        Assert.Equal("ממתין לדו״ח מבנה", road.MappingDisplay);
        // A surface that is not a pavement (an island, a structure area) keeps its own row.
        var island = InfrastructureBoq.Group(new[] { Line("HW-HATCH-ILND", area: 40, part: "שטחים") }).Single();
        RoadPavement.MarkPricedByStructure(new[] { island }, bare);
        Assert.True(island.IsPayItem);
    }

    [Fact]
    public void Export_lists_a_structure_priced_area_on_the_roads_sheet_only()
    {
        var catalog = Nti();
        var lines = new[] { Line("HW-ROAD-HA", area: 1000, part: "שטחים"), Line("HW-CURB-GRDN", length: 100) };
        var groups = InfrastructureBoq.Group(lines).ToList();
        RoadPavement.MarkPricedByStructure(groups, Typed);
        var surfaces = RoadPavement.Surfaces(groups);
        var structure = RoadPavement.Structure(surfaces, Typed);
        var all = groups.Concat(RoadPavement.DerivedGroups(structure)).ToList();
        new InfrastructureCatalogSearch(catalog).PlanAutomaticAssignment(all).Apply();
        var path = Path.Combine(_dir, "roads-once.xlsx");
        InfrastructureBoq.Export(path, all, lines, new[] { catalog }, Array.Empty<string>(), "d.dwg", "H", "TEST",
            new RoadPavement.Report(surfaces, structure, Array.Empty<InfrastructureGroup>()));

        using var zip = ZipFile.OpenRead(path);
        var mainSheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ToString();
        Assert.DoesNotContain("HW-ROAD-HA", mainSheet);
        Assert.Contains("51.04.0340", mainSheet);
        Assert.Contains("HW-CURB-GRDN", mainSheet);
    }
}
