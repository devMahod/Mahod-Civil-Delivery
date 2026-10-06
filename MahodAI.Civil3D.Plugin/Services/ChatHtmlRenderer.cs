using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MahodAI.Civil3D.Plugin.Models;
using MahodAI.Civil3D.Plugin.WebSocket;

namespace MahodAI.Civil3D.Plugin.Services
{
    /// <summary>
    /// Handles all HTML generation, markdown conversion, and rendering helpers for the chat UI.
    /// Stateless — all methods are static or take required data as parameters.
    /// </summary>
    public static class ChatHtmlRenderer
    {
        /// <summary>
        /// Convert markdown text to HTML.
        /// </summary>
        public static string ConvertMarkdownToHtml(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
                return markdown;

            // Normalize line endings to Unix style
            var result = markdown.Replace("\r\n", "\n").Replace("\r", "\n");

            // Code blocks first
            result = Regex.Replace(result, @"```(\w*)\n?([\s\S]*?)```", m =>
            {
                var code = System.Net.WebUtility.HtmlEncode(m.Groups[2].Value.Trim());
                return $"<pre><code>{code}</code></pre>";
            });

            // Inline code
            result = Regex.Replace(result, @"`([^`]+)`", "<code>$1</code>");

            // Bold
            result = Regex.Replace(result, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
            result = Regex.Replace(result, @"__(.+?)__", "<strong>$1</strong>");

            // Italic
            result = Regex.Replace(result, @"(?<!\w)\*(?!\*)(.+?)(?<!\*)\*(?!\w)", "<em>$1</em>");
            result = Regex.Replace(result, @"(?<!\w)_(?!_)(.+?)(?<!_)_(?!\w)", "<em>$1</em>");

            // Headers - trim whitespace from content
            result = Regex.Replace(result, @"^####\s+(.+?)\s*$", "<h5>$1</h5>", RegexOptions.Multiline);
            result = Regex.Replace(result, @"^###\s+(.+?)\s*$", "<h4>$1</h4>", RegexOptions.Multiline);
            result = Regex.Replace(result, @"^##\s+(.+?)\s*$", "<h3>$1</h3>", RegexOptions.Multiline);
            result = Regex.Replace(result, @"^#\s+(.+?)\s*$", "<h2>$1</h2>", RegexOptions.Multiline);

            // Markdown links [text](url) → clickable <a> tags
            result = Regex.Replace(result, @"\[([^\]]+)\]\(([^)]+)\)",
                "<a href='$2' target='_blank' style='color:#2e9535; text-decoration:underline;'>$1</a>");

            // Horizontal rule
            result = Regex.Replace(result, @"^[-*]{3,}$", "<hr/>", RegexOptions.Multiline);

            // Markdown tables
            result = ConvertMarkdownTables(result);

            // Lists
            var lines = result.Split('\n');
            var processed = new StringBuilder();
            bool inUnorderedList = false;
            bool inOrderedList = false;

            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();

                if (Regex.IsMatch(trimmed, @"^[\*\-]\s+"))
                {
                    if (inOrderedList) { processed.AppendLine("</ol>"); inOrderedList = false; }
                    if (!inUnorderedList) { processed.AppendLine("<ul>"); inUnorderedList = true; }
                    var content = Regex.Replace(trimmed, @"^[\*\-]\s+", "");
                    processed.AppendLine($"<li>{content}</li>");
                }
                else if (Regex.IsMatch(trimmed, @"^\d+\.\s+"))
                {
                    if (inUnorderedList) { processed.AppendLine("</ul>"); inUnorderedList = false; }
                    if (!inOrderedList) { processed.AppendLine("<ol>"); inOrderedList = true; }
                    var content = Regex.Replace(trimmed, @"^\d+\.\s+", "");
                    processed.AppendLine($"<li>{content}</li>");
                }
                else
                {
                    if (inUnorderedList) { processed.AppendLine("</ul>"); inUnorderedList = false; }
                    if (inOrderedList) { processed.AppendLine("</ol>"); inOrderedList = false; }
                    processed.AppendLine(line);
                }
            }

            if (inUnorderedList) processed.AppendLine("</ul>");
            if (inOrderedList) processed.AppendLine("</ol>");

            result = processed.ToString();

            // Paragraphs
            result = Regex.Replace(result, @"\n\n+", "</p><p>");
            // Convert single newlines to <br/> but not before/after block elements
            result = Regex.Replace(result, @"(?<!</(?:li|ul|ol|h[1-6]|p|div|pre|hr|table|thead|tbody|tr|details|summary)>)\n(?!<(?:table|/table|thead|/thead|tbody|/tbody|tr|/tr|th|td|ul|ol|li|h[1-6]|p|div|pre|hr|details|/details|summary|/summary))", "<br/>");

            var trimmedResult = result.Trim();
            if (!string.IsNullOrEmpty(trimmedResult) &&
                !trimmedResult.StartsWith("<h", StringComparison.OrdinalIgnoreCase) &&
                !trimmedResult.StartsWith("<p", StringComparison.OrdinalIgnoreCase) &&
                !trimmedResult.StartsWith("<ul", StringComparison.OrdinalIgnoreCase) &&
                !trimmedResult.StartsWith("<ol", StringComparison.OrdinalIgnoreCase) &&
                !trimmedResult.StartsWith("<div", StringComparison.OrdinalIgnoreCase) &&
                !trimmedResult.StartsWith("<pre", StringComparison.OrdinalIgnoreCase) &&
                !trimmedResult.StartsWith("<details", StringComparison.OrdinalIgnoreCase) &&
                !trimmedResult.StartsWith("<table", StringComparison.OrdinalIgnoreCase))
            {
                result = "<p>" + result + "</p>";
            }

            // Clean up empty elements and excessive breaks
            result = Regex.Replace(result, @"<p>\s*</p>", "");
            result = Regex.Replace(result, @"(<br/>\s*){2,}", "<br/>");
            result = Regex.Replace(result, @"^\s*<br/>\s*", "");
            result = Regex.Replace(result, @"\s*<br/>\s*$", "");
            result = Regex.Replace(result, @"<(h[2-5])>\s*<br/>\s*", "<$1>");
            result = Regex.Replace(result, @"\s*<br/>\s*</(h[2-5])>", "</$1>");
            // Clean up breaks before and after tables
            result = Regex.Replace(result, @"(<br/>\s*)+<table", "<table", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"</table>(\s*<br/>)+", "</table>", RegexOptions.IgnoreCase);
            // Clean up empty paragraphs before tables
            result = Regex.Replace(result, @"<p>\s*</p>\s*<table", "<table", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"</p>\s*<table", "</p><table", RegexOptions.IgnoreCase);

            // Treat <details>/<summary> as block-level so the fold widget (used by the
            // road-design output to collapse heavy tables) isn't wrapped in stray
            // <p>/<br/> that the markdown passes would otherwise inject around it.
            result = Regex.Replace(result, @"<p>\s*(</?(?:details|summary)[^>]*>)", "$1", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"(</?(?:details|summary)[^>]*>)\s*</p>", "$1", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"(</?(?:details|summary)[^>]*>)\s*<br/>", "$1", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"<br/>\s*(</?(?:details|summary)[^>]*>)", "$1", RegexOptions.IgnoreCase);

            return result.Trim();
        }

