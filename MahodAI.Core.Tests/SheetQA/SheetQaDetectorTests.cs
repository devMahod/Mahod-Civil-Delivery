using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Services.SheetQA.Pure;
using Xunit;

namespace MahodAI.Core.Tests.SheetQA
{
    /// <summary>
    /// The visual-scan detectors, exercised without a Civil 3D host. These are the rules the
    /// engineer's report is built from, so each boundary is pinned explicitly rather than
    /// inferred from a happy-path example.
    /// </summary>
    public class TextOverlapDetectorTests
    {
        private static SheetTextRecord Text(
            double minX, double minY, double maxX, double maxY, string content = "label") => new()
            {
                MinX = minX,
                MinY = minY,
                MaxX = maxX,
                MaxY = maxY,
                RawText = content,
                DisplayText = content,
                Height = maxY - minY,
                Layer = "TEXT"
            };

        [Fact]
        public void Detect_SeparateLabels_FindsNothing()
        {
            var texts = new[] { Text(0, 0, 10, 2), Text(20, 0, 30, 2) };

            TextOverlapDetector.Detect(texts).Should().BeEmpty();
        }

        [Fact]
        public void Detect_TouchingEdges_IsNotAnOverlap()
        {
            var texts = new[] { Text(0, 0, 10, 2), Text(10, 0, 20, 2) };

            TextOverlapDetector.Detect(texts).Should().BeEmpty();
        }

        [Fact]
        public void Detect_FullyBuriedLabel_ScoresOne()
        {
            var big = Text(0, 0, 100, 10, "a long survey note");
            var small = Text(40, 4, 50, 6, "57.56");

            var pairs = TextOverlapDetector.Detect(new[] { big, small });

            pairs.Should().HaveCount(1);
            pairs[0].Ratio.Should().BeApproximately(1.0, 1e-9);
            pairs[0].MinX.Should().Be(0);
            pairs[0].MaxX.Should().Be(100);
        }

        [Fact]
        public void Detect_RatioIsMeasuredAgainstTheSmallerLabel()
        {
            // Half of the small label is covered; measured against the big one this would
            // be a rounding error and the collision would be missed.
            var big = Text(0, 0, 100, 10);
            var small = Text(95, 0, 105, 10);

            var pairs = TextOverlapDetector.Detect(new[] { big, small });

            pairs.Should().HaveCount(1);
            pairs[0].Ratio.Should().BeApproximately(0.5, 1e-9);
        }

        [Fact]
        public void Detect_BelowThreshold_IsIgnored()
        {
            // 10% of the smaller box — kerning slack, not a collision.
            var a = Text(0, 0, 10, 10);
            var b = Text(9, 0, 19, 10);

            TextOverlapDetector.Detect(new[] { a, b }, minOverlapRatio: 0.20).Should().BeEmpty();
            TextOverlapDetector.Detect(new[] { a, b }, minOverlapRatio: 0.05).Should().HaveCount(1);
        }

        [Fact]
        public void Detect_ReportsWorstCollisionsFirst()
        {
            var anchor = Text(0, 0, 10, 10);
            var slight = Text(7, 0, 17, 10);      // 30%
            var severe = Text(1, 0, 11, 10);      // 90%

            var pairs = TextOverlapDetector.Detect(new[] { anchor, slight, severe });

            pairs.Should().HaveCountGreaterThanOrEqualTo(2);
            pairs[0].Ratio.Should().BeGreaterThan(pairs[^1].Ratio);
        }

        [Fact]
        public void Detect_ZeroAreaText_DoesNotDivideByZero()
        {
            var degenerate = Text(5, 5, 5, 5, "");
            var normal = Text(0, 0, 10, 10);

            var act = () => TextOverlapDetector.Detect(new[] { degenerate, normal });

            act.Should().NotThrow();
        }

        [Theory]
        [InlineData(0.9, "high")]
        [InlineData(0.5, "medium")]
        [InlineData(0.25, "low")]
        public void SeverityFor_GradesByHowBuriedTheLabelIs(double ratio, string expected)
        {
            TextOverlapDetector.SeverityFor(ratio).Should().Be(expected);
        }
    }

    public class RotationClassifierTests
    {
        [Theory]
        [InlineData(0, false)]        // horizontal
        [InlineData(45, false)]       // following a line, still readable
        [InlineData(90, false)]       // read from the edge — a normal convention
        [InlineData(95, false)]       // exactly at the limit stays legal
        [InlineData(96, true)]
        [InlineData(180, true)]       // upside down
        [InlineData(264, true)]
        [InlineData(265, false)]      // the other limit
        [InlineData(270, false)]
        [InlineData(359, false)]
        public void IsUnreadable_FlagsOnlyPastVertical(double degrees, bool expected)
        {
            RotationClassifier.IsUnreadable(degrees).Should().Be(expected);
        }

        [Theory]
        [InlineData(-90, 270)]
        [InlineData(-1, 359)]
        [InlineData(360, 0)]
        [InlineData(725, 5)]
        public void Normalize_FoldsAnyAngleIntoOneTurn(double input, double expected)
        {
            RotationClassifier.Normalize(input).Should().BeApproximately(expected, 1e-9);
        }

