using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Arrow = MahodAI.CivilDelivery.Shared.TrafficDirectionEvidenceLogic.ArrowEvidence;
using P2 = MahodAI.CivilDelivery.Shared.SectionProjectionLogic.P2;

namespace MahodAI.Core.Tests;

/// <summary>
/// Synthetic source-arrow track contracts only. These tests do not constitute
/// native 6422 geometry/vehicle acceptance or infer internal lane boundaries.
/// </summary>
public sealed class SectionTrafficTrackLogicTests
{
    private const double Heading = Math.PI / 2;
    private const string Source = "SM.dwg|sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static SectionCutFrame Frame()
    {
        SectionCutFrame.TryCreate(new P2(-10, 0), new P2(10, 0), new P2(0, 0),
            90, out var frame).Should().BeTrue();
        return frame!;
    }

    private static Arrow Motor(double offset, double along, string handle,
        double heading = Heading) =>
        new(offset, along, heading, "SM|BL-TR-ARRW", "TR-ARW", Source, "SM-INSERT/" + handle);

    private static SectionTrafficTrackLogic.Resolution Resolve(
        IReadOnlyList<Arrow> arrows, double from = -5, double to = 5,
        double heading = Heading) =>
        SectionTrafficTrackLogic.Resolve(Frame(), heading, from, to, arrows);

