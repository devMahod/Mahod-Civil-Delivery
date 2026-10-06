using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// Local, hash-verified read copies of XREF parent drawings that sit on a non-local drive (P:), so the
/// original-reference cache can read the exact bytes this scan already proved (01/10: 16 of 23 XREF refusals were
/// the cache refusing a mapped network drive, while the traversal had hashed the same P: files). The copy is an
/// explicit alias of ONE logical path — never a search — and is passed only when its bytes hash to the SHA-256 the
/// same scan computed from the logical path; otherwise no alias is given and the refusal stays visible.
/// Reads the logical file only (never writes to it). Creates and deletes only its own run folder and files; every
/// outcome (prepared, refused, deleted, orphan, folder left) goes to the ledger AND the stage log, so it is reported
/// even when the scan stops before its evidence is published.
/// Dispose it AFTER the cache that read the copies (declare it before the cache: <c>using</c> disposes in reverse).
/// </summary>
internal sealed class XrefReadSnapshots : IDisposable
{
    internal const int MaxSnapshots = 16;
    internal const long DefaultMaxTotalBytes = 2L * 1024 * 1024 * 1024;
    internal const string FolderName = "xref-read-snapshots";

    /// <summary>One prepared (or refused) copy; kept after cleanup as the receipt of what was copied.</summary>
    internal sealed class Receipt
    {
        public required string LogicalSourcePath { get; init; }
        public required string ExpectedSha256 { get; init; }
        public string? ReadSourcePath { get; set; }
        public long Bytes { get; set; }
        public required string Status { get; set; }
        /// <summary>"deleted", "none (no copy created)" or "orphan: …" — never "deleted" for a file still on disk.</summary>
        public string? Cleanup { get; set; }
    }

    /// <summary>Everything the helper did in one scan, published with the scan evidence.</summary>
    internal sealed class Ledger
    {
        public List<Receipt> Copies { get; } = new();
        public string? RunFolder { get; set; }
        /// <summary>null = no folder was created; "deleted" or "left: …".</summary>
        public string? RunFolderCleanup { get; set; }
        /// <summary>Run folders of earlier scans still present when this one started (reported, never deleted here).</summary>
        public int LeftoverRunFolders { get; set; }
    }

    private readonly string _baseRoot;
    private readonly string _runFolderName;
    private readonly Func<string, DriveType> _driveType;
    private readonly long _maxTotalBytes;
    private readonly StageLog? _log;
    private readonly Dictionary<string, Receipt> _byLogical = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _created = new(StringComparer.OrdinalIgnoreCase);
    private string? _runRoot;
    private long _totalBytes;
    private bool _disposed;

    internal Ledger Record { get; }

    /// <param name="runId">Product-generated scan run id; only [A-Za-z0-9-] is kept for the folder name.</param>
    /// <param name="ledger">Where the outcome is kept (the extraction result), so it outlives the copies.</param>
    /// <param name="driveType">Drive type of a canonical path (tests simulate a network drive; default probes its root).</param>
    internal XrefReadSnapshots(string runId, Ledger ledger, string? baseRoot = null,
        Func<string, DriveType>? driveType = null, long maxTotalBytes = DefaultMaxTotalBytes, StageLog? log = null)
    {
        Record = ledger;
        _baseRoot = baseRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MahodAI_Civil3D", "civil-delivery", FolderName);
        var safeRun = new System.Text.StringBuilder();
        foreach (var c in runId ?? "") if (char.IsAsciiLetterOrDigit(c) || c == '-') safeRun.Append(c);
        // Unique per extraction even when a run id repeats; never derived from a drawing name.
        _runFolderName = (safeRun.Length > 0 ? safeRun + "-" : "") + Guid.NewGuid().ToString("N");
        _driveType = driveType ?? (path => new DriveInfo(Path.GetPathRoot(path)!).DriveType);
        _maxTotalBytes = maxTotalBytes;
        _log = log;
    }

