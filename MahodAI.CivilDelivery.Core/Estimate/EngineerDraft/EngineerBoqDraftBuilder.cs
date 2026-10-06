using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate.EngineerDraft;

/// <summary>One measured group: same source drawing, layer, measurement kind, method class and (for counts) block name.</summary>
public sealed record DraftBaseGroup(
    string Source,
    DraftSourceRole SourceRole,
    string Layer,
    string? Block,
    string Kind,
    string Unit,
    string MethodClass,
    int Count,
    double Quantity,
    string? TopBlocks,
    DraftLayerRole Role,
    string Reason,
    string? ApprovedCode = null,
    string? ApprovedBy = null,
    double? DrawnLength = null,
    string? Linetype = null,
    int UnmeasuredObjects = 0,
    int OverlapPairs = 0,
    string GroupId = "");

/// <summary>A source row of a library element, with the default include decision, the reason and the scan evidence on it.</summary>
public sealed record DraftElementSource(DraftBaseGroup Group, bool Included, string Note, int UnmeasuredObjects, int OverlapPairs, bool InPrimarySource = true);

/// <summary>A library element measured in this scan: the base measurement that its catalog lines multiply.</summary>
public sealed record DraftElement(DraftRule Rule, IReadOnlyList<DraftElementSource> Sources)
{
    public double IncludedQuantity => Sources.Where(s => s.Included).Sum(s => s.Group.Quantity);
    public int UnmeasuredObjects => Sources.Where(s => s.Included).Sum(s => s.UnmeasuredObjects);
    public int OverlapPairs => Sources.Where(s => s.Included).Sum(s => s.OverlapPairs);
    public string Unit => Sources.FirstOrDefault()?.Group.Unit ?? string.Empty;
}

/// <summary>A proposed BoQ contribution: a catalog item times an element's base measurement.</summary>
public sealed record DraftLine(
    DraftElement Element,
    DraftEmit Emit,
    CatalogItem? Item,
    decimal? Price,
    string Chapter,
    string SubChapter)
{
    /// <summary>The catalog item's unit does not match the measurement and no conversion is declared: never priced.</summary>
    public bool UnitMismatch { get; init; }

    /// <summary>
    /// Contributions with the same key share one BoQ row (one row per catalog item). A rule may ask for its
    /// own row, and a unit mismatch always gets its own unpriced row so it can never enter a priced sum.
    /// </summary>
    public string RowKey => Element.Rule.SeparateBoqRow || UnitMismatch ? $"{Emit.Code}#{Element.Rule.Id}" : Emit.Code;
}

/// <summary>A check the engineer must read. <see cref="AffectsTotal"/> marks items that change or are missing from the priced subtotal.</summary>
public sealed record DraftWarning(string Topic, string Message, string Affects, string Action, bool AffectsTotal);

public sealed record DraftSourceSummary(string Source, DraftSourceRole Role, int Records, string Meaning);

/// <summary>One scan finding code as it appears in the draft's check list.</summary>
public sealed record DraftFindingSummary(string Code, string Severity, int Count, string ExampleTitle, int AffectedRecords);

/// <summary>Scan identity and profile hints for the draft.</summary>
public sealed record EngineerDraftContext(
    string ProjectName,
    string ProfileId,
    string ScanRunId,
    string SourceDrawing,
    string CatalogLabel,
    IReadOnlyList<string> SectionClLayerPatterns,
    string? PluginVersion = null,
    int RecordedEstimateDecisions = 0,
    string? CatalogIdentity = null,
    IReadOnlyDictionary<string, string>? ExcludedRuleDecisions = null,
    IReadOnlyList<ProjectProfile.EstimateProfile.FamilyDecision>? FamilyDecisions = null,
    IFamilyClassifier? Classifier = null);

