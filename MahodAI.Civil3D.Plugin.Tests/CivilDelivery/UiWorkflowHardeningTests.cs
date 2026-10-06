using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public class UiWorkflowHardeningTests
    {
        private static ClSourceRecord Cl(string id) => new()
        {
            RecordId = id,
            SourceDrawing = "CL.dwg",
            SourceDrawingHash = "hash-cl",
            SourceHandle = id,
            SourceEntityType = "LINE",
            SourceLayer = "CL",
            SourceEndpoints = new[] { 0.0, 10.0, 0.0, -10.0 },
            WcsEndpoints = new[] { 0.0, 10.0, 0.0, -10.0 },
        };

        private static SectionPlanRecord Unchanged(string id) => new()
        {
            RecordId = id,
            Cl = Cl(id),
            Action = PlanAction.Unchanged,
            Status = DeliveryStatus.Ready,
        };

        private static string PluginFile(params string[] path) => Path.Combine(
            EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", Path.Combine(path));

        [Fact]
        public void ScanLog_IsBoundedPerFindingCodeAndInTotal()
        {
            // 07/09 18:56: 75,321 blocking findings logged one by one froze the palette
            // (each Log rebuilt a growing multi-megabyte TextBox on the UI thread).
            var control = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
            control.Should().Contain("LogFindingsBounded(blockers);")
                .And.Contain("LogFindingsBounded(_catalogFindings.ToList());")
                .And.Contain("private void LogFindingsBounded(IReadOnlyList<DeliveryFinding> findings)")
                .And.Contain("items.Take(MaxLoggedFindingsPerCode)")
                .And.Contain("if (_activity.Length > MaxActivityChars) _activity.Length = MaxActivityChars;")
                .And.NotContain("foreach (var finding in blockers) Log(")
                .And.NotContain("foreach (var finding in _catalogFindings) Log(");
            CivilDeliveryControl.MaxLoggedFindingsPerCode.Should().BeInRange(3, 20);
            CivilDeliveryControl.MaxActivityChars.Should().BeInRange(50_000, 2_000_000);
        }

        [Fact]
        public void DirtyEstimateScan_UsesExplicitQsaveAndFailClosedContinuation()
        {
            var control = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
            var palette = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryPalette.cs"));
            var commands = File.ReadAllText(PluginFile("CivilDelivery", "Commands", "MhdEstimateCommand.cs"));
            var save = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryControl.SaveGuidance.cs"));

            control.Should().Contain("EstimateSourceSnapshotPolicy.ForScan")
                .And.Contain("OfferExplicitSaveAndResume")
                .And.Contain("EnsureSavedForAction(\"סריקת כמויות\", OnScan)")
                .And.Contain("_pendingWorkflowSaveDocument != null")
                .And.Contain("!estimateSavePending")
                .And.Contain("לא נשלחה פקודת שמירה נוספת")
                .And.NotContain("doc.Database.SaveAs(");
            save.Should().Contain("doc.SendStringToExecute(\"_.QSAVE\\n\", true, false, false);")
                .And.NotContain("_.QSAVE\\nMHD_DELIVERY_AFTER_SAVE\\n")
                .And.Contain("CommandEnded += OnWorkflowSaveEnded")
                .And.Contain("private void OnWorkflowSaveEnded(")
                .And.Contain("WorkflowSavePhase.ContinuationQueued")
                .And.Contain("MessageBoxButton.YesNo")
                .And.Contain("SavedDrawingContinuationPolicy.CanConsume(")
                .And.Contain("ReferenceEquals(doc, expected)")
                .And.Contain("doc!.Database.UnmanagedObject == expectedDatabase")
                .And.Contain("source.DbMod, source.Failure")
                .And.Contain("CommandCancelled += OnWorkflowSaveCancelled")
                .And.Contain("Dispatcher.BeginInvoke(new Action(RefreshGates))");
            var legacyStart = control.IndexOf("internal void ResumeEstimateScanAfterExplicitSave()", StringComparison.Ordinal);
            var legacyEnd = control.IndexOf("private void ShowEstimateSourceBlocker", legacyStart, StringComparison.Ordinal);
            control[legacyStart..legacyEnd].Should().NotContain("OnScan(")
                .And.NotContain("_pendingWorkflowSaveToken =")
                .And.NotContain("CancelWorkflowSave(");
            palette.Should().Contain("ResumeEstimateScanAfterExplicitSave");
            commands.Should().Contain("[CommandMethod(CivilDeliveryCommandNames.EstimateScanAfterSave")
                .And.Contain("CommandFlags.NoHistory");
        }

        [Fact]
        public void EstimatePalette_ExposesAllFiveGuidedStagesAndActionableGates()
        {
            var xaml = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryControl.xaml"));
            var control = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));

            xaml.Should().Contain("EstimateFlowProgress")
                .And.Contain("EstimateNextAction");
            control.Should().Contain("EstimateFlowPolicy.Evaluate")
                .And.Contain("שמור וסרוק")
                .And.NotContain("נדרש אישור מקורות לפני הסריקה")
                .And.NotContain("נדרשת החלטת עבודות עפר לפני הסריקה")
                .And.Contain("earthworksDecision.IsResolved,")
                .And.Contain("SelectNextQuantityRow")
                .And.Contain("_catalogFindings");

            // Reviewed-line pricing may proceed while earthworks is undecided;
            // that is not permission for the separate final-estimate export.
            var buildStart = control.IndexOf("BtnBuild.IsEnabled =", StringComparison.Ordinal);
            var exportStart = control.IndexOf("BtnExport.IsEnabled =", buildStart, StringComparison.Ordinal);
            var exportEnd = control.IndexOf("RefreshPricedDraftExport(", exportStart, StringComparison.Ordinal);
            buildStart.Should().BeGreaterThan(0);
            exportStart.Should().BeGreaterThan(buildStart);
            exportEnd.Should().BeGreaterThan(exportStart);
            control[buildStart..exportStart].Should().Contain("profileUsable && estimateScanFresh")
                .And.Contain("estimateScopeApproved")
                .And.Contain("_scan!.Records.Count > 0 && _catalog != null")
                .And.NotContain("earthworksDecision.IsResolved");
            control[exportStart..exportEnd].Should().Contain("EstimatePreflightPolicy.CanExport(_estimateResult)")
                .And.Contain("estimateScanFresh");
            var workflow = File.ReadAllText(PluginFile("CivilDelivery", "Estimate", "EstimateWorkflowService.cs"));
            var finalStart = workflow.IndexOf("internal static void RequireFinalEstimateScope(", StringComparison.Ordinal);
            var finalEnd = workflow.IndexOf("internal static void RequireReviewedSourceScope(", finalStart, StringComparison.Ordinal);
            workflow[finalStart..finalEnd].Should().Contain("RequireReviewedSourceScope(profile)")
                .And.Contain("!GetEarthworksDecision(profile).IsResolved")
                .And.Contain("throw new InvalidOperationException(EstimateGuidedActionPolicy.EarthworksNotAssessed)");
        }

        [Fact]
        public void ClosedPolylineAlternative_IsMatchedByExactSourcesAndReviewedNext()
        {
            var control = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
            var viewModels = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryViewModels.cs"));
            var command = File.ReadAllText(PluginFile(
                "CivilDelivery", "Commands", "MhdEstimateCommand.cs"));

            control.Should().Contain("ClosedPolylineAlternativePolicy")
                .And.Contain("EstimateReviewContinuationSelection.Select(")
                .And.Contain("_quantityRows, mappedRuleKeys, previousRuleKey, preservePreviousSelection)")
                .And.Contain("SaveApprovedClosedPolylineMapping")
                .And.Contain("הכלי אינו מתמחר את שתי החלופות יחד")
                .And.Contain("חלופת מדידה לא נבחרת לאותם פוליליינים סגורים");
            var selection = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryControl.ProjectPrice.cs"));
            selection.Should().Contain("previous.AlternativeRuleKey")
                .And.Contain("!candidate.IsIgnored && string.IsNullOrWhiteSpace(candidate.CatalogCode)")
                .And.Contain("mappedRuleKeys?.Count == 1");
            viewModels.Should().Contain("IsUnselectedClosedPolylineAlternative")
                .And.Contain("AlternativeCatalogCode")
                .And.Contain("CanApproveCatalogMapping");
            command.Should().Contain("ClosedPolylineAlternativePolicy.FindUnambiguousExactPairs(scan.Records)")
                .And.Contain("lockedAlternatives.Contains(group.Key)")
                .And.Contain("SaveApprovedMappingsWithClosedPolylineExclusions");
        }

        [Fact]
        public void SharedPalette_SeparatesWorkflowIdentityAndDoesNotHashOnRowSelection()
        {
            var control = File.ReadAllText(PluginFile(
                "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));

            control.Should().Contain("_sectionResultsDrawing")
                .And.Contain("_estimateResultsDrawing")
                .And.NotContain("private string? _resultsDrawing;");
            control.Should().Contain("EstimateScanIsKnownStale")
                .And.Contain("DrawingRevisionTracker.Capture(doc.Database)")
                .And.Contain("VerifyEstimateSourcesForAction");
            (control.Split(new[] { "EstimateWorkflowService.FreshnessReason" },
                    StringSplitOptions.None).Length - 1)
                .Should().Be(1,
                    "full host+XREF hashing belongs only to the action verifier, not RefreshGates/row selection");
        }

        [Fact]
        public void SectionVerifyRetry_ClearsPriorGreenBeforeReadBackAndAfterExceptions()
        {
            var control = File.ReadAllText(PluginFile(
                "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));

            var batchStart = control.IndexOf(
                "private void OnVerify(object sender", StringComparison.Ordinal);
            var batchEnd = control.IndexOf(
                "private void RebuildSectionRows(", batchStart, StringComparison.Ordinal);
            batchStart.Should().BeGreaterThanOrEqualTo(0);
            batchEnd.Should().BeGreaterThan(batchStart);
            var batch = control.Substring(batchStart, batchEnd - batchStart);
            batch.IndexOf("ResetDisplayedSectionVerification(", StringComparison.Ordinal)
                .Should().BeLessThan(batch.IndexOf("_sections.Verify(", StringComparison.Ordinal));
            batch[(batch.IndexOf("catch (Exception ex)", StringComparison.Ordinal))..]
                .Should().Contain("ResetDisplayedSectionVerification(")
                .And.Contain("restoreApplyBaseline: true");

            var selectedStart = control.IndexOf(
                "private void OnVerifySelected(object sender", StringComparison.Ordinal);
            var selectedEnd = control.IndexOf(
                "private void InvalidateEstimateEvidence(", selectedStart, StringComparison.Ordinal);
            selectedStart.Should().BeGreaterThanOrEqualTo(0);
            selectedEnd.Should().BeGreaterThan(selectedStart);
            var selected = control.Substring(selectedStart, selectedEnd - selectedStart);
            selected.IndexOf("ResetDisplayedSectionVerification(", StringComparison.Ordinal)
                .Should().BeLessThan(selected.IndexOf("_sections.VerifySelectedCurrent(", StringComparison.Ordinal));
            selected[(selected.IndexOf("catch (Exception ex)", StringComparison.Ordinal))..]
                .Should().Contain("ResetDisplayedSectionVerification(")
                .And.Contain("restoreApplyBaseline: true");
        }

        [Fact]
        public void SectionApplyRetry_ClearsPriorApplyAndVerifyGreenBeforeCivilAndOnFailure()
        {
            var control = File.ReadAllText(PluginFile(
                "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));

            var batchStart = control.IndexOf(
                "private void OnApply(object sender", StringComparison.Ordinal);
            var batchEnd = control.IndexOf(
                "private void OnApplySelected(", batchStart, StringComparison.Ordinal);
            batchStart.Should().BeGreaterThanOrEqualTo(0);
            batchEnd.Should().BeGreaterThan(batchStart);
            var batch = control.Substring(batchStart, batchEnd - batchStart);
            batch.IndexOf("ResetDisplayedSectionApplyAttempt(", StringComparison.Ordinal)
                .Should().BeLessThan(batch.IndexOf("_sections.Apply(", StringComparison.Ordinal));
            batch[(batch.IndexOf("catch (Exception ex)", StringComparison.Ordinal))..]
                .Should().Contain("ResetDisplayedSectionApplyAttempt(");

            var selectedStart = control.IndexOf(
                "private void OnApplySelected(", StringComparison.Ordinal);
            var selectedEnd = control.IndexOf(
                "private void OnVerify(object sender", selectedStart, StringComparison.Ordinal);
            selectedStart.Should().BeGreaterThanOrEqualTo(0);
            selectedEnd.Should().BeGreaterThan(selectedStart);
            var selected = control.Substring(selectedStart, selectedEnd - selectedStart);
            selected.IndexOf("ResetDisplayedSectionApplyAttempt(", StringComparison.Ordinal)
                .Should().BeLessThan(selected.IndexOf("_sections.ApplySelected(", StringComparison.Ordinal));
            selected[(selected.IndexOf("catch (Exception ex)", StringComparison.Ordinal))..]
                .Should().Contain("ResetDisplayedSectionApplyAttempt(");

            var resetStart = control.IndexOf(
                "private void ResetDisplayedSectionApplyAttempt", StringComparison.Ordinal);
            var resetEnd = control.IndexOf(
                "private void ResetDisplayedSectionPlanAttempt", resetStart, StringComparison.Ordinal);
            var reset = control.Substring(resetStart, resetEnd - resetStart);
            reset.Should().Contain("_apply = null;")
                .And.Contain("_verifySummary = null;")
                .And.Contain("_sectionDisplayStatuses.Clear();");
        }

        [Fact]
        public void SectionPlanRetry_WithdrawsTheEntireOldGreenChainBeforeCivilAndOnFailure()
        {
            var control = File.ReadAllText(PluginFile(
                "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
            var start = control.IndexOf(
                "private void OnPlan(object sender", StringComparison.Ordinal);
            var end = control.IndexOf(
                "private void OnResolveSection(", start, StringComparison.Ordinal);
            start.Should().BeGreaterThanOrEqualTo(0);
            end.Should().BeGreaterThan(start);
            var method = control.Substring(start, end - start);
            method.IndexOf("ResetDisplayedSectionPlanAttempt();", StringComparison.Ordinal)
                .Should().BeLessThan(method.IndexOf("ReloadProfile();", StringComparison.Ordinal),
                    "even a failed authoritative profile reread begins a new PLAN attempt");
            method.IndexOf("ResetDisplayedSectionPlanAttempt();", StringComparison.Ordinal)
                .Should().BeLessThan(method.IndexOf("_sections.Plan(", StringComparison.Ordinal));
            method[(method.IndexOf("catch (Exception ex)", StringComparison.Ordinal))..]
                .Should().Contain("ResetDisplayedSectionPlanAttempt();");

            var resetStart = control.IndexOf(
                "private void ResetDisplayedSectionPlanAttempt", StringComparison.Ordinal);
            var resetEnd = control.IndexOf(
                "private static DeliveryFinding? EvidenceWriteFailure", resetStart,
                StringComparison.Ordinal);
            var reset = control.Substring(resetStart, resetEnd - resetStart);
            reset.Should().Contain("_plan = null;")
                .And.Contain("_apply = null;")
                .And.Contain("_verifySummary = null;")
                .And.Contain("_sectionDisplayStatuses.Clear();")
                .And.Contain("_sectionRows.Clear();")
                .And.Contain("_sectionResultsDrawing = null;");
        }

        [Fact]
        public void SectionPaletteGreen_IsBoundToLiveHostAndExternalSourceEvidence()
        {
            var control = File.ReadAllText(PluginFile(
                "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));

            var staleStart = control.IndexOf(
                "private bool PlanIsStale()", StringComparison.Ordinal);
            var staleEnd = control.IndexOf(
                "private ActiveProjectProfileService.ActiveLoadResult RequireFreshSectionPlan",
                staleStart, StringComparison.Ordinal);
            staleStart.Should().BeGreaterThanOrEqualTo(0);
            staleEnd.Should().BeGreaterThan(staleStart);
            var stale = control.Substring(staleStart, staleEnd - staleStart);
            stale.Should().Contain("_apply!.PostApplyDatabaseRevision")
                .And.Contain("_lastVerifyResult.VerifiedDatabaseRevision")
                .And.Contain("_applyPlanRunId == _plan.RunId")
                .And.Contain("_plan.SourceDatabaseRevision")
                .And.Contain("ActiveProjectProfileService.ReloadForExistingWorkflow(")
                .And.Contain("_profileSource, _profileWriteTarget")
                .And.Contain("SectionInputIntegrityService.StaleReason(")
                .And.Contain("doc.Database, _plan, expectedRevision")
                .And.Contain("SectionsWorkflowService.RequirePlanEvidence(_plan);")
                .And.Contain("SectionsWorkflowService.RequireApplyEvidence(_apply!);")
                .And.Contain("RuntimeRunManifestService.RequirePublishedArtifact(")
                .And.Contain("\"verify_result.json\"")
                .And.Contain("catch")
                .And.Contain("return true;");

            var freshnessStart = control.IndexOf(
                "private ActiveProjectProfileService.ActiveLoadResult RequireFreshSectionPlan",
                StringComparison.Ordinal);
            var freshnessEnd = control.IndexOf(
                "private bool EstimateScanIsKnownStale", freshnessStart,
                StringComparison.Ordinal);
            var freshness = control.Substring(
                freshnessStart, freshnessEnd - freshnessStart);
            freshness.Should().Contain(
                    "ActiveProjectProfileService.ReloadForExistingWorkflow(")
                .And.Contain("SectionPlanLogic.ScopeStaleReason(")
                .And.Contain("SectionInputIntegrityService.StaleReason(");
            control.Split("_lastVerifyResult = verify;", StringSplitOptions.None)
                .Should().HaveCount(3,
                    "batch and selected VERIFY must retain the exact result whose evidence stays green");

            var integrity = File.ReadAllText(PluginFile(
                "CivilDelivery", "Sections", "Services", "SectionInputIntegrityService.cs"));
            integrity.Should().Contain("ValidateExternalSourcesCurrent(plan, stage)")
                .And.Contain("ClInstructionReader.HashFileShared(path)")
                .And.Contain("DrawingRevisionTracker.Capture(db)");

            var gatesStart = control.IndexOf(
                "private void RefreshGates()", StringComparison.Ordinal);
            var gatesEnd = control.IndexOf(
                "private static bool EstimateSaveMayBeRequired", gatesStart,
                StringComparison.Ordinal);
            gatesStart.Should().BeGreaterThanOrEqualTo(0);
            gatesEnd.Should().BeGreaterThan(gatesStart);
            var gates = control.Substring(gatesStart, gatesEnd - gatesStart);
            gates.Should().Contain("var planStale = PlanIsStale();")
                .And.Contain("ResetDisplayedSectionVerification(")
                .And.Contain("restoreApplyBaseline: false")
                .And.Contain("planStale && _previewShown")
                .And.Contain("showDialog: false")
                .And.Contain("profileUsable, _plan, planStale")
                .And.Contain("var selectedApplyReady = profileUsable && _plan != null &&")
                .And.Contain("BtnApplySelected.IsEnabled = selectedApplyReady &&")
                .And.Contain("(_apply is not { Committed: true } || canRebuildSelected);")
                .And.Contain("_applyAwaitsFreshPlan = selectedApplyReady && _apply is { Committed: true } &&")
                .And.Contain("!planStale &&");
            var workflow = File.ReadAllText(PluginFile(
                "CivilDelivery", "Sections", "Services", "SectionsWorkflowService.cs"));
            workflow.Should().Contain("doc.Database, plan, plan.SourceDatabaseRevision, \"VERIFY-REFRESH\"")
                .And.Contain("? plan : Plan(doc, profile, profileHash)")
                .And.Contain("SectionVerificationRecoveryService.RecoverSelected(doc, current, recordId)")
                .And.Contain("result.VerifiedDatabaseRevision = DrawingRevisionTracker.Capture(doc.Database)");
        }

        [Fact]
        public void EstimateRescan_InvalidatesOldBuildAndExportBeforeCivilTraversal()
        {
            var control = File.ReadAllText(PluginFile(
                "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
            var methodStart = control.IndexOf(
                "private void RunEstimateScan(Document doc)", StringComparison.Ordinal);
            var methodEnd = control.IndexOf(
                "private void SelectNextQuantityRow()", methodStart, StringComparison.Ordinal);
            methodStart.Should().BeGreaterThanOrEqualTo(0);
            methodEnd.Should().BeGreaterThan(methodStart);
            var method = control.Substring(methodStart, methodEnd - methodStart);

            var invalidate = method.IndexOf("InvalidateEstimateEvidence(", StringComparison.Ordinal);
            var scan = method.IndexOf("_estimate.Scan(", StringComparison.Ordinal);
            invalidate.Should().BeGreaterThanOrEqualTo(0);
            scan.Should().BeGreaterThan(invalidate,
                "a failed replacement scan must leave no previous estimate/export evidence actionable");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ClosedPolylineApproval_AtomicallyMapsOneAndPersistsSiblingExclusion(
            bool approveArea)
        {
            var root = Path.Combine(Path.GetTempPath(),
                "mcd-closed-choice-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var profile = EstimateFixtures.Profile();
                var areaKey = "layer:PAVING|area";
                var lengthKey = "layer:PAVING|length";
                var area = EstimateFixtures.Record(
                    "closed-area", null!, 80, "מ\"ר", kind: "area", handle: "AA10",
                    ruleKey: areaKey, layer: "PAVING", method: "closed-polyline-area");
                var length = EstimateFixtures.Record(
                    "closed-length", null!, 42, "מטר", kind: "length", handle: "AA10",
                    ruleKey: lengthKey, layer: "PAVING", method: "closed-polyline-perimeter");
                var scan = new EstimateWorkflowService.ScanResult
                {
                    RunId = "closed-choice-run",
                    ProjectProfileId = profile.ProfileId,
                    ProfileSource = Path.Combine(root, "profile.yaml"),
                    SourceDrawing = @"C:\drawings\PD.dwg",
                    ProjectProfileHash = new string('a', 64),
                    ProjectProfileEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(profile),
                    DatabaseRevision = "db:1",
                    SourceDrawingHash = new string('b', 64),
                    SourceDbMod = 0,
                    Records = { area, length },
                    DiscoveryMode = true,
                };
                var selected = approveArea ? area : length;
                var selectedKey = approveArea ? areaKey : lengthKey;
                var siblingKey = approveArea ? lengthKey : areaKey;
                var code = approveArea ? "U51.01.0090" : "U51.01.0250";
                var approval = new EstimateWorkflowService.MappingApproval(
                    selectedKey, code, "PAVING", selected.Source.EntityType,
                    selected.Measurement.Kind, selected.Measurement.Unit);

                var saved = new EstimateWorkflowService().SaveApprovedClosedPolylineMapping(
                    profile, EstimateFixtures.Snapshot(), scan, approval, siblingKey,
                    "nataly", scan.ProfileSource);

                var decision = EstimateWorkflowService.ApprovedIgnoredRuleDecision(
                    profile, siblingKey);
                decision.Should().NotBeNull();
                decision!.ApprovedBy.Should().Be("nataly");
                decision.Reason.Should().Contain("אותם פוליליינים סגורים");
                profile.Estimate.QuantitySources.Rules.Should().ContainSingle(rule =>
                    rule.RuleKey == selectedKey && rule.CandidateCatalogCode == code);

                var rebased = EstimateWorkflowService.RebaseAfterProfileDecision(
                    scan, profile, saved, selectedKey);
                rebased.Findings.Should().NotContain(finding =>
                    finding.Code == EstimateFindingCodes.MixedDimensionLayer);

                var reloaded = ProjectProfileLoader.LoadFromFile(saved.Path);
                reloaded.IsUsable.Should().BeTrue();
                reloaded.Profile.Should().NotBeNull();
                EstimateWorkflowService.ApprovedIgnoredRuleDecision(
                    reloaded.Profile, siblingKey).Should().NotBeNull();

                var application = EstimateWorkflowService.ApplyIgnoredRules(
                    rebased.Records, reloaded.Profile!);
                application.PricedRecords.Should().ContainSingle(record =>
                    record.Classification.RuleKey == selectedKey);
                application.Exclusions.Should().ContainSingle(exclusion =>
                    exclusion.RuleKey == siblingKey);
                var result = EstimateBuilder.Build(
                    application.PricedRecords, EstimateFixtures.Snapshot(), reloaded.Profile!);
                result.Exclusions.AddRange(application.Exclusions);
                result.Findings.AddRange(application.AuditFindings);
                result.Lines.Should().ContainSingle();
                EstimatePreflightPolicy.CanExport(result).Should().BeTrue();
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Fact]
        public void AllUnchangedPlan_DoesNotOfferFabricatedApplyOrVerifyEvidence()
        {
            var plan = new SectionPlan { RunId = "run", ProjectProfileId = "6422" };
            plan.Records.Add(Unchanged("A1"));

            var beforeApply = WorkflowGate.From(
                profileUsable: true, plan, planStale: false, apply: null, previewShown: false);

            beforeApply.CanApply.Should().BeFalse();
            beforeApply.IsNoOpBaseline.Should().BeFalse();
            beforeApply.CanVerify.Should().BeFalse();
            beforeApply.Reason.Should().Contain("אינו ראיית אימות");

            var noOp = new SectionApplyResult { RunId = "run", Committed = true };
            noOp.Records.Add(new SectionApplyRecordResult
            {
                RecordId = "A1",
                ActionTaken = PlanAction.Unchanged,
                Status = DeliveryStatus.Verified,
            });

            var afterApply = WorkflowGate.From(
                profileUsable: true, plan, planStale: false, apply: noOp, previewShown: false);

            afterApply.CanApply.Should().BeFalse();
            afterApply.IsNoOpBaseline.Should().BeFalse();
            afterApply.CanVerify.Should().BeFalse();
        }

        [Fact]
        public void FullyApprovedExclusionPlan_AlsoOffersNoOpBaseline()
        {
            var record = new SectionPlanRecord
            {
                RecordId = "A1",
                Cl = Cl("A1"),
                Action = PlanAction.Excluded,
                Status = DeliveryStatus.Ready,
                ExplicitExclusion = new SectionExclusionPlan
                {
                    FindingCode = "SEC-EXPLICIT",
                    Reason = "outside the approved delivery scope",
                    ApprovedBy = "nataly",
                    ApprovedAtUtc = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
                },
            };
            var plan = new SectionPlan { RunId = "run", ProjectProfileId = "6422" };
            plan.Records.Add(record);

            var gate = WorkflowGate.From(
                profileUsable: true, plan, planStale: false, apply: null, previewShown: false);

            gate.CanApply.Should().BeTrue();
            gate.IsNoOpBaseline.Should().BeTrue();
            gate.Reason.Should().Contain("ראיות ההחרגה");
        }

        [Fact]
        public void GlobalPlanReviewFinding_BlocksApplyAndNamesTheSourceProblem()
        {
            var plan = new SectionPlan { RunId = "run", ProjectProfileId = "6422" };
            plan.Records.Add(Unchanged("A1"));
            plan.Findings.Add(new DeliveryFinding
            {
                Code = SectionFindingCodes.PlanMarksMissing,
                Severity = FindingSeverity.ReviewRequired,
                Domain = "sections",
                Title = "אין סימוני תכנית מוכחים",
            });

            var gate = WorkflowGate.From(
                profileUsable: true, plan, planStale: false, apply: null, previewShown: false);

            gate.CanApply.Should().BeFalse();
            gate.CanPreview.Should().BeFalse();
            // SEC-m1 (review of 1.3.9): the main card names the problem in Hebrew;
            // the machine code stays in the details panel.
            gate.Reason.Should().StartWith("התכנון חסום במקור: ")
                .And.Contain("אין סימוני תכנית מוכחים")
                .And.NotContain(SectionFindingCodes.PlanMarksMissing)
                .And.NotContain("«מוכן» אינם מושפעים",
                    "a blocker with no affected-record scope blocks every row");
        }

        [Fact]
        public void ScopedHatchBlockers_AreGroupedByFileLayerAndReason_AndReadyRowsAreNamedUnaffected()
        {
            var plan = new SectionPlan { RunId = "run", ProjectProfileId = "6422" };
            var ready = Unchanged("A1");
            plan.Records.Add(ready);
            DeliveryFinding Hatch(string handle, string reason, params string[] records)
            {
                var finding = new DeliveryFinding
                {
                    Code = "SEC-PROJECTION-GEOMETRY-UNSUPPORTED",
                    Severity = FindingSeverity.Error,
                    Domain = "sections",
                    Title = "גאומטריה בשכבה תואמת אינה ניתנת לקריאה — החתכים המושפעים חסומים",
                    Message = "role=plan-region; kind=sidewalk; label=מדרכה; entity=Hatch; " +
                              "layer=6422-HA-MODEL-NATAZ|HW_HA_SIDEWALK; handle=8BB299/" + handle + "; " + reason,
                    SourceRefs =
                    {
                        new ProvenanceRef
                        {
                            SourceKind = "xref",
                            SourcePathOrUri = @"C:\copies\6422-HA-MODEL-NATAZ.dwg",
                            EntityType = "Hatch",
                            Layer = "6422-HA-MODEL-NATAZ|HW_HA_SIDEWALK",
                            SourceHandle = "8BB299/" + handle,
                        },
                    },
                };
                finding.AffectedRecordIds.AddRange(records);
                return finding;
            }
            plan.Findings.Add(Hatch("262393", "Hatch loop 0 has unsupported flags External, NotClosed.", "cl-1"));
            plan.Findings.Add(Hatch("26236C", "Hatch loop has disconnected directed edges.", "cl-2"));
            plan.Findings.Add(Hatch("2623AF",
                "reason=Ring Self-intersection at or near point (203646.25, 649194.39).", "cl-3"));

            var gate = WorkflowGate.From(
                profileUsable: true, plan, planStale: false, apply: null, previewShown: false);

            gate.CanApply.Should().BeFalse();
            gate.Reason.Should().StartWith("חלק מהחתכים חסומים: ")
                .And.Contain("2 הצללות מדרכה בשכבה HW_HA_SIDEWALK בקובץ 6422-HA-MODEL-NATAZ.dwg")
                .And.Contain("לולאה פתוחה")
                .And.Contain("1 הצללת מדרכה בשכבה HW_HA_SIDEWALK")
                .And.Contain("חיתוך עצמי ליד (203646.25, 649194.39)")
                .And.Contain("החתכים המסומנים «מוכן» אינם מושפעים")
                .And.NotContain("SEC-PROJECTION-GEOMETRY-UNSUPPORTED")
                .And.NotContain("262393", "handles belong to the details panel")
                .And.NotContain("סגור את", "a self-intersecting hatch is not 'closed'; the action stays a check");
        }

        [Fact]
        public void SectionTaskSummary_ReportsEveryOutstandingDecisionClass()
        {
            var record = Unchanged("A1");
            record.PresentationCoverage.RowCandidateSourceKeys.Add("hash|GM.dwg|GM");
            record.PresentationCoverage.RowAuthorityState = "ambiguous";
            record.PresentationCoverage.UnresolvedSpans.Add(new SectionUnresolvedSpanPlan
            {
                LeftKind = "curb",
                RightKind = "curb",
                Reason = "manual decision required",
            });
            record.TrafficDirections.Add(new SectionTrafficDirectionPlan
            {
                StripLabel = "נתיב נסיעה",
                StripKind = "road",
                EvidenceMode = "motor",
                State = "unknown",
                Reason = "manual decision required",
            });
            record.PresentationCoverage.PlanMarkCount = 0;
            record.PresentationCoverage.Complete = false;
            record.SelectedCrossing = new AlignmentCrossing
            {
                AlignmentName = "ROAD",
                Point = new[] { 0d, 0d },
            };
            record.Findings.Add(new DeliveryFinding
            {
                Code = SectionFindingCodes.ClNoIntersection,
                Severity = FindingSeverity.ReviewRequired,
                Domain = "sections",
                Title = "ללא חיתוך",
            });
            var plan = new SectionPlan { RunId = "run", ProjectProfileId = "6422" };
            plan.Records.Add(record);

            var summary = WorkflowGate.SectionTaskSummary(plan);

            summary.Should().Contain("מקור ROW חסר ב־1 חתך")
                .And.Contain("1 שמות רצועות")
                .And.Contain("1 כיווני נסיעה")
                .And.Contain("1 ללא מקור רצועות חוצה")
                .And.Contain("1 ללא חיתוך");
        }

        [Fact]
        public void SharedRowSource_IsReportedAsAffectedSections_NotIndependentApprovals()
        {
            var plan = new SectionPlan { RunId = "shared-source", ProjectProfileId = "6422" };
            for (var index = 0; index < 27; index++)
            {
                var record = Unchanged("A" + index);
                record.PresentationCoverage.RowCandidateSourceKeys.Add("hash|GM.dwg|GM");
                record.PresentationCoverage.RowAuthorityState = "suppressed";
                plan.Records.Add(record);
            }

            WorkflowGate.SectionTaskSummary(plan)
                .Should().Contain("מקור ROW חסר ב־27 חתכים")
                .And.NotContain("27 אישורי ROW");
        }

        [Fact]
        public void SectionDecisionCapture_ContainsPreModalErrors_WithoutOpeningStaleDialogs()
        {
            var ui = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
            var scope = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryControl.SectionDecisionScope.cs"));
            foreach (var stage in new[] { "TRAFFIC-DIRECTION-DECISION", "SPAN-LABEL-DECISION", "ROW-AUTHORITY-DECISION", "SECTION-DECISION" })
            {
                var position = ui.IndexOf("\"" + stage + "\"", StringComparison.Ordinal);
                position.Should().BeGreaterThan(0);
                var lineStart = ui.LastIndexOf('\n', position);
                var lineEnd = ui.IndexOf('\n', position);
                ui.Substring(lineStart, lineEnd - lineStart)
                    .Should().Contain("if (!TryCaptureSectionDecisionScope(")
                    .And.Contain("out var decisionScope)) return;");
            }
            var wrapperStart = scope.IndexOf("private bool TryCaptureSectionDecisionScope(", StringComparison.Ordinal);
            var wrapperEnd = scope.IndexOf("private SectionDecisionScope CaptureSectionDecisionScope(", wrapperStart, StringComparison.Ordinal);
            scope.Substring(wrapperStart, wrapperEnd - wrapperStart)
                .Should().Contain("catch (Exception ex)")
                .And.Contain("ShowError(")
                .And.Contain("return false;");
        }

        [Fact]
        public void EveryDurableEstimateDecision_IsFreshnessChecked_AndRebasedForBatchReview()
        {
            var ui = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
            var workflow = File.ReadAllText(PluginFile(
                "CivilDelivery", "Estimate", "EstimateWorkflowService.cs"));
            var decisionGateStart = workflow.IndexOf(
                "internal static void RequireFreshForDecision(", StringComparison.Ordinal);
            decisionGateStart.Should().BeGreaterThanOrEqualTo(0);
            var decisionGateEnd = workflow.IndexOf(
                "internal static ScanResult RebaseAfterProfileDecision(",
                decisionGateStart, StringComparison.Ordinal);
            decisionGateEnd.Should().BeGreaterThan(decisionGateStart);
            var decisionGate = workflow[decisionGateStart..decisionGateEnd];

            ui.Should().Contain("RequireFreshForDecision(")
                .And.Contain("שמירת החלטת הרלוונטיות")
                .And.Contain("אישור המיפוי")
                .And.Contain("אישור מרוכז של סימוני עזר")
                .And.Contain("saved, profileForSave, mappedRuleKey: null, row.RuleKey")
                .And.Contain("saved, profileForSave, row.RuleKey, row.RuleKey")
                .And.Contain("BtnApprove.IsEnabled = profileUsable && estimateScanFresh")
                .And.Contain("BtnRelevance.IsEnabled = estimateScanFresh && profileUsable")
                .And.Contain("BtnFilterDrawingNoise.IsEnabled = estimateScanFresh && estimateScopeApproved &&")
                .And.Contain("estimateScopeApproved")
                .And.Contain("SaveIgnoredRuleDecisions(")
                .And.Contain("BtnTrace.IsEnabled = profileUsable && _estimateResult != null && estimateScanFresh");

            workflow.Should().Contain("RequireFresh(doc, scan, operation)")
                .And.Contain("RequirePublishedScanEvidence(scan)")
                .And.Contain("RebaseAfterProfileDecision(")
                .And.Contain("PublishProfileDecisionOrRestore(")
                .And.Contain("RestoreProfileAfterFailedDecisionEvidence(saved)")
                .And.Contain("SourceDrawingHash = scan.SourceDrawingHash")
                .And.Contain("DatabaseRevision = scan.DatabaseRevision")
                .And.Contain("ExternalSources = scan.ExternalSources.ToList()")
                .And.Contain("ProjectProfileHash = saved.NewHash")
                .And.Contain("ProjectProfileEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(profile)");
            decisionGate.Should().Contain("RequireFresh(doc, scan, operation)")
                .And.Contain("RequirePublishedScanEvidence(scan)",
                    "freshness alone cannot authorize a durable estimate decision");
            ui.Should().Contain("PublishProfileDecisionOrRestore(")
                .And.Contain("ReloadProfile();");
        }

        [Fact]
        public void BulkNoiseUi_ExposesOnlyClosedClassifierKinds_AndAReviewedDialog()
        {
            var vm = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryViewModels.cs"));
            var dialog = File.ReadAllText(PluginFile(
                "CivilDelivery", "UI", "QuantityNoiseBatchDecisionDialog.xaml"));
            var dialogCode = File.ReadAllText(PluginFile(
                "CivilDelivery", "UI", "QuantityNoiseBatchDecisionDialog.xaml.cs"));

            vm.Should().Contain("IsBulkNoiseCandidate")
                .And.Contain("QuantitySignificance.Kind.StationGeometry")
                .And.Contain("QuantitySignificance.Kind.Auxiliary")
                .And.Contain("string.IsNullOrWhiteSpace(CatalogCode)")
                .And.Contain("(ללא חוק)");
            dialog.Should().Contain("CandidatesGrid")
                .And.Contain("מפתח מדויק")
                .And.Contain("סיבת הסיווג")
                .And.Contain("ConfirmBox");
            dialogCode.Should().Contain("candidate.IsBulkNoiseCandidate")
                .And.Contain("ApproverBox.Text = ApproverContext.Session.Name ?? \"\"")
                .And.Contain("ConfirmBox.IsChecked == true");
        }

        [Fact]
        public void ApprovedMappingRebase_UpdatesClassificationAndStatus_ButPreservesSourceSnapshot()
        {
            var directory = Path.Combine(Path.GetTempPath(), "mcd-ui-rebase-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var profilePath = Path.Combine(directory, "6422.yaml");
                File.WriteAllText(profilePath, "profile_version: 2");
                var catalogHash = new string('a', 64);
                var itemFingerprint = new string('b', 64);
                var sourceHash = new string('c', 64);
                var oldProfileHash = new string('d', 64);
                var newProfileHash = ArtifactHash.Sha256OfText(
                    File.ReadAllText(profilePath));
                var profile = new ProjectProfile { ProfileId = "6422" };
                profile.Estimate.Catalog.CatalogFile = "book.xlsx";
                profile.Estimate.Catalog.CatalogFileHash = catalogHash;
                profile.Estimate.Pricing.PriceBookSnapshotId = "office-book";
                profile.Estimate.Pricing.PriceBookHash = catalogHash;
                profile.Estimate.PriceBooks.Add(new ProjectProfile.EstimateProfile.PriceBookEntry
                {
                    Id = "office-book",
                    File = "book.xlsx",
                    FileHash = catalogHash,
                });
                profile.Estimate.QuantitySources.Rules.Add(new
                    ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
                {
                    RuleKey = "layer:KERB|length|m",
                    LayerPattern = "KERB",
                    EntityType = "LINE",
                    MeasurementKind = "length",
                    ExpectedUnit = "מטר",
                    CandidateCatalogCode = "51.06.1900",
                    ApprovedBy = "nataly",
                    ApprovedAtUtc = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
                    ApprovedCatalogId = "office-book",
                    ApprovedCatalogHash = catalogHash,
                    ApprovedCatalogItemFingerprint = itemFingerprint,
                });

                var unmapped = new DeliveryFinding
                {
                    Code = EstimateFindingCodes.Unmapped,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "unmapped",
                    AffectedRecordIds = { "Q1" },
                };
                var record = new NeutralQuantityRecord
                {
                    RecordId = "Q1",
                    ProjectProfileId = "6422",
                    RunId = "run",
                    Source = new QuantitySource
                    {
                        Drawing = "host.dwg",
                        DrawingPath = @"C:\drawings\host.dwg",
                        DrawingHash = sourceHash,
                        Handle = "A1",
                        EntityType = "LINE",
                        Layer = "KERB",
                    },
                    Measurement = new QuantityMeasurement
                    {
                        Kind = "length",
                        Method = "line-length",
                        RawValue = 12.5,
                        Unit = "מטר",
                    },
                    Classification = new QuantityClassification
                    {
                        RuleKey = "layer:KERB|length|m",
                        Tags = { "discovered" },
                    },
                    Status = DeliveryStatus.ReviewRequired,
                    Findings = { unmapped },
                };
                var secondUnmapped = new DeliveryFinding
                {
                    Code = EstimateFindingCodes.Unmapped,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "unmapped",
                    AffectedRecordIds = { "Q2" },
                };
                var secondRecord = new NeutralQuantityRecord
                {
                    RecordId = "Q2",
                    ProjectProfileId = "6422",
                    RunId = "run",
                    Source = new QuantitySource
                    {
                        Drawing = "host.dwg",
                        DrawingPath = @"C:\drawings\host.dwg",
                        DrawingHash = sourceHash,
                        Handle = "A2",
                        EntityType = "LINE",
                        Layer = "KERB",
                    },
                    Measurement = new QuantityMeasurement
                    {
                        Kind = "length",
                        Method = "line-length",
                        RawValue = 7.5,
                        Unit = "מטר",
                    },
                    Classification = new QuantityClassification
                    {
                        RuleKey = "layer:KERB|length|m",
                        Tags = { "discovered" },
                    },
                    Status = DeliveryStatus.ReviewRequired,
                    Findings = { secondUnmapped },
                };
                var external = new EstimateExternalSource
                {
                    DrawingPath = @"C:\drawings\xref.dwg",
                    DrawingHash = new string('f', 64),
                    XrefChain = "SITE",
                    ReferenceHandlePath = "10",
                };
                var scan = new EstimateWorkflowService.ScanResult
                {
                    RunId = "run",
                    ProjectProfileId = "6422",
                    ProfileSource = profilePath,
                    SourceDrawing = @"C:\drawings\host.dwg",
                    ProjectProfileHash = oldProfileHash,
                    ProjectProfileEffectiveHash = new string('1', 64),
                    DatabaseRevision = "drawing-guid:7",
                    SourceDrawingHash = sourceHash,
                    SourceDbMod = 0,
                    ExternalSources = { external },
                    Records = { record, secondRecord },
                    Status = DeliveryStatus.ReviewRequired,
                };

                var rebased = EstimateWorkflowService.RebaseAfterProfileDecision(
                    scan, profile,
                    new ProjectProfileWriter.SaveResult(profilePath, profilePath + ".bak", 2, newProfileHash),
                    "layer:KERB|length|m");

                rebased.ProjectProfileHash.Should().Be(newProfileHash);
                rebased.SourceDrawingHash.Should().Be(sourceHash);
                rebased.DatabaseRevision.Should().Be("drawing-guid:7");
                rebased.ExternalSources.Should().ContainSingle().Which.Should().BeSameAs(external);
                rebased.Records.Should().HaveCount(2,
                    "one approved rule deterministically rebases every measured object in that exact group");
                rebased.Records.Should().OnlyContain(approved =>
                    approved.Classification.CandidateCatalogCode == "51.06.1900" &&
                    approved.Classification.ApprovedCatalogHash == catalogHash &&
                    approved.Classification.Tags.Contains("rule-classified") &&
                    !approved.Classification.Tags.Contains("discovered") &&
                    approved.Findings.All(finding =>
                        finding.Code != EstimateFindingCodes.Unmapped) &&
                    approved.Status == DeliveryStatus.Ready);
                rebased.Records.Select(record => record.Source.Handle)
                    .Should().BeEquivalentTo(new[] { "A1", "A2" },
                        "group approval changes classification, never source identity");
                rebased.Status.Should().Be(DeliveryStatus.Ready);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void XrefQuantityShow_UsesVerifiedHostWcsBounds_WithoutPretendingToSelectChildHandles()
        {
            var locator = File.ReadAllText(PluginFile(
                "CivilDelivery", "Estimate", "QuantityLocatorService.cs"));

            locator.Should().Contain("TryGeometryEvidenceExtents(")
                .And.Contain("r.Measurement.GeometryEvidence")
                .And.Contain("drawingUnits.LinearToMetres")
                .And.Contain("התמקדות לפי תחומי המקור")
                .And.Contain("לא ניתנים לבחירה ישירה")
                .And.NotContain("if (r.Source.Xref != null) { inXref++; continue; }");
        }

        [Fact]
        public void SectionPreviewCleanupFailure_BlocksPaletteAndDirectTransitions()
        {
            var ui = File.ReadAllText(PluginFile(
                "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
            var direct = File.ReadAllText(PluginFile(
                "CivilDelivery", "Commands", "MhdSectionsCommand.cs"));

            var uiHelperStart = ui.IndexOf(
                "private bool TryClearPreview", StringComparison.Ordinal);
            var uiHelperEnd = ui.IndexOf(
                "private void OnApply", uiHelperStart, StringComparison.Ordinal);
            var uiHelper = ui.Substring(uiHelperStart, uiHelperEnd - uiHelperStart);
            uiHelper.Should().Contain("_sections.ClearPreview();")
                .And.Contain("_previewShown = _sections.HasActivePreview;")
                .And.Contain("throw new InvalidOperationException(")
                .And.Contain("_previewCleanupBlockingStatus = message;")
                .And.Contain("_previewCleanupBlockingStatus = null;")
                .And.NotContain("_evidenceBlockingStatus = null;")
                .And.Contain("SetStatus(message);")
                .And.Contain("MahodLogger.Error(")
                .And.Contain("return false;");

            var clearStart = ui.IndexOf("private void OnClearPreview", StringComparison.Ordinal);
            var clearEnd = ui.IndexOf("private bool TryClearPreview", clearStart,
                StringComparison.Ordinal);
            var clear = ui.Substring(clearStart, clearEnd - clearStart);
            clear.IndexOf("if (!TryClearPreview", StringComparison.Ordinal).Should().BeLessThan(
                clear.IndexOf("התצוגה המקדימה נוקתה", StringComparison.Ordinal));
            ui.Should().Contain("if (!TryClearPreview(\"תכנון\")) return;")
                .And.Contain("if (!TryClearPreview(\"החלה\")) return;")
                .And.Contain("if (!TryClearPreview(\"החלת חתך נבחר\")) return;");

            direct.Should().Contain("if (!TryClearPreview(ed, \"PLAN\")) return;")
                .And.Contain("if (!TryClearPreview(ed, \"APPLY\")) return;")
                .And.Contain("if (!TryClearPreview(ed, \"סיום הפקודה\")) return;")
                .And.Contain("if (Workflow.HasActivePreview)")
                .And.Contain("var previewCleared = TryClearPreview(")
                .And.Contain("if (!previewCleared)");
        }

        [Fact]
        public void SectionProfileDecisions_UseAuthoritativeReloadCloneCasAndReadback()
        {
            var ui = File.ReadAllText(PluginFile(
                "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
            ui.Should().Contain("CloneProfileForDecision(currentProfile.Profile!)")
                .And.Contain("expectedState: decisionWrite.ExpectedState")
                .And.Contain("PublishSavedProfile(saved)")
                .And.Contain("JsonSerializer.Deserialize<ProjectProfile>(")
                .And.Contain("!string.Equals(_profileHash, saved.NewHash")
                .And.Contain("ProjectSetupService.CaptureReadySource(")
                .And.Contain("שמירת החלטה בפרופיל הפרויקט");

            foreach (var stage in new[]
                     {
                         "TRAFFIC-DIRECTION-DECISION",
                         "SPAN-LABEL-DECISION",
                         "ROW-AUTHORITY-DECISION",
                         "SECTION-DECISION",
                     })
            {
                var guard = ui.IndexOf($"\"{stage}\"", StringComparison.Ordinal);
                var clone = ui.IndexOf("CloneProfileForDecision(currentProfile.Profile!)",
                    guard, StringComparison.Ordinal);
                var save = ui.IndexOf("ProjectProfileWriter.Save(", clone,
                    StringComparison.Ordinal);
                var publish = ui.IndexOf("PublishSavedProfile(saved)", save,
                    StringComparison.Ordinal);
                guard.Should().BeGreaterThan(-1, stage);
                clone.Should().BeGreaterThan(guard, stage);
                save.Should().BeGreaterThan(clone, stage);
                publish.Should().BeGreaterThan(save, stage);
                ui.Substring(save, publish - save).Should()
                    .Contain("expectedState: decisionWrite.ExpectedState");
                ui.Substring(guard, clone - guard).Should()
                    .Contain("RequireSectionDecisionScope(decisionScope)");
            }

            // CAS state is now captured by the exact dialog-scope guard, after
            // authoritative freshness checks, rather than recaptured at Save.
            var scope = File.ReadAllText(PluginFile(
                "CivilDelivery", "UI", "CivilDeliveryControl.SectionDecisionScope.cs"));
            scope.Should().Contain("RequireFreshSectionPlan(scope.Stage)")
                .And.Contain("CaptureExpectedProfileState(currentProfile)")
                .And.Contain("ReferenceEquals(_plan, scope.Plan)")
                .And.Contain("ReferenceEquals(Doc(), scope.Document)");

            var ai = File.ReadAllText(PluginFile(
                "Tools", "CivilDelivery", "SectionsTools.cs"));
            ai.Should().Contain("SectionToolProfileFreshness.RequireCurrent(")
                .And.Contain("currentProfile.ProfileWriteState ??")
                .And.NotContain("ProjectProfileWriter.CaptureExpectedState(")
                .And.Contain("expectedState: expectedProfileState")
                .And.Contain("CivilDeliverySession.ClearSectionsContext();");
        }
    }
}