    private static void Refused(SectionTrafficTrackLogic.Resolution result)
    {
        result.IsResolved.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void HostArrowNullChainIsExplicitCollectorContract_AndExternalAuthorityHashChangesDigest()
    {
        var arrows = new[] { Motor(-2, 1, "A") with { Source = null }, Motor(2, 1, "B") with { Source = null } };
        Resolve(arrows).IsResolved.Should().BeTrue();
        var first = SectionTrafficTrackLogic.Resolve(Frame(), Heading, -5, 5, arrows, new string('a', 64));
        var changed = SectionTrafficTrackLogic.Resolve(Frame(), Heading, -5, 5, arrows, new string('b', 64));
        first.IsResolved.Should().BeTrue(); changed.IsResolved.Should().BeTrue();
        first.Tracks[0].EvidenceDigest.Should().NotBe(changed.Tracks[0].EvidenceDigest);
    }

    [Fact]
    public void TwoSourceTracks_RetainActualOffsetsAndResolveOppositeFlowsIndependently()
    {
        var arrows = new[] { Motor(-2, 1, "A"), Motor(2, 1, "B", -Heading) };
        var result = Resolve(arrows);

        result.IsResolved.Should().BeTrue();
        result.Tracks.Should().HaveCount(2);
        result.Tracks[0].OffsetM.Should().BeApproximately(-2, 1e-12);
        result.Tracks[1].OffsetM.Should().BeApproximately(2, 1e-12);
        result.Tracks.SelectMany(track => track.Arrows).Should().BeEquivalentTo(arrows);
        result.Tracks.Should().OnlyContain(track => track.Arrows.Count == 1);

        var flows = result.Tracks.Select(track =>
        {
            var point = Frame().PointAt(track.OffsetM);
            var direction = TrafficDirectionEvidenceLogic.ResolveNearest(
                point.X, point.Y, Heading, track.Arrows);
            direction.IsResolved.Should().BeTrue();
            return TrafficDirectionEvidenceLogic.RelativeToAlignment(
                direction.HeadingRadians!.Value, Heading);
        }).ToArray();
        flows.Should().Equal(TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment,
            TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment);
    }

    [Fact]
    public void AlongTrackRepeats_RetainEveryArrowButUseClosestSourceOffsetRatherThanMean()
    {
        var near = Motor(-2.1, 1, "NEAR");
        var far = Motor(-2.3, 8, "FAR");
        var result = Resolve(new[] { far, Motor(2, 1, "RIGHT"), near });

        result.IsResolved.Should().BeTrue();
        result.Tracks.Should().HaveCount(2);
        result.Tracks[0].OffsetM.Should().BeApproximately(-2.1, 1e-12);
        result.Tracks[0].Arrows.Should().BeEquivalentTo(new[] { near, far });
        result.Tracks.SelectMany(track => track.Arrows).Should().HaveCount(3);
    }

    [Fact]
    public void EqualAlongDistance_UsesStableSourceIdentityForRepresentative()
    {
        var a = Motor(-2.1, 2, "A");
        var z = Motor(-2.3, -2, "Z");
        var result = Resolve(new[] { z, Motor(2, 1, "RIGHT"), a });

        result.IsResolved.Should().BeTrue();
        result.Tracks[0].OffsetM.Should().BeApproximately(-2.1, 1e-12);
    }

    [Fact]
    public void InputPermutationAndIdenticalDuplicates_DoNotChangeTracksOrDigests()
    {
        var a = Motor(-2.1, 1, "A");
        var b = Motor(-2.3, 8, "B");
        var c = Motor(2, 1, "C", -Heading);
        var baseline = Resolve(new[] { a, b, c });
        baseline.IsResolved.Should().BeTrue();

        foreach (var reordered in new[]
                 {
                     new[] { c, b, a }, new[] { b, a, c },
                     new[] { c, a, b, a, c }
                 })
        {
            var result = Resolve(reordered);
            result.IsResolved.Should().BeTrue();
            result.Tracks.Select(track => track.OffsetM)
                .Should().Equal(baseline.Tracks.Select(track => track.OffsetM));
            result.Tracks.Select(track => track.EvidenceDigest)
                .Should().Equal(baseline.Tracks.Select(track => track.EvidenceDigest));
            result.Tracks.SelectMany(track => track.Arrows).Should().HaveCount(3);
        }
    }

    [Theory]
    [InlineData("x")]
    [InlineData("y")]
    [InlineData("heading")]
    [InlineData("layer")]
    [InlineData("block")]
    [InlineData("source")]
    [InlineData("handle")]
    public void EverySourceEvidenceField_ChangesItsTrackDigest(string field)
    {
        var a = Motor(-2, 1, "A");
        var b = Motor(2, 1, "B");
        var changed = field switch
        {
            "x" => a with { X = a.X + 0.01 },
            "y" => a with { Y = a.Y + 0.01 },
            "heading" => a with { HeadingRadians = a.HeadingRadians + 0.01 },
            "layer" => a with { Layer = "SM|BL-TR-ARRW-WHITE" },
            "block" => a with { BlockName = "TR-ARW-STRAIGHT" },
            "source" => a with { Source = Source + "-changed" },
            "handle" => a with { HandlePath = a.HandlePath + "-changed" },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        var baseline = Resolve(new[] { a, b });
        var result = Resolve(new[] { changed, b });
        baseline.IsResolved.Should().BeTrue();
        result.IsResolved.Should().BeTrue();
        result.Tracks[0].EvidenceDigest.Should().NotBeNullOrWhiteSpace()
            .And.NotBe(baseline.Tracks[0].EvidenceDigest);
    }

    [Fact]
    public void NonRepresentativeArrowAndExactSpanBounds_AlsoParticipateInDigest()
    {
        var near = Motor(-2, 1, "NEAR");
        var far = Motor(-2.2, 8, "FAR");
        var right = Motor(2, 1, "RIGHT");
        var baseline = Resolve(new[] { near, far, right });
        var changedFar = Resolve(new[] { near, far with { Y = 9 }, right });
        var changedFrom = Resolve(new[] { near, far, right }, from: -5.01);
        var changedTo = Resolve(new[] { near, far, right }, to: 5.01);

        baseline.IsResolved.Should().BeTrue();
        foreach (var changed in new[] { changedFar, changedFrom, changedTo })
        {
            changed.IsResolved.Should().BeTrue();
            changed.Tracks[0].OffsetM.Should().Be(baseline.Tracks[0].OffsetM);
            changed.Tracks[0].EvidenceDigest.Should().NotBe(baseline.Tracks[0].EvidenceDigest);
        }
    }

    [Fact]
    public void SameSourceIdentityWithConflictingGeometry_IsRejectedInsteadOfTakingFirst()
    {
        var a = Motor(-2, 1, "A");
        Refused(Resolve(new[] { a, Motor(2, 1, "B"), a with { X = -2.01 } }));
        Refused(Resolve(new[] { a with { X = -2.01 }, Motor(2, 1, "B"), a }));
        Refused(Resolve(new[] { a, Motor(2, 1, "B"), a with { HeadingRadians = -Heading } }));
    }

    [Fact]
    public void RepeatedArrowsOnOneTrack_DoNotInventASecondVehicle()
    {
        Refused(Resolve(new[] { Motor(-2, 1, "A"), Motor(-2.1, 8, "B") }));
        Refused(Resolve(Array.Empty<Arrow>()));
    }

    [Fact]
    public void SeparateEvidenceGroupsTooCloseForCars_AreRejectedWithoutMovingSources()
    {
        var arrows = new[] { Motor(-1, 0, "A"), Motor(1, 0, "B") };
        var snapshot = arrows.ToArray();

        Refused(Resolve(arrows)); // 2.0 m exceeds grouping 1.75 m, but not car clearance 2.2 m.
        arrows.Should().Equal(snapshot);
    }

    [Theory]
    [InlineData(-3.95, 2)]
    [InlineData(-2, 3.95)]
    public void SourceTrackTooNearEitherSpanEdge_IsRejected(double left, double right)
    {
        Refused(Resolve(new[] { Motor(left, 0, "A"), Motor(right, 0, "B") }));
    }

    [Theory]
    [InlineData("source", "")]
    [InlineData("source", " ")]
    [InlineData("handle", "")]
    [InlineData("handle", " ")]
    public void MissingSourceIdentity_IsRejected(string field, string? value)
    {
        var a = Motor(-2, 1, "A");
        var invalid = field == "source"
            ? a with { Source = value }
            : a with { HandlePath = value! };
        Refused(Resolve(new[] { invalid, Motor(2, 1, "B") }));
    }

    [Theory]
    [InlineData("x", double.NaN)]
    [InlineData("y", double.PositiveInfinity)]
    [InlineData("heading", double.NaN)]
    [InlineData("heading", double.NegativeInfinity)]
    public void NonFiniteAdmittedArrow_IsRejected(string field, double value)
    {
        var a = Motor(-2, 1, "A");
        var invalid = field switch
        {
            "x" => a with { X = value },
            "y" => a with { Y = value },
            _ => a with { HeadingRadians = value }
        };
        Refused(Resolve(new[] { invalid, Motor(2, 1, "B") }));
    }

    [Theory]
    [InlineData(double.NaN, 5, Heading)]
    [InlineData(-5, double.PositiveInfinity, Heading)]
    [InlineData(5, -5, Heading)]
    [InlineData(2, 2, Heading)]
    [InlineData(-5, 5, double.NaN)]
    public void InvalidQuery_DoesNotPublishResolvedTracks(double from, double to, double heading)
    {
        Refused(Resolve(new[] { Motor(-2, 1, "A"), Motor(2, 1, "B") }, from, to, heading));
    }
}
