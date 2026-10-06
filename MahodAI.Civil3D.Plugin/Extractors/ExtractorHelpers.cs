using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.Civil3D.Plugin.Models;

namespace MahodAI.Civil3D.Plugin.Extractors
{
    /// <summary>
    /// Shared utilities and helpers for extractors.
    /// </summary>
    public static class ExtractorHelpers
    {
        /// <summary>
        /// Safely execute an extraction operation with error handling.
        /// </summary>
        public static T SafeExecute<T>(Func<T> operation, T defaultValue, string operationName = "")
        {
            try
            {
                return operation();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Extraction error{(string.IsNullOrEmpty(operationName) ? "" : $" in {operationName}")}: {ex.Message}");
                return defaultValue;
            }
        }

        /// <summary>
        /// Safely execute an extraction operation with error handling (no return value).
        /// </summary>
        public static void SafeExecute(Action operation, string operationName = "")
        {
            try
            {
                operation();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Extraction error{(string.IsNullOrEmpty(operationName) ? "" : $" in {operationName}")}: {ex.Message}");
            }
        }

        /// <summary>
        /// Convert AutoCAD Point3d to Point2D model.
        /// </summary>
        public static Point2D ToPoint2D(Point3d point)
        {
            return new Point2D(point.X, point.Y);
        }

        /// <summary>
        /// Convert AutoCAD Point3d to Point3D model.
        /// </summary>
        public static Point3D ToPoint3D(Point3d point)
        {
            return new Point3D(point.X, point.Y, point.Z);
        }

        /// <summary>
        /// Convert AutoCAD Point2d to Point2D model.
        /// </summary>
        public static Point2D ToPoint2D(Point2d point)
        {
            return new Point2D(point.X, point.Y);
        }

        /// <summary>
        /// Create bounding box from extents.
        /// </summary>
        public static BoundingBox2D ToBoundingBox2D(Extents3d extents)
        {
            return new BoundingBox2D
            {
                MinX = extents.MinPoint.X,
                MinY = extents.MinPoint.Y,
                MaxX = extents.MaxPoint.X,
                MaxY = extents.MaxPoint.Y
            };
        }

        /// <summary>
        /// Create 3D bounding box from extents.
        /// </summary>
        public static BoundingBox3D ToBoundingBox3D(Extents3d extents)
        {
            return new BoundingBox3D
            {
                MinX = extents.MinPoint.X,
                MinY = extents.MinPoint.Y,
                MinZ = extents.MinPoint.Z,
                MaxX = extents.MaxPoint.X,
                MaxY = extents.MaxPoint.Y,
                MaxZ = extents.MaxPoint.Z
            };
        }

        /// <summary>
        /// Convert radians to degrees.
        /// </summary>
        public static double RadiansToDegrees(double radians)
        {
            return radians * 180.0 / Math.PI;
        }

        /// <summary>
        /// Convert degrees to radians.
        /// </summary>
        public static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        /// <summary>
        /// Calculate bearing from direction (AutoCAD direction: radians from East, counter-clockwise).
        /// Returns degrees from North, clockwise.
        /// </summary>
        public static double DirectionToBearing(double directionRadians)
        {
            double bearingDegrees = 90.0 - RadiansToDegrees(directionRadians);

            // Normalize to 0-360
            while (bearingDegrees < 0) bearingDegrees += 360.0;
            while (bearingDegrees >= 360) bearingDegrees -= 360.0;

            return Math.Round(bearingDegrees, 2);
        }

        /// <summary>
        /// Calculate grade percent from elevation change and horizontal distance.
        /// </summary>
        public static double CalculateGradePercent(double elevationChange, double horizontalDistance)
        {
            if (Math.Abs(horizontalDistance) < 0.001)
                return 0;

            return (elevationChange / horizontalDistance) * 100.0;
        }

