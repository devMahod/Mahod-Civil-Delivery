using System;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.AnnotationInventoryLogic;

namespace MahodAI.Core.Tests;

public sealed class AnnotationRegistryRecoveryTests
{
    private static readonly string Sha = new('a', 64);
    private static Registered Entry(string handle = "BD89FC", string key = "K-target") => new(handle, key, Sha);

    [Fact]
    public void SingleOtherRepairTargetBlocksCreateButItsOwnUpdateCanProceed()
    {
        var create = EvaluateRepairScope(new[] { "repair-A" }, new[] { "create-B" });
        create.CanProceed.Should().BeFalse();
        create.RequiresBatch.Should().BeFalse();
        create.RequiredRecordIds.Should().Equal("repair-A");
        create.OutsideTargetRecordIds.Should().Equal("repair-A");
        EvaluateRepairScope(new[] { "repair-A" }, new[] { "repair-A" }).CanProceed.Should().BeTrue();
    }

    [Fact]
    public void MultipleRepairKeysRequireCompleteExplicitBatch_NoCrossTargetCleanup()
    {
        var selected = EvaluateRepairScope(new[] { "repair-B", "repair-A", "repair-A" }, new[] { "repair-A" });
        selected.CanProceed.Should().BeFalse();
        selected.RequiresBatch.Should().BeTrue();
        selected.RequiredRecordIds.Should().Equal("repair-A", "repair-B");
        selected.OutsideTargetRecordIds.Should().Equal("repair-B");
        EvaluateRepairScope(selected.RequiredRecordIds, new[] { "repair-B", "repair-A", "create-C" })
            .CanProceed.Should().BeTrue();
    }

    [Fact]
    public void NoRepairWarningsLeavesNormalSelectedScopeUnchanged()
    {
        var scope = EvaluateRepairScope(Array.Empty<string>(), new[] { "create" });
        scope.CanProceed.Should().BeTrue();
        scope.RequiredRecordIds.Should().BeEmpty();
    }

    [Fact]
    public void EightyNineProvenDeadReferencesCanBeRepairedButStrictVerificationStillFailsBeforeCleanup()
    {
        var entries = Enumerable.Range(0, 89).Select(i => Entry((0xBD8900 + i).ToString("X"))).ToArray();
        var lookups = entries.Select(e => new HandleLookup(e.Handle, HandleState.Missing)).ToArray();
        Evaluate(entries, Array.Empty<LayerEntity>()).IsValid.Should().BeFalse();
        var recovery = EvaluateRecovery(entries, Array.Empty<LayerEntity>(), lookups, new[] { "K-target" });
        recovery.IsValid.Should().BeTrue();
        recovery.DeadEntries.Should().BeEquivalentTo(entries);
        // Read-only PLAN did not change its inputs. Strict verification becomes valid
        // only after the exact recovery proposal is applied to the registry set.
        entries.Should().HaveCount(89);
        Evaluate(entries.Except(recovery.DeadEntries), Array.Empty<LayerEntity>()).IsValid.Should().BeTrue();
        EvaluateRecovery(entries.Except(recovery.DeadEntries), Array.Empty<LayerEntity>(), lookups,
            new[] { "K-target" }).DeadEntries.Should().BeEmpty("a repaired registry is an idempotent no-op");
    }

    [Theory]
    [InlineData(HandleState.Missing, true)]
    [InlineData(HandleState.Erased, true)]
    [InlineData(HandleState.Live, false)]
    [InlineData(HandleState.Unreadable, false)]
    public void AbsenceFromOwnedLayerIsNotEnough_HandleMustBeIndependentlyProvenDead(HandleState state, bool eligible)
    {
        var result = EvaluateRecovery(new[] { Entry() }, Array.Empty<LayerEntity>(),
            new[] { new HandleLookup("BD89FC", state) }, new[] { "K-target" });
        result.IsValid.Should().Be(eligible);
        result.DeadEntries.Count.Should().Be(eligible ? 1 : 0);
    }

    [Fact]
    public void AnotherTargetsMissingReferencePreventsPartialCleanup()
    {
        var entries = new[] { Entry(), Entry("BD89FD", "K-other") };
        var result = EvaluateRecovery(entries, Array.Empty<LayerEntity>(),
            entries.Select(e => new HandleLookup(e.Handle, HandleState.Missing)), new[] { "K-target" });
        result.IsValid.Should().BeFalse();
        result.DeadEntries.Should().BeEmpty();
        result.Problems.Should().Contain(p => p.Contains("BD89FD"));
    }

