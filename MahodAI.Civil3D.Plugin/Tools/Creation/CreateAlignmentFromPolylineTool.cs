using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace MahodAI.Civil3D.Plugin.Tools.Creation;

/// <summary>Imports one exact source polyline; no empty-alignment or queued-command fallback.</summary>
public class CreateAlignmentFromPolylineTool : DrawingToolBase
{
    public override string Name => "create_alignment_from_polyline";
    public override string Description => "Creates and verifies alignment geometry from one model-space polyline on polyline_layer; specify polyline_handle when the layer is ambiguous. The source is preserved.";
    public override string Category => ToolCategories.Creation;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public override Task<ToolResult> ExecuteAsync(Transaction tr, CivilDocument civilDoc,
        JsonElement parameters, ToolCache cache, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var layer = GetRequiredStringParam(parameters, "polyline_layer");
            var name = GetRequiredStringParam(parameters, "alignment_name");
            var requestedHandle = GetStringParam(parameters, "polyline_handle")?.Trim();
            var styleName = GetStringParam(parameters, "style_name");
            var labelSetName = GetStringParam(parameters, "label_set_style_name");
            if (civilDoc == null) throw new InvalidOperationException("Not a Civil 3D document.");
            foreach (ObjectId id in civilDoc.GetAlignmentIds())
                if (string.Equals(((CivilAlignment)tr.GetObject(id, OpenMode.ForRead)).Name, name, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Alignment '{name}' already exists; no existing alignment is modified.");
            var db = HostApplicationServices.WorkingDatabase;
            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var model = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            var candidates = new List<Curve>();
            foreach (ObjectId id in model)
            {
                ct.ThrowIfCancellationRequested();
                var entity = tr.GetObject(id, OpenMode.ForRead);
                if (entity is Curve curve && (curve is Polyline || curve is Polyline2d || curve is Polyline3d) &&
                    string.Equals(curve.Layer, layer, StringComparison.OrdinalIgnoreCase)) candidates.Add(curve);
            }
            var handle = CreationReadbackPolicy.SelectSource(candidates.Select(curve => curve.Handle.ToString()).ToArray(), requestedHandle);
            var source = candidates.Single(curve => string.Equals(curve.Handle.ToString(), handle, StringComparison.OrdinalIgnoreCase));
            if (source is Polyline2d p2 && p2.PolyType != Poly2dType.SimplePoly ||
                source is Polyline3d p3 && p3.PolyType != Poly3dType.SimplePoly)
                throw new ArgumentException("Fitted/spline polylines require explicit geometry review; no conversion or smoothing is inferred.");
            var firstParameter = source.StartParam; var lastParameter = source.EndParam;
            if (!double.IsFinite(firstParameter) || !double.IsFinite(lastParameter) || lastParameter <= firstParameter ||
                lastParameter - firstParameter > 10000)
                throw new ArgumentException("The source polyline is empty or exceeds the 10000-segment readback limit.");
            var initialDistance = source.GetDistanceAtParameter(firstParameter);
            var length = source.GetDistanceAtParameter(lastParameter) - initialDistance;
            if (!double.IsFinite(length) || length <= 0) throw new ArgumentException("The source polyline has no finite positive length.");
            var sourcePoints = new List<Autodesk.AutoCAD.Geometry.Point3d>();
            var distances = new List<double>();
            var parametersRead = new List<double>();
            // Every source vertex and quarter/mid/three-quarter point of every
            // straight/bulged segment is checked. No approximation is used to CREATE.
            var count = checked((int)Math.Ceiling((lastParameter - firstParameter) * 4));
            for (var i = 0; i <= count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var parameter = Math.Min(lastParameter, firstParameter + i * 0.25);
                var point = source.GetPointAtParameter(parameter);
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z) ||
                    sourcePoints.Count > 0 && Math.Abs(point.Z - sourcePoints[0].Z) > CreationReadbackPolicy.Tolerance)
                    throw new ArgumentException("Source is not finite horizontal geometry; a 3D-to-plan projection is not silently approved.");
                parametersRead.Add(parameter); sourcePoints.Add(point);
                distances.Add(source.GetDistanceAtParameter(parameter) - initialDistance);
            }
            _ = source.GeometricExtents; // unavailable source extents must fail before creation

            var styles = civilDoc.Styles.AlignmentStyles;
            if (styles.Count == 0) throw new InvalidOperationException("No alignment style is available.");
            ObjectId styleId = styles[0]; // existing default retained, explicit names never fall back
            if (styleName != null)
            {
                var found = Enumerable.Range(0, styles.Count).Select(i => styles[i]).Where(id =>
                    string.Equals(((AlignmentStyle)tr.GetObject(id, OpenMode.ForRead)).Name, styleName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (found.Length != 1) throw new ArgumentException($"Expected one alignment style '{styleName}'; found {found.Length}.");
                styleId = found[0];
            }
            var labelSets = civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles;
            if (labelSets.Count == 0) throw new InvalidOperationException("No alignment label-set style is available.");
            ObjectId labelSetId = labelSets[0];
            if (labelSetName != null)
            {
                var found = Enumerable.Range(0, labelSets.Count).Select(i => labelSets[i]).Where(id =>
                    string.Equals(((AlignmentLabelSetStyle)tr.GetObject(id, OpenMode.ForRead)).Name, labelSetName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (found.Length != 1) throw new ArgumentException($"Expected one alignment label set '{labelSetName}'; found {found.Length}.");
                labelSetId = found[0];
            }
            var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!layers.Has("0")) throw new InvalidOperationException("Existing output layer 0 is unavailable.");
            var options = new PolylineOptions { PlineId = source.ObjectId, AddCurvesBetweenTangents = false, EraseExistingEntities = false };
            // Exact overload verified against both supported SDKs. No reflection or empty fallback.
            var alignmentId = CivilAlignment.Create(civilDoc, options, name, ObjectId.Null, layers["0"], styleId, labelSetId);
            if (alignmentId.IsNull) throw new InvalidOperationException("Polyline alignment creation returned null.");
            var alignment = (CivilAlignment)tr.GetObject(alignmentId, OpenMode.ForRead);
            if (alignment.Name != name || alignment.StyleId != styleId || alignment.LayerId != layers["0"])
                throw new InvalidOperationException("Created alignment identity/style/layer differs from the request.");
            var actualPoints = new List<CreationReadbackPolicy.Point>();
            foreach (var distance in distances)
            {
                ct.ThrowIfCancellationRequested();
                double x = double.NaN, y = double.NaN;
                alignment.PointLocation(alignment.StartingStation + distance, 0, ref x, ref y);
                actualPoints.Add(new(x,y));
            }
            var sourcePreserved = !source.IsErased && parametersRead.Select((parameter, i) =>
                source.GetPointAtParameter(parameter).DistanceTo(sourcePoints[i]) <= CreationReadbackPolicy.Tolerance).All(equal => equal);
            var extents = alignment.GeometricExtents;
            var bounds = new CreationReadbackPolicy.Bounds(extents.MinPoint.X,extents.MinPoint.Y,extents.MaxPoint.X,extents.MaxPoint.Y);
            CreationReadbackPolicy.RequireAlignment(length, alignment.Length, alignment.StartingStation, alignment.EndingStation,
                alignment.Entities.Count, sourcePoints.Select(point => new CreationReadbackPolicy.Point(point.X,point.Y)).ToArray(), actualPoints, bounds, sourcePreserved);
            ct.ThrowIfCancellationRequested();
            cache.RemoveByPattern("get_drawing_summary:"); cache.RemoveByPattern("list_alignments:");
            return Task.FromResult(ToolResult.Ok(new
            {
                success = true, alignment_name = alignment.Name, alignment_handle = alignment.Handle.ToString(),
                length = alignment.Length, start_station = alignment.StartingStation, end_station = alignment.EndingStation,
                entity_count = alignment.Entities.Count, source_layer = layer, source_handle = handle,
                source_preserved = true, readback_verified = true, checked_geometry_points = actualPoints.Count,
                extents = bounds, style_handle = styleId.Handle.ToString(), label_set_handle = labelSetId.Handle.ToString(),
                message = $"Alignment '{name}' imported from exact polyline {handle} and geometry/stations verified; transaction commit is controlled by ToolExecutor."
            }));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Task.FromResult(ToolResult.Fail(error is ArgumentException ? ToolErrorCodes.InvalidParameters : ToolErrorCodes.ExecutionFailed,
                "Alignment was not accepted; executor rollback required: " + error.Message, error.ToString()));
        }
    }
}
