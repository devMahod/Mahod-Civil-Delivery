using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

/// <summary>
/// VERIFY's datum label check double-rounded: decoration labels the full elevation to 2 decimals and
/// stores the evidence to 3, so 102.21489 is labelled "...102.21", stored as 102.215, and re-rounding
/// the evidence gave "...102.22" — a correct label failed (MahodAI live test, 2026-09-30).
/// </summary>
public class SectionDatumTextMatchesTests
{
    [Fact]
    public void A_label_rounded_from_the_full_value_matches_three_decimal_evidence()
    {
        SectionAnnotationContractLogic.DatumTextMatches(SectionDrawingTextLogic.DatumText(102.21489), 102.215).Should().BeTrue();
        SectionAnnotationContractLogic.DatumTextMatches(SectionDrawingTextLogic.DatumText(102.22), 102.215).Should().BeTrue();
        SectionAnnotationContractLogic.DatumTextMatches(SectionDrawingTextLogic.DatumText(100.40), 100.4).Should().BeTrue();
    }

    [Fact]
    public void A_label_for_another_elevation_or_shape_does_not_match()
    {
        SectionAnnotationContractLogic.DatumTextMatches(SectionDrawingTextLogic.DatumText(102.24), 102.215).Should().BeFalse();
        SectionAnnotationContractLogic.DatumTextMatches("102.21", 102.215).Should().BeFalse();
        SectionAnnotationContractLogic.DatumTextMatches(null, 102.215).Should().BeFalse();
        SectionAnnotationContractLogic.DatumTextMatches(SectionDrawingTextLogic.DatumText(102.21), double.NaN).Should().BeFalse();
    }
}
