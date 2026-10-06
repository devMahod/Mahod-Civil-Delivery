using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Xunit.Abstractions;

namespace MahodAI.Core.Tests.Estimate;

public sealed class SemanticHintStoreTests
{
    private readonly ITestOutputHelper _output;
    public SemanticHintStoreTests(ITestOutputHelper output) => _output = output;
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static SemanticHintPolicy.Scope Scope(string rule = "layer:DSFSDF|length") =>
        new("hint-store-fixture", @"C:\fixture\host.dwg", Hash, rule, "DSFSDF", "length", "m", Hash);

    [Fact]
    public void SaveReadAndUpdateAreSmallLocalFilesWithoutChangingProfileOrScan()
    {
        using var f = new Fixture();
        var profile = new ProjectProfile { ProfileId = "hint-store-fixture" };
        var before = JsonSerializer.Serialize(profile);
        var scope = Scope();
        var initial = f.Store.Read(scope.ProfileId, scope.DrawingPath, scope.RuleKey);
        initial.Hint.Should().BeNull(); initial.FileHash.Should().BeNull();
        var saved = f.Store.Save(scope, new("curb", "תיאור נבדק"), "SYNTHETIC REVIEWER", initial.FileHash);
        var loaded = f.Store.Read(scope.ProfileId, scope.DrawingPath, scope.RuleKey);
        loaded.Should().Be(saved);
        loaded.Hint!.Input.RoleId.Should().Be("curb");
        loaded.Hint.RecordedAtUtc.Kind.Should().Be(DateTimeKind.Utc);
        CatalogIdentity.IsValidSha256(loaded.FileHash).Should().BeTrue();
        var updated = f.Store.Save(scope, new("curb", "תיאור מתוקן"), "SYNTHETIC REVIEWER", loaded.FileHash);
        updated.FileHash.Should().NotBe(saved.FileHash);
        f.Store.Read(scope.ProfileId, scope.DrawingPath, scope.RuleKey).Should().Be(updated);
        JsonSerializer.Serialize(profile).Should().Be(before);
        Directory.GetFiles(f.Root, "*.json").Should().ContainSingle();
        Directory.GetFiles(f.Root, "*.tmp-*").Should().BeEmpty();
        new FileInfo(Directory.GetFiles(f.Root, "*.json").Single()).Length.Should().BeLessThan(8192);
    }

    [Fact]
    public void UnchangedSaveReturnsOriginalTimestampAndHashWithoutRewriting()
    {
        using var f = new Fixture(); var scope = Scope();
        var saved = f.Store.Save(scope, new("curb", "説明"), "FIXTURE", null);
        var path = Directory.GetFiles(f.Root, "*.json").Single();
        var stamp = File.GetLastWriteTimeUtc(path);
        var result = f.Store.Save(scope, new("curb", "説明 "), "FIXTURE", saved.FileHash);
        result.Should().Be(saved);
        File.GetLastWriteTimeUtc(path).Should().Be(stamp);
    }

    [Fact]
    public void ConcurrentNewAndEditedDescriptionsCannotOverwriteOriginalBaseline()
    {
        using var f = new Fixture(); var scope = Scope();
        var original = f.Store.Read(scope.ProfileId, scope.DrawingPath, scope.RuleKey);
        var first = f.Store.Save(scope, new("curb", "ראשון"), "FIXTURE", original.FileHash);
        Action staleCreation = () => f.Store.Save(scope, new("water", ""), "OTHER", original.FileHash);
        staleCreation.Should().Throw<InvalidOperationException>().WithMessage("*לא נדרס*");
        var second = f.Store.Save(scope, new("curb", "שני"), "FIXTURE", first.FileHash);
        Action staleEdit = () => f.Store.Save(scope, new("water", ""), "OTHER", first.FileHash);
        staleEdit.Should().Throw<InvalidOperationException>().WithMessage("*לא נדרס*");
        f.Store.Read(scope.ProfileId, scope.DrawingPath, scope.RuleKey).Should().Be(second);
    }

