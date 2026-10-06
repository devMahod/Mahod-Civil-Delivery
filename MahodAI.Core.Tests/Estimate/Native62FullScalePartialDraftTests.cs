using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// Optional explicit scale-acceptance filter: a full immutable native62 measurement
/// replay (62/63/67), not live Civil and never production engineering authority. A separate local
/// test profile receives clearly named SYNTHETIC HW-CURB approval through the real
/// save/reopen/rebase workflow. The active project profile remains untouched. No source,
/// record, alternative, earthworks or coverage finding is excluded to make it pass.
/// </summary>
public sealed class Native62FullScalePartialDraftTests
{
    [Fact]
    [Trait("Category", "Native62FullScaleReplay")]
    public async Task All79197NativeRecords_WithIsolatedCurbApproval_BuildAndExportAnHonestPartialDraft()
        => await RunRecordedCase("62", "estimate-extract-20260910-101153-80274c7f", 79197, 461,
            "7ae9e9403808849b9f7b519df9f70830f8f17c00e7bdd73f5ad1dd82ad394500",
            "8d8ffbd6e53e100cb1d2a2e563db1441553dda5b90b856428346ade557ed89b5", false);

    [Fact]
    [Trait("Category", "Native63FullScaleReplay")]
    public async Task All79203Native63Records_SaveReloadEditCancelRebase_BuildAndExportAnHonestPartialDraft()
        => await RunRecordedCase("63", "estimate-extract-20260910-125000-e6b2f447", 79203, 467,
            "78d310afc13b02616c90e5618724b9614ec6a1107d4d3f43b16ab773ec7ed47e",
            "a3fe94a30727eb3a8e3a4e70630d155a1659c25e6e91f78eb15b36307b3e5c18", true,
            new("640a8598da049b7f7a0306a85a3c25a48366f04ae25366f534b8f25f6f74726c",
                "0581964414cd31e86feada750948327bdbf3624bb7359510b14e284e677d323b",
                "0efc9ea2bdf184c6e96000163ed6bc32128453f4ce9b1ed1d982fa3a521e8f7b"));

    [Fact]
    [Trait("Category", "Native67FullScaleReplay")]
    public async Task All79176Native67Records_CounterfactualApprovalReopenEditCancel_ExportExactCatalogSubtotalNotFullEstimate()
        => await RunRecordedCase("67", "estimate-extract-20260912-080550-7979b8d4", 79176, 462,
            "c01d3f88bee50f92d70b23eda96fa055a4b65b4c02c8ecec7873d15023e6341e",
            "a8ecd36dc3336102220faa38cf326bc4e62cebfbe6f248e74737606ec19304e5", true,
            new("4ac76116846025c87e504f3c05b8d4136a8881f6b8079eae2ef7bf9dcca885ba",
                "d290a7fcc31cdf8bed49ec57a077a7350cd0394a625022378773490cd4aaa763",
                "723b1b12dd49c1445803102016315ff545fb29b69ea1aab2a4fdb23a7dfcb98c"));

    private sealed record RecordedRunIdentity(string ManifestHash, string ProposalsHash, string DrawingHash);

