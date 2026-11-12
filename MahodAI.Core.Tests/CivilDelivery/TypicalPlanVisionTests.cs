using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.TypicalPlanVision;
using P2 = MahodAI.CivilDelivery.Shared.SectionProjectionLogic.P2;
using K = MahodAI.CivilDelivery.Shared.DraftedTypicalSectionLogic.Kind;

namespace MahodAI.Core.Tests.CivilDelivery;

public sealed class TypicalPlanVisionTests
{
    private static List<Stretch> Scan(TypicalPlanVision image, int samples = 36) =>
        Select(Enumerable.Range(0, samples).Select(i =>
        {
            var station = 2.5 + 5 * i;
            return new Sample(station, image.Read(new P2(station, 0), 0, 20));
        }).ToList());

    private static void Line(TypicalPlanVision image, string kind, double offset, double from = 0, double to = 180, int stage = 1)
        => image.Add(new P2(from, -offset), new P2(to, -offset), kind, stage);

    [Fact]
    public void ExtraLaneThenReturnSelectsInsideEachDistinctStretch()
    {
        var image = new TypicalPlanVision();
        Line(image, K.Edge, -3.5);
        Line(image, K.Edge, 3.5);
        Line(image, K.Edge, 7, 60, 120);
        Line(image, K.Marking, 0);
        var sections = Scan(image);
        Assert.Equal(3, sections.Count);
        Assert.InRange(sections[0].Representative.Station, 25, 35);
        Assert.InRange(sections[1].Representative.Station, 85, 95);
        Assert.InRange(sections[2].Representative.Station, 145, 155);
    }

    [Fact]
    public void DashedPaintDuplicateLineworkAndPerpendicularIntersectionGiveOneSection()
    {
        var image = new TypicalPlanVision();
        Line(image, K.Edge, -3.5);
        Line(image, K.Edge, -3.48);
        Line(image, K.Edge, 3.5);
        for (int x = 0; x < 180; x += 9) Line(image, K.Marking, 0, x, x + 3);
        image.Add(new P2(90, -19), new P2(90, 19), K.Marking, 1);
        var chosen = Assert.Single(Scan(image));
        Assert.Equal(3, chosen.Representative.Features.Count);
    }

    [Fact]
    public void UnreadableGapSeparatesIdenticalSectionsAndBareAxisAbstains()
    {
        var image = new TypicalPlanVision();
        Assert.Empty(Scan(new TypicalPlanVision()));
        Line(image, K.Edge, -4);
        Line(image, K.Edge, 4, 0, 65);
        Line(image, K.Edge, 4, 115);
        var sections = Scan(image);
        Assert.Equal(2, sections.Count);
        Assert.True(sections[0].To < 65 && sections[1].From > 115);
    }

    [Fact]
    public void DoubleTrackAndStationPlatformProduceDifferentRailSections()
    {
        var image = new TypicalPlanVision();
        Line(image, K.Track, 0);
        Line(image, K.Track, 4.75, 60);
        Line(image, K.Platform, 7, 120);
        Line(image, K.Platform, 13, 120);
        var cuts = Scan(image);
        Assert.Equal(new[] { 1, 2, 4 }, cuts.Select(c => c.Representative.Features.Count));
        var section = DraftedTypicalSectionLogic.Build(cuts.Last().Representative.Features.Select(f =>
            new DraftedTypicalSectionLogic.Crossing(f.Offset, f.Kind, "", "host")), null, 20);
        Assert.Contains(section.Strips, s => s.Name == "רציף");
        Assert.DoesNotContain(section.Strips, s => s.Name == "נתיב");
    }

    [Fact]
    public void WidthChangeBeyondJitterMakesASectionEvenWithSameNumberOfLines()
    {
        var image = new TypicalPlanVision();
        Line(image, K.Edge, -4);
        Line(image, K.Edge, 4, 0, 60);
        Line(image, K.Edge, 4.2, 60, 120);
        Line(image, K.Edge, 6, 120);
        Assert.Equal(2, Scan(image).Count);
    }

    [Theory]
    [InlineData(true, 3.5, true)]
    [InlineData(false, 3.5, false)]
    [InlineData(true, 6.0, false)]
    public void UnlabelledPaintRequiresBothBrokenInkAndLaneWidthBounds(bool broken, double width, bool expected)
    {
        var image = new TypicalPlanVision();
        Line(image, K.Edge, -width);
        Line(image, K.Edge, width);
        if (broken)
            for (int x = 0; x < 180; x += 6) Line(image, UnclassifiedStroke, 0, x, x + 3);
        else Line(image, UnclassifiedStroke, 0);
        var strokes = image.Read(new P2(90, 0), 0, 20);
        Assert.Equal(expected, strokes.Any(f => f.Kind == InferredLaneMark));
    }

