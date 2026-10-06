using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// A database opened from a DWT can retain that template's Filename while its
/// Document is still Drawing1.dwg. A readable template is not saved drawing evidence.
/// DWGTITLED is the native read-only named-drawing flag (0/1); only an active-document
/// caller may supply it. File existence/hash and DBMOD remain separate mandatory gates.
/// </summary>
public static class SavedDrawingPathPolicy
{
    public sealed class Identity
    {
        internal Identity(string path, string name, bool needsSaveAs, string? failure)
        { DrawingPath = path; DocumentName = name; NeedsSaveAs = needsSaveAs; Failure = failure; }

        public string DrawingPath { get; }
        public string DocumentName { get; }
        public bool NeedsSaveAs { get; }
        public string? Failure { get; }
        public bool IsSaved => !NeedsSaveAs && Failure == null && DrawingPath.Length > 0;

        /// <summary>The production hash callback cannot run for an unnamed/template/invalid identity.</summary>
        public string ReadSavedHash(Func<string, string> readHash)
        {
            ArgumentNullException.ThrowIfNull(readHash);
            if (!IsSaved) throw new InvalidOperationException(Failure ?? "Save the active document as a DWG first.");
            return readHash(DrawingPath);
        }
    }

    // Session-only seeds: never look up a shared DWT-derived or Drawing1-derived
    // persisted profile. After SaveAs, the normal saved-path selector is used again.
    private sealed class SessionIdentity { internal string Id { get; } = "drawing-unsaved-" + Guid.NewGuid().ToString("N"); }
    private static readonly ConditionalWeakTable<object, SessionIdentity> Sessions = new();

    public static string UnsavedProfileId(object document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Sessions.GetValue(document, _ => new SessionIdentity()).Id;
    }

    public static Identity Refused(string reason) =>
        new("", "", false, string.IsNullOrWhiteSpace(reason) ? "Saved drawing identity is unavailable." : reason);

    /// <summary>Read named-state first; an untitled document never reads its database's template filename.</summary>
    public static Identity Capture(Func<int> readTitled, Func<string?> readDocumentName,
        Func<string?> readDatabaseFilename)
    {
        ArgumentNullException.ThrowIfNull(readTitled);
        ArgumentNullException.ThrowIfNull(readDocumentName);
        ArgumentNullException.ThrowIfNull(readDatabaseFilename);
        try
        {
            var titled = readTitled();
            if (titled is not (0 or 1)) return Refused("DWGTITLED was neither 0 nor 1.");
            var documentName = readDocumentName();
            if (titled == 0) return new Identity("", documentName ?? "", true, null);
            return Evaluate(titled, readDatabaseFilename(), documentName);
        }
        catch (Exception ex) { return Refused("Saved drawing identity could not be read: " + ex.Message); }
    }

    public static Identity Evaluate(int? titled, string? databaseFilename, string? documentName)
    {
        if (titled == 0) return new Identity("", documentName ?? "", true, null);
        if (titled != 1) return Refused("DWGTITLED is unavailable or invalid.");
        try
        {
            if (string.Equals(Path.GetExtension(databaseFilename), ".dwt", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetExtension(documentName), ".dwt", StringComparison.OrdinalIgnoreCase))
                return new Identity("", documentName ?? "", true, null);
            if (string.IsNullOrWhiteSpace(databaseFilename) || string.IsNullOrWhiteSpace(documentName) ||
                !Path.IsPathFullyQualified(databaseFilename) || !Path.IsPathFullyQualified(documentName) ||
                !string.Equals(Path.GetExtension(databaseFilename), ".dwg", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(documentName), ".dwg", StringComparison.OrdinalIgnoreCase))
                return Refused("The named active document and database must both identify an absolute DWG path.");
            var databasePath = Path.GetFullPath(databaseFilename);
            var documentPath = Path.GetFullPath(documentName);
            if (!string.Equals(databasePath, documentPath, StringComparison.OrdinalIgnoreCase))
                return Refused("The active document path does not match its database filename.");
            return new Identity(documentPath, documentName, false, null);
        }
        catch (Exception ex) { return Refused("The saved DWG path could not be resolved: " + ex.Message); }
    }
}
