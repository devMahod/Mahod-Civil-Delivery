using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Wiring and host-free acceptance checks; no native sampling or placement execution is claimed.</summary>
public sealed class SectionVerificationDiagnosticContractTests
{
    private static string Source(string name)
    {
        var root = typeof(SectionVerificationDiagnosticContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(root, "CivilDelivery", "Sections", "Services", name));
    }

    [Fact]
    public void MeasuredLayoutRetainsOriginalSlopeAcceptanceAndAddsFailureDetailOnly()
    {
        var text = Source("SectionAnnotationPlacementContract.cs");
        Assert.Contains("record.PresentationCoverage.ResolvedSpans.Where", text);
        Assert.Contains("matchingSpans.Count != 1", text);
        Assert.Contains("!SectionAnnotationContractLogic.TryVerifySlopeEvidence(surfaces.Design,", text);
        Assert.Contains("matchingSpans[0].FromOffsetM, matchingSpans[0].ToOffsetM, slope, out live)", text);
        Assert.Contains("SlopePosition(view, live.FromOffset, live.ToOffset,", text);
        Assert.Contains("live.FromElevation, live.ToElevation)", text);
        Assert.Contains("throw new InvalidOperationException(\"Measured layout slope is not proven from live FG: \"", text);
        Assert.Contains("DescribeMeasuredSlopeFailure(slope, live, surfaces.Design, error)", text);
    }

    [Theory]
    [InlineData("from", 0.000005, true)]
    [InlineData("from", 0.000005000001, false)]
    [InlineData("to", 0.000005, true)]
    [InlineData("to", 0.000005000001, false)]
    [InlineData("percent", 0.000005, true)]
    [InlineData("percent", 0.000005000001, false)]
    public void SharedSlopeHelperPreservesActualElevationAndPercentTolerance(string field, double delta, bool accepted)
    {
        (double Offset, double Elevation)[] design = [(0, 0), (1, 0)];
        var stored = new SectionAnnotationContractLogic.SlopeAnnotationEvidence(0, 1, 0, 0, 0, "ABC");
        stored = field switch
        {
            "from" => stored with { FromElevation = delta },
            "to" => stored with { ToElevation = delta },
            _ => stored with { Percent = delta },
        };
        Assert.Equal(accepted, SectionAnnotationContractLogic.TryVerifySlopeEvidence(design, 0, 1, stored, out var live));
        Assert.NotNull(live);
        if (!accepted)
        {
            var detail = SectionVerificationRecoveryPolicy.DescribeMeasuredSlopeFailure(stored, live, design, "");
            Assert.Contains("handle=ABC", detail);
            Assert.Contains("stored offsets=", detail);
            Assert.Contains("live offsets=", detail);
            Assert.Contains("delta(stored-live) offsets=", detail);
            Assert.Contains("elevations=", detail);
            Assert.Contains("percent=", detail);
            Assert.Contains("elevation-tolerance=0.000005", detail);
            Assert.Contains("percent-tolerance=0.000005 percentage-points", detail);
        }
    }

    [Fact]
    public void SharedSlopeHelperUsesExactPlanSpan_WithOnlyF6OffsetAllowanceAndUnchangedMinimumWidth()
    {
        (double Offset, double Elevation)[] design = [(-1, 0), (2, 0)];
        var stored = new SectionAnnotationContractLogic.SlopeAnnotationEvidence(0, 1, 0, 0, 0, "ABC");
        Assert.True(SectionAnnotationContractLogic.TryVerifySlopeEvidence(design, 0, 1,
            stored with { FromOffset = 0.0000005 }, out _));
        Assert.False(SectionAnnotationContractLogic.TryVerifySlopeEvidence(design, 0, 1,
            stored with { FromOffset = 0.000000501 }, out _));
        Assert.True(SectionFurnitureLogic.TrySlopeEvidence(design, 0.2, 0.7000001, out var applied));
        Assert.True(SectionAnnotationContractLogic.TryParseSlopeReference(
            SectionAnnotationContractLogic.FormatSlopeReference(applied!, "ABC"), out var persisted, out var error), error);
        Assert.True(SectionAnnotationContractLogic.TryVerifySlopeEvidence(design, 0.2, 0.7000001, persisted, out var live));
        Assert.Equal(0.7000001, live!.ToOffset);
        Assert.False(SectionAnnotationContractLogic.TryVerifySlopeEvidence(design, 0.2, 0.6999999, persisted, out _));
    }

    [Fact]
    public void LiveSurfaceMismatchKeepsSourceIdentityProofAndRemainsFailed()
    {
        var text = Source("SectionVerificationRecoveryService.cs");
        Assert.Contains("{source.SourceName}/{source.SourceHandle}: {mismatch}; {sourceProof}", text);
        Assert.Contains("Pass = mismatch == null", text);
        Assert.Contains("Check = SurfaceComparisonCheck(mismatchKind)", text);
        Assert.Contains("Check = SurfaceSourceProofCheck", text);
        Assert.DoesNotContain("Check = \"live_surface_cut_matches_section\"", text);
        Assert.Contains("SectionVerificationRecoveryService.CheckLiveSources(", Source("SectionVerifyService.cs"));
    }
}
