using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.CivilDelivery.Shared;
using Snapshot = MahodAI.CivilDelivery.Shared.SectionOfficeBlockFingerprintEvidence.Snapshot;
using Trace = System.Diagnostics.Trace;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

/// <summary>
/// Explicit operation-local diagnostics. No ambient state, database writes, native
/// re-sampling, acceptance changes, or authoritative run-manifest publication.
/// </summary>
internal sealed class SectionOfficeBlockFingerprintDiagnostics
{
    private readonly SectionOfficeBlockFingerprintEvidenceWriter _writer;
    private readonly string _operationId = "office-block-" + Guid.NewGuid().ToString("N");
    private readonly string? _runId;
    private readonly string _hostDrawing;
    private readonly string? _logPath;

    private SectionOfficeBlockFingerprintDiagnostics(Database db, string? runId, StageLog? log)
    {
        _runId = runId;
        _hostDrawing = db.Filename;
        // VERIFY has no StageLog argument. Its advisory file is separate from the
        // authoritative VERIFY result, and uses the existing logging location.
        log ??= new StageLog("office_block_fingerprint_diagnostics");
        _logPath = log.Path_;
        _writer = new SectionOfficeBlockFingerprintEvidenceWriter(
            Path.Combine(StageLog.DefaultDirectory, "office-block-fingerprints"), log.Info);
    }

    internal static SectionOfficeBlockFingerprintDiagnostics? TryCreate(
        Database db, string? runId = null, StageLog? log = null)
    {
        try { return new SectionOfficeBlockFingerprintDiagnostics(db, runId, log); }
        catch (Exception ex)
        {
            var message = "office_block.fingerprint_evidence_unavailable initialization: " + ex.Message;
            try { if (log != null) log.Info(message); else Trace.TraceWarning(message); }
            catch { }
            return null;
        }
    }

    internal static Snapshot? Begin(List<Snapshot>? snapshots, string stage)
    {
        if (snapshots == null) return null;
        var snapshot = new Snapshot { Stage = stage };
        snapshots.Add(snapshot);
        return snapshot;
    }

    // Only these diagnostic-only metadata getters are best-effort. Exceptions
    // from production GeometrySignature/Header/Compose are NOT caught here.
    internal static void Metadata(Snapshot? snapshot, BlockTableRecord block,
        bool includeSupplementalHeader = false)
    {
        if (snapshot == null) return;
        Read("definition identity", () =>
        {
            snapshot.BlockHandle = block.Handle.ToString();
            snapshot.BlockName = block.Name;
            snapshot.Comments = block.Comments;
            snapshot.DatabaseFilename = block.Database.Filename;
        });
        Read("working database", () =>
        {
            var working = HostApplicationServices.WorkingDatabase;
            snapshot.WorkingDatabaseFilename = working?.Filename;
            snapshot.IsWorkingDatabase = ReferenceEquals(block.Database, working);
        });
        if (includeSupplementalHeader)
            Read("supplemental non-hashed header", () => snapshot.Header =
                new BlockDefinitionFingerprintLogic.Header(
                    block.Origin.X, block.Origin.Y, block.Origin.Z,
                    block.Units.ToString(), block.BlockScaling.ToString(), block.Explodable,
                    block.Annotative.ToString(), block.PaperOrientation.ToString()));
        void Read(string name, Action read)
        {
            try { read(); }
            catch (Exception ex) { snapshot.DiagnosticErrors.Add(name + ": " + ex.Message); }
        }
    }

    internal static void Entity(Snapshot? snapshot, Autodesk.AutoCAD.DatabaseServices.Entity entity,
        string signature)
    {
        if (snapshot == null) return;
        var handle = string.Empty;
        try { handle = entity.Handle.ToString(); }
        catch (Exception ex) { snapshot.DiagnosticErrors.Add("entity handle: " + ex.Message); }
        snapshot.Entities.Add(new SectionOfficeBlockFingerprintEvidence.EntitySignature(
            handle, entity.GetType().FullName ?? entity.GetType().Name, signature));
    }

    internal void Write(SectionFurnitureLogic.OfficeCarView view, string resource,
        string phase, string outcome, IReadOnlyList<Snapshot> snapshots, Exception? error = null)
    {
        // Envelope construction is diagnostic too. An unavailable assembly identity
        // must not replace or swallow the original native validation failure.
        try
        {
            var assembly = typeof(SectionVehicleBlockService).Assembly;
            _writer.WriteOnce(new SectionOfficeBlockFingerprintEvidence
            {
                OperationId = _operationId, RunId = _runId, HostDrawing = _hostDrawing,
                StageLogPath = _logPath, LoadedAssembly = assembly.FullName ?? string.Empty,
                LoadedModuleVersionId = assembly.ManifestModule.ModuleVersionId.ToString(),
                Asset = view.ToString(), ResourceName = resource,
                SourceSha256 = SectionOfficeVehicleAssetEvidenceLogic.SourceSha256(view),
                ProtectedBlockName = SectionOfficeVehicleAssetEvidenceLogic.ProtectedBlockName(view),
                Phase = phase, Outcome = outcome, Error = error?.ToString(), Snapshots = snapshots,
            });
        }
        catch (Exception ex)
        {
            try { Trace.TraceWarning("office_block.fingerprint_evidence_unavailable: " + ex.Message); }
            catch { }
        }
    }
}
