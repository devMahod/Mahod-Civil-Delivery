using System.IO;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// N1 (Codex 01:27, 02/10): the landscape library. Neutral global factor 1, no roads wording, every candidate item learned
/// from the project-293 landscape BoQ held back (not in the total) until an engineer includes it, trees / fence / preserved
/// areas without an item, m² → dunam only as the exact declared conversion, and a missing price kept missing.
/// Records, items and prices are SYNTHETIC.
/// </summary>
public sealed class LandscapeLibraryTests
{
    private static int _handle;

    private static NeutralQuantityRecord Rec(string layer, string kind, string method, double value, string unit, string? block = null)
    {
        var parameters = new Dictionary<string, string>();
        if (block != null) parameters["cad_block_name_effective"] = block;
        var handle = "L" + System.Threading.Interlocked.Increment(ref _handle).ToString("X");
        return new NeutralQuantityRecord
        {
            RecordId = $"q-{handle}",
            ProjectProfileId = "SYNTHETIC",
            RunId = "SYNTHETIC-RUN",
            Source = new QuantitySource
            {
                Drawing = "synthetic-landscape.dwg", DrawingHash = new string('c', 64), Handle = handle, EntityType = "X", Layer = layer,
            },
            Measurement = new QuantityMeasurement { Kind = kind, Method = method, RawValue = value, Unit = unit, Parameters = parameters },
            Classification = new QuantityClassification(),
        };
    }

    private static CatalogSnapshot Catalog()
    {
        var catalog = new CatalogSnapshot { SnapshotId = "SYNTHETIC", FileHash = new string('d', 64) };
        void Add(string code, string description, string unit, decimal? price)
        {
            catalog.Items[code] = new CatalogItem { Code = code, Description = description, UnitRaw = unit };
            catalog.Prices[code] = new PriceRecord { Code = code, Price = price, PriceBookId = "SYNTHETIC" };
        }
        Add("40.01.0010", "SYNTHETIC soil cover", "מ\"ר", 4m);
        Add("41.01.0020", "SYNTHETIC garden levelling", "מ\"ר", null); // no price in the edition: stays missing
        Add("41.01.5110", "SYNTHETIC weed control", "דונם", 100m);
        Add("51.06.0030", "SYNTHETIC garden curb", "מטר", 81m);
        return catalog;
    }

    private static readonly EngineerDraftContext Context =
        new("SYNTHETIC landscape", "SYNTHETIC", "SYNTHETIC-RUN", "synthetic-landscape.dwg", "SYNTHETIC catalog", Array.Empty<string>());

    private static EngineerBoqDraft Build(IReadOnlyList<NeutralQuantityRecord> records, EngineerBoqLibrary? library = null) =>
        EngineerBoqDraftBuilder.Build(records, Array.Empty<DeliveryFinding>(), Catalog(),
            new Dictionary<string, string> { ["40"] = "40 — פיתוח נופי", ["41"] = "41 — גינון והשקיה", ["51"] = "51 — סלילה" },
            library ?? EngineerBoqLibrary.LandscapeV1, Context);

    private static NeutralQuantityRecord[] Landscape() => new[]
    {
        Rec("La-hatch-SP", "area", "hatch-area", 6000, "מ\"ר"),
        Rec("La-hatch-SP", "area", "hatch-area", 4000, "מ\"ר"),
        Rec("La-hatch-save", "area", "hatch-area", 700, "מ\"ר"),
        Rec("la-garden-curve", "length", "polyline-length", 200, "מטר"),
        Rec("LA-FENC-TEMP", "length", "polyline-length", 50, "מטר"),
        Rec("LA-TREE-Ella", "count", "block-count", 1, "יח'", "BL-TREE-ELLA"),
        Rec("LA-TREE-Ella", "count", "block-count", 1, "יח'", "BL-TREE-ELLA"),
        Rec("LA-TREE-RPL", "count", "block-count", 1, "יח'", "TreeKayam"),
        Rec("La-help", "length", "line-length", 30, "מטר"),
        Rec("la-text-1", "length", "line-length", 12, "מטר"),
    };