    [Fact]
    public void ExistingAndProposedSourcesCannotCorroborateEachOther()
    {
        var image = new TypicalPlanVision();
        Line(image, K.Edge, -4, stage: 1);
        Line(image, K.Edge, 4, stage: 0);
        Assert.Empty(Scan(image));
    }

    [Fact]
    public void ModelBoundsKeepInLaneMarkingChangesAndIgnoreTheAdjacentCarriageway()
    {
        var model = new[] { new Feature(-1, "TOP", -4), new Feature(-1, "TOP", 4) };
        var samples = Enumerable.Range(0, 18).Select(i =>
        {
            var markings = new List<Feature>();
            if (i >= 4) markings.Add(new(1, K.Marking, 8));
            if (i >= 9) markings.Add(new(1, K.HovLine, 2));
            return new Sample(i * 5, CombineWithModel(markings, model), "one assembly");
        }).ToList();
        Assert.Equal(2, Select(samples, modelRegion: true).Count);
    }

    [Fact]
    public void NearCoincidentComputedStationsDoNotProveANewRoadConfiguration()
    {
        Sample At(double s, int lanes) => new(s, Enumerable.Range(0, lanes)
            .Select(i => new Feature(-1, "ETW", 3.5 * i)).ToList(), "assembly");
        var close = new[] { At(100, 1), At(100.04, 1) };
        Assert.Single(Select(close, modelRegion: true)); // entire region
        var runs = Select(close.Concat(new[] { At(105, 2), At(110, 2), At(115, 2) }).ToList(), modelRegion: true);
        Assert.Equal(110, Assert.Single(runs).Representative.Station);
    }

    [Fact]
    public void AlternatingEvidenceIsNotAStableRoadLayout()
    {
        var samples = Enumerable.Range(0, 20).Select(i => new Sample(i * 5,
            new[] { new Feature(1, K.Edge, -4), new Feature(1, K.Edge, i % 2 == 0 ? 4 : 8) })).ToList();
        Assert.Empty(Select(samples));
    }

    [Fact]
    public void AContinuousTaperIsRepresentedByTheStableSectionsAtItsEnds()
    {
        var image = new TypicalPlanVision();
        Line(image, K.Edge, -4);
        Line(image, K.Edge, 4, 0, 60);
        image.Add(new P2(60, -4), new P2(120, -10), K.Edge, 1);
        Line(image, K.Edge, 10, 120, 180);
        var sections = Scan(image);
        Assert.Equal(2, sections.Count);
        Assert.True(sections[0].Representative.Station < 60);
        Assert.True(sections[1].Representative.Station > 120);
    }

    [Fact]
    public void RotationStationOriginAndWorldCoordinatesPreserveTheCut()
    {
        var image = new TypicalPlanVision();
        var axis = new P2(194200, 681777);
        foreach (var offset in new[] { -4, 0, 4 })
            image.Add(new P2(axis.X + offset, axis.Y + 50), new P2(axis.X + offset, axis.Y - 50), K.Edge, 1);
        Assert.Equal(new double[] { -4, 0, 4 }, image.Read(axis, 90, 20).Select(f => f.Offset));
        var s = Assert.Single(Select(new[] { 90302.0, 90307, 90312 }.Select(st =>
            new Sample(st, image.Read(axis, 90, 20))).ToList()));
        Assert.Equal(90307, s.Representative.Station);
    }

    [Fact]
    public void ShortModelRegionUsesAnActualComputedStationButOneVisualHitDoesNotSuffice()
    {
        var single = new[] { new Sample(104.75, Array.Empty<Feature>(), "assembly") };
        Assert.Equal(104.75, Assert.Single(Select(single, modelRegion: true)).Representative.Station);
        Assert.Empty(Select(single));
    }

    [Theory]
    [InlineData("CL|RR-ALGN", K.Track)]
    [InlineData("RR-RAIL-LEFT", K.Rail)]
    [InlineData("RR-PLTF-DES", K.Platform)]
    [InlineData("RR-SWITCH-TXT", null)]
    [InlineData("RR-PLTF-DIM", null)]
    [InlineData("GUARDRAIL", K.Barrier)]
    public void RailAndAnnotationLayersAreDistinct(string layer, string? kind) =>
        Assert.Equal(kind, DraftedTypicalSectionLogic.ClassifyLayer(layer));
}
