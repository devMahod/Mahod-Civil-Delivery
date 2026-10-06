using System.Collections.Generic;
using System.Text.Json.Serialization;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts
{
    /// <summary>
    /// One CL instruction discovered in a source drawing (locked plan §7.2).
    /// Serialized boundary: handles as strings, no Autodesk ObjectIds.
    /// </summary>
    public sealed class ClSourceRecord
    {
        [JsonPropertyName("record_id")]
        public required string RecordId { get; init; }

        [JsonPropertyName("source_drawing")]
        public required string SourceDrawing { get; init; }

        [JsonPropertyName("source_drawing_hash")]
        public required string SourceDrawingHash { get; init; }

        /// <summary>
        /// Resolved absolute path of the database that actually owns this entity.
        /// Unlike SourceXref (a readable insert chain), this is suitable for an exact
        /// pre-APPLY byte-hash check.
        /// </summary>
        [JsonPropertyName("source_drawing_path")]
        public string? SourceDrawingPath { get; init; }

        [JsonPropertyName("source_handle")]
        public required string SourceHandle { get; init; }

        [JsonPropertyName("source_xref")]
        public string? SourceXref { get; init; }

        [JsonPropertyName("source_entity_type")]
        public required string SourceEntityType { get; init; }

        [JsonPropertyName("source_layer")]
        public required string SourceLayer { get; init; }

        /// <summary>Endpoints in the source (block/XREF) space, [x1,y1,x2,y2].</summary>
        [JsonPropertyName("source_endpoints")]
        public required double[] SourceEndpoints { get; init; }

        /// <summary>Endpoints in host WCS after XREF transform, [x1,y1,x2,y2].</summary>
        [JsonPropertyName("wcs_endpoints")]
        public required double[] WcsEndpoints { get; init; }

        [JsonPropertyName("xref_transform")]
        public double[]? XrefTransform { get; init; }

        [JsonPropertyName("length")]
        public double Length { get; init; }

        [JsonPropertyName("angle_deg")]
        public double AngleDeg { get; init; }

        [JsonPropertyName("nearby_labels")]
        public List<string> NearbyLabels { get; init; } = new();

        [JsonPropertyName("candidate_section_number")]
        public string? CandidateSectionNumber { get; set; }

        /// <summary>
        /// 1.4.1: the chain of XREF insertion handles the line was read through (null for model space and side files).
        /// In-memory only — it binds label choice to one XREF instance and is not part of the record identity.
        /// </summary>
        [JsonIgnore]
        public string? SourceInstanceKey { get; set; }

        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DeliveryStatus Status { get; set; } = DeliveryStatus.Discovered;

        [JsonPropertyName("findings")]
        public List<DeliveryFinding> Findings { get; init; } = new();
    }
}
