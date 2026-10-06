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
    /// Parametric vertex cleanup — no clicking. Covers PLTOOLS <c>PL-VxRdc</c> ("weeding", #4 in the
    /// firm's own command list), <c>PL-VxOpt</c> ("removing coincident vertices", #5) and the
    /// Douglas-Peucker thinning PLTOOLS lacks, which is the answer to "this polyline came out of a
    /// 3D conversion with thousands of points".
    ///
    /// This is also the tool behind "clear the polyline, leave one point on each bend": weed +
    /// coincident + thin, run together, on one polyline or on every polyline of a layer.
    /// </summary>
    public class CleanPolylineVerticesTool : DrawingToolBase
    {
        public override string Name => "clean_polyline_vertices";

        public override string Description =>
            "Removes redundant vertices from polylines without any user clicking. operations: " +
            "'weed' drops vertices whose offset from the line between their neighbours is within " +
            "deviation_tolerance (or whose deflection is within angle_tolerance_deg); 'coincident' " +
            "drops duplicate vertices; 'thin' runs Douglas-Peucker so the whole result stays within " +
            "thin_tolerance of the original line (use this to reduce a dense polyline to one vertex " +
            "per bend). Target one polyline with entity_handle, several with entity_handles, or every " +
            "polyline on a layer with layer. Returns per-polyline before/after vertex counts.";

        public override string Category => ToolCategories.Modification;

        /// <summary>Long enough for the engineer to select the polylines when none were named.</summary>
        public override TimeSpan Timeout => TimeSpan.FromMinutes(5);

        /// <summary>Prompts for a selection when the agent named no target.</summary>
        public bool IsInteractive => true;

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""entity_handle"": { ""type"": ""string"", ""description"": ""Hex handle of one polyline."" },
                ""entity_handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Hex handles of several polylines."" },
                ""layer"": { ""type"": ""string"", ""description"": ""Clean every polyline on this layer."" },
                ""operations"": {
                    ""type"": ""array"",
                    ""items"": { ""type"": ""string"", ""enum"": [""weed"", ""coincident"", ""thin""] },
                    ""description"": ""Which passes to run, in this order: coincident, weed, thin. Default ['coincident','weed'].""
                },
                ""deviation_tolerance"": { ""type"": ""number"", ""description"": ""weed: max offset from the neighbours' chord (drawing units). Default 0.15 (the PLTOOLS default)."" },
                ""angle_tolerance_deg"": { ""type"": ""number"", ""description"": ""weed: max deflection angle in degrees. Optional, works alongside deviation_tolerance."" },
                ""coincident_tolerance"": { ""type"": ""number"", ""description"": ""coincident: distance below which two vertices are the same point. Default 1e-6."" },
                ""thin_tolerance"": { ""type"": ""number"", ""description"": ""thin: max deviation of the thinned line from the original. Required for 'thin'."" },
                ""preserve_arcs"": { ""type"": ""boolean"", ""description"": ""Default true: never merge across an arc segment (which would silently straighten a curve)."" }
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
            var db = HostApplicationServices.WorkingDatabase;
            if (db == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "לא נמצא מסד נתונים פעיל"));

            var operations = GetStringArrayParam(parameters, "operations") ?? new[] { "coincident", "weed" };
            var ops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var op in operations)
            {
                if (op is null) continue;
                var normalized = op.Trim().ToLowerInvariant();
                if (normalized is "weed" or "coincident" or "thin") ops.Add(normalized);
                else
                    return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                        $"פעולה לא נתמכת: '{op}'. אפשרויות: weed, coincident, thin"));
            }
            if (ops.Count == 0)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "לא נבחרה שום פעולה (operations)"));

            double deviation = GetDoubleParam(parameters, "deviation_tolerance") ?? VertexCleaner.DefaultWeedDeviation;
            double? angleDeg = GetDoubleParam(parameters, "angle_tolerance_deg");
            double coincidentTol = GetDoubleParam(parameters, "coincident_tolerance") ?? VertexCleaner.DefaultCoincidentTolerance;
            double? thinTol = GetDoubleParam(parameters, "thin_tolerance");
            bool preserveArcs = GetBoolParam(parameters, "preserve_arcs", true);

            if (ops.Contains("thin") && (!thinTol.HasValue || thinTol.Value <= 0))
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "לפעולת thin חובה לספק thin_tolerance גדול מאפס"));

            var ids = PolylineToolSupport.ResolveTargetsOrPrompt(
                tr, db, parameters, "בחרי פוליליינים לניקוי/דילול (Enter לסיום):",
                out var unresolved, out var targetSource);
            if (ids.Count == 0)
            {
                return Task.FromResult(targetSource == PolylineToolSupport.TargetSource.Cancelled
                    ? ToolResult.Cancelled(new { cancelled = true, polylines_processed = 0 })
                    : ToolResult.Fail(ToolErrorCodes.ObjectNotFound,
                        "לא נמצאו פוליליינים לעיבוד בשרטוט"));
            }

            var perEntity = new List<object>();
            var warnings = new List<string>();
            foreach (var h in unresolved) warnings.Add($"handle לא נמצא בשרטוט: {h}");

            int totalRemoved = 0, changedCount = 0;

            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();

                if (!PolylineAdapter.TryOpen(tr, id, out var target, out var errCode, out var errMsg))
                {
                    warnings.Add($"{PolylineToolSupport.HandleOf(tr, id)}: {errMsg} [{errCode}]");
                    continue;
                }

                var shape = target!.Shape;
                int before = shape.Count;
                var notes = new List<string>();
                bool anyChange = false;

                // Order matters: duplicates first (they distort every deviation measurement),
                // then weeding, then the global-error-bounded thinning.
                if (ops.Contains("coincident"))
                {
                    var r = VertexCleaner.RemoveCoincident(shape, coincidentTol);
                    notes.AddRange(r.Notes);
                    anyChange |= r.Changed;
                }
                if (ops.Contains("weed"))
                {
                    var r = VertexCleaner.Weed(shape, deviation, angleDeg, preserveArcs);
                    notes.AddRange(r.Notes);
                    anyChange |= r.Changed;
                }
                if (ops.Contains("thin"))
                {
                    var r = VertexCleaner.Thin(shape, thinTol!.Value, preserveArcs);
                    notes.AddRange(r.Notes);
                    anyChange |= r.Changed;
                }

                int removed = before - shape.Count;

                if (anyChange)
                {
                    if (!PolylineAdapter.TryWrite(tr, target, out var writeErr))
                    {
                        warnings.Add($"{PolylineToolSupport.HandleOf(tr, id)}: {writeErr}");
                        continue;
                    }
                }

                if (removed > 0) changedCount++;
                totalRemoved += removed;

                perEntity.Add(new
                {
                    entity_handle = PolylineToolSupport.HandleOf(tr, id),
                    entity_type = target.HebrewKind,
                    vertices_before = before,
                    vertices_after = shape.Count,
                    removed,
                    notes,
                });
            }

            if (perEntity.Count == 0)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "אף פוליליין לא נוקה — " + string.Join("; ", warnings)));

            return Task.FromResult(ToolResult.Ok(new
            {
                operations = new List<string>(ops),
                polylines_processed = perEntity.Count,
                polylines_changed = changedCount,
                vertices_removed = totalRemoved,
                deviation_tolerance = ops.Contains("weed") ? deviation : (double?)null,
                angle_tolerance_deg = angleDeg,
                thin_tolerance = thinTol,
                results = perEntity,
                warnings,
            }));
        }
    }
}
