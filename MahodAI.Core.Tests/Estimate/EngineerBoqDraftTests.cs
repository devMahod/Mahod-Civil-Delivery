using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// The engineer draft turns measured design layers into an editable, unapproved
/// BoQ workbook. Records and prices below are SYNTHETIC unless the opt-in real-data
/// test is enabled with MHD_ENGINEER_DRAFT_REAL_RUN_DIR.
/// </summary>
public sealed class EngineerBoqDraftTests
{
    private const string Gm = "6422-GM-MODEL-NATAZ";
    private const string Ha = "6422-HA-MODEL-NATAZ";
    private const string Sm = "6422-SM-MODEL-NATAZ";
    private const string Survey = "6422-SP-MEDVA-ALL-2026-MHD";
    private const string Utilities = "UT-3D";
    private static int _handle;

    private static NeutralQuantityRecord Rec(string? xref, string layer, string kind, string method, double value,
        string unit, string? block = null, QuantityClassification? classification = null,
        double? width = null, string? linetype = null, string units = "Meters", string? xrefTransform = null)
    {
        var parameters = new Dictionary<string, string>();
        if (xrefTransform != null)
        {
            parameters[XrefWidthPolicy.TransformKey] = xrefTransform;
            parameters[XrefWidthPolicy.TransformKey + "_status"] = "read";
        }
        if (block != null) parameters["cad_block_name_effective"] = block;
        if (width != null)
        {
            parameters["cad_polyline_constant_width_raw"] = width.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            parameters["cad_entity_database_insunits"] = units;
        }
        if (linetype != null)
        {
            parameters["cad_entity_linetype"] = "ByLayer";
            parameters["cad_layer_linetype"] = $"{xref}|{linetype}";
        }
        var handle = System.Threading.Interlocked.Increment(ref _handle).ToString("X");
        var hosted = xref == null || xref == "(host)";
        return new NeutralQuantityRecord
        {
            RecordId = $"q-{handle}",
            ProjectProfileId = "SYNTHETIC",
            RunId = "SYNTHETIC-RUN",
            Source = new QuantitySource
            {
                Drawing = "synthetic.dwg", DrawingHash = new string('a', 64), Handle = handle, EntityType = "X",
                Layer = hosted ? layer : $"{xref}|{layer}", Xref = xref,
            },
            Measurement = new QuantityMeasurement { Kind = kind, Method = method, RawValue = value, Unit = unit, Parameters = parameters },
            Classification = classification ?? new QuantityClassification(),
        };
    }

    /// <summary>ev_xref_transform JSON for a chain of one insert with scale (sx, sy, sz), rotated 30° and translated.</summary>
    private static string Transform(double sx, double sy, double sz, string? declaredClass = null, string sourceUnits = "Meters",
        string chain = Sm)
    {
        var (c, n) = (Math.Cos(Math.PI / 6), Math.Sin(Math.PI / 6));
        var m = new[] { c * sx, -n * sy, 0, 1000.5, n * sx, c * sy, 0, -20, 0, 0, sz, 3, 0, 0, 0, 1 };
        var matrix = string.Join(",", m.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        var cls = declaredClass == null ? string.Empty : $",\"class\":\"{declaredClass}\"";
        return $"{{\"space\":\"host\",\"chain\":[{{\"xref\":\"{chain}\",\"matrix\":[{matrix}]}}]{cls}," +
               $"\"units\":{{\"source\":\"{sourceUnits}\",\"host\":\"Meters\"}}}}";
    }

    private static readonly string Proven = Transform(1, 1, 1, "rigid");

    private static CatalogSnapshot Catalog()
    {
        var catalog = new CatalogSnapshot { SnapshotId = "SYNTHETIC", FileHash = new string('b', 64) };
        void Add(string code, string description, string unit, decimal? price)
        {
            catalog.Items[code] = new CatalogItem { Code = code, Description = description, UnitRaw = unit };
            catalog.Prices[code] = new PriceRecord { Code = code, Price = price, PriceBookId = "SYNTHETIC" };
        }
        foreach (var code in new[] { "U51.04.1820", "U51.04.0130", "U51.04.0160", "U51.04.2400", "U51.04.2410", "U51.04.2420", "U51.02.0110" })
            Add(code, "SYNTHETIC asphalt/coat " + code, "מ\"ר", 10m);
        Add("U51.04.2530", "SYNTHETIC milling", "מ\"ר", 5m);
        Add("U51.04.2430", "SYNTHETIC prime on milled", "מ\"ר", 5m);
        Add("U51.03.0010", "SYNTHETIC subbase", "מ\"ק", 100m);
        Add("U51.06.1900", "SYNTHETIC road curb", "מטר", 90m);
        Add("U51.06.2460", "SYNTHETIC island curb", "מטר", 150m);
        Add("U51.32.0290", "SYNTHETIC painted area", "מ\"ר", 50m);
        Add("U51.32.0210", "SYNTHETIC 10 cm line", "מטר", 17m);
        Add("U51.32.0240", "SYNTHETIC 15 cm line", "מטר", 23m);
        Add("U40.02.2340", "SYNTHETIC shelter", "יח'", null);
        Add("U51.33.2330", "SYNTHETIC barrier", "מטר", 300m);
        Add("U51.06.2930", "SYNTHETIC bike curb", "מטר", 120m);
        Add("U51.04.1830", "SYNTHETIC SMA PG76", "מ\"ר", 70m);
        Add("U51.06.8040", "SYNTHETIC pavers", "מ\"ר", 138m);
        Add("U51.04.2310", "SYNTHETIC sidewalk asphalt", "מ\"ר", 40m);
        Add("U51.01.2000", "SYNTHETIC weed control", "מ\"ר", 1m);
        Add("U51.06.3060", "SYNTHETIC garden curb", "מטר", 90m);
        return catalog;
    }

    private static QuantityClassification Approved(CatalogSnapshot catalog, string code, string hash) => new()
    {
        RuleKey = "SYNTHETIC",
        CandidateCatalogCode = code,
        ApprovedCatalogId = catalog.SnapshotId,
        ApprovedCatalogHash = hash,
        ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(catalog.Items[code]),
        MappingApprovedBy = "SYNTHETIC engineer",
        MappingApprovedAtUtc = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc),
    };

    private static EngineerDraftContext Context() =>
        new("SYNTHETIC project", "SYNTHETIC", "SYNTHETIC-RUN", "synthetic.dwg", "SYNTHETIC catalog", new[] { "GFC111" });

    private static EngineerBoqDraft Build(IReadOnlyList<NeutralQuantityRecord> records, IReadOnlyList<DeliveryFinding>? findings = null,
        CatalogSnapshot? catalog = null, EngineerDraftContext? context = null) =>
        EngineerBoqDraftBuilder.Build(records, findings ?? Array.Empty<DeliveryFinding>(), catalog ?? Catalog(),
            new Dictionary<string, string> { ["51"] = "51 — עבודות סלילה", ["51.04"] = "51.04 — שכבות אספלט" },
            EngineerBoqLibrary.RoadsV1, context ?? Context());

    private static double DefaultRowQuantity(EngineerBoqDraft draft, string rowKey) =>
        EngineerBoqDraftExcelWriter.RowQuantity(EngineerBoqDraftExcelWriter.BoqRows(draft).Single(r => r.RowKey == rowKey),
            EngineerBoqDraftExcelWriter.DefaultParameter(draft), null);

    [Fact]
    public void ReviewedSourceExclusionsRemainExplicitInTheEngineeringDraft()
    {
        const string summary = "SIMULATION ONLY: architecture.dwg הוחרג; מספר ישויות לא ידוע — לא נמדד";
        var draft = Build(new[] { Rec(Gm, "KERB-NEW", "length", "polyline-length", 10, "מטר") }, new[]
        {
            new DeliveryFinding { Code = EstimateSourceSelectionPolicy.ExcludedScopeCode, Domain = "estimate",
                Severity = FindingSeverity.Info, Title = "היקף המקורות", Message = summary },
        });
        draft.Warnings.Should().Contain(w => w.Message == summary && w.AffectsTotal);
    }

    private static DeliveryFinding Finding(string code, string title, string message = "", params string[] affected) => new()
    {
        Code = code, Domain = "estimate", Severity = FindingSeverity.Error, Title = title, Message = message,
        AffectedRecordIds = affected.ToList(),
    };

    [Fact]
    public void SurveyAndUtilitySourcesNeverEnterTheBoqButStayVisible()
    {
        var records = new[]
        {
            Rec(Survey, "S_CURB", "length", "polyline-length+xref-transform", 500, "מטר"),
            Rec(Survey, "S_WALL_BT", "length", "polyline-length+xref-transform", 800, "מטר"),
            Rec(Utilities, "MAIM8Z", "length", "polyline-length+xref-transform", 60, "מטר"),
            Rec(Gm, "HW-CURB", "length", "polyline-length+xref-transform", 100, "מטר"),
        };
        var draft = Build(records);

        draft.Lines.Should().OnlyContain(l => l.Emit.Code == "U51.06.1900");
        draft.Elements.Single().IncludedQuantity.Should().Be(100);
        draft.Existing.Select(g => g.Layer).Should().BeEquivalentTo("S_CURB", "S_WALL_BT");
        draft.Utilities.Select(g => g.Layer).Should().BeEquivalentTo("MAIM8Z");
        draft.AccountedRecords.Should().Be(records.Length);
    }

    [Fact]
    public void HostLayersAreClassifiedAndDesignedUtilitiesAreNotFiledAsExisting()
    {
        var records = new[]
        {
            Rec(null, "2000-DES+110+W", "length", "polyline-length", 80, "מטר"),
            Rec(null, "0-EZER", "length", "line-length", 30, "מטר"),
            Rec("(host)", "CURB-EXST", "length", "polyline-length", 40, "מטר"),
            Rec(null, "BIUV315", "length", "line-length", 5, "מטר"),
            Rec(Gm, "GFC111", "length", "polyline-length+xref-transform", 70, "מטר"),
            Rec(null, "corridor:MAZA", "volume", "corridor-qto-avg-end-area", 12, "מ\"ק"),
        };
        var draft = Build(records);

        draft.Lines.Should().BeEmpty();
        draft.DraftingAids.Select(g => g.Layer).Should().BeEquivalentTo("2000-DES+110+W", "0-EZER", "GFC111");
        draft.Existing.Select(g => g.Layer).Should().BeEquivalentTo("CURB-EXST");
        draft.Existing.Single().Source.Should().Be(EngineerBoqDraftBuilder.HostSource);
        draft.Utilities.Should().BeEmpty();
        draft.UnmappedDesign.Should().BeEmpty();
        var sewer = draft.Elements.Single(e => e.Rule.Id == "utility-sewer");
        sewer.IncludedQuantity.Should().Be(5);
        sewer.Rule.Emits.Should().BeEmpty("the domain is recognised; the item, diameter and price stay the engineer's");
        sewer.Sources.Single().Note.Should().Contain("קוטר 315");
        draft.CorridorVolumes.Select(g => g.Layer).Should().BeEquivalentTo("corridor:MAZA");
        draft.Sources.Should().ContainSingle(s => s.Source == EngineerBoqDraftBuilder.HostSource && s.Records == 5);
        draft.AccountedRecords.Should().Be(records.Length);
    }

    [Fact]
    public void DrainageModelLayersJoinTheDrainageDecisionFamiliesWithTheirSizeShownWithoutAUnit()
    {
        // Layer and block names as measured in 6422-DR (28.09.2026); quantities synthetic.
        var records = new[]
        {
            Rec(null, "DR-PIPE-40", "length", "line-length", 120, "מטר"),
            Rec(null, "DR-PPIPE-100", "length", "polyline-length", 18, "מטר"),
            Rec(null, "DR-MNHL-BL", "count", "block-count", 1, "יח'", "koltan3"),
            Rec(null, "DR-MNHL-BL", "count", "block-count", 1, "יח'", "shuha140_140"),
            Rec(null, "DR-MNHL-BL", "length", "circle-circumference", 5.8, "מטר"),
            Rec(null, "BIUV315", "length", "line-length", 5, "מטר"),
        };
        var draft = Build(records);

        var pipes = draft.Elements.Single(e => e.Rule.Id == "utility-drainage");
        pipes.IncludedQuantity.Should().Be(138);
        pipes.Rule.Emits.Should().BeEmpty("item, class, depth and price stay the engineer's");
        pipes.Sources.Single(s => s.Group.Layer == "DR-PIPE-40").Note.Should().Contain("מידה 40 — יחידה לא מצוינת").And.NotContain("מ\"מ");
        pipes.Sources.Single(s => s.Group.Layer == "DR-PPIPE-100").Note.Should().Contain("מידה 100 — יחידה לא מצוינת").And.NotContain("מ\"מ");
        var structures = draft.Elements.Single(e => e.Rule.Id == "utility-drainage-structures");
        structures.Sources.Select(s => s.Group.Block).Should().BeEquivalentTo("koltan3", "shuha140_140");
        structures.Sources.Single(s => s.Group.Block == "shuha140_140").Note.Should().Contain("מידה 140/140 — יחידה לא מצוינת");
        draft.NotUsedAlternatives.Should().ContainSingle(g => g.Layer == "DR-MNHL-BL" && g.Kind == "length",
            "a manhole layer's drawn circles are not pipe length");
        draft.Elements.Single(e => e.Rule.Id == "utility-sewer").Sources.Single().Note.Should().Contain("קוטר 315 מ\"מ");
        draft.UnmappedDesign.Should().BeEmpty();
        draft.Lines.Should().BeEmpty();
        draft.AccountedRecords.Should().Be(records.Length);
    }

    [Theory]
    [InlineData("DR-BL-TX-2", "DR-MNHL_120-100", "מידה 120/100 — יחידה לא מצוינת")]
    [InlineData("NIKUZ", "shuha140_140", "מידה 140/140 — יחידה לא מצוינת")]
    [InlineData("DR-PPIPE-100", null, "מידה 100 — יחידה לא מצוינת")]
    [InlineData("DR-MNHL-BL", "koltan3", null)]
    [InlineData("BIUV315", null, "קוטר 315 מ\"מ")]
    [InlineData("MAIM8Z", "MAIM8Z", "קוטר 8 צול")]
    public void OnlyAnExplicitUnitOrANonDrLayerNumberIsReadAsADiameter(string layer, string? block, string? expected)
    {
        // Numbers in block names and DR-model names have no stated unit (Codex review, 28.09): never millimetres.
        EngineerBoqDraftBuilder.DiameterHint(layer, block).Should().Be(expected);
    }

