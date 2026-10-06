using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

/// <summary>
/// MHD_SMOKE_SECTIONS applies real sections: it may run only on a saved copy inside
/// the fixture folder or with an override naming the exact file (final sweep, 1.2.30).
/// </summary>
public sealed class SmokeApplyGuardTests
{
    private const string Root = @"C:\Users\nataly\MahodCivilDelivery_Fixtures";

    [Fact]
    public void FixtureCopy_IsAllowed()
    {
        SmokeApplyGuard.Refusal(Root + @"\HW-CIVIL-CL.dwg", Root, null).Should().BeNull();
        SmokeApplyGuard.Refusal(Root + @"\sub\6422-HW-CS.dwg", Root, null).Should().BeNull();
        SmokeApplyGuard.Refusal(Root.ToLowerInvariant() + @"\HW-CIVIL-CL.DWG", Root, null).Should().BeNull("Windows paths are case-insensitive");
    }

    [Fact]
    public void ProjectDrawing_IsRefused_WithTheRemedy()
    {
        var refusal = SmokeApplyGuard.Refusal(@"P:\data\6422\HW-CIVIL-CL.dwg", Root, null);
        refusal.Should().NotBeNull().And.Contain("HW-CIVIL-CL.dwg").And.Contain(Root)
            .And.Contain("make-fixture.ps1").And.Contain(SmokeApplyGuard.OverrideVariable);
    }

    [Fact]
    public void UnsavedDrawing_IsRefused()
    {
        SmokeApplyGuard.Refusal(null, Root, null).Should().Contain("not a saved file");
        SmokeApplyGuard.Refusal("   ", Root, null).Should().Contain("not a saved file");
    }

    [Fact]
    public void FolderNameLookalikes_AreRefused()
    {
        // A sibling folder whose name merely starts with the fixture folder name.
        SmokeApplyGuard.Refusal(Root + @"_old\HW-CIVIL-CL.dwg", Root, null).Should().NotBeNull();
        // Traversal out of the folder is resolved before the check.
        SmokeApplyGuard.Refusal(Root + @"\..\6422\HW-CIVIL-CL.dwg", Root, null).Should().NotBeNull();
    }

    [Fact]
    public void Override_MustNameTheExactFile()
    {
        SmokeApplyGuard.Refusal(@"D:\work\copy.dwg", Root, "copy.dwg").Should().BeNull();
        SmokeApplyGuard.Refusal(@"D:\work\copy.dwg", Root, "COPY.DWG").Should().BeNull();
        SmokeApplyGuard.Refusal(@"D:\work\copy.dwg", Root, "1").Should().NotBeNull("a bare flag is not a file name");
        SmokeApplyGuard.Refusal(@"D:\work\copy.dwg", Root, "other.dwg").Should().NotBeNull();
        SmokeApplyGuard.Refusal(@"D:\work\copy.dwg", Root, @"D:\work\copy.dwg").Should().NotBeNull("the override is the file name only");
    }

    [Fact]
    public void MissingFixtureRoot_RefusesEverythingWithoutOverride()
    {
        SmokeApplyGuard.Refusal(Root + @"\HW-CIVIL-CL.dwg", null, null).Should().NotBeNull();
        SmokeApplyGuard.Refusal(Root + @"\HW-CIVIL-CL.dwg", null, "HW-CIVIL-CL.dwg").Should().BeNull();
    }
}
