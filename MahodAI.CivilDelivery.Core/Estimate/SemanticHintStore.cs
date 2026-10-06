using System.Text.Json;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>
/// Local retrieval descriptions, not project decisions. This store never receives
/// a mutable profile/scan and never rebases or republishes quantity evidence.
/// A small per-group file replaces itself atomically; source changes invalidate
/// reuse through the full Hint.Source scope, without accumulating scan history.
/// </summary>
public sealed class SemanticHintStore
{
    public sealed record Snapshot(SemanticHintPolicy.Hint? Hint, string? FileHash);
    private sealed record Envelope(int SchemaVersion, SemanticHintPolicy.Hint Hint);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _root;
    private readonly Action<string, string, string> _replaceFile;
    private const long MaxFileBytes = 64 * 1024;

    public SemanticHintStore(string? root = null) : this(root, (pending, path, backup) => File.Replace(pending, path, backup)) { }

    // Narrow seam for reproducing documented partial ReplaceFile failures in
    // tests. Production always uses the OS replacement operation above.
    internal SemanticHintStore(string? root, Action<string, string, string> replaceFile)
    {
        _replaceFile = replaceFile ?? throw new ArgumentNullException(nameof(replaceFile));
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MahodAI_Civil3D", "civil-delivery", "semantic-hints");
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("An absolute local hint directory is required.", nameof(root));
        _root = Path.GetFullPath(root);
    }

