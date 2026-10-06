using System.IO;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Xunit.Abstractions;
using Dialog = MahodAI.Civil3D.Plugin.CivilDelivery.UI.ManualMappingReviewDialog;
using Batch = MahodAI.Civil3D.Plugin.Tests.CivilDelivery.ManualMappingBatchDialogTests;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Opt-in full saved-run replay, NOT native acceptance. Original run/profile/catalog are
/// read-only. All synthetic approvals are confined to the explicit acceptance directory.
/// The 333 MB neutral-record file is deserialized exactly once per test process.
/// </summary>
public sealed class RealBoqSaved170520AcceptanceTests(ITestOutputHelper output)
{
    private const string Run = @"C:\Users\arthurf\AppData\Local\MahodAI_Civil3D\civil-delivery\runs\estimate-extract-20260924-170520-0aebd0fe";
    private const string LiveProfile = @"C:\Users\arthurf\AppData\Local\MahodAI_Civil3D\civil-delivery\profiles\6422\project-profile.yaml";
    private const string ProfileHash = "1938b55ad6ab1708a3726224cc4e509316f8bcf2ee58c8b5f96ebc0b41f1cc67";
    private const string RecordsHash = "810316eed8c78cd1aaeee565711d8408ed9c8895bd1423d4f166f6472f02c072";
    private const string CatalogHash = "90da59809602c4127fcf0b91c3f037c2b98d7b93288beba2c851e3a3550f313c";
    private const string First = "layer:HW-CURB|length", Second = "layer:HW-CURB-ILND|length";
    private const string WallPoint = "layer:S_WALL_BT|count|block:6422-SP-MEDVA-ALL-2026-MHD%7CS_POINT_KR";
    private const string PavingPoint = "layer:S_PAVED_PRIM|count|block:6422-SP-MEDVA-ALL-2026-MHD%7CS_POINT_N";
    private const string PavingLength = "layer:S_PAVED_PRIM|length";
    private const string Reviewer = "SYNTHETIC TEST ONLY - NOT ENGINEERING APPROVAL";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Lazy<Data> Saved = new(Load);

