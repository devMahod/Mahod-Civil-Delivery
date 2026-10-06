using FluentAssertions;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// Display-only left-to-right isolation in the Hebrew notes of the rules workbook (Codex 22:57 / 00:45): whole numeric and
/// inch ranges after Hebrew text are wrapped in LRM; the text without the marks is unchanged; a second pass adds nothing.
/// </summary>
public sealed class BoqRulesDisplayBidiTests
{
    private const char Lrm = '‎';
    private const char Rlm = '\u200F';

    [Theory]
    [InlineData("עמודי שלטי הדרכה 4.00 מ' בקוטר 4\"–6\" (51.31.2205)", "4\"–6\"")]
    [InlineData("לא נמצאו שלטים 613–640; טקסטים לא נבדקו", "613–640")]
    [InlineData("במרחק 0.9–1.7 מ' מקולטנים", "0.9–1.7")]
    public void AWholeRangeAfterHebrewIsIsolated(string text, string token)
    {
        var shown = BoqRulesWorkbookWriter.DisplayNumericRanges(text);
        shown.Should().Contain(Lrm + token + Lrm);
        shown.Replace(Lrm.ToString(), "").Replace(Rlm.ToString(), "").Should().Be(text, "without the marks the text is unchanged");
        BoqRulesWorkbookWriter.DisplayNumericRanges(shown).Should().Be(shown, "a second pass must not wrap twice");
    }

    [Theory]
    [InlineData("Pipe 4\"–6\" only")]           // no Hebrew: untouched
    [InlineData("בקוטר \"4–\"6")]                // not the number-then-inch shape: untouched by the inch rule
    [InlineData("קוד 51.31.2205")]              // an identifier is not a range
    // Part of a compound token is never wrapped alone (Codex 01:38): a thousands group, a Unicode minus, a following operator.
    [InlineData("בקוטר 1,000\"–2\"")]
    [InlineData("בקוטר −4\"–6\"")]
    [InlineData("בקוטר 4\"–6\" /10")]
    public void OtherTextIsLeftAsIs(string text) =>
        BoqRulesWorkbookWriter.DisplayInchRanges(text).Should().Be(text);

    private const string Inch = "4\"–6\"";
    private const string Tail = " (51.31.2205 / 2208), כבדוגמה";

    [Theory]
    [InlineData("raw")]
    [InlineData("prewrapped")]
    [InlineData("half-open")]
    [InlineData("half-close")]
    public void ANumberAfterAWrappedTokenKeepsItsRightToLeftOrder(string form)
    {
        // b18 live (02/10, Codex 03:33): the closing LRM turned "(51.31.2205 / 2208)" into part of the LTR run in Excel.
        // Every input form ends as exactly LRM + token + LRM + RLM, once; a repeated call is byte-identical.
        var token = form switch
        {
            "prewrapped" => Lrm + Inch + Lrm,
            "half-open" => Lrm + Inch,
            "half-close" => Inch + Lrm,
            _ => Inch,
        };
        var text = "עמודי שלטי הדרכה 4.00 מ' בקוטר " + token + Tail;
        var shown = BoqRulesWorkbookWriter.DisplayNumericRanges(text);
        shown.Should().Be("עמודי שלטי הדרכה 4.00 מ' בקוטר " + Lrm + Inch + Lrm + Rlm + Tail);
        BoqRulesWorkbookWriter.DisplayNumericRanges(shown).Should().Be(shown, "a second pass adds nothing");
    }

    [Theory]
    [InlineData("לא נמצאו שלטים 613–640; טקסטים לא נבדקו")]              // a letter comes first: byte-identical to b18
    [InlineData("במרחק 0.9–1.7\n12 מקולטנים")]                        // a new line is a new paragraph
    [InlineData("במרחק 0.9–1.7\u2029 12 מקולטנים")]                   // paragraph separator
    [InlineData("במרחק 0.9–1.7 \u2067(12)\u2069 מקולטנים")]           // an isolate control before the number
    [InlineData("בקוטר 4\"–6\" ٣ שלטים")]                            // a non-ASCII digit is not the proven case
    [InlineData("טווח 1–2 ٣ 12 מקולטנים")]                            // ...and stops the look-ahead before a later ASCII number
    [InlineData("טווח \u200E1–2\u200E ٣ 12 מקולטנים")]                // pre-wrapped: the same (Codex 04:04)
    public void NoRtlMarkWithoutAnAsciiNumberRightAfterTheToken(string text) =>
        BoqRulesWorkbookWriter.DisplayNumericRanges(text).Should().NotContain(Rlm.ToString());

    [Theory]
    [InlineData("טווח 1–2 /3")]                                   // raw compound: not wrapped, no RLM
    [InlineData("טווח \u200E1–2\u200E /3")]                       // pre-wrapped compound: unchanged (Codex 03:59)
    [InlineData("בקוטר \u200E4\"–6\"\u200E /10")]                  // inch range continued by an operator
    [InlineData("מסגרת \u200E512+801\u200E /3")]                  // additive run continued by an operator
    public void ACompoundTokenGetsNoRtlMark(string text)
    {
        BoqRulesWorkbookWriter.DisplayNumericRanges(text).Should().Be(text);
        BoqRulesWorkbookWriter.DisplayAdditiveRuns(text).Should().Be(text);
    }

    [Fact]
    public void ACompoundSignTokenGetsNoRtlMark() =>
        BoqRulesWorkbookWriter.DisplayToken("שלטים \u200E613–640\u200E /2", "613–640").Should().Be("שלטים \u200E613–640\u200E /2");

    [Fact]
    public void ASemicolonAndHebrewAfterTheRangeKeepTheB18Bytes() =>
        BoqRulesWorkbookWriter.DisplayNumericRanges("לא נמצאו שלטים 613–640; טקסטים לא נבדקו")
            .Should().Be("לא נמצאו שלטים " + Lrm + "613–640" + Lrm + "; טקסטים לא נבדקו");

    [Theory]
    [InlineData("la-293-SP-E--Q-כביש.claude-copy.dwg", true)]
    [InlineData("6422-SM-MODEL-NATAZ_2.dwg", false)]
    [InlineData("נת\"צ מודיעין", false)]
    [InlineData("", false)]
    public void AMixedHebrewLatinNameIsRecognised(string text, bool mixed) =>
        MahodAI.CivilDelivery.Shared.Bidi.MixesHebrewAndLatin(text).Should().Be(mixed);
}