        /// <summary>
        /// Calculate K-value for vertical curves.
        /// K = L / |A| where A = grade change in percent.
        /// </summary>
        public static double CalculateKValue(double curveLength, double gradeInPercent, double gradeOutPercent)
        {
            double algebraicDiff = Math.Abs(gradeOutPercent - gradeInPercent);
            if (algebraicDiff < 0.001)
                return 0;

            return curveLength / algebraicDiff;
        }

        /// <summary>
        /// Calculate clothoid A parameter.
        /// A = sqrt(L * R) where L = length and R = radius at end.
        /// </summary>
        public static double CalculateClothoidA(double length, double radius)
        {
            if (length <= 0 || radius <= 0)
                return 0;

            return Math.Sqrt(length * radius);
        }

        /// <summary>
        /// Create a quality issue.
        /// </summary>
        public static QualityIssue CreateIssue(
            string id,
            string severity,
            string category,
            string title,
            string message,
            string? objectName = null,
            double? station = null,
            Point3D? location = null,
            string? suggestedFix = null)
        {
            return new QualityIssue
            {
                Id = id,
                Severity = severity,
                Category = category,
                Title = title,
                Message = message,
                ObjectName = objectName,
                Station = station,
                Location = location,
                SuggestedFix = suggestedFix
            };
        }

        /// <summary>
        /// Estimate coordinate system from bounding box.
        /// </summary>
        public static CoordinateSystem EstimateCoordinateSystem(BoundingBox2D? extents)
        {
            var crs = new CoordinateSystem { IsEstimated = true };

            if (extents == null)
            {
                crs.Name = "Unknown";
                return crs;
            }

            // Check for Israel TM Grid (EPSG:2039)
            if (extents.MinX > 100000 && extents.MinX < 300000 &&
                extents.MinY > 300000 && extents.MinY < 800000)
            {
                crs.Name = "Israel TM Grid";
                crs.Epsg = "EPSG:2039";
                crs.Description = "Israel Transverse Mercator (estimated from coordinates)";
                return crs;
            }

            // Check for UTM zones (typical X range 100000-900000)
            if (extents.MinX > 100000 && extents.MinX < 900000 &&
                extents.MinY > 0 && extents.MinY < 10000000)
            {
                crs.Name = "UTM (estimated)";
                crs.Description = "Universal Transverse Mercator (zone unknown)";
                return crs;
            }

            // Check for geographic coordinates
            if (Math.Abs(extents.MinX) <= 180 && Math.Abs(extents.MinY) <= 90)
            {
                crs.Name = "Geographic (WGS84?)";
                crs.Epsg = "EPSG:4326";
                crs.Description = "Geographic coordinates (possibly WGS84)";
                return crs;
            }

            crs.Name = "Unknown";
            crs.Description = "Could not determine coordinate system from coordinates";
            return crs;
        }

        /// <summary>
        /// Get property value via reflection (for API compatibility).
        /// </summary>
        public static T? GetPropertyValue<T>(object obj, string propertyName) where T : class
        {
            try
            {
                var prop = obj.GetType().GetProperty(propertyName);
                return prop?.GetValue(obj) as T;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Get property value via reflection (for value types).
        /// </summary>
        public static T? GetPropertyValueStruct<T>(object obj, string propertyName) where T : struct
        {
            try
            {
                var prop = obj.GetType().GetProperty(propertyName);
                var value = prop?.GetValue(obj);
                if (value is T typed)
                    return typed;
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Round to specified decimal places with null handling.
        /// </summary>
        public static double Round(double value, int decimals = 3)
        {
            return Math.Round(value, decimals);
        }

        /// <summary>
        /// Format large number for display.
        /// </summary>
        public static string FormatNumber(double value)
        {
            if (Math.Abs(value) >= 1000000)
                return $"{value / 1000000:F2}M";
            if (Math.Abs(value) >= 1000)
                return $"{value / 1000:F2}K";
            return $"{value:F2}";
        }
    }
}
