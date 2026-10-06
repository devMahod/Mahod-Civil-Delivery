using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// A partition rule already covers three shelters that share an incidental colour; a new shelter without that colour is the
    /// uncovered rest of the same proven partition (int-b review, 28.09). Its row is approvable, its scope counts the three as
    /// covered by the earlier decision and the bench as open, and the real service saves it without touching the earlier decision.
    /// Records are TEST ONLY; no catalog code, price or engineering decision.
    /// </summary>
    public sealed class FamilyPartitionScopeReviewTests : IDisposable
    {
        private const string Reason = "TEST ONLY: the new shelter, same block attribute";
        private static readonly string[] Shelters = { "TEST-S1", "TEST-S2", "TEST-S3" };
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcd-family-partition-scope", Guid.NewGuid().ToString("N"));

        public FamilyPartitionScopeReviewTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static NeutralQuantityRecord Counted(string id, string subject, string colour)
        {
            var record = PartitionApprovalFixture.Record(id, PartitionApprovalFixture.Layer, subject);
            record.Measurement.Parameters[EvidenceKeys.BlockAttributes] = JsonSerializer.Serialize(new { synthetic_injection = true,
                source = new { handle = id }, observation = new { attributes = new[]
                {
                    new { tag = "TEST_CLASS", value = subject }, new { tag = "COLOUR", value = colour },
                } } });
            return record;
        }

        private static NeutralQuantityRecord[] Existing() => new[]
        {
            Counted("TEST-S1", "BUS SHELTER", "RED"), Counted("TEST-S2", "BUS SHELTER", "RED"), Counted("TEST-S3", "BUS SHELTER", "RED"),
            Counted("TEST-U1", "BENCH", "RED"),
        };

        [Fact]
        public void TheNewShelterWithoutTheIncidentalColourIsApprovableAndTheScopeCountsTheCoveredRecordsApart()
        {
            var library = PartitionApprovalFixture.Library;
            var earlier = FamilyDecisionPolicy.CreatePartitionApproval(PartitionApprovalFixture.Family,
                EngineerBoqDraftBuilder.RecognitionGroups(Existing(), library).Single(), Shelters, library,
                PartitionApprovalFixture.Approver, "TEST ONLY: earlier proven partition", DateTime.UtcNow.AddHours(-1));
            var profile = new ProjectProfile { ProfileId = PartitionApprovalFixture.ProfileId, ProjectName = "TEST ONLY partition scope" };
            profile.Estimate.FamilyDecisions.Add(earlier);
            var file = Path.Combine(_dir, "TEST-ONLY-partition-scope-profile.yaml");
            ProfileCasTest.Save(profile, file, "TEST ONLY baseline", PartitionApprovalFixture.Approver);
            var loaded = ProjectProfileLoader.LoadFromFile(file);
            loaded.IsUsable.Should().BeTrue(string.Join("; ", loaded.Findings.Select(f => f.Code + ":" + f.Title)));
            profile = loaded.Profile!;
            var state = ProfileCasTest.For(profile, file);
            var earlierBefore = JsonSerializer.Serialize(profile.Estimate.FamilyDecisions.Single());

            var records = Existing().Append(Counted("TEST-N1", "BUS SHELTER", "BLUE")).ToArray();
            var recordsBefore = JsonSerializer.Serialize(records);
            PartitionApprovalFixture.AppliedIds(profile.Estimate.FamilyDecisions, records).Should().Equal(Shelters);

            var model = PartitionApprovalFixture.Review(records, profile.Estimate.FamilyDecisions);
            var row = model.Rows.Single(r => r.Group.Records.Any(record => record.RecordId == "TEST-N1"));
            row.Group.Records.Select(r => r.RecordId).Should().Equal("TEST-N1");
            row.IsPartition.Should().BeTrue("the rest of its proven partition is already covered as the same family");
            row.PartitionMatches!.Select(m => m.Text).Should().BeEquivalentTo(new[] { "BUS SHELTER", "BLUE" });
            row.ScopeText.Should().Contain("1 מתוך 5")
                .And.Contain("3 כבר מכוסים בהחלטה קודמת והאישור הזה אינו משנה אותם")
                .And.Contain("ו-1 אינם מאושרים ונשארים לבדיקה")
                .And.NotContain("4 העצמים האחרים בקבוצה אינם מאושרים");
            var bench = model.Rows.SingleOrDefault(r => r.Group.Records.Any(record => record.RecordId == "TEST-U1"));
            if (bench != null) bench.Missing.Should().NotContain("החלטה שמורה אחרת", "the saved decision is of no other family here");

            row.IsSelected = true;
            var requests = CivilDeliveryControl.FamilyDecisionRequests(model.SelectedBatches(), Reason);
            var request = requests.Should().ContainSingle().Subject;
            request.Partition!.RecordIds.Should().Equal("TEST-N1");
            var service = new EstimateWorkflowService();
            var scan = PartitionApprovalFixture.Scan(profile, state, records);
            CivilDeliveryControl.CommitFamilyApprovals(true, FamilyReviewAction.ApproveFamilies, requests, approved =>
                    service.SaveFamilyDecisions(profile, library, scan, approved, PartitionApprovalFixture.Approver, file, state))
                .Should().BeTrue();

            var reloaded = ProjectProfileLoader.LoadFromFile(file);
            reloaded.IsUsable.Should().BeTrue();
            var decisions = reloaded.Profile!.Estimate.FamilyDecisions;
            decisions.Should().HaveCount(2);
            JsonSerializer.Serialize(decisions.Single(d => d.DecisionId == earlier.DecisionId)).Should().Be(earlierBefore);
            PartitionApprovalFixture.AppliedIds(decisions, records).Should().Equal("TEST-N1", "TEST-S1", "TEST-S2", "TEST-S3");
            var partitions = FamilyDecisionPolicy.ResolvePartitions(decisions,
                EngineerBoqDraftBuilder.RecognitionGroups(records, library), library);
            partitions.Single(p => p.Group.Records.Any(r => r.RecordId == "TEST-S1")).Resolution.DecisionId.Should().Be(earlier.DecisionId);
            partitions.Single(p => p.Group.Records.Any(r => r.RecordId == "TEST-U1")).Resolution.IsApplied.Should().BeFalse();
            JsonSerializer.Serialize(records).Should().Be(recordsBefore);
        }

        [Fact]
        public void APartitionRowInAGroupNothingCoversKeepsTodaysScopeWording()
        {
            var records = PartitionApprovalFixture.Records();
            var row = PartitionApprovalFixture.Review(records, PartitionApprovalFixture.UnrelatedDecision(records)).Rows.Single(r => r.IsPartition);
            row.ScopeText.Should().Contain("1 העצמים האחרים בקבוצה אינם מאושרים ונשארים לבדיקה").And.NotContain("כבר מכוסים");
        }
    }
}
