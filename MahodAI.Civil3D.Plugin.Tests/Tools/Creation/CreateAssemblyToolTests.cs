using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools;
using MahodAI.Civil3D.Plugin.Tools.Creation;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Tools.Creation
{
    /// <summary>
    /// AutoCAD-free tests for create_assembly: the empty-shell + Hebrew guided-add contract.
    ///
    /// Two layers are covered without a live Civil 3D document:
    ///  - <see cref="AssemblyTemplateCatalog"/> — the pure road-type catalog + Hebrew guide
    ///    generation (default widths, element inclusion, overrides, fallback);
    ///  - <see cref="CreateAssemblyTool"/> — metadata, registration, and parameter validation
    ///    reachable before a document is touched.
    /// The live-document creation path (LISP _-CREATEASSEMBLY) is marked Skip.
    /// </summary>
    public class CreateAssemblyToolTests
    {
        // ───────────────────────── Tool metadata / registration ─────────────────────────

        [Fact]
        public void Tool_HasExpectedMetadata()
        {
            var tool = new CreateAssemblyTool();
            tool.Name.Should().Be("create_assembly");
            tool.Category.Should().Be(ToolCategories.Creation);
            tool.ParameterSchema.Should().NotBeNull();
        }

        [Fact]
        public void Tool_IsRegistered()
        {
            ToolRegistry.Instance.HasTool("create_assembly").Should().BeTrue();
        }

        [Fact]
        public void Schema_RequiresAssemblyName_AndEnumeratesRoadTypes()
        {
            var tool = new CreateAssemblyTool();
            var schema = tool.ParameterSchema!.Value;

            schema.GetProperty("required").EnumerateArray()
                .Select(e => e.GetString())
                .Should().Contain("assembly_name");

            var enumValues = schema
                .GetProperty("properties")
                .GetProperty("road_type")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(e => e.GetString())
                .ToArray();

            enumValues.Should().BeEquivalentTo(
                "urban_2lane", "rural_2lane", "divided_highway", "collector_local");
        }

        // ───────────────────────── Parameter validation (no document) ─────────────────────────

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_MissingAssemblyName_ReturnsInvalidParameters()
        {
            var tool = new CreateAssemblyTool();
            var parameters = JsonDocument.Parse(
                """{"road_type": "urban_2lane"}""").RootElement;

            var result = await tool.ExecuteAsync(
                null!, null!, parameters, new ToolCache(), CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.InvalidParameters);
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_NullCivilDoc_ReturnsFail()
        {
            var tool = new CreateAssemblyTool();
            var parameters = JsonDocument.Parse(
                """{"assembly_name": "A1", "road_type": "urban_2lane"}""").RootElement;

            var result = await tool.ExecuteAsync(
                null!, null!, parameters, new ToolCache(), CancellationToken.None);

            // assembly_name present → passes validation, then fails on the null document.
            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.ExecutionFailed);
        }

        [Fact(Skip = "Live creation via LISP _-CREATEASSEMBLY requires a Civil 3D document")]
        [Trait("Category", "RequiresCivil3D")]
        public void Happy_CreatesEmptyShell_AndQueuesCommand() { }

        // ───────────────────────── Catalog: default widths per road type ─────────────────────────

        [Theory]
        [InlineData("urban_2lane", 3.65, 2.5)]
        [InlineData("rural_2lane", 3.75, 2.5)]
        [InlineData("divided_highway", 3.75, 3.0)]
        [InlineData("collector_local", 3.25, 1.5)]
        public void Resolve_UsesMot2011DefaultWidths(string roadType, double lane, double shoulder)
        {
            var t = AssemblyTemplateCatalog.Resolve(roadType);
            t.RoadType.Should().Be(roadType);
            t.FellBackToDefault.Should().BeFalse();
            t.LaneWidth.Should().Be(lane);
            t.ShoulderWidth.Should().Be(shoulder);
        }

        // ───────────────────────── Catalog: element inclusion per road type ─────────────────────────

        [Fact]
        public void Urban_HasCurbs_NoMedian_NoDaylight()
        {
            var t = AssemblyTemplateCatalog.Resolve("urban_2lane");
            t.HasCurbs.Should().BeTrue();
            t.HasMedian.Should().BeFalse();
            t.HasDaylight.Should().BeFalse();
        }

        [Fact]
        public void Rural_HasDaylight_NoMedian_NoCurbs()
        {
            var t = AssemblyTemplateCatalog.Resolve("rural_2lane");
            t.HasDaylight.Should().BeTrue();
            t.HasMedian.Should().BeFalse();
            t.HasCurbs.Should().BeFalse();
        }

        [Fact]
        public void DividedHighway_HasMedianAndDaylight_NoCurbs()
        {
            var t = AssemblyTemplateCatalog.Resolve("divided_highway");
            t.HasMedian.Should().BeTrue();
            t.MedianWidth.Should().BeGreaterThan(0);
            t.HasDaylight.Should().BeTrue();
            t.HasCurbs.Should().BeFalse();
        }

        [Fact]
        public void CollectorLocal_HasCurbs_NoMedian_NoDaylight()
        {
            var t = AssemblyTemplateCatalog.Resolve("collector_local");
            t.HasCurbs.Should().BeTrue();
            t.HasMedian.Should().BeFalse();
            t.HasDaylight.Should().BeFalse();
        }

        // ───────────────────────── Catalog: fallback on invalid / missing road type ─────────────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("highway")]            // old/legacy value no longer in the enum
        [InlineData("not_a_real_type")]
        public void Resolve_InvalidRoadType_FallsBackToRural2Lane(string? roadType)
        {
            var t = AssemblyTemplateCatalog.Resolve(roadType);
            t.RoadType.Should().Be("rural_2lane");
            t.FellBackToDefault.Should().BeTrue();
            // Falls back with the rural default widths.
            t.LaneWidth.Should().Be(3.75);
            t.ShoulderWidth.Should().Be(2.5);
        }

        [Fact]
        public void Resolve_RoadType_IsCaseAndWhitespaceInsensitive()
        {
            var t = AssemblyTemplateCatalog.Resolve("  Divided_Highway  ");
            t.RoadType.Should().Be("divided_highway");
            t.FellBackToDefault.Should().BeFalse();
            t.HasMedian.Should().BeTrue();
        }

        // ───────────────────────── Catalog: width overrides ─────────────────────────

        [Fact]
        public void Resolve_PositiveOverrides_ReplaceDefaultWidths()
        {
            var t = AssemblyTemplateCatalog.Resolve(
                "urban_2lane", laneWidthOverride: 3.0, shoulderWidthOverride: 1.75);
            t.LaneWidth.Should().Be(3.0);
            t.ShoulderWidth.Should().Be(1.75);
            // Element inclusion is unchanged by width overrides.
            t.HasCurbs.Should().BeTrue();
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-2.0)]
        public void Resolve_NonPositiveOverrides_AreIgnored(double bad)
        {
            var t = AssemblyTemplateCatalog.Resolve(
                "rural_2lane", laneWidthOverride: bad, shoulderWidthOverride: bad);
            t.LaneWidth.Should().Be(3.75);   // catalog default retained
            t.ShoulderWidth.Should().Be(2.5);
        }

        // ───────────────────────── Catalog: Hebrew subassembly guide ─────────────────────────

        [Theory]
        [InlineData("urban_2lane")]
        [InlineData("rural_2lane")]
        [InlineData("divided_highway")]
        [InlineData("collector_local")]
        public void Guide_IsNonEmpty_Ordered_AndHebrew(string roadType)
        {
            var t = AssemblyTemplateCatalog.Resolve(roadType);
            var guide = AssemblyTemplateCatalog.BuildSubassemblyGuide(t);

            guide.Should().NotBeEmpty();
            // Every step is non-blank...
            guide.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s));
            // ...numbered in ascending order (each step starts with "<n>.")...
            for (int i = 0; i < guide.Count; i++)
                guide[i].Should().StartWith($"{i + 1}");
            // ...and contains Hebrew text.
            guide.Should().Contain(s => ContainsHebrew(s));
            // The final step tells the engineer to press continue.
            guide.Last().Should().Contain("המשך");
        }

        [Fact]
        public void Guide_DividedHighway_IncludesMedianAndDaylight_NoCurb()
        {
            var t = AssemblyTemplateCatalog.Resolve("divided_highway");
            var guide = AssemblyTemplateCatalog.BuildSubassemblyGuide(t);
            string all = string.Join("\n", guide);

            all.Should().Contain("רצועת הפרדה");   // median
            all.Should().Contain("מדרון התחברות"); // daylight
            all.Should().NotContain("אבני שפה");    // no curbs
        }

        [Fact]
        public void Guide_Urban_IncludesCurb_NoMedian_NoDaylight()
        {
            var t = AssemblyTemplateCatalog.Resolve("urban_2lane");
            var guide = AssemblyTemplateCatalog.BuildSubassemblyGuide(t);
            string all = string.Join("\n", guide);

            all.Should().Contain("אבני שפה");        // curbs
            all.Should().NotContain("רצועת הפרדה");  // no median
            all.Should().NotContain("מדרון התחברות"); // no daylight
        }

        [Fact]
        public void Guide_Rural_IncludesDaylight_NoMedian_NoCurb()
        {
            var t = AssemblyTemplateCatalog.Resolve("rural_2lane");
            var guide = AssemblyTemplateCatalog.BuildSubassemblyGuide(t);
            string all = string.Join("\n", guide);

            all.Should().Contain("מדרון התחברות");   // daylight
            all.Should().NotContain("רצועת הפרדה");  // no median
            all.Should().NotContain("אבני שפה");      // no curbs
        }

        [Fact]
        public void Guide_ReflectsOverriddenLaneWidth()
        {
            var t = AssemblyTemplateCatalog.Resolve("urban_2lane", laneWidthOverride: 3.0);
            var guide = AssemblyTemplateCatalog.BuildSubassemblyGuide(t);
            string all = string.Join("\n", guide);
            all.Should().Contain("3 מ'");        // overridden lane width appears
            all.Should().NotContain("3.65 מ'");  // catalog default no longer shown
        }

        // ───────────────────────── Catalog: cross-section text & message ─────────────────────────

        [Fact]
        public void CrossSectionHe_IsPopulatedForEveryRoadType()
        {
            foreach (var rt in AssemblyTemplateCatalog.SupportedRoadTypes)
            {
                var t = AssemblyTemplateCatalog.Resolve(rt);
                t.CrossSectionHe.Should().NotBeNullOrWhiteSpace();
                ContainsHebrew(t.CrossSectionHe).Should().BeTrue();
            }
        }

        [Fact]
        public void Message_MentionsContinue_AndIsHebrew()
        {
            var t = AssemblyTemplateCatalog.Resolve("urban_2lane");
            var msg = AssemblyTemplateCatalog.BuildMessage("Main St", t, commandQueued: true);
            msg.Should().Contain("Main St");
            msg.Should().Contain("המשך");
            ContainsHebrew(msg).Should().BeTrue();
        }

        [Fact]
        public void Message_NotesFallbackWhenRoadTypeDefaulted()
        {
            var t = AssemblyTemplateCatalog.Resolve("bogus");
            var msg = AssemblyTemplateCatalog.BuildMessage("X", t, commandQueued: true);
            msg.Should().Contain("ברירת מחדל"); // notes that the default was substituted
        }

        // ───────────────────────── helpers ─────────────────────────

        private static bool ContainsHebrew(string s) =>
            s.Any(c => c >= '֐' && c <= '׿');
    }
}
