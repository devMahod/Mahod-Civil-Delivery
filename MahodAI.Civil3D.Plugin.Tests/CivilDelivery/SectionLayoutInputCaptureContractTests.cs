using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Bounds = MahodAI.CivilDelivery.Shared.SectionAnnotationPlacementLogic.Bounds;
using LabelBox = MahodAI.CivilDelivery.Shared.SectionAnnotationPlacementLogic.LabelBox;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Diagnostic layout-input capture for selected-record VERIFY. Source contracts pin
/// the narrow seam (batch keeps the five-argument call, the observer is assignment
/// only, publication only through the VERIFY evidence bundle); host-free tests pin
/// the receipt shape. Neither is a native capture, a replay or layout acceptance.
/// </summary>
public sealed class SectionLayoutInputCaptureContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcd-layout-capture-test-" + Guid.NewGuid().ToString("N"));
    private const string Run = "SYNTHETIC-ONLY-verify";
    private const string Record = "cl-SYNTHETIC";

    private static string PluginSourceDir => typeof(SectionLayoutInputCaptureContractTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "MahodPluginSourceDir").Value!;

    private static string Service(string name) =>
        File.ReadAllText(Path.Combine(PluginSourceDir, "CivilDelivery", "Sections", "Services", name));

    // Whitespace and line-ending neutral: these pins are about tokens, not layout.
    private static string Flat(string source) => Regex.Replace(source, @"\s+", " ");

    private static int Count(string source, string token) => source.Split(token).Length - 1;

    [Fact]
    public void BatchVerifyKeepsTheFiveArgumentCall_AndOnlySelectedScopePassesAnObserver()
    {
        var raw = Service("SectionVerifyService.cs");
        var verify = Flat(raw);
        const string fiveArguments = "SectionAnnotationPlacementContract.ComputeLabelLayout( " +
            "sv, planRecord, applyRecord, placementSurfaces, nativeAnnotations)";
        const string observed = "SectionAnnotationPlacementContract.ComputeLabelLayout( " +
            "sv, planRecord, applyRecord, placementSurfaces, nativeAnnotations, inputs => capturedInputs = inputs);";

        Count(verify, "ComputeLabelLayout(").Should().Be(2);
        Count(verify, observed).Should().Be(1, "the only observer is a plain local assignment");
        verify.Should().Contain("measuredLayout = reportLayoutInputs == null ? " + fiveArguments + " : " + observed,
                "a null observer (batch) takes the original five-argument call")
            .And.Contain("Action<SectionLayoutInputCaptureRecord>? reportLayoutInputs = selectedScope " +
                "? capture => result.LayoutInputCapture = capture : null;")
            .And.Contain("VerifyOne(db, tr, civilDoc, plan, planRecord, applyRecord, recordResult, " +
                "sampledSourcesByGroup, fingerprintDiagnostics, reportLayoutInputs);")
            .And.Contain("SectionMeasuredLayoutInputs? capturedInputs = null; try { if (sv == null || placementSurfaces == null",
                "the snapshot is declared outside the guarded try so a refused solver keeps its input")
            .And.Contain("catch (Exception ex) { measuredLayoutError = ex.Message; }")
            .And.Contain("SectionAnnotationPlacementContract.RequirePlacedLabelBounds(measuredLayout, nativeAnnotations);")
            .And.Contain("SectionAnnotationPlacementContract.RequireLayoutLeaders(measuredLayout, nativeAnnotations);");
        Count(verify, "result.LayoutInputCapture =").Should().Be(1);
        Count(verify, "capturedInputs =").Should().Be(2, "declaration plus the observer; nothing else writes it");
        Count(verify, "reportLayoutInputs?.Invoke(").Should().Be(1);
        raw.IndexOf("reportLayoutInputs?.Invoke(SectionLayoutInputCapture.ForRecord(", StringComparison.Ordinal)
            .Should().BeGreaterThan(raw.IndexOf("Check(\"native_measured_label_layout_exact\"", StringComparison.Ordinal),
                "the receipt is built after the unchanged check and cannot influence it");
        raw.Should().NotContain("canonical_layout_inputs.json")
            .And.NotContain("SectionLayoutInputCapture.Stage(")
            .And.NotContain("JsonSerializer.")
            .And.NotContain("File.Write");

        // APPLY (the emitter) never observes: it keeps the same five-argument contract.
        var decoration = Service("SectionDecorationService.cs");
        Count(decoration, "ComputeLabelLayout(").Should().Be(1);
        Flat(decoration).Should().Contain(
            "var measuredLayout = SectionAnnotationPlacementContract.ComputeLabelLayout( view, record, rec, surfaceChains, created);");
        decoration.Should().NotContain("SectionMeasuredLayoutInputs").And.NotContain("LayoutInputCapture");
    }

    [Fact]
    public void ContractObserverRunsOnceAfterClearanceAndBeforeTheFirstSolverCall()
    {
        var contract = Service("SectionAnnotationPlacementContract.cs");
        Flat(contract).Should().Contain(
                "SurfaceChains surfaces, IReadOnlyList<Entity> registeredEntities) => " +
                "ComputeLabelLayout(view, record, applied, surfaces, registeredEntities, observeInputs: null);")
            .And.Contain("Action<SectionMeasuredLayoutInputs>? observeInputs)");
        Count(contract, "observeInputs").Should().Be(3, "forwarder argument, parameter and one invocation");
        Count(contract, "observeInputs?.Invoke(").Should().Be(1);
        var clearance = contract.IndexOf("var clearance = textHeight[textHeight.Count / 2] * 0.45;", StringComparison.Ordinal);
        contract.Should().Contain("var wordGap = textHeight[textHeight.Count / 2] * 1.5;")
            .And.Contain("out var layout, out _, out var layoutError, wordGap, bandGap)")
            .And.Contain("[3] = textHeight[textHeight.Count / 2] * 1.0,")
            .And.Contain("[4] = textHeight[textHeight.Count / 2] * 1.0,")
            .And.Contain("var bottomClearance = textHeight[textHeight.Count / 2] * 0.30;")
            .And.Contain("obstacles.Concat(layout.Labels.Select(l => l.Ink)).ToList(), bottomClearance,");
        var observe = contract.IndexOf("observeInputs?.Invoke(SectionMeasuredLayoutInputs.Capture(", StringComparison.Ordinal);
        var solve = contract.IndexOf("SectionAnnotationPlacementLogic.TryLayoutLabelsClearOfLines(labels", StringComparison.Ordinal);
        clearance.Should().BeGreaterThan(0);
        observe.Should().BeGreaterThan(clearance);
        solve.Should().BeGreaterThan(observe);
        Service("SectionMeasuredLayoutInputs.cs").Should().NotContain("Autodesk")
            .And.NotContain("File.").And.NotContain("Json");
    }

    [Fact]
    public void ReceiptIsPublishedOnlyThroughTheSelectedVerifyEvidenceBundle()
    {
        var separator = Path.DirectorySeparatorChar;
        var sources = Directory.GetFiles(PluginSourceDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .ToList();
        sources.Where(file => file.Text.Contains("SectionLayoutInputCapture.Stage(", StringComparison.Ordinal))
            .Select(file => file.Name).Should().Equal("SectionsWorkflowService.cs");
        sources.Where(file => file.Text.Contains("\"canonical_layout_inputs.json\"", StringComparison.Ordinal))
            .Select(file => file.Name).Should().Equal("SectionLayoutInputCapture.cs");
        sources.Where(file => file.Text.Contains("SectionLayoutInputCapture.Write(", StringComparison.Ordinal))
            .Should().BeEmpty("only Stage, inside the bundle, writes the receipt");

        var workflow = Service("SectionsWorkflowService.cs");
        var start = workflow.IndexOf("internal static bool PersistVerifyEvidence(", StringComparison.Ordinal);
        var end = workflow.IndexOf("internal static void PersistEvidenceBundle<T>(", start, StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        end.Should().BeGreaterThan(start);
        Flat(workflow[start..end]).Should()
            .Contain("var selectedScope = string.Equals( result.Scope, \"selected-record\", StringComparison.Ordinal);")
            .And.Contain("Action<string>? stageLayoutInputs = selectedScope ? pendingRoot => SectionLayoutInputCapture.Stage( " +
                "pendingRoot, doc, profile, profileHash, plan, applied, result) : null;")
            .And.Contain("(pendingRoot, publishedRoot) => WriteVerifyManifest(")
            .And.Contain("stageAdditionalArtifacts: stageLayoutInputs);");
        Count(workflow, "SectionLayoutInputCapture.").Should().Be(1, "APPLY and PLAN bundles never stage the receipt");

        var capture = Service("SectionLayoutInputCapture.cs");
        capture.Should().Contain("Path.Combine(pendingRoot, runId)")
            .And.Contain("\".pending-\"")
            .And.NotContain("RunsRoot")
            .And.NotContain("StageLog")
            .And.NotContain("OpenMode")
            .And.NotContain("GetObject(")
            .And.NotContain("TryLayoutLabels")
            .And.NotContain("ComputeLabelLayout(");
    }

    [Fact]
    public void StagedReceiptIsInThePendingRunWhenTheManifestIsWritten_AndPublishedWithTheResult()
    {
        var artifact = SectionLayoutInputCapture.Compose(Identity(), SelectedResult(GuardRefused()));
        var atManifest = Array.Empty<string?>();
        SectionsWorkflowService.PersistEvidenceBundle(Run, "verify_result.json", new { Status = "Failed" },
            (pendingRoot, publishedRoot) =>
            {
                publishedRoot.Should().Be(_root);
                var pendingRun = Path.Combine(pendingRoot, Run);
                atManifest = Directory.GetFiles(pendingRun).Select(Path.GetFileName).ToArray();
                File.WriteAllText(Path.Combine(pendingRun, "run_manifest.json"), "synthetic manifest");
            },
            stageAdditionalArtifacts: pendingRoot => SectionLayoutInputCapture.Write(pendingRoot, Run, artifact),
            runsRoot: _root);

        // RuntimeRunManifestService lists and hashes exactly Directory.GetFiles of this
        // pending run (pinned by SectionManifests_CoverDirectAndAiRoutesArtifactsAndSourceFiles).
        atManifest.Should().BeEquivalentTo(new[] { "verify_result.json", SectionLayoutInputCapture.ArtifactName });
        File.Exists(Path.Combine(_root, Run, SectionLayoutInputCapture.ArtifactName)).Should().BeTrue();
        Directory.GetDirectories(_root).Select(Path.GetFileName).Should().Equal(Run);
    }

    [Fact]
    public void ReceiptRefusesAnyRootOutsideAPendingBundle()
    {
        var artifact = SectionLayoutInputCapture.Compose(Identity(), SelectedResult(null));
        Action outside = () => SectionLayoutInputCapture.Write(_root, Run, artifact);
        outside.Should().Throw<InvalidOperationException>();
        Directory.Exists(Path.Combine(_root, Run)).Should().BeFalse();
    }

    [Fact]
    public void BlockedVerifyPublishesStatusAndReason_WithoutInventedArrays()
    {
        var blocked = new SectionVerifyResult
        {
            RunId = Run, Scope = "selected-record", SelectedRecordId = Record, Status = DeliveryStatus.Blocked,
        };
        var json = Written(SectionLayoutInputCapture.Compose(Identity(), blocked));
        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("capture_status").GetString().Should().Be("not-captured");
        root.GetProperty("capture_reason").GetString().Should().Contain("before per-record read-back").And.Contain("Blocked");
        root.GetProperty("record").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("verify").GetProperty("record_status").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("verify").GetProperty("failed_checks").ValueKind.Should().Be(JsonValueKind.Null);
        json.Should().NotContain("top_labels").And.NotContain("fixed_obstacles");
    }

    [Fact]
    public void GuardRefusalBeforeTheSolverKeepsInputsMissingWithTheRealError()
    {
        var record = GuardRefused();
        record.LayoutStage.Should().Be(SectionLayoutInputCapture.RefusedBeforeCapture);
        record.LayoutCheckPass.Should().BeFalse();
        record.Inputs.Should().BeNull();
        record.Counts.Should().BeNull();
        record.LabelMetadata.Should().BeNull();
        record.AnnotationRegistryError.Should().Be("missing entry");

        var root = JsonDocument.Parse(Written(SectionLayoutInputCapture.Compose(Identity(), SelectedResult(record))))
            .RootElement;
        root.GetProperty("capture_status").GetString().Should().Be("not-captured");
        root.GetProperty("capture_reason").GetString().Should().Be(GuardError);
        root.GetProperty("record").GetProperty("inputs").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("record").GetProperty("counts").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("verify").GetProperty("failed_checks").EnumerateArray().Select(item => item.GetString())
            .Should().Equal("native_measured_label_layout_exact");
    }

    [Fact]
    public void CapturedInputsAreCopiedExactly_AndTheHandleJoinIsReportedNotRepaired()
    {
        var top = new[]
        {
            new LabelBox("1A", 0, new(0.1 + 0.2, 1.0 / 3.0), new Bounds(0.1, 0.2, 0.7, 0.45)),
            new LabelBox("1B", 7, new(12.5, 40.125), new Bounds(10.0, 40.0, 15.0, 40.25)),
        };
        var bottom = new[] { new LabelBox("2C", 1, new(-3.0, -1.6), new Bounds(-3.5, -1.7, -2.5, -1.5)) };
        // Equal obstacle values stay two unassigned obstacles in their captured order.
        var obstacles = new[] { new Bounds(1, 1, 2, 2), new Bounds(1, 1, 2, 2) };
        var captured = SectionMeasuredLayoutInputs.Capture(top, bottom, obstacles, 0.25, 0.25 * 0.30, 0.25 * 60);
        var metadata = new[] { Meta("1a"), Meta("1B"), Meta("1B"), Meta("FFFF") };
        var record = SectionLayoutInputCapture.ForRecord(Record, "MCD:synthetic", "4A2F",
            metadata, "missing entry", captured, null, "synthetic solver refusal");

        record.LayoutStage.Should().Be(SectionLayoutInputCapture.ComputeFailedAfterCapture);
        record.LayoutCheckPass.Should().BeFalse();
        record.AnnotationRegistryError.Should().BeNull();
        var counts = record.Counts!;
        counts.TopLabels.Should().Be(2);
        counts.BottomLabels.Should().Be(1);
        counts.MovableLabels.Should().Be(3);
        counts.UniqueLabelIds.Should().Be(3);
        counts.DuplicateLabelIds.Should().BeEmpty();
        counts.FixedObstacles.Should().Be(2);
        counts.RegisteredAnnotations.Should().Be(4);
        counts.LabelIdsWithSingleMetadata.Should().Be(1);
        counts.LabelIdsWithoutMetadata.Should().Equal("2C");
        counts.LabelIdsWithDuplicateMetadata.Should().Equal("1B");

        var root = JsonDocument.Parse(Written(SectionLayoutInputCapture.Compose(Identity(), SelectedResult(record))))
            .RootElement;
        root.GetProperty("capture_status").GetString().Should().Be("captured");
        root.GetProperty("capture_reason").ValueKind.Should().Be(JsonValueKind.Null);
        var inputs = root.GetProperty("record").GetProperty("inputs");
        var first = inputs.GetProperty("top_labels")[0];
        first.GetProperty("id").GetString().Should().Be("1A");
        first.GetProperty("anchor").GetProperty("x").GetDouble().Should().Be(0.1 + 0.2);
        first.GetProperty("anchor").GetProperty("y").GetDouble().Should().Be(1.0 / 3.0);
        inputs.GetProperty("top_labels")[1].GetProperty("band").GetInt32().Should().Be(7);
        inputs.GetProperty("bottom_labels")[0].GetProperty("ink").GetProperty("min_y").GetDouble().Should().Be(-1.7);
        inputs.GetProperty("median_text_height").GetDouble().Should().Be(0.25);
        inputs.GetProperty("clearance").GetDouble().Should().Be(0.25 * 0.30);
        inputs.GetProperty("maximum_rise").GetDouble().Should().Be(0.25 * 60);
        var fixedObstacles = inputs.GetProperty("fixed_obstacles");
        fixedObstacles.GetArrayLength().Should().Be(2);
        fixedObstacles[0].EnumerateObject().Select(property => property.Name)
            .Should().Equal("min_x", "min_y", "max_x", "max_y");
        root.GetProperty("record").GetProperty("layout_output").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(true, null, "layout-exact", true)]
    [InlineData(true, "Native text bounds differ from measured layout: 1A", "native-readback-mismatch", false)]
    [InlineData(false, "synthetic solver refusal", "compute-failed-after-capture", false)]
    public void StageMirrorsWhereTheUnchangedVerifyLayoutPathStopped(
        bool computed, string? error, string stage, bool pass)
    {
        var captured = SectionMeasuredLayoutInputs.Capture(
            Array.Empty<LabelBox>(), Array.Empty<LabelBox>(), Array.Empty<Bounds>(), 0.25, 0.075, 15);
        var output = computed
            ? new SectionLayoutCapturedOutput { OverallBounds = new SectionLayoutCapturedBounds(), Clearance = 0.075 }
            : null;
        var record = SectionLayoutInputCapture.ForRecord(Record, null, null, null, "missing entry",
            captured, output, error);
        record.LayoutStage.Should().Be(stage);
        record.LayoutCheckPass.Should().Be(pass);
    }

    [Fact]
    public void VerifyResultJsonIsIdenticalWithOrWithoutTheAttachedReceipt()
    {
        var result = new SectionVerifyResult { RunId = Run, Scope = "selected-record", SelectedRecordId = Record };
        var before = JsonSerializer.Serialize(result, SectionsWorkflowService.Json);
        result.LayoutInputCapture = GuardRefused();
        JsonSerializer.Serialize(result, SectionsWorkflowService.Json).Should().Be(before,
            "verify_result.json and every producer hash derived from it stay unchanged");
        before.Should().NotContain("layout_input").And.NotContain("LayoutInputCapture");
    }

    private const string GuardError = "Layout requires readable owned annotations and exact live surfaces.";

    private static SectionLayoutInputCaptureRecord GuardRefused() =>
        SectionLayoutInputCapture.ForRecord(Record, "MCD:synthetic", "4A2F",
            null, "missing entry", null, null, GuardError);

    private static SectionLayoutAnnotationMetadata Meta(string handle) => new()
    {
        Handle = handle, EntityType = "DBText", Text = "SYNTHETIC",
    };

    private static SectionLayoutInputCaptureIdentity Identity() => new()
    {
        HostPath = "SYNTHETIC-ONLY.dwg", HostHashError = "synthetic: not hashed",
        PlanRunId = "SYNTHETIC-plan", ApplyRunId = "SYNTHETIC-apply", VerifyRunId = Run,
        SelectedRecordId = Record,
    };

    private static SectionVerifyResult SelectedResult(SectionLayoutInputCaptureRecord? record) => new()
    {
        RunId = Run, Scope = "selected-record", SelectedRecordId = Record, Status = DeliveryStatus.Failed,
        Records =
        {
            new SectionVerifyRecordResult
            {
                RecordId = Record, Status = DeliveryStatus.Failed,
                Checks = { new SectionVerifyCheck { Check = "native_measured_label_layout_exact", Pass = false } },
            },
        },
        LayoutInputCapture = record,
    };

    private string Written(SectionLayoutInputCaptureArtifact artifact) =>
        File.ReadAllText(SectionLayoutInputCapture.Write(
            Path.Combine(_root, ".pending-" + Guid.NewGuid().ToString("N")), Run, artifact));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
