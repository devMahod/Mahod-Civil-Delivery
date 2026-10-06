using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class EstimateViewOnlyFreshnessTests
{
    private static readonly string Hash = new('a', 64);
    private static readonly string OtherHash = new('b', 64);

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void SavedScan_UnchangedHashesAndRevision_AllowsCleanOrViewOnlyReuse(int currentDbMod)
    {
        EstimateSourceSnapshotPolicy.FreshnessFailure(Hash, "fingerprint:7", Hash, "fingerprint:7",
            currentDbMod, scannedDbMod: 0).Should().BeNull();
        EstimateSourceSnapshotPolicy.CanReadSavedDrawingHash(currentDbMod).Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(17)]
    [InlineData(20)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(-1)]
    [InlineData(null)]
    public void OtherOrUnknownDirtyFlags_DoNotBorrowViewOnlyPermission(int? currentDbMod)
    {
        EstimateSourceSnapshotPolicy.FreshnessFailure(Hash, "rev", Hash, "rev",
            currentDbMod, scannedDbMod: 0).Should().NotBeNull();
        EstimateSourceSnapshotPolicy.CanReadSavedDrawingHash(currentDbMod).Should().BeFalse();
        EstimateSourceSnapshotPolicy.IsViewOnlySavedScanRevisionMatch(0, "rev", currentDbMod, "rev")
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(16)]
    public void ViewOnlyReuseRequiresProofThatTheOriginalScanWasSaved(int? scannedDbMod)
    {
        EstimateSourceSnapshotPolicy.FreshnessFailure(Hash, "rev", Hash, "rev", 16,
            scannedDbMod: scannedDbMod).Should().NotBeNull();
        EstimateSourceSnapshotPolicy.IsViewOnlySavedScanRevisionMatch(scannedDbMod, "rev", 16, "rev")
            .Should().BeFalse();
    }

    [Fact]
    public void ViewOnlyFlagNeverWaivesSourceHashOrLiveMutationRevision()
    {
        EstimateSourceSnapshotPolicy.FreshnessFailure(Hash, "rev", OtherHash, "rev", 16, scannedDbMod: 0)
            .Should().Contain("bytes changed");
        EstimateSourceSnapshotPolicy.FreshnessFailure(Hash, "rev", Hash, "changed", 16, scannedDbMod: 0)
            .Should().Contain("database changed");
        EstimateSourceSnapshotPolicy.FreshnessFailure(Hash, "rev", "invalid", "rev", 16, scannedDbMod: 0)
            .Should().NotBeNull();
        EstimateSourceSnapshotPolicy.FreshnessFailure("invalid", "rev", Hash, "rev", 16, scannedDbMod: 0)
            .Should().NotBeNull();
        EstimateSourceSnapshotPolicy.FreshnessFailure(Hash, null, Hash, "rev", 16, scannedDbMod: 0)
            .Should().NotBeNull();
        EstimateSourceSnapshotPolicy.FreshnessFailure(Hash, "rev", Hash, null, 16, scannedDbMod: 0)
            .Should().NotBeNull();
        EstimateSourceSnapshotPolicy.FreshnessFailure(Hash, "rev", Hash, "rev", 16,
            dbModError: "capture was not trustworthy", scannedDbMod: 0).Should().NotBeNull();
    }

    [Fact]
    public void InitialScanAndUnqualifiedCallerRemainStrictDespiteHashReadPermission()
    {
        EstimateSourceSnapshotPolicy.CanReadSavedDrawingHash(16).Should().BeTrue();
        EstimateSourceSnapshotPolicy.InitialFailure(Hash, 16).Should().Contain("unsaved changes");
        var initial = EstimateSourceSnapshotPolicy.ForScan(@"C:\SYNTHETIC-ONLY\host.dwg", Hash, 16);
        initial.IsReady.Should().BeFalse();
        initial.Action.Should().Be(EstimateSourceSnapshotPolicy.ScanSourceAction.SaveAndScan);
        EstimateSourceSnapshotPolicy.FreshnessFailure(Hash, "rev", Hash, "rev", 16)
            .Should().NotBeNull("the end-of-initial-scan call has no saved-scan exception");
    }

    [Fact]
    public void SavePromptHintRequiresExactCapturedRevisionAndDoesNotClaimDiskFreshness()
    {
        EstimateSourceSnapshotPolicy.IsViewOnlySavedScanRevisionMatch(0, "rev", 16, "rev").Should().BeTrue();
        EstimateSourceSnapshotPolicy.IsViewOnlySavedScanRevisionMatch(0, "rev", 16, "changed").Should().BeFalse();
        EstimateSourceSnapshotPolicy.IsViewOnlySavedScanRevisionMatch(0, null, 16, null).Should().BeFalse();
        EstimateSourceSnapshotPolicy.IsViewOnlySavedScanRevisionMatch(0, "rev", 0, "rev").Should().BeFalse();
        // A UI hint cannot authorize an action: a changed disk hash still blocks.
        EstimateSourceSnapshotPolicy.FreshnessFailure(Hash, "rev", OtherHash, "rev", 16, scannedDbMod: 0)
            .Should().NotBeNull();
    }
}
