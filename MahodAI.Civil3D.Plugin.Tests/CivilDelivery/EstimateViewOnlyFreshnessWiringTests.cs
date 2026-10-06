using System;
using System.IO;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

// Source wiring plus host-free identity tests supplement the required native
// scan -> locate/zoom -> export test. They do not claim that Civil was exercised.
public sealed class EstimateViewOnlyFreshnessWiringTests
{
    private static string Read(string folder, string name) => File.ReadAllText(Path.Combine(
        EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", "CivilDelivery", folder, name));

    [Fact]
    public void NativeCaptureHashesViewOnlyStateWithoutClearingDirtyFlagsOrIgnoringMutations()
    {
        var source = Read("Estimate", "DrawingRevisionTracker.cs");
        source.Should().Contain("EstimateSourceSnapshotPolicy.CanReadSavedDrawingHash(dbMod)")
            .And.Contain("db.ObjectAppended += OnObjectChanged")
            .And.Contain("db.ObjectModified += OnObjectChanged")
            .And.Contain("db.ObjectErased += OnObjectErased")
            .And.Contain("!string.Equals(before, after, StringComparison.Ordinal)")
            .And.Contain("return new LiveSnapshot(path, hash, after, dbMod, failure)")
            .And.NotContain("SetSystemVariable(\"DBMOD\"");
    }

    [Fact]
    public void OnlyPublishedScanFreshnessSuppliesSavedBaseline_AndRetainsEveryIndependentGate()
    {
        var source = Read("Estimate", "EstimateWorkflowService.cs");
        var start = source.IndexOf("internal static string? FreshnessReason(", StringComparison.Ordinal);
        var end = source.IndexOf("private static void RequireFresh(", start, StringComparison.Ordinal);
        var freshness = source[start..end];
        freshness.Should().Contain("source.Failure ?? EstimateSourceSnapshotPolicy.FreshnessFailure")
            .And.Contain("scannedDbMod: scan.SourceDbMod")
            .And.Contain("ExternalSourcesFreshnessReason(scan.ExternalSources)")
            .And.Contain("ProjectProfileWriter.RequireExpectedFilesUnchanged(expectedState)")
            .And.Contain("scan.ProjectProfileEffectiveHash")
            .And.Contain("return scan.StaleReason(");
        source[..start].Should().NotContain("scannedDbMod:", "a new scan still ends with the strict saved-drawing gate");
        var export = Read("Estimate", "EstimateWorkflowService.MeasurementDraft.cs");
        export.Should().Contain("() => RequireFresh(doc, scan, \"ייצוא מדידות לבדיקה\")");
        var beforeWrite = export.IndexOf("requireFresh();", StringComparison.Ordinal);
        var write = export.IndexOf("var written = write(before);", beforeWrite, StringComparison.Ordinal);
        var afterWrite = export.IndexOf("requireFresh();", write, StringComparison.Ordinal);
        afterWrite.Should().BeGreaterThan(write);
        export[afterWrite..].Should().Contain("SameProof(before.Scan, after.Scan)");
    }

    [Fact]
    public void PaletteSuppressesOnlySavedScanViewPromptWithoutPretendingToHashSourceFiles()
    {
        var ui = Read("UI", "CivilDeliveryControl.xaml.cs");
        var start = ui.IndexOf("private static bool EstimateSaveMayBeRequired(", StringComparison.Ordinal);
        var end = ui.IndexOf("private string UnmappedPotentialText(", start, StringComparison.Ordinal);
        ui[start..end].Should().Contain("includeFileHash: false")
            .And.Contain("probe.Failure == null && scan != null")
            .And.Contain("EstimateSourceSnapshotPolicy.IsViewOnlySavedScanRevisionMatch(")
            .And.Contain("scan.SourceDbMod, scan.DatabaseRevision, probe.DbMod, probe.DatabaseRevision")
            .And.Contain("scan.StaleReason(probe.DrawingPath, profileId, profileHash,")
            .And.Contain("!File.Exists(probe.DrawingPath)");
    }

    [Fact]
    public void SavedScanIdentityStillRejectsDrawingProfileAndRevisionSwitchAfterViewOnlyFlag()
    {
        var hash = new string('a', 64);
        var scan = new EstimateWorkflowService.ScanResult
        {
            RunId = "SYNTHETIC-ONLY", ProjectProfileId = "SYNTHETIC-ONLY", ProfileSource = @"C:\SYNTHETIC-ONLY\profile.yaml",
            SourceDrawing = @"C:\SYNTHETIC-ONLY\host.dwg", ProjectProfileHash = hash,
            SourceDrawingHash = hash, SourceDbMod = 0, DatabaseRevision = "fingerprint:7",
        };
        EstimateSourceSnapshotPolicy.IsViewOnlySavedScanRevisionMatch(scan.SourceDbMod, scan.DatabaseRevision,
            16, "fingerprint:7").Should().BeTrue();
        scan.StaleReason(scan.SourceDrawing, scan.ProjectProfileId, hash, scan.DatabaseRevision).Should().BeNull();
        scan.StaleReason(@"C:\SYNTHETIC-ONLY\other.dwg", scan.ProjectProfileId, hash, scan.DatabaseRevision).Should().NotBeNull();
        scan.StaleReason(scan.SourceDrawing, "OTHER-PROFILE", hash, scan.DatabaseRevision).Should().NotBeNull();
        scan.StaleReason(scan.SourceDrawing, scan.ProjectProfileId, new string('b', 64), scan.DatabaseRevision).Should().NotBeNull();
        scan.StaleReason(scan.SourceDrawing, scan.ProjectProfileId, hash, "fingerprint:8").Should().NotBeNull();
    }
}