/// <summary>The complete, unapproved engineer draft of one scan.</summary>
public sealed class EngineerBoqDraft
{
    public required EngineerBoqLibrary Library { get; init; }
    public required EngineerDraftContext Context { get; init; }
    public required CatalogSnapshot Catalog { get; init; }
    /// <summary>Sub-chapter key (e.g. "51.04") → price-list title, read from the price-list file when available.</summary>
    public required IReadOnlyDictionary<string, string> ChapterTitles { get; init; }
    public required IReadOnlyList<DraftElement> Elements { get; init; }
    public required IReadOnlyList<DraftLine> Lines { get; init; }
    public required IReadOnlyList<DraftBaseGroup> UnmappedDesign { get; init; }
    public required IReadOnlyList<DraftBaseGroup> NotUsedAlternatives { get; init; }
    public required IReadOnlyList<DraftBaseGroup> Existing { get; init; }
    public required IReadOnlyList<DraftBaseGroup> Utilities { get; init; }
    public required IReadOnlyList<DraftBaseGroup> DraftingAids { get; init; }
    public required IReadOnlyList<DraftBaseGroup> CorridorVolumes { get; init; }
    /// <summary>Groups an engineer marked "not a construction quantity" in the profile (with approver and time).</summary>
    public IReadOnlyList<DraftBaseGroup> ExcludedByDecision { get; init; } = Array.Empty<DraftBaseGroup>();
    /// <summary>
    /// The groups recognition and family decisions work on: valid records by source, layer, kind, unit, method class
    /// (before any drawn-width re-read) and, for counts, block. Their ids are <see cref="RecognitionGroupInput.GroupId"/>.
    /// </summary>
    public IReadOnlyList<RecognitionGroupInput> RecognitionGroups { get; init; } = Array.Empty<RecognitionGroupInput>();
    /// <summary>Original full-scan groups, before evidence-rule partitions. Required when reviewing saved literal approvals.</summary>
    public IReadOnlyList<RecognitionGroupInput> WholeRecognitionGroups { get; init; } = Array.Empty<RecognitionGroupInput>();
    /// <summary>Contribution groups and their original validation scopes; preserves literal approval evidence after partitioning.</summary>
    public IReadOnlyList<FamilyDecisionPolicy.PartitionResolution> FamilyDecisionPartitions { get; init; } =
        Array.Empty<FamilyDecisionPolicy.PartitionResolution>();
    /// <summary>The profile's family decisions resolved on <see cref="RecognitionGroups"/>: applied, stale (kept, not applied) or not covered.</summary>
    public IReadOnlyList<FamilyResolution> FamilyResolutions { get; init; } = Array.Empty<FamilyResolution>();
    /// <summary>Recognition review for uncovered design groups and unapproved assumption/decision recipes. Never priced.</summary>
    public IReadOnlyList<RecognitionProposal> RecognitionProposals { get; init; } = Array.Empty<RecognitionProposal>();
    public string? ClassifierIdentity { get; init; }
    public required IReadOnlyList<DraftSourceSummary> Sources { get; init; }
    public required IReadOnlyList<DraftWarning> Warnings { get; init; }
    public required IReadOnlyList<DraftFindingSummary> Findings { get; init; }
    public required int RecordCount { get; init; }
    /// <summary>Record id → the id of the measured base group it was counted in (LibraryProposalGate reads it).</summary>
    public IReadOnlyDictionary<string, string> RecordGroupIds { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public required int InvalidMeasurementRecords { get; init; }
    /// <summary>Objects the scan could not measure at all, whatever their source (includes unlocated failures).</summary>
    public required int UnmeasuredObjectsTotal { get; init; }
    /// <summary>Unmeasured objects on design sources (these can make BoQ quantities too low).</summary>
    public required int UnmeasuredDesignObjects { get; init; }

    /// <summary>
    /// Every valid record lands in exactly one bucket. The writer and the tests
    /// check this so a record can never silently disappear from the draft.
    /// </summary>
    public int AccountedRecords =>
        Elements.Sum(e => e.Sources.Sum(s => s.Group.Count)) +
        UnmappedDesign.Sum(g => g.Count) + NotUsedAlternatives.Sum(g => g.Count) +
        Existing.Sum(g => g.Count) + Utilities.Sum(g => g.Count) +
        DraftingAids.Sum(g => g.Count) + CorridorVolumes.Sum(g => g.Count) + ExcludedByDecision.Sum(g => g.Count) +
        InvalidMeasurementRecords;
}

/// <summary>
/// Builds the engineer draft from neutral scan records. Pure and host-free: it reads
/// records, findings, the catalog snapshot and a library, and changes none of them.
/// </summary>
public static class EngineerBoqDraftBuilder
{
    private static readonly Regex FailureLine = new(
        @"xref=(?<xref>[^;\r\n]*);\s*layer=(?<layer>[^;\r\n]*);\s*entity=(?<entity>[^;\r\n]*);\s*method=(?<method>[^;\s\r\n]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LeadingCount = new(@"^\s*(?<n>\d+)\s", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OverlapPairsInTitle = new(@"(?<n>\d+)\s+overlapping pair", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public const string HostSource = "(קובץ ראשי)";

    public static EngineerBoqDraft Build(
        IReadOnlyList<NeutralQuantityRecord> records,
        IReadOnlyList<DeliveryFinding> scanFindings,
        CatalogSnapshot catalog,
        IReadOnlyDictionary<string, string>? chapterTitles,
        EngineerBoqLibrary library,
        EngineerDraftContext context)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(scanFindings);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(context);
        chapterTitles ??= new Dictionary<string, string>();

        var invalid = 0;
        // Same authority as the approved estimate (IgnoredRulePolicy): ordinal keys, and a key with any
        // failed measurement is never excluded, so a decision can never hide a measurement failure.
        var excludedRules = context.ExcludedRuleDecisions == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(context.ExcludedRuleDecisions, StringComparer.Ordinal);
        var blockedExclusions = records
            .Where(r => r.Classification?.RuleKey is { } k && excludedRules.ContainsKey(k) &&
                        !QuantitySignificance.IsValidMeasurement(r.Measurement.RawValue))
            .Select(r => r.Classification.RuleKey!).ToHashSet(StringComparer.Ordinal);
        foreach (var key in blockedExclusions) excludedRules.Remove(key);

        // Family decisions bind recognition groups (whatever their layer name) to a library family. They are resolved
        // once, on groups taken before any drawn-width re-read, so a width split never changes which decision applies.
        var wholeRecognitionGroups = RecognitionGroups(records, library);
        // A semantic rule can cover a proper subset of a mixed measured group. Preserve its
        // contribution identity through accumulation; do not reunite it with the unmatched records.
        var partitioned = context.FamilyDecisions is { Count: > 0 } partitionDecisions &&
                          partitionDecisions.Any(d => d != null && d.Status == FamilyDecisionPolicy.Active &&
                              d.Selectors != null && d.Selectors.Any(s => s != null && s.EvidenceMatch is { Count: > 0 }))
            ? FamilyDecisionPolicy.ResolvePartitions(partitionDecisions, wholeRecognitionGroups, library, catalog,
                checkLocalContradictions: false)
            : Array.Empty<FamilyDecisionPolicy.PartitionResolution>();
        IReadOnlyList<RecognitionGroupInput> recognitionGroups = partitioned.Count > 0
            ? partitioned.Select(p => p.Group).ToList() : wholeRecognitionGroups;
        var resolutions = context.FamilyDecisions is { Count: > 0 } decisions
            ? partitioned.Count > 0 ? partitioned.Select(p => p.Resolution).ToList()
                : FamilyDecisionPolicy.Resolve(decisions, recognitionGroups, library).ToList()
            : new List<FamilyResolution>();
        var partitionByRecord = partitioned.Where(p => p.Group.GroupId != p.WholeGroup.GroupId)
            .SelectMany(p => p.Group.Records.Select(r => (r.RecordId, Partition: p)))
            .ToDictionary(x => x.RecordId, x => x.Partition, StringComparer.Ordinal);
        var validationScopes = partitioned.ToDictionary(p => p.Group.GroupId, p => p.ValidationScope, StringComparer.Ordinal);
        // New evidence can contradict an approval: when the local recognition of the covered records now says something
        // other than both the approved family and what it said at approval, the decision is kept but not applied.
        if (context.Classifier is { } contradictionCheck && resolutions.Count > 0)
        {
            var decisionById = context.FamilyDecisions!.Where(d => d?.DecisionId != null)
                .GroupBy(d => d.DecisionId!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
            var groupById = recognitionGroups.ToDictionary(g => g.GroupId, StringComparer.Ordinal);
            for (var i = 0; i < resolutions.Count; i++)
            {
                var r = resolutions[i];
                if (r.State != FamilyDecisionState.Applied || r.FamilyId == null || r.DecisionId == null ||
                    !decisionById.TryGetValue(r.DecisionId, out var decision) || r.SelectorIndex < 0 ||
                    r.SelectorIndex >= (decision.Selectors?.Count ?? 0) || !groupById.TryGetValue(r.GroupId, out var covered))
                    continue;
                var validationScope = validationScopes.TryGetValue(r.GroupId, out var originalScope) ? originalScope : covered;
                var now = FamilyDecisionPolicy.LocalVerdict(contradictionCheck.Classify(validationScope, library, catalog));
                if (FamilyDecisionPolicy.IsContradicted(r.FamilyId, decision.Selectors![r.SelectorIndex].LocalVerdictAtApproval, now))
                    resolutions[i] = r with { State = FamilyDecisionState.Stale, StaleReason = FamilyDecisionPolicy.StaleContradicted };
            }
        }
        var appliedFamilies = resolutions
            .Where(r => r.State == FamilyDecisionState.Applied && r.FamilyId != null &&
                        library.Rules.Any(rule => rule.Id == r.FamilyId))
            .ToDictionary(r => r.GroupId, StringComparer.Ordinal);
        // A family that splits marking lines by drawn width reads the width on its decided layers too.
        var widthFamilyGroups = appliedFamilies.Values
            .Where(r => library.Rules.First(rule => rule.Id == r.FamilyId).SplitByDrawnWidth)
            .Select(r => r.GroupId).ToHashSet(StringComparer.Ordinal);
        var recognitionIdsByKey = new Dictionary<GroupKey, HashSet<string>>();
        var buckets = new Dictionary<GroupKey, Accumulator>();
        var sourceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var recordIndex = new Dictionary<string, GroupKey>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var value = record.Measurement.RawValue;
            var source = NormalizeSource(record.Source.Xref);
            sourceCounts[source] = sourceCounts.TryGetValue(source, out var n) ? n + 1 : 1;
            var layer = SectionProjectionLogic.LayerLeaf(record.Source.Layer);
            if (!QuantitySignificance.IsValidMeasurement(value)) { invalid++; continue; }
            var kind = (record.Measurement.Kind ?? string.Empty).Trim().ToLowerInvariant();
            var unit = (record.Measurement.Unit ?? string.Empty).Trim();
            var cls = MethodClass(kind, record.Measurement.Method);
            record.Measurement.Parameters.TryGetValue("cad_block_name_effective", out var rawBlock);
            var blockLeaf = string.IsNullOrWhiteSpace(rawBlock) ? string.Empty : SectionProjectionLogic.LayerLeaf(rawBlock);
            var recognitionId = RecognitionGroupId(source, layer, kind, unit, cls, kind == "count" ? blockLeaf : string.Empty);
            var familyPartitionId = string.Empty;
            if (partitionByRecord.TryGetValue(record.RecordId, out var partition))
                recognitionId = familyPartitionId = partition.Group.GroupId;
            // An engineer's approved mapping, valid for the active price list, is authority the draft keeps.
            string? approvedCode = null, approvedBy = null;
            if (record.Classification is { } approval && !string.IsNullOrWhiteSpace(approval.MappingApprovedBy) &&
                CatalogIdentity.IsClassificationCurrent(approval, catalog))
            {
                approvedCode = approval.CandidateCatalogCode!.Trim();
                approvedBy = approval.MappingApprovedBy!.Trim();
            }
            double? drawnLength = null;
            // An approved per-metre mapping keeps its metres; only unapproved marking lines are re-read by drawn width.
            var localWidth = 0d;
            var decidedWithoutWidth = appliedFamilies.TryGetValue(recognitionId, out var decided) &&
                                      !library.Rules.First(rule => rule.Id == decided.FamilyId).SplitByDrawnWidth;
            var widthState = approvedCode == null && kind == "length" && cls == "open" &&
                             ((EngineerBoqLibrary.AnyGlob(layer, library.WidthClassifiedLayerPatterns) && !decidedWithoutWidth) ||
                              widthFamilyGroups.Contains(recognitionId))
                ? ReadDrawnWidth(record.Measurement.Parameters, out localWidth)
                : DrawnWidthState.Absent;
            // A width that exists but is not proven (units, reading, a varying width) is never replaced by the
            // parameter: only a width that is truly absent falls back to the declared width.
            if (widthState == DrawnWidthState.Unproven) cls = "open-width-unproven";
            else if (widthState == DrawnWidthState.Proven)
            {
                // The width is stored in the entity's own drawing. It is a host width only for a host entity or
                // when the XREF transform is proven; otherwise the width-dependent pricing stays explicitly open.
                var hostScale = XrefWidthPolicy.HostWidthScale(record);
                var width = hostScale is { } scale ? localWidth * scale : double.NaN;
                if (hostScale == null) cls = "open-width-unproven";
                else if (width <= 0.105) cls = "open-w10";
                else if (width <= 0.155) cls = "open-w15";
                else
                {
                    // A wide marking line is paint on an area: length × drawn width.
                    drawnLength = value;
                    value *= width;
                    kind = "area";
                    unit = "מ\"ר";
                    cls = "painted-width";
                }
            }
            var excludedBy = record.Classification?.RuleKey is { } ruleKey && excludedRules.TryGetValue(ruleKey, out var approver)
                ? approver : string.Empty;
            var key = new GroupKey(source, layer, kind == "count" ? blockLeaf : string.Empty, kind, unit, cls, excludedBy, familyPartitionId);
            recordIndex[record.RecordId] = key;
            if (!recognitionIdsByKey.TryGetValue(key, out var recognitionIds)) recognitionIdsByKey[key] = recognitionIds = new HashSet<string>(StringComparer.Ordinal);
            recognitionIds.Add(recognitionId);
            if (!buckets.TryGetValue(key, out var acc)) buckets[key] = acc = new Accumulator();
            acc.Count++;
            acc.Quantity += value;
            if (drawnLength is { } drawn) acc.DrawnLength += drawn;
            if (EffectiveLinetype(record.Measurement.Parameters) is { } linetype)
                acc.Linetypes[linetype] = acc.Linetypes.TryGetValue(linetype, out var lt) ? lt + 1 : 1;
            if (approvedCode != null)
            {
                if (acc.ApprovedCode == null) { acc.ApprovedCode = approvedCode; acc.ApprovedBy = approvedBy; }
                else if (!string.Equals(acc.ApprovedCode, approvedCode, StringComparison.OrdinalIgnoreCase)) acc.ApprovedConflict = true;
                acc.ApprovedRecords++;
            }
            if (blockLeaf.Length > 0) acc.Blocks[blockLeaf] = acc.Blocks.TryGetValue(blockLeaf, out var b) ? b + 1 : 1;
        }

        // Scan evidence is attributed per measured group: overlaps by the records they name,
        // located failures to exactly one group of the matching measurement kind.
        var overlapByKey = ParseOverlaps(scanFindings, recordIndex);
        var failures = ParseFailures(scanFindings, out var unlocatedFailures, out var scopedEvidenceFailures);
        var failureByKey = new Dictionary<GroupKey, int>();
        var failureTargets = new Dictionary<Failure, GroupKey?>();
        foreach (var failure in failures)
        {
            var target = buckets.Keys
                .Where(k => SameSource(k.Source, failure.Source) && string.Equals(k.Layer, failure.Layer, StringComparison.OrdinalIgnoreCase))
                .Select(k => (Key: k, Rank: FailureRank(failure, k.Class)))
                .Where(x => x.Rank >= 0)
                .OrderBy(x => x.Rank).ThenBy(x => x.Key.Block, StringComparer.Ordinal)
                .Select(x => (GroupKey?)x.Key).FirstOrDefault();
            failureTargets[failure] = target;
            if (target is { } attached) failureByKey[attached] = (failureByKey.TryGetValue(attached, out var c) ? c : 0) + failure.Count;
        }

        var partialApprovals = new HashSet<(string, string)>();
        var groupKeys = new Dictionary<string, GroupKey>(StringComparer.Ordinal);
        var groups = buckets.Select(pair =>
        {
            var k = pair.Key;
            var sourceRole = SourceRole(k.Source, library);
            var role = LayerRole(sourceRole, k.Layer, k.Kind, k.Class, k.Unit, pair.Value.Quantity, pair.Value.Count,
                context.SectionClLayerPatterns, library, out var reason);
            if (k.Kind == "count" && IsOwnToolBlock(k.Block) && role is DraftLayerRole.Design or DraftLayerRole.DesignUtility)
            {
                role = DraftLayerRole.DraftingAid;
                reason = "בלוק של כלי מהוד (הקרנה או סימון) — לא כמות בנייה";
            }
            if (k.ExcludedBy.Length > 0)
            {
                // An engineer's recorded decision outranks every heuristic and library rule.
                role = DraftLayerRole.ExcludedByDecision;
                reason = $"הוחרג בהחלטת מהנדס — 'לא כמות בנייה' ({k.ExcludedBy})";
            }
            var blocks = pair.Value.Blocks.Count == 0 ? null : string.Join("; ", pair.Value.Blocks
                .OrderByDescending(b => b.Value).ThenBy(b => b.Key, StringComparer.Ordinal).Take(4)
                .Select(b => $"{b.Key}×{b.Value}"));
            var fullyApproved = k.ExcludedBy.Length == 0 && pair.Value.ApprovedRecords == pair.Value.Count && !pair.Value.ApprovedConflict;
            if (k.ExcludedBy.Length == 0 && pair.Value.ApprovedRecords > 0 && !fullyApproved) partialApprovals.Add((k.Source, k.Layer));
            var linetype = pair.Value.Linetypes.Count == 0 ? null : pair.Value.Linetypes
                .OrderByDescending(l => l.Value).ThenBy(l => l.Key, StringComparer.Ordinal).First().Key;
            var id = k.Id;
            groupKeys[id] = k;
            return new DraftBaseGroup(k.Source, sourceRole, k.Layer, k.Block.Length == 0 ? null : k.Block, k.Kind, k.Unit, k.Class,
                pair.Value.Count, pair.Value.Quantity, blocks, role, reason,
                fullyApproved ? pair.Value.ApprovedCode : null, fullyApproved ? pair.Value.ApprovedBy : null,
                pair.Value.DrawnLength > 0 ? pair.Value.DrawnLength : null, linetype,
                failureByKey.TryGetValue(k, out var unmeasured) ? unmeasured : 0,
                overlapByKey.TryGetValue(k, out var pairs) ? pairs : 0, id);
        }).OrderBy(g => g.Layer, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Source, StringComparer.OrdinalIgnoreCase)
          .ThenBy(g => g.Kind, StringComparer.Ordinal).ThenBy(g => g.MethodClass, StringComparer.Ordinal)
          .ThenBy(g => g.Block ?? string.Empty, StringComparer.Ordinal).ToList();

        var warnings = new List<DraftWarning>();
        foreach (var scope in scanFindings.Where(f => f.Code == EstimateSourceSelectionPolicy.ExcludedScopeCode))
            warnings.Add(new DraftWarning("מקורות שנבחרו לפני מדידה", scope.Message ?? scope.Title,
                "הטיוטה כוללת רק את מקורות הסריקה שנבחרו", "לשינוי ההיקף: עריכת מקורות, סריקה חדשה וייצוא מחדש.", true));
        foreach (var key in blockedExclusions.OrderBy(k => k, StringComparer.Ordinal))
            warnings.Add(new DraftWarning("החרגה נדחתה",
                $"להחלטת 'לא כמות בנייה' על {Bidi.Ltr(key)} יש רשומות עם כמות לא תקינה, ולכן ההחלטה לא הוחלה והכמויות נשארו בטיוטה.",
                "הכמויות של המפתח", "לתקן את מקור המדידה ולסרוק מחדש, ורק אז לבחון שוב את ההחלטה.", true));
        // The family a measured group was bound to by an engineer decision: only when every record of the group
        // comes from recognition groups decided for the same family.
        FamilyResolution? FamilyOf(DraftBaseGroup group)
        {
            if (appliedFamilies.Count == 0 || !recognitionIdsByKey.TryGetValue(groupKeys[group.GroupId], out var ids)) return null;
            FamilyResolution? first = null;
            foreach (var id in ids)
            {
                if (!appliedFamilies.TryGetValue(id, out var resolution)) return null;
                if (first != null && !string.Equals(first.FamilyId, resolution.FamilyId, StringComparison.Ordinal)) return null;
                first ??= resolution;
            }
            return first;
        }
        DraftRule? FamilyRule(DraftBaseGroup group) =>
            FamilyOf(group) is { FamilyId: { } familyId } ? library.Rules.FirstOrDefault(r => r.Id == familyId) : null;
        var hatchLayersAnySource = new HashSet<string>(groups.Where(g => g.Kind == "area" && g.MethodClass == "hatch")
            .Select(g => g.Layer), StringComparer.OrdinalIgnoreCase);
        // A rule "has hatches" when any design model hatches any of its layers: an outline on a sibling
        // layer is then a boundary or a copy, not extra work.
        bool RuleHasHatch(DraftRule rule) => groups.Any(g => g.Kind == "area" && g.MethodClass == "hatch" &&
            g.SourceRole is DraftSourceRole.Design or DraftSourceRole.Host &&
            (EngineerBoqLibrary.AnyGlob(g.Layer, rule.LayerPatterns) || FamilyRule(g)?.Id == rule.Id));

        // --- Assign groups to library elements and engineer-approved mappings ------------------
        var matched = new Dictionary<DraftRule, List<DraftBaseGroup>>();
        var variants = new Dictionary<(string RuleId, string Code), DraftRule>();
        var unmapped = new List<DraftBaseGroup>();
        var notUsed = new List<DraftBaseGroup>();
        var approvedGroups = new List<DraftBaseGroup>();
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var gatedApprovals = new List<(DraftBaseGroup Group, DraftRule Rule, DraftEmit Emit)>();
        int approvalsInRecipe = 0, approvalsSubstituted = 0, approvalsRefused = 0;
        var reportedSubstitutions = new HashSet<(string, string)>();
        var substitutionWarnings = new List<(string VariantId, string Layer, DraftWarning Warning)>();
        var familyAssigned = new Dictionary<string, FamilyResolution>(StringComparer.Ordinal);
        var blockedFamilies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var designSource = group.SourceRole is DraftSourceRole.Design or DraftSourceRole.Host;
            // An engineer's family decision outranks the layer-name heuristics, as an approved item does.
            // A family decision applies to design groups only (a group now classified as a drafting aid, existing work or an
            // implausible magnitude is not silently covered), and never over a current approved item, which is the stronger
            // item-level authority.
            var decidedFamily = designSource && group.Role != DraftLayerRole.ExcludedByDecision && group.Role != DraftLayerRole.CorridorVolume
                ? FamilyRule(group) : null;
            DraftRule? familyRule = null;
            if (decidedFamily != null)
            {
                if (group.ApprovedCode != null) blockedFamilies[group.GroupId] = FamilyDecisionPolicy.StaleApprovedItem;
                else if (group.Role is not (DraftLayerRole.Design or DraftLayerRole.DesignUtility)) blockedFamilies[group.GroupId] = FamilyDecisionPolicy.StaleRole;
                else familyRule = decidedFamily;
            }
            var candidate = group.Role is DraftLayerRole.Design or DraftLayerRole.DesignUtility ||
                            (group.ApprovedCode != null && designSource &&
                             group.Role is DraftLayerRole.Existing or DraftLayerRole.Utility or DraftLayerRole.DraftingAid);
            if (!candidate) continue;
            consumed.Add(group.GroupId);
            // Designed infrastructure layers match the infrastructure domain families (decision rows, no items).
            var nameMatched = group.Role is DraftLayerRole.Design or DraftLayerRole.DesignUtility;
            var acceptRule = familyRule != null
                ? (Accepts(familyRule, group) ? familyRule : null)
                : nameMatched
                    ? library.Rules.FirstOrDefault(r => Matches(r, group) && Accepts(r, group))
                    : null;
            var layerRule = familyRule ?? (nameMatched ? library.Rules.FirstOrDefault(r => Matches(r, group)) : null);
            if (familyRule != null) familyAssigned[group.GroupId] = FamilyOf(group)!;
            if (group.ApprovedCode != null)
            {
                if (acceptRule != null)
                {
                    // A recipe line counts as the approved item through the active list's own code (an approved edition link).
                    var inRecipe = acceptRule.Emits.FirstOrDefault(e =>
                        string.Equals(catalog.ResolveLibraryCode(e.Code) ?? e.Code, group.ApprovedCode, StringComparison.OrdinalIgnoreCase));
                    if (inRecipe != null)
                    {
                        // The approved item is part of the recipe: the recipe stands; its other lines stay proposals.
                        Add(matched, acceptRule, group);
                        approvalsInRecipe++;
                        if (inRecipe.ParameterKeys.Any()) gatedApprovals.Add((group, acceptRule, inRecipe));
                        continue;
                    }
                    // The approval is an item choice: it replaces the matching line of the recipe (or gives a rule that
                    // waited for a decision its item). Hold-backs, notes and the primary model of the rule are kept.
                    var variant = ApprovedVariant(acceptRule, group.ApprovedCode, catalog, variants, out var replaced);
                    if (variant == null)
                    {
                        Add(matched, acceptRule, group);
                        approvalsRefused++;
                        var refusal = ApprovedVariantRefusal(acceptRule, group.ApprovedCode, catalog);
                        warnings.Add(new DraftWarning("שיוך מאושר לא הוחל",
                            $"לשכבה {Bidi.Ltr(group.Layer)} ({Bidi.Ltr(DisplaySource(group.Source))}) יש בפרופיל שיוך מאושר לפריט {Bidi.Ltr(group.ApprovedCode)}, " +
                            $"ולא ניתן להחליף בו שורה במתכון '{acceptRule.DisplayName}'" + (refusal == null ? "" : $": {refusal}") + ". המתכון נשאר כפי שהוא.",
                            acceptRule.DisplayName, "להחליט אם להחליף שורה במתכון ידנית, או לשנות את השיוך בפרופיל.", true));
                        continue;
                    }
                    Add(matched, variant, group);
                    approvalsSubstituted++;
                    if (reportedSubstitutions.Add((variant.Id, group.Layer.ToUpperInvariant())))
                    {
                        var substitution = replaced == null
                            ? new DraftWarning("שיוך מאושר הוחל על רכיב להחלטה",
                                $"הרכיב '{acceptRule.DisplayName}' המתין להחלטה הנדסית, ובפרופיל יש לשכבה {Bidi.Ltr(group.Layer)} שיוך מאושר לפריט {Bidi.Ltr(group.ApprovedCode)}. השיוך הוחל.",
                                acceptRule.DisplayName, "לידיעה.", false)
                            : new DraftWarning("שיוך מאושר הוחלף בשורת מתכון",
                                $"לשכבה {Bidi.Ltr(group.Layer)} אושר בפרופיל הפריט {Bidi.Ltr(group.ApprovedCode)}. בטיוטה הוא מחליף את {Bidi.Ltr(replaced)} במתכון '{acceptRule.DisplayName}', ושאר שורות המתכון נשארו הצעה.",
                                acceptRule.DisplayName, "לידיעה; לבדוק ששאר שורות המתכון מתאימות.", false);
                        warnings.Add(substitution);
                        substitutionWarnings.Add((variant.Id, group.Layer, substitution));
                    }
                    continue;
                }
                if (layerRule != null)
                {
                    if (layerRule.Basis == DraftQuantityBasis.HatchArea && group.Kind == "area" && group.MethodClass == "closed-polyline" &&
                        !RuleHasHatch(layerRule))
                    {
                        // No model hatches this rule: the approved outline is the only drawing of the area.
                        approvedGroups.Add(group);
                        continue;
                    }
                    // The library owns this layer and rejects this measurement (a hatch boundary, a perimeter, a
                    // count on an area layer). An approval keyed by layer and kind does not make it new work.
                    approvalsRefused++;
                    notUsed.Add(group with { Reason = AlternativeReason(layerRule, group, RuleHasHatch(layerRule)) + " · יש שיוך מאושר בפרופיל לשכבה — מדידה חלופית, לא נוספה" });
                    warnings.Add(new DraftWarning("שיוך מאושר לא הוחל",
                        $"לשכבה {Bidi.Ltr(group.Layer)} ({Bidi.Ltr(DisplaySource(group.Source))}) יש שיוך מאושר לפריט {Bidi.Ltr(group.ApprovedCode)}, " +
                        $"אבל {Number(group.Quantity)} {group.Unit} ({MethodLabel(group.MethodClass)}) הם מדידה חלופית של '{layerRule.DisplayName}' ולא נוספו.",
                        layerRule.DisplayName, "לבדוק בשרטוט; אם זו עבודה נפרדת — להוסיף שורה ידנית.", true));
                    continue;
                }
                approvedGroups.Add(group);
                continue;
            }
            if (acceptRule != null) { Add(matched, acceptRule, group); continue; }
            if (group.Role == DraftLayerRole.DesignUtility && familyRule == null) { unmapped.Add(group); continue; }
            if (layerRule != null)
            {
                if (layerRule.Basis == DraftQuantityBasis.HatchArea && group.Kind == "area" && group.MethodClass == "closed-polyline" &&
                    !RuleHasHatch(layerRule))
                {
                    // No model hatches any layer of this rule: the closed outline may be real extra scope, not a boundary.
                    unmapped.Add(group with { Reason = $"שטח שאינו מוצלל באף מודל בשכבות של '{layerRule.DisplayName}' — ייתכן שטח עבודה נוסף; להחלטה" });
                    warnings.Add(new DraftWarning("שטח ללא הצללה",
                        $"בשכבה {Bidi.Ltr(group.Layer)} ({Bidi.Ltr(DisplaySource(group.Source))}) יש {Number(group.Quantity)} {group.Unit} של פוליליינים סגורים, ואין הצללה בשכבות של '{layerRule.DisplayName}' באף מודל. הם לא נכללו בסכום.",
                        layerRule.DisplayName, "לבדוק בשרטוט אם זה שטח עבודה; אם כן — להוסיף שורה או לשייך בכלי.", true));
                    continue;
                }
                notUsed.Add(group with { Reason = AlternativeReason(layerRule, group, RuleHasHatch(layerRule)) });
                continue;
            }
            unmapped.Add(group with { Reason = "אין כלל בספריית השיוך ואין החלטת משפחה — אם זו עבודה: לאשר משפחה לפי גיליון 'הצעות זיהוי', לשייך פעם אחת בחלון השיוך (אפשר בעזרת עוזר ה-AI) ולסרוק מחדש, או להוסיף שורה ידנית" });
        }

