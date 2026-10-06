using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace MahodAI.Civil3D.Plugin.Tools.Creation;

/// <summary>Creates and reads back every requested sample line in the executor-owned transaction.</summary>
public class CreateSampleLineGroupTool : DrawingToolBase
{
    public override string Name => "create_sample_line_group";
    public override string Description => "Creates a complete sample-line group with verified stations and requested left/right extents; never queues commands.";
    public override string Category => ToolCategories.Creation;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public override Task<ToolResult> ExecuteAsync(Transaction tr, CivilDocument civilDoc,
        JsonElement parameters, ToolCache cache, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            var groupName = GetRequiredStringParam(parameters, "group_name");
            var interval = GetDoubleParam(parameters, "interval") ?? throw new ArgumentException("A positive interval is required.");
            // Preserve the existing parameter defaults; no new engineering dimensions.
            var left = GetDoubleParam(parameters, "left_extent") ?? 20.0;
            var right = GetDoubleParam(parameters, "right_extent") ?? 20.0;
            if (civilDoc == null) throw new InvalidOperationException("Not a Civil 3D document.");
            var matches = civilDoc.GetAlignmentIds().Cast<ObjectId>()
                .Select(id => (CivilAlignment)tr.GetObject(id, OpenMode.ForRead))
                .Where(alignment => string.Equals(alignment.Name, alignmentName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException($"Expected one alignment named '{alignmentName}'; found {matches.Length}.");
            var alignment = matches[0];
            var start = GetDoubleParam(parameters, "start_station") ?? alignment.StartingStation;
            var end = GetDoubleParam(parameters, "end_station") ?? alignment.EndingStation;
            var stations = CreationReadbackPolicy.Stations(alignment.StartingStation, alignment.EndingStation,
                start, end, interval, left, right);
            foreach (ObjectId id in alignment.GetSampleLineGroupIds())
                if (string.Equals(((SampleLineGroup)tr.GetObject(id, OpenMode.ForRead)).Name, groupName, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Sample line group '{groupName}' already exists; no existing group is modified.");

            // Typed signatures verified in the local Civil 2026 XML and 2027 SDK metadata.
            var groupId = SampleLineGroup.Create(groupName, alignment.ObjectId);
            if (groupId.IsNull) throw new InvalidOperationException("SampleLineGroup.Create returned null.");
            var group = (SampleLineGroup)tr.GetObject(groupId, OpenMode.ForRead);
            if (group.ParentAlignmentId != alignment.ObjectId || group.Name != groupName)
                throw new InvalidOperationException("Created group identity/parent differs from the request.");
            var proofs = new List<CreationReadbackPolicy.SampleProof>();
            foreach (var station in stations)
            {
                ct.ThrowIfCancellationRequested();
                Point2d At(double offset)
                {
                    double x = double.NaN, y = double.NaN;
                    alignment.PointLocation(station, offset, ref x, ref y);
                    if (!double.IsFinite(x) || !double.IsFinite(y)) throw new InvalidOperationException("Alignment.PointLocation returned nonfinite geometry.");
                    return new Point2d(x, y);
                }
                var leftPoint = At(-left); var centerPoint = At(0); var rightPoint = At(right);
                var name = groupName + "-" + (proofs.Count + 1).ToString(CultureInfo.InvariantCulture);
                var id = SampleLine.Create(name, groupId, new Point2dCollection { leftPoint, rightPoint });
                if (id.IsNull) throw new InvalidOperationException($"SampleLine.Create returned null at {station:R}.");
                var line = (SampleLine)tr.GetObject(id, OpenMode.ForRead);
                var vertices = new List<CreationReadbackPolicy.Point>();
                for (var i = 0; i < line.Vertices.Count; i++)
                {
                    var point = line.Vertices[i].Location;
                    if (!double.IsFinite(point.Z)) throw new InvalidOperationException("Sample-line vertex Z is not finite.");
                    vertices.Add(new(point.X, point.Y));
                }
                var extents = line.GeometricExtents;
                var proof = new CreationReadbackPolicy.SampleProof(line.Handle.ToString(), line.Station, vertices,
                    new(extents.MinPoint.X, extents.MinPoint.Y, extents.MaxPoint.X, extents.MaxPoint.Y), line.GroupId == groupId);
                CreationReadbackPolicy.RequireSample(station, new(leftPoint.X,leftPoint.Y), new(centerPoint.X,centerPoint.Y),
                    new(rightPoint.X,rightPoint.Y), proof);
                proofs.Add(proof);
            }
            var groupHandles = group.GetSampleLineIds().Cast<ObjectId>()
                .Select(id => tr.GetObject(id, OpenMode.ForRead).Handle.ToString()).ToArray();
            CreationReadbackPolicy.RequireGroup(stations, proofs, groupHandles);
            ct.ThrowIfCancellationRequested();
            cache.RemoveByPattern("get_drawing_summary:");
            return Task.FromResult(ToolResult.Ok(new
            {
                success = true, group_name = groupName, group_handle = group.Handle.ToString(),
                alignment_name = alignment.Name, alignment_handle = alignment.Handle.ToString(), interval,
                left_extent = left, right_extent = right, start_station = start, end_station = end,
                line_count = proofs.Count, expected_line_count = stations.Count,
                lines = proofs.Select(line => new { handle = line.Handle, station = line.Station, vertices = line.Vertices, extents = line.Extents }).ToArray(),
                readback_verified = true, command_queued = false,
                message = $"Created and verified all {proofs.Count} sample lines in '{groupName}'; transaction commit is controlled by ToolExecutor."
            }));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            // ToolExecutor aborts Failed results, including every group/child made above.
            return Task.FromResult(ToolResult.Fail(error is ArgumentException ? ToolErrorCodes.InvalidParameters : ToolErrorCodes.ExecutionFailed,
                "Sample line group was not accepted; executor rollback required: " + error.Message, error.ToString()));
        }
    }
}
