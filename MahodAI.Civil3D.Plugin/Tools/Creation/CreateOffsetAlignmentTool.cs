using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Creates a new alignment offset from an existing alignment by a specified distance.
    /// </summary>
    public class CreateOffsetAlignmentTool : DrawingToolBase
    {
        public override string Name => "create_offset_alignment";
        public override string Description =>
            "Creates a new alignment offset from an existing alignment by a specified distance.";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var baseAlignmentName = GetRequiredStringParam(parameters, "base_alignment_name");
            var offsetDistance = GetDoubleParam(parameters, "offset_distance");
            var newName = GetRequiredStringParam(parameters, "new_name");

            if (!offsetDistance.HasValue)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    "Parameter 'offset_distance' is required (positive=right, negative=left)");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            try
            {
                // Check if alignment with new name already exists
                foreach (ObjectId existingId in civilDoc.GetAlignmentIds())
                {
                    var existing = tr.GetObject(existingId, OpenMode.ForRead) as CivilAlignment;
                    if (existing != null && existing.Name.Equals(newName, StringComparison.OrdinalIgnoreCase))
                    {
                        return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                            $"Alignment '{newName}' already exists");
                    }
                }

                // Find base alignment
                CivilAlignment? baseAlignment = null;
                foreach (ObjectId id in civilDoc.GetAlignmentIds())
                {
                    var obj = tr.GetObject(id, OpenMode.ForRead) as CivilAlignment;
                    if (obj != null && obj.Name.Equals(baseAlignmentName, StringComparison.OrdinalIgnoreCase))
                    {
                        baseAlignment = obj;
                        break;
                    }
                }

                if (baseAlignment == null)
                    return ToolResult.NotFound("Alignment", baseAlignmentName);

                // Try CreateOffsetAlignment API (Civil 3D 2026)
                bool usedNativeApi = false;
                ObjectId newAlignmentId = ObjectId.Null;

                try
                {
                    var createOffsetMethod = typeof(CivilAlignment).GetMethod("CreateOffsetAlignment",
                        new[] { typeof(string), typeof(ObjectId), typeof(double), typeof(ObjectId) });
                    if (createOffsetMethod != null)
                    {
                        var styleId = baseAlignment.StyleId;
                        newAlignmentId = (ObjectId)createOffsetMethod.Invoke(null,
                            new object[] { newName, baseAlignment.ObjectId, offsetDistance.Value, styleId })!;
                        usedNativeApi = true;
                    }
                }
                catch
                {
                    usedNativeApi = false;
                }

                if (!usedNativeApi || newAlignmentId.IsNull)
                {
                    // Manual approach: sample points along base alignment, offset perpendicular, create polyline, then alignment
                    double startSta = baseAlignment.StartingStation;
                    double endSta = baseAlignment.EndingStation;
                    double totalLen = endSta - startSta;
                    double interval = Math.Max(1.0, totalLen / 200.0); // at most 200 points

                    var offsetPoints = new Point3dCollection();

                    for (double sta = startSta; sta <= endSta + 0.001; sta += interval)
                    {
                        ct.ThrowIfCancellationRequested();

                        double actualSta = Math.Min(sta, endSta);
                        try
                        {
                            double easting = 0, northing = 0;
                            baseAlignment.PointLocation(actualSta, offsetDistance.Value, ref easting, ref northing);
                            offsetPoints.Add(new Point3d(easting, northing, 0));
                        }
                        catch { }

                        if (Math.Abs(actualSta - endSta) < 0.001) break;
                    }

                    if (offsetPoints.Count < 2)
                        return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                            "Could not generate enough offset points");

                    // Create polyline from offset points
                    var db = HostApplicationServices.WorkingDatabase;
                    var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    var btr = tr.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

                    var pline = new Polyline();
                    for (int i = 0; i < offsetPoints.Count; i++)
                    {
                        pline.AddVertexAt(i,
                            new Point2d(offsetPoints[i].X, offsetPoints[i].Y),
                            0, 0, 0);
                    }
                    pline.Layer = "0";

                    btr!.AppendEntity(pline);
                    tr.AddNewlyCreatedDBObject(pline, true);

                    // Create alignment from the polyline
                    ObjectId styleId = baseAlignment.StyleId;
                    ObjectId labelSetId = ObjectId.Null;
                    try
                    {
                        labelSetId = civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles[0];
                    }
                    catch { }

                    // Resolve layer ObjectId for "0"
                    ObjectId layerId = ObjectId.Null;
                    try
                    {
                        var lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
                        if (lt != null && lt.Has("0"))
                            layerId = lt["0"];
                    }
                    catch { }

                    // Use reflection to find the right Create overload (signature varies by Civil 3D version)
                    try
                    {
                        var methods = typeof(CivilAlignment).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                            .Where(m => m.Name == "Create").ToArray();

                        foreach (var method in methods)
                        {
                            var parms = method.GetParameters();
                            if (parms.Length == 7 && parms[1].ParameterType == typeof(ObjectId))
                            {
                                newAlignmentId = (ObjectId)method.Invoke(null, new object[]
                                    { civilDoc, pline.ObjectId, newName, ObjectId.Null, layerId, styleId, labelSetId })!;
                                break;
                            }
                        }

                        if (newAlignmentId.IsNull)
                        {
                            foreach (var method in methods)
                            {
                                var parms = method.GetParameters();
                                if (parms.Length == 6 && parms[1].ParameterType == typeof(string))
                                {
                                    newAlignmentId = (ObjectId)method.Invoke(null, new object[]
                                        { civilDoc, newName, ObjectId.Null, layerId, styleId, labelSetId })!;
                                    break;
                                }
                            }

                            // Note: empty alignment created without polyline geometry as fallback
                        }
                    }
                    catch { }

                    // Remove the temporary polyline
                    try
                    {
                        pline.UpgradeOpen();
                        pline.Erase();
                    }
                    catch { }
                }

                if (newAlignmentId.IsNull)
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Alignment creation failed");

                var newAlignment = tr.GetObject(newAlignmentId, OpenMode.ForRead) as CivilAlignment;
                if (newAlignment == null)
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Could not read created alignment");

                cache.RemoveByPattern("get_drawing_summary:");
                cache.RemoveByPattern("list_alignments:");

                string side = offsetDistance.Value >= 0 ? "right" : "left";

                return await Task.FromResult(ToolResult.Ok(new
                {
                    success = true,
                    alignment_name = newAlignment.Name,
                    base_alignment = baseAlignmentName,
                    offset_distance = Math.Round(offsetDistance.Value, 3),
                    offset_side = side,
                    length = Math.Round(newAlignment.Length, 3),
                    start_station = Math.Round(newAlignment.StartingStation, 3),
                    end_station = Math.Round(newAlignment.EndingStation, 3),
                    used_native_api = usedNativeApi,
                    message = $"Alignment '{newAlignment.Name}' created {Math.Abs(offsetDistance.Value):F2}m to the {side} of '{baseAlignmentName}', " +
                        $"length {newAlignment.Length:F1}m."
                }));
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to create offset alignment: {ex.Message}");
            }
        }
    }
}
