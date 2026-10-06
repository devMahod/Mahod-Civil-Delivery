using System;
using System.Linq;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class EstimateReviewWorklistTests
{
    private static QuantityRowViewModel Row(string key, string? proposal = null, string? code = null) => new()
    {
        RuleKey = key, Layer = key, EntityType = "LINE", Method = "line-length", ObjectCount = 10,
        Quantity = 100, Unit = "מטר", MappingState = "לבדיקה", CatalogCode = code, ProposedCode = proposal,
    };

    [Fact]
    public void CountsRawFindingsSeparatelyWithoutTurningThemIntoMappingDecisions()
    {
        var rows = new[] { Row("water", "W1"), Row("unknown"), Row("electric", code: "E1") };
        var findings = Enumerable.Range(0, 455).Select(index => new DeliveryFinding
        { Code = index < 300 ? "MEASURE-A" : "SOURCE-B", Domain = "estimate",
            Title = "Evidence", Severity = FindingSeverity.ReviewRequired }).ToArray();
        var result = EstimateReviewWorklist.Build(rows, findings, true);
        Assert.Equal(455, result.RawBlockingFindings); Assert.Equal(2, result.FindingKinds);
        Assert.Equal(2, result.PendingGroups); Assert.Equal(1, result.ProposedGroups); Assert.Equal(1, result.ApprovedGroups);
        Assert.Contains("לא מספר שיוכי המחירון", result.Text);
        Assert.Contains("300", result.Detail); Assert.Contains("155", result.Detail);
        Assert.Null(rows[0].CatalogCode); Assert.Equal(455, findings.Length);
        Assert.All(rows, row => Assert.Equal(100, row.Quantity));
    }

    [Fact]
    public void HistoricalIgnoredAndUnselectedAlternativeNeverLookLikeActionableNewProposals()
    {
        var historical = Row("old", "W1"); historical.HistoricalReason = "old scan";
        var ignored = Row("helper", "W2"); ignored.IsIgnored = true;
        var alternative = Row("perimeter", "W3");
        alternative.AlternativeRuleKey = "area"; alternative.AlternativeCatalogCode = "A1";
        var result = EstimateReviewWorklist.Build(new[] { historical, ignored, alternative },
            Array.Empty<DeliveryFinding>(), false);
        Assert.Equal(3, result.TotalGroups); Assert.Equal(0, result.PendingGroups);
        Assert.Equal(0, result.ProposedGroups); Assert.Contains("לעיון בלבד", result.Text);
    }
    [Fact]
    public void ProjectWideBlockersAreNamedAsCoverage_AndTheMeasurementFilterShowsOnlyLocalProblems()
    {
        var rows = new[] { Row("water", "W1"), Row("kerb", code: "K1"), Row("broken", code: "B1") };
        var xref = Enumerable.Range(0, 7).Select(index => new DeliveryFinding
        { Code = "EST-XREF-TRAVERSAL-UNRESOLVED", Domain = "estimate", Title = "XREF", Severity = FindingSeverity.Error }).ToList();
        var local = new DeliveryFinding { Code = "EST-MEASUREMENT-FAILED", Domain = "estimate", Title = "Local",
            Severity = FindingSeverity.ReviewRequired, AffectedRecordIds = { "r-broken" } };
        // Earthworks-not-assessed is identity-less too, but it is decision-handled and does not block independent lines.
        var earthworks = new DeliveryFinding { Code = "EST-EARTHWORKS-NOT-ASSESSED", Domain = "estimate", Title = "Earthworks",
            Severity = FindingSeverity.ReviewRequired };
        var result = EstimateReviewWorklist.Build(rows, xref.Append(local).Append(earthworks).ToList(), true);
        Assert.Contains("לא מספר שיוכי המחירון", result.Text);
        Assert.Contains("מתוכם 7 ממצאים כלליים שדורשים טיפול לפני אומדן מלא", result.Text);
        Assert.Contains("כיסוי פרויקט: 7 ממצאים כלליים", result.Detail);
        Assert.DoesNotContain("כל השורות", result.Detail);
        Assert.Contains("2 עם שיוך שמור", result.Text);

        rows[0].Presentation = new("דרוש שיוך", "", null, "review", false, true, false, false, 7);
        rows[1].Presentation = new("שויך · כיסוי חסר", "", "10.00", "review", true, true, false, false, 7);
        rows[2].Presentation = new("בדיקת מדידה", "", null, "review", true, true, false, true, 7);
        var legacy = Row("legacy"); legacy.Presentation = new("בדיקת מדידה", "", null, "review", true, true, false);
        var measurement = new[] { rows[0], rows[1], rows[2], legacy }
            .Where(row => QuantityReviewFilter.Matches(row, null, QuantityReviewFilter.Mode.Measurement))
            .Select(row => row.RuleKey).ToList();
        Assert.Equal(new[] { "broken", "legacy" }, measurement);
    }
}

