using FluentAssertions;
using MahodAI.Civil3D.Plugin.Services;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    /// <summary>
    /// Guards the collapsible-fold rendering the road-design output relies on: a
    /// <c>&lt;details&gt;&lt;summary&gt;…&lt;/summary&gt;…&lt;/details&gt;</c> block must
    /// survive <see cref="ChatHtmlRenderer.ConvertMarkdownToHtml"/> as native block-level
    /// elements (not escaped, not wrapped in a stray &lt;p&gt;), with any markdown table
    /// inside it converted to a real &lt;table&gt;.
    /// </summary>
    public class ChatHtmlRendererDetailsFoldTests
    {
        [Fact]
        public void ConvertMarkdownToHtml_KeepsDetailsAndConvertsInnerTable()
        {
            const string md =
                "סיכום\n\n" +
                "<details><summary>טבלה</summary>\n\n" +
                "| # | ערך |\n|---|---|\n| 1 | 42 |\n\n" +
                "</details>";

            var html = ChatHtmlRenderer.ConvertMarkdownToHtml(md);

            html.Should().Contain("<details>");
            html.Should().Contain("<summary>");
            html.Should().Contain("</details>");
            // The markdown table inside the fold is converted to a real HTML table.
            html.Should().Contain("<table>");
            // The details/summary tags are not swallowed by a paragraph wrapper.
            html.Should().NotContain("<p><details>");
            html.Should().NotContain("<summary></p>");
        }

        [Fact]
        public void ConvertMarkdownToHtml_FoldOnlyPayloadIsNotParagraphWrapped()
        {
            // The routing-warning message is a bare fold — it must not be wrapped in
            // a <p>, which would put the whole widget inside a paragraph box.
            const string md = "<details><summary>אזהרות</summary>\n\n- א\n- ב\n\n</details>";

            var html = ChatHtmlRenderer.ConvertMarkdownToHtml(md);

            html.TrimStart().Should().StartWith("<details>");
            html.Should().Contain("<ul>");
        }

        [Fact]
        public void ConvertMarkdownToHtml_SummaryKeepsInlineMarkdown()
        {
            const string md = "📐 **חתכים רוחביים** — 100 חתכים\n\n" +
                              "<details><summary>טבלת חתכים</summary>\n\nשורה\n\n</details>";

            var html = ChatHtmlRenderer.ConvertMarkdownToHtml(md);

            html.Should().Contain("<strong>חתכים רוחביים</strong>");
            html.Should().Contain("<details>");
        }
    }
}
