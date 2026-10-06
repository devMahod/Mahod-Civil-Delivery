using MahodAI.Civil3D.Plugin.Services;
using Xunit;
using Xunit.Abstractions;

namespace MahodAI.Civil3D.Plugin.Tests.Services
{
    /// <summary>
    /// Road-design / analysis messages must reach the bubble as RENDERED html —
    /// the owner reported seeing "&lt;details&gt;" tags and raw markdown markers
    /// in the chat during generation and road draw (2026-08-05).
    ///
    /// The agent's design pipeline emits collapsible folds
    /// (design.py `_fold()`: &lt;details&gt;&lt;summary&gt;…) wrapped around
    /// markdown, so the renderer has to handle markdown INSIDE raw HTML.
    /// </summary>
    public class DesignMessageRenderingTests
    {
        private readonly ITestOutputHelper _out;

        public DesignMessageRenderingTests(ITestOutputHelper output) => _out = output;

        // Shaped exactly like a real stage message: heading, bold, a fold whose
        // body holds bold text and a markdown table.
        private const string DesignMessage =
            "## שלב 3/8 — יצירת ציר\n" +
            "**רדיוס מינימלי:** 250 מ'\n" +
            "\n" +
            "<details><summary>פרטי התוואי</summary>\n" +
            "\n" +
            "**אורך:** 1,240 מ'\n" +
            "\n" +
            "| תחנה | ערך |\n" +
            "|---|---|\n" +
            "| 0+000 | 1.5 |\n" +
            "\n" +
            "</details>\n";

        [Fact]
        public void DesignMessage_RendersFold_AndLeavesNoRawMarkers()
        {
            string html = ChatHtmlRenderer.ConvertMarkdownToHtml(DesignMessage);
            _out.WriteLine(html);

            // The fold must survive as a real element, not as escaped text.
            Assert.Contains("<details", html);
            Assert.Contains("<summary>", html);
            Assert.DoesNotContain("&lt;details", html);
            Assert.DoesNotContain("&lt;summary", html);

            // No raw markdown may reach the bubble.
            Assert.DoesNotContain("**", html);
            Assert.DoesNotContain("## ", html);

            // The content itself must be formatted.
            Assert.Contains("<strong>", html);
            Assert.Contains("<h3>", html);   // "##" -> h3
        }

        [Fact]
        public void MarkdownInsideTheFold_IsConvertedToo()
        {
            string html = ChatHtmlRenderer.ConvertMarkdownToHtml(DesignMessage);

            // "**אורך:**" lives INSIDE <details> — it must render bold there.
            int foldStart = html.IndexOf("<details", System.StringComparison.Ordinal);
            Assert.True(foldStart >= 0, "fold missing entirely");
            string insideFold = html.Substring(foldStart);

            Assert.DoesNotContain("**", insideFold);
            Assert.Contains("<strong>אורך:</strong>", insideFold);
            // The markdown table inside the fold must become a real table.
            Assert.Contains("<table", insideFold);
        }
    }
}
