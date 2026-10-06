using FluentAssertions;
using MahodAI.Civil3D.Plugin.Services;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    /// <summary>
    /// Verifies that analysis-report "location" cells (alignment + station range) are
    /// turned into clickable links that zoom the drawing, while ordinary cells are left
    /// alone. Pure HTML rendering — no AutoCAD runtime required.
    /// </summary>
    public class ChatHtmlRendererLocationTests
    {
        private const string LocationTable =
            "| מיקום | בעיה |\n" +
            "| --- | --- |\n" +
            "| ציר Second Street — תחנה 0+035.05 עד 0+175.48 — עקומה 2 | רדיוס 99.1 מ׳ קטן מהמינימום הדרוש 110 מ׳ |\n";

        [Fact]
        public void LocationCell_BecomesClickableLink_WithParsedStations()
        {
            var html = ChatHtmlRenderer.ConvertMarkdownToHtml(LocationTable);

            html.Should().Contain("mahod-loc-link");
            html.Should().Contain("data-align=\"Second Street\"");
            // "0+035.05" -> 35.05, "0+175.48" -> 175.48
            html.Should().Contain("data-s1=\"35.05\"");
            html.Should().Contain("data-s2=\"175.48\"");
            html.Should().Contain("window.mahodZoomTo");
        }

        [Fact]
        public void NonLocationCell_IsNotLinkified()
        {
            var html = ChatHtmlRenderer.ConvertMarkdownToHtml(LocationTable);

            // The "בעיה" (problem) cell mentions radii but no alignment/station, so it
            // must not be wrapped — exactly one location link in the row.
            System.Text.RegularExpressions.Regex
                .Matches(html, "mahod-loc-link")
                .Count.Should().Be(1);
        }

        [Fact]
        public void SingleStationLocation_OmitsEndStation()
        {
            const string table =
                "| מיקום |\n" +
                "| --- |\n" +
                "| ציר Route-5 תחנה 1+234.56 |\n";

            var html = ChatHtmlRenderer.ConvertMarkdownToHtml(table);

            html.Should().Contain("data-align=\"Route-5\"");
            html.Should().Contain("data-s1=\"1234.56\"");
            html.Should().Contain("data-s2=\"\"");
        }

        [Fact]
        public void HeaderCells_AreNeverLinkified()
        {
            var html = ChatHtmlRenderer.ConvertMarkdownToHtml(LocationTable);

            // The header row text "מיקום" must remain a plain <th>, not a link.
            html.Should().Contain("<th>מיקום</th>");
        }
    }
}