        /// <summary>
        /// True when the text still carries any common raw markdown markers — bold
        /// (<c>**…**</c> / <c>__…__</c>), italic (<c>*…*</c> / <c>_…_</c>), headings,
        /// list bullets, markdown tables, or fenced code. Used to force a markdown pass
        /// even on content that <see cref="ContentLooksLikeHtml"/> flags as HTML —
        /// otherwise prose markers sitting next to a server-emitted tag/table would
        /// render literally. Genuine HTML reports never contain these markers so it
        /// is still safe to gate on this.
        /// </summary>
        public static bool HasMarkdownFormatting(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            return Regex.IsMatch(text, @"\*\*[^*\n]+\*\*")
                   || Regex.IsMatch(text, @"__[^_\n]+__")
                   || Regex.IsMatch(text, @"(?<!\w)\*(?!\*)[^*\n]+\*(?!\w)")
                   || Regex.IsMatch(text, @"(?<!\w)_(?!_)[^_\n]+_(?!\w)")
                   || Regex.IsMatch(text, @"(?m)^\s{0,3}#{1,6}\s+\S")
                   || Regex.IsMatch(text, @"(?m)^\s*[-*]\s+\S")
                   || Regex.IsMatch(text, @"(?m)^\s*\d+\.\s+\S")
                   || Regex.IsMatch(text, @"(?m)^\|.*\|\s*$")
                   || text.Contains("```");
        }

