using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class EstimateProfileDecisionScopeTests
{
    private static readonly string Hash = new('a', 64);

    private static EstimateWorkflowService.ScanResult Scan(int? sourceDbMod = 0) => new()
    {
        RunId = "SYNTHETIC-ONLY-original-scan", ProjectProfileId = "SYNTHETIC-ONLY",
        ProfileSource = "C:/SYNTHETIC-ONLY/profile.yaml",
        SourceDrawing = "C:/SYNTHETIC-ONLY/host.dwg", SourceDrawingHash = Hash,
        SourceDbMod = sourceDbMod, DatabaseRevision = "fingerprint:7",
    };

    private static void RequireSnapshot(EstimateWorkflowService.ScanResult scan, int? currentDbMod,
        string? currentHash = null, string? currentRevision = null)
    {
        var failure = EstimateSourceSnapshotPolicy.FreshnessFailure(scan.SourceDrawingHash,
            scan.DatabaseRevision, currentHash ?? Hash, currentRevision ?? "fingerprint:7",
            currentDbMod, scannedDbMod: scan.SourceDbMod);
        if (failure != null) throw new InvalidOperationException(failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void OriginalSavedScan_CanOpenAndRecheckAfterViewOnlyNavigation(int currentDbMod)
    {
        var original = Scan();
        var checks = 0;
        void Fresh(EstimateWorkflowService.ScanResult value)
        {
            value.Should().BeSameAs(original);
            RequireSnapshot(value, currentDbMod);
            checks++;
        }

        EstimateProfileDecisionScanGuard.RequireOriginalFreshScan(original, () => original, Fresh);
        EstimateProfileDecisionScanGuard.RequireOriginalFreshScan(original, () => original, Fresh);

        checks.Should().Be(2, "rechecking is not replaced by the opening result");
        original.SourceDbMod.Should().Be(0, "view-only reuse must not rewrite original evidence");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(null)]
    public void DirtyOrUnknownCurrentDrawing_StillRejectsTheDecision(int? currentDbMod)
    {
        var original = Scan();
        var action = () => EstimateProfileDecisionScanGuard.RequireOriginalFreshScan(original,
            () => original, value => RequireSnapshot(value, currentDbMod));
        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(16)]
    public void ViewOnlyReuse_RequiresOriginalSavedScanEvidence(int? scannedDbMod)
    {
        var original = Scan(scannedDbMod);
        var action = () => EstimateProfileDecisionScanGuard.RequireOriginalFreshScan(original,
            () => original, value => RequireSnapshot(value, 16));
        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("revision")]
    public void ChangeAfterOpening_RejectsTheSaveRecheck(string changed)
    {
        var original = Scan();
        EstimateProfileDecisionScanGuard.RequireOriginalFreshScan(original, () => original,
            value => RequireSnapshot(value, 16));
        var saveCalled = false;
        var action = () =>
        {
            EstimateProfileDecisionScanGuard.RequireOriginalFreshScan(original, () => original,
                value => RequireSnapshot(value, 16,
                    changed == "hash" ? new string('b', 64) : Hash,
                    changed == "revision" ? "fingerprint:8" : "fingerprint:7"));
            saveCalled = true;
        };
        action.Should().Throw<InvalidOperationException>();
        saveCalled.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacementScanWithIdenticalValues_IsRejectedBeforeOrDuringFreshness(bool duringCheck)
    {
        var original = Scan();
        var replacement = Scan();
        var current = duringCheck ? original : replacement;
        var checks = 0;
        var action = () => EstimateProfileDecisionScanGuard.RequireOriginalFreshScan(original, () => current,
            value => { RequireSnapshot(value, 16); checks++; current = replacement; });
        action.Should().Throw<InvalidOperationException>();
        checks.Should().Be(duringCheck ? 1 : 0);
    }

    [Theory]
    [InlineData("external source hash changed")]
    [InlineData("profile CAS changed")]
    [InlineData("published scan evidence changed")]
    public void FullFreshnessFailuresArePropagated_NotWaivedByTheViewOnlyPath(string reason)
    {
        var original = Scan();
        var action = () => EstimateProfileDecisionScanGuard.RequireOriginalFreshScan(original, () => original,
            _ => throw new InvalidOperationException(reason));
        action.Should().Throw<InvalidOperationException>().WithMessage(reason);
    }

    [Fact]
    public void OpeningThenCancelling_IsReadOnlyAndDoesNotRequireAFabricatedCommit()
    {
        var original = Scan();
        var originalRecords = original.Records;
        var originalSources = original.ExternalSources;
        EstimateProfileDecisionScanGuard.RequireOriginalFreshScan(original, () => original,
            value => RequireSnapshot(value, 16));
        // The modal cancel routes return without calling their SaveChoices/price writer.
        original.Records.Should().BeSameAs(originalRecords).And.BeEmpty();
        original.ExternalSources.Should().BeSameAs(originalSources).And.BeEmpty();
        original.SourceDbMod.Should().Be(0);
        original.SourceDrawingHash.Should().Be(Hash);
        original.DatabaseRevision.Should().Be("fingerprint:7");
    }

    [Fact]
    public void FreshnessValidationCannotBeOmitted()
    {
        var original = Scan();
        var action = () => EstimateProfileDecisionScanGuard.RequireOriginalFreshScan(original, () => original, null!);
        action.Should().Throw<ArgumentNullException>();
    }

    private static string Ui(string name)
    {
        var directory = typeof(EstimateProfileDecisionScopeTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().Single(value => value.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(directory, "CivilDelivery", "UI", name)).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CivilDeliveryControl.MappingReview.cs", "בדיקת שיוכים מרוכזת")]
    [InlineData("CivilDeliveryControl.ProjectPrice.cs", "אישור מחיר פרויקט")]
    public void EstimateEditorsCaptureExactScanBeforeModal_AndPreserveFreshnessAndOriginalCas(string file, string operation)
    {
        var source = Ui(file);
        source.Should().Contain($"var scan = _scan;\n            var scope = CaptureProfileDecisionScope(\"{operation}\", scan);")
            .And.Contain("RequireProfileDecisionScope(scope);")
            .And.Contain("EstimateWorkflowService.RequireFreshForDecision(")
            .And.Contain("scan.ProfileWriteState ??")
            .And.Contain("ReferenceEquals(_scan, scan)")
            .And.Contain("ReferenceEquals(_catalog, catalog)");
    }

    [Fact]
    public void EstimateScopeUsesFullServiceFreshness_WhileInitialScopeStaysStrict()
    {
        var source = Ui("CivilDeliveryControl.ProfileDecisionScope.cs");
        source.Should().Contain("CaptureReadySource(document, operation)")
            .And.Contain("CaptureReadySource(scope.Document, scope.Operation)")
            .And.Contain("EstimateProfileDecisionScanGuard.RequireOriginalFreshScan(scan, () => _scan,")
            .And.Contain("original => EstimateWorkflowService.RequireFreshForDecision(document, original, operation)")
            .And.Contain("scope.EstimateScan is { } scan")
            .And.Contain("RequireUnchanged(scope.Identity, current)")
            .And.Contain("RequireExpectedStateUnchanged(scope.Profile, scope.ExpectedState)")
            .And.NotContain("SetSystemVariable(")
            .And.NotContain("EnsureSavedForAction(");
    }

    [Fact]
    public void EstimateScopeDoesNotReenterTheStrictInitialGuardThroughProfileCasCapture()
    {
        var source = Ui("CivilDeliveryControl.ProfileDecisionScope.cs");
        var start = source.IndexOf("string operation, EstimateWorkflowService.ScanResult scan)", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = source.IndexOf("private void RequireProfileDecisionScope(", start, StringComparison.Ordinal);
        var estimateCapture = source[start..end];
        estimateCapture.Should().Contain("scan.ProfileWriteState ??")
            .And.Contain("RequireExpectedStateUnchanged(profile, expectedState)")
            .And.NotContain("CaptureExpectedProfileState(")
            .And.NotContain("CaptureReadySource(");
    }

    [Fact]
    public void CancelRoutesDoNotPublishProfileDecisions()
    {
        var price = Ui("CivilDeliveryControl.ProjectPrice.cs");
        price.Should().Contain("if (CivilModalHost.ShowFromPalette(dialog) != true || dialog.Approval == null) return;");
        var reviewDialog = Ui("ManualMappingReviewDialog.cs");
        reviewDialog.Should().Contain("cancel.Click += (_, _) => Close();");
    }
}
