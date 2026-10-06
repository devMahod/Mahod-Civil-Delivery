using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Civil.ApplicationServices;

namespace MahodAI.Civil3D.Plugin.Tools.Discovery
{
    /// <summary>
    /// Prompts the user to pick a point in the active drawing via Editor.GetPoint.
    /// </summary>
    public class PickPointTool : DrawingToolBase
    {
        public override string Name => "pick_point";
        public override string Description =>
            "Prompts the user to click a point in the drawing. Returns {status: 'ok'|'cancel', x, y, z}. " +
            "Use this to let the user choose a placement location (e.g., origin for section views).";
        public override string Category => ToolCategories.Discovery;
        public override TimeSpan Timeout => TimeSpan.FromMinutes(5);

        /// <summary>Modal point pick — the executor flushes chat renders first.</summary>
        public bool IsInteractive => true;

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""prompt"": {
                    ""type"": ""string"",
                    ""description"": ""Message shown on the Civil 3D command line (default: 'בחר נקודה בשרטוט:')""
                }
            }
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var prompt = GetStringParam(parameters, "prompt") ?? "בחר נקודה בשרטוט:";

            // Mirror the instruction into the chat BEFORE the modal prompt so the
            // engineer sees what is expected without hunting the command line.
            ToolUiNotifier.Step($"🖱️ {prompt}");

            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "No active document"));

            var opts = new PromptPointOptions("\n" + prompt)
            {
                AllowNone = false,
            };

            // Intersection snapping on a dense survey base makes picking impossible
            // ("Too many objects selected for INTERSECT") — suspend it while we prompt.
            using var pickScope = new Utilities.InteractivePickScope();

            PromptPointResult res;
            try
            {
                res = doc.Editor.GetPoint(opts);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, ex.Message, ex.ToString()));
            }

            if (res.Status == PromptStatus.OK)
            {
                return Task.FromResult(ToolResult.Ok(new
                {
                    status = "ok",
                    x = res.Value.X,
                    y = res.Value.Y,
                    z = res.Value.Z,
                }));
            }

            return Task.FromResult(ToolResult.Ok(new
            {
                status = "cancel",
                x = 0.0,
                y = 0.0,
                z = 0.0,
            }));
        }
    }
}
