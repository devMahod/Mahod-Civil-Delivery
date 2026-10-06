using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>Production writer fixture, NOT a native export or an engineering approval.
/// Raw lengths transcribed from native75 q-disc-8BB5AA-length / q-disc-8BB86A-length;
/// source identities and approval below are explicitly synthetic. The native catalog
/// unit price is retained at full precision. No production file is read or changed.</summary>
public sealed class EstimateScopeWarningLayoutTests
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const decimal Price = 94.720012860000011m;
    private static readonly string Hash = new('a', 64);
    private const string Code = "U51.06.1900";

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ActualWriterMergesOnlyScopeNotices_PreservingTextsTotalsAndTypedPrecision(bool partial, bool civilShape)
    {
        var profile = new ProjectProfile { ProfileId = "TEST-ONLY-SCOPE-LAYOUT", ProjectName = "FIXTURE — NOT NATIVE EXPORT" };
        profile.Estimate.QuantitySources.SourceScopePolicy = EstimatePreflightPolicy.DiscoverAllSourceScopePolicy;
        profile.Estimate.QuantitySources.XrefPolicy = EstimatePreflightPolicy.IncludeXrefsPolicy;
        var catalog = new CatalogSnapshot { SnapshotId = "TEST-ONLY-CATALOG", FileHash = Hash };
        catalog.Items[Code] = new() { Code = Code, Description = "בדיקת תוכנה בלבד — אבן שפה", UnitRaw = "מטר" };
        catalog.Prices[Code] = new() { Code = Code, Price = Price, PriceBookId = catalog.SnapshotId, SourceHash = Hash };
        var records = new[] { ("8BB5AA", 45.84665579747579), ("8BB86A", 69.40510369324552) }.Select(pair => new NeutralQuantityRecord
        {
            RecordId = "TEST-ONLY-" + pair.Item1, ProjectProfileId = profile.ProfileId, RunId = "TEST-ONLY-SCOPE-LAYOUT",
            Source = new() { Drawing = "TEST-ONLY.dwg", DrawingPath = @"C:\SYNTHETIC\TEST-ONLY.dwg", DrawingHash = Hash,
                Handle = pair.Item1, EntityType = "POLYLINE", Layer = "HW-CURB" },
            Measurement = new() { Kind = "length", Method = "polyline-length", Unit = "מטר", RawValue = pair.Item2 },
            Classification = new() { RuleKey = "layer:HW-CURB|length", CandidateCatalogCode = Code,
                ApprovedCatalogId = catalog.SnapshotId, ApprovedCatalogHash = Hash,
                ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(catalog.Items[Code]),
                MappingApprovedBy = "TEST-ONLY software fixture; not engineering approval", MappingApprovedAtUtc = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc) },
        }).ToArray();
        var context = EstimateTraceIdentity.InferHeadless(records, profile);
        if (civilShape) context = context with { SourceSnapshotKind = EstimateBuildContext.CivilLiveSaved,
            SourceDbMod = 0, SourceDatabaseRevision = "TEST-ONLY-CIVIL-SHAPE-NOT-LIVE" };
        var findings = partial ? new[] { new DeliveryFinding { Code = EstimatePreflightPolicy.EarthworksNotAssessedCode,
            Domain = "estimate", Severity = FindingSeverity.ReviewRequired, Title = "TEST-ONLY — עבודות עפר לא הוכרעו" } } : Array.Empty<DeliveryFinding>();
        var built = EstimateBuilder.Build(records, catalog, profile, "TEST-ONLY-SCOPE-LAYOUT", findings, context);
        Assert.Equal(2, built.Lines.Count); Assert.All(built.Lines, line => Assert.True(line.IncludedInTotals));
        var quantity = 115.2518m; var total = Math.Round(quantity * Price, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(total, built.CleanTotal);
        var parent = Environment.GetEnvironmentVariable("MAHOD_SCOPE_WARNING_FIXTURE_DIR") ?? Path.GetTempPath();
        var directory = Path.Combine(parent, "scope-layout-" + Guid.NewGuid().ToString("N"));
        var name = $"FIXTURE-NOT-NATIVE-{(partial ? "partial" : "full")}-{(civilShape ? "civil-shape" : "neutral")}";
        var options = new EstimateExcelWriter.WriteOptions(ProjectTitle: "בדיקת תוכנה בלבד — FIXTURE, NOT NATIVE EXPORT", PreparedBy: "TEST-ONLY");
        var output = partial ? EstimateExcelWriter.WritePartialPricedDraft(built, directory, name, options) : EstimateExcelWriter.Write(built, directory, name, options);
        using var zip = ZipFile.OpenRead(output.XlsxPath);
        using var sheetStream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(); var sheet = XDocument.Load(sheetStream);
        using var styleStream = zip.GetEntry("xl/styles.xml")!.Open(); var styles = XDocument.Load(styleStream);
        var scope = civilShape ? EstimatePreflightPolicy.CompleteScopeNotice : EstimatePreflightPolicy.NeutralRecordScopeNotice;
        var notices = partial ? new[] { EstimatePartialPricedDraftPolicy.DraftNotice, scope } : new[] { scope };
        var merges = sheet.Descendants(Ns + "mergeCell").Select(node => (string)node.Attribute("ref")!).ToArray();
        Assert.Equal(notices.Length, merges.Length);
        foreach (var notice in notices)
        {
            var cell = sheet.Descendants(Ns + "c").Single(c => c.Element(Ns + "is")?.Value == notice);
            var row = cell.Parent!; var rowId = (string)row.Attribute("r")!;
            Assert.Contains($"A{rowId}:H{rowId}", merges); Assert.Single(row.Elements(Ns + "c"));
            Assert.InRange(double.Parse((string)row.Attribute("ht")!, CultureInfo.InvariantCulture), 30, 60);
            var alignment = styles.Root!.Element(Ns + "cellXfs")!.Elements().ElementAt((int)cell.Attribute("s")!).Element(Ns + "alignment")!;
            Assert.Equal("1", (string?)alignment.Attribute("wrapText")); Assert.Equal("2", (string?)alignment.Attribute("readingOrder"));
            Assert.Equal("right", (string?)alignment.Attribute("horizontal"));
        }
        var pricedRow = sheet.Descendants(Ns + "row").Single(row => row.Descendants(Ns + "f").Any(f => f.Value.StartsWith("ROUND(E", StringComparison.Ordinal)));
        var id = (string)pricedRow.Attribute("r")!;
        foreach (var (column, expected) in new[] { ("E", quantity), ("F", Price) })
        {
            var cell = pricedRow.Elements().Single(c => (string?)c.Attribute("r") == column + id);
            Assert.Null(cell.Attribute("t")); Assert.Equal(expected, decimal.Parse(cell.Element(Ns + "v")!.Value, CultureInfo.InvariantCulture));
        }
        Assert.Equal($"ROUND(E{id}*F{id},2)", pricedRow.Descendants(Ns + "f").Single().Value);
        using var audit = JsonDocument.Parse(File.ReadAllText(output.AuditPath));
        Assert.Equal(total, audit.RootElement.GetProperty("clean_total").GetDecimal());
        Assert.Equal(2, audit.RootElement.GetProperty("lines").GetArrayLength());
        Assert.Equal(!partial, audit.RootElement.GetProperty("full_estimate_export_allowed").GetBoolean());
        Assert.Empty(audit.RootElement.GetProperty("exclusions").EnumerateArray());
    }
}
