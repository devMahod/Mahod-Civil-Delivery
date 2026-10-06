using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Civil.ApplicationServices;

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>
    /// The click-loop vertex editor — the tool the whole port exists for. One call runs a whole
    /// interactive session: pick the polyline once, then click vertex after vertex (delete), or
    /// point after point on a segment (add), until Enter/Esc ends it. <c>U</c> undoes the last
    /// edit without leaving the loop, like PLTOOLS' PL-VxDel.
    ///
    /// Replaces PLTOOLS <c>PL-VxDel</c>, <c>PL-VxAdd</c>, <c>PL-VxMove</c>, <c>PL-Vx1</c> and
    /// MAHOD-PL <c>VDEL</c>/<c>VADD</c>/<c>HDEL</c> — including hatch contours, which PLTOOLS never
    /// supported at all (an associative hatch is edited through its boundary polyline, a plain one
    /// through its own loop).
    ///
    /// Interaction contract:
    ///   • The whole session is ONE undo step: Ctrl+Z afterwards restores the polyline as it was
    ///     before the first click, not one vertex at a time (the tool transaction commits once).
    ///   • Nothing is committed if the engineer cancels before the first edit — the result is
    ///     <see cref="ToolOutcome.Cancelled"/>, which the executor rolls back and the UI treats as
    ///     neutral rather than as an error.
    ///   • Each edit is flushed to the screen immediately, so the vertex visibly disappears under
    ///     the cursor instead of at the end of the session.
    /// </summary>
    public class EditPolylineVerticesTool : DrawingToolBase
    {
        public override string Name => "edit_polyline_vertices";

        public override string Description =>
            "Interactive vertex editing of a polyline or hatch contour: the user clicks vertices/points " +
            "in the drawing and each click is applied immediately, until Enter or Esc ends the session. " +
            "mode='delete' removes the vertex nearest each click, 'add' inserts a vertex where the user " +
            "clicks on a segment, 'move' moves a picked vertex to a picked location, 'set_start' makes a " +
            "picked vertex the start of a closed polyline. Works on LWPOLYLINE, old 2D POLYLINE, 3D " +
            "POLYLINE and Hatch (associative hatches are edited through their boundary polyline). " +
            "Omit entity_handle to let the user pick the polyline. Returns {edits, vertices_before, " +
            "vertices_after, entity_handle, notes[]}.";

        public override string Category => ToolCategories.Modification;

        /// <summary>A click loop is open-ended; the whole session shares this budget.</summary>
        public override TimeSpan Timeout => TimeSpan.FromMinutes(15);

        /// <summary>Modal picks — the executor flushes pending chat renders before starting.</summary>
        public bool IsInteractive => true;

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""mode"": {
                    ""type"": ""string"",
                    ""enum"": [""delete"", ""add"", ""move"", ""set_start""],
                    ""description"": ""delete = remove clicked vertices; add = insert a vertex where clicked on a segment; move = move a clicked vertex; set_start = make a clicked vertex the start of a closed polyline""
                },
                ""entity_handle"": {
                    ""type"": ""string"",
                    ""description"": ""Hex handle of the polyline/hatch. Omit to have the user pick it in the drawing.""
                },
                ""max_edits"": {
                    ""type"": ""integer"",
                    ""description"": ""Safety cap on edits in one session (default 500).""
                },
                ""preserve_arcs"": {
                    ""type"": ""boolean"",
                    ""description"": ""delete mode: when true (default) deleting a vertex that touches an arc segment straightens the merged segment, as in the LISP originals.""
                }
            },
            ""required"": [""mode""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var mode = (GetStringParam(parameters, "mode") ?? "delete").Trim().ToLowerInvariant();
            if (mode is not ("delete" or "add" or "move" or "set_start"))
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"mode לא נתמך: '{mode}'. אפשרויות: delete, add, move, set_start"));

            int maxEdits = GetIntParam(parameters, "max_edits") ?? 500;
            if (maxEdits <= 0) maxEdits = 500;

            var doc = PolylineToolSupport.ActiveDocument;
            if (doc == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "אין שרטוט פעיל"));
            var ed = doc.Editor;
            var db = doc.Database;

            // ── resolve the target: given handle, or one pick ──
            ObjectId targetId;
            var handle = GetStringParam(parameters, "entity_handle");
            if (!string.IsNullOrEmpty(handle))
            {
                if (!PolylineToolSupport.TryResolveHandle(db, handle!, out targetId))
                    return Task.FromResult(ToolResult.NotFound("Polyline", handle!));
            }
            else
            {
                using var pickScope = new Utilities.InteractivePickScope();
                var picked = PolylineToolSupport.PickEntity(ed, PickPrompt(mode));
                if (picked == null || picked.Status != PromptStatus.OK)
                    return Task.FromResult(ToolResult.Cancelled(new { cancelled = true, edits = 0 }));
                targetId = picked.ObjectId;
            }

            if (!PolylineAdapter.TryOpen(tr, targetId, out var target, out var errCode, out var errMsg))
                return Task.FromResult(ToolResult.Fail(errCode ?? ToolErrorCodes.ExecutionFailed,
                    errMsg ?? "לא ניתן לערוך את האובייקט"));

            var shape = target!.Shape;
            int verticesBefore = shape.Count;
            var notes = new List<string>();
            if (!target.HatchId.IsNull && target.Kind != PlTargetKind.HatchLoop)
                notes.Add("Hatch אסוציאטיבי — נערך פוליליין הגבול וה-Hatch מתעדכן אחריו");
            if (target.Kind == PlTargetKind.HatchLoop)
                notes.Add("Hatch ללא גבול — נערך קו המתאר עצמו");

            int edits = 0;
            var undoStack = new Stack<List<PlVertex>>();

            using (var pickScope = new Utilities.InteractivePickScope())
            {
                while (edits < maxEdits && !ct.IsCancellationRequested)
                {
                    var step = StepPrompt(mode, edits);
                    ToolUiNotifier.Step($"🖱️ {step}");

                    var opts = new PromptPointOptions("\n" + step)
                    {
                        AllowNone = true,          // Enter ends the session
                    };
                    opts.Keywords.Add("Undo");
                    opts.Keywords.Add("eXit");

                    PromptPointResult res;
                    try
                    {
                        res = ed.GetPoint(opts);
                    }
                    catch (Exception ex)
                    {
                        return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                            $"בחירת נקודה נכשלה: {ex.Message}", ex.ToString()));
                    }

                    if (res.Status == PromptStatus.Keyword)
                    {
                        if (res.StringResult != null &&
                            res.StringResult.StartsWith("U", StringComparison.OrdinalIgnoreCase))
                        {
                            if (undoStack.Count == 0)
                            {
                                ed.WriteMessage("\nאין מה לבטל.");
                                continue;
                            }
                            shape.Vertices.Clear();
                            shape.Vertices.AddRange(undoStack.Pop());
                            if (!PolylineAdapter.TryWrite(tr, target, out var undoErr))
                                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                                    undoErr ?? "ביטול הפעולה נכשל"));
                            PolylineToolSupport.FlushGraphics(doc);
                            edits = Math.Max(0, edits - 1);
                            continue;
                        }
                        break;                      // eXit
                    }

                    if (res.Status != PromptStatus.OK) break;   // Enter / Esc / cancel

                    var world = PolylineToolSupport.UcsToWorld(ed, res.Value);
                    var local = target.ToLocal(world);
                    var snapshot = new List<PlVertex>(shape.Vertices);

                    var applied = ApplyEdit(mode, target, ed, local, notes, out bool endSession);
                    if (applied)
                    {
                        if (!PolylineAdapter.TryWrite(tr, target, out var writeErr))
                        {
                            // Put the shape back so the failed edit cannot leak into later ones.
                            shape.Vertices.Clear();
                            shape.Vertices.AddRange(snapshot);
                            return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                                writeErr ?? "עדכון הפוליליין נכשל"));
                        }
                        PolylineToolSupport.FlushGraphics(doc);
                        undoStack.Push(snapshot);
                        edits++;
                    }

                    if (endSession) break;
                }
            }

            if (edits >= maxEdits)
                notes.Add($"הגענו לתקרת {maxEdits} עריכות בסשן אחד — הריצי שוב אם צריך עוד");

            // Nothing changed → a neutral cancellation, never a committed no-op.
            if (edits == 0)
            {
                return Task.FromResult(ToolResult.Cancelled(new
                {
                    cancelled = true,
                    edits = 0,
                    mode,
                    entity_handle = PolylineToolSupport.HandleOf(tr, target.Id),
                    vertices_before = verticesBefore,
                    vertices_after = verticesBefore,
                    notes,
                }));
            }

            return Task.FromResult(ToolResult.Ok(new
            {
                mode,
                edits,
                entity_handle = PolylineToolSupport.HandleOf(tr, target.Id),
                entity_type = target.HebrewKind,
                vertices_before = verticesBefore,
                vertices_after = shape.Count,
                closed = shape.Closed,
                notes,
            }));
        }

        /// <summary>Applies one click. Returns true when the shape actually changed.</summary>
        private static bool ApplyEdit(
            string mode,
            PolylineTarget target,
            Editor ed,
            Creation.Routing.Pt2 local,
            List<string> notes,
            out bool endSession)
        {
            endSession = false;
            var shape = target.Shape;

            switch (mode)
            {
                case "delete":
                {
                    var hit = VertexPicker.NearestVertex(shape, local);
                    if (hit == null) return false;
                    if (shape.Count <= shape.MinVertices)
                    {
                        ed.WriteMessage($"\nנשארו {shape.Count} קודקודים — אי אפשר למחוק עוד.");
                        endSession = true;
                        return false;
                    }
                    VertexCleaner.RemoveVertexKeepingGeometry(shape, hit.Index);
                    ed.WriteMessage($"\nקודקוד {hit.Index + 1} נמחק ({shape.Count} נשארו).");
                    return true;
                }

                case "add":
                {
                    var hit = VertexPicker.NearestSegment(shape, local);
                    if (hit == null) return false;
                    if (VertexPicker.IsOnExistingVertex(hit))
                    {
                        ed.WriteMessage("\nהנקודה כמעט על קודקוד קיים — לחצי על אמצע המקטע.");
                        return false;
                    }
                    int index = VertexDensifier.InsertOnSegment(shape, hit.SegmentIndex, hit.T);
                    if (index < 0) return false;
                    ed.WriteMessage($"\nנוסף קודקוד במקטע {hit.SegmentIndex + 1} ({shape.Count} בסך הכול).");
                    return true;
                }

                case "move":
                {
                    var hit = VertexPicker.NearestVertex(shape, local);
                    if (hit == null) return false;

                    ToolUiNotifier.Step("🖱️ עכשיו לחצי על המקום החדש של הקודקוד");
                    var destOpts = new PromptPointOptions($"\nהמקום החדש של קודקוד {hit.Index + 1}: ")
                    {
                        AllowNone = false,
                        UseBasePoint = true,
                        BasePoint = target.ToWorld(hit.Point, shape.Vertices[hit.Index].Z),
                    };
                    var dest = ed.GetPoint(destOpts);
                    if (dest.Status != PromptStatus.OK) return false;

                    var destWorld = PolylineToolSupport.UcsToWorld(ed, dest.Value);
                    var destLocal = target.ToLocal(destWorld);
                    var v = shape.Vertices[hit.Index];
                    double z = shape.Is3d ? destWorld.Z : v.Z;
                    shape.Vertices[hit.Index] = v with { X = destLocal.X, Y = destLocal.Y, Z = z };
                    ed.WriteMessage($"\nקודקוד {hit.Index + 1} הוזז.");
                    return true;
                }

                case "set_start":
                {
                    var hit = VertexPicker.NearestVertex(shape, local);
                    if (hit == null) return false;
                    var result = PolylineOrientation.SetStartVertex(shape, hit.Index);
                    endSession = true;                       // one start vertex is enough
                    if (!result.Changed)
                    {
                        foreach (var note in result.Notes) notes.Add(note);
                        ed.WriteMessage("\n" + (result.Notes.Count > 0 ? result.Notes[0] : "לא בוצע שינוי."));
                        return false;
                    }
                    ed.WriteMessage($"\nקודקוד {hit.Index + 1} הוא עכשיו תחילת הפוליליין.");
                    return true;
                }

                default:
                    return false;
            }
        }

        private static string PickPrompt(string mode) => mode switch
        {
            "delete" => "בחרי פוליליין או Hatch למחיקת קודקודים:",
            "add" => "בחרי פוליליין או Hatch להוספת קודקודים:",
            "move" => "בחרי פוליליין להזזת קודקוד:",
            "set_start" => "בחרי פוליליין סגור לשינוי נקודת ההתחלה:",
            _ => "בחרי פוליליין:"
        };

        private static string StepPrompt(string mode, int edits)
        {
            string tail = edits > 0 ? $" (בוצעו {edits})" : string.Empty;
            return mode switch
            {
                "delete" => $"לחצי ליד קודקוד למחיקה — Enter לסיום, U לבטל{tail}:",
                "add" => $"לחצי על מקטע להוספת קודקוד — Enter לסיום, U לבטל{tail}:",
                "move" => $"לחצי על הקודקוד שברצונך להזיז — Enter לסיום, U לבטל{tail}:",
                "set_start" => "לחצי על הקודקוד שיהיה תחילת הפוליליין:",
                _ => "לחצי בשרטוט — Enter לסיום:"
            };
        }
    }
}
