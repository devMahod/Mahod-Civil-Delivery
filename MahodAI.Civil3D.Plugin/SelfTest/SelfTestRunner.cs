using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.Runtime;
using MahodAI.Civil3D.Plugin.SelfTest;
using MahodAI.Civil3D.Plugin.SelfTest.Scenarios;
using MahodAI.Civil3D.Plugin.Tools;
using MahodAI.Civil3D.Plugin.Utilities;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Exception = System.Exception;

// Additive command-class registration (multiple [assembly: CommandClass] attributes
// are allowed), so MAHOD_SELFTEST is discovered without touching MahodCommands.cs.
[assembly: CommandClass(typeof(SelfTestCommand))]

namespace MahodAI.Civil3D.Plugin.SelfTest
{
    /// <summary>
    /// In-host scenario runner. Executes an ORDERED scenario list against the drawing
    /// that is currently open and writes ONE machine-readable JSON report.
    ///
    /// Why it exists: the layers below can only be exercised inside a live Civil 3D
    /// process — tool execution against a real document, the ToolExecutor Idle hop, the
    /// serial FIFO queue, session→document binding, a real WebSocket round trip.
    /// Headless <c>accoreconsole</c> cannot load the Civil 3D managed stack at all, so
    /// there is no test-process substitute (see SelfTest.md).
    /// </summary>
    public sealed class SelfTestRunner
    {
        /// <summary>
        /// The starter set, in execution order: cheap read-only probes first, the
        /// invariants that only exist in-host next, network last, mutation (opt-in) last of all.
        /// </summary>
        public static IReadOnlyList<ISelfTestScenario> DefaultScenarios => new ISelfTestScenario[]
        {
            new ToolMarshalingScenario(),
            new ListLayersScenario(),
            new ListObjectsScenario(),
            new AlignmentGeometryScenario(),
            new SerialQueueScenario(),
            new SessionDocumentBindingScenario(),
            new DrawingUnavailableScenario(),
            new WebSocketAnalyzeRoundTripScenario(),
            new AlignmentRadiusUndoScenario(),
        };

        public async Task<SelfTestReport> RunAsync(
            SelfTestContext ctx,
            IEnumerable<ISelfTestScenario> scenarios,
            CancellationToken ct = default)
        {
            var report = new SelfTestReport
            {
                RunId = DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                StartedAt = DateTime.Now.ToString("o"),
                MutationAllowed = ctx.AllowMutation,
                AgentUrl = ctx.AgentUrl
            };

            foreach (var scenario in scenarios)
            {
                ct.ThrowIfCancellationRequested();

                var entry = new ScenarioReportEntry
                {
                    Name = scenario.Name,
                    Description = scenario.Description,
                    Mutates = scenario.Mutates
                };

                var sw = Stopwatch.StartNew();
                try
                {
                    if (scenario.Mutates && !ctx.AllowMutation)
                    {
                        Apply(entry, ScenarioResult.Skip(
                            "mutating scenario — opt in with MAHOD_SELFTEST_ALLOW_MUTATION=1"));
                    }
                    else
                    {
                        // Watchdog only — a scenario owns its own internal cancellation.
                        // Cancelling the delay when the scenario wins keeps its timer from
                        // outliving the loop.
                        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);

                        var run = scenario.RunAsync(ctx);
                        var finished = await Task.WhenAny(run, Task.Delay(scenario.Timeout, watchdog.Token))
                            .ConfigureAwait(false);
                        watchdog.Cancel();

                        if (finished == run)
                        {
                            Apply(entry, await run.ConfigureAwait(false));
                        }
                        else
                        {
                            Apply(entry, ScenarioResult.Fail(
                                "scenario completes within its budget",
                                $"still running after {scenario.Timeout.TotalSeconds:0}s",
                                "TIMEOUT"));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Apply(entry, ScenarioResult.Fail("scenario runs to completion", "threw", ex.Message, ex));
                }
                finally
                {
                    sw.Stop();
                    entry.DurationMs = sw.ElapsedMilliseconds;
                }

                report.Scenarios.Add(entry);
                MahodLogger.Info(
                    $"SELFTEST {entry.Name} = {entry.Status} ({entry.DurationMs} ms){(entry.Error != null ? " — " + entry.Error : "")}");
                ctx.Log($"  {StatusGlyph(entry.Status)} {entry.Name} ({entry.DurationMs} ms)" +
                        (entry.Status == ScenarioStatus.Pass ? "" : $" — {entry.Actual}"));
            }

            report.Passed = report.Scenarios.Count(s => s.Status == ScenarioStatus.Pass);
            report.Failed = report.Scenarios.Count(s => s.Status == ScenarioStatus.Fail);
            report.Skipped = report.Scenarios.Count(s => s.Status == ScenarioStatus.Skip);
            report.Total = report.Scenarios.Count;
            report.Overall = report.Failed == 0;
            report.FinishedAt = DateTime.Now.ToString("o");

            return report;
        }

        private static void Apply(ScenarioReportEntry entry, ScenarioResult result)
        {
            entry.Status = result.Status;
            entry.Expected = result.Expected;
            entry.Actual = result.Actual;
            entry.Error = result.Error;
            entry.Stack = result.Stack;
            if (result.Details.Count > 0) entry.Details = result.Details;
        }

        private static string StatusGlyph(string status) => status switch
        {
            ScenarioStatus.Pass => "PASS",
            ScenarioStatus.Skip => "SKIP",
            _ => "FAIL"
        };
    }