        /// <summary>
        /// Run the inline-only markdown passes — bold, italic, inline code, links —
        /// over text that is already valid HTML. Unlike <see cref="ConvertMarkdownToHtml"/>
        /// this never rewrites paragraphs/lists/line-breaks, so it is safe to apply as a
        /// "post-render" pass over server-emitted HTML. Use it whenever a payload looks
        /// like HTML but still carries raw inline markers (mixed payloads, fix-card
        /// summaries, agent-supplied descriptions that bypassed the markdown route).
        /// </summary>
        public static string ApplyInlineMarkdown(string html)
        {
            if (string.IsNullOrEmpty(html))
                return html;

            // CRITICAL: never rewrite the contents of <script>/<style> blocks. The
            // __…__ → <strong> pass mangles JS identifiers like window.__currentFixEdits
            // into "window.<strong>currentFixEdits = window.</strong>currentFixEdits",
            // which is a syntax error — it silently kills a card's inline <script> (e.g.
            // the fix-plan Approve/Cancel handlers stop binding to window). Apply the
            // inline passes only to the markup between such blocks; pass the blocks through
            // verbatim.
            var protectedBlocks = Regex.Matches(
                html, @"<(script|style)\b[^>]*>[\s\S]*?</\1>", RegexOptions.IgnoreCase);
            if (protectedBlocks.Count == 0)
                return ApplyInlineMarkdownCore(html);

            var sb = new StringBuilder();
            int pos = 0;
            foreach (Match m in protectedBlocks)
            {
                if (m.Index > pos)
                    sb.Append(ApplyInlineMarkdownCore(html.Substring(pos, m.Index - pos)));
                sb.Append(m.Value); // verbatim — no markdown passes
                pos = m.Index + m.Length;
            }
            if (pos < html.Length)
                sb.Append(ApplyInlineMarkdownCore(html.Substring(pos)));
            return sb.ToString();
        }

        private static string ApplyInlineMarkdownCore(string html)
        {
            // Bold first so the italic pass doesn't claim one asterisk of a bold pair.
            html = Regex.Replace(html, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
            html = Regex.Replace(html, @"__(.+?)__", "<strong>$1</strong>");
            html = Regex.Replace(html, @"(?<!\w)\*(?!\*)(.+?)(?<!\*)\*(?!\w)", "<em>$1</em>");
            html = Regex.Replace(html, @"(?<!\w)_(?!_)(.+?)(?<!_)_(?!\w)", "<em>$1</em>");
            html = Regex.Replace(html, @"`([^`\n]+)`", "<code>$1</code>");
            html = Regex.Replace(html, @"\[([^\]]+)\]\(([^)]+)\)",
                "<a href='$2' target='_blank' style='color:#2e9535; text-decoration:underline;'>$1</a>");
            return html;
        }

        /// <summary>
        /// Converts markdown tables to HTML tables.
        /// </summary>
        public static string ConvertMarkdownTables(string text)
        {
            var lines = text.Split('\n');
            var result = new StringBuilder();
            var tableLines = new List<string>();
            var pendingEmptyLines = new List<string>();
            bool inTable = false;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                bool isTableRow = line.StartsWith("|") && line.EndsWith("|");
                bool isSeparator = Regex.IsMatch(line, @"^\|[\s\-:|]+\|$");

                if (isTableRow || isSeparator)
                {
                    if (!inTable)
                    {
                        inTable = true;
                        tableLines.Clear();
                        pendingEmptyLines.Clear();
                    }
                    tableLines.Add(line);
                }
                else
                {
                    if (inTable)
                    {
                        result.Append(BuildHtmlTable(tableLines));
                        tableLines.Clear();
                        inTable = false;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        pendingEmptyLines.Add(lines[i]);
                    }
                    else
                    {
                        foreach (var emptyLine in pendingEmptyLines)
                        {
                            result.AppendLine(emptyLine);
                        }
                        pendingEmptyLines.Clear();
                        result.AppendLine(lines[i]);
                    }
                }
            }

            if (inTable && tableLines.Count > 0)
            {
                result.Append(BuildHtmlTable(tableLines));
            }

            return result.ToString();
        }

