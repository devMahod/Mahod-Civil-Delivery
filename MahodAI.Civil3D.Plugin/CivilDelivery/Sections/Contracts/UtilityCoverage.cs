using System.Collections.Generic;
using System.Text.Json.Serialization;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts
{
    /// <summary>One utility-bearing source found in the drawing, independent of configuration.</summary>
    public sealed class DiscoveredUtility
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        /// <summary>pipe-network | pressure-network | feature-line | polyline-3d | block | other</summary>
        [JsonPropertyName("kind")]
        public required string Kind { get; init; }

        [JsonPropertyName("handle")]
        public string? Handle { get; init; }

        /// <summary>Whether Civil can sample this type directly as a Section Source.</summary>
        [JsonPropertyName("natively_sampleable")]
        public bool NativelySampleable { get; init; }

        [JsonPropertyName("part_count")]
        public int? PartCount { get; init; }
    }

    /// <summary>
    /// Per-section utility accounting (directive §16 / locked plan §7.8). Nataly's
    /// requirement is that existing systems appear in the sections, so "no utilities"
    /// must always be an explicit, explained state — never silence.
    /// </summary>
    public sealed class UtilityCoverageReport
    {
        /// <summary>Utility sources that exist in the drawing and could belong in a section here.</summary>
        [JsonPropertyName("relevant")]
        public List<DiscoveredUtility> Relevant { get; init; } = new();

        /// <summary>Utilities the plan will actually sample/project into this section.</summary>
        [JsonPropertyName("represented")]
        public List<string> Represented { get; init; } = new();

        /// <summary>Present in the drawing, sampleable, but NOT configured in the profile.</summary>
        [JsonPropertyName("not_configured")]
        public List<string> NotConfigured { get; init; } = new();

        /// <summary>Configured/required but absent from the drawing.</summary>
        [JsonPropertyName("missing")]
        public List<string> Missing { get; init; } = new();

        /// <summary>Present but no safe representation path exists yet, with the reason.</summary>
        [JsonPropertyName("unsupported")]
        public Dictionary<string, string> Unsupported { get; init; } = new();

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        /// <summary>Completeness of the drawing/XREF utility-linework scan.</summary>
        [JsonPropertyName("projection_scan_state")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public UtilityProjectionScanState ProjectionScanState { get; set; } =
            UtilityProjectionScanState.NotRun;

        /// <summary>Recognized utility entities anywhere in the drawing/XREF scan.</summary>
        [JsonPropertyName("projection_drawing_entity_count")]
        public int ProjectionDrawingEntityCount { get; set; }

        /// <summary>Exact projected-utility crossings of this section line.</summary>
        [JsonPropertyName("projection_section_crossing_count")]
        public int ProjectionSectionCrossingCount { get; set; }

        /// <summary>True only when every discovered, sampleable utility is represented.</summary>
        [JsonPropertyName("complete")]
        public bool Complete => NotConfigured.Count == 0 && Missing.Count == 0 && Unsupported.Count == 0;
    }
}
