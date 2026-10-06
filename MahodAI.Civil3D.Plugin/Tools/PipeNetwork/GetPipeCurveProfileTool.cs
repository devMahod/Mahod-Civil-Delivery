using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.PipeNetwork
{
    /// <summary>
    /// Samples a pipe's centerline as 3D points along its path.
    /// </summary>
    public class GetPipeCurveProfileTool : DrawingToolBase
    {
        public override string Name => "get_pipe_curve_profile";
        public override string Description =>
            "Samples a pipe's centerline as 3D points along its path.";
        public override string Category => ToolCategories.PipeNetwork;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var networkName = GetRequiredStringParam(parameters, "network_name");
            var pipeName = GetRequiredStringParam(parameters, "pipe_name");
            var numPoints = GetIntParam(parameters, "num_points") ?? 50;

            if (numPoints < 2) numPoints = 2;
            if (numPoints > 500) numPoints = 500;

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // Find the network
            Network? network = null;
            foreach (ObjectId id in civilDoc.GetPipeNetworkIds())
            {
                var obj = tr.GetObject(id, OpenMode.ForRead) as Network;
                if (obj != null && obj.Name.Equals(networkName, StringComparison.OrdinalIgnoreCase))
                {
                    network = obj;
                    break;
                }
            }

            if (network == null)
                return ToolResult.NotFound("PipeNetwork", networkName);

            // Find the pipe
            Pipe? pipe = null;
            foreach (ObjectId pipeId in network.GetPipeIds())
            {
                var p = tr.GetObject(pipeId, OpenMode.ForRead) as Pipe;
                if (p != null && p.Name.Equals(pipeName, StringComparison.OrdinalIgnoreCase))
                {
                    pipe = p;
                    break;
                }
            }

            if (pipe == null)
                return ToolResult.NotFound("Pipe", pipeName);

            try
            {
                var startPt = pipe.StartPoint;
                var endPt = pipe.EndPoint;
                double length3D = pipe.Length3D;

                var points = new List<object>();
                bool usedCurve2d = false;

                // Strategy 1: Try Curve2d.GetSamplePoints for actual pipe geometry
                try
                {
                    var curve2dProp = pipe.GetType().GetProperty("Curve2d");
                    object? curve2dObj = curve2dProp?.GetValue(pipe);

                    // Fallback: try Get2dFittedCurve() method
                    if (curve2dObj == null)
                    {
                        var curveMethod = pipe.GetType().GetMethod("Get2dFittedCurve");
                        if (curveMethod != null)
                            curve2dObj = curveMethod.Invoke(pipe, null);
                    }

                    if (curve2dObj != null)
                    {
                        // Try GetSamplePoints(int) on the Curve2d object
                        var getSampleMethod = curve2dObj.GetType().GetMethod("GetSamplePoints",
                            new[] { typeof(int) });
                        if (getSampleMethod != null)
                        {
                            var sampleResult = getSampleMethod.Invoke(curve2dObj, new object[] { numPoints });

                            // Result is Point2d[] or Point3d[] — iterate as array
                            if (sampleResult is Array sampleArray && sampleArray.Length > 0)
                            {
                                for (int i = 0; i < sampleArray.Length; i++)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    var p = sampleArray.GetValue(i);
                                    if (p == null) continue;

                                    var xProp = p.GetType().GetProperty("X");
                                    var yProp = p.GetType().GetProperty("Y");
                                    if (xProp == null || yProp == null) continue;

                                    double px = Convert.ToDouble(xProp.GetValue(p));
                                    double py = Convert.ToDouble(yProp.GetValue(p));

                                    // Interpolate Z between start and end
                                    double t = sampleArray.Length > 1 ? (double)i / (sampleArray.Length - 1) : 0;
                                    double z = startPt.Z + t * (endPt.Z - startPt.Z);
                                    double dist = t * length3D;

                                    points.Add(new
                                    {
                                        x = Math.Round(px, 3),
                                        y = Math.Round(py, 3),
                                        z = Math.Round(z, 3),
                                        distance_along = Math.Round(dist, 3)
                                    });
                                }

                                usedCurve2d = points.Count > 0;
                            }
                        }

                        // Strategy 2: If GetSamplePoints didn't work, try as AutoCAD Curve
                        if (!usedCurve2d && curve2dObj is Curve curve2dCurve)
                        {
                            double curveLen = curve2dCurve.GetDistanceAtParameter(curve2dCurve.EndParam);
                            for (int i = 0; i < numPoints; i++)
                            {
                                ct.ThrowIfCancellationRequested();

                                double t = (double)i / (numPoints - 1);
                                double dist = t * curveLen;

                                try
                                {
                                    var param = curve2dCurve.GetParameterAtDistance(dist);
                                    var pt2d = curve2dCurve.GetPointAtParameter(param);

                                    double z = startPt.Z + (endPt.Z - startPt.Z) * t;

                                    points.Add(new
                                    {
                                        x = Math.Round(pt2d.X, 3),
                                        y = Math.Round(pt2d.Y, 3),
                                        z = Math.Round(z, 3),
                                        distance_along = Math.Round(dist, 3)
                                    });
                                }
                                catch { }
                            }

                            usedCurve2d = points.Count > 0;

                            // Dispose the curve if we created it via method
                            try { curve2dCurve.Dispose(); } catch { }
                        }

                        // Dispose Curve2d if it's IDisposable and not an AutoCAD Curve already handled
                        if (!usedCurve2d || !(curve2dObj is Curve))
                        {
                            try { (curve2dObj as IDisposable)?.Dispose(); } catch { }
                        }
                    }
                }
                catch { }

                // Strategy 3: Fall back to linear interpolation
                if (!usedCurve2d)
                {
                    for (int i = 0; i < numPoints; i++)
                    {
                        ct.ThrowIfCancellationRequested();

                        double t = (double)i / (numPoints - 1);
                        double dist = t * length3D;

                        points.Add(new
                        {
                            x = Math.Round(startPt.X + (endPt.X - startPt.X) * t, 3),
                            y = Math.Round(startPt.Y + (endPt.Y - startPt.Y) * t, 3),
                            z = Math.Round(startPt.Z + (endPt.Z - startPt.Z) * t, 3),
                            distance_along = Math.Round(dist, 3)
                        });
                    }
                }

                return await Task.FromResult(ToolResult.Ok(new
                {
                    network_name = network.Name,
                    pipe_name = pipe.Name,
                    length_3d = Math.Round(length3D, 3),
                    length_2d = Math.Round(pipe.Length2D, 3),
                    slope = Math.Round(pipe.Slope, 6),
                    slope_percent = Math.Round(pipe.Slope * 100.0, 3),
                    start_point = new { x = Math.Round(startPt.X, 3), y = Math.Round(startPt.Y, 3), z = Math.Round(startPt.Z, 3) },
                    end_point = new { x = Math.Round(endPt.X, 3), y = Math.Round(endPt.Y, 3), z = Math.Round(endPt.Z, 3) },
                    num_points = points.Count,
                    points
                }));
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to get pipe curve profile: {ex.Message}");
            }
        }
    }
}
