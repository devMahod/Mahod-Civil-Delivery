using System.Collections.Generic;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Services;
using MahodAI.Civil3D.Plugin.WebSocket;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    /// <summary>
    /// The single classification source shared by the result table, the summary banner, and the
    /// on-drawing pin colors. If these three ever disagreed the engineer saw contradicting
    /// fixed/manual/failed counts — the bug this class exists to prevent.
    /// </summary>
    public class FixResultClassifierTests
    {
        private static FixResultItem Item(bool success, string? status = null, string description = "")
            => new() { ItemId = "i", Success = success, Status = status, Description = description };

        [Theory]
        [InlineData("applied", false, "Applied")]   // status wins over success flag
        [InlineData("failed", true, "Failed")]      // status wins over success flag
        [InlineData("skipped", true, "Manual")]
        [InlineData("manual_required", true, "Manual")]
        public void Classify_PrefersExplicitStatus(string status, bool success, string expectedName)
        {
            // The expected outcome travels as a string and is mapped to the enum here.
            // (Enum values are intentionally NOT passed via InlineData — xUnit 2.x drops
            // the whole theory from discovery; measured 2026-08-03: all 4 rows vanished.)
            var expected = Enum.Parse<FixOutcome>(expectedName);
            FixResultClassifier.Classify(Item(success, status)).Should().Be(expected);
        }

        [Fact]
        public void Classify_NullStatus_FallsBackToSuccessFlag()
        {
            FixResultClassifier.Classify(Item(true)).Should().Be(FixOutcome.Applied);
            FixResultClassifier.Classify(Item(false)).Should().Be(FixOutcome.Failed);
        }

        [Fact]
        public void Classify_LegacyDescriptionMarker_IsManual()
        {
            // Older agents: no status, success=true, but the description says it can't be fixed.
            FixResultClassifier.Classify(Item(true, null, "אלמנט זה אינו ניתן לתיקון אוטומטי"))
                .Should().Be(FixOutcome.Manual);
        }

        [Fact]
        public void Tally_CountsEachBucket_AndDerivesTotal()
        {
            var items = new List<FixResultItem>
            {
                Item(true, "applied"),
                Item(true, "applied"),
                Item(true, "applied"),
                Item(true, "applied"),   // 4 applied
                Item(false, "failed"),   // 1 failed
                Item(true, "skipped"),
                Item(true, "manual_required"),
                Item(true, null, "אינו ניתן לתיקון"),   // 3 manual (incl. legacy marker)
            };

            var t = FixResultClassifier.Tally(items);

            t.Applied.Should().Be(4);
            t.Failed.Should().Be(1);
            t.Manual.Should().Be(3);
            t.Total.Should().Be(8);
        }

        [Fact]
        public void Tally_Null_IsZero()
        {
            var t = FixResultClassifier.Tally(null);
            t.Total.Should().Be(0);
        }
    }
}
