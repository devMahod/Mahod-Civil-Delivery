using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.AnnotationInventoryLogic;

namespace MahodAI.Core.Tests;

public sealed class AnnotationInventoryLogicTests
{
    private const string ShaA =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void ExactRegistryAndLayerSet_IsValid()
    {
        Evaluate(
            new[] { new Registered("A1", "K1", ShaA), new Registered("B2", "K2", ShaB) },
            new[]
            {
                new LayerEntity("B2", ShaB, OwnershipState.Valid),
                new LayerEntity("A1", ShaA, OwnershipState.Valid),
            }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void HandleRemovedOnlyFromRegistry_LeavesAnOrphanAndFails()
    {
        var verdict = Evaluate(
            new[] { new Registered("A1", "K1", ShaA) },
            new[]
            {
                new LayerEntity("A1", ShaA, OwnershipState.Valid),
                new LayerEntity("B2", ShaB, OwnershipState.Valid),
            });

        verdict.IsValid.Should().BeFalse();
        verdict.Problems.Should().Contain(problem => problem.Contains("B2") &&
            problem.Contains("unregistered"));
    }

    [Fact]
    public void MissingLiveEntityDuplicateFingerprintOrInvalidOwnership_Fails()
    {
        Evaluate(
            new[]
            {
                new Registered("A1", "K1", ShaA),
                new Registered("A1", "K2", ShaA),
                new Registered("B2", "K2", ShaB),
            },
            new[]
            {
                new LayerEntity("A1", ShaB, OwnershipState.Invalid),
            }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void RegisteredLegacyEntity_MayEnterOneTimeMigrationButCannotBeUnregistered()
    {
        Evaluate(
            new[] { new Registered("A1", "K1", ShaA) },
            new[] { new LayerEntity("A1", ShaA, OwnershipState.LegacyAbsent) })
            .IsValid.Should().BeTrue();
    }
}