    private static async Task RunRecordedCase(string nativeVersion, string run, int recordCount, int findingCount,
        string preflightHash, string recordsHash, bool exerciseEditAndCancel, RecordedRunIdentity? identity = null)
    {
        const string code = "U51.06.1900";
        var notice = $"TEST-ONLY COUNTERFACTUAL REPLAY — real native{nativeVersion} quantities; SYNTHETIC HW-CURB approval; NOT an approved 6422 estimate";
        var runDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MahodAI_Civil3D", "civil-delivery", "runs", run);
        var outputParent = Environment.GetEnvironmentVariable("MAHOD_PARTIAL_DRAFT_SCALE_EVIDENCE_DIR") ??
            Path.Combine(Path.GetTempPath(), "MahodAI-native62-full-scale-partial-draft");
        var output = Path.Combine(outputParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        // ~170 MB per run. Kept only when an evidence folder was named; otherwise removed at the end of the run, pass
        // or fail (293 leaked runs in %TEMP% once filled the build machine's disk while Civil was open).
        using var cleanup = new RunOutputCleanup(output,
            keep: Environment.GetEnvironmentVariable("MAHOD_PARTIAL_DRAFT_SCALE_EVIDENCE_DIR") != null);
        var watch = Stopwatch.StartNew();
        double loadSeconds = 0, decisionRebaseSeconds = 0, buildSeconds = 0, evaluationSeconds = 0, writerSeconds = 0;
        EstimateResult? built = null;
        EstimateExcelWriter.WriteResult? written = null;
        int selectedCount = 0;
        decimal? exactCatalogPrice = null, pricedCanonicalQuantity = null;
        int scannedEntityCount = 81370;
        void Report(string stage) => File.WriteAllText(Path.Combine(output, "scale-summary.json"), JsonSerializer.Serialize(new
        {
            stage, notice, source_run = run, native_execution_performed = false,
            active_profile_written = false, source_drawing_written = false, earthworks_excluded = false,
            native_record_count = recordCount, test_approved_curb_records = selectedCount,
            derived_decision_findings_recomputed = true,
            real_save_reopen_rebase_workflow = true,
            original_preflight_sha256 = preflightHash, original_records_sha256 = recordsHash,
            edit_cancel_wrong_unit_exercised = exerciseEditAndCancel,
            estimated_lines = built?.Lines.Count, included_lines = built?.Lines.Count(line => line.IncludedInTotals),
            unresolved_lines = built?.ExcludedLineCount, subtotal = built?.CleanTotal,
            exact_catalog_unit_price = exactCatalogPrice, priced_canonical_quantity = pricedCanonicalQuantity,
            price_origin = "unchanged SHA-pinned existing catalog; no synthetic project-price approval",
            approval_scope = "isolated counterfactual test profile only; not real engineering authority",
            full_export_allowed = built != null && EstimatePreflightPolicy.CanExport(built),
            load_seconds = loadSeconds, build_seconds = buildSeconds, evaluation_seconds = evaluationSeconds,
            decision_rebase_seconds = decisionRebaseSeconds,
            writer_seconds = writerSeconds, elapsed_seconds = watch.Elapsed.TotalSeconds,
            peak_working_set_bytes = Process.GetCurrentProcess().PeakWorkingSet64,
            workbook = written?.XlsxPath, audit = written?.AuditPath, manifest = written?.ManifestPath,
            workbook_bytes = written == null ? (long?)null : new FileInfo(written.XlsxPath).Length,
            audit_bytes = written == null ? (long?)null : new FileInfo(written.AuditPath).Length,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Report("loading");

        var preflightPath = Path.Combine(runDirectory, "quantity_preflight.json");
        ArtifactHash.Sha256OfFile(preflightPath).Should().Be(preflightHash);
        var findings = JsonSerializer.Deserialize<List<DeliveryFinding>>(File.ReadAllText(preflightPath))!;
        findings.Should().HaveCount(findingCount);
        findings.Count(finding => finding.AffectedRecordIds.Count == 0 && EstimatePreflightPolicy.IsBlocking(finding)).Should().Be(10);
        // Read only the small scan header for immutable source identities, not its
        // repeated 353MB Records body. The neutral artifact is streamed once below.
        var headerLines = File.ReadLines(Path.Combine(runDirectory, "estimate_scan.json"))
            .TakeWhile(line => !line.TrimStart().StartsWith("\"Records\":", StringComparison.Ordinal));
        using var header = JsonDocument.Parse(string.Join("\n", headerLines) + "\n\"Records\": []\n}");
        header.RootElement.GetProperty("RunId").GetString().Should().Be(run);
        if (exerciseEditAndCancel)
        {
            identity.Should().NotBeNull();
            ArtifactHash.Sha256OfFile(Path.Combine(runDirectory, "run_manifest.json")).Should()
                .Be(identity!.ManifestHash);
            ArtifactHash.Sha256OfFile(Path.Combine(runDirectory, "mapping_proposals.json")).Should()
                .Be(identity.ProposalsHash);
            header.RootElement.GetProperty("SourceDrawingHash").GetString().Should()
                .Be(identity.DrawingHash);
            using var nativeManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDirectory, "run_manifest.json")));
            nativeManifest.RootElement.GetProperty("plugin_package_revision").GetString().Should().Be("1.2." + nativeVersion);
            nativeManifest.RootElement.GetProperty("record_counts").GetProperty("neutral_records").GetInt32().Should().Be(recordCount);
            scannedEntityCount = nativeManifest.RootElement.GetProperty("record_counts").GetProperty("scanned_entities").GetInt32();
            nativeManifest.RootElement.GetProperty("artifact_hashes")
                .GetProperty(Path.Combine(runDirectory, "neutral_quantity_records.json")).GetString().Should().Be(recordsHash);
            nativeManifest.RootElement.GetProperty("artifact_hashes")
                .GetProperty(Path.Combine(runDirectory, "quantity_preflight.json")).GetString().Should().Be(preflightHash);
        }
        var external = header.RootElement.GetProperty("ExternalSources").Deserialize<List<EstimateExternalSource>>()!;
        var profilePath = header.RootElement.GetProperty("ProfileSource").GetString()!;
        var expectedProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MahodAI_Civil3D", "civil-delivery", "profiles", "6422", "project-profile.yaml");
        // A path recorded inside an artifact is evidence, not filesystem authority.
        // Hash only the exact known local profile; never follow an arbitrary/UNC path.
        profilePath.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        Path.GetFullPath(profilePath).Should().BeEquivalentTo(Path.GetFullPath(expectedProfile));
        var activeProfileBefore = ArtifactHash.Sha256OfFile(profilePath);

        var records = new List<NeutralQuantityRecord>(recordCount);
        using (var sha = SHA256.Create())
        await using (var stream = File.OpenRead(Path.Combine(runDirectory, "neutral_quantity_records.json")))
        await using (var verified = new CryptoStream(stream, sha, CryptoStreamMode.Read))
        {
            await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable<NeutralQuantityRecord>(verified))
                records.Add(record ?? throw new InvalidDataException("Null recorded quantity."));
            var tail = new byte[4096];
            while (await verified.ReadAsync(tail) > 0) { } // Finish SHA over the same single read through EOF.
            Convert.ToHexString(sha.Hash!).ToLowerInvariant().Should().Be(recordsHash);
        }
        records.Should().HaveCount(recordCount); records.Should().OnlyContain(record => record.RunId == run);
        if (nativeVersion == "67")
        {
            records.Select(record => record.Classification.RuleKey).Distinct(StringComparer.Ordinal).Should().HaveCount(1031);
            records.Should().OnlyContain(record => record.Classification.CandidateCatalogCode == null &&
                record.Classification.MappingApprovedBy == null, "no real project approval exists in this captured scan");
        }
        var selected = records.Where(record => record.Classification.RuleKey == "layer:HW-CURB|length").ToList();
        selected.Should().HaveCount(297); selectedCount = selected.Count;
        selected.Should().OnlyContain(record => record.Classification.CandidateCatalogCode == null && record.Classification.MappingApprovedBy == null);
        records.Count(record => record.Classification.RuleKey == "layer:HW-CURB|area").Should().Be(5);

        var catalogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MahodAI-Plugin",
            "fixtures", "civil-delivery", "estimate", "nti-urban-082025.xlsx");
        var catalog = PriceBookXlsxLoader.Load(catalogPath, "nti-urban-082025");
        catalog.FileHash.Should().Be("90da59809602c4127fcf0b91c3f037c2b98d7b93288beba2c851e3a3550f313c");
        exactCatalogPrice = catalog.Prices[code].Price;
        exactCatalogPrice.Should().BeGreaterThan(0);
        var profile = new ProjectProfile { ProfileId = "6422", ProjectName = notice };
        profile.Estimate.QuantitySources.SourceScopePolicy = EstimatePreflightPolicy.DiscoverAllSourceScopePolicy;
        profile.Estimate.QuantitySources.XrefPolicy = EstimatePreflightPolicy.IncludeXrefsPolicy;
        profile.Estimate.Catalog.CatalogFile = catalogPath;
        profile.Estimate.Catalog.CatalogFileHash = catalog.FileHash;
        profile.Estimate.Pricing.PriceBookSnapshotId = catalog.SnapshotId;
        profile.Estimate.Pricing.PriceBookHash = catalog.FileHash;
        profile.Estimate.PriceBooks.Add(new() { Id = catalog.SnapshotId, File = catalogPath, FileHash = catalog.FileHash });
        var syntheticProfilePath = Path.Combine(output, "SYNTHETIC-ONLY-NOT-ACTIVE-profile.yaml");
        Path.GetFullPath(syntheticProfilePath).Should().StartWith(Path.GetFullPath(output) + Path.DirectorySeparatorChar);
        File.Exists(syntheticProfilePath).Should().BeFalse();
        var originalScan = new EstimateWorkflowService.ScanResult
        {
            RunId = run, ProjectProfileId = profile.ProfileId, ProfileSource = syntheticProfilePath,
            SourceDrawing = header.RootElement.GetProperty("SourceDrawing").GetString()!,
            SourceDrawingHash = header.RootElement.GetProperty("SourceDrawingHash").GetString()!,
            DatabaseRevision = "REPLAY-ONLY:" + run, SourceDbMod = null,
            ProjectProfileHash = EstimateTraceIdentity.EffectiveProfileHash(profile),
            ProjectProfileEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(profile),
            ProfileWriteState = ProjectProfileWriter.CaptureExpectedGeneratedState(profile,
                EstimateTraceIdentity.EffectiveProfileHash(profile), syntheticProfilePath),
            Records = records, Findings = findings, ExternalSources = external,
            ScannedEntities = scannedEntityCount, DiscoveryMode = true, Status = DeliveryStatus.Failed,
        };
        loadSeconds = watch.Elapsed.TotalSeconds;
        Report("saving-synthetic-decision-and-rebasing");
        var decisionWatch = Stopwatch.StartNew();
        const string approver = "SYNTHETIC-SCALE-TEST-ONLY-NOT-ENGINEER-AUTHORITY";
        if (exerciseEditAndCancel)
        {
            var wrongUnit = catalog.Items.Values.First(item => item.Unit.Canonical == "m2").Code;
            Action rejected = () => new EstimateWorkflowService().SaveReviewedMappings(profile, catalog, originalScan,
                new[] { new EstimateWorkflowService.ReviewedMappingChoice("layer:HW-CURB|length", wrongUnit) },
                approver, syntheticProfilePath, originalScan.ProfileWriteState);
            rejected.Should().Throw<InvalidOperationException>().WithMessage("*Unit mismatch*");
            File.Exists(syntheticProfilePath).Should().BeFalse();
            profile.Estimate.QuantitySources.Rules.Should().BeEmpty();
        }
        var saved = new EstimateWorkflowService().SaveReviewedMappings(profile, catalog, originalScan,
            new[] { new EstimateWorkflowService.ReviewedMappingChoice("layer:HW-CURB|length", code) },
            approver, syntheticProfilePath, originalScan.ProfileWriteState);
        saved.Path.Should().Be(syntheticProfilePath);
        profile = ProjectProfileLoader.LoadFromFile(saved.Path).Profile!;
        profile.Estimate.QuantitySources.Rules.Should().ContainSingle().Which.ApprovedBy.Should().Be(approver);
        profile.Estimate.IgnoredRuleDecisions.Should().BeEmpty();
        profile.Estimate.Earthworks.Requested.Should().BeNull();
        // Execute the real production rebase, not a second classification algorithm.
        // Native source/geometry findings survive; only decision-derived gates change.
        var originalCoverage = findings.Where(finding => finding.Code != EstimateFindingCodes.QuantitySignificanceReview &&
            finding.Code != EstimateFindingCodes.MixedDimensionLayer).ToList();
        var rebased = EstimateWorkflowService.RebaseAfterProfileDecisions(originalScan, profile, saved,
            new[] { "layer:HW-CURB|length" });
        rebased.Records.Should().HaveCount(records.Count);
        for (var index = 0; index < records.Count; index++)
        {
            rebased.Records[index].RecordId.Should().Be(records[index].RecordId);
            rebased.Records[index].Measurement.Should().BeSameAs(records[index].Measurement);
            rebased.Records[index].Source.Should().BeSameAs(records[index].Source);
        }
        selected.Should().OnlyContain(record => record.Classification.CandidateCatalogCode == null &&
            record.Classification.MappingApprovedBy == null, "the original native scan must stay immutable");
        records = rebased.Records;
        records.Where(record => record.Classification.RuleKey == "layer:HW-CURB|length")
            .Should().HaveCount(297).And.OnlyContain(record => CatalogIdentity.IsClassificationCurrent(record.Classification, catalog));
        findings = rebased.Findings;
        findings.Should().Contain(originalCoverage);
        findings.Count(finding => finding.AffectedRecordIds.Count == 0 && EstimatePreflightPolicy.IsBlocking(finding)).Should().Be(10);
        if (exerciseEditAndCancel)
        {
            // Exercise actual cancellation policy, not a pretend modal click: a proposed
            // project price without explicit approval cannot write or change the profile.
            var priceContext = ProjectPriceApprovalPolicy.CaptureForRecords(profile, catalog, code,
                records.Where(record => record.Classification.RuleKey == "layer:HW-CURB|length").ToList());
            var beforeCancelHash = ArtifactHash.Sha256OfFile(syntheticProfilePath);
            ProjectPriceApprovalPolicy.Approve(priceContext, 85.50m, "REPLAY-ONLY-QUOTE", notice,
                approver, DateTime.UtcNow, explicitlyApproved: false).Should().BeNull();
            ArtifactHash.Sha256OfFile(syntheticProfilePath).Should().Be(beforeCancelHash);
            profile.Estimate.ProjectOverrides.Should().BeEmpty();

            const string editedCode = "U51.06.1910";
            catalog.Items[editedCode].Unit.Canonical.Should().Be(catalog.Items[code].Unit.Canonical);
            catalog.Prices[editedCode].Price.Should().NotBe(catalog.Prices[code].Price);
            var firstEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(profile);
            var editedSave = new EstimateWorkflowService().SaveReviewedMappings(profile, catalog, rebased,
                new[] { new EstimateWorkflowService.ReviewedMappingChoice("layer:HW-CURB|length", editedCode) },
                approver, syntheticProfilePath, rebased.ProfileWriteState);
            var editedProfile = ProjectProfileLoader.LoadFromFile(editedSave.Path).Profile!;
            EstimateTraceIdentity.EffectiveProfileHash(editedProfile).Should().NotBe(firstEffectiveHash);
            var editedScan = EstimateWorkflowService.RebaseAfterProfileDecisions(rebased, editedProfile, editedSave,
                new[] { "layer:HW-CURB|length" });
            var editedContext = new EstimateBuildContext(editedSave.NewHash, EstimateBuildContext.SourceFileHash,
                EstimateTraceIdentity.EffectiveProfileHash(editedProfile), EstimateBuildContext.NeutralRecordSet,
                editedScan.SourceDrawing, editedScan.SourceDrawingHash!, editedScan.DatabaseRevision!, null)
                { ExternalSources = editedScan.ExternalSources };
            var editedBuild = EstimateBuilder.Build(editedScan.Records, catalog, editedProfile,
                $"SYNTHETIC-NATIVE{nativeVersion}-EDITED-CATALOG", editedScan.Findings, editedContext);
            var editedEligible = editedBuild.Lines.Where(line => line.IncludedInTotals).ToList();
            editedEligible.Should().HaveCount(292).And.OnlyContain(line => line.CatalogCode == editedCode);
            editedBuild.CleanTotal.Should().Be(Math.Round(editedEligible.Sum(line =>
                Math.Round((decimal)line.RawQuantity, 4, MidpointRounding.AwayFromZero)) *
                catalog.Prices[editedCode].Price!.Value, 2, MidpointRounding.AwayFromZero));
            EstimatePreflightPolicy.CanExport(editedBuild).Should().BeFalse();
            editedBuild.Findings.Should().Contain(originalCoverage);
            editedBuild.Lines.Should().HaveCount(recordCount);

            // Return to the first explicitly synthetic catalog choice via production save,
            // reload and rebase, leaving a single final workbook and no live approvals.
            saved = new EstimateWorkflowService().SaveReviewedMappings(editedProfile, catalog, editedScan,
                new[] { new EstimateWorkflowService.ReviewedMappingChoice("layer:HW-CURB|length", code) },
                approver, syntheticProfilePath, editedScan.ProfileWriteState);
            profile = ProjectProfileLoader.LoadFromFile(saved.Path).Profile!;
            rebased = EstimateWorkflowService.RebaseAfterProfileDecisions(editedScan, profile, saved,
                new[] { "layer:HW-CURB|length" });
            records = rebased.Records; findings = rebased.Findings;
            findings.Should().Contain(originalCoverage);
            records.Should().HaveCount(recordCount);
            ArtifactHash.Sha256OfFile(profilePath).Should().Be(activeProfileBefore);
        }
        var profileHash = EstimateTraceIdentity.EffectiveProfileHash(profile);
        var context = new EstimateBuildContext(saved.NewHash, EstimateBuildContext.SourceFileHash, profileHash,
            EstimateBuildContext.NeutralRecordSet, rebased.SourceDrawing,
            rebased.SourceDrawingHash!, rebased.DatabaseRevision!, null)
        { ExternalSources = rebased.ExternalSources };
        decisionRebaseSeconds = decisionWatch.Elapsed.TotalSeconds;
        Report("building");
        var step = Stopwatch.StartNew();
        built = EstimateBuilder.Build(records, catalog, profile, $"SYNTHETIC-FULL-SCALE-REPLAY-NATIVE{nativeVersion}", findings, context);
        buildSeconds = step.Elapsed.TotalSeconds;
        built.Lines.Should().HaveCount(records.Count);
        built.Lines.Select(line => line.RecordId).Should().Equal(records.Select(record => record.RecordId));
        built.Findings.Should().Contain(findings);
        built.Exclusions.Should().BeEmpty(); profile.Estimate.Earthworks.Requested.Should().BeNull();
        var selectedIds = selected.Select(record => record.RecordId).ToHashSet(StringComparer.Ordinal);
        var eligible = built.Lines.Where(line => line.IncludedInTotals).ToList();
        eligible.Should().NotBeEmpty("unrelated coverage gaps must not erase every independently measured, test-approved curb line");
        eligible.Should().HaveCount(292, "267 independent GM/HA open lengths plus 25 host/SM lengths are valid; five closed perimeters remain ambiguous");
        var closedIds = records.Where(record => record.Classification.RuleKey is "layer:HW-CURB|length" or "layer:HW-CURB|area")
            .Where(record => record.Measurement.Method.StartsWith("closed-polyline", StringComparison.Ordinal))
            .Select(record => record.RecordId).ToHashSet(StringComparer.Ordinal);
        closedIds.Should().HaveCount(10);
        built.Lines.Where(line => closedIds.Contains(line.RecordId)).Should().OnlyContain(line =>
            !line.IncludedInTotals && line.Total == null && line.Findings.Any(finding => finding.Code == EstimateFindingCodes.MixedDimensionLayer));
        eligible.Count(line => line.SourceLayer is "6422-GM-MODEL-NATAZ|HW-CURB" or "6422-HA-MODEL-NATAZ|HW-CURB")
            .Should().Be(267);
        eligible.Should().OnlyContain(line => selectedIds.Contains(line.RecordId) && line.Total != null);
        built.Lines.Where(line => !selectedIds.Contains(line.RecordId)).Should().OnlyContain(line => !line.IncludedInTotals && line.Total == null);
        var expectedSubtotal = Math.Round(eligible.Sum(line => Math.Round((decimal)line.RawQuantity, 4, MidpointRounding.AwayFromZero)) *
            catalog.Prices[code].Price!.Value, 2, MidpointRounding.AwayFromZero);
        built.CleanTotal.Should().Be(expectedSubtotal);
        pricedCanonicalQuantity = eligible.Sum(line => Math.Round((decimal)line.RawQuantity, 4, MidpointRounding.AwayFromZero));
        built.Lines.Where(line => line.IncludedInTotals).Should().OnlyContain(line => line.Price == exactCatalogPrice);
        profile.Estimate.ProjectOverrides.Should().BeEmpty();
        var groups = EstimateBoqSemantics.GroupLines(built.Lines);
        groups.Where(group => group.IncludedInTotals).Should().ContainSingle().Which.Total.Should().Be(expectedSubtotal);
        groups.Sum(group => group.ObjectCount).Should().Be(recordCount);
        EstimatePreflightPolicy.CanExport(built).Should().BeFalse();
        Report("evaluating");
        step.Restart();
        var draft = EstimatePartialPricedDraftPolicy.Evaluate(built);
        evaluationSeconds = step.Elapsed.TotalSeconds;
        if (!draft.CanExport)
        {
            var fullReasons = EstimatePreflightPolicy.ExportBlockingReasons(built);
            var diagnosticImpact = EstimateFindingImpactPolicy.CreateIndex(built.Findings);
            var rejectedIncluded = eligible.Select(line => new
            {
                line.LineId, line.RecordId, line.SourceDrawingPath, line.SourceDrawingHash,
                line.SourceHandle, line.SourceXref, line.SourceLayer, line.SourceEntityType,
                line.MeasurementMethod, line.Status, line.RawQuantity, line.BoqQuantity, line.Price, line.Total,
                invalid_full_export_reasons = fullReasons.Where(reason => reason.StartsWith("line:" + line.LineId + ":", StringComparison.Ordinal)).ToArray(),
                blocking_line_findings = line.Findings.Where(EstimatePreflightPolicy.IsBlocking).Select(f => new { f.Code, f.FindingId }).ToArray(),
                blocking_project_impacts = diagnosticImpact.BlockingFindings(line).Select(f => new { f.Code, f.FindingId }).ToArray(),
            }).Where(line => line.Status != DeliveryStatus.Ready || line.Price is not > 0 || line.Total == null ||
                !double.IsFinite(line.RawQuantity) || line.RawQuantity <= 0 ||
                !double.IsFinite(line.BoqQuantity) || line.BoqQuantity <= 0 ||
                line.invalid_full_export_reasons.Length != 0 || line.blocking_line_findings.Length != 0 || line.blocking_project_impacts.Length != 0)
                .ToList();
            File.WriteAllText(Path.Combine(output, "partial-draft-rejected-included-lines.json"), JsonSerializer.Serialize(new
            {
                notice, included_count = eligible.Count, draft_eligible_count = draft.EligibleLineCount,
                draft.BlockingReasons, rejected_included = rejectedIncluded,
            }, new JsonSerializerOptions { WriteIndented = true }));
            Report("partial-draft-policy-rejected");
        }
        draft.CanExport.Should().BeTrue(string.Join(", ", draft.BlockingReasons));
        draft.EligibleLineCount.Should().Be(eligible.Count); draft.Subtotal.Should().Be(expectedSubtotal);
        Report("writing");
        step.Restart();
        written = EstimateExcelWriter.WritePartialPricedDraft(built, output,
            $"REPLAY-ONLY-NATIVE{nativeVersion}-FULL-SCALE-PARTIAL", new(ProjectTitle: notice));
        writerSeconds = step.Elapsed.TotalSeconds;
        Report("verifying");
        new FileInfo(written.AuditPath).Length.Should().BeLessThan(512L * 1024 * 1024,
            "global source evidence is a registry, not a copy inside every blocked line");
        using (var auditReader = new StreamReader(written.AuditPath))
        {
            var prefix = new char[4096]; var read = auditReader.Read(prefix, 0, prefix.Length);
            var text = new string(prefix, 0, read);
            text.Should().Contain("\"schema_version\": 3").And.Contain(EstimatePartialPricedDraftPolicy.PackageKind);
        }
        int sourcePriceStyle;
        using (var zip = ZipFile.OpenRead(written.XlsxPath))
        using (var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open())
        {
            var xml = System.Xml.Linq.XDocument.Load(sheet);
            System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var rows = xml.Descendants(ns + "row").ToList();
            rows.Count.Should().BeLessThan(100, "the main sheet contains priced groups, not every unresolved source object");
            xml.Descendants(ns + "t").Should().Contain(text => text.Value.Contains(EstimatePartialPricedDraftPolicy.DraftNotice));
            var subtotalRows = rows.Where(row => row.Descendants(ns + "t").Any(text =>
                text.Value == EstimatePartialPricedDraftPolicy.SubtotalNotice)).OrderBy(row => (int)row.Attribute("r")!).ToList();
            subtotalRows.Should().HaveCount(2);
            var openingSubtotalRow = (int)subtotalRows[0].Attribute("r")!;
            var lastSubtotalRow = (int)subtotalRows[1].Attribute("r")!;
            openingSubtotalRow.Should().Be(4, "the partial total must be visible before any appendix");
            var boqHeaderRow = rows.Single(row => row.Descendants(ns + "t").Any(text => text.Value == "מס' קטלוגי"));
            var frozenRows = int.Parse(xml.Descendants(ns + "pane").Single().Attribute("ySplit")!.Value, CultureInfo.InvariantCulture);
            frozenRows.Should().Be((int)boqHeaderRow.Attribute("r")!, "freeze the actual BOQ labels instead of a stale hard-coded row");
            lastSubtotalRow.Should().BeGreaterThan(frozenRows);
            subtotalRows[0].Descendants(ns + "f").Single().Value.Should().Be("G" + lastSubtotalRow,
                "the opening subtotal links to the canonical sum instead of duplicating it");
            var pricedRow = rows.Single(row => row.Elements(ns + "c").Any(cell =>
                cell.Descendants(ns + "t").Any(text => text.Value == code)));
            var priceCell = pricedRow.Elements(ns + "c").Single(cell => cell.Attribute("r")!.Value == "F" + pricedRow.Attribute("r")!.Value);
            priceCell.Attribute("t").Should().BeNull("source prices stay numeric");
            sourcePriceStyle = int.Parse(priceCell.Attribute("s")!.Value, CultureInfo.InvariantCulture);
            decimal.Parse(priceCell.Element(ns + "v")!.Value, CultureInfo.InvariantCulture).Should().Be(catalog.Prices[code].Price!.Value);
            using var traceStream = zip.GetEntry("xl/worksheets/sheet4.xml")!.Open();
            var trace = System.Xml.Linq.XDocument.Load(traceStream);
            var traceIds = trace.Descendants(ns + "t").Select(text => text.Value).Where(text => text.StartsWith("L", StringComparison.Ordinal) && text.Contains(" / ")).ToList();
            traceIds.Should().BeEquivalentTo(eligible.Select(line => $"{line.LineId} / {line.RecordId}"),
                "every included record retains its full trace on the dedicated sheet");
        }
        using (var priceZip = ZipFile.OpenRead(written.XlsxPath))
        using (var stylesStream = priceZip.GetEntry("xl/styles.xml")!.Open())
        {
            var styles = System.Xml.Linq.XDocument.Load(stylesStream);
            System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var priceFormat = styles.Root!.Element(ns + "cellXfs")!.Elements(ns + "xf").ElementAt(sourcePriceStyle);
            priceFormat.Attribute("numFmtId")!.Value.Should().Be("0", "General retains source-price precision rather than a two-decimal display");
        }
        Math.Round(eligible.Sum(line => Math.Round((decimal)line.RawQuantity, 4, MidpointRounding.AwayFromZero)) *
            Math.Round(catalog.Prices[code].Price!.Value, 2, MidpointRounding.AwayFromZero), 2, MidpointRounding.AwayFromZero)
            .Should().NotBe(expectedSubtotal, "this real source price demonstrates why hiding its extra decimals misleads the reader");
        using var manifest = JsonDocument.Parse(File.ReadAllText(written.ManifestPath));
        manifest.RootElement.GetProperty("package_kind").GetString().Should().Be(EstimatePartialPricedDraftPolicy.PackageKind);
        manifest.RootElement.GetProperty("workbook").GetProperty("sha256").GetString().Should().Be(written.XlsxHash);
        ArtifactHash.Sha256OfFile(profilePath).Should().Be(activeProfileBefore);
        (buildSeconds + evaluationSeconds + writerSeconds).Should().BeLessThan(120,
            "this bounded native-scale replay should finish calculation/export in two minutes on the acceptance host");
        Report("passed");
    }

    private sealed class RunOutputCleanup : IDisposable
    {
        private readonly string _path;
        private readonly bool _keep;
        public RunOutputCleanup(string path, bool keep) { _path = path; _keep = keep; }
        public void Dispose()
        {
            if (_keep) return;
            try { Directory.Delete(_path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
