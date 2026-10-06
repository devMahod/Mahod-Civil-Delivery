using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Models
{
    /// <summary>
    /// Comprehensive corridor data with regions, assemblies, and cross-sections.
    /// </summary>
    public class CorridorData
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Style { get; set; } = string.Empty;

        /// <summary>
        /// Classified corridor type: Road, Junction, Roundabout, Ramp, Unknown.
        /// </summary>
        public string CorridorType { get; set; } = "Road";

        /// <summary>
        /// Corridor baselines (usually one per alignment).
        /// </summary>
        public List<CorridorBaseline> Baselines { get; set; } = new();

        /// <summary>
        /// Corridor surfaces generated.
        /// </summary>
        public List<CorridorSurfaceInfo> Surfaces { get; set; } = new();

        /// <summary>
        /// Volume calculations.
        /// </summary>
        public CorridorVolumes? Volumes { get; set; }

        /// <summary>
        /// Corridor statistics.
        /// </summary>
        public CorridorStatistics Statistics { get; set; } = new();

        /// <summary>
        /// Quality issues.
        /// </summary>
        public List<QualityIssue> Issues { get; set; } = new();
    }

    /// <summary>
    /// Corridor baseline (one per alignment).
    /// </summary>
    public class CorridorBaseline
    {
        public string AlignmentName { get; set; } = string.Empty;
        public string ProfileName { get; set; } = string.Empty;
        public double StartStation { get; set; }
        public double EndStation { get; set; }

        /// <summary>
        /// Regions along this baseline.
        /// </summary>
        public List<CorridorRegion> Regions { get; set; } = new();
    }

    /// <summary>
    /// Corridor region (assembly applied over station range).
    /// </summary>
    public class CorridorRegion
    {
        public string Name { get; set; } = string.Empty;
        public string AssemblyName { get; set; } = string.Empty;
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public double Length { get; set; }

        /// <summary>
        /// Sampling frequency.
        /// </summary>
        public double? SamplingInterval { get; set; }

        /// <summary>
        /// Sample cross-sections at key stations.
        /// </summary>
        public List<CrossSectionSample> SampleCrossSections { get; set; } = new();

        /// <summary>
        /// Feature lines extracted from this region.
        /// </summary>
        public List<CorridorFeatureLine> FeatureLines { get; set; } = new();
    }

    /// <summary>
    /// Sample cross-section at a station.
    /// </summary>
    public class CrossSectionSample
    {
        public double Station { get; set; }

        /// <summary>
        /// Links (elements) in this cross-section.
        /// </summary>
        public List<CrossSectionLink> Links { get; set; } = new();

        /// <summary>
        /// Total width (left + right).
        /// </summary>
        public double TotalWidth { get; set; }
        public double LeftWidth { get; set; }
        public double RightWidth { get; set; }

        /// <summary>
        /// Ditch subassemblies detected in this cross-section.
        /// </summary>
        public List<DitchInfo> Ditches { get; set; } = new();

        /// <summary>
        /// Lane subassemblies detected in this cross-section.
        /// </summary>
        public List<LaneInfo> Lanes { get; set; } = new();

        /// <summary>
        /// Elevation-based element classification summary.
        /// </summary>
        public CrossSectionClassification? Classification { get; set; }
    }

    /// <summary>
    /// Drainage ditch information from a corridor cross-section.
    /// </summary>
    public class DitchInfo
    {
        public string Side { get; set; } = string.Empty;  // Left, Right
        public string SubassemblyName { get; set; } = string.Empty;
        public double InvertElevation { get; set; }
        public double Depth { get; set; }
        public double BottomWidth { get; set; }
        public double? LeftSideSlope { get; set; }   // ratio H:V
        public double? RightSideSlope { get; set; }
    }

    /// <summary>
    /// Lane information from a corridor cross-section.
    /// </summary>
    public class LaneInfo
    {
        public string Side { get; set; } = string.Empty;  // Left, Right
        public string SubassemblyName { get; set; } = string.Empty;
        public double Width { get; set; }
        public double? Slope { get; set; }
    }

    /// <summary>
    /// Cross-section link (subassembly output element).
    /// </summary>
    public class CrossSectionLink
    {
        public string Code { get; set; } = string.Empty;
        public string SubassemblyName { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;  // Left, Right
        public double Offset { get; set; }
        public double Elevation { get; set; }
        public double? Slope { get; set; }
        public string? ClassifiedType { get; set; }  // road_surface, shoulder, sidewalk, median, ditch, etc.
        public double? ElevationDelta { get; set; }   // relative to road surface centerline
    }

    /// <summary>
    /// Summarized cross-section element classification at a station.
    /// </summary>
    public class CrossSectionClassification
    {
        public double RoadSurfaceWidth { get; set; }
        public double? LeftSidewalkWidth { get; set; }
        public double? RightSidewalkWidth { get; set; }
        public double? MedianWidth { get; set; }
        public bool HasLeftDitch { get; set; }
        public bool HasRightDitch { get; set; }
    }

    /// <summary>
    /// Corridor feature line.
    /// </summary>
    public class CorridorFeatureLine
    {
        public string Code { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;  // Left, Right, Center
        public double Length { get; set; }
        public int PointCount { get; set; }
        public ElevationRange? ElevationRange { get; set; }
    }

    /// <summary>
    /// Corridor surface information.
    /// </summary>
    public class CorridorSurfaceInfo
    {
        public string Name { get; set; } = string.Empty;
        public string SurfaceType { get; set; } = string.Empty;  // Top, Datum, etc.
        public bool IsBuilt { get; set; }
        public double? Area { get; set; }
        public ElevationRange? ElevationRange { get; set; }
    }

    /// <summary>
    /// Corridor volume calculations.
    /// </summary>
    public class CorridorVolumes
    {
        public double CutVolume { get; set; }
        public double FillVolume { get; set; }
        public double NetVolume { get; set; }

        /// <summary>
        /// Volume by region.
        /// </summary>
        public List<RegionVolume> ByRegion { get; set; } = new();
    }

    /// <summary>
    /// Volume for a specific region.
    /// </summary>
    public class RegionVolume
    {
        public string RegionName { get; set; } = string.Empty;
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public double CutVolume { get; set; }
        public double FillVolume { get; set; }
        public double NetVolume { get; set; }
    }

    /// <summary>
    /// Corridor statistics.
    /// </summary>
    public class CorridorStatistics
    {
        public int BaselineCount { get; set; }
        public int RegionCount { get; set; }
        public int AssemblyCount { get; set; }
        public int SurfaceCount { get; set; }
        public double TotalLength { get; set; }
        public int SampleCount { get; set; }
    }
}
