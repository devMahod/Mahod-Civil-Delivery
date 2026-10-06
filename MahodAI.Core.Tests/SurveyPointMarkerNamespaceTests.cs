using System;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class SurveyPointMarkerNamespaceTests
{
    private const string Survey = "6422-SP-MEDVA-ALL-2026-MHD";

    [Theory]
    [InlineData("CURB-EXST", "S_POINT_E", 3889)]
    [InlineData(Survey + "|S_CURB", Survey + "|S_POINT_E", 3895)]
    [InlineData("PARENT|CHILD|S_CURB", "PARENT|CHILD|S_POINT_E", 2)]
    [InlineData("Parent|Child|S_CURB", "PARENT|CHILD|s_point_e", 2)]
    public void OnlyMatchingSourceNamespaceMakesKnownMarkerAReviewCandidate(string layer, string block, int count)
    {
        var group = G(layer, block, count);
        var verdict = QuantitySignificance.Classify(group);
        verdict.Kind.Should().Be(QuantitySignificance.Kind.Auxiliary);
        verdict.Reason.Should().Contain("S_POINT_E");
        MappingProposalEngine.IsProposalEligible(new MappingProposalEngine.DiscoveredGroup(
            group.RuleKey, group.Layer, "count", group.Unit, count, count)).Should().BeFalse(
            "survey markers should be offered for explicit noise review, not automatic catalog proposals");
        group.Quantity.Should().Be(count);
        group.ObjectCount.Should().Be(count);
        group.Layer.Should().Be(layer, "no persisted source identity is rewritten");
        group.RuleKey.Should().EndWith(Uri.EscapeDataString(block));
    }

    [Theory]
    [InlineData("S_CURB", "UNPROVEN|S_POINT_E")]
    [InlineData("ACTUAL|S_CURB", "OTHER|S_POINT_E")]
    [InlineData("PARENT|CHILD|S_CURB", "CHILD|S_POINT_E")]
    [InlineData("CHILD|S_CURB", "PARENT|CHILD|S_POINT_E")]
    [InlineData("PARENT||CHILD|S_CURB", "PARENT||CHILD|S_POINT_E")]
    [InlineData("PARENT| CHILD|S_CURB", "PARENT| CHILD|S_POINT_E")]
    [InlineData("ACTUAL|S_CURB", "ACTUAL|S_POINT_EG")]
    [InlineData("ACTUAL|S_CURB", "ACTUAL|S_POINT_KR")]
    [InlineData("ACTUAL|S_CURB", "ACTUAL|KERB_UNIT")]
    [InlineData("ACTUAL|S_CURB", "ACTUAL|M1502_P")]
    public void ArbitraryPipeOtherNamespaceAndUnevidencedBlockFamiliesStayQuantities(string layer, string block)
    {
        QuantitySignificance.Classify(G(layer, block, 2)).Kind.Should().Be(QuantitySignificance.Kind.Quantity);
    }

    [Theory]
    [InlineData("layer:OTHER|count|block:ACTUAL%7CS_POINT_E")]
    [InlineData("layer:S_CURB|area|block:ACTUAL%7CS_POINT_E")]
    [InlineData("layer:S_CURB|count|block:ACTUAL%7CS_POINT_E|extra")]
    [InlineData("layer:S_CURB|count|name:ACTUAL%7CS_POINT_E")]
    public void ContradictoryOrMalformedDiscoveryKeyDoesNotQualifyTheNamespace(string key)
    {
        QuantitySignificance.Classify(new(key, "ACTUAL|S_CURB", "יח'", 2, 2))
            .Kind.Should().Be(QuantitySignificance.Kind.Quantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RecognizedMarkerCannotHideAnInvalidMeasurement(double quantity)
    {
        var group = G(Survey + "|S_CURB", Survey + "|S_POINT_E", 1) with { Quantity = quantity };
        QuantitySignificance.Classify(group).Kind.Should().Be(QuantitySignificance.Kind.InvalidMeasurement);
    }

    [Fact]
    public void ExactNative71SubjectStaysMeasuredAndBlockedUntilAnExplicitDecision()
    {
        // Exact subject key/layer from 336457/6489 in the recorded native71 scan.
        // Remaining path/hash are isolated test evidence, not a production approval.
        var group = G(Survey + "|S_CURB", Survey + "|S_POINT_E", 1);
        var record = new NeutralQuantityRecord
        {
            RecordId = "q-disc-336457-6489-count", ProjectProfileId = "TEST-ONLY", RunId = "TEST-ONLY",
            Source = new QuantitySource
            {
                Drawing = "TEST-ONLY.dwg", DrawingPath = @"C:\TEST-ONLY\Survey.dwg",
                DrawingHash = new string('a', 64), Handle = "336457/6489", EntityType = "BLOCKREFERENCE",
                Layer = group.Layer, Xref = Survey,
            },
            Measurement = new QuantityMeasurement { Kind = "count", Method = "block-count+xref-transform", RawValue = 1, Unit = "יח'" },
            Classification = new QuantityClassification { RuleKey = group.RuleKey },
        };
        var findings = QuantitySignificance.DetectReviewFindings(new[] { record });
        var finding = findings.Single(x => x.Code == EstimateFindingCodes.QuantitySignificanceReview);
        finding.Severity.Should().Be(FindingSeverity.ReviewRequired);
        finding.AffectedRecordIds.Should().Equal(record.RecordId);
        record.Measurement.RawValue.Should().Be(1);
        record.Classification.CandidateCatalogCode.Should().BeNull();
        record.Classification.MappingApprovedBy.Should().BeNull();
        record.Classification.RuleKey.Should().Be(group.RuleKey);
        record.Source.Layer.Should().Be(group.Layer);
        QuantitySignificance.DetectReviewFindings(new[] { record }).Should().ContainSingle(
            "repeated classification is not an exclusion or an approval");
    }

    private static QuantitySignificance.Group G(string layer, string block, int count) =>
        new($"layer:{SectionProjectionLogic.LayerLeaf(layer)}|count|block:{Uri.EscapeDataString(block)}",
            layer, "יח'", count, count);
}
