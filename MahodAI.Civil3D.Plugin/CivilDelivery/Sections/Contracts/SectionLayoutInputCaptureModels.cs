using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts
{
    /// <summary>
    /// canonical_layout_inputs.json: a diagnostic receipt published beside exactly one
    /// selected-record VERIFY result, inside the same evidence bundle and manifest. It
    /// copies the detached measured-layout inputs at the existing solver boundary and
    /// the identities of that same operation. It is not a VERIFY PASS, not layout
    /// acceptance, and never an input to PLAN, APPLY or VERIFY.
    /// </summary>
    internal sealed class SectionLayoutInputCaptureArtifact
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName("purpose")]
        public string Purpose { get; init; } =
            "diagnostic-only measured-layout input capture; not a VERIFY PASS and not layout acceptance";

        /// <summary>captured | not-captured</summary>
        [JsonPropertyName("capture_status")]
        public required string CaptureStatus { get; init; }

        /// <summary>The real reason no snapshot exists; null only when captured.</summary>
        [JsonPropertyName("capture_reason")]
        public string? CaptureReason { get; init; }

        [JsonPropertyName("identity")]
        public required SectionLayoutInputCaptureIdentity Identity { get; init; }

        [JsonPropertyName("verify")]
        public required SectionLayoutInputCaptureVerifyOutcome Verify { get; init; }

        /// <summary>Null when VERIFY never reached the measured-layout boundary of the record.</summary>
        [JsonPropertyName("record")]
        public SectionLayoutInputCaptureRecord? Record { get; init; }
    }

    /// <summary>
    /// Identities read during the same VERIFY publication. A value that is not
    /// available in-process stays null (with its reason where one exists); a
    /// historical pin is never copied in as if it had been loaded now.
    /// </summary>
    internal sealed class SectionLayoutInputCaptureIdentity
    {
        [JsonPropertyName("host_path")]
        public string? HostPath { get; init; }

        /// <summary>SHA-256 of the saved host file on disk, as the run manifest hashes it.</summary>
        [JsonPropertyName("host_sha256")]
        public string? HostSha256 { get; init; }

        [JsonPropertyName("host_hash_error")]
        public string? HostHashError { get; init; }

        [JsonPropertyName("post_apply_database_revision")]
        public string? PostApplyDatabaseRevision { get; init; }

        /// <summary>Unsaved in-memory edits are visible only through this revision.</summary>
        [JsonPropertyName("verified_database_revision")]
        public string? VerifiedDatabaseRevision { get; init; }

        [JsonPropertyName("plugin_assembly_path")]
        public string? PluginAssemblyPath { get; init; }

        [JsonPropertyName("plugin_assembly_sha256")]
        public string? PluginAssemblySha256 { get; init; }

        [JsonPropertyName("plugin_build_version")]
        public string? PluginBuildVersion { get; init; }

        [JsonPropertyName("plugin_git_sha")]
        public string? PluginGitSha { get; init; }

        [JsonPropertyName("plugin_package_revision")]
        public string? PluginPackageRevision { get; init; }

        [JsonPropertyName("core_assembly_path")]
        public string? CoreAssemblyPath { get; init; }

        [JsonPropertyName("core_assembly_sha256")]
        public string? CoreAssemblySha256 { get; init; }

        [JsonPropertyName("core_build_version")]
        public string? CoreBuildVersion { get; init; }

        [JsonPropertyName("project_profile_id")]
        public string? ProjectProfileId { get; init; }

        [JsonPropertyName("project_profile_sha256")]
        public string? ProjectProfileSha256 { get; init; }

        [JsonPropertyName("plan_run_id")]
        public string? PlanRunId { get; init; }

        [JsonPropertyName("apply_run_id")]
        public string? ApplyRunId { get; init; }

        [JsonPropertyName("verify_run_id")]
        public string? VerifyRunId { get; init; }

        [JsonPropertyName("selected_record_id")]
        public string? SelectedRecordId { get; init; }
    }

    /// <summary>The VERIFY verdict exactly as published; the snapshot never changes it.</summary>
    internal sealed class SectionLayoutInputCaptureVerifyOutcome
    {
        [JsonPropertyName("scope")]
        public required string Scope { get; init; }

        [JsonPropertyName("verify_status")]
        public required string VerifyStatus { get; init; }

        /// <summary>Null when VERIFY produced no per-record result for the selected record.</summary>
        [JsonPropertyName("record_status")]
        public string? RecordStatus { get; init; }

        /// <summary>Failing check names of the selected record; null when no record result exists.</summary>
        [JsonPropertyName("failed_checks")]
        public List<string>? FailedChecks { get; init; }

        [JsonPropertyName("error_findings")]
        public List<string> ErrorFindings { get; init; } = new();
    }

    /// <summary>
    /// Built inside selected-record VERIFY right after native_measured_label_layout_exact,
    /// from values that check already read. No Entity, ObjectId or Transaction is kept.
    /// </summary>
    internal sealed class SectionLayoutInputCaptureRecord
    {
        [JsonPropertyName("record_id")]
        public required string RecordId { get; init; }

        [JsonPropertyName("logical_key")]
        public string? LogicalKey { get; init; }

        /// <summary>Handle of the SectionView opened by this VERIFY; null when it was missing.</summary>
        [JsonPropertyName("section_view_handle")]
        public string? SectionViewHandle { get; init; }

        /// <summary>
        /// refused-before-capture | compute-failed-after-capture |
        /// native-readback-mismatch | layout-exact
        /// </summary>
        [JsonPropertyName("layout_stage")]
        public required string LayoutStage { get; init; }

        /// <summary>The unchanged native_measured_label_layout_exact verdict.</summary>
        [JsonPropertyName("layout_check_pass")]
        public bool LayoutCheckPass { get; init; }

        [JsonPropertyName("layout_error")]
        public string? LayoutError { get; init; }

        /// <summary>Why label_metadata is null; null whenever the registry was readable.</summary>
        [JsonPropertyName("annotation_registry_error")]
        public string? AnnotationRegistryError { get; init; }

        /// <summary>Null when a guard stopped before the solver boundary.</summary>
        [JsonPropertyName("inputs")]
        public SectionLayoutCapturedInputs? Inputs { get; init; }

        [JsonPropertyName("counts")]
        public SectionLayoutCaptureCounts? Counts { get; init; }

        [JsonPropertyName("label_metadata")]
        public List<SectionLayoutAnnotationMetadata>? LabelMetadata { get; init; }

        /// <summary>Solver output, kept apart from the inputs; null unless the layout was computed.</summary>
        [JsonPropertyName("layout_output")]
        public SectionLayoutCapturedOutput? LayoutOutput { get; init; }
    }

    /// <summary>
    /// The solver input exactly as captured. Anchor is the canonical PLAN/live-ground
    /// anchor and Ink is already translated to it; neither is the final TextPosition.
    /// </summary>
    internal sealed class SectionLayoutCapturedInputs
    {
        [JsonPropertyName("top_labels")]
        public required List<SectionLayoutCapturedLabel> TopLabels { get; init; }

        [JsonPropertyName("bottom_labels")]
        public required List<SectionLayoutCapturedLabel> BottomLabels { get; init; }

        /// <summary>Values and order only. No handle is attached: none is inferred from bounds.</summary>
        [JsonPropertyName("fixed_obstacles")]
        public required List<SectionLayoutCapturedBounds> FixedObstacles { get; init; }

        [JsonPropertyName("median_text_height")]
        public double MedianTextHeight { get; init; }

        [JsonPropertyName("clearance")]
        public double Clearance { get; init; }

        [JsonPropertyName("maximum_rise")]
        public double MaximumRise { get; init; }
    }

    internal sealed class SectionLayoutCapturedLabel
    {
        /// <summary>The native handle of the measured DBText.</summary>
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("band")]
        public int Band { get; init; }

        [JsonPropertyName("anchor")]
        public required SectionLayoutCapturedPoint Anchor { get; init; }

        [JsonPropertyName("ink")]
        public required SectionLayoutCapturedBounds Ink { get; init; }
    }

    internal sealed class SectionLayoutCapturedPoint
    {
        [JsonPropertyName("x")]
        public double X { get; init; }

        [JsonPropertyName("y")]
        public double Y { get; init; }
    }

    internal sealed class SectionLayoutCapturedBounds
    {
        [JsonPropertyName("min_x")]
        public double MinX { get; init; }

        [JsonPropertyName("min_y")]
        public double MinY { get; init; }

        [JsonPropertyName("max_x")]
        public double MaxX { get; init; }

        [JsonPropertyName("max_y")]
        public double MaxY { get; init; }
    }

    /// <summary>Counts and the handle join as found; a gap is reported, never repaired.</summary>
    internal sealed class SectionLayoutCaptureCounts
    {
        [JsonPropertyName("top_labels")]
        public int TopLabels { get; init; }

        [JsonPropertyName("bottom_labels")]
        public int BottomLabels { get; init; }

        [JsonPropertyName("movable_labels")]
        public int MovableLabels { get; init; }

        [JsonPropertyName("unique_label_ids")]
        public int UniqueLabelIds { get; init; }

        [JsonPropertyName("duplicate_label_ids")]
        public List<string> DuplicateLabelIds { get; init; } = new();

        [JsonPropertyName("fixed_obstacles")]
        public int FixedObstacles { get; init; }

        /// <summary>Registry entries with metadata; null when the registry was unreadable.</summary>
        [JsonPropertyName("registered_annotations")]
        public int? RegisteredAnnotations { get; init; }

        [JsonPropertyName("label_ids_with_single_metadata")]
        public int? LabelIdsWithSingleMetadata { get; init; }

        [JsonPropertyName("label_ids_without_metadata")]
        public List<string>? LabelIdsWithoutMetadata { get; init; }

        [JsonPropertyName("label_ids_with_duplicate_metadata")]
        public List<string>? LabelIdsWithDuplicateMetadata { get; init; }
    }

    /// <summary>Display metadata the registry read already holds, copied by value.</summary>
    internal sealed class SectionLayoutAnnotationMetadata
    {
        [JsonPropertyName("handle")]
        public required string Handle { get; init; }

        [JsonPropertyName("entity_type")]
        public string? EntityType { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("text_style_name")]
        public string? TextStyleName { get; init; }

        [JsonPropertyName("text_height")]
        public double? TextHeight { get; init; }

        [JsonPropertyName("text_rotation")]
        public double? TextRotation { get; init; }

        [JsonPropertyName("text_width_factor")]
        public double? TextWidthFactor { get; init; }

        [JsonPropertyName("text_oblique")]
        public double? TextOblique { get; init; }

        [JsonPropertyName("text_position")]
        public double[]? TextPosition { get; init; }

        [JsonPropertyName("text_alignment_point")]
        public double[]? TextAlignmentPoint { get; init; }

        [JsonPropertyName("text_horizontal_mode")]
        public int? TextHorizontalMode { get; init; }

        [JsonPropertyName("text_vertical_mode")]
        public int? TextVerticalMode { get; init; }

        [JsonPropertyName("text_mirrored_in_x")]
        public bool? TextMirroredInX { get; init; }

        [JsonPropertyName("text_mirrored_in_y")]
        public bool? TextMirroredInY { get; init; }

        /// <summary>APPLY CoreSemantics bound to this exact handle; slopes, offsets and datum have none.</summary>
        [JsonPropertyName("core_semantics")]
        public List<SectionLayoutCoreSemantic> CoreSemantics { get; init; } = new();
    }

    internal sealed class SectionLayoutCoreSemantic
    {
        [JsonPropertyName("kind")]
        public required string Kind { get; init; }

        [JsonPropertyName("semantic_key")]
        public required string SemanticKey { get; init; }
    }

    internal sealed class SectionLayoutCapturedOutput
    {
        [JsonPropertyName("placed_labels")]
        public List<SectionLayoutPlacedLabel> PlacedLabels { get; init; } = new();

        [JsonPropertyName("overall_bounds")]
        public required SectionLayoutCapturedBounds OverallBounds { get; init; }

        [JsonPropertyName("leaders")]
        public List<SectionLayoutCapturedLeader> Leaders { get; init; } = new();

        [JsonPropertyName("clearance")]
        public double Clearance { get; init; }
    }

    internal sealed class SectionLayoutPlacedLabel
    {
        [JsonPropertyName("handle")]
        public required string Handle { get; init; }

        /// <summary>[x, y, z] solver-expected anchor.</summary>
        [JsonPropertyName("expected_position")]
        public required double[] ExpectedPosition { get; init; }

        [JsonPropertyName("final_bounds")]
        public SectionLayoutCapturedBounds? FinalBounds { get; init; }
    }

    internal sealed class SectionLayoutCapturedLeader
    {
        /// <summary>[x, y, z]</summary>
        [JsonPropertyName("start")]
        public required double[] Start { get; init; }

        /// <summary>[x, y, z]</summary>
        [JsonPropertyName("end")]
        public required double[] End { get; init; }
    }
}