    public Snapshot Read(string profileId, string drawingPath, string ruleKey)
    {
        var path = FilePath(profileId, drawingPath, ruleKey);
        if (!File.Exists(path)) return new(null, null);
        if (new FileInfo(path).Length > MaxFileBytes)
            throw new InvalidDataException("קובץ משמעות הקבוצה גדול מהצפוי; לא נעשה בו שימוש.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength > MaxFileBytes)
            throw new InvalidDataException("קובץ משמעות הקבוצה גדול מהצפוי; לא נעשה בו שימוש.");
        Envelope? stored;
        try { stored = JsonSerializer.Deserialize<Envelope>(bytes, JsonOptions); }
        catch (JsonException error)
        { throw new InvalidDataException("קובץ משמעות הקבוצה אינו קריא; לא נעשה בו שימוש.", error); }
        if (stored?.SchemaVersion != 1 || !SemanticHintPolicy.IsValid(stored.Hint) ||
            stored.Hint.Source!.ProfileId != profileId || stored.Hint.Source.RuleKey != ruleKey ||
            !string.Equals(Path.GetFullPath(stored.Hint.Source.DrawingPath), Path.GetFullPath(drawingPath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("קובץ משמעות הקבוצה אינו שייך למקור שנפתח; לא נעשה בו שימוש.");
        return new(stored.Hint, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
    }

    /// <summary>
    /// expectedFileHash is the snapshot read when review opened, or null for an
    /// absent file. Never capture it here to accept a concurrent description edit.
    /// Callers additionally prove current profile/scan/source freshness before save.
    /// </summary>
    public Snapshot Save(SemanticHintPolicy.Scope scope, SemanticHintPolicy.Draft input,
        string recordedBy, string? expectedFileHash)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!SemanticHintPolicy.TryContext(input, out var context, out var error)) throw new InvalidOperationException(error);
        if (context.Length == 0) throw new InvalidOperationException("יש לבחור משמעות או להזין תיאור לפני שמירה.");
        var hint = new SemanticHintPolicy.Hint
        {
            Source = scope,
            Input = input with { Description = input.Description.Trim() },
            RecordedBy = recordedBy?.Trim() ?? "",
            RecordedAtUtc = DateTime.UtcNow
        };
        if (!SemanticHintPolicy.IsValid(hint))
            throw new InvalidOperationException("נדרשים מקור תקף ושם הבודק בשורה אחת.");
        if (expectedFileHash != null && !CatalogIdentity.IsValidSha256(expectedFileHash))
            throw new InvalidOperationException("זהות משמעות הקבוצה שנפתחה אינה תקפה; יש לפתוח את הבדיקה מחדש.");
        var path = FilePath(scope.ProfileId, scope.DrawingPath, scope.RuleKey);
        Directory.CreateDirectory(_root);
        // Same-key writers in separate Civil processes cannot both consume the
        // same baseline. Lock contention fails promptly instead of freezing UI.
        using var publicationLock = AcquireLock(path + ".lock");
        var before = Read(scope.ProfileId, scope.DrawingPath, scope.RuleKey);
        if (!string.Equals(before.FileHash, expectedFileHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("משמעות הקבוצה השתנתה בחלון אחר; פתחו את הבדיקה מחדש. לא נדרס תיאור.");
        if (before.Hint is { } existing && existing.Source == scope && existing.Input == hint.Input &&
            existing.RecordedBy == hint.RecordedBy)
            return before;
        var text = JsonSerializer.Serialize(new Envelope(1, hint), JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxFileBytes)
            throw new InvalidOperationException("פרטי משמעות הקבוצה גדולים מהצפוי; יש לקצר את התיאור או פרטי הבודק.");
        PublishAtomically(path, text, expectedFileHash);
        return new(hint, ArtifactHash.Sha256OfText(text));
    }

    private void PublishAtomically(string path, string text, string? expectedFileHash)
    {
        var publicationId = Guid.NewGuid().ToString("N");
        var pending = path + ".tmp-" + publicationId;
        var backup = path + ".previous-" + publicationId;
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            // A sidecar is repeatedly replaced in place. Windows Move(overwrite)
            // produced reproducible E_ACCESSDENIED during that lifecycle. Use its
            // dedicated same-volume atomic replacement primitive for existing files;
            // initial creation must not overwrite a newly appeared destination.
            if (expectedFileHash == null) File.Move(pending, path);
            else
            {
                for (var attempt = 0; ; attempt++)
                {
                    try { _replaceFile(pending, path, backup); break; }
                    catch (IOException error) when (error.HResult == unchecked((int)0x80070497) && attempt < 2)
                    {
                        // ERROR_UNABLE_TO_REMOVE_REPLACED leaves both filenames
                        // unchanged. The 1,031-edit Windows probe reproduced it,
                        // then succeeded with the same original CAS on immediate
                        // retry. Retry only that observed error, for at most 75ms,
                        // retaining the key lock and proving the old bytes again.
                        System.Threading.Thread.Sleep(25 * (attempt + 1));
                        if (File.Exists(backup) || !File.Exists(pending) || !File.Exists(path) || new FileInfo(path).Length > MaxFileBytes ||
                            !string.Equals(ArtifactHash.Sha256OfFile(path), expectedFileHash, StringComparison.OrdinalIgnoreCase))
                            throw;
                    }
                }
            }
            // Only the exact backup generated for this successful publication is
            // removed; never sweep historical or unrelated sibling files.
            if (File.Exists(backup)) File.Delete(backup);
        }
        catch (Exception publicationError) when (publicationError is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(backup))
            {
                try
                {
                    if (expectedFileHash == null || new FileInfo(backup).Length > MaxFileBytes ||
                        !string.Equals(ArtifactHash.Sha256OfFile(backup), expectedFileHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The recovery file does not match the original hint snapshot.");
                    // A documented partial replacement can leave the old file at
                    // the backup path. Restore only to an absent destination;
                    // File.Move without overwrite also protects against a race.
                    if (!File.Exists(path)) File.Move(backup, path);
                    else if (string.Equals(ArtifactHash.Sha256OfFile(path), expectedFileHash, StringComparison.OrdinalIgnoreCase))
                        File.Delete(backup);
                    else throw new IOException("The hint destination changed; it was not overwritten during recovery.");
                }
                catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // Keep the only original-byte recovery artifact when restoring
                    // is unsafe or fails. The caller must not publish success.
                    throw new IOException($"שמירת המשמעות לא הושלמה. היעד לא נדרס: {path}. קובץ שחזור נשמר: {backup}",
                        new AggregateException(publicationError, recoveryError));
                }
            }
            throw;
        }
        finally
        {
            try { if (File.Exists(pending)) File.Delete(pending); } catch { }
        }
    }

    private string FilePath(string profileId, string drawingPath, string ruleKey)
    {
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(ruleKey) ||
            !Path.IsPathFullyQualified(drawingPath))
            throw new ArgumentException("A project, absolute host drawing path and exact rule key are required.");
        var identity = JsonSerializer.Serialize(new
        {
            ProfileId = profileId,
            DrawingPath = Path.GetFullPath(drawingPath).ToUpperInvariant(),
            RuleKey = ruleKey
        });
        return Path.Combine(_root, ArtifactHash.Sha256OfText(identity) + ".json");
    }

    private static FileStream AcquireLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error)
        { throw new InvalidOperationException("לא ניתן לנעול את קובץ המשמעות כרגע; ייתכן שהוא נשמר בחלון אחר. נסו שוב.", error); }
    }
}
