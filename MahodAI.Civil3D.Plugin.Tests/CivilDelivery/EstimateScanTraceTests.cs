using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>The opt-in scan journal: silent without the variable, and past its cap it still records stage boundaries.</summary>
[Collection("EnvironmentVariable")]
public sealed class EstimateScanTraceTests : IDisposable
{
    private const string Variable = "MAHOD_SCAN_DIAGNOSTICS";
    private readonly string? _saved = Environment.GetEnvironmentVariable(Variable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scan-trace-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Variable, _saved);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string[] Stages() => File.ReadAllLines(Directory.GetFiles(_dir).Single())
        .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("stage").GetString()!).ToArray();

    [Fact]
    public void WithoutTheVariableNothingIsWrittenAndStepsStillRun()
    {
        Environment.SetEnvironmentVariable(Variable, null);
        using (var trace = EstimateScanTrace.Start("TEST", _dir))
        {
            Assert.Null(trace);
            Assert.Equal(7, EstimateScanTrace.Step("stage", () => 7));
        }
        Assert.False(Directory.Exists(_dir));
    }

    private sealed class FailingWriter : StringWriter
    {
        public bool Disposed { get; private set; }
        public override void WriteLine(string? value) => throw new IOException("SYNTHETIC: the journal cannot be written");
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    [Fact]
    public void AFailingFirstWriteLeavesNoActiveTraceClosesTheWriterAndKeepsTheOriginalError()
    {
        // Codex 18:24: Start set the trace active before its first line; a failing write left it active without a disposer.
        Environment.SetEnvironmentVariable(Variable, "1");
        var open = EstimateScanTrace.OpenWriter;
        FailingWriter? failing = null;
        try
        {
            EstimateScanTrace.OpenWriter = _ => failing = new FailingWriter();
            var error = Assert.Throws<IOException>(() => EstimateScanTrace.Start("activation", _dir));
            Assert.Contains("SYNTHETIC", error.Message);
            Assert.False(EstimateScanTrace.Active);
            Assert.True(failing!.Disposed);
        }
        finally { EstimateScanTrace.OpenWriter = open; }
        using (var later = EstimateScanTrace.Start("activation", _dir))
        {
            Assert.NotNull(later);
            Assert.True(EstimateScanTrace.Active);
        }
        Assert.False(EstimateScanTrace.Active);
        Environment.SetEnvironmentVariable(Variable, null);
        using (var off = EstimateScanTrace.Start("activation", Path.Combine(_dir, "off")))
        {
            Assert.Null(off);
            Assert.Equal(3, EstimateScanTrace.Step("stage", () => 3));
        }
    }

    [Fact]
    public void AnEnabledFileInTheJournalFolderTurnsTheTraceOnWithoutTheVariable()
    {
        // b26: Civil started from the taskbar does not see a variable set later; the user environment is not changed for a
        // diagnostic. An ENABLED file in the journal folder is the second, explicit switch.
        Environment.SetEnvironmentVariable(Variable, null);
        Directory.CreateDirectory(_dir);
        Assert.False(EstimateScanTrace.IsEnabled(_dir));
        using (var off = EstimateScanTrace.Start("activation", _dir)) Assert.Null(off);
        File.WriteAllText(Path.Combine(_dir, "ENABLED"), "");
        Assert.True(EstimateScanTrace.IsEnabled(_dir));
        using (var trace = EstimateScanTrace.Start("activation", _dir))
        {
            Assert.NotNull(trace);
            EstimateScanTrace.Mark("dbmod.activation.enter", 0);
        }
        var journal = Assert.Single(Directory.GetFiles(_dir, "scan_*.jsonl"));
        Assert.Contains("dbmod.activation.enter", File.ReadAllText(journal));
    }

    [Fact]
    public void TheActivationCallbacksAreWrappedOnlyByTheOptInTraceAndProbe()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryControl.DrawingSaveContext.cs"));
        var activated = source[source.IndexOf("private void OnObservedDocumentActivated(", StringComparison.Ordinal)..];
        activated = activated[..activated.IndexOf("QueueIdleGateRefresh();", StringComparison.Ordinal)];
        Assert.Contains("using (Shared.EstimateScanTrace.Start(\"activation\"))", activated);
        Assert.Contains("using (Shared.ScanDatabaseChangeProbe.Attach(e.Document))", activated);
        Assert.True(activated.IndexOf("\"activation.enter\"", StringComparison.Ordinal) <
                    activated.IndexOf("Shared.EstimateScanTrace.Mark(\"activation.drawing-changed.begin\");", StringComparison.Ordinal));
        Assert.Contains("Shared.EstimateScanTrace.Mark(\"activation.drawing-changed.end\");", activated);
        Assert.Contains("Dispatcher.BeginInvoke(new Action(RefreshGatesAfterActivation))", source);
        var idle = source[source.IndexOf("private void RefreshGatesAfterActivation(", StringComparison.Ordinal)..];
        idle = idle[..idle.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("Shared.EstimateScanTrace.Step(\"idle.refresh-gates\", RefreshGates);", idle);
        // nothing here resets DBMOD, saves or closes
        Assert.DoesNotContain("DBMOD\", 0", source);
        Assert.DoesNotContain(".Save", activated + idle);
    }

    [Fact]
    public void PastTheCapObjectMarksStopButStageAndXrefBoundariesKeepTheirEvidence()
    {
        Environment.SetEnvironmentVariable(Variable, "1");
        using (EstimateScanTrace.Start("TEST", _dir, recordLimit: 4))
        {
            for (var i = 0; i < 10; i++) EstimateScanTrace.Mark("entity.consider", i, "H" + i, "Hatch");
            EstimateScanTrace.Step("xref.loaded_database", () => true, 1, "1A2/3B4");
            EstimateScanTrace.Step("evidence.legends", () => { });
            Assert.Throws<InvalidOperationException>(() =>
                EstimateScanTrace.Step("evidence.complete", () => throw new InvalidOperationException()));
        }
        var stages = Stages();
        Assert.Equal(new[]
        {
            "route.begin", "entity.consider", "entity.consider", "entity.consider",
            "trace.limit_reached_object_marks_stopped",
            "xref.loaded_database.begin", "xref.loaded_database.end",
            "evidence.legends.begin", "evidence.legends.end", "evidence.complete.begin", "evidence.complete.fail",
            "route.dispose_not_success",
        }, stages);
    }

    [Fact]
    public void ANestedRouteKeepsTheOriginatingJournal()
    {
        Environment.SetEnvironmentVariable(Variable, "1");
        using (EstimateScanTrace.Start("palette", _dir))
        {
            using (var nested = EstimateScanTrace.Start("shared-scan", _dir)) Assert.Null(nested);
            EstimateScanTrace.Mark("scan.entry");
        }
        Assert.Equal(new[] { "route.begin", "scan.entry", "route.dispose_not_success" }, Stages());
    }
}
