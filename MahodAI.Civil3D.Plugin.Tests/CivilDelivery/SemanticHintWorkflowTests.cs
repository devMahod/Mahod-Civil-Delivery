using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Dialog = MahodAI.Civil3D.Plugin.CivilDelivery.UI.ManualMappingReviewDialog;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Host-free local hint lifecycle. No live DWG, native acceptance or network calls.</summary>
public sealed class SemanticHintWorkflowTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Key = "layer:DSFSDF|length";

    [Fact]
    public void HintOnlySaveReloadUpdateLeavesProfileScanAndEvidenceBytesUnchanged()
    {
        using var f = new Fixture();
        var beforeProfile = JsonSerializer.Serialize(f.Profile);
        var beforeScan = JsonSerializer.Serialize(f.Scan);
        var fileBytes = File.ReadAllBytes(f.Path);
        var artifactBytes = File.ReadAllBytes(f.ArtifactPath);
        var state = f.Scan.ProfileWriteState;
        var service = new EstimateWorkflowService();
        var saved = service.SaveSemanticHint(f.Profile, f.Scan, Key, new("curb", "תיאור נבדק"),
            "SYNTHETIC REVIEWER", f.Store, null);
        var loaded = new SemanticHintStore(f.HintRoot).Read(f.Profile.ProfileId, f.Scan.SourceDrawing, Key);
        loaded.Hint!.Input.Description.Should().Be("תיאור נבדק");
        loaded.FileHash.Should().Be(saved.FileHash);
        var updated = service.SaveSemanticHint(f.Profile, f.Scan, Key, new("curb", "תיאור מתוקן"),
            "SYNTHETIC REVIEWER", f.Store, loaded.FileHash);
        updated.Hint!.Input.Description.Should().Be("תיאור מתוקן");
        f.Groups().Single().SemanticHint!.Description.Should().Be("תיאור מתוקן");
        JsonSerializer.Serialize(f.Profile).Should().Be(beforeProfile);
        JsonSerializer.Serialize(f.Scan).Should().Be(beforeScan);
        f.Scan.ProfileWriteState.Should().BeSameAs(state);
        File.ReadAllBytes(f.Path).Should().Equal(fileBytes);
        File.ReadAllBytes(f.ArtifactPath).Should().Equal(artifactBytes);
        Directory.GetFiles(f.HintRoot, "*.json").Should().ContainSingle();
        f.Profile.Estimate.QuantitySources.Rules.Should().BeEmpty();
        f.Profile.Estimate.ProjectOverrides.Should().BeEmpty();
    }

    [Fact]
    public void ChangedSourceDoesNotReuseHintButRetainsItsSidecarCasForExplicitReplacement()
    {
        using var f = new Fixture();
        var saved = new EstimateWorkflowService().SaveSemanticHint(f.Profile, f.Scan, Key, new("curb", ""),
            "FIXTURE", f.Store, null);
        f.Groups().Single().SemanticHint!.RoleId.Should().Be("curb");
        f.Scan = f.NewScan(new string('b', 64));
        var changed = f.Groups().Single();
        changed.SemanticHint.Should().BeNull();
        changed.SemanticHintRevision.Should().Be(saved.FileHash);
        changed.SemanticHintStoreAvailable.Should().BeTrue();
    }

    [Fact]
    public void UnreadableLocalHintShowsRecoveryAndPreservesManualMapping() => Sta(() =>
    {
        using var f = new Fixture();
        new EstimateWorkflowService().SaveSemanticHint(f.Profile, f.Scan, Key, new("curb", ""), "FIXTURE", f.Store, null);
        var sidecar = Directory.GetFiles(f.HintRoot, "*.json").Single();
        File.WriteAllText(sidecar, "invalid synthetic json");
        var group = f.Groups().Single();
        group.SemanticHintStoreAvailable.Should().BeFalse();
        var dialog = new Dialog(new[] { group }, f.Catalog, saveSemanticHint: (_, _, _) => throw new Exception("must not write"));
        try
        {
            dialog.SaveSemanticHint.IsEnabled.Should().BeFalse(); dialog.TrySaveSemanticHint().Should().BeFalse();
            dialog.AiStatus.Text.Should().Contain("אינו זמין");
            dialog.CatalogSearch.Text = "TEST.LENGTH"; dialog.CatalogGrid.SelectedIndex = 0;
            dialog.StageSelection().Should().BeTrue();
            File.ReadAllText(sidecar).Should().Be("invalid synthetic json");
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void StagedMappingSurvivesDurableHintSaveThenExplicitMappingSave() => Sta(() =>
    {
        using var f = new Fixture();
        var service = new EstimateWorkflowService();
        string? revision = null;
        var dialog = new Dialog(f.Groups(), f.Catalog,
            saveReviewed: (choices, author) =>
            {
                var saved = service.SaveReviewedMappings(f.Profile, f.Catalog, f.Scan,
                    choices.Select(choice => new EstimateWorkflowService.ReviewedMappingChoice(choice.RuleKey, choice.CatalogCode)).ToArray(),
                    author, f.Path, f.Scan.ProfileWriteState!);
                f.Scan = EstimateWorkflowService.RebaseAfterProfileDecision(f.Scan, f.Profile, saved, Key);
            },
            saveSemanticHint: (_, input, author) =>
                revision = service.SaveSemanticHint(f.Profile, f.Scan, Key, input, author, f.Store, revision).FileHash);
        try
        {
            dialog.CatalogSearch.Text = "TEST.LENGTH"; dialog.CatalogGrid.SelectedIndex = 0;
            dialog.StageSelection().Should().BeTrue(); dialog.Approver.Text = "FIXTURE";
            dialog.SemanticRole.SelectedValue = "curb"; dialog.AiContext.Text = "משמעות שנבדקה";
            var version = f.Profile.Provenance.Version;
            dialog.TrySaveSemanticHint().Should().BeTrue();
            f.Profile.Provenance.Version.Should().Be(version);
            f.Profile.Estimate.QuantitySources.Rules.Should().BeEmpty("saving meaning never approves the staged item");
            dialog.Confirm.IsChecked = true; dialog.TryConfirm().Should().BeTrue();
            var loaded = ProjectProfileLoader.LoadFromFile(f.Path).Profile!;
            loaded.Estimate.QuantitySources.Rules.Should().ContainSingle().Which.CandidateCatalogCode.Should().Be("TEST.LENGTH");
            loaded.Estimate.ProjectOverrides.Should().BeEmpty();
            f.Scan.Records.Single().Measurement.RawValue.Should().Be(12.5);
            f.Scan.Records.Single().Classification.CandidateCatalogCode.Should().Be("TEST.LENGTH");
            f.Groups().Single().SemanticHint!.Description.Should().Be("משמעות שנבדקה");
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData("other-rule")]
    [InlineData("changed-profile")]
    [InlineData("changed-sidecar")]
    public void StaleOrForeignScopeDoesNotSaveHintOrChangeMapping(string defect)
    {
        using var f = new Fixture();
        if (defect == "changed-profile") f.Profile.ProjectName = "CONCURRENT SYNTHETIC CHANGE";
        if (defect == "changed-sidecar") new EstimateWorkflowService().SaveSemanticHint(f.Profile, f.Scan, Key,
            new("curb", "first author"), "FIXTURE", f.Store, null);
        var before = JsonSerializer.Serialize(f.Profile);
        var profileBytes = File.ReadAllBytes(f.Path);
        Action save = () => new EstimateWorkflowService().SaveSemanticHint(f.Profile, f.Scan,
            defect == "other-rule" ? "another" : Key, new("curb", "second author"), "FIXTURE", f.Store, null);
        save.Should().Throw<InvalidOperationException>();
        JsonSerializer.Serialize(f.Profile).Should().Be(before);
        File.ReadAllBytes(f.Path).Should().Equal(profileBytes);
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) throw new TimeoutException("Hint workflow fixture did not finish.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mahod-hint-workflow-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(_directory, "fixture-profile.yaml");
        internal string ArtifactPath => System.IO.Path.Combine(_directory, "scan-evidence.json");
        internal string HintRoot => System.IO.Path.Combine(_directory, "local-hints");
        internal SemanticHintStore Store => new(HintRoot);
        internal ProjectProfile Profile { get; } = new() { ProfileId = "hint-workflow" };
        internal CatalogSnapshot Catalog { get; } = new() { SnapshotId = "HINT-TEST-CATALOG", FileHash = Hash,
            Items = { ["TEST.LENGTH"] = new() { Code = "TEST.LENGTH", Description = "אבני שפה לדוגמה", UnitRaw = "m" } } };
        internal EstimateWorkflowService.ScanResult Scan { get; set; }
        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            Profile.Estimate.Catalog.CatalogFile = "HINT-TEST-CATALOG.xlsx";
            Profile.Estimate.Catalog.CatalogFileHash = Catalog.FileHash;
            Profile.Estimate.Pricing.PriceBookSnapshotId = Catalog.SnapshotId;
            Profile.Estimate.Pricing.PriceBookHash = Catalog.FileHash;
            Profile.Estimate.PriceBooks.Add(new() { Id = Catalog.SnapshotId, File = "HINT-TEST-CATALOG.xlsx", FileHash = Catalog.FileHash });
            ProfileCasTest.Save(Profile, Path, "fixture baseline", "FIXTURE");
            Scan = NewScan();
            File.WriteAllText(ArtifactPath, JsonSerializer.Serialize(Scan));
        }
        internal EstimateWorkflowService.ScanResult NewScan(string sourceHash = Hash) => new()
        {
            RunId = "HINT-SIMULATION-ONLY", ProjectProfileId = Profile.ProfileId, ProfileSource = Path,
            SourceDrawing = @"C:\fixture\host.dwg", SourceDrawingHash = sourceHash, SourceDbMod = 0,
            DatabaseRevision = "SIMULATION", ProjectProfileHash = ArtifactHash.Sha256OfText(File.ReadAllText(Path)),
            ProjectProfileEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(Profile),
            ProfileWriteState = ProfileCasTest.For(Profile, Path), DiscoveryMode = true,
            Records = { new() { RecordId = "one", RunId = "HINT-SIMULATION-ONLY", ProjectProfileId = Profile.ProfileId,
                Source = new() { Drawing = "host.dwg", DrawingPath = @"C:\fixture\host.dwg", DrawingHash = sourceHash,
                    EntityType = "LINE", Handle = "1", Layer = "DSFSDF" },
                Measurement = new() { Kind = "length", Method = "line-length", Unit = "m", RawValue = 12.5 },
                Classification = new() { RuleKey = Key }, Status = DeliveryStatus.ReviewRequired } },
        };
        internal IReadOnlyList<Dialog.Group> Groups() => CivilDeliveryControl.BuildMappingReviewGroups(Scan,
            new[] { new QuantityRowViewModel { RuleKey = Key, Layer = "DSFSDF", EntityType = "LINE", Method = "length",
                ObjectCount = 1, Quantity = 12.5, Unit = "m", MappingState = "לבדיקה" } }, Array.Empty<MappingProposal>(), Profile, Store);
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
