using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Pure mutation boundary for explicit engineer decisions.  The caller persists
    /// the resulting profile through ProjectProfileWriter; this service makes it
    /// impossible to construct a partial crossing/exclusion/traffic-direction
    /// decision from the UI.
    /// </summary>
    public static class SectionDecisionProfileService
    {
        public sealed record SpanLabelApproval(
            SectionUnresolvedSpanPlan Span,
            string Label);

        public sealed record SpanLabelBatchApproval(
            SectionPlanRecord Record,
            SectionUnresolvedSpanPlan Span,
            string Label);

        public sealed record TrafficDirectionBatchApproval(
            SectionPlanRecord Record,
            SectionTrafficDirectionPlan Direction,
            TrafficDirectionEvidenceLogic.RelativeFlow Flow);

        public static void ApproveCrossing(
            ProjectProfile profile,
            SectionPlanRecord record,
            AlignmentCrossing selected,
            string approvedBy,
            DateTime approvedAtUtc)
        {
            RequireApproverAndTime(approvedBy, approvedAtUtc);
            var exactCandidates = record.CandidateCrossings.Where(c =>
                    string.Equals(c.AlignmentName, selected.AlignmentName,
                        StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(c.Station - selected.Station) <=
                        SectionPlanLogic.CrossingDecisionStationToleranceM)
                .ToList();
            if (exactCandidates.Count != 1)
                throw new InvalidOperationException(
                    "The selected alignment+station is not one unique candidate crossing for this CL record.");

            RemoveForSource(profile.Sections.Decisions.Crossings, record,
                d => (d.SourceDrawingHash, d.SourceHandle));
            RemoveForSource(profile.Sections.Decisions.Exclusions, record,
                d => (d.SourceDrawingHash, d.SourceHandle));

            profile.Sections.Decisions.Crossings.Add(
                new ProjectProfile.SectionsProfile.DecisionsProfile.CrossingDecision
                {
                    SourceDrawingHash = record.Cl.SourceDrawingHash,
                    SourceHandle = record.Cl.SourceHandle,
                    AlignmentName = selected.AlignmentName,
                    Station = selected.Station,
                    ApprovedBy = approvedBy.Trim(),
                    ApprovedAtUtc = ToUtc(approvedAtUtc),
                });
        }

        public static int ApproveExclusions(
            ProjectProfile profile,
            IReadOnlyCollection<SectionPlanRecord> records,
            string findingCode,
            string reason,
            string approvedBy,
            DateTime approvedAtUtc)
        {
            RequireApproverAndTime(approvedBy, approvedAtUtc);
            if (records.Count == 0)
                throw new ArgumentException("At least one CL record is required.", nameof(records));
            if (string.IsNullOrWhiteSpace(findingCode))
                throw new ArgumentException("A finding code is required for an exclusion.", nameof(findingCode));
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("A reason is required for an exclusion.", nameof(reason));
            if (records.Any(r => r.Findings.All(f =>
                    !string.Equals(f.Code, findingCode, StringComparison.Ordinal) ||
                    !SectionPlanLogic.IsExcludableFinding(f))))
                throw new InvalidOperationException(
                    "Every excluded CL record must currently carry the approved engineering-scope finding code.");

            var utc = ToUtc(approvedAtUtc);
            foreach (var record in records)
            {
                RemoveForSource(profile.Sections.Decisions.Crossings, record,
                    d => (d.SourceDrawingHash, d.SourceHandle));
                RemoveForSource(profile.Sections.Decisions.Exclusions, record,
                    d => (d.SourceDrawingHash, d.SourceHandle));
                profile.Sections.Decisions.Exclusions.Add(
                    new ProjectProfile.SectionsProfile.DecisionsProfile.ExclusionDecision
                    {
                        SourceDrawingHash = record.Cl.SourceDrawingHash,
                        SourceHandle = record.Cl.SourceHandle,
                        FindingCode = findingCode.Trim(),
                        Reason = reason.Trim(),
                        ApprovedBy = approvedBy.Trim(),
                        ApprovedAtUtc = utc,
                    });
            }
            return records.Count;
        }

        /// <summary>
        /// Records one lane-specific flow decision.  The lane is keyed by immutable
        /// CL drawing SHA + entity handle and its midpoint offset, while the selected
        /// alignment makes the along/against meaning explicit.  Re-approval replaces
        /// the previous decision for this CL lane instead of leaving competitors.
        /// </summary>
        public static ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision
            ApproveTrafficDirection(
                ProjectProfile profile,
                string sourceDrawingHash,
                string sourceHandle,
                string alignmentName,
                double laneMidOffsetM,
                TrafficDirectionEvidenceLogic.RelativeFlow flow,
                string approvedBy,
                DateTime approvedAtUtc)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            RequireApproverAndTime(approvedBy, approvedAtUtc);
            var decision = BuildTrafficDirectionDecision(
                sourceDrawingHash, sourceHandle, alignmentName, laneMidOffsetM, flow,
                approvedBy, approvedAtUtc);
            ReplaceTrafficDirection(profile, decision);
            return decision;
        }

        /// <summary>
        /// Saves every unresolved direction shown in one review dialog as one atomic
        /// profile mutation. Validation finishes for the whole displayed set before a
        /// previous decision is removed, so a stale/partial/duplicate batch changes
        /// nothing and the caller can safely rerun PLAN once.
        /// </summary>
        public static int ApproveTrafficDirectionsBatch(
            ProjectProfile profile,
            IReadOnlyCollection<SectionPlanRecord> displayedRecords,
            IReadOnlyCollection<TrafficDirectionBatchApproval> approvals,
            string approvedBy,
            DateTime approvedAtUtc) =>
            ApproveTrafficDirectionsBatchCore(profile, displayedRecords, approvals, approvedBy, approvedAtUtc, false);

        public static int ApproveEditedTrafficDirectionsBatch(
            ProjectProfile profile,
            IReadOnlyCollection<SectionPlanRecord> displayedRecords,
            IReadOnlyCollection<TrafficDirectionBatchApproval> approvals,
            string approvedBy,
            DateTime approvedAtUtc) =>
            ApproveTrafficDirectionsBatchCore(profile, displayedRecords, approvals, approvedBy, approvedAtUtc, true);

        private static int ApproveTrafficDirectionsBatchCore(
            ProjectProfile profile,
            IReadOnlyCollection<SectionPlanRecord> displayedRecords,
            IReadOnlyCollection<TrafficDirectionBatchApproval> approvals,
            string approvedBy,
            DateTime approvedAtUtc,
            bool includeResolvedDirections)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (displayedRecords == null) throw new ArgumentNullException(nameof(displayedRecords));
            if (approvals == null) throw new ArgumentNullException(nameof(approvals));
            RequireApproverAndTime(approvedBy, approvedAtUtc);

            var records = displayedRecords.Distinct().ToList();
            if (records.Count == 0)
                throw new ArgumentException("At least one displayed section is required.", nameof(displayedRecords));
            if (records.Any(record => string.IsNullOrWhiteSpace(record.SelectedAlignment)))
                throw new InvalidOperationException(
                    "Every displayed section requires one selected alignment before traffic directions can be approved.");

            var expected = records
                .SelectMany(record => record.TrafficDirections
                    .Where(direction => includeResolvedDirections || !direction.IsResolved)
                    .Select(direction => (Record: record, Direction: direction)))
                .ToList();
            if (expected.Count == 0)
                throw new InvalidOperationException(
                    "The displayed sections have no currently unresolved traffic directions.");
            if (approvals.Count == 0 || (!includeResolvedDirections && approvals.Count != expected.Count))
                throw new InvalidOperationException(
                    "Every currently unresolved traffic direction must receive one explicit decision.");

            var targets = includeResolvedDirections ? expected.Where(current => approvals.Any(approval =>
                ReferenceEquals(approval.Record, current.Record) &&
                SameDirectionExact(approval.Direction, current.Direction))).ToList() : expected;
            if (targets.Count != approvals.Count)
                throw new InvalidOperationException("Every checked direction must match one unique exact current lane.");
            var normalized = new List<ProjectProfile.SectionsProfile.DecisionsProfile
                .TrafficDirectionDecision>();
            foreach (var current in targets)
            {
                if (current.Direction.TrackEvidenceDigest != null &&
                    SectionTrafficPlanContract.Validate(current.Record) is { } trackError)
                    throw new InvalidOperationException("Current source-track evidence is not valid: " + trackError);
                var matches = approvals.Where(approval =>
                        ReferenceEquals(approval.Record, current.Record) &&
                        (includeResolvedDirections ? SameDirectionExact(approval.Direction, current.Direction) :
                            SameDirection(approval.Direction, current.Direction)))
                    .ToList();
                if (matches.Count != 1)
                    throw new InvalidOperationException(
                        $"Traffic lane {current.Record.RecordId}@{current.Direction.LaneMidOffsetM:F3} must have one exact approval.");

                var decision = BuildTrafficDirectionDecision(
                    current.Record.Cl.SourceDrawingHash,
                    current.Record.Cl.SourceHandle,
                    current.Record.SelectedAlignment!,
                    current.Direction.LaneMidOffsetM,
                    matches[0].Flow,
                    approvedBy,
                    approvedAtUtc);
                if (includeResolvedDirections || current.Direction.TrackEvidenceDigest != null)
                {
                    decision.AllowArrowOverride = true;
                    decision.TrackEvidenceDigest = current.Direction.TrackEvidenceDigest;
                    decision.FromOffsetM = current.Direction.FromOffsetM;
                    decision.ToOffsetM = current.Direction.ToOffsetM;
                    decision.EvidenceMode = current.Direction.EvidenceMode;
                    if (!SectionVehicleDirectionPlanner.IsValidManualDecision(decision))
                        throw new InvalidOperationException("The reviewed lane requires exact finite bounds, midpoint and evidence mode.");
                }
                normalized.Add(decision);
            }

            var duplicateKey = normalized.GroupBy(decision =>
                    $"{decision.SourceDrawingHash}|{decision.SourceHandle}|{decision.AlignmentName}|{decision.LaneMidOffsetM:R}|{decision.TrackEvidenceDigest}",
                    StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() != 1);
            if (duplicateKey != null)
                throw new InvalidOperationException(
                    "The displayed traffic-direction batch contains duplicate lane identities.");

            foreach (var decision in normalized)
                ReplaceTrafficDirection(profile, decision);
            return normalized.Count;
        }

        public static int RemoveTrafficDirection(
            ProjectProfile profile,
            string sourceDrawingHash,
            string sourceHandle,
            double laneMidOffsetM)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (!SectionVehicleDirectionPlanner.IsSha256(sourceDrawingHash) ||
                string.IsNullOrWhiteSpace(sourceHandle) ||
                sourceHandle.Any(c => !Uri.IsHexDigit(c)) ||
                double.IsNaN(laneMidOffsetM) || double.IsInfinity(laneMidOffsetM))
                throw new ArgumentException("A valid CL identity and finite lane midpoint are required.");

            return profile.Sections.Decisions.TrafficDirections.RemoveAll(existing =>
                string.Equals(existing.SourceDrawingHash, sourceDrawingHash,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.SourceHandle, sourceHandle,
                    StringComparison.OrdinalIgnoreCase) &&
                existing.TrackEvidenceDigest == null &&
                existing.LaneMidOffsetM is { } offset &&
                Math.Abs(offset - laneMidOffsetM) <=
                    SectionVehicleDirectionPlanner.ManualLaneOffsetToleranceM);
        }

        /// <summary>
        /// Approves every currently unresolved visible strip in one selected section.
        /// Partial batches are refused: a save followed by PLAN must either close the
        /// whole naming gap shown to the engineer or change nothing.
        /// </summary>
        public static int ApproveSpanLabels(
            ProjectProfile profile,
            SectionPlanRecord record,
            IReadOnlyCollection<SpanLabelApproval> approvals,
            string approvedBy,
            DateTime approvedAtUtc)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (approvals == null) throw new ArgumentNullException(nameof(approvals));
            return ApproveSpanLabelsBatch(
                profile,
                new[] { record },
                approvals.Select(approval => new SpanLabelBatchApproval(
                    record, approval.Span, approval.Label)).ToList(),
                approvedBy,
                approvedAtUtc);
        }

        /// <summary>
        /// Approves all unresolved spans displayed across one PLAN in one atomic
        /// operation. No label is suggested or inferred here; every value must come
        /// back from the engineer-reviewed grid.
        /// </summary>
        public static int ApproveSpanLabelsBatch(
            ProjectProfile profile,
            IReadOnlyCollection<SectionPlanRecord> displayedRecords,
            IReadOnlyCollection<SpanLabelBatchApproval> approvals,
            string approvedBy,
            DateTime approvedAtUtc) =>
            ApproveSpanLabelsBatchCore(
                profile, displayedRecords, approvals, approvedBy, approvedAtUtc,
                requireEveryDisplayedSpan: true);

        /// <summary>
        /// Atomically approves only the checked unresolved spans from a displayed
        /// section.  Every checked row is still matched to the fresh PLAN by exact
        /// CL identity and measured boundaries; unchecked rows remain unresolved and
        /// visible on the next PLAN instead of making the whole 138-row batch unusable.
        /// </summary>
        public static int ApproveSelectedSpanLabelsBatch(
            ProjectProfile profile,
            IReadOnlyCollection<SectionPlanRecord> displayedRecords,
            IReadOnlyCollection<SpanLabelBatchApproval> approvals,
            string approvedBy,
            DateTime approvedAtUtc) =>
            ApproveSpanLabelsBatchCore(
                profile, displayedRecords, approvals, approvedBy, approvedAtUtc,
                requireEveryDisplayedSpan: false);

        /// <summary>
        /// Explicit edits to checked current measured spans, including names already
        /// resolved in PLAN. Old boundary decisions are history, never target authority.
        /// The caller still owns fresh-PLAN/source checks and profile-writer CAS.
        /// </summary>
        public static int ApproveEditedSpanLabelsBatch(
            ProjectProfile profile,
            IReadOnlyCollection<SectionPlanRecord> displayedRecords,
            IReadOnlyCollection<SpanLabelBatchApproval> approvals,
            string approvedBy,
            DateTime approvedAtUtc) =>
            ApproveSpanLabelsBatchCore(
                profile, displayedRecords, approvals, approvedBy, approvedAtUtc,
                requireEveryDisplayedSpan: false, includeResolvedSpans: true);

        private static int ApproveSpanLabelsBatchCore(
            ProjectProfile profile,
            IReadOnlyCollection<SectionPlanRecord> displayedRecords,
            IReadOnlyCollection<SpanLabelBatchApproval> approvals,
            string approvedBy,
            DateTime approvedAtUtc,
            bool requireEveryDisplayedSpan,
            bool includeResolvedSpans = false)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (displayedRecords == null) throw new ArgumentNullException(nameof(displayedRecords));
            if (approvals == null) throw new ArgumentNullException(nameof(approvals));
            RequireApproverAndTime(approvedBy, approvedAtUtc);

            const double toleranceM = 0.01;
            var records = displayedRecords.Distinct().ToList();
            if (records.Count == 0)
                throw new ArgumentException("At least one displayed section is required.", nameof(displayedRecords));
            foreach (var record in records)
            {
                if (string.IsNullOrWhiteSpace(record.SelectedAlignment))
                    throw new InvalidOperationException(
                        "Every displayed section requires one selected alignment before strip labels can be approved.");
                var rowState = record.PresentationCoverage.RowAuthorityState;
                if (!string.Equals(rowState, "authoritative", StringComparison.Ordinal) &&
                    !string.Equals(rowState, "nocandidates", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "ROW authority must be resolved before strip labels can be approved.");
            }

            var expected = records
                .SelectMany(record => SectionSpanEditTargets.ForRecord(record, includeResolvedSpans)
                    .OrderBy(span => span.FromOffsetM)
                    .ThenBy(span => span.ToOffsetM)
                    .Select(span => (Record: record, Span: span)))
                .ToList();
            if (expected.Count == 0)
                throw new InvalidOperationException(
                    "The displayed sections have no current width spans available for this review.");
            if (approvals.Count == 0)
                throw new InvalidOperationException(
                    "At least one current width span must be checked for approval.");
            if (requireEveryDisplayedSpan && approvals.Count != expected.Count)
                throw new InvalidOperationException(
                    "Every currently unresolved width span must receive one explicit label.");

            var normalized = new List<(SectionPlanRecord Record, SectionUnresolvedSpanPlan Span, string Label, string PhysicalKey)>();
            foreach (var approval in approvals)
            {
                var matches = expected.Where(current =>
                        ReferenceEquals(approval.Record, current.Record) &&
                        approval.Span != null &&
                        (includeResolvedSpans
                            ? approval.Span.FromOffsetM == current.Span.FromOffsetM &&
                              approval.Span.ToOffsetM == current.Span.ToOffsetM
                            : Math.Abs(approval.Span.FromOffsetM - current.Span.FromOffsetM) <= toleranceM &&
                              Math.Abs(approval.Span.ToOffsetM - current.Span.ToOffsetM) <= toleranceM))
                    .ToList();
                if (matches.Count != 1)
                    throw new InvalidOperationException(
                        "Every checked span must match one exact current target from the displayed fresh PLAN.");
                var current = matches[0];
                if (includeResolvedSpans &&
                    (!double.IsFinite(current.Span.FromOffsetM) || !double.IsFinite(current.Span.ToOffsetM) ||
                     !double.IsFinite(current.Span.WidthM) || current.Span.WidthM <= 0 ||
                     current.Span.ToOffsetM <= current.Span.FromOffsetM))
                    throw new InvalidOperationException("The current measured span must have finite, ordered boundaries and a positive width.");

                var label = approval.Label?.Trim() ?? string.Empty;
                if (label.Length == 0 || label.Length > 80)
                    throw new ArgumentException(
                        "Each strip label must contain 1-80 visible characters.", nameof(approvals));

                var vehicle = SectionFurnitureLogic.VehicleForStrip(label);
                if (vehicle != null &&
                    (!SectionFurnitureLogic.FitsStrip(vehicle, current.Span.WidthM) || current.Span.WidthM > SectionFurnitureLogic.MaxSingleVehicleStripWidthM))
                    throw new InvalidOperationException(
                        $"Vehicle strip '{label}' is not credible for measured width {current.Span.WidthM:F2} m; add source boundaries instead of collapsing multiple lanes.");
                var physicalKey = SectionSpanPhysicalIdentity.TryCreate(current.Record,
                    current.Span.FromOffsetM, current.Span.ToOffsetM) ?? throw new InvalidOperationException(
                    "The current span has no finite, source-backed pre-manual physical identity; rerun PLAN before approving it.");
                normalized.Add((current.Record, current.Span, label, physicalKey));
            }

            if (requireEveryDisplayedSpan && normalized.Count != expected.Count)
                throw new InvalidOperationException(
                    "Every currently unresolved width span must receive one explicit label.");

            var duplicateKey = normalized.GroupBy(item =>
                    item.PhysicalKey,
                    StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() != 1);
            if (duplicateKey != null)
                throw new InvalidOperationException(
                    "The displayed span batch contains duplicate source identities.");

            var utc = ToUtc(approvedAtUtc);
            foreach (var item in normalized)
            {
                profile.Sections.Decisions.SpanLabels.RemoveAll(existing =>
                    string.Equals(existing.PhysicalDecisionKey, item.PhysicalKey, StringComparison.Ordinal) &&
                    SectionSpanPhysicalIdentity.SameSourceScope(item.Record, existing) &&
                    string.Equals(existing.SourceHandle, item.Record.Cl.SourceHandle,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.AlignmentName, item.Record.SelectedAlignment,
                        StringComparison.OrdinalIgnoreCase) &&
                    existing.FromOffsetM is { } from && existing.ToOffsetM is { } to &&
                    (includeResolvedSpans
                        ? from == item.Span.FromOffsetM && to == item.Span.ToOffsetM
                        : Math.Abs(from - item.Span.FromOffsetM) <= toleranceM &&
                          Math.Abs(to - item.Span.ToOffsetM) <= toleranceM));
                profile.Sections.Decisions.SpanLabels.Add(
                    new ProjectProfile.SectionsProfile.DecisionsProfile.SpanLabelDecision
                    {
                        PhysicalDecisionKey = item.PhysicalKey,
                        HostSourceDrawingPath = SectionSpanPhysicalIdentity.HostSourceDrawingPath(item.Record),
                        AllowSourceLabelOverride = includeResolvedSpans,
                        SourceDrawingHash = item.Record.Cl.SourceDrawingHash,
                        SourceHandle = item.Record.Cl.SourceHandle,
                        AlignmentName = item.Record.SelectedAlignment,
                        FromOffsetM = item.Span.FromOffsetM,
                        ToOffsetM = item.Span.ToOffsetM,
                        Label = item.Label,
                        ApprovedBy = approvedBy.Trim(),
                        ApprovedAtUtc = utc,
                    });
            }
            return normalized.Count;
        }

        /// <summary>
        /// Approves one exact ROW source shown in PLAN.  Competing approvals for the
        /// candidate source set are removed, while authorities for unrelated project
        /// drawings remain intact.
        /// </summary>
        public static ProjectProfile.SectionsProfile.ProjectionProfile.RowAuthority
            ApproveRowAuthority(
                ProjectProfile profile,
                SectionPlanRecord record,
                string selectedSourceKey,
                string approvedBy,
                DateTime approvedAtUtc)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (record == null) throw new ArgumentNullException(nameof(record));
            RequireApproverAndTime(approvedBy, approvedAtUtc);
            var exact = record.PresentationCoverage.RowCandidateSourceKeys
                .Where(key => string.Equals(key, selectedSourceKey, StringComparison.Ordinal))
                .ToList();
            if (exact.Count != 1 || !TryParseRowSourceKey(selectedSourceKey,
                    out var hash, out var path, out var xref) ||
                hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidOperationException(
                    "The selected ROW source is not one exact hash-backed candidate from the current PLAN.");

            var candidateHashes = record.PresentationCoverage.RowCandidateSourceKeys
                .Select(key => TryParseRowSourceKey(key, out var candidateHash, out _, out _)
                    ? candidateHash : string.Empty)
                .Where(candidateHash => candidateHash.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            profile.Sections.Projection.RowAuthorities.RemoveAll(authority =>
                authority.SourceDrawingSha256 != null &&
                candidateHashes.Contains(authority.SourceDrawingSha256));

            var result = new ProjectProfile.SectionsProfile.ProjectionProfile.RowAuthority
            {
                SourceDrawingSha256 = hash,
                SourcePathPattern = path.Length == 0 ? null : path,
                XrefPattern = xref.Length == 0 ? null : xref,
                ApprovedBy = approvedBy.Trim(),
                ApprovedAtUtc = ToUtc(approvedAtUtc),
            };
            profile.Sections.Projection.RowAuthorities.Add(result);
            return result;
        }

        internal static bool TryParseRowSourceKey(
            string? sourceKey,
            out string hash,
            out string path,
            out string xref)
        {
            hash = path = xref = string.Empty;
            if (string.IsNullOrWhiteSpace(sourceKey)) return false;
            var parts = sourceKey.Split('|');
            if (parts.Length != 3) return false;
            hash = parts[0].Trim();
            path = parts[1].Trim();
            xref = parts[2].Trim();
            return true;
        }

        private static ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision
            BuildTrafficDirectionDecision(
                string sourceDrawingHash,
                string sourceHandle,
                string alignmentName,
                double laneMidOffsetM,
                TrafficDirectionEvidenceLogic.RelativeFlow flow,
                string approvedBy,
                DateTime approvedAtUtc)
        {
            var decision = new ProjectProfile.SectionsProfile.DecisionsProfile
                .TrafficDirectionDecision
            {
                SourceDrawingHash = sourceDrawingHash?.Trim(),
                SourceHandle = sourceHandle?.Trim().ToUpperInvariant(),
                AlignmentName = alignmentName?.Trim(),
                LaneMidOffsetM = laneMidOffsetM,
                Flow = SectionVehicleDirectionPlanner.FlowToken(flow),
                ApprovedBy = approvedBy.Trim(),
                ApprovedAtUtc = ToUtc(approvedAtUtc),
            };
            if (!SectionVehicleDirectionPlanner.IsValidManualDecision(decision))
                throw new ArgumentException(
                    "Traffic direction requires SHA-256 CL identity, hexadecimal handle, alignment, finite lane midpoint, resolved flow, approver and UTC time.");
            return decision;
        }

        private static void ReplaceTrafficDirection(
            ProjectProfile profile,
            ProjectProfile.SectionsProfile.DecisionsProfile.TrafficDirectionDecision decision)
        {
            profile.Sections.Decisions.TrafficDirections.RemoveAll(existing =>
                string.Equals(existing.SourceDrawingHash, decision.SourceDrawingHash,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.SourceHandle, decision.SourceHandle,
                    StringComparison.OrdinalIgnoreCase) &&
                existing.LaneMidOffsetM is { } offset &&
                string.Equals(existing.TrackEvidenceDigest, decision.TrackEvidenceDigest, StringComparison.Ordinal) &&
                (decision.AllowArrowOverride
                    ? offset == decision.LaneMidOffsetM &&
                      string.Equals(existing.AlignmentName, decision.AlignmentName, StringComparison.OrdinalIgnoreCase) &&
                      (!existing.AllowArrowOverride ||
                       (existing.FromOffsetM == decision.FromOffsetM && existing.ToOffsetM == decision.ToOffsetM &&
                        existing.EvidenceMode == decision.EvidenceMode))
                    : Math.Abs(offset - decision.LaneMidOffsetM!.Value) <=
                      SectionVehicleDirectionPlanner.ManualLaneOffsetToleranceM));
            profile.Sections.Decisions.TrafficDirections.Add(decision);
        }

        private static bool SameDirectionExact(SectionTrafficDirectionPlan left, SectionTrafficDirectionPlan right) =>
            left != null && right != null && left.FromOffsetM == right.FromOffsetM &&
            left.ToOffsetM == right.ToOffsetM && left.LaneMidOffsetM == right.LaneMidOffsetM &&
            left.TrackEvidenceDigest == right.TrackEvidenceDigest &&
            left.StripLabel == right.StripLabel && left.StripKind == right.StripKind && left.EvidenceMode == right.EvidenceMode;

        private static bool SameDirection(
            SectionTrafficDirectionPlan left,
            SectionTrafficDirectionPlan right) =>
            Math.Abs(left.FromOffsetM - right.FromOffsetM) <= 0.01 &&
            Math.Abs(left.ToOffsetM - right.ToOffsetM) <= 0.01 &&
            Math.Abs(left.LaneMidOffsetM - right.LaneMidOffsetM) <=
                SectionVehicleDirectionPlanner.ManualLaneOffsetToleranceM &&
            left.TrackEvidenceDigest == right.TrackEvidenceDigest &&
            string.Equals(left.StripLabel, right.StripLabel, StringComparison.Ordinal) &&
            string.Equals(left.StripKind, right.StripKind, StringComparison.Ordinal);

        private static void RequireApproverAndTime(string approvedBy, DateTime approvedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(approvedBy))
                throw new ArgumentException("An approver is required.", nameof(approvedBy));
            if (approvedAtUtc == default)
                throw new ArgumentException("An approval timestamp is required.", nameof(approvedAtUtc));
        }

        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        private static void RemoveForSource<T>(
            List<T> values,
            SectionPlanRecord record,
            Func<T, (string? Hash, string? Handle)> key)
        {
            values.RemoveAll(value =>
            {
                var (hash, handle) = key(value);
                return string.Equals(hash, record.Cl.SourceDrawingHash,
                           StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(handle, record.Cl.SourceHandle,
                           StringComparison.OrdinalIgnoreCase);
            });
        }
    }
}