    [Fact]
    public void StableFileIdentityRetainsExactSourceScopeSoChangedSourceCannotReuseDescription()
    {
        using var f = new Fixture(); var scope = Scope();
        var saved = f.Store.Save(scope, new("curb", ""), "FIXTURE", null);
        var changed = scope with { DrawingHash = new string('b', 64), SourcesHash = new string('c', 64) };
        var existing = f.Store.Read(changed.ProfileId, changed.DrawingPath.ToUpperInvariant(), changed.RuleKey);
        existing.Should().Be(saved);
        existing.Hint!.Source.Should().NotBe(changed, "the caller must require the exact captured scope before reuse");
        f.Store.Read("other-project", scope.DrawingPath, scope.RuleKey).Hint.Should().BeNull();
        f.Store.Read(scope.ProfileId, @"C:\fixture\other.dwg", scope.RuleKey).Hint.Should().BeNull();
        f.Store.Read(scope.ProfileId, scope.DrawingPath, "other-rule").Hint.Should().BeNull();
        f.Store.Save(changed, new("water", ""), "FIXTURE", existing.FileHash);
        Directory.GetFiles(f.Root, "*.json").Should().ContainSingle();
    }

    [Fact]
    public void FileNameIsHashNotUntrustedProjectOrRuleText()
    {
        using var f = new Fixture(); var scope = Scope("../../not-a-path") with { ProfileId = "../../not-a-directory" };
        f.Store.Save(scope, new("curb", ""), "FIXTURE", null);
        var file = Directory.GetFiles(f.Root, "*.json").Should().ContainSingle().Subject;
        Path.GetFileNameWithoutExtension(file).Should().MatchRegex("^[0-9a-f]{64}$");
        Path.GetDirectoryName(file).Should().Be(f.Root);
        Directory.GetDirectories(f.Root).Should().BeEmpty();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("foreign")]
    [InlineData("version")]
    [InlineData("oversized")]
    public void CorruptOrForeignSidecarIsNotReusedOrSilentlyOverwritten(string defect)
    {
        using var f = new Fixture(); var scope = Scope();
        var saved = f.Store.Save(scope, new("curb", ""), "FIXTURE", null);
        var path = Directory.GetFiles(f.Root, "*.json").Single();
        var text = File.ReadAllText(path);
        File.WriteAllText(path, defect switch
        {
            "foreign" => text.Replace(scope.ProfileId, "other-project", StringComparison.Ordinal),
            "version" => text.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 2", StringComparison.Ordinal),
            "oversized" => new string('x', 65537),
            _ => "not json"
        });
        var bytes = File.ReadAllBytes(path);
        Action read = () => f.Store.Read(scope.ProfileId, scope.DrawingPath, scope.RuleKey);
        read.Should().Throw<InvalidDataException>();
        Action save = () => f.Store.Save(scope, new("water", ""), "FIXTURE", saved.FileHash);
        save.Should().Throw<InvalidDataException>();
        File.ReadAllBytes(path).Should().Equal(bytes);
    }

    [Fact]
    public void ConcurrentLockRefusesPromptlyWithoutPublishing()
    {
        using var f = new Fixture(); var scope = Scope();
        var saved = f.Store.Save(scope, new("curb", ""), "FIXTURE", null);
        var path = Directory.GetFiles(f.Root, "*.json").Single();
        using var held = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Action save = () => f.Store.Save(scope, new("water", ""), "FIXTURE", saved.FileHash);
        save.Should().Throw<InvalidOperationException>().WithMessage("*לנעול*");
        f.Store.Read(scope.ProfileId, scope.DrawingPath, scope.RuleKey).Should().Be(saved);
    }