        static void Add(Dictionary<DraftRule, List<DraftBaseGroup>> map, DraftRule rule, DraftBaseGroup group)
        {
            if (!map.TryGetValue(rule, out var list)) map[rule] = list = new List<DraftBaseGroup>();
            list.Add(group);
        }

        DraftElementSource SourceFor(DraftRule rule, DraftBaseGroup g, bool inPrimary, string? extraNote = null)
        {
            var included = inPrimary && rule.IncludedByDefault;
            var notes = new List<string>();
            if (!inPrimary)
                notes.Add(familyAssigned.ContainsKey(g.GroupId)
                    ? $"שויך למשפחה בהחלטת מהנדס, אבל המקור אינו המודל הראשי של הכלל ({Bidi.Ltr(rule.PrimarySourcePattern ?? string.Empty)}) — " +
                      "לא נכלל כברירת מחדל כדי למנוע ספירה כפולה אם זה העתק; לשנות ל-1 אם זו עבודה נפרדת"
                    : "אותה שכבה במודל נוסף — לא נכללה כברירת מחדל כדי למנוע ספירה כפולה; לשנות ל-1 אם זו עבודה נפרדת");
            else if (!rule.IncludedByDefault)
                notes.Add(extraNote ?? rule.HeldBackNote ?? "לא נכלל כברירת מחדל — חשד לשרטוט כפול; לשנות ל-1 רק אחרי בדיקה בשרטוט");
            if (g.UnmeasuredObjects > 0)
                notes.Add($"לא נמדדו {g.UnmeasuredObjects} עצמים בשכבה זו — הכמות כאן חסרה");
            if (g.OverlapPairs > 0)
                notes.Add($"הסריקה מצאה {g.OverlapPairs} זוגות עצמים שהמלבנים החוסמים שלהם חופפים — לבדוק שאותו שטח לא שורטט פעמיים");
            if (g.DrawnLength is { } drawnLength && drawnLength > 0)
                notes.Add($"אורך {Number(drawnLength)} מ' × רוחב משורטט ממוצע {(g.Quantity / drawnLength).ToString("0.00", CultureInfo.InvariantCulture)} מ'");
            if (EngineerBoqLibrary.IsUtilityFamily(BaseRuleId(rule)) && DiameterHint(g.Layer, g.Block) is { } diameter)
                notes.Add($"לפי השם: {diameter} (לא אומת — לבדוק בתכנית המערכת)");
            if (familyAssigned.TryGetValue(g.GroupId, out var family))
                notes.Add($"שויך למשפחה בהחלטת מהנדס ({family.ApprovedBy}" +
                          (family.ApprovedAtUtc is { } at ? $", {at.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}" : string.Empty) +
                          ") — הסעיפים עצמם נשארים הצעה");
            if (rule.Emits.Count > 0 && (g.Kind == "length" || g.MethodClass == "painted-width") &&
                (EngineerBoqLibrary.AnyGlob(g.Layer, library.WidthClassifiedLayerPatterns) ||
                 familyAssigned.ContainsKey(g.GroupId) && library.Rules.FirstOrDefault(r => r.Id == BaseRuleId(rule))?.SplitByDrawnWidth == true) &&
                IsDashed(g.Linetype) &&
                !rule.Emits.SelectMany(e => e.ParameterKeys).Any(k => k.Contains("RATIO", StringComparison.Ordinal)))
            {
                notes.Add($"סוג הקו בשרטוט ({Bidi.Ltr(g.Linetype)}) מקווקו, והכמות מניחה קו רציף — לבדוק את החלק הצבוע");
                warnings.Add(new DraftWarning("קו מקווקו שתומחר כרציף",
                    $"בשכבה {Bidi.Ltr(g.Layer)} סוג הקו בשרטוט הוא {Bidi.Ltr(g.Linetype)}, והכמות ({Number(g.Quantity)} {g.Unit}) מניחה קו רציף.",
                    rule.DisplayName,
                    "לבדוק בשרטוט. אם הקו מקווקו — להקטין את הכמות בעמודה 'כמות' בגיליון 'מדידות בסיס' לאורך הצבוע בלבד (למשל פי 0.5 לקו 1-1); הסכומים מתעדכנים.", true));
            }
            return new DraftElementSource(g, included, string.Join("; ", notes), g.UnmeasuredObjects, g.OverlapPairs, inPrimary);
        }

