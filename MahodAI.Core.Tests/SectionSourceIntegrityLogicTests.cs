using System.IO;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class SectionSourceIntegrityLogicTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void MetresEvidence_IsRequiredAtPlanAndAtUseTime()
    {
        SectionSourceIntegrityLogic.ValidateMetres(6, 6).Should().BeNull();
        SectionSourceIntegrityLogic.ValidateMetres(null, 6)!.Kind.Should()
            .Be(SectionSourceIntegrityLogic.FailureKind.UnitNotMetres);
        SectionSourceIntegrityLogic.ValidateMetres(6, 4)!.Kind.Should()
            .Be(SectionSourceIntegrityLogic.FailureKind.UnitNotMetres);
    }

    [Fact]
    public void Revision_IsFailClosedWhenMissingOrChanged()
    {
        SectionSourceIntegrityLogic.ValidateRevision("db:4", "db:4").Should().BeNull();
        SectionSourceIntegrityLogic.ValidateRevision(null, "db:4")!.Kind.Should()
            .Be(SectionSourceIntegrityLogic.FailureKind.RevisionUnavailable);
        SectionSourceIntegrityLogic.ValidateRevision("db:4", "db:5")!.Kind.Should()
            .Be(SectionSourceIntegrityLogic.FailureKind.RevisionChanged);
    }

    [Fact]
    public void ExternalSource_RejectsChangedBytes()
    {
        var source = Path.GetFullPath("CL.dwg");
        var failures = SectionSourceIntegrityLogic.ValidateExternalSources(
            new[] { new SectionSourceIntegrityLogic.ExternalSourceSnapshot(source, HashA) },
            _ => new SectionSourceIntegrityLogic.ExternalSourceProbe(true, HashB));

        failures.Should().ContainSingle(f =>
            f.Kind == SectionSourceIntegrityLogic.FailureKind.SourceChanged);
    }

    [Fact]
    public void LiveExternalSource_MustRemainOpenAndAtCapturedRevision()
    {
        var source = Path.GetFullPath("CL.dwg");
        var snapshot = new SectionSourceIntegrityLogic.ExternalSourceSnapshot(
            source, HashA, "live:7", RequiresLiveDatabase: true);

        SectionSourceIntegrityLogic.ValidateExternalSources(
            new[] { snapshot },
            _ => new SectionSourceIntegrityLogic.ExternalSourceProbe(true, HashA, "live:7"))
            .Should().BeEmpty();

        SectionSourceIntegrityLogic.ValidateExternalSources(
            new[] { snapshot },
            _ => new SectionSourceIntegrityLogic.ExternalSourceProbe(true, HashA, null))
            .Should().ContainSingle(f =>
                f.Kind == SectionSourceIntegrityLogic.FailureKind.LiveSourceUnavailable);
    }

    [Fact]
    public void DuplicatePath_WithConflictingPlanHashes_IsRejectedBeforeProbe()
    {
        var source = Path.GetFullPath("CL.dwg");
        var probes = 0;
        var failures = SectionSourceIntegrityLogic.ValidateExternalSources(
            new[]
            {
                new SectionSourceIntegrityLogic.ExternalSourceSnapshot(source, HashA),
                new SectionSourceIntegrityLogic.ExternalSourceSnapshot(source, HashB),
            },
            _ =>
            {
                probes++;
                return new SectionSourceIntegrityLogic.ExternalSourceProbe(true, HashA);
            });

        probes.Should().Be(0);
        failures.Should().ContainSingle(f =>
            f.Kind == SectionSourceIntegrityLogic.FailureKind.SourceEvidenceConflict);
    }

    [Fact]
    public void DuplicateCanonicalPath_WithSameEvidence_IsProbedOnlyOnce()
    {
        var source = Path.GetFullPath(Path.Combine("evidence", "..", "CL.dwg"));
        var probes = 0;

        var failures = SectionSourceIntegrityLogic.ValidateExternalSources(
            new[]
            {
                new SectionSourceIntegrityLogic.ExternalSourceSnapshot(source, HashA),
                new SectionSourceIntegrityLogic.ExternalSourceSnapshot(
                    Path.Combine(Path.GetDirectoryName(source)!, ".", Path.GetFileName(source)), HashA),
            },
            _ =>
            {
                probes++;
                return new SectionSourceIntegrityLogic.ExternalSourceProbe(true, HashA);
            });

        failures.Should().BeEmpty();
        probes.Should().Be(1);
    }

    [Fact]
    public void LiveExternalSource_RejectsAChangedDatabaseRevision()
    {
        var source = Path.GetFullPath("CL.dwg");
        var failures = SectionSourceIntegrityLogic.ValidateExternalSources(
            new[]
            {
                new SectionSourceIntegrityLogic.ExternalSourceSnapshot(
                    source, HashA, "live:7", RequiresLiveDatabase: true),
            },
            _ => new SectionSourceIntegrityLogic.ExternalSourceProbe(true, HashA, "live:8"));

        failures.Should().ContainSingle(f =>
            f.Kind == SectionSourceIntegrityLogic.FailureKind.LiveSourceChanged);
    }

    [Fact]
    public void SourceChildren_RequireExactTypedIdentityAndCardinality()
    {
        static SectionSourceIntegrityLogic.SourceIdentity Source(
            string name, string kind, string handle) => new(name, kind, handle);

        var sampled = new[]
        {
            Source("EG", "surface", "10"),
            Source("FG", "surface", "20"),
            Source("HOT", "pipe-network", "30"),
        };

        SectionSourceIntegrityLogic.ExactSourceIdentitySet(sampled, new[]
        {
            Source("hot", "PIPE-NETWORK", "30"),
            Source("fg", "surface", "20"),
            Source("eg", "surface", "10"),
        }).Should().BeTrue();

        SectionSourceIntegrityLogic.ExactSourceIdentitySet(sampled, new[]
        {
            Source("EG", "surface", "10"),
            Source("FG", "surface", "20"),
            Source("HOT", "pipe-network", "30"),
            Source("EXTRA", "corridor", "40"),
        }).Should().BeFalse("an extra live Section child must stay red");

        SectionSourceIntegrityLogic.ExactSourceIdentitySet(sampled, new[]
        {
            Source("EG", "surface", "10"),
            Source("FG", "surface", "20"),
            Source("HOT", "pipe-network", "31"),
        }).Should().BeFalse("same display name with another source handle is not identity");

        SectionSourceIntegrityLogic.ExactSourceIdentitySet(sampled, new[]
        {
            Source("EG", "surface", "10"),
            Source("FG", "surface", "20"),
            Source("HOT", "corridor", "30"),
        }).Should().BeFalse("source kind is part of the contract");

        SectionSourceIntegrityLogic.ExactSourceIdentitySet(sampled, new[]
        {
            Source("EG", "surface", "10"),
            Source("FG", "surface", "20"),
            Source("HOT", "pipe-network", "30"),
            Source("HOT", "pipe-network", "30"),
        }).Should().BeFalse("duplicate children must not collapse to a set");
    }
}
