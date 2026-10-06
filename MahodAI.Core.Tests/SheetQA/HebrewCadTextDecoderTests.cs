using FluentAssertions;
using MahodAI.Civil3D.Plugin.Services.SheetQA.Pure;
using Xunit;

namespace MahodAI.Core.Tests.SheetQA
{
    /// <summary>
    /// Locks the two legacy Hebrew encodings found in the real coordination packages.
    /// Every expectation here was read straight out of
    /// MHD-UT-000-000RD383-P1-30XX.dwg on 2026-08-12, so a regression means the report
    /// stopped matching what the engineer sees on the sheet.
    /// </summary>
    public class HebrewCadTextDecoderTests
    {
        [Theory]
        // Keyboard-mapped ASCII, as stored by the Hebrew SHX faces on the RD383 sheet.
        [InlineData("np,j dkhubu,", "מפתח גליונות")]
        [InlineData("asrud uvrjc, fcha", "שדרוג והרחבת כביש")]
        [InlineData("pej", "פקח")]
        public void Decode_KeyboardMappedHebrew_ReturnsReadableHebrew(string raw, string expected)
        {
            HebrewCadTextDecoder.Decode(raw, "mirym").Should().Be(expected);
        }

        [Fact]
        public void Decode_Cp862BytesReadAsCp1255_RecoversHebrew()
        {
            // "ע.ת" (עמוד תאורה) as it survives in the survey xref: CP862 0x9A ת, '.',
            // and 0x92 ע — the last one having been turned into U+2019 by the CP1255 read.
            var raw = ".’";

            HebrewCadTextDecoder.Decode(raw, "arial.ttf").Should().Be("ע.ת");
        }

        [Fact]
        public void Decode_Cp862VisualOrder_IsRestoredToLogicalOrder()
        {
            // Stored ם,י,מ — final mem first, which only happens when the letters were
            // written out backwards. Logical order is מים (water).
            var raw = "‰";

            HebrewCadTextDecoder.Decode(raw, "arial.ttf").Should().Be("מים");
        }

        [Theory]
        // The spike's real bug: '.' maps to ץ, so a chainage became "57ץ56".
        [InlineData("57.56")]
        [InlineData("1502")]
        [InlineData("+0.00")]
        [InlineData("M3027")]
        [InlineData("PL102")]
        public void Decode_NumericLabels_AreNeverTouched(string raw)
        {
            HebrewCadTextDecoder.Decode(raw, "mirym").Should().Be(raw);
        }

        [Theory]
        [InlineData("EXISTING BEZEQ LINE")]
        [InlineData("TO BE CANCELED/RELOCATED")]
        [InlineData("the new water line")]
        public void Decode_EnglishText_IsNeverTouched(string raw)
        {
            HebrewCadTextDecoder.Decode(raw, "romans.shx").Should().Be(raw);
        }

        [Fact]
        public void Decode_RealUnicodeHebrew_IsReturnedUnchanged()
        {
            const string raw = "חיבור לתכנון מאושר";

            HebrewCadTextDecoder.Decode(raw, "arial.ttf").Should().Be(raw);
        }

        [Theory]
        // Real strings from the RD383 xrefs whose text style is "techno_m" — not a
        // font stem the decoder knows. Every character maps, so the statistical
        // fallback must still recover the Hebrew; relying on the font list alone
        // would silently leave these as gibberish in the report.
        [InlineData("gusfi", "עודכן")]
        [InlineData("kchyuk", "לביטול")]
        public void Decode_UnknownShxFont_FallsBackToFullCoverage(string raw, string expected)
        {
            HebrewCadTextDecoder.Decode(raw, "techno_m").Should().Be(expected);
        }

        [Fact]
        public void Decode_AsciiInTrueTypeFont_IsLeftAlone()
        {
            // A TTF style is never keyboard-mapped, so lowercase Latin there is genuine
            // Latin — decoding it would invent Hebrew that is not on the sheet.
            HebrewCadTextDecoder.Decode("water", "arial.ttf").Should().Be("water");
        }

        [Theory]
        [InlineData("mirym", true)]
        [InlineData("miryl.shx", true)]
        [InlineData("mvt-mirym", true)]
        [InlineData("arial.ttf", false)]
        [InlineData("romans.shx", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsHebrewShxFont_IdentifiesTheHebrewFaces(string? font, bool expected)
        {
            HebrewCadTextDecoder.IsHebrewShxFont(font).Should().Be(expected);
        }

        [Theory]
        [InlineData("מים", false)]          // logical order: final mem last
        [InlineData("םימ", true)]           // visual order: final mem first
        [InlineData("שלום", false)]
        [InlineData("57.56", false)]        // no Hebrew at all
        public void LooksVisuallyReversed_DetectsBackwardsWords(string text, bool expected)
        {
            HebrewCadTextDecoder.LooksVisuallyReversed(text).Should().Be(expected);
        }

        [Fact]
        public void RestoreLogicalOrder_KeepsNumberRunsIntact()
        {
            // Reversing naively would turn the diameter into "052"; digits must survive.
            HebrewCadTextDecoder.RestoreLogicalOrder("םימ 250").Should().Be("250 מים");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Decode_EmptyInput_IsSafe(string? raw)
        {
            HebrewCadTextDecoder.Decode(raw, "mirym").Should().Be(raw ?? string.Empty);
        }
    }
}