        var elements = new List<DraftElement>();
        // An item choice never changes which models are included: the primary model is decided once for the
        // base rule, over the groups of its recipe and of every approved-item variant together.
        var basePrimary = matched
            .GroupBy(pair => BaseRuleId(pair.Key), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g =>
            {
                var pattern = library.Rules.First(r => r.Id == g.Key).PrimarySourcePattern;
                return pattern != null && g.SelectMany(pair => pair.Value).Any(group => EngineerBoqLibrary.Glob(group.Source, pattern));
            }, StringComparer.Ordinal);
        // An approval replaces a recipe line only where the rule counts it (F-5, b27 host 02.10). Approved groups from a
        // model the rule does not count (a copy, excluded by default) go back to the recipe as copies: they were excluded
        // in the variant too, so no total changes, but the approved item is no longer printed for them. A layer of the
        // variant with no counted group is reported as not applied instead of "replaced" (per layer, review 03.10: a
        // variant that also holds a counted group of another layer keeps no false claim), and the approval is never
        // carried over to the counted model's layer (that would be a new approval).
        foreach (var variant in variants.Values.Where(matched.ContainsKey).OrderBy(v => v.Id, StringComparer.Ordinal).ToList())
        {
            var baseId = BaseRuleId(variant);
            var pattern = variant.PrimarySourcePattern;
            if (!basePrimary[baseId] || pattern == null) continue;
            var copies = matched[variant].Where(g => !EngineerBoqLibrary.Glob(g.Source, pattern)).ToList();
            if (copies.Count == 0) continue;
            var countedInVariant = matched[variant].Where(g => EngineerBoqLibrary.Glob(g.Source, pattern)).ToList();
            var baseRule = library.Rules.First(r => r.Id == baseId);
            if (countedInVariant.Count == 0) matched.Remove(variant);
            else matched[variant] = countedInVariant;
            foreach (var g in copies) Add(matched, baseRule, g);
            var countedLayers = new HashSet<string>(countedInVariant.Select(g => g.Layer), StringComparer.OrdinalIgnoreCase);
            var unapplied = copies.Where(g => !countedLayers.Contains(g.Layer)).ToList();
            // No copy replaced a counted line (Codex 03.10 13:07): a copy of a counted layer is only an excluded copy.
            approvalsSubstituted -= copies.Count;
            if (unapplied.Count == 0) continue;
            approvalsRefused += unapplied.Count;
            // The counted groups of the rule that do not carry this approval (the variant's own counted groups do).
            var countedWithout = matched.Where(pair => BaseRuleId(pair.Key) == baseId && pair.Key.Id != variant.Id)
                .SelectMany(pair => pair.Value).Where(g => EngineerBoqLibrary.Glob(g.Source, pattern))
                .Select(g => $"{Bidi.Ltr(g.Layer)} ({Bidi.Ltr(DisplaySource(g.Source))})").Distinct(StringComparer.Ordinal).ToList();
            foreach (var layerCopies in unapplied.GroupBy(g => g.Layer, StringComparer.OrdinalIgnoreCase))
            {
                var refusals = layerCopies
                    .GroupBy(g => g.Source, StringComparer.OrdinalIgnoreCase)
                    .Select(set => set.First())
                    .Select(g => new DraftWarning("שיוך מאושר לא הוחל",
                        $"לשכבה {Bidi.Ltr(g.Layer)} ({Bidi.Ltr(DisplaySource(g.Source))}) יש בפרופיל שיוך מאושר לפריט {Bidi.Ltr(g.ApprovedCode)}, " +
                        $"אבל המקור הזה אינו המודל הראשי של '{baseRule.DisplayName}' ({Bidi.Ltr(pattern)}) ולא נספר כברירת מחדל (עותק). " +
                        (countedWithout.Count > 0 ? $"הכמות נספרת מ-{string.Join(", ", countedWithout)}, ולהם אין שיוך מאושר לפריט הזה. " : "") +
                        "השיוך לא הוחל על שום כמות נספרת.",
                        baseRule.DisplayName,
                        "לאשר את השיוך לשכבה שנספרת בחלון השיוך ולסרוק מחדש. אם זו עבודה נפרדת ולא עותק — לבדוק בשרטוט ולהוסיף שורה ידנית.", true))
                    .ToList();
                var reported = substitutionWarnings
                    .Where(s => s.VariantId == variant.Id && string.Equals(s.Layer, layerCopies.Key, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Warning).ToList();
                var at = reported.Count > 0 ? warnings.IndexOf(reported[0]) : warnings.Count;
                foreach (var w in reported) warnings.Remove(w);
                warnings.InsertRange(Math.Min(at, warnings.Count), refusals);
            }
        }
        var ruleOrder = library.Rules.SelectMany(r => new[] { r }.Concat(
            variants.Values.Where(v => v.Id.StartsWith(r.Id + "+approved:", StringComparison.Ordinal)).OrderBy(v => v.Id, StringComparer.Ordinal)));
        foreach (var rule in ruleOrder)
        {
            if (!matched.TryGetValue(rule, out var list)) continue;
            var hasPrimary = basePrimary[BaseRuleId(rule)];
            var partitions = rule.SplitByDrawnWidth
                ? list.GroupBy(WidthClass).OrderBy(p => WidthOrder(p.Key)).Select(p => (Rule: WidthRule(rule, p.Key, library), Groups: p.ToList())).ToList()
                : new List<(DraftRule Rule, List<DraftBaseGroup> Groups)> { (rule, list) };
            foreach (var (partRule, partGroups) in partitions)
            {
                var sources = partGroups.Select(g => SourceFor(partRule, g,
                    !hasPrimary || EngineerBoqLibrary.Glob(g.Source, rule.PrimarySourcePattern!))).ToList();
                var element = new DraftElement(partRule, sources);
                elements.Add(element);
                AddElementWarnings(element, warnings);
            }
        }

        // Engineer-approved mappings with no library rule: taught once in the mapping window, reused on every scan.
        // They get the library's protections: one model per approved layer (all its blocks and methods), and a
        // hatch outline never counted on top of its hatch.
        foreach (var layerSet in approvedGroups
                     .GroupBy(g => (Layer: g.Layer.ToUpperInvariant(), Code: g.ApprovedCode!.ToUpperInvariant()))
                     .OrderBy(set => set.Key.Layer, StringComparer.Ordinal).ThenBy(set => set.Key.Code, StringComparer.Ordinal))
        {
            var primarySource = layerSet.GroupBy(g => g.Source, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(bySource => bySource.Sum(g => g.Count)).ThenBy(bySource => bySource.Key, StringComparer.OrdinalIgnoreCase)
                .First().Key;
            foreach (var set in layerSet.GroupBy(g => (g.Kind, g.MethodClass))
                         .OrderBy(set => set.Key.Kind, StringComparer.Ordinal).ThenBy(set => set.Key.MethodClass, StringComparer.Ordinal))
            {
                var head = set.First();
                var approvers = string.Join(", ", set.Select(g => g.ApprovedBy).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.Ordinal));
                var boundaryLikely = head.Kind == "area" && head.MethodClass == "closed-polyline" && hatchLayersAnySource.Contains(head.Layer);
                var rule = new DraftRule($"profile-approved:{head.Layer}:{head.ApprovedCode}:{head.Kind}:{head.MethodClass}",
                    $"{head.Layer} — שיוך מאושר בפרופיל", new[] { head.Layer }, BasisFor(head), DraftConfidence.Direct,
                    new[] { new DraftEmit(head.ApprovedCode!, Note: $"שיוך שאושר בפרופיל הפרויקט ע\"י {approvers}") },
                    "שיוך שמהנדס אישר בפרופיל הפרויקט (בחלון השיוך, כולל הצעות של עוזר ה-AI). המחיר לפי המחירון הפעיל. " +
                    "כמו בספרייה: נכלל מודל אחד כברירת מחדל, וגבול של הצללה אינו נספר מעליה.",
                    SeparateBoqRow: true, IncludedByDefault: !boundaryLikely,
                    Variant: head.MethodClass is "hatch" or "open" or "block" ? null : MethodLabel(head.MethodClass));
                var element = new DraftElement(rule, set.Select(g => SourceFor(rule, g, SameSource(g.Source, primarySource),
                    boundaryLikely ? "באותה שכבה יש הצללות — הפוליליין הסגור הוא כנראה גבול ההצללה; לא נכלל. לשנות ל-1 רק אם זה שטח נוסף" : null)).ToList());
                elements.Add(element);
                AddElementWarnings(element, warnings);
            }
        }
        foreach (var (source, layer) in partialApprovals.OrderBy(p => p.Item2, StringComparer.OrdinalIgnoreCase))
            warnings.Add(new DraftWarning("שיוך מאושר חלקי",
                $"רק חלק מהעצמים בשכבה {Bidi.Ltr(layer)} ({Bidi.Ltr(DisplaySource(source))}) נושאים שיוך מאושר תקף, או שהם שויכו לפריטים שונים. השיוך לא הוחל.",
                "השכבה מופיעה בגיליון המתאים ללא השיוך", "לבדוק את השיוך בחלון השיוך ולסרוק מחדש.", true));

        foreach (var (group, rule, emit) in gatedApprovals)
        {
            // A copy the rule does not count (review 03.10, F-5 family): no share of it is included at any parameter value,
            // so a parameter hint for it would move the counted totals instead.
            if (rule.PrimarySourcePattern is { } primaryPattern && basePrimary.TryGetValue(BaseRuleId(rule), out var ruleHasPrimary) &&
                ruleHasPrimary && !EngineerBoqLibrary.Glob(group.Source, primaryPattern)) continue;
            double Default(string key) => library.Parameters.First(p => p.Key == key).DefaultValue;
            var factor = 1.0;
            if (emit.ParameterKey != null) factor *= Default(emit.ParameterKey);
            if (emit.SecondParameterKey != null) factor *= Default(emit.SecondParameterKey);
            if (emit.ComplementKey != null) factor *= 1 - Default(emit.ComplementKey);
            if (factor >= 0.999) continue;
            warnings.Add(new DraftWarning("שיוך מאושר תלוי בפרמטר",
                // F-3 (02.10): name the item the engineer approved; the recipe line it stands for is a library code (an
                // approved edition link resolves it), shown next to it only when the two differ.
                $"לשכבה {Bidi.Ltr(group.Layer)} אושר בפרופיל הפריט {Bidi.Ltr(group.ApprovedCode ?? emit.Code)}" +
                (group.ApprovedCode != null && !string.Equals(group.ApprovedCode, emit.Code, StringComparison.OrdinalIgnoreCase)
                    ? $" (בספרייה: {Bidi.Ltr(emit.Code)})" : "") +
                $", אבל במתכון '{rule.DisplayName}' כמותו תלויה ב-{string.Join(", ", emit.ParameterKeys.Select(Bidi.Ltr))} " +
                $"ובערכי ברירת המחדל נכללים רק {(factor * 100).ToString("0", CultureInfo.InvariantCulture)}% מהכמות ({Number(group.Quantity * factor)} מתוך {Number(group.Quantity)} {group.Unit}).",
                rule.DisplayName, "אם הפריט המאושר חל על כל השכבה — לשנות את הפרמטר בגיליון 'פרמטרים' (הוא משותף לכל השכבות של המתכון).", true));
        }

        // --- Catalog lines ----------------------------------------------------------------------
        var lines = new List<DraftLine>();
        foreach (var element in elements)
        {
            foreach (var recipeEmit in element.Rule.Emits)
            {
                // A library code is priced by the active list's own code: itself when listed, else the item of an
                // engineer-approved edition link (EditionLinkPolicy). Never by a number or a similar description.
                var listed = catalog.ResolveLibraryCode(recipeEmit.Code);
                var emit = listed != null && !string.Equals(listed, recipeEmit.Code, StringComparison.OrdinalIgnoreCase)
                    ? recipeEmit with { Code = listed, Note = $"קישור מהדורה מאושר: {recipeEmit.Code} → {listed}" + (recipeEmit.Note == null ? string.Empty : " · " + recipeEmit.Note) }
                    : recipeEmit;
                catalog.Items.TryGetValue(emit.Code, out var item);
                decimal? price = catalog.Prices.TryGetValue(emit.Code, out var record) && !record.IsMissing ? record.Price : null;
                var (chapter, sub) = ChapterKeys(emit.Code);
                var unitMismatch = false;
                if (item == null)
                    warnings.Add(LibraryItemReferences.Items.TryGetValue(emit.Code, out var reference)
                        ? new DraftWarning("סעיף הספרייה אינו במהדורה הפעילה",
                            $"סעיף המתכון {Bidi.Ltr(emit.Code)} ({Short(reference.Description)}) נכתב במחירון {Bidi.Ltr(LibraryItemReferences.SourcePriceList)} ואינו במחירון הפעיל " +
                            $"({context.CatalogLabel}), ואין לו קישור מהדורה מאושר. השורה נשארת ללא תיאור ומחיר; אותו מספר או תיאור דומה במהדורה אחרת אינם אותו סעיף.",
                            element.Rule.DisplayName, "לקשר פעם אחת לכל מהדורה בחלון 'קישור ספרייה למהדורה', או לבחור פריט מהמחירון הפעיל.", true)
                        : new DraftWarning("פריט חסר במחירון", $"הפריט {Bidi.Ltr(emit.Code)} לא נמצא במחירון הפעיל ({context.CatalogLabel}); השורה נשארת ללא תיאור ומחיר.",
                            element.Rule.DisplayName, "לבחור פריט אחר מהמחירון הפעיל.", true));
                else if (!UnitCompatible(element, emit, item))
                {
                    unitMismatch = true;
                    price = null;
                    warnings.Add(new DraftWarning("יחידה לא תואמת", $"יחידת הפריט {Bidi.Ltr(emit.Code)} ({item.UnitRaw.Trim()}) שונה מיחידת המדידה ({element.Unit}) ואין המרה מוצהרת. השורה לא תומחרה ואינה בסכום.",
                        element.Rule.DisplayName, "לבחור פריט ביחידה המתאימה או להגדיר פרמטר המרה.", true));
                }
                else if (price == null)
                    warnings.Add(new DraftWarning("פריט ללא מחיר", $"לפריט {Bidi.Ltr(emit.Code)} אין מחיר במהדורת המחירון. השורה נספרת 0 עד שמזינים מחיר בעמודה 'מחיר יח''.",
                        element.Rule.DisplayName, "להשיג הצעת מחיר ולהזין אותה בשורה, או לבחור פריט חלופי.", true));
                lines.Add(new DraftLine(element, emit, item, price, chapter, sub) { UnitMismatch = unitMismatch });
            }
            if (element.Rule.Emits.Count == 0 && !element.Rule.Id.EndsWith("@unproven", StringComparison.Ordinal))
                warnings.Add(new DraftWarning("רכיב ללא פריט", $"נמדדו {Number(element.IncludedQuantity)} {element.Unit} והרכיב ממתין להגדרת פריט. אפשר להזין פריט ומחיר בשורה שלו בסוף גיליון 'כתב כמויות'.",
                    element.Rule.DisplayName, element.Rule.Note, true));
        }

        // --- Other buckets and global checks --------------------------------------------------
        DraftBaseGroup NotApplied(DraftBaseGroup g) => g.ApprovedCode == null ? g
            : g with { Reason = g.Reason + " · יש שיוך מאושר בפרופיל לשכבה — לא הוחל על מקור של מצב קיים/תשתיות" };
        var existing = groups.Where(g => g.Role == DraftLayerRole.Existing && !consumed.Contains(g.GroupId)).Select(NotApplied).ToList();
        var utilities = groups.Where(g => g.Role == DraftLayerRole.Utility && !consumed.Contains(g.GroupId)).Select(NotApplied).ToList();
        var aids = groups.Where(g => g.Role == DraftLayerRole.DraftingAid && !consumed.Contains(g.GroupId)).ToList();
        var corridor = groups.Where(g => g.Role == DraftLayerRole.CorridorVolume).ToList();
        var excludedByDecision = groups.Where(g => g.Role == DraftLayerRole.ExcludedByDecision).ToList();
        if (excludedByDecision.Count > 0)
            warnings.Add(new DraftWarning("הוחרג בהחלטת מהנדס",
                $"{excludedByDecision.Count} קבוצות מדידה ({excludedByDecision.Sum(g => g.Count):N0} עצמים) סומנו בפרופיל 'לא כמות בנייה' עם שם מאשר: " +
                string.Join(", ", excludedByDecision.Select(g => Bidi.Ltr(g.Layer)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12)) +
                ". הן לא נכללו בטיוטה ומופיעות בגיליון 'סימוני עזר'.",
                "לידיעה", "לבטל את ההחלטה בכלי (החזר לרשימה) אם היא כבר לא נכונה, ולסרוק מחדש.", false));
        var approvedCorridor = corridor.Where(g => g.ApprovedCode != null).ToList();
        if (approvedCorridor.Count > 0)
            warnings.Add(new DraftWarning("נפח קורידור עם שיוך מאושר",
                $"ל-{approvedCorridor.Count} קבוצות נפח קורידור יש שיוך מאושר בפרופיל (" +
                string.Join(", ", approvedCorridor.Select(g => $"{Bidi.Ltr(g.Layer)} → {Bidi.Ltr(g.ApprovedCode)}")) +
                "). הן לא נכללו בטיוטה, כי שכבות המיסעה כבר מתומחרות לפי שטחי ההצללה.",
                "מבנה המיסעה", "לבחור שיטה אחת: נפחי קורידור או מתכון לפי שטח — לא את שתיהן.", true));

        // Failures that reached a priced element are reported on its 'כמות חסרה' row; the rest per layer here.
        var elementGroupIds = new HashSet<string>(elements.SelectMany(e => e.Sources).Where(s => s.Included).Select(s => s.Group.GroupId), StringComparer.Ordinal);
        var failureRows = failures.Where(f => !(failureTargets.TryGetValue(f, out var t) && t is { } k && elementGroupIds.Contains(k.Id))).ToList();
        var designFailures = failures.Where(f => SourceRole(f.Source, library) is DraftSourceRole.Design or DraftSourceRole.Host).ToList();
        foreach (var failure in failureRows.OrderByDescending(f => designFailures.Contains(f)).ThenByDescending(f => f.Count))
        {
            var isDesign = designFailures.Contains(failure);
            warnings.Add(new DraftWarning("עצמים שלא נמדדו",
                $"לא נמדדו {failure.Count} עצמים מסוג {Bidi.Ltr(failure.Entity)} בשכבה {Bidi.Ltr(failure.Layer)}.",
                isDesign ? $"כמויות התכנון (מקור: {Bidi.Ltr(DisplaySource(failure.Source))})" : "לא משפיע על כתב הכמויות — מצב קיים/תשתיות",
                isDesign
                    ? $"לתקן את העצמים בקובץ {Bidi.Ltr(DisplaySource(failure.Source))} (למשל גבולות הצללה פתוחים) ולסרוק מחדש."
                    : "לידיעה בלבד.",
                isDesign));
        }
        if (unlocatedFailures > 0)
            warnings.Add(new DraftWarning("עצמים שלא נמדדו", $"{unlocatedFailures} עצמים לא נמדדו והסריקה לא רשמה את שכבתם.",
                "ייתכן שכמויות התכנון חסרות", "לבדוק את ממצא הסריקה EST-MEASUREMENT-FAILED ולסרוק מחדש.", true));
        if (scopedEvidenceFailures > 0)
            warnings.Add(new DraftWarning("ראיות מדידה חלקיות", $"ל-{scopedEvidenceFailures} עצמים שנמדדו חסרה ראיה משלימה (למשל גבולות).",
                "הכמויות נכללו", "לידיעה.", false));
        if (corridor.Count > 0)
            warnings.Add(new DraftWarning("נפחי קורידור",
                "נפחי החומר מהקורידורים לא נכללו בכתב הכמויות, כדי שלא ייספרו פעמיים עם שטחי ההצללה. הם מופיעים בראש גיליון 'סימוני עזר' לבדיקת עוביים.",
                "מבנה המיסעה", "להשוות את העוביים בקורידורים לפרמטרי המיסעה.", false));
        if (invalid > 0)
            warnings.Add(new DraftWarning("רשומות לא תקינות", $"{invalid} רשומות עם כמות אפס או לא סופית לא נכללו בשום גיליון כמות.",
                "לא ידוע", "לבדוק את הסריקה.", true));
        if (context.RecordedEstimateDecisions > 0)
            warnings.Add(new DraftWarning("החלטות שמורות בפרופיל",
                $"בפרופיל הפרויקט שמורות {context.RecordedEstimateDecisions} החלטות אומדן. שיוכים מאושרים שתקפים למחירון הפעיל: " +
                $"{elements.Count(e => e.Rule.Id.StartsWith("profile-approved:", StringComparison.Ordinal))} רכיבים משלהם, {approvalsInRecipe} קבוצות שהפריט שלהן כבר במתכון, " +
                $"{approvalsSubstituted} קבוצות שבהן הפריט המאושר החליף שורת מתכון, ו-{approvalsRefused} שלא הוחלו (מפורטות בבדיקות). " +
                $"החלטות 'לא כמות בנייה' עם סיבה, מאשר וזמן הוחלו ({excludedByDecision.Count} קבוצות). מפתחות ישנים בלי מאשר אינם מוחלים.",
                "שורות שיש עליהן החלטה קודמת", "להשוות את השורות להחלטות השמורות.", false));
        AddPlausibilityChecks(elements, warnings);

        var findingSummaries = scanFindings
            .Where(f => f.Code != EstimateFindingCodes.Unmapped)
            .GroupBy(f => f.Code)
            .Select(g => new DraftFindingSummary(g.Key, Bidi.Severity(g.First()), g.Count(), g.First().Title,
                g.Sum(f => f.AffectedRecordIds?.Count ?? 0)))
            .OrderByDescending(f => f.Count).ToList();

        if (blockedFamilies.Count > 0)
        {
            var blockedRecognition = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (groupId, reason) in blockedFamilies)
                if (recognitionIdsByKey.TryGetValue(groupKeys[groupId], out var ids))
                    foreach (var id in ids) blockedRecognition.TryAdd(id, reason);
            for (var i = 0; i < resolutions.Count; i++)
                if (resolutions[i].State == FamilyDecisionState.Applied && blockedRecognition.TryGetValue(resolutions[i].GroupId, out var reason))
                    resolutions[i] = resolutions[i] with { State = FamilyDecisionState.Stale, StaleReason = reason };
        }
        AddFamilyDecisionWarnings(resolutions, recognitionGroups, library, familyAssigned.Count, warnings);

