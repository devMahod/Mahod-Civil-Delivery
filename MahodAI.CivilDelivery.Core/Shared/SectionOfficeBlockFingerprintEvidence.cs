using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Diagnostic preimages, never an acceptance artifact. Signatures are copied from
/// the actual hash read, not obtained by a second geometry inspection. Handles and
/// context are diagnostic only and never participate in the protected hash.
/// </summary>
public sealed class SectionOfficeBlockFingerprintEvidence
{
    public string Schema { get; init; } = "office-block-fingerprint-evidence-v1";
    public string FingerprintVersion { get; init; } = BlockDefinitionFingerprintLogic.Version;
    public string EntityFingerprintVersion { get; init; } = BlockDefinitionFingerprintLogic.EntityVersion;
    public string OperationId { get; init; } = string.Empty;
    public string? RunId { get; init; }
    public string HostDrawing { get; init; } = string.Empty;
    public string LoadedAssembly { get; init; } = string.Empty;
    public string LoadedModuleVersionId { get; init; } = string.Empty;
    public string? StageLogPath { get; init; }
    public int ProcessId { get; init; } = Environment.ProcessId;
    public DateTimeOffset WrittenUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Asset { get; init; } = string.Empty;
    public string ResourceName { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public string ProtectedBlockName { get; init; } = string.Empty;
    public string Phase { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string? Error { get; init; }
    public IReadOnlyList<Snapshot> Snapshots { get; init; } = Array.Empty<Snapshot>();

    public sealed record EntitySignature(string Handle, string RuntimeType, string Signature);

    public sealed class Snapshot
    {
        public DateTimeOffset ReadStartedUtc { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? ReadCompletedUtc { get; set; }
        public string Stage { get; init; } = string.Empty;
        public string HashKind { get; set; } = string.Empty;
        public string? Fingerprint { get; set; }
        public string? BlockHandle { get; set; }
        public string? BlockName { get; set; }
        public string? Comments { get; set; }
        public string? DatabaseFilename { get; set; }
        public string? WorkingDatabaseFilename { get; set; }
        public bool? IsWorkingDatabase { get; set; }
        // For entity-only reads this is supplemental metadata, NOT hash input.
        public BlockDefinitionFingerprintLogic.Header? Header { get; set; }
        public List<EntitySignature> Entities { get; } = new();
        public List<string> DiagnosticErrors { get; } = new();
    }
}

/// <summary>
/// A per-operation, best-effort sidecar sink. A failed attempt is also deduplicated:
/// lack of disk access must neither affect a decision nor flood the normal log.
/// </summary>
public sealed class SectionOfficeBlockFingerprintEvidenceWriter
{
    private readonly string _directory;
    private readonly Action<string>? _diagnostic;
    private readonly Action<string, string> _write;
    private readonly HashSet<string> _attempted = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SectionOfficeBlockFingerprintEvidenceWriter(
        string directory, Action<string>? diagnostic = null,
        Action<string, string>? write = null)
    {
        _directory = directory;
        _diagnostic = diagnostic;
        _write = write ?? WriteNew;
    }

    public void WriteOnce(SectionOfficeBlockFingerprintEvidence evidence)
    {
        try
        {
            var key = evidence.OperationId + "|" + evidence.SourceSha256 + "|" + evidence.Phase;
            if (!_attempted.Add(key)) return;
            var path = Path.Combine(_directory,
                $"office-block-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json");
            _write(path, JsonSerializer.Serialize(evidence, JsonOptions));
            Report($"office_block.fingerprint_evidence operation={evidence.OperationId} " +
                   $"asset={evidence.Asset} phase={evidence.Phase} outcome={evidence.Outcome} path={path}");
        }
        catch (Exception ex)
        {
            Report($"office_block.fingerprint_evidence_unavailable operation={evidence.OperationId} " +
                   $"asset={evidence.Asset} phase={evidence.Phase} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Report(string message)
    {
        try { _diagnostic?.Invoke(message); }
        catch { /* Diagnostic logging has no authority over engineering decisions. */ }
    }

    private static void WriteNew(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var bytes = Encoding.UTF8.GetBytes(json);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }
}
