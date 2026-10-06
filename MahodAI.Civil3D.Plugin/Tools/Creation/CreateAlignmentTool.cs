using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Creates an Alignment (horizontal road centerline) from a list of 2D points.
    ///
    /// Strategy:
    /// 1. Primary: Managed API — create Polyline → PolylineOptions → Alignment.Create()
    /// 2. Fallback: LISP command — PLINE + CREATEALIGNMENTENTITIES (queued, may show dialog)
    ///
    /// Input: name, points [[x,y],...], design_speed (optional), layer (optional)
    /// Output: alignment name, length, number of points, start/end coordinates
    /// </summary>
    public class CreateAlignmentTool : DrawingToolBase
    {
        public override string Name => "create_alignment";
        public override string Description =>
            "Creates a horizontal alignment (road centerline) from a list of X,Y coordinate points. " +
            "The alignment is created as a polyline-based alignment in Civil 3D. " +
            "After creation, use create_profile to create EG and FG profiles along this alignment.";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""name"": {
                    ""type"": ""string"",
                    ""description"": ""Name for the alignment (use English only — Hebrew breaks in Civil 3D). Example: 'Road 80 - Alt A'""
                },
                ""points"": {
                    ""type"": ""array"",
                    ""items"": {
                        ""type"": ""array"",
                        ""items"": { ""type"": ""number"" },
                        ""minItems"": 2,
                        ""maxItems"": 2
                    },
                    ""description"": ""Array of [X, Y] coordinate pairs defining the alignment path. Minimum 2 points, recommended 8-15 for realistic road geometry.""
                },
                ""design_speed"": {
                    ""type"": ""number"",
                    ""description"": ""Design speed in km/h (e.g. 60, 80, 100, 120). Used for criteria and label display.""
                },
                ""layer"": {
                    ""type"": ""string"",
                    ""description"": ""Layer name for the alignment (default: '0')""
                }
            },
            ""required"": [""name"", ""points""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            // ── Parse parameters ──────────────────────────────────
            var name = GetRequiredStringParam(parameters, "name");
            var designSpeed = GetDoubleParam(parameters, "design_speed");
            var layer = GetStringParam(parameters, "layer") ?? "0";

            // Parse points array
            List<Point2d> points;
            try
            {
                points = ParsePoints(parameters);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Invalid points parameter: {ex.Message}");
            }

            if (points.Count < 2)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "At least 2 points are required to create an alignment");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // ── Check if alignment already exists ─────────────────
            var existingId = ObjectFinder.FindAlignment(civilDoc, tr, name);
            if (existingId != null)
            {
                var existing = tr.GetObject(existingId.Value, OpenMode.ForRead) as CivilDb.Alignment;
                if (existing != null)
                {
                    return ToolResult.Ok(new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["alignment_name"] = name,
                        ["already_existed"] = true,
                        ["length"] = Math.Round(existing.Length, 2),
                        ["message"] = $"Alignment '{name}' already exists (length: {existing.Length:F1}m). " +
                            "Skipping creation. Use modify_alignment_curve_radius to adjust curves."
                    });
                }
            }

            // ── Create alignment ──────────────────────────────────
            try
            {
                // Strategy 1: Managed API — Polyline → PolylineOptions → Alignment.Create
                bool createdViaApi = false;
                string creationMethod = "unknown";
                ObjectId alignmentId = ObjectId.Null;

                try
                {
                    alignmentId = CreateByManagedApi(tr, civilDoc, name, points, layer);
                    if (alignmentId != ObjectId.Null)
                    {
                        createdViaApi = true;
                        creationMethod = "API_PolylineOptions";
                    }
                }
                catch (Exception apiEx)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"CreateAlignmentTool API approach failed: {apiEx.Message}");
                }

                // Strategy 2: Fallback — LISP command
                if (!createdViaApi)
                {
                    try
                    {
                        bool queued = CreateByLispCommand(name, points, layer);
                        if (queued)
                        {
                            creationMethod = "LISP_CreateAlignmentEntities";

                            double approxLength = 0;
                            for (int i = 1; i < points.Count; i++)
                                approxLength += points[i].GetDistanceTo(points[i - 1]);

                            cache.RemoveByPattern("list_alignments:");
                            cache.RemoveByPattern("get_drawing_summary:");

                            return await Task.FromResult(ToolResult.Ok(new Dictionary<string, object>
                            {
                                ["success"] = true,
                                ["alignment_name"] = name,
                                ["creation_method"] = creationMethod,
                                ["command_queued"] = true,
                                ["point_count"] = points.Count,
                                ["approximate_length_m"] = Math.Round(approxLength, 2),
                                ["start_point"] = new { x = Math.Round(points[0].X, 3), y = Math.Round(points[0].Y, 3) },
                                ["end_point"] = new { x = Math.Round(points.Last().X, 3), y = Math.Round(points.Last().Y, 3) },
                                ["design_speed_kmh"] = designSpeed,
                                ["message"] = $"Alignment '{name}' creation command queued (LISP fallback). " +
                                    $"{points.Count} waypoints, ~{approxLength:F0}m length. " +
                                    "A dialog may appear — confirm with OK/Enter. " +
                                    "Next: use modify_alignment_design_speed to set design speed."
                            }));
                        }
                    }
                    catch (Exception lispEx)
                    {
                        return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                            $"Both API and LISP creation failed. API: see log. LISP: {lispEx.Message}");
                    }
                }

                // If API method succeeded, read back alignment data
                if (createdViaApi && alignmentId != ObjectId.Null)
                {
                    var alignment = tr.GetObject(alignmentId, OpenMode.ForRead) as CivilDb.Alignment;
                    if (alignment != null)
                    {
                        // Set design speed if provided
                        if (designSpeed.HasValue && designSpeed.Value > 0)
                        {
                            try
                            {
                                var writeAlign = tr.GetObject(alignmentId, OpenMode.ForWrite) as CivilDb.Alignment;
                                if (writeAlign != null)
                                {
                                    var speeds = writeAlign.DesignSpeeds;
                                    if (speeds != null)
                                    {
                                        speeds.Add(writeAlign.StartingStation, designSpeed.Value);
                                    }
                                }
                            }
                            catch
                            {
                                // Design speed setting can fail — non-critical
                            }
                        }

                        int entityCount = 0;
                        try { entityCount = alignment.Entities.Count; } catch { }

                        cache.RemoveByPattern("list_alignments:");
                        cache.RemoveByPattern("get_drawing_summary:");

                        return await Task.FromResult(ToolResult.Ok(new Dictionary<string, object>
                        {
                            ["success"] = true,
                            ["alignment_name"] = alignment.Name,
                            ["creation_method"] = creationMethod,
                            ["length_m"] = Math.Round(alignment.Length, 2),
                            ["start_station"] = Math.Round(alignment.StartingStation, 2),
                            ["end_station"] = Math.Round(alignment.EndingStation, 2),
                            ["point_count"] = points.Count,
                            ["entity_count"] = entityCount,
                            ["start_point"] = new { x = Math.Round(points[0].X, 3), y = Math.Round(points[0].Y, 3) },
                            ["end_point"] = new { x = Math.Round(points.Last().X, 3), y = Math.Round(points.Last().Y, 3) },
                            ["design_speed_kmh"] = designSpeed,
                            ["message"] = $"Alignment '{alignment.Name}' created successfully via managed API. " +
                                $"Length: {alignment.Length:F1}m, {entityCount} entities. " +
                                "Next: use create_profile to create EG and FG profiles."
                        }));
                    }
                }

                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "Alignment creation completed but alignment object could not be verified");
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to create alignment: {ex.Message}", ex.ToString());
            }
        }

        /// <summary>
        /// Parse the points array from JSON parameters.
        /// </summary>
        private static List<Point2d> ParsePoints(JsonElement parameters)
        {
            var points = new List<Point2d>();

            if (!parameters.TryGetProperty("points", out var pointsArray) ||
                pointsArray.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException("'points' must be an array of [x, y] coordinate pairs");
            }

            foreach (var pointElement in pointsArray.EnumerateArray())
            {
                if (pointElement.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("Each point must be an array [x, y]");

                var coords = new List<double>();
                foreach (var coord in pointElement.EnumerateArray())
                {
                    if (coord.ValueKind != JsonValueKind.Number)
                        throw new ArgumentException("Coordinates must be numbers");
                    coords.Add(coord.GetDouble());
                }

                if (coords.Count < 2)
                    throw new ArgumentException("Each point must have at least 2 coordinates [x, y]");

                points.Add(new Point2d(coords[0], coords[1]));
            }

            return points;
        }

        /// <summary>
        /// Create alignment via managed API:
        /// 1. Create Polyline in ModelSpace
        /// 2. Create PolylineOptions (addCurves=false, erasePolyline=true)
        /// 3. Alignment.Create(civilDoc, polylineOptions, name, layer, style, labelSet)
        /// </summary>
        private static ObjectId CreateByManagedApi(
            Transaction tr,
            CivilDocument civilDoc,
            string name,
            List<Point2d> points,
            string layer)
        {
            var db = HostApplicationServices.WorkingDatabase;

            // Create polyline from points
            var polyline = new Polyline();
            for (int i = 0; i < points.Count; i++)
            {
                polyline.AddVertexAt(i, points[i], 0, 0, 0);
            }

            // Set layer
            if (!string.IsNullOrEmpty(layer))
            {
                try
                {
                    var lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
                    if (lt != null && lt.Has(layer))
                        polyline.Layer = layer;
                }
                catch { }
            }

            // Add polyline to ModelSpace
            var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            if (bt == null)
                throw new InvalidOperationException("Cannot access BlockTable");

            var btr = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;
            if (btr == null)
                throw new InvalidOperationException("Cannot access ModelSpace");

            ObjectId polyId = btr.AppendEntity(polyline);
            tr.AddNewlyCreatedDBObject(polyline, true);

            // Create PolylineOptions
            var plops = new CivilDb.PolylineOptions();
            plops.AddCurvesBetweenTangents = false;
            plops.EraseExistingEntities = true;
            plops.PlineId = polyId;

            // Find the Create overload that accepts PolylineOptions via reflection
            // (signature varies across Civil 3D versions)
            ObjectId alignmentId = ObjectId.Null;

            var createMethods = typeof(CivilDb.Alignment)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Create" &&
                       m.GetParameters().Any(p => p.ParameterType == typeof(CivilDb.PolylineOptions)))
                .ToList();

            foreach (var method in createMethods)
            {
                var parms = method.GetParameters();
                try
                {
                    if (parms.Length == 7 &&
                        parms[1].ParameterType == typeof(CivilDb.PolylineOptions))
                    {
                        // Overload: (CivilDocument, PolylineOptions, string, string, string, string, string)
                        // or: (CivilDocument, PolylineOptions, string, ObjectId, ObjectId, ObjectId, ObjectId)
                        var args = new object[7];
                        args[0] = civilDoc;
                        args[1] = plops;
                        args[2] = name;

                        if (parms[3].ParameterType == typeof(string))
                        {
                            args[3] = layer;  // layer name
                            args[4] = "";     // style (default)
                            args[5] = "";     // label set (default)
                            args[6] = parms.Length > 6 && parms[6].ParameterType == typeof(string) ? "" : (object)ObjectId.Null;
                        }
                        else
                        {
                            // ObjectId-based overload
                            ObjectId layerObjId = db.Clayer;
                            try
                            {
                                var lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
                                if (lt != null && lt.Has(layer))
                                    layerObjId = lt[layer];
                            }
                            catch { }

                            ObjectId styleId = ObjectId.Null;
                            try
                            {
                                foreach (ObjectId id in civilDoc.Styles.AlignmentStyles)
                                { styleId = id; break; }
                            }
                            catch { }

                            ObjectId labelSetId = ObjectId.Null;
                            try
                            {
                                foreach (ObjectId id in civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles)
                                { labelSetId = id; break; }
                            }
                            catch { }

                            args[3] = ObjectId.Null; // siteId
                            args[4] = layerObjId;
                            args[5] = styleId;
                            args[6] = labelSetId;
                        }

                        alignmentId = (ObjectId)method.Invoke(null, args);
                        break;
                    }
                    else if (parms.Length == 6 &&
                             parms[1].ParameterType == typeof(CivilDb.PolylineOptions))
                    {
                        // 6-arg overload
                        var args = new object[6];
                        args[0] = civilDoc;
                        args[1] = plops;
                        args[2] = name;

                        if (parms[3].ParameterType == typeof(string))
                        {
                            args[3] = layer;
                            args[4] = "";
                            args[5] = "";
                        }
                        else
                        {
                            args[3] = ObjectId.Null;
                            args[4] = db.Clayer;
                            args[5] = ObjectId.Null;
                        }

                        alignmentId = (ObjectId)method.Invoke(null, args);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Alignment.Create overload ({parms.Length} params) failed: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            // If no PolylineOptions overload found, clean up polyline
            if (alignmentId == ObjectId.Null)
            {
                try
                {
                    var pline = tr.GetObject(polyId, OpenMode.ForWrite) as Entity;
                    pline?.Erase();
                }
                catch { }

                throw new InvalidOperationException(
                    $"No compatible Alignment.Create(PolylineOptions) overload found. " +
                    $"Discovered {createMethods.Count} candidate(s).");
            }

            return alignmentId;
        }

        /// <summary>
        /// Fallback: LISP command — PLINE + CREATEALIGNMENTENTITIES.
        /// Note: CREATEALIGNMENTENTITIES may show a dialog that the user must confirm.
        /// </summary>
        private static bool CreateByLispCommand(string name, List<Point2d> points, string layer)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager.MdiActiveDocument;

            if (doc == null)
                throw new InvalidOperationException("No active document");

            var ci = CultureInfo.InvariantCulture;

            var parts = new List<string>();
            parts.Add("(progn");

            // Set layer if specified
            if (!string.IsNullOrEmpty(layer) && layer != "0")
            {
                parts.Add($"  (command \"_-LAYER\" \"S\" \"{layer}\" \"\")");
            }

            // Draw polyline
            parts.Add("  (command \"_PLINE\"");
            foreach (var p in points)
            {
                parts.Add($"    (list {p.X.ToString(ci)} {p.Y.ToString(ci)})");
            }
            parts.Add("    \"\")");

            // Convert to alignment via CREATEALIGNMENTENTITIES
            // CMDDIA=0 pre-fills the dialog but it still appears — user presses Enter
            parts.Add("  (setvar \"CMDDIA\" 0)");
            parts.Add("  (command \"CREATEALIGNMENTENTITIES\" (entlast) \"\" \"\")");
            parts.Add("  (setvar \"CMDDIA\" 1)");
            parts.Add(")");

            string lispCmd = string.Join(" ", parts);
            doc.SendStringToExecute(lispCmd, true, false, false);
            return true;
        }
    }
}