using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Xunit.Abstractions;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// The product's own input path on the real 6422 plugin runs the reference used for HA / DR / GM (28.09.2026, read-only):
/// the neutral-record adapter must reproduce the frozen golden records of those roles exactly (same records, kinds,
/// quantities, closed flags and 0.1-rounded extents). Skipped where the local runs are not present. SM had no plugin run
/// (the reference took SM from a COM reconnaissance) and the runs predate cad_segments, so crossings on the native path
/// need a fresh scan of all four drawings — an integration step, not claimed here.
/// </summary>
public sealed class BoqRulesV2NativeRunTests
{
    private readonly ITestOutputHelper _output;
    public BoqRulesV2NativeRunTests(ITestOutputHelper output) => _output = output;

    internal static string RunsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MahodAI_Civil3D", "civil-delivery", "runs");

    internal static readonly IReadOnlyDictionary<string, string> Runs = new Dictionary<string, string>
    {
        ["HA"] = "estimate-extract-20260928-074135-59c5ac0b",
        ["DR"] = "estimate-extract-20260928-082016-f7ec90a6",
        ["GM"] = "estimate-extract-20260928-082452-0bb8ddc8",
    };

    [NativeRuns6422Fact]
    public void TheNeutralRecordAdapterReproducesTheGoldenRecordsOfThePluginRuns()
    {
        var rules = BoqRuleset.LoadEmbedded6422();
        var golden = BoqGoldenInputReader.Read(File.ReadAllText(BoqRulesV2GoldenAcceptanceTests.FixturePath("golden_inputs_6422.json")),
            rules, BoqRulesV2GoldenAcceptanceTests.UnmeasuredLayers);
        var options = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var scans = Runs.Select(pair =>
        {
            using var stream = File.OpenRead(Path.Combine(RunsRoot, pair.Value, BoqRulesSourceResolver.RecordsArtifact));
            var records = JsonSerializer.Deserialize<List<NeutralQuantityRecord>>(stream, options)!;
            return new BoqNeutralRecordAdapter.SourceScan(pair.Key, pair.Value, pair.Key, "", records, Array.Empty<DeliveryFinding>()) { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route") };
        }).ToList();
        var native = BoqNeutralRecordAdapter.Build(rules, scans);

        static string Key(BoqRecord r) => string.Join("|", r.Src, r.Handle, r.Kind, r.Layer, r.Etype, r.Closed,
            Math.Round(r.Qty, 6).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            // The golden extents are Python round(v, 1) of the same raw values: compare with the exact same rounding.
            r.Bbox == null ? "" : string.Join(",", r.Bbox.Take(4).Select(v => (BoqRulesEngine.PythonRound1(v) + 0.0).ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
        foreach (var role in Runs.Keys)
        {
            var expected = golden.Records.Where(r => r.Src == role).Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var actual = native.Records.Where(r => r.Src == role).Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var missing = expected.Except(actual).ToList();
            var extra = actual.Except(expected).ToList();
            _output.WriteLine($"{role}: golden {expected.Count}, native {actual.Count}, missing {missing.Count}, extra {extra.Count}");
            foreach (var line in missing.Take(10)) _output.WriteLine("  missing " + line);
            foreach (var line in extra.Take(10)) _output.WriteLine("  extra   " + line);
            actual.Should().Equal(expected, $"the native adapter must normalise the {role} plugin run as the reference did");
        }
    }
}

public sealed class NativeRuns6422FactAttribute : FactAttribute
{
    public NativeRuns6422FactAttribute()
    {
        if (BoqRulesV2NativeRunTests.Runs.Values.Any(run =>
                !File.Exists(Path.Combine(BoqRulesV2NativeRunTests.RunsRoot, run, BoqRulesSourceResolver.RecordsArtifact))))
            Skip = "The 28.09.2026 6422 plugin runs (HA/DR/GM) are not on this machine; native-path replay not run.";
    }
}
