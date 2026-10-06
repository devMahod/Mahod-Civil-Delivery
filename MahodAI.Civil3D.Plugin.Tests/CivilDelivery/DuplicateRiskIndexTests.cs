using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Xunit.Abstractions;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class DuplicateRiskIndexTests
{
    private readonly ITestOutputHelper _output;
    public DuplicateRiskIndexTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void DenseEqualCounts_PreservesAllHostAndExternalMembers()
    {
        var records = Enumerable.Range(0, 30000).Select(i => Record("r" + i,
            handle: (i % 2 == 0 ? "left/" : "right/") + "AB",
            xref: i < 15000 ? null : "xref", box: new[] { 0.0, 0.0, 1.0, 1.0 })).ToList();
        var watch = Stopwatch.StartNew();
        var findings = DuplicateRiskDetector.Detect(records);
        watch.Stop();
        var representations = findings.Where(f => f.Code == EstimateFindingCodes.XrefDoubleCountRisk).ToList();
        Assert.Equal(2, representations.Count);
        Assert.Equal(30000, representations.Single(f => !f.Message.Contains("terminal handle")).AffectedRecordIds.Count);
        Assert.Equal(15000, representations.Single(f => f.Message.Contains("terminal handle")).AffectedRecordIds.Count);
        _output.WriteLine($"Dense equal counts: {watch.Elapsed.TotalMilliseconds:F1} ms; 30,000 records, all memberships retained.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Dense count scan took {watch.Elapsed}.");
    }

    [Fact]
    public void EqualCountsWithUniqueExternalHandles_HaveNoRepresentationRisk()
    {
        var records = Enumerable.Range(0, 75000).Select(i => Record("r" + i,
            handle: "xref/" + i.ToString("X"), xref: "xref",
            box: new[] { 0.0, 0.0, 1.0, 1.0 })).ToList();
        var watch = Stopwatch.StartNew();
        var findings = DuplicateRiskDetector.Detect(records);
        watch.Stop();
        Assert.Empty(findings);
        _output.WriteLine($"Distinct external handles: {watch.Elapsed.TotalMilliseconds:F1} ms; 75,000 count=1 records.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"External count scan took {watch.Elapsed}.");
    }

    [Fact]
    public void SameFullHandleDenseSubtree_DoesNotInventARepeatedExternalSource()
    {
        var records = Enumerable.Range(0, 30000).Select(i => Record("r" + i,
            handle: "left/AB", xref: "xref", box: new[] { 0.0, 0.0, 1.0, 1.0 })).ToList();
        records.Add(Record("distant", handle: "right/AB", xref: "xref",
            box: new[] { 100.0, 100.0, 101.0, 101.0 }));
        var watch = Stopwatch.StartNew();
        var findings = DuplicateRiskDetector.Detect(records);
        watch.Stop();
        Assert.DoesNotContain(findings, f => f.Code == EstimateFindingCodes.XrefDoubleCountRisk);
        Assert.Single(findings, f => f.Code == EstimateFindingCodes.DuplicateSource);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Uniform-handle scan took {watch.Elapsed}.");
    }

    [Fact]
    public void ToleranceBoundaries_UnitAliasesAndCaseMatchThePairReference()
    {
        foreach (var value in new[] { -1e100, -650000.0, -1.0, -1e-8, 0.0, 1.0, 650000.0, 1e100 })
        {
            var limit = 1e-7 * Math.Max(1, Math.Abs(value)) / (1 - 1e-7);
            foreach (var delta in new[] { -limit * 1.0000001, -limit, -limit * 0.9999999,
                         0.0, limit * 0.9999999, limit, limit * 1.0000001 })
            {
                var records = new[]
                {
                    Record("a", value, kind: "COUNT", unit: "יח'", box: new[] { value, value, value, value }),
                    Record("b", value + delta, kind: "count", unit: "unit", xref: "x", handle: "x/AB",
                        box: new[] { value + delta, value, value, value }),
                    Record("c", value, kind: "Count", unit: "unit", xref: "y", handle: "y/ab",
                        box: new[] { value, value, value, value }),
                    Record("wrong-unit", value, unit: "m", xref: "x", handle: "x/CD",
                        box: new[] { value, value, value, value }),
                };
                Assert.Equal(Reference(records), Signatures(DuplicateRiskDetector.Detect(records)));
            }
        }
    }

    [Fact]
    public void NonfiniteAndExtremeCoordinates_PreserveExactComparisonSemantics()
    {
        foreach (var value in new[] { double.MinValue, -double.Epsilon, -0.0, double.Epsilon, double.MaxValue })
        {
            var records = new List<NeutralQuantityRecord>
            {
                Record("host", value, box: new[] { value, value, value, value }),
                Record("external", value, handle: "x/AB", xref: "x", box: new[] { value, value, value, value }),
                Record("next", Math.BitIncrement(value), handle: "y/AB", xref: "y",
                    box: new[] { Math.BitIncrement(value), value, value, value }),
                Record("previous", Math.BitDecrement(value), handle: "z/AB", xref: "z",
                    box: new[] { Math.BitDecrement(value), value, value, value }),
                Record("missing", value, handle: "x/M", xref: "x"),
                Record("short", value, handle: "x/S", xref: "x", box: new[] { value, value, value }),
            };
            foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                records.Add(Record("raw-" + invalid, invalid, handle: "x/RAW", xref: "x",
                    box: new[] { value, value, value, value }));
                for (var axis = 0; axis < 4; axis++)
                {
                    var bounds = new[] { value, value, value, value };
                    bounds[axis] = invalid;
                    records.Add(Record($"bounds-{axis}-{invalid}", value, handle: "x/B", xref: "x", box: bounds));
                }
            }
            Assert.Equal(Reference(records), Signatures(DuplicateRiskDetector.Detect(records)));
        }
    }

    [Fact]
    public void SeededMixedRecords_MatchAllPairReferenceIncludingOverlapCounts()
    {
        for (var seed = 0; seed < 150; seed++)
        {
            var random = new Random(seed);
            var records = Enumerable.Range(0, 70).Select(i =>
            {
                var x = random.Next(4) + random.Next(3) * 0.00000009;
                var y = random.Next(4);
                var kind = new[] { "count", "COUNT", "area", "area", "length" }[random.Next(5)];
                var value = new[] { 1.0, 1.000000099, 1.000000101, -1.0, 2.0, double.NaN }[random.Next(6)];
                var record = Record("r" + i, value, kind: kind,
                    unit: new[] { "unit", "יח'", "m2", "m²", "m", "unknown" }[random.Next(6)],
                    handle: new[] { "AB", "x/AB", "y/ab", "x/CD", "y/CD" }[random.Next(5)],
                    xref: new string?[] { null, "x", "y", " " }[random.Next(4)],
                    rule: new string?[] { null, "a", "b", "a" }[random.Next(4)],
                    hash: random.Next(3) == 0 ? "BBBB" : "aaaa",
                    box: random.Next(10) == 0 ? null : new[] { x, y, x + 1, y + 1 });
                if (random.Next(2) == 0)
                {
                    record.Classification.CandidateCatalogCode = "U1";
                    record.Classification.ApprovedCatalogId = "catalog";
                    record.Classification.ApprovedCatalogHash = new string('a', 64);
                    record.Classification.ApprovedCatalogItemFingerprint = new string('b', 64);
                }
                return record;
            }).ToList();
            Assert.Equal(Reference(records), Signatures(DuplicateRiskDetector.Detect(records)));
        }
    }

    [FullModelFact]
    public void Saved74900RecordScan_PreservesPublishedFindingsAndCompletes()
    {
        // Read only the saved JSON artifacts. Source.DrawingPath may name a server;
        // this replay never opens or checks any drawing path carried by a record.
        using var stream = File.OpenRead(FullModelFactAttribute.RecordsPath);
        var records = JsonSerializer.Deserialize<List<NeutralQuantityRecord>>(stream)!;
        Assert.Equal(74900, records.Count);
        using var preflightStream = File.OpenRead(Path.Combine(
            Path.GetDirectoryName(FullModelFactAttribute.RecordsPath)!, "quantity_preflight.json"));
        var published = JsonSerializer.Deserialize<List<DeliveryFinding>>(preflightStream)!;
        var watch = Stopwatch.StartNew();
        var actual = DuplicateRiskDetector.Detect(records);
        watch.Stop();
        Assert.Equal(Signatures(published.Where(IsDuplicateFinding)), Signatures(actual));
        Assert.Contains(actual, f => f.Code == EstimateFindingCodes.XrefDoubleCountRisk);
        _output.WriteLine($"Saved full model: {watch.Elapsed.TotalMilliseconds:F1} ms; {records.Count} records; " +
                          $"{actual.Count} duplicate/overlap findings match the published preflight.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Full-model detector took {watch.Elapsed}.");
    }

    private static NeutralQuantityRecord Record(string id, double value = 1, string kind = "count",
        string unit = "unit", string handle = "AB", string? xref = null,
        string? rule = "r", string hash = "aaaa", double[]? box = null) => new()
    {
        RecordId = id, ProjectProfileId = "test", RunId = "read-only-replay",
        Source = new QuantitySource { Drawing = "fixture.dwg", DrawingHash = hash,
            Handle = handle, Xref = xref, EntityType = "BLOCK", Layer = "L" },
        Measurement = new QuantityMeasurement { Kind = kind, Unit = unit, Method = "fixture",
            RawValue = value, GeometryEvidence = box },
        Classification = new QuantityClassification { RuleKey = rule },
    };

    private static bool IsDuplicateFinding(DeliveryFinding finding) => finding.Code is
        EstimateFindingCodes.DuplicateSource or EstimateFindingCodes.XrefDoubleCountRisk or
        EstimateFindingCodes.OverlapRisk or EstimateFindingCodes.CrossSourceDuplicateRisk;

    private static string Signature(string code, IEnumerable<string> ids, string key = "", int pairs = 0) =>
        $"{code}|{key}|{pairs}|{string.Join(",", ids.Distinct().OrderBy(id => id, StringComparer.Ordinal))}";

    private static string[] Signatures(IEnumerable<DeliveryFinding> findings) => findings.Select(f =>
    {
        var key = "";
        var pairs = 0;
        if (f.Code == EstimateFindingCodes.XrefDoubleCountRisk)
            key = (f.Message.Contains("terminal handle") ? "external:" : "host:") + f.Message.Split(';')[0];
        if (f.Code == EstimateFindingCodes.OverlapRisk)
            pairs = int.Parse(Regex.Match(f.Title, @"— (\d+) overlapping").Groups[1].Value);
        if (f.Code == EstimateFindingCodes.CrossSourceDuplicateRisk)
            pairs = int.Parse(Regex.Match(f.Message, @"נמצאו (\d+) זוגות").Groups[1].Value);
        return Signature(f.Code, f.AffectedRecordIds, key, pairs);
    }).OrderBy(value => value, StringComparer.Ordinal).ToArray();

    // Deliberately straightforward O(n²) reference used only on small fixtures.
    // Keep it independent of the production range/provenance indexes.
    private static string[] Reference(IReadOnlyList<NeutralQuantityRecord> records)
    {
        var result = new List<string>();
        foreach (var group in records.GroupBy(r => new { r.Source.DrawingHash, r.Source.Handle,
                     r.Source.Xref, r.Source.Layer, r.Source.CivilIdentity, r.Source.StationFrom,
                     r.Source.StationTo, r.Measurement.Kind, r.Measurement.Method }).Where(g => g.Count() > 1))
            result.Add(Signature(EstimateFindingCodes.DuplicateSource, group.Select(r => r.RecordId)));
        var risks = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var bucket in records.GroupBy(r => (r.Classification.RuleKey,
                     r.Measurement.Kind?.ToUpperInvariant(), Units.Parse(r.Measurement.Unit).Canonical)))
        {
            var ordered = bucket.OrderBy(r => r.Measurement.RawValue).ToList();
            for (var i = 0; i < ordered.Count; i++)
            for (var j = i + 1; j < ordered.Count; j++)
            {
                var a = ordered[i]; var b = ordered[j];
                var ax = !string.IsNullOrWhiteSpace(a.Source.Xref);
                var bx = !string.IsNullOrWhiteSpace(b.Source.Xref);
                var host = ax != bx;
                var external = ax && bx && a.Source.Handle != b.Source.Handle &&
                    string.Equals(a.Source.DrawingHash, b.Source.DrawingHash, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Terminal(a.Source.Handle), Terminal(b.Source.Handle), StringComparison.OrdinalIgnoreCase);
                if ((!host && !external) || !Near(a.Measurement.RawValue, b.Measurement.RawValue) ||
                    a.Measurement.GeometryEvidence is not { Length: 4 } ab ||
                    b.Measurement.GeometryEvidence is not { Length: 4 } bb || !ab.Zip(bb, Near).All(v => v)) continue;
                var key = (host ? "host:" : "external:") + $"rule={a.Classification.RuleKey}|{a.Measurement.Kind}";
                if (!risks.TryGetValue(key, out var ids)) risks[key] = ids = new HashSet<string>();
                ids.Add(a.RecordId); ids.Add(b.RecordId);
            }
        }
        result.AddRange(risks.Select(pair => Signature(EstimateFindingCodes.XrefDoubleCountRisk, pair.Value, pair.Key)));
        var areas = records.Where(r => r.Measurement.Kind == "area" && r.Classification.RuleKey != null &&
            r.Measurement.GeometryEvidence is { Length: 4 }).ToList();
        var overlaps = new Dictionary<string, (HashSet<string> Ids, int Pairs)>();
        var catalog = new Dictionary<string, (HashSet<string> Ids, int Pairs)>();
        for (var i = 0; i < areas.Count; i++)
        for (var j = i + 1; j < areas.Count; j++)
        {
            var a = areas[i]; var b = areas[j]; var ab = a.Measurement.GeometryEvidence!; var bb = b.Measurement.GeometryEvidence!;
            if (!(ab[0] < bb[2] && bb[0] < ab[2] && ab[1] < bb[3] && bb[1] < ab[3])) continue;
            if (a.Classification.RuleKey == b.Classification.RuleKey &&
                !(a.Source.Handle == b.Source.Handle && a.Source.DrawingHash == b.Source.DrawingHash))
                Add(overlaps, a.Classification.RuleKey!, a.RecordId, b.RecordId);
            var ac = a.Classification; var bc = b.Classification;
            if (ac.RuleKey == bc.RuleKey || a.Source.Handle == b.Source.Handle ||
                !string.Equals(a.Source.DrawingHash, b.Source.DrawingHash, StringComparison.OrdinalIgnoreCase) ||
                !Approved(ac) || !Approved(bc) || Units.Parse(a.Measurement.Unit).Canonical == "?" ||
                Units.Parse(a.Measurement.Unit).Canonical != Units.Parse(b.Measurement.Unit).Canonical ||
                !string.Equals(ac.ApprovedCatalogId, bc.ApprovedCatalogId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ac.ApprovedCatalogHash, bc.ApprovedCatalogHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ac.CandidateCatalogCode, bc.CandidateCatalogCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ac.ApprovedCatalogItemFingerprint, bc.ApprovedCatalogItemFingerprint, StringComparison.OrdinalIgnoreCase)) continue;
            Add(catalog, ac.ApprovedCatalogId!.Trim().ToUpperInvariant() + "|" + ac.ApprovedCatalogHash!.Trim().ToLowerInvariant() +
                "|" + ac.CandidateCatalogCode!.Trim().ToUpperInvariant() + "|" + Units.Parse(a.Measurement.Unit).Canonical,
                a.RecordId, b.RecordId);
        }
        result.AddRange(overlaps.Values.Select(v => Signature(EstimateFindingCodes.OverlapRisk, v.Ids, pairs: v.Pairs)));
        result.AddRange(catalog.Values.Select(v => Signature(EstimateFindingCodes.CrossSourceDuplicateRisk, v.Ids, pairs: v.Pairs)));
        return result.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static void Add(Dictionary<string, (HashSet<string> Ids, int Pairs)> groups,
        string key, string a, string b)
    {
        if (!groups.TryGetValue(key, out var entry)) entry = (new HashSet<string>(), 0);
        entry.Ids.Add(a); entry.Ids.Add(b); entry.Pairs++;
        groups[key] = entry;
    }

    private static bool Approved(QuantityClassification c) => !string.IsNullOrWhiteSpace(c.CandidateCatalogCode) &&
        !string.IsNullOrWhiteSpace(c.ApprovedCatalogId) && CatalogIdentity.IsValidSha256(c.ApprovedCatalogHash) &&
        CatalogIdentity.IsValidSha256(c.ApprovedCatalogItemFingerprint);
    private static string Terminal(string handle) => string.IsNullOrWhiteSpace(handle) ? "" : handle[(handle.LastIndexOf('/') + 1)..];
    private static bool Near(double a, double b) => double.IsFinite(a) && double.IsFinite(b) &&
        Math.Abs(a - b) <= 1e-7 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
}

public sealed class FullModelFactAttribute : FactAttribute
{
    public static string RecordsPath => Environment.GetEnvironmentVariable("MAHOD_FULL_MODEL_RECORDS") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MahodAI_Civil3D", "civil-delivery",
        "runs", "estimate-extract-20260907-152235-b8ebac44", "neutral_quantity_records.json");
    public FullModelFactAttribute()
    {
        if (!File.Exists(RecordsPath) || !File.Exists(Path.Combine(Path.GetDirectoryName(RecordsPath)!, "quantity_preflight.json")))
            Skip = "The saved local 74,900-record scan and preflight are unavailable; set MAHOD_FULL_MODEL_RECORDS to a local copy.";
    }
}
