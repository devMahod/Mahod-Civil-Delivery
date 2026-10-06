using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools.Validation;
using Xunit;

namespace MahodAI.Core.Tests
{
    /// <summary>
    /// P0-05: locks the corrected speed-limit sign-code mapping. The old (code-200)*10 was
    /// 10 km/h low against the plugin's own extractor dictionary (201 = "מהירות מרבית 20").
    /// </summary>
    public class SignSpeedCodeTests
    {
        [Theory]
        [InlineData("201", 20)]
        [InlineData("202", 30)]
        [InlineData("207", 80)]
        [InlineData("211", 120)]
        public void SpeedFromSignCode_MatchesExtractorDictionary(string code, int expectedKph)
        {
            ValidateSignsTool.SpeedFromSignCode(code).Should().Be(expectedKph);
        }

        [Theory]
        [InlineData("401")]   // warning sign, not a speed sign
        [InlineData("301")]   // no-entry
        [InlineData("2")]     // malformed
        [InlineData("2011")]  // too long
        [InlineData("")]
        [InlineData(null)]
        public void SpeedFromSignCode_NonSpeedCodes_ReturnNull(string? code)
        {
            ValidateSignsTool.SpeedFromSignCode(code).Should().BeNull();
        }
    }
}
