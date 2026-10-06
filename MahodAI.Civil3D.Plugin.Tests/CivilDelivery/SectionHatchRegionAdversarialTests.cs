using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SectionHatchRegionAdversarialTests
{
    private static IReadOnlyList<P2> Box(double x1, double y1, double x2, double y2) =>
        new[] { new P2(x1, y1), new P2(x2, y1), new P2(x2, y2), new P2(x1, y2) };

    private static SectionHatchSpanLabelService.Region Region(string label,
        IReadOnlyList<IReadOnlyList<P2>>? loops = null, double error = 0) =>
        SectionHatchSpanLabelService.CreateRegion(loops ?? new[] { Box(-5, -2, 5, 2) },
            SectionRegionCoverageLogic.FillStyle.Normal, label, "approved-layer", "GM", label,
            "C:/source.dwg", new string('A', 64), error);

    private static Crossing Mark(double offset, string kind = "curb", string label = "אבן שפה",
        double? x = null, double y = 0, string? handle = null) =>
        new(offset, null, kind, "GM", new(kind, label, 7), handle ?? $"{kind}:{offset}", x ?? offset, y);

    private static PresentationAnalysis Analyze(IReadOnlyList<Crossing> marks,
        IReadOnlyList<SpanLabelOverride>? overrides = null) =>
        AnalyzePresentationCoverage(marks.Select(mark =>
            new PresentationMark(mark.Offset, mark.Rule.Kind, mark.Rule.Label, ProjectionEvidenceKey(mark))),
            approvedOverrides: overrides);

    private static PresentationAnalysis Resolve(IReadOnlyList<Crossing> marks,
        IReadOnlyList<SectionHatchSpanLabelService.Region> regions,
        IReadOnlyList<SpanLabelOverride>? decisions = null)
    {
        var actual = decisions?.Where(item => !SectionHatchSpanLabelService.IsRegionEvidence(item.Source)).ToList()
                     ?? new List<SpanLabelOverride>();
        var baseline = Analyze(marks, actual);
        actual.AddRange(SectionHatchSpanLabelService.Resolve(regions, marks, baseline));
        return Analyze(marks, actual);
    }

    [Fact]
    public void ARegionContradictingAnAlreadyResolvedBoundarySpan_CannotBeIgnored()
    {
        var marks = new[] { Mark(-3, "sidewalk", "מדרכה"), Mark(3, "sidewalk", "מדרכה") };
        Analyze(marks).Summary.IsComplete.Should().BeTrue();
        var result = Resolve(marks, new[] { Region("גינון") });
        result.Summary.IsComplete.Should().BeFalse();
        result.UnresolvedSpans.Should().ContainSingle().Which.Reason.Should().Be("conflicting-strip-label-evidence");
    }

    [Fact]
    public void ARegionContradictingAnInteriorSourceStripMark_RemainsUnresolved()
    {
        var marks = new[] { Mark(-3), Mark(0, "strip", "נת\"צ"), Mark(3) };
        var result = Resolve(marks, new[] { Region("שביל אופניים") });
        result.Summary.IsComplete.Should().BeFalse();
        result.StripLabels.Should().BeEmpty();
    }

    [Fact]
    public void SmallPartialOverlapOfADifferentArea_CannotHideBehindAFullRegion()
    {
        var marks = new[] { Mark(-3), Mark(3) };
        var result = Resolve(marks, new[]
        {
            Region("מדרכה"), Region("גינון", new[] { Box(2.123, -1, 2.125, 1) }),
        });
        result.Summary.IsComplete.Should().BeFalse();
        result.UnresolvedSpans.Should().ContainSingle();
    }

    [Fact]
    public void PartialEvidenceByItself_NeverNamesAnEntireSpan()
    {
        var marks = new[] { Mark(-3), Mark(3) };
        var partial = Region("גינון", new[] { Box(2, -1, 4, 1) });
        SectionHatchSpanLabelService.Resolve(new[] { partial }, marks, Analyze(marks))
            .Should().BeEmpty();
        Resolve(marks, new[] { partial }).Summary.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void AManualWholeSpanName_CannotBypassConflictingPartialSourceEvidence()
    {
        var marks = new[] { Mark(-3), Mark(3) };
        var manual = new[] { new SpanLabelOverride(0, "מדרכה", "manual-profile", "approved-review") };
        Analyze(marks, manual).Summary.IsComplete.Should().BeTrue();
        var result = Resolve(marks, new[] { Region("גינון", new[] { Box(2, -1, 4, 1) }) }, manual);
        result.Summary.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void APartialSameLabelRegion_DoesNotInventAConflictWithAFullSource()
    {
        var marks = new[] { Mark(-3), Mark(3) };
        var result = Resolve(marks, new[] { Region("מדרכה"), Region("מדרכה", new[] { Box(2, -1, 4, 1) }) });
        result.Summary.IsComplete.Should().BeTrue();
        result.StripLabels.Should().ContainSingle().Which.Label.Should().Be("מדרכה");
    }

    [Fact]
    public void CoincidentCrossingRoundoff_DoesNotSilentlyDiscardUsefulRegionEvidence()
    {
        var marks = new[] { Mark(-3), Mark(-3, "mark", "", -3 + 1e-10, 1e-10, "roundoff"), Mark(3) };
        var result = Resolve(marks, new[] { Region("מדרכה") });
        result.Summary.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void DistinctWorldEndpointsAtOneOffset_RemainAmbiguous()
    {
        var marks = new[] { Mark(-3), Mark(-3, "mark", "", -3, 0.02, "different"), Mark(3) };
        Resolve(marks, new[] { Region("מדרכה") }).Summary.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void RegionProvenanceOfAlreadyNamedSpans_ParticipatesInFreshEvidenceDigest()
    {
        var marks = new[] { Mark(-3, "sidewalk", "מדרכה"), Mark(3, "sidewalk", "מדרכה") };
        var first = Resolve(marks, new[] { Region("מדרכה") });
        var shifted = Resolve(marks, new[] { Region("מדרכה", new[] { Box(-5, -2, 5.1, 2) }) });
        first.Summary.IsComplete.Should().BeTrue();
        shifted.Summary.IsComplete.Should().BeTrue();
        shifted.Summary.EvidenceDigest.Should().NotBe(first.Summary.EvidenceDigest);
    }

    [Fact]
    public void ApplyRemovesOldConflictEvidenceAndRecomputesTheCurrentSource()
    {
        var marks = new[] { Mark(-3, "sidewalk", "מדרכה"), Mark(3, "sidewalk", "מדרכה") };
        var old = SectionHatchSpanLabelService.Resolve(new[] { Region("גינון") }, marks, Analyze(marks));
        old.Should().HaveCount(2);
        old.Should().OnlyContain(item => SectionHatchSpanLabelService.IsRegionEvidence(item.Source));
        var refreshed = Resolve(marks, new[] { Region("מדרכה") }, old);
        refreshed.Summary.IsComplete.Should().BeTrue();
        refreshed.StripLabels.Should().ContainSingle().Which.Label.Should().Be("מדרכה");
    }

    [Fact]
    public void MirroredAnisotropicRegionAndHole_UseWorldSpanEndpointsNotOffsetAsX()
    {
        static P2 Transform(P2 point) => new(200000 + 2 * point.Y, 650000 + 3 * point.X);
        var loops = new[] { Box(-5, -2, 5, 2), Box(1, -0.5, 2, 0.5) }
            .Select(loop => (IReadOnlyList<P2>)loop.Select(Transform).ToArray()).ToArray();
        var from = Transform(new(-3, 0));
        var to = Transform(new(3, 0));
        var marks = new[] { Mark(-3, x: from.X, y: from.Y), Mark(3, x: to.X, y: to.Y) };
        Resolve(marks, new[] { Region("מדרכה", loops) }).Summary.IsComplete.Should().BeFalse();
    }
}
