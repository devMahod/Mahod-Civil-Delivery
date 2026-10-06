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
using R = MahodAI.CivilDelivery.Estimate.BoqRulesV2.BoqSourceResolutionReceipt;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// L05 producer observation (Codex B520 / 0BF95EE5): the resolution the rules export uses, plus the receipt of what was read
/// — one byte read per artifact — and the reasons a project-rule context on it may not be bound. Private temporary runs root.
/// </summary>
public sealed class BoqRulesV2ResolveObservedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "boq-rules-v2-observed-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;

    public BoqRulesV2ResolveObservedTests()
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
        RecordId = "q-" + handle, ProjectProfileId = "6422", RunId = "run",
        Source = new QuantitySource { Drawing = "d", DrawingHash = new string('a', 64), Handle = handle, EntityType = "LINE", Layer = layer },
        Measurement = new QuantityMeasurement { Kind = "length", Method = "line-length", RawValue = 2, Unit = "m" },
    };

    private string Drawing(string name, string content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    private void Publish(string runId, string drawing, DateTime completed, IReadOnlyList<NeutralQuantityRecord> records)
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
            RunId = runId, Feature = "estimate", Operation = "extract", CompletedAtUtc = completed,
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
    }

    private (BoqRuleset Rules, BoqRulesSourceResolver.ActiveScan Active) AllFour(bool includeSm = true)
    {
        var rules = BoqRuleset.LoadEmbedded6422();
        var ha = Drawing("6422-HA-MODEL.dwg", "ha");
        var dr = Drawing("6422-DR-MODEL.dwg", "dr");
        var gm = Drawing("6422-GM-MODEL.dwg", "gm");
        var sm = Drawing("6422-SM-MODEL.dwg", "sm");
        Publish("estimate-extract-20260902-100000-dr", dr, new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc), new[] { Record("2", "DRL") });
        Publish("estimate-extract-20260902-110000-gm", gm, new DateTime(2026, 9, 2, 11, 0, 0, DateTimeKind.Utc), new[] { Record("3", "GML") });
        if (includeSm)
            Publish("estimate-extract-20260902-120000-sm", sm, new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc), new[] { Record("4", "SML") });
        var activeRecords = new[] { Record("5", "HAL") };
        Publish("estimate-extract-20260903-090000-ha", ha, new DateTime(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc), activeRecords);
        return (rules, new BoqRulesSourceResolver.ActiveScan("estimate-extract-20260903-090000-ha", ha, ArtifactHash.Sha256OfFile(ha),
            activeRecords, Array.Empty<DeliveryFinding>()) { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route") });
    }

    [Fact]
    public void EveryRoleReadOnceGivesAReceiptAndNoPendingReason()
    {
        var (rules, active) = AllFour();
        var observed = BoqRulesSourceResolver.ResolveObserved(rules, active, Runs);

        observed.PendingReasons.Should().BeEmpty();
        observed.Receipt.Should().NotBeNull();
        observed.Receipt!.Observed.InventoryState.Should().Be(R.InventoryState.Complete);
        observed.Receipt.Observed.Choices.Select(c => (c.RoleId, c.State)).Should().BeEquivalentTo(new[]
        {
            ("HA", R.SelectionState.Active), ("DR", R.SelectionState.Selected), ("GM", R.SelectionState.Selected), ("SM", R.SelectionState.Selected),
        });
        observed.Sources.Should().HaveCount(4);
        foreach (var source in observed.Sources)
            source.RecordsSha256.Should().BeEquivalentTo(ArtifactHash.Sha256OfFile(source.RecordsPath));
        observed.Receipt.IsApproval.Should().BeFalse();
    }

    [Fact]
    public void ResolveIsExactlyTheObservedResolution()
    {
        var (rules, active) = AllFour(includeSm: false);
        var plain = BoqRulesSourceResolver.Resolve(rules, active, Runs);
        var observed = BoqRulesSourceResolver.ResolveObserved(rules, active, Runs).Resolution;

        plain.ActiveRole.Should().Be(observed.ActiveRole);
        plain.Scans.Select(s => (s.Role, s.RunId, s.DrawingHash, s.Records.Count)).Should().Equal(
            observed.Scans.Select(s => (s.Role, s.RunId, s.DrawingHash, s.Records.Count)));
        plain.MissingRoles.Should().Equal(observed.MissingRoles);
        plain.Notes.Should().Equal(observed.Notes);
    }

    [Fact]
    public void AMissingRoleIsRecordedAndKeepsTheContextPending()
    {
        var (rules, active) = AllFour(includeSm: false);
        var observed = BoqRulesSourceResolver.ResolveObserved(rules, active, Runs);

        observed.PendingReasons.Should().Contain("missing_role:SM");
        observed.Receipt.Should().NotBeNull();
        observed.Receipt!.Observed.Choices.Single(c => c.RoleId == "SM").State.Should().Be(R.SelectionState.Missing);
    }

    [Fact]
    public void AnUnparsableManifestKeepsTheContextPendingWhateverTheTimeInItsFolderName()
    {
        // Codex 23:25 (BABB60AB §2): the folder name is not evidence — its manifest could be republished later than the selection.
        var (rules, active) = AllFour();
        var old = Path.Combine(Runs, "estimate-extract-20000101-000000-broken");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "run_manifest.json"), "{ not json");
        var observed = BoqRulesSourceResolver.ResolveObserved(rules, active, Runs);
        observed.PendingReasons.Should().Contain(r => r.StartsWith("inventory_unreadable_run:estimate-extract-20000101", StringComparison.Ordinal));
        observed.Receipt!.Observed.InventoryState.Should().Be(R.InventoryState.Partial);
        observed.Receipt.Observed.InventoryFailures.Should().ContainSingle(f => f.State == R.ReadState.ParseFailed);
    }

    [Fact]
    public void AFolderWithNoManifestIsRecordedButIsNeverACandidateOfThePolicy()
    {
        // The 16 manifest-less runs of 19–31.08 on the real machine: never published, so the policy cannot rank them. The
        // receipt stays Partial and names them; binding is not blocked — by their content, not by the time in their names.
        var (rules, active) = AllFour();
        var unpublished = Path.Combine(Runs, "estimate-extract-20991231-000000-legacy");
        Directory.CreateDirectory(unpublished);
        File.WriteAllText(Path.Combine(unpublished, BoqRulesSourceResolver.RecordsArtifact), "[]");
        var observed = BoqRulesSourceResolver.ResolveObserved(rules, active, Runs);
        observed.PendingReasons.Should().BeEmpty();
        observed.Receipt!.Observed.InventoryState.Should().Be(R.InventoryState.Partial);
        observed.Receipt.Observed.InventoryFailures.Should().ContainSingle(f => f.Kind == R.ArtifactKind.Manifest && f.State == R.ReadState.Missing);
    }

    [Fact]
    public void ADirectoryAtTheManifestPathIsUnreadableNotAbsent()
    {
        // Codex 23:55: File.Exists is false for a directory too — that is not proof of absence.
        var (rules, active) = AllFour();
        Directory.CreateDirectory(Path.Combine(Runs, "estimate-extract-20000101-000000-dir", "run_manifest.json"));
        var observed = BoqRulesSourceResolver.ResolveObserved(rules, active, Runs);
        observed.PendingReasons.Should().Contain(r => r.StartsWith("inventory_unreadable_run:estimate-extract-20000101-000000-dir", StringComparison.Ordinal));
        observed.Receipt!.Observed.InventoryFailures.Should().ContainSingle(f => f.State == R.ReadState.Unavailable);
    }

    [Fact]
    public void OnlyAListedFolderWithoutTheFileIsMissing()
    {
        var folder = Path.Combine(Runs, "estimate-extract-capture");
        Directory.CreateDirectory(folder);
        var options = RunManifestWriter.JsonOptions;
        BoqRulesSourceResolver.CaptureFile<RunManifest>(R.ArtifactKind.Manifest, Path.Combine(folder, "run_manifest.json"), null, options)
            .Evidence.State.Should().Be(R.ReadState.Missing, "the parent was listed and holds no such entry");
        BoqRulesSourceResolver.CaptureFile<RunManifest>(R.ArtifactKind.Manifest, Path.Combine(Runs, "vanished", "run_manifest.json"), null, options)
            .Evidence.State.Should().Be(R.ReadState.Unavailable, "a vanished parent proves nothing about the file");
        Directory.CreateDirectory(Path.Combine(folder, "neutral_quantity_records.json"));
        BoqRulesSourceResolver.CaptureFile<RunManifest>(R.ArtifactKind.Records, Path.Combine(folder, "neutral_quantity_records.json"), null, options)
            .Evidence.State.Should().Be(R.ReadState.Unavailable, "a directory at the file's path is not an absent file");
    }

    [Fact]
    public void ALatestScanWhoseRecordsAreMissingIsRefusedNotReplacedByAnOlderScan()
    {
        // Codex 23:25: an old folder name, a manifest completed after the selected scan, and no records — the latest is chosen
        // and refused explicitly; the older SM scan is never taken silently.
        var (rules, active) = AllFour();
        var sm = Path.Combine(_folder, "6422-SM-MODEL.dwg");
        Publish("estimate-extract-20000101-000000-sm2", sm, new DateTime(2026, 9, 4, 8, 0, 0, DateTimeKind.Utc), new[] { Record("7", "SML") });
        File.Delete(Path.Combine(Runs, "estimate-extract-20000101-000000-sm2", BoqRulesSourceResolver.RecordsArtifact));
        var observed = BoqRulesSourceResolver.ResolveObserved(rules, active, Runs);

        var choice = observed.Receipt!.Observed.Choices.Single(c => c.RoleId == "SM");
        choice.State.Should().Be(R.SelectionState.Rejected);
        choice.RunId.Should().Be("estimate-extract-20000101-000000-sm2");
        observed.PendingReasons.Should().Contain("rejected_role:SM");
        observed.Resolution.Scans.Should().NotContain(s => s.Role == "SM");
        observed.Resolution.MissingRoles.Should().Contain("SM");
        observed.Receipt.Observed.InventoryFailures.Should().ContainSingle(f => f.ReasonCode == "required_records_missing");
    }

    [Fact]
    public void ActiveRecordsThatDifferFromThePublishedRecordsAreNeverBound()
    {
        var (rules, active) = AllFour();
        var drifted = active with { Records = new[] { Record("5", "HAL"), Record("6", "HAL") } };
        var observed = BoqRulesSourceResolver.ResolveObserved(rules, drifted, Runs);

        observed.PendingReasons.Should().Contain("active_published_records_differ");
        observed.Receipt.Should().BeNull();
        observed.PendingReasons.Should().Contain(r => r.StartsWith("receipt_invalid:", StringComparison.Ordinal));
        observed.Sources.Should().NotContain(s => s.Role == "HA");
    }
}