        /// <summary>
        /// Builds an HTML table from markdown table lines.
        /// </summary>
        public static string BuildHtmlTable(List<string> tableLines)
        {
            if (tableLines.Count < 2)
                return string.Join(" ", tableLines);

            var sb = new StringBuilder();
            sb.Append("<table>");

            bool headerDone = false;
            bool inBody = false;

            foreach (var line in tableLines)
            {
                if (Regex.IsMatch(line, @"^\|[\s\-:|]+\|$"))
                {
                    if (!headerDone)
                    {
                        headerDone = true;
                        sb.Append("</thead><tbody>");
                        inBody = true;
                    }
                    continue;
                }

                var cells = line.Split('|')
                    .Select(c => c.Trim())
                    .ToList();

                if (cells.Count > 0 && string.IsNullOrEmpty(cells[0]))
                    cells.RemoveAt(0);
                if (cells.Count > 0 && string.IsNullOrEmpty(cells[cells.Count - 1]))
                    cells.RemoveAt(cells.Count - 1);

                if (cells.Count == 0)
                    continue;

                sb.Append("<tr>");

                if (!headerDone)
                {
                    if (!inBody)
                        sb.Append("<thead>");

                    foreach (var cell in cells)
                    {
                        sb.Append($"<th>{cell}</th>");
                    }
                }
                else
                {
                    foreach (var cell in cells)
                    {
                        sb.Append(BuildTableBodyCell(cell));
                    }
                }

                sb.Append("</tr>");
            }

            if (inBody)
                sb.Append("</tbody>");

            sb.Append("</table>");
            return sb.ToString();
        }

        /// <summary>
        /// Builds a single <c>&lt;td&gt;</c> for a markdown table body cell. When the cell
        /// names an alignment + station(s) (a finding location), it is wrapped in a clickable
        /// link that posts a <c>zoom_to_location</c> message to the host so the drawing focuses
        /// on that stretch of the alignment. See <see cref="LocationParser"/>.
        /// </summary>
        private static string BuildTableBodyCell(string cell)
        {
            if (LocationParser.TryParse(cell, out string align, out double s1, out double? s2))
            {
                string s2Attr = s2.HasValue
                    ? s2.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "";

                string alignAttr = System.Net.WebUtility.HtmlEncode(align);
                string s1Attr = s1.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string display = ConvertCellInlineMarkdown(cell);

                return $"<td><span class='mahod-loc-link' data-align=\"{alignAttr}\" " +
                       $"data-s1=\"{s1Attr}\" data-s2=\"{s2Attr}\" " +
                       "onclick=\"window.mahodZoomTo && window.mahodZoomTo(this)\" " +
                       "title=\"לחץ למיקוד המקטע בשרטוט\" " +
                       "style=\"cursor:pointer; color:#2e9535; text-decoration:underline;\">" +
                       $"📍 {display}</span></td>";
            }

            return $"<td>{ConvertCellInlineMarkdown(cell)}</td>";
        }

        /// <summary>Convert markdown links inside a table cell to anchor tags.</summary>
        private static string ConvertCellInlineMarkdown(string cell)
        {
            return Regex.Replace(cell, @"\[([^\]]+)\]\(([^)]+)\)",
                "<a href='$2' target='_blank' style='color:#2e9535; text-decoration:underline;'>$1</a>");
        }

