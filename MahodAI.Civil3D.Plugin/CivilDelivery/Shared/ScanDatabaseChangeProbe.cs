using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Shared;

/// <summary>
/// Opt-in, read-only diagnostic for "a scan sets DBMOD" (landscape 293, 02/10; Codex 01:40 / 01:42). It exists only
/// inside an <see cref="EstimateScanTrace"/> (MAHOD_SCAN_DIAGNOSTICS=1) and is otherwise never attached.
/// The database callbacks only append event / trace stage / handle / class to a bounded buffer: they open no object and
/// read no geometry, system variable or file. Only the listeners that attached are tracked; a failed attach rolls back
/// the ones already added. Dispose stops buffering, removes each of its own listeners separately (never those of
/// DrawingRevisionTracker), then writes the buffer, the dropped count, the capture failures and any detach failure — a
/// listener that could not be removed is reported, never presented as a clean detach.
/// DBMOD is sampled at operation boundaries on the original document, and only while it is the active document.
/// Nothing here writes to the drawing, resets DBMOD, saves or closes.
/// </summary>
internal sealed class ScanDatabaseChangeProbe : IDisposable
{
    private const int Capacity = 4096;
    private readonly Database _db;
    private readonly List<(string Event, string Stage, string Handle, string Class)> _events = new();
    private int _dropped;
    private int _captureFailed;
    private bool _disposed;
    private bool _modifiedAttached;
    private bool _appendedAttached;
    private bool _erasedAttached;

    private ScanDatabaseChangeProbe(Database db)
    {
        _db = db;
        try
        {
            _db.ObjectModified += OnModified;
            _modifiedAttached = true;
            _db.ObjectAppended += OnAppended;
            _appendedAttached = true;
            _db.ObjectErased += OnErased;
            _erasedAttached = true;
        }
        catch
        {
            _disposed = true;
            // Codex 02:38: a rollback that cannot remove a listener is reported (outside any callback) before the rethrow.
            var rollbackFailures = Detach();
            if (rollbackFailures.Count > 0)
                EstimateScanTrace.Mark("probe.attach.rollback_failed", rollbackFailures.Count, null, string.Join(",", rollbackFailures));
            throw;
        }
    }

    /// <summary>Attaches to the document's database while a scan trace is active; null otherwise or when attaching fails.</summary>
    internal static ScanDatabaseChangeProbe? Attach(Document? doc)
    {
        if (!EstimateScanTrace.Active || doc == null) return null;
        SampleDbmod(doc, "probe.attach");
        try
        {
            return new ScanDatabaseChangeProbe(doc.Database);
        }
        catch (Exception ex)
        {
            // A diagnostic never stops the scan; the trace says the probe is absent and why.
            EstimateScanTrace.Mark("probe.attach.fail", null, null, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Removes each of this probe's own listeners separately; returns the ones that could not be removed.</summary>
    private List<string> Detach()
    {
        var failures = new List<string>();
        if (_modifiedAttached)
        {
            try { _db.ObjectModified -= OnModified; _modifiedAttached = false; }
            catch (Exception ex) { failures.Add("modified:" + ex.GetType().Name); }
        }
        if (_appendedAttached)
        {
            try { _db.ObjectAppended -= OnAppended; _appendedAttached = false; }
            catch (Exception ex) { failures.Add("appended:" + ex.GetType().Name); }
        }
        if (_erasedAttached)
        {
            try { _db.ObjectErased -= OnErased; _erasedAttached = false; }
            catch (Exception ex) { failures.Add("erased:" + ex.GetType().Name); }
        }
        return failures;
    }

    /// <summary>Writes DBMOD of <paramref name="doc"/> to the trace at a boundary; skipped when it is not the active document.</summary>
    internal static void SampleDbmod(Document? doc, string boundary)
    {
        if (!EstimateScanTrace.Active || doc == null) return;
        if (!ReferenceEquals(AcadApp.DocumentManager.MdiActiveDocument, doc))
        {
            EstimateScanTrace.Mark("dbmod." + boundary + ".not-active");
            return;
        }
        try
        {
            EstimateScanTrace.Mark("dbmod." + boundary, Convert.ToInt32(AcadApp.GetSystemVariable("DBMOD"), CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            EstimateScanTrace.Mark("dbmod." + boundary + ".fail", null, null, ex.GetType().Name);
        }
    }

    // A listener that could not be removed returns before touching the event's object (Codex 02:38).
    private void OnModified(object sender, ObjectEventArgs e)
    {
        if (_disposed) return;
        Capture("modified", e.DBObject);
    }

    private void OnAppended(object sender, ObjectEventArgs e)
    {
        if (_disposed) return;
        Capture("appended", e.DBObject);
    }

    private void OnErased(object sender, ObjectErasedEventArgs e)
    {
        if (_disposed) return;
        Capture(e.Erased ? "erased" : "unerased", e.DBObject);
    }

    private void Capture(string kind, DBObject? obj)
    {
        if (_disposed) return;
        if (_events.Count >= Capacity) { _dropped++; return; }
        try
        {
            _events.Add((kind, EstimateScanTrace.CurrentStage ?? "?", obj?.Handle.ToString() ?? "?", obj?.GetRXClass()?.Name ?? "?"));
        }
        catch
        {
            _captureFailed++;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; // callbacks stop buffering first, even if a listener cannot be removed below
        var detachFailures = Detach();
        foreach (var (kind, stage, handle, cls) in _events)
            EstimateScanTrace.Mark("db." + kind + "@" + stage, null, handle, cls);
        EstimateScanTrace.Mark("db.events", _events.Count);
        EstimateScanTrace.Mark("db.dropped", _dropped);
        EstimateScanTrace.Mark("db.capture_failed", _captureFailed);
        EstimateScanTrace.Mark(detachFailures.Count == 0 ? "db.detached" : "db.detach_failed", detachFailures.Count, null,
            detachFailures.Count == 0 ? null : string.Join(",", detachFailures));
    }
}
