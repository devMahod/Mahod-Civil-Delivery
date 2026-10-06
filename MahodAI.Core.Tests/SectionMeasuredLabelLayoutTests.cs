using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionAnnotationPlacementLogic;
using Point = MahodAI.CivilDelivery.Shared.SectionAnnotationPlacementLogic.Point;

namespace MahodAI.Core.Tests;

public sealed class SectionMeasuredLabelLayoutTests
{
    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(0.25, 0.0)]
    [InlineData(3.0, 10000000.0)]
    public void Real31NativeBoxes_AllElevenOriginalConflictsClearWithoutChangingInkOrAnchors(double scale, double translate)
    {
        var (labels, obstacles) = ReadFixture(scale, translate);
        labels.Should().HaveCount(26);
        obstacles.Should().HaveCount(5);
        var original = labels.Select(l => l.Ink).Concat(obstacles).ToList();
        CountConflicts(original).Should().Be(11, "the fixture must reproduce the observed failure before placing anything");

        TryLayoutLabels(labels, obstacles, 0.24 * scale, 48 * scale, out var layout, out var error)
            .Should().BeTrue(error);
        var final = layout!.Labels.Select(l => l.Ink).Concat(obstacles).ToList();
        CountConflicts(final).Should().Be(0);
        foreach (var placed in layout.Labels)
        {
            var input = labels.Single(l => l.Id == placed.Id);
            placed.Anchor.X.Should().Be(input.Anchor.X);
            placed.Ink.Width.Should().BeApproximately(input.Ink.Width, 1e-7);
            placed.Ink.Height.Should().BeApproximately(input.Ink.Height, 1e-7);
            placed.Ink.MinX.Should().Be(input.Ink.MinX);
            placed.Rise.Should().BeGreaterThanOrEqualTo(0);
        }
        layout.OverallBounds.Should().Be(Envelope(final));
    }

    [Fact]
    public void RealFixture_IsIndependentOfEnumerationOrder()
    {
        var (labels, obstacles) = ReadFixture(1, 0);
        TryLayoutLabels(labels, obstacles, .24, 48, out var first, out var firstError).Should().BeTrue(firstError);
        TryLayoutLabels(labels.AsEnumerable().Reverse().ToList(), obstacles.AsEnumerable().Reverse().ToList(),
            .24, 48, out var second, out var secondError).Should().BeTrue(secondError);
        second.Should().BeEquivalentTo(first, options => options.WithStrictOrdering());
    }

    [Fact]
    public void LongHebrewAndNarrow132And201Strips_KeepLegibleMeasuredSizeAndDistinctTiers()
    {
        var labels = new[]
        {
            new LabelBox("1.32", 1, new Point(.66, 1), new Bounds(-.35, 1, 1.67, 1.75)),
            new LabelBox("2.01", 1, new Point(2.325, 1), new Bounds(1.30, 1, 3.35, 1.75)),
            new LabelBox("שביל אופניים ארוך", 2, new Point(.66, 3), new Bounds(-3.84, 3, 5.16, 3.8)),
            new LabelBox("מדרכה", 2, new Point(2.325, 3), new Bounds(.7, 3, 3.95, 3.8)),
        };
        TryLayoutLabels(labels, Array.Empty<Bounds>(), .24, 20, out var result, out var error).Should().BeTrue(error);
        CountConflicts(result!.Labels.Select(l => l.Ink).ToList()).Should().Be(0);
        result.Labels.Single(l => l.Id == "שביל אופניים ארוך").Ink.Width.Should().Be(9);
        result.Labels.Where(l => l.Id is "1.32" or "2.01").Max(l => l.Ink.MaxY)
            .Should().BeLessThan(result.Labels.Where(l => l.Id is "שביל אופניים ארוך" or "מדרכה").Min(l => l.Ink.MinY));
    }

    [Fact]
    public void OpposingVehicleBoxesStayFixed_AdjacentSlopesMoveAndLeadersDoNotStrikeThroughInk()
    {
        var (labels, obstacles) = ReadFixture(1, 0);
        var unchanged = obstacles.ToArray();
        TryLayoutLabels(labels, obstacles, .24, 48, out var result, out var error).Should().BeTrue(error);
        obstacles.Should().Equal(unchanged);
        var boxes = result!.Labels.Select(l => l.Ink).Concat(obstacles).ToList();
        var movedSlopes = result.Labels.Where(l => labels.Single(x => x.Id == l.Id).Band == 0 && l.Rise > .24).ToList();
        movedSlopes.Count.Should().BeGreaterThanOrEqualTo(2, "both recorded slope/car intersections must be addressed");
        foreach (var label in result.Labels.Where(l => l.Rise > .24))
        {
            var original = labels.Single(l => l.Id == label.Id);
            foreach (var stem in ClearVerticalLeader(original.Anchor, label.Ink.MinY - .24, boxes, .24))
            {
                stem.Start.X.Should().Be(original.Anchor.X);
                stem.End.X.Should().Be(original.Anchor.X);
                boxes.Should().NotContain(b => stem.Start.X > b.MinX && stem.Start.X < b.MaxX &&
                    stem.Start.Y < b.MaxY && stem.End.Y > b.MinY);
            }
        }
    }

    [Fact]
    public void FixedUtilityLabelsAndDatumAreObstacles_NotRenamedResizedOrMoved()
    {
        var fixedInk = new[] { new Bounds(0, 0, 2, 1), new Bounds(2.1, 0, 4, 1), new Bounds(-4, -2, -1, -1) };
        var labels = new[] { new LabelBox("slope", 0, new Point(1, .5), new Bounds(0, .5, 2, 1.1)) };
        TryLayoutLabels(labels, fixedInk, .24, 10, out var result, out var error).Should().BeTrue(error);
        result!.Labels[0].Ink.MinY.Should().BeGreaterThan(1.23);
        result.OverallBounds!.MinY.Should().Be(-2);
        fixedInk[2].Should().Be(new Bounds(-4, -2, -1, -1));
    }

    [Fact]
    public void ImpossibleHeightBudgetAndUnknownInkFailInsteadOfShrinkingOrClaimingSuccess()
    {
        var label = new LabelBox("width", 0, new Point(0, 0), new Bounds(-1, 0, 1, 1));
        TryLayoutLabels(new[] { label }, new[] { new Bounds(-2, 0, 2, 50) }, .24, 10,
            out var result, out _).Should().BeFalse();
        result.Should().BeNull();
        TryLayoutLabels(new[] { label with { Ink = new Bounds(0, 0, double.NaN, 1) } },
            Array.Empty<Bounds>(), .24, 10, out _, out _).Should().BeFalse();
        TryLayoutLabels(new[] { label, label }, Array.Empty<Bounds>(), .24, 10, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void RemoteTallVerticalRowLabelDoesNotPushUnrelatedCenterTitleIntoEmptySpace()
    {
        var row = new Bounds(-15, 2, -14, 30);
        var labels = new[]
        {
            new LabelBox("name", 2, new Point(0, 3), new Bounds(-2, 3, 2, 4)),
            new LabelBox("axis", 4, new Point(0, 5), new Bounds(-.5, 5, .5, 6)),
            new LabelBox("title", 5, new Point(0, 7), new Bounds(-4, 7, 4, 8)),
        };
        TryLayoutLabels(labels, new[] { row }, .24, 48, out var result, out var error).Should().BeTrue(error);
        result!.Labels.Should().OnlyContain(label => label.Rise == 0);
        result.OverallBounds!.MaxY.Should().Be(30, "the fixed ROW ink is still included for zoom/packing");
    }

    private static int CountConflicts(IReadOnlyList<Bounds> boxes) => boxes.SelectMany((box, i) =>
        boxes.Skip(i + 1).Select(other => BoxesConflict(box, other))).Count(conflict => conflict);

    [Fact]
    public void BottomDatumAndCloseRotatedOffsets_MoveDownWithoutChangingXInkOrValue()
    {
        // Synthetic adversarial geometry, not part of the 31-entity native fixture.
        var labels = new[]
        {
            new LabelBox("offset:-14.22", 0, new Point(.35, -1.1), new Bounds(.35, -3.4, 1.0, -1.1)),
            new LabelBox("offset:-11.09", 0, new Point(.8, -1.1), new Bounds(.8, -3.4, 1.45, -1.1)),
            new LabelBox("EG:234.75", 1, new Point(0, -1.6), new Bounds(0, -1.6, 8, -.9)),
        };
        var utilities = new[] { new Bounds(1.1, -4.6, 1.7, -1), new Bounds(2.1, -5, 2.7, -1) };
        CountConflicts(labels.Select(l => l.Ink).Concat(utilities).ToList()).Should().BeGreaterThan(0);
        TryLayoutLabelsDown(labels, utilities, .24, 48, out var result, out var error).Should().BeTrue(error);
        foreach (var placed in result!.Labels)
        {
            var original = labels.Single(l => l.Id == placed.Id);
            placed.Anchor.X.Should().Be(original.Anchor.X);
            placed.Anchor.Y.Should().BeLessThanOrEqualTo(original.Anchor.Y);
            placed.Ink.Width.Should().BeApproximately(original.Ink.Width, 1e-10);
            placed.Ink.Height.Should().BeApproximately(original.Ink.Height, 1e-10);
        }
        var ink = result.Labels.Select(l => new InkBox(l.Id, l.Ink, true)).Concat(
            utilities.Select((b, i) => new InkBox("utility" + i, b, true))).ToList();
        FindTextInkConflicts(ink).Should().BeEmpty();
        result.Labels.Single(l => l.Id == "EG:234.75").Ink.MaxY.Should().BeLessThan(
            result.Labels.Where(l => l.Id.StartsWith("offset", StringComparison.Ordinal)).Min(l => l.Ink.MinY));
        var allBoxes = ink.Select(i => i.Ink).ToList();
        foreach (var placed in result.Labels.Where(l => l.Rise < -.24))
        foreach (var stem in ClearVerticalLeader(MirrorY(labels.Single(l => l.Id == placed.Id).Anchor),
                     -placed.Ink.MaxY - .24, allBoxes.Select(MirrorY).ToList(), .24))
            allBoxes.Select(MirrorY).Should().NotContain(b => stem.Start.X > b.MinX && stem.Start.X < b.MaxX &&
                stem.Start.Y < b.MaxY && stem.End.Y > b.MinY);
    }

    [Fact]
    public void AllTextGateCatchesFixedUtilityPairsAndTextFurnitureButNotIntentionalFurnitureComponents()
    {
        var overlaps = FindTextInkConflicts(new[]
        {
            new InkBox("fixed-utility-A", new Bounds(0, 0, 1, 4), true),
            new InkBox("fixed-utility-B", new Bounds(.5, 1, 1.5, 5), true),
            new InkBox("car", new Bounds(10, 0, 12, 2), false),
            new InkBox("wheel", new Bounds(10.2, -.1, 10.8, .5), false),
            new InkBox("slope", new Bounds(11, 1, 13, 1.6), true),
        });
        overlaps.Should().HaveCount(2);
        overlaps.Should().Contain(new InkConflict("fixed-utility-A", "fixed-utility-B"));
        overlaps.Should().Contain(new InkConflict("car", "slope"));
        overlaps.Should().NotContain(c => c.First == "car" && c.Second == "wheel");
    }

    // Codex 03.10 14:40 (STA-42676 at 1:200): a slope label in a narrow strip overflowed into a second row and was
    // struck by the dimension ticks and the frame top; the axis struck the minus sign of the next label.
    private const double FrameTop = 10, Clear = .186;
    private static readonly Segment[] Ticks =
        new[] { 0.0, 3, 4, 7 }.Select(x => new Segment(new Point(x, 8), new Point(x, FrameTop))).ToArray();
    private static readonly Bounds Car = new(.5, 6.8, 6.5, 8.2);
    private static LabelBox Slope(string id, double center) =>
        new(id, 0, new Point(center, 7), new Bounds(center - .85, 7, center + .85, 7.62));
    private static readonly LabelBox Width = new("2.88", 1, new Point(1.5, 11.3), new Bounds(1, 11.3, 2, 11.9));

    [Fact]
    public void NoLineCrossing_IsExactlyThePlainSolver()
    {
        var (labels, obstacles) = ReadFixture(1, 0);
        TryLayoutLabels(labels, obstacles, .24, 48, out var plain, out var plainError).Should().BeTrue(plainError);
        var far = new[] { new Segment(new Point(1e6, 0), new Point(1e6, 1)) };
        TryLayoutLabelsClearOfLines(labels, obstacles, far, 1e6, .24, 48, out var clear, out var raised, out var error)
            .Should().BeTrue(error);
        raised.Should().BeEmpty();
        clear.Should().BeEquivalentTo(plain, options => options.WithStrictOrdering());
    }

    [Fact]
    public void NarrowStripSlopeStruckByTicks_RisesAboveTheFrame_OthersKeepTheirPlace()
    {
        var labels = new[] { Slope("-3.33%", 1.5), Slope("-5.15%", 3.5), Slope("-3.50%", 5.5), Width };
        TryLayoutLabels(labels, new[] { Car }, Clear, 40, out var plain, out var plainError).Should().BeTrue(plainError);
        var plainNarrow = plain!.Labels.Single(l => l.Id == "-5.15%");
        Ticks.Should().Contain(t => Strikes(t, plainNarrow.Ink), "the fixture reproduces the printed defect first");

        TryLayoutLabelsClearOfLines(labels, new[] { Car }, Ticks, FrameTop, Clear, 40,
            out var clear, out var raised, out var error).Should().BeTrue(error);
        raised.Should().Equal("-5.15%");
        var narrow = clear!.Labels.Single(l => l.Id == "-5.15%");
        narrow.Ink.MinY.Should().BeGreaterThanOrEqualTo(FrameTop + Clear);
        narrow.Anchor.X.Should().Be(3.5);
        narrow.Ink.Width.Should().BeApproximately(1.7, 1e-9);
        foreach (var id in new[] { "-3.33%", "-3.50%", "2.88" })
            clear.Labels.Single(l => l.Id == id).Should().Be(plain.Labels.Single(l => l.Id == id));
        clear.Labels.Where(l => l.Id.EndsWith('%')).Should().OnlyContain(l => !Ticks.Any(t => Strikes(t, l.Ink)) &&
            !(l.Ink.MinY < FrameTop && l.Ink.MaxY > FrameTop));
        CountConflicts(clear.Labels.Select(l => l.Ink).Append(Car).ToList()).Should().Be(0);
    }

    [Fact]
    public void AxisThroughALabel_RaisesOnlyThatLabel()
    {
        var labels = new[] { Slope("-3.50%", 1.5), Slope("-3.55%", 5.5), Width };
        var axis = new Segment(new Point(1, 0), new Point(1, FrameTop));
        TryLayoutLabels(labels, new[] { Car }, Clear, 40, out var plain, out var plainError).Should().BeTrue(plainError);
        Strikes(axis, plain!.Labels.Single(l => l.Id == "-3.50%").Ink).Should().BeTrue();

        TryLayoutLabelsClearOfLines(labels, new[] { Car }, new[] { axis }, FrameTop, Clear, 40,
            out var clear, out var raised, out var error).Should().BeTrue(error);
        raised.Should().Equal("-3.50%");
        Strikes(axis, clear!.Labels.Single(l => l.Id == "-3.50%").Ink).Should().BeFalse();
        clear.Labels.Single(l => l.Id == "-3.55%").Should().Be(plain.Labels.Single(l => l.Id == "-3.55%"));
        CountConflicts(clear.Labels.Select(l => l.Ink).Append(Car).ToList()).Should().Be(0);
    }

    [Fact]
    public void LineClearLayout_IsIndependentOfOrderAndRefusesNonFiniteLines()
    {
        var labels = new[] { Slope("-3.33%", 1.5), Slope("-5.15%", 3.5), Slope("-3.50%", 5.5), Width };
        TryLayoutLabelsClearOfLines(labels, new[] { Car }, Ticks, FrameTop, Clear, 40,
            out var first, out _, out var firstError).Should().BeTrue(firstError);
        TryLayoutLabelsClearOfLines(labels.Reverse().ToList(), new[] { Car }, Ticks.Reverse().ToList(), FrameTop, Clear, 40,
            out var second, out _, out var secondError).Should().BeTrue(secondError);
        second!.Labels.OrderBy(l => l.Id, StringComparer.Ordinal).Should()
            .Equal(first!.Labels.OrderBy(l => l.Id, StringComparer.Ordinal));
        TryLayoutLabelsClearOfLines(labels, new[] { Car },
            new[] { new Segment(new Point(double.NaN, 0), new Point(1, 1)) }, FrameTop, Clear, 40,
            out var refused, out _, out _).Should().BeFalse();
        refused.Should().BeNull();
    }

    // b32 (Arthur 04.10 on the STA-42676 print): two strip names 0.3 text heights apart passed the ink gate but read as
    // one phrase. The word gap splits them onto separate rows; labels that already stand a word apart keep one row.
    [Fact]
    public void WordGap_SplitsNeighboursThatOnlyClearTheClearance_AndKeepsWideSpacedLabelsOnOneRow()
    {
        const double h = .62, clearance = .45 * h, gap = 1.5 * h;
        LabelBox Name(string id, double minX, double width) =>
            new(id, 2, new Point(minX + width / 2, 10), new Bounds(minX, 10, minX + width, 10 + h));
        var tight = new[] { Name("נתיב נסיעה א", 0, 3), Name("אי תנועה", 3.3, 2), Name("נתיב נסיעה ב", 5.6, 3) };
        TryLayoutLabels(tight, Array.Empty<Bounds>(), clearance, 60 * h, out var plain, out var plainError)
            .Should().BeTrue(plainError);
        plain!.Labels.Select(l => l.Rise).Distinct().Should().ContainSingle("0.3 m > clearance: the plain solver keeps one row");

        TryLayoutLabels(tight, Array.Empty<Bounds>(), clearance, 60 * h, out var spaced, out var error, gap)
            .Should().BeTrue(error);
        spaced!.Labels.Select(l => l.Rise).Distinct().Should().HaveCount(2, "the island name moves to its own row");
        SameRowPairsCloserThan(spaced.Labels, gap).Should().BeEmpty();
        foreach (var placed in spaced.Labels)
        {
            var input = tight.Single(l => l.Id == placed.Id);
            placed.Anchor.X.Should().Be(input.Anchor.X);
            placed.Ink.Width.Should().BeApproximately(input.Ink.Width, 1e-9);
            placed.Rise.Should().BeGreaterThanOrEqualTo(0);
        }

        var wide = new[] { Name("מדרכה", 0, 2), Name("נתיב נסיעה", 2 + gap + .01, 3) };
        TryLayoutLabels(wide, Array.Empty<Bounds>(), clearance, 60 * h, out var oneRow, out var wideError, gap)
            .Should().BeTrue(wideError);
        oneRow!.Labels.Select(l => l.Rise).Distinct().Should().ContainSingle();
    }

    [Fact]
    public void WordGapNullOrBelowClearance_IsExactlyThePlainSolver_AndNonFiniteGapFails()
    {
        var (labels, obstacles) = ReadFixture(1, 0);
        TryLayoutLabels(labels, obstacles, .24, 48, out var plain, out var plainError).Should().BeTrue(plainError);
        TryLayoutLabels(labels, obstacles, .24, 48, out var none, out var noneError, null).Should().BeTrue(noneError);
        TryLayoutLabels(labels, obstacles, .24, 48, out var small, out var smallError, .1).Should().BeTrue(smallError);
        none.Should().BeEquivalentTo(plain, options => options.WithStrictOrdering());
        small.Should().BeEquivalentTo(plain, options => options.WithStrictOrdering());
        TryLayoutLabels(labels, obstacles, .24, 48, out var bad, out var badError, double.NaN).Should().BeFalse();
        bad.Should().BeNull();
        badError.Should().Contain("finite horizontal label gap");
    }

    [Fact]
    public void Native42676Capture_WithWordGap_NoTwoLabelsOnOneRowCloserThanTheGap()
    {
        var (labels, obstacles, h) = Read42676Capture();
        TryLayoutLabels(labels, obstacles, .45 * h, 60 * h, out var layout, out var error, 1.5 * h).Should().BeTrue(error);
        CountConflicts(layout!.Labels.Select(l => l.Ink).Concat(obstacles).ToList()).Should().Be(0);
        SameRowPairsCloserThan(layout.Labels, 1.5 * h).Should().BeEmpty();
        layout.Labels.Should().HaveCount(labels.Count);
        foreach (var placed in layout.Labels)
            placed.Anchor.X.Should().Be(labels.Single(l => l.Id == placed.Id).Anchor.X);
    }

    // Codex 04.10 03:45 on the b32 print: strip names, overall width and axis read as one block. One text height between
    // those groups only; every other band keeps its rows, X and ink, and nothing overlaps.
    [Fact]
    public void Native42676Capture_HeaderGroupGaps_OneTextHeightBetweenNamesWidthAndAxisOnly()
    {
        var (labels, obstacles, h) = Read42676Capture();
        var gaps = new Dictionary<int, double> { [3] = h, [4] = h };
        TryLayoutLabels(labels, obstacles, .45 * h, 60 * h, out var plain, out var plainError, 1.5 * h).Should().BeTrue(plainError);
        TryLayoutLabels(labels, obstacles, .45 * h, 60 * h, out var spaced, out var error, 1.5 * h, gaps).Should().BeTrue(error);
        CountConflicts(spaced!.Labels.Select(l => l.Ink).Concat(obstacles).ToList()).Should().Be(0);
        SameRowPairsCloserThan(spaced.Labels, 1.5 * h).Should().BeEmpty();
        double Top(LabelLayout l, int band) => l.Labels.Where(p => Band(p.Id) == band).Max(p => p.Ink.MaxY);
        double Bottom(LabelLayout l, int band) => l.Labels.Where(p => Band(p.Id) == band).Min(p => p.Ink.MinY);
        int Band(string id) => labels.Single(l => l.Id == id).Band;
        (Bottom(spaced, 3) - Top(spaced, 2)).Should().BeGreaterThanOrEqualTo(h);
        (Bottom(spaced, 4) - Top(spaced, 3)).Should().BeGreaterThanOrEqualTo(h);
        (Bottom(plain!, 3) - Top(plain!, 2)).Should().BeLessThan(h, "the fixture reproduces the cramped header first");
        foreach (var placed in spaced.Labels.Where(p => Band(p.Id) <= 2))
            placed.Ink.Should().Be(plain.Labels.Single(p => p.Id == placed.Id).Ink, "bands below the gaps do not move");
        foreach (var placed in spaced.Labels)
        {
            var input = labels.Single(l => l.Id == placed.Id);
            placed.Anchor.X.Should().Be(input.Anchor.X);
            placed.Ink.Width.Should().BeApproximately(input.Ink.Width, 1e-9);
            placed.Ink.Height.Should().BeApproximately(input.Ink.Height, 1e-9);
        }
        TryLayoutLabels(labels, obstacles, .45 * h, 60 * h, out _, out var bad, 1.5 * h,
            new Dictionary<int, double> { [3] = double.NaN }).Should().BeFalse();
        bad.Should().Contain("finite band gaps");
    }

    // Codex 02:47 replay: the word gap is for the labels above the section; the rotated offsets below keep 0.30h and
    // must land exactly where the native b31 VERIFY placed them.
    [Fact]
    public void Native42676Capture_BottomOffsetsAt030_KeepEveryNativePosition()
    {
        var (labels, obstacles, h) = Read42676Capture();
        var (bottom, fixedInk, native) = Read42676Bottom();
        TryLayoutLabels(labels, obstacles, .45 * h, 60 * h, out var top, out var topError, 1.5 * h,
            new Dictionary<int, double> { [3] = h, [4] = h }).Should().BeTrue(topError);
        TryLayoutLabelsDown(bottom, fixedInk.Concat(top!.Labels.Select(l => l.Ink)).ToList(), .30 * h, 60 * h,
            out var down, out var error).Should().BeTrue(error);
        down!.Labels.Should().HaveCount(24);
        foreach (var placed in down.Labels)
        {
            var expected = native[placed.Id];
            placed.Ink.MinX.Should().BeApproximately(expected.MinX, 1e-9);
            placed.Ink.MinY.Should().BeApproximately(expected.MinY, 1e-9);
            placed.Ink.MaxX.Should().BeApproximately(expected.MaxX, 1e-9);
            placed.Ink.MaxY.Should().BeApproximately(expected.MaxY, 1e-9);
        }
    }

    private static (List<LabelBox> Bottom, List<Bounds> FixedInk, Dictionary<string, Bounds> Native) Read42676Bottom(
        [CallerFilePath] string source = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, ".."));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,
            "fixtures", "section-layout-0410", "sta-42676-b31-canonical-layout-inputs.json")));
        var record = document.RootElement.GetProperty("record");
        var inputs = record.GetProperty("inputs");
        static Bounds Box(JsonElement e) => new(e.GetProperty("min_x").GetDouble(), e.GetProperty("min_y").GetDouble(),
            e.GetProperty("max_x").GetDouble(), e.GetProperty("max_y").GetDouble());
        var bottom = inputs.GetProperty("bottom_labels").EnumerateArray().Select(e => new LabelBox(
            e.GetProperty("id").GetString()!, e.GetProperty("band").GetInt32(),
            new Point(e.GetProperty("anchor").GetProperty("x").GetDouble(), e.GetProperty("anchor").GetProperty("y").GetDouble()),
            Box(e.GetProperty("ink")))).ToList();
        var ids = bottom.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var native = record.GetProperty("layout_output").GetProperty("placed_labels").EnumerateArray()
            .Where(e => ids.Contains(e.GetProperty("handle").GetString()!))
            .ToDictionary(e => e.GetProperty("handle").GetString()!, e => Box(e.GetProperty("final_bounds")), StringComparer.Ordinal);
        return (bottom, inputs.GetProperty("fixed_obstacles").EnumerateArray().Select(Box).ToList(), native);
    }

    private static List<(string, string)> SameRowPairsCloserThan(IReadOnlyList<PlacedLabel> placed, double gap) =>
        (from a in placed
         from b in placed
         where string.CompareOrdinal(a.Id, b.Id) < 0 && SameRowTooClose(a.Ink, b.Ink, gap - 1e-9)
         select (a.Id, b.Id)).ToList();

    private static (List<LabelBox> Labels, List<Bounds> Obstacles, double TextHeight) Read42676Capture(
        [CallerFilePath] string source = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, ".."));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,
            "fixtures", "section-layout-0410", "sta-42676-b31-canonical-layout-inputs.json")));
        var inputs = document.RootElement.GetProperty("record").GetProperty("inputs");
        static Bounds Box(JsonElement e) => new(e.GetProperty("min_x").GetDouble(), e.GetProperty("min_y").GetDouble(),
            e.GetProperty("max_x").GetDouble(), e.GetProperty("max_y").GetDouble());
        var labels = inputs.GetProperty("top_labels").EnumerateArray().Select(e => new LabelBox(
            e.GetProperty("id").GetString()!, e.GetProperty("band").GetInt32(),
            new Point(e.GetProperty("anchor").GetProperty("x").GetDouble(), e.GetProperty("anchor").GetProperty("y").GetDouble()),
            Box(e.GetProperty("ink")))).ToList();
        // The top solver treats the fixed obstacles and the (not yet moved) bottom labels as occupied ink.
        var obstacles = inputs.GetProperty("fixed_obstacles").EnumerateArray().Select(Box)
            .Concat(inputs.GetProperty("bottom_labels").EnumerateArray().Select(e => Box(e.GetProperty("ink")))).ToList();
        return (labels, obstacles, inputs.GetProperty("median_text_height").GetDouble());
    }

    [Fact]
    public void Strikes_CountsOnlyInteriorCrossings()
    {
        var box = new Bounds(0, 0, 2, 1);
        Strikes(new Segment(new Point(1, -1), new Point(1, 2)), box).Should().BeTrue();
        Strikes(new Segment(new Point(0, -1), new Point(0, 2)), box).Should().BeFalse("touching an edge is not a strike");
        Strikes(new Segment(new Point(1, 1.5), new Point(1, 3)), box).Should().BeFalse();
        Strikes(new Segment(new Point(-1, .5), new Point(3, .5)), box).Should().BeTrue();
    }

    private static (List<LabelBox> Labels, List<Bounds> Obstacles) ReadFixture(double scale, double translate,
        [CallerFilePath] string source = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, ".."));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,
            "fixtures", "section-layout-070926", "native-31-boxes.json")));
        document.RootElement.GetProperty("originalOverlapCandidateCount").GetInt32().Should().Be(11);
        var labels = new List<LabelBox>();
        var obstacles = new List<Bounds>();
        foreach (var entity in document.RootElement.GetProperty("entities").EnumerateArray())
        {
            double Value(string name) => entity.GetProperty(name).GetDouble() * scale + translate;
            var bounds = new Bounds(Value("minX"), Value("minY"), Value("maxX"), Value("maxY"));
            if (entity.GetProperty("type").GetString() == "AcDbBlockReference") { obstacles.Add(bounds); continue; }
            var band = entity.GetProperty("kind").GetString() switch
            {
                "width-label" => 1, "strip-label" => 2, "axis-label" => 4, "title" => 5, _ => 0,
            };
            labels.Add(new LabelBox(entity.GetProperty("handle").GetString()!, band,
                new Point((bounds.MinX + bounds.MaxX) / 2, bounds.MinY), bounds));
        }
        return (labels, obstacles);
    }
}
