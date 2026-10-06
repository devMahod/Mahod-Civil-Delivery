using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using MahodAI.Civil3D.Plugin.SelfTest;
using MahodAI.Civil3D.Plugin.Tools;
using MahodAI.Civil3D.Plugin.Utilities;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
// Disambiguate from Autodesk.AutoCAD.Runtime.Exception (imported for CommandMethod/CommandClass).
using Exception = System.Exception;

// Additive command-class registration. MahodCommands.cs declares its own
// [assembly: CommandClass]; multiple attributes are allowed and additive, so
// this spike command is discovered WITHOUT touching MahodCommands.cs.
[assembly: CommandClass(typeof(SelfTestSpikeCommand))]

namespace MahodAI.Civil3D.Plugin.SelfTest
{
    /// <summary>
    /// De-risk spike for the automated self-test harness (AUTOTEST_RESEARCH.md §3.4).
    ///
    /// It answers the ONE load-bearing, statically-unverifiable question behind the
    /// whole "acad.exe /b + MAHOD_SELFTEST" design: while a [CommandMethod] is running
    /// on AutoCAD's main thread and pumping the message queue via
    /// System.Windows.Forms.Application.DoEvents(), does Autodesk.AutoCAD...Application.Idle
    /// still fire so ToolExecutor.ExecuteOnMainThreadAsync can marshal a tool onto the
    /// main thread? If yes, the harness pattern (scenario on a ThreadPool thread, main
    /// thread pumping until a terminal event) is viable. If no, every agent tool_call
    /// would deadlock to timeout and the approach must change.
    ///
    /// Two independent probes, no agent / no WebSocket / no network required:
    ///   1. Raw Idle probe   — register a one-shot Application.Idle handler, then pump;
    ///                          does it fire, and on the main thread?
    ///   2. ToolExecutor path — run the read-only "list_layers" tool through the REAL
    ///                          ToolExecutor from a Task.Run thread while pumping; does
    ///                          it complete successfully (proving the production
    ///                          marshaling path works under the pump)?
    ///
    /// Writes %LOCALAPPDATA%\MahodAI_Civil3D\selftest_spike_&lt;ts&gt;.json (+ _latest copy)
    /// and logs a SPIKE RESULT line to mahod_ai.log. Run interactively first
    /// (type MAHOD_SELFTEST_SPIKE), then under acad.exe /b to prove the headless case.
    /// </summary>
    public class SelfTestSpikeCommand
    {
        // list_layers is read-only, needs no required args, and works on ANY drawing
        // (it ignores civilDoc and reads the AutoCAD layer table directly).
        private const string ProbeTool = "list_layers";
        private const int TimeoutSeconds = 30;

        [CommandMethod("MAHOD_SELFTEST_SPIKE", CommandFlags.Modal)]
        public void RunSpike()
        {
            // Capture everything the plugin logs during the run.
            MahodLogger.MinLevel = 1;

            var startedAt = DateTime.Now;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int mainThreadId = Thread.CurrentThread.ManagedThreadId;
            MahodLogger.Info($"SPIKE start — mainThreadId={mainThreadId}, mode=DoEvents-pump-in-CommandMethod");

            // ---- Probe 1: raw Application.Idle ----------------------------------
            bool idleFired = false;
            int idleThreadId = -1;

            void IdleProbe(object? sender, EventArgs e)
            {
                AcadApp.Idle -= IdleProbe;
                idleThreadId = Thread.CurrentThread.ManagedThreadId;
                idleFired = true;
            }

            AcadApp.Idle += IdleProbe;

            // ---- Probe 2: real ToolExecutor marshaling path ---------------------
            var executor = new ToolExecutor();
            var toolArgs = JsonSerializer.SerializeToElement(new
            {
                include_entity_counts = false,
                limit = 50
            });

            ToolResult? toolResult = null;
            Exception? toolException = null;
            int continuationThreadId = -1;
            var done = new ManualResetEventSlim(false);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));

            // Fire-and-forget on the ThreadPool. With no SyncContext installed under /b,
            // every await continuation resumes on a ThreadPool thread, NOT the (pumping)
            // main thread — so this never re-enters the main thread except through the
            // Idle handler ToolExecutor registers internally.
            _ = Task.Run(async () =>
            {
                try
                {
                    toolResult = await executor.ExecuteAsync(ProbeTool, toolArgs, cts.Token);
                }
                catch (Exception ex)
                {
                    toolException = ex;
                }
                finally
                {
                    continuationThreadId = Thread.CurrentThread.ManagedThreadId;
                    done.Set();
                }
            });

