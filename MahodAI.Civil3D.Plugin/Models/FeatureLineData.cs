using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Models
{
    /// <summary>
    /// 3D feature line data.
    /// </summary>
    public class FeatureLineData
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Layer { get; set; } = string.Empty;
        public string Style { get; set; } = string.Empty;
        public string? SiteName { get; set; }

        /// <summary>
        /// Geometry information.
        /// </summary>
        public double Length { get; set; }
        public double Length2D { get; set; }
        public double Length3D { get; set; }
        public int VertexCount { get; set; }
        public int ElevationPointCount { get; set; }

        /// <summary>
        /// Is this a closed feature line?
        /// </summary>
        public bool IsClosed { get; set; }

        /// <summary>
        /// Does this feature line have 3D elevation data?
        /// </summary>
        public bool Has3DElevations { get; set; }

        /// <summary>
        /// Elevation range.
        /// </summary>
        public ElevationRange? ElevationRange { get; set; }

        /// <summary>
        /// Slope information along the feature line.
        /// </summary>
        public double? MaxGradePercent { get; set; }
        public double? MinGradePercent { get; set; }
        public double? AverageGradePercent { get; set; }

        /// <summary>
        /// Sample vertices (first and last few points for reference).
        /// </summary>
        public List<FeatureLineVertex> SampleVertices { get; set; } = new();

        /// <summary>
        /// Station-based data if associated with alignment.
        /// </summary>
        public string? AssociatedAlignment { get; set; }
        public StationRange? StationRange { get; set; }
    }

    /// <summary>
    /// Feature line vertex.
    /// </summary>
    public class FeatureLineVertex
    {
        public int Index { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Elevation { get; set; }
        public double? DistanceFromStart { get; set; }
        public double? GradeToNext { get; set; }
    }
}
