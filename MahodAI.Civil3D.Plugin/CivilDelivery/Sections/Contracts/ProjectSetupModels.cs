using System.Collections.Generic;
using System.Text.Json.Serialization;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts
{
    /// <summary>
    /// One layer that could carry CL section-location instructions, with the
    /// evidence that makes it a candidate. Produced by the first-run scan so the
    /// engineer chooses from real drawing facts instead of typing a layer name.
    /// </summary>
    public sealed class ClLayerCandidate
    {
        [JsonPropertyName("layer")]
        public required string Layer { get; init; }

        [JsonPropertyName("line_count")]
        public int LineCount { get; set; }

        [JsonPropertyName("polyline_count")]
        public int PolylineCount { get; set; }

        /// <summary>Two-point entities: the shape a section line actually has.</summary>
        [JsonPropertyName("two_point_count")]
        public int TwoPointCount { get; set; }

        /// <summary>How many of this layer's entities actually cross a candidate alignment.</summary>
        [JsonPropertyName("crossing_count")]
        public int CrossingCount { get; set; }

        /// <summary>Distinct alignments crossed by this layer's entities.</summary>
        [JsonPropertyName("alignments_crossed")]
        public List<string> AlignmentsCrossed { get; init; } = new();

        [JsonPropertyName("median_length")]
        public double MedianLength { get; set; }

        [JsonPropertyName("in_xref")]
        public bool InXref { get; set; }

        [JsonPropertyName("sample_labels")]
        public List<string> SampleLabels { get; init; } = new();

        /// <summary>
        /// Evidence-ranked score, 0..100. Ranking ONLY orders the choices shown to
        /// the engineer; it never selects anything (locked plan: no guessed selection).
        /// </summary>
        [JsonPropertyName("evidence_score")]
        public int EvidenceScore { get; set; }

        [JsonPropertyName("why")]
        public List<string> Why { get; init; } = new();

        /// <summary>1.4.1: the host drawing (model space / its XREFs) offered this layer.</summary>
        [JsonPropertyName("from_host")]
        public bool FromHost { get; set; }

        /// <summary>1.4.1: a separate CL drawing offered this layer. Both true = mixed: never scoped to the file.</summary>
        [JsonPropertyName("from_separate_file")]
        public bool FromSeparateFile { get; set; }
    }

    /// <summary>An alignment offered for confirmation during first-run setup.</summary>
    public sealed class AlignmentCandidateSummary
    {
        [JsonPropertyName("handle")]
        public string? Handle { get; init; }
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("start_station")]
        public double StartStation { get; init; }

        [JsonPropertyName("end_station")]
        public double EndStation { get; init; }

        [JsonPropertyName("length")]
        public double Length { get; init; }

        [JsonPropertyName("crossed_by_candidate_layers")]
        public List<string> CrossedByCandidateLayers { get; init; } = new();
    }

    /// <summary>A sampled source (surface/corridor/pipe network) offered for confirmation.</summary>
    public sealed class SourceCandidateSummary
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        /// <summary>surface | corridor | pipe-network</summary>
        [JsonPropertyName("kind")]
        public required string Kind { get; init; }

        [JsonPropertyName("handle")]
        public string? Handle { get; init; }
    }

    /// <summary>
    /// Everything the first-run setup screen needs: what the drawing actually
    /// contains, ranked by evidence, with nothing chosen yet.
    /// </summary>
    public sealed class ProjectSetupScan
    {
        [JsonPropertyName("drawing_fingerprint")]
        public string? DrawingFingerprint { get; set; }
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName("run_id")]
        public required string RunId { get; init; }

        [JsonPropertyName("project_profile_id")]
        public required string ProjectProfileId { get; init; }

        [JsonPropertyName("drawing")]
        public string? Drawing { get; set; }

        [JsonPropertyName("drawing_hash")]
        public string? DrawingHash { get; set; }

        [JsonPropertyName("database_revision")]
        public string? DatabaseRevision { get; set; }

        [JsonPropertyName("source_dbmod")]
        public int? SourceDbMod { get; set; }

        [JsonPropertyName("profile_source")]
        public string? ProfileSource { get; set; }

        [JsonPropertyName("project_profile_hash")]
        public string? ProjectProfileHash { get; set; }

        [JsonPropertyName("project_profile_effective_hash")]
        public string? ProjectProfileEffectiveHash { get; set; }

        [JsonPropertyName("profile_write_state")]
        public ProjectProfileWriter.ExpectedProfileState? ProfileWriteState { get; set; }

        [JsonPropertyName("profile_is_configured")]
        public bool ProfileIsConfigured { get; init; }

        [JsonPropertyName("scan_complete")]
        public bool ScanComplete { get; set; } = true;

        [JsonPropertyName("cl_layer_candidates")]
        public List<ClLayerCandidate> ClLayerCandidates { get; init; } = new();

        [JsonPropertyName("alignments")]
        public List<AlignmentCandidateSummary> Alignments { get; init; } = new();

        [JsonPropertyName("sources")]
        public List<SourceCandidateSummary> Sources { get; init; } = new();

        [JsonPropertyName("findings")]
        public List<DeliveryFinding> Findings { get; init; } = new();

        // Exact separately selected CL inputs included in this discovery pass.
        // The setup decision must recheck these bytes, not just the host DWG.
        [JsonPropertyName("external_cl_hashes")]
        public Dictionary<string, string> ExternalClHashes { get; init; } = new(System.StringComparer.OrdinalIgnoreCase);

        // Nested dependencies are freshness evidence, not additional chosen CL files.
        [JsonPropertyName("discovery_source_hashes")]
        public Dictionary<string, string> DiscoverySourceHashes { get; init; } = new(System.StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The engineer's explicit choices, ready to be written into the profile.
    /// Every field is a decision a human made; nothing here is inferred.
    /// </summary>
    public sealed class ProjectSetupSelection
    {
        /// <summary>Null preserves legacy callers. A supplied list replaces explicit pairs
        /// only for the selected alignments; an empty list explicitly returns those to legacy selection.</summary>
        [JsonPropertyName("surface_pairs")]
        public List<ProjectProfile.SectionsProfile.SourcesProfile.SurfacePair>? SurfacePairs { get; init; }

        [JsonPropertyName("cl_layers")]
        public List<string> ClLayers { get; init; } = new();

        [JsonPropertyName("cl_source_files")]
        public List<string> ClSourceFiles { get; init; } = new();

        [JsonPropertyName("allowed_alignments")]
        public List<string> AllowedAlignments { get; init; } = new();

        /// <summary>Required sampled sources: name → kind.</summary>
        [JsonPropertyName("sampled_sources")]
        public Dictionary<string, string> SampledSources { get; init; } = new();

        [JsonPropertyName("intersection_tolerance_m")]
        public double? IntersectionToleranceM { get; init; }

        /// <summary>1.4.1: "source-file" when every selected CL layer comes from the separate CL drawing; else null.</summary>
        [JsonPropertyName("cl_layer_scope")]
        public string? ClLayerScope { get; init; }

        /// <summary>1.4.1: null = ordinary section lines; "station-markers" = engineer-chosen tick mode.</summary>
        [JsonPropertyName("cl_mode")]
        public string? ClMode { get; init; }

        /// <summary>Engineer-entered half-width for the station-markers mode, metres.</summary>
        [JsonPropertyName("station_marker_half_width_m")]
        public double? StationMarkerHalfWidthM { get; init; }

        [JsonPropertyName("approved_by")]
        public string? ApprovedBy { get; init; }
    }
}
