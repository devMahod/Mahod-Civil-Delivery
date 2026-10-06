using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using FluentAssertions;
using Xunit;
using S = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.XrefReadSnapshots;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// 01/10 (Codex ACK 12:52): an XREF parent on a non-local drive is read by the original-reference cache from a local
/// copy of that exact path, passed only when the copied bytes hash to the SHA the scan computed. Every case runs on a
/// private temp tree; the "network" drive is simulated by the injected drive-type probe (path under the "net" folder).
/// </summary>
public sealed class XrefReadSnapshotsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xref-read-copies-" + Guid.NewGuid().ToString("N"));
    private readonly string _network;   // stands for P:\ — a folder on this disk that the probe reports as Network
    private readonly string _base;      // the read-copy base folder

    public XrefReadSnapshotsTests()
    {
        _network = Path.Combine(_root, "net");
        _base = Path.Combine(_root, "copies");
        Directory.CreateDirectory(_network);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private S New(S.Ledger ledger, string runId = "estimate-extract-20261001-091107-02f3d5d3", long maxBytes = S.DefaultMaxTotalBytes) =>
        new(runId, ledger, _base,
            path => path.StartsWith(_network, StringComparison.OrdinalIgnoreCase) ? DriveType.Network : DriveType.Fixed, maxBytes);

    private string Source(string name, string content)
    {
        var path = Path.Combine(_network, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private int CopiesOnDisk() => Directory.Exists(_base) ? Directory.GetFiles(_base, "*", SearchOption.AllDirectories).Length : 0;

    [Fact]
    public void AParentOnALocalDriveNeedsNoCopy()
    {
        var ledger = new S.Ledger();
        var source = Source("gm.dwg", "gm bytes");
        using var copies = new S("run", ledger, _base, path => DriveType.Fixed);
        copies.ReadPathFor(source, Sha(source)).Should().BeNull();
        ledger.Copies.Should().BeEmpty();
        Directory.Exists(_base).Should().BeFalse("nothing is created when no copy is needed");
    }

    [Fact]
    public void ACopyWhoseBytesHashToTheScannedShaIsTheReadPathAndIsMadeOnce()
    {
        var ledger = new S.Ledger();
        var source = Source("gm.dwg", "gm bytes");
        var sha = Sha(source);
        string? path;
        using (var copies = New(ledger))
        {
            path = copies.ReadPathFor(source, sha);
            path.Should().NotBeNull();
            File.ReadAllText(path!).Should().Be("gm bytes");
            Sha(path!).Should().Be(sha);
            path!.Should().StartWith(Path.GetFullPath(_base)).And.EndWith(".dwg");
            Path.GetFileNameWithoutExtension(path).Should().NotContain("gm", "the copy name never comes from the drawing name");
            copies.ReadPathFor(source, sha).Should().Be(path, "one copy per logical path in a scan");
            CopiesOnDisk().Should().Be(1);
            ledger.Copies.Should().ContainSingle();
            ledger.Copies[0].Status.Should().Be("prepared");
            ledger.Copies[0].LogicalSourcePath.Should().Be(Path.GetFullPath(source));
            ledger.Copies[0].Bytes.Should().Be(8);
            copies.NoteFor(source).Should().BeEmpty();
        }
        File.Exists(path!).Should().BeFalse("the copy is deleted on dispose");
        ledger.Copies[0].Cleanup.Should().Be("deleted");
        ledger.Copies[0].ReadSourcePath.Should().Be(path, "the receipt keeps the read path after cleanup");
        ledger.RunFolderCleanup.Should().Be("deleted");
        Directory.GetDirectories(_base).Should().BeEmpty("the run folder is removed when empty");
        File.ReadAllText(source).Should().Be("gm bytes", "the logical source is only read");
    }

    [Fact]
    public void BytesThatDoNotHashToTheScannedShaGiveNoAliasAndAreNotRetriedEvenWhenTheyWouldNowMatch()
    {
        var ledger = new S.Ledger();
        var scannedBytes = System.Text.Encoding.UTF8.GetBytes("the bytes the scan hashed");
        var scanned = Sha(scannedBytes);
        var source = Source("gm.dwg", "changed after the scan hashed it");
        using var copies = New(ledger);
        copies.ReadPathFor(source, scanned).Should().BeNull();
        ledger.Copies.Should().ContainSingle();
        ledger.Copies[0].Status.Should().StartWith("refused: InvalidDataException").And.Contain("not the scanned");
        ledger.Copies[0].ReadSourcePath.Should().BeNull();
        ledger.Copies[0].Cleanup.Should().Be("deleted", "the rejected copy existed and was removed at once");
        CopiesOnDisk().Should().Be(0);
        copies.NoteFor(source).Should().StartWith("; read-copy=refused: InvalidDataException");
        // Now the file DOES hash to the scanned SHA: a retry would succeed — it must not happen in the same scan.
        File.WriteAllBytes(source, scannedBytes);
        copies.ReadPathFor(source, scanned).Should().BeNull("a failed attempt is never retried in the same scan");
        CopiesOnDisk().Should().Be(0);
        ledger.Copies.Should().ContainSingle();
    }

    [Fact]
    public void ASecondShaForTheSamePathIsRefusedAndNotAdopted()
    {
        var ledger = new S.Ledger();
        var source = Source("gm.dwg", "gm bytes");
        using var copies = New(ledger);
        copies.ReadPathFor(source, Sha(source)).Should().NotBeNull();
        File.WriteAllText(source, "gm bytes, saved again");
        copies.ReadPathFor(source, Sha(source)).Should().BeNull();
        ledger.Copies.Should().HaveCount(2);
        ledger.Copies[1].Status.Should().StartWith("refused: the same source was seen with SHA-256");
        CopiesOnDisk().Should().Be(1, "no second copy is made");
    }

    [Fact]
    public void AnUnreachableSourceIsRefusedWithNoCopyCreatedAndAnInvalidShaToo()
    {
        var ledger = new S.Ledger();
        using var copies = New(ledger);
        copies.ReadPathFor(Path.Combine(_network, "missing.dwg"), new string('B', 64)).Should().BeNull();
        ledger.Copies[0].Status.Should().StartWith("refused: FileNotFoundException");
        ledger.Copies[0].Cleanup.Should().Be("none (no copy created)", "nothing was created, so nothing was deleted");
        var source = Source("albx.dwg", "albx");
        copies.ReadPathFor(source, "not-a-sha").Should().BeNull();
        ledger.Copies[1].Status.Should().Be("refused: invalid expected SHA-256");
        ledger.Copies[1].Cleanup.Should().Be("none (no copy created)");
        CopiesOnDisk().Should().Be(0);
    }

    [Theory]
    [InlineData(@"\\server\share\gm.dwg")]
    [InlineData(@"relative\gm.dwg")]
    [InlineData(@"C:gm.dwg")]
    public void APathTheCacheWouldRefuseGetsNoCopy(string logical)
    {
        var ledger = new S.Ledger();
        using var copies = New(ledger);
        copies.ReadPathFor(logical, new string('C', 64)).Should().BeNull();
        ledger.Copies.Should().BeEmpty();
    }

    [Fact]
    public void TheNumberOfCopiesPerScanIsBounded()
    {
        var ledger = new S.Ledger();
        using var copies = New(ledger);
        for (var i = 0; i < S.MaxSnapshots; i++)
        {
            var source = Source($"s{i}.dwg", "bytes " + i);
            copies.ReadPathFor(source, Sha(source)).Should().NotBeNull();
        }
        var extra = Source("extra.dwg", "extra");
        copies.ReadPathFor(extra, Sha(extra)).Should().BeNull();
        ledger.Copies.Last().Status.Should().Contain($"more than {S.MaxSnapshots}");
        CopiesOnDisk().Should().Be(S.MaxSnapshots);
    }

    [Fact]
    public void TheBytesPerScanAreBoundedAndAnOverflowingCopyIsRemoved()
    {
        var ledger = new S.Ledger();
        using var copies = New(ledger, maxBytes: 10);
        var small = Source("small.dwg", "12345");
        copies.ReadPathFor(small, Sha(small)).Should().NotBeNull();
        var big = Source("big.dwg", "1234567890");
        copies.ReadPathFor(big, Sha(big)).Should().BeNull("5 + 10 bytes exceed the 10-byte budget");
        ledger.Copies[1].Status.Should().Contain("exceed 10 bytes");
        ledger.Copies[1].Cleanup.Should().Be("deleted");
        CopiesOnDisk().Should().Be(1);
    }

    [Fact]
    public void ASourceHeldOpenForWritingByAnotherProcessIsStillCopiedWhenItsBytesMatch()
    {
        // Headless 01/10: GM on P: was held open (AutoCAD / an editor) and FileShare.Read refused the copy.
        var ledger = new S.Ledger();
        var source = Source("gm.dwg", "gm bytes");
        var sha = Sha(source);
        using var holder = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var copies = New(ledger);
        copies.ReadPathFor(source, sha).Should().NotBeNull();
        ledger.Copies[0].Status.Should().Be("prepared");
    }

    [Fact]
    public void BytesOfARefusedAttemptStillCountAgainstTheBudget()
    {
        var ledger = new S.Ledger();
        using var copies = New(ledger, maxBytes: 12);
        var wrong = Source("wrong.dwg", "1234567890");                  // 10 bytes, hash will not match
        copies.ReadPathFor(wrong, new string('D', 64)).Should().BeNull();
        var next = Source("next.dwg", "12345");                         // 10 + 5 > 12
        copies.ReadPathFor(next, Sha(next)).Should().BeNull("the refused attempt already used 10 of 12 bytes");
        ledger.Copies[1].Status.Should().Contain("exceed 12 bytes");
    }

    [Fact]
    public void CancelDeletesThePartialCopyRecordsItAndPropagates()
    {
        var ledger = new S.Ledger();
        var source = Source("gm.dwg", "gm bytes");
        using var copies = New(ledger);
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        var act = () => copies.ReadPathFor(source, Sha(source), cts.Token);
        act.Should().Throw<OperationCanceledException>();
        ledger.Copies.Should().ContainSingle();
        ledger.Copies[0].Status.Should().Be("refused: canceled");
        ledger.Copies[0].Cleanup.Should().Be("deleted", "the partial copy existed and was removed");
        CopiesOnDisk().Should().Be(0);
    }

    [Fact]
    public void ADriveThatCannotBeProbedIsRefusedVisiblyNotThrown()
    {
        var ledger = new S.Ledger();
        var source = Source("gm.dwg", "gm bytes");
        using var copies = new S("run", ledger, _base, path => throw new IOException("drive probe failed"));
        copies.ReadPathFor(source, Sha(source)).Should().BeNull();
        ledger.Copies.Should().ContainSingle();
        ledger.Copies[0].Status.Should().StartWith("refused: drive type unavailable: IOException");
        copies.NoteFor(source).Should().StartWith("; read-copy=refused: drive type unavailable");
    }

    [Fact]
    public void ACopyThatCannotBeDeletedIsReportedAsAnOrphanAndTheFolderAsLeft()
    {
        var ledger = new S.Ledger();
        var source = Source("gm.dwg", "gm bytes");
        var copies = New(ledger);
        var path = copies.ReadPathFor(source, Sha(source))!;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            copies.Dispose();
        ledger.Copies[0].Cleanup.Should().StartWith("orphan: ").And.Contain(path);
        ledger.RunFolderCleanup.Should().StartWith("left: ").And.Contain(ledger.RunFolder!);
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void AFolderThatCannotBeRemovedIsRecordedEvenWhenEveryCopyWasDeleted()
    {
        var ledger = new S.Ledger();
        var source = Source("gm.dwg", "gm bytes");
        var copies = New(ledger);
        var path = copies.ReadPathFor(source, Sha(source))!;
        File.WriteAllText(Path.Combine(ledger.RunFolder!, "not-ours.txt"), "x");   // something else wrote here
        copies.Dispose();
        ledger.Copies[0].Cleanup.Should().Be("deleted");
        ledger.RunFolderCleanup.Should().StartWith("left: ");
        File.Exists(Path.Combine(ledger.RunFolder!, "not-ours.txt")).Should().BeTrue("a file it did not create is never deleted");
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void RunFoldersOfEarlierScansAreCountedButNotTouched()
    {
        Directory.CreateDirectory(Path.Combine(_base, "estimate-extract-old-run-0001"));
        var ledger = new S.Ledger();
        var source = Source("gm.dwg", "gm bytes");
        using (var copies = New(ledger))
            copies.ReadPathFor(source, Sha(source)).Should().NotBeNull();
        ledger.LeftoverRunFolders.Should().Be(1);
        Directory.Exists(Path.Combine(_base, "estimate-extract-old-run-0001")).Should().BeTrue();
    }

    [Fact]
    public void TheRunFolderNameKeepsOnlySafeCharactersOfTheRunId()
    {
        var ledger = new S.Ledger();
        var source = Source("gm.dwg", "gm bytes");
        using var copies = New(ledger, @"..\..\evil:run");
        var path = copies.ReadPathFor(source, Sha(source))!;
        Path.GetDirectoryName(path).Should().StartWith(Path.Combine(Path.GetFullPath(_base), "evilrun-"));
    }

    [Fact]
    public void TheCallerPassesTheCopyOnlyForACandidateAndKeepsTheScanShaAndLogicalPath()
    {
        var extraction = File.ReadAllText(Path.Combine(SourceFolder(), "CivilQuantityExtractionService.cs")).Replace("\r\n", "\n");
        // The copies are released after the cache that read them (reverse disposal order).
        Before(extraction, "using var readSnapshots = new XrefReadSnapshots(runId, result.XrefReadCopies, log: log);",
            "using var originalReferences = new XrefOriginalReferenceCache();");
        // Exactly this gate — inside an XREF AND the cache's own candidate test returns null — guards the copy.
        extraction.Should().Contain(
            "                    try\n" +
            "                    {\n" +
            "                        if (isInsideExternalReference && XrefOriginalReferenceCache.CheckCandidate(definition.IsAnonymous,\n" +
            "                                definition.IsFromExternalReference, definition.IsFromOverlayReference, definition.IsLayout,\n" +
            "                                () => { foreach (ObjectId _ in definition) return true; return false; }) == null)\n" +
            "                            readCopy = readSnapshots.ReadPathFor(parentSource.DrawingPath, parentSource.DrawingHash);\n" +
            "                    }\n" +
            "                    catch (Autodesk.AutoCAD.Runtime.Exception) { readCopy = null; }\n" +
            "                    catch (InvalidOperationException) { readCopy = null; }\n" +
            "                    var original = originalReferences.Query(reference, definition,\n" +
            "                        parentSource.DrawingPath, parentSource.DrawingHash,\n");
        extraction.Split("readSnapshots.ReadPathFor(").Length.Should().Be(2, "the copy is requested in one place only");
        extraction.Should().Contain("isInsideExternalReference, hasExternalOverlayAncestor, readCopy);")
            .And.Contain("readCopy, original.ReadSourcePath, original.Decision.ToString(), original.Detail));")
            .And.Contain("readSnapshots.NoteFor(parentSource.DrawingPath));");
        var helper = File.ReadAllText(Path.Combine(SourceFolder(), "XrefReadSnapshots.cs"));
        helper.Should().NotContain("EnumerateFiles").And.NotContain("GetFiles").And.NotContain("SearchOption")
            .And.NotContain("recursive: true")
            .And.Contain("FileMode.CreateNew, FileAccess.Write, FileShare.None")
            .And.Contain("new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)");
        var workflow = File.ReadAllText(Path.Combine(SourceFolder(), "EstimateWorkflowService.cs"));
        workflow.Should().Contain("result.XrefReadCopies = extraction.XrefReadCopies;")
            .And.Contain("SectionsWorkflowService.WriteArtifact(result.RunId, XrefOriginalReadsArtifact,")
            // The logical P: sources are re-hashed after extraction exactly as before; the copies never replace them.
            .And.Contain("ExternalSourcesFreshnessReason(extraction.ExternalSources)");
    }

    private static void Before(string text, string first, string second)
    {
        var a = text.IndexOf(first, StringComparison.Ordinal);
        var b = text.IndexOf(second, StringComparison.Ordinal);
        a.Should().BeGreaterThanOrEqualTo(0, first);
        b.Should().BeGreaterThan(a, $"'{first}' must come before '{second}'");
    }

    private static string SourceFolder() => Path.Combine(
        typeof(XrefReadSnapshotsTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "MahodPluginSourceDir").Value!, "CivilDelivery", "Estimate");
}
