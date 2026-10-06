using System.Collections.Generic;
using System.Text.Json.Serialization;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts
{
    public sealed class ProjectedEntityEvidence
    {
        [JsonPropertyName("projection_key")]
        public required string ProjectionKey { get; init; }

        [JsonPropertyName("system_label")]
        public required string SystemLabel { get; init; }

        [JsonPropertyName("annotation_handles")]
        public List<string> AnnotationHandles { get; init; } = new();

        /// <summary>
        /// Exact post-creation semantic fingerprint by annotation handle. VERIFY
        /// recomputes these from the live entities; a moved marker, changed color or
        /// edited label therefore fails even though its handle still exists.
        /// </summary>
        [JsonPropertyName("annotation_fingerprints")]
        public Dictionary<string, string> AnnotationFingerprints { get; init; } = new();
    }

    /// <summary>
    /// Exact handle binding for one deterministic, non-surface presentation mark.
    /// SemanticKey is derived from PLAN parameters (never from drawing proximity),
    /// while VERIFY recomputes the expected live geometry through the same pure
    /// placement contract used by APPLY.
    /// </summary>
    public sealed class SectionCoreAnnotationEvidence
    {
        [JsonPropertyName("kind")]
        public required string Kind { get; init; }

        [JsonPropertyName("semantic_key")]
        public required string SemanticKey { get; init; }

        [JsonPropertyName("handle")]
        public required string Handle { get; init; }
    }

    /// <summary>Handles of the native Civil objects created/updated for one section.</summary>
    public sealed class SectionObjectHandles
    {
        [JsonPropertyName("sample_line_group")]
        public string? SampleLineGroup { get; set; }

        [JsonPropertyName("sample_line")]
        public string? SampleLine { get; set; }

        [JsonPropertyName("section_view")]
        public string? SectionView { get; set; }

        [JsonPropertyName("sections")]
        public List<string> Sections { get; init; } = new();
    }

    public sealed class SectionApplyRecordResult
    {
        [JsonPropertyName("record_id")]
        public required string RecordId { get; init; }

        [JsonPropertyName("logical_key")]
        public string? LogicalKey { get; init; }

        [JsonPropertyName("action_taken")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public PlanAction ActionTaken { get; set; }

        [JsonPropertyName("handles")]
        public SectionObjectHandles Handles { get; init; } = new();

        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DeliveryStatus Status { get; set; } = DeliveryStatus.Discovered;

        /// <summary>
        /// Source names Civil is actually sampling for this section, read back from the
        /// sample line group. This is the difference between "the plan wanted utilities"
        /// and "the section contains them".
        /// </summary>
        [JsonPropertyName("sampled_sources")]
        public List<string> SampledSources { get; init; } = new();

        /// <summary>
        /// Legacy or otherwise unplanned group sources that APPLY explicitly turned
        /// off while reconciling the tool-owned sample-line group.
        /// </summary>
        [JsonPropertyName("disabled_sources")]
        public List<string> DisabledSources { get; init; } = new();

        /// <summary>
        /// Hebrew labels of utilities PROJECTED into this section from drawing linework
        /// (usually the UT XREF) — distinct from sampled Civil sources, and said so.
        /// </summary>
        [JsonPropertyName("projected_systems")]
        public List<string> ProjectedSystems { get; init; } = new();

        [JsonPropertyName("projected_entities")]
        public List<ProjectedEntityEvidence> ProjectedEntities { get; init; } = new();

        /// <summary>Live post-arrangement view extents [minX,minY,maxX,maxY].</summary>
        [JsonPropertyName("layout_bounds")]
        public double[]? LayoutBounds { get; set; }

        /// <summary>Live post-arrangement SectionView.Location [x,y].</summary>
        [JsonPropertyName("layout_view_location")]
        public double[]? LayoutViewLocation { get; set; }

        /// <summary>Annotation entities the tool drew inside this section view.</summary>
        [JsonPropertyName("annotation_count")]
        public int AnnotationCount { get; set; }

        /// <summary>
        /// LTSCALE / MSLTSCALE / CANNOSCALE state the annotation linetypes were drawn
        /// against (ltdisplay-v1). VERIFY re-reads the drawing and requires equality.
        /// </summary>
        [JsonPropertyName("linetype_display_evidence")]
        public string? LinetypeDisplayEvidence { get; set; }

        /// <summary>
        /// One audit entry per lane-furniture placement. Entries name whether the
        /// approved office block or the schematic fallback was used, which front/rear
        /// elevation was selected, and explicitly state that traffic direction was
        /// not inferred from the drawn CL direction.
        /// </summary>
        [JsonPropertyName("vehicle_blocks")]
        public List<string> VehicleBlocks { get; init; } = new();

        /// <summary>
        /// One strict evidence row per directional road/bus/bike strip. Each row
        /// binds flow/source/digest to the one registered reference to Nataly's
        /// pinned HW-ARRW-01 office block drawn above that strip.
        /// </summary>
        [JsonPropertyName("traffic_direction_arrows")]
        public List<string> TrafficDirectionArrows { get; init; } = new();

        /// <summary>
        /// One strict registered DBText reference for every dimension-mark offset.
        /// It binds the true anchor to its bounded ladder position and visible text.
        /// </summary>
        [JsonPropertyName("dimension_offset_labels")]
        public List<string> DimensionOffsetLabels { get; init; } = new();

        /// <summary>
        /// Exact axis/title/ROW/dimension-tick/width/strip entities.  This closes the
        /// gap where an unchanged APPLY-time fingerprint could prove only that a bad
        /// placement stayed unchanged, rather than that it matches the current PLAN.
        /// </summary>
        [JsonPropertyName("core_presentation_annotations")]
        public List<SectionCoreAnnotationEvidence> CorePresentationAnnotations { get; init; } = new();

        /// <summary>
        /// Number of car-eligible strips proven from the adjacent plan-mark contract.
        /// VERIFY requires one approved office block for every such strip, so an
        /// empty VehicleBlocks list cannot pass merely because both sides are empty.
        /// </summary>
        [JsonPropertyName("expected_office_car_blocks")]
        public int ExpectedOfficeCarBlocks { get; set; }

        [JsonPropertyName("presentation_coverage")]
        public SectionPresentationCoveragePlan PresentationCoverage { get; set; } = new();

        /// <summary>
        /// One exact registered label per adjacent width span, derived only from the
        /// uniquely selected design section. Each entry carries sampled endpoints,
        /// signed percent and the live DBText handle used by VERIFY.
        /// </summary>
        [JsonPropertyName("slope_labels")]
        public List<string> SlopeLabels { get; init; } = new();

        /// <summary>
        /// Exact, registered proof for the single elevation reference drawn by the
        /// tool: semantic source, value and entity handle. APPLY remains atomic when
        /// this evidence cannot be produced.
        /// </summary>
        [JsonPropertyName("datum_reference")]
        public string? DatumReference { get; set; }

        /// <summary>
        /// SEC-B3 (review of 1.3.9): round-trip evidence of the managed view's elevation
        /// band — EG/FG sampled range, projected utility elevations, arrow headroom and
        /// the drafting margin it was derived from. VERIFY re-derives it; legacy
        /// records without it must be re-applied.
        /// </summary>
        [JsonPropertyName("elevation_band_evidence")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ElevationBandEvidence { get; set; }

        /// <summary>
        /// Versioned proof that the complete annotation pass ran.  VERIFY rejects a
        /// legacy/incomplete apply record instead of guessing from a non-zero count.
        /// </summary>
        [JsonPropertyName("annotation_contract_version")]
        public int AnnotationContractVersion { get; set; }

        /// <summary>
        /// True after every eligible strip was evaluated for an office vehicle block.
        /// An empty VehicleBlocks list is then meaningful (there were no car strips),
        /// rather than indistinguishable from an interrupted decoration pass.
        /// </summary>
        [JsonPropertyName("vehicle_evidence_complete")]
        public bool VehicleEvidenceComplete { get; set; }

        /// <summary>
        /// True only after every eligible marked span received a persisted, registered
        /// design-surface percentage label. Legacy/interrupted evidence fails VERIFY.
        /// </summary>
        [JsonPropertyName("slope_evidence_complete")]
        public bool SlopeEvidenceComplete { get; set; }

        [JsonPropertyName("findings")]
        public List<DeliveryFinding> Findings { get; init; } = new();
    }

    public sealed class SectionApplyResult
    {
        [JsonPropertyName("run_id")]
        public required string RunId { get; init; }

        /// <summary>
        /// batch = the legacy all-record atomic workflow; selected-record = one
        /// explicitly selected, independently ready record.  A selected result never
        /// implies that any omitted PLAN record was applied or verified.
        /// </summary>
        [JsonPropertyName("scope")]
        public string Scope { get; set; } = "batch";

        [JsonPropertyName("selected_record_id")]
        public string? SelectedRecordId { get; set; }

        [JsonPropertyName("records")]
        public List<SectionApplyRecordResult> Records { get; init; } = new();

        [JsonPropertyName("committed")]
        public bool Committed { get; set; }

        /// <summary>
        /// Live host-database revision captured only after the APPLY transaction was
        /// committed (or after publishing an all-exclusion decision bundle). VERIFY compares against this revision,
        /// never the pre-PLAN revision that APPLY necessarily changed.
        /// </summary>
        [JsonPropertyName("post_apply_database_revision")]
        public string? PostApplyDatabaseRevision { get; set; }

        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DeliveryStatus Status { get; set; } = DeliveryStatus.Discovered;

        [JsonPropertyName("findings")]
        public List<DeliveryFinding> Findings { get; init; } = new();
    }

    public sealed class SectionVerifyCheck
    {
        [JsonPropertyName("check")]
        public required string Check { get; init; }

        [JsonPropertyName("expected")]
        public string? Expected { get; init; }

        [JsonPropertyName("actual")]
        public string? Actual { get; init; }

        [JsonPropertyName("pass")]
        public bool Pass { get; init; }
    }

    public sealed class SectionVerifyRecordResult
    {
        [JsonPropertyName("record_id")]
        public required string RecordId { get; init; }

        [JsonPropertyName("logical_key")]
        public string? LogicalKey { get; init; }

        [JsonPropertyName("checks")]
        public List<SectionVerifyCheck> Checks { get; init; } = new();

        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DeliveryStatus Status { get; set; } = DeliveryStatus.Discovered;
    }

    public sealed class SectionVerifyResult
    {
        /// <summary>Revision after the read-only verification transaction disposed; not an APPLY waiver.</summary>
        [JsonPropertyName("verified_database_revision")]
        public string? VerifiedDatabaseRevision { get; set; }

        [JsonPropertyName("run_id")]
        public required string RunId { get; init; }

        /// <summary>
        /// batch or selected-record. A selected result proves only
        /// <see cref="SelectedRecordId"/> and never represents the omitted PLAN rows.
        /// </summary>
        [JsonPropertyName("scope")]
        public string Scope { get; set; } = "batch";

        [JsonPropertyName("selected_record_id")]
        public string? SelectedRecordId { get; set; }

        [JsonPropertyName("records")]
        public List<SectionVerifyRecordResult> Records { get; init; } = new();

        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DeliveryStatus Status { get; set; } = DeliveryStatus.Discovered;

        [JsonPropertyName("findings")]
        public List<DeliveryFinding> Findings { get; init; } = new();

        /// <summary>
        /// Selected-record VERIFY only, diagnostic: the detached measured-layout input
        /// receipt that PersistVerifyEvidence stages as a separate bundle artifact.
        /// Never part of verify_result.json and never read by a check, gate or status.
        /// </summary>
        [JsonIgnore]
        internal SectionLayoutInputCaptureRecord? LayoutInputCapture { get; set; }
    }
}