    #region Report model

    public sealed class SelfTestReport
    {
        public string RunId { get; set; } = string.Empty;
        public string StartedAt { get; set; } = string.Empty;
        public string FinishedAt { get; set; } = string.Empty;

        /// <summary>The one field a launcher script needs: true ⇔ zero failures.</summary>
        public bool Overall { get; set; }

        public int Total { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }

        public bool MutationAllowed { get; set; }
        public string? AgentUrl { get; set; }
        public string? Drawing { get; set; }
        public string? Civil3DVersion { get; set; }
        public bool TimedOut { get; set; }

        public List<ScenarioReportEntry> Scenarios { get; set; } = new();
    }

    public sealed class ScenarioReportEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Status { get; set; } = ScenarioStatus.Fail;
        public bool Mutates { get; set; }
        public long DurationMs { get; set; }
        public string Expected { get; set; } = string.Empty;
        public string Actual { get; set; } = string.Empty;
        public string? Error { get; set; }
        public string? Stack { get; set; }
        public Dictionary<string, object?>? Details { get; set; }
    }

    #endregion

    /// <summary>
    /// <c>MAHOD_SELFTEST</c> — runs <see cref="SelfTestRunner.DefaultScenarios"/> against the
    /// open drawing and writes <c>%LOCALAPPDATA%\MahodAI_Civil3D\selftest_&lt;ts&gt;.json</c>
    /// plus the stable <c>selftest_latest.json</c> a script can poll.
    ///
    /// Threading is the spike's proven pattern (MAHOD_SELFTEST_SPIKE): the scenarios run on
    /// the ThreadPool while this command pumps <c>DoEvents()</c> on the main thread, so the
    /// <c>Application.Idle</c> hop ToolExecutor depends on keeps firing.
    /// </summary>
    public class SelfTestCommand
    {
        /// <summary>Hard ceiling for a whole run; the launcher's timeout should exceed it.</summary>
        private static readonly TimeSpan GlobalBudget = TimeSpan.FromMinutes(15);

