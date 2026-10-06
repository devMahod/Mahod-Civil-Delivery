using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Services.Extraction.Models
{
    /// <summary>
    /// Road sign data extracted from block references.
    /// </summary>
    public class RoadSignData
    {
        /// <summary>Israeli sign code (e.g., "101", "301", "501")</summary>
        public string SignCode { get; set; } = string.Empty;
        
        /// <summary>Sign type: warning, regulatory, guide, information, prohibition</summary>
        public string SignType { get; set; } = "unknown";
        
        /// <summary>Hebrew description of the sign</summary>
        public string Description { get; set; } = string.Empty;
        
        /// <summary>Station along parent alignment</summary>
        public double Station { get; set; }
        
        /// <summary>Parent alignment name</summary>
        public string AlignmentName { get; set; } = string.Empty;
        
        /// <summary>Placement side: left, right, overhead, median</summary>
        public string Side { get; set; } = string.Empty;
        
        public double X { get; set; }
        public double Y { get; set; }
        public double Rotation { get; set; }
        
        /// <summary>Block reference name in drawing</summary>
        public string BlockName { get; set; } = string.Empty;
        
        /// <summary>Layer the sign is on</summary>
        public string LayerName { get; set; } = string.Empty;
        
        /// <summary>Block attributes (tag -> value)</summary>
        public Dictionary<string, string> Attributes { get; set; } = new();
        
        /// <summary>Mounting height in meters</summary>
        public double? Height { get; set; }
        
        /// <summary>Sign size (diameter or side length) in cm</summary>
        public double? Size { get; set; }
    }

    /// <summary>
    /// Road marking data extracted from polylines and blocks on marking layers.
    /// </summary>
    public class RoadMarkingData
    {
        /// <summary>center_line, edge_line, lane_line, stop_line, crosswalk, arrow, text, speed_marking</summary>
        public string MarkingType { get; set; } = string.Empty;
        
        /// <summary>solid, dashed, double_solid, double_dashed</summary>
        public string Pattern { get; set; } = string.Empty;
        
        /// <summary>white, yellow</summary>
        public string Color { get; set; } = "white";
        
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        
        public string AlignmentName { get; set; } = string.Empty;
        
        /// <summary>left, right, center</summary>
        public string Side { get; set; } = string.Empty;
        
        /// <summary>Line width in cm</summary>
        public double Width { get; set; }
        
        /// <summary>Dash length for dashed patterns (meters)</summary>
        public double? DashLength { get; set; }
        
        /// <summary>Gap length for dashed patterns (meters)</summary>
        public double? GapLength { get; set; }
        
        public string LayerName { get; set; } = string.Empty;
        
        /// <summary>For arrows: straight, left, right, uturn</summary>
        public string? ArrowType { get; set; }
        
        /// <summary>For text markings: the text content</summary>
        public string? TextContent { get; set; }
    }

    /// <summary>
    /// Container for all sign and marking extraction results.
    /// </summary>
    public class SignMarkingExtractionResult
    {
        public List<RoadSignData> Signs { get; set; } = new();
        public List<RoadMarkingData> Markings { get; set; } = new();
        public int TotalSignCount { get; set; }
        public int TotalMarkingCount { get; set; }
        public Dictionary<string, int> SignsByType { get; set; } = new();
        public Dictionary<string, int> MarkingsByType { get; set; } = new();
    }
}
