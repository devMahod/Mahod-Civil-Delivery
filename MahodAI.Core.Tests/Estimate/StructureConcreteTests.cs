using System;
using System.IO;
using System.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>Concrete from the plan (task parked 2026-11-06): walls, footings, piles, beams, slabs and columns get concrete m³,
/// lean concrete, pile metres and rebar tons; sections not typed in are typical values, marked as defaults everywhere.</summary>
public sealed class StructureConcreteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcd-concrete-" + Guid.NewGuid().ToString("N"));
    public StructureConcreteTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private static InfrastructureGroup G(string layer, double length = 0, double area = 0, int count = 1, int closed = 0, double units = 0, string part = "") => new()
    {
        Source = @"C:\x\ST-RW05.dwg", Layer = layer, Topic = "קירות ומבנים", Count = count, LengthMetres = length, AreaSquareMetres = area,
        ClosedCount = closed, CountUnits = units, GroupPart = part, Evidence = "", MeasurementUnit = units > 0 ? "יח׳" : "מטר",
    };

    [Theory]
    [InlineData("S-WALL-1", "", "RetainingWall")]
    [InlineData("S-WALL-D", "", "RetainingWall")]
    [InlineData("HW-WALL", "", "RetainingWall")]
    [InlineData("ST-WALL", "", "RetainingWall")]
    [InlineData("S-FOUND-D", "", "Footing")]
    [InlineData("S-PILE-D", "", "Pile")]
    [InlineData("ST-PILE-HD", "", "Pile")]
    [InlineData("S-BLOCKS", "בלוק pile 60-pl", "Pile")]
    [InlineData("ST-COLN-1", "בלוק COLN-200", "Column")]
    [InlineData("ST-DECK", "", "Slab")]
    [InlineData("S-HELP-2", "", "None")]
    [InlineData("S-NOTE-1-FREEZE", "", "None")]
    [InlineData("S-HATCH-3", "", "None")]
    [InlineData("S-BLOCKS", "בלוק EL-ALL-FOUNDS-PL", "None")]
    [InlineData("A-WALL-SECT-3", "", "None")]
    [InlineData("HW-WALL-TX", "", "None")]
    [InlineData("HW-ROAD-HA", "", "None")]
    [InlineData("DR-PIPE", "", "None")]
    public void Detects_concrete_elements_from_the_firm_layers(string layer, string part, string kind) =>
        Assert.Equal(Enum.Parse<ConcreteKind>(kind), StructureConcrete.Detect(G(layer, 10, part: part, units: part.Length > 0 ? 1 : 0)));

    [Fact]
    public void A_wall_line_with_defaults_gets_stem_footing_lean_and_rebar_all_marked()
    {
        var e = StructureConcrete.Compute(G("S-WALL-1", length: 100), ConcreteKind.RetainingWall, new ConcreteSizes(), useDefaults: true, separateFooting: false);
        var q = e.Quantities.ToDictionary(x => x.Name, x => x.Amount);
        Assert.Equal(90, q["גזע קיר תומך"], 6);                 // 100 × 3.0 × 0.30
        Assert.Equal(72, q["רגל קיר תומך"], 6);                  // 100 × 1.8 (0.6H) × 0.40 (H/10, at least 40)
        Assert.Equal(9, q["בטון רזה מתחת לרגל"], 6);              // 100 × 1.8 × 0.05
        Assert.Equal(162 * 0.110, q["זיון קיר תומך"], 6);         // 110 kg/m³
        Assert.True(e.UsesDefaults);
        Assert.Equal(StructureConcrete.DefaultMark, e.Origin);
        var rows = StructureConcrete.DerivedGroups(new StructureConcrete.Plan(new[] { e }));
        Assert.All(rows, r => Assert.Contains(StructureConcrete.DefaultMark, r.Layer));
        Assert.All(rows, r => Assert.Contains("ערך טיפוסי", r.Evidence));
        Assert.Equal(162 * 0.110, rows.Single(r => r.MeasurementUnit == "טון").Quantity, 6);
    }

    [Fact]
    public void Typed_sections_replace_defaults_and_a_separate_footing_layer_drops_the_walls_own()
    {
        var typed = new ConcreteSizes { HeightM = 4, ThicknessCm = 40, LeanCm = 0, RebarKgPerM3 = 100 };
        var e = StructureConcrete.Compute(G("S-WALL-1", length: 50), ConcreteKind.RetainingWall, typed, useDefaults: true, separateFooting: true);
        Assert.Equal(80, e.ConcreteM3, 6);                        // 50 × 4 × 0.40, no footing of its own
        Assert.DoesNotContain(e.Quantities, x => x.Name.Contains("רגל"));
        Assert.False(e.UsesDefaults);
        Assert.Contains("יסוד בשכבה נפרדת", e.Section);
        Assert.Equal(8, e.RebarTon, 6);
    }

    [Fact]
    public void Defaults_off_leaves_an_element_without_a_section_unpriced()
    {
        var e = StructureConcrete.Compute(G("S-WALL-1", length: 100), ConcreteKind.RetainingWall, new ConcreteSizes(), useDefaults: false, separateFooting: false);
        Assert.True(e.Unpriced);
        Assert.Empty(e.Quantities);
        var plan = StructureConcrete.Build(new[] { G("S-WALL-1", length: 100) }, new StructureConcrete.Settings { UseDefaults = false });
        Assert.Contains(plan.Notes, n => n.Contains("לא תומחרו"));
        StructureConcrete.MarkPricedByConcrete(plan.Elements.Select(x => x.Group), plan);
        Assert.False(plan.Elements[0].Group.PricedByConcrete);    // still its own (unpriced) row, never silently dropped
        // The section typed but no rebar ratio: the concrete is priced, only the rebar row is missing (and said so).
        var typed = StructureConcrete.Compute(G("S-WALL-1", length: 100), ConcreteKind.RetainingWall,
            new ConcreteSizes { HeightM = 3, ThicknessCm = 30, FootingWidthM = 0 }, useDefaults: false, separateFooting: false);
        Assert.False(typed.Unpriced);
        Assert.True(typed.RebarMissing);
        Assert.Equal(90, typed.ConcreteM3, 6);
        Assert.DoesNotContain(typed.Quantities, q => q.Unit == "טון");
        Assert.Equal("ללא זיון", typed.Origin);
    }

    [Fact]
    public void A_closed_wall_outline_gives_the_axis_and_the_thickness()
    {
        // A 20 m × 0.35 m wall drawn as a closed outline: perimeter 40.7, area 7.
        var e = StructureConcrete.Compute(G("S-WALL-D", length: 40.7, area: 7, closed: 1), ConcreteKind.RetainingWall, new ConcreteSizes(), true, true);
        var t = e.Fields.Single(f => f.Name == "עובי גזע");
        Assert.Equal("מהשרטוט", t.Origin);
        Assert.Equal(35, t.Value, 0);
        Assert.Equal(20, e.BasisValue, 1);
    }

    [Fact]
    public void A_wall_drawn_as_two_faces_is_measured_on_its_axis_with_the_drawings_thickness()
    {
        // ST-RW05: RW-05 = two 34.4 m faces 0.34 m apart, closed by two 0.34 m caps — 69.5 m of lines, one 34.4 m wall.
        var faces = FacePairs.Measure(new[]
        {
            new FacePairs.Segment(0, 0, 20, 0), new FacePairs.Segment(20, 0, 34.43, 4), new FacePairs.Segment(0, 0.34, 20, 0.34), new FacePairs.Segment(20, 0.34, 34.42, 4.34),
            new FacePairs.Segment(0, 0, 0, 0.34), new FacePairs.Segment(34.43, 4, 34.42, 4.34),
        }, 0.1, 1.5);
        Assert.True(faces.IsFaces);
        Assert.Equal(0.34, faces.WidthM, 2);
        Assert.InRange(faces.AxisM, 34.6, 35.2);                 // the two faces' mean, caps not counted
        var wall = G("S-WALL-1", length: faces.DrawnM, count: 6); wall.Faces = faces;
        var e = StructureConcrete.Compute(wall, ConcreteKind.RetainingWall, new ConcreteSizes(), true, true);
        Assert.Equal(faces.AxisM, e.BasisValue, 6);
        Assert.Equal("מהשרטוט", e.Fields.Single(f => f.Name == "עובי גזע").Origin);
        Assert.Equal(34, e.Fields.Single(f => f.Name == "עובי גזע").Value);
        // A single line is not a pair: its length stays the axis.
        Assert.False(FacePairs.Measure(new[] { new FacePairs.Segment(0, 0, 30, 0), new FacePairs.Segment(30, 0, 30, 10) }, 0.1, 1.5).IsFaces);
    }

    [Fact]
    public void A_footing_drawn_as_its_two_edges_gives_its_area()
    {
        var faces = FacePairs.Measure(new[] { new FacePairs.Segment(0, 0, 40, 0), new FacePairs.Segment(0, 2, 40, 2) }, 0.1, 6);
        var footing = G("S-FOUND-D", length: 80, count: 2); footing.Faces = faces;
        var e = StructureConcrete.Compute(footing, ConcreteKind.Footing, new ConcreteSizes(), true, false);
        Assert.Equal(80, e.BasisValue, 6);                         // 40 m axis × 2 m width
        Assert.Equal(40, e.ConcreteM3, 6);                         // × 0.50 m default depth
    }

    [Fact]
    public void Piles_count_by_block_and_diameter_by_block_name_or_by_line_and_spacing()
    {
        var blocks = StructureConcrete.Compute(G("S-BLOCKS", units: 14, count: 14, part: "בלוק pile 60-pl"), ConcreteKind.Pile, new ConcreteSizes(), true, false);
        Assert.Equal(14, blocks.BasisValue);
        Assert.Equal("מהשרטוט", blocks.Fields.Single(f => f.Name == "קוטר").Origin);
        var pile = blocks.Quantities.Single(q => q.Unit == "מטר");
        Assert.Equal(14 * 12, pile.Amount, 6);
        Assert.Equal(14 * 12 * Math.PI * 0.09 * 0.120, blocks.RebarTon, 6);   // π·0.3² m² × 120 kg/m³
        var line = StructureConcrete.Compute(G("S-PILE-D", length: 35.61), ConcreteKind.Pile, new ConcreteSizes(), true, false);
        Assert.Equal(15, line.BasisValue);                          // ⌊35.61 / 2.5⌋ + 1
        Assert.Contains(line.Fields, f => f.Name == "מרווח" && f.IsDefault);
    }

    [Theory]
    [InlineData(60, 8, "23.01.0072")]
    [InlineData(60, 12, "23.01.0073")]
    [InlineData(60, 18, "23.01.0075")]
    [InlineData(55, 12, "23.01.0073")]     // rounded up to the next priced diameter
    [InlineData(70, 12, "23.01.0092")]
    [InlineData(80, 18, "23.01.0113")]
    [InlineData(100, 25, "23.01.0153")]
    [InlineData(100, 35, "23.01.0155")]
    [InlineData(100, 45, "23.01.0157")]
    [InlineData(40, 12, "23.01.0032")]
    public void Each_pile_finds_its_NTI_diameter_and_depth_band(double diameter, double length, string code)
    {
        var search = Search();
        var row = Row(StructureConcrete.PileItem(diameter, length).Hint, "מטר");
        Assert.Equal(code, search.Find(row).First(o => o.IsRecommended).Code);
    }

    [Theory]
    [InlineData("^קירות תומכים מבטון", "מ״ק", "02.01.0661")]
    [InlineData("^יסודות עוברים ורגלי קירות תומכים", "מ״ק", "02.01.0041")]
    [InlineData("^בטון רזה יצוק מתחת לאלמנטים", "מ״ק", "02.01.0010")]
    [InlineData("^מוטות פלדה מצולעים רתיכים", "טון", "02.01.0824")]
    [InlineData("^קורות ראש מבטון · של כלונסאות", "מ״ק", "02.01.0086")]
    [InlineData("^מרצפי בטון ומשטחי בטון", "מ״ק", "02.01.0109")]
    public void Each_concrete_row_finds_its_NTI_item(string hint, string unit, string code) =>
        Assert.Equal(code, Search().Find(Row(hint, unit)).First(o => o.IsRecommended).Code);

    [Theory]
    [InlineData(60, "02.01.0141")]
    [InlineData(100, "02.01.0151")]
    [InlineData(130, "02.01.0161")]
    public void Round_columns_find_their_diameter_band(double diameter, string code) =>
        Assert.Equal(code, Search().Find(Row(StructureConcrete.ColumnHint(diameter), "מ״ק")).First(o => o.IsRecommended).Code);

    [Fact]
    public void Every_derived_row_of_a_full_plan_is_priced_in_the_NTI_book()
    {
        var groups = new[]
        {
            G("S-WALL-1", length: 212.55), G("S-FOUND-D", length: 40.9), G("S-PILE-D", length: 35.61),
            G("S-BLOCKS", units: 14, count: 14, part: "בלוק pile 60-pl"), G("ST-HEAD-BEAM", length: 30), G("ST-DECK", area: 120, closed: 1),
            G("ST-COLN-1", units: 4, count: 4, part: "בלוק COLN-80"),
        };
        var plan = StructureConcrete.Build(groups, new StructureConcrete.Settings());
        Assert.Equal(7, plan.Elements.Count);
        var search = Search();
        foreach (var row in StructureConcrete.DerivedGroups(plan))
        {
            var item = search.Find(row).FirstOrDefault(o => o.IsRecommended);
            Assert.True(item is { Price: > 0 }, row.Layer + " → " + (item?.Code ?? "none"));
            row.Assign(Catalog(), item!.Code);
            Assert.True(row.Total > 0, row.Layer);
        }
        StructureConcrete.MarkPricedByConcrete(groups, plan);
        Assert.All(groups, g => Assert.False(g.IsPayItem));       // the lines themselves are not priced a second time
        Assert.Equal("לפי חישוב בטון", groups[0].MappingDisplay);
        Assert.Contains(plan.Notes, n => n.Contains(StructureConcrete.DefaultMark));
    }

    [Fact]
    public void Settings_survive_a_round_trip_and_a_kind_correction_sticks()
    {
        var path = Path.Combine(_dir, "x.concrete.json");
        var wall = G("S-WALL-1", length: 10); var odd = G("ST-CONC-1", length: 5);
        var s = new StructureConcrete.Settings { UseDefaults = false };
        s.SetSizes(wall, new ConcreteSizes { HeightM = 2.5 });
        s.SetKind(odd, ConcreteKind.HeadBeam);
        StructureConcrete.Save(path, s);
        var back = StructureConcrete.Load(path);
        Assert.False(back.UseDefaults);
        Assert.Equal(2.5, back.SizesFor(wall).HeightM);
        Assert.Equal(ConcreteKind.HeadBeam, back.KindFor(odd));
        Assert.Equal(ConcreteKind.RetainingWall, back.KindFor(wall));
        back.SetSizes(wall, new ConcreteSizes());                   // cleared → back to the drawing's own reading
        Assert.Single(back.Entries);
        Assert.Equal(ConcreteKind.None, StructureConcrete.Load(Path.Combine(_dir, "missing.json")).KindFor(odd));
    }

    private static CatalogSnapshot? _catalog;
    private static CatalogSnapshot Catalog()
    {
        if (_catalog != null) return _catalog;
        var dir = Path.Combine(Path.GetTempPath(), "mcd-concrete-book-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var offer = Assert.Single(BundledPriceBooks.Offers(dir, new MahodAI.CivilDelivery.Shared.ProjectProfile()));
        return _catalog = PriceBookXlsxLoader.Load(offer.Entry.Path, "TEST NTI");
    }
    private static InfrastructureCatalogSearch Search() => new(Catalog());
    private static InfrastructureGroup Row(string hint, string unit) => new()
    {
        Source = StructureConcrete.DerivedSource, Layer = "test", Topic = StructureConcrete.Topic, Count = 1, LengthMetres = unit == "מטר" ? 10 : 0,
        VolumeCubicMetres = unit == "מ״ק" ? 10 : 0, WeightTons = unit == "טון" ? 1 : 0, GroupPart = StructureConcrete.DerivedPart,
        MeasurementUnit = unit, Evidence = "", Element = hint,
    };
}