        // A name-matched recipe is still only an assumption/decision, not recognition or approval.
        // Review it without changing its existing contribution, prices or quantity. Valid item/family
        // decisions retain authority and are not reopened merely because their recipe is provisional.
        var provisionalRecipes = matched
            .Where(pair => pair.Key.Confidence is DraftConfidence.Assumption or DraftConfidence.Decision)
            .SelectMany(pair => pair.Value
                .Where(g => g.ApprovedCode == null && !familyAssigned.ContainsKey(g.GroupId))
                .Select(g => (g.GroupId, Rule: pair.Key)))
            .ToDictionary(pair => pair.GroupId, pair => pair.Rule, StringComparer.Ordinal);
        var currentItemApprovals = records
            .Where(r => r.Classification is { } approval && !string.IsNullOrWhiteSpace(approval.MappingApprovedBy) &&
                        CatalogIdentity.IsClassificationCurrent(approval, catalog))
            .Select(r => r.RecordId).ToHashSet(StringComparer.Ordinal);
        var reviewKeys = unmapped.Select(g => g.GroupId).ToHashSet(StringComparer.Ordinal);
        reviewKeys.UnionWith(provisionalRecipes.Keys);

        // Recognition proposes only; it never adds a second measured contribution or approves a price.
        var proposals = new List<RecognitionProposal>();
        if (context.Classifier is { } classifier && reviewKeys.Count > 0)
        {
            foreach (var recognitionGroup in recognitionGroups)
            {
                var pending = recognitionGroup.Records
                    .Where(r => recordIndex.TryGetValue(r.RecordId, out var key) && reviewKeys.Contains(key.Id) &&
                                (!provisionalRecipes.ContainsKey(key.Id) || !currentItemApprovals.Contains(r.RecordId))).ToList();
                if (pending.Count == 0) continue;
                foreach (var proposal in classifier.Classify(recognitionGroup with { Records = pending }, library, catalog))
                {
                    // UI Interpretation already displays Inferred separately from Observed/CAD citations.
                    // This is disclosure of an existing recipe, never a new evidence reference or verdict.
                    var recipeNotes = proposal.RecordIds
                        .Select(id => recordIndex.TryGetValue(id, out var key) && provisionalRecipes.TryGetValue(key.Id, out var rule) ? rule : null)
                        .Where(rule => rule != null).DistinctBy(rule => rule!.Id)
                        .Select(rule => $"מתכון קיים כהנחה או להחלטה — לא אישור: {rule!.DisplayName}. הכמות נשארת בשורה הקיימת; בחירת משפחה אינה מאשרת סעיף או מחיר.")
                        .ToList();
                    proposals.Add(recipeNotes.Count == 0 ? proposal : proposal with
                    {
                        Inferred = proposal.Inferred.Concat(recipeNotes).ToList(),
                    });
                }
            }
        }

        var sourceSummaries = sourceCounts.Select(pair => new DraftSourceSummary(pair.Key, SourceRole(pair.Key, library), pair.Value,
                SourceMeaning(SourceRole(pair.Key, library), library)))
            .OrderBy(s => s.Role).ThenByDescending(s => s.Records).ToList();

        return new EngineerBoqDraft
        {
            Library = library,
            Context = context,
            Catalog = catalog,
            ChapterTitles = chapterTitles,
            Elements = elements,
            Lines = lines,
            UnmappedDesign = unmapped.OrderBy(g => g.Role).ThenBy(g => UnitRank(g.Unit)).ThenByDescending(g => g.Quantity).ToList(),
            NotUsedAlternatives = notUsed.OrderBy(g => UnitRank(g.Unit)).ThenByDescending(g => g.Quantity).ToList(),
            Existing = existing.OrderByDescending(g => g.Quantity).ToList(),
            Utilities = utilities.OrderByDescending(g => g.Quantity).ToList(),
            DraftingAids = aids.OrderByDescending(g => g.Count).ToList(),
            CorridorVolumes = corridor,
            ExcludedByDecision = excludedByDecision,
            Sources = sourceSummaries,
            Warnings = warnings,
            Findings = findingSummaries,
            RecordCount = records.Count,
            RecordGroupIds = recordIndex.ToDictionary(pair => pair.Key, pair => pair.Value.Id, StringComparer.Ordinal),
            InvalidMeasurementRecords = invalid,
            UnmeasuredObjectsTotal = failures.Sum(f => f.Count) + unlocatedFailures,
            UnmeasuredDesignObjects = designFailures.Sum(f => f.Count) + unlocatedFailures,
            RecognitionGroups = recognitionGroups,
            WholeRecognitionGroups = wholeRecognitionGroups,
            FamilyDecisionPartitions = partitioned.Select(p => p with
            {
                Resolution = resolutions.First(r => r.GroupId == p.Group.GroupId),
            }).ToList(),
            FamilyResolutions = resolutions,
            RecognitionProposals = proposals,
            ClassifierIdentity = context.Classifier?.Identity,
        };
    }

