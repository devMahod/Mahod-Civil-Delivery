using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class SectionRecordEvidenceLogicTests
{
    private static SectionRecordEvidenceLogic.Identity Row(string id, string? key) => new(id, key);

    [Fact]
    public void ExactRows_Reordered_AreGreen()
    {
        var verdict = SectionRecordEvidenceLogic.Compare(
            new[] { Row("A", "KA"), Row("B", null) },
            new[] { Row("B", null), Row("A", "KA") });

        Assert.True(verdict.IsExact);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate-plan")]
    [InlineData("duplicate-apply")]
    [InlineData("logical-key")]
    [InlineData("empty-id")]
    public void AnyCardinalityOrIdentityDrift_IsRed(string scenario)
    {
        var plan = new[] { Row("A", "KA"), Row("B", null) };
        var apply = new[] { Row("A", "KA"), Row("B", null) };

        var verdict = scenario switch
        {
            "missing" => SectionRecordEvidenceLogic.Compare(plan, new[] { Row("A", "KA") }),
            "extra" => SectionRecordEvidenceLogic.Compare(plan,
                new[] { Row("A", "KA"), Row("B", null), Row("C", "KC") }),
            "duplicate-plan" => SectionRecordEvidenceLogic.Compare(
                new[] { Row("A", "KA"), Row("A", "KA") }, apply),
            "duplicate-apply" => SectionRecordEvidenceLogic.Compare(plan,
                new[] { Row("A", "KA"), Row("A", "KA"), Row("B", null) }),
            "logical-key" => SectionRecordEvidenceLogic.Compare(plan,
                new[] { Row("A", "OTHER"), Row("B", null) }),
            "empty-id" => SectionRecordEvidenceLogic.Compare(
                new[] { Row("", "KA") }, new[] { Row("", "KA") }),
            _ => throw new System.ArgumentOutOfRangeException(nameof(scenario)),
        };

        Assert.False(verdict.IsExact);
    }
}
