using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Services.Extraction.Analyzers
{
    /// <summary>
    /// Summary of shapefile analysis results
    /// </summary>
    public class ShapefileSummary
    {
        public string FilePath { get; set; } = string.Empty;
        public long TotalSizeBytes { get; set; }
        public string? GeometryType { get; set; }
        public int FeatureCount { get; set; }
        public List<ShapefileFieldInfo> Fields { get; set; } = new();
        public Dictionary<string, int> StatusCounts { get; set; } = new();
        public List<string> SuspectedKeyFields { get; set; } = new();
        public int IdDuplicateCount { get; set; }
        public double TotalAreaSquareMeters { get; set; }
        public int SmallAreaCountUnder100 { get; set; }
        public int LargeAreaCountOver1M { get; set; }
        public string? CrsWkt { get; set; }
        public string? CrsEpsg { get; set; }
        public bool HasAdditionalShapefilesInFolder { get; set; }
        public int SelfIntersectingCount { get; set; }
        public int DuplicateGeometryCount { get; set; }
        public int OpenRingCount { get; set; }
        public List<string> Issues { get; set; } = new();
    }

    /// <summary>
    /// Shapefile field metadata
    /// </summary>
    public class ShapefileFieldInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public int Length { get; set; }
        public int DecimalCount { get; set; }
        public int NullCount { get; set; }
    }
}