        [Fact]
        public void NormalizeRadians_ConvertsAndFolds()
        {
            // -2π is what the RD383 viewport stores, and it means no rotation at all.
            RotationClassifier.NormalizeRadians(-2 * System.Math.PI).Should().BeApproximately(0, 1e-6);
        }

        [Fact]
        public void FindUnreadable_ReturnsOnlyTheOffendingLabels()
        {
            var texts = new List<SheetTextRecord>
            {
                new() { RotationDeg = 0, DisplayText = "fine" },
                new() { RotationDeg = 180, DisplayText = "mirrored" },
                new() { RotationDeg = 217, DisplayText = "tipped over" }
            };

            var found = RotationClassifier.FindUnreadable(texts);

            found.Should().HaveCount(2);
            found.Select(t => t.DisplayText).Should().NotContain("fine");
        }

        [Theory]
        [InlineData(180, "high")]
        [InlineData(140, "medium")]
        [InlineData(100, "low")]
        public void SeverityFor_TreatsFullyMirroredTextAsWorst(double degrees, string expected)
        {
            RotationClassifier.SeverityFor(degrees).Should().Be(expected);
        }
    }

    public class DeclutterAnalyzerTests
    {
        private static SheetTextRecord OnLayer(string layer, string text) => new()
        {
            Layer = layer,
            RawText = text,
            DisplayText = text
        };

