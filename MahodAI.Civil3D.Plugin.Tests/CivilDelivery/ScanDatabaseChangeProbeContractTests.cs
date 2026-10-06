using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Source contract of the opt-in DBMOD diagnostic (Codex 01:40 / 01:42, 02/10): read-only callbacks that only buffer,
/// active only inside the scan trace, own listeners removed before the buffer is written, and no drawing action at all.
/// Not a native proof of the cause; that needs the traced live scan.
/// </summary>
public sealed class ScanDatabaseChangeProbeContractTests
{
    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery" }.Concat(parts).ToArray()));

    [Fact]
    public void TheProbeOnlyBuffersAndNeverActsOnTheDrawing()
    {
        var probe = Source("Shared", "ScanDatabaseChangeProbe.cs");
        foreach (var forbidden in new[] { "ForWrite", "UpgradeOpen", "GetObject(", ".Open(", "SetSystemVariable", "SaveAs", "CloseAndDiscard",
                     "CloseAndSave", "File.", "StartTransaction", "SendStringToExecute", "DBMOD\", 0" })
            probe.Should().NotContain(forbidden);
        probe.Should().Contain("if (!EstimateScanTrace.Active || doc == null) return null;", "attached only inside the opt-in trace");

        var capture = Between(probe, "private void Capture(", "public void Dispose()");
        capture.Should().NotContain("EstimateScanTrace.Mark", "the callback writes nothing; Dispose writes the buffer");
        capture.Should().NotContain("GetSystemVariable");

        var dispose = probe[probe.IndexOf("public void Dispose()", StringComparison.Ordinal)..];
        dispose.IndexOf("_disposed = true;", StringComparison.Ordinal).Should()
            .BeLessThan(dispose.IndexOf("Detach()", StringComparison.Ordinal), "buffering stops before the listeners are removed");
        dispose.IndexOf("Detach()", StringComparison.Ordinal).Should()
            .BeLessThan(dispose.IndexOf("EstimateScanTrace.Mark(", StringComparison.Ordinal), "own listeners are removed before writing");
        dispose.Should().Contain("\"db.detach_failed\"", "a listener that could not be removed is reported");
        // Codex 02:31: each listener is removed in its own try, so one failure does not skip the others.
        var detach = Between(probe, "private List<string> Detach()", "private void OnModified(");
        foreach (var listener in new[] { "_db.ObjectModified -= OnModified;", "_db.ObjectAppended -= OnAppended;", "_db.ObjectErased -= OnErased;" })
            detach.Should().Contain("try { " + listener);
        var constructor = Between(probe, "private ScanDatabaseChangeProbe(Database db)", "internal static ScanDatabaseChangeProbe? Attach(");
        constructor.Should().Contain("var rollbackFailures = Detach();", "a failed attach rolls back the listeners already added")
            .And.Contain("\"probe.attach.rollback_failed\"", "a failed rollback is reported").And.Contain("throw;");
        constructor.IndexOf("probe.attach.rollback_failed", StringComparison.Ordinal).Should()
            .BeLessThan(constructor.IndexOf("throw;", StringComparison.Ordinal), "reported before the rethrow");
        // Codex 02:38: each handler returns on _disposed before it reads e.DBObject.
        foreach (var handler in new[] { "private void OnModified(", "private void OnAppended(", "private void OnErased(" })
        {
            var body = probe[probe.IndexOf(handler, StringComparison.Ordinal)..];
            body.IndexOf("if (_disposed) return;", StringComparison.Ordinal).Should()
                .BeLessThan(body.IndexOf("e.DBObject", StringComparison.Ordinal), handler);
        }
        probe.Should().NotContain("DrawingRevisionTracker.", "the revision tracker's listeners are never touched");
    }

    [Fact]
    public void ThePaletteScanAttachesTheProbeBeforeTheProjectStepAndSamplesTheTransactionBoundaries()
    {
        var palette = Source("UI", "CivilDeliveryControl.xaml.cs");
        var onScan = Between(palette, "private void OnScan(", "private void OfferExplicitSaveAndResume(");
        onScan.IndexOf("using var databaseProbe = ScanDatabaseChangeProbe.Attach(doc);", StringComparison.Ordinal).Should()
            .BeGreaterThan(0).And.BeLessThan(onScan.IndexOf("EnsureEstimateProjectForScan", StringComparison.Ordinal));

        var service = Source("Estimate", "EstimateWorkflowService.cs");
        var scan = Between(service, "public ScanResult Scan(", "Complete scan route for callers");
        foreach (var boundary in new[] { "\"transaction.abort\");", "\"transaction.dispose\");", "\"scan.publish\");" })
            scan.Should().Contain("ScanDatabaseChangeProbe.SampleDbmod(doc, " + boundary);
        scan.IndexOf("SampleDbmod(doc, \"transaction.dispose\")", StringComparison.Ordinal).Should()
            .BeLessThan(scan.IndexOf("PublishScanEvidence", StringComparison.Ordinal));

        // Codex 01:56: the first-start inventory lock is bounded on both sides in the trace.
        var inventory = Source("Estimate", "EstimateSourceInventoryService.cs");
        var lockAt = inventory.IndexOf("using (document.LockDocument())", StringComparison.Ordinal);
        inventory.IndexOf("EstimateScanTrace.Mark(\"inventory.lock.begin\");", StringComparison.Ordinal).Should().BeGreaterThan(0).And.BeLessThan(lockAt);
        inventory.IndexOf("EstimateScanTrace.Mark(\"inventory.lock.acquired\");", StringComparison.Ordinal).Should().BeGreaterThan(lockAt);
        inventory.IndexOf("EstimateScanTrace.Mark(\"inventory.lock.released\");", StringComparison.Ordinal).Should()
            .BeGreaterThan(inventory.IndexOf("transaction.Abort();", StringComparison.Ordinal));
    }

    private static string Between(string text, string start, string end)
    {
        var begin = text.IndexOf(start, StringComparison.Ordinal);
        begin.Should().BeGreaterThanOrEqualTo(0, start);
        var stop = text.IndexOf(end, begin, StringComparison.Ordinal);
        stop.Should().BeGreaterThan(begin, end);
        return text[begin..stop];
    }
}
