using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class SectionOwnedStateLogicTests
{
    private const string Profile = "6422";
    private const string Fingerprint = "fingerprint-v9";

    [Fact]
    public void ExactPairAndEntirelyLiveRegistry_IsTheOnlyUnchangedState()
    {
        var result = Evaluate(Pair(), Registry(entryCount: 8, liveEntryCount: 8));

        result.State.Should().Be(SectionOwnedStateLogic.State.Complete);
        result.AllowsUnchanged.Should().BeTrue();
    }

    [Fact]
    public void DeletedOwnedView_ForcesRepairInsteadOfUnchanged()
    {
        var result = Evaluate(
            new[] { SampleLine() }, Registry(entryCount: 8, liveEntryCount: 8));

        result.State.Should().Be(SectionOwnedStateLogic.State.Repairable);
        result.Reason.Should().Be("owned-section-view-missing");
        result.AllowsUnchanged.Should().BeFalse();
    }

    [Fact]
    public void MissingOrDeadAnnotationRegistry_ForcesRepairInsteadOfUnchanged()
    {
        Evaluate(Pair(), Registry(entryExists: false, entryCount: 0, liveEntryCount: 0))
            .State.Should().Be(SectionOwnedStateLogic.State.Repairable);
        Evaluate(Pair(), Registry(entryExists: true, entryCount: 0, liveEntryCount: 0))
            .Reason.Should().Be("annotation-registry-empty");
        Evaluate(Pair(), Registry(entryCount: 8, liveEntryCount: 7))
            .State.Should().Be(SectionOwnedStateLogic.State.Repairable);
    }

    [Fact]
    public void DuplicateOwnedRole_IsAConflictNeverUnchanged()
    {
        var duplicateSampleLine = Pair().Append(SampleLine("SL-2"));
        var duplicateView = Pair().Append(View("SV-2", "SL-1"));

        Evaluate(duplicateSampleLine, Registry()).Reason
            .Should().Be("duplicate-owned-sample-line");
        Evaluate(duplicateView, Registry()).Reason
            .Should().Be("duplicate-owned-section-view");
        Evaluate(duplicateSampleLine, Registry()).State
            .Should().Be(SectionOwnedStateLogic.State.Conflict);
    }

    [Fact]
    public void UnlinkedViewOrProfileMismatch_IsAConflict()
    {
        Evaluate(
                new[] { SampleLine(), View(parent: "SOME-OTHER-SL") }, Registry())
            .Reason.Should().Be("owned-view-not-linked-to-owned-sample-line");
        Evaluate(
                new[] { SampleLine(), View(profile: "another-project") }, Registry())
            .Reason.Should().Be("project-profile-mismatch");
    }

    [Fact]
    public void ChangedFingerprintOrLegacyKey_ForcesManagedRepair()
    {
        Evaluate(
                new[] { SampleLine(fingerprint: "old"), View(fingerprint: "old") }, Registry())
            .Reason.Should().Be("owned-fingerprint-mismatch");
        Evaluate(
                new[] { SampleLine(exact: false), View(exact: false) }, Registry())
            .Reason.Should().Be("legacy-logical-key-requires-migration");
    }

    [Fact]
    public void LegacyAnnotationsWithoutEntityOwnership_ForceOneTimeRepairNeverUnchanged()
    {
        var result = Evaluate(Pair(), Registry(ownershipComplete: false));

        result.State.Should().Be(SectionOwnedStateLogic.State.Repairable);
        result.Reason.Should().Be("annotation-ownership-migration-required");
        result.AllowsUnchanged.Should().BeFalse();
    }

    [Fact]
    public void LegacyRegistryWithoutCivilPair_IsUpdateMigration_NotCreateOrAdoption()
    {
        var result = Evaluate(
            Array.Empty<SectionOwnedStateLogic.ObjectEvidence>(),
            Registry(ownershipComplete: false));

        result.State.Should().Be(SectionOwnedStateLogic.State.Repairable);
        result.Reason.Should().Be("annotation-registry-requires-owned-pair-migration");
        Evaluate(Array.Empty<SectionOwnedStateLogic.ObjectEvidence>(),
                Registry(entryExists: false, entryCount: 0, liveEntryCount: 0))
            .State.Should().Be(SectionOwnedStateLogic.State.Absent);
        Evaluate(Array.Empty<SectionOwnedStateLogic.ObjectEvidence>(),
                Registry(readable: false))
            .State.Should().Be(SectionOwnedStateLogic.State.Conflict);
    }

    [Fact]
    public void MalformedRegistryOrUnreadableInventory_IsReviewConflict()
    {
        Evaluate(Pair(), Registry(readable: false)).State
            .Should().Be(SectionOwnedStateLogic.State.Conflict);
        SectionOwnedStateLogic.Evaluate(
                Pair(), Profile, Fingerprint, inventoryReadable: false, Registry())
            .State.Should().Be(SectionOwnedStateLogic.State.Conflict);
    }

    private static SectionOwnedStateLogic.Evaluation Evaluate(
        IEnumerable<SectionOwnedStateLogic.ObjectEvidence> objects,
        SectionOwnedStateLogic.RegistryEvidence registry) =>
        SectionOwnedStateLogic.Evaluate(
            objects, Profile, Fingerprint, inventoryReadable: true, registry);

    private static SectionOwnedStateLogic.ObjectEvidence[] Pair() =>
        new[] { SampleLine(), View() };

    private static SectionOwnedStateLogic.ObjectEvidence SampleLine(
        string identity = "SL-1",
        string profile = Profile,
        string fingerprint = Fingerprint,
        bool exact = true) =>
        new(identity, "sections", "sample-line", profile, fingerprint, exact);

    private static SectionOwnedStateLogic.ObjectEvidence View(
        string identity = "SV-1",
        string parent = "SL-1",
        string profile = Profile,
        string fingerprint = Fingerprint,
        bool exact = true) =>
        new(identity, "sections", "section-view", profile, fingerprint, exact, parent);

    private static SectionOwnedStateLogic.RegistryEvidence Registry(
        bool readable = true,
        bool entryExists = true,
        int entryCount = 8,
        int liveEntryCount = 8,
        bool ownershipComplete = true) =>
        new(readable, entryExists, entryCount, liveEntryCount, ownershipComplete);
}