        [Theory]
        [InlineData("57.56", true)]
        [InlineData("250", true)]
        [InlineData("+12.5", true)]
        [InlineData("-3.75", true)]
        [InlineData("  80  ", true)]
        [InlineData("מים", false)]
        [InlineData("K+250", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsNumericLabel_RecognisesElevationStyleAnnotation(string? raw, bool expected)
        {
            DeclutterAnalyzer.IsNumericLabel(raw).Should().Be(expected);
        }

        [Fact]
        public void Analyze_RanksTheHeaviestLayersFirst()
        {
            var texts = new List<SheetTextRecord>();
            for (int i = 0; i < 40; i++) texts.Add(OnLayer("contours", "12.5"));
            for (int i = 0; i < 25; i++) texts.Add(OnLayer("notes", "הערה"));

            var candidates = DeclutterAnalyzer.Analyze(texts, minTextCount: 20);

            candidates.Should().HaveCount(2);
            candidates[0].Layer.Should().Be("contours");
            candidates[0].NumericShare.Should().BeApproximately(1.0, 1e-9);
            candidates[1].NumericShare.Should().BeApproximately(0.0, 1e-9);
        }

        [Fact]
        public void Analyze_IgnoresLayersTooSmallToMatter()
        {
            var texts = Enumerable.Range(0, 5).Select(_ => OnLayer("sparse", "1.0")).ToList();

            DeclutterAnalyzer.Analyze(texts, minTextCount: 20).Should().BeEmpty();
        }

        [Fact]
        public void SeverityFor_RatesDenseNumericLayersHighest()
        {
            var elevation = new DeclutterCandidate { TextCount = 138, NumericShare = 1.0 };
            var mixed = new DeclutterCandidate { TextCount = 60, NumericShare = 0.6 };
            var textual = new DeclutterCandidate { TextCount = 30, NumericShare = 0.1 };

            DeclutterAnalyzer.SeverityFor(elevation).Should().Be("high");
            DeclutterAnalyzer.SeverityFor(mixed).Should().Be("medium");
            DeclutterAnalyzer.SeverityFor(textual).Should().Be("low");
        }
    }

    public class LegendMatcherTests
    {
        [Theory]
        [InlineData("BK", "bk")]
        [InlineData("survey|BK", "bk")]
        [InlineData("xref|nested|DASHED2", "dashed2")]
        [InlineData("", "?")]
        [InlineData(null, "?")]
        public void NormalizeLinetype_StripsXrefPrefixesAndCase(string? input, string expected)
        {
            LegendMatcher.NormalizeLinetype(input).Should().Be(expected);
        }

        [Fact]
        public void BuildStyleKey_MakesTheSameStyleCompareEqualAcrossXrefs()
        {
            LegendMatcher.BuildStyleKey(102, "BK")
                .Should().Be(LegendMatcher.BuildStyleKey(102, "bezeq_xref|bk"));
        }

        [Fact]
        public void PairRows_AttachesTheNearestCaption()
        {
            var lines = new[]
            {
                new LegendSampleLine { ColorIndex = 102, Linetype = "BK", MidY = 10 },
                new LegendSampleLine { ColorIndex = 204, Linetype = "HK", MidY = 20 }
            };
            var labels = new[]
            {
                new LegendLabelText { Label = "קו בזק קיים", MidY = 10.2 },
                new LegendLabelText { Label = "קו הוט קיים", MidY = 19.8 }
            };

            var rows = LegendMatcher.PairRows(lines, labels);

            rows.Should().HaveCount(2);
            rows[0].Label.Should().Be("קו בזק קיים");
            rows[1].Label.Should().Be("קו הוט קיים");
        }

        [Fact]
        public void PairRows_LeavesTheLabelEmptyWhenNothingIsNearEnough()
        {
            var lines = new[] { new LegendSampleLine { ColorIndex = 1, Linetype = "X", MidY = 0 } };
            var labels = new[] { new LegendLabelText { Label = "far away", MidY = 500 } };

            var rows = LegendMatcher.PairRows(lines, labels, maxLabelDistance: 5);

            rows.Should().HaveCount(1);
            rows[0].Label.Should().BeEmpty();
        }

        [Fact]
        public void Diff_ReportsBothDirections()
        {
            var legend = new[]
            {
                new LegendRow { ColorIndex = 102, Linetype = "bk", Label = "בזק" },
                new LegendRow { ColorIndex = 30, Linetype = "gas", Label = "גז" }   // never drawn
            };
            var plan = new[]
            {
                new SheetCurveRecord { ColorIndex = 102, Linetype = "bk" },
                new SheetCurveRecord { ColorIndex = 7, Linetype = "hidden" }        // not in the legend
            };

            var diff = LegendMatcher.Diff(legend, plan);

            diff.MatchedStyleCount.Should().Be(1);
            diff.MissingFromLegend.Should().ContainSingle()
                .Which.StyleKey.Should().Be(LegendMatcher.BuildStyleKey(7, "hidden"));
            diff.OrphanLegendRows.Should().ContainSingle()
                .Which.Label.Should().Be("גז");
        }

        [Fact]
        public void Diff_ScopingKeepsSurveyNoiseOutOfTheMissingList()
        {
            var legend = System.Array.Empty<LegendRow>();
            var plan = new[]
            {
                new SheetCurveRecord { ColorIndex = 102, Linetype = "bk", SourceXref = "6327-BEZEK-EX-2500" },
                new SheetCurveRecord { ColorIndex = 238, Linetype = "ltp077", SourceXref = "6327-SR--383-1000ALL" }
            };
            var scope = new HashSet<string> { "6327-BEZEK" };

            var diff = LegendMatcher.Diff(legend, plan, scope);

            diff.MissingFromLegend.Should().ContainSingle()
                .Which.SourceXref.Should().Be("6327-BEZEK-EX-2500");
        }

        [Fact]
        public void Diff_CountsRepeatedCurvesUnderOneStyle()
        {
            var plan = Enumerable.Range(0, 5)
                .Select(_ => new SheetCurveRecord { ColorIndex = 11, Linetype = "continuous" })
                .ToArray();

            var diff = LegendMatcher.Diff(System.Array.Empty<LegendRow>(), plan);

            diff.PlanStyleCount.Should().Be(1);
            diff.MissingFromLegend.Should().ContainSingle().Which.Count.Should().Be(5);
        }
    }

    public class FindingIdGeneratorTests
    {
        [Theory]
        // The number is what is drawn on the sheet, so these are the labels the engineer
        // reads off the drawing and looks up in the report.
        [InlineData("VS-3001-01-007", "7")]
        [InlineData("VS-LAYOUT1-125", "125")]
        [InlineData("VS-SHEET-001", "1")]
        [InlineData("VS-3001-01-000", "0")]
        // Anything that is not a numeric suffix is shown whole rather than mangled.
        [InlineData("VS-3001-01-abc", "VS-3001-01-abc")]
        [InlineData("", "")]
        public void MarkerLabel_IsTheTrailingNumber(string id, string expected)
        {
            FindingIdGenerator.MarkerLabel(id).Should().Be(expected);
        }

        [Fact]
        public void MarkerLabel_RoundTripsEveryIdTheGeneratorIssues()
        {
            var generator = new FindingIdGenerator("LAYOUT1");
            var labels = Enumerable.Range(0, 130).Select(_ => FindingIdGenerator.MarkerLabel(generator.Next()));

            // Distinct within a sheet: the report groups by sheet, so a repeated number
            // would point at two different circles.
            labels.Should().OnlyHaveUniqueItems();
        }

        [Fact]
        public void Next_ProducesStableOrderedIds()
        {
            var generator = new FindingIdGenerator("3001-01");

            generator.Next().Should().Be("VS-3001-01-001");
            generator.Next().Should().Be("VS-3001-01-002");
            generator.Count.Should().Be(2);
        }

        [Theory]
        [InlineData("3001-01", "3001-01")]
        [InlineData("PL102", "PL102")]
        [InlineData("pl102", "PL102")]
        [InlineData("sheet 01", "SHEET-01")]
        [InlineData("גליון 0", "0")]                     // Hebrew drops out, the number survives
        [InlineData("   ", "SHEET")]
        [InlineData(null, "SHEET")]
        [InlineData("a-very-long-layout-name-that-keeps-going", "A-VERY-LONG-LAYO")]
        public void SanitizeLayoutName_KeepsIdsPlottable(string? layout, string expected)
        {
            FindingIdGenerator.SanitizeLayoutName(layout).Should().Be(expected);
        }

        [Fact]
        public void SanitizeLayoutName_CollapsesPunctuationRuns()
        {
            FindingIdGenerator.SanitizeLayoutName("A___B").Should().Be("A-B");
        }
    }
}
