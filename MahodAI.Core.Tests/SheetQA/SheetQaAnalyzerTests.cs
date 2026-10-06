using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Services.SheetQA.Pure;
using Xunit;

namespace MahodAI.Core.Tests.SheetQA
{
    /// <summary>
    /// The assembly stage: four checks in, one numbered finding list out. The behaviour that
    /// matters most here is honesty about limits — a capped run must still report the true
    /// total, because a truncated list and a clean sheet otherwise look identical.
    /// </summary>
    public class SheetQaAnalyzerTests
    {
        private static SheetTextRecord Text(
            double minX, double minY, double maxX, double maxY,
            string content = "label", double rotation = 0,
            string layer = "TEXT", SheetSpace space = SheetSpace.Model) => new()
            {
                MinX = minX,
                MinY = minY,
                MaxX = maxX,
                MaxY = maxY,
                RawText = content,
                DisplayText = content,
                RotationDeg = rotation,
                Height = maxY - minY,
                Layer = layer,
                Space = space,
                Handle = $"H{minX}:{minY}"
            };

        private static SheetQaResult Analyze(
            IReadOnlyList<SheetTextRecord> texts,
            IReadOnlyList<SheetCurveRecord>? curves = null,
            IReadOnlyList<LegendRow>? legend = null,
            SheetQaOptions? options = null) =>
            SheetQaAnalyzer.Analyze(
                "3001-01",
                texts,
                curves ?? System.Array.Empty<SheetCurveRecord>(),
                legend ?? System.Array.Empty<LegendRow>(),
                options);

        [Fact]
        public void Analyze_CleanSheet_ProducesNoFindings()
        {
            var texts = new[] { Text(0, 0, 10, 2), Text(50, 50, 60, 52) };

            var result = Analyze(texts);

            result.Findings.Should().BeEmpty();
            result.Layout.Should().Be("3001-01");
            result.Stats.VisibleTexts.Should().Be(2);
        }

        [Fact]
        public void Analyze_OverlapAndRotation_AreReportedSeparately()
        {
            var texts = new[]
            {
                Text(0, 0, 10, 2, "note"),
                Text(1, 0, 11, 2, "chainage"),
                Text(80, 80, 90, 82, "upside down", rotation: 180)
            };

            var result = Analyze(texts);

            result.Findings.Select(f => f.Category).Should()
                .Contain(VisualFindingCategories.TextOverlap)
                .And.Contain(VisualFindingCategories.RotatedText);
        }

        [Fact]
        public void Analyze_EveryFindingGetsAUniqueIdAndACircleToDraw()
        {
            var texts = new[]
            {
                Text(0, 0, 10, 2), Text(1, 0, 11, 2),
                Text(40, 0, 50, 2, rotation: 180)
            };

            var result = Analyze(texts);

            result.Findings.Select(f => f.Id).Should().OnlyHaveUniqueItems();
            result.Findings.Should().OnlyContain(f => f.Id.StartsWith("VS-3001-01-"));
            result.Findings.Where(f => f.HasLocation).Should().OnlyContain(f => f.Radius > 0);
        }

        [Fact]
        public void Analyze_CapReportsTheTrueTotalAndNamesTheTruncation()
        {
            // Twelve labels stacked on the same spot: far more collisions than the cap.
            var texts = Enumerable.Range(0, 12).Select(i => Text(i * 0.1, 0, 10 + i * 0.1, 2)).ToArray();
            var options = new SheetQaOptions { MaxFindingsPerCheck = 3 };

            var result = Analyze(texts, options: options);

            result.Findings.Count(f => f.Category == VisualFindingCategories.TextOverlap).Should().Be(3);
            result.TotalsByCategory[VisualFindingCategories.TextOverlap].Should().BeGreaterThan(3);
            result.TruncatedCategories.Should().Contain(VisualFindingCategories.TextOverlap);
        }

        [Fact]
        public void Analyze_UntruncatedRun_ClaimsNoTruncation()
        {
            var texts = new[] { Text(0, 0, 10, 2), Text(1, 0, 11, 2) };

            var result = Analyze(texts, options: new SheetQaOptions { MaxFindingsPerCheck = 60 });

            result.TruncatedCategories.Should().BeEmpty();
        }