        /// <summary>
        /// Build HTML for the LISP tool recommendations section.
        /// </summary>
        public static string BuildLispRecommendationsHtml(List<LispRecommendation>? tools)
        {
            if (tools == null || tools.Count == 0)
                return string.Empty;

            // Theme-aware: colors come from the page's :root CSS variables so the
            // cards render correctly in BOTH light and dark mode (the old
            // hardcoded #fff/#f1f8f3 read as glaring white boxes on dark).
            var sb = new StringBuilder();
            sb.Append("<div style='margin-top:14px; padding:12px 14px; background:var(--green-tint); border:1px solid var(--green-tint-border); border-radius:10px;'>");
            sb.Append("<div style='font-size:13px; font-weight:700; color:var(--accent-green); margin-bottom:10px;'>🛠️ כלי LISP מומלצים</div>");
            sb.Append("<div style='display:flex; flex-direction:column; gap:8px;'>");

            foreach (var t in tools)
            {
                var name = System.Net.WebUtility.HtmlEncode(t.Name ?? string.Empty);
                var cmd = System.Net.WebUtility.HtmlEncode(t.Command ?? string.Empty);
                var desc = System.Net.WebUtility.HtmlEncode(t.ShortDesc ?? string.Empty);
                var sub = System.Net.WebUtility.HtmlEncode(t.Subcategory ?? string.Empty);
                var url = System.Net.WebUtility.HtmlEncode(t.DownloadUrl ?? string.Empty);
                var install = System.Net.WebUtility.HtmlEncode(t.InstallInstructions ?? string.Empty);

                sb.Append("<div style='padding:10px 12px; background:var(--bg-card); border:1px solid var(--border-color); border-radius:8px;'>");
                sb.Append("<div style='display:flex; flex-wrap:wrap; align-items:center; gap:8px; margin-bottom:4px;'>");
                sb.Append($"<span style='font-weight:600; font-size:14px; color:var(--text-primary);'>{name}</span>");
                if (!string.IsNullOrEmpty(cmd))
                    sb.Append($"<span style='font-family:Consolas,Monaco,monospace; font-size:11px; padding:2px 8px; border-radius:12px; background:var(--bg-secondary); color:var(--text-primary); border:1px solid var(--border-color);'>{cmd}</span>");
                if (!string.IsNullOrEmpty(sub))
                    sb.Append($"<span style='font-size:10px; color:var(--text-muted);'>[{sub}]</span>");
                sb.Append("</div>");

                if (!string.IsNullOrEmpty(desc))
                    sb.Append($"<div style='font-size:13px; color:var(--text-secondary); margin-bottom:6px; line-height:1.5;'>{desc}</div>");

                sb.Append("<div style='display:flex; flex-wrap:wrap; gap:8px; align-items:center;'>");
                if (!string.IsNullOrEmpty(url))
                    sb.Append($"<a href='{url}' target='_blank' rel='noopener' style='font-size:12px; padding:5px 10px; border-radius:6px; background:#2e9535; color:#fff; text-decoration:none; font-weight:600;'>⬇️ הורד מ-SharePoint</a>");
                if (!string.IsNullOrEmpty(install))
                    sb.Append($"<span style='font-size:11px; color:var(--text-muted);'>התקנה: {install}</span>");
                sb.Append("</div>");

                sb.Append("</div>");
            }

            sb.Append("</div></div>");
            return sb.ToString();
        }

