using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;
using C = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.XrefOriginalReferenceCache;

public sealed class ExplicitLocalReadTests
{
    // Synthetic paths: ResolveReadIdentity does not access any of them.
    const string Logical = @"P:\project\source.dwg";
    const string Local = @"C:\review\source-copy.dwg";
    const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    static XrefQuantityPolicy.SourceSnapshotIdentity Snapshot() => new(
        "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222", 3, 123456);
    static C.SourceReadIdentity Identity(string? path = Local) =>
        C.ResolveReadIdentity(Logical, Logical, Logical, path);

    [Fact] public void NoAliasPreservesLogicalReadPathWithoutFilesystemProbe()
    {
        var result = Identity(null);
        result.LogicalSourcePath.Should().Be(Logical);
        result.ReadSourcePath.Should().Be(Logical);
        result.UsedExplicitLocalReadPath.Should().BeFalse();
    }
    [Fact] public void ExplicitAliasDoesNotReplaceLogicalIdentity()
    {
        var result = Identity();
        result.LogicalSourcePath.Should().Be(Logical);
        result.ReadSourcePath.Should().Be(Local);
        result.UsedExplicitLocalReadPath.Should().BeTrue();
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void EitherLoadedFilenameMismatchCannotBeCuredByAnAlias(bool parent)
    {
        Action act = () => C.ResolveReadIdentity(Logical, parent ? Local : Logical,
            parent ? Logical : Local, Local);
        act.Should().Throw<InvalidOperationException>().WithMessage("*filename mismatch*");
    }
    [Theory]
    [InlineData("")] [InlineData("relative.dwg")] [InlineData(@"C:source.dwg")]
    [InlineData(@"\\server\share\source.dwg")] [InlineData(@"\\?\C:\source.dwg")]
    [InlineData(@"C:\source.dwg:stream")]
    public void InvalidExplicitAliasNeverFallsBackToLogical(string alias)
    {
        Action act = () => Identity(alias);
        act.Should().Throw<InvalidOperationException>();
    }
    [Fact] public void SameBindingIsIdempotentAndHashCaseIsNotAnIdentityChange()
    {
        using var cache = new C();
        cache.BindReadSource(Identity(), Sha, Snapshot()).Should().BeNull();
        cache.BindReadSource(Identity(), Sha.ToUpperInvariant(), Snapshot()).Should().BeNull();
    }
    [Theory] [InlineData("path")] [InlineData("hash")] [InlineData("snapshot")]
    public void RebindingLogicalSourcePoisonsSubsequentBindings(string change)
    {
        using var cache = new C();
        cache.BindReadSource(Identity(), Sha, Snapshot()).Should().BeNull();
        var id = change == "path" ? Identity(@"C:\review\other.dwg") : Identity();
        var hash = change == "hash" ? new string('b', 64) : Sha;
        var snapshot = change == "snapshot" ? Snapshot() with { NumberOfSaves = 4 } : Snapshot();
        cache.BindReadSource(id, hash, snapshot).Should().StartWith("original-source-read-binding-changed:");
        cache.BindReadSource(Identity(), Sha, Snapshot()).Should().StartWith("original-source-read-binding-changed:");
    }
    [Fact] public void TwoLogicalSourcesMayShareVerifiedReadFileButKeepSeparateBindings()
    {
        using var cache = new C();
        cache.BindReadSource(Identity(), Sha, Snapshot()).Should().BeNull();
        const string other = @"P:\other\source.dwg";
        var id = C.ResolveReadIdentity(other, other, other, Local);
        cache.BindReadSource(id, Sha, Snapshot()).Should().BeNull();
        // No file is read by BindReadSource. Query still checks the disk hash and
        // each loaded snapshot; this assertion is not a native read claim.
        cache.CachedSourceCount.Should().Be(0);
    }
    [Theory] [InlineData("")] [InlineData("wrong")] [InlineData("g", true)]
    public void InvalidExpectedHashNeverBinds(string hash, bool repeat = false)
    {
        using var cache = new C();
        cache.BindReadSource(Identity(), repeat ? new string(hash[0], 64) : hash, Snapshot())
            .Should().Be("invalid expected source SHA256");
    }
    [Fact] public void IncompleteLoadedSnapshotCannotBind()
    {
        using var cache = new C();
        cache.BindReadSource(Identity(), Sha, Snapshot() with { FingerprintGuid = null })
            .Should().Be("loaded-xref-identity-unavailable");
    }
    [Fact] public void ResultRetainsTwoArgumentConstructorAndBothIdentitiesInOldDetailReceipt()
    {
        var original = new C.Result(C.Decision.Refused, "test refusal");
        var result = C.WithSourceReadIdentity(original, Identity());
        result.Decision.Should().Be(C.Decision.Refused);
        result.Detail.Should().Contain("test refusal").And.Contain(Logical).And.Contain(Local);
        result.LogicalSourcePath.Should().Be(Logical);
        result.ReadSourcePath.Should().Be(Local);
        result.UsedExplicitLocalReadPath.Should().BeTrue();
        JsonSerializer.Serialize(result).Should().Contain("LogicalSourcePath").And.Contain("ReadSourcePath");
        C.WithSourceReadIdentity(original, Identity(null)).Detail.Should().Be(original.Detail);
        C.WithSourceReadIdentity(original, null).Should().BeSameAs(original);
    }
    [Fact] public void LoadedAndDiskRevisionMismatchIsStillRefusedByExistingPolicy()
    {
        XrefQuantityPolicy.LoadedSnapshotFailure(Snapshot(), Snapshot() with { NumberOfSaves = 4 },
            Snapshot() with { NumberOfSaves = 4 }).Should().Be("loaded-xref-stale");
        XrefQuantityPolicy.LoadedSnapshotFailure(Snapshot(), Snapshot(), Snapshot() with { NumberOfSaves = 4 })
            .Should().Be("disk-xref-changed-during-scan");
    }
    [Fact] public void DisposedCacheCannotAcceptBindings()
    {
        var cache = new C(); cache.Dispose();
        cache.BindReadSource(Identity(), Sha, Snapshot()).Should().Be("original-reference-cache-disposed");
    }
    // Product lane (01/10): Codex's review project read ../../../fixtures/read-source.txt and a pre-made junction.
    // Same bytes and the same assertions, made self-contained under a private temp folder for this test run.
    static readonly string FixtureRoot = MakeFixtureRoot();
    static string MakeFixtureRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "explicit-local-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "read-source.txt"),
            "Synthetic local byte fixture. Not a DWG and never sent to Database.ReadDwgFile.\n");
        // A directory junction needs no elevation (mklink /J); it points back at the fixture folder itself.
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{Path.Combine(root, "reparse-link")}\" \"{root}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        // Leave nothing in %TEMP%: remove the junction itself (never its target), then the folder.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                var link = Path.Combine(root, "reparse-link");
                if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
                Directory.Delete(root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
        return root;
    }
    static string FixturePath => Path.Combine(FixtureRoot, "read-source.txt");
    static string FixtureSha => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(FixturePath)));
    [Fact] public void RealLocalBytesWithExactHashPassTheUnchangedPreOpenGate()
    {
        var stamp = C.VerifyInitialSourceFile(FixturePath, FixtureSha.ToLowerInvariant());
        stamp.Length.Should().Be(new FileInfo(FixturePath).Length);
    }
    [Fact] public void SameNameOrExplicitPathDoesNotExcuseWrongHash()
    {
        Action act = () => C.VerifyInitialSourceFile(FixturePath, new string('0', 64));
        act.Should().Throw<InvalidOperationException>().WithMessage("original-source-hash-mismatch");
    }
    [Fact] public void RealReparseParentIsRefusedBeforeReadingItsChild()
    {
        var junction = Path.Combine(FixtureRoot, "reparse-link");
        ((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0).Should().BeTrue();
        Action act = () => C.VerifyInitialSourceFile(Path.Combine(junction, "read-source.txt"), FixtureSha);
        act.Should().Throw<InvalidOperationException>().WithMessage("*reparse point*");
    }
    [Fact] public void UncReadPathFailsBeforeAttributesOrHash()
    {
        // No server connection is made: the drive-type guard rejects this root.
        Action act = () => C.VerifyInitialSourceFile(@"\\not-a-real-server.invalid\share\source.dwg", Sha);
        act.Should().Throw<Exception>();
    }
}