            // ---- Pump the main thread until the scenario completes or times out --
            // This is the exact pattern the harness will use. NEVER a bare .Wait() on
            // the main thread (that would block the very thread Idle needs).
            var deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds + 5);
            bool timedOut = false;
            while (!done.IsSet)
            {
                System.Windows.Forms.Application.DoEvents();
                if (DateTime.UtcNow > deadline)
                {
                    timedOut = true;
                    break;
                }
                done.Wait(15); // brief throttle between pumps; returns instantly once set
            }

            // Clean up the raw probe if it never fired.
            AcadApp.Idle -= IdleProbe;
            sw.Stop();

            bool toolExecuted = toolResult != null;
            bool toolSuccess = toolResult?.Success == true;
            bool idleOnMainThread = idleFired && idleThreadId == mainThreadId;
            bool overallPass = !timedOut && idleOnMainThread && toolSuccess;

            string verdict;
            if (overallPass)
                verdict = "PASS — Idle fires under the DoEvents pump and ToolExecutor marshaled a tool on the main thread. Harness pattern is viable.";
            else if (!idleFired)
                verdict = "FAIL — Application.Idle never fired during the DoEvents pump inside the command. The in-command pump approach does NOT work; drive the scenario from an Idle/event context outside the command instead.";
            else if (!idleOnMainThread)
                verdict = "FAIL — Idle fired but not on the captured main thread (unexpected).";
            else if (timedOut)
                verdict = "FAIL — timed out waiting for the tool to complete; ToolExecutor marshaling stalled under the pump.";
            else
                verdict = $"PARTIAL — Idle fired on the main thread, but the tool did not succeed (error: {toolResult?.Error?.Message ?? toolException?.Message ?? "unknown"}). Idle premise holds; investigate the tool/lock path.";

            // ---- Write the report -----------------------------------------------
            var report = new
            {
                run_id = startedAt.ToString("yyyyMMdd_HHmmss"),
                purpose = "Prove Application.Idle marshaling fires during a DoEvents pump inside a running [CommandMethod] (the load-bearing acad.exe /b premise).",
                started_at = startedAt.ToString("o"),
                elapsed_ms = sw.ElapsedMilliseconds,
                main_thread_id = mainThreadId,
                command_flags = "Modal",
                raw_idle_probe = new
                {
                    fired = idleFired,
                    fired_on_thread = idleThreadId,
                    on_main_thread = idleOnMainThread
                },
                tool_exec = new
                {
                    tool = ProbeTool,
                    executed = toolExecuted,
                    success = toolSuccess,
                    error = toolResult?.Error?.Message ?? toolException?.Message,
                    continuation_thread_id = continuationThreadId
                },
                timed_out = timedOut,
                overall = overallPass ? "pass" : "fail",
                verdict
            };

            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            string? reportPath = TryWriteReport(startedAt, json);

            MahodLogger.Info(
                $"SPIKE RESULT overall={(overallPass ? "pass" : "fail")} " +
                $"idleFired={idleFired} idleOnMain={idleOnMainThread} " +
                $"toolExecuted={toolExecuted} toolSuccess={toolSuccess} " +
                $"timedOut={timedOut} elapsedMs={sw.ElapsedMilliseconds} report={reportPath ?? "<write-failed>"}");
            MahodLogger.Info($"SPIKE VERDICT: {verdict}");

            // Best-effort editor echo (no-op-safe under /b if there is no editor).
            try
            {
                var doc = AcadApp.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage(
                    $"\n[MAHOD_SELFTEST_SPIKE] overall={(overallPass ? "PASS" : "FAIL")} " +
                    $"idleFired={idleFired} toolSuccess={toolSuccess} timedOut={timedOut}\n{verdict}\n" +
                    $"Report: {reportPath}\n");
            }
            catch { /* no editor in some headless contexts */ }
        }

        private static string? TryWriteReport(DateTime startedAt, string json)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MahodAI_Civil3D");
                Directory.CreateDirectory(dir);

                var stamped = Path.Combine(dir, $"selftest_spike_{startedAt:yyyyMMdd_HHmmss}.json");
                File.WriteAllText(stamped, json);

                // Stable path the PowerShell launcher / Claude can always read.
                File.WriteAllText(Path.Combine(dir, "selftest_spike_latest.json"), json);
                return stamped;
            }
            catch (Exception ex)
            {
                MahodLogger.Error("SPIKE failed to write report", ex);
                return null;
            }
        }
    }
}
