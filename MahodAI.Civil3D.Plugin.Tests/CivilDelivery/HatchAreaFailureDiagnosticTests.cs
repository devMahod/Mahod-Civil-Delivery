using System;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class HatchAreaFailureDiagnosticTests
{
    [Fact]
    public void ReferenceVectorCapture_ReadsOnceAndRetainsActualG17Components()
    {
        var calls = 0;
        var value = HatchAreaFailureDiagnostic.CaptureReferenceVector(() =>
        { calls++; return (0.12345678901234567, -1.0); });
        calls.Should().Be(1);
        value.Should().Be("; reference_vector=0.12345678901234566,-1");
    }

    [Fact]
    public void ReferenceVectorFailure_IsExplicitAndNeverInventsAnAxis()
    {
        var value = HatchAreaFailureDiagnostic.CaptureReferenceVector(() =>
            throw new InvalidOperationException("eNotApplicable"));
        value.Should().Be("; reference_vector_error=InvalidOperationException");
        value.Should().NotContain("reference_vector=");
    }

    [Fact]
    public void NonFiniteReferenceVector_IsRetainedForStrictReaderRefusal()
    {
        HatchAreaFailureDiagnostic.CaptureReferenceVector(() => (double.NaN, double.PositiveInfinity))
            .Should().Be("; reference_vector=NaN,Infinity");
    }

    [Fact]
    public void ClosedReadableBoundaryIsOnlyRawDiagnostic_NotAnAcceptedArea()
    {
        // Synthetic boundary, not the real HA/7204 native capture.
        var result = HatchAreaFailureDiagnostic.Capture(() => "style=Normal", () => 1,
            (_, _) => new("edge-list", "External", new[] { "line=0,0 -> 1,0", "line=1,0 -> 0,0" }, false));
        result.Purpose.Should().Contain("original Area API failure evidence")
            .And.Contain("boundary capture alone grants no quantity")
            .And.Contain("strict recovery outcome is recorded in quantity evidence");
        result.CoordinateSystem.Should().Contain("source drawing units").And.Contain("not host WCS or square metres");
        result.Loops.Single().Evidence!.Items.Should().HaveCount(2);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public void UnreadableLoopIsRetainedAndDoesNotHideOtherLoops()
    {
        var result = HatchAreaFailureDiagnostic.Capture(() => "header", () => 3,
            (i, _) => i == 1 ? throw new InvalidOperationException("eNotApplicable") :
                new("polyline", "Default", new[] { "vertex=" + i }, false));
        result.Loops.Should().HaveCount(3);
        result.Loops[1].Error.Should().Contain("eNotApplicable");
        result.Loops[1].Evidence.Should().BeNull();
        result.Loops[2].Evidence!.Items.Should().ContainSingle().Which.Should().Be("vertex=2");
    }

    [Fact]
    public void HeaderAndLoopCountFailuresRemainExplicit_NotAnEmptySource()
    {
        var result = HatchAreaFailureDiagnostic.Capture(
            () => throw new InvalidOperationException("header-read"),
            () => throw new InvalidOperationException("count-read"),
            (_, _) => throw new Xunit.Sdk.XunitException("Must not read loops"));
        result.DeclaredLoops.Should().BeNull();
        result.Error.Should().Contain("header-read").And.Contain("count-read");
        result.Loops.Should().BeEmpty();
    }

    [Fact]
    public void ExcessLoopsAreBoundedAndMarkedIncomplete()
    {
        var calls = 0;
        var result = HatchAreaFailureDiagnostic.Capture(() => "header", () => 10000,
            (_, _) => { calls++; return new("empty", "Default", Array.Empty<string>(), false); });
        calls.Should().Be(HatchAreaFailureDiagnostic.MaximumLoops);
        result.DeclaredLoops.Should().Be(10000);
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public void TotalItemsAreBoundedEvenWhenAdapterIgnoresBudget()
    {
        var calls = 0;
        var result = HatchAreaFailureDiagnostic.Capture(() => "header", () => 3,
            (_, budget) => { calls++; return new("polyline", "Default",
                Enumerable.Range(0, budget + 10).Select(i => "vertex=" + i).ToArray(), false); });
        calls.Should().Be(1);
        result.Loops.Single().Evidence!.Items.Should().HaveCount(HatchAreaFailureDiagnostic.MaximumItems);
        result.Loops.Single().Evidence!.Truncated.Should().BeTrue();
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public void NegativeLoopCountIsAnExplicitReadError()
    {
        var result = HatchAreaFailureDiagnostic.Capture(() => "header", () => -1,
            (_, _) => throw new Xunit.Sdk.XunitException("Must not read loops"));
        result.Error.Should().Contain("negative loop count");
        result.Purpose.Should().Contain("boundary capture alone grants no quantity");
    }

    [Fact]
    public void RunBatchCapsNativeReadsAndCountsOmissionsWithoutLosingSourceIdentity()
    {
        var batch = new HatchAreaFailureDiagnostic.Batch();
        var nativeReads = 0;
        for (var i = 0; i < HatchAreaFailureDiagnostic.MaximumHatches + 9; i++)
        {
            var source = new ProvenanceRef { SourceKind = "xref", SourcePathOrUri = "C:/TEST/source.dwg",
                SourceHandle = "INSERT/" + i.ToString("X"), DrawingChecksum = new string('a', 64) };
            batch.Add(source, () =>
            {
                nativeReads++;
                return HatchAreaFailureDiagnostic.Capture(() => "header", () => 0,
                    (_, _) => throw new Xunit.Sdk.XunitException("No loops"));
            });
        }
        nativeReads.Should().Be(HatchAreaFailureDiagnostic.MaximumHatches);
        batch.Sources.Should().HaveCount(HatchAreaFailureDiagnostic.MaximumHatches);
        batch.OmittedByDiagnosticLimit.Should().Be(9);
        batch.Sources[0].Source.SourceHandle.Should().Be("INSERT/0");
        batch.Purpose.Should().Contain("failed quantities remain failed");
    }

    [Fact]
    public void RawDiagnosticIsNotEmbeddedInScanOrGlobalFindings()
    {
        var scan = new EstimateWorkflowService.ScanResult
        {
            RunId = "test-only", ProjectProfileId = "test-only", ProfileSource = "test-only",
            SourceDrawing = "C:/TEST/host.dwg", HatchDiagnostics = new(),
        };
        scan.HatchDiagnostics.Add(new ProvenanceRef { SourceKind = "xref", SourceHandle = "RAW-ONLY" },
            () => HatchAreaFailureDiagnostic.Capture(() => "raw-secret-diagnostic-marker", () => 0,
                (_, _) => throw new Xunit.Sdk.XunitException("No loops")));
        var scanJson = System.Text.Json.JsonSerializer.Serialize(scan);
        scanJson.Should().NotContain("RAW-ONLY").And.NotContain("raw-secret-diagnostic-marker");
        var diagnosticJson = System.Text.Json.JsonSerializer.Serialize(scan.HatchDiagnostics);
        diagnosticJson.Should().Contain("RAW-ONLY").And.Contain("raw-secret-diagnostic-marker");
        scan.Findings.Should().BeEmpty();
        scan.Records.Should().BeEmpty();
    }
}
