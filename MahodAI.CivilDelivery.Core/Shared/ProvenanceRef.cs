using System.Text.Json.Serialization;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Traceability of any Civil Delivery output back to its source, per locked
    /// plan §5.4. Every final Section Plan record and Neutral Quantity Record must
    /// be traceable through one of these.
    /// </summary>
    public sealed class ProvenanceRef
    {
        /// <summary>drawing | xref | profile | pricebook | workbook | manual | tool</summary>
        [JsonPropertyName("source_kind")]
        public required string SourceKind { get; init; }

        [JsonPropertyName("source_path_or_uri")]
        public string? SourcePathOrUri { get; init; }

        [JsonPropertyName("drawing_checksum")]
        public string? DrawingChecksum { get; init; }

        /// <summary>AutoCAD entity handle as hex string (stable across sessions, unlike ObjectId).</summary>
        [JsonPropertyName("source_handle")]
        public string? SourceHandle { get; init; }

        [JsonPropertyName("source_subentity_path")]
        public string? SourceSubentityPath { get; init; }

        [JsonPropertyName("xref_path")]
        public string? XrefPath { get; init; }

        /// <summary>Row-major 4x3 affine transform of the XREF block insert, when applicable.</summary>
        [JsonPropertyName("xref_transform")]
        public double[]? XrefTransform { get; init; }

        [JsonPropertyName("entity_type")]
        public string? EntityType { get; init; }

        [JsonPropertyName("layer")]
        public string? Layer { get; init; }

        [JsonPropertyName("station_from")]
        public double? StationFrom { get; init; }

        [JsonPropertyName("station_to")]
        public double? StationTo { get; init; }

        [JsonPropertyName("measurement_method")]
        public string? MeasurementMethod { get; init; }

        [JsonPropertyName("input_hash")]
        public string? InputHash { get; init; }

        [JsonPropertyName("tool_version")]
        public string? ToolVersion { get; init; }

        [JsonPropertyName("project_profile_version")]
        public string? ProjectProfileVersion { get; init; }

        [JsonPropertyName("run_id")]
        public string? RunId { get; init; }
    }
}