    [Fact]
    public void MixedLiveAndDeadKeepsExactLiveOwnedEntitiesAndRejectsEditedFingerprints()
    {
        var entries = new[] { Entry(), Entry("AB12") };
        var live = new[] { new LayerEntity("AB12", Sha, OwnershipState.Valid) };
        var states = new[] { new HandleLookup("BD89FC", HandleState.Erased) };
        var result = EvaluateRecovery(entries, live, states, new[] { "K-target" });
        result.IsValid.Should().BeTrue();
        result.DeadEntries.Should().ContainSingle().Which.Handle.Should().Be("BD89FC");
        Evaluate(entries.Except(result.DeadEntries), live).IsValid.Should().BeTrue();
        EvaluateRecovery(entries, new[] { live[0] with { Fingerprint = new string('b', 64) } },
            states, new[] { "K-target" }).DeadEntries.Should().BeEmpty();
        EvaluateRecovery(entries, new[] { live[0] with { Ownership = OwnershipState.Invalid } },
            states, new[] { "K-target" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void UnregisteredLiveEntityAndDuplicateRegistryHandleBlockAllRecovery()
    {
        var states = new[] { new HandleLookup("BD89FC", HandleState.Missing) };
        EvaluateRecovery(new[] { Entry() }, new[] { new LayerEntity("FOREIGN", Sha, OwnershipState.LegacyAbsent) },
            states, new[] { "K-target" }).IsValid.Should().BeFalse();
        EvaluateRecovery(new[] { Entry(), Entry() }, Array.Empty<LayerEntity>(), states,
            new[] { "K-target" }).DeadEntries.Should().BeEmpty();
    }

    [Fact]
    public void LegacyUnpinnedOrMissingAmbiguousLookupNeverAuthorizesCleanup()
    {
        var dead = new HandleLookup("BD89FC", HandleState.Missing);
        foreach (var evidence in new[] { Array.Empty<HandleLookup>(), new[] { dead, dead } })
            EvaluateRecovery(new[] { Entry() }, Array.Empty<LayerEntity>(), evidence,
                new[] { "K-target" }).IsValid.Should().BeFalse();
        foreach (var fingerprint in new[] { "", "not-sha", new string('z', 64) })
            EvaluateRecovery(new[] { Entry() with { Fingerprint = fingerprint } }, Array.Empty<LayerEntity>(),
                new[] { dead }, new[] { "K-target" }).DeadEntries.Should().BeEmpty();
        foreach (var handle in new[] { "0", "not-a-handle", "FFFFFFFFFFFFFFFF" })
            EvaluateRecovery(new[] { Entry(handle) }, Array.Empty<LayerEntity>(),
                new[] { new HandleLookup(handle, HandleState.Missing) }, new[] { "K-target" })
                .DeadEntries.Should().BeEmpty();
    }

    [Fact]
    public void ContradictoryLiveLayerEvidenceCannotBePrunedEvenWhenLookupSaysMissing()
    {
        var result = EvaluateRecovery(new[] { Entry() },
            new[] { new LayerEntity("BD89FC", Sha, OwnershipState.Valid) },
            new[] { new HandleLookup("BD89FC", HandleState.Missing) }, new[] { "K-target" });
        result.DeadEntries.Should().BeEmpty();
    }

    private static SectionOwnedStateLogic.ObjectEvidence[] Pair() => new[]
    {
        new SectionOwnedStateLogic.ObjectEvidence("SL1", "sections", "sample-line", "6422", "old", true),
        new SectionOwnedStateLogic.ObjectEvidence("SV1", "sections", "section-view", "6422", "old", true, "SL1"),
    };
    private static SectionOwnedStateLogic.RegistryEvidence DeadRegistry() => new(true, true, 89, 0, false);

    [Fact]
    public void ExistingExactOwnedPairWithChangedFingerprintIsEligibleForMissingAnnotationRecovery()
    {
        SectionOwnedStateLogic.Evaluate(Pair(), "6422", "new", true, DeadRegistry()).Reason
            .Should().Be("owned-fingerprint-mismatch");
        SectionOwnedStateLogic.CanRepairDeadAnnotations(Pair(), "6422", "new", true, DeadRegistry())
            .Should().BeTrue();
    }

    [Fact]
    public void BareRegistryLegacyIdentityWrongProfileDuplicateAndUnlinkedPairAreNotRecoveryAuthority()
    {
        var pair = Pair();
        foreach (var objects in new[]
        {
            Array.Empty<SectionOwnedStateLogic.ObjectEvidence>(),
            new[] { pair[0] with { LogicalKeyIsExact = false }, pair[1] },
            new[] { pair[0], pair[1] with { ProjectProfileId = "other" } },
            pair.Append(pair[0] with { ObjectIdentity = "SL2" }).ToArray(),
            new[] { pair[0], pair[1] with { ParentSampleLineIdentity = "foreign" } },
        })
            SectionOwnedStateLogic.CanRepairDeadAnnotations(objects, "6422", "new", true, DeadRegistry())
                .Should().BeFalse();
        SectionOwnedStateLogic.CanRepairDeadAnnotations(pair, "6422", "new", false, DeadRegistry()).Should().BeFalse();
        SectionOwnedStateLogic.CanRepairDeadAnnotations(pair, "6422", "new", true,
            DeadRegistry() with { IsReadable = false }).Should().BeFalse();
    }
}
