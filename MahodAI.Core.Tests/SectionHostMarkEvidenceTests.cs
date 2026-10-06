using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Core.Tests;

/// <summary>
/// Live 29/09: after QSAVE and reopen, STA-12145 and STA-42676 planned as
/// "owned-fingerprint-mismatch" because their CURB-EXST marks (drawn in the host
/// drawing) carried the host file hash, which every save changes.
/// </summary>
public class SectionHostMarkEvidenceTests
{
    private const string HashBeforeSave = "24a0817067c632d77543d82afee6943748d0df64194e544c0eda73b699a5f066";
    private const string HashAfterSave = "5ce19964fbf72d686a2f810fadf9b99ba71c259faba3ef33bcbabe431d14a631";
    private static readonly ProjectionRuleMatch Curb = new("curb", "אבן שפה", 7);

    private static Crossing HostCurb(string hostHash, double wcsX = 215000.25) =>
        new(-8.953, null, "CURB-EXST", null, Curb, "11A0F", wcsX, 655000.5)
        {
            SourceDrawingPath = @"C:\work\6422-CIVIL-WEST.dwg",
            SourceDrawingHash = SourceHashForEvidence(null, hostHash),
        };

    [Fact]
    public void AHostMark_KeepsItsIdentity_WhenOnlyTheHostFileWasSaved()
    {
        var before = DimensionPresentationMark(HostCurb(HashBeforeSave));
        var after = DimensionPresentationMark(HostCurb(HashAfterSave));
        Assert.Equal(before.SourceIdentity, after.SourceIdentity);
        Assert.Null(after.SourceEvidence!.SourceDrawingHash);
    }

    [Fact]
    public void AMovedHostMark_StillChangesItsIdentity()
    {
        var before = DimensionPresentationMark(HostCurb(HashBeforeSave));
        var moved = DimensionPresentationMark(HostCurb(HashBeforeSave, wcsX: 215000.30));
        Assert.NotEqual(before.SourceIdentity, moved.SourceIdentity);
    }

    [Fact]
    public void AnXrefMark_StaysBoundToItsExternalFileBytes()
    {
        Assert.Equal(HashAfterSave, SourceHashForEvidence("6422-GM-MODEL-NATAZ", HashAfterSave));
        Assert.Null(SourceHashForEvidence(null, HashAfterSave));
        Assert.Null(SourceHashForEvidence(" ", HashAfterSave));
    }
}