    [Fact]
    public void DesignedInfrastructureIsRecognisedByDomainAndNeverPricedWithoutTheEngineer()
    {
        var records = new[]
        {
            Rec(null, "MAIM8Z", "length", "polyline-length", 39, "מטר"),
            Rec(null, "MAIM8Z", "count", "block-count", 1, "יח'", "MAIM8Z"),
            Rec(null, "BEZEQ", "length", "polyline-length", 85, "מטר"),
            Rec(null, "BEZEQ", "count", "block-count", 1, "יח'", "Mahod_Profile_Projection"),
            Rec(null, "TEURA", "length", "polyline-length", 216, "מטר"),
            Rec("UT-3D", "MAIM", "length", "polyline-length+xref-transform", 500, "מטר"),
        };
        var draft = Build(records);

        draft.Elements.Select(e => e.Rule.Id).Should().BeEquivalentTo(
            "utility-water", "utility-water-structures", "utility-telecom", "utility-lighting");
        draft.Elements.Should().OnlyContain(e => e.Rule.Emits.Count == 0 && e.Rule.Confidence == DraftConfidence.Decision);
        draft.Elements.Single(e => e.Rule.Id == "utility-water").Sources.Single().Note.Should().Contain("קוטר 8 צול").And.Contain("לא אומת");
        draft.DraftingAids.Should().ContainSingle(g => g.Block == "Mahod_Profile_Projection", "our own projection blocks are not structures");
        draft.Utilities.Should().ContainSingle(g => g.Source == "UT-3D", "existing infrastructure never becomes new work");
        draft.Lines.Should().BeEmpty();
        EngineerBoqDraftExcelWriter.TotalAt(draft, EngineerBoqDraftExcelWriter.BoqRows(draft), EngineerBoqDraftExcelWriter.DefaultParameter(draft), null)
            .Should().Be(0m);
        draft.AccountedRecords.Should().Be(records.Length);

        var path = Path.Combine(Path.GetTempPath(), "mhd-engineer-draft-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            EngineerBoqDraftExcelWriter.Write(draft, path);
            using var zip = ZipFile.OpenRead(path);
            var boq = Cells(zip, 3);
            var definitions = boq.Where(c => c.Key.StartsWith("A", StringComparison.Ordinal) && c.Value.Text == "הגדרה").Select(c => c.Key[1..]).ToList();
            definitions.Should().HaveCount(4, "each domain is a definition row the engineer completes with an item and a price");
            foreach (var row in definitions)
                boq["G" + row].Formula.Should().StartWith("IF(AND(ISNUMBER(F");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void PavementRecipeBuildsTheStructureOnlyOnTheFullDepthShare()
    {
        var records = new[]
        {
            Rec(Ha, "HW-HTCH-ROAD", "area", "hatch-area+xref-transform", 1000, "מ\"ר"),
            Rec(Ha, "HW-HTCH-ROAD", "area", "hatch-area+xref-transform", 500, "מ\"ר"),
            // A closed polyline on the same layer is probably the hatch boundary: never added.
            Rec(Ha, "HW-HTCH-ROAD", "area", "closed-polyline-area+xref-transform", 1500, "מ\"ר"),
        };
        var draft = Build(records);

        draft.Elements.Single().IncludedQuantity.Should().Be(1500);
        draft.Lines.Select(l => l.Emit.Code).Should().Equal(
            "U51.04.1820", "U51.04.0130", "U51.04.0160", "U51.04.2410", "U51.04.2400", "U51.04.2420",
            "U51.03.0010", "U51.02.0110", "U51.04.2530");
        draft.NotUsedAlternatives.Should().ContainSingle(g => g.MethodClass == "closed-polyline" && g.Reason.Contains("גבול ההצללה"));
        // Defaults: 40% full depth, 0.30 m subbase, milling on the other 60%.
        DefaultRowQuantity(draft, "U51.04.1820").Should().BeApproximately(1500, 1e-9);
        DefaultRowQuantity(draft, "U51.04.0130").Should().BeApproximately(600, 1e-9);
        // Light tack twice between new layers on the full-depth part; heavy tack on the milled rest only.
        DefaultRowQuantity(draft, "U51.04.2410").Should().BeApproximately(1500 * 0.4 * 2, 1e-9);
        DefaultRowQuantity(draft, "U51.04.2400").Should().BeApproximately(900, 1e-9);
        DefaultRowQuantity(draft, "U51.03.0010").Should().BeApproximately(1500 * 0.4 * 0.3, 1e-9);
        DefaultRowQuantity(draft, "U51.04.2530").Should().BeApproximately(900, 1e-9);
        draft.Warnings.Should().NotContain(w => w.Topic == "יחידה לא תואמת");
    }

    [Fact]
    public void DuplicateLayerInSecondaryModelIsListedButNotIncluded()
    {
        var records = new[]
        {
            Rec(Gm, "HW-CURB", "length", "polyline-length+xref-transform", 1000, "מטר"),
            Rec(Gm, "HW-CURB", "length", "closed-polyline-perimeter+xref-transform", 50, "מטר"),
            Rec(Gm, "HW-CURB", "area", "closed-polyline-area+xref-transform", 30, "מ\"ר"),
            Rec(Ha, "HW-CURB", "length", "polyline-length+xref-transform", 200, "מטר"),
        };
        var draft = Build(records);

        var element = draft.Elements.Single();
        element.IncludedQuantity.Should().Be(1050);
        element.Sources.Should().ContainSingle(s => !s.Included && s.Group.Source == Ha && !s.InPrimarySource);
        draft.NotUsedAlternatives.Should().ContainSingle(g => g.Kind == "area");
        draft.Warnings.Should().Contain(w => w.Topic == "מודלים נוספים" && w.AffectsTotal);
        draft.Warnings.Should().Contain(w => w.Topic == "מקורות שחוברו");
        draft.AccountedRecords.Should().Be(records.Length);
    }

    [Fact]
    public void UnmeasuredHatchesAreReportedOnTheElementAndNeverInvented()
    {
        var records = new[] { Rec(Ha, "HW-HTCH-ROAD", "area", "hatch-area+xref-transform", 1000, "מ\"ר") };
        var failure = Finding(EstimateFindingCodes.MeasurementFailed, "2 supported construction objects could not be measured",
            $"Failed to measure supported entity Hatch 1: eNotApplicable\nmeasurement-kind=area; source=x; sha256=y; " +
            $"handle=1/2; xref={Ha}; layer={Ha}|HW-HTCH-ROAD; entity=Hatch; method=hatch-area\n" +
            $"measurement-kind=area; source=x; sha256=y; handle=1/3; xref={Ha}; layer={Ha}|HW-HTCH-ROAD; entity=Hatch; method=hatch-area");
        var draft = Build(records, new[] { failure });

        draft.Elements.Single().UnmeasuredObjects.Should().Be(2);
        draft.Elements.Single().IncludedQuantity.Should().Be(1000);
        draft.UnmeasuredDesignObjects.Should().Be(2);
        draft.Warnings.Should().Contain(w => w.Topic == "כמות חסרה" && w.Message.Contains("לא נמדדו 2 עצמים") && w.Action.Contains(Ha));
    }

    [Fact]
    public void FailuresWithoutProvenanceStayVisibleAsUnlocated()
    {
        var records = new[] { Rec(Ha, "HW-HTCH-ROAD", "area", "hatch-area+xref-transform", 1000, "מ\"ר") };
        var failure = Finding(EstimateFindingCodes.MeasurementFailed, "3 supported construction objects could not be measured",
            "Could not open model-space object: eWasErased\n" +
            $"measurement-kind=area; source=x; sha256=y; handle=1/4; xref={Ha}; layer={Ha}|HW-HTCH-ROAD; entity=Hatch; method=hatch-area\n" +
            "Could not read extents: eInvalidExtents");
        var draft = Build(records, new[] { failure });

        draft.Elements.Single().UnmeasuredObjects.Should().Be(1);
        draft.UnmeasuredObjectsTotal.Should().Be(3);
        draft.UnmeasuredDesignObjects.Should().Be(3);
        draft.Warnings.Should().Contain(w => w.Message.StartsWith("2 עצמים לא נמדדו") && w.AffectsTotal);
    }

    [Fact]
    public void ScopedEvidenceFailuresAreNotCountedAsMissingQuantities()
    {
        var road = Rec(Ha, "HW-HTCH-ROAD", "area", "hatch-area+xref-transform", 1000, "מ\"ר");
        var failure = Finding(EstimateFindingCodes.MeasurementFailed, "1 measured object has incomplete boundary evidence", "boundary", road.RecordId);
        var draft = Build(new[] { road }, new[] { failure });

        draft.Elements.Single().UnmeasuredObjects.Should().Be(0);
        draft.UnmeasuredObjectsTotal.Should().Be(0);
        draft.Warnings.Should().Contain(w => w.Topic == "ראיות מדידה חלקיות" && !w.AffectsTotal);
    }

    [Fact]
    public void ScanOverlapFindingsReachTheirElement()
    {
        var a = Rec(Ha, "HW-HTCH-ROAD", "area", "hatch-area+xref-transform", 1000, "מ\"ר");
        var b = Rec(Ha, "HW-HTCH-ROAD", "area", "hatch-area+xref-transform", 400, "מ\"ר");
        var overlap = Finding(EstimateFindingCodes.OverlapRisk,
            "Overlapping 'layer:HW-HTCH-ROAD|area' areas — 3 overlapping pair(s) across 2 objects", "", a.RecordId, b.RecordId);
        var draft = Build(new[] { a, b }, new[] { overlap });

        draft.Elements.Single().OverlapPairs.Should().Be(3);
        draft.Warnings.Should().Contain(w => w.Topic == "חפיפה אפשרית" && w.AffectsTotal);
        draft.Findings.Should().ContainSingle(f => f.Code == EstimateFindingCodes.OverlapRisk && f.AffectedRecords == 2);
    }

    [Fact]
    public void BlocksOnOneLayerAreSplitByMeaning()
    {
        var records = new[]
        {
            Rec(Sm, "TR-MARK-ARW-YLW", "count", "block-count+xref-transform", 1, "יח'", "813(814)"),
            Rec(Sm, "TR-MARK-ARW-YLW", "count", "block-count+xref-transform", 1, "יח'", "813(814)"),
            Rec(Sm, "TR-MARK-ARW-YLW", "count", "block-count+xref-transform", 1, "יח'", "M"),
            Rec(Sm, "TR-MARK-ARW-YLW", "count", "block-count+xref-transform", 1, "יח'", "M"),
            Rec(Sm, "TR-MARK-ARW-YLW", "count", "block-count+xref-transform", 1, "יח'", "M"),
            Rec(Sm, "TR-MARK-ARW-YLW", "count", "block-count+xref-transform", 1, "יח'", "UNKNOWN-BLOCK"),
        };
        var draft = Build(records);

        draft.Elements.Single(e => e.Rule.Id == "marking-arrows").IncludedQuantity.Should().Be(2);
        draft.Elements.Single(e => e.Rule.Id == "marking-m-blocks").IncludedQuantity.Should().Be(3);
        draft.NotUsedAlternatives.Concat(draft.UnmappedDesign).Should().ContainSingle(g => g.Block == "UNKNOWN-BLOCK");
        DefaultRowQuantity(draft, "U51.32.0290").Should().BeApproximately(2 * 1.0, 1e-9);
        draft.AccountedRecords.Should().Be(records.Length);
    }

    [Fact]
    public void SuspectedDoubleDrawnIslandCurbHasItsOwnRowAndIsNotIncluded()
    {
        var records = new[]
        {
            Rec(Gm, "TR-ISLAND-CURBSTONE", "length", "polyline-length+xref-transform", 1000, "מטר"),
            Rec(Gm, "HW-CURB-ILND", "length", "polyline-length+xref-transform", 300, "מטר"),
            Rec(Gm, "TR-INNER-ISLAND-CURBSTONE", "length", "polyline-length+xref-transform", 990, "מטר"),
        };
        var draft = Build(records);
        var rows = EngineerBoqDraftExcelWriter.BoqRows(draft);

        rows.Where(r => r.Head.Emit.Code == "U51.06.2460").Select(r => r.RowKey).Should().BeEquivalentTo(
            "U51.06.2460", "U51.06.2460#curb-island-hw", "U51.06.2460#curb-inner-island");
        DefaultRowQuantity(draft, "U51.06.2460").Should().Be(1000);
        DefaultRowQuantity(draft, "U51.06.2460#curb-inner-island").Should().Be(0);
        draft.Warnings.Should().Contain(w => w.Topic == "לא נכלל עד בדיקה");
        var inner = draft.Elements.Single(e => e.Rule.Id == "curb-inner-island");
        inner.Sources.Should().OnlyContain(s => !s.Included && s.InPrimarySource);
    }

    [Fact]
    public void AnApprovedProfileMappingTeachesTheDraftAMeaninglessLayerName()
    {
        var catalog = Catalog();
        var records = new[]
        {
            Rec(Gm, "asdasd23423", "length", "polyline-length+xref-transform", 120, "מטר",
                classification: Approved(catalog, "U51.06.1900", catalog.FileHash)),
            Rec(Gm, "asdasd23423", "length", "polyline-length+xref-transform", 30, "מטר",
                classification: Approved(catalog, "U51.06.1900", catalog.FileHash)),
            // Approved against another price-list file: not current, so it stays unmapped.
            Rec(Gm, "qwe999", "length", "polyline-length+xref-transform", 70, "מטר",
                classification: Approved(catalog, "U51.06.1900", new string('c', 64))),
        };
        var draft = Build(records, catalog: catalog);

        var element = draft.Elements.Single();
        element.Rule.Id.Should().StartWith("profile-approved:");
        element.Rule.Confidence.Should().Be(DraftConfidence.Direct);
        element.IncludedQuantity.Should().Be(150);
        draft.Lines.Single().RowKey.Should().StartWith("U51.06.1900#profile-approved:");
        draft.Lines.Single().Emit.Note.Should().Contain("SYNTHETIC engineer");
        draft.UnmappedDesign.Should().ContainSingle(g => g.Layer == "qwe999" && g.Reason.Contains("עוזר ה-AI"));
        draft.AccountedRecords.Should().Be(records.Length);
    }

    [Fact]
    public void ApprovedMappingOutranksADifferentLibraryRecipeAndPartialApprovalIsFlagged()
    {
        var catalog = Catalog();
        var records = new[]
        {
            Rec(Gm, "HW-CURB", "length", "polyline-length+xref-transform", 100, "מטר",
                classification: Approved(catalog, "U51.06.2460", catalog.FileHash)),
            Rec(Gm, "HW-FOO", "length", "polyline-length+xref-transform", 10, "מטר",
                classification: Approved(catalog, "U51.06.2460", catalog.FileHash)),
            Rec(Gm, "HW-FOO", "length", "polyline-length+xref-transform", 10, "מטר"),
        };
        var draft = Build(records, catalog: catalog);

        draft.Elements.Should().NotContain(e => e.Rule.Id == "curb-road");
        draft.Elements.Should().ContainSingle(e => e.Rule.Id == "curb-road+approved:U51.06.2460");
        draft.Lines.Should().ContainSingle(l => l.Element.Rule.Id == "curb-road+approved:U51.06.2460" && l.Emit.Code == "U51.06.2460");
        draft.Warnings.Should().Contain(w => w.Topic == "שיוך מאושר הוחלף בשורת מתכון");
        draft.Warnings.Should().Contain(w => w.Topic == "שיוך מאושר חלקי");
        draft.UnmappedDesign.Should().ContainSingle(g => g.Layer == "HW-FOO");
        draft.AccountedRecords.Should().Be(records.Length);
    }

    [Fact]
    public void ApprovedItemThatIsPartOfTheRecipeKeepsTheRecipe()
    {
        var catalog = Catalog();
        var records = new[]
        {
            Rec(Ha, "HW-HTCH-ROAD", "area", "hatch-area+xref-transform", 1000, "מ\"ר",
                classification: Approved(catalog, "U51.04.1820", catalog.FileHash)),
        };
        var draft = Build(records, catalog: catalog);

        draft.Elements.Single().Rule.Id.Should().Be("road-pavement");
        draft.Lines.Select(l => l.Emit.Code).Should().Contain(new[] { "U51.04.1820", "U51.04.0130", "U51.03.0010", "U51.04.2530" });
    }

    [Fact]
    public void EngineerNotConstructionDecisionsAreAppliedAndBareKeysAreNot()
    {
        var excluded = new QuantityClassification { RuleKey = "layer:HW-CURB|length" };
        var legacy = new QuantityClassification { RuleKey = "layer:HW-MYSTERY|length" };
        var records = new[]
        {
            Rec(Gm, "HW-CURB", "length", "polyline-length+xref-transform", 100, "מטר", classification: excluded),
            Rec(Gm, "HW-MYSTERY", "length", "polyline-length+xref-transform", 20, "מטר", classification: legacy),
        };
        var context = Context() with
        {
            ExcludedRuleDecisions = new Dictionary<string, string> { ["layer:HW-CURB|length"] = "SYNTHETIC engineer" },
        };
        var draft = Build(records, context: context);

        draft.Elements.Should().BeEmpty();
        draft.ExcludedByDecision.Should().ContainSingle(g => g.Layer == "HW-CURB" && g.Reason.Contains("SYNTHETIC engineer"));
        draft.UnmappedDesign.Should().ContainSingle(g => g.Layer == "HW-MYSTERY");
        draft.Warnings.Should().Contain(w => w.Topic == "הוחרג בהחלטת מהנדס");
        draft.AccountedRecords.Should().Be(records.Length);
    }

    [Fact]
    public void AnItemInTheWrongUnitIsNeverPricedIntoTheSubtotal()
    {
        var catalog = Catalog();
        // The curb item is (wrongly) priced per m² while curbs are measured in metres.
        catalog.Items["U51.06.1900"] = new CatalogItem { Code = "U51.06.1900", Description = "SYNTHETIC curb per m2", UnitRaw = "מ\"ר" };
        var records = new[]
        {
            Rec(Gm, "HW-CURB", "length", "polyline-length+xref-transform", 100, "מטר"),
            Rec(Gm, "TR-ISLAND-CURBSTONE", "length", "polyline-length+xref-transform", 10, "מטר"),
        };
        var draft = Build(records, catalog: catalog);
        var rows = EngineerBoqDraftExcelWriter.BoqRows(draft);

        var curb = rows.Single(r => r.Head.Emit.Code == "U51.06.1900");
        curb.RowKey.Should().Be("U51.06.1900#curb-road");
        curb.Price.Should().BeNull();
        draft.Warnings.Should().Contain(w => w.Topic == "יחידה לא תואמת" && w.AffectsTotal);
        EngineerBoqDraftExcelWriter.TotalAt(draft, rows, EngineerBoqDraftExcelWriter.DefaultParameter(draft), null)
            .Should().Be(150m * 10);
    }

    [Fact]
    public void MarkingQuantitiesUseTheWidthDrawnInTheModel()
    {
        var records = new[]
        {
            Rec(Sm, "TR-MARK-WHT-811", "length", "polyline-length+xref-transform", 100, "מטר", width: 3.0, linetype: "DASHED1-1", xrefTransform: Proven),
            Rec(Sm, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 50, "מטר", width: 0.5, xrefTransform: Proven),
            Rec(Sm, "TR-MARK-WHT-815", "length", "polyline-length+xref-transform", 40, "מטר", width: 0.1, xrefTransform: Proven),
            Rec(Sm, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 10, "מטר"),
            Rec(Sm, "TR-MARK-YLW-3-3", "length", "polyline-length+xref-transform", 200, "מטר", width: 0.15, linetype: "DASHED3-3", xrefTransform: Proven),
            Rec(Sm, "TR-MARK-WHT-809", "length", "polyline-length+xref-transform", 60, "מטר", width: 0.1, linetype: "1-1", xrefTransform: Proven),
            // A width in a non-metre database is not proven: it is never replaced by the parameter or a default item.
            Rec(Sm, "TR-MARK-WHT-801", "length", "polyline-length+xref-transform", 30, "מטר", width: 500, units: "Millimeters", xrefTransform: Proven),
        };
        var draft = Build(records);

        var crossing = draft.Elements.Single(e => e.Rule.Id == "marking-crossings@area");
        crossing.IncludedQuantity.Should().BeApproximately(300, 1e-9);
        crossing.Unit.Should().Be("מ\"ר");
        crossing.Sources.Single().Note.Should().Contain("רוחב משורטט ממוצע 3.00");
        draft.Elements.Should().Contain(e => e.Rule.Id == "marking-transverse@area" && Math.Abs(e.IncludedQuantity - 25) < 1e-9);
        draft.Elements.Should().Contain(e => e.Rule.Id == "marking-transverse@w10" && e.IncludedQuantity == 40);
        draft.Warnings.Should().Contain(w => w.Topic == "קווים רוחביים דקים" && w.AffectsTotal);
        draft.Elements.Should().Contain(e => e.Rule.Id == "marking-transverse" && e.IncludedQuantity == 10);
        // Crossing 300 × 0.5 paint + stop line 25 + undrawn-width stop line 10 × 0.5; the 10 cm 815 line is a line item.
        DefaultRowQuantity(draft, "U51.32.0290").Should().BeApproximately(150 + 25 + 5, 1e-9);
        DefaultRowQuantity(draft, "U51.32.0240").Should().BeApproximately(200 * 0.5, 1e-9);
        DefaultRowQuantity(draft, "U51.32.0210").Should().BeApproximately(60 + 40, 1e-9);
        draft.Elements.Should().Contain(e => e.Rule.Id == "marking-lines@unproven" && e.IncludedQuantity == 30);
        draft.Warnings.Should().ContainSingle(w => w.Topic == "קו מקווקו שתומחר כרציף" && w.Message.Contains("TR-MARK-WHT-809"));
        draft.Warnings.Should().NotContain(w => w.Topic == "יחידה לא תואמת");
        draft.AccountedRecords.Should().Be(records.Length);
    }

    [Fact]
    public void UnknownDesignLayersAreListedAsUnmappedAndMissingPricesStayZero()
    {
        var records = new[]
        {
            Rec(Gm, "HW-MYSTERY", "length", "polyline-length+xref-transform", 42, "מטר"),
            Rec(Sm, "BUS-STATION-NEW", "count", "block-count+xref-transform", 1, "יח'", "SHELTER"),
            Rec(Sm, "BUS-STATION-NEW", "count", "block-count+xref-transform", 1, "יח'", "SHELTER"),
        };
        var draft = Build(records);

        draft.UnmappedDesign.Should().ContainSingle(g => g.Layer == "HW-MYSTERY");
        var shelter = draft.Lines.Single();
        shelter.Price.Should().BeNull();
        shelter.Element.IncludedQuantity.Should().Be(2);
        draft.Warnings.Should().Contain(w => w.Topic == "פריט ללא מחיר" && w.AffectsTotal);
        draft.Warnings.Should().NotContain(w => w.Topic == "יחידה לא תואמת");
    }

    [Fact]
    public void WorkbookFormulasResolveToTheRightRowsAndTheDefaultTotalMatches()
    {
        var records = new[]
        {
            Rec(Ha, "HW-HTCH-ROAD", "area", "hatch-area+xref-transform", 1000, "מ\"ר"),
            Rec(Gm, "HW-CURB", "length", "polyline-length+xref-transform", 100, "מטר"),
            Rec(Survey, "S_CURB", "length", "polyline-length+xref-transform", 500, "מטר"),
        };
        var draft = Build(records);
        var path = Path.Combine(Path.GetTempPath(), "mhd-engineer-draft-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            var result = EngineerBoqDraftExcelWriter.Write(draft, path);
            result.LineCount.Should().Be(10);
            result.BoqRowCount.Should().Be(10);
            // 1820 1000×10, 0130/0160/2420/0110 400×10 each, light tack 800×10, heavy tack 600×10,
            // subbase 120 m³×100, milling 600×5, curb 100×90.
            result.PricedTotalAtDefaults.Should().Be(10000m + 4 * 4000m + 8000m + 6000m + 12000m + 3000m + 9000m);

            using var zip = ZipFile.OpenRead(path);
            var sheetNames = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open())
                .Descendants().Where(e => e.Name.LocalName == "sheet").Select(e => (string)e.Attribute("name")!).ToList();
            sheetNames.Should().Equal(EngineerBoqDraftExcelWriter.SummarySheet, EngineerBoqDraftExcelWriter.ChecksSheet,
                EngineerBoqDraftExcelWriter.BoqSheet, EngineerBoqDraftExcelWriter.ParametersSheet,
                EngineerBoqDraftExcelWriter.BaseSheet, EngineerBoqDraftExcelWriter.UnmappedSheet,
                EngineerBoqDraftExcelWriter.AlternativesSheet, EngineerBoqDraftExcelWriter.ExistingSheet,
                EngineerBoqDraftExcelWriter.AidsSheet);
            var cells = sheetNames.Select((name, i) => (name, cells: Cells(zip, i + 1)))
                .ToDictionary(s => s.name, s => s.cells);
            var boq = cells[EngineerBoqDraftExcelWriter.BoqSheet];
            var baseSheet = cells[EngineerBoqDraftExcelWriter.BaseSheet];
            var parameters = cells[EngineerBoqDraftExcelWriter.ParametersSheet];
            // The survey layer is never a priced source (its name may appear only in explanatory scope notes).
            baseSheet.Values.Should().NotContain(c => c.Text != null && c.Text.Contains("S_CURB"));

            // Every cross-sheet reference lands on the cell it means: element totals and parameter values.
            var references = 0;
            foreach (var formula in boq.Values.Where(c => c.Formula != null).Select(c => c.Formula!))
            {
                foreach (Match m in Regex.Matches(formula, @"'(?<sheet>[^']+)'!(?<col>[A-Z]+)(?<row>\d+)"))
                {
                    references++;
                    var address = m.Groups["col"].Value + m.Groups["row"].Value;
                    if (m.Groups["sheet"].Value == EngineerBoqDraftExcelWriter.BaseSheet)
                    {
                        address.Should().StartWith("F");
                        baseSheet[address].Formula.Should().StartWith("SUMPRODUCT(F");
                    }
                    else
                    {
                        m.Groups["sheet"].Value.Should().Be(EngineerBoqDraftExcelWriter.ParametersSheet);
                        address.Should().StartWith("G", "formulas read the validated value, never the raw input");
                        parameters[address].Formula.Should().StartWith($"IF(AND(ISNUMBER(D{m.Groups["row"].Value}),");
                        parameters["D" + m.Groups["row"].Value].Number.Should().NotBeNull();
                        parameters["A" + m.Groups["row"].Value].Text.Should().NotBeNullOrEmpty();
                    }
                }
            }
            references.Should().BeGreaterThan(20);
            boq.Values.Should().Contain(c => c.Formula != null && c.Formula.Contains("(1-'" + EngineerBoqDraftExcelWriter.ParametersSheet));
            boq.Values.Should().Contain(c => c.Formula != null && c.Formula.StartsWith("ROUND(E"));

            var summary = cells[EngineerBoqDraftExcelWriter.SummarySheet];
            summary.Values.Should().Contain(c => c.Text == EngineerBoqDraftExcelWriter.SubtotalLabel);
            summary.Values.Should().Contain(c => c.Formula != null && c.Formula.StartsWith("SUMIF("));
            summary.Values.Should().Contain(c => c.Text == "מה לא נכלל בסכום הביניים");
            var subtotalRef = summary.Values.Single(c => c.Formula != null && c.Formula.StartsWith("'" + EngineerBoqDraftExcelWriter.BoqSheet)).Formula!;
            var subtotalCell = subtotalRef.Split('!')[1];
            boq[subtotalCell].Formula.Should().MatchRegex(@"^G\d+(\+G\d+)*$");
            boq["C" + subtotalCell[1..]].Text.Should().Be(EngineerBoqDraftExcelWriter.SubtotalLabel);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed record Cell(string? Text, string? Formula, double? Number);

    private static Dictionary<string, Cell> Cells(ZipArchive zip, int sheet)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var doc = XDocument.Load(zip.GetEntry($"xl/worksheets/sheet{sheet}.xml")!.Open());
        return doc.Descendants(ns + "c").ToDictionary(
            c => (string)c.Attribute("r")!,
            c => new Cell(
                c.Element(ns + "is")?.Element(ns + "t")?.Value,
                c.Element(ns + "f")?.Value,
                // Formula cells now carry Excel's cached value; text/boolean results (t="str"/"b") are not numbers.
                c.Element(ns + "v") is { } v && c.Attribute("t") == null
                    ? double.Parse(v.Value, System.Globalization.CultureInfo.InvariantCulture) : null));
    }

    [Fact]
    public void AnApprovedHatchAndItsOutlineCountOnce()
    {
        var catalog = Catalog();
        var approved = Approved(catalog, "U51.32.0290", catalog.FileHash);
        var records = new[]
        {
            Rec(Sm, "qq-area-77", "area", "hatch-area+xref-transform", 18, "מ\"ר", classification: approved),
            Rec(Sm, "qq-area-77", "area", "closed-polyline-area+xref-transform", 18, "מ\"ר", classification: approved),
        };
        var draft = Build(records, catalog: catalog);
        var rows = EngineerBoqDraftExcelWriter.BoqRows(draft);

        rows.Where(r => r.Head.Emit.Code == "U51.32.0290").Sum(r =>
            EngineerBoqDraftExcelWriter.RowQuantity(r, EngineerBoqDraftExcelWriter.DefaultParameter(draft), null)).Should().Be(18);
        draft.Elements.Single(e => e.Rule.Id.EndsWith(":closed-polyline", StringComparison.Ordinal))
            .Sources.Should().OnlyContain(s => !s.Included && s.Note.Contains("גבול ההצללה"));
        draft.AccountedRecords.Should().Be(records.Length);
    }

    [Fact]
    public void AnApprovalNeverPricesAMeasurementTheLibraryRejects()
    {
        var catalog = Catalog();
        var records = new[]
        {
            // '*BIKE*' approved per metre: the PL-BIKE hatch outline is still an alternative, not bike curb.
            Rec(Gm, "PL-BIKE", "length", "polyline-length+xref-transform", 6479, "מטר",
                classification: Approved(catalog, "U51.06.2930", catalog.FileHash)),
            Rec(Ha, "PL-BIKE", "area", "hatch-area+xref-transform", 8357, "מ\"ר"),
        };
        var draft = Build(records, catalog: catalog);

        draft.Elements.Should().NotContain(e => e.Rule.Id.StartsWith("profile-approved:", StringComparison.Ordinal));
        draft.NotUsedAlternatives.Should().ContainSingle(g => g.Layer == "PL-BIKE" && g.Kind == "length" && g.Reason.Contains("יש שיוך מאושר"));
        draft.AccountedRecords.Should().Be(records.Length);
    }

    [Fact]
    public void AnApprovalCompletesAnElementThatWaitedForADecision()
    {
        var catalog = Catalog();
        var records = new[]
        {
            Rec(Gm, "HW-BARI-H2-W4", "length", "polyline-length+xref-transform", 180, "מטר",
                classification: Approved(catalog, "U51.33.2330", catalog.FileHash)),
        };
        var draft = Build(records, catalog: catalog);

        var element = draft.Elements.Single();
        element.Rule.Id.Should().Be("safety-barrier+approved:U51.33.2330");
        draft.Lines.Single().Price.Should().Be(300m);
        draft.Warnings.Should().Contain(w => w.Topic == "שיוך מאושר הוחל על רכיב להחלטה");
    }

    [Fact]
    public void AnApprovalAppliesToOneDesignModelAndNeverToTheSurvey()
    {
        var catalog = Catalog();
        var approved = Approved(catalog, "U51.06.1900", catalog.FileHash);
        var records = new[]
        {
            Rec(Gm, "zz-edge", "length", "polyline-length+xref-transform", 500, "מטר", classification: approved),
            Rec(Ha, "zz-edge", "length", "polyline-length+xref-transform", 480, "מטר", classification: approved),
            Rec(Survey, "S_ZZ", "length", "polyline-length+xref-transform", 900, "מטר", classification: approved),
        };
        var draft = Build(records, catalog: catalog);

        var element = draft.Elements.Single();
        element.IncludedQuantity.Should().Be(500);
        element.Sources.Should().ContainSingle(s => !s.Included && s.Group.Source == Ha);
        draft.Existing.Should().ContainSingle(g => g.Layer == "S_ZZ" && g.Reason.Contains("לא הוחל"));
        draft.Warnings.Should().Contain(w => w.Topic == "מודלים נוספים");
        draft.AccountedRecords.Should().Be(records.Length);
    }

    [Fact]
    public void CountedItemsAreNeverScaledByTheQuantityFactor()
    {
        var records = new[]
        {
            Rec(Sm, "BUS-STATION-NEW", "count", "block-count+xref-transform", 1, "יח'", "SHELTER"),
            Rec(Sm, "BUS-STATION-NEW", "count", "block-count+xref-transform", 1, "יח'", "SHELTER"),
            Rec(Gm, "HW-CURB", "length", "polyline-length+xref-transform", 100, "מטר"),
        };
        var draft = Build(records);
        var rows = EngineerBoqDraftExcelWriter.BoqRows(draft);
        var defaults = EngineerBoqDraftExcelWriter.DefaultParameter(draft);
        double Factor09(string key) => key == EngineerBoqLibrary.GlobalFactorKey ? 0.9 : defaults(key);

        EngineerBoqDraftExcelWriter.RowQuantity(rows.Single(r => r.Head.Emit.Code == "U40.02.2340"), Factor09, null).Should().Be(2);
        EngineerBoqDraftExcelWriter.RowQuantity(rows.Single(r => r.Head.Emit.Code == "U51.06.1900"), Factor09, null)
            .Should().BeApproximately(90, 1e-9);
    }

    [Fact]
    public void AFailureIsCountedOnceAndOverlapsReachOnlyTheGroupsTheyName()
    {
        var hatch = Rec(Sm, "TR-MARK-WHT-811", "area", "hatch-area+xref-transform", 20, "מ\"ר");
        var records = new[]
        {
            Rec(Gm, "TR-ISLAND-CURBSTONE", "length", "polyline-length+xref-transform", 700, "מטר"),
            Rec(Gm, "TR-ISLAND-CURBSTONE", "length", "closed-polyline-perimeter+xref-transform", 300, "מטר"),
            Rec(Sm, "TR-MARK-WHT-811", "length", "polyline-length+xref-transform", 100, "מטר", width: 3.0, linetype: "DASHED1-1", xrefTransform: Proven),
            hatch,
        };
        var failure = Finding(EstimateFindingCodes.MeasurementFailed, "1 supported construction objects could not be measured",
            $"measurement-kind=length; source=x; sha256=y; handle=1/9; xref={Gm}; layer={Gm}|TR-ISLAND-CURBSTONE; entity=Line; method=line-length");
        var overlap = Finding(EstimateFindingCodes.OverlapRisk,
            "Overlapping 'layer:TR-MARK-WHT-811|area' areas — 4 overlapping pair(s) across 2 objects", "", hatch.RecordId);
        var draft = Build(records, new[] { failure, overlap });

        draft.Elements.Single(e => e.Rule.Id == "curb-island").UnmeasuredObjects.Should().Be(1);
        draft.Elements.Single(e => e.Rule.Id == "marking-crossings@area").OverlapPairs.Should().Be(0);
        draft.NotUsedAlternatives.Should().ContainSingle(g => g.MethodClass == "hatch" && g.OverlapPairs == 4);
        draft.Warnings.Count(w => w.Message.Contains("TR-ISLAND-CURBSTONE") && w.Topic == "עצמים שלא נמדדו").Should().Be(0);
    }

    [Fact]
    public void AUniformPerVertexWidthIsADrawnWidth()
    {
        var record = Rec(Sm, "TR-MARK-WHT-815", "length", "polyline-length+xref-transform", 50, "מטר", xrefTransform: Proven);
        record.Measurement.Parameters["cad_entity_database_insunits"] = "Meters";
        record.Measurement.Parameters["cad_polyline_constant_width_raw_status"] = "unavailable:Exception";
        record.Measurement.Parameters["cad_polyline_width_min_raw"] = "0.2";
        record.Measurement.Parameters["cad_polyline_width_max_raw"] = "0.2";
        var draft = Build(new[] { record });

        draft.Elements.Single().Rule.Id.Should().Be("marking-transverse@area");
        draft.Elements.Single().IncludedQuantity.Should().BeApproximately(10, 1e-9);
    }

    [Fact]
    public void UnpricedAndDefinitionRowsTakeATypedPriceIntoTheTotal()
    {
        var records = new[]
        {
            Rec(Sm, "BUS-STATION-NEW", "count", "block-count+xref-transform", 1, "יח'", "SHELTER"),
            Rec(Ha, "PL-BIKE", "area", "hatch-area+xref-transform", 800, "מ\"ר"),
        };
        var draft = Build(records);
        var path = Path.Combine(Path.GetTempPath(), "mhd-engineer-draft-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            EngineerBoqDraftExcelWriter.Write(draft, path);
            using var zip = ZipFile.OpenRead(path);
            var boq = Cells(zip, 3);
            var shelter = boq.Single(c => c.Value.Text == "U40.02.2340").Key[1..];
            boq["G" + shelter].Formula.Should().Be($"IF(ISNUMBER(F{shelter}),ROUND(E{shelter}*F{shelter},2),0)");
            var definition = boq.Where(c => c.Key.StartsWith("A", StringComparison.Ordinal) && c.Value.Text == "הגדרה").Single().Key[1..];
            // A price alone never completes a definition row: it also needs an item code other than the placeholder.
            boq["G" + definition].Formula.Should().Be(
                $"IF(AND(ISNUMBER(F{definition}),LEN(TRIM(B{definition}))>0,B{definition}<>\"—\"),ROUND(E{definition}*F{definition},2),0)");
            boq["B" + definition].Text.Should().Be("—");
            boq["I" + definition].Formula.Should().Be(
                $"IF(OR(LEN(TRIM(B{definition}))=0,B{definition}=\"—\"),\"חסר מק\"\"ט\",IF(ISNUMBER(F{definition}),\"הושלם\",\"חסר מחיר\"))");
            var completed = boq.Single(c => c.Value.Text == EngineerBoqDraftExcelWriter.CompletedTotalLabel).Key[1..];
            boq["G" + completed].Formula.Should().MatchRegex(@"^G\d+\+G\d+\+G\d+$");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AnItemChoiceNeverChangesWhichModelsAreIncluded()
    {
        // Codex F1: two layers of the curb-garden rule with independent approvals. Changing the item approved for the
        // secondary (HA) layer must not make HA a primary model: the quantity stays GM only.
        var catalog = Catalog();
        QuantityClassification Approval(string layer, string code)
        {
            var approval = Approved(catalog, code, catalog.FileHash);
            approval.RuleKey = $"layer:{layer}|length";
            return approval;
        }
        var primary = Rec(Gm, "TR-GRDN-STONE", "length", "polyline-length+xref-transform", 100, "מטר",
            classification: Approval("TR-GRDN-STONE", "U51.06.3060"));
        var control = Build(new[]
        {
            primary,
            Rec(Ha, "0L-EV-GAN", "length", "polyline-length+xref-transform", 100, "מטר", classification: Approval("0L-EV-GAN", "U51.06.3060")),
        }, catalog: catalog);
        var changed = Build(new[]
        {
            primary,
            Rec(Ha, "0L-EV-GAN", "length", "polyline-length+xref-transform", 100, "מטר", classification: Approval("0L-EV-GAN", "U51.06.2460")),
        }, catalog: catalog);

        decimal Total(EngineerBoqDraft d) => EngineerBoqDraftExcelWriter.TotalAt(d, EngineerBoqDraftExcelWriter.BoqRows(d),
            EngineerBoqDraftExcelWriter.DefaultParameter(d), null);
        Total(control).Should().Be(9000m);
        Total(changed).Should().Be(9000m);
        foreach (var draft in new[] { control, changed })
        {
            var sources = draft.Elements.SelectMany(e => e.Sources).ToList();
            sources.Should().Contain(s => s.Group.Source == Ha && !s.Included && !s.InPrimarySource, "HA stays visible as a copy");
            sources.Where(s => s.Included).Should().OnlyContain(s => s.Group.Source == Gm);
            draft.AccountedRecords.Should().Be(2);
        }
        // F-5 (02.10): an approval on a copy that is not counted is reported as not applied, never printed as a
        // 0-quantity replacement line beside the counted recipe.
        changed.Elements.Should().NotContain(e => e.Rule.Id.Contains("+approved:"));
        changed.Warnings.Should().ContainSingle(w => w.Topic == "שיוך מאושר לא הוחל" &&
            w.Message.Contains("U51.06.2460") && w.Message.Contains("אינו המודל הראשי"));
    }

    [Fact]
    public void AnXrefWidthIsPricedOnlyWithAProvenTransform()
    {
        // Codex F2: world length 200 m, width 1 m (200 m² painted) drawn as a local XREF width of 0.5 m at scale 2.
        decimal Total(EngineerBoqDraft d) => EngineerBoqDraftExcelWriter.TotalAt(d, EngineerBoqDraftExcelWriter.BoqRows(d),
            EngineerBoqDraftExcelWriter.DefaultParameter(d), null);
        var host = Build(new[] { Rec(null, "TR-MARK-WHT-810", "length", "polyline-length", 200, "מטר", width: 1) });
        Total(host).Should().Be(10000m, "a host entity's width is already in host space");

        var unproven = Build(new[] { Rec(Sm, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 200, "מטר", width: 0.5) });
        Total(unproven).Should().Be(0m);
        unproven.Elements.Single().Rule.Id.Should().Be("marking-transverse@unproven");
        unproven.Elements.Single().Rule.Confidence.Should().Be(DraftConfidence.Decision);
        unproven.Lines.Should().BeEmpty("a width-dependent quantity is never priced from an unproven width, nor from the parameter");
        unproven.Warnings.Should().ContainSingle(w => w.Topic == "רוחב משורטט לא מוכח" && w.AffectsTotal);
        unproven.Warnings.Should().NotContain(w => w.Topic == "רכיב ללא פריט");
        unproven.AccountedRecords.Should().Be(1);

        var scaled = Build(new[] { Rec(Sm, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 200, "מטר", width: 0.5,
            xrefTransform: Transform(2, 2, 2, "uniform")) });
        Total(scaled).Should().Be(10000m, "a proven uniform scale of 2 turns the 0.5 m local width into 1 m in the host");

        foreach (var evidence in new[]
                 {
                     Transform(2, 1, 1),                                  // nonuniform
                     Transform(2, 2, 2, "rigid"),                         // declared rigid but scaled
                     Transform(1, 1, 1, "nonuniform"),                    // declared nonuniform
                     Transform(1, 1, 1, sourceUnits: "Millimeters"),      // units differ
                     "{\"space\":\"host\",\"chain\":[{\"xref\":\"x\",\"matrix\":[1,2]}],\"units\":{\"source\":\"Meters\",\"host\":\"Meters\"}}",
                     "not json",
                 })
        {
            var draft = Build(new[] { Rec(Sm, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 200, "מטר", width: 0.5, xrefTransform: evidence) });
            Total(draft).Should().Be(0m, evidence);
            draft.Elements.Single().Rule.Id.Should().Be("marking-transverse@unproven", evidence);
        }

        // Evidence that was not read is not proof, whatever its value.
        var notRead = Rec(Sm, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 200, "מטר", width: 0.5, xrefTransform: Proven);
        notRead.Measurement.Parameters[XrefWidthPolicy.TransformKey + "_status"] = "unavailable:NotSupported";
        Total(Build(new[] { notRead })).Should().Be(0m);

        // A line without a drawn width is unchanged: it uses the declared width parameter.
        var undrawn = Build(new[] { Rec(Sm, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 200, "מטר") });
        Total(undrawn).Should().Be(200m * 0.5m * 50m);
    }

    [Fact]
    public void AnXrefWidthNeedsTheChainOfItsOwnInsertion()
    {
        // Codex F2a: an empty chain proves only a host entity, never an XREF record.
        decimal Total(EngineerBoqDraft d) => EngineerBoqDraftExcelWriter.TotalAt(d, EngineerBoqDraftExcelWriter.BoqRows(d),
            EngineerBoqDraftExcelWriter.DefaultParameter(d), null);
        var empty = "{\"space\":\"host\",\"chain\":[],\"class\":\"rigid\",\"scale\":[1,1,1],\"units\":{\"source\":\"Meters\",\"host\":\"Meters\"}}";
        var emptyChain = Build(new[] { Rec(Sm, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 200, "מטר", width: 0.5, xrefTransform: empty) });
        Total(emptyChain).Should().Be(0m);
        emptyChain.Warnings.Should().Contain(w => w.Topic == "רוחב משורטט לא מוכח" && w.AffectsTotal);

        var foreign = Build(new[] { Rec(Sm, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 200, "מטר", width: 0.5,
            xrefTransform: Transform(2, 2, 2, "uniform", chain: "OTHER-MODEL")) });
        Total(foreign).Should().Be(0m, "a transform of another XREF proves nothing about this record");

        var own = Build(new[] { Rec(Sm, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 200, "מטר", width: 0.5,
            xrefTransform: Transform(2, 2, 2, "uniform")) });
        Total(own).Should().Be(10000m, "positive control: the record's own proven uniform ×2 insertion");

        // Nested XREFs: every boundary of the record's chain, in order.
        const string outer = "{\"space\":\"host\",\"chain\":[{\"xref\":\"A\",\"matrix\":[2,0,0,0,0,2,0,0,0,0,2,0,0,0,0,1]}," +
                             "{\"xref\":\"B\",\"matrix\":[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]}],\"units\":{\"source\":\"Meters\",\"host\":\"Meters\"}}";
        XrefWidthPolicy.ChainMatches(outer, "A > B").Should().BeTrue();
        XrefWidthPolicy.ChainMatches(outer, "B > A").Should().BeFalse();
        XrefWidthPolicy.ChainMatches(outer, "B").Should().BeFalse();
        XrefWidthPolicy.ChainMatches(empty, "A").Should().BeFalse();
        XrefWidthPolicy.ChainMatches("not json", "A").Should().BeFalse();
    }

    [Fact]
    public void APresentWidthThatIsNotProvenIsNeverReplacedByTheParameter()
    {
        // Codex F2b: 1000 mm drawn width, no transform → not priced; only a truly absent width uses the parameter.
        decimal Total(EngineerBoqDraft d) => EngineerBoqDraftExcelWriter.TotalAt(d, EngineerBoqDraftExcelWriter.BoqRows(d),
            EngineerBoqDraftExcelWriter.DefaultParameter(d), null);
        var millimetres = Build(new[] { Rec(Sm, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 200, "מטר", width: 1000, units: "Millimeters") });
        Total(millimetres).Should().Be(0m);
        millimetres.Elements.Single().Rule.Id.Should().Be("marking-transverse@unproven");
        millimetres.Warnings.Should().Contain(w => w.Topic == "רוחב משורטט לא מוכח" && w.AffectsTotal);
        // Even the record's own proven insertion does not make a millimetre width a metre width.
        Total(Build(new[] { Rec(Sm, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 200, "מטר", width: 1000, units: "Millimeters",
            xrefTransform: Proven) })).Should().Be(0m);

        var unreadable = Rec(null, "TR-MARK-WHT-810", "length", "polyline-length", 200, "מטר");
        unreadable.Measurement.Parameters["cad_width_units"] = "entity database drawing units; untransformed";
        unreadable.Measurement.Parameters["cad_polyline_constant_width_raw_status"] = "unavailable:Exception";
        unreadable.Measurement.Parameters["cad_polyline_segment_width_status"] = "invalid width observation; no width summary";
        Total(Build(new[] { unreadable })).Should().Be(0m, "a width that could not be read is unknown, not absent");

        var varying = Rec(null, "TR-MARK-WHT-810", "length", "polyline-length", 200, "מטר");
        varying.Measurement.Parameters["cad_entity_database_insunits"] = "Meters";
        varying.Measurement.Parameters["cad_polyline_constant_width_raw_status"] = "unavailable:Exception";
        varying.Measurement.Parameters["cad_polyline_width_min_raw"] = "0.1";
        varying.Measurement.Parameters["cad_polyline_width_max_raw"] = "0.5";
        Total(Build(new[] { varying })).Should().Be(0m, "a varying width is not one width");

        var zero = Rec(null, "TR-MARK-WHT-810", "length", "polyline-length", 200, "מטר", width: 0);
        zero.Measurement.Parameters["cad_polyline_width_min_raw"] = "0";
        zero.Measurement.Parameters["cad_polyline_width_max_raw"] = "0";
        Total(Build(new[] { zero })).Should().Be(5000m, "positive control: a width read as zero is no drawn width");
        Total(Build(new[] { Rec(Sm, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 200, "מטר") }))
            .Should().Be(5000m, "positive control: a line with no width at all uses the declared width");
        Total(Build(new[] { Rec(null, "TR-MARK-WHT-810", "length", "polyline-length", 200, "מטר", width: 1) }))
            .Should().Be(10000m, "positive control: a host metre width");
    }

    [Fact]
    public void XrefWidthPolicyAcceptsOnlyASimilarityWithMatchingUnits()
    {
        XrefWidthPolicy.UniformScale(Transform(1, 1, 1)).Should().BeApproximately(1, 1e-12);
        XrefWidthPolicy.UniformScale(Transform(2.5, 2.5, 2.5, "uniform")).Should().BeApproximately(2.5, 1e-12);
        XrefWidthPolicy.UniformScale(Transform(-2, 2, 2)).Should().BeApproximately(2, 1e-12, "a mirrored insert keeps widths");
        XrefWidthPolicy.UniformScale(Transform(2, 1, 1)).Should().BeNull();
        XrefWidthPolicy.UniformScale(Transform(2, 2, 2, "rigid")).Should().BeNull();
        XrefWidthPolicy.UniformScale(Transform(1, 1, 1, sourceUnits: "Millimeters")).Should().BeNull();
        // Equal column norms with a shear are not a similarity.
        var shear = "{\"space\":\"host\",\"chain\":[{\"xref\":\"x\",\"matrix\":[1,0.6,0,0, 0,0.8,0,0, 0,0,1,0, 0,0,0,1]}],\"units\":{\"source\":\"Meters\",\"host\":\"Meters\"}}";
        XrefWidthPolicy.UniformScale(shear).Should().BeNull();
        // Nested inserts compose: 2 × 0.5 = 1.
        var nested = "{\"space\":\"host\",\"chain\":[{\"xref\":\"a\",\"matrix\":[2,0,0,5, 0,2,0,0, 0,0,2,0, 0,0,0,1]}," +
                     "{\"xref\":\"b\",\"matrix\":[0,-0.5,0,0, 0.5,0,0,0, 0,0,0.5,0, 0,0,0,1]}],\"units\":{\"source\":\"Meters\",\"host\":\"Meters\"}}";
        XrefWidthPolicy.UniformScale(nested).Should().BeApproximately(1, 1e-12);
        XrefWidthPolicy.UniformScale("{\"chain\":[],\"units\":{\"source\":\"Meters\",\"host\":\"Meters\"},\"scale\":[3,3,3]}").Should().BeNull(
            "a declared scale must agree with the matrices");
        XrefWidthPolicy.UniformScale("[]").Should().BeNull();
        XrefWidthPolicy.UniformScale("{\"chain\":[{\"matrix\":[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,\"x\"]}],\"units\":{\"source\":\"Meters\",\"host\":\"Meters\"}}").Should().BeNull();

        var host = Rec(null, "TR-MARK-WHT-810", "length", "polyline-length", 10, "מטר");
        XrefWidthPolicy.HostWidthScale(host).Should().Be(1);
        var xref = Rec(Sm, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 10, "מטר");
        XrefWidthPolicy.HostWidthScale(xref).Should().BeNull();
        // A host-looking record measured through an XREF transform is still not a host entity.
        var disguised = Rec(null, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 10, "מטר");
        XrefWidthPolicy.HostWidthScale(disguised).Should().BeNull();
    }

    [Fact]
    public void ParametersAreValidatedAndABlankIsNeverZero()
    {
        foreach (var parameter in EngineerBoqLibrary.RoadsV1.Parameters)
        {
            parameter.Min.Should().BeLessThan(parameter.Max, parameter.Key);
            parameter.Accepts(parameter.DefaultValue).Should().BeTrue(parameter.Key);
        }
        var share = EngineerBoqLibrary.RoadsV1.Parameters.Single(p => p.Key == "FULL_DEPTH_SHARE");
        (share.Accepts(0), share.Accepts(1), share.Accepts(1.5), share.Accepts(-0.1), share.Accepts(double.NaN))
            .Should().Be((true, true, false, false, false));

        var draft = Build(new[] { Rec(Ha, "HW-HTCH-ROAD", "area", "hatch-area+xref-transform", 1000, "מ\"ר"), Rec(Ha, "PL-BIKE", "area", "hatch-area+xref-transform", 80, "מ\"ר") });
        var path = Path.Combine(Path.GetTempPath(), "mhd-engineer-draft-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            EngineerBoqDraftExcelWriter.Write(draft, path);
            using var zip = ZipFile.OpenRead(path);
            var parameters = Cells(zip, 4);
            var row = parameters.Single(c => c.Value.Text == "FULL_DEPTH_SHARE").Key[1..];
            parameters["D" + row].Number.Should().Be(0.4);
            parameters["G" + row].Formula.Should().Be($"IF(AND(ISNUMBER(D{row}),D{row}>=0,D{row}<=1),D{row},NA())");
            parameters["H" + row].Text.Should().Contain("0").And.Contain("1");

            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var sheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet4.xml")!.Open());
            var validations = sheet.Descendants(ns + "dataValidation").ToList();
            validations.Should().HaveCount(EngineerBoqLibrary.RoadsV1.Parameters.Count);
            var shareValidation = validations.Single(v => (string)v.Attribute("sqref")! == "D" + row);
            ((string)shareValidation.Attribute("type")!, (string)shareValidation.Element(ns + "formula1")!, (string)shareValidation.Element(ns + "formula2")!)
                .Should().Be(("decimal", "0", "1"));
            // Schema order: dataValidations after the sheet data and before the page setup.
            var order = sheet.Root!.Elements().Select(e => e.Name.LocalName).ToList();
            order.IndexOf("dataValidations").Should().BeGreaterThan(order.IndexOf("sheetData")).And.BeLessThan(order.IndexOf("pageMargins"));

            var summary = Cells(zip, 1);
            summary.Values.Should().ContainSingle(c => c.Text == "פרמטרים לא תקינים");
            summary.Values.Should().Contain(c => c.Formula != null &&
                c.Formula.Contains($"NOT(ISNUMBER('{EngineerBoqDraftExcelWriter.ParametersSheet}'!G4:G{3 + EngineerBoqLibrary.RoadsV1.Parameters.Count}))"));
            summary.Values.Should().Contain(c => c.Formula != null && c.Formula.Contains("\"הושלם\""));
            var boq = Cells(zip, 3);
            boq.Values.Where(c => c.Formula != null).Should().NotContain(c => c.Formula!.Contains($"'{EngineerBoqDraftExcelWriter.ParametersSheet}'!D"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AWorkbookValidationOutsideAnExplicitRangeIsRefused()
    {
        var wb = new MiniXlsx.Workbook { SheetName = "x" };
        wb.DataValidations.Add(new MiniXlsx.DecimalValidation("D4", 1, 0, "t", "e"));
        var path = Path.Combine(Path.GetTempPath(), "mhd-minixlsx-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            FluentActions.Invoking(() => MiniXlsx.Write(wb, path)).Should().Throw<ArgumentException>();
            wb.DataValidations.Clear();
            wb.DataValidations.Add(new MiniXlsx.DecimalValidation("D4;D5", 0, 1, "t", "e"));
            FluentActions.Invoking(() => MiniXlsx.Write(wb, path)).Should().Throw<ArgumentException>();
            wb.DataValidations.Clear();
            wb.DataValidations.Add(new MiniXlsx.DecimalValidation("D4", 0, 1, new string('t', 33), "e"));
            FluentActions.Invoking(() => MiniXlsx.Write(wb, path)).Should().Throw<ArgumentException>();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AnOutlineOfAHatchOnASiblingLayerIsAnAlternativeNotExtraWork()
    {
        var records = new[]
        {
            Rec(Ha, "HACTH-GINUN", "area", "hatch-area+xref-transform", 207.83, "מ\"ר"),
            Rec(Gm, "HW-HATCH-GARDEN", "area", "closed-polyline-area+xref-transform", 207.83, "מ\"ר"),
        };
        var draft = Build(records);

        draft.NotUsedAlternatives.Should().ContainSingle(g => g.Layer == "HW-HATCH-GARDEN");
        draft.Warnings.Should().NotContain(w => w.Topic == "שטח ללא הצללה");
    }

    [Fact]
    public void AnApprovedOutlineWithNoHatchAnywhereIsApplied()
    {
        var catalog = Catalog();
        var records = new[]
        {
            Rec(Gm, "HW-HATCH-GARDEN", "area", "closed-polyline-area+xref-transform", 222, "מ\"ר",
                classification: Approved(catalog, "U51.32.0290", catalog.FileHash)),
        };
        var draft = Build(records, catalog: catalog);

        var element = draft.Elements.Single();
        element.Rule.Id.Should().StartWith("profile-approved:HW-HATCH-GARDEN:U51.32.0290");
        element.IncludedQuantity.Should().Be(222);
    }

    [Fact]
    public void TheApprovalBridgeChoosesOneModelPerLayerAndKeepsEveryBlock()
    {
        var catalog = Catalog();
        var shelter = Approved(catalog, "U40.02.2340", catalog.FileHash);
        var edge = Approved(catalog, "U51.06.1900", catalog.FileHash);
        var records = new[]
        {
            Rec(Gm, "yy-bench", "count", "block-count+xref-transform", 1, "יח'", "BENCH-A", shelter),
            Rec(Gm, "yy-bench", "count", "block-count+xref-transform", 1, "יח'", "BENCH-A", shelter),
            Rec(Gm, "yy-bench", "count", "block-count+xref-transform", 1, "יח'", "BENCH-B", shelter),
            Rec(Gm, "yy-edge", "length", "polyline-length+xref-transform", 300, "מטר", classification: edge),
            Rec(Gm, "yy-edge", "length", "polyline-length+xref-transform", 200, "מטר", classification: edge),
            Rec(Ha, "yy-edge", "length", "closed-polyline-perimeter+xref-transform", 480, "מטר", classification: edge),
        };
        var draft = Build(records, catalog: catalog);

        draft.Elements.Where(e => e.Rule.Id.StartsWith("profile-approved:yy-bench", StringComparison.Ordinal))
            .Sum(e => e.IncludedQuantity).Should().Be(3);
        draft.Elements.Where(e => e.Rule.Id.StartsWith("profile-approved:yy-edge", StringComparison.Ordinal))
            .Sum(e => e.IncludedQuantity).Should().Be(500);
        draft.AccountedRecords.Should().Be(records.Length);
    }

    [Fact]
    public void AnApprovalNeverLiftsALibraryHoldBack()
    {
        var catalog = Catalog();
        var records = new[]
        {
            Rec(Gm, "TR-INNER-ISLAND-CURBSTONE", "length", "polyline-length+xref-transform", 990, "מטר",
                classification: Approved(catalog, "U51.06.1900", catalog.FileHash)),
        };
        var draft = Build(records, catalog: catalog);

        var element = draft.Elements.Single();
        element.Rule.Id.Should().Be("curb-inner-island+approved:U51.06.1900");
        element.IncludedQuantity.Should().Be(0);
        draft.Warnings.Should().Contain(w => w.Topic == "לא נכלל עד בדיקה");
    }

    private static CatalogSnapshot UnifiedLikeCatalog()
    {
        // Active-edition codes only (no U codes, no links): the library's U40.02.1260 is not listed here.
        var catalog = new CatalogSnapshot { SnapshotId = "SYNTHETIC-UNIFIED", FileHash = new string('c', 64) };
        catalog.Items["40.02.0254"] = new CatalogItem { Code = "40.02.0254", Description = "SYNTHETIC bike rack 'arc'", UnitRaw = "יח'" };
        catalog.Prices["40.02.0254"] = new PriceRecord { Code = "40.02.0254", Price = 906m, PriceBookId = "SYNTHETIC-UNIFIED" };
        catalog.Items["40.02.0003"] = new CatalogItem { Code = "40.02.0003", Description = "SYNTHETIC furniture by length", UnitRaw = "מטר" };
        catalog.Prices["40.02.0003"] = new PriceRecord { Code = "40.02.0003", Price = 10m, PriceBookId = "SYNTHETIC-UNIFIED" };
        return catalog;
    }

    [Fact]
    public void AnApprovedItemReplacesARecipeLineTheActiveEditionDoesNotListOnItsReference()
    {
        // byc (02.10, Codex 22:24): the bike-racks recipe emits U40.02.1260 (urban list); the active edition has no such
        // code and no link. The engineer approved 40.02.0254 for the layer: same sub-chapter (40.02) and unit (יח') as the
        // line's proven reference, exactly one line -> it replaces it. No link is created and nothing else changes.
        var catalog = UnifiedLikeCatalog();
        var record = Rec(Gm, "byc", "count", "block-count+xref-transform", 1, "יח'", "מתקן ל-2 אופניים - 100_250-1013471-ח1 גלריה",
            classification: Approved(catalog, "40.02.0254", catalog.FileHash));
        var draft = Build(new[] { record }, catalog: catalog);

        draft.Lines.Should().Contain(l => l.Emit.Code == "40.02.0254" && l.Price == 906m);
        draft.Lines.Should().NotContain(l => l.Emit.Code == "U40.02.1260");
        draft.Warnings.Should().NotContain(w => w.Topic == "שיוך מאושר לא הוחל");
        catalog.LibraryAliases.Should().BeEmpty();
    }

    [Fact]
    public void AnApprovedItemOfAnotherUnitDoesNotReplaceTheUnlistedLineAndSaysWhy()
    {
        var catalog = UnifiedLikeCatalog();
        var record = Rec(Gm, "byc", "count", "block-count+xref-transform", 1, "יח'", "מתקן ל-2 אופניים - 100_250-1013471-ח1 גלריה",
            classification: Approved(catalog, "40.02.0003", catalog.FileHash));
        var draft = Build(new[] { record }, catalog: catalog);

        draft.Lines.Should().NotContain(l => l.Emit.Code == "40.02.0003");
        var warning = draft.Warnings.Should().ContainSingle(w => w.Topic == "שיוך מאושר לא הוחל").Which;
        warning.Message.Should().Contain("שונה מיחידת הפריט שאושר");
    }

    [Fact]
    public void AnApprovalOnAModelCopyThatIsNotCountedDoesNotReplaceTheCountedLine()
    {
        // F-5 (b27 host, 02.10 23:19): byc is approved on the host file, but the bike-racks rule counts only the
        // geometric model (*-GM-*), where byc has no approval. The approved copy is not counted, so the approval must not
        // yield a 0-quantity priced line beside the unchanged counted line, nor claim it replaced it.
        var catalog = UnifiedLikeCatalog();
        var records = new[]
        {
            Rec(null, "byc", "count", "block-count", 25, "יח'", "מתקן ל-2 אופניים - 100_250-1013471-ח1 גלריה",
                classification: Approved(catalog, "40.02.0254", catalog.FileHash)),
            Rec(Gm, "byc", "count", "block-count+xref-transform", 16, "יח'", "מתקן ל-2 אופניים - 100_250-1013471-ח1 גלריה"),
            Rec(Gm, "PDF_tnua$305$EVEN_SAFA", "count", "block-count+xref-transform", 9, "יח'", "מתקן ל-2 אופניים - 100_250-1013471-ח1 גלריה"),
        };
        var draft = Build(records, catalog: catalog);

        draft.Lines.Should().NotContain(l => l.Emit.Code == "40.02.0254");
        DefaultRowQuantity(draft, "U40.02.1260").Should().Be(25);
        draft.Warnings.Should().NotContain(w => w.Topic == "שיוך מאושר הוחלף בשורת מתכון");
        var warning = draft.Warnings.Should().ContainSingle(w => w.Topic == "שיוך מאושר לא הוחל").Which;
        warning.Message.Should().Contain("40.02.0254").And.Contain("אינו המודל הראשי").And.Contain(Gm)
            .And.Contain("byc").And.Contain("PDF_tnua$305$EVEN_SAFA");
        warning.AffectsTotal.Should().BeTrue();
        var element = draft.Elements.Should().ContainSingle(e => e.Rule.Id.StartsWith("bike-racks", StringComparison.Ordinal)).Which;
        element.Rule.Id.Should().Be("bike-racks");
        element.IncludedQuantity.Should().Be(25);
        element.Sources.Should().ContainSingle(s => s.Group.SourceRole == DraftSourceRole.Host && !s.Included && !s.InPrimarySource);
    }

    [Fact]
    public void AHeldBackRuleKeepsItsApprovedVariantOnTheCountedModel()
    {
        // Codex 23:32: F-5 concerns copies that are not counted. A hold-back's 0 on the counted model is a decision the
        // engineer takes in the workbook; the approved variant stays, held back as its rule, and nothing says "not applied".
        var catalog = Catalog();
        var draft = Build(new[]
        {
            Rec(Gm, "TR-INNER-ISLAND-CURBSTONE", "length", "polyline-length+xref-transform", 50, "מטר",
                classification: Approved(catalog, "U51.06.3060", catalog.FileHash)),
        }, catalog: catalog);

        var variant = draft.Elements.Should().ContainSingle(e => e.Rule.Id == "curb-inner-island+approved:U51.06.3060").Which;
        variant.IncludedQuantity.Should().Be(0);
        variant.Sources.Should().OnlyContain(s => s.InPrimarySource && !s.Included);
        draft.Warnings.Should().NotContain(w => w.Topic == "שיוך מאושר לא הוחל");
        draft.Warnings.Should().ContainSingle(w => w.Topic == "שיוך מאושר הוחלף בשורת מתכון");
    }

    private const string Rack = "מתקן ל-2 אופניים - 100_250-1013471-ח1 גלריה";

    [Fact]
    public void AnApprovalOnAnUncountedLayerIsNotClaimedAsReplacedWhenAnotherLayerOfTheVariantIsCounted()
    {
        // Review 03.10 (F-5, mixed): host byc and GM PDF_tnua carry the same approval, GM byc carries none. The variant keeps
        // the counted PDF_tnua (9) only; byc's approval applies to no counted quantity and says so instead of "replaced".
        var catalog = UnifiedLikeCatalog();
        var draft = Build(new[]
        {
            Rec(null, "byc", "count", "block-count", 25, "יח'", Rack, classification: Approved(catalog, "40.02.0254", catalog.FileHash)),
            Rec(Gm, "byc", "count", "block-count+xref-transform", 16, "יח'", Rack),
            Rec(Gm, "PDF_tnua$305$EVEN_SAFA", "count", "block-count+xref-transform", 9, "יח'", Rack,
                classification: Approved(catalog, "40.02.0254", catalog.FileHash)),
        }, catalog: catalog);

        DefaultRowQuantity(draft, "40.02.0254").Should().Be(9);
        DefaultRowQuantity(draft, "U40.02.1260").Should().Be(16);
        draft.Warnings.Should().ContainSingle(w => w.Topic == "שיוך מאושר הוחלף בשורת מתכון")
            .Which.Message.Should().Contain("PDF_tnua$305$EVEN_SAFA").And.NotContain("byc");
        draft.Warnings.Should().ContainSingle(w => w.Topic == "שיוך מאושר לא הוחל")
            .Which.Message.Should().Contain("byc").And.Contain("אינו המודל הראשי").And.NotContain("PDF_tnua");
        draft.Elements.SelectMany(e => e.Sources).Should().ContainSingle(s => s.Group.SourceRole == DraftSourceRole.Host)
            .Which.Included.Should().BeFalse();
    }

    [Fact]
    public void AnApprovalOnTheHostIsAppliedWhenNoModelOfTheRuleIsLoaded()
    {
        // Review 03.10: the fold needs the counted model to be present. With the racks drawn only in the host (no GM xref),
        // the host is what is counted, and the approval applies.
        var catalog = UnifiedLikeCatalog();
        var draft = Build(new[]
        {
            Rec(null, "byc", "count", "block-count", 3, "יח'", Rack, classification: Approved(catalog, "40.02.0254", catalog.FileHash)),
        }, catalog: catalog);

        DefaultRowQuantity(draft, "40.02.0254").Should().Be(3);
        draft.Lines.Should().ContainSingle(l => l.Emit.Code == "40.02.0254").Which.Price.Should().Be(906m);
        draft.Warnings.Should().NotContain(w => w.Topic == "שיוך מאושר לא הוחל");
    }

    [Fact]
    public void TheCountedModelIsDecidedForTheRuleEvenWhenOnlyAVariantHoldsIt()
    {
        // Codex F1, mirror (review 03.10): the GM layer is approved to another item (a variant), the HA layer is not. The
        // recipe element then holds only the HA copy; HA must stay an excluded copy, because GM is the rule's counted model.
        var catalog = Catalog();
        var approval = Approved(catalog, "U51.06.2460", catalog.FileHash);
        approval.RuleKey = "layer:TR-GRDN-STONE|length";
        var draft = Build(new[]
        {
            Rec(Gm, "TR-GRDN-STONE", "length", "polyline-length+xref-transform", 100, "מטר", classification: approval),
            Rec(Ha, "0L-EV-GAN", "length", "polyline-length+xref-transform", 100, "מטר"),
        }, catalog: catalog);

        EngineerBoqDraftExcelWriter.TotalAt(draft, EngineerBoqDraftExcelWriter.BoqRows(draft),
            EngineerBoqDraftExcelWriter.DefaultParameter(draft), null).Should().Be(15000m);
        draft.Elements.Should().Contain(e => e.Rule.Id == "curb-garden+approved:U51.06.2460" && e.IncludedQuantity == 100);
        draft.Elements.SelectMany(e => e.Sources).Should().ContainSingle(s => s.Group.Source == Ha).Which.Included.Should().BeFalse();
    }

    [Fact]
    public void AnApprovalOnBothTheHostCopyAndTheCountedLayerIsAppliedOnceToTheCountedQuantity()
    {
        // Review 03.10: the state after following the refusal — host byc and GM byc both approved 40.02.0254. The counted GM
        // byc (16) takes the approved item once; the host copy stays an excluded copy; nothing is reported as not applied.
        var catalog = UnifiedLikeCatalog();
        var draft = Build(new[]
        {
            Rec(null, "byc", "count", "block-count", 25, "יח'", Rack, classification: Approved(catalog, "40.02.0254", catalog.FileHash)),
            Rec(Gm, "byc", "count", "block-count+xref-transform", 16, "יח'", Rack, classification: Approved(catalog, "40.02.0254", catalog.FileHash)),
            Rec(Gm, "PDF_tnua$305$EVEN_SAFA", "count", "block-count+xref-transform", 9, "יח'", Rack),
        }, catalog: catalog, context: Context() with { RecordedEstimateDecisions = 1 });

        DefaultRowQuantity(draft, "40.02.0254").Should().Be(16);
        DefaultRowQuantity(draft, "U40.02.1260").Should().Be(9);
        // Codex 03.10 13:07: the host copy did not replace a line; only the counted GM byc did.
        draft.Warnings.Should().ContainSingle(w => w.Topic == "החלטות שמורות בפרופיל").Which.Message.Should()
            .Contain("1 קבוצות שבהן הפריט המאושר החליף שורת מתכון").And.Contain("ו-0 שלא הוחלו");
        draft.Warnings.Should().NotContain(w => w.Topic == "שיוך מאושר לא הוחל");
        draft.Warnings.Should().ContainSingle(w => w.Topic == "שיוך מאושר הוחלף בשורת מתכון");
        var copy = draft.Elements.SelectMany(e => e.Sources).Should().ContainSingle(s => s.Group.SourceRole == DraftSourceRole.Host).Which;
        copy.Included.Should().BeFalse();
        copy.InPrimarySource.Should().BeFalse();
        var rows = EngineerBoqDraftExcelWriter.BoqRows(draft);
        EngineerBoqDraftExcelWriter.ApprovalNote(rows.Single(r => r.RowKey == "40.02.0254")).Should().StartWith("הסעיף הזה אושר");
        EngineerBoqDraftExcelWriter.ApprovalNote(rows.Single(r => r.RowKey == "U40.02.1260")).Should().BeNull();
    }

    [Fact]
    public void TheProfileSummaryAndTheRowNoteTreatAnApprovalOnAnUncountedCopyAsNotApplied()
    {
        // Review 03.10: the summary counters and the BoQ row note of the F-5 case.
        var catalog = UnifiedLikeCatalog();
        var draft = Build(new[]
        {
            Rec(null, "byc", "count", "block-count", 25, "יח'", Rack, classification: Approved(catalog, "40.02.0254", catalog.FileHash)),
            Rec(Gm, "byc", "count", "block-count+xref-transform", 16, "יח'", Rack),
        }, catalog: catalog, context: Context() with { RecordedEstimateDecisions = 1 });

        draft.Warnings.Should().ContainSingle(w => w.Topic == "החלטות שמורות בפרופיל").Which.Message.Should()
            .Contain("0 קבוצות שבהן הפריט המאושר החליף שורת מתכון").And.Contain("ו-1 שלא הוחלו");
        EngineerBoqDraftExcelWriter.ApprovalNote(EngineerBoqDraftExcelWriter.BoqRows(draft).Single(r => r.RowKey == "U40.02.1260"))
            .Should().BeNull();
    }

    [Fact]
    public void AGatedApprovalOnAnUncountedCopyGivesNoParameterHint()
    {
        // Review 03.10: host HW-HTCH-ROAD is approved to U51.04.0130, a pavement line gated by FULL_DEPTH_SHARE; the rule
        // counts only HA. No share of the host copy is included at any parameter value, so no parameter hint is given.
        // The same approval on the counted HA hatch still gets the hint.
        var catalog = Catalog();
        var onCopy = Build(new[]
        {
            Rec(null, "HW-HTCH-ROAD", "area", "hatch-area", 1000, "מ\"ר", classification: Approved(catalog, "U51.04.0130", catalog.FileHash)),
            Rec(Ha, "HW-HTCH-ROAD", "area", "hatch-area+xref-transform", 1000, "מ\"ר"),
        }, catalog: catalog);
        var onCounted = Build(new[]
        {
            Rec(Ha, "HW-HTCH-ROAD", "area", "hatch-area+xref-transform", 1000, "מ\"ר", classification: Approved(catalog, "U51.04.0130", catalog.FileHash)),
        }, catalog: catalog);

        onCopy.Warnings.Should().NotContain(w => w.Topic == "שיוך מאושר תלוי בפרמטר");
        onCounted.Warnings.Should().Contain(w => w.Topic == "שיוך מאושר תלוי בפרמטר");
        DefaultRowQuantity(onCopy, "U51.04.0130").Should().Be(DefaultRowQuantity(onCounted, "U51.04.0130"));
    }

    [Fact]
    public void AnApprovalOnTheCountedModelReplacesTheLineAndTheHostCopyStaysAnExcludedCopy()
    {
        var catalog = UnifiedLikeCatalog();
        var records = new[]
        {
            Rec(null, "byc", "count", "block-count", 25, "יח'", "מתקן ל-2 אופניים - 100_250-1013471-ח1 גלריה"),
            Rec(Gm, "byc", "count", "block-count+xref-transform", 16, "יח'", "מתקן ל-2 אופניים - 100_250-1013471-ח1 גלריה",
                classification: Approved(catalog, "40.02.0254", catalog.FileHash)),
            Rec(Gm, "PDF_tnua$305$EVEN_SAFA", "count", "block-count+xref-transform", 9, "יח'", "מתקן ל-2 אופניים - 100_250-1013471-ח1 גלריה"),
        };
        var draft = Build(records, catalog: catalog);

        var approved = draft.Elements.Should().ContainSingle(e => e.Rule.Id == "bike-racks+approved:40.02.0254").Which;
        approved.IncludedQuantity.Should().Be(16);
        draft.Lines.Should().ContainSingle(l => l.Emit.Code == "40.02.0254").Which.Price.Should().Be(906m);
        DefaultRowQuantity(draft, "40.02.0254").Should().Be(16);
        DefaultRowQuantity(draft, "U40.02.1260").Should().Be(9);
        draft.Elements.Where(e => e.Rule.Id.StartsWith("bike-racks", StringComparison.Ordinal)).Sum(e => e.IncludedQuantity).Should().Be(25);
        draft.Warnings.Should().NotContain(w => w.Topic == "שיוך מאושר לא הוחל");
        draft.Warnings.Should().ContainSingle(w => w.Topic == "שיוך מאושר הוחלף בשורת מתכון");
        var copy = draft.Elements.SelectMany(e => e.Sources).Should().ContainSingle(s => s.Group.SourceRole == DraftSourceRole.Host).Which;
        copy.Included.Should().BeFalse();
        copy.InPrimarySource.Should().BeFalse();
    }

    [Fact]
    public void AnApprovedTopLayerReplacesOnlyItsLineOfThePavementRecipe()
    {
        var catalog = Catalog();
        var records = new[]
        {
            Rec(Ha, "HW-HTCH-ROAD", "area", "hatch-area+xref-transform", 1000, "מ\"ר",
                classification: Approved(catalog, "U51.04.1830", catalog.FileHash)),
        };
        var draft = Build(records, catalog: catalog);

        var codes = draft.Lines.Select(l => l.Emit.Code).ToList();
        codes.Should().Contain(new[] { "U51.04.1830", "U51.04.0130", "U51.04.0160", "U51.03.0010", "U51.02.0110", "U51.04.2530" });
        codes.Should().NotContain("U51.04.1820");
        DefaultRowQuantity(draft, "U51.04.1830").Should().Be(1000);
    }

    [Fact]
    public void SidewalkRecipeAndAnApprovedFinishThatAParameterZeroesAreBothVisible()
    {
        var catalog = Catalog();
        var plain = Build(new[] { Rec(Ha, "HW_HA_SIDEWALK", "area", "hatch-area+xref-transform", 1000, "מ\"ר") });
        DefaultRowQuantity(plain, "U51.06.8040").Should().Be(1000);
        DefaultRowQuantity(plain, "U51.04.2310").Should().Be(0);
        DefaultRowQuantity(plain, "U51.04.2420").Should().Be(0);
        DefaultRowQuantity(plain, "U51.03.0010").Should().BeApproximately(200, 1e-9);
        DefaultRowQuantity(plain, "U51.02.0110").Should().Be(1000);
        DefaultRowQuantity(plain, "U51.01.2000").Should().Be(1000);

        var approved = Build(new[]
        {
            Rec(Ha, "HW_HA_SIDEWALK", "area", "hatch-area+xref-transform", 1000, "מ\"ר",
                classification: Approved(catalog, "U51.04.2310", catalog.FileHash)),
        }, catalog: catalog);
        approved.Warnings.Should().Contain(w => w.Topic == "שיוך מאושר תלוי בפרמטר" && w.AffectsTotal && w.Message.Contains("SIDEWALK_PAVER_SHARE"));
    }

    [Fact]
    public void AFailureOnAModelThatIsNotIncludedStaysInTheChecks()
    {
        var records = new[]
        {
            Rec(Gm, "HW-CURB", "length", "polyline-length+xref-transform", 100, "מטר"),
            Rec(Ha, "HW-CURB", "length", "polyline-length+xref-transform", 50, "מטר"),
        };
        var failure = Finding(EstimateFindingCodes.MeasurementFailed, "1 supported construction objects could not be measured",
            $"measurement-kind=length; source=x; sha256=y; handle=1/7; xref={Ha}; layer={Ha}|HW-CURB; entity=Line; method=line-length");
        var draft = Build(records, new[] { failure });

        draft.Warnings.Should().Contain(w => w.Topic == "עצמים שלא נמדדו" && w.Message.Contains("HW-CURB"));
    }

    [Fact]
    public void AnExclusionNeverHidesAFailedMeasurement()
    {
        var key = new QuantityClassification { RuleKey = "layer:HW-CURB|length" };
        var records = new[]
        {
            Rec(Gm, "HW-CURB", "length", "polyline-length+xref-transform", 100, "מטר", classification: key),
            Rec(Gm, "HW-CURB", "length", "polyline-length+xref-transform", 0, "מטר", classification: new QuantityClassification { RuleKey = "layer:HW-CURB|length" }),
        };
        var context = Context() with
        {
            ExcludedRuleDecisions = new Dictionary<string, string> { ["layer:HW-CURB|length"] = "SYNTHETIC engineer" },
        };
        var draft = Build(records, context: context);

        draft.ExcludedByDecision.Should().BeEmpty();
        draft.Elements.Single().IncludedQuantity.Should().Be(100);
        draft.Warnings.Should().Contain(w => w.Topic == "החרגה נדחתה" && w.AffectsTotal);
    }

    [Fact]
    public void TheQuantityFactorScalesMeasuredContributionsOnlyAndAWrongUnitRowNeverTakesAPrice()
    {
        var catalog = Catalog();
        catalog.Items["U51.06.1900"] = new CatalogItem { Code = "U51.06.1900", Description = "SYNTHETIC curb per m2", UnitRaw = "מ\"ר" };
        var records = new[]
        {
            Rec(Sm, "TR-MARK-ARW-YLW", "count", "block-count+xref-transform", 1, "יח'", "813(814)"),
            Rec(Sm, "TR-MARK-ARW-YLW", "count", "block-count+xref-transform", 1, "יח'", "813(814)"),
            Rec(Sm, "TR-MARK-WHT-810", "length", "polyline-length+xref-transform", 50, "מטר"),
            Rec(Sm, "BUS-STATION-NEW", "count", "block-count+xref-transform", 1, "יח'", "SHELTER"),
            Rec(Gm, "HW-CURB", "length", "polyline-length+xref-transform", 100, "מטר"),
        };
        var draft = Build(records, catalog: catalog);
        var path = Path.Combine(Path.GetTempPath(), "mhd-engineer-draft-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            EngineerBoqDraftExcelWriter.Write(draft, path);
            using var zip = ZipFile.OpenRead(path);
            var boq = Cells(zip, 3);
            var parameters = Cells(zip, 4);
            var globalRow = parameters.Single(c => c.Value.Text == EngineerBoqLibrary.GlobalFactorKey).Key[1..];
            var global = $"'{EngineerBoqDraftExcelWriter.ParametersSheet}'!G{globalRow}";
            var painted = boq.Single(c => c.Value.Text == "U51.32.0290").Key[1..];
            var mixed = boq["E" + painted].Formula!;
            mixed.Should().Contain($"*{global}+'{EngineerBoqDraftExcelWriter.BaseSheet}'!F");
            mixed.Split('+').Last().Should().NotContain(global);
            var shelter = boq.Single(c => c.Value.Text == "U40.02.2340").Key[1..];
            boq["E" + shelter].Formula.Should().NotContain(global);
            var curb = boq.Single(c => c.Value.Text == "U51.06.1900").Key[1..];
            boq["G" + curb].Formula.Should().BeNull();
            boq["G" + curb].Number.Should().Be(0);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LatinNamesInsideHebrewTextAreIsolated()
    {
        EngineerBoqDraftExcelWriter.IsolateLatin("88 בלוקים בשכבה 24-TAMRUR-BUS_ST")
            .Should().Be("88 בלוקים בשכבה \u200E24-TAMRUR-BUS_ST\u200E");
        EngineerBoqDraftExcelWriter.IsolateLatin("TR-MARK-811").Should().Be("TR-MARK-811");
        EngineerBoqDraftExcelWriter.IsolateLatin("1,019.18 מטר").Should().Be("1,019.18 מטר");
        EngineerBoqDraftExcelWriter.IsolateLatin("בעזרת עוזר ה-AI)").Should().Be("בעזרת עוזר ה-‎AI‎)");
        EngineerBoqDraftExcelWriter.IsolateLatin("לתמחר כ-U51.06.3060.").Should().Be("לתמחר כ-‎U51.06.3060‎.");
        EngineerBoqDraftExcelWriter.IsolateLatin("סימון 804 (-ZEVA-804)").Should().Be("סימון 804 (‎-ZEVA-804‎)");
    }

    [Fact]
    public void RowTotalsRoundHalfAwayFromZeroLikeExcel()
    {
        EngineerBoqDraftExcelWriter.RowTotal(0.125, 1m).Should().Be(0.13m);
        EngineerBoqDraftExcelWriter.RowTotal(2.5, 0.005m).Should().Be(0.01m);
    }

    [Fact]
    public void RefusesToWriteWhenARecordWouldBeLost()
    {
        var draft = Build(new[] { Rec(Gm, "HW-CURB", "length", "polyline-length+xref-transform", 10, "מטר") });
        var broken = new EngineerBoqDraft
        {
            Library = draft.Library, Context = draft.Context, Catalog = draft.Catalog, ChapterTitles = draft.ChapterTitles,
            Elements = draft.Elements, Lines = draft.Lines, UnmappedDesign = draft.UnmappedDesign,
            NotUsedAlternatives = draft.NotUsedAlternatives, Existing = draft.Existing, Utilities = draft.Utilities,
            DraftingAids = draft.DraftingAids, CorridorVolumes = draft.CorridorVolumes, Sources = draft.Sources,
            Warnings = draft.Warnings, Findings = draft.Findings, RecordCount = draft.RecordCount + 1, InvalidMeasurementRecords = 0,
            UnmeasuredObjectsTotal = 0, UnmeasuredDesignObjects = 0,
        };
        var path = Path.Combine(Path.GetTempPath(), "never-" + Guid.NewGuid().ToString("N") + ".xlsx");
        var act = () => EngineerBoqDraftExcelWriter.Write(broken, path);
        act.Should().Throw<InvalidOperationException>().WithMessage("*שגיאה פנימית בטיוטה*");
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void LibraryIsConsistentAndRulesResolveByLayerAndBlock()
    {
        var library = EngineerBoqLibrary.RoadsV1;
        library.Rules.Select(r => r.Id).Should().OnlyHaveUniqueItems();
        library.Parameters.Select(p => p.Key).Should().OnlyHaveUniqueItems().And.Contain(EngineerBoqLibrary.GlobalFactorKey);
        library.Rules.SelectMany(r => r.Emits).SelectMany(e => e.ParameterKeys)
            .Should().OnlyContain(key => library.Parameters.Any(p => p.Key == key));
        library.Parameters.Should().OnlyContain(p => p.Key == EngineerBoqLibrary.GlobalFactorKey ||
            library.Rules.SelectMany(r => r.Emits).Any(e => e.ParameterKeys.Contains(p.Key)));
        EngineerBoqLibrary.Glob("HW-HTCH-ROAD", "HW-HTCH-ROAD").Should().BeTrue();
        EngineerBoqLibrary.Glob("hw-htch-road", "HW-HTCH-ROAD").Should().BeTrue();
        EngineerBoqLibrary.Glob("HW-HTCH-ROAD-2", "HW-HTCH-ROAD").Should().BeFalse();
        library.RuleFor("TR-MARK-ARW-YLW").Should().BeNull();
        library.RuleFor("TR-MARK-ARW-YLW", "813(814)")!.Id.Should().Be("marking-arrows");
        library.RuleFor("TR-MARK-ARW-YLW", "M")!.Id.Should().Be("marking-m-blocks");
        library.RuleFor("TR-MARK-ARW-YLW-926", "arrow-D")!.Id.Should().Be("bike-arrows");
        library.RuleFor("TR-MARK-WHT-812-BIKE")!.Id.Should().Be("marking-lines");
        library.RuleFor("TR-MARK-WHT-811")!.Id.Should().Be("marking-crossings");
        library.RuleFor("TR-MARK-811")!.Id.Should().Be("marking-crossing-lines");
        library.RuleFor("TR-MARK-WHT-815")!.Id.Should().Be("marking-transverse");
        library.RuleFor("TR-MARK-YLW-3-3")!.Id.Should().Be("marking-dash-3-3");
        library.RuleFor("HW-CURB-ILND")!.Id.Should().Be("curb-island-hw");
        library.RuleFor("-ZEVA-804")!.Id.Should().Be("marking-804");
        library.Rules.Where(r => !r.IncludedByDefault).Should().OnlyContain(r => r.SeparateBoqRow);
    }

    /// <summary>
    /// Opt-in: builds the real draft from a saved scan. Set MHD_ENGINEER_DRAFT_REAL_RUN_DIR to the run folder,
    /// MHD_ENGINEER_DRAFT_PRICEBOOK to the price-list xlsx and optionally MHD_ENGINEER_DRAFT_OUT to keep the workbook.
    /// </summary>
    [Fact]
    public void RealSavedScanProducesAnAccountedDraft()
    {
        var runDir = Environment.GetEnvironmentVariable("MHD_ENGINEER_DRAFT_REAL_RUN_DIR");
        var priceBook = Environment.GetEnvironmentVariable("MHD_ENGINEER_DRAFT_PRICEBOOK");
        if (string.IsNullOrWhiteSpace(runDir) || string.IsNullOrWhiteSpace(priceBook)) return;
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var records = JsonSerializer.Deserialize<List<NeutralQuantityRecord>>(
            File.ReadAllText(Path.Combine(runDir, "neutral_quantity_records.json")), options)!;
        var findings = JsonSerializer.Deserialize<List<DeliveryFinding>>(
            File.ReadAllText(Path.Combine(runDir, "quantity_preflight.json")), options)!;
        var catalog = PriceBookXlsxLoader.Load(priceBook, "real-opt-in");
        var titles = PriceBookChapterTitles.Read(priceBook);
        var draft = EngineerBoqDraftBuilder.Build(records, findings, catalog, titles, EngineerBoqLibrary.RoadsV1,
            new EngineerDraftContext("נת\"צ מודיעין (שמור)", "6422", Path.GetFileName(runDir),
                records.First().Source.Drawing, "נתיבי ישראל עירוני 08/2025", new[] { "GFC111" }, "test",
                Classifier: MahodAI.CivilDelivery.Estimate.Recognition.LocalFamilyClassifier.Instance));

        draft.AccountedRecords.Should().Be(records.Count);
        draft.Lines.Should().NotBeEmpty();
        draft.Lines.Should().OnlyContain(l => l.Element.Sources.All(s => s.Group.SourceRole != DraftSourceRole.Survey &&
                                                                         s.Group.SourceRole != DraftSourceRole.ExistingUtilities));
        titles.Should().ContainKey("51.04");
        var output = Environment.GetEnvironmentVariable("MHD_ENGINEER_DRAFT_OUT");
        var path = string.IsNullOrWhiteSpace(output)
            ? Path.Combine(Path.GetTempPath(), "mhd-engineer-draft-real-" + Guid.NewGuid().ToString("N") + ".xlsx")
            : output;
        var result = EngineerBoqDraftExcelWriter.Write(draft, path);
        result.AccountedRecords.Should().Be(records.Count);

        // The approval bridge against the real price-list identity: one meaningless layer taught once.
        var taught = new NeutralQuantityRecord
        {
            RecordId = "synthetic-taught-1", ProjectProfileId = "6422", RunId = "synthetic",
            Source = new QuantitySource { Drawing = "synthetic.dwg", DrawingHash = new string('a', 64), Handle = "1", EntityType = "Polyline",
                Layer = "6422-GM-MODEL-NATAZ|asdasd23423", Xref = "6422-GM-MODEL-NATAZ" },
            Measurement = new QuantityMeasurement { Kind = "length", Method = "polyline-length+xref-transform", RawValue = 100, Unit = "מטר",
                Parameters = new Dictionary<string, string>() },
            Classification = new QuantityClassification
            {
                RuleKey = "layer:asdasd23423|length", CandidateCatalogCode = "U51.06.1900", ApprovedCatalogId = catalog.SnapshotId,
                ApprovedCatalogHash = catalog.FileHash, ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(catalog.Items["U51.06.1900"]),
                MappingApprovedBy = "SYNTHETIC", MappingApprovedAtUtc = DateTime.UtcNow,
            },
        };
        var bridged = EngineerBoqDraftBuilder.Build(records.Append(taught).ToList(), findings, catalog, titles, EngineerBoqLibrary.RoadsV1, draft.Context);
        bridged.Elements.Should().ContainSingle(e => e.Rule.Id.StartsWith("profile-approved:asdasd23423:U51.06.1900", StringComparison.Ordinal));
        bridged.Lines.Single(l => l.Element.Rule.Id.StartsWith("profile-approved:asdasd23423", StringComparison.Ordinal)).Price.Should().NotBeNull();

        // Approvals on real 6422 layers (synthetic decisions, real groups and real price list): the library's
        // protections must hold — a hatch outline counted once, an outline length never priced, one model only.
        QuantityClassification Approve(string code) => new()
        {
            RuleKey = "synthetic", CandidateCatalogCode = code, ApprovedCatalogId = catalog.SnapshotId, ApprovedCatalogHash = catalog.FileHash,
            ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(catalog.Items[code]), MappingApprovedBy = "SYNTHETIC",
            MappingApprovedAtUtc = DateTime.UtcNow,
        };
        string Leaf(NeutralQuantityRecord r) => r.Source.Layer.Split('|').Last();
        var approvedRecords = records.Select(r =>
        {
            var classification = Leaf(r) switch
            {
                "BL-TR-ARRW" when r.Measurement.Kind == "area" => Approve("U51.32.0290"),
                "PL-BIKE" when r.Measurement.Kind == "length" => Approve("U51.06.2930"),
                "HW-CURB" when r.Measurement.Kind == "length" => Approve("U51.06.2460"),
                _ => null,
            };
            return classification == null ? r : new NeutralQuantityRecord
            {
                RecordId = r.RecordId, ProjectProfileId = r.ProjectProfileId, RunId = r.RunId, Source = r.Source,
                Measurement = r.Measurement, Classification = classification,
            };
        }).ToList();
        var taughtReal = EngineerBoqDraftBuilder.Build(approvedRecords, findings, catalog, titles, EngineerBoqLibrary.RoadsV1, draft.Context);
        taughtReal.AccountedRecords.Should().Be(approvedRecords.Count);
        var arrowHatch = records.Where(r => Leaf(r) == "BL-TR-ARRW" && r.Measurement.Kind == "area" && r.Measurement.Method.StartsWith("hatch", StringComparison.Ordinal))
            .Sum(r => r.Measurement.RawValue);
        taughtReal.Elements.Where(e => e.Rule.Id.StartsWith("profile-approved:BL-TR-ARRW", StringComparison.Ordinal))
            .Sum(e => e.IncludedQuantity).Should().BeApproximately(arrowHatch, 1e-6);
        taughtReal.Elements.Should().NotContain(e => e.Rule.Id.StartsWith("profile-approved:PL-BIKE", StringComparison.Ordinal));
        taughtReal.NotUsedAlternatives.Should().Contain(g => g.Layer == "PL-BIKE" && g.Kind == "length" && g.Reason.Contains("יש שיוך מאושר"));
        var curb = taughtReal.Elements.Single(e => e.Rule.Id == "curb-road+approved:U51.06.2460");
        curb.Sources.Where(s => s.Included).Should().OnlyContain(s => s.Group.Source.Contains("-GM-", StringComparison.Ordinal));
        File.WriteAllText(Path.ChangeExtension(path, ".taught.json"), JsonSerializer.Serialize(new
        {
            arrowHatch, arrowIncluded = taughtReal.Elements.Where(e => e.Rule.Id.StartsWith("profile-approved:BL-TR-ARRW", StringComparison.Ordinal)).Sum(e => e.IncludedQuantity),
            curbIncluded = curb.IncludedQuantity, warnings = taughtReal.Warnings.Where(w => w.Topic.StartsWith("שיוך", StringComparison.Ordinal)),
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var rows = EngineerBoqDraftExcelWriter.BoqRows(draft);
        var parameter = EngineerBoqDraftExcelWriter.DefaultParameter(draft);
        File.WriteAllText(Path.ChangeExtension(path, ".summary.json"), JsonSerializer.Serialize(new
        {
            records = records.Count, draft.InvalidMeasurementRecords, result.LineCount, result.BoqRowCount, result.PricedLineCount,
            result.PricedTotalAtDefaults, draft.UnmeasuredObjectsTotal, draft.UnmeasuredDesignObjects,
            rows = rows.Select(r => new
            {
                r.RowKey, r.Head.Item?.Description, unit = r.Head.Item?.UnitRaw, r.Price, confidence = r.Confidence.ToString(),
                quantity = EngineerBoqDraftExcelWriter.RowQuantity(r, parameter, null),
                total = r.Price is { } p ? EngineerBoqDraftExcelWriter.RowTotal(EngineerBoqDraftExcelWriter.RowQuantity(r, parameter, null), p) : 0m,
                elements = r.Lines.Select(l => l.Element.Rule.Id).Distinct(),
            }),
            elements = draft.Elements.Select(e => new
            {
                e.Rule.Id, e.Rule.Element, e.IncludedQuantity, e.Unit, e.UnmeasuredObjects, e.OverlapPairs,
                sources = e.Sources.Select(s => new { s.Group.Source, s.Group.Layer, s.Group.Block, s.Group.MethodClass, s.Group.Quantity, s.Included }),
            }),
            unmapped = draft.UnmappedDesign.Take(60).Select(g => new { g.Source, g.Layer, g.Block, g.Kind, g.Quantity, g.Unit, g.Role, g.Reason }),
            alternatives = draft.NotUsedAlternatives.Select(g => new { g.Source, g.Layer, g.Kind, g.MethodClass, g.Quantity, g.Reason }),
            existing = draft.Existing.Sum(g => g.Count), utilities = draft.Utilities.Sum(g => g.Count),
            aids = draft.DraftingAids.Sum(g => g.Count), corridor = draft.CorridorVolumes.Sum(g => g.Count),
            warnings = draft.Warnings, findings = draft.Findings,
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.WriteAllText(Path.ChangeExtension(path, ".recognition.json"), JsonSerializer.Serialize(new
        {
            classifier = draft.ClassifierIdentity,
            unmappedGroups = draft.UnmappedDesign.Count,
            unmappedRecords = draft.UnmappedDesign.Sum(g => g.Count),
            proposed = draft.RecognitionProposals.Where(p => p.Status == MahodAI.CivilDelivery.Estimate.Recognition.RecognitionStatus.Proposed)
                .GroupBy(p => p.FamilyId).Select(g => new { family = g.Key, groups = g.Count(), records = g.Sum(p => p.RecordIds.Count) }),
            abstainedRecords = draft.RecognitionProposals.Where(p => p.Status == MahodAI.CivilDelivery.Estimate.Recognition.RecognitionStatus.Abstained)
                .Sum(p => p.RecordIds.Count),
            proposals = draft.RecognitionProposals.Select(p => new
            {
                p.GroupId, status = p.Status.ToString(), p.FamilyId, records = p.RecordIds.Count, refs = p.EvidenceRefs.Select(r => r.Key),
                p.Observed, alternatives = p.Alternatives.Select(a => a.FamilyId), p.MissingDetails,
            }),
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        if (string.IsNullOrWhiteSpace(output)) File.Delete(path);
    }
}
