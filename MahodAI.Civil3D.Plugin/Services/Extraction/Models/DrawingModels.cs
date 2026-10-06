using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Services.Extraction.Models
{
    #region Alignment Models

    /// <summary>
    /// Basic alignment information
    /// </summary>
    public class AlignmentInfo
    {
        public string Name { get; set; } = string.Empty;
        public double Length { get; set; }
        public double MinRadius { get; set; }
        public string Style { get; set; } = string.Empty;
        public int CurveCount { get; set; }
        public int SpiralCount { get; set; }
    }

    /// <summary>
    /// Extended alignment data with curves, spirals, design speeds
    /// </summary>
    public class AlignmentDetailData
    {
        public string Name { get; set; } = string.Empty;
        public double Length { get; set; }
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public double MinRadius { get; set; }
        public List<double> DesignSpeeds { get; set; } = new();
        public List<HorizontalCurveData> HorizontalCurves { get; set; } = new();
        public List<VerticalCurveData> VerticalCurves { get; set; } = new();
    }

    /// <summary>
    /// Horizontal curve data (Arc or Spiral)
    /// </summary>
    public class HorizontalCurveData
    {
        public string Type { get; set; } = string.Empty; // Arc, Spiral, SCS_*, STS_*, etc.
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public double Length { get; set; }
        public double Radius { get; set; }
        public double RadiusIn { get; set; }  // For spirals
        public double RadiusOut { get; set; } // For spirals
        public double AValue { get; set; }    // Spiral A-value: sqrt(Length * RadiusOut)
        public string Direction { get; set; } = string.Empty; // CW, CCW
        public bool IsClockwise { get; set; }
    }

    /// <summary>
    /// Vertical curve data
    /// </summary>
    public class VerticalCurveData
    {
        public double Station { get; set; }
        public double Length { get; set; }
        public double GradeIn { get; set; }
        public double GradeOut { get; set; }
        public string CurveType { get; set; } = string.Empty; // Crest, Sag
        public double Radius { get; set; }
        public double KValue { get; set; }
    }

    #endregion

    #region Superelevation Models

    /// <summary>
    /// Superelevation data for an alignment
    /// </summary>
    public class SuperElevationInfo
    {
        public string AlignmentName { get; set; } = string.Empty;
        public List<SuperElevationCurveData> Curves { get; set; } = new();
    }

    /// <summary>
    /// Superelevation data for a single curve
    /// </summary>
    public class SuperElevationCurveData
    {
        public double CurveStation { get; set; }
        public double CurveRadius { get; set; }
        public double FullSuperRate { get; set; }      // Max superelevation rate (%)
        public double LeftSlope { get; set; }          // Left edge slope (%)
        public double RightSlope { get; set; }         // Right edge slope (%)
        public string CurveDirection { get; set; } = string.Empty; // CW or CCW
        public List<SuperElevationStation> Stations { get; set; } = new();
    }

    /// <summary>
    /// Superelevation at a critical station
    /// </summary>
    public class SuperElevationStation
    {
        public double Station { get; set; }
        public string StationType { get; set; } = string.Empty; // LevelCrown, NormalCrown, FullSuper, etc.
        public double LeftSlope { get; set; }
        public double RightSlope { get; set; }
    }

    #endregion

    #region Profile Models

    /// <summary>
    /// Profile data
    /// </summary>
    public class ProfileInfo
    {
        public string AlignmentName { get; set; } = string.Empty;
        public string ProfileName { get; set; } = string.Empty;
        public string ProfileType { get; set; } = string.Empty; // Design, ExistingGround, Surface
        public double MinGradePercent { get; set; }
        public double MaxGradePercent { get; set; }
        public List<ProfileSegmentInfo> Segments { get; set; } = new();
    }

    /// <summary>
    /// Profile segment data
    /// </summary>
    public class ProfileSegmentInfo
    {
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public double GradePercent { get; set; }
        public string SegmentType { get; set; } = string.Empty; // Tangent, VerticalCurve
        public double Length { get; set; }
        public double? VerticalCurveLength { get; set; }
        public double? GradeIn { get; set; }
        public double? GradeOut { get; set; }
        public string? VerticalCurveType { get; set; } // Crest, Sag
        public double? KValue { get; set; }
        public double? Radius { get; set; }
        public double? PviStation { get; set; }
        public double? PviElevation { get; set; }
    }

    #endregion

    #region Layer and Block Models

    /// <summary>
    /// Layer information
    /// </summary>
    public class LayerInfo
    {
        public string Name { get; set; } = string.Empty;
        public int ColorAci { get; set; }
        public bool IsOff { get; set; }
        public bool IsFrozen { get; set; }
        public bool IsLocked { get; set; }
        public bool IsPlottable { get; set; }
        public int EntityCount { get; set; }
        public string InfrastructureType { get; set; } = "Other";
    }

    /// <summary>
    /// Block usage summary
    /// </summary>
    public class BlockInfo
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
        public List<string> Layers { get; set; } = new();
    }

    /// <summary>
    /// External reference status
    /// </summary>
    public class XRefInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public bool IsResolved { get; set; }
        public string Status { get; set; } = string.Empty;
        public List<string> Layers { get; set; } = new();
        public List<string> BlockNames { get; set; } = new();
        public Dictionary<string, int> EntityTypeCounts { get; set; } = new();
        public int TotalEntityCount { get; set; }
    }

    #endregion

    #region Geometry Models

    /// <summary>
    /// Drawing extents
    /// </summary>
    public class ExtentsInfo
    {
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MinZ { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
        public double MaxZ { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    /// <summary>
    /// Geometry summary
    /// </summary>
    public class GeometrySummary
    {
        public double TotalLengthDrawingUnits { get; set; }
        public double TotalAreaDrawingUnits { get; set; }
        public double MaxZ { get; set; }
        public int ZAnomalyCount { get; set; }
    }

    #endregion
}
