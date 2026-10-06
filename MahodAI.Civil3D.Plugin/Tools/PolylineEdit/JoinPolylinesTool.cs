using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>
    /// PLTOOLS <c>PL-JOIN</c> / <c>PL-JOIN3D</c> / <c>PL-CSE</c>: chains separate polylines into
    /// longer ones, tolerating the millimetre gaps that real surveyed and hand-drawn linework has
    /// (AutoCAD's own join wants exact coincidence — the reason the original prompts for a fuzz).
    ///
    /// 2D and 3D polylines are chained separately: a Polyline3d cannot merge into a flat polyline
    /// without inventing elevations, so mixing them is refused rather than fudged.
    /// </summary>
    public class JoinPolylinesTool : DrawingToolBase
    {
        public override string Name => "join_polylines";

        public override string Description =>
            "Joins polylines whose ends meet into single longer polylines, within `fuzz` tolerance " +
            "(default 1e-6; raise it for survey linework with small gaps). Pieces are reversed as " +
            "needed and arcs/width tapers are carried across; a chain whose two ends meet comes out " +
            "closed. 2D and 3D polylines are chained separately. Returns one entry per resulting " +
            "polyline with the handles that went into it.";

        public override string Category => ToolCategories.Modification;

        /// <summary>Long enough for the engineer to select the pieces when none were named.</summary>
        public override TimeSpan Timeout => TimeSpan.FromMinutes(5);

        /// <summary>Prompts for a selection when the agent named no target.</summary>
        public bool IsInteractive => true;

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""entity_handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Hex handles of the polylines to join."" },
                ""layer"": { ""type"": ""string"", ""description"": ""Join every polyline on this layer."" },
                ""fuzz"": { ""type"": ""number"", ""description"": ""Largest gap between two ends that still counts as joined. Default 1e-6."" },
                ""delete_source"": { ""type"": ""boolean"", ""description"": ""Default true: the joined pieces are erased and replaced by the chain."" }
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

            double fuzz = GetDoubleParam(parameters, "fuzz") ?? 1e-6;
            bool deleteSource = GetBoolParam(parameters, "delete_source", true);

            var ids = PolylineToolSupport.ResolveTargetsOrPrompt(
                tr, db, parameters, "בחרי את הפוליליינים לחיבור (Enter לסיום):",
                out var unresolved, out var targetSource);
            if (ids.Count < 2)
            {
                return Task.FromResult(targetSource == PolylineToolSupport.TargetSource.Cancelled
                    ? ToolResult.Cancelled(new { cancelled = true, chains_created = 0 })
                    : ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                        "צריך לפחות שני פוליליינים לחיבור — נבחר רק אחד"));
            }

            var warnings = new List<string>();
            foreach (var h in unresolved) warnings.Add($"handle לא נמצא בשרטוט: {h}");

            // Read every piece once; keep the id/target next to its shape so a chain can report
            // exactly which handles it consumed.
            var shapes = new List<PlShape>();
            var sourceIds = new List<ObjectId>();
            var sourceTargets = new List<PolylineTarget>();

            foreach (var id in ids)
            {
                if (!PolylineAdapter.TryOpen(tr, id, out var target, out var code, out var msg))
                {
                    warnings.Add($"{PolylineToolSupport.HandleOf(tr, id)}: {msg} [{code}]");
                    continue;
                }
                if (target!.Kind == PlTargetKind.HatchLoop)
                {
                    warnings.Add($"{PolylineToolSupport.HandleOf(tr, id)}: קו מתאר של Hatch אינו ניתן לחיבור");
                    continue;
                }

                // Chaining happens in world coordinates so pieces on different OCS planes still
                // meet where they visually meet.
                var worldShape = new PlShape(closed: target.Shape.Closed, is3d: target.Shape.Is3d,
                    elevation: target.Shape.Elevation);
                foreach (var v in target.Shape.Vertices)
                {
                    var w = target.ToWorld(v.P, v.Z);
                    worldShape.Vertices.Add(v with { X = w.X, Y = w.Y, Z = w.Z });
                }

                shapes.Add(worldShape);
                sourceIds.Add(id);
                sourceTargets.Add(target);
            }

            if (shapes.Count < 2)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "לא נאספו שני פוליליינים תקינים — " + string.Join("; ", warnings)));

            var chains = PolylineJoiner.Chain2d(shapes, fuzz);

            var results = new List<object>();
            int created = 0, erased = 0;

            foreach (var chain in chains)
            {
                ct.ThrowIfCancellationRequested();

                var memberHandles = new List<string>();
                foreach (int index in chain.SourceIndices)
                    memberHandles.Add(PolylineToolSupport.HandleOf(tr, sourceIds[index]));

                // A "chain" of one piece is just that piece — leave it alone entirely.
                if (chain.SourceIndices.Count < 2)
                {
                    results.Add(new
                    {
                        joined = false,
                        source_handles = memberHandles,
                        vertex_count = chain.Shape.Count,
                        note = "לא נמצא חיבור בטווח ה-fuzz",
                    });
                    continue;
                }

                var firstSource = (Entity)tr.GetObject(sourceIds[chain.SourceIndices[0]], OpenMode.ForRead);
                ObjectId newId;

                if (chain.Shape.Is3d)
                {
                    var pts = new List<Point3d>(chain.Shape.Count);
                    foreach (var v in chain.Shape.Vertices) pts.Add(new Point3d(v.X, v.Y, v.Z));
                    newId = PolylineToolSupport.CreatePolyline3d(tr, db, pts, chain.Shape.Closed, null);
                }
                else
                {
                    var pts = new List<Pt2>(chain.Shape.Count);
                    var bulges = new List<double>(chain.Shape.Count);
                    foreach (var v in chain.Shape.Vertices)
                    {
                        pts.Add(new Pt2(v.X, v.Y));
                        bulges.Add(v.Bulge);
                    }
                    newId = PolylineToolSupport.CreatePolyline(
                        tr, db, pts, chain.Shape.Closed, chain.Shape.Elevation, null, bulges);
                }

                var createdEntity = (Entity)tr.GetObject(newId, OpenMode.ForWrite);
                PolylineToolSupport.CopyEntityProperties(firstSource, createdEntity);
                created++;

                if (deleteSource)
                {
                    foreach (int index in chain.SourceIndices)
                    {
                        var src = (Entity)tr.GetObject(sourceIds[index], OpenMode.ForWrite);
                        if (!src.IsErased)
                        {
                            src.Erase();
                            erased++;
                        }
                    }
                }

                results.Add(new
                {
                    joined = true,
                    new_handle = createdEntity.Handle.ToString(),
                    source_handles = memberHandles,
                    pieces = chain.SourceIndices.Count,
                    vertex_count = chain.Shape.Count,
                    closed = chain.Shape.Closed,
                    closed_up = chain.ClosedUp,
                    is_3d = chain.Shape.Is3d,
                });
            }

            if (created == 0)
                return Task.FromResult(ToolResult.Ok(new
                {
                    fuzz,
                    polylines_in = shapes.Count,
                    chains_created = 0,
                    sources_erased = 0,
                    results,
                    warnings,
                    note = "אף שני פוליליינים לא נגעו זה בזה בטווח ה-fuzz — נסי fuzz גדול יותר",
                }));

            return Task.FromResult(ToolResult.Ok(new
            {
                fuzz,
                polylines_in = shapes.Count,
                chains_created = created,
                sources_erased = erased,
                results,
                warnings,
            }));
        }
    }
}