    public sealed class RealBoqFactAttribute : FactAttribute
    {
        public RealBoqFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MHD_BOQ_REAL_ACCEPTANCE_DIR")))
                Skip = "Explicit isolated full saved-run acceptance was not requested; no native coverage claimed.";
        }
    }

    private sealed record Data(string Root, List<NeutralQuantityRecord> Records,
        List<DeliveryFinding> Findings, EstimateWorkflowService.ScanResult Header,
        CatalogSnapshot Catalog, HashSet<string> ReferenceCodes, List<MappingProposal> OldProposals);

    private static Data Load()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("MHD_BOQ_REAL_ACCEPTANCE_DIR")!);
        var allowed = @"C:\Users\arthurf\Downloads\Cutz\Work\review-270926\boq-workflow\acceptance\";
        if (!root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe acceptance output root.");
        Directory.CreateDirectory(root);
        ArtifactHash.Sha256OfFile(LiveProfile).Should().Be(ProfileHash);
        ArtifactHash.Sha256OfFile(Path.Combine(Run, "neutral_quantity_records.json")).Should().Be(RecordsHash);
        var inputs = Path.Combine(root, "inputs"); Directory.CreateDirectory(inputs);
        foreach (var name in new[] { "project-profile.yaml", "nti-urban-082025.xlsx", "judgment2-golden.xlsx" })
            File.Copy(Path.Combine(Path.GetDirectoryName(LiveProfile)!, name), Path.Combine(inputs, name), false);
        List<NeutralQuantityRecord> records;
        using (var stream = File.OpenRead(Path.Combine(Run, "neutral_quantity_records.json")))
            records = JsonSerializer.Deserialize<List<NeutralQuantityRecord>>(stream, Json)!;
        records.Should().HaveCount(79180);
        records.Select(r => r.Classification.RuleKey).Distinct().Should().HaveCount(1031);
        var findings = JsonSerializer.Deserialize<List<DeliveryFinding>>(File.ReadAllText(Path.Combine(Run, "quantity_preflight.json")), Json)!;
        findings.Count(EstimatePreflightPolicy.IsBlocking).Should().Be(455);
        var headerText = string.Join("\n", File.ReadLines(Path.Combine(Run, "estimate_scan.json"))
            .TakeWhile(line => !line.TrimStart().StartsWith("\"Records\":", StringComparison.Ordinal))) + "\n\"Records\": []\n}";
        var header = JsonSerializer.Deserialize<EstimateWorkflowService.ScanResult>(headerText, Json)!;
        using var old = JsonDocument.Parse(File.ReadAllText(Path.Combine(Run, "mapping_proposals.json")));
        var proposals = old.RootElement.GetProperty("proposals").Deserialize<List<MappingProposal>>(Json)!;
        proposals.Should().HaveCount(682);
        var catalog = PriceBookXlsxLoader.Load(Path.Combine(inputs, "nti-urban-082025.xlsx"), "nti-urban-082025");
        catalog.FileHash.Should().Be(CatalogHash);
        var reference = ReferenceEstimateLoader.Load(Path.Combine(inputs, "judgment2-golden.xlsx"));
        return new(root, records, findings, header, catalog, reference.CatalogCodes.ToHashSet(), proposals);
    }

    private static (ProjectProfile Profile, string Target, EstimateWorkflowService.ScanResult Scan) Context(string name)
    {
        var data = Saved.Value; var directory = Path.Combine(data.Root, name); Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "SYNTHETIC-TEST-ONLY-project-profile.yaml");
        File.Copy(Path.Combine(data.Root, "inputs", "project-profile.yaml"), target, false);
        // Relative catalog resolution stays inside the isolated context too.
        File.Copy(Path.Combine(data.Root, "inputs", "nti-urban-082025.xlsx"), Path.Combine(directory, "nti-urban-082025.xlsx"), false);
        var profile = ProjectProfileLoader.LoadFromFile(target).Profile!;
        profile.Should().NotBeNull();
        var h = data.Header;
        return (profile, target, new()
        {
            RunId = "SYNTHETIC-REPLAY-170520-" + name, ProjectProfileId = profile.ProfileId,
            ProfileSource = target, ProjectProfileHash = ArtifactHash.Sha256OfFile(target),
            ProjectProfileEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(profile),
            ProfileWriteState = ProfileCasTest.For(profile, target), SourceDrawing = h.SourceDrawing,
            SourceDrawingHash = h.SourceDrawingHash, SourceDbMod = h.SourceDbMod,
            DatabaseRevision = "SAVED-RECORD-REPLAY-NOT-LIVE-FRESHNESS", PhysicalUnits = h.PhysicalUnits,
            PhysicalUnitConfigurationHash = h.PhysicalUnitConfigurationHash, ExternalSources = h.ExternalSources,
            Records = data.Records, Findings = data.Findings, DiscoveryMode = true,
            ScannedEntities = 81343, Status = DeliveryStatus.ReviewRequired,
        });
    }

    private static List<MappingProposal> CurrentProposals(Data data, ProjectProfile profile)
    {
        var groups = EstimateWorkflowService.BuildMappingProposalGroups(data.Records, MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.RoadsV1);
        return EstimateWorkflowService.CuratedRuleProposals(groups, data.Catalog, profile)
            .Concat(MappingProposalEngine.Propose(groups, data.Catalog, data.ReferenceCodes))
            .GroupBy(p => p.RuleKey, StringComparer.Ordinal)
            .SelectMany(g => g.GroupBy(p => p.ProposedCode, StringComparer.OrdinalIgnoreCase)
                .Select(c => c.OrderByDescending(p => p.Score).First())
                .OrderByDescending(p => p.Score).ThenBy(p => p.ProposedCode, StringComparer.Ordinal)
                .Take(MappingProposalEngine.MaxProposalsPerGroup)).ToList();
    }

    [RealBoqFact, Trait("Category", "RecordedRunReplay")]
    public void FullSavedRun_CurrentRecognitionAndProposalsNeverApproveOrEraseSourceEvidence()
    {
        var data = Saved.Value; var context = Context("recognition");
        var before = ArtifactHash.Sha256OfFile(context.Target);
        var groups = EstimateWorkflowService.BuildMappingProposalGroups(data.Records, MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.RoadsV1);
        var proposals = CurrentProposals(data, context.Profile);
        var recomputed = QuantitySignificance.RecomputeDecisionFindings(data.Records, data.Findings).ToList();
        foreach (var finding in data.Findings.Where(f => f.Code != EstimateFindingCodes.MixedDimensionLayer && f.Code != EstimateFindingCodes.QuantitySignificanceReview))
            recomputed.Should().Contain(f => ReferenceEquals(f, finding), "recognition may not erase source/coverage failures");
        data.Records.Should().OnlyContain(r => r.Classification.CandidateCatalogCode == null && r.Classification.MappingApprovedBy == null);
        ArtifactHash.Sha256OfFile(context.Target).Should().Be(before);
        var oldKeys = data.OldProposals.Select(p => p.RuleKey).ToHashSet();
        var keys = proposals.Select(p => p.RuleKey).ToHashSet();
        var recognition = groups.Select(g => new { g.RuleKey, g.Layer, g.ObjectCount,
            verdict = QuantitySignificance.Classify(new(g.RuleKey, g.Layer, g.MeasuredUnit, g.TotalQuantity, g.ObjectCount)) }).ToList();
        var evidence = new
        {
            scope = "Complete saved 89 neutral dataset replay; recognition/proposals only, no engineering approval or native scan",
            run = Path.GetFileName(Run), record_count = data.Records.Count, group_count = groups.Count,
            records_sha256 = RecordsHash, catalog_sha256 = CatalogHash, profile_sha256 = ProfileHash,
            original_blocking_findings = 455, original_findings_by_code = data.Findings.GroupBy(f => f.Code).ToDictionary(g => g.Key, g => g.Count()),
            current_blocking_findings = recomputed.Count(EstimatePreflightPolicy.IsBlocking), current_findings_by_code = recomputed.GroupBy(f => f.Code).ToDictionary(g => g.Key, g => g.Count()),
            old_proposals = 682, old_proposed_groups = oldKeys.Count,
            current_proposals = proposals.Count, current_proposed_groups = keys.Count,
            groups_without_proposal = groups.Count - keys.Count,
            wall_point_unapproved_records_retained = data.Records.Count(r => r.Classification.RuleKey == WallPoint && r.Classification.CandidateCatalogCode == null),
            wall_point_proposals_without_proven_asset_identity = proposals.Count(p => p.RuleKey == WallPoint),
            guarded_exact_groups = new[] { WallPoint, PavingPoint, PavingLength }.Select(key => new
            {
                rule_key = key, retained_records = data.Records.Count(r => r.Classification.RuleKey == key),
                retained_raw_quantity = data.Records.Where(r => r.Classification.RuleKey == key).Sum(r => r.Measurement.RawValue),
                proposals_without_proven_asset_identity = proposals.Count(p => p.RuleKey == key),
            }).ToArray(),
            recognition_by_kind = recognition.GroupBy(g => g.verdict.Kind.ToString()).ToDictionary(g => g.Key, g => new { groups = g.Count(), records = g.Sum(x => x.ObjectCount) }),
            removed_proposal_groups = oldKeys.Except(keys).ToArray(), added_proposal_groups = keys.Except(oldKeys).ToArray(),
            proven_automatic_batch_candidates = EstimateWorkflowService.ProvenBatchMappingCandidates(context.Scan, data.Catalog, context.Profile, proposals).Count,
            approved_records = 0, production_profile_unchanged = ArtifactHash.Sha256OfFile(LiveProfile) == ProfileHash,
        };
        Write(data, "recognition-summary.json", evidence);
        Write(data, "current-proposals.json", proposals);
        // Persist the original complete findings alongside recomputed, never overwrite the original run.
        Write(data, "original-findings.json", data.Findings); Write(data, "current-findings.json", recomputed);
        output.WriteLine(JsonSerializer.Serialize(evidence, Json));
        // Preserve the quantitative diagnostic evidence even when a proposal safety assertion fails.
        proposals.Should().NotContain(p => p.RuleKey == "layer:TR-MARK-WHT-812-BIKE|length" && p.ProposedCode == "U51.06.2930",
            "the actual installed profile's broad BIKE default must not turn a painted marking into a physical kerb proposal");
        proposals.Should().NotContain(p => p.RuleKey == WallPoint,
            "S_WALL_BT/S_POINT_KR has no positive asset identity or precise curated count rule; rejecting one wrong subject must not expose three new wall-mounted products");
        data.Records.Count(r => r.Classification.RuleKey == WallPoint).Should().Be(2415);
        proposals.Should().NotContain(p => p.RuleKey == PavingPoint || p.RuleKey == PavingLength,
            "the real paving-point count and generic surveyed paving line have no positive counted-asset or linear-edge identity; paving context alone is not a grate, sleeve or edge");
        data.Records.Count(r => r.Classification.RuleKey == PavingPoint).Should().Be(168);
        data.Records.Count(r => r.Classification.RuleKey == PavingLength).Should().Be(97);
    }

    private static Dialog.Group DialogGroup(string key)
    {
        var records = Saved.Value.Records.Where(r => r.Classification.RuleKey == key).ToList();
        var first = records[0];
        return new(key, first.Source.Layer!, first.Source.EntityType!, first.Measurement.Kind,
            first.Measurement.Unit, records.Sum(r => r.Measurement.RawValue), records.Count, null, null, null,
            "SYNTHETIC TEST ONLY: full saved source findings remain unresolved; mapping is not quantity/scope approval.", Array.Empty<MappingProposal>());
    }

    [RealBoqFact, Trait("Category", "RecordedRunReplay")]
    public void FullSavedRun_TwoExplicitDifferentCodesSaveOnceReopenReuseAndKeepMixedDimensionsAndAllSourceFailures()
    {
        var data = Saved.Value; var context = Context("explicit-batch");
        var before = ArtifactHash.Sha256OfFile(context.Target); var originalVersion = context.Profile.Provenance.Version;
        ProjectProfileWriter.SaveResult? saved = null; var writes = 0;
        Batch.RunSta(() =>
        {
            var dialog = new Dialog(new[] { DialogGroup(First), DialogGroup(Second) }, data.Catalog, (choices, reviewer) =>
            {
                writes++;
                saved = new EstimateWorkflowService().SaveReviewedMappings(context.Profile, data.Catalog, context.Scan,
                    choices.Select(c => new EstimateWorkflowService.ReviewedMappingChoice(c.RuleKey, c.CatalogCode, c.ExcludedAlternativeRuleKey)).ToArray(),
                    reviewer, context.Target, context.Scan.ProfileWriteState!);
            });
            try
            {
                dialog.GroupsGrid.SelectedItem = Batch.Row(dialog, First); Batch.SelectCode(dialog, "U51.06.1900");
                dialog.StageSelection().Should().BeTrue();
                dialog.GroupsGrid.SelectedItem = Batch.Row(dialog, Second); Batch.SelectCode(dialog, "U51.06.2140");
                dialog.StageSelection().Should().BeTrue();
                writes.Should().Be(0); ArtifactHash.Sha256OfFile(context.Target).Should().Be(before);
                dialog.TryConfirm().Should().BeFalse("staging is not named explicit approval");
                dialog.Approver.Text = Reviewer; dialog.Confirm.IsChecked = true;
                dialog.TryConfirm().Should().BeTrue();
            }
            finally { dialog.Close(); }
        });
        writes.Should().Be(1); saved.Should().NotBeNull(); saved!.NewVersion.Should().Be(originalVersion + 1);
        var reopened = ProjectProfileLoader.LoadFromFile(context.Target).Profile!;
        reopened.Estimate.QuantitySources.Rules.Should().HaveCount(10);
        reopened.Estimate.IgnoredRuleDecisions.Should().BeEmpty(); reopened.Estimate.ProjectOverrides.Should().BeEmpty();
        reopened.Estimate.Earthworks.Requested.Should().BeNull();
        var rebased = EstimateWorkflowService.RebaseAfterProfileDecisions(context.Scan, reopened, saved, new[] { First, Second });
        rebased.Records.Should().HaveCount(79180);
        for (var i = 0; i < data.Records.Count; i++)
        {
            rebased.Records[i].RecordId.Should().Be(data.Records[i].RecordId);
            rebased.Records[i].Measurement.Should().BeSameAs(data.Records[i].Measurement);
            rebased.Records[i].Source.Should().BeSameAs(data.Records[i].Source);
        }
        var approved = rebased.Records.Where(r => r.Classification.RuleKey is First or Second).ToList();
        approved.Should().HaveCount(423).And.OnlyContain(r => CatalogIdentity.IsClassificationCurrent(r.Classification, data.Catalog));
        var reused = data.Records.Where(r => CivilQuantityExtractionService.ResolveApprovedRule(reopened,
            r.Classification.RuleKey!, r.Source.Layer!, r.Measurement.Kind).Rule != null).ToList();
        reused.Should().HaveCount(423, "this is the actual native discovery rule resolver on replayed measured rows, not a fresh CAD scan");
        var areas = rebased.Records.Where(r => r.Classification.RuleKey == "layer:HW-CURB|area").ToList();
        areas.Should().HaveCount(5).And.OnlyContain(r => r.Classification.CandidateCatalogCode == null);
        foreach (var finding in data.Findings.Where(f => f.Code != EstimateFindingCodes.MixedDimensionLayer && f.Code != EstimateFindingCodes.QuantitySignificanceReview))
            rebased.Findings.Should().Contain(f => ReferenceEquals(f, finding));
        rebased.Findings.Should().Contain(f => f.Code == EstimateFindingCodes.MixedDimensionLayer &&
            f.AffectedRecordIds.Any(id => areas.Any(r => r.RecordId == id)));
        data.Records.Should().OnlyContain(r => r.Classification.CandidateCatalogCode == null);
        ArtifactHash.Sha256OfFile(LiveProfile).Should().Be(ProfileHash);
        Write(data, "explicit-batch-summary.json", new
        {
            scope = "Full saved dataset; two deliberately selected TEST-ONLY different-code choices, not engineering approval",
            records = 79180, writes, approved_records = approved.Count, replay_resolver_reused_records = reused.Count,
            choices = new[] { new { key = First, code = "U51.06.1900", records = 297 }, new { key = Second, code = "U51.06.2140", records = 126 } },
            retained_unmapped_records = rebased.Records.Count(r => r.Classification.CandidateCatalogCode == null),
            retained_curb_area_alternatives = areas.Count,
            retained_global_blockers = rebased.Findings.Count(f => f.AffectedRecordIds.Count == 0 && EstimatePreflightPolicy.IsBlocking(f)),
            findings_after_by_code = rebased.Findings.GroupBy(f => f.Code).ToDictionary(g => g.Key, g => g.Count()),
            no_exclusions = true, no_price_changes = true, no_earthworks_decision = true,
            target = context.Target, profile_sha256 = ArtifactHash.Sha256OfFile(context.Target), production_profile_unchanged = true,
        });
    }

    [RealBoqFact, Trait("Category", "RecordedRunReplay")]
    public void FullSavedRun_StagedCancelAndMixedUnitBatchLeaveProfileAndApprovalsUntouched()
    {
        var data = Saved.Value; var context = Context("cancel-and-unit");
        var before = ArtifactHash.Sha256OfFile(context.Target); var writes = 0;
        Batch.RunSta(() =>
        {
            var dialog = new Dialog(new[] { DialogGroup(First), DialogGroup("layer:HW-CURB|area") }, data.Catalog, (_, _) => writes++);
            try
            {
                Batch.Row(dialog, First).MarkedForBatch = true;
                Batch.Row(dialog, "layer:HW-CURB|area").MarkedForBatch = true;
                Batch.SelectCode(dialog, "U51.06.1900");
                dialog.StageMarkedGroups().Should().BeFalse("a length item must never silently map the five square-metre alternatives");
                dialog.GroupsGrid.Items.Cast<Dialog.GroupRow>().Should().OnlyContain(row => row.Draft == "—");
                dialog.ClearBatchMarks(); dialog.GroupsGrid.SelectedItem = Batch.Row(dialog, First);
                Batch.SelectCode(dialog, "U51.06.1900"); dialog.StageSelection().Should().BeTrue();
                dialog.ApprovedChoices.Should().BeNull();
                // Close/cancel without TryConfirm: the writer must not be invoked.
            }
            finally { dialog.Close(); }
        });
        writes.Should().Be(0); ArtifactHash.Sha256OfFile(context.Target).Should().Be(before);
        var invalid = () => new EstimateWorkflowService().SaveReviewedMappings(context.Profile, data.Catalog, context.Scan,
            new[] { new EstimateWorkflowService.ReviewedMappingChoice(First, "U51.06.1900"),
                new EstimateWorkflowService.ReviewedMappingChoice("layer:HW-CURB|area", "U51.06.1900") },
            Reviewer, context.Target, context.Scan.ProfileWriteState!);
        invalid.Should().Throw<InvalidOperationException>();
        ArtifactHash.Sha256OfFile(context.Target).Should().Be(before);
        context.Profile.Estimate.QuantitySources.Rules.Should().HaveCount(8);
        Write(data, "cancel-and-unit-summary.json", new { staged_cancel_writes = writes, mixed_unit_batch_rejected_atomically = true,
            profile_bytes_unchanged = true, original_rule_count = 8, original_record_count = data.Records.Count,
            production_profile_unchanged = ArtifactHash.Sha256OfFile(LiveProfile) == ProfileHash });
    }

    [RealBoqFact, Trait("Category", "RecordedRunReplay")]
    public void FullSavedRun_ExplicitClassifierBackedNoiseBatchSavesOnceReopensAndRetainsEveryMeasurement()
    {
        var data = Saved.Value; var context = Context("noise-batch");
        var before = ArtifactHash.Sha256OfFile(context.Target);
        var originalVersion = context.Profile.Provenance.Version;
        var groups = data.Records.GroupBy(r => r.Classification.RuleKey!, StringComparer.Ordinal)
            .Select(g => new { key = g.Key, records = g.ToList(), verdict = QuantitySignificance.Classify(new(g.Key,
                g.First().Source.Layer, g.First().Measurement.Unit, g.Sum(r => r.Measurement.RawValue), g.Count())) }).ToList();
        var eligible = groups.Where(g => g.verdict.Kind is QuantitySignificance.Kind.StationGeometry or QuantitySignificance.Kind.Auxiliary).ToList();
        eligible.Should().NotBeEmpty();
        var requests = eligible.Select(g => new EstimateWorkflowService.IgnoredRuleDecisionRequest(g.key, g.verdict.Reason)).ToArray();
        // Merely constructing/reviewing the batch has no effect on either memory or disk.
        context.Profile.Estimate.IgnoredRuleDecisions.Should().BeEmpty();
        ArtifactHash.Sha256OfFile(context.Target).Should().Be(before);
        var saved = new EstimateWorkflowService().SaveIgnoredRuleDecisions(context.Profile, context.Scan,
            requests, Reviewer, context.Target, context.Scan.ProfileWriteState!);
        saved.NewVersion.Should().Be(originalVersion + 1, "one complete explicit batch is one profile write");
        var reopened = ProjectProfileLoader.LoadFromFile(context.Target).Profile!;
        var keys = IgnoredRulePolicy.ApprovedKeys(reopened).ToHashSet(StringComparer.Ordinal);
        keys.Should().BeEquivalentTo(requests.Select(r => r.RuleKey));
        reopened.Estimate.IgnoredRuleDecisions.Should().HaveCount(requests.Length).And.OnlyContain(d => d.ApprovedBy == Reviewer && d.ApprovedAtUtc != null);
        reopened.Estimate.IgnoredRuleDecisions.Select(d => d.ApprovedAtUtc).Distinct().Should().ContainSingle();
        reopened.Estimate.QuantitySources.Rules.Should().HaveCount(8);
        reopened.Estimate.ProjectOverrides.Should().BeEmpty(); reopened.Estimate.Earthworks.Requested.Should().BeNull();
        var rebased = EstimateWorkflowService.RebaseAfterProfileDecision(context.Scan, reopened, saved);
        rebased.Records.Should().HaveCount(data.Records.Count);
        for (var i = 0; i < data.Records.Count; i++) rebased.Records[i].Should().BeSameAs(data.Records[i]);
        rebased.Records.Should().OnlyContain(r => r.Classification.CandidateCatalogCode == null);
        foreach (var finding in data.Findings.Where(f => f.Code != EstimateFindingCodes.MixedDimensionLayer && f.Code != EstimateFindingCodes.QuantitySignificanceReview))
            rebased.Findings.Should().Contain(f => ReferenceEquals(f, finding), "drawing relevance does not resolve geometry, coverage or source failures");
        groups.Where(g => g.verdict.Kind == QuantitySignificance.Kind.ExistingUtility).Should().OnlyContain(g => !keys.Contains(g.key));
        ArtifactHash.Sha256OfFile(LiveProfile).Should().Be(ProfileHash);
        Write(data, "noise-batch-findings.json", rebased.Findings);
        Write(data, "noise-batch-summary.json", new
        {
            scope = "Explicit synthetic TEST-ONLY review of all current classifier-eligible exact groups; NOT production scope approval",
            retained_records = rebased.Records.Count, original_groups = groups.Count,
            explicitly_reviewed_groups = keys.Count, retained_records_in_reviewed_groups = eligible.Sum(g => g.records.Count),
            pending_groups_before = groups.Count, pending_groups_after = groups.Count(g => !keys.Contains(g.key)),
            decisions_by_kind = eligible.GroupBy(g => g.verdict.Kind.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            preserved_existing_utility_groups = groups.Count(g => g.verdict.Kind == QuantitySignificance.Kind.ExistingUtility),
            original_blocking_findings = 455, remaining_blocking_findings = rebased.Findings.Count(EstimatePreflightPolicy.IsBlocking),
            findings_after_by_code = rebased.Findings.GroupBy(f => f.Code).ToDictionary(g => g.Key, g => g.Count()),
            exact_decisions = requests, saved_profile_version = saved.NewVersion, saved_profile_sha256 = saved.NewHash,
            no_mapping_or_price_changes = true, no_earthworks_decision = true, production_profile_unchanged = true,
        });
    }

    private static void Write(Data data, string name, object evidence) =>
        File.WriteAllText(Path.Combine(data.Root, name), JsonSerializer.Serialize(evidence, Json));
}
