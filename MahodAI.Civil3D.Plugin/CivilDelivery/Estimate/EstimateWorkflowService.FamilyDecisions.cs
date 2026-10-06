using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

public sealed partial class EstimateWorkflowService
{
    /// <summary>
    /// One engineer family decision in a batch. The groups are checked against the original scan: every record
    /// must be a record of that scan, and the group's facts (source, layer, kind, method, unit, block) must be the
    /// facts of its records, so the persisted selector is derived from measured data, never from dialog fields.
    /// <see cref="EvidenceMatch"/> (optional) records a layer-independent selector instead of a literal one.
    /// <see cref="SupersedesDecisionId"/> (optional) marks one active decision as superseded by this one.
    /// <see cref="Partition"/> (optional, see <see cref="ForPartition"/>) approves one locally proven semantic partition of a
    /// measured group instead of whole groups; such a request carries no groups, evidence keys or evidence match.
    /// </summary>
    public sealed record FamilyDecisionRequest(
        string FamilyId,
        IReadOnlyList<RecognitionGroupInput> Groups,
        IReadOnlyList<string> EvidenceKeys,
        string Reason,
        string? SupersedesDecisionId = null,
        IReadOnlyList<ProjectProfile.EstimateProfile.FamilyEvidenceMatch>? EvidenceMatch = null,
        string Decision = FamilyDecisionPolicy.ApproveFamily,
        FamilyPartitionRequest? Partition = null)
    {
        public FamilyVisualBinding? VisualBinding { get; init; }
        /// <summary>
        /// A partition approval: the id of the whole measured group (EngineerBoqDraftBuilder.RecognitionGroups over the complete
        /// scan) and the exact record ids of one partition the local classifier proved in it. Nothing else is taken from the
        /// caller: the whole group, its records and the cited evidence are rebuilt from the scan when saving.
        /// </summary>
        public static FamilyDecisionRequest ForPartition(string familyId, string wholeGroupId, IReadOnlyList<string> recordIds, string reason) =>
            new(familyId, Array.Empty<RecognitionGroupInput>(), Array.Empty<string>(), reason,
                Partition: new FamilyPartitionRequest(wholeGroupId, recordIds));
    }

    /// <summary>The whole measured group of the scan and the exact records of the one proven partition approved in it.</summary>
    public sealed record FamilyPartitionRequest(string WholeGroupId, IReadOnlyList<string> RecordIds);

    /// <summary>
    /// Saves a batch of family approvals in one atomic profile write (same boundary as
    /// SaveIgnoredRuleDecisions / SaveReviewedMappings): the original scan's CAS evidence, same profile and
    /// effective hash, the whole batch validated before any mutation, one shared UTC time, history kept by
    /// superseding, and the family list restored if validation, serialization or the write fails.
    ///
    /// A family approval never approves catalog items and never prices anything by itself. v1 refuses
    /// <c>exclude</c> here: removing measured work from a priced document stays an audited
    /// IgnoredRuleDecision (one money-exclusion authority).
    ///
    /// A partition request is resolved against the whole measured groups rebuilt from this scan, never against a group
    /// the caller trimmed: its records must be exactly one complete partition the local classifier proves in that group
    /// (FamilyDecisionPolicy.CreatePartitionApproval), and before the write the new rule must govern exactly those records
    /// among all groups of the same source and layer.
    /// </summary>
    public ProjectProfileWriter.SaveResult SaveFamilyDecisions(
        ProjectProfile profile,
        EngineerBoqLibrary library,
        ScanResult scan,
        IReadOnlyList<FamilyDecisionRequest> requests,
        string approvedBy,
        string targetPath,
        ProjectProfileWriter.ExpectedProfileState expectedProfileState)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(expectedProfileState);
        var path = RequireProfileWriteTarget(targetPath);
        // The window's library is the one its draft was built with; a profile that now declares another discipline is
        // refused here, before anything is written (a discipline change needs a rescan and a fresh review).
        if (!ReferenceEquals(EngineerBoqLibrary.For(profile), library))
            throw new InvalidOperationException("ספריית השיוך של הפרופיל אינה הספרייה שבה נבנה חלון הזיהוי (תחום הפרויקט השתנה) — לא נשמר דבר. יש לסרוק מחדש.");
        if (requests.Count == 0)
            throw new ArgumentException("No family decisions to save.", nameof(requests));
        var approver = approvedBy?.Trim();
        if (string.IsNullOrWhiteSpace(approver) || approver.Any(char.IsControl))
            throw new ArgumentException("A named approver on one line is required for family decisions.", nameof(approvedBy));
        RequireFamilyDecisionScope(profile, scan, expectedProfileState);