        [CommandMethod("MAHOD_SELFTEST", CommandFlags.Modal)]
        public void RunSelfTest()
        {
            MahodLogger.MinLevel = 1;

            var startedAt = DateTime.Now;
            var mainThreadId = Thread.CurrentThread.ManagedThreadId;
            var sw = Stopwatch.StartNew();

            var editor = TryGetEditor();
            void Echo(string line)
            {
                try { editor?.WriteMessage("\n" + line); } catch { /* no editor under /b */ }
            }

            Echo($"[MAHOD_SELFTEST] start — {SelfTestRunner.DefaultScenarios.Count} scenarios");
            MahodLogger.Info($"SELFTEST start — mainThreadId={mainThreadId}");

            var allowMutation = string.Equals(
                Environment.GetEnvironmentVariable("MAHOD_SELFTEST_ALLOW_MUTATION"), "1", StringComparison.Ordinal);
            var agentUrl = NullIfBlank(Environment.GetEnvironmentVariable("MAHOD_SELFTEST_AGENT_URL"));
            var agentKey = NullIfBlank(Environment.GetEnvironmentVariable("MAHOD_SELFTEST_AGENT_KEY"))
                           ?? NullIfBlank(Environment.GetEnvironmentVariable("MAHOD_AGENT_API_KEY"));

            var ctx = new SelfTestContext(
                new ToolExecutor(), mainThreadId, allowMutation, agentUrl, agentKey, Echo);

            SelfTestReport? report = null;
            Exception? runnerException = null;
            var done = new ManualResetEventSlim(false);
            using var cts = new CancellationTokenSource(GlobalBudget);

            _ = Task.Run(async () =>
            {
                try
                {
                    report = await new SelfTestRunner()
                        .RunAsync(ctx, SelfTestRunner.DefaultScenarios, cts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    runnerException = ex;
                }
                finally
                {
                    done.Set();
                }
            });

            // Pump the main thread so Application.Idle keeps firing (spike-verified).
            // NEVER a bare .Wait() here — that would block the thread Idle needs.
            var deadline = DateTime.UtcNow + GlobalBudget + TimeSpan.FromSeconds(30);
            bool timedOut = false;
            while (!done.IsSet)
            {
                System.Windows.Forms.Application.DoEvents();
                if (DateTime.UtcNow > deadline) { timedOut = true; break; }
                done.Wait(15);
            }

            sw.Stop();

            report ??= new SelfTestReport
            {
                RunId = startedAt.ToString("yyyyMMdd_HHmmss"),
                StartedAt = startedAt.ToString("o"),
                FinishedAt = DateTime.Now.ToString("o"),
                Overall = false
            };

            report.TimedOut = timedOut;
            if (timedOut || runnerException != null) report.Overall = false;
            report.Drawing = TryGetDrawingName();
            report.Civil3DVersion = TryGetHostVersion();

            if (runnerException != null)
            {
                report.Scenarios.Add(new ScenarioReportEntry
                {
                    Name = "runner",
                    Description = "the runner itself",
                    Status = ScenarioStatus.Fail,
                    Expected = "runner completes",
                    Actual = "threw",
                    Error = runnerException.Message,
                    Stack = runnerException.ToString()
                });
                report.Failed++;
                report.Total++;
            }

            var path = WriteReport(startedAt, report);

            Echo($"[MAHOD_SELFTEST] {(report.Overall ? "PASS" : "FAIL")} — " +
                 $"{report.Passed} passed, {report.Failed} failed, {report.Skipped} skipped " +
                 $"in {sw.ElapsedMilliseconds} ms");
            Echo($"[MAHOD_SELFTEST] report: {path ?? "<write failed>"}");
            MahodLogger.Info(
                $"SELFTEST RESULT overall={(report.Overall ? "pass" : "fail")} " +
                $"passed={report.Passed} failed={report.Failed} skipped={report.Skipped} " +
                $"timedOut={timedOut} elapsedMs={sw.ElapsedMilliseconds} report={path ?? "<write-failed>"}");
        }

        private static string? WriteReport(DateTime startedAt, SelfTestReport report)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MahodAI_Civil3D");
                Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

                var stamped = Path.Combine(dir, $"selftest_{startedAt:yyyyMMdd_HHmmss}.json");
                File.WriteAllText(stamped, json);

                // Stable path the launcher polls. Written LAST so a reader never sees a
                // half-finished run at the well-known location.
                File.WriteAllText(Path.Combine(dir, "selftest_latest.json"), json);
                return stamped;
            }
            catch (Exception ex)
            {
                MahodLogger.Error("SELFTEST failed to write report", ex);
                return null;
            }
        }

        private static Autodesk.AutoCAD.EditorInput.Editor? TryGetEditor()
        {
            try { return AcadApp.DocumentManager.MdiActiveDocument?.Editor; }
            catch { return null; }
        }

        private static string? TryGetDrawingName()
        {
            try { return AcadApp.DocumentManager.MdiActiveDocument?.Name; }
            catch { return null; }
        }

        private static string? TryGetHostVersion()
        {
            try { return AcadApp.GetSystemVariable("ACADVER")?.ToString(); }
            catch { return null; }
        }

        private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
