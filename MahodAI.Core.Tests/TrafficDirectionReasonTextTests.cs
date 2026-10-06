using System;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests
{
    public class TrafficDirectionReasonTextTests
    {
        [Fact]
        public void LiveDialogEvidence_ReadsAsHebrewSentences()
        {
            // The exact evidence of STA-42676 in the live directions dialog (30.09.2026).
            TrafficDirectionReasonText.Describe("unknown",
                    "automatic-traffic-scope=cut-not-contained-in-one-native-straight-segment;no-approved-arrow-in-range")
                .Should().Be("לא נקבע · החתך אינו על קטע ישר אחד של התוואי, לכן אין זיהוי אוטומטי מחצים · " +
                             "אין חץ תנועה מאושר בטווח — נדרש אישור ידני");
            TrafficDirectionReasonText.Describe("resolved",
                    "automatic-traffic-scope=cut-not-contained-in-one-native-straight-segment;resolved-from-explicit-current-lane-edit")
                .Should().Be("נקבע · החתך אינו על קטע ישר אחד של התוואי, לכן אין זיהוי אוטומטי מחצים · נקבע באישור ידני לנתיב זה");
        }

        [Theory]
        [InlineData("cut-not-contained-in-one-native-straight-segment")]
        [InlineData("ambiguous-native-straight-segment")]
        [InlineData("invalid-native-straight-segment-evidence")]
        [InlineData("nearby-arrows-outside-proven-straight-segment")]
        [InlineData("resolved-from-explicit-current-lane-edit")]
        [InlineData("explicit-current-lane-direction-v1")]
        [InlineData("resolved-from-approved-arrow")]
        [InlineData("nearest-approved-arrows-agree")]
        [InlineData("no-approved-arrow-in-range")]
        [InlineData("nearest-arrows-conflict")]
        [InlineData("nearest-arrow-heading-cancels")]
        [InlineData("nearest-approved-bike-arrows-agree")]
        [InlineData("no-approved-bike-arrow-in-range")]
        [InlineData("nearest-bike-arrows-conflict")]
        [InlineData("nearest-bike-arrow-heading-cancels")]
        [InlineData("duplicate-explicit-direction-edits")]
        [InlineData("duplicate-manual-direction-decisions")]
        [InlineData("restored-from-plan")]
        [InlineData("plan-evidence-restored")]
        [InlineData("invalid-lane-cut-scope")]
        [InlineData("invalid-arrow-evidence-mode")]
        [InlineData("invalid-lane-identity")]
        [InlineData("invalid-query")]
        public void EveryProducedCode_HasHebrewWording(string code)
        {
            var text = TrafficDirectionReasonText.Reason(code);
            text.Should().NotBe(code);
            Regex.IsMatch(text, "[a-z]{3,}").Should().BeFalse($"'{text}' must not show code words");
        }

        [Fact]
        public void UnknownCodes_AreShownAsIs_AndDetailIsKept()
        {
            TrafficDirectionReasonText.Reason("some-future-code").Should().Be("some-future-code");
            TrafficDirectionReasonText.Reason("nearest-arrows-conflict:2")
                .Should().Be("החצים הקרובים סותרים זה את זה — נדרש אישור ידני (2)");
            TrafficDirectionReasonText.Reason("no-approved-arrow-in-range;no-approved-arrow-in-range")
                .Should().Be("אין חץ תנועה מאושר בטווח — נדרש אישור ידני", "the same sentence is not repeated");
            TrafficDirectionReasonText.Reason(null).Should().BeEmpty();
            TrafficDirectionReasonText.Describe("ambiguous", null).Should().Be("סותר");
            TrafficDirectionReasonText.State("other").Should().Be("other");
        }

        [Fact]
        public void Flow_UsesThePlannerTokens()
        {
            TrafficDirectionReasonText.Flow(SectionVehicleDirectionPlanner.AlongFlowToken).Should().Be("עם כיוון הציר");
            TrafficDirectionReasonText.Flow(SectionVehicleDirectionPlanner.AgainstFlowToken).Should().Be("נגד כיוון הציר");
            TrafficDirectionReasonText.Flow(null).Should().Be("לא נקבע");
        }

        [Fact]
        public void CodesProducedByTheSource_AreAllWorded()
        {
            // Every reason literal in the direction planners must have wording; a new code fails here, not on screen.
            var root = FindRepoRoot();
            var sources = new[]
            {
                "MahodAI.CivilDelivery.Core/Shared/SectionVehicleDirectionPlanner.cs",
                "MahodAI.CivilDelivery.Core/Shared/TrafficDirectionEvidenceLogic.cs",
                "MahodAI.CivilDelivery.Core/Shared/SectionTrafficStraightScopeLogic.cs",
            };
            var literals = sources
                .SelectMany(s => Regex.Matches(System.IO.File.ReadAllText(System.IO.Path.Combine(root, s)),
                    "\"([a-z0-9]+(?:-[a-z0-9]+){2,})\"").Select(m => m.Groups[1].Value))
                .Where(c => c is not SectionVehicleDirectionPlanner.AlongFlowToken and not SectionVehicleDirectionPlanner.AgainstFlowToken)
                // "…-v1" literals are evidence contract names, not reasons; the one lane-edit reason that is
                // versioned is covered by the explicit theory above.
                .Where(c => !Regex.IsMatch(c, "-v[0-9]+$") || c == "explicit-current-lane-direction-v1")
                .Distinct().ToArray();
            literals.Should().NotBeEmpty();
            literals.Where(c => TrafficDirectionReasonText.Reason(c) == c).Should().BeEmpty();
        }

        private static string FindRepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "MahodAI.CivilDelivery.Core", "MahodAI.CivilDelivery.Core.csproj")))
                dir = System.IO.Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("repo root not found");
        }
    }
}