        /// <summary>
        /// Build HTML for RAG reference sources section.
        /// </summary>
        public static string BuildReferencesHtml(List<RagReference>? references)
        {
            if (references == null || references.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.Append("<div style='margin-top:12px; padding-top:10px; border-top:1px solid var(--border-color);'>");
            sb.Append("<div style='font-size:12px; font-weight:600; color:var(--accent-green); margin-bottom:6px;'>📚 מקורות</div>");
            sb.Append("<ul style='list-style:none; padding:0; margin:0; font-size:12px;'>");

            foreach (var r in references)
            {
                var docName = System.Net.WebUtility.HtmlEncode(
                    !string.IsNullOrEmpty(r.DocumentName) ? r.DocumentName
                    : !string.IsNullOrEmpty(r.Filename) ? r.Filename
                    : "מסמך לא ידוע"
                );
                var simPct = (int)(r.Similarity * 100);
                var simBadge = simPct > 0
                    ? $" <span style='color:var(--text-muted); font-size:11px;'>({simPct}%)</span>"
                    : "";
                var chunkInfo = r.ChunksUsed > 1
                    ? $" <span style='color:var(--text-muted); font-size:11px;'>[{r.ChunksUsed} קטעים]</span>"
                    : "";

                if (!string.IsNullOrEmpty(r.Url))
                {
                    if (r.Pages.Count > 0)
                    {
                        var pageLinks = r.Pages.Select(p =>
                        {
                            var pageUrl = System.Net.WebUtility.HtmlEncode($"{r.Url}#page={p}");
                            return $"<a href='{pageUrl}' style='color:#2e9535; text-decoration:none;'>עמ' {p}</a>";
                        });
                        sb.Append($"<li style='margin-bottom:3px;'>📄 <a href='{System.Net.WebUtility.HtmlEncode(r.Url)}' style='color:#2e9535; text-decoration:none;'>{docName}</a> ({string.Join(", ", pageLinks)}){simBadge}{chunkInfo}</li>");
                    }
                    else
                    {
                        sb.Append($"<li style='margin-bottom:3px;'>📄 <a href='{System.Net.WebUtility.HtmlEncode(r.Url)}' style='color:#2e9535; text-decoration:none;'>{docName}</a>{simBadge}{chunkInfo}</li>");
                    }
                }
                else
                {
                    sb.Append($"<li style='margin-bottom:3px;'>📄 {docName}{simBadge}{chunkInfo}</li>");
                }
            }

            sb.Append("</ul></div>");
            return sb.ToString();
        }

        /// <summary>
        /// Build the tab strip HTML for rendering in WebView2.
        /// </summary>
        public static string BuildTabStripHtml(Dictionary<string, DrawingTab> drawingTabs, string? activeTabKey)
        {
            // Render the strip even with a single tab — engineers asked for a
            // permanent pinned bar so they can switch chats from anywhere
            // in the conversation without scrolling back to the top.
            if (drawingTabs.Count == 0)
                return "";

            var sb = new StringBuilder();
            sb.Append("<div class='tab-strip'>");

            foreach (var tab in drawingTabs.Values.OrderBy(t => t.CreatedAt))
            {
                bool isActive = tab.TabId == activeTabKey;
                string safeName = System.Net.WebUtility.HtmlEncode(tab.DisplayName);
                string activeClass = isActive ? " active" : "";

                sb.Append($@"<div class='tab-item{activeClass}' onclick=""window.chrome.webview.postMessage(JSON.stringify({{action:'switch_tab', key:'{EscapeJsString(tab.TabId)}'}}))"">");
                sb.Append($"<span class='tab-name'>{safeName}</span>");
                sb.Append($@"<span class='tab-close' onclick=""event.stopPropagation(); window.chrome.webview.postMessage(JSON.stringify({{action:'close_tab', key:'{EscapeJsString(tab.TabId)}'}}))"">&times;</span>");
                sb.Append("</div>");
            }

            // Trailing "+" button — opens a new sibling chat for the active
            // drawing. Handled by NewChatControl's WebMessage dispatcher.
            sb.Append(@"<div class='tab-add' title='New chat' onclick=""window.chrome.webview.postMessage(JSON.stringify({action:'new_tab'}))"">+</div>");

            sb.Append("</div>");
            return sb.ToString();
        }

        /// <summary>
        /// Replace the last <c>assistant-stream-block</c> div in the supplied HTML with
        /// <paramref name="replacement"/>. If no stream-block is found, appends.
        /// Pure string transform — does not touch any UI state.
        /// </summary>
        public static string ReplaceLastStreamBlock(string html, string replacement)
        {
            int marker = html.LastIndexOf("assistant-stream-block", StringComparison.Ordinal);
            if (marker == -1) return html + replacement;

            int start = html.LastIndexOf("<div", marker, StringComparison.Ordinal);
            if (start == -1) return html + replacement;

            int depth = 0;
            int pos = start;
            int end = -1;

            while (pos < html.Length)
            {
                int open = html.IndexOf("<div", pos, StringComparison.Ordinal);
                int close = html.IndexOf("</div>", pos, StringComparison.Ordinal);

                if (open != -1 && open < close)
                {
                    depth++;
                    pos = open + 4;
                }
                else if (close != -1)
                {
                    depth--;
                    pos = close + 6;
                    if (depth == 0)
                    {
                        end = pos;
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            return end != -1
                ? html.Remove(start, end - start).Insert(start, replacement)
                : html + replacement;
        }

        /// <summary>
        /// Escape a string for use in JavaScript string literals.
        /// </summary>
        public static string EscapeJsString(string s)
        {
            return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"");
        }

        /// <summary>
        /// Extract body content from a full HTML document, stripping head/scripts/styles.
        /// </summary>
        public static string ExtractBodyContent(string fullHtml)
        {
            if (string.IsNullOrWhiteSpace(fullHtml))
                return string.Empty;

            try
            {
                string lower = fullHtml.ToLowerInvariant();
                int bodyStart = lower.IndexOf("<body", StringComparison.Ordinal);
                if (bodyStart >= 0)
                {
                    bodyStart = lower.IndexOf(">", bodyStart, StringComparison.Ordinal);
                    if (bodyStart >= 0)
                    {
                        bodyStart++;
                        int bodyEnd = lower.IndexOf("</body>", bodyStart, StringComparison.Ordinal);
                        if (bodyEnd > bodyStart)
                            return fullHtml.Substring(bodyStart, bodyEnd - bodyStart).Trim();
                    }
                }

                string cleaned = fullHtml;

                cleaned = Regex.Replace(cleaned, "<!DOCTYPE.*?>", "",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                cleaned = Regex.Replace(cleaned, "</?html.*?>", "",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                cleaned = Regex.Replace(cleaned, "<head.*?</head>", "",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                cleaned = Regex.Replace(cleaned, "<(script|style|meta).*?</\\1>", "",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                cleaned = Regex.Replace(cleaned, "<(script|style|meta)[^>]*?>", "",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);

                return cleaned.Trim();
            }
            catch
            {
                return fullHtml;
            }
        }

        /// <summary>
        /// Check if content looks like HTML (starts with or contains HTML tags).
        /// </summary>
        public static bool LooksLikeHtml(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            string c = content.TrimStart();
            return c.StartsWith("<h", StringComparison.OrdinalIgnoreCase)
                   || c.StartsWith("<p", StringComparison.OrdinalIgnoreCase)
                   || c.StartsWith("<div", StringComparison.OrdinalIgnoreCase)
                   || c.StartsWith("<!", StringComparison.OrdinalIgnoreCase)
                   || c.Contains("<ul", StringComparison.OrdinalIgnoreCase)
                   || c.Contains("<ol", StringComparison.OrdinalIgnoreCase)
                   || c.Contains("<table", StringComparison.OrdinalIgnoreCase)
                   || c.Contains("<div", StringComparison.OrdinalIgnoreCase)
                   || c.Contains("<strong>", StringComparison.OrdinalIgnoreCase)
                   || c.Contains("<em>", StringComparison.OrdinalIgnoreCase)
                   || c.Contains("<body", StringComparison.OrdinalIgnoreCase)
                   || c.Contains("</html>", StringComparison.OrdinalIgnoreCase)
                   || c.Contains("</body>", StringComparison.OrdinalIgnoreCase)
                   || c.Contains("</div>", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Check if content looks like HTML (for body content, slightly different set of checks).
        /// </summary>
        public static bool ContentLooksLikeHtml(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            string c = content.TrimStart();
            return c.StartsWith("<h", StringComparison.OrdinalIgnoreCase)
                   || c.StartsWith("<p", StringComparison.OrdinalIgnoreCase)
                   || c.StartsWith("<div", StringComparison.OrdinalIgnoreCase)
                   || c.StartsWith("<!", StringComparison.OrdinalIgnoreCase)
                   || c.Contains("<ul", StringComparison.OrdinalIgnoreCase)
                   || c.Contains("<ol", StringComparison.OrdinalIgnoreCase)
                   || c.Contains("<table", StringComparison.OrdinalIgnoreCase)
                   || c.Contains("<strong>", StringComparison.OrdinalIgnoreCase)
                   || c.Contains("</div>", StringComparison.OrdinalIgnoreCase)
                   || c.Contains("</p>", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Strip HTML to plain text (single-line).
        /// </summary>
        public static string StripHtmlToPlainText(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return string.Empty;

            string noTags = Regex.Replace(html, "<.*?>", " ", RegexOptions.Singleline);

            return System.Net.WebUtility.HtmlDecode(noTags)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("\t", " ")
                .Trim();
        }

        /// <summary>
        /// Strip HTML to multi-line text (preserves newlines from block elements).
        /// </summary>
        public static string StripHtmlToText(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return string.Empty;

            // Remove script and style tags with content
            var result = Regex.Replace(html, @"<(script|style)[^>]*>[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);

            // Replace br/p/div tags with newlines
            result = Regex.Replace(result, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"</?(p|div|h[1-6]|tr|li)[^>]*>", "\n", RegexOptions.IgnoreCase);

            // Remove all remaining HTML tags
            result = Regex.Replace(result, @"<[^>]+>", "");

            // Decode HTML entities
            result = System.Net.WebUtility.HtmlDecode(result);

            // Normalize whitespace
            result = Regex.Replace(result, @"[ \t]+", " ");
            result = Regex.Replace(result, @"\n\s*\n+", "\n\n");

            return result.Trim();
        }

        /// <summary>
        /// Safely serialize an object to JSON, returning empty string on failure.
        /// </summary>
        public static string SafeSerialize(object? obj)
        {
            if (obj == null) return string.Empty;
            try
            {
                return System.Text.Json.JsonSerializer.Serialize(obj);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
