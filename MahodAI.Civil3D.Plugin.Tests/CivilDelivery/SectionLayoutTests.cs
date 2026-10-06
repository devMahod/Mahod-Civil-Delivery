using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Layout must produce a sheet an engineer can read: station order, stable across
    /// reruns, and never overlapping. The previous behaviour stacked every view
    /// downwards forever, which is why these exist.
    /// </summary>
    /// <summary>
    /// The sheet layout APPLY performs once the section views exist and their REAL
    /// extents are known. The first live batch (6422, 2026-08-19) placed 25 views on an
    /// 80 m grid while the deepest view was 335 m tall, so their elevation axes printed
    /// on top of each other. These tests pin the geometry, not the wording.
    /// </summary>
    public class SectionSheetLayoutTests
    {
        private static List<SectionLayoutPlanner.Box> Boxes(params (double W, double H)[] sizes) =>
            sizes.Select(s => new SectionLayoutPlanner.Box(s.W, s.H)).ToList();

        [Fact]
        public void MixedSizes_NeverOverlap()
        {
            // Deliberately nasty: one huge view among small ones, a prime count so the
            // last row is partial, and columns that do not divide the count.
            var boxes = Boxes(
                (100, 40), (106, 335), (60, 120), (90, 240), (100, 40),
                (131, 300), (40, 40), (100, 90), (80, 200), (100, 40), (55, 61));

            foreach (var columns in new[] { 1, 2, 3, 4, 5 })
            {
                var placed = SectionLayoutPlanner.ArrangeSheet(boxes, columns, 25, 30, 1000, 2000);
                placed.Should().HaveCount(boxes.Count);
                SectionLayoutPlanner.IsSheetOverlapFree(boxes, placed)
                    .Should().BeTrue($"columns={columns} must not overlap");
            }
        }

        [Fact]
        public void EachRowIsOnlyAsTallAsItsOwnTallestSection()
        {
            // Row 0 is shallow, row 1 holds the deep cut: row 0 must not inherit 335 m.
            var boxes = Boxes((100, 40), (100, 40), (100, 40), (100, 335), (100, 40), (100, 40));
            var placed = SectionLayoutPlanner.ArrangeSheet(boxes, 3, 25, 30, 0, 0);

            placed[0].MinY.Should().BeApproximately(-40, 1e-9, "row 0 top sits on the origin");
            // Row 1 starts one shallow row + margin below the origin, not one 335 m row.
            placed[3].MinY.Should().BeApproximately(-(40 + 30) - 335, 1e-9);
            SectionLayoutPlanner.IsSheetOverlapFree(boxes, placed).Should().BeTrue();
        }

        [Fact]
        public void ViewsAreCentredInTheirColumn_AndAlignedToTheRowTop()
        {
            var boxes = Boxes((100, 50), (40, 80), (100, 50));
            var placed = SectionLayoutPlanner.ArrangeSheet(boxes, 3, 20, 30, 0, 0);

            var cellW = 100 + 20;
            placed[0].MinX.Should().BeApproximately((cellW - 100) / 2.0, 1e-9);
            placed[1].MinX.Should().BeApproximately(cellW + (cellW - 40) / 2.0, 1e-9, "narrow views are centred, not left-stuck");
            placed[2].MinX.Should().BeApproximately(2 * cellW + (cellW - 100) / 2.0, 1e-9);

            // Tops aligned: MinY + Height == row top (0) for every box in the row.
            for (int i = 0; i < 3; i++)
                (placed[i].MinY + boxes[i].Height).Should().BeApproximately(0, 1e-9);
        }

        [Fact]
        public void GapsAreAtLeastTheRequestedMargins()
        {
            var boxes = Boxes((100, 40), (60, 40), (100, 90), (100, 40));
            var placed = SectionLayoutPlanner.ArrangeSheet(boxes, 2, 25, 30, 0, 0);

            // horizontal gap inside row 0
            var rightOf0 = placed[0].MinX + boxes[0].Width;
            (placed[1].MinX - rightOf0).Should().BeGreaterThanOrEqualTo(25 - 1e-9);
            // vertical gap between row 0 and row 1
            var bottomOfRow0 = placed.Take(2).Min(x => x.MinY);
            var topOfRow1 = placed.Skip(2).Max(x => x.MinY + boxes[x.Index].Height);
            (bottomOfRow0 - topOfRow1).Should().BeGreaterThanOrEqualTo(30 - 1e-9);
        }

        [Fact]
        public void EmptyAndSingle_AreHandled()
        {
            SectionLayoutPlanner.ArrangeSheet(new List<SectionLayoutPlanner.Box>(), 3, 25, 30, 0, 0)
                .Should().BeEmpty();

            var one = Boxes((100, 40));
            var placed = SectionLayoutPlanner.ArrangeSheet(one, 3, 25, 30, 500, 900);
            placed.Should().ContainSingle();
            placed[0].MinY.Should().BeApproximately(900 - 40, 1e-9);
        }

        [Fact]
        public void Deterministic_SameInputSamePlacement()
        {
            var boxes = Boxes((100, 40), (106, 335), (60, 120), (90, 240));
            var a = SectionLayoutPlanner.ArrangeSheet(boxes, 3, 25, 30, 1000, 2000);
            var b = SectionLayoutPlanner.ArrangeSheet(boxes, 3, 25, 30, 1000, 2000);
            a.Should().BeEquivalentTo(b, "a rerun must not shuffle the sheet");
        }
    }

    public class SectionLayoutPlannerTests
    {
        private static SectionLayoutPlanner.Options Opts(int columns = 3) => new()
        {
            Columns = columns,
            SpacingX = 100,
            SpacingY = 60,
            OriginX = 1000,
            OriginY = 2000,
        };

        private static List<SectionLayoutPlanner.Input> Inputs(params (string id, double? sta)[] items) =>
            items.Select(i => new SectionLayoutPlanner.Input(i.id, i.sta, null)).ToList();

        [Fact]
        public void PlacesInStationOrder_NotDiscoveryOrder()
        {
            var cells = SectionLayoutPlanner.Plan(
                Inputs(("c", 300), ("a", 100), ("b", 200)), Opts());

            cells.Select(c => c.RecordId).Should().ContainInOrder("a", "b", "c");
        }

        [Fact]
        public void WrapsIntoRowsAtColumnLimit()
        {
            var cells = SectionLayoutPlanner.Plan(
                Inputs(("a", 1), ("b", 2), ("c", 3), ("d", 4)), Opts(columns: 3));

            cells[0].Column.Should().Be(0); cells[0].Row.Should().Be(0);
            cells[2].Column.Should().Be(2); cells[2].Row.Should().Be(0);
            cells[3].Column.Should().Be(0); cells[3].Row.Should().Be(1);
        }

        [Fact]
        public void RowsAdvanceDownwardAndColumnsRightward()
        {
            var cells = SectionLayoutPlanner.Plan(
                Inputs(("a", 1), ("b", 2), ("c", 3), ("d", 4)), Opts(columns: 3));

            cells[1].X.Should().BeGreaterThan(cells[0].X, "columns read left to right");
            cells[3].Y.Should().BeLessThan(cells[0].Y, "rows read top to bottom");
        }

        [Fact]
        public void LayoutIsDeterministicAcrossRuns()
        {
            var input = Inputs(("a", 100), ("b", 200), ("c", 300));
            var first = SectionLayoutPlanner.Plan(input, Opts());
            var second = SectionLayoutPlanner.Plan(input, Opts());

            second.Should().BeEquivalentTo(first, "a rerun must not move existing views");
        }

        [Fact]
        public void RecordsWithoutStation_KeepStableTailOrder()
        {
            var cells = SectionLayoutPlanner.Plan(
                Inputs(("z", null), ("a", null), ("m", 50)), Opts());

            cells[0].RecordId.Should().Be("m", "stationed records come first");
            cells.Skip(1).Select(c => c.RecordId).Should().ContainInOrder("a", "z");
        }

        [Fact]
        public void FitToContent_GrowsSpacingForWideSections()
        {
            var fitted = SectionLayoutPlanner.FitToContent(
                viewWidths: new[] { 40.0, 180.0 },
                viewHeights: new[] { 30.0 },
                baseline: Opts());

            fitted.SpacingX.Should().BeGreaterThan(180, "a 180 m swath cannot sit in a 100 m step");
        }

        [Fact]
        public void FitToContent_KeepsBaselineWhenContentIsSmall()
        {
            var fitted = SectionLayoutPlanner.FitToContent(
                new[] { 10.0 }, new[] { 5.0 }, Opts());

            fitted.SpacingX.Should().Be(100);
            fitted.SpacingY.Should().Be(60);
        }

        [Fact]
        public void PlacedGrid_IsOverlapFree()
        {
            var input = Inputs(("a", 1), ("b", 2), ("c", 3), ("d", 4), ("e", 5), ("f", 6), ("g", 7));
            var options = SectionLayoutPlanner.FitToContent(
                Enumerable.Repeat(60.0, 7).ToList(), Enumerable.Repeat(40.0, 7).ToList(), Opts());

            var cells = SectionLayoutPlanner.Plan(input, options);

            SectionLayoutPlanner.IsOverlapFree(cells, 60, 40).Should().BeTrue();
        }

        [Fact]
        public void OverlapCheck_DetectsATooTightGrid()
        {
            var cells = SectionLayoutPlanner.Plan(
                Inputs(("a", 1), ("b", 2)),
                Opts() with { SpacingX = 10 });

            SectionLayoutPlanner.IsOverlapFree(cells, viewWidth: 50, viewHeight: 40)
                .Should().BeFalse("the checker must catch a grid that would overlap");
        }

        [Fact]
        public void SingleColumnIsAllowed_ButStillOrdered()
        {
            var cells = SectionLayoutPlanner.Plan(
                Inputs(("b", 200), ("a", 100)), Opts(columns: 1));

            cells.Select(c => c.RecordId).Should().ContainInOrder("a", "b");
            cells.Select(c => c.Column).Should().AllBeEquivalentTo(0);
        }
    }

    /// <summary>
    /// Section identity must never smuggle in the unconfirmed "1086 == station 1+086"
    /// convention. Label and station are separate facts.
    /// </summary>
    public class SectionNumberingTests
    {
        private static ClSourceRecord Cl(string? label, string handle = "AB12") => new()
        {
            RecordId = "r1",
            SourceDrawing = "CL.dwg",
            SourceDrawingHash = "h",
            SourceHandle = handle,
            SourceEntityType = "LINE",
            SourceLayer = "CL",
            SourceEndpoints = new double[] { 0, 10, 0, -10 },
            WcsEndpoints = new double[] { 0, 10, 0, -10 },
            CandidateSectionNumber = label,
        };

        private static ProjectProfile Profile() => new() { ProfileId = "6422" };

        [Fact]
        public void ExplicitProjectLabel_IsPreserved()
        {
            var r = new SectionPlanRecord { RecordId = "r1", Cl = Cl("חתך 12"), Station = 1086.4 };
            SectionPlanLogic.ValidateNumbering(r, Profile());

            r.SectionId.Should().Be("חתך 12");
            r.Station.Should().Be(1086.4, "the station stays a separate fact");
        }

        [Fact]
        public void NoLabel_FallsBackToNeutralStationIdentity()
        {
            var r = new SectionPlanRecord { RecordId = "r1", Cl = Cl(null), Station = 1086.4 };
            SectionPlanLogic.ValidateNumbering(r, Profile());

            r.SectionId.Should().Be("STA-1086");
        }

        [Fact]
        public void NoLabelAndNoStation_FallsBackToClIdentity()
        {
            var r = new SectionPlanRecord { RecordId = "r1", Cl = Cl(null, "FF07") };
            SectionPlanLogic.ValidateNumbering(r, Profile());

            r.SectionId.Should().Be("CL-FF07");
        }

        [Fact]
        public void NumericLabel_IsNotInterpretedAsAStation()
        {
            // "1086" next to a CL at station 240 must NOT be read as 1+086.
            var r = new SectionPlanRecord { RecordId = "r1", Cl = Cl("1086"), Station = 240.0 };
            SectionPlanLogic.ValidateNumbering(r, Profile());

            r.SectionId.Should().Be("1086");
            r.Station.Should().Be(240.0);
            r.Findings.Should().BeEmpty(
                "station/label correlation is only checked when the project explicitly enables it");
        }
    }
}
