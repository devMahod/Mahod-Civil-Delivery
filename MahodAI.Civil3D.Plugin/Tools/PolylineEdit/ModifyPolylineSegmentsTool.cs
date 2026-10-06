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
    /// Per-segment geometry: PLTOOLS <c>PL-L2A</c> (straight → arc), <c>PL-A2L</c> (arc → straight),
    /// <c>PL-NOARC</c> (approximate arcs with straights, in the original's five modes),
    /// <c>PL-SgWidth</c> (segment width) and <c>PL-SgInfo</c> (segment report — read-only).
    /// </summary>
    public class ModifyPolylineSegmentsTool : DrawingToolBase
    {
        public override string Name => "modify_polyline_segments";

        public override string Description =>
            "Per-segment polyline edits. action='line_to_arc' turns a straight segment into an arc of " +
            "`radius`; 'arc_to_line' replaces an arc with its chord; 'flatten_arcs' approximates every " +
            "arc with straights (flatten_mode: count | segment_length | chord_deviation | chord_length, " +
            "with flatten_value, and min_radius to leave flatter arcs alone); 'set_width' sets " +
            "start_width/end_width on one segment or all of them; 'info' returns each segment's length, " +
            "radius, centre, delta and widths without changing anything. segment_index is 0-based.";

        public override string Category => ToolCategories.Modification;

        /// <summary>Long enough for the engineer to pick the polyline when none was named.</summary>
        public override TimeSpan Timeout => TimeSpan.FromMinutes(5);

        /// <summary>Prompts for a pick when the agent named no target.</summary>
        public bool IsInteractive => true;

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""entity_handle"": { ""type"": ""string"", ""description"": ""Hex handle of the polyline."" },
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""line_to_arc"", ""arc_to_line"", ""flatten_arcs"", ""set_width"", ""info""]
                },
                ""segment_index"": { ""type"": ""integer"", ""description"": ""0-based segment. Required for line_to_arc/arc_to_line; optional for set_width and info (omit = all)."" },
                ""radius"": { ""type"": ""number"", ""description"": ""line_to_arc: arc radius. Must be at least half the segment's chord."" },
                ""clockwise"": { ""type"": ""boolean"", ""description"": ""line_to_arc: bulge direction. Default false (counter-clockwise)."" },
                ""major_arc"": { ""type"": ""boolean"", ""description"": ""line_to_arc: take the long way round. Default false."" },
                ""start_width"": { ""type"": ""number"", ""description"": ""set_width: width at the segment start."" },
                ""end_width"": { ""type"": ""number"", ""description"": ""set_width: width at the segment end."" },
                ""flatten_mode"": {
                    ""type"": ""string"",
                    ""enum"": [""count"", ""segment_length"", ""chord_deviation"", ""chord_length""],
                    ""description"": ""flatten_arcs: how to size the straights.""
                },
                ""flatten_value"": { ""type"": ""number"", ""description"": ""flatten_arcs: the number/length/deviation for flatten_mode."" },
                ""min_radius"": { ""type"": ""number"", ""description"": ""flatten_arcs: only flatten arcs with a radius up to this (PLTOOLS' 'min processed radius')."" }
            },
            ""required"": [""entity_handle"", ""action""]
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

            // No handle from the agent → use what the engineer selected, or ask.
            var id = PolylineToolSupport.ResolveSingleTargetOrPrompt(
                tr, db, parameters, "בחרי פוליליין לעריכת מקטעים:", out var targetSource);
            if (id.IsNull)
            {
                return Task.FromResult(targetSource == PolylineToolSupport.TargetSource.Cancelled
                    ? ToolResult.Cancelled(new { cancelled = true })
                    : ToolResult.Fail(ToolErrorCodes.ObjectNotFound, "לא נבחר פוליליין"));
            }
            var handle = PolylineToolSupport.HandleOf(tr, id);

            var action = (GetStringParam(parameters, "action") ?? string.Empty).Trim().ToLowerInvariant();
            if (!PolylineAdapter.TryOpen(tr, id, out var target, out var errCode, out var errMsg))
                return Task.FromResult(ToolResult.Fail(errCode ?? ToolErrorCodes.ExecutionFailed,
                    errMsg ?? "לא ניתן לערוך את האובייקט"));

            var shape = target!.Shape;
            int? segmentIndex = GetIntParam(parameters, "segment_index");

            // ── read-only report first (PL-SgInfo) ──
            if (action == "info")
            {
                var segments = new List<object>();
                int from = segmentIndex ?? 0;
                int to = segmentIndex ?? shape.SegmentCount - 1;
                if (from < 0 || to >= shape.SegmentCount || from > to)
                    return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                        $"segment_index מחוץ לתחום (0..{shape.SegmentCount - 1})"));

                for (int s = from; s <= to; s++)
                {
                    var info = PolylineOrientation.Describe(shape, s);
                    if (info == null) continue;
                    segments.Add(new
                    {
                        segment_index = info.SegmentIndex,
                        is_arc = info.IsArc,
                        length = Math.Round(info.Length, 6),
                        chord_length = Math.Round(info.ChordLength, 6),
                        radius = info.Radius.HasValue ? Math.Round(info.Radius.Value, 6) : (double?)null,
                        center_x = info.Center?.X,
                        center_y = info.Center?.Y,
                        delta_deg = info.DeltaDegrees.HasValue ? Math.Round(info.DeltaDegrees.Value, 6) : (double?)null,
                        sagitta = info.Sagitta.HasValue ? Math.Round(info.Sagitta.Value, 6) : (double?)null,
                        start_width = info.StartWidth,
                        end_width = info.EndWidth,
                    });
                }

                return Task.FromResult(ToolResult.Ok(new
                {
                    entity_handle = handle,
                    entity_type = target.HebrewKind,
                    vertex_count = shape.Count,
                    segment_count = shape.SegmentCount,
                    closed = shape.Closed,
                    total_length = Math.Round(shape.TotalLength(), 6),
                    has_arcs = shape.HasArcs,
                    segments,
                }));
            }

            PlEditResult result;
            switch (action)
            {
                case "line_to_arc":
                {
                    if (!segmentIndex.HasValue)
                        return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                            "חובה לספק segment_index"));
                    var radius = GetDoubleParam(parameters, "radius");
                    if (!radius.HasValue || radius.Value <= 0)
                        return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                            "חובה לספק radius גדול מאפס"));
                    result = SegmentEditor.LineToArc(
                        shape,
                        segmentIndex.Value,
                        radius.Value,
                        GetBoolParam(parameters, "clockwise"),
                        GetBoolParam(parameters, "major_arc"));
                    break;
                }

                case "arc_to_line":
                {
                    if (!segmentIndex.HasValue)
                        return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                            "חובה לספק segment_index"));
                    result = SegmentEditor.ArcToLine(shape, segmentIndex.Value);
                    break;
                }

                case "flatten_arcs":
                {
                    var modeStr = (GetStringParam(parameters, "flatten_mode") ?? "count").Trim().ToLowerInvariant();
                    var value = GetDoubleParam(parameters, "flatten_value");
                    if (!value.HasValue || value.Value <= 0)
                        return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                            "חובה לספק flatten_value גדול מאפס"));

                    BulgeMath.FlattenMode flattenMode;
                    switch (modeStr)
                    {
                        case "count": flattenMode = BulgeMath.FlattenMode.Count; break;
                        case "segment_length": flattenMode = BulgeMath.FlattenMode.SegmentLength; break;
                        case "chord_deviation": flattenMode = BulgeMath.FlattenMode.ChordDeviation; break;
                        case "chord_length": flattenMode = BulgeMath.FlattenMode.ChordLength; break;
                        default:
                            return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                                $"flatten_mode לא נתמך: '{modeStr}'"));
                    }

                    result = SegmentEditor.FlattenArcs(
                        shape, flattenMode, value.Value, GetDoubleParam(parameters, "min_radius"));
                    break;
                }

                case "set_width":
                {
                    var sw = GetDoubleParam(parameters, "start_width");
                    var ew = GetDoubleParam(parameters, "end_width");
                    if (!sw.HasValue && !ew.HasValue)
                        return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                            "חובה לספק start_width ו/או end_width"));
                    double startWidth = sw ?? ew!.Value;
                    double endWidth = ew ?? sw!.Value;
                    result = SegmentEditor.SetWidth(shape, segmentIndex, startWidth, endWidth);
                    break;
                }

                default:
                    return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                        $"action לא נתמך: '{action}'"));
            }

            if (!result.Changed)
            {
                // A refusal with a reason, not a silent success.
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.InvalidParameters,
                    result.Notes.Count > 0 ? string.Join("; ", result.Notes) : "לא בוצע שינוי"));
            }

            if (!PolylineAdapter.TryWrite(tr, target, out var writeErr))
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    writeErr ?? "עדכון הפוליליין נכשל"));

            return Task.FromResult(ToolResult.Ok(new
            {
                entity_handle = handle,
                entity_type = target.HebrewKind,
                action,
                segment_index = segmentIndex,
                vertices_before = result.VerticesBefore,
                vertices_after = shape.Count,
                vertices_added = result.Added,
                notes = result.Notes,
            }));
        }
    }
}
