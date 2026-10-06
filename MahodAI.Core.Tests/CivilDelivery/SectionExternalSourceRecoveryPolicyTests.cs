using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionExternalSourceRecoveryPolicy;

namespace MahodAI.Core.Tests.CivilDelivery;

public sealed class SectionExternalSourceRecoveryPolicyTests
{
    private static SourceProof Original => new(@"P:\project\survey.dwg", "survey.dwg", new string('a', 64),
        new[] { "cl-xref", "projected-utility" }, "survey", false, null);
    private static SourceProof Local => Original with { Path = @"C:\local\survey.dwg" };

    [Fact] public void ByteIdenticalRelocationIsExplicitAndDoesNotOpenOldPath()
    {
        var result = Compare(new[] { Original }, new[] { Local });
        Assert.True(result.Equivalent, result.Reason);
        var proof = Assert.Single(result.Relocations);
        Assert.Equal(Original.Path, proof.OriginalPath);
        Assert.Equal(Local.Path, proof.CurrentPath);
        Assert.Equal(Original.Sha256, proof.Sha256);
    }

    [Fact] public void ExactPathRetainsOriginalContentContractDespiteReaderRoleChanges()
    {
        var changedReader = Original with { Roles = new[] { "plan-mark" }, Chain = "new-context", Name = "new-reader-name" };
        var result = Compare(new[] { Original }, new[] { changedReader });
        Assert.True(result.Equivalent, result.Reason);
        Assert.Empty(result.Relocations);
    }

    [Fact] public void ExactPathsBindBeforeRelocationsEvenWhenBytesAndContextAreEqual()
    {
        var second = Original with { Path = @"P:\second\survey.dwg" };
        var result = Compare(new[] { second, Original }, new[] { Original, Local });
        Assert.True(result.Equivalent, result.Reason);
        Assert.Equal(second.Path, Assert.Single(result.Relocations).OriginalPath);
    }

    [Fact] public void InputOrderAndRoleOrderDoNotChangeOneToOneMatching()
    {
        var other = Original with { Path = @"P:\project\other.dwg", Name = "other.dwg", Chain = "other" };
        var result = Compare(new[] { Original, other }, new[] { other, Local with { Roles = Original.Roles.Reverse().ToArray() } });
        Assert.True(result.Equivalent, result.Reason);
        Assert.Single(result.Relocations);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("requires-live")]
    [InlineData("live-revision")]
    [InlineData("basename")]
    [InlineData("name")]
    [InlineData("chain")]
    [InlineData("roles")]
    [InlineData("role-multiplicity")]
    [InlineData("missing-chain")]
    [InlineData("missing-roles")]
    [InlineData("invalid-hash")]
    [InlineData("relative-path")]
    [InlineData("parent-path")]
    public void RelocationCannotSubstituteAnotherSourceOrIncompleteIdentity(string change)
    {
        var candidate = change switch
        {
            "hash" => Local with { Sha256 = new string('b', 64) },
            "requires-live" => Local with { RequiresLiveDatabase = true, LiveDatabaseRevision = "db-1" },
            "live-revision" => Local with { LiveDatabaseRevision = "db-2" },
            "basename" => Local with { Path = @"C:\local\other.dwg" },
            "name" => Local with { Name = "other.dwg" },
            "chain" => Local with { Chain = "other/survey" },
            "roles" => Local with { Roles = new[] { "plan-mark" } },
            "role-multiplicity" => Local with { Roles = Original.Roles.Concat(new[] { "cl-xref" }).ToArray() },
            "missing-chain" => Local with { Chain = null },
            "missing-roles" => Local with { Roles = Array.Empty<string>() },
            "invalid-hash" => Local with { Sha256 = "unknown" },
            "relative-path" => Local with { Path = "survey.dwg" },
            "parent-path" => Local with { Path = @"C:\local\..\survey.dwg" },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.False(Compare(new[] { Original }, new[] { candidate }).Equivalent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveDatabaseRevisionMustMatchForExactPathsAndRelocations(bool relocate)
    {
        var source = Original with { RequiresLiveDatabase = true, LiveDatabaseRevision = "db-1" };
        var target = source with { Path = relocate ? Local.Path : Original.Path };
        Assert.True(Compare(new[] { source }, new[] { target }).Equivalent);
        Assert.False(Compare(new[] { source }, new[] { target with { LiveDatabaseRevision = "db-2" } }).Equivalent);
        Assert.False(Compare(new[] { source }, new[] { target with { LiveDatabaseRevision = null } }).Equivalent);
        Assert.False(Compare(new[] { source }, new[] { target with { RequiresLiveDatabase = false } }).Equivalent);
    }

    [Fact] public void MissingExtraDuplicateOrAmbiguousSourcesCannotCollapseContentMultiplicity()
    {
        var secondOld = Original with { Path = @"P:\second\survey.dwg" };
        var secondNew = Local with { Path = @"C:\second\survey.dwg" };
        Assert.False(Compare(new[] { Original, secondOld }, new[] { Local }).Equivalent);
        Assert.False(Compare(new[] { Original }, new[] { Local, secondNew }).Equivalent);
        Assert.False(Compare(new[] { Original, Original }, new[] { Local, secondNew }).Equivalent);
        Assert.False(Compare(new[] { Original, secondOld }, new[] { Local, Local }).Equivalent);
        Assert.False(Compare(new[] { Original, secondOld }, new[] { Local, secondNew }).Equivalent);
    }

    [Fact] public void ExactPathChangedBytesCannotBeReassignedToAnIdenticalOtherPath()
    {
        var other = Original with { Path = @"P:\other\survey.dwg", Sha256 = new string('b', 64) };
        Assert.False(Compare(new[] { Original, other }, new[]
        {
            Original with { Sha256 = other.Sha256 }, other with { Sha256 = Original.Sha256 },
        }).Equivalent);
    }
}