    [Fact]
    public void TheLibraryIsNeutralAndHoldsEveryCandidateBack()
    {
        var library = EngineerBoqLibrary.LandscapeV1;
        library.Parameters.Should().ContainSingle().Which.Should().Match<DraftParameter>(p =>
            p.Key == EngineerBoqLibrary.GlobalFactorKey && p.DefaultValue == 1.0);
        library.Texts.ReferenceGlobalFactor.Should().BeNull("the roads 0.9 reference does not apply to landscape");
        library.Texts.Structure.Should().NotContain("כביש");
        foreach (var rule in library.Rules)
        {
            rule.Confidence.Should().Be(DraftConfidence.Decision, rule.Id);
            if (rule.Emits.Count > 0)
            {
                rule.IncludedByDefault.Should().BeFalse($"{rule.Id}: a candidate learned from one project is not an approval");
                rule.HeldBackNote.Should().NotBeNullOrWhiteSpace(rule.Id);
            }
        }
        library.Rules.Where(r => r.Id is "la-trees-rpl" or "la-trees-ella" or "la-fence" or "la-preserve-area")
            .Should().HaveCount(4).And.OnlyContain(r => r.Emits.Count == 0, "no item until a decision / a typed specification");
        EngineerBoqLibrary.Lineage(library.Id).Should().NotBe(EngineerBoqLibrary.Lineage(EngineerBoqLibrary.RoadsV1.Id));
        EngineerBoqLibrary.Lineage("mahod-roads-nti-urban-v1.4").Should().Be("mahod-roads-nti-urban");
        EngineerBoqLibrary.Lineage("mahod-roads-nti-urban-v1.3").Should().Be("mahod-roads-nti-urban");
    }

    [Fact]
    public void ALandscapeDraftPricesNothingUntilAnEngineerIncludesIt()
    {
        var draft = Build(Landscape());
        draft.Library.Should().BeSameAs(EngineerBoqLibrary.LandscapeV1);
        var rows = EngineerBoqDraftExcelWriter.BoqRows(draft);
        var parameter = EngineerBoqDraftExcelWriter.DefaultParameter(draft);

        EngineerBoqDraftExcelWriter.TotalAt(draft, rows, parameter, null).Should().Be(0m, "every candidate row is held back");
        var planting = draft.Elements.Single(e => e.Rule.Id == "la-planting-area");
        planting.Sources.Should().OnlyContain(s => !s.Included && s.Note.Contains("לא אושר הנדסית"));

        // Included by the engineer: 10,000 m² × 4 + 10 dunam × 100; levelling has no price in the edition and stays out.
        var include = new HashSet<string>(StringComparer.Ordinal) { "la-planting-area" };
        EngineerBoqDraftExcelWriter.TotalAt(draft, rows, parameter, include).Should().Be(41_000m);
        var weed = rows.Single(r => r.Head.Emit.Code == "41.01.5110");
        weed.Lines.Should().OnlyContain(l => !l.UnitMismatch);
        EngineerBoqDraftExcelWriter.RowQuantity(weed, parameter, include).Should().BeApproximately(10, 1e-9);
        rows.Single(r => r.Head.Emit.Code == "41.01.0020").Price.Should().BeNull("a missing price stays missing, never 0 or copied");

        draft.Elements.Where(e => e.Rule.Id is "la-trees-ella" or "la-trees-rpl" or "la-fence" or "la-preserve-area")
            .Should().HaveCount(4).And.OnlyContain(e => e.Rule.Emits.Count == 0);
        draft.Lines.Should().NotContain(l => l.Element.Rule.Id.StartsWith("la-trees", StringComparison.Ordinal));
        draft.UnmappedDesign.Should().Contain(g => g.Layer == "La-help", "a helper layer with measured objects stays visible");
        draft.DraftingAids.Should().Contain(g => g.Layer == "la-text-1");
        draft.AccountedRecords.Should().Be(Landscape().Length);
    }