        var decisions = profile.Estimate.FamilyDecisions
            ?? throw new InvalidOperationException("estimate.family_decisions is not a list; reload the project profile.");

        // Index the scan once; never rescan all measured rows per request.
        var scanRecords = new Dictionary<string, NeutralQuantityRecord>(StringComparer.Ordinal);
        var ambiguousIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in scan.Records)
            if (!scanRecords.TryAdd(record.RecordId, record)) ambiguousIds.Add(record.RecordId);

        // Validate the complete batch before mutating even one profile decision.
        var approvedAtUtc = DateTime.UtcNow;
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        var recordIds = new HashSet<string>(StringComparer.Ordinal);
        var superseded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IReadOnlyList<NeutralQuantityRecord>>? wholeGroupOf = null;
        // The whole measured groups of the complete scan, built once; partition requests are resolved against these only.
        IReadOnlyList<RecognitionGroupInput>? wholeGroups = null;
        var created = new List<(FamilyDecisionRequest Request, ProjectProfile.EstimateProfile.FamilyDecision Decision,
            IReadOnlyList<RecognitionGroupInput> Groups, IReadOnlyList<string>? PartitionIds)>();

        void RequireSupersedable(FamilyDecisionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.SupersedesDecisionId)) return;
            var supersedes = request.SupersedesDecisionId.Trim();
            if (!superseded.Add(supersedes))
                throw new InvalidOperationException($"Decision '{supersedes}' is superseded twice in one batch.");
            var matches = decisions.Count(d => d != null &&
                string.Equals(d.DecisionId?.Trim(), supersedes, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.Status?.Trim(), FamilyDecisionPolicy.Active, StringComparison.Ordinal));
            if (matches != 1)
                throw new InvalidOperationException($"Decision '{supersedes}' is not one active family decision of this profile.");
        }

        foreach (var request in requests)
        {
            if (request == null)
                throw new InvalidOperationException("Every family decision request must name a family, groups and a reason.");
            if (!string.Equals(request.Decision, FamilyDecisionPolicy.ApproveFamily, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "This version saves family approvals only. To keep measured work out of the priced document, " +
                    "record an audited 'not a construction quantity' decision for its rule key instead.");
            if (string.IsNullOrWhiteSpace(request.Reason))
                throw new InvalidOperationException($"Family decision '{request.FamilyId}' requires an engineering reason.");
            if (request.Partition != null)
            {
                if (request.VisualBinding != null)
                    throw new InvalidOperationException("Visual approval covers one reviewed whole group, not a semantic partition.");
                wholeGroups ??= EngineerBoqDraftBuilder.RecognitionGroups(scan.Records, library);
                var (whole, partitionIds) = ScanPartition(request, wholeGroups, scanRecords, ambiguousIds, recordIds);
                RequireSupersedable(request);
                // Throws unless the records are exactly one complete partition the local classifier proves in the whole group,
                // less only records the profile's active decisions already apply as this same family.
                var partitionDecision = FamilyDecisionPolicy.CreatePartitionApproval(request.FamilyId, whole, partitionIds, library,
                    approver, request.Reason, approvedAtUtc, CoveredAsFamily(decisions, whole, request.FamilyId, library));
                created.Add((request, partitionDecision, new[] { whole }, partitionIds));
                continue;
            }
            if (request.Groups == null || request.Groups.Count == 0)
                throw new InvalidOperationException($"Family decision '{request.FamilyId}' covers no measured group.");

            var groups = new List<RecognitionGroupInput>(request.Groups.Count);
            foreach (var group in request.Groups)
            {
                if (group == null || string.IsNullOrWhiteSpace(group.GroupId) || group.Records == null || group.Records.Count == 0)
                    throw new InvalidOperationException("Every approved group needs a stable group id and its measured records.");
                if (!groupIds.Add(group.GroupId))
                    throw new InvalidOperationException(
                        $"Group '{group.GroupId}' appears more than once in the batch; one group receives one family decision.");
                var records = new List<NeutralQuantityRecord>(group.Records.Count);
                foreach (var candidate in group.Records)
                {
                    var id = candidate?.RecordId;
                    if (string.IsNullOrWhiteSpace(id) || ambiguousIds.Contains(id) ||
                        !scanRecords.TryGetValue(id, out var record))
                        throw new InvalidOperationException(
                            $"Group '{group.GroupId}' contains a record that is not one exact record of the current scan.");
                    if (!recordIds.Add(id))
                        throw new InvalidOperationException($"Record '{id}' belongs to more than one approved group.");
                    if (!string.Equals(record.ProjectProfileId, profile.ProfileId, StringComparison.Ordinal))
                        throw new InvalidOperationException($"Group '{group.GroupId}' contains another project's records.");
                    if (!QuantitySignificance.IsValidMeasurement(record.Measurement.RawValue))
                        throw new InvalidOperationException(
                            $"Group '{group.GroupId}' contains an invalid raw measurement and cannot be approved as a family.");
                    // The scan's own record is the evidence, not the caller's copy.
                    records.Add(record);
                }
                var scanned = group with { Records = records };
                var problem = FamilyDecisionPolicy.GroupFactsProblem(scanned);
                if (problem != null)
                    throw new InvalidOperationException($"Group '{group.GroupId}' does not match its measured records: {problem}.");
                groups.Add(scanned);
            }

            // The saved selector is literal, so at draft time it covers the whole measured group its records belong to:
            // approving part of a group would silently approve the rest. Matched by record membership, not by id.
            if (wholeGroupOf == null)
            {
                wholeGroupOf = new Dictionary<string, IReadOnlyList<NeutralQuantityRecord>>(StringComparer.Ordinal);
                foreach (var whole in wholeGroups ??= EngineerBoqDraftBuilder.RecognitionGroups(scan.Records, library))
                    foreach (var record in whole.Records) wholeGroupOf.TryAdd(record.RecordId, whole.Records);
            }
            foreach (var group in groups)
            {
                var ids = group.Records.Select(r => r.RecordId).OrderBy(id => id, StringComparer.Ordinal).ToList();
                if (!wholeGroupOf.TryGetValue(ids[0], out var whole) ||
                    !whole.Select(r => r.RecordId).OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(ids, StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        $"Group '{group.GroupId}' is not one whole measured group of the scan; a family decision covers whole groups only.");
            }

            RequireSupersedable(request);

            var decision = request.EvidenceMatch is { Count: > 0 } evidenceMatch
                ? FamilyDecisionPolicy.CreateEvidenceApproval(request.FamilyId, groups, evidenceMatch,
                    request.EvidenceKeys ?? Array.Empty<string>(), library, approver, request.Reason, approvedAtUtc)
                : FamilyDecisionPolicy.CreateApproval(request.FamilyId, groups,
                    request.EvidenceKeys ?? Array.Empty<string>(), library, approver, request.Reason, approvedAtUtc);
            if (request.VisualBinding != null)
            {
                if (groups.Count != 1 || request.EvidenceMatch is { Count: > 0 })
                    throw new InvalidOperationException("Visual approval must preserve its one exact reviewed group.");
                FamilyDecisionPolicy.AttachVisualBinding(decision, groups[0], request.VisualBinding);
            }
            created.Add((request, decision, groups, null));
        }

        if (created.GroupBy(c => c.Decision.DecisionId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidOperationException("The batch contains the same family decision twice.");
        if (created.Any(c => decisions.Any(d => d != null &&
                string.Equals(d.DecisionId?.Trim(), c.Decision.DecisionId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.Status?.Trim(), FamilyDecisionPolicy.Active, StringComparison.Ordinal))))
            throw new InvalidOperationException("One of the family decisions is already recorded as active.");

        IReadOnlyList<ProjectProfile.EstimateProfile.FamilyDecision> updated = decisions.Select(d => FamilyDecisionPolicy.Clone(d)!).ToList();
        foreach (var item in created)
        {
            if (!string.IsNullOrWhiteSpace(item.Request.SupersedesDecisionId))
                updated = FamilyDecisionPolicy.Supersede(updated, item.Request.SupersedesDecisionId.Trim(), item.Decision);
            else
                updated = updated.Append(FamilyDecisionPolicy.Clone(item.Decision)!).ToList();
        }

        // The batch must apply exactly as approved: a conflict or an unexpected stale outcome is refused
        // here, before the write, instead of being discovered on the next draft.
        foreach (var item in created)
        {
            if (item.PartitionIds != null)
            {
                RequirePartitionAppliesAsApproved(updated, item.Decision, item.Groups[0], item.PartitionIds,
                    wholeGroups ?? throw new InvalidOperationException("The scan's measured groups were not built."), library);
                continue;
            }
            foreach (var resolution in FamilyDecisionPolicy.Resolve(updated, item.Groups, library))
            {
                if (resolution.State != FamilyDecisionState.Applied ||
                    !string.Equals(resolution.FamilyId, item.Decision.FamilyId, StringComparison.Ordinal) ||
                    !string.Equals(resolution.DecisionId, item.Decision.DecisionId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Group '{resolution.GroupId}' would not apply as approved ({resolution.StaleReason ?? resolution.State.ToString()}); no family decision was saved.");
            }
        }

        var oldDecisions = decisions.ToList();
        try
        {
            decisions.Clear();
            decisions.AddRange(updated);
            var errors = FamilyDecisionPolicy.Validate(profile.Estimate, profile.ProfileId);
            if (errors.Count != 0)
                throw new InvalidOperationException($"{errors[0].Code}: {errors[0].Title}. {errors[0].Message}");

            var ids = created.Select(c => c.Decision.DecisionId!).OrderBy(id => id, StringComparer.Ordinal).ToList();
            var digest = ArtifactHash.Sha256OfText(string.Join("\n", ids));
            var families = created.Select(c => c.Decision.FamilyId ?? FamilyDecisionPolicy.Exclude)
                .Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal);
            var summary = $"estimate family decisions approved: count={ids.Count}; ids_sha256={digest}; " +
                          $"superseded={superseded.Count}; partitions={created.Count(c => c.PartitionIds != null)}; " +
                          $"families={OneLineForAudit(string.Join(", ", families))}";
            return ProjectProfileWriter.Save(
                profile, path, summary, approver, expectedProfileState,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["library:" + library.Id] = LibraryIdentity.LibraryHash(library),
                });
        }
        catch
        {
            decisions.Clear();
            decisions.AddRange(oldDecisions);
            throw;
        }
    }

    /// <summary>
    /// Revokes one active family decision with an engineering reason. The entry stays in the profile
    /// (status revoked) as history. A scan is optional: when supplied, its profile CAS evidence must still hold.
    /// </summary>
    public ProjectProfileWriter.SaveResult RevokeFamilyDecision(
        ProjectProfile profile,
        ScanResult? scan,
        string decisionId,
        string reason,
        string approvedBy,
        string targetPath,
        ProjectProfileWriter.ExpectedProfileState expectedProfileState)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(expectedProfileState);
        var path = RequireProfileWriteTarget(targetPath);
        var approver = approvedBy?.Trim();
        if (string.IsNullOrWhiteSpace(approver) || approver.Any(char.IsControl))
            throw new ArgumentException("A named approver on one line is required to revoke a family decision.", nameof(approvedBy));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("An engineering reason is required to revoke a family decision.", nameof(reason));
        if (string.IsNullOrWhiteSpace(decisionId))
            throw new ArgumentException("The family decision id is required.", nameof(decisionId));
        if (scan != null)
            RequireFamilyDecisionScope(profile, scan, expectedProfileState);
        else
            ProjectProfileWriter.RequireExpectedStateUnchanged(profile, expectedProfileState);

        var decisions = profile.Estimate.FamilyDecisions
            ?? throw new InvalidOperationException("estimate.family_decisions is not a list; reload the project profile.");
        var id = decisionId.Trim();
        var updated = FamilyDecisionPolicy.Revoke(decisions, id, reason);

        var oldDecisions = decisions.ToList();
        try
        {
            decisions.Clear();
            decisions.AddRange(updated);
            var summary = $"estimate family decision revoked: {id}; reason={OneLineForAudit(reason.Trim())}";
            return ProjectProfileWriter.Save(profile, path, summary, approver, expectedProfileState);
        }
        catch
        {
            decisions.Clear();
            decisions.AddRange(oldDecisions);
            throw;
        }
    }

    /// <summary>
    /// The whole measured group a partition request names, rebuilt from the scan, and the request's record ids after the same
    /// record checks as a whole-group request (exact scan records, never in two requests of the batch). Whether the ids are one
    /// complete proven partition is decided by FamilyDecisionPolicy.CreatePartitionApproval, not here.
    /// </summary>
    private static (RecognitionGroupInput Whole, IReadOnlyList<string> RecordIds) ScanPartition(FamilyDecisionRequest request,
        IReadOnlyList<RecognitionGroupInput> wholeGroups, IReadOnlyDictionary<string, NeutralQuantityRecord> scanRecords,
        IReadOnlySet<string> ambiguousIds, ISet<string> batchRecordIds)
    {
        var partition = request.Partition!;
        // The group, its records and the cited evidence come from the scan: a request cannot carry its own.
        if ((request.Groups?.Count ?? 0) > 0 || (request.EvidenceKeys?.Count ?? 0) > 0 || (request.EvidenceMatch?.Count ?? 0) > 0)
            throw new InvalidOperationException(
                $"Family decision '{request.FamilyId}' names a partition and also groups or evidence; a partition approval takes only " +
                "the whole group id and the partition's record ids, and derives everything else from the scan.");
        if (string.IsNullOrWhiteSpace(partition.WholeGroupId) || partition.RecordIds == null || partition.RecordIds.Count == 0)
            throw new InvalidOperationException(
                $"Family decision '{request.FamilyId}' names no measured group or no records of the partition it approves.");
        var matches = wholeGroups.Where(g => string.Equals(g.GroupId, partition.WholeGroupId, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException($"Group '{partition.WholeGroupId}' is not one measured group of the current scan.");
        var whole = matches[0];
        var members = whole.Records.Select(r => r.RecordId).ToHashSet(StringComparer.Ordinal);
        var ids = new List<string>(partition.RecordIds.Count);
        foreach (var id in partition.RecordIds)
        {
            if (string.IsNullOrWhiteSpace(id) || ambiguousIds.Contains(id) || !scanRecords.ContainsKey(id) || !members.Contains(id))
                throw new InvalidOperationException(
                    $"Group '{whole.GroupId}' contains a record that is not one exact record of the current scan.");
            if (!batchRecordIds.Add(id))
                throw new InvalidOperationException($"Record '{id}' belongs to more than one approved group.");
            ids.Add(id);
        }
        return (whole, ids);
    }

    /// <summary>
    /// The records of <paramref name="whole"/> the profile's active decisions (as they stand before this batch) apply as
    /// <paramref name="familyId"/>, resolved as a draft resolves them, local contradictions included.
    /// </summary>
    private static IReadOnlySet<string> CoveredAsFamily(IReadOnlyList<ProjectProfile.EstimateProfile.FamilyDecision> decisions,
        RecognitionGroupInput whole, string familyId, EngineerBoqLibrary library)
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var partition in FamilyDecisionPolicy.ResolvePartitions(decisions, new[] { whole }, library))
            if (partition.Resolution.IsApplied && string.Equals(partition.Resolution.FamilyId, familyId, StringComparison.Ordinal))
                foreach (var record in partition.Group.Records) covered.Add(record.RecordId);
        return covered;
    }

    /// <summary>
    /// The partition rule is bound to its whole group's source and exact layer, so only groups of that source and layer can be
    /// addressed by it. Resolved with the complete batch, the new decision must govern exactly the approved records there: no
    /// fewer (a conflict or a stale outcome) and no more (a sibling group of the same layer that also carries the evidence).
    /// </summary>
    private static void RequirePartitionAppliesAsApproved(IReadOnlyList<ProjectProfile.EstimateProfile.FamilyDecision> decisions,
        ProjectProfile.EstimateProfile.FamilyDecision decision, RecognitionGroupInput whole, IReadOnlyList<string> recordIds,
        IReadOnlyList<RecognitionGroupInput> wholeGroups, EngineerBoqLibrary library)
    {
        var source = FamilyDecisionPolicy.NormalizeSource(whole.Source);
        var layer = (whole.LayerLeaf ?? string.Empty).Trim();
        var scope = wholeGroups.Where(g =>
            string.Equals(FamilyDecisionPolicy.NormalizeSource(g.Source), source, StringComparison.OrdinalIgnoreCase) &&
            string.Equals((g.LayerLeaf ?? string.Empty).Trim(), layer, StringComparison.OrdinalIgnoreCase)).ToList();
        var governed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var partition in FamilyDecisionPolicy.ResolvePartitions(decisions, scope, library))
        {
            if (!string.Equals(partition.Resolution.DecisionId, decision.DecisionId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!partition.Resolution.IsApplied ||
                !string.Equals(partition.Resolution.FamilyId, decision.FamilyId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Group '{partition.WholeGroup.GroupId}' would not apply as approved " +
                    $"({partition.Resolution.StaleReason ?? partition.Resolution.State.ToString()}); no family decision was saved.");
            foreach (var record in partition.Group.Records) governed.Add(record.RecordId);
        }
        if (!governed.SetEquals(recordIds))
            throw new InvalidOperationException(
                $"The approval in group '{whole.GroupId}' would govern {governed.Count} records instead of exactly the " +
                $"{recordIds.Count} approved ones; no family decision was saved.");
    }

    private static void RequireFamilyDecisionScope(
        ProjectProfile profile, ScanResult scan, ProjectProfileWriter.ExpectedProfileState expectedProfileState)
    {
        if (scan.ProfileWriteState == null || scan.ProfileWriteState != expectedProfileState)
            throw new InvalidOperationException(
                "The family decisions require the original scan's profile CAS evidence.");
        if (!string.Equals(scan.ProjectProfileId, profile.ProfileId, StringComparison.Ordinal) ||
            !string.Equals(scan.ProjectProfileEffectiveHash,
                EstimateTraceIdentity.EffectiveProfileHash(profile), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The family decisions do not belong to the current project-profile evidence; the profile changed after the quantity scan.");
        if (scan.Records.Any(record => !string.Equals(record.ProjectProfileId, profile.ProfileId, StringComparison.Ordinal)))
            throw new InvalidOperationException("The quantity scan contains records from a different project profile.");
        ProjectProfileWriter.RequireExpectedStateUnchanged(profile, expectedProfileState);
    }
}
