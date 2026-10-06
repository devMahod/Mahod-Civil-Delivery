using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// One structured finding, per locked plan §5.3. No free-floating warnings:
    /// when a source can be identified it must be referenced.
    /// </summary>
    public sealed class DeliveryFinding
    {
        [JsonPropertyName("finding_id")]
        public string FindingId { get; init; } = Guid.NewGuid().ToString("N");

        /// <summary>Machine code, e.g. SEC-CL-NO-INTERSECTION / EST-UNIT-MISMATCH.</summary>
        [JsonPropertyName("code")]
        public required string Code { get; init; }

        /// <summary>sections | estimate | shared</summary>
        [JsonPropertyName("domain")]
        public required string Domain { get; init; }

        [JsonPropertyName("severity")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public required FindingSeverity Severity { get; init; }

        [JsonPropertyName("title")]
        public required string Title { get; init; }

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        [JsonPropertyName("project_profile_id")]
        public string? ProjectProfileId { get; init; }

        [JsonPropertyName("run_id")]
        public string? RunId { get; init; }

        [JsonPropertyName("source_refs")]
        public List<ProvenanceRef> SourceRefs { get; init; } = new();

        [JsonPropertyName("affected_record_ids")]
        public List<string> AffectedRecordIds { get; init; } = new();

        // Optional, native-source bounds for a failed projection entity. Missing or
        // malformed evidence keeps the failure global; it never implies no impact.
        [JsonPropertyName("projection_role")]
        public string? ProjectionRole { get; init; }

        [JsonPropertyName("source_bounds_wcs")]
        public double[]? SourceBoundsWcs { get; init; }

        [JsonPropertyName("evidence_refs")]
        public List<string> EvidenceRefs { get; init; } = new();

        [JsonPropertyName("recommended_action")]
        public string? RecommendedAction { get; init; }

        [JsonPropertyName("created_at_utc")]
        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

        [JsonPropertyName("resolved_at_utc")]
        public DateTime? ResolvedAtUtc { get; set; }

        [JsonPropertyName("resolution")]
        public string? Resolution { get; set; }

        [JsonPropertyName("resolved_by")]
        public string? ResolvedBy { get; set; }
    }

    /// <summary>Locked Sections finding codes (plan §16). Add codes, never replace with generic exceptions.</summary>
    public static class SectionFindingCodes
    {
        public const string ClNoIntersection = "SEC-CL-NO-INTERSECTION";
        public const string ClMultipleIntersections = "SEC-CL-MULTIPLE-INTERSECTIONS";
        public const string AlignmentAmbiguous = "SEC-ALIGNMENT-AMBIGUOUS";
        public const string XrefTransformInvalid = "SEC-XREF-TRANSFORM-INVALID";
        public const string XrefMissing = "SEC-XREF-MISSING";
        public const string ClDegenerate = "SEC-CL-DEGENERATE";
        public const string ClSourceMissing = "SEC-CL-SOURCE-MISSING";
        public const string ClStationLabelMismatch = "SEC-CL-STATION-LABEL-MISMATCH";
        public const string SampleLineGroupCreateFailed = "SEC-SAMPLELINE-GROUP-CREATE-FAILED";
        public const string SampleLineCreateFailed = "SEC-SAMPLELINE-CREATE-FAILED";
        public const string SourceMissing = "SEC-SOURCE-MISSING";
        public const string SourceSamplingFailed = "SEC-SOURCE-SAMPLING-FAILED";
        public const string GroupSamplingForeign = "SEC-GROUP-SAMPLING-FOREIGN";
        public const string SharedResourceChangeRequired = "SEC-SHARED-RESOURCE-CHANGE-REQUIRED";
        public const string EvidenceWriteFailed = "SEC-EVIDENCE-WRITE-FAILED";
        public const string PlanEvidenceInvalid = "SEC-PLAN-EVIDENCE-INVALID";
        public const string AnnotationLinetypeMissing = "SEC-ANNOTATION-LINETYPE-MISSING";
        public const string AnnotationLinetypeNotApplied = "SEC-ANNOTATION-LINETYPE-NOT-APPLIED";
        public const string AnnotationInventoryConflict = "SEC-ANNOTATION-INVENTORY-CONFLICT";
        public const string AnnotationRegistryRepairable = "SEC-ANNOTATION-REGISTRY-REPAIRABLE";
        public const string AnnotationRegistryRepaired = "SEC-ANNOTATION-REGISTRY-REPAIRED";
        public const string AnnotationRepairScopeRequired = "SEC-ANNOTATION-REPAIR-SCOPE";
        public const string SectionViewCreateFailed = "SEC-SECTIONVIEW-CREATE-FAILED";
        public const string StyleMissing = "SEC-STYLE-MISSING";
        public const string PresentationStyleBuiltIn = "SEC-PRESENTATION-STYLE-BUILTIN";
        public const string DatumUnproven = "SEC-DATUM-UNPROVEN";
        public const string BandStyleMissing = "SEC-BAND-STYLE-MISSING";
        public const string LabelStyleMissing = "SEC-LABEL-STYLE-MISSING";
        public const string UtilityUnsupported = "SEC-UTILITY-UNSUPPORTED";
        public const string UtilityProjectionFailed = "SEC-UTILITY-PROJECTION-FAILED";
        public const string ProjectionGeometryUnsupported = "SEC-PROJECTION-GEOMETRY-UNSUPPORTED";
        public const string ProjectionGeometryOutsideCuts = "SEC-PROJECTION-GEOMETRY-OUTSIDE-CUTS";
        /// <summary>
        /// An unreadable HA area meets a cut whose every strip already carries a
        /// confident name: it can neither name nor contradict anything, so it is
        /// kept visible as a warning and does not block creation (live 06/09).
        /// </summary>
        public const string ProjectionRegionUnreadableNamed = "SEC-PROJECTION-REGION-UNREADABLE-NAMED";
        /// <summary>
        /// b7: the ONLY topology failure of a single-loop self-intersecting HA area was
        /// replaced, for one cut, by a decided local cut proof (every span classified).
        /// The source area is still invalid; this is a warning, not a repair or an area.
        /// </summary>
        public const string ProjectionRegionLocalCutProven = "SEC-PROJECTION-REGION-LOCAL-CUT-PROVEN";
        public const string UtilityElevationOutOfRange = "SEC-UTILITY-ELEVATION-OUT-OF-RANGE";
        public const string OwnershipConflict = "SEC-OWNERSHIP-CONFLICT";
        public const string OwnedStateIncomplete = "SEC-OWNED-STATE-INCOMPLETE";
        public const string ManualSectionReused = "SEC-MANUAL-SECTION-REUSED";
        public const string ManualSectionAmbiguous = "SEC-MANUAL-SECTION-AMBIGUOUS";
        public const string ManualSectionGeometryMismatch = "SEC-MANUAL-SECTION-GEOMETRY-MISMATCH";
        public const string ManualSectionPresentationMismatch = "SEC-MANUAL-SECTION-PRESENTATION-MISMATCH";
        public const string VerifyMismatch = "SEC-VERIFY-MISMATCH";
        public const string LayoutUnresolved = "SEC-LAYOUT-UNRESOLVED";
        public const string VehicleBlockUnavailable = "SEC-VEHICLE-BLOCK-UNAVAILABLE";
        public const string TrafficDirectionUnresolved = "SEC-TRAFFIC-DIRECTION-UNRESOLVED";
        public const string TrafficDirectionEvidenceMismatch = "SEC-TRAFFIC-DIRECTION-EVIDENCE-MISMATCH";
        public const string SlopeUnproven = "SEC-SLOPE-UNPROVEN";
        public const string PresentationCoverageMissing = "SEC-PRESENTATION-COVERAGE-MISSING";
        /// <summary>Info only: a strip beside a bus-lane (נת"צ) line was named by the tool — check it is not the bus lane.</summary>
        public const string StripBesideBusLaneLine = "SEC-STRIP-BESIDE-BUS-LANE-LINE";
        public const string PlanMarksMissing = "SEC-PLAN-MARKS-MISSING";
        public const string RowAuthorityUnresolved = "SEC-ROW-AUTHORITY-UNRESOLVED";
        public const string SpanLabelDecisionStale = "SEC-SPAN-LABEL-DECISION-STALE";
        public const string IncompleteBatch = "SEC-INCOMPLETE-BATCH";
        public const string NoMutationEvidence = "SEC-NO-MUTATION-EVIDENCE";
        public const string UnitNotMetres = "SEC-UNIT-NOT-METRES";
        public const string DrawingChanged = "SEC-DRAWING-CHANGED-SINCE-PLAN";
        public const string ExternalSourceChanged = "SEC-EXTERNAL-SOURCE-CHANGED-SINCE-PLAN";
        public const string XrefTraversalUnresolved = "SEC-XREF-TRAVERSAL-UNRESOLVED";
        public const string XrefCycle = "SEC-XREF-CYCLE";
        public const string ExclusionStale = "SEC-EXCLUSION-STALE";
        public const string SourceIdentityConflict = "SEC-SOURCE-IDENTITY-CONFLICT";
        public const string ClNonInstructionIgnored = "SEC-CL-NON-INSTRUCTION-IGNORED";
        public const string SetupScanIncomplete = "SEC-SETUP-SCAN-INCOMPLETE";
    }

    /// <summary>Locked Estimate finding codes (plan §16).</summary>
    public static class EstimateFindingCodes
    {
        public const string SourceMissing = "EST-SOURCE-MISSING";
        public const string SourceScopePolicyUnapproved = "EST-SOURCE-SCOPE-POLICY-UNAPPROVED";
        public const string XrefPolicyUnapproved = "EST-XREF-POLICY-UNAPPROVED";
        public const string XrefPolicyUnsupported = "EST-XREF-POLICY-UNSUPPORTED";
        public const string XrefTraversalUnresolved = "EST-XREF-TRAVERSAL-UNRESOLVED";
        public const string XrefTransformInvalid = "EST-XREF-TRANSFORM-INVALID";
        public const string XrefCycle = "EST-XREF-CYCLE";
        public const string ExternalSourceChanged = "EST-EXTERNAL-SOURCE-CHANGED";
        public const string UnsupportedEntityCoverage = "EST-UNSUPPORTED-ENTITY-COVERAGE";
        public const string NonQuantityEntityTypesSkipped = "EST-NON-QUANTITY-ENTITY-TYPES-SKIPPED";
        public const string MeasurementFailed = "EST-MEASUREMENT-FAILED";
        public const string DrawingChanged = "EST-DRAWING-CHANGED-SINCE-SCAN";
        public const string UnitUnknown = "EST-UNIT-UNKNOWN";
        public const string UnitMismatch = "EST-UNIT-MISMATCH";
        public const string DuplicateSource = "EST-DUPLICATE-SOURCE";
        public const string XrefDoubleCountRisk = "EST-XREF-DOUBLECOUNT-RISK";
        public const string OverlapRisk = "EST-OVERLAP-RISK";
        public const string CrossSourceDuplicateRisk = "EST-CROSS-SOURCE-DUPLICATE-RISK";
        public const string Unmapped = "EST-UNMAPPED";
        public const string NotAQuantity = "EST-NOT-A-QUANTITY";
        public const string QuantitySignificanceReview = "EST-QUANTITY-SIGNIFICANCE-REVIEW";
        public const string MixedDimensionLayer = "EST-MIXED-DIMENSION-LAYER";
        public const string AdjustmentUnverified = "EST-ADJUSTMENT-UNVERIFIED";
        public const string MissingPrice = "EST-MISSING-PRICE";
        public const string OverrideIncomplete = "EST-OVERRIDE-INCOMPLETE";
        public const string PriceSourceUnverified = "EST-PRICE-SOURCE-UNVERIFIED";
        public const string LandQContractUnverified = "EST-LANDQ-CONTRACT-UNVERIFIED";
        public const string LandQSubmissionFailed = "EST-LANDQ-SUBMISSION-FAILED";
        public const string ExportFailed = "EST-EXPORT-FAILED";
        public const string TraceIncomplete = "EST-TRACE-INCOMPLETE";
        public const string ConfigurationAmbiguous = "EST-CONFIGURATION-AMBIGUOUS";
        public const string NonPositiveMoney = "EST-NONPOSITIVE-MONEY";
    }
}