    [Fact]
    public void ALandscapeWorkbookCarriesNoRoadsWording_TheRoadsWorkbookKeepsItsOwn()
    {
        var landscape = WorkbookText(Build(Landscape()));
        landscape.Should().Contain("מבנה 01 — פיתוח נופי וגינון").And.Contain("ספריית הנוף");
        foreach (var roads in new[] { "כביש", "מיסעה", "נת\"צ", "מקדם 0.9", "סימון לפי רוחב" })
            landscape.Should().NotContain(roads);

        var roadsDraft = EngineerBoqDraftBuilder.Build(new[] { Rec("HW-HTCH-ROAD", "area", "hatch-area", 100, "מ\"ר") },
            Array.Empty<DeliveryFinding>(), Catalog(), new Dictionary<string, string>(), EngineerBoqLibrary.RoadsV1, Context);
        WorkbookText(roadsDraft).Should().Contain("מבנה 01 — כבישים ותנועה").And.Contain("ספריית הכבישים")
            .And.Contain("מקדם 0.9 על כמויות מדודות (אורך, שטח, נפח)");
    }

    [Theory]
    [InlineData("area", "hatch-area", "מ\"ר", 0.001, false)]  // the declared conversion
    [InlineData("area", "hatch-area", "מ\"ר", 0.002, true)]   // another factor is not m² → dunam
    [InlineData("area", "hatch-area", "מ\"ר", 1.0, true)]     // no conversion declared
    [InlineData("length", "polyline-length", "מטר", 0.001, true)] // a length is never an area in dunam
    [InlineData("area", "hatch-area", "דונם", 0.001, true)]  // a dunam source never converts again (Codex 02:29)
    [InlineData("area", "hatch-area", "דונם", 1.0, true)]    // a dunam measurement is not a declared source at all
    public void AreaToDunamIsOnlyTheExactDeclaredConversion(string kind, string method, string unit, double factor, bool mismatch)
    {
        var rule = new DraftRule("synthetic-dunam", "SYNTHETIC", new[] { "SYN-LAYER" },
            kind == "area" ? DraftQuantityBasis.HatchArea : DraftQuantityBasis.OpenLength, DraftConfidence.Decision,
            new[] { new DraftEmit("41.01.5110", Factor: factor) }, "SYNTHETIC");
        var source = EngineerBoqLibrary.LandscapeV1;
        var library = new EngineerBoqLibrary
        {
            Id = source.Id, Title = source.Title, Basis = source.Basis, Parameters = source.Parameters, Rules = new[] { rule },
            ScopeItems = source.ScopeItems, SurveySourcePatterns = source.SurveySourcePatterns, UtilitySourcePatterns = source.UtilitySourcePatterns,
            DraftingAidLayerPatterns = source.DraftingAidLayerPatterns, ExistingLayerPatterns = source.ExistingLayerPatterns,
            WidthClassifiedLayerPatterns = source.WidthClassifiedLayerPatterns, WidthParameterKeys = source.WidthParameterKeys, Texts = source.Texts,
        };
        var draft = Build(new[] { Rec("SYN-LAYER", kind, method, 5000, unit) }, library);
        draft.Lines.Should().ContainSingle().Which.UnitMismatch.Should().Be(mismatch);
        var rows = EngineerBoqDraftExcelWriter.BoqRows(draft);
        // A mismatched row never reaches the total, whatever its price; the declared conversion prices 5 dunam × 100.
        EngineerBoqDraftExcelWriter.TotalAt(draft, rows, EngineerBoqDraftExcelWriter.DefaultParameter(draft), null)
            .Should().Be(mismatch ? 0m : 500m);
    }

    [Fact]
    public void TheProfileDeclarationSelectsTheLibrary()
    {
        var profile = new ProjectProfile { ProfileId = "X" };
        EngineerBoqLibrary.For(profile).Should().BeSameAs(EngineerBoqLibrary.RoadsV1);
        profile.Estimate.Discipline = "landscape";
        EngineerBoqLibrary.For(profile).Should().BeSameAs(EngineerBoqLibrary.LandscapeV1);
        profile.Estimate.Discipline = "roads";
        EngineerBoqLibrary.For(profile).Should().BeSameAs(EngineerBoqLibrary.RoadsV1);
    }

    private static string WorkbookText(EngineerBoqDraft draft)
    {
        var path = Path.Combine(Path.GetTempPath(), "mhd-landscape-draft-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            EngineerBoqDraftExcelWriter.Write(draft, path);
            using var zip = ZipFile.OpenRead(path);
            var text = new StringBuilder();
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal)))
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                text.Append(System.Net.WebUtility.HtmlDecode(reader.ReadToEnd()).Replace("‎", string.Empty));
            }
            return text.ToString();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