    [Fact]
    public void AtomicReplacementFailurePreservesExactPreviousBytesAndCas()
    {
        using var f = new Fixture(); var scope = Scope();
        var saved = f.Store.Save(scope, new("curb", "before"), "FIXTURE", null);
        var path = Directory.GetFiles(f.Root, "*.json").Single();
        var before = File.ReadAllBytes(path);
        // A reader that deliberately denies delete/replacement reproduces an OS
        // publication refusal without changing permissions or weakening the guard.
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Xunit.Record.Exception(() => f.Store.Save(scope, new("water", "after"), "FIXTURE", saved.FileHash));
            (error is IOException or UnauthorizedAccessException).Should().BeTrue();
            File.ReadAllBytes(path).Should().Equal(before);
            f.Store.Read(scope.ProfileId, scope.DrawingPath, scope.RuleKey).Should().Be(saved);
        }
        Directory.GetFiles(f.Root, "*.tmp-*").Should().BeEmpty();
        f.Store.Save(scope, new("water", "after"), "FIXTURE", saved.FileHash).Hint!.Input.RoleId.Should().Be("water");
    }

    [Fact]
    public void PartialReplacementRestoresMissingDestinationFromExactOriginalBackup()
    {
        using var f = new Fixture(); var scope = Scope();
        var saved = f.Store.Save(scope, new("curb", "before"), "FIXTURE", null);
        var path = Directory.GetFiles(f.Root, "*.json").Single();
        var before = File.ReadAllBytes(path);
        var failing = new SemanticHintStore(f.Root, (_, destination, backup) =>
        {
            File.Move(destination, backup);
            throw new IOException("Synthetic ERROR_UNABLE_TO_MOVE_REPLACEMENT_2", unchecked((int)0x80070499));
        });
        Action save = () => failing.Save(scope, new("water", "after"), "FIXTURE", saved.FileHash);
        save.Should().Throw<IOException>().Which.HResult.Should().Be(unchecked((int)0x80070499));
        File.ReadAllBytes(path).Should().Equal(before);
        f.Store.Read(scope.ProfileId, scope.DrawingPath, scope.RuleKey).Should().Be(saved);
        Directory.GetFiles(f.Root, "*.previous-*").Should().BeEmpty();
        Directory.GetFiles(f.Root, "*.tmp-*").Should().BeEmpty();
    }

    [Fact]
    public void PartialReplacementNeverOverwritesChangedDestinationAndRetainsExactRecoveryPath()
    {
        using var f = new Fixture(); var scope = Scope();
        var saved = f.Store.Save(scope, new("curb", "before"), "FIXTURE", null);
        var path = Directory.GetFiles(f.Root, "*.json").Single();
        var before = File.ReadAllBytes(path);
        var failing = new SemanticHintStore(f.Root, (_, destination, backup) =>
        {
            File.Move(destination, backup);
            File.WriteAllText(destination, "UNRELATED CHANGED DESTINATION");
            throw new IOException("Synthetic ERROR_UNABLE_TO_MOVE_REPLACEMENT_2", unchecked((int)0x80070499));
        });
        Action save = () => failing.Save(scope, new("water", "after"), "FIXTURE", saved.FileHash);
        var error = save.Should().Throw<IOException>().Which;
        var recovery = Directory.GetFiles(f.Root, "*.previous-*").Should().ContainSingle().Subject;
        error.Message.Should().Contain(path).And.Contain(recovery);
        File.ReadAllText(path).Should().Be("UNRELATED CHANGED DESTINATION");
        File.ReadAllBytes(recovery).Should().Equal(before);
        Directory.GetFiles(f.Root, "*.tmp-*").Should().BeEmpty();
    }

    [Fact]
    public void SuccessfulPublicationDoesNotDeleteUnrelatedBackupFiles()
    {
        using var f = new Fixture(); var scope = Scope();
        var saved = f.Store.Save(scope, new("curb", "before"), "FIXTURE", null);
        var path = Directory.GetFiles(f.Root, "*.json").Single();
        var unrelated = path + ".previous-unrelated";
        File.WriteAllText(unrelated, "KEEP");
        f.Store.Save(scope, new("water", "after"), "FIXTURE", saved.FileHash);
        Directory.GetFiles(f.Root, "*.previous-*").Should().ContainSingle().Which.Should().Be(unrelated);
        File.ReadAllText(unrelated).Should().Be("KEEP");
    }

    [Fact]
    public void ThousandAndThirtyOneRepeatedEditsKeepOneSmallFileNotGrowingScanHistory()
    {
        using var f = new Fixture(); var scope = Scope();
        string? expected = null;
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 1031; i++)
        {
            try { expected = f.Store.Save(scope, new("curb", "SYNTHETIC EDIT " + i), "FIXTURE", expected).FileHash; }
            catch (Exception error)
            {
                _output.WriteLine($"Save {i} failed: {error.GetType().Name}, HRESULT=0x{error.HResult:X8}. Expected baseline={expected}.");
                foreach (var file in Directory.GetFiles(f.Root))
                    _output.WriteLine($"{Path.GetFileName(file)} bytes={new FileInfo(file).Length} attributes={File.GetAttributes(file)}");
                throw;
            }
        }
        clock.Stop();
        var files = Directory.GetFiles(f.Root);
        files.Should().HaveCount(2, "only one JSON description and its publication lock, with no scan history");
        files.Sum(file => new FileInfo(file).Length).Should().BeLessThan(8192);
        f.Store.Read(scope.ProfileId, scope.DrawingPath, scope.RuleKey).Hint!.Input.Description.Should().EndWith("1030");
        _output.WriteLine($"Synthetic local sidecar: 1,031 saves in {clock.Elapsed.TotalSeconds:F3}s; retained {files.Sum(file => new FileInfo(file).Length):N0} bytes. Not a Civil timing test.");
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mahod-hint-sidecar-" + Guid.NewGuid().ToString("N"));
        internal SemanticHintStore Store { get; }
        internal Fixture() { Directory.CreateDirectory(Root); Store = new(Root); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
