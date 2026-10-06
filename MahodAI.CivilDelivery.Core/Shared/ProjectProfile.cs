using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Versioned per-project configuration (locked plan §6). Generic product,
    /// project-specific data: 6422 is a profile, never a fork. For engineering and
    /// project rules, null is preferable to an invented default.
    /// </summary>
    public sealed class ProjectProfile
    {
        public int SchemaVersion { get; set; } = 1;
        public string ProfileId { get; set; } = string.Empty;
        public string? ProjectName { get; set; }
        public string? ProjectStage { get; set; }

        /// <summary>
        /// Explicit physical-unit decisions for host drawings (unitless, or an explicit INSUNITS that was reviewed). A
        /// drawing GUID plus saved full path survives Save but does not transfer
        /// approval to SaveAs, another drawing or an XREF. No metres default.
        /// </summary>
        public List<DrawingUnitDeclaration> DrawingUnitDeclarations { get; set; } = new();

        /// <summary>
        /// b24 (Codex 12:24 C): technical records that a host needed a unit review (suspicion, conflict, a related
        /// identity, or an engineer's own override), kept until — and after — a decision resolves them by its digest.
        /// Not an approval. Null for every profile without one, so existing profiles keep their bytes and hashes.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
        public List<DrawingUnitReview>? DrawingUnitReviews { get; set; }

        public sealed class DrawingUnitReview
        {
            public string? DrawingFingerprint { get; set; }
            public string? DrawingPath { get; set; }
            public int? ObservedInsunitsCode { get; set; }
            public string? Kind { get; set; }
            public string? Reason { get; set; }
            public string? Evidence { get; set; }
            public DateTime? ObservedAtUtc { get; set; }
            public string? RecordedBy { get; set; }
            /// <summary>The digest of the decision that resolves this review for the same identity; null while open.</summary>
            public string? ResolvedByDigest { get; set; }
        }

        public sealed class DrawingUnitDeclaration
        {
            public string? DrawingFingerprint { get; set; }
            public string? DrawingPath { get; set; }
            public int? OriginalInsunitsCode { get; set; }
            public int? PhysicalUnitCode { get; set; }
            public double? MetresPerUnit { get; set; }
            public bool Approved { get; set; }
            public string? ApprovedBy { get; set; }
            public DateTime? ApprovedAtUtc { get; set; }
            public string? Reason { get; set; }
            public string? Source { get; set; }
            // b24 (E4, la-038-ew): null = the original unitless-host metres declaration, so an existing approval keeps
            // its YAML, JSON and digest. A kind is written only for a decision on a host with an explicit INSUNITS.
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
            public string? DecisionKind { get; set; }
            // b24 (Codex 12:04 D): the Civil drawing-unit evidence a reviewed explicit-unit decision was made against —
            // not-applicable / unreadable / observed (+ the unit). A changed or lost observation reopens the review.
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
            public string? CivilDrawingUnitStatus { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
            public int? CivilDrawingUnitCode { get; set; }
        }

        public ProfileProvenance Provenance { get; set; } = new();
        public SectionsProfile Sections { get; set; } = new();
        public EstimateProfile Estimate { get; set; } = new();

        public sealed class ProfileProvenance
        {
            public string? Source { get; set; }
            public int Version { get; set; } = 1;
            public DateTime? CreatedAtUtc { get; set; }
            public string? CreatedBy { get; set; }
            public DateTime? ApprovedAtUtc { get; set; }
            public string? ApprovedBy { get; set; }
            public Dictionary<string, string> SourceHashes { get; set; } = new();
        }

        public sealed class SectionsProfile
        {
            public ClProfile Cl { get; set; } = new();
            public AlignmentsProfile Alignments { get; set; } = new();
            public DecisionsProfile Decisions { get; set; } = new();
            public SourcesProfile Sources { get; set; } = new();
            public StylesProfile Styles { get; set; } = new();
            public LayoutProfile Layout { get; set; } = new();
            public ProjectionProfile Projection { get; set; } = new();

            /// <summary>
            /// Projecting drawing linework into created sections: the utility polylines
            /// that arrive through the UT XREF, plan marks (curbs, lane lines), and the
            /// right-of-way. Civil samples only its own objects, so this is the honest
            /// path for systems that exist as plain geometry (6422: UT-3D.dwg).
            /// </summary>
            public sealed class ProjectionProfile
            {
                public bool Enabled { get; set; } = true;

                /// <summary>Cap on the section reach each side of the axis, metres. Null = the drawn CL decides.</summary>
                public double? MaxHalfWidthM { get; set; }

                /// <summary>Layer/XREF rules for utility lines. Empty = built-in dictionary only.</summary>
                public List<ProjectionRule> UtilityRules { get; set; } = new();

                /// <summary>Layer rules for plan marks: curbs, lane lines, sidewalks, right-of-way.</summary>
                public List<ProjectionRule> PlanMarkRules { get; set; } = new();

                /// <summary>
                /// Engineer-approved source identities from which ROW geometry may be
                /// shown.  A layer name alone is not authority: the exact source DWG
                /// SHA-256 is mandatory, while path/XREF patterns may narrow one
                /// insertion when the same bytes are attached more than once.
                /// </summary>
                public List<RowAuthority> RowAuthorities { get; set; } = new();

                /// <summary>Surface-name patterns that mean "existing ground" (styled distinctly).</summary>
                public List<string> ExistingSurfacePatterns { get; set; } = new();

                public sealed class ProjectionRule
                {
                    /// <summary>
                    /// Optional authority marker, e.g. "project-explicit". Any non-null
                    /// value (including an unknown one) prevents legacy tool-default
                    /// recognition. Null alone is not evidence that a rule is a default.
                    /// </summary>
                    public string? Origin { get; set; }
                    public string? LayerPattern { get; set; }
                    public string? XrefPattern { get; set; }
                    /// <summary>Hebrew label shown in the section; null = derived from the layer.</summary>
                    public string? Label { get; set; }
                    /// <summary>utility | row | curb | lane | sidewalk | mark</summary>
                    public string? Kind { get; set; }
                    public short? ColorIndex { get; set; }
                }

                public sealed class RowAuthority
                {
                    public string? SourceDrawingSha256 { get; set; }
                    public string? SourcePathPattern { get; set; }
                    public string? XrefPattern { get; set; }
                    public string? ApprovedBy { get; set; }
                    public DateTime? ApprovedAtUtc { get; set; }
                }
            }

            public sealed class ClProfile
            {
                public List<string> SourceFiles { get; set; } = new();
                public List<string> LayerPatterns { get; set; } = new();
                public List<string> AllowedEntityTypes { get; set; } = new();
                public double? IntersectionToleranceM { get; set; }

                // 1.4.1 (984, 06.10). Null keeps the b34 behaviour and the b34 effective-profile hash. Schema 11.
                public const string LayerScopeSourceFile = "source-file";
                public const string ModeStationMarkers = "station-markers";
                /// <summary>A station-marker half-width above this is refused as a likely unit mistake.</summary>
                public const double StationMarkerMaxHalfWidthM = 200.0;

                /// <summary>
                /// null = the CL layers are read from the host and from <see cref="SourceFiles"/> (legacy);
                /// "source-file" = they belong only to <see cref="SourceFiles"/>, as picked with "בחר קובץ CL נפרד",
                /// so host lines on a layer of the same name never become section instructions.
                /// </summary>
                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
                public string? LayerScope { get; set; }

                /// <summary>
                /// null = ordinary section lines (the drawn line decides direction and reach);
                /// "station-markers" = an engineer-chosen mode for short station ticks (e.g. AeccTickLine): each tick
                /// marks a station and is extended from its alignment crossing to ±<see cref="StationMarkerHalfWidthM"/>.
                /// </summary>
                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
                public string? Mode { get; set; }

                /// <summary>Engineer-entered half-width for station markers, metres; never derived or defaulted.</summary>
                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
                public double? StationMarkerHalfWidthM { get; set; }

                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
                public string? StationMarkerApprovedBy { get; set; }

                public NumberingProfile Numbering { get; set; } = new();

                public sealed class NumberingProfile
                {
                    /// <summary>null | "explicit-label" | "station-derived" — Gate-0 evidence decides, never a guess.</summary>
                    public string? Mode { get; set; }
                    public List<string> LabelLayerPatterns { get; set; } = new();
                    public bool StationValidationEnabled { get; set; }
                    public double? StationValidationToleranceM { get; set; }
                }
            }

            public sealed class AlignmentsProfile
            {
                public List<string> AllowedNames { get; set; } = new();
                /// <summary>Explicit user-confirmed CL-source → alignment mapping (persisted approvals).</summary>
                public Dictionary<string, string> ExplicitSourceToAlignment { get; set; } = new();
            }

            /// <summary>
            /// Durable, engineer-approved decisions for individual CL source entities.
            /// Source drawing hash + handle is the identity boundary: a changed CL file
            /// deliberately invalidates the old decision instead of applying it to new
            /// geometry that happens to reuse the same handle.
            /// </summary>
            public sealed class DecisionsProfile
            {
                public List<CrossingDecision> Crossings { get; set; } = new();
                public List<ExclusionDecision> Exclusions { get; set; } = new();
                public List<TrafficDirectionDecision> TrafficDirections { get; set; } = new();
                public List<SpanLabelDecision> SpanLabels { get; set; } = new();

                public sealed class CrossingDecision
                {
                    public string? SourceDrawingHash { get; set; }
                    public string? SourceHandle { get; set; }
                    public string? AlignmentName { get; set; }
                    public double? Station { get; set; }
                    public string? ApprovedBy { get; set; }
                    public DateTime? ApprovedAtUtc { get; set; }
                }

                public sealed class ExclusionDecision
                {
                    public string? SourceDrawingHash { get; set; }
                    public string? SourceHandle { get; set; }
                    /// <summary>The review finding that was shown when the exclusion was approved.</summary>
                    public string? FindingCode { get; set; }
                    public string? Reason { get; set; }
                    public string? ApprovedBy { get; set; }
                    public DateTime? ApprovedAtUtc { get; set; }
                }

                /// <summary>
                /// Engineer-approved traffic flow for one lane in one immutable CL
                /// source entity.  Flow is relative to the selected alignment
                /// ("along-alignment" | "against-alignment"), never to the sign of
                /// the lane offset.  Source hash + handle invalidates the decision if
                /// the CL drawing changes; alignment + lane midpoint prevents an
                /// approval for one carriageway from leaking into another.
                /// </summary>
                public sealed class TrafficDirectionDecision
                {
                    /// <summary>New explicit editor authority, never inferred for historical decisions.</summary>
                    public bool AllowArrowOverride { get; set; }
                    /// <summary>Exact source-track authority; absent on legacy midpoint decisions.</summary>
                    public string? TrackEvidenceDigest { get; set; }
                    public double? FromOffsetM { get; set; }
                    public double? ToOffsetM { get; set; }
                    public string? EvidenceMode { get; set; }
                    public string? SourceDrawingHash { get; set; }
                    public string? SourceHandle { get; set; }
                    public string? AlignmentName { get; set; }
                    public double? LaneMidOffsetM { get; set; }
                    public string? Flow { get; set; }
                    public string? ApprovedBy { get; set; }
                    public DateTime? ApprovedAtUtc { get; set; }
                }

                /// <summary>
                /// Explicit label for one otherwise-unidentified width span.  The
                /// immutable CL source and selected alignment scope the decision;
                /// exact signed offsets prevent it leaking after plan geometry moves.
                /// </summary>
                public sealed class SpanLabelDecision
                {
                    /// <summary>Versioned pre-manual physical target proof. Missing historical proof never grants current authority.</summary>
                    public string? PhysicalDecisionKey { get; set; }
                    /// <summary>Explicit host-owned CL scope, set only from matching PLAN/owning-drawing paths; never inferred for old decisions.</summary>
                    public string? HostSourceDrawingPath { get; set; }
                    /// <summary>True only for a new explicit current-span editor approval; old decisions default false.</summary>
                    public bool AllowSourceLabelOverride { get; set; }
                    public string? SourceDrawingHash { get; set; }
                    public string? SourceHandle { get; set; }
                    public string? AlignmentName { get; set; }
                    public double? FromOffsetM { get; set; }
                    public double? ToOffsetM { get; set; }
                    public string? Label { get; set; }
                    public string? ApprovedBy { get; set; }
                    public DateTime? ApprovedAtUtc { get; set; }
                }
            }

            public sealed class SourcesProfile
            {
                /// <summary>Optional reviewed roles, scoped to one live drawing/alignment.
                /// Empty preserves the legacy name-based selection contract.</summary>
                public List<SurfacePair> SurfacePairs { get; set; } = new();

                public sealed record SurfacePair
                {
                    public string? DrawingFingerprint { get; init; }
                    public string? AlignmentName { get; init; }
                    public string? AlignmentHandle { get; init; }
                    public string? ExistingName { get; init; }
                    public string? ExistingHandle { get; init; }
                    public string? DesignName { get; init; }
                    public string? DesignHandle { get; init; }
                    public string? ApprovedBy { get; init; }
                    public DateTime? ApprovedAtUtc { get; init; }
                }

                public List<SourceRule> SampledSourceRules { get; set; } = new();
                public List<SourceRule> UtilitySourceRules { get; set; } = new();

                public sealed class SourceRule
                {
                    public string? Name { get; set; }
                    /// <summary>surface | corridor | pipe-network | pressure-network | feature-line | polyline | block</summary>
                    public string? Kind { get; set; }
                    public string? NamePattern { get; set; }
                    public string? LayerPattern { get; set; }
                    public bool Required { get; set; }
                }
            }

            public sealed class StylesProfile
            {
                public string? TemplateSource { get; set; }
                public string? SampleLineGroupStyle { get; set; }
                public string? SampleLineStyle { get; set; }
                public string? SectionViewStyle { get; set; }
                public string? BandSetStyle { get; set; }
                public Dictionary<string, string> LabelStyles { get; set; } = new();
            }

            public sealed class LayoutProfile
            {
                /// <summary>null | "model-grid" | "paper" — Gate-0/Golden evidence decides.</summary>
                public string? Strategy { get; set; }
                public string? TargetSpace { get; set; }
                public string? SheetSize { get; set; }
                public int? Columns { get; set; }
                public int? Rows { get; set; }
                public double? SpacingX { get; set; }
                public double? SpacingY { get; set; }
                public bool? PlanIndexEnabled { get; set; }
            }
        }

        public sealed class EstimateProfile
        {
            /// <summary>
            /// Schema 6 (Codex 01:29, 02/10): the engineering discipline whose BoQ library this estimate uses —
            /// "roads" or "landscape". Null = a legacy profile: the roads library exactly as before, and the YAML/JSON bytes
            /// do not change. Any value (also an explicit "roads") requires schema 6, so an older build refuses the file
            /// instead of silently dropping the field and pricing a landscape project with the roads library.
            /// </summary>
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
            public string? Discipline { get; set; }

            /// <summary>Explicit source/category choices, schema 10; null preserves legacy all-source behavior.</summary>
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
            public MahodAI.CivilDelivery.Estimate.EstimateSourceSelection? SourceSelection { get; set; }

            public LandQProfile Landq { get; set; } = new();
            public QuantitySourcesProfile QuantitySources { get; set; } = new();
            public CatalogProfile Catalog { get; set; } = new();
            public PricingProfile Pricing { get; set; } = new();
            public EarthworksProfile Earthworks { get; set; } = new();
            public List<AdjustmentRule> ApprovedAdjustments { get; set; } = new();
            public List<PriceOverride> ProjectOverrides { get; set; } = new();
            /// <summary>Candidates seen in evidence but NOT approved — never auto-applied.</summary>
            public List<AdjustmentRule> CandidateAdjustments { get; set; } = new();

            /// <summary>
            /// Rule keys the engineer marked "not a construction quantity" (annotation,
            /// helper geometry, a stray hatch). They stay measured and traceable in the
            /// scan and the audit, and are kept out of the priced document. Only an
            /// engineer decision puts a key here; the tool never adds one by itself.
            /// </summary>
            public List<string> IgnoredRuleKeys { get; set; } = new();
            /// <summary>
            /// Audited replacements for <see cref="IgnoredRuleKeys"/>. Only entries
            /// with rule key, reason, approver and UTC timestamp may remove measured
            /// work from the priced line set. Legacy bare keys remain visible for
            /// migration but are not exclusion authority.
            /// </summary>
            public List<IgnoredRuleDecision> IgnoredRuleDecisions { get; set; } = new();

            public sealed class IgnoredRuleDecision
            {
                public string? RuleKey { get; set; }
                public string? Reason { get; set; }
                public string? ApprovedBy { get; set; }
                public DateTime? ApprovedAtUtc { get; set; }
            }

            /// <summary>
            /// Engineer decisions that bind measured groups (whatever their layer name) to a library family.
            /// One decision may cover several groups through its selectors. A decision whose selector, source
            /// scope, rule version or cited evidence no longer matches is kept but not applied ("re-approve").
            /// History is never deleted: a changed decision is superseded or revoked. A family approval never
            /// approves the family's catalog items; <see cref="FamilyDecision.ItemApprovals"/> do, one by one.
            /// </summary>
            public List<FamilyDecision> FamilyDecisions { get; set; } = new();

            /// <summary>
            /// Schema 5 (Codex contract E7F6811B): money approvals of an exact, proven subset of one measured group
            /// (e.g. one landscape specification inside a raw family). Kept apart from <see cref="FamilyDecisions"/> so a
            /// subset never poses as a complete family partition. Null = none; it is then omitted from YAML and JSON so
            /// older profiles keep their exact bytes. Every binding is recomputed from the current scan before use.
            /// </summary>
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
            public List<ScopedCatalogApproval>? ScopedCatalogApprovals { get; set; }

            public sealed class ScopedCatalogApproval
            {
                public string? ApprovalId { get; set; }
                /// <summary>active | superseded | revoked.</summary>
                public string? Status { get; set; }
                public string? WholeGroupId { get; set; }
                public string? RawRuleKey { get; set; }
                public string? PartitionHash { get; set; }
                public List<string> MemberRecordIds { get; set; } = new();
                public string? MembersHash { get; set; }
                public string? SpecificationHash { get; set; }
                public string? EvidenceHash { get; set; }
                public string? SourceScopeHash { get; set; }
                public FamilyItemApproval ItemApproval { get; set; } = new();
                public string? Reason { get; set; }
                public string? SupersededBy { get; set; }
                public string? RevokedReason { get; set; }
            }

            public sealed class FamilyDecision
            {
                /// <summary>Stable identity: SHA-256 of the canonical decision content (recomputed and checked).</summary>
                public string? DecisionId { get; set; }
                /// <summary>active | superseded | revoked</summary>
                public string? Status { get; set; }
                /// <summary>approve_family | exclude</summary>
                public string? Decision { get; set; }
                public string? LibraryId { get; set; }
                /// <summary>The base library rule id (never a width variant).</summary>
                public string? FamilyId { get; set; }
                /// <summary>LibraryIdentity.RuleFingerprint of the family's rule when it was approved.</summary>
                public string? RuleVersion { get; set; }
                public List<FamilySelector> Selectors { get; set; } = new();
                /// <summary>The evidence keys the approval relied on (the proposal's citations).</summary>
                public List<string> EvidenceKeys { get; set; } = new();
                public string? Reason { get; set; }
                public string? ApprovedBy { get; set; }
                public DateTime? ApprovedAtUtc { get; set; }
                public string? SupersededBy { get; set; }
                public string? RevokedReason { get; set; }
                public List<FamilyParameterOverride> ParameterOverrides { get; set; } = new();
                public List<FamilyItemApproval> ItemApprovals { get; set; } = new();
            }

            /// <summary>
            /// Which measured groups a family decision covers. A group matches when every non-empty field matches.
            /// The evidence fingerprint binds the approval to the meaning that was approved, not to quantities,
            /// handles or the drawing hash.
            /// </summary>
            public sealed class FamilySelector
            {
                /// <summary>Source drawing (XREF leaf name or the host marker), exact, case-insensitive.</summary>
                public string? Source { get; set; }
                /// <summary>Layer leaf, exact, case-insensitive. Empty = any layer (then an evidence match is required).</summary>
                public string? LayerLeaf { get; set; }
                /// <summary>length | area | count | volume</summary>
                public string? MeasurementKind { get; set; }
                /// <summary>The measurement method class before any drawn-width reclassification (open, hatch, closed-polyline...).</summary>
                public string? MethodClass { get; set; }
                public string? Unit { get; set; }
                public string? BlockName { get; set; }
                public List<FamilyEvidenceMatch> EvidenceMatch { get; set; } = new();
                /// <summary>EvidenceReader.Fingerprint of the covered records over the decision's evidence keys at approval.</summary>
                public string? EvidenceFingerprint { get; set; }
                /// <summary>Optional exact-scope visual approval binding. Changed content requires fresh explicit review.</summary>
                public MahodAI.CivilDelivery.Estimate.Recognition.FamilyVisualBinding? VisualBinding { get; set; }
                /// <summary>
                /// What the local recognition said about the covered records when the engineer approved (a family id,
                /// "abstained" or "mixed"). A later verdict that differs from it and from the approved family means new
                /// evidence contradicts the approval; an engineer's deliberate override of the verdict it saw is not.
                /// </summary>
                public string? LocalVerdictAtApproval { get; set; }
            }

            public sealed class FamilyEvidenceMatch
            {
                public string? Key { get; set; }
                /// <summary>An exact evidence text (after EvidenceReader.Clean) that must be present on every covered record.</summary>
                public string? Text { get; set; }
            }

            public sealed class FamilyParameterOverride
            {
                public string? Key { get; set; }
                public double? Value { get; set; }
                public string? Reason { get; set; }
                public string? ApprovedBy { get; set; }
                public DateTime? ApprovedAtUtc { get; set; }
            }

            public sealed class FamilyItemApproval
            {
                public string? CatalogCode { get; set; }
                public string? ExpectedUnit { get; set; }
                public string? ApprovedCatalogId { get; set; }
                public string? ApprovedCatalogHash { get; set; }
                public string? ApprovedCatalogItemFingerprint { get; set; }
                public string? ApprovedBy { get; set; }
                public DateTime? ApprovedAtUtc { get; set; }
            }

            /// <summary>
            /// Engineer links from a library recipe code (the price list the library was written in) to an item of the
            /// project's active price-list edition. A link applies only to the price list it was approved against (id,
            /// file hash and the item's fingerprint); on any other list it is kept and not applied. The tool proposes
            /// candidates with evidence and never records a link without an approver. History is never deleted: a
            /// changed link is superseded or revoked.
            /// </summary>
            public List<EditionItemLink> EditionLinks { get; set; } = new();

            public sealed class EditionItemLink
            {
                /// <summary>The library's own code (e.g. U51.06.3060).</summary>
                public string? LibraryCode { get; set; }
                /// <summary>The code of the linked item in the edition below (e.g. 51.06.1578).</summary>
                public string? CatalogCode { get; set; }
                public string? CatalogId { get; set; }
                public string? CatalogHash { get; set; }
                /// <summary>CatalogIdentity.ItemFingerprint of the linked item when the engineer approved it.</summary>
                public string? CatalogItemFingerprint { get; set; }
                /// <summary>How the candidate was shown: exact_text | truncated_prefix | consistent_parameters | parameter_conflict</summary>
                public string? Evidence { get; set; }
                /// <summary>active | superseded | revoked</summary>
                public string? Status { get; set; }
                public string? Reason { get; set; }
                public string? ApprovedBy { get; set; }
                public DateTime? ApprovedAtUtc { get; set; }
            }

            public sealed class LandQProfile
            {
                public string? ProjectRef { get; set; }
                public string? ContractVersion { get; set; }
            }

            public sealed class QuantitySourcesProfile
            {
                public string? RulesRef { get; set; }
                public string? SourceScopePolicy { get; set; }
                public string? XrefPolicy { get; set; }
                public List<QuantitySourceRule> Rules { get; set; } = new();

                public sealed class QuantitySourceRule
                {
                    public string? RuleKey { get; set; }
                    public string? LayerPattern { get; set; }
                    public string? EntityType { get; set; }
                    /// <summary>length | area | count | volume</summary>
                    public string? MeasurementKind { get; set; }
                    public string? CandidateCatalogCode { get; set; }
                    public string? ExpectedUnit { get; set; }
                    /// <summary>
                    /// Catalog mappings affect money, so a rule is priceable only when
                    /// both approval fields are present. Legacy/default rules without
                    /// this pair remain measured suggestions.
                    /// </summary>
                    public string? ApprovedBy { get; set; }
                    public DateTime? ApprovedAtUtc { get; set; }
                    /// <summary>
                    /// Immutable catalog identity against which this money-affecting
                    /// choice was approved. A legacy approval without this pair is a
                    /// proposal only; switching price books invalidates the approval.
                    /// </summary>
                    public string? ApprovedCatalogId { get; set; }
                    public string? ApprovedCatalogHash { get; set; }
                    /// <summary>SHA-256 of code + description + canonical/raw unit.</summary>
                    public string? ApprovedCatalogItemFingerprint { get; set; }
                    public string? Notes { get; set; }
                }
            }

            public sealed class CatalogProfile
            {
                public string? CatalogVersion { get; set; }
                public string? CatalogFile { get; set; }
                public string? CatalogFileHash { get; set; }
            }

            /// <summary>
            /// Every price book this project knows about - NTI, Dekel, a contractor's
            /// own - each identified by the SHA-256 of its file. One is active (the one
            /// <see cref="CatalogProfile"/> / <see cref="PricingProfile"/> point at);
            /// switching is an explicit, logged act, never a silent re-price.
            /// </summary>
            public List<PriceBookEntry> PriceBooks { get; set; } = new();

            /// <summary>
            /// The engineer's explicit sheet/header/column choice for one registered workbook (schema 5). It is bound to
            /// the entry's <see cref="PriceBookEntry.FileHash"/>, never stored with a hash of its own, and is immutable
            /// under the entry id: another interpretation of the same bytes (price column D instead of E) is a new entry.
            /// </summary>
            public sealed class PriceBookColumnMapping
            {
                public string? SheetName { get; set; }
                public int HeaderRow { get; set; }
                public string? CodeColumn { get; set; }
                public string? DescriptionColumn { get; set; }
                public string? UnitColumn { get; set; }
                public string? PriceColumn { get; set; }
            }

            public sealed class PriceBookEntry
            {
                /// <summary>Null = the reader's automatic first-sheet reading, exactly as before schema 5.</summary>
                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
                public PriceBookColumnMapping? Mapping { get; set; }

                /// <summary>Stable id used as the snapshot id, e.g. "nti-urban-082025", "dekel-052025".</summary>
                public string? Id { get; set; }
                /// <summary>"נתיבי ישראל" | "דקל" | free text.</summary>
                public string? Publisher { get; set; }
                public string? Edition { get; set; }
                /// <summary>Absolute path, or relative to the profile folder.</summary>
                public string? File { get; set; }
                public string? FileHash { get; set; }
                public int? ItemCount { get; set; }
                public DateTime? RegisteredAtUtc { get; set; }
                public string? RegisteredBy { get; set; }
                public string? Notes { get; set; }
            }

            public sealed class PricingProfile
            {
                public string? PriceBookSnapshotId { get; set; }
                public string? PriceBookHash { get; set; }
            }

            /// <summary>
            /// Earthworks need an explicit engineer decision: true includes them and
            /// still requires proven Civil coverage; false records that they are out
            /// of scope and requires a reason. Null means unresolved and may never
            /// turn missing sample-line evidence into an implicit zero.
            /// </summary>
            public sealed class EarthworksProfile
            {
                public bool? Requested { get; set; }
                public string? DecidedBy { get; set; }
                public DateTime? DecidedAtUtc { get; set; }
                public string? Reason { get; set; }
            }

            public sealed class AdjustmentRule
            {
                public string? RuleId { get; set; }
                public double? Factor { get; set; }
                public string? Formula { get; set; }
                public string? Reason { get; set; }
                public string? Source { get; set; }
                /// <summary>CONFIRMED | UNCONFIRMED — UNCONFIRMED is never auto-applied.</summary>
                public string Status { get; set; } = "UNCONFIRMED";
                public string? Scope { get; set; }
                public string? ApprovedBy { get; set; }
                public DateTime? ApprovedAtUtc { get; set; }
                public int Order { get; set; }
            }

            public sealed class PriceOverride
            {
                public string? ItemCode { get; set; }
                public decimal? Price { get; set; }
                public string? Source { get; set; }
                public string? Reason { get; set; }
                public string? ApprovedBy { get; set; }
                public DateTime? ApprovedAtUtc { get; set; }
                public string? ApprovedCatalogId { get; set; }
                public string? ApprovedCatalogHash { get; set; }
                public string? ApprovedCatalogItemFingerprint { get; set; }
                public string? ExpectedUnit { get; set; }
            }
        }
    }
}
