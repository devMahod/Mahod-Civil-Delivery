using System;
using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Services.Extraction.Models
{
    /// <summary>
    /// Complete drawing data model containing all extracted Civil 3D data.
    /// This is the unified container sent to AI for analysis.
    /// Contains raw extracted data and basic statistics - compliance analysis is done by AI.
    /// </summary>
    public class DrawingDataModel
    {
        #region Metadata

        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public DateTime ExtractedAt { get; set; } = DateTime.UtcNow;
        public string Units { get; set; } = string.Empty;
        public string CoordinateSystem { get; set; } = string.Empty;
        public string DwgVersion { get; set; } = string.Empty;
        public string FileSize { get; set; } = string.Empty;

        #endregion

        #region Civil 3D Objects - Counts

        public int AlignmentCount { get; set; }
        public int ProfileCount { get; set; }
        public int SurfaceCount { get; set; }
        public int CorridorCount { get; set; }
        public int PipeNetworkCount { get; set; }
        public int ParcelCount { get; set; }

        #endregion

        #region Alignment Data

        public List<AlignmentInfo> Alignments { get; set; } = new();
        public List<AlignmentDetailData> AlignmentDetails { get; set; } = new();
        public List<ProfileInfo> Profiles { get; set; } = new();
        public List<SuperElevationInfo> SuperElevations { get; set; } = new();

        #endregion

        #region Layer and Block Data

        public List<LayerInfo> Layers { get; set; } = new();
        public List<BlockInfo> Blocks { get; set; } = new();
        public List<XRefInfo> XRefs { get; set; } = new();

        #endregion

        #region Geometry Data

        public ExtentsInfo Extents { get; set; } = new();
        public GeometrySummary Geometry { get; set; } = new();
        public int TotalEntityCount { get; set; }
        public Dictionary<string, int> EntityTypeCounts { get; set; } = new();

        #endregion

        #region Summary Statistics (calculated locally)

        // Counts
        public int LayerCount { get; set; }
        public int BlockCount { get; set; }
        public int EmptyLayerCount { get; set; }
        public int ActiveLayerCount { get; set; }
        public int TotalCurveCount { get; set; }
        public int TotalSpiralCount { get; set; }
        public int TotalVerticalCurveCount { get; set; }

        // Alignment statistics
        public double TotalAlignmentLength { get; set; }
        public double MinimumRadius { get; set; }
        public double MaxGradePercent { get; set; }

        // Curve statistics
        public double MinCurveRadius { get; set; }
        public double MaxCurveRadius { get; set; }
        public double TotalCurveLength { get; set; }

        // Spiral statistics
        public double MinSpiralLength { get; set; }
        public double MaxSpiralLength { get; set; }
        public double TotalSpiralLength { get; set; }

        // Design speeds found in alignments
        public List<double> DesignSpeeds { get; set; } = new();

        #endregion
    }
}
