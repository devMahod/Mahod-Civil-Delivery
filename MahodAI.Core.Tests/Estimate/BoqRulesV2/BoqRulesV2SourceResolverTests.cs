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

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// The multi-drawing source rule, on a private temporary runs root (never the user's run store): the latest published scan
/// of each other role in the active drawing's folder, only when its records and its drawing are unchanged.
/// </summary>
public sealed class BoqRulesV2SourceResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "boq-rules-v2-runs-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;

    public BoqRulesV2SourceResolverTests()
    {
        _folder = Path.Combine(_root, "drawings");
        Directory.CreateDirectory(Path.Combine(_root, "runs"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Runs => Path.Combine(_root, "runs");

    private static NeutralQuantityRecord Record(string handle, string layer) => new()
    {
        RecordId = "q-" + handle, ProjectProfileId = "p", RunId = "run",
        Source = new QuantitySource { Drawing = "d", DrawingHash = new string('a', 64), Handle = handle, EntityType = "LINE", Layer = layer },
        Measurement = new QuantityMeasurement { Kind = "length", Method = "line-length", RawValue = 2, Unit = "m" },
    };

    private string Drawing(string name, string content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    private void Publish(string runId, string drawing, DateTime completed, IReadOnlyList<NeutralQuantityRecord> records,
        string operation = "extract", bool tamper = false)
    {
        var dir = Path.Combine(Runs, runId);
        Directory.CreateDirectory(dir);
        var recordsPath = Path.Combine(dir, BoqRulesSourceResolver.RecordsArtifact);
        File.WriteAllText(recordsPath, JsonSerializer.Serialize(records));
        var findingsPath = Path.Combine(dir, BoqRulesSourceResolver.FindingsArtifact);
        File.WriteAllText(findingsPath, "[]");
        // b24: the scan header a writer before the unit contract produced (physical units, no contract, no authority).
        var scanPath = Path.Combine(dir, BoqRulesSourceResolver.ScanArtifact);
        File.WriteAllText(scanPath, JsonSerializer.Serialize(new
        {
            RunId = runId, ScannedAtUtc = completed, SourceDrawing = drawing, SourceDrawingHash = ArtifactHash.Sha256OfFile(drawing),
            PhysicalUnits = new { IsSupported = true, RawUnitCode = 6, EffectiveUnitCode = 6, LinearToMetres = 1.0, UsedDeclaration = false },
        }));
        var manifest = new RunManifest
        {
            RunId = runId, Feature = "estimate", Operation = operation, CompletedAtUtc = completed,
            InputDrawings = { drawing },
            InputHashesByPath = { [drawing] = ArtifactHash.Sha256OfFile(drawing) },
            ArtifactHashes =
            {
                [recordsPath] = ArtifactHash.Sha256OfFile(recordsPath),
                [findingsPath] = ArtifactHash.Sha256OfFile(findingsPath),
                [scanPath] = ArtifactHash.Sha256OfFile(scanPath),
            },
        };
        File.WriteAllText(Path.Combine(dir, "run_manifest.json"), JsonSerializer.Serialize(manifest, RunManifestWriter.JsonOptions));
        if (tamper) File.AppendAllText(recordsPath, " ");
    }

    [Fact]
    public void TakesTheLatestUnchangedScanOfEachOtherRoleAndReportsTheRest()
    {
        var rules = BoqRuleset.LoadEmbedded6422();
        var gm = Drawing("6422-GM-MODEL.dwg", "gm");
        var dr = Drawing("6422-DR-MODEL.dwg", "dr");
        var sm = Drawing("6422-SM-MODEL.dwg", "sm");
        var ha = Drawing("6422-HA-MODEL.dwg", "ha-active");
        Publish("estimate-extract-old-dr", dr, new DateTime(2026, 9, 1), new[] { Record("1", "OLD") });
        Publish("estimate-extract-new-dr", dr, new DateTime(2026, 9, 2), new[] { Record("2", "NEW") }, operation: "build");
        Publish("estimate-extract-gm", gm, new DateTime(2026, 9, 2), new[] { Record("3", "G") });
        File.WriteAllText(gm, "gm changed after its scan");
        Publish("estimate-extract-sm", sm, new DateTime(2026, 9, 3), new[] { Record("4", "S") }, tamper: true);
        Publish("measurement-draft-x", dr, new DateTime(2026, 9, 9), new[] { Record("9", "X") }, operation: "measurement-draft");

        var resolution = BoqRulesSourceResolver.Resolve(rules,
            new BoqRulesSourceResolver.ActiveScan("active-run", ha, new string('f', 64), new[] { Record("5", "HA") }, Array.Empty<DeliveryFinding>()) { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route") },
            Runs);

        resolution.ActiveRole.Should().Be("HA");
        resolution.Scans.Select(s => (s.Role, s.RunId)).Should().Equal(("HA", "active-run"), ("DR", "estimate-extract-new-dr"));
        resolution.Scans[1].Records.Single().Source.Handle.Should().Be("2");
        resolution.MissingRoles.Should().BeEquivalentTo("GM", "SM");
        resolution.Notes.Should().Contain(n => n.StartsWith("GM:") && n.Contains("השתנה מאז הסריקה"));
        resolution.Notes.Should().Contain(n => n.StartsWith("SM:") && n.Contains("קובץ הרשומות השתנה"));
    }

    // ---- b24 (Codex 12:45): the run's own verified unit evidence decides whether its records are taken ----

    private static NeutralQuantityRecord Stamped(string handle) => new()
    {
        RecordId = "q-" + handle, ProjectProfileId = "p", RunId = "run",
        Source = new QuantitySource { Drawing = "d", DrawingHash = new string('a', 64), Handle = handle, EntityType = "LINE", Layer = "DRL" },
        Measurement = new QuantityMeasurement
        {
            Kind = "length", Method = "line-length", RawValue = 2, Unit = "m",
            Parameters = new Dictionary<string, string>
            {
                [QuantityPhysicalUnits.RawUnitsKey] = "Meters", [QuantityPhysicalUnits.AuthorityKey] = "explicit-insunits",
                [QuantityPhysicalUnits.UnitCodeKey] = "6", [QuantityPhysicalUnits.MetresPerUnitKey] = "1",
            },
        },
    };

    private void PublishWithHeader(string runId, string drawing, NeutralQuantityRecord record, string? contract, bool authority, string? configuration)
    {
        Publish(runId, drawing, new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc), new[] { record });
        var dir = Path.Combine(Runs, runId);
        var scanPath = Path.Combine(dir, BoqRulesSourceResolver.ScanArtifact);
        var units = new Dictionary<string, object?>
        {
            ["IsSupported"] = true, ["RawUnitCode"] = 6, ["EffectiveUnitCode"] = 6, ["LinearToMetres"] = 1.0, ["DeclarationDigest"] = null,
        };
        if (authority) units["Authority"] = "explicit-insunits";
        var header = new Dictionary<string, object?>
        {
            ["RunId"] = runId, ["ScannedAtUtc"] = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc), ["SourceDrawing"] = drawing,
            ["SourceDrawingHash"] = ArtifactHash.Sha256OfFile(drawing), ["PhysicalUnits"] = units,
        };
        if (contract != null) header["PhysicalUnitsContract"] = contract;
        if (configuration != null) header["PhysicalUnitConfigurationHash"] = configuration;
        File.WriteAllText(scanPath, JsonSerializer.Serialize(header));
        var manifestPath = Path.Combine(dir, "run_manifest.json");
        var manifest = JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(manifestPath), RunManifestWriter.JsonOptions)!;
        manifest.ArtifactHashes[scanPath] = ArtifactHash.Sha256OfFile(scanPath);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, RunManifestWriter.JsonOptions));
    }

    [Theory]
    [InlineData("bound", true, "")]
    [InlineData("legacy", true, "")]
    [InlineData("authority without contract", false, "לא אומתה")]
    [InlineData("unknown contract", false, "לא אומתה")]
    [InlineData("bound but unstamped records", false, "אינן תואמות")]
    [InlineData("unit configuration changed", false, "הגדרות היחידות")]
    [InlineData("no scan header", false, "לא אומתה")]
    public void TheRunsVerifiedUnitEvidenceDecidesWhetherItsRecordsAreTaken(string variant, bool taken, string note)
    {
        var rules = BoqRuleset.LoadEmbedded6422();
        var dr = Drawing("6422-DR-MODEL.dwg", "dr");
        var ha = Drawing("6422-HA-MODEL.dwg", "ha-active");
        switch (variant)
        {
            case "bound": PublishWithHeader("estimate-extract-dr", dr, Stamped("2"), ScanUnitEvidence.Contract, true, "cfg"); break;
            case "legacy": PublishWithHeader("estimate-extract-dr", dr, Record("2", "DRL"), null, false, "cfg"); break;
            case "authority without contract": PublishWithHeader("estimate-extract-dr", dr, Stamped("2"), null, true, "cfg"); break;
            case "unknown contract": PublishWithHeader("estimate-extract-dr", dr, Stamped("2"), "scan-physical-units/9", true, "cfg"); break;
            case "bound but unstamped records":
                var stripped = Stamped("2");   // the raw INSUNITS stays, the stamp was removed
                foreach (var key in new[] { QuantityPhysicalUnits.AuthorityKey, QuantityPhysicalUnits.UnitCodeKey, QuantityPhysicalUnits.MetresPerUnitKey })
                    stripped.Measurement.Parameters.Remove(key);
                PublishWithHeader("estimate-extract-dr", dr, stripped, ScanUnitEvidence.Contract, true, "cfg"); break;
            case "unit configuration changed": PublishWithHeader("estimate-extract-dr", dr, Stamped("2"), ScanUnitEvidence.Contract, true, "older"); break;
            case "no scan header":
                Publish("estimate-extract-dr", dr, new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc), new[] { Stamped("2") });
                var dir = Path.Combine(Runs, "estimate-extract-dr");
                var manifestPath = Path.Combine(dir, "run_manifest.json");
                var manifest = JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(manifestPath), RunManifestWriter.JsonOptions)!;
                var scanPath = Path.Combine(dir, BoqRulesSourceResolver.ScanArtifact);
                manifest.ArtifactHashes.Remove(scanPath); File.Delete(scanPath);
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, RunManifestWriter.JsonOptions));
                break;
        }
        var resolution = BoqRulesSourceResolver.Resolve(rules,
            new BoqRulesSourceResolver.ActiveScan("active-run", ha, new string('f', 64), new[] { Record("5", "HA") }, Array.Empty<DeliveryFinding>())
            { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route"), PhysicalUnitConfigurationHash = "cfg" },
            Runs);
        resolution.Scans.Any(s => s.Role == "DR").Should().Be(taken, variant);
        if (!taken) resolution.Notes.Should().Contain(n => n.StartsWith("DR:") && n.Contains(note), variant);
        else resolution.Scans.Single(s => s.Role == "DR").Units.State.Should().Be(
            variant == "bound" ? ScanUnitEvidenceState.Bound : ScanUnitEvidenceState.Legacy);
    }

    [Theory]
    [InlineData("bound", "cfg", false)]                                    // a Bound run must name its configuration
    [InlineData("legacy", "cfg", false)]                                   // legacy without one, profile has unit content
    [InlineData("legacy", "empty", true)]                                  // legacy without one, profile has none
    public void ARunWithoutItsUnitConfigurationIsTakenOnlyAsLegacyOfAProfileWithoutUnitContent(string kind, string active, bool taken)
    {
        var rules = BoqRuleset.LoadEmbedded6422();
        var dr = Drawing("6422-DR-MODEL.dwg", "dr");
        var ha = Drawing("6422-HA-MODEL.dwg", "ha-active");
        if (kind == "bound") PublishWithHeader("estimate-extract-dr", dr, Stamped("2"), ScanUnitEvidence.Contract, true, null);
        else PublishWithHeader("estimate-extract-dr", dr, Record("2", "DRL"), null, false, null);
        var resolution = BoqRulesSourceResolver.Resolve(rules,
            new BoqRulesSourceResolver.ActiveScan("active-run", ha, new string('f', 64), new[] { Record("5", "HA") }, Array.Empty<DeliveryFinding>())
            {
                Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route"),
                PhysicalUnitConfigurationHash = active == "empty" ? ScanUnitEvidence.EmptyUnitConfigurationHash : "cfg",
            },
            Runs);
        resolution.Scans.Any(s => s.Role == "DR").Should().Be(taken, kind + "/" + active);
    }

    [Fact]
    public void ADrawingThatIsNoneOfTheRulesetRolesIsRefused()
    {
        var rules = BoqRuleset.LoadEmbedded6422();
        var other = Drawing("unrelated.dwg", "x");
        FluentActions.Invoking(() => BoqRulesSourceResolver.Resolve(rules,
                new BoqRulesSourceResolver.ActiveScan("r", other, new string('f', 64), Array.Empty<NeutralQuantityRecord>(), Array.Empty<DeliveryFinding>()) { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route") },
                Runs))
            .Should().Throw<InvalidOperationException>().WithMessage("*unrelated.dwg*");
    }
}