        [Fact]
        public void Analyze_ChecksCanBeRunIndividually()
        {
            var texts = new[]
            {
                Text(0, 0, 10, 2), Text(1, 0, 11, 2),
                Text(40, 0, 50, 2, rotation: 180)
            };
            var options = new SheetQaOptions { Checks = SheetQaChecks.RotatedText };

            var result = Analyze(texts, options: options);

            result.Findings.Should().OnlyContain(f => f.Category == VisualFindingCategories.RotatedText);
            result.TotalsByCategory.Should().NotContainKey(VisualFindingCategories.TextOverlap);
        }

        [Fact]
        public void Analyze_DeclutterFindings_CarryNoCircle()
        {
            var texts = Enumerable.Range(0, 30)
                .Select(i => Text(i * 100, 0, i * 100 + 5, 2, "12.5", layer: "contours"))
                .ToArray();

            var result = Analyze(texts);

            var declutter = result.Findings.Where(f => f.Category == VisualFindingCategories.DeclutterCandidate);
            declutter.Should().NotBeEmpty();
            declutter.Should().OnlyContain(f => !f.HasLocation);
            result.DeclutterCandidates.Should().ContainSingle().Which.Layer.Should().Be("contours");
        }

        [Fact]
        public void Analyze_LegendDifferences_BecomeFindingsInBothDirections()
        {
            var curves = new[] { new SheetCurveRecord { ColorIndex = 7, Linetype = "hidden", Layer = "misc" } };
            var legend = new[] { new LegendRow { ColorIndex = 30, Linetype = "gas", Label = "גז" } };

            var result = Analyze(System.Array.Empty<SheetTextRecord>(), curves, legend);

            result.Findings.Select(f => f.Category).Should()
                .Contain(VisualFindingCategories.LegendMissingRow)
                .And.Contain(VisualFindingCategories.LegendOrphanRow);
            result.Legend!.MatchedStyleCount.Should().Be(0);
        }

        [Fact]
        public void Analyze_OrphanLegendRow_IsMarkedOnTheSheetNotInTheModel()
        {
            var legend = new[] { new LegendRow { ColorIndex = 30, Linetype = "gas", Label = "גז" } };

            var result = Analyze(System.Array.Empty<SheetTextRecord>(), null, legend);

            result.Findings.Single(f => f.Category == VisualFindingCategories.LegendOrphanRow)
                .Space.Should().Be(SheetSpace.Paper);
        }

        [Fact]
        public void Analyze_FindingDetail_UsesTheDecodedHebrew()
        {
            var texts = new[]
            {
                Text(0, 0, 10, 2, "מפתח גליונות"),
                Text(1, 0, 11, 2, "חיבור לתכנון מאושר")
            };

            var result = Analyze(texts);

            result.Findings.Single(f => f.Category == VisualFindingCategories.TextOverlap)
                .Detail.Should().Contain("מפתח גליונות");
        }

        [Fact]
        public void Analyze_OverlapFinding_KeepsTheEntityHandlesForALaterFix()
        {
            var texts = new[] { Text(0, 0, 10, 2), Text(1, 0, 11, 2) };

            var result = Analyze(texts);

            result.Findings.Single(f => f.Category == VisualFindingCategories.TextOverlap)
                .Handles.Should().HaveCount(2);
        }

        [Fact]
        public void Analyze_EmptySheet_IsSafe()
        {
            var result = SheetQaAnalyzer.Analyze("empty", null!, null!, null!, null);

            result.Findings.Should().BeEmpty();
            result.Stats.VisibleTexts.Should().Be(0);
        }

        [Fact]
        public void Analyze_PaperAndModelTextsAreCountedSeparately()
        {
            var texts = new[]
            {
                Text(0, 0, 10, 2, space: SheetSpace.Paper),
                Text(50, 0, 60, 2, space: SheetSpace.Model),
                Text(70, 0, 80, 2, space: SheetSpace.Model)
            };

            var result = Analyze(texts);

            result.Stats.PaperTexts.Should().Be(1);
            result.Stats.ModelTexts.Should().Be(2);
        }
    }
}
