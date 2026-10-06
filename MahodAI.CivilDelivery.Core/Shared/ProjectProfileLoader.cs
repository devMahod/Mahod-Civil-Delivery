using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Loads and validates a version-controlled YAML Project Profile (plan §6.2/§6.4).
    /// Validation produces explicit findings for unresolved fields — it never fills
    /// engineering values with invented defaults.
    /// </summary>
    public static class ProjectProfileLoader
    {
        public sealed class LoadResult
        {
            public ProjectProfile? Profile { get; init; }
            public string? ProfileHash { get; init; }
            public List<DeliveryFinding> Findings { get; init; } = new();
            public bool IsUsable => Profile != null &&
                Findings.TrueForAll(f => f.Severity < FindingSeverity.Error);
        }

        public static LoadResult LoadFromFile(string path)
        {
            if (!File.Exists(path))
            {
                return new LoadResult
                {
                    Findings =
                    {
                        new DeliveryFinding
                        {
                            Code = "SHR-PROFILE-MISSING",
                            Domain = "shared",
                            Severity = FindingSeverity.Error,
                            Title = "קובץ פרופיל הפרויקט לא נמצא",
                            Message = path,
                        }
                    }
                };
            }

            var text = File.ReadAllText(path);
            var result = LoadFromText(text);
            return new LoadResult
            {
                Profile = result.Profile,
                ProfileHash = ArtifactHash.Sha256OfText(text),
                Findings = result.Findings,
            };
        }

        public static LoadResult LoadFromText(string yaml)
        {
            ProjectProfile profile;
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                profile = deserializer.Deserialize<ProjectProfile>(yaml)
                          ?? throw new InvalidOperationException("empty profile document");
                // Family-decision times are UTC by contract; give them UTC kind so the loaded
                // profile hashes exactly like the in-memory profile that was saved.
                MahodAI.CivilDelivery.Estimate.Recognition.FamilyDecisionPolicy.NormalizeTimestamps(profile.Estimate);
                MahodAI.CivilDelivery.Estimate.Recognition.EditionLinkPolicy.NormalizeTimestamps(profile.Estimate);
                ProjectProfileSchemaPolicy.NormalizeTimestamps(profile.Estimate);
            }
            catch (Exception ex)
            {
                return new LoadResult
                {
                    Findings =
                    {
                        new DeliveryFinding
                        {
                            Code = "SHR-PROFILE-INVALID",
                            Domain = "shared",
                            Severity = FindingSeverity.Error,
                            Title = "לא ניתן לקרוא את קובץ פרופיל הפרויקט",
                            Message = ex.Message,
                        }
                    }
                };
            }

            var findings = Validate(profile);
            return new LoadResult
            {
                Profile = profile,
                ProfileHash = ArtifactHash.Sha256OfText(yaml),
                Findings = findings,
            };
        }

        /// <summary>Structural validation (plan §6.4). Style existence is checked at runtime against the DWG.</summary>
        public static List<DeliveryFinding> Validate(ProjectProfile profile)
        {
            var findings = new List<DeliveryFinding>();

            void Add(string code, FindingSeverity severity, string title, string message = "") =>
                findings.Add(new DeliveryFinding
                {
                    Code = code,
                    Domain = "shared",
                    Severity = severity,
                    Title = title,
                    Message = message,
                    ProjectProfileId = profile.ProfileId,
                });

            // Schema 2 = the profile carries estimate.family_decisions (the writer bumps it only
            // then). A build that knows only schema 1 refuses such a file with this Error. That is
            // deliberate: IgnoreUnmatchedProperties would otherwise drop the unknown section on
            // load and the next save of any approval would silently erase every family decision.
            // Schema 3 = the profile also carries estimate.edition_links (same reason, same gate).
            // Schema 4 = exact visual bindings. Schema 5 = explicit price-book column mappings and scoped catalog
            // approvals (Codex contract E7F6811B); schema-5 content under an older version, or a future version, is refused.
            if (!ProjectProfileSchemaPolicy.IsSupportedVersion(profile.SchemaVersion))
                Add("SHR-PROFILE-SCHEMA-VERSION", FindingSeverity.Error,
                    $"Unsupported profile schema_version {profile.SchemaVersion}");
            findings.AddRange(ProjectProfileSchemaPolicy.Validate(profile));

            if (string.IsNullOrWhiteSpace(profile.ProfileId))
                Add("SHR-PROFILE-ID-MISSING", FindingSeverity.Error, "profile_id is required");

            findings.AddRange(PhysicalDrawingUnitPolicy.ValidateDeclarations(
                profile.DrawingUnitDeclarations, profile.ProfileId));
            findings.AddRange(PhysicalDrawingUnitPolicy.ValidateReviews(profile.DrawingUnitReviews, profile.ProfileId));
            // ReviewRequired, not Error: the profile stays usable so the units review can repair the gap.
            findings.AddRange(PhysicalDrawingUnitPolicy.ValidateDecisionLinks(profile.DrawingUnitDeclarations,
                profile.DrawingUnitReviews, profile.ProfileId, FindingSeverity.ReviewRequired));

            if (profile.Sections.Cl.SourceFiles.Count == 0)
                Add("SHR-PROFILE-CL-SOURCES-EMPTY", FindingSeverity.ReviewRequired,
                    "sections.cl.source_files is empty",
                    "PLAN cannot select CL geometry without a configured CL source.");

            if (profile.Sections.Cl.IntersectionToleranceM is null)
                Add("SHR-PROFILE-CL-TOLERANCE-NULL", FindingSeverity.Warning,
                    "sections.cl.intersection_tolerance_m is not set",
                    "Exact geometric intersection will be required (no drafting-gap tolerance).");
            else if (profile.Sections.Cl.IntersectionToleranceM <= 0 ||
                     profile.Sections.Cl.IntersectionToleranceM > 5)
                Add("SHR-PROFILE-CL-TOLERANCE-RANGE", FindingSeverity.Error,
                    $"sections.cl.intersection_tolerance_m={profile.Sections.Cl.IntersectionToleranceM} out of sane range (0..5]");

            if (profile.Sections.Cl.Numbering.Mode is null)
                Add("SHR-PROFILE-NUMBERING-UNRESOLVED", FindingSeverity.Warning,
                    "sections.cl.numbering.mode is unresolved (OQ-003)",
                    "Section numbering will be reported from labels when present but not validated against station.");

            foreach (var d in profile.Sections.Decisions.Crossings)
            {
                if (string.IsNullOrWhiteSpace(d.SourceDrawingHash) ||
                    string.IsNullOrWhiteSpace(d.SourceHandle) ||
                    string.IsNullOrWhiteSpace(d.AlignmentName) || d.Station is null ||
                    string.IsNullOrWhiteSpace(d.ApprovedBy) || d.ApprovedAtUtc is null)
                    Add("SHR-SECTION-CROSSING-DECISION-INCOMPLETE", FindingSeverity.Error,
                        $"sections.decisions.crossings entry '{d.SourceHandle}' is incomplete",
                        "A crossing decision requires source drawing hash + handle, exact alignment + station, approver and approval timestamp.");
            }

            foreach (var d in profile.Sections.Decisions.Exclusions)
            {
                if (string.IsNullOrWhiteSpace(d.SourceDrawingHash) ||
                    string.IsNullOrWhiteSpace(d.SourceHandle) ||
                    string.IsNullOrWhiteSpace(d.FindingCode) ||
                    string.IsNullOrWhiteSpace(d.Reason) ||
                    string.IsNullOrWhiteSpace(d.ApprovedBy) || d.ApprovedAtUtc is null)
                    Add("SHR-SECTION-EXCLUSION-INCOMPLETE", FindingSeverity.Error,
                        $"sections.decisions.exclusions entry '{d.SourceHandle}' is incomplete",
                        "An exclusion requires source drawing hash + handle, finding code, reason, approver and approval timestamp.");
            }

            var trafficDirectionDecisions = profile.Sections.Decisions.TrafficDirections;
            if (trafficDirectionDecisions == null)
            {
                Add("SHR-SECTION-TRAFFIC-DIRECTION-LIST-NULL", FindingSeverity.Error,
                    "sections.decisions.traffic_directions must be a list",
                    "Use an empty list when there are no approved lane directions.");
                trafficDirectionDecisions = new List<ProjectProfile.SectionsProfile
                    .DecisionsProfile.TrafficDirectionDecision>();
            }

            foreach (var d in trafficDirectionDecisions)
            {
                if (!SectionVehicleDirectionPlanner.IsValidManualDecision(d))
                    Add("SHR-SECTION-TRAFFIC-DIRECTION-INCOMPLETE", FindingSeverity.Error,
                        $"sections.decisions.traffic_directions entry '{d.SourceHandle}' / lane '{d.LaneMidOffsetM}' is incomplete",
                        "A traffic-direction decision requires the immutable SHA-256 CL source + hexadecimal handle, " +
                        "alignment, finite lane midpoint, along-alignment/against-alignment flow, approver and approval timestamp.");
            }

            var validTrafficDecisions = trafficDirectionDecisions
                .Where(SectionVehicleDirectionPlanner.IsValidManualDecision)
                .ToList();
            for (var i = 0; i < validTrafficDecisions.Count; i++)
            {
                for (var j = i + 1; j < validTrafficDecisions.Count; j++)
                {
                    var a = validTrafficDecisions[i];
                    var b = validTrafficDecisions[j];
                    if (string.Equals(a.SourceDrawingHash, b.SourceDrawingHash,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(a.SourceHandle, b.SourceHandle,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(a.AlignmentName?.Trim(), b.AlignmentName?.Trim(),
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(a.TrackEvidenceDigest, b.TrackEvidenceDigest, StringComparison.Ordinal) &&
                        Math.Abs(a.LaneMidOffsetM!.Value - b.LaneMidOffsetM!.Value) <=
                            SectionVehicleDirectionPlanner.ManualLaneOffsetToleranceM)
                    {
                        Add("SHR-SECTION-TRAFFIC-DIRECTION-DUPLICATE", FindingSeverity.Error,
                            "sections.decisions.traffic_directions contains competing approvals",
                            $"CL '{a.SourceHandle}', alignment '{a.AlignmentName}', lane '{a.LaneMidOffsetM}' has more than one approval; direction remains ambiguous until one is retained.");
                    }
                }
            }

            var spanLabels = profile.Sections.Decisions.SpanLabels;
            if (spanLabels == null)
            {
                Add("SHR-SECTION-SPAN-LABEL-LIST-NULL", FindingSeverity.Error,
                    "sections.decisions.span_labels must be a list",
                    "Use an empty list when no strip-name decisions have been approved.");
                spanLabels = new List<ProjectProfile.SectionsProfile.DecisionsProfile
                    .SpanLabelDecision>();
            }

            bool ValidSpanLabel(ProjectProfile.SectionsProfile.DecisionsProfile.SpanLabelDecision d) =>
                SectionVehicleDirectionPlanner.IsSha256(d.SourceDrawingHash) &&
                !string.IsNullOrWhiteSpace(d.SourceHandle) &&
                !string.IsNullOrWhiteSpace(d.AlignmentName) &&
                d.FromOffsetM is { } from && double.IsFinite(from) &&
                d.ToOffsetM is { } to && double.IsFinite(to) && to - from >= 0.5 &&
                !string.IsNullOrWhiteSpace(d.Label) && d.Label.Trim().Length <= 80 &&
                !string.IsNullOrWhiteSpace(d.ApprovedBy) && d.ApprovedAtUtc is not null &&
                (d.PhysicalDecisionKey == null || SectionVehicleDirectionPlanner.IsSha256(d.PhysicalDecisionKey)) &&
                (d.HostSourceDrawingPath == null ||
                    SectionVehicleDirectionPlanner.IsSha256(d.PhysicalDecisionKey) &&
                    Path.IsPathFullyQualified(d.HostSourceDrawingPath));

            foreach (var d in spanLabels)
            {
                if (!ValidSpanLabel(d))
                    Add("SHR-SECTION-SPAN-LABEL-INCOMPLETE", FindingSeverity.Error,
                        $"sections.decisions.span_labels entry '{d.SourceHandle}' / " +
                        $"'{d.FromOffsetM}'..'{d.ToOffsetM}' is incomplete",
                        "A strip label requires immutable CL SHA+handle, alignment, finite ordered offsets, label, approver and UTC timestamp.");
            }

            foreach (var duplicate in spanLabels.Where(ValidSpanLabel)
                         .GroupBy(d => d.PhysicalDecisionKey != null
                             ? "physical|" + d.PhysicalDecisionKey.ToUpperInvariant()
                             : FormattableString.Invariant(
                                 $"{d.SourceDrawingHash!.ToUpperInvariant()}|{d.SourceHandle!.ToUpperInvariant()}|{d.AlignmentName!.ToUpperInvariant()}|{d.FromOffsetM!.Value:F3}|{d.ToOffsetM!.Value:F3}"),
                             StringComparer.Ordinal)
                         .Where(group => group.Count() > 1))
            {
                Add("SHR-SECTION-SPAN-LABEL-DUPLICATE", FindingSeverity.Error,
                    "sections.decisions.span_labels contains competing approvals",
                    $"Span '{duplicate.Key}' has more than one approved label; retain one decision.");
            }

            var rowAuthorities = profile.Sections.Projection.RowAuthorities;
            if (rowAuthorities == null)
            {
                Add("SHR-SECTION-ROW-AUTHORITY-LIST-NULL", FindingSeverity.Error,
                    "sections.projection.row_authorities must be a list",
                    "Use an empty list to suppress unapproved ROW geometry.");
                rowAuthorities = new List<ProjectProfile.SectionsProfile.ProjectionProfile.RowAuthority>();
            }
            foreach (var authority in rowAuthorities)
            {
                var valid = SectionRowAuthorityLogic.IsCompleteAuthority(
                    new SectionRowAuthorityLogic.Authority(
                        authority.SourceDrawingSha256,
                        authority.SourcePathPattern,
                        authority.XrefPattern,
                        authority.ApprovedBy,
                        authority.ApprovedAtUtc));
                if (!valid)
                    Add("SHR-SECTION-ROW-AUTHORITY-INCOMPLETE", FindingSeverity.Error,
                        "sections.projection.row_authorities contains an incomplete authority",
                        "ROW authority requires the exact source DWG SHA-256, approver and UTC timestamp; path/XREF patterns are optional narrowing evidence.");
            }

            // An explicit YAML null list is a controlled Error, never a NullReferenceException
            // escaping LoadFromText (same contract as traffic_directions/span_labels above).
            var approvedAdjustments = profile.Estimate.ApprovedAdjustments;
            if (approvedAdjustments == null)
            {
                Add("EST-APPROVED-ADJUSTMENTS-LIST-NULL", FindingSeverity.Error,
                    "estimate.approved_adjustments must be a list",
                    "Use an empty list when no adjustment has been approved.");
                approvedAdjustments = new List<ProjectProfile.EstimateProfile.AdjustmentRule>();
            }

            foreach (var adj in approvedAdjustments)
            {
                var confirmed = string.Equals(adj.Status, "CONFIRMED", StringComparison.OrdinalIgnoreCase);
                var hasAuthority = !string.IsNullOrWhiteSpace(adj.ApprovedBy) && adj.ApprovedAtUtc != null;
                if (!confirmed || !hasAuthority)
                    Add("EST-ADJUSTMENT-UNVERIFIED", FindingSeverity.Error,
                        $"approved_adjustments contains unapproved rule '{adj.RuleId}'",
                        "Only CONFIRMED adjustments with approver + timestamp may live under approved_adjustments; " +
                        "candidates belong under candidate_adjustments.");
                if (string.IsNullOrWhiteSpace(adj.RuleId))
                    Add("EST-ADJUSTMENT-ID-MISSING", FindingSeverity.Error,
                        "approved adjustment is missing rule_id");
                if (adj.Factor is null)
                    Add("EST-ADJUSTMENT-EMPTY", FindingSeverity.Error,
                        $"adjustment '{adj.RuleId}' has no numeric factor",
                        "Formula-only adjustments are not executable by the deterministic engine.");
                else if (!double.IsFinite(adj.Factor.Value) || adj.Factor.Value <= 0)
                    Add(EstimateFindingCodes.NonPositiveMoney, FindingSeverity.Error,
                        $"adjustment '{adj.RuleId}' has an invalid factor {adj.Factor}",
                        "An approved factor must be finite and greater than zero.");
                if (!MahodAI.CivilDelivery.Estimate.AdjustmentEngine
                        .HasSupportedExplicitScope(adj.Scope))
                    Add("EST-ADJUSTMENT-SCOPE-UNSUPPORTED", FindingSeverity.Error,
                        $"adjustment '{adj.RuleId}' has no supported explicit scope",
                        "Use record:<record_id>, rule:<rule_key>, or catalog:<catalog_code>. Blank/global scopes are never applied.");
            }

            var projectOverrides = profile.Estimate.ProjectOverrides;
            if (projectOverrides == null)
            {
                Add("EST-PROJECT-OVERRIDES-LIST-NULL", FindingSeverity.Error,
                    "estimate.project_overrides must be a list",
                    "Use an empty list when no project price has been approved.");
                projectOverrides = new List<ProjectProfile.EstimateProfile.PriceOverride>();
            }

            foreach (var o in projectOverrides)
            {
                if (o.Price is null || string.IsNullOrWhiteSpace(o.ItemCode) ||
                    string.IsNullOrWhiteSpace(o.Source) || string.IsNullOrWhiteSpace(o.Reason) ||
                    string.IsNullOrWhiteSpace(o.ApprovedBy) || o.ApprovedAtUtc == null)
                    Add(EstimateFindingCodes.OverrideIncomplete, FindingSeverity.Error,
                        $"project_overrides entry '{o.ItemCode}' must carry price+source+reason+approver+approval timestamp");
                else if (o.Price <= 0)
                    Add(EstimateFindingCodes.NonPositiveMoney, FindingSeverity.Error,
                        $"project_overrides entry '{o.ItemCode}' has non-positive price {o.Price}",
                        "A missing or non-positive price is not an approved project price.");
            }

            var ignoredRuleDecisions = profile.Estimate.IgnoredRuleDecisions;
            if (ignoredRuleDecisions == null)
            {
                Add("EST-IGNORED-RULE-DECISION-LIST-NULL", FindingSeverity.Error,
                    "estimate.ignored_rule_decisions must be a list",
                    "Use an empty list when no measured work has been excluded.");
                ignoredRuleDecisions = new List<ProjectProfile.EstimateProfile.IgnoredRuleDecision>();
            }

            foreach (var decision in ignoredRuleDecisions)
            {
                if (!MahodAI.CivilDelivery.Estimate.IgnoredRulePolicy.IsApproved(decision))
                {
                    Add("EST-IGNORED-RULE-DECISION-INCOMPLETE", FindingSeverity.Error,
                        $"ignored_rule_decisions entry '{decision.RuleKey}' is incomplete",
                        "Removing measured work requires rule key, engineering reason, named approver and UTC approval timestamp.");
                }
            }

            foreach (var duplicate in ignoredRuleDecisions
                         .Where(MahodAI.CivilDelivery.Estimate.IgnoredRulePolicy.IsApproved)
                         .GroupBy(d => d.RuleKey!.Trim(), StringComparer.Ordinal)
                         .Where(g => g.Count() > 1))
            {
                Add("EST-IGNORED-RULE-DECISION-DUPLICATE", FindingSeverity.Error,
                    $"ignored_rule_decisions contains competing approvals for '{duplicate.Key}'",
                    "Keep one audited exclusion decision for each rule key.");
            }

            // Family decisions: structural checks only (a null list included). Whether a
            // decision still holds for the current scan and library is a run-time state
            // (FamilyDecisionPolicy.Resolve) and never a loader error.
            findings.AddRange(MahodAI.CivilDelivery.Estimate.Recognition.FamilyDecisionPolicy
                .Validate(profile.Estimate, profile.ProfileId));
            findings.AddRange(MahodAI.CivilDelivery.Estimate.Recognition.EditionLinkPolicy
                .Validate(profile.Estimate, profile.ProfileId));

            findings.AddRange(MahodAI.CivilDelivery.Estimate.EstimateConfigurationPolicy.Validate(profile));
            return findings;
        }
    }
}
