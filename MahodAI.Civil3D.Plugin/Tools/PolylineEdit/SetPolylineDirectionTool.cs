using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>
    /// Direction control: PLTOOLS <c>ENTREV</c> / <c>ENTREVS</c> (reverse — the FIRST entry in the
    /// firm's own MENU.pdf list of commands they use), <c>PL-CW</c> and <c>PL-CCW</c> (force an
    /// orientation, reversing only when needed).
    ///
    /// The reversal itself is the pure core's job, because carrying bulges and width tapers across
    /// a reversal is exactly what hand-rolled implementations get wrong.
    /// </summary>
    public class SetPolylineDirectionTool : DrawingToolBase
    {
        public override string Name => "set_polyline_direction";

        public override string Description =>
            "Reverses a polyline's direction, or forces it clockwise/counter-clockwise. " +
            "direction='reverse' always flips; 'cw'/'ccw' flip only when the polyline does not " +
            "already run that way (and report that nothing was needed). Arc bulges and width tapers " +
            "are carried across correctly. Accepts entity_handle, entity_handles or layer.";

        public override string Category => ToolCategories.Modification;

        /// <summary>Long enough for the engineer to select the polylines when none were named.</summary>
        public override TimeSpan Timeout => TimeSpan.FromMinutes(5);

        /// <summary>Prompts for a selection when the agent named no target.</summary>
        public bool IsInteractive => true;

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""entity_handle"": { ""type"": ""string"" },
                ""entity_handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                ""layer"": { ""type"": ""string"", ""description"": ""Apply to every polyline on this layer."" },
                ""direction"": {
                    ""type"": ""string"",
                    ""enum"": [""reverse"", ""cw"", ""ccw""],
                    ""description"": ""reverse = always flip; cw/ccw = make it run that way""
                }
            },
            ""required"": [""direction""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var db = HostApplicationServices.WorkingDatabase;
            if (db == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "לא נמצא מסד נתונים פעיל"));

            var direction = (GetStringParam(parameters, "direction") ?? string.Empty).Trim().ToLowerInvariant();
            if (direction is not ("reverse" or "cw" or "ccw"))
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"direction לא נתמך: '{direction}'. אפשרויות: reverse, cw, ccw"));

            var ids = PolylineToolSupport.ResolveTargetsOrPrompt(
                tr, db, parameters, "בחרי פוליליינים לשינוי כיוון (Enter לסיום):",
                out var unresolved, out var targetSource);
            if (ids.Count == 0)
            {
                return Task.FromResult(targetSource == PolylineToolSupport.TargetSource.Cancelled
                    ? ToolResult.Cancelled(new { cancelled = true, polylines_processed = 0 })
                    : ToolResult.Fail(ToolErrorCodes.ObjectNotFound,
                        "לא נמצאו פוליליינים בשרטוט"));
            }

            var results = new List<object>();
            var warnings = new List<string>();
            foreach (var h in unresolved) warnings.Add($"handle לא נמצא בשרטוט: {h}");
            int reversed = 0, unchanged = 0;

            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();

                if (!PolylineAdapter.TryOpen(tr, id, out var target, out var errCode, out var errMsg))
                {
                    warnings.Add($"{PolylineToolSupport.HandleOf(tr, id)}: {errMsg} [{errCode}]");
                    continue;
                }

                var shape = target!.Shape;
                bool wasClockwise = shape.Count >= 3 && PolylineOrientation.IsClockwise(shape);

                var result = direction == "reverse"
                    ? PolylineOrientation.Reverse(shape)
                    : PolylineOrientation.SetOrientation(shape, clockwise: direction == "cw");

                if (result.Changed)
                {
                    if (!PolylineAdapter.TryWrite(tr, target, out var writeErr))
                    {
                        warnings.Add($"{PolylineToolSupport.HandleOf(tr, id)}: {writeErr}");
                        continue;
                    }
                    reversed++;
                }
                else
                {
                    unchanged++;
                }

                results.Add(new
                {
                    entity_handle = PolylineToolSupport.HandleOf(tr, id),
                    entity_type = target.HebrewKind,
                    reversed = result.Changed,
                    was_clockwise = wasClockwise,
                    is_clockwise = shape.Count >= 3 ? PolylineOrientation.IsClockwise(shape) : (bool?)null,
                    notes = result.Notes,
                });
            }

            if (results.Count == 0)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "אף פוליליין לא טופל — " + string.Join("; ", warnings)));

            return Task.FromResult(ToolResult.Ok(new
            {
                direction,
                polylines_processed = results.Count,
                polylines_reversed = reversed,
                polylines_already_correct = unchanged,
                results,
                warnings,
            }));
        }
    }
}
