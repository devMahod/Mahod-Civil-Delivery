using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using MahodAI.Civil3D.Plugin.Commands;
using MahodAI.Civil3D.Plugin.Tools;
using MahodAI.Civil3D.Plugin.Tools.Creation;
using MahodAI.Civil3D.Plugin.Utilities;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Exception = System.Exception;

// Additive command-class registration (multiple [assembly: CommandClass] are allowed).
[assembly: CommandClass(typeof(BuildAssemblyLibraryCommand))]

namespace MahodAI.Civil3D.Plugin.Commands
{
    /// <summary>
    /// P1-01: bootstraps the MahodAI assembly library. For each supported road type it imports
    /// the matching Civil 3D STOCK assembly (which already carries real subassemblies and a
    /// daylight/surface target — the .NET API cannot build subassemblies from scratch, see
    /// <see cref="AssemblyTemplateCatalog"/>) and names it <c>MahodAI_&lt;road_type&gt;</c> in the
    /// CURRENT drawing, by driving the existing <c>clone_assembly_from_library</c> tool through
    /// the real <see cref="ToolExecutor"/> marshaling path.
    ///
    /// The result is a starting point that a Mahod engineer must REVIEW and adjust to the exact
    /// MOT-2011 widths (this command does not certify MOT compliance), then SaveAs to
    /// <c>…\MahodAI.bundle\Contents\Assemblies\MahodAI_Assemblies_2027.dwg</c>. After the DWG is
    /// in place, flip <c>use_stock_only</c> to false in the agent so the library is consulted.
    ///
    /// Writes %LOCALAPPDATA%\MahodAI_Civil3D\build_assembly_library_&lt;ts&gt;.json (+ _latest).
    /// Run interactively: type MAHOD_BUILD_ASSEMBLY_LIBRARY in Civil 3D 2027 with a blank/template
    /// drawing open.
    /// </summary>
    public class BuildAssemblyLibraryCommand
    {
        private const int PerAssemblyTimeoutSeconds = 120;

        // Target the running host year. This machine is Civil 3D 2027; the engineer saves to
        // the versioned name the loader looks for (MahodAI_Assemblies_&lt;year&gt;.dwg).
        private const string TargetLibraryFileName = "MahodAI_Assemblies_2027.dwg";

