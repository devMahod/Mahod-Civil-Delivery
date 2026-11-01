using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class InfrastructureAutoAssignmentTests
{
    private static CatalogSnapshot Catalog(string hash = "TEST BOOK") => new()
    {
        SnapshotId = "TEST ONLY", FileHash = hash,
        Items = new()
        {
            ["57.02.0001"] = new() { Code = "57.02.0001", Description = "צינור מים מפוליאתילן בקוטר 100 מ״מ", UnitRaw = "מטר", ChapterPath = "מים וביוב > מים" },
            ["57.02.0002"] = new() { Code = "57.02.0002", Description = "צינור מים מפוליאתילן בקוטר 200 מ״מ", UnitRaw = "מטר", ChapterPath = "מים וביוב > מים" },
            ["57.03.0001"] = new() { Code = "57.03.0001", Description = "צינור ביוב PVC בקוטר 315 מ״מ", UnitRaw = "מטר", ChapterPath = "מים וביוב > ביוב" },
            ["40.01.0001"] = new() { Code = "40.01.0001", Description = "ריצוף משתלב", UnitRaw = "מ״ר", ChapterPath = "ריצופים" },
            ["41.01.0001"] = new() { Code = "41.01.0001", Description = "עץ לשתילה וגינון", UnitRaw = "יח׳", ChapterPath = "גינון" },
            ["01.01.0001"] = new() { Code = "01.01.0001", Description = "עבודות עפר חפירה", UnitRaw = "מ״ק", ChapterPath = "עבודות עפר" },
            ["51.01.0001"] = new() { Code = "51.01.0001", Description = "סלילת כביש באספלט", UnitRaw = "טון", ChapterPath = "כבישים ואספלט" },
        },
        Prices = new()
        {
            ["57.02.0001"] = new() { Code = "57.02.0001", Price = 10, PriceBookId = "TEST ONLY" },
            ["57.02.0002"] = new() { Code = "57.02.0002", Price = 27, PriceBookId = "TEST ONLY" },
            ["57.03.0001"] = new() { Code = "57.03.0001", Price = 42, PriceBookId = "TEST ONLY" },
        }
    };
    private static InfrastructureGroup Group(string layer = "MAIM-PE-DN200", string topic = "מים", double length = 10) =>
        InfrastructureBoq.Group(new[] { new InfrastructureLine("TEST ONLY.dwg", Guid.NewGuid().ToString(), layer,
            "Polyline", length, false, topic, "TEST ONLY", "TEST") }).Single();

    [Fact]
    public void Manual_badge_and_automatic_assignment_choose_the_same_layer_recommendation()
    {
        var catalog = Catalog(); var search = new InfrastructureCatalogSearch(catalog); var g = Group();
        var options = search.Find(g);
        var recommended = Assert.Single(options, o => o.IsRecommended);
        Assert.Equal("57.02.0002", recommended.Code); Assert.Equal("מומלץ", recommended.RecommendationLabel);
        Assert.True(recommended.MatchesLayerTopic); Assert.Equal(27m, recommended.Price);
        var batch = search.PlanAutomaticAssignment(new[] { g });
        Assert.Equal(1, batch.RecommendedCount); Assert.False(g.Approved); // Planning is read-only.
        Assert.Equal(1, batch.Apply()); Assert.Equal(recommended.Code, g.Code);
        Assert.True(g.AutomaticallyAssigned); Assert.Equal(270m, g.Total);
        Assert.Null(g.ApprovedBy); Assert.Equal("שויך אוטומטית", g.Status);
        Assert.Equal(0, batch.Apply());
    }

    [Fact]
    public void Explicit_search_and_category_do_not_manufacture_a_layer_recommendation()
    {
        var catalog = Catalog(); var search = new InfrastructureCatalogSearch(catalog);
        var sewerResults = search.Find(Group(), "ביוב 315");
        Assert.NotEmpty(sewerResults); Assert.All(sewerResults, o => Assert.False(o.IsRecommended));
        var book = new InfrastructurePriceBook("TEST", "TEST", catalog, new Dictionary<string, string>());
        var category = BoqCatalogCategories.For(book).Single(c => c.Code == "57.02");
        foreach (var g in new[] { Group("DRAWING", InfrastructureBoq.Unknown), Group("SW-DE-315", "ביוב"), Group("MAIM-TEXT", "מים") })
        {
            Assert.NotEmpty(search.Find(g, category: category));
            Assert.All(search.Find(g, category: category), o => Assert.False(o.IsRecommended));
            var batch = search.PlanAutomaticAssignment(new[] { g }, category);
            Assert.Equal(1, batch.UnmatchedCount); Assert.Equal(0, batch.Apply()); Assert.False(g.Approved);
        }
    }

    [Fact]
    public void Batch_preserves_existing_editions_and_only_changes_the_supplied_rows()
    {
        var catalog = Catalog(); var search = new InfrastructureCatalogSearch(catalog);
        var water = Group(); var sewer = Group("BIUV-DN315", "ביוב");
        var existing = Group(); existing.Assign(Catalog("ANOTHER EDITION"), "57.02.0001");
        var hiddenOrExcluded = Group(); var noQuantity = Group(length: 0); var unknown = Group("DRAWING", InfrastructureBoq.Unknown);
        var batch = search.PlanAutomaticAssignment(new[] { water, sewer, existing, noQuantity, unknown, water });
        Assert.Equal(2, batch.RecommendedCount); Assert.Equal(1, batch.ExistingCount);
        Assert.Equal(1, batch.UnmeasuredCount); Assert.Equal(1, batch.UnmatchedCount);
        Assert.Equal(2, batch.Apply()); Assert.Equal("57.03.0001", sewer.Code);
        Assert.Equal("ANOTHER EDITION", existing.CatalogHash); Assert.Equal("57.02.0001", existing.Code);
        Assert.False(existing.AutomaticallyAssigned); Assert.False(hiddenOrExcluded.Approved);
        Assert.False(noQuantity.Approved); Assert.False(unknown.Approved);
    }

    [Fact]
    public void Automatic_assignment_respects_the_chosen_catalogue_category()
    {
        var catalog = Catalog(); var book = new InfrastructurePriceBook("TEST", "TEST", catalog, new Dictionary<string, string>());
        var category = BoqCatalogCategories.For(book).Single(c => c.Code == "57.03");
        var water = Group(); var sewer = Group("BIUV-DN315", "ביוב");
        var batch = new InfrastructureCatalogSearch(book).PlanAutomaticAssignment(new[] { water, sewer }, category);
        Assert.Equal(1, batch.Apply()); Assert.False(water.Approved); Assert.Equal("57.03.0001", sewer.Code);
    }

    [Fact]
    public void Planning_does_not_overwrite_a_manual_decision_or_changed_measurement()
    {
        var catalog = Catalog(); var manual = Group(); var changed = Group(); var movedTopic = Group();
        var batch = new InfrastructureCatalogSearch(catalog).PlanAutomaticAssignment(new[] { manual, changed, movedTopic });
        manual.Assign(catalog, "57.02.0001"); changed.ManualQuantity = 25; movedTopic.Topic = "ביוב";
        Assert.Equal(0, batch.Apply()); Assert.Equal("57.02.0001", manual.Code);
        Assert.False(manual.AutomaticallyAssigned); Assert.False(changed.Approved); Assert.False(movedTopic.Approved);
    }

    [Fact]
    public void Undo_only_withdraws_this_batch_and_preserves_later_manual_choices()
    {
        var catalog = Catalog(); var first = Group(); var second = Group();
        var batch = new InfrastructureCatalogSearch(catalog).PlanAutomaticAssignment(new[] { first, second });
        Assert.Equal(2, batch.Apply()); second.Assign(catalog, "57.02.0002"); // Same code, now explicitly selected manually.
        Assert.Equal(1, batch.Undo()); Assert.False(first.Approved);
        Assert.True(second.Approved); Assert.False(second.AutomaticallyAssigned); Assert.Equal(270m, second.Total);
        Assert.Equal(0, batch.Undo()); Assert.Equal(0, batch.Apply());
    }

    [Fact]
    public void Undo_does_not_remove_the_same_code_assigned_by_a_later_batch()
    {
        var search = new InfrastructureCatalogSearch(Catalog()); var g = Group();
        var first = search.PlanAutomaticAssignment(new[] { g }); first.Apply(); g.Withdraw();
        var later = search.PlanAutomaticAssignment(new[] { g }); later.Apply();
        Assert.Equal(0, first.Undo()); Assert.True(g.AutomaticallyAssigned); Assert.Equal("57.02.0002", g.Code);
        Assert.Equal(1, later.Undo()); Assert.False(g.Approved);
    }

    [Theory]
    [InlineData("ריצופים", "מ״ר", "40.01.0001")]
    [InlineData("גינון", "יח׳", "41.01.0001")]
    [InlineData("עבודות עפר", "מ״ק", "01.01.0001")]
    [InlineData("כבישים ואספלט", "טון", "51.01.0001")]
    public void Other_domains_keep_their_own_quantity_units(string topic, string unit, string code)
    {
        var g = new InfrastructureGroup { Source = "TEST", Layer = "TEST", Topic = topic, Count = 1,
            LengthMetres = 900, AreaSquareMetres = 12, VolumeCubicMetres = 8, CountUnits = 3, Evidence = "TEST", MeasurementUnit = unit };
        var search = new InfrastructureCatalogSearch(Catalog());
        if (unit == "טון")
        {
            Assert.Equal(0, search.PlanAutomaticAssignment(new[] { g }).Apply());
            g.ManualQuantity = 4;
        }
        Assert.Equal(1, search.PlanAutomaticAssignment(new[] { g }).Apply()); Assert.Equal(code, g.Code);
        Assert.True(Units.Parse(unit).SameUnit(Catalog().Items[g.Code!].Unit));
        Assert.NotEqual(900, g.Quantity); // An area, count or mass never inherits the line length.
    }

    [Fact]
    public void Automatic_origin_survives_reopening_and_is_written_with_catalogue_prices_to_excel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcd-auto-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            var catalog = Catalog(); var g = Group();
            new InfrastructureCatalogSearch(catalog).PlanAutomaticAssignment(new[] { g }).Apply();
            var path = Path.Combine(dir, "review.json");
            InfrastructureReviewStore.Save(path, "DRAWING", catalog, new[] { g }, new HashSet<InfrastructureGroup>());
            var restored = Group();
            Assert.Equal(1, InfrastructureReviewStore.Restore(path, "DRAWING", catalog, new[] { restored }, new HashSet<InfrastructureGroup>()));
            Assert.True(restored.AutomaticallyAssigned); Assert.Equal(270m, restored.Total);
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            foreach (var choice in legacy["Choices"]!.AsArray()) choice!.AsObject().Remove("AutomaticallyAssigned");
            File.WriteAllText(path, legacy.ToJsonString());
            var legacyRestored = Group();
            Assert.Equal(1, InfrastructureReviewStore.Restore(path, "DRAWING", catalog, new[] { legacyRestored }, new HashSet<InfrastructureGroup>()));
            Assert.False(legacyRestored.AutomaticallyAssigned); Assert.Equal(270m, legacyRestored.Total);
            var excel = Path.Combine(dir, "output.xlsx");
            InfrastructureBoq.Export(excel, new[] { restored }, Array.Empty<InfrastructureLine>(), catalog, Array.Empty<string>(), "TEST", "DRAWING", "TEST");
            using var zip = ZipFile.OpenRead(excel); using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var xml = XDocument.Load(stream).ToString();
            Assert.Contains("שויך אוטומטית", xml); Assert.Contains("F6*G6", xml); Assert.Contains(">270<", xml);
            restored.Assign(catalog, "57.02.0001"); Assert.False(restored.AutomaticallyAssigned);
            restored.Withdraw(); Assert.Equal("טרם שויך", restored.Status);
        }
        finally { Directory.Delete(dir, true); }
    }
}