    /// <summary>
    /// The local read path for <paramref name="logicalPath"/>, or null when none is needed (already on a local drive)
    /// or none can be proven (copy failed, limits, hash differs). One attempt per logical path per scan: a failed
    /// attempt is not retried, and a second SHA for the same path is refused, so no new hash is adopted mid-scan.
    /// </summary>
    /// <param name="cancel">Cancels between copy blocks: the partial copy is deleted, the receipt says so, and the
    /// OperationCanceledException propagates (the extraction has no cancel input yet; this is the helper's contract).</param>
    internal string? ReadPathFor(string logicalPath, string expectedSha256, CancellationToken cancel = default)
    {
        if (_disposed) return null;
        string canonical;
        try { canonical = XrefOriginalReferenceCache.CanonicalLocalPath(logicalPath); }
        catch (InvalidOperationException) { return null; }   // UNC / relative: the cache refuses it as before
        bool local;
        try { local = IsLocal(canonical); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A drive that cannot be probed is never treated as local, and never aborts the scan.
            if (!_byLogical.ContainsKey(canonical))
            {
                var probe = new Receipt { LogicalSourcePath = canonical, ExpectedSha256 = expectedSha256, Status = "pending" };
                _byLogical[canonical] = probe;
                Add(probe);
                Refuse(probe, "drive type unavailable: " + ex.GetType().Name + ": " + ex.Message);
            }
            return null;
        }
        if (local) return null;
        if (_byLogical.TryGetValue(canonical, out var known))
        {
            if (string.Equals(known.ExpectedSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                return known.Status == "prepared" ? known.ReadSourcePath : null;
            Add(new Receipt
            {
                LogicalSourcePath = canonical, ExpectedSha256 = expectedSha256, Cleanup = "none (no copy created)",
                Status = "refused: the same source was seen with SHA-256 " + known.ExpectedSha256 + " earlier in this scan",
            });
            return null;
        }

        var receipt = new Receipt { LogicalSourcePath = canonical, ExpectedSha256 = expectedSha256, Status = "pending" };
        _byLogical[canonical] = receipt;
        Add(receipt);
        if (expectedSha256 is not { Length: 64 } || !IsHex(expectedSha256))
            return Refuse(receipt, "invalid expected SHA-256");
        if (_byLogical.Count > MaxSnapshots)
            return Refuse(receipt, $"more than {MaxSnapshots} read copies in one scan");
        string? target = null;
        try
        {
            var root = RunRoot();
            // Same sharing as the scan's own hash of this file (ClInstructionReader.HashFileShared): AutoCAD or an
            // editor may hold it open for writing (headless 01/10: GM refused with FileShare.Read). Safe because the
            // copied bytes must hash to the scanned SHA — a change during the copy is a refusal, never an alias.
            using var source = new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            target = Path.Combine(root, Guid.NewGuid().ToString("N") + ".dwg");
            using (var copy = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                _created.Add(target);   // only now is it ours to delete
                cancel.ThrowIfCancellationRequested();   // also before the first read of an empty source
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 20];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    receipt.Bytes += read;
                    if (_totalBytes + receipt.Bytes > _maxTotalBytes)
                        throw new IOException($"read copies exceed {_maxTotalBytes} bytes in one scan");
                    hash.AppendData(buffer, 0, read);
                    copy.Write(buffer, 0, read);
                }
                copy.Flush(true);
                var actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"copied bytes hash to {actual}, not the scanned {expectedSha256.ToUpperInvariant()}");
            }
            receipt.ReadSourcePath = target;
            receipt.Status = "prepared";
            _log?.Info($"estimate.xref_read_copy prepared {canonical} -> {target} ({receipt.Bytes} bytes)");
            return target;
        }
        catch (OperationCanceledException)
        {
            Refuse(receipt, "canceled");
            if (target != null && _created.Contains(target)) DeleteOwned(target, receipt);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       InvalidOperationException or NotSupportedException or ArgumentException or
                                       System.Security.SecurityException)
        {
            Refuse(receipt, ex.GetType().Name + ": " + ex.Message);
            if (target != null && _created.Contains(target)) DeleteOwned(target, receipt);
            return null;
        }
        finally
        {
            // Every byte copied counts against the budget, also from attempts that were then refused.
            _totalBytes += receipt.Bytes;
        }
    }

    /// <summary>"; read-copy=…" for a refused copy of this logical path (so the visible finding names the real cause),
    /// or "" when no copy was attempted or it was prepared.</summary>
    internal string NoteFor(string logicalPath)
    {
        try
        {
            var canonical = XrefOriginalReferenceCache.CanonicalLocalPath(logicalPath);
            return _byLogical.TryGetValue(canonical, out var r) && r.Status != "prepared" ? "; read-copy=" + r.Status : "";
        }
        catch (InvalidOperationException) { return ""; }
    }

    private void Add(Receipt receipt)
    {
        Record.Copies.Add(receipt);
    }

    private string? Refuse(Receipt receipt, string why)
    {
        receipt.Status = "refused: " + why;
        receipt.Cleanup ??= "none (no copy created)";
        _log?.Info($"estimate.xref_read_copy refused {receipt.LogicalSourcePath}: {why}");
        return null;
    }

    private bool IsLocal(string canonical) => _driveType(canonical) is DriveType.Fixed or DriveType.Removable;

    /// <summary>The run folder: created on first use under a local base whose existing ancestors are checked for
    /// reparse points BEFORE anything is created through them; never reused.</summary>
    private string RunRoot()
    {
        if (_runRoot != null)
        {
            CheckDirectoryPath(_runRoot, createMissing: false);
            return _runRoot;
        }
        var baseRoot = XrefOriginalReferenceCache.CanonicalLocalPath(_baseRoot);
        if (!IsLocal(baseRoot)) throw new InvalidOperationException("read-copy folder is not on a local drive");
        CheckDirectoryPath(baseRoot, createMissing: true);
        Record.LeftoverRunFolders = Directory.EnumerateDirectories(baseRoot).Count();
        if (Record.LeftoverRunFolders > 0)
            _log?.Info($"estimate.xref_read_copy {Record.LeftoverRunFolders} run folder(s) of earlier scans remain under {baseRoot} (not deleted here)");
        var root = Path.Combine(baseRoot, _runFolderName);
        if (Directory.Exists(root)) throw new IOException("read-copy run folder already exists");
        CheckDirectoryPath(root, createMissing: true);
        Record.RunFolder = root;
        return _runRoot = root;
    }

    /// <summary>From the drive root inward: every existing component must not be a reparse point; each missing one is
    /// created alone and checked before anything is created below it. Reuse/cleanup never recreates missing paths.
    /// These checks do not provide atomic protection against a concurrent check-to-use race.</summary>
    private static void CheckDirectoryPath(string path, bool createMissing)
    {
        var components = new Stack<string>();
        for (var cursor = path; !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
            components.Push(cursor);
        foreach (var component in components)
        {
            if (!Directory.Exists(component))
            {
                if (!createMissing) throw new IOException("read-copy folder path disappeared: " + component);
                Directory.CreateDirectory(component);
            }
            if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("read-copy folder path contains a reparse point: " + component);
        }
    }

    private static bool HasReparseAncestor(string path)
    {
        for (var cursor = Path.GetDirectoryName(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
            if (Directory.Exists(cursor) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                return true;
        return false;
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    private void DeleteOwned(string file, Receipt receipt)
    {
        try
        {
            // Never delete through a folder that has since become a reparse point: report it instead.
            if (HasReparseAncestor(file))
            {
                receipt.Cleanup = "orphan: " + file + " (its folder became a reparse point; not deleted through it)";
                _log?.Info("estimate.xref_read_copy " + receipt.Cleanup);
                return;
            }
            File.Delete(file);
            _created.Remove(file);
            receipt.Cleanup = "deleted";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            receipt.Cleanup = "orphan: " + file + " (" + ex.GetType().Name + ": " + ex.Message + ")";
            _log?.Info("estimate.xref_read_copy " + receipt.Cleanup);
        }
    }

    /// <summary>Deletes only the copies this instance created, then its own run folder (never recursive). Failures are
    /// recorded in the ledger and the stage log ("orphan: …", "left: …"), never reported as deleted.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var receipt in _byLogical.Values.ToList())
            if (receipt.ReadSourcePath != null && _created.Contains(receipt.ReadSourcePath)) DeleteOwned(receipt.ReadSourcePath, receipt);
        if (_runRoot == null) return;
        try
        {
            CheckDirectoryPath(_runRoot, createMissing: false);
            Directory.Delete(_runRoot, recursive: false);
            Record.RunFolderCleanup = "deleted";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Record.RunFolderCleanup = "left: " + _runRoot + " (" + ex.GetType().Name + ": " + ex.Message + ")";
            _log?.Info("estimate.xref_read_copy run folder " + Record.RunFolderCleanup);
        }
    }
}
