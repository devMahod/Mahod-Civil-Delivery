using System.IO;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class EvidenceDirectoryPublicationRetryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mcd-publication-retry-" + Guid.NewGuid().ToString("N"));
    private const string Run = "SYNTHETIC-ONLY-retry";

    [Theory]
    [InlineData(5)] [InlineData(32)] [InlineData(33)]
    public void RecognizedWin32ErrorsRetryAtMostFourTimesAndPreserveLastError(int nativeCode)
    {
        var attempts = 0;
        var waits = new List<int>();
        var error = Win32(nativeCode);
        Action act = () => SectionsWorkflowService.MoveEvidenceDirectory("source", "destination",
            (_, _) => { attempts++; throw error; }, waits.Add, _ => true, _ => false);
        act.Should().Throw<IOException>().Which.Should().BeSameAs(error);
        attempts.Should().Be(4);
        waits.Should().Equal(25, 75, 150);
        waits.Sum().Should().Be(250);
    }

    [Fact]
    public void AccessExceptionThenSuccessRetainsOnlyRequiredDelay()
    {
        var attempts = 0;
        var waits = new List<int>();
        SectionsWorkflowService.MoveEvidenceDirectory("source", "destination",
            (_, _) => { if (++attempts == 1) throw new UnauthorizedAccessException("synthetic access denial"); },
            waits.Add, _ => true, _ => false);
        attempts.Should().Be(2); waits.Should().Equal(25);
    }

    [Theory]
    [InlineData("disk-full")] [InlineData("file-exists")] [InlineData("non-win32-prefix")]
    [InlineData("non-io")] [InlineData("missing-source")] [InlineData("destination-exists")]
    public void OtherErrorsOrChangedPathsAreNeverRetried(string scenario)
    {
        Exception error = scenario switch
        {
            "disk-full" => Win32(112), "file-exists" => Win32(183),
            "non-win32-prefix" => new IOException("not a Win32 HRESULT", unchecked((int)0x80130005)),
            "non-io" => new ArgumentException("synthetic invalid path"), _ => Win32(5),
        };
        var calls = 0;
        var waits = new List<int>();
        Action act = () => SectionsWorkflowService.MoveEvidenceDirectory("source", "destination",
            (_, _) => { calls++; throw error; }, waits.Add,
            _ => scenario != "missing-source", _ => scenario == "destination-exists");
        act.Should().Throw<Exception>().Which.Should().BeSameAs(error);
        calls.Should().Be(1); waits.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void StateChangedDuringWaitStopsBeforeAnotherMove(bool destinationAppeared)
    {
        var waited = false;
        var calls = 0;
        var error = Win32(5);
        Action act = () => SectionsWorkflowService.MoveEvidenceDirectory("source", "destination",
            (_, _) => { calls++; throw error; }, _ => waited = true,
            _ => destinationAppeared || !waited, _ => destinationAppeared && waited);
        act.Should().Throw<IOException>().Which.Should().BeSameAs(error);
        calls.Should().Be(1); waited.Should().BeTrue();
    }

    [Fact]
    public void NativeNoDeleteShareLockReleasedByWaitMovesOriginalGenerationSuccessfully()
    {
        var original = Seed();
        var source = Path.Combine(root, Run);
        var destination = Path.Combine(root, "SYNTHETIC-ONLY-backup");
        using var held = new FileStream(Path.Combine(source, "estimate_scan.json"),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        var waits = 0;
        SectionsWorkflowService.MoveEvidenceDirectory(
            source, destination, wait: _ => { waits++; held.Dispose(); });
        waits.Should().BeInRange(1, 3, "the actual Windows directory rename encounters the controlled handle and all waits remain bounded");
        Directory.Exists(source).Should().BeFalse();
        AssertBytes(destination, original);
        Directory.GetDirectories(root).Should().Equal(destination);
    }

    [Fact]
    public void PersistentNativeNoDeleteShareLockRefusesAndPreservesEveryOldByte()
    {
        var original = Seed();
        using var held = new FileStream(Path.Combine(root, Run, "estimate_scan.json"),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        Action act = () => Publish();
        act.Should().Throw<IOException>().Where(e => (e.HResult & 0xffff) == 5);
        AssertBytes(Path.Combine(root, Run), original);
        Directory.GetDirectories(root).Select(Path.GetFileName).Should().Equal(Run);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SecondRenameFailureRestoresOriginalGenerationAndRethrowsPublicationError(bool transientRestoreDenial)
    {
        var original = Seed();
        var publicationError = Win32(112);
        var restoreCalls = 0;
        void Move(string source, string destination)
        {
            if (source.Contains(Path.DirectorySeparatorChar + ".pending-", StringComparison.Ordinal))
                throw publicationError;
            if (Path.GetFileName(source).StartsWith(".replaced-", StringComparison.Ordinal) &&
                ++restoreCalls == 1 && transientRestoreDenial) throw Win32(33);
            Directory.Move(source, destination);
        }
        Action act = () => Publish(Move);
        act.Should().Throw<IOException>().Which.Should().BeSameAs(publicationError);
        AssertBytes(Path.Combine(root, Run), original);
        restoreCalls.Should().BeInRange(transientRestoreDenial ? 2 : 1, 4);
        Directory.GetDirectories(root).Select(Path.GetFileName).Should().Equal(Run);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PendingPublicationRetriesKnownErrorForBothNewAndReplacementRuns(bool replacing)
    {
        if (replacing) Seed();
        var pendingMoves = 0;
        void Move(string source, string destination)
        {
            if (source.Contains(Path.DirectorySeparatorChar + ".pending-", StringComparison.Ordinal) &&
                ++pendingMoves == 1) throw Win32(32);
            Directory.Move(source, destination);
        }
        SectionsWorkflowService.PersistEvidenceBundle(Run, "estimate_scan.json", new { Value = "new generation" },
            (pendingRoot, _) => File.WriteAllText(Path.Combine(pendingRoot, Run, "run_manifest.json"), "new manifest"),
            replaceExisting: replacing, runsRoot: root, moveDirectory: Move);
        pendingMoves.Should().BeInRange(2, 4);
        File.ReadAllText(Path.Combine(root, Run, "estimate_scan.json")).Should().Contain("new generation");
        Directory.GetDirectories(root).Select(Path.GetFileName).Should().Equal(Run);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailedRestoreOrForeignDestinationPreservesCompleteBackupAndBothErrors(bool foreignDestination)
    {
        var original = Seed();
        var publicationError = Win32(112);
        var restorationError = Win32(112);
        var rollbackCalls = 0;
        void Move(string source, string destination)
        {
            if (source.Contains(Path.DirectorySeparatorChar + ".pending-", StringComparison.Ordinal))
            {
                if (foreignDestination)
                {
                    Directory.CreateDirectory(Path.Combine(root, Run));
                    File.WriteAllText(Path.Combine(root, Run, "foreign.txt"), "must not overwrite or delete");
                }
                throw publicationError;
            }
            if (Path.GetFileName(source).StartsWith(".replaced-", StringComparison.Ordinal))
            {
                rollbackCalls++;
                throw restorationError;
            }
            Directory.Move(source, destination);
        }
        Action act = () => Publish(Move);
        var error = act.Should().Throw<AggregateException>().Which;
        error.InnerExceptions.Should().HaveCount(2);
        error.InnerExceptions[0].Should().BeSameAs(publicationError);
        if (!foreignDestination) error.InnerExceptions[1].Should().BeSameAs(restorationError);
        var backup = Directory.GetDirectories(root).Single(path => Path.GetFileName(path).StartsWith(".replaced-", StringComparison.Ordinal));
        error.Message.Should().Contain(backup).And.Contain(Path.Combine(root, Run));
        AssertBytes(backup, original);
        Directory.GetDirectories(root).Should().NotContain(path => Path.GetFileName(path).StartsWith(".pending-", StringComparison.Ordinal));
        rollbackCalls.Should().Be(foreignDestination ? 0 : 1);
        if (foreignDestination)
            File.ReadAllText(Path.Combine(root, Run, "foreign.txt")).Should().Be("must not overwrite or delete");
        else Directory.Exists(Path.Combine(root, Run)).Should().BeFalse();
    }

    private void Publish(Action<string, string>? move = null) => SectionsWorkflowService.PersistEvidenceBundle(
        Run, "estimate_scan.json", new { Value = "new generation" },
        (pendingRoot, _) => File.WriteAllText(Path.Combine(pendingRoot, Run, "run_manifest.json"), "new manifest"),
        replaceExisting: true, runsRoot: root, moveDirectory: move);

    private Dictionary<string, byte[]> Seed()
    {
        var run = Path.Combine(root, Run);
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "estimate_scan.json"), "old measured scan");
        File.WriteAllText(Path.Combine(run, "run_manifest.json"), "old manifest");
        File.WriteAllText(Path.Combine(run, "unrelated.json"), "old unrelated");
        return Directory.GetFiles(run).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
    }

    private static void AssertBytes(string directory, Dictionary<string, byte[]> original)
    {
        Directory.GetFiles(directory).Select(Path.GetFileName).Should().BeEquivalentTo(original.Keys);
        foreach (var pair in original)
            File.ReadAllBytes(Path.Combine(directory, pair.Key)).Should().Equal(pair.Value);
    }
    private static IOException Win32(int code) => new("synthetic Win32 " + code, unchecked((int)(0x80070000u | (uint)code)));

    public void Dispose()
    {
        var full = Path.GetFullPath(root);
        var name = Path.GetFileName(full);
        const string prefix = "mcd-publication-retry-";
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) || !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new InvalidOperationException("Refusing cleanup outside the exact synthetic fixture: " + full);
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
