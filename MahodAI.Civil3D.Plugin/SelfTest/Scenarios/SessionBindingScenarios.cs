using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MahodAI.Civil3D.Plugin.Tools;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.SelfTest.Scenarios
{
    /// <summary>
    /// PROTOCOL v1.12: "a tool_call executes ONLY against that session's bound document —
    /// if another drawing is active, the plugin activates the bound one first".
    ///
    /// Needs TWO open drawings to mean anything; skips cleanly with one. The scenario
    /// swaps <see cref="ToolExecutor.SessionBindingResolver"/> for a stub and ALWAYS
    /// restores it — the resolver is global state wired once at plugin init.
    /// </summary>
    public sealed class SessionDocumentBindingScenario : ISelfTestScenario
    {
        public string Name => "tools.session_document_binding";
        public string Description => "Two sessions bound to two different drawings each run against their OWN drawing.";
        public bool Mutates => false;
        public TimeSpan Timeout => TimeSpan.FromSeconds(180);

        public async Task<ScenarioResult> RunAsync(SelfTestContext ctx)
        {
            var documents = await ctx.OnMainThreadAsync(OpenDocumentNames).ConfigureAwait(false);
            if (documents.Count < 2)
                return ScenarioResult.Skip($"needs two open drawings, found {documents.Count}");

            var docA = documents[0];
            var docB = documents[1];
            const string sessionA = "selftest-session-A";
            const string sessionB = "selftest-session-B";

            var previousResolver = ToolExecutor.SessionBindingResolver;
            var activeBefore = await ctx.OnMainThreadAsync(ActiveDocumentName).ConfigureAwait(false);

            try
            {
                ToolExecutor.SessionBindingResolver = sessionId => sessionId switch
                {
                    sessionA => new SessionBinding(true, docA),
                    sessionB => new SessionBinding(true, docB),
                    _ => new SessionBinding(false, null)
                };

                // Issued together: the queue must run them one at a time, each against
                // its own bound drawing, activating as needed.
                var callA = ctx.RunToolAsync("get_drawing_summary", new { include_object_names = false }, sessionA);
                var callB = ctx.RunToolAsync("get_drawing_summary", new { include_object_names = false }, sessionB);
                var results = await Task.WhenAll(callA, callB).ConfigureAwait(false);

                if (!results[0].Success || !results[1].Success)
                {
                    return ScenarioResult.Fail(
                        "both session-bound calls succeed",
                        $"A={results[0].Success}, B={results[1].Success}",
                        results[0].Error?.Message ?? results[1].Error?.Message);
                }

                var fileA = DrawingFileOf(results[0]);
                var fileB = DrawingFileOf(results[1]);
                var expectedA = System.IO.Path.GetFileName(docA);
                var expectedB = System.IO.Path.GetFileName(docB);

                if (!Same(fileA, expectedA) || !Same(fileB, expectedB))
                {
                    return ScenarioResult.Fail(
                        $"session A → '{expectedA}', session B → '{expectedB}'",
                        $"session A → '{fileA}', session B → '{fileB}' — a tool ran against the WRONG drawing");
                }

                return ScenarioResult
                    .Pass($"each session ran against its bound drawing", $"A='{fileA}', B='{fileB}'")
                    .With("session_a_drawing", fileA)
                    .With("session_b_drawing", fileB)
                    .With("active_before", activeBefore);
            }
            finally
            {
                ToolExecutor.SessionBindingResolver = previousResolver;
            }
        }

        internal static List<string> OpenDocumentNames()
        {
            var names = new List<string>();
            foreach (Autodesk.AutoCAD.ApplicationServices.Document d in AcadApp.DocumentManager)
                names.Add(d.Name);
            return names;
        }

        internal static string? ActiveDocumentName() => AcadApp.DocumentManager.MdiActiveDocument?.Name;

        internal static string? DrawingFileOf(ToolResult result)
        {
            var data = SelfTestContext.AsJson(result.Data);
            var metadata = ScenarioJson.Find(data, "metadata");
            if (metadata.HasValue)
            {
                var name = ScenarioJson.String(metadata.Value, "file_name");
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            return ScenarioJson.String(data, "file_name");
        }

        private static bool Same(string? a, string? b) =>
            !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PROTOCOL v1.12: "If the session's bound drawing has been closed (tabs survive
    /// drawing close), the tool fails with error code <c>DRAWING_UNAVAILABLE</c> and a
    /// Hebrew message — nothing executes and no other drawing is touched."
    ///
    /// The failure mode this guards is the dangerous one: silently running a mutation
    /// against whatever drawing happens to be active.
    /// </summary>
    public sealed class DrawingUnavailableScenario : ISelfTestScenario
    {
        public string Name => "tools.drawing_unavailable";
        public string Description => "A tool bound to a closed drawing fails DRAWING_UNAVAILABLE and touches nothing.";
        public bool Mutates => false;
        public TimeSpan Timeout => TimeSpan.FromSeconds(90);

        private const string ClosedSession = "selftest-session-closed";
        private const string MissingDrawing = @"C:\__mahod_selftest__\never_opened.dwg";

        public async Task<ScenarioResult> RunAsync(SelfTestContext ctx)
        {
            var previousResolver = ToolExecutor.SessionBindingResolver;
            var activeBefore = await ctx.OnMainThreadAsync(SessionDocumentBindingScenario.ActiveDocumentName)
                .ConfigureAwait(false);

            try
            {
                ToolExecutor.SessionBindingResolver = sessionId =>
                    sessionId == ClosedSession
                        ? new SessionBinding(true, MissingDrawing)
                        : new SessionBinding(false, null);

                var result = await ctx.RunToolAsync(
                    "get_drawing_summary", new { include_object_names = false }, ClosedSession);

                var activeAfter = await ctx.OnMainThreadAsync(SessionDocumentBindingScenario.ActiveDocumentName)
                    .ConfigureAwait(false);

                if (result.Success)
                {
                    return ScenarioResult.Fail(
                        "the tool FAILS for a closed bound drawing",
                        "the tool succeeded — it ran against some other drawing",
                        "session→document containment breach");
                }

                var code = result.Error?.Code;
                if (!string.Equals(code, ToolTargetPlanner.ErrorCodeDrawingUnavailable, StringComparison.Ordinal))
                {
                    return ScenarioResult.Fail(
                        $"error code '{ToolTargetPlanner.ErrorCodeDrawingUnavailable}'",
                        $"error code '{code}'",
                        result.Error?.Message);
                }

                var message = result.Error?.Message ?? "";
                if (!ContainsHebrew(message))
                {
                    return ScenarioResult.Fail(
                        "a Hebrew, user-facing message",
                        $"message is not Hebrew: '{message}'");
                }

                if (!string.Equals(activeBefore, activeAfter, StringComparison.OrdinalIgnoreCase))
                {
                    return ScenarioResult.Fail(
                        "the active drawing is untouched",
                        $"active drawing changed: '{activeBefore}' → '{activeAfter}'");
                }

                return ScenarioResult
                    .Pass("DRAWING_UNAVAILABLE + Hebrew message + active drawing untouched",
                          $"code={code}, active drawing unchanged ('{activeAfter}')")
                    .With("message", message);
            }
            finally
            {
                ToolExecutor.SessionBindingResolver = previousResolver;
            }
        }

        private static bool ContainsHebrew(string s) => s.Any(c => c >= '\u0590' && c <= '\u05FF');
    }
}
