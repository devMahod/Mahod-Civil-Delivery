using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Models
{
    /// <summary>
    /// Drawing metadata including file info and coordinate system.
    /// </summary>
    public class DrawingMetadata
    {
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string DwgVersion { get; set; } = string.Empty;
        public string FileSize { get; set; } = string.Empty;
        public string LastSaved { get; set; } = string.Empty;
        public string Units { get; set; } = string.Empty;
        public double UnitsToMeters { get; set; } = 1.0;
    }

    /// <summary>
    /// Coordinate system information.
    /// </summary>
    public class CoordinateSystem
    {
        public string Name { get; set; } = "Unknown";
        public string Epsg { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsEstimated { get; set; } = true;
    }

    /// <summary>
    /// 2D point coordinates.
    /// </summary>
    public class Point2D
    {
        public double X { get; set; }
        public double Y { get; set; }

        public Point2D() { }
        public Point2D(double x, double y) { X = x; Y = y; }
    }

    /// <summary>
    /// 3D point coordinates.
    /// </summary>
    public class Point3D
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }

        public Point3D() { }
        public Point3D(double x, double y, double z) { X = x; Y = y; Z = z; }
    }

    /// <summary>
    /// Bounding box extents.
    /// </summary>
    public class BoundingBox2D
    {
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
    }

    /// <summary>
    /// 3D bounding box extents.
    /// </summary>
    public class BoundingBox3D
    {
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MinZ { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
        public double MaxZ { get; set; }
    }

    /// <summary>
    /// Drawing-wide statistics.
    /// </summary>
    public class DrawingStatistics
    {
        public int TotalEntityCount { get; set; }
        public int LayerCount { get; set; }
        public int BlockCount { get; set; }
        public int CivilObjectCount { get; set; }
        public double TotalLengthMeters { get; set; }
        public double TotalAreaSquareMeters { get; set; }
        public BoundingBox2D? Extents { get; set; }
        public Dictionary<string, int> EntityTypeCounts { get; set; } = new();
    }

    /// <summary>
    /// Quality issue found in drawing.
    /// </summary>
    public class QualityIssue
    {
        public string Id { get; set; } = string.Empty;
        public string Severity { get; set; } = "Info";  // Info, Warning, Error, Critical
        public string Category { get; set; } = string.Empty;  // Z-Anomaly, Xref, Surface, Alignment, etc.
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? ObjectName { get; set; }
        public double? Station { get; set; }
        public Point3D? Location { get; set; }
        public string? SuggestedFix { get; set; }
    }

    /// <summary>
    /// Layer information.
    /// </summary>
    public class LayerData
    {
        public string Name { get; set; } = string.Empty;
        public int ColorAci { get; set; }
        public bool IsOff { get; set; }
        public bool IsFrozen { get; set; }
        public bool IsLocked { get; set; }
        public bool IsPlottable { get; set; }
        public int EntityCount { get; set; }
        public Dictionary<string, int> EntityTypes { get; set; } = new();
    }

    /// <summary>
    /// Layer statistics summary.
    /// </summary>
    public class LayerStatistics
    {
        public int TotalCount { get; set; }
        public int OffCount { get; set; }
        public int FrozenCount { get; set; }
        public int LockedCount { get; set; }
        public int EmptyCount { get; set; }
        public int ActiveCount { get; set; }
        public List<string> TopLayers { get; set; } = new();
    }

    /// <summary>
    /// External reference (Xref) information.
    /// </summary>
    public class XrefData
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Type { get; set; } = "Attach";  // Attach, Overlay
        public bool IsLoaded { get; set; }
        public bool IsResolved { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    /// <summary>
    /// Block usage information.
    /// </summary>
    public class BlockData
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
        public List<string> Layers { get; set; } = new();
        public Dictionary<string, List<string>> AttributeSamples { get; set; } = new();
    }

    /// <summary>
    /// Elevation range statistics.
    /// </summary>
    public class ElevationRange
    {
        public double Min { get; set; }
        public double Max { get; set; }
        public double Mean { get; set; }
        public double Range => Max - Min;
    }

    /// <summary>
    /// Station range along an alignment.
    /// </summary>
    public class StationRange
    {
        public double Start { get; set; }
        public double End { get; set; }
        public double Length => End - Start;
    }
}
