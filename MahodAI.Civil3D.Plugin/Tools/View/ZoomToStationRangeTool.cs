using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Utilities;

namespace MahodAI.Civil3D.Plugin.Tools.View
{
    /// <summary>
    /// Zooms the active viewport to a station (or station range) along a named alignment.
    /// Backs the clickable "location" cells in the analysis report: clicking a finding
    /// focuses the drawing on the exact stretch of the alignment it refers to.
    /// </summary>
    public class ZoomToStationRangeTool : DrawingToolBase
    {
        public override string Name => "zoom_to_station_range";
        public override string Description => "Zooms the active viewport to a station, or station range, along a named alignment (focuses the drawing on a finding's location).";
        public override string Category => ToolCategories.Utility;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

        // Smallest viewport window (drawing units) so a short curve segment or a single
        // station still shows generous surrounding context rather than zooming to a pinpoint.
        private const double MinWindow = 200.0;

        // The focused stretch should fill roughly this fraction of the viewport — the rest
        // is context. Lower = more zoomed out.
        private const double SegmentViewportFraction = 0.45;

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""alignment_name"": { ""type"": ""string"" },
                ""station_start"": { ""type"": ""number"" },
                ""station_end"": { ""type"": ""number"", ""description"": ""Optional. Omit to focus on a single station."" },
                ""padding"": { ""type"": ""number"", ""description"": ""Extra padding around the segment as a fraction of its size (default 1.2)"" }
            },
            ""required"": [""alignment_name"", ""station_start""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc,
            JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            var alignmentName = GetStringParam(parameters, "alignment_name");
            var stationStart = GetDoubleParam(parameters, "station_start");
            var stationEnd = GetDoubleParam(parameters, "station_end");
            var padding = GetDoubleParam(parameters, "padding") ?? 1.2;

            if (string.IsNullOrEmpty(alignmentName))
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters, "יש לספק 'alignment_name'"));
            if (!stationStart.HasValue)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.InvalidParameters, "יש לספק 'station_start'"));
            if (civilDoc == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "לא מסמך Civil 3D"));

            var alignment = CivilObjectFinder.FindAlignmentByName(tr, civilDoc, alignmentName!);
            if (alignment == null)
                return Task.FromResult(ToolResult.NotFound("Alignment", alignmentName!));

            double startSta = alignment.StartingStation;
            double endSta = alignment.EndingStation;

            // Clamp to the alignment's valid station range (the report's stations should
            // already be within range, but a stale report against an edited alignment
            // could fall outside it).
            double s1 = Math.Max(startSta, Math.Min(endSta, stationStart.Value));
            double s2 = stationEnd.HasValue
                ? Math.Max(startSta, Math.Min(endSta, stationEnd.Value))
                : s1;
            if (s2 < s1) (s1, s2) = (s2, s1);

            // Sample centerline points across the range so the bounding box follows a
            // curved segment — endpoints alone miss the bulge of an arc.
            var pts = new List<Point2d>();
            int samples = s2 > s1 ? 24 : 0;
            for (int i = 0; i <= samples; i++)
            {
                double st = samples == 0 ? s1 : s1 + (s2 - s1) * i / samples;
                double x = 0, y = 0;
                try { alignment.PointLocation(st, 0, ref x, ref y); }
                catch { continue; }
                pts.Add(new Point2d(x, y));
            }

            if (pts.Count == 0)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "לא ניתן לחשב נקודות לאורך הציר בטווח התחנות שצוין"));

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            try { ApplyView(minX, minY, maxX, maxY, padding); }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"הפעלת תצוגת זום נכשלה: {ex.Message}"));
            }

            return Task.FromResult(ToolResult.Ok(new
            {
                alignment = alignment.Name,
                station_start = s1,
                station_end = s2,
                center = new[] { (minX + maxX) * 0.5, (minY + maxY) * 0.5 },
                points_sampled = pts.Count,
            }));
        }

        private static void ApplyView(double minX, double minY, double maxX, double maxY, double padding)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var editor = doc.Editor;
            double cx = (minX + maxX) * 0.5;
            double cy = (minY + maxY) * 0.5;

            double bboxW = maxX - minX;
            double bboxH = maxY - minY;

            // Scale the window to the segment so longer stretches zoom out more, with an
            // absolute floor so a single station / tiny curve still shows context.
            double segSpan = Math.Max(bboxW, bboxH);
            double target = Math.Max(segSpan / SegmentViewportFraction, MinWindow);
            double width = Math.Max(bboxW, target);
            double height = Math.Max(bboxH, target);

            double factor = 1.0 + Math.Max(0, padding);
            width *= factor;
            height *= factor;

            // Reconcile against the viewport aspect ratio. SetCurrentView fits whichever
            // dimension is relatively larger, so a thin/short sliver would otherwise blow
            // up on its near-zero axis and over-tighten the zoom.
            try
            {
                using var cur = editor.GetCurrentView();
                double ar = cur.Height > 0 ? cur.Width / cur.Height : 0;
                if (ar > 0)
                {
                    if (width / height < ar) width = height * ar;
                    else height = width / ar;
                }
            }
            catch { /* aspect correction is best-effort */ }

            using var view = new ViewTableRecord
            {
                CenterPoint = new Point2d(cx, cy),
                Width = width,
                Height = height,
            };
            editor.SetCurrentView(view);
        }
    }
}
