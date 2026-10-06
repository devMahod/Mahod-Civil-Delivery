using System;
using System.IO;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>b24 (Codex 12:24 C): where the palette records and closes a unit review. Source contract only.</summary>
public sealed class UnitReviewWiringSourceContractTests
{
    private static string Ui(string file) => File.ReadAllText(Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery", "UI", file));

    [Fact]
    public void AReviewIsRecordedBeforeTheScanStartsAndAFailedRecordStartsNoScan()
    {
        var control = Ui("CivilDeliveryControl.xaml.cs");
        var ensure = control.IndexOf("if (!EnsureEstimateProjectForScan(doc)) return;", StringComparison.Ordinal);
        var record = control.IndexOf("if (!EnsureUnitReviewRecorded(doc)) return;", ensure, StringComparison.Ordinal);
        var scan = control.IndexOf("RunEstimateScan(doc);", record, StringComparison.Ordinal);
        Assert.True(ensure > 0 && record > ensure && scan > record, $"{ensure} < {record} < {scan}");
    }

    [Fact]
    public void TheFirstObservationIsRecordedAndEarlierResultsAreInvalidatedBeforeTheWrite()
    {
        // Codex 13:02 §3: no second observation can cancel the first; stale results are invalidated before writing.
        var units = Ui("CivilDeliveryControl.DrawingUnits.cs");
        var method = units[units.IndexOf("private bool EnsureUnitReviewRecorded(", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("private void ShowUnitsLine(", StringComparison.Ordinal)];
        Assert.Equal(1, method.Split("NewReviewRecord(").Length - 1);
        var invalidate = method.IndexOf("InvalidateEstimateEvidence(", StringComparison.Ordinal);
        var scope = method.IndexOf("CaptureProfileDecisionScope(", StringComparison.Ordinal);
        var write = method.IndexOf("SaveTechnicalRecord(", StringComparison.Ordinal);
        Assert.True(invalidate > 0 && invalidate < scope && scope < write, $"{invalidate} < {scope} < {write}");
        Assert.Contains("PhysicalDrawingUnitPolicy.IsReviewOf(record, db.FingerprintGuid, scope.Identity.DrawingPath)", method);
        // b25 (Codex 15:12): the verified profile counts as "already recorded" by the same rule as the observation — an
        // open record, or any record unless this is a new conflict — so a later Civil conflict is written, not skipped.
        Assert.Contains("PhysicalDrawingUnitPolicy.IsRecorded(quick, db.FingerprintGuid, scope.Identity.DrawingPath,", method);
        Assert.DoesNotContain("DrawingUnitReviews?.Any(", method);
    }

    [Theory]
    [InlineData("private void OnPriceBookChanged(", "_estimate.SetActivePriceBook(", null)]
    [InlineData("private void OnLoadPriceBook(", "_estimate.RegisterPriceBook(", "new Microsoft.Win32.OpenFileDialog")]
    public void ThePriceBookWindowsWriteOnlyToTheContextCapturedBeforeThem(string start, string write, string? firstWindow)
    {
        // Codex 13:02 §4: scope captured before the first window, checked after the last, and used for target and CAS.
        var control = Ui("CivilDeliveryControl.xaml.cs");
        var method = control[control.IndexOf(start, StringComparison.Ordinal)..];
        method = method[..method.IndexOf("\n        private ", 10, StringComparison.Ordinal)];
        var capture = method.IndexOf("CaptureProfileDecisionScope(", StringComparison.Ordinal);
        var approver = method.IndexOf("RequireApprover(", StringComparison.Ordinal);
        var check = method.IndexOf("RequireProfileDecisionScope(scope);", StringComparison.Ordinal);
        var save = method.IndexOf(write, StringComparison.Ordinal);
        Assert.True(capture > 0 && capture < approver && approver < check && check < save, $"{capture} < {approver} < {check} < {save}");
        if (firstWindow != null) Assert.True(capture < method.IndexOf(firstWindow, StringComparison.Ordinal));
        Assert.DoesNotContain("RequireProfileWriteTarget()", method);
        Assert.DoesNotContain("CaptureExpectedProfileState()", method);
        Assert.Contains("scope.ExpectedState", method);
    }

    [Theory]
    [InlineData("EstimateWorkflowService.EngineerDraft.cs", "EngineerBoqDraftBuilder.Build(")]
    [InlineData("EstimateWorkflowService.Recognition.cs", "EngineerBoqDraftBuilder.Build(")]
    [InlineData("EstimateWorkflowService.LibraryGate.cs", "EngineerBoqDraftBuilder.Build(")]
    public void EveryProductBuildFromScanRecordsChecksTheUnitEvidenceFirst(string file, string build)
    {
        // Codex 13:21: drawn widths change the draft quantity, so the draft, recognition and library builds share the guard.
        var source = File.ReadAllText(Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery", "Estimate", file));
        var guard = source.IndexOf("RequireTrustedUnitEvidence(scan,", StringComparison.Ordinal);
        Assert.True(guard > 0 && guard < source.IndexOf(build, StringComparison.Ordinal), file);
    }

    [Fact]
    public void TheRecordIsATechnicalWriteAndADecisionClosesItInTheSameWrite()
    {
        var units = Ui("CivilDeliveryControl.DrawingUnits.cs");
        var method = units[units.IndexOf("private bool EnsureUnitReviewRecorded(", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("private void ShowUnitsLine(", StringComparison.Ordinal)];
        Assert.Contains("ProjectProfileWriter.SaveTechnicalRecord(", method);
        Assert.DoesNotContain("ProjectProfileWriter.Save(", method);
        Assert.Contains("if (!scope.ExpectedState.SourceExisted)", method);   // never creates a profile
        Assert.Contains("return false;", method);                             // a failed record starts no scan
        // b25 (Codex 16:10 P2-1): the palette commits through PrepareCommit — the live Civil evidence is read again after
        // the dialog and the cleanup — and saves only what it returns; the decision closes the reviews in that clone.
        var onUnits = units[units.IndexOf("private void OnDrawingUnits(", StringComparison.Ordinal)..];
        var commit = onUnits.IndexOf("DrawingUnitDeclarationReview.PrepareCommit(", StringComparison.Ordinal);
        Assert.True(commit > 0 && commit < onUnits.IndexOf("ProjectProfileWriter.Save(", StringComparison.Ordinal));
        Assert.Contains("() => HostDrawingUnitService.CivilUnits(db),", onUnits);
        Assert.DoesNotContain("CloneProfileForDecision(scope.Profile);", onUnits[..onUnits.IndexOf("ProjectProfileWriter.Save(", StringComparison.Ordinal)]);
        var prepare = units[units.IndexOf("internal static ProjectProfile? PrepareCommit(", StringComparison.Ordinal)..];
        prepare = prepare[..prepare.IndexOf("internal static void ApplyToClone(", StringComparison.Ordinal)];
        var reread = prepare.IndexOf("var now = readCivil();", StringComparison.Ordinal);
        var apply = prepare.IndexOf("ApplyToClone(updated, decision!);", StringComparison.Ordinal);
        Assert.True(reread > prepare.IndexOf("if (!clearPreview()) return null;", StringComparison.Ordinal) && apply > reread);
        Assert.True(prepare.IndexOf("PhysicalDrawingUnitPolicy.LinkReviews(updated, decision!, nowUtc);", StringComparison.Ordinal) > apply);
        // Revocation removes the declaration only; the review record stays.
        var remove = units[units.IndexOf("internal static void RemoveFromClone(", StringComparison.Ordinal)..];
        Assert.DoesNotContain("DrawingUnitReviews", remove[..remove.IndexOf("\n    }", StringComparison.Ordinal)]);
    }
    [Fact]
    public void TheCivilUnitsReaderDecidesByTheApiValueNotByAnEnumName()
    {
        // b26 (LA-40 live): Autodesk.Civil.Settings.DrawingUnitType names only Feet and Meters in 2027; inches read
        // through ToString() became "unreadable". The value is read and passed with its name.
        var service = File.ReadAllText(Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery", "Estimate", "HostDrawingUnitService.cs"));
        Assert.Contains("PhysicalDrawingUnitPolicy.CivilUnitsFromApi((int)units, units.ToString())", service);
        Assert.DoesNotContain("CivilUnitsFromName(", service);
    }
}
