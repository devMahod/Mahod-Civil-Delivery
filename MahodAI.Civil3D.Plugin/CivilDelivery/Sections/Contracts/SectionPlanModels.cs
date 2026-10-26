using System.Collections.Generic;
using System.Text.Json.Serialization;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts
{
    /// <summary>One geometrically valid alignment crossing found for a CL.</summary>
    public sealed class AlignmentCrossing
    {
        [JsonPropertyName("alignment_name")]
        public required string AlignmentName { get; init; }

        [JsonPropertyName("point")]
        public required double[] Point { get; init; } // [x, y]

        [JsonPropertyName("station")]
        public double Station { get; init; }

        [JsonPropertyName("tangent_deg")]
        public double TangentDeg { get; init; }

        /// <summary>Distance from CL to alignment when matched through tolerance (0 for exact crossings).</summary>
        [JsonPropertyName("gap_distance")]
        public double GapDistance { get; init; }
    }

    /// <summary>Planned participation of one source (surface/corridor/utility) in a section.</summary>
    public sealed class SectionSourcePlan
    {
        [JsonPropertyName("source_name")]
        public required string SourceName { get; init; }

        /// <summary>surface | corridor | pipe-network | pressure-network | feature-line | polyline | block | other</summary>
        [JsonPropertyName("source_type")]
        public required string SourceType { get; init; }

        [JsonPropertyName("source_handle")]
        public string? SourceHandle { get; init; }

        [JsonPropertyName("native_sample_capability")]
        public bool NativeSampleCapability { get; init; }

        /// <summary>sampled | adapter | unsupported</summary>
        [JsonPropertyName("planned_state")]
        public required string PlannedState { get; init; }

        [JsonPropertyName("adapter_required")]
        public bool AdapterRequired { get; init; }

        [JsonPropertyName("required")]
        public bool Required { get; init; }

        [JsonPropertyName("style_mapping")]
        public string? StyleMapping { get; init; }

        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DeliveryStatus Status { get; set; } = DeliveryStatus.Discovered;
    }

    /// <summary>One exact drawing-entity crossing promised as a projected utility.</summary>
    public sealed class ProjectedEntityPlan
    {
        [JsonPropertyName("projection_key")]
        public required string ProjectionKey { get; init; }

        [JsonPropertyName("system_label")]
        public required string SystemLabel { get; init; }

        [JsonPropertyName("source_layer")]
        public required string SourceLayer { get; init; }

        [JsonPropertyName("source_xref")]
        public string? SourceXref { get; init; }

        [JsonPropertyName("source_drawing_path")]
        public string? SourceDrawingPath { get; init; }

        [JsonPropertyName("source_drawing_hash")]
        public string? SourceDrawingHash { get; init; }

        [JsonPropertyName("source_handle")]
        public required string SourceHandle { get; init; }

        [JsonPropertyName("intersection_wcs")]
        public required double[] IntersectionWcs { get; init; }
    }

    /// <summary>rerun action for one planned object (plan §7.12).</summary>
    public enum PlanAction
    {
        Create,
        Unchanged,
        Update,
        Replace,
        ReviewRequired,
        Excluded,
    }

    /// <summary>
    /// Exact foreign Civil object identities selected by the SEC-02 resolver.  These
    /// handles are evidence, not ownership: APPLY must revalidate them and may only
    /// replace Mahod-owned annotations around the existing view.
    /// </summary>
    public sealed class ManualSectionReusePlan
    {
        [JsonPropertyName("sample_line_group_handle")]
        public required string SampleLineGroupHandle { get; init; }

        [JsonPropertyName("sample_line_handle")]
        public required string SampleLineHandle { get; init; }

        [JsonPropertyName("section_view_handle")]
        public required string SectionViewHandle { get; init; }

        [JsonPropertyName("sample_line_name")]
        public string? SampleLineName { get; init; }

        [JsonPropertyName("section_view_name")]
        public string? SectionViewName { get; init; }

        [JsonPropertyName("preserved_section_view_style")]
        public string? PreservedSectionViewStyle { get; init; }

        [JsonPropertyName("preserved_band_styles")]
        public List<string> PreservedBandStyles { get; init; } = new();
    }

    /// <summary>One planned section derived from one CL record (plan §7.1 PLAN output).</summary>
    public sealed class SectionPlanRecord
    {
        [JsonPropertyName("explicit_surface_pair")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ProjectProfile.SectionsProfile.SourcesProfile.SurfacePair? ExplicitSurfacePair { get; set; }

        [JsonPropertyName("record_id")]
        public required string RecordId { get; init; }

        [JsonPropertyName("section_id")]
        public string? SectionId { get; set; }

        [JsonPropertyName("cl")]
        public required ClSourceRecord Cl { get; init; }

        [JsonPropertyName("candidate_crossings")]
        public List<AlignmentCrossing> CandidateCrossings { get; init; } = new();

        [JsonPropertyName("selected_alignment")]
        public string? SelectedAlignment { get; set; }

        [JsonPropertyName("selected_crossing")]
        public AlignmentCrossing? SelectedCrossing { get; set; }

        [JsonPropertyName("station")]
        public double? Station { get; set; }

        [JsonPropertyName("skew_deg")]
        public double? SkewDeg { get; set; }

        [JsonPropertyName("endpoint_offset_a")]
        // Perpendicular alignment metadata. Presentation coordinates are signed
        // distances along the actual CL, provided by SectionCutFrame instead.
        public double? EndpointOffsetA { get; set; }

        [JsonPropertyName("endpoint_offset_b")]
        public double? EndpointOffsetB { get; set; }

        [JsonPropertyName("left_extent")]
        public double? LeftExtent { get; set; }

        [JsonPropertyName("right_extent")]
        public double? RightExtent { get; set; }

        [JsonPropertyName("planned_sources")]
        public List<SectionSourcePlan> PlannedSources { get; init; } = new();

        [JsonPropertyName("utility_coverage")]
        public UtilityCoverageReport UtilityCoverage { get; set; } = new();

        /// <summary>
        /// Hebrew labels of utilities that will be PROJECTED into this section from
        /// drawing linework (the UT XREF's polylines). Computed at PLAN time so the
        /// panel can answer "אין מערכות?" with the truth before anything is created.
        /// </summary>
        [JsonPropertyName("projected_systems")]
        public List<string> ProjectedSystems { get; init; } = new();

        [JsonPropertyName("projected_entities")]
        public List<ProjectedEntityPlan> ProjectedEntities { get; init; } = new();

        [JsonPropertyName("presentation_coverage")]
        public SectionPresentationCoveragePlan PresentationCoverage { get; set; } = new();

        /// <summary>Source presentation digest captured before any saved span-label decision is applied.</summary>
        [JsonPropertyName("pre_manual_span_evidence_digest")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PreManualSpanEvidenceDigest { get; set; }

        /// <summary>Verified PLAN host identity; required to distinguish host CLs from side DWGs with no XREF chain.</summary>
        [JsonPropertyName("span_decision_host_drawing_path")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SpanDecisionHostDrawingPath { get; set; }

        /// <summary>
        /// One immutable PLAN-time direction decision for every strip that carries a
        /// car, bus or bicycle. APPLY consumes this exact evidence; it never resolves
        /// direction again and never interprets the sign of the section offset.
        /// </summary>
        [JsonPropertyName("traffic_directions")]
        public List<SectionTrafficDirectionPlan> TrafficDirections { get; init; } = new();

        /// <summary>Canonical native straight-segment proof, or its explicit refusal. Absent on historical PLANs.</summary>
        [JsonPropertyName("traffic_straight_scope_evidence")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TrafficStraightScopeEvidence { get; set; }

        /// <summary>Source tracks within unchanged composite envelopes. Empty on historical PLANs.</summary>
        [JsonIgnore]
        public List<SectionCompositeTrafficEnvelopePlan> CompositeTrafficEnvelopes { get; init; } = new();

        private bool _compositeTrafficEnvelopesWasPresent;

        /// <summary>
        /// Preserve the exact historical wire shape: absent stays absent even after a
        /// caller reads the non-null collection, explicit [] stays [], and populated
        /// source evidence is always serialized. Producer artifact hashes are not normalized.
        /// </summary>
        [JsonPropertyName("composite_traffic_envelopes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SectionCompositeTrafficEnvelopePlan>? SerializedCompositeTrafficEnvelopes
        {
            get => _compositeTrafficEnvelopesWasPresent || CompositeTrafficEnvelopes.Count != 0
                ? CompositeTrafficEnvelopes : null;
            init
            {
                CompositeTrafficEnvelopes = value ?? throw new System.Text.Json.JsonException(
                    "Composite traffic envelopes must be an array when present.");
                _compositeTrafficEnvelopesWasPresent = true;
            }
        }

        [JsonPropertyName("explicit_exclusion")]
        public SectionExclusionPlan? ExplicitExclusion { get; set; }

        [JsonPropertyName("planned_styles")]
        public Dictionary<string, string> PlannedStyles { get; init; } = new();

        [JsonPropertyName("planned_layout_position")]
        public double[]? PlannedLayoutPosition { get; set; } // [x, y] view origin

        /// <summary>
        /// Non-null only for one unambiguous, geometry-matching manual SectionView.
        /// Its native objects, names, styles, locations and ownership stay untouched.
        /// </summary>
        [JsonPropertyName("manual_section_reuse")]
        public ManualSectionReusePlan? ManualSectionReuse { get; set; }

        [JsonPropertyName("action")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public PlanAction Action { get; set; } = PlanAction.Create;

        [JsonPropertyName("logical_key")]
        public string? LogicalKey { get; set; }

        [JsonPropertyName("input_fingerprint")]
        public string? InputFingerprint { get; set; }

        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DeliveryStatus Status { get; set; } = DeliveryStatus.Discovered;

        [JsonPropertyName("findings")]
        public List<DeliveryFinding> Findings { get; init; } = new();
    }

    /// <summary>Serializable per-strip traffic-direction evidence.</summary>
    public sealed class SectionTrafficDirectionPlan
    {
        [JsonPropertyName("track_evidence_digest")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TrackEvidenceDigest { get; init; }

        [JsonPropertyName("from_offset_m")]
        public double FromOffsetM { get; init; }

        [JsonPropertyName("to_offset_m")]
        public double ToOffsetM { get; init; }

        [JsonPropertyName("lane_mid_offset_m")]
        public double LaneMidOffsetM { get; init; }

        [JsonPropertyName("strip_label")]
        public required string StripLabel { get; init; }

        /// <summary>road | bus | bike</summary>
        [JsonPropertyName("strip_kind")]
        public required string StripKind { get; init; }

        /// <summary>motor | bicycle</summary>
        [JsonPropertyName("evidence_mode")]
        public required string EvidenceMode { get; init; }

        /// <summary>resolved | unknown | ambiguous</summary>
        [JsonPropertyName("state")]
        public required string State { get; init; }

        /// <summary>along-alignment | against-alignment; null when unresolved.</summary>
        [JsonPropertyName("flow")]
        public string? Flow { get; init; }

        /// <summary>rear | front; derived only from resolved flow.</summary>
        [JsonPropertyName("office_car_view")]
        public string? OfficeCarView { get; init; }

        /// <summary>arrow | manual; null when unresolved.</summary>
        [JsonPropertyName("direction_source")]
        public string? DirectionSource { get; init; }

        [JsonPropertyName("direction_digest")]
        public string? DirectionDigest { get; init; }

        [JsonPropertyName("reason")]
        public required string Reason { get; init; }

        [JsonIgnore]
        public bool IsResolved =>
            string.Equals(State, "resolved", System.StringComparison.Ordinal) &&
            MahodAI.CivilDelivery.Shared.SectionVehicleDirectionPlanner
                .TryParseFlowToken(Flow, out _) &&
            MahodAI.CivilDelivery.Shared.SectionVehicleDirectionPlanner
                .IsSupportedDirectionSource(DirectionSource) &&
            MahodAI.CivilDelivery.Shared.SectionVehicleDirectionPlanner
                .IsSha256(DirectionDigest);
    }

    public sealed class SectionCompositeTrafficEnvelopePlan
    {
        [JsonPropertyName("source_authorities")]
        public List<SectionExternalSourceEvidence> SourceAuthorities { get; init; } = new();
        [JsonPropertyName("from_offset_m")]
        public double FromOffsetM { get; init; }
        [JsonPropertyName("to_offset_m")]
        public double ToOffsetM { get; init; }
        [JsonPropertyName("source_tracks")]
        public List<SectionTrafficTrackLogic.Track> SourceTracks { get; init; } = new();
    }

    /// <summary>PLAN-time promise for every visible dimension/strip/furniture row.</summary>
    public sealed class SectionPresentationCoveragePlan
    {
        [JsonPropertyName("plan_mark_count")]
        public int PlanMarkCount { get; set; }
        [JsonPropertyName("dimension_mark_count")]
        public int DimensionMarkCount { get; set; }
        [JsonPropertyName("dimension_offsets")]
        public List<double> DimensionOffsets { get; init; } = new();
        /// <summary>
        /// Exact normalized dimension anchors, including synthetic/real ROW boundary
        /// anchors.  APPLY draws from this normalized contract rather than from raw
        /// coincident source crossings; VERIFY recomputes every tick/label from it.
        /// </summary>
        [JsonPropertyName("dimension_marks")]
        public List<SectionDimensionMarkPlan> DimensionMarks { get; init; } = new();
        [JsonPropertyName("width_span_count")]
        public int WidthSpanCount { get; set; }
        [JsonPropertyName("named_strip_count")]
        public int NamedStripCount { get; set; }
        [JsonPropertyName("vehicle_strip_count")]
        public int VehicleStripCount { get; set; }
        [JsonPropertyName("office_car_strip_count")]
        public int OfficeCarStripCount { get; set; }
        [JsonPropertyName("evidence_digest")]
        public string? EvidenceDigest { get; set; }
        [JsonPropertyName("boundary_source")]
        public string? BoundarySource { get; set; }
        [JsonPropertyName("boundary_from_m")]
        public double? BoundaryFromM { get; set; }
        [JsonPropertyName("boundary_to_m")]
        public double? BoundaryToM { get; set; }
        [JsonPropertyName("row_authority_state")]
        public string? RowAuthorityState { get; set; }
        [JsonPropertyName("authoritative_row_source_key")]
        public string? AuthoritativeRowSourceKey { get; set; }
        [JsonPropertyName("row_candidate_source_keys")]
        public List<string> RowCandidateSourceKeys { get; init; } = new();
        [JsonPropertyName("explicit_span_overrides")]
        public List<SectionSpanLabelOverridePlan> ExplicitSpanOverrides { get; init; } = new();
        /// <summary>
        /// Every already-resolved span with the exact evidence that named it.  PLAN
        /// uses these rows only to propose labels for homologous unresolved spans;
        /// APPLY continues to consume the signed overrides and coverage digest.
        /// </summary>
        [JsonPropertyName("resolved_spans")]
        public List<SectionResolvedSpanPlan> ResolvedSpans { get; init; } = new();
        [JsonPropertyName("unresolved_spans")]
        public List<SectionUnresolvedSpanPlan> UnresolvedSpans { get; init; } = new();
        [JsonPropertyName("complete")]
        public bool Complete { get; set; }
    }

    public sealed class SectionDimensionMarkPlan
    {
        [JsonPropertyName("geometry_key")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? GeometryKey { get; init; }

        [JsonPropertyName("source_evidence")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SectionProjectionLogic.DimensionSourceEvidence>? SourceEvidence { get; init; }

        [JsonPropertyName("offset_m")]
        public double OffsetM { get; init; }

        [JsonPropertyName("kind")]
        public required string Kind { get; init; }

        [JsonPropertyName("label")]
        public required string Label { get; init; }

        [JsonPropertyName("color_index")]
        public short ColorIndex { get; init; }
    }

    public sealed class SectionSpanLabelOverridePlan
    {
        [JsonPropertyName("offset_m")]
        public double OffsetM { get; init; }
        [JsonPropertyName("label")]
        public required string Label { get; init; }
        /// <summary>traffic-arrow | manual-profile</summary>
        [JsonPropertyName("source")]
        public required string Source { get; init; }
        [JsonPropertyName("evidence")]
        public string? Evidence { get; init; }
    }

    public sealed class SectionUnresolvedSpanPlan
    {
        [JsonPropertyName("from_offset_m")]
        public double FromOffsetM { get; init; }
        [JsonPropertyName("to_offset_m")]
        public double ToOffsetM { get; init; }
        [JsonPropertyName("width_m")]
        public double WidthM { get; init; }
        [JsonPropertyName("left_kind")]
        public required string LeftKind { get; init; }
        [JsonPropertyName("right_kind")]
        public required string RightKind { get; init; }
        [JsonPropertyName("reason")]
        public required string Reason { get; init; }
        [JsonPropertyName("homology_group_key")]
        public string? HomologyGroupKey { get; set; }
        [JsonPropertyName("homologous_span_count")]
        public int HomologousSpanCount { get; set; }
        [JsonPropertyName("suggested_label")]
        public string? SuggestedLabel { get; set; }
        /// <summary>none | low | medium | high | conflict</summary>
        [JsonPropertyName("suggestion_confidence")]
        public string SuggestionConfidence { get; set; } = "none";
        [JsonPropertyName("suggestion_support_count")]
        public int SuggestionSupportCount { get; set; }
        [JsonPropertyName("suggestion_conflict_count")]
        public int SuggestionConflictCount { get; set; }
        [JsonPropertyName("suggestion_sources")]
        public List<string> SuggestionSources { get; init; } = new();
        [JsonPropertyName("suggestion_evidence")]
        public List<string> SuggestionEvidence { get; init; } = new();
        [JsonPropertyName("strong_review_candidate")]
        public bool StrongReviewCandidate { get; set; }
    }

    public sealed class SectionResolvedSpanPlan
    {
        [JsonPropertyName("from_offset_m")]
        public double FromOffsetM { get; init; }
        [JsonPropertyName("to_offset_m")]
        public double ToOffsetM { get; init; }
        [JsonPropertyName("width_m")]
        public double WidthM { get; init; }
        [JsonPropertyName("left_kind")]
        public required string LeftKind { get; init; }
        [JsonPropertyName("right_kind")]
        public required string RightKind { get; init; }
        [JsonPropertyName("label")]
        public required string Label { get; init; }
        /// <summary>source-mark | traffic-arrow | boundary-rule | manual-profile</summary>
        [JsonPropertyName("evidence_source")]
        public required string EvidenceSource { get; init; }
        [JsonPropertyName("evidence_digest")]
        public required string EvidenceDigest { get; init; }
    }

    public sealed class SectionExclusionPlan
    {
        [JsonPropertyName("finding_code")]
        public required string FindingCode { get; init; }
        [JsonPropertyName("reason")]
        public required string Reason { get; init; }
        [JsonPropertyName("approved_by")]
        public required string ApprovedBy { get; init; }
        [JsonPropertyName("approved_at_utc")]
        public System.DateTime ApprovedAtUtc { get; init; }
    }

    /// <summary>
    /// One external DWG whose exact bytes contributed CL, projected-utility or
    /// plan-mark geometry to the PLAN. Paths are resolved before serialization.
    /// </summary>
    public sealed class SectionExternalSourceEvidence
    {
        [JsonPropertyName("source_path")]
        public required string SourcePath { get; init; }

        [JsonPropertyName("source_name")]
        public required string SourceName { get; init; }

        [JsonPropertyName("sha256")]
        public required string Sha256 { get; init; }

        [JsonPropertyName("roles")]
        public List<string> Roles { get; init; } = new();

        [JsonPropertyName("source_chain")]
        public string? SourceChain { get; init; }

        /// <summary>
        /// Present when PLAN read an unsaved/open drawing database rather than a side
        /// database. APPLY must find that same live database at this exact revision.
        /// </summary>
        [JsonPropertyName("live_database_revision")]
        public string? LiveDatabaseRevision { get; init; }

        [JsonPropertyName("requires_live_database")]
        public bool RequiresLiveDatabase { get; init; }
    }

    /// <summary>The full read-only PLAN result for one run.</summary>
    public sealed class SectionPlan
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName("run_id")]
        public required string RunId { get; init; }

        [JsonPropertyName("project_profile_id")]
        public required string ProjectProfileId { get; init; }

        [JsonPropertyName("project_profile_hash")]
        public string? ProjectProfileHash { get; init; }

        /// <summary>
        /// The drawing this plan describes. APPLY refuses a plan whose drawing is not
        /// the active one, so a multi-drawing Civil session cannot cross wires.
        /// </summary>
        [JsonPropertyName("source_drawing")]
        public string? SourceDrawing { get; set; }

        [JsonPropertyName("source_database_revision")]
        public string? SourceDatabaseRevision { get; set; }

        [JsonPropertyName("source_unit_code")]
        public int? SourceUnitCode { get; set; }

        [JsonPropertyName("source_unit_name")]
        public string? SourceUnitName { get; set; }

        [JsonPropertyName("physical_unit_code")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? PhysicalUnitCode { get; set; }

        [JsonPropertyName("physical_unit_declaration_digest")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PhysicalUnitDeclarationDigest { get; set; }

        [JsonPropertyName("source_drawing_fingerprint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SourceDrawingFingerprint { get; set; }

        [JsonPropertyName("physical_unit_evidence")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PhysicalUnitEvidence { get; set; }

        [JsonPropertyName("external_sources")]
        public List<SectionExternalSourceEvidence> ExternalSources { get; init; } = new();

        [JsonPropertyName("records")]
        public List<SectionPlanRecord> Records { get; init; } = new();

        [JsonPropertyName("findings")]
        public List<DeliveryFinding> Findings { get; init; } = new();

        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DeliveryStatus Status { get; set; } = DeliveryStatus.Discovered;
    }
}
