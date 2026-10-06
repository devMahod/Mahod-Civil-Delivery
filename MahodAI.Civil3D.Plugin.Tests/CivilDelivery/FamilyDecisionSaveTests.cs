using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// The family-decision save path: the original scan's CAS evidence, groups proven against the scan's own records,
    /// one atomic write that the loader reads back, and restoration of the in-memory list and the file on failure.
    /// Records are SYNTHETIC.
    /// </summary>
    public sealed class FamilyDecisionSaveTests : IDisposable
    {
        private const string Ha = "6422-HA-MODEL-NATAZ";
        private const string Reason = "זיהוי לפי דפוס הצללה ומקרא — בדיקה סינתטית";
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "mcd-family-decision-tests", Guid.NewGuid().ToString("N"));

        public FamilyDecisionSaveTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static EngineerBoqLibrary Library => EngineerBoqLibrary.RoadsV1;

        private static NeutralQuantityRecord HatchRecord(string id, string layer, double value, string pattern = "ANSI31")
        {
            var record = EstimateFixtures.Record(
                id, null!, value, "מ\"ר", kind: "area", handle: id.ToUpperInvariant(), xref: Ha,
                ruleKey: $"layer:{layer}|area", layer: $"{Ha}|{layer}", method: "hatch-area");
            record.Measurement.Parameters[EvidenceKeys.Schema] = EvidenceKeys.SchemaV1;
            record.Measurement.Parameters[EvidenceKeys.Hatch] =
                "{\"pattern\":\"" + pattern + "\",\"scale\":1,\"angle\":0,\"solid\":false,\"associative\":true,\"space\":\"source\"}";
            record.Measurement.Parameters[EvidenceKeys.Hatch + EvidenceKeys.StatusSuffix] = "read";
            return record;
        }

        private static RecognitionGroupInput Group(string groupId, params NeutralQuantityRecord[] records) =>
            new(groupId, Ha, DraftSourceRole.Design, SectionProjectionLogic.LayerLeaf(records[0].Source.Layer),
                "area", records[0].Measurement.Unit, "hatch", null, records);

        private static EstimateWorkflowService.FamilyDecisionRequest Request(string family, params RecognitionGroupInput[] groups) =>
            new(family, groups, new[] { EvidenceKeys.Hatch }, Reason);

        private static EstimateWorkflowService.ScanResult Scan(
            ProjectProfile profile, ProjectProfileWriter.ExpectedProfileState state, params NeutralQuantityRecord[] records) => new()
        {
            RunId = "family-decision-scan",
            ProjectProfileId = profile.ProfileId,
            ProfileSource = state.SourcePath,
            SourceDrawing = "PD.dwg",
            ProjectProfileEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(profile),
            ProfileWriteState = state,
            DiscoveryMode = true,
            Records = records.ToList(),
            Status = DeliveryStatus.ReviewRequired,
        };

        /// <summary>An existing profile file, and the CAS evidence a workflow captures when it starts from it.</summary>
        private (ProjectProfile Profile, string File, ProjectProfileWriter.ExpectedProfileState State) SavedProfile(string name)
        {
            var profile = EstimateFixtures.Profile();
            var file = Path.Combine(_dir, name);
            ProfileCasTest.Save(profile, file, "initial test profile", "nataly");
            return (profile, file, ProfileCasTest.For(profile, file));
        }

        [Fact]
        public void SaveThenReload_ResolvesTheFamilyOnRandomLayers()
        {
            var (profile, file, state) = SavedProfile("family-save.yaml");
            var a1 = HatchRecord("fam-a1", "asdasd23423", 100);
            var a2 = HatchRecord("fam-a2", "asdasd23423", 40);
            var b1 = HatchRecord("fam-b1", "qwe987", 250);
            var scan = Scan(profile, state, a1, a2, b1);
            var groups = new[] { Group("A", a1, a2), Group("B", b1) };

            var saved = new EstimateWorkflowService().SaveFamilyDecisions(
                profile, Library, scan, new[] { Request("road-pavement", groups) }, "nataly", file, state);

            saved.NewHash.Should().Be(ArtifactHash.Sha256OfText(File.ReadAllText(file)));
            profile.Estimate.FamilyDecisions.Should().ContainSingle();
            var loaded = ProjectProfileLoader.LoadFromFile(file);
            loaded.IsUsable.Should().BeTrue(string.Join("; ", loaded.Findings.Select(f => f.Code + ":" + f.Title)));
            loaded.Profile!.SchemaVersion.Should().Be(FamilyDecisionPolicy.FamilyDecisionsSchemaVersion);
            loaded.Profile.Provenance.SourceHashes.Should().ContainKey("library:" + Library.Id)
                .WhoseValue.Should().Be(LibraryIdentity.LibraryHash(Library));
            EstimateTraceIdentity.EffectiveProfileHash(loaded.Profile).Should().Be(
                EstimateTraceIdentity.EffectiveProfileHash(profile), "the rebased scan must stay fresh after the save");

            var decision = loaded.Profile.Estimate.FamilyDecisions.Should().ContainSingle().Which;
            decision.FamilyId.Should().Be("road-pavement");
            decision.ApprovedBy.Should().Be("nataly");
            decision.ApprovedAtUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
            decision.ItemApprovals.Should().BeEmpty("a family approval never approves catalog items");
            decision.Selectors.Select(s => s.LayerLeaf).Should().BeEquivalentTo("asdasd23423", "qwe987");
            FamilyDecisionPolicy.Resolve(loaded.Profile.Estimate.FamilyDecisions, groups, Library)
                .Should().OnlyContain(r => r.State == FamilyDecisionState.Applied && r.FamilyId == "road-pavement");
        }

        [Fact]
        public void AWindowLibraryThatIsNotTheProfilesLibrary_IsRefusedWithoutWriting()
        {
            // Codex 01:27 (02/10): a discipline change between the window's draft and the save needs a rescan, not old proposals.
            var (profile, file, state) = SavedProfile("family-discipline.yaml");
            var bytes = File.ReadAllText(file);
            var a1 = HatchRecord("fam-a1", "asdasd23423", 100);
            var scan = Scan(profile, state, a1);
            profile.Estimate.Discipline = "landscape";
            var act = () => new EstimateWorkflowService().SaveFamilyDecisions(profile, Library, scan,
                new[] { Request("road-pavement", Group("A", a1)) }, "nataly", file, state);
            act.Should().Throw<InvalidOperationException>().WithMessage("*ספריית השיוך*");
            profile.Estimate.FamilyDecisions.Should().BeEmpty();
            File.ReadAllText(file).Should().Be(bytes);
        }

        [Fact]
        public void StaleCasEvidence_IsRefusedWithoutWriting()
        {
            var (profile, file, state) = SavedProfile("family-stale.yaml");
            var bytes = File.ReadAllText(file);
            var a1 = HatchRecord("fam-a1", "asdasd23423", 100);
            var scan = Scan(profile, state, a1);
            var requests = new[] { Request("road-pavement", Group("A", a1)) };
            var service = new EstimateWorkflowService();

            scan.ProfileWriteState = null;
            var noEvidence = () => service.SaveFamilyDecisions(profile, Library, scan, requests, "nataly", file, state);
            noEvidence.Should().Throw<InvalidOperationException>().WithMessage("*CAS evidence*");
            scan.ProfileWriteState = state;

            profile.ProjectName = "changed after the scan";
            var edited = () => service.SaveFamilyDecisions(profile, Library, scan, requests, "nataly", file, state);
            edited.Should().Throw<InvalidOperationException>().WithMessage("*profile changed after the quantity scan*");
            profile.ProjectName = null;

            File.AppendAllText(file, "\n# concurrent newer bytes\n");
            var concurrent = () => service.SaveFamilyDecisions(profile, Library, scan, requests, "nataly", file, state);
            concurrent.Should().Throw<InvalidOperationException>();

            profile.Estimate.FamilyDecisions.Should().BeEmpty();
            File.ReadAllText(file).Should().Be(bytes + "\n# concurrent newer bytes\n");
        }

        [Fact]
        public void FailedWrite_RestoresTheListAndLeavesTheFileUnchanged()
        {
            var (profile, file, state) = SavedProfile("family-write-failure.yaml");
            var bytes = File.ReadAllText(file);
            var version = profile.Provenance.Version;
            var a1 = HatchRecord("fam-a1", "asdasd23423", 100);
            var scan = Scan(profile, state, a1);

            // Readers may still hash the file; the atomic replace cannot delete/rename it.
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var act = () => new EstimateWorkflowService().SaveFamilyDecisions(
                    profile, Library, scan, new[] { Request("road-pavement", Group("A", a1)) }, "nataly", file, state);
                act.Should().Throw<Exception>();
            }

            profile.Estimate.FamilyDecisions.Should().BeEmpty("a failed atomic save cannot leave the decision in memory");
            profile.SchemaVersion.Should().Be(1);
            profile.Provenance.Version.Should().Be(version);
            EstimateTraceIdentity.EffectiveProfileHash(profile).Should().Be(scan.ProjectProfileEffectiveHash);
            File.ReadAllText(file).Should().Be(bytes);

            var directoryAsTarget = Path.Combine(_dir, "cannot-replace-directory");
            Directory.CreateDirectory(directoryAsTarget);
            var directoryState = ProfileCasTest.For(profile, directoryAsTarget);
            var directoryScan = Scan(profile, directoryState, a1);
            var writeFailure = () => new EstimateWorkflowService().SaveFamilyDecisions(
                profile, Library, directoryScan, new[] { Request("road-pavement", Group("A", a1)) }, "nataly",
                directoryAsTarget, directoryState);
            writeFailure.Should().Throw<Exception>();
            profile.Estimate.FamilyDecisions.Should().BeEmpty();
            profile.SchemaVersion.Should().Be(1);
        }

        [Fact]
        public void ApproverIsRequired_AndExclusionIsNotAFamilySaveInThisVersion()
        {
            var (profile, file, state) = SavedProfile("family-approver.yaml");
            var bytes = File.ReadAllText(file);
            var a1 = HatchRecord("fam-a1", "asdasd23423", 100);
            var scan = Scan(profile, state, a1);
            var service = new EstimateWorkflowService();

            var noApprover = () => service.SaveFamilyDecisions(
                profile, Library, scan, new[] { Request("road-pavement", Group("A", a1)) }, "   ", file, state);
            noApprover.Should().Throw<ArgumentException>();
            var injected = () => service.SaveFamilyDecisions(
                profile, Library, scan, new[] { Request("road-pavement", Group("A", a1)) }, "nataly\nprofile_id: x", file, state);
            injected.Should().Throw<ArgumentException>();

            var exclusion = Request(string.Empty, Group("A", a1)) with { Decision = FamilyDecisionPolicy.Exclude };
            var exclude = () => service.SaveFamilyDecisions(profile, Library, scan, new[] { exclusion }, "nataly", file, state);
            exclude.Should().Throw<InvalidOperationException>().WithMessage("*not a construction quantity*");

            profile.Estimate.FamilyDecisions.Should().BeEmpty();
            File.ReadAllText(file).Should().Be(bytes);
        }

        [Fact]
        public void GroupsMustBeTheScansOwnRecordsWithTheirOwnFacts()
        {
            var (profile, file, state) = SavedProfile("family-groups.yaml");
            var bytes = File.ReadAllText(file);
            var a1 = HatchRecord("fam-a1", "asdasd23423", 100);
            var outsider = HatchRecord("fam-x1", "asdasd23423", 100);
            var scan = Scan(profile, state, a1);
            var service = new EstimateWorkflowService();

            var foreign = () => service.SaveFamilyDecisions(
                profile, Library, scan, new[] { Request("road-pavement", Group("A", a1, outsider)) }, "nataly", file, state);
            foreign.Should().Throw<InvalidOperationException>().WithMessage("*not one exact record of the current scan*");

            var mislabelled = Group("A", a1) with { LayerLeaf = "some-other-layer" };
            var mismatch = () => service.SaveFamilyDecisions(
                profile, Library, scan, new[] { Request("road-pavement", mislabelled) }, "nataly", file, state);
            mismatch.Should().Throw<InvalidOperationException>().WithMessage("*does not match its measured records*");

            var twice = () => service.SaveFamilyDecisions(profile, Library, scan,
                new[] { Request("road-pavement", Group("A", a1)), Request("sidewalk-paving", Group("A", a1)) }, "nataly", file, state);
            twice.Should().Throw<InvalidOperationException>().WithMessage("*more than once*");

            var wrongBasis = () => service.SaveFamilyDecisions(
                profile, Library, scan, new[] { Request("curb-road", Group("A", a1)) }, "nataly", file, state);
            wrongBasis.Should().Throw<InvalidOperationException>().WithMessage("*does not measure*");

            // A literal selector covers the whole measured group, so a part of a group can never be approved.
            var a2 = HatchRecord("fam-a2", "asdasd23423", 40);
            var both = Scan(profile, state, a1, a2);
            var partial = () => service.SaveFamilyDecisions(
                profile, Library, both, new[] { Request("road-pavement", Group("A", a1)) }, "nataly", file, state);
            partial.Should().Throw<InvalidOperationException>().WithMessage("*whole measured group*");

            profile.Estimate.FamilyDecisions.Should().BeEmpty();
            File.ReadAllText(file).Should().Be(bytes);
        }

        [Fact]
        public void SupersedeAndRevokeKeepHistoryThroughSaveAndReload()
        {
            var (profile, file, state) = SavedProfile("family-history.yaml");
            var a1 = HatchRecord("fam-a1", "asdasd23423", 100);
            var group = Group("A", a1);
            var service = new EstimateWorkflowService();
            service.SaveFamilyDecisions(profile, Library, Scan(profile, state, a1),
                new[] { Request("road-pavement", group) }, "nataly", file, state);
            var first = profile.Estimate.FamilyDecisions.Single().DecisionId!;

            // The next decision starts from the saved bytes, as a rebased scan would.
            var state2 = ProfileCasTest.For(profile, file);
            service.SaveFamilyDecisions(profile, Library, Scan(profile, state2, a1),
                new[] { Request("sidewalk-paving", group) with { SupersedesDecisionId = first } }, "nataly", file, state2);

            var reloaded = ProjectProfileLoader.LoadFromFile(file);
            reloaded.IsUsable.Should().BeTrue(string.Join("; ", reloaded.Findings.Select(f => f.Code + ":" + f.Title)));
            var history = reloaded.Profile!.Estimate.FamilyDecisions;
            history.Should().HaveCount(2);
            var old = history.Single(d => d.DecisionId == first);
            var current = history.Single(d => d.DecisionId != first);
            old.Status.Should().Be(FamilyDecisionPolicy.Superseded);
            old.SupersededBy.Should().Be(current.DecisionId);
            current.Status.Should().Be(FamilyDecisionPolicy.Active);
            FamilyDecisionPolicy.Resolve(history, new[] { group }, Library).Single().FamilyId.Should().Be("sidewalk-paving");

            var state3 = ProfileCasTest.For(profile, file);
            service.RevokeFamilyDecision(profile, Scan(profile, state3, a1), current.DecisionId!,
                "זוהה בטעות", "nataly", file, state3);

            var afterRevoke = ProjectProfileLoader.LoadFromFile(file);
            afterRevoke.IsUsable.Should().BeTrue(string.Join("; ", afterRevoke.Findings.Select(f => f.Code + ":" + f.Title)));
            var entries = afterRevoke.Profile!.Estimate.FamilyDecisions;
            entries.Should().HaveCount(2, "revocation keeps history");
            entries.Single(d => d.DecisionId == current.DecisionId).Status.Should().Be(FamilyDecisionPolicy.Revoked);
            entries.Single(d => d.DecisionId == current.DecisionId).RevokedReason.Should().Be("זוהה בטעות");
            FamilyDecisionPolicy.Resolve(entries, new[] { group }, Library).Single().State
                .Should().Be(FamilyDecisionState.NotCovered);

            var state4 = ProfileCasTest.For(profile, file);
            var revokeAgain = () => service.RevokeFamilyDecision(profile, null, current.DecisionId!,
                "again", "nataly", file, state4);
            revokeAgain.Should().Throw<InvalidOperationException>();
            profile.Estimate.FamilyDecisions.Should().HaveCount(2);
        }
    }
}
