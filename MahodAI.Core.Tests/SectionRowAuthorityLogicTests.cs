using System;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class SectionRowAuthorityLogicTests
{
    private const string GmHash =
        "46E79D5725145026D09443CE48C599BE994F21119FD33A37BEF44A31A262FF51";
    private const string PhotoHash =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void TwoUnapprovedRowSources_AreSuppressedNotMixed()
    {
        var selection = SectionRowAuthorityLogic.Select(
            new[]
            {
                new SectionRowAuthorityLogic.Source("C:/6422-GM.dwg", GmHash, "GM"),
                new SectionRowAuthorityLogic.Source("C:/PHOTO.dwg", PhotoHash, "PHOTO"),
            },
            Array.Empty<SectionRowAuthorityLogic.Authority>());

        selection.State.Should().Be(SectionRowAuthorityLogic.SelectionState.Suppressed);
        selection.AuthoritativeSourceKey.Should().BeNull();
        selection.CandidateSourceKeys.Should().HaveCount(2);
    }

    [Fact]
    public void ExactApprovedHash_SelectsOnlyThatSource()
    {
        var gm = new SectionRowAuthorityLogic.Source("C:/6422-GM.dwg", GmHash, "GM");
        var photo = new SectionRowAuthorityLogic.Source("C:/PHOTO.dwg", PhotoHash, "PHOTO");
        var selection = SectionRowAuthorityLogic.Select(
            new[] { gm, photo },
            new[]
            {
                new SectionRowAuthorityLogic.Authority(
                    GmHash, "*6422-GM.DWG", "GM", "nataly", DateTime.UtcNow),
            });

        selection.State.Should().Be(SectionRowAuthorityLogic.SelectionState.Authoritative);
        selection.Accepts(gm).Should().BeTrue();
        selection.Accepts(photo).Should().BeFalse();
    }

    [Fact]
    public void SameApprovedBytesInTwoXrefChains_AreAmbiguousWithoutNarrowing()
    {
        var selection = SectionRowAuthorityLogic.Select(
            new[]
            {
                new SectionRowAuthorityLogic.Source("C:/6422-GM.dwg", GmHash, "GM-A"),
                new SectionRowAuthorityLogic.Source("C:/6422-GM.dwg", GmHash, "GM-B"),
            },
            new[]
            {
                new SectionRowAuthorityLogic.Authority(
                    GmHash, null, null, "nataly", DateTime.UtcNow),
            });

        selection.State.Should().Be(SectionRowAuthorityLogic.SelectionState.Ambiguous);
        selection.MatchingSourceKeys.Should().HaveCount(2);
    }

    [Fact]
    public void ExactXrefNarrowing_SelectsOneInsertion()
    {
        var selection = SectionRowAuthorityLogic.Select(
            new[]
            {
                new SectionRowAuthorityLogic.Source("C:/6422-GM.dwg", GmHash, "PARENT > GM-A"),
                new SectionRowAuthorityLogic.Source("C:/6422-GM.dwg", GmHash, "PARENT > GM-B"),
            },
            new[]
            {
                new SectionRowAuthorityLogic.Authority(
                    GmHash, "*6422-GM.DWG", "GM-A", "nataly", DateTime.UtcNow),
            });

        selection.State.Should().Be(SectionRowAuthorityLogic.SelectionState.Authoritative);
        selection.AuthoritativeSourceKey.Should().EndWith("|PARENT > GM-A");
    }

    [Fact]
    public void HashWithoutApprovalSignature_IsNeverAuthority()
    {
        SectionRowAuthorityLogic.IsCompleteAuthority(
                new SectionRowAuthorityLogic.Authority(GmHash, null, null, null, null))
            .Should().BeFalse();
    }
}