    /// <summary>
    /// The groups recognition and family decisions are resolved on: valid records by source, layer leaf (case-insensitive),
    /// kind, unit, method class before any drawn-width re-read, and for counts the effective block. Ordered by id.
    /// </summary>
    public static IReadOnlyList<RecognitionGroupInput> RecognitionGroups(IReadOnlyList<NeutralQuantityRecord> records, EngineerBoqLibrary library)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(library);
        var map = new Dictionary<string, (RecognitionGroupInput Head, List<NeutralQuantityRecord> Records)>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (!QuantitySignificance.IsValidMeasurement(record.Measurement.RawValue)) continue;
            var source = NormalizeSource(record.Source.Xref);
            var layer = SectionProjectionLogic.LayerLeaf(record.Source.Layer);
            var kind = (record.Measurement.Kind ?? string.Empty).Trim().ToLowerInvariant();
            var unit = (record.Measurement.Unit ?? string.Empty).Trim();
            var cls = MethodClass(kind, record.Measurement.Method);
            record.Measurement.Parameters.TryGetValue("cad_block_name_effective", out var rawBlock);
            var block = kind == "count" && !string.IsNullOrWhiteSpace(rawBlock) ? SectionProjectionLogic.LayerLeaf(rawBlock) : string.Empty;
            var id = RecognitionGroupId(source, layer, kind, unit, cls, block);
            if (!map.TryGetValue(id, out var entry))
                map[id] = entry = (new RecognitionGroupInput(id, source, SourceRole(source, library), layer, kind, unit, cls,
                    block.Length == 0 ? null : block, Array.Empty<NeutralQuantityRecord>()), new List<NeutralQuantityRecord>());
            entry.Records.Add(record);
        }
        return map.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value.Head with { Records = pair.Value.Records }).ToList();
    }

    public static string RecognitionGroupId(string source, string layer, string kind, string unit, string methodClass, string block) =>
        string.Join("|", "rg", source.ToUpperInvariant(), layer.ToUpperInvariant(), kind, unit, methodClass, block.ToUpperInvariant());

    /// <summary>The builder's measurement-basis rule, shared with recognition and family decisions.</summary>
    public static bool BasisAcceptsMeasurement(DraftQuantityBasis basis, string kind, string methodClass) =>
        BasisAccepts(basis, new DraftBaseGroup(string.Empty, DraftSourceRole.Design, string.Empty, null, kind, string.Empty, methodClass,
            0, 0, null, DraftLayerRole.Design, string.Empty));

    private static void AddFamilyDecisionWarnings(IReadOnlyList<FamilyResolution> resolutions, IReadOnlyList<RecognitionGroupInput> groups,
        EngineerBoqLibrary library, int assignedGroups, List<DraftWarning> warnings)
    {
        if (resolutions.Count == 0) return;
        var byId = groups.ToDictionary(g => g.GroupId, StringComparer.Ordinal);
        string Family(string? id) => library.Rules.FirstOrDefault(r => r.Id == id)?.Element ?? Bidi.Ltr(id ?? "?");
        foreach (var stale in resolutions.Where(r => r.State == FamilyDecisionState.Stale)
                     .GroupBy(r => (r.DecisionId, r.StaleReason, r.FamilyId)).OrderBy(g => g.Key.DecisionId, StringComparer.Ordinal))
        {
            var layers = stale.Select(r => byId.TryGetValue(r.GroupId, out var g) ? g.LayerLeaf : r.GroupId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).Select(Bidi.Ltr);
            var reason = stale.Key.StaleReason switch
            {
                "library-missing" => "המשפחה אינה בספרייה הפעילה",
                "library-changed" => "ההחלטה ניתנה בספריית תחום אחר (כבישים / נוף) — נדרשות סריקה ובדיקה מחודשות",
                "rule-changed" => "הגדרת הכלל בספרייה השתנתה מאז האישור",
                "basis" => "סוג המדידה אינו מתאים לכלל של המשפחה",
                "scope" => "המקור אינו בתחולת ההחלטה",
                "evidence" => "הראיות שעליהן התבסס האישור השתנו",
                "role" => "הקבוצה אינה עבודה חדשה בתכנון עכשיו (מקור מדידה או תשתית קיימת, שכבת עזר, או כמות חריגה), וההחלטה לא חלה עליה",
                "contradicted" => "ראיה חדשה בשרטוט מצביעה על משפחה אחרת מזו שאושרה",
                "approved-item" => "לשכבה יש שיוך סעיף מאושר, והוא קודם להחלטת המשפחה",
                "conflict" => "שתי החלטות תקפות מצביעות על משפחות שונות",
                _ => "ההחלטה אינה תואמת עוד את הקבוצה",
            };
            warnings.Add(new DraftWarning("החלטת משפחה לא בתוקף",
                $"{stale.Count()} קבוצות ({string.Join(", ", layers)}) מכוסות בהחלטה למשפחה '{Family(stale.Key.FamilyId)}' " +
                $"של {stale.First().ApprovedBy}, אבל {reason}. ההחלטה נשמרה בפרופיל ולא הוחלה, והקבוצות לא נכללו מכוחה.",
                Family(stale.Key.FamilyId), "לבדוק את הקבוצות ולאשר מחדש בחלון זיהוי השכבות.", true));
        }
        if (assignedGroups > 0)
            warnings.Add(new DraftWarning("שיוך לפי החלטות משפחה",
                $"{assignedGroups} קבוצות מדידה שויכו לכללי הספרייה לפי החלטות משפחה שמורות בפרופיל (ללא תלות בשם השכבה). " +
                "אישור משפחה אינו מאשר את סעיפי המתכון — הם נשארים הצעה.", "לידיעה", "—", false));
    }

    public static string DisplaySource(string source) =>
        string.IsNullOrWhiteSpace(source) || source == HostSource ? "הקובץ הראשי" : source;

    private static string NormalizeSource(string? xref)
    {
        var value = (xref ?? string.Empty).Trim();
        return value.Length == 0 || value.Equals("(host)", StringComparison.OrdinalIgnoreCase) ? HostSource : value;
    }

    private sealed class Accumulator
    {
        public int Count;
        public double Quantity;
        public Dictionary<string, int> Blocks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public double DrawnLength;
        public Dictionary<string, int> Linetypes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int ApprovedRecords;
        public string? ApprovedCode;
        public string? ApprovedBy;
        public bool ApprovedConflict;
    }

    private sealed record Failure(string Source, string Layer, string Entity, string Method, int Count);

    /// <summary>The identity of one measured group (a bucket of records).</summary>
    private readonly record struct GroupKey(string Source, string Layer, string Block, string Kind, string Unit, string Class, string ExcludedBy,
        string FamilyPartitionId = "")
    {
        public string Id => string.Join("|", Source, Layer, Block, Kind, Unit, Class, ExcludedBy) +
                            (FamilyPartitionId.Length == 0 ? string.Empty : "|" + FamilyPartitionId);
    }

    /// <summary>
    /// Which measured group a located failure belongs to (lower is preferred; −1 = never). A hatch failure
    /// belongs to hatch areas, a closed polyline to closed classes, anything else to open lines.
    /// </summary>
    private static int FailureRank(Failure failure, string methodClass)
    {
        var hatch = failure.Method.StartsWith("hatch", StringComparison.OrdinalIgnoreCase) ||
                    failure.Entity.Equals("Hatch", StringComparison.OrdinalIgnoreCase);
        var closed = failure.Method.StartsWith("closed-polyline", StringComparison.OrdinalIgnoreCase);
        return hatch ? (methodClass == "hatch" ? 0 : -1)
            : closed ? methodClass switch { "closed-perimeter" => 0, "closed-polyline" => 1, _ => -1 }
            : methodClass switch { "open" => 0, "open-w10" => 1, "open-w15" => 2, "open-width-unproven" => 3, "painted-width" => 4, _ => -1 };
    }

    private static bool SameSource(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Matches(DraftRule rule, DraftBaseGroup group) =>
        EngineerBoqLibrary.AnyGlob(group.Layer, rule.LayerPatterns) &&
        (rule.BlockPatterns == null || (group.Block != null && EngineerBoqLibrary.AnyGlob(group.Block, rule.BlockPatterns)));

    /// <summary>
    /// Complete measurement failures (no emitted record). Lines with provenance are located by
    /// source and layer; the aggregate title count keeps any unlocated remainder visible.
    /// Scoped aggregates (with affected records) are measured records with incomplete evidence.
    /// </summary>
    private static List<Failure> ParseFailures(IReadOnlyList<DeliveryFinding> findings, out int unlocated, out int scopedEvidence)
    {
        var counts = new Dictionary<(string, string, string, string), int>();
        unlocated = 0;
        scopedEvidence = 0;
        foreach (var finding in findings.Where(f => f.Code == EstimateFindingCodes.MeasurementFailed))
        {
            var titleCount = LeadingCount.Match(finding.Title ?? string.Empty) is { Success: true } m &&
                             int.TryParse(m.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            if ((finding.AffectedRecordIds?.Count ?? 0) > 0)
            {
                scopedEvidence += Math.Max(titleCount, 1);
                continue;
            }
            var parsed = 0;
            foreach (Match match in FailureLine.Matches(finding.Message ?? string.Empty))
            {
                var key = (NormalizeSource(match.Groups["xref"].Value), SectionProjectionLogic.LayerLeaf(match.Groups["layer"].Value.Trim()),
                    match.Groups["entity"].Value.Trim(), match.Groups["method"].Value.Trim());
                counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
                parsed++;
            }
            unlocated += Math.Max(0, (titleCount > 0 ? titleCount : parsed == 0 ? 1 : parsed) - parsed);
        }
        return counts.Select(pair => new Failure(pair.Key.Item1, pair.Key.Item2, pair.Key.Item3, pair.Key.Item4, pair.Value)).ToList();
    }

    /// <summary>Overlap pairs per measured group, resolved through the records each finding names.</summary>
    private static Dictionary<GroupKey, int> ParseOverlaps(IReadOnlyList<DeliveryFinding> findings,
        IReadOnlyDictionary<string, GroupKey> records)
    {
        var result = new Dictionary<GroupKey, int>();
        foreach (var finding in findings.Where(f => f.Code == EstimateFindingCodes.OverlapRisk))
        {
            var pairs = OverlapPairsInTitle.Match(finding.Title ?? string.Empty) is { Success: true } m &&
                        int.TryParse(m.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
            foreach (var key in (finding.AffectedRecordIds ?? new List<string>()).Where(records.ContainsKey).Select(id => records[id]).Distinct())
                result[key] = (result.TryGetValue(key, out var c) ? c : 0) + pairs;
        }
        return result;
    }

    private static void AddElementWarnings(DraftElement element, List<DraftWarning> warnings)
    {
        var rule = element.Rule;
        if (element.UnmeasuredObjects > 0)
            warnings.Add(new DraftWarning("כמות חסרה",
                $"לא נמדדו {element.UnmeasuredObjects} עצמים בשכבות הרכיב, ולכן הכמות נמוכה מהמשורטט.",
                rule.DisplayName,
                "לתקן את העצמים בקובץ המקור (" + string.Join(", ", element.Sources.Where(s => s.UnmeasuredObjects > 0)
                    .Select(s => Bidi.Ltr(DisplaySource(s.Group.Source))).Distinct()) + ") ולסרוק מחדש.", true));
        if (element.OverlapPairs > 0)
            warnings.Add(new DraftWarning("חפיפה אפשרית",
                $"הסריקה מצאה {element.OverlapPairs} זוגות עצמים בשכבות הרכיב שהמלבנים החוסמים שלהם חופפים. ייתכן שאותו שטח שורטט פעמיים.",
                rule.DisplayName, "לבדוק בשרטוט; אם יש כפילות — לתקן את השרטוט או להפחית בגיליון מדידות בסיס.", true));
        if (element.Sources.Any(s => !s.Included) && rule.IncludedByDefault)
            warnings.Add(new DraftWarning("מודלים נוספים",
                "הרכיב מופיע ביותר ממודל אחד. נכלל רק המודל הראשי; המקורות הנוספים מופיעים בגיליון 'מדידות בסיס' עם 0 בעמודת 'לכלול'.",
                rule.DisplayName, "לשנות ל-1 רק אם זו עבודה נפרדת ולא העתק.", true));
        if (!rule.IncludedByDefault)
            warnings.Add(rule.Id.StartsWith("profile-approved:", StringComparison.Ordinal)
                ? new DraftWarning("גבול הצללה לא נוסף",
                    $"לשכבה {Bidi.Ltr(rule.LayerPatterns[0])} יש שיוך מאושר, ובאותה שכבה יש הצללות; הפוליליינים הסגורים ({Number(element.Sources.Sum(s => s.Group.Quantity))} {element.Unit}) הם כנראה גבולותיהן ולא נוספו.",
                    rule.DisplayName, "לא להוסיף, אלא אם בדיקה בשרטוט מראה שזה שטח נפרד.", false)
                : rule.HeldBackNote != null
                    // A candidate held back for a decision (landscape): the reason is the decision, not a suspected double drawing.
                    ? new DraftWarning("מועמד שלא אושר", rule.HeldBackNote, rule.DisplayName,
                        "החלטה הנדסית על הפריטים. שינוי 'לכלול' ל-1 בגיליון מדידות בסיס מכליל את כל פריטי המתכון כתרחיש — אינו אישור מתועד.", true)
                    : new DraftWarning("לא נכלל עד בדיקה", rule.Note, rule.DisplayName,
                        "לבדוק בשרטוט; אם אלה אבנים נפרדות — לשנות 'לכלול' ל-1 בגיליון מדידות בסיס.", true));
        var included = element.Sources.Where(s => s.Included).ToList();
        var mixedOutline = included.GroupBy(s => s.Group.Layer, StringComparer.OrdinalIgnoreCase)
            .Any(g => g.Any(s => s.Group.MethodClass == "closed-perimeter") && g.Any(s => s.Group.MethodClass.StartsWith("open", StringComparison.Ordinal)));
        if (rule.Id.EndsWith("@unproven", StringComparison.Ordinal))
            warnings.Add(new DraftWarning("רוחב משורטט לא מוכח",
                $"{Number(element.IncludedQuantity)} מ' קווי סימון בשכבות {string.Join(", ", element.Sources.Select(s => Bidi.Ltr(s.Group.Layer)).Distinct())} " +
                "הם בעלי רוחב משורטט, ואין הוכחה שהרוחב הוא רוחב במארח במטרים (התמרת XREF, יחידות, או רוחב שלא נקרא או אינו אחיד). הרוחב לא הוחל והקווים לא תומחרו.",
                rule.DisplayName, "לסרוק מחדש בגרסה שאוספת את התמרת ה-XREF, או לבדוק את הרוחב ואת היחידות בשרטוט ולתמחר ידנית.", true));
        if (rule.Id.Contains("@w", StringComparison.Ordinal) &&
            (rule.Id.StartsWith("marking-transverse", StringComparison.Ordinal) || rule.Id.StartsWith("marking-crossings", StringComparison.Ordinal)))
            warnings.Add(new DraftWarning("קווים רוחביים דקים",
                $"{Number(element.IncludedQuantity)} מ' של סימון רוחבי משורטטים ברוחב {(rule.Id.EndsWith("@w15", StringComparison.Ordinal) ? "15" : "10")} ס\"מ ותומחרו כקו לפי מטר. " +
                "קווי עצירה ומעברי חצייה הם בדרך כלל רחבים יותר — ייתכן שזה רוחב שרטוטי בלבד.",
                rule.DisplayName, "לבדוק בשרטוט; אם זה קו עצירה רחב — לתמחר לפי שטח (אורך × רוחב אמיתי) בשורה U51.32.0290.", true));
        if (element.Sources.Count(s => s.Included) > 1)
            warnings.Add(new DraftWarning("מקורות שחוברו",
                $"הכמות מחברת {element.Sources.Count(s => s.Included)} מקורות (שכבות/שיטות מדידה): " +
                string.Join("; ", element.Sources.Where(s => s.Included).Select(s => $"{Bidi.Ltr(s.Group.Layer)} {MethodLabel(s.Group.MethodClass)} {Number(s.Group.Quantity)}")) + ".",
                rule.DisplayName,
                mixedOutline
                    ? "באותה שכבה חוברו קווים פתוחים והיקפי פוליליינים סגורים — לוודא בשרטוט שאותה אבן אינה משורטטת פעמיים (קו וגם מתאר)."
                    : "לוודא שאין כפילות בין המקורות.", mixedOutline));
    }

    private static void AddPlausibilityChecks(IReadOnlyList<DraftElement> elements, List<DraftWarning> warnings)
    {
        double Sum(params string[] ids) => elements.Where(e => ids.Contains(BaseRuleId(e.Rule))).Sum(e => e.IncludedQuantity);
        var paving = Sum("road-pavement", "brt-pavement");
        var curbs = Sum("curb-road", "curb-island", "curb-island-hw", "curb-garden", "curb-lowered", "curb-bike");
        if (paving > 0 && curbs > 0)
            warnings.Add(new DraftWarning("בדיקת סבירות — אבני שפה",
                $"יש {Number(curbs)} מ' אבני שפה ל-{Number(paving)} מ\"ר מיסעה ({(paving / curbs).ToString("0.0", CultureInfo.InvariantCulture)} מ\"ר למטר). " +
                "באומדן הייחוס היחס היה כ-5 מ\"ר למטר. יחס נמוך מרמז על שרטוט כפול של אבני שפה.",
                "פרק אבני שפה", "לבדוק בשרטוט שאבני השפה משורטטות בקו אחד.", true));
        var poles = elements.FirstOrDefault(e => BaseRuleId(e.Rule) == "sign-plates");
        var faces = elements.FirstOrDefault(e => BaseRuleId(e.Rule) == "sign-faces");
        if (poles != null && faces != null && Math.Abs(poles.IncludedQuantity - faces.IncludedQuantity) > 0.5)
            warnings.Add(new DraftWarning("בדיקת סבירות — תמרורים",
                $"נספרו {Number(poles.IncludedQuantity)} עמודי תמרורים ו-{Number(faces.IncludedQuantity)} סמלי תמרורים. השלטים תומחרו לפי מספר העמודים.",
                "פרק שילוט", "לבדוק כמה שלטים יש על כל עמוד.", true));
    }

    internal static string MethodClass(string kind, string? method)
    {
        var m = (method ?? string.Empty).Trim().ToLowerInvariant();
        return kind switch
        {
            "area" when m.StartsWith("hatch", StringComparison.Ordinal) => "hatch",
            "area" when m.StartsWith("closed-polyline", StringComparison.Ordinal) => "closed-polyline",
            "area" => "other",
            "length" when m.StartsWith("closed-polyline-perimeter", StringComparison.Ordinal) => "closed-perimeter",
            "length" => "open",
            "count" => "block",
            "volume" when m.StartsWith("corridor", StringComparison.Ordinal) => "corridor",
            _ => "other",
        };
    }

    public static DraftSourceRole SourceRole(string source, EngineerBoqLibrary library)
    {
        if (string.IsNullOrWhiteSpace(source) || source == HostSource) return DraftSourceRole.Host;
        if (EngineerBoqLibrary.AnyGlob(source, library.SurveySourcePatterns)) return DraftSourceRole.Survey;
        if (EngineerBoqLibrary.AnyGlob(source, library.UtilitySourcePatterns)) return DraftSourceRole.ExistingUtilities;
        return DraftSourceRole.Design;
    }

    private static string SourceMeaning(DraftSourceRole role, EngineerBoqLibrary library) => role switch
    {
        DraftSourceRole.Survey => "תכנית מדידה (מצב קיים). לא נכללת בכתב הכמויות לעבודות חדשות; מפורטת בגיליון 'מצב קיים ותשתיות'.",
        DraftSourceRole.ExistingUtilities => $"תשתיות קיימות. לא נכללות {library.Texts.BillName}; מפורטות בגיליון 'מצב קיים ותשתיות'.",
        DraftSourceRole.Host => "הקובץ הראשי — כל שכבה סווגה בנפרד (תכנון / מצב קיים / תשתית / עזר).",
        _ => "מודל תכנון — עבודה חדשה. השכבות שלו מוצעות לכתב הכמויות.",
    };

    internal static DraftLayerRole LayerRole(DraftSourceRole sourceRole, string layer, string kind, string methodClass,
        string unit, double quantity, int count, IReadOnlyList<string> clPatterns, EngineerBoqLibrary library, out string reason)
    {
        if (kind == "volume" && methodClass == "corridor")
        {
            reason = "נפח חומר מקורידור (כיסוי חלקי) — לבדיקת מבנה המיסעה בלבד";
            return DraftLayerRole.CorridorVolume;
        }
        if (sourceRole == DraftSourceRole.Survey) { reason = "תכנית מדידה — מצב קיים"; return DraftLayerRole.Existing; }
        if (sourceRole == DraftSourceRole.ExistingUtilities) { reason = "מקור תשתיות קיימות"; return DraftLayerRole.Utility; }
        if (EngineerBoqLibrary.AnyGlob(layer, clPatterns))
        {
            reason = "שכבת קווי החתך של הפרויקט";
            return DraftLayerRole.DraftingAid;
        }
        var verdict = QuantitySignificance.Classify(new QuantitySignificance.Group($"layer:{layer}|{kind}", layer, unit, quantity, count));
        switch (verdict.Kind)
        {
            case QuantitySignificance.Kind.StationGeometry:
            case QuantitySignificance.Kind.Auxiliary:
            case QuantitySignificance.Kind.ImplausibleMagnitude:
                reason = verdict.Reason;
                return DraftLayerRole.DraftingAid;
            case QuantitySignificance.Kind.ExistingUtility:
                reason = verdict.Reason;
                return DraftLayerRole.Utility;
        }
        if (EngineerBoqLibrary.AnyGlob(layer, library.DraftingAidLayerPatterns))
        {
            reason = "שכבת עזר / טקסט / סימון שרטוט";
            return DraftLayerRole.DraftingAid;
        }
        if (EngineerBoqLibrary.AnyGlob(layer, library.ExistingLayerPatterns))
        {
            reason = "שם השכבה מציין מצב קיים";
            return DraftLayerRole.Existing;
        }
        var utility = SectionProjectionLogic.Classify(layer, null, Array.Empty<SectionProjectionLogic.ProjectionRuleConfig>());
        if (utility is { Kind: "utility" })
        {
            // A utility name alone does not make it existing (BIUV315 in a design model is new work).
            reason = $"תשתית בתכנון ({utility.Label}) — מחוץ ל{library.Texts.LibraryName}; לתמחר בכתב הכמויות של המערכת";
            return DraftLayerRole.DesignUtility;
        }
        reason = string.Empty;
        return DraftLayerRole.Design;
    }

    /// <summary>Constant polyline width in metres, only when the source database is in metres.</summary>
    private static readonly Regex InchToken = new(@"(?<![0-9])(?<n>[0-9]{1,2})\s*Z(?![A-Z])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex MillimetreToken = new(@"(?<![0-9])(?<n>[1-9][0-9]{2,3})(?![0-9])", RegexOptions.CultureInvariant);

    private static readonly Regex NameNumber = new(@"(?<![0-9])[1-9][0-9]{1,3}(?![0-9])", RegexOptions.CultureInvariant);

    /// <summary>A catalog description, normalised and cut for a warning line.</summary>
    private static string Short(string text)
    {
        var value = CatalogTextIdentity.Normalize(text);
        return value.Length <= 60 ? value : value[..60] + "…";
    }

    /// <summary>
    /// A size the layer or block name suggests. Shown, never priced. Only an explicit inch mark ("8Z") or a
    /// 3–4 digit number in a non-DR layer name (BIUV315 = 315 mm) carries a unit. A number in a block name
    /// (shuha140_140, DR-MNHL_120-100) or in a DR-model name (DR-PIPE-40) is shown as written, without a unit.
    /// </summary>
    internal static string? DiameterHint(string layer, string? block)
    {
        if (!string.IsNullOrWhiteSpace(block))
        {
            if (InchToken.Match(block) is { Success: true } inch) return $"קוטר {inch.Groups["n"].Value} צול";
            if (UnitlessSize(block) is { } size) return size;
        }
        if (EngineerBoqLibrary.IsDrainageModelName(layer)) return UnitlessSize(layer);
        if (InchToken.Match(layer) is { Success: true } layerInch) return $"קוטר {layerInch.Groups["n"].Value} צול";
        if (MillimetreToken.Match(layer) is { Success: true } mm) return $"קוטר {mm.Groups["n"].Value} מ\"מ";
        return null;

        static string? UnitlessSize(string name)
        {
            var numbers = NameNumber.Matches(name).Select(m => m.Value).ToArray();
            return numbers.Length == 0 ? null : $"מידה {string.Join("/", numbers)} — יחידה לא מצוינת";
        }
    }

    /// <summary>Blocks our own tools insert (profile projections, marks) are never construction quantities.</summary>
    internal static bool IsOwnToolBlock(string? block) =>
        !string.IsNullOrWhiteSpace(block) &&
        (block.StartsWith("Mahod_", StringComparison.OrdinalIgnoreCase) || block.StartsWith("MHD_", StringComparison.OrdinalIgnoreCase) ||
         block.StartsWith("MHD-", StringComparison.OrdinalIgnoreCase));

    internal enum DrawnWidthState { Absent, Proven, Unproven }

    /// <summary>
    /// What the scan says about a polyline's drawn width. Absent: not a width-carrying entity, or its widths were read
    /// and are zero. Proven: one uniform width in a metre drawing. Unproven: a width exists but is varying, implausible,
    /// in another unit, or could not be read — never the same as "no width".
    /// </summary>
    internal static DrawnWidthState ReadDrawnWidth(IReadOnlyDictionary<string, string> parameters, out double width)
    {
        width = 0;
        double? Read(string key) =>
            parameters.TryGetValue(key, out var raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
            double.IsFinite(v) ? v : null;
        var widthCarrying = parameters.ContainsKey("cad_width_units") ||
                            parameters.Keys.Any(k => k.StartsWith("cad_polyline_", StringComparison.Ordinal));
        if (!widthCarrying) return DrawnWidthState.Absent;
        var constant = Read("cad_polyline_constant_width_raw");
        var min = Read("cad_polyline_width_min_raw");
        var max = Read("cad_polyline_width_max_raw");
        var positive = constant is > 0.001 || max is > 0.001;
        if (!positive)
            // Zero widths that were actually read mean "no drawn width"; nothing read means unknown, not absent.
            return constant != null || max != null ? DrawnWidthState.Absent : DrawnWidthState.Unproven;
        if (DrawnWidth(parameters) is { } proven)
        {
            width = proven;
            return DrawnWidthState.Proven;
        }
        return DrawnWidthState.Unproven;
    }

    internal static double? DrawnWidth(IReadOnlyDictionary<string, string> parameters)
    {
        // b24: drawn widths count only when one drawing unit is one physical metre — the scan's recorded factor
        // (a unit decision included), or the raw "Meters" of a scan written before b24.
        if (QuantityPhysicalUnits.MetresPerUnit(parameters, declaredMetres: false) != 1.0)
            return null;
        double? Read(string key) =>
            parameters.TryGetValue(key, out var raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
            double.IsFinite(v) ? v : null;
        var width = Read("cad_polyline_constant_width_raw");
        if (width == null && Read("cad_polyline_width_min_raw") is { } min && Read("cad_polyline_width_max_raw") is { } max &&
            Math.Abs(max - min) <= 1e-6)
            width = min;
        return width is { } w && w > 0.001 && w <= 20 ? w : null;
    }

    internal static string? EffectiveLinetype(IReadOnlyDictionary<string, string> parameters)
    {
        parameters.TryGetValue("cad_entity_linetype", out var entity);
        parameters.TryGetValue("cad_layer_linetype", out var layer);
        var value = string.IsNullOrWhiteSpace(entity) || entity.Trim() is var e &&
                    (e.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) || e.Equals("ByBlock", StringComparison.OrdinalIgnoreCase))
            ? layer : entity;
        if (string.IsNullOrWhiteSpace(value)) return null;
        var leaf = SectionProjectionLogic.LayerLeaf(value.Trim());
        return leaf.Length == 0 ? null : leaf;
    }

    private static bool IsDashed(string? linetype) =>
        !string.IsNullOrWhiteSpace(linetype) &&
        !linetype.Equals("Continuous", StringComparison.OrdinalIgnoreCase) &&
        !linetype.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) &&
        !linetype.Equals("ByBlock", StringComparison.OrdinalIgnoreCase);

    private static string WidthClass(DraftBaseGroup group) => group.MethodClass switch
    {
        "painted-width" => "area",
        "open-w10" => "w10",
        "open-w15" => "w15",
        "open-width-unproven" => "unproven",
        _ => string.Empty,
    };

    private static int WidthOrder(string widthClass) => widthClass switch { "" => 0, "w10" => 1, "w15" => 2, "area" => 3, _ => 4 };

    /// <summary>
    /// A width-aware marking rule applied to one drawn-width class: 10 cm and 15 cm lines keep a
    /// per-metre item of that width; wider lines are painted area (the drawn width replaces any
    /// width parameter, a paint-ratio parameter stays).
    /// </summary>
    private static DraftRule WidthRule(DraftRule rule, string widthClass, EngineerBoqLibrary library)
    {
        if (widthClass.Length == 0) return rule;
        if (widthClass == "unproven")
            return rule with
            {
                Id = $"{rule.Id}@unproven",
                Variant = "רוחב משורטט לא מוכח — לא תומחר",
                Confidence = DraftConfidence.Decision,
                Emits = Array.Empty<DraftEmit>(),
                Note = rule.Note + " לקווים האלה יש רוחב משורטט, אבל אין הוכחה שהוא רוחב במארח במטרים: התמרת ה-XREF לא נאספה או אינה של הרשומה, " +
                       "יחידות השרטוט אינן מטרים, או שהרוחב לא נקרא או אינו אחיד. הם לא תומחרו ולא הומרו לפרמטר.",
                SplitByDrawnWidth = false,
            };
        var widthKeys = new HashSet<string>(library.WidthParameterKeys, StringComparer.Ordinal);
        string? Keep(string? key) => key != null && !widthKeys.Contains(key) ? key : null;
        IEnumerable<DraftEmit> Map(DraftEmit emit)
        {
            var hasWidthParameter = emit.ParameterKeys.Any(widthKeys.Contains);
            var ratios = emit.ParameterKeys.Where(k => !widthKeys.Contains(k)).ToList();
            switch (widthClass)
            {
                case "area":
                    yield return new DraftEmit(EngineerBoqLibrary.PaintedAreaCode, emit.Factor,
                        ratios.ElementAtOrDefault(0), "שטח צבוע = אורך × רוחב משורטט" + (ratios.Count > 0 ? " × חלק צבוע" : string.Empty),
                        ratios.ElementAtOrDefault(1));
                    break;
                default:
                            var label = widthClass == "w15" ? "15" : "10";
                    if (hasWidthParameter)
                        yield return new DraftEmit(widthClass == "w15" ? EngineerBoqLibrary.Line15Code : EngineerBoqLibrary.Line10Code,
                            emit.Factor, Keep(emit.ParameterKey), $"קו ברוחב {label} ס\"מ לפי השרטוט, לפי מטר",
                            Keep(emit.SecondParameterKey), emit.ComplementKey);
                    else
                        yield return emit with
                        {
                            Code = widthClass == "w15" && emit.Code == EngineerBoqLibrary.Line10Code ? EngineerBoqLibrary.Line15Code : emit.Code,
                            Note = (emit.Note == null ? string.Empty : emit.Note + "; ") + $"רוחב {label} ס\"מ לפי השרטוט",
                        };
                    break;
            }
        }
        var variant = widthClass switch
        {
            "area" => "קו רחב, לפי שטח (אורך × רוחב משורטט)",
            "w15" => "רוחב 15 ס\"מ לפי השרטוט",
            _ => "רוחב 10 ס\"מ לפי השרטוט",
        };
        var thinWidthLines = widthClass != "area" && rule.Emits.Any(e => e.ParameterKeys.Any(widthKeys.Contains));
        return rule with
        {
            Id = $"{rule.Id}@{widthClass}",
            Variant = variant,
            Note = thinWidthLines ? rule.Note + " קווים שמשורטטים ברוחב 10/15 ס\"מ תומחרו לפי מטר, עד בדיקה אם זה רוחב שרטוטי בלבד." : rule.Note,
            Emits = rule.Emits.SelectMany(Map).ToList(),
            SplitByDrawnWidth = false,
        };
    }

    /// <summary>The library rule a derived rule came from (width variants '@…', approved variants '+approved:…').</summary>
    internal static string BaseRuleId(DraftRule rule)
    {
        var id = rule.Id;
        var cut = id.IndexOfAny(new[] { '+', '@' });
        return cut < 0 ? id : id[..cut];
    }

    /// <summary>
    /// The recipe line an approved item replaces, or the reason none can. First the line whose code the active list
    /// resolves (itself or an approved edition link) in the item's sub-chapter and unit — as before. byc (02.10, Codex
    /// 22:24): when no such line exists, a line the active edition does not list may still be replaced, but only on its
    /// own proven reference (LibraryItemReferences: the description and unit in the list the library was written on) —
    /// same sub-chapter, same unit, and exactly one such line. No edition equivalence is inferred and nothing is linked;
    /// a missing reference, another unit or more than one candidate is refused with that reason.
    /// </summary>
    private static (DraftEmit? Target, string? Refusal) ApprovedTarget(DraftRule rule, string code, CatalogSnapshot catalog,
        CatalogItem approvedItem)
    {
        var (_, subChapter) = ChapterKeys(code);
        var listedTarget = rule.Emits.FirstOrDefault(e => catalog.ResolveLibraryCode(e.Code) is { } listed &&
            ChapterKeys(listed).SubChapter == subChapter &&
            catalog.Items.TryGetValue(listed, out var item) && item.Unit.SameUnit(approvedItem.Unit));
        if (listedTarget != null) return (listedTarget, null);
        var sameSubChapter = rule.Emits.Where(e => catalog.ResolveLibraryCode(e.Code) == null &&
                                                   ChapterKeys(e.Code).SubChapter == subChapter).ToList();
        if (sameSubChapter.Count == 0)
            return (null, $"אין במתכון שורה בתת פרק {subChapter} שאפשר להחליף");
        var referenced = sameSubChapter.Where(e => LibraryItemReferences.Items.ContainsKey(e.Code)).ToList();
        if (referenced.Count == 0)
            return (null, "לשורת המתכון אין תיאור ויחידה מוכחים מהמחירון שעליו נכתבה הספרייה");
        var sameUnit = referenced.Where(e => Units.Parse(LibraryItemReferences.Items[e.Code].Unit).SameUnit(approvedItem.Unit)).ToList();
        if (sameUnit.Count == 0)
            return (null, $"יחידת שורת המתכון ({LibraryItemReferences.Items[referenced[0].Code].Unit}) שונה מיחידת הפריט שאושר ({approvedItem.UnitRaw})");
        if (sameUnit.Count > 1)
            return (null, $"יותר משורה אחת במתכון מתאימה ({string.Join(", ", sameUnit.Select(e => e.Code))}) — אין בחירה אוטומטית");
        return (sameUnit[0], null);
    }

    /// <summary>Why an approved item replaces no recipe line (for the warning); null when it does.</summary>
    private static string? ApprovedVariantRefusal(DraftRule rule, string code, CatalogSnapshot catalog)
    {
        if (!catalog.Items.TryGetValue(code, out var approvedItem)) return $"הפריט {code} אינו במחירון הפעיל";
        if (rule.Emits.Count == 0) return null;
        return ApprovedTarget(rule, code, catalog, approvedItem).Refusal;
    }

    /// <summary>
    /// A library rule with an engineer-approved item: it replaces the recipe line of the same sub-chapter and
    /// unit, or becomes the single line of a rule that waited for a decision. Everything else about the rule —
    /// hold-back, note, primary model, width split — is kept. Null when no line can be replaced.
    /// </summary>
    private static DraftRule? ApprovedVariant(DraftRule rule, string code, CatalogSnapshot catalog,
        Dictionary<(string RuleId, string Code), DraftRule> variants, out string? replaced)
    {
        replaced = null;
        if (!catalog.Items.TryGetValue(code, out var approvedItem)) return null;
        var key = (rule.Id, code.ToUpperInvariant());
        IReadOnlyList<DraftEmit> emits;
        if (rule.Emits.Count == 0)
        {
            emits = new[] { new DraftEmit(code, Note: "פריט שאושר בפרופיל לשכבה") };
        }
        else
        {
            var (target, _) = ApprovedTarget(rule, code, catalog, approvedItem);
            if (target == null) return null;
            replaced = target.Code;
            var targetCode = target.Code;
            emits = rule.Emits.Select(e => ReferenceEquals(e, target)
                ? e with { Code = code, Note = $"פריט שאושר בפרופיל במקום {targetCode}" + (e.Note == null ? string.Empty : " · " + e.Note) }
                : e).ToList();
        }
        if (variants.TryGetValue(key, out var existing)) return existing;
        var variant = rule with { Id = $"{rule.Id}+approved:{code}", Emits = emits, Variant = $"שיוך מאושר {code}" };
        variants[key] = variant;
        return variant;
    }

    private static DraftQuantityBasis BasisFor(DraftBaseGroup group) => group.Kind switch
    {
        "area" => DraftQuantityBasis.HatchArea,
        "count" => DraftQuantityBasis.Count,
        _ => DraftQuantityBasis.LengthWithClosedPerimeters,
    };

    /// <summary>The draft's own name and basis test of a rule on a measured group (LibraryProposalGate's overlap check).</summary>
    internal static bool MatchesAndAccepts(DraftRule rule, DraftBaseGroup group) => Matches(rule, group) && Accepts(rule, group);

    private static bool Accepts(DraftRule rule, DraftBaseGroup group) =>
        BasisAccepts(rule.Basis, group) || (rule.SplitByDrawnWidth && group.MethodClass == "painted-width");

    private static bool IsOpenLine(string methodClass) => methodClass is "open" or "open-w10" or "open-w15" or "open-width-unproven";

    private static bool BasisAccepts(DraftQuantityBasis basis, DraftBaseGroup group) => basis switch
    {
        DraftQuantityBasis.HatchArea => group.Kind == "area" && group.MethodClass == "hatch",
        DraftQuantityBasis.LengthWithClosedPerimeters => group.Kind == "length" &&
                                                        (IsOpenLine(group.MethodClass) || group.MethodClass == "closed-perimeter"),
        DraftQuantityBasis.OpenLength => group.Kind == "length" && IsOpenLine(group.MethodClass),
        DraftQuantityBasis.Count => group.Kind == "count",
        _ => false,
    };

    private static string AlternativeReason(DraftRule rule, DraftBaseGroup group, bool hasHatch)
    {
        return rule.Basis switch
        {
            DraftQuantityBasis.HatchArea when group.Kind == "area" && group.MethodClass == "closed-polyline" && hasHatch =>
                "מדידה חלופית — שטח פוליליין סגור בשכבות של רכיב שיש לו הצללות (באותו מודל או באחר); כנראה גבול ההצללה או העתק שלה. לא להוסיף (ספירה כפולה)",
            DraftQuantityBasis.HatchArea when group.Kind == "area" =>
                "שטח שאינו משורטט כהצללה (פוליליין סגור) — לא נכלל; להחלטה אם זה שטח עבודה נוסף",
            DraftQuantityBasis.HatchArea => "מדידה חלופית — הרכיב נמדד לפי שטח הצללה; מדידת אורך/ספירה בשכבה זו לא נכללה. לא להוסיף",
            DraftQuantityBasis.LengthWithClosedPerimeters or DraftQuantityBasis.OpenLength when group.Kind == "area" && group.MethodClass == "hatch" =>
                "מדידה חלופית — הצללה בשכבת קווים; הרכיב נמדד לפי הקווים. לא להוסיף (ספירה כפולה)",
            DraftQuantityBasis.LengthWithClosedPerimeters or DraftQuantityBasis.OpenLength when group.Kind == "area" =>
                "מדידה חלופית — שטח של פוליליין סגור; הרכיב נמדד באורך. לא להוסיף (ספירה כפולה)",
            DraftQuantityBasis.OpenLength when group.MethodClass == "closed-perimeter" =>
                "מדידה חלופית — היקף פוליליין סגור; הרכיב נמדד לפי קווים פתוחים בלבד",
            DraftQuantityBasis.Count => "מדידה חלופית — הרכיב נספר לפי בלוקים; מדידת אורך/שטח בשכבה זו לא נכללה. לא להוסיף",
            _ => "מדידה חלופית באותה שכבה — לא נכללה",
        };
    }

    private static bool UnitCompatible(DraftElement element, DraftEmit emit, CatalogItem item)
    {
        var measured = Units.Parse(element.Unit);
        // m² → dunam only as the exact declared conversion (Codex 01:27, 02/10): measured m², item in dunam, factor 0.001.
        // Any other source or factor for a dunam item, and a dunam measurement, is not a conversion the library declares.
        // Checked before SameUnit (Codex 02:29): dunam → dunam with the 0.001 factor would otherwise convert twice.
        if (item.Unit.Canonical == "dunam" || measured.Canonical == "dunam")
            return measured.Canonical == "m2" && item.Unit.Canonical == "dunam" && emit.Factor == 0.001;
        if (measured.SameUnit(item.Unit)) return true;
        // One counted object is one complete unit (e.g. a shelter priced per "קומפ'").
        if (measured.Dimension == UnitDimension.Count && item.Unit.Dimension == UnitDimension.Compound && emit.ParameterKey == null)
            return true;
        // An explicit parameter or a width factor is the declared conversion: area × thickness → volume,
        // count × area-per-item → area, count × length-per-item → length, length × width → area.
        var declared = emit.ParameterKey != null || emit.SecondParameterKey != null || emit.Factor != 1.0;
        return declared && measured.Dimension switch
        {
            UnitDimension.Area => item.Unit.Dimension is UnitDimension.Volume or UnitDimension.Area,
            UnitDimension.Count => item.Unit.Dimension is UnitDimension.Area or UnitDimension.Length,
            UnitDimension.Length => item.Unit.Dimension is UnitDimension.Length or UnitDimension.Area,
            _ => false,
        };
    }

    /// <summary>U51.04.0080 → ("51", "51.04").</summary>
    internal static (string Chapter, string SubChapter) ChapterKeys(string code)
    {
        var bare = code.Length > 1 && char.IsLetter(code[0]) ? code[1..] : code;
        var parts = bare.Split('.');
        return parts.Length >= 2 ? (parts[0], parts[0] + "." + parts[1]) : (bare, bare);
    }

    private static int UnitRank(string unit) => Units.Parse(unit).Dimension switch
    {
        UnitDimension.Area => 0,
        UnitDimension.Length => 1,
        UnitDimension.Count => 2,
        UnitDimension.Volume => 3,
        _ => 4,
    };

    public static string KindLabel(string kind) => kind switch
    {
        "area" => "שטח",
        "length" => "אורך",
        "count" => "ספירה",
        "volume" => "נפח",
        _ => kind,
    };

    public static string MethodLabel(string methodClass) => methodClass switch
    {
        "hatch" => "הצללה",
        "closed-polyline" => "שטח פוליליין סגור",
        "closed-perimeter" => "היקף פוליליין סגור",
        "open" => "קווים",
        "open-w10" => "קווים ברוחב 10 ס\"מ",
        "open-w15" => "קווים ברוחב 15 ס\"מ",
        "open-width-unproven" => "קווים עם רוחב משורטט לא מוכח",
        "painted-width" => "אורך × רוחב משורטט",
        "block" => "בלוקים",
        "corridor" => "קורידור",
        _ => methodClass,
    };

    public static string ConfidenceLabel(DraftConfidence confidence) => confidence switch
    {
        DraftConfidence.Direct => "ישיר",
        DraftConfidence.Assumption => "הנחה — לאשר",
        DraftConfidence.Decision => "להחלטה הנדסית",
        _ => confidence.ToString(),
    };

    public static string LayerRoleLabel(DraftLayerRole role) => role switch
    {
        DraftLayerRole.Design => "תכנון",
        DraftLayerRole.Existing => "מצב קיים",
        DraftLayerRole.Utility => "תשתית קיימת",
        DraftLayerRole.DesignUtility => "תשתית בתכנון",
        DraftLayerRole.DraftingAid => "עזר שרטוט",
        DraftLayerRole.CorridorVolume => "נפח קורידור",
        DraftLayerRole.ExcludedByDecision => "הוחרג בהחלטה",
        _ => role.ToString(),
    };

    public static string SourceRoleLabel(DraftSourceRole role) => role switch
    {
        DraftSourceRole.Design => "מודל תכנון",
        DraftSourceRole.Survey => "תכנית מדידה",
        DraftSourceRole.ExistingUtilities => "תשתיות קיימות",
        DraftSourceRole.Host => "קובץ ראשי",
        _ => role.ToString(),
    };

    internal static string Number(double value) => value.ToString("#,##0.##", CultureInfo.InvariantCulture);
}
