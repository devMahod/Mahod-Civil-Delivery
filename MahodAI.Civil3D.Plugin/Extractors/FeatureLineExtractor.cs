using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.Models;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace MahodAI.Civil3D.Plugin.Extractors
{
    /// <summary>
    /// Extracts 3D feature line data from Civil 3D drawings.
    /// ROW boundary feature lines are identified by layer name in DrawingSummaryExtractor.
    /// </summary>
    public class FeatureLineExtractor
    {
        private const int MaxSampleVertices = 10;

        /// <summary>
        /// Extract all feature lines from the drawing.
        /// </summary>
        public List<FeatureLineData> ExtractAll(Transaction tr, CivilDocument civilDoc)
        {
            var results = new List<FeatureLineData>();

            if (civilDoc == null)
                return results;

            try
            {
                // Feature lines are typically stored in sites
                foreach (ObjectId siteId in civilDoc.GetSiteIds())
                {
                    ExtractorHelpers.SafeExecute(() =>
                    {
                        var site = tr.GetObject(siteId, OpenMode.ForRead) as Site;
                        if (site == null)
                            return;

                        var featureLineIds = site.GetFeatureLineIds();
                        foreach (ObjectId flId in featureLineIds)
                        {
                            var flData = Extract(tr, flId, site.Name);
                            if (flData != null)
                                results.Add(flData);
                        }
                    }, $"ExtractSiteFeatureLines_{siteId}");
                }

                // Also check for standalone feature lines (not in a site)
                ExtractStandaloneFeatureLines(tr, results);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FeatureLineExtractor.ExtractAll error: {ex.Message}");
            }

            return results;
        }

        /// <summary>
        /// Extract data for a single feature line.
        /// </summary>
        public FeatureLineData? Extract(Transaction tr, ObjectId featureLineId, string? siteName = null)
        {
            try
            {
                var featureLine = tr.GetObject(featureLineId, OpenMode.ForRead, false) as FeatureLine;
                if (featureLine == null)
                    return null;

                var data = new FeatureLineData
                {
                    Name = featureLine.Name ?? string.Empty,
                    SiteName = siteName,
                    Layer = featureLine.Layer ?? string.Empty
                };

                // Get style via reflection
                ExtractorHelpers.SafeExecute(() =>
                {
                    if (!featureLine.StyleId.IsNull)
                    {
                        var style = tr.GetObject(featureLine.StyleId, OpenMode.ForRead);
                        data.Style = ExtractorHelpers.GetPropertyValue<string>(style, "Name") ?? string.Empty;
                    }
                }, "GetFeatureLineStyle");

                // Extract geometry
                ExtractGeometry(featureLine, data);

                // Extract sample vertices
                ExtractSampleVertices(featureLine, data);

                // Calculate grade statistics
                CalculateGradeStatistics(featureLine, data);

                return data;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FeatureLineExtractor.Extract error: {ex.Message}");
                return null;
            }
        }

        private void ExtractStandaloneFeatureLines(Transaction tr, List<FeatureLineData> results)
        {
            try
            {
                var doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc == null)
                    return;

                var db = doc.Database;
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                var existingNames = new HashSet<string>(results.Select(r => r.Name));

                foreach (ObjectId id in ms)
                {
                    ExtractorHelpers.SafeExecute(() =>
                    {
                        var featureLine = tr.GetObject(id, OpenMode.ForRead, false) as FeatureLine;
                        if (featureLine != null && !existingNames.Contains(featureLine.Name))
                        {
                            var flData = Extract(tr, id, null);
                            if (flData != null)
                                results.Add(flData);
                        }
                    }, $"CheckStandaloneFeatureLine_{id}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractStandaloneFeatureLines error: {ex.Message}");
            }
        }

        private void ExtractGeometry(FeatureLine featureLine, FeatureLineData data)
        {
            ExtractorHelpers.SafeExecute(() =>
            {
                data.Length2D = featureLine.Length2D;
                data.Length3D = featureLine.Length3D;
                data.Length = featureLine.Length2D; // Primary length is 2D
            }, "GetFeatureLineLength");

            ExtractorHelpers.SafeExecute(() =>
            {
                data.IsClosed = featureLine.Closed;
            }, "GetFeatureLineClosed");

            // Get points using reflection-safe approach
            ExtractorHelpers.SafeExecute(() =>
            {
                Point3dCollection? points = null;

                // Try different methods to get points
                ExtractorHelpers.SafeExecute(() =>
                {
                    // Try GetPoints with enum parameter
                    var getPointsMethod = featureLine.GetType().GetMethod("GetPoints");
                    if (getPointsMethod != null)
                    {
                        var parameters = getPointsMethod.GetParameters();
                        if (parameters.Length == 1)
                        {
                            // Try to get FeatureLinePointType.AllPoints value
                            var enumType = parameters[0].ParameterType;
                            if (enumType.IsEnum)
                            {
                                var allPointsValue = Enum.Parse(enumType, "AllPoints");
                                points = getPointsMethod.Invoke(featureLine, new object[] { allPointsValue }) as Point3dCollection;
                            }
                        }
                        else if (parameters.Length == 0)
                        {
                            points = getPointsMethod.Invoke(featureLine, null) as Point3dCollection;
                        }
                    }
                }, "TryGetPoints");

                // Fallback: try to access points via Polyline base
                if (points == null || points.Count == 0)
                {
                    ExtractorHelpers.SafeExecute(() =>
                    {
                        points = new Point3dCollection();
                        var numVertsProp = featureLine.GetType().GetProperty("NumberOfVertices");
                        int numVerts = numVertsProp != null ? Convert.ToInt32(numVertsProp.GetValue(featureLine)) : 0;
                        for (int i = 0; i < numVerts; i++)
                        {
                            try
                            {
                                var pt = featureLine.GetPointAtParameter(i);
                                points.Add(pt);
                            }
                            catch { }
                        }
                    }, "TryGetPointsFromVertices");
                }

                if (points == null || points.Count == 0)
                    return;

                data.VertexCount = points.Count;

                // Get elevation point count if available
                ExtractorHelpers.SafeExecute(() =>
                {
                    var elevProp = featureLine.GetType().GetProperty("ElevationPointsCount");
                    if (elevProp != null)
                        data.ElevationPointCount = Convert.ToInt32(elevProp.GetValue(featureLine));
                    else
                        data.ElevationPointCount = points.Count;
                }, "GetElevationPointsCount");

                // Check if feature line has meaningful 3D elevations
                double minZ = double.MaxValue, maxZ = double.MinValue;
                bool hasVariedZ = false;
                double firstZ = points[0].Z;

                foreach (Point3d point in points)
                {
                    if (point.Z < minZ) minZ = point.Z;
                    if (point.Z > maxZ) maxZ = point.Z;
                    if (Math.Abs(point.Z - firstZ) > 0.001)
                        hasVariedZ = true;
                }

                data.Has3DElevations = hasVariedZ || (Math.Abs(minZ) > 0.001 || Math.Abs(maxZ) > 0.001);

                if (minZ < double.MaxValue)
                {
                    data.ElevationRange = new ElevationRange
                    {
                        Min = minZ,
                        Max = maxZ,
                        Mean = (minZ + maxZ) / 2.0
                    };
                }
            }, "GetFeatureLinePoints");
        }

        private void ExtractSampleVertices(FeatureLine featureLine, FeatureLineData data)
        {
            try
            {
                Point3dCollection? points = GetFeatureLinePoints(featureLine);
                if (points == null || points.Count == 0)
                    return;

                // Sample key vertices (first few and last few)
                var sampleIndices = new List<int>();

                if (points.Count <= MaxSampleVertices)
                {
                    sampleIndices = Enumerable.Range(0, points.Count).ToList();
                }
                else
                {
                    // First 3
                    for (int i = 0; i < 3 && i < points.Count; i++)
                        sampleIndices.Add(i);

                    // Middle
                    sampleIndices.Add(points.Count / 2);

                    // Last 3
                    for (int i = Math.Max(3, points.Count - 3); i < points.Count; i++)
                        if (!sampleIndices.Contains(i))
                            sampleIndices.Add(i);
                }

                double runningDistance = 0;
                Point3d? prevPoint = null;

                for (int i = 0; i < points.Count; i++)
                {
                    var point = points[i];

                    // Calculate running distance
                    if (prevPoint.HasValue)
                    {
                        double dx = point.X - prevPoint.Value.X;
                        double dy = point.Y - prevPoint.Value.Y;
                        runningDistance += Math.Sqrt(dx * dx + dy * dy);
                    }

                    if (sampleIndices.Contains(i))
                    {
                        var vertex = new FeatureLineVertex
                        {
                            Index = i,
                            X = point.X,
                            Y = point.Y,
                            Elevation = point.Z,
                            DistanceFromStart = ExtractorHelpers.Round(runningDistance, 2)
                        };

                        // Calculate grade to next point
                        if (i < points.Count - 1)
                        {
                            var nextPoint = points[i + 1];
                            double dx = nextPoint.X - point.X;
                            double dy = nextPoint.Y - point.Y;
                            double dz = nextPoint.Z - point.Z;
                            double horizontalDist = Math.Sqrt(dx * dx + dy * dy);

                            if (horizontalDist > 0.001)
                            {
                                vertex.GradeToNext = ExtractorHelpers.Round((dz / horizontalDist) * 100.0, 2);
                            }
                        }

                        data.SampleVertices.Add(vertex);
                    }

                    prevPoint = point;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractSampleVertices error: {ex.Message}");
            }
        }

        private void CalculateGradeStatistics(FeatureLine featureLine, FeatureLineData data)
        {
            try
            {
                Point3dCollection? points = GetFeatureLinePoints(featureLine);
                if (points == null || points.Count < 2)
                    return;

                var grades = new List<double>();
                double totalHorizontalDist = 0;
                double totalElevChange = 0;

                for (int i = 0; i < points.Count - 1; i++)
                {
                    var p1 = points[i];
                    var p2 = points[i + 1];

                    double dx = p2.X - p1.X;
                    double dy = p2.Y - p1.Y;
                    double dz = p2.Z - p1.Z;
                    double horizontalDist = Math.Sqrt(dx * dx + dy * dy);

                    if (horizontalDist > 0.01) // Minimum segment length
                    {
                        double grade = (dz / horizontalDist) * 100.0;
                        grades.Add(grade);
                        totalHorizontalDist += horizontalDist;
                        totalElevChange += dz;
                    }
                }

                if (grades.Count > 0)
                {
                    data.MinGradePercent = ExtractorHelpers.Round(grades.Min(), 2);
                    data.MaxGradePercent = ExtractorHelpers.Round(grades.Max(), 2);

                    if (totalHorizontalDist > 0.01)
                    {
                        data.AverageGradePercent = ExtractorHelpers.Round(
                            (totalElevChange / totalHorizontalDist) * 100.0, 2);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CalculateGradeStatistics error: {ex.Message}");
            }
        }

        private Point3dCollection? GetFeatureLinePoints(FeatureLine featureLine)
        {
            Point3dCollection? points = null;

            ExtractorHelpers.SafeExecute(() =>
            {
                // Try GetPoints with enum parameter
                var getPointsMethod = featureLine.GetType().GetMethod("GetPoints");
                if (getPointsMethod != null)
                {
                    var parameters = getPointsMethod.GetParameters();
                    if (parameters.Length == 1)
                    {
                        var enumType = parameters[0].ParameterType;
                        if (enumType.IsEnum)
                        {
                            var allPointsValue = Enum.Parse(enumType, "AllPoints");
                            points = getPointsMethod.Invoke(featureLine, new object[] { allPointsValue }) as Point3dCollection;
                        }
                    }
                    else if (parameters.Length == 0)
                    {
                        points = getPointsMethod.Invoke(featureLine, null) as Point3dCollection;
                    }
                }
            }, "TryGetPoints");

            // Fallback: try to access points via Polyline base
            if (points == null || points.Count == 0)
            {
                ExtractorHelpers.SafeExecute(() =>
                {
                    points = new Point3dCollection();
                    var numVertsProp = featureLine.GetType().GetProperty("NumberOfVertices");
                        int numVerts = numVertsProp != null ? Convert.ToInt32(numVertsProp.GetValue(featureLine)) : 0;
                    for (int i = 0; i < numVerts; i++)
                    {
                        try
                        {
                            var pt = featureLine.GetPointAtParameter(i);
                            points.Add(pt);
                        }
                        catch { }
                    }
                }, "TryGetPointsFromVertices");
            }

            return points;
        }
    }
}
