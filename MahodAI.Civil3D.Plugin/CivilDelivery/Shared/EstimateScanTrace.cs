using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Shared;

/// <summary>
/// Opt-in diagnostic journal; no Autodesk objects, threads, timers or cancellation changes.
/// A missing END locates an unfinished boundary, not its cause. Past the record limit only
/// per-object marks (those with a handle) stop, after one explicit marker: stage boundaries
/// and the per-XREF boundaries (xref.*, one set per external reference) keep their evidence,
/// so a late stall (XREF load, legends, completion, publication) stays locatable, while an
/// open per-object mark after the marker proves nothing. Diagnostic IO errors propagate;
/// this is not silent telemetry.
/// </summary>
internal sealed class EstimateScanTrace : IDisposable
{
    [ThreadStatic] private static EstimateScanTrace? _current;
    private readonly TextWriter _writer;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private readonly string _route;
    private readonly int _limit;
    private int _sequence;
    private bool _limited;
    private bool _disposed;
    private string? _lastStage;

    /// <summary>True while an opt-in trace runs on this thread.</summary>
    internal static bool Active => _current != null;

    /// <summary>The last stage marked on this thread's trace (for ScanDatabaseChangeProbe); null without a trace.</summary>
    internal static string? CurrentStage => _current?._lastStage;

    private EstimateScanTrace(string route, string directory, int limit)
    {
        _route = Clip(route, 32)!;
        _limit = Math.Clamp(limit, 2, 131_072);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "scan_" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "_" + _runId + ".jsonl");
        _writer = OpenWriter(path);
    }

    /// <summary>Opens the journal; a test replaces it with a writer that fails (Codex 18:24), nothing else does.</summary>
    internal static Func<string, TextWriter> OpenWriter { get; set; } = path =>
        new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 4096, FileOptions.SequentialScan), new UTF8Encoding(false)) { AutoFlush = true };

    internal static IDisposable? Start(string route, string? directory = null, int recordLimit = 131_072)
    {
        if (_current != null) return null; // nested shared workflow retains the originating route
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MahodAI_Civil3D", "civil-delivery", "logs", "scan-diagnostics");
        // b26: besides the environment switch, a file named ENABLED in the journal folder turns tracing on — Civil started
        // from the taskbar does not see a variable set later, and the user environment is not changed for a diagnostic.
        if (!IsEnabled(directory)) return null;
        var trace = new EstimateScanTrace(route, directory, recordLimit);
        _current = trace;
        try
        {
            trace.Write("route.begin", null, null, null);
        }
        catch
        {
            // Codex 18:24: a trace whose first line could not be written is not left active without a disposer — the
            // thread is clear again, the writer is closed, and the original exception is the one that propagates.
            _current = null;
            trace._disposed = true;
            try { trace._writer.Dispose(); } catch { /* the original failure is the one reported */ }
            throw;
        }
        return trace;
    }

    /// <summary>The opt-in: MAHOD_SCAN_DIAGNOSTICS=1, or an ENABLED file in the journal folder.</summary>
    internal static bool IsEnabled(string directory) =>
        Environment.GetEnvironmentVariable("MAHOD_SCAN_DIAGNOSTICS") == "1" || File.Exists(Path.Combine(directory, "ENABLED"));

    internal static void Mark(string stage, int? count = null, string? handle = null, string? type = null) =>
        _current?.Write(stage, count, handle, type);

    internal static T Step<T>(string stage, Func<T> action, int? count = null, string? handle = null, string? type = null)
    {
        if (_current == null) return action();
        Mark(stage + ".begin", count, handle, type);
        try
        {
            var result = action();
            Mark(stage + ".end", count, handle, type);
            return result;
        }
        catch (Exception ex)
        {
            Mark(stage + ".fail", count, handle, ex.GetType().Name);
            throw;
        }
    }

    internal static void Step(string stage, Action action, int? count = null, string? handle = null, string? type = null) =>
        Step(stage, () => { action(); return true; }, count, handle, type);

    private void Write(string stage, int? count, string? handle, string? type)
    {
        _lastStage = stage;
        if (_sequence >= _limit)
        {
            if (!_limited)
            {
                _limited = true;
                WriteLine("trace.limit_reached_object_marks_stopped", _limit, null, null);
            }
            if (handle != null && !stage.StartsWith("xref.", StringComparison.Ordinal)) return;
        }
        WriteLine(stage, count, handle, type);
    }

    private void WriteLine(string stage, int? count, string? handle, string? type)
    {
        _writer.WriteLine(JsonSerializer.Serialize(new
        {
            run_id = _runId, route = _route, sequence = ++_sequence,
            stage = Clip(stage, 96), elapsed_ms = _elapsed.ElapsedMilliseconds,
            count, handle = Clip(handle, 192), type = Clip(type, 96)
        }));
    }

    private static string? Clip(string? value, int length) => value is { Length: var n } && n > length ? value[..length] : value;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _current = null;
        try { Write("route.dispose_not_success", null, null, null); }
        finally { _writer.Dispose(); }
    }
}