        [CommandMethod("MAHOD_BUILD_ASSEMBLY_LIBRARY", CommandFlags.Modal)]
        public void Run()
        {
            MahodLogger.MinLevel = 1;
            var startedAt = DateTime.Now;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            var ed = doc?.Editor;
            ed?.WriteMessage("\n[MAHOD_BUILD_ASSEMBLY_LIBRARY] Building MahodAI_* assemblies from Civil 3D stock…\n");

            var roadTypes = AssemblyTemplateCatalog.SupportedRoadTypes;
            var executor = new ToolExecutor();

            // Run all clones on a ThreadPool thread while the main thread pumps, so
            // ToolExecutor.ExecuteOnMainThreadAsync's Idle marshaling can fire (same proven
            // pattern as the self-test spike). Never a bare .Wait() on the main thread.
            var results = new List<AssemblyBuildOutcome>();
            var done = new ManualResetEventSlim(false);
            Exception? fatal = null;

            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var roadType in roadTypes)
                    {
                        var outcome = new AssemblyBuildOutcome
                        {
                            RoadType = roadType,
                            AssemblyName = $"MahodAI_{roadType}",
                        };
                        try
                        {
                            var args = JsonSerializer.SerializeToElement(new
                            {
                                road_type = roadType,
                                // Bootstrap from the matching stock assembly (has real
                                // subassemblies + a Surface daylight target).
                                use_stock_only = true,
                            });
                            using var cts = new CancellationTokenSource(
                                TimeSpan.FromSeconds(PerAssemblyTimeoutSeconds));
                            var r = await executor.ExecuteAsync("clone_assembly_from_library", args, cts.Token);
                            outcome.Success = r.Success;
                            outcome.Detail = DescribeResult(r);
                            outcome.SubassemblyCount = ExtractInt(r.Data, "subassembly_count");
                            outcome.Source = ExtractString(r.Data, "source");
                            if (!r.Success)
                                outcome.Error = r.Error?.Message;
                        }
                        catch (Exception ex)
                        {
                            outcome.Success = false;
                            outcome.Error = ex.Message;
                        }
                        results.Add(outcome);
                    }
                }
                catch (Exception ex)
                {
                    fatal = ex;
                }
                finally
                {
                    done.Set();
                }
            });

            // Pump until every clone finishes (generous overall deadline).
            var deadline = DateTime.UtcNow.AddSeconds(PerAssemblyTimeoutSeconds * roadTypes.Count + 30);
            bool timedOut = false;
            while (!done.IsSet)
            {
                System.Windows.Forms.Application.DoEvents();
                if (DateTime.UtcNow > deadline) { timedOut = true; break; }
                done.Wait(15);
            }
            sw.Stop();

            int built = results.FindAll(o => o.Success).Count;
            bool allBuilt = !timedOut && fatal == null && built == roadTypes.Count;

            string targetPath = Path.Combine(DeployedAssembliesDir(), TargetLibraryFileName);

            // ── Report to the command line ────────────────────────────────────
            ed?.WriteMessage($"\n[MAHOD_BUILD_ASSEMBLY_LIBRARY] {built}/{roadTypes.Count} assemblies built" +
                             (timedOut ? " (TIMED OUT)" : "") + (fatal != null ? $" (ERROR: {fatal.Message})" : "") + ".\n");
            foreach (var o in results)
            {
                ed?.WriteMessage($"  {(o.Success ? "✓" : "✗")} {o.AssemblyName}" +
                                 (o.Success ? $" — {o.SubassemblyCount} subassemblies (source: {o.Source})" : $" — {o.Error}") + "\n");
            }
            if (built > 0)
            {
                ed?.WriteMessage(
                    "\nNEXT STEPS (engineer):\n" +
                    "  1. Review each MahodAI_* assembly and adjust to MOT-2011 widths: " +
                    "urban 3.65 / rural 3.75 / divided 3.75 + median 3.0 / collector 3.25 m (lanes at -2%).\n" +
                    "  2. AUDIT + PURGE, confirm every subassembly reports Up-to-date and has a Surface daylight target.\n" +
                    $"  3. SAVEAS the drawing to:\n     {targetPath}\n" +
                    "     (and copy the same file into the repo bundle plugin\\MahodAI.bundle\\Contents\\Assemblies\\).\n" +
                    "  4. Flip use_stock_only False in ai_agent design.py so the library is consulted.\n");
            }

            // ── JSON report (readable back out-of-band) ───────────────────────
            var report = new
            {
                run_id = startedAt.ToString("yyyyMMdd_HHmmss"),
                started_at = startedAt.ToString("o"),
                elapsed_ms = sw.ElapsedMilliseconds,
                target_library = targetPath,
                road_types = roadTypes,
                built_count = built,
                total = roadTypes.Count,
                all_built = allBuilt,
                timed_out = timedOut,
                fatal_error = fatal?.Message,
                assemblies = results,
                note = "Stock-derived bootstrap — requires engineer review/adjustment to MOT-2011 and SaveAs to target_library before it is a Mahod standard library.",
            };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            string? reportPath = TryWriteReport(startedAt, json);

            MahodLogger.Info(
                $"BUILD_ASSEMBLY_LIBRARY built={built}/{roadTypes.Count} allBuilt={allBuilt} " +
                $"timedOut={timedOut} elapsedMs={sw.ElapsedMilliseconds} report={reportPath ?? "<write-failed>"}");
            ed?.WriteMessage($"\nReport: {reportPath}\n");
        }

        private static string DescribeResult(ToolResult r)
        {
            try { return JsonSerializer.Serialize(r.Data); }
            catch { return r.Success ? "ok" : (r.Error?.Message ?? "error"); }
        }

        private static int? ExtractInt(object? data, string key)
        {
            try
            {
                var el = JsonSerializer.SerializeToElement(data);
                if (el.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v))
                    return v;
            }
            catch { }
            return null;
        }

        private static string? ExtractString(object? data, string key)
        {
            try
            {
                var el = JsonSerializer.SerializeToElement(data);
                if (el.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String)
                    return p.GetString();
            }
            catch { }
            return null;
        }

        /// <summary>The deployed bundle's Assemblies folder where the loader looks for the DWG.</summary>
        private static string DeployedAssembliesDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "ApplicationPlugins", "MahodAI.bundle", "Contents", "Assemblies");
        }

        private static string? TryWriteReport(DateTime startedAt, string json)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MahodAI_Civil3D");
                Directory.CreateDirectory(dir);
                var stamped = Path.Combine(dir, $"build_assembly_library_{startedAt:yyyyMMdd_HHmmss}.json");
                File.WriteAllText(stamped, json);
                File.WriteAllText(Path.Combine(dir, "build_assembly_library_latest.json"), json);
                return stamped;
            }
            catch (Exception ex)
            {
                MahodLogger.Error("BUILD_ASSEMBLY_LIBRARY failed to write report", ex);
                return null;
            }
        }
    }

    /// <summary>Per-assembly outcome for the report.</summary>
    public sealed class AssemblyBuildOutcome
    {
        public string RoadType { get; set; } = string.Empty;
        public string AssemblyName { get; set; } = string.Empty;
        public bool Success { get; set; }
        public int? SubassemblyCount { get; set; }
        public string? Source { get; set; }
        public string? Error { get; set; }
        public string? Detail { get; set; }
    }
}
