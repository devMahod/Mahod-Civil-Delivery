using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using FamilyDecisionRequest = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.EstimateWorkflowService.FamilyDecisionRequest;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// TEST-ONLY mixed measured group (Codex partial-approval fixture, schema /2): S1, S2 and U1 carry the block attribute
    /// BUS SHELTER, U2 carries BENCH, all four on layer QX971 of one source with one block and one count basis; C1 on VQ204 is
    /// covered by an earlier, unrelated family decision. No real catalog code, price or engineering decision.
    /// </summary>
    internal static class PartitionApprovalFixture
    {
        internal const string Family = "bus-shelters", Source = "TEST-ONLY-GM-MODEL", Layer = "QX971", OtherLayer = "VQ204";
        internal const string ProfileId = "TEST-ONLY-PARTIAL-APPROVAL", Approver = "TEST ONLY APPROVER";
        internal static readonly string[] Proven = { "TEST-S1", "TEST-S2", "TEST-U1" };
        internal static EngineerBoqLibrary Library => EngineerBoqLibrary.RoadsV1;

        internal static NeutralQuantityRecord Record(string id, string layer, string subject) => new()
        {
            RecordId = id, ProjectProfileId = ProfileId, RunId = "TEST-ONLY-PARTIAL-RUN",
            Source = new QuantitySource { Drawing = "TEST-ONLY-NOT-A-REAL-DRAWING.dwg", DrawingHash = new string('a', 64),
                Handle = id, Xref = Source, EntityType = "BlockReference", Layer = Source + "|" + layer },
            Measurement = new QuantityMeasurement
            {
                Kind = "count", Method = "block-count", Unit = "unit", RawValue = 1,
                Parameters = new Dictionary<string, string>
                {
                    [EvidenceKeys.Schema] = EvidenceKeys.SchemaV1,
                    [EvidenceKeys.Schema + EvidenceKeys.StatusSuffix] = "read",
                    [EvidenceKeys.BlockNameEffective] = "TEST_OBJECT",
                    [EvidenceKeys.BlockAttributes + EvidenceKeys.StatusSuffix] = "read",
                    [EvidenceKeys.BlockAttributes] = JsonSerializer.Serialize(new { synthetic_injection = true,
                        source = new { handle = id }, observation = new { attributes = new[] { new { tag = "TEST_CLASS", value = subject } } } }),
                },
            },
            Classification = new QuantityClassification(),
        };

        internal static NeutralQuantityRecord[] Records() => new[]
        {
            Record("TEST-S1", Layer, "BUS SHELTER"), Record("TEST-S2", Layer, "BUS SHELTER"), Record("TEST-U1", Layer, "BUS SHELTER"),
            Record("TEST-U2", Layer, "BENCH"), Record("TEST-C1", OtherLayer, "BUS SHELTER"),
        };

        /// <summary>The real draft with the real local classifier, as the family review builds it.</summary>
        internal static EngineerBoqDraft Draft(IReadOnlyList<NeutralQuantityRecord> records,
            IReadOnlyList<ProjectProfile.EstimateProfile.FamilyDecision>? decisions) =>
            EngineerBoqDraftBuilder.Build(records, Array.Empty<DeliveryFinding>(),
                new CatalogSnapshot { SnapshotId = "TEST-ONLY-NO-PRICES", FileHash = new string('b', 64) },
                new Dictionary<string, string>(), Library,
                new EngineerDraftContext("TEST ONLY", ProfileId, "TEST-ONLY-PARTIAL-RUN", "TEST-ONLY-NOT-A-REAL-DRAWING.dwg",
                    "TEST NO PRICES SUPPLIED", Array.Empty<string>(), FamilyDecisions: decisions, Classifier: LocalFamilyClassifier.Instance));

        internal static FamilyDecisionReviewModel Review(IReadOnlyList<NeutralQuantityRecord> records,
            IReadOnlyList<ProjectProfile.EstimateProfile.FamilyDecision> decisions) =>
            FamilyDecisionReviewModel.Create(Draft(records, decisions), decisions);

        /// <summary>The earlier, unrelated family-only approval of C1 (FamilyDecisionPolicy.CreateApproval), as the only decision.</summary>
        internal static List<ProjectProfile.EstimateProfile.FamilyDecision> UnrelatedDecision(IReadOnlyList<NeutralQuantityRecord> records)
        {
            var other = EngineerBoqDraftBuilder.RecognitionGroups(records, Library).Single(g => g.LayerLeaf == OtherLayer);
            return new List<ProjectProfile.EstimateProfile.FamilyDecision>
            {
                FamilyDecisionPolicy.CreateApproval(Family, new[] { other }, new[] { EvidenceKeys.BlockAttributes }, Library, Approver,
                    "TEST ONLY: earlier unrelated family-only decision", DateTime.UtcNow.AddHours(-1)),
            };
        }

        /// <summary>
        /// The baseline profile file: <see cref="UnrelatedDecision"/> written by the real writer, read back by the real loader,
        /// and the CAS evidence a scan captures from those bytes.
        /// </summary>
        internal static (ProjectProfile Profile, string File, ProjectProfileWriter.ExpectedProfileState State) SavedProfile(
            string directory, IReadOnlyList<NeutralQuantityRecord> records)
        {
            var profile = new ProjectProfile { ProfileId = ProfileId, ProjectName = "TEST ONLY partial approval" };
            profile.Estimate.FamilyDecisions.AddRange(UnrelatedDecision(records));
            var file = Path.Combine(directory, "TEST-ONLY-partial-approval-profile.yaml");
            ProfileCasTest.Save(profile, file, "TEST ONLY baseline", Approver);
            var loaded = ProjectProfileLoader.LoadFromFile(file);
            loaded.IsUsable.Should().BeTrue(string.Join("; ", loaded.Findings.Select(f => f.Code + ":" + f.Title)));
            return (loaded.Profile!, file, ProfileCasTest.For(loaded.Profile!, file));
        }

        internal static EstimateWorkflowService.ScanResult Scan(ProjectProfile profile, ProjectProfileWriter.ExpectedProfileState state,
            IReadOnlyList<NeutralQuantityRecord> records) => new()
        {
            RunId = "TEST-ONLY-PARTIAL-RUN",
            ProjectProfileId = profile.ProfileId,
            ProfileSource = state.SourcePath,
            SourceDrawing = "TEST-ONLY-NOT-A-REAL-DRAWING.dwg",
            ProjectProfileEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(profile),
            ProfileWriteState = state,
            DiscoveryMode = true,
            Records = records.ToList(),
            Status = DeliveryStatus.ReviewRequired,
        };

        /// <summary>The records every applied partition of the complete scan gives to a decision.</summary>
        internal static IReadOnlyList<string> AppliedIds(IReadOnlyList<ProjectProfile.EstimateProfile.FamilyDecision> decisions,
            IReadOnlyList<NeutralQuantityRecord> records) =>
            FamilyDecisionPolicy.ResolvePartitions(decisions, EngineerBoqDraftBuilder.RecognitionGroups(records, Library), Library)
                .Where(p => p.Resolution.IsApplied).SelectMany(p => p.Group.Records).Select(r => r.RecordId)
                .OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Semantic partition approval (review item 34) through the production boundaries: the review model's partition row, the
    /// control's request mapping and commit/cancel dispatch, the real SaveFamilyDecisions CAS write, ProjectProfileLoader and a
    /// real draft after reload. Records are TEST ONLY.
    /// </summary>
    public sealed class FamilyPartitionApprovalTests : IDisposable
    {
        private const string Reason = "TEST ONLY: the block attribute BUS SHELTER on the three objects";
        private const string UncommittedReason = "TEST ONLY UNCOMMITTED REASON 7f3a";
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcd-family-partition-tests", Guid.NewGuid().ToString("N"));

        public FamilyPartitionApprovalTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void AProvenPartitionIsSavedThroughTheServiceAndTheReloadedDraftKeepsThreeApprovedOneResidualAndTheUnrelatedDecision()
        {
            var records = PartitionApprovalFixture.Records();
            var recordsBefore = JsonSerializer.Serialize(records);
            var (profile, file, state) = PartitionApprovalFixture.SavedProfile(_dir, records);
            var unrelated = profile.Estimate.FamilyDecisions.Single();
            var unrelatedBefore = JsonSerializer.Serialize(unrelated);
            PartitionApprovalFixture.AppliedIds(profile.Estimate.FamilyDecisions, records).Should().Equal("TEST-C1");

            var model = PartitionApprovalFixture.Review(records, profile.Estimate.FamilyDecisions);
            var partition = model.Rows.Single(r => r.IsPartition);
            partition.IsSelected.Should().BeFalse("nothing is checked for the engineer");
            partition.IsSelected = true;
            var batch = model.SelectedBatches().Should().ContainSingle().Subject;
            batch.Partition.Should().NotBeNull();
            var requests = CivilDeliveryControl.FamilyDecisionRequests(model.SelectedBatches(), Reason);
            var request = requests.Should().ContainSingle().Subject;
            request.Groups.Should().BeEmpty("the service rebuilds the whole group from the scan");
            request.EvidenceKeys.Should().BeEmpty();
            request.Partition!.WholeGroupId.Should().Be(partition.WholeGroup.GroupId);
            request.Partition.RecordIds.Should().BeEquivalentTo(PartitionApprovalFixture.Proven);
            request.Reason.Should().StartWith(Reason).And.Contain("3 מתוך 4");

            var service = new EstimateWorkflowService();
            var scan = PartitionApprovalFixture.Scan(profile, state, records);
            ProjectProfileWriter.SaveResult? saved = null;
            CivilDeliveryControl.CommitFamilyApprovals(true, FamilyReviewAction.ApproveFamilies, requests, approved =>
                    saved = service.SaveFamilyDecisions(profile, PartitionApprovalFixture.Library, scan, approved,
                        PartitionApprovalFixture.Approver, file, state))
                .Should().BeTrue();
            saved!.NewHash.Should().Be(ArtifactHash.Sha256OfText(File.ReadAllText(file)));
            var afterCommit = PartitionApprovalFixture.AppliedIds(profile.Estimate.FamilyDecisions, records);
            afterCommit.Should().Equal("TEST-C1", "TEST-S1", "TEST-S2", "TEST-U1");

            // Discard the in-memory profile: everything below comes from the saved bytes.
            var loaded = ProjectProfileLoader.LoadFromFile(file);
            loaded.IsUsable.Should().BeTrue(string.Join("; ", loaded.Findings.Select(f => f.Code + ":" + f.Title)));
            var decisions = loaded.Profile!.Estimate.FamilyDecisions;
            decisions.Should().HaveCount(2);
            JsonSerializer.Serialize(decisions.Single(d => d.DecisionId == unrelated.DecisionId)).Should().Be(unrelatedBefore,
                "the unrelated earlier decision keeps its fields and identity");
            var added = decisions.Single(d => d.DecisionId != unrelated.DecisionId);
            added.Status.Should().Be(FamilyDecisionPolicy.Active);
            added.FamilyId.Should().Be(PartitionApprovalFixture.Family);
            added.ApprovedBy.Should().Be(PartitionApprovalFixture.Approver);
            var selector = added.Selectors.Should().ContainSingle().Subject;
            selector.LayerLeaf.Should().Be(PartitionApprovalFixture.Layer);
            selector.EvidenceMatch.Should().Contain(m => m.Key == EvidenceKeys.BlockAttributes && m.Text == "BUS SHELTER");
            decisions.Should().OnlyContain(d => d.ItemApprovals.Count == 0, "a family approval never approves catalog items");
            PartitionApprovalFixture.AppliedIds(decisions, records).Should().Equal(afterCommit, "coverage after reload equals coverage after commit");

            var draft = PartitionApprovalFixture.Draft(records, decisions);
            var bus = draft.Elements.Single(e => e.Rule.Id == PartitionApprovalFixture.Family);
            bus.IncludedQuantity.Should().Be(4d, "3 newly approved objects + the unrelated C1");
            var main = bus.Sources.Single(s => s.Group.Layer == PartitionApprovalFixture.Layer);
            main.Included.Should().BeTrue();
            main.Group.Count.Should().Be(3);
            main.Group.Quantity.Should().Be(3d);
            bus.Sources.Single(s => s.Group.Layer == PartitionApprovalFixture.OtherLayer).Group.Quantity.Should().Be(1d);
            var residual = draft.UnmappedDesign.Should().ContainSingle().Subject;
            residual.Count.Should().Be(1);
            residual.Quantity.Should().Be(1d);
            draft.RecognitionProposals.SelectMany(p => p.RecordIds).Should().Equal("TEST-U2");

            // Every record once, no quantity lost or doubled, no record, source or mapping approval changed.
            var buckets = draft.Elements.SelectMany(e => e.Sources).Select(s => s.Group)
                .Concat(draft.UnmappedDesign).Concat(draft.NotUsedAlternatives).Concat(draft.Existing).Concat(draft.Utilities)
                .Concat(draft.DraftingAids).Concat(draft.CorridorVolumes).Concat(draft.ExcludedByDecision).ToList();
            draft.AccountedRecords.Should().Be(records.Length);
            buckets.Sum(g => g.Count).Should().Be(records.Length);
            buckets.Sum(g => g.Quantity).Should().Be(records.Sum(r => r.Measurement.RawValue)).And.Be(5d);
            buckets.Select(g => g.GroupId).Should().OnlyHaveUniqueItems();
            draft.RecognitionGroups.SelectMany(g => g.Records).Select(r => r.RecordId)
                .Should().OnlyHaveUniqueItems().And.BeEquivalentTo(records.Select(r => r.RecordId));
            JsonSerializer.Serialize(records).Should().Be(recordsBefore);
            records.Should().OnlyContain(r => r.Classification.MappingApprovedBy == null && r.Classification.CandidateCatalogCode == null);

            // The review after reload: only the residual is left, and the saved list shows the partition's coverage.
            var review = FamilyDecisionReviewModel.Create(draft, decisions);
            var left = review.Rows.Should().ContainSingle().Subject;
            left.Group.Records.Select(r => r.RecordId).Should().Equal("TEST-U2");
            left.CanSelect.Should().BeFalse();
            left.Missing.Should().Contain("1 מתוך 4").And.Contain("אי אפשר לאשר");
            review.SavedDecisions.Single(s => s.DecisionId == added.DecisionId).Should().Match<SavedFamilyDecision>(s =>
                s.AppliedGroups == 1 && s.StaleGroups == 0 && s.State == "בתוקף" && s.Coverage == "3 מתוך 4 עצמים" &&
                s.Groups.Contains("3 מתוך 4") && s.Layers == PartitionApprovalFixture.Layer);
            review.SavedDecisions.Single(s => s.DecisionId == unrelated.DecisionId).Should().Match<SavedFamilyDecision>(s =>
                s.AppliedGroups == 1 && s.StaleGroups == 0 && s.Coverage == null);
        }

        [Fact]
        public void ATrimmedGroupAnArbitrarySubsetOrLessThanTheProvenPartitionIsRefusedWithoutWriting()
        {
            var records = PartitionApprovalFixture.Records();
            var (profile, file, state) = PartitionApprovalFixture.SavedProfile(_dir, records);
            var bytes = File.ReadAllText(file);
            var decisionsBefore = JsonSerializer.Serialize(profile.Estimate.FamilyDecisions);
            var scan = PartitionApprovalFixture.Scan(profile, state, records);
            var service = new EstimateWorkflowService();
            var whole = EngineerBoqDraftBuilder.RecognitionGroups(records, PartitionApprovalFixture.Library)
                .Single(g => g.LayerLeaf == PartitionApprovalFixture.Layer);
            var row = PartitionApprovalFixture.Review(records, profile.Estimate.FamilyDecisions).Rows.Single(r => r.IsPartition);
            ProjectProfileWriter.SaveResult Save(FamilyDecisionRequest request) => service.SaveFamilyDecisions(profile,
                PartitionApprovalFixture.Library, scan, new[] { request }, PartitionApprovalFixture.Approver, file, state);
            FamilyDecisionRequest Partition(params string[] ids) =>
                FamilyDecisionRequest.ForPartition(PartitionApprovalFixture.Family, whole.GroupId, ids, Reason);

            // The dialog's partition sent as if it were a whole group: today's refusal of part of a group stays.
            var trimmed = () => Save(new FamilyDecisionRequest(PartitionApprovalFixture.Family, new[] { row.Group },
                new[] { EvidenceKeys.BlockAttributes }, Reason));
            trimmed.Should().Throw<InvalidOperationException>().WithMessage("*whole measured group*");

            // Fewer records than the proven partition (S1 and S2 without the identical U1), an arbitrary mix, the whole group.
            var smaller = () => Save(Partition("TEST-S1", "TEST-S2"));
            smaller.Should().Throw<InvalidOperationException>().WithMessage("*complete locally proven semantic partition*");
            var arbitrary = () => Save(Partition("TEST-S1", "TEST-U2"));
            arbitrary.Should().Throw<InvalidOperationException>();
            var all = () => Save(Partition("TEST-S1", "TEST-S2", "TEST-U1", "TEST-U2"));
            all.Should().Throw<InvalidOperationException>().WithMessage("*proper*partition*");

            // A record of another group, a group the scan does not have, groups or evidence sent along, another family.
            var outsider = () => Save(Partition("TEST-S1", "TEST-S2", "TEST-U1", "TEST-C1"));
            outsider.Should().Throw<InvalidOperationException>().WithMessage("*not one exact record of the current scan*");
            var foreignGroup = () => Save(FamilyDecisionRequest.ForPartition(PartitionApprovalFixture.Family, row.Group.GroupId + "|trimmed",
                PartitionApprovalFixture.Proven, Reason));
            foreignGroup.Should().Throw<InvalidOperationException>().WithMessage("*not one measured group of the current scan*");
            var withGroups = () => Save(Partition(PartitionApprovalFixture.Proven) with { Groups = new[] { row.Group } });
            withGroups.Should().Throw<InvalidOperationException>().WithMessage("*names a partition and also groups or evidence*");
            var otherFamily = () => Save(FamilyDecisionRequest.ForPartition("bike-racks", whole.GroupId, PartitionApprovalFixture.Proven, Reason));
            otherFamily.Should().Throw<InvalidOperationException>();

            JsonSerializer.Serialize(profile.Estimate.FamilyDecisions).Should().Be(decisionsBefore);
            File.ReadAllText(file).Should().Be(bytes);
        }

        [Fact]
        public void TheSavedListJudgesALiteralDecisionOnItsWholeGroupAndShowsEachDecisionsPartialCoverage()
        {
            // A synthetic earlier literal override on the whole mixed group (preservation test, not an engineering
            // recommendation), then a later partition rule that takes the proven part over.
            var records = PartitionApprovalFixture.Records();
            var library = PartitionApprovalFixture.Library;
            var whole = EngineerBoqDraftBuilder.RecognitionGroups(records, library).Single(g => g.LayerLeaf == PartitionApprovalFixture.Layer);
            var at = DateTime.UtcNow.AddMinutes(-1);
            var literal = FamilyDecisionPolicy.CreateApproval(PartitionApprovalFixture.Family, new[] { whole },
                new[] { EvidenceKeys.BlockAttributes }, library, PartitionApprovalFixture.Approver, "TEST ONLY: earlier literal override", at.AddMinutes(-5));
            var partition = FamilyDecisionPolicy.CreatePartitionApproval(PartitionApprovalFixture.Family, whole, PartitionApprovalFixture.Proven,
                library, PartitionApprovalFixture.Approver, "TEST ONLY: proven partition", at);
            var decisions = PartitionApprovalFixture.UnrelatedDecision(records);
            decisions.Add(literal);
            decisions.Add(partition);

            var draft = PartitionApprovalFixture.Draft(records, decisions);
            draft.FamilyDecisionPartitions.Should().NotBeEmpty();
            draft.FamilyResolutions.Should().NotBeEmpty().And.OnlyContain(r => r.IsApplied, "the literal override still holds on its whole group");
            var model = FamilyDecisionReviewModel.Create(draft, decisions);
            model.SavedDecisions.Single(s => s.DecisionId == literal.DecisionId).Should().Match<SavedFamilyDecision>(s =>
                s.AppliedGroups == 1 && s.StaleGroups == 1 && s.State.Contains("החלטה מאוחרת יותר") && !s.State.Contains("הראיות השתנו") &&
                s.Coverage == "1 מתוך 4 עצמים");
            model.SavedDecisions.Single(s => s.DecisionId == partition.DecisionId).Should().Match<SavedFamilyDecision>(s =>
                s.AppliedGroups == 1 && s.StaleGroups == 0 && s.Coverage == "3 מתוך 4 עצמים");
            model.SavedDecisions.Single(s => s.DecisionId == decisions[0].DecisionId).Should().Match<SavedFamilyDecision>(s =>
                s.AppliedGroups == 1 && s.StaleGroups == 0 && s.Coverage == null);
            model.Rows.Should().BeEmpty("every record is covered");
        }

        [Fact]
        public void ADecisionWhoseGroupsNoRuleSplitKeepsTodaysCoverageTextWhileAnotherGroupIsSplit()
        {
            var records = PartitionApprovalFixture.Records();
            var library = PartitionApprovalFixture.Library;
            var whole = EngineerBoqDraftBuilder.RecognitionGroups(records, library).Single(g => g.LayerLeaf == PartitionApprovalFixture.Layer);
            var decisions = PartitionApprovalFixture.UnrelatedDecision(records);
            decisions.Add(FamilyDecisionPolicy.CreatePartitionApproval(PartitionApprovalFixture.Family, whole, PartitionApprovalFixture.Proven,
                library, PartitionApprovalFixture.Approver, "TEST ONLY: proven partition", DateTime.UtcNow));
            // After both approvals the cited block attribute of C1 changed: the earlier decision no longer holds on its unsplit group.
            records[4] = PartitionApprovalFixture.Record("TEST-C1", PartitionApprovalFixture.OtherLayer, "BENCH");

            var draft = PartitionApprovalFixture.Draft(records, decisions);
            draft.FamilyDecisionPartitions.Should().Contain(p => p.Group.GroupId != p.WholeGroup.GroupId, "the proven partition splits QX971");
            var model = FamilyDecisionReviewModel.Create(draft, decisions);
            model.SavedDecisions.Single(s => s.DecisionId == decisions[0].DecisionId).Should().Match<SavedFamilyDecision>(s =>
                s.AppliedGroups == 0 && s.StaleGroups == 1 && s.Coverage == null && s.Groups == "0 בתוקף · 1 לא בתוקף",
                "no rule split VQ204, so the decision's text is today's text");
            model.SavedDecisions.Single(s => s.DecisionId == decisions[1].DecisionId).Coverage.Should().Be("3 מתוך 4 עצמים");
        }

        [Fact]
        public void OnlyAnOkCloseThatApprovesSaves()
        {
            var calls = 0;
            var requests = new[] { FamilyDecisionRequest.ForPartition(PartitionApprovalFixture.Family, "rg|TEST", PartitionApprovalFixture.Proven, Reason) };
            void Save(IReadOnlyList<FamilyDecisionRequest> approved) => calls++;
            CivilDeliveryControl.CommitFamilyApprovals(false, FamilyReviewAction.ApproveFamilies, requests, Save).Should().BeFalse();
            CivilDeliveryControl.CommitFamilyApprovals(null, FamilyReviewAction.ApproveFamilies, requests, Save).Should().BeFalse();
            CivilDeliveryControl.CommitFamilyApprovals(false, FamilyReviewAction.None, requests, Save).Should().BeFalse();
            CivilDeliveryControl.CommitFamilyApprovals(true, FamilyReviewAction.None, requests, Save).Should().BeFalse();
            CivilDeliveryControl.CommitFamilyApprovals(true, FamilyReviewAction.RevokeDecision, requests, Save).Should().BeFalse();
            calls.Should().Be(0);
            var nothingChecked = () => CivilDeliveryControl.CommitFamilyApprovals(true, FamilyReviewAction.ApproveFamilies,
                Array.Empty<FamilyDecisionRequest>(), Save);
            nothingChecked.Should().Throw<InvalidOperationException>();
            calls.Should().Be(0);
            CivilDeliveryControl.CommitFamilyApprovals(true, FamilyReviewAction.ApproveFamilies, requests, Save).Should().BeTrue();
            calls.Should().Be(1);
        }

        [Fact]
        public void ThePaletteSavesFamilyApprovalsOnlyThroughTheCommitSeam()
        {
            // Source contract: the seam tested above is the palette's only way from a closed review to the approval write.
            var source = File.ReadAllText(Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryControl.FamilyDecisions.cs"));
            var seam = source.IndexOf("CommitFamilyApprovals(closed, dialog.ReviewAction, requests, approved =>", StringComparison.Ordinal);
            seam.Should().BeGreaterThan(0, "OnFamilyReview dispatches the dialog's result through CommitFamilyApprovals");
            source.Split("_estimate.SaveFamilyDecisions(", StringSplitOptions.None).Should().HaveCount(2, "the palette has one approval write");
            source.IndexOf("_estimate.SaveFamilyDecisions(", StringComparison.Ordinal).Should().BeGreaterThan(seam,
                "the one write runs inside the seam's save callback, never before the dialog's result is checked");
            source.Split("FamilyDecisionRequests(batches, dialog.Reason)", StringSplitOptions.None).Should().HaveCount(2,
                "the palette builds its requests with the mapping these tests exercise");
        }

        [Fact]
        public void CancellingTheRealDialogAfterCheckingThePartitionAndTypingAReasonWritesNothing()
        {
            var records = PartitionApprovalFixture.Records();
            var (profile, file, state) = PartitionApprovalFixture.SavedProfile(_dir, records);
            var bytes = File.ReadAllBytes(file);
            var decisionsBefore = JsonSerializer.Serialize(profile.Estimate.FamilyDecisions);
            var recordsBefore = JsonSerializer.Serialize(records);
            var inventoryBefore = Inventory();
            var scan = PartitionApprovalFixture.Scan(profile, state, records);
            var service = new EstimateWorkflowService();
            var saves = 0;
            void Save(IReadOnlyList<FamilyDecisionRequest> approved)
            {
                saves++;
                service.SaveFamilyDecisions(profile, PartitionApprovalFixture.Library, scan, approved, PartitionApprovalFixture.Approver, file, state);
            }

            bool? closed = null;
            var action = FamilyReviewAction.ApproveFamilies;
            IReadOnlyList<FamilyDecisionRequest> requests = Array.Empty<FamilyDecisionRequest>();
            var cancelClicked = false;
            var timedOut = false;
            var saveWasEnabled = false;
            var shownScope = string.Empty;
            RunOnSta(() =>
            {
                var model = PartitionApprovalFixture.Review(records, profile.Estimate.FamilyDecisions);
                var dialog = new FamilyDecisionsDialog(model)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, Opacity = 0,
                };
                dialog.ApproverBox.Text = "SYNTHETIC TEST REVIEWER"; // b24: typed, never prefilled from Windows
                Exception? callbackError = null;
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                timer.Tick += (_, _) => { timer.Stop(); timedOut = true; dialog.Close(); };
                timer.Start();
                dialog.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    try
                    {
                        // Check the partition in one grouped action, toggle it, write a reason and attest; then Cancel.
                        var row = model.Rows.Single(r => r.IsPartition);
                        dialog.RowsGrid.SelectedItem = row;
                        row.IsSelected = true;
                        row.IsSelected = false;
                        row.IsSelected = true;
                        dialog.ReasonBox.Text = UncommittedReason;
                        dialog.ConfirmBox.IsChecked = true;
                        saveWasEnabled = dialog.BtnSave.IsEnabled;
                        shownScope = dialog.RowDetailScope.Text;
                        dialog.BtnCancel.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        cancelClicked = true;
                    }
                    catch (Exception error)
                    {
                        callbackError = error;
                        dialog.Close();
                    }
                }));
                closed = dialog.ShowDialog();
                timer.Stop();
                if (callbackError != null) ExceptionDispatchInfo.Capture(callbackError).Throw();
                action = dialog.ReviewAction;
                // The same production mapping the palette runs after the dialog closes.
                requests = CivilDeliveryControl.FamilyDecisionRequests(dialog.Batches, dialog.Reason);
            });

            cancelClicked.Should().BeTrue();
            timedOut.Should().BeFalse();
            saveWasEnabled.Should().BeTrue("the approval was possible when the engineer cancelled");
            shownScope.Should().Contain("3 מתוך 4");
            closed.Should().BeFalse();
            action.Should().Be(FamilyReviewAction.None);
            requests.Should().ContainSingle(r => r.Partition != null, "the checked partition was still there when the dialog was cancelled");

            CivilDeliveryControl.CommitFamilyApprovals(closed, action, requests, Save).Should().BeFalse();
            saves.Should().Be(0);
            File.ReadAllBytes(file).Should().Equal(bytes);
            Inventory().Should().Equal(inventoryBefore);
            File.ReadAllText(file).Should().NotContain(UncommittedReason);
            JsonSerializer.Serialize(profile.Estimate.FamilyDecisions).Should().Be(decisionsBefore);
            JsonSerializer.Serialize(records).Should().Be(recordsBefore);

            // Reopening from the unchanged file: nothing is checked and no reason is kept.
            var reloaded = ProjectProfileLoader.LoadFromFile(file);
            reloaded.IsUsable.Should().BeTrue();
            var reopened = PartitionApprovalFixture.Review(records, reloaded.Profile!.Estimate.FamilyDecisions);
            reopened.Rows.Should().NotBeEmpty().And.OnlyContain(r => !r.IsSelected);
            reopened.SelectedBatches().Should().BeEmpty();
            reopened.SavedDecisions.Should().ContainSingle().Which.AppliedGroups.Should().Be(1);
            RunOnSta(() =>
            {
                var dialog = new FamilyDecisionsDialog(reopened);
                dialog.ReasonBox.Text.Should().BeEmpty();
                dialog.ConfirmBox.IsChecked.Should().BeFalse();
                dialog.BtnSave.IsEnabled.Should().BeFalse();
                dialog.Close();
            });

            // The same requests were valid: committing them through the same seam is what writes.
            CivilDeliveryControl.CommitFamilyApprovals(true, FamilyReviewAction.ApproveFamilies, requests, Save).Should().BeTrue();
            saves.Should().Be(1);
            ProjectProfileLoader.LoadFromFile(file).Profile!.Estimate.FamilyDecisions.Should().HaveCount(2);
        }

        private IReadOnlyList<string> Inventory() => Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(_dir, path) + "=" + ArtifactHash.Sha256OfText(File.ReadAllText(path)))
            .ToList();

        private static void RunOnSta(Action action)
        {
            Exception? error = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
