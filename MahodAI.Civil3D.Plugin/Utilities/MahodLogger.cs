using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace MahodAI.Civil3D.Plugin.Utilities;

/// <summary>
/// File-based structured logger for production diagnostics.
/// Logs to %LOCALAPPDATA%\MahodAI_Civil3D\logs\mahod_ai.log.
/// Debug levels: 1=Trace, 2=Debug, 3=Info, 4=Warning, 5=Error.
/// </summary>
public static class MahodLogger
{
    private static readonly string LogDir;
    private static readonly string LogFile;
    private static readonly object Lock = new();
    private static int _minLevel = 3; // Default: Info and above

    static MahodLogger()
    {
        LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MahodAI_Civil3D", "logs");
        LogFile = Path.Combine(LogDir, LogName + ".log");
    }

    // The separate Mahod Civil Delivery plugin runs in the same Civil 3D process as MahodAI. Each assembly has its
    // own lock and its own rotation, so the two must never append to (or rotate) the same file.
#if MAHOD_CD_STANDALONE
    private const string LogName = "mahod_civil_delivery";
#else
    private const string LogName = "mahod_ai";
#endif

    /// <summary>Set minimum log level (1=Trace, 2=Debug, 3=Info, 4=Warning, 5=Error).</summary>
    public static int MinLevel
    {
        get => _minLevel;
        set => _minLevel = Math.Clamp(value, 1, 5);
    }

    public static void Trace(string message, [CallerMemberName] string? caller = null)
        => Write(1, "TRC", message, caller);

    public static void Debug(string message, [CallerMemberName] string? caller = null)
        => Write(2, "DBG", message, caller);

    public static void Info(string message, [CallerMemberName] string? caller = null)
        => Write(3, "INF", message, caller);

    public static void Warning(string message, [CallerMemberName] string? caller = null)
        => Write(4, "WRN", message, caller);

    public static void Error(string message, Exception? ex = null, [CallerMemberName] string? caller = null)
    {
        var msg = ex != null ? $"{message} | {ex.GetType().Name}: {ex.Message}" : message;
        Write(5, "ERR", msg, caller);
    }

    public static void ToolExecution(string toolName, bool success, long elapsedMs, string? error = null)
    {
        var status = success ? "OK" : "FAIL";
        var msg = error != null
            ? $"Tool={toolName} Status={status} Elapsed={elapsedMs}ms Error={error}"
            : $"Tool={toolName} Status={status} Elapsed={elapsedMs}ms";
        Write(3, "TOOL", msg, null);
    }

    private static void Write(int level, string tag, string message, string? caller)
    {
        if (level < _minLevel) return;

        try
        {
            Directory.CreateDirectory(LogDir);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var callerPart = caller != null ? $" [{caller}]" : "";
            var line = $"{timestamp} {tag}{callerPart} {message}{Environment.NewLine}";

            lock (Lock)
            {
                File.AppendAllText(LogFile, line);
            }

            // Rotate log if > 10MB
            var fi = new FileInfo(LogFile);
            if (fi.Exists && fi.Length > 10 * 1024 * 1024)
            {
                RotateLog();
            }
        }
        catch
        {
            // Never throw from logger
            System.Diagnostics.Debug.WriteLine($"[MahodLogger] Failed to write: {message}");
        }
    }

    private static void RotateLog()
    {
        try
        {
            var rotated = Path.Combine(LogDir, $"{LogName}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            lock (Lock)
            {
                if (File.Exists(LogFile))
                {
                    File.Move(LogFile, rotated);
                }
            }

            // Keep only last 5 rotated logs
            var files = Directory.GetFiles(LogDir, LogName + "_*.log");
            if (files.Length > 5)
            {
                Array.Sort(files);
                for (int i = 0; i < files.Length - 5; i++)
                {
                    File.Delete(files[i]);
                }
            }
        }
        catch
        {
            // Never throw from logger
        }
    }
}
