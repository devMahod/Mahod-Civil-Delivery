using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public class SectionProjectionFailureScopeTests
{
    private static DeliveryFinding Failure(double[]? bounds = null,
        string? role = "plan-region", string? code = null) => new()
    {
        Code = code ?? SectionFindingCodes.ProjectionGeometryUnsupported,
        Domain = "sections", Severity = FindingSeverity.Error, Title = "Unreadable HA hatch",
        Message = "handle=8BB299/262379; Native NotClosed",
        ProjectionRole = role, SourceBoundsWcs = bounds,
        SourceRefs = new() { new() { SourceKind = "xref", SourceHandle = "8BB299/262379" } },
        EvidenceRefs = new() { "native-source-evidence" },
    };

    [Theory]
    [InlineData(-2, 0, 2, 0, true)]
    [InlineData(0, -2, 0, 2, true)]
    [InlineData(-2, -2, 2, 2, true)]
    [InlineData(2, 2, -2, -2, true)]
    [InlineData(-2, 1, 2, 1, true)]
    [InlineData(-2, 1.005, 2, 1.005, true)]
    [InlineData(-2, 1.02, 2, 1.02, false)]
    [InlineData(2, 2, 3, 3, false)]
    [InlineData(-3, 0, -2, 0, false)]
    [InlineData(2, 0, 3, 0, false)]
    [InlineData(0, 0, .5, .5, true)]
    public void SegmentEnvelopeTestIsFiniteAndIncludesContact(double x1, double y1,
        double x2, double y2, bool affected) =>
        SectionProjectionFailureScope.MayIntersect(new[] { -1d, -1, 1, 1 },
            new[] { x1, y1, x2, y2 }).Should().Be(affected);

    public static IEnumerable<object?[]> UnknownEnvelopes()
    {
        yield return new object?[] { null };
        yield return new object?[] { Array.Empty<double>() };
        yield return new object?[] { new[] { double.NaN, 0, 1, 1 } };
        yield return new object?[] { new[] { 0d, 0, double.PositiveInfinity, 1 } };
        yield return new object?[] { new[] { 1d, 0, 0, 1 } };
        yield return new object?[] { new[] { 0d, 1, 1, 0 } };
        yield return new object?[] { new[] { 0d, 0, 0, 0 } };
    }

    [Theory, MemberData(nameof(UnknownEnvelopes))]
    public void MissingMalformedOrCollapsedBoundsRemainGlobal(double[]? bounds)
    {
        var finding = Failure(bounds);
        SectionProjectionFailureScope.AffectsCut(finding, new[] { 100d, 100, 200, 200 }).Should().BeTrue();
        SectionProjectionFailureScope.ForPlan(finding,
            new[] { new SectionProjectionFailureScope.Cut("far", new[] { 100d, 100, 200, 200 }) })
            .Should().BeSameAs(finding);
    }

    [Fact]
    public void UnknownCutCoordinatesOrOverflowNeverProveNoImpact()
    {
        var bounds = new[] { 0d, 0, 1, 1 };
        foreach (var cut in new double[]?[] { null, Array.Empty<double>(),
                     new[] { 0d, 0, 0, 0 }, new[] { double.NaN, 0, 1, 1 },
                     new[] { -double.MaxValue, 0, double.MaxValue, 0 } })
            SectionProjectionFailureScope.MayIntersect(bounds, cut).Should().BeTrue();
    }

    [Theory]
    [InlineData("SEC-XREF-MISSING", "plan-region")]
    [InlineData("SEC-XREF-TRAVERSAL-UNRESOLVED", "plan-region")]
    [InlineData("SEC-PROJECTION-GEOMETRY-UNSUPPORTED", "unknown")]
    [InlineData("SEC-PROJECTION-GEOMETRY-UNSUPPORTED", null)]
    public void SourceFailuresAndUnknownRolesCannotUseAnEnvelopeWaiver(string code, string? role)
    {
        var finding = Failure(new[] { 0d, 0, 1, 1 }, role, code);
        SectionProjectionFailureScope.AffectsCut(finding, new[] { 10d, 10, 20, 20 }).Should().BeTrue();
    }

    [Fact]
    public void ABScenarioOnlyIntersectingCutIsBlockedAndFailureEvidenceSurvives()
    {
        var original = Failure(new[] { -1d, -1, 1, 1 });
        var scoped = SectionProjectionFailureScope.ForPlan(original, new[]
        {
            new SectionProjectionFailureScope.Cut("A", new[] { -5d, 0, 5, 0 }),
            new SectionProjectionFailureScope.Cut("B", new[] { -5d, 50, 5, 50 }),
        });
        scoped.Severity.Should().Be(FindingSeverity.Error);
        scoped.AffectedRecordIds.Should().Equal("A");
        scoped.SourceRefs.Should().BeEquivalentTo(original.SourceRefs);
        scoped.EvidenceRefs.Should().Equal("native-source-evidence");
        scoped.Message.Should().Be(original.Message);
        original.AffectedRecordIds.Should().BeEmpty("the source finding is immutable during scoping");
        SectionProjectionFailureScope.ForCut(original, "A").AffectedRecordIds.Should().Equal("A");
    }

    [Fact]
    public void ProvenFarFailureRemainsAnExplicitUnresolvedWarningNotAHiddenSuccess()
    {
        var original = Failure(new[] { -1d, -1, 1, 1 });
        var scoped = SectionProjectionFailureScope.ForPlan(original,
            new[] { new SectionProjectionFailureScope.Cut("B", new[] { -5d, 50, 5, 50 }) });
        scoped.Severity.Should().Be(FindingSeverity.Warning);
        scoped.Code.Should().Be(SectionFindingCodes.ProjectionGeometryOutsideCuts);
        scoped.SourceRefs.Should().BeEquivalentTo(original.SourceRefs);
        scoped.SourceBoundsWcs.Should().Equal(original.SourceBoundsWcs!);
        scoped.ResolvedAtUtc.Should().BeNull();
        scoped.AffectedRecordIds.Should().BeEmpty();
        SectionProjectionFailureScope.ForPlan(original, Array.Empty<SectionProjectionFailureScope.Cut>())
            .Should().BeSameAs(original);
    }

    [Theory]
    [InlineData("plan-region", false)]
    [InlineData("plan-mark", false)]
    [InlineData("projected-utility", true)]
    [InlineData("unknown", true)]
    public void AFailedSidewalkDoesNotEraseMeasuredUtilitiesButStillBlocksItsCut(string role, bool utilityBlocked)
    {
        var f = Failure(new[] { -1d, -1, 1, 1 }, role);
        var cut = new[] { -5d, 0, 5, 0 };
        SectionProjectionFailureScope.AffectsCut(f, cut).Should().BeTrue();
        SectionProjectionFailureScope.BlocksUtilityScan(f, cut).Should().Be(utilityBlocked);
        SectionProjectionFailureScope.BlocksUtilityScan(
            Failure(null, role, SectionFindingCodes.XrefMissing), cut).Should().BeTrue();
    }

    [Fact]
    public void UnreadableAreaOnAFullyNamedCut_IsAWarning_NotABlock()
    {
        // Live 06/09: sidewalk hatch 8BB299/262391 met STA-12145, whose eight strips
        // were all named by the engineer. Name evidence cannot block a cut it can
        // neither name nor contradict.
        var region = Failure(new[] { -1d, -1, 1, 1 });
        var cut = new[] { -5d, 0, 5, 0 };
        SectionProjectionFailureScope.BlocksCreation(region, cut, 0).Should().BeFalse();
        SectionProjectionFailureScope.BlocksCreation(region, cut, 1).Should().BeTrue("an unnamed span could have been named by that area");
        SectionProjectionFailureScope.BlocksCreation(region, cut, int.MaxValue).Should().BeTrue("unknown naming state fails closed");
        SectionProjectionFailureScope.BlocksCreation(Failure(new[] { -1d, -1, 1, 1 }, "plan-mark"), cut, 0)
            .Should().BeTrue("a mark defines widths; only areas are name-only evidence");
        SectionProjectionFailureScope.BlocksCreation(Failure(null), cut, 0)
            .Should().BeTrue("unknown bounds stay global even on a named cut");
        SectionProjectionFailureScope.BlocksCreation(region, new[] { -5d, 50, 5, 50 }, 0).Should().BeFalse();

        var named = SectionProjectionFailureScope.ForNamedCut(region, "A");
        named.Severity.Should().Be(FindingSeverity.Warning);
        named.Code.Should().Be(SectionFindingCodes.ProjectionRegionUnreadableNamed);
        named.AffectedRecordIds.Should().Equal("A");
        named.Message.Should().Be(region.Message);
        named.SourceRefs.Should().BeEquivalentTo(region.SourceRefs);

        var onlyNamed = SectionProjectionFailureScope.ForPlan(region, new[]
        {
            new SectionProjectionFailureScope.Cut("A", cut, 0),
            new SectionProjectionFailureScope.Cut("far", new[] { -5d, 50, 5, 50 }, 3),
        });
        onlyNamed.Severity.Should().Be(FindingSeverity.Warning);
        onlyNamed.Code.Should().Be(SectionFindingCodes.ProjectionRegionUnreadableNamed);
        onlyNamed.AffectedRecordIds.Should().Equal("A");

        var mixed = SectionProjectionFailureScope.ForPlan(region, new[]
        {
            new SectionProjectionFailureScope.Cut("A", cut, 0),
            new SectionProjectionFailureScope.Cut("B", new[] { -5d, 0.5, 5, 0.5 }, 2),
        });
        mixed.Severity.Should().Be(FindingSeverity.Error);
        mixed.AffectedRecordIds.Should().ContainSingle().Which.Should().Be("B", "the cut with unnamed spans stays blocked; the named one does not");
        region.AffectedRecordIds.Should().BeEmpty("the source finding is immutable during scoping");
    }

    [Fact]
    public void FailureScopeRoundTripsAndOldEvidenceStillFailsClosed()
    {
        var f = Failure(new[] { -1d, -1, 1, 1 });
        var copy = JsonSerializer.Deserialize<DeliveryFinding>(JsonSerializer.Serialize(f))!;
        copy.ProjectionRole.Should().Be("plan-region");
        copy.SourceBoundsWcs.Should().Equal(f.SourceBoundsWcs!);
        var legacy = JsonSerializer.Deserialize<DeliveryFinding>(
            "{\"code\":\"SEC-PROJECTION-GEOMETRY-UNSUPPORTED\",\"domain\":\"sections\",\"severity\":\"Error\",\"title\":\"legacy\"}")!;
        SectionProjectionFailureScope.AffectsCut(legacy, new[] { 100d, 100, 200, 200 }).Should().BeTrue();
    }
}
