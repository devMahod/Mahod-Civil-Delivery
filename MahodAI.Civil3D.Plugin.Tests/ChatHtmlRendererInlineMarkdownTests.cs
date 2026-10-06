using FluentAssertions;
using MahodAI.Civil3D.Plugin.Services;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    /// <summary>
    /// Guards <see cref="ChatHtmlRenderer.ApplyInlineMarkdown"/> against rewriting markup
    /// inside &lt;script&gt;/&lt;style&gt; blocks. The fix-plan card embeds an inline
    /// &lt;script&gt; that references <c>window.__currentFixEdits</c>; the markdown
    /// <c>__…__ → &lt;strong&gt;</c> pass used to mangle that identifier into a syntax
    /// error, which silently broke the card's Approve/Cancel button handlers.
    /// </summary>
    public class ChatHtmlRendererInlineMarkdownTests
    {
        [Fact]
        public void ApplyInlineMarkdown_LeavesScriptBlockUntouched()
        {
            const string html =
                "<p>סיכום __חשוב__</p>" +
                "<script>(function(){ window.__currentFixEdits = window.__currentFixEdits || {}; })();</script>";

            var result = ChatHtmlRenderer.ApplyInlineMarkdown(html);

            // The double-underscore identifier inside the script must survive verbatim …
            result.Should().Contain("window.__currentFixEdits = window.__currentFixEdits || {}");
            // … and the script must NOT contain an injected <strong> from the bold pass.
            result.Should().NotContain("<strong>currentFixEdits");
            // Prose outside the script is still converted.
            result.Should().Contain("<strong>חשוב</strong>");
        }

        [Fact]
        public void ApplyInlineMarkdown_StillConvertsPlainProse()
        {
            ChatHtmlRenderer.ApplyInlineMarkdown("text **bold** and `code`")
                .Should().Contain("<strong>bold</strong>").And.Contain("<code>code</code>");
        }
    }
}
