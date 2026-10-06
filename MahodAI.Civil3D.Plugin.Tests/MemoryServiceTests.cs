using System.Linq;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    public class MemoryServiceTests
    {
        #region Jaccard Similarity

        [Fact]
        public void Jaccard_IdenticalStrings_Returns1()
        {
            MemoryService.Jaccard("hello world", "hello world").Should().Be(1.0);
        }

        [Fact]
        public void Jaccard_CompletelyDifferent_Returns0()
        {
            MemoryService.Jaccard("hello world", "foo bar").Should().Be(0.0);
        }

        [Fact]
        public void Jaccard_PartialOverlap_ReturnsBetween0And1()
        {
            // "hello world" tokens: {hello, world}
            // "hello there" tokens: {hello, there}
            // intersection: {hello} = 1, union: {hello, world, there} = 3
            // jaccard = 1/3
            var score = MemoryService.Jaccard("hello world", "hello there");
            score.Should().BeApproximately(1.0 / 3.0, 0.01);
        }

        [Fact]
        public void Jaccard_IsCaseInsensitive()
        {
            MemoryService.Jaccard("Hello World", "hello world").Should().Be(1.0);
        }

        [Fact]
        public void Jaccard_HandlesPunctuation()
        {
            // Punctuation characters are separators, so "hello, world!" → {hello, world}
            MemoryService.Jaccard("hello, world!", "hello world").Should().Be(1.0);
        }

        [Fact]
        public void Jaccard_EmptyStrings_Returns0()
        {
            MemoryService.Jaccard("", "").Should().Be(0.0);
        }

        #endregion

        #region RememberQA / FindSimilar

        [Fact]
        public void RememberQA_StoresEntryAndFindSimilar_RetrievesIt()
        {
            var svc = new MemoryService();
            svc.ClearAll();
            svc.RememberQA("What is the alignment radius?", "<p>The radius is 150m</p>");

            var result = svc.FindSimilar("What is the alignment radius?", 0.8);
            result.Should().NotBeNull();
            result.Should().Contain("150m");
        }

        [Fact]
        public void FindSimilar_ReturnsNull_WhenNoMatchAboveThreshold()
        {
            var svc = new MemoryService();
            svc.ClearAll();
            svc.RememberQA("radius question", "<p>answer</p>");

            var result = svc.FindSimilar("completely unrelated query about surfaces", 0.8);
            result.Should().BeNull();
        }

        [Fact]
        public void RememberQA_IgnoresEmptyQuestion()
        {
            var svc = new MemoryService();
            svc.ClearAll();
            svc.RememberQA("", "<p>html</p>");
            svc.All().Should().BeEmpty();
        }

        [Fact]
        public void RememberQA_IgnoresEmptyHtml()
        {
            var svc = new MemoryService();
            svc.ClearAll();
            svc.RememberQA("question", "");
            svc.All().Should().BeEmpty();
        }

        [Fact]
        public void RememberQA_LimitsTo200Entries()
        {
            var svc = new MemoryService();
            svc.ClearAll();

            for (int i = 0; i < 210; i++)
            {
                svc.RememberQA($"question {i}", $"<p>answer {i}</p>");
            }

            svc.All().Count().Should().BeLessOrEqualTo(200);
        }

        #endregion

        #region Preferences

        [Fact]
        public void SetPref_And_GetPref_RoundTrip()
        {
            var svc = new MemoryService();
            svc.ClearAll();
            svc.SetPref("theme", "dark");
            svc.GetPref("theme").Should().Be("dark");
        }

        [Fact]
        public void GetPref_ReturnsNull_ForMissingKey()
        {
            var svc = new MemoryService();
            svc.ClearAll();
            svc.GetPref("nonexistent").Should().BeNull();
        }

        [Fact]
        public void SetPref_IgnoresEmptyKey()
        {
            var svc = new MemoryService();
            svc.ClearAll();
            svc.SetPref("", "value");
            svc.AllPrefs().Should().BeEmpty();
        }

        [Fact]
        public void ClearAll_RemovesEntriesAndPrefs()
        {
            var svc = new MemoryService();
            svc.RememberQA("q", "<p>a</p>");
            svc.SetPref("key", "val");
            svc.ClearAll();

            svc.All().Should().BeEmpty();
            svc.AllPrefs().Should().BeEmpty();
        }

        #endregion
    }
}
