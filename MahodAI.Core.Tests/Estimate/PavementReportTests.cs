using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using NetTopologySuite.Geometries;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// The pavement-structure report in the bill of quantities (2026-11-09). The report shapes follow the real one the firm got
/// for the Modiin Illit bus corridor (Eng. Horany, 28/09/2026): two all-asphalt road structures (31 and 28 cm), each with a
/// full-replacement and a widening composition, sidewalk 6+3+30, bike path 3+5+30, and a segment table by cross-section.
/// </summary>
public sealed class PavementReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcd-pavement-test-" + Guid.NewGuid().ToString("N"));
    public PavementReportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private CatalogSnapshot Nti()
    {
        var offer = Assert.Single(BundledPriceBooks.Offers(_dir, new MahodAI.CivilDelivery.Shared.ProjectProfile()));
        return PriceBookXlsxLoader.Load(offer.Entry.Path, "TEST NTI");
    }

    private static PavementReportLayer Asphalt(double cm, double mm, string binder) =>
        new(cm, $"תא\"צ {mm} אגרגט גיר/דולומיט ביטומן {binder}", "asphalt", mm, binder, "גיר/דולומיט");

    private static PavementReportStructure Structure(string id, string family, string use, string variant, params PavementReportLayer[] layers) =>
        new(id, family, family.All(char.IsDigit) ? $"מבנה מיסעה מס' {family}" : family, family == "1" ? "5.2.1" : family == "2" ? "5.2.2" : null, use, variant, null,
            layers, layers.Sum(l => l.ThicknessCm), layers.Sum(l => l.ThicknessCm), true, 12);

    private static PavementReportContent Report(params PavementReportSegment[] segments) => new(
        new PavementReportDocument("דו״ח תכן מיסעות", "ציר העדפה", "Eng. Test", "28/09/2026", "1", "1560-25"),
        new[]
        {
            Structure("S1", "1", "road", "full", Asphalt(5, 19, "PG70-10"), Asphalt(5, 19, "PG70-10"), Asphalt(7, 25, "PG68-10"), Asphalt(7, 25, "PG68-10"), Asphalt(7, 25, "PG68-10")),
            Structure("S2", "1", "road", "widening", Asphalt(5, 19, "PG70-10"), Asphalt(5, 19, "PG70-10"), Asphalt(5, 25, "PG68-10"), Asphalt(5, 25, "PG68-10"), Asphalt(5, 25, "PG68-10"), Asphalt(6, 25, "PG68-10")),
            Structure("S3", "2", "road", "full", Asphalt(4, 19, "PG70-10"), Asphalt(5, 19, "PG70-10"), Asphalt(6, 25, "PG68-10"), Asphalt(6, 25, "PG68-10"), Asphalt(7, 25, "PG68-10")),
            Structure("S5", "sidewalk", "sidewalk", "minimum", new PavementReportLayer(6, "ריצוף בשילוב דו-כיווני", "paving"),
                new PavementReportLayer(3, "שכבת הנחה חול", "sand"), new PavementReportLayer(30, "מצע א' מהודק", "base", BaseGrade: "A")),
            Structure("S6", "bike", "bike_path", "minimum", Asphalt(3, 12.5, "PG70-10"), Asphalt(5, 19, "PG68-10"), new PavementReportLayer(30, "מצע א'", "base", BaseGrade: "A")),
        },
        segments, new PavementReportCoats(true, true, null), Array.Empty<string>(), Array.Empty<string>());

    private static readonly PavementReportSegment Seg1 = new("1", "1000", "1018", "שד' בית הלל", "1", "1");
    private static readonly PavementReportSegment Seg6 = new("6", "3000", "3008", "שד' בית שמאי", "2", "2");

    private static PavementReportSet Set(PavementReportContent content, Dictionary<string, string>? variants = null) =>
        new(new[] { new AttachedPavementReport("report.pdf", "AB", DateTime.UtcNow, "gemini-test", "pavement-report-1.0.0", 23, content, Array.Empty<string>()) },
            variants ?? new Dictionary<string, string>());

    // ───────────────────────────── layers ─────────────────────────────

    [Fact]
    public void Identical_report_layers_become_one_row_laid_several_times_with_their_sprays()
    {
        var layers = PavementReportPlan.Layers(Report().Structures[0], new PavementReportCoats(true, true, null));
        Assert.Equal(new[] { "תא״צ 19 PG70-10 5 ס״מ ×2", "תא״צ 25 PG68-10 7 ס״מ ×3", "ריסוס ציפוי מאחה ×4", "ריסוס ציפוי יסוד" }, layers.Select(l => l.Display));
        Assert.All(layers, l => Assert.Equal("מ״ר", l.Unit));
        Assert.Equal(31, layers.Sum(l => l.ThicknessCm * l.Times), 6);
    }

    [Fact]
    public void Every_report_layer_finds_its_own_price_book_item()
    {
        var search = new InfrastructureCatalogSearch(Nti());
        string Item(PavementLayer layer, RoadSurfaceKind surface = RoadSurfaceKind.Road)
        {
            var row = RoadPavement.DerivedGroups(new[] { new RoadPavement.LayerQuantity(surface, layer, 100, "מבנה 1") }).Single();
            return search.Find(row).First(o => o.IsRecommended).Code;
        }
        var road = PavementReportPlan.Layers(Report().Structures[0]);
        Assert.Equal("51.04.0350", Item(road[0]));                         // תא"צ 19, 5 ס"מ, גירי, PG70-10
        Assert.Contains(Item(road[1]), new[] { "51.04.0140", "51.04.0160" }); // תא"צ 25, 7 ס"מ, PG68-10 (סוג א / ב, same price)
        Assert.Equal("51.04.1470", Item(road[2]));                         // ריסוס ציפוי מאחה 0.25-0.5 between new layers
        Assert.Equal("51.04.1480", Item(road[3]));                         // ריסוס ציפוי יסוד 0.8-1.2
        var second = PavementReportPlan.Layers(Report().Structures[2]);
        Assert.Contains(Item(second[0]), new[] { "51.04.0310", "51.04.0330" }); // תא"צ 19, 4 ס"מ, PG70-10
        var sidewalk = PavementReportPlan.Layers(Report().Structures[3]);
        Assert.Equal("51.06.0451", Item(sidewalk[0], RoadSurfaceKind.Sidewalk));
        Assert.Equal("51.03.0010", Item(sidewalk.Single(l => l.Name.StartsWith("מצע")), RoadSurfaceKind.Sidewalk));
        var bike = PavementReportPlan.Layers(Report().Structures[4]);
        Assert.Equal("51.04.0482", Item(bike[0], RoadSurfaceKind.BikePath));  // תא"צ 12.5, 3 ס"מ, PG70-10
    }

    [Fact]
    public void An_overlay_mills_first_and_tacks_under_every_new_layer()
    {
        var overlay = new PavementReportStructure("S7", "1", "מיסעה קיימת", "6", "existing_road_overlay", "overlay", null,
            new[] { Asphalt(5, 19, "PG70-10"), Asphalt(5, 19, "PG70-10"), Asphalt(7, 25, "PG68-10") }, null, 17, true, 16);
        var layers = PavementReportPlan.Layers(overlay);
        Assert.Equal("קרצוף אספלט", layers[0].Name);
        Assert.Equal(17, layers[0].ThicknessCm);
        Assert.Equal(3, layers.Single(l => l.Name == "ריסוס ציפוי מאחה").Times);
        Assert.DoesNotContain(layers, l => l.Name == "ריסוס ציפוי יסוד");
    }

    // ───────────────────────────── which structure where ─────────────────────────────

    [Fact]
    public void Each_segment_gets_its_own_family_and_the_engineers_variant()
    {
        var set = Set(Report(Seg1, Seg6));
        var one = new PavementReportPlan.Zone(0, Seg1); var six = new PavementReportPlan.Zone(0, Seg6);
        Assert.Equal("S1", PavementReportPlan.StructureFor(set, one, RoadSurfaceKind.Road)!.Id);
        Assert.Equal("S1", PavementReportPlan.StructureFor(set, one, RoadSurfaceKind.PublicTransport)!.Id);
        Assert.Equal("S3", PavementReportPlan.StructureFor(set, six, RoadSurfaceKind.Road)!.Id);
        Assert.Equal("S5", PavementReportPlan.StructureFor(set, six, RoadSurfaceKind.Sidewalk)!.Id);
        Assert.Equal("S6", PavementReportPlan.StructureFor(set, one, RoadSurfaceKind.BikePath)!.Id);
        Assert.Null(PavementReportPlan.StructureFor(set, one, RoadSurfaceKind.Shoulder));       // the report has none
        Assert.Null(PavementReportPlan.StructureFor(set, PavementReportPlan.Zone.Outside, RoadSurfaceKind.Road));

        var widening = Set(Report(Seg1, Seg6), new Dictionary<string, string> { [PavementReportSet.Key(0, "1")] = "widening" });
        Assert.Equal("S2", PavementReportPlan.StructureFor(widening, one, RoadSurfaceKind.Road)!.Id);
        Assert.Equal("S3", PavementReportPlan.StructureFor(widening, six, RoadSurfaceKind.Road)!.Id);
    }

    [Fact]
    public void A_report_without_segments_holds_for_the_whole_drawing_only_when_its_road_structure_is_unambiguous()
    {
        var two = Set(Report());
        Assert.Null(PavementReportPlan.StructureFor(two, new PavementReportPlan.Zone(0, null), RoadSurfaceKind.Road));   // families 1 and 2: which?
        Assert.Equal("S3", PavementReportPlan.StructureFor(two with { FallbackFamily = "2" }, new PavementReportPlan.Zone(0, null), RoadSurfaceKind.Road)!.Id);
        Assert.Equal("S5", PavementReportPlan.StructureFor(two, new PavementReportPlan.Zone(0, null), RoadSurfaceKind.Sidewalk)!.Id);
    }

    [Fact]
    public void Areas_of_the_same_structure_are_summed_and_uncovered_area_is_listed_not_priced()
    {
        var set = Set(Report(Seg1, Seg6));
        var plan = PavementReportPlan.Quantities(set, new[]
        {
            new PavementReportPlan.ZoneArea(new(0, Seg1), RoadSurfaceKind.Road, 1000),
            new PavementReportPlan.ZoneArea(new(0, Seg6), RoadSurfaceKind.Road, 400),
            new PavementReportPlan.ZoneArea(new(0, Seg1), RoadSurfaceKind.Sidewalk, 300),
            new PavementReportPlan.ZoneArea(new(0, Seg6), RoadSurfaceKind.Sidewalk, 200),
            new PavementReportPlan.ZoneArea(PavementReportPlan.Zone.Outside, RoadSurfaceKind.Road, 250),
        });
        var wearing1 = plan.Layers.Single(l => l.Structure.StartsWith("מבנה 1") && l.Layer.Name == "תא״צ 19 PG70-10");
        Assert.Equal(1000, wearing1.AreaSquareMetres, 6);
        Assert.Equal(2000, wearing1.Quantity, 6);                                  // two 5 cm courses
        Assert.Equal(1000 * 0.05 * 2, wearing1.VolumeCubicMetres, 6);
        var base30 = plan.Layers.Single(l => l.Surface == RoadSurfaceKind.Sidewalk && l.Layer.ByVolume && l.Layer.Name.StartsWith("מצע"));
        Assert.Equal(500 * 0.30, base30.Quantity, 6);                             // one sidewalk structure over both segments
        Assert.Contains(plan.Zones, z => z.Zone == "מחוץ למקטעי הדו״ח" && !z.Priced && z.AreaSquareMetres == 250);
        Assert.Contains(plan.Notes, n => n.Contains("250"));
        Assert.Equal(new HashSet<RoadSurfaceKind> { RoadSurfaceKind.Road, RoadSurfaceKind.Sidewalk }, plan.Priced);
        // An island has no structure in the report: it keeps its own area row and is not listed as unpriced.
        var withIsland = PavementReportPlan.Quantities(set, new[] { new PavementReportPlan.ZoneArea(new(0, Seg1), RoadSurfaceKind.Island, 80) });
        Assert.Empty(withIsland.Zones); Assert.Empty(withIsland.Notes);

        var rows = RoadPavement.DerivedGroups(plan.Layers);
        Assert.Equal(rows.Count, rows.Select(r => r.Layer).Distinct().Count());   // every BOQ row is its own line
        Assert.Contains(rows, r => r.Layer.Contains("מבנה 1 · 5.2.1") && r.Evidence.Contains("× 2 שכבות"));
    }

    // ───────────────────────────── segments on the plan ─────────────────────────────

    /// <summary>A straight centre line along X with a section label every 20 m from 1000 (1003 deliberately missing).</summary>
    private static (PavementSegmentBands.Centreline[], PavementSegmentBands.SectionLabel[]) Road(double y = 0)
    {
        var line = new PavementSegmentBands.Centreline("L1", "HW-ALGN", new[] { (0d, y), (400d, y) });
        var labels = Enumerable.Range(0, 21).Where(i => i != 3)
            .Select(i => new PavementSegmentBands.SectionLabel((1000 + i).ToString(), i * 20, y + 3, "HW-ALGN-SEC-NAME")).ToArray();
        return (new[] { line }, labels);
    }

    [Fact]
    public void A_segment_is_the_band_between_its_two_sections_cut_square()
    {
        var (lines, labels) = Road();
        var r = PavementSegmentBands.Build(lines, labels, new[] { (0, new PavementReportSegment("1", "1000", "1005", null, "1", "1")) }, 15, 15);
        var band = Assert.Single(r.Bands);
        Assert.Equal(0, band.FromStation, 6); Assert.Equal(100, band.ToStation, 6);
        Assert.Equal(100 * 30, band.Shape.Area, 3);
        Assert.Empty(r.Missing);
        Assert.Equal(20, r.LabelsOnLines);
    }

    [Fact]
    public void A_missing_section_label_is_interpolated_between_its_neighbours()
    {
        var (lines, labels) = Road();
        var r = PavementSegmentBands.Build(lines, labels, new[] { (0, new PavementReportSegment("1", "1003", "1010", null, "1", "1")) }, 15, 15);
        Assert.Equal(60, Assert.Single(r.Bands).FromStation, 6);
    }

    [Fact]
    public void Overlapping_segments_never_count_the_same_area_twice()
    {
        // The real table: segment 7 runs 2000-2016 and segment 8 2015-2050 — they share one section interval.
        var (lines, labels) = Road();
        var r = PavementSegmentBands.Build(lines, labels, new[]
        {
            (0, new PavementReportSegment("7", "1000", "1006", null, "2", "2")),
            (0, new PavementReportSegment("8", "1005", "1010", null, "2", "2")),
        }, 15, 15);
        Assert.Equal(2, r.Bands.Count);
        Assert.Equal(200 * 30, r.Bands.Sum(b => b.Shape.Area), 3);
        Assert.NotEmpty(r.Findings);
    }

    [Fact]
    public void Labels_of_crossing_roads_near_an_axis_do_not_break_its_own_series()
    {
        // Live 2026-11-09: the 1000 axis carried 406, 600, 2117 and 3062 at its junctions; ordering by number made the strays
        // win and every segment collapsed to a few square metres.
        var (lines, labels) = Road();
        var strays = new[]
        {
            new PavementSegmentBands.SectionLabel("406", 45, 4, "HW-ALGN-SEC-NAME"), new PavementSegmentBands.SectionLabel("600", 47, -4, "HW-ALGN-SEC-NAME"),
            new PavementSegmentBands.SectionLabel("2117", 395, 5, "HW-ALGN-SEC-NAME"), new PavementSegmentBands.SectionLabel("3062", 210, -6, "HW-ALGN-SEC-NAME"),
        };
        var r = PavementSegmentBands.Build(lines, labels.Concat(strays).ToArray(), new[] { (0, new PavementReportSegment("1", "1000", "1018", null, "1", "1")) }, 15, 15);
        var band = Assert.Single(r.Bands);
        Assert.Equal(0, band.FromStation, 6); Assert.Equal(360, band.ToStation, 6);
        Assert.Contains(r.Findings, f => f.Contains("4 מספרי חתך"));
        // A stray never becomes a station of the axis: its number is not found on it.
        var none = PavementSegmentBands.Build(lines, labels.Concat(strays).ToArray(), new[] { (0, new PavementReportSegment("x", "2100", "2117", null, "2", "2")) }, 15, 15);
        Assert.Empty(none.Bands);
    }

    [Fact]
    public void A_side_roads_first_section_on_the_main_axis_still_starts_the_side_road()
    {
        // Live 2026-11-09, segment 11 (700-705): label 700 lies 0.2 m from road 2000's axis and 3.3 m from road 700's own.
        var main = new PavementSegmentBands.Centreline("MAIN", "HW-ALGN", new[] { (0d, 0d), (400d, 0d) });
        var side = new PavementSegmentBands.Centreline("SIDE", "HW-ALGN", new[] { (100d, 3.3d), (100d, 200d) });
        var labels = Enumerable.Range(0, 21).Select(i => new PavementSegmentBands.SectionLabel((2000 + i).ToString(), i * 20, 3, "HW-ALGN-SEC-NAME"))
            .Concat(Enumerable.Range(0, 10).Select(i => new PavementSegmentBands.SectionLabel((700 + i).ToString(), 100, i == 0 ? 0.2 : 3.3 + i * 20, "HW-ALGN-SEC-NAME")))
            .ToArray();
        var r = PavementSegmentBands.Build(new[] { main, side }, labels, new[] { (0, new PavementReportSegment("11", "700", "705", null, "2", "2")) }, 10, 15);
        var band = Assert.Single(r.Bands);
        Assert.Equal("SIDE", band.CentrelineId);
        Assert.Equal(0, band.FromStation, 1);
        Assert.Empty(r.Missing);
    }

    [Fact]
    public void An_axis_drawn_against_its_numbering_still_gives_the_right_stations()
    {
        var line = new PavementSegmentBands.Centreline("L1", "HW-ALGN", new[] { (400d, 0d), (0d, 0d) });   // drawn from 1020 back to 1000
        var labels = Enumerable.Range(0, 21).Select(i => new PavementSegmentBands.SectionLabel((1000 + i).ToString(), i * 20, 3, "HW-ALGN-SEC-NAME")).ToArray();
        var band = Assert.Single(PavementSegmentBands.Build(new[] { line }, labels, new[] { (0, new PavementReportSegment("1", "1000", "1005", null, "1", "1")) }, 15, 15).Bands);
        Assert.Equal(300, band.FromStation, 6); Assert.Equal(400, band.ToStation, 6);
    }

    [Fact]
    public void Sections_that_are_not_on_any_centre_line_are_reported_as_missing()
    {
        var (lines, labels) = Road();
        var r = PavementSegmentBands.Build(lines, labels, new[]
        {
            (0, new PavementReportSegment("6", "3000", "3008", null, "2", "2")),
            (0, new PavementReportSegment("1", "1000", "1002", null, "1", "1")),
        }, 15, 15);
        Assert.Single(r.Bands);
        Assert.Contains(r.Missing, m => m.Contains("3000") && m.Contains("לא נמצאו"));
        // Labels far from every line are not sections of it.
        var far = labels.Select(l => l with { Y = l.Y + 100 }).ToArray();
        var none = PavementSegmentBands.Build(lines, far, new[] { (0, new PavementReportSegment("1", "1000", "1002", null, "1", "1")) }, 15, 15);
        Assert.Empty(none.Bands);
        Assert.Equal(0, none.LabelsOnLines);
    }

    /// <summary>Real layer names from the 6422 west model and its 22 XREFs (live 2026-11-09): a loose match took 6,846
    /// "centre lines" — contours, drainage, cross-section lines — and every section number snapped to the wrong line.</summary>
    [Theory]
    [InlineData("6422-CL-M38-NEW|HW-ALGN", true)]
    [InlineData("6422-CL-M44$0$AX", true)]
    [InlineData("R-AXIS", true)]
    [InlineData("AX-43", true)]
    [InlineData("CL", true)]
    [InlineData("CENTERLINE", true)]
    [InlineData("HW-ALGN-SEC-NAME", false)]
    [InlineData("HW-ALGN-IP-TANG", false)]
    [InlineData("HW-CS-ALGN", false)]
    [InlineData("CL_Cont_Minor", false)]
    [InlineData("CL_Cont_Major", false)]
    [InlineData("CL_Slope", false)]
    [InlineData("0-DR-CL", false)]
    [InlineData("CL_FL_Top", false)]
    [InlineData("HW-CURB-ILND", false)]
    public void Only_road_axis_layers_are_centre_lines(string layer, bool expected) =>
        Assert.Equal(expected, PavementCentrelineReader.IsCentreLineLayer(layer));

    [Theory]
    [InlineData("6422-CL-M38-NEW|HW-ALGN-SEC-NAME", true)]
    [InlineData("CL_Sect", true)]
    [InlineData("STATIONS", true)]
    [InlineData("6422-CL-M44$0$SEC-DIST", false)]
    [InlineData("CL_Cont_Major", false)]
    [InlineData("CL_Elev_Hinge", false)]
    [InlineData("CL_Sect_Arrow", false)]
    [InlineData("HW-ALGN", false)]
    public void Section_numbers_are_read_only_from_section_label_layers(string layer, bool expected) =>
        Assert.Equal(expected, PavementCentrelineReader.IsSectionLabelLayer(layer));

    [Theory]
    [InlineData("1000", 1000)]
    [InlineData(" 2015 ", 2015)]
    [InlineData("חתך 700", 700)]
    [InlineData("1000.00", 1000)]
    public void Section_numbers_are_read_from_their_labels(string text, int expected) =>
        Assert.Equal(expected, PavementSegmentBands.SectionNumber(text));

    [Theory]
    [InlineData("12.35")]
    [InlineData("1+250.00")]
    [InlineData("שד' בית הלל")]
    public void Elevations_stations_and_names_are_not_section_numbers(string text) =>
        Assert.Null(PavementSegmentBands.SectionNumber(text));

    // ───────────────────────────── the server's answer and the store ─────────────────────────────

    private const string ServerAnswer = """
    {"ok": true, "model": "gemini-3.7-flash", "prompt_version": "pavement-report-1.0.0", "pages": 23, "warnings": [],
     "report": {
       "document": {"title": "דו״ח תכן מיסעות", "project": "ציר העדפה", "consultant": "אינג׳ חנא חוראני", "date": "28/09/2026", "edition": "1", "reference": "1560-25"},
       "structures": [
         {"id": "S1", "family": "1", "name": "מבנה מיסעה מס' 1", "section": "5.2.1", "use": "road", "variant": "full", "milling_cm": null,
          "layers": [{"thickness_cm": 5, "material": "תא״צ 19 PG70-10", "kind": "asphalt", "aggregate_mm": 19, "binder": "PG70-10", "aggregate_type": "גיר/דולומיט", "base_grade": null},
                     {"thickness_cm": 7, "material": "תא״צ 25 PG68-10", "kind": "asphalt", "aggregate_mm": 25, "binder": "PG68-10", "aggregate_type": "גיר/דולומיט", "base_grade": null}],
          "stated_total_cm": 12, "total_cm": 12, "total_matches": true, "page": 12}],
       "segments": [{"segment": "1", "from_section": "1000", "to_section": "1018", "road_name": "שד' בית הלל", "road_family": "1", "bus_lane_family": "1", "bus_lane_note": null, "page": 12}],
       "coats": {"tack_between_asphalt_layers": true, "prime_on_base": true, "note": null},
       "notes": ["חישוף 20 ס״מ"], "problems": []}}
    """;

    [Fact]
    public void The_servers_answer_becomes_an_attached_report_and_survives_the_store()
    {
        var data = new byte[] { 1, 2, 3 };
        var report = PavementReportClient.Parse(HttpStatusCode.OK, ServerAnswer, @"C:\p\דוח מבנה.pdf", data);
        Assert.Equal("דוח מבנה.pdf", report.FileName);
        Assert.Equal(PavementReportJson.Sha256(data), report.FileSha256);
        Assert.Equal("gemini-3.7-flash", report.Model);
        var s = Assert.Single(report.Content.Structures);
        Assert.Equal(new[] { 5d, 7d }, s.Layers.Select(l => l.ThicknessCm));
        Assert.Equal("PG70-10", s.Layers[0].Binder);
        Assert.Equal("1018", Assert.Single(report.Content.Segments).ToSection);

        var path = Path.Combine(_dir, "x.pavement.json");
        var set = new PavementReportSet(new[] { report }, new Dictionary<string, string> { ["0:1"] = "widening" }, 25, "1");
        PavementReportSet.Save(path, set);
        var loaded = PavementReportSet.Load(path);
        Assert.Equal(25, loaded.BandHalfWidthMetres);
        Assert.Equal("widening", loaded.VariantOf(0, "1"));
        Assert.Equal("1", loaded.FallbackFamily);
        Assert.Equal(7, loaded.Reports[0].Content.Structures[0].Layers[1].ThicknessCm);
        Assert.False(PavementReportSet.Load(Path.Combine(_dir, "none.json")).HasReport);
    }

    [Fact]
    public void A_refusal_carries_the_servers_own_message_to_the_popup()
    {
        var ex = Assert.Throws<PavementReportException>(() => PavementReportClient.Parse((HttpStatusCode)422,
            """{"error": "not_pavement_report", "message": "הקובץ אינו דו״ח מבנה מיסעה"}""", "a.pdf", new byte[1]));
        Assert.Equal("הקובץ אינו דו״ח מבנה מיסעה", ex.Message);
        Assert.Equal("not_pavement_report", ex.Code);
        var old = Assert.Throws<PavementReportException>(() => PavementReportClient.Parse(HttpStatusCode.NotFound, """{"detail": "Not Found"}""", "a.pdf", new byte[1]));
        Assert.Contains("עדכון שרת", old.Message);
        Assert.Throws<PavementReportException>(() => PavementReportClient.Parse(HttpStatusCode.BadGateway, "<html>", "a.pdf", new byte[1]));
    }

    [Fact]
    public void A_word_document_is_refused_before_anything_is_sent()
    {
        Assert.Null(PavementReportClient.MimeType("report.docx"));
        Assert.Equal("application/pdf", PavementReportClient.MimeType("R.PDF"));
        var ex = Assert.ThrowsAsync<PavementReportException>(() => PavementReportClient.ReadAsync(Path.Combine(_dir, "r.docx"), default)).Result;
        Assert.Contains("PDF", ex.Message);
    }
}
