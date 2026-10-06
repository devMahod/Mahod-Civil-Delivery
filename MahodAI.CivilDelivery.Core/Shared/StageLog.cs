using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Crash/hang-survivable progress log. Every entry is written AND flushed
    /// immediately, so when Civil 3D blocks inside an Autodesk API call the last
    /// line in the file names the exact stage that never returned.
    ///
    /// This exists because a `/b` script run that hangs produces no report at all:
    /// without a flushed stage trail there is no evidence about where it stopped.
    /// </summary>
    public sealed class StageLog : IDisposable
    {
        private readonly string _path;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _lock = new();
        private string? _openStage;
        private long _openStageStartedMs;

        public StageLog(string sessionName, string? directory = null)
        {
            var dir = directory ?? DefaultDirectory;
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, $"stage_{Sanitize(sessionName)}.log");
            Write($"=== {sessionName} @ {DateTime.UtcNow:O} (pid {Environment.ProcessId}) ===");
        }

        public static string DefaultDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MahodAI_Civil3D", "civil-delivery", "logs");

        public string Path_ => _path;

        /// <summary>
        /// Advisory progress feed for a UI that cannot animate while the host works on
        /// its thread (the palette's progress window subscribes while a stage runs).
        /// Invoked outside the log lock; observer failures never reach the caller.
        /// </summary>
        public static event Action<string, string?>? StageObserver;

        /// <summary>Records that a stage is STARTING — written before the risky call.</summary>
        public void Begin(string stage, string? detail = null)
        {
            lock (_lock)
            {
                _openStage = stage;
                _openStageStartedMs = _clock.ElapsedMilliseconds;
                Write($"BEGIN {stage}{(detail == null ? "" : " | " + detail)}");
            }
            var observer = StageObserver;
            if (observer == null) return;
            try { observer.Invoke(stage, detail); }
            catch { /* progress is advisory; the engineering operation must not fail on it */ }
        }

        /// <summary>Records that the previously begun stage returned.</summary>
        public void End(string stage, string? detail = null)
        {
            lock (_lock)
            {
                var ms = _clock.ElapsedMilliseconds - _openStageStartedMs;
                if (_openStage == stage) _openStage = null;
                Write($"END   {stage} ({ms} ms){(detail == null ? "" : " | " + detail)}");
            }
        }

        /// <summary>Begin/End around a delegate — the common case for one Civil API call.</summary>
        public T Step<T>(string stage, Func<T> action, string? detail = null)
        {
            Begin(stage, detail);
            try
            {
                var result = action();
                End(stage);
                return result;
            }
            catch (Exception ex)
            {
                Fail(stage, ex);
                throw;
            }
        }

        public void Step(string stage, Action action, string? detail = null)
        {
            Begin(stage, detail);
            try
            {
                action();
                End(stage);
            }
            catch (Exception ex)
            {
                Fail(stage, ex);
                throw;
            }
        }

        public void Info(string message)
        {
            lock (_lock) { Write($"INFO  {message}"); }
        }

        public void Fail(string stage, Exception ex)
        {
            lock (_lock)
            {
                Write($"FAIL  {stage} | {ex.GetType().Name}: {ex.Message}");
                Write($"      {ex.StackTrace?.Replace(Environment.NewLine, Environment.NewLine + "      ")}");
            }
        }

        /// <summary>The stage that was open when the process died — read by diagnostics.</summary>
        public string? OpenStage
        {
            get { lock (_lock) { return _openStage; } }
        }

        private void Write(string line)
        {
            var stamped = $"[{_clock.ElapsedMilliseconds,8} ms] {line}";
            try
            {
                // FileShare.ReadWrite so the log stays readable while Civil is hung on it.
                using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(stamped);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            catch
            {
                // Logging must never break the engineering operation.
            }
            Debug.WriteLine("[MahodCivilDelivery] " + stamped);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_openStage != null)
                    Write($"ABORT stage still open at dispose: {_openStage}");
                Write($"=== end @ {DateTime.UtcNow:O} ===");
            }
        }

        private static string Sanitize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
            return sb.ToString();
        }
    }

    internal static class CharArrayExtensions
    {
        public static bool Contains(this char[] array, char value)
        {
            foreach (var c in array) if (c == value) return true;
            return false;
        }
    }
}
