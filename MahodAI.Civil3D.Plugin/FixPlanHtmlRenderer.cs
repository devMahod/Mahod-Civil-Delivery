using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MahodAI.Civil3D.Plugin
{
    /// <summary>
    /// Renders fix plan, execution results and verification data as RTL Hebrew HTML
    /// for WebView2 display. All rendered cards wrap in a <c>&lt;div id="fix-card-{planId}"&gt;</c>
    /// so they can be replaced in place when the post-fix verification arrives.
    /// </summary>
    public static class FixPlanHtmlRenderer
    {
        private static readonly JsonSerializerOptions _jsOpts = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// Renders a fix plan with per-item editable inputs and approval buttons.
        /// </summary>
        public static string RenderPlan(WebSocket.FixPlanPayload plan)
        {
            var sb = new StringBuilder();
            var planId = plan.PlanId ?? "";
            var planIdEnc = WebUtility.HtmlEncode(planId);

            sb.Append($"<div id='fix-card-{planIdEnc}' class='fix-plan' dir='rtl' style='text-align:right; font-family:\"Segoe UI\",Arial,sans-serif;'>");

            // Header
            sb.Append("<h3 style='margin:8px 0; color:#1e40af;'>&#x1f527; תוכנית תיקונים</h3>");

            // Legend — blue values are editable
            sb.Append("<p style='margin:4px 0; font-size:11px; color:#4a90e2; font-weight:600;'>ערכים כחולים ניתנים לעריכה לפני אישור.</p>");

            // Summary
            if (!string.IsNullOrWhiteSpace(plan.Summary))
            {
                sb.Append($"<p style='margin:4px 0; color:#374151;'>{WebUtility.HtmlEncode(plan.Summary)}</p>");
            }

            // Items table
            if (plan.Items != null && plan.Items.Count > 0)
            {
                sb.Append(@"<table style='width:100%; border-collapse:collapse; margin:8px 0; font-size:13px;'>
                    <tr style='background:#e5e7eb;'>
                        <th style='padding:6px 8px; text-align:center; border:1px solid #d1d5db; width:30px;'><input type='checkbox' id='fix-select-all' checked onchange='toggleAllFix(this)' /></th>
                        <th style='padding:6px 8px; text-align:right; border:1px solid #d1d5db;'>אובייקט</th>
                        <th style='padding:6px 8px; text-align:right; border:1px solid #d1d5db;'>מיקום</th>
                        <th style='padding:6px 8px; text-align:right; border:1px solid #d1d5db;'>תיאור</th>
                        <th style='padding:6px 8px; text-align:right; border:1px solid #d1d5db;'>ערך נוכחי</th>
                        <th style='padding:6px 8px; text-align:right; border:1px solid #d1d5db;'>ערך מוצע</th>
                        <th style='padding:6px 8px; text-align:right; border:1px solid #d1d5db;'>חומרה</th>
                    </tr>");

                foreach (var item in plan.Items)
                {
                    string severityColor = GetSeverityColor(item.Severity);
                    string severityLabel = GetSeverityLabel(item.Severity);
                    string currentDisplay = FormatJsonElement(item.CurrentValue);
                    string proposedCell = RenderEditableProposedCell(item);
                    string locationCell = RenderLocationCell(
                        item.Location, item.ObjectName, item.Description,
                        item.ToolParams, item.ObjectType);

                    sb.Append($@"<tr>
                        <td style='padding:6px 8px; border:1px solid #d1d5db; text-align:center;'><input type='checkbox' class='fix-item-cb' data-id='{WebUtility.HtmlEncode(item.Id)}' checked /></td>
                        <td style='padding:6px 8px; border:1px solid #d1d5db;'>{WebUtility.HtmlEncode(item.ObjectName ?? "")}</td>
                        <td style='padding:6px 8px; border:1px solid #d1d5db;'>{locationCell}</td>
                        <td style='padding:6px 8px; border:1px solid #d1d5db;'>{WebUtility.HtmlEncode(item.Description ?? "")}</td>
                        <td style='padding:6px 8px; border:1px solid #d1d5db; direction:ltr; text-align:center; white-space:nowrap;'>{WebUtility.HtmlEncode(currentDisplay)}</td>
                        <td style='padding:6px 8px; border:1px solid #d1d5db; direction:ltr; text-align:center; white-space:nowrap;'>{proposedCell}</td>
                        <td style='padding:6px 8px; border:1px solid #d1d5db; text-align:center; white-space:nowrap;'><span style='display:inline-block; white-space:nowrap; background:{severityColor}; color:white; padding:2px 8px; border-radius:10px; font-size:11px;'>{severityLabel}</span></td>
                    </tr>");
                }

                sb.Append("</table>");
            }
            else
            {
                sb.Append("<p style='color:#6b7280;'>לא נמצאו פריטים לתיקון.</p>");
            }

            // Skipped violations — itemized Hebrew reasons (collapsible).
            // Without this the engineer sees a plan smaller than the analysis
            // report with no explanation (2026-06 Stage 2 review, Finding 4).
            sb.Append(RenderSkippedReasons(plan.SkippedReasons));

            // RAG references
            if (plan.RagReferences != null && plan.RagReferences.Count > 0)
            {
                sb.Append("<p style='font-size:11px; color:#6b7280; margin:4px 0;'>תקנים שנבדקו: ");
                sb.Append(WebUtility.HtmlEncode(string.Join(", ", plan.RagReferences)));
                sb.Append("</p>");
            }

            // Approval buttons
            sb.Append(@"<div style='margin:12px 0; display:flex; gap:8px; justify-content:flex-end;'>
                <button onclick=""sendFixApproval('accept')"" style='background:#16a34a; color:white; border:none; padding:8px 20px; border-radius:6px; cursor:pointer; font-size:14px; font-weight:bold;'>&#x2705; אישור</button>
                <button onclick=""sendFixApproval('decline')"" style='background:#dc2626; color:white; border:none; padding:8px 20px; border-radius:6px; cursor:pointer; font-size:14px;'>&#x274C; ביטול</button>
            </div>");

            // JavaScript handlers — scoped to this card's plan id via IIFE
            sb.Append("<script>");
            sb.Append("(function(){");
            sb.Append($"var _planId = {JsonEncode(planId)};");
            sb.Append(@"
function toggleAllFix(el) {
    var cbs = document.querySelectorAll('.fix-item-cb');
    for (var i = 0; i < cbs.length; i++) cbs[i].checked = el.checked;
}
function _readInputValue(inp) {
    if (!inp) return null;
    if (inp.tagName === 'SELECT') return inp.value;
    if (inp.type === 'checkbox') return inp.checked;
    if (inp.type === 'number') {
        if (inp.value === '') return null;
        var n = parseFloat(inp.value);
        return isNaN(n) ? inp.value : n;
    }
    return inp.value;
}
function _captureCurrentEdits() {
    var edits = {};
    var inputs = document.querySelectorAll('.fix-item-edit');
    for (var i = 0; i < inputs.length; i++) {
        var el = inputs[i];
        var itemId = el.getAttribute('data-item-id');
        var param = el.getAttribute('data-param-name');
        if (!itemId || !param) continue;
        var initial = el.getAttribute('data-initial-value');
        var current = _readInputValue(el);
        var currentStr = current === null ? '' : String(current);
        if (currentStr === (initial === null ? '' : initial)) continue;
        if (!edits[itemId]) edits[itemId] = { item_id: itemId, tool_params: {} };
        edits[itemId].tool_params[param] = current;
    }
    var arr = [];
    for (var k in edits) { if (edits.hasOwnProperty(k)) arr.push(edits[k]); }
    return arr;
}
window.__currentFixEdits = window.__currentFixEdits || {};
document.addEventListener('input', function(ev) {
    if (ev.target && ev.target.classList && ev.target.classList.contains('fix-item-edit')) {
        window.__currentFixEdits[_planId] = _captureCurrentEdits();
    }
}, true);
function sendFixApproval(decision) {
    var ids = [];
    var cbs = document.querySelectorAll('.fix-item-cb');
    for (var i = 0; i < cbs.length; i++) {
        if (cbs[i].checked) ids.push(cbs[i].getAttribute('data-id'));
    }
    var edited = _captureCurrentEdits();
    window.chrome.webview.postMessage(JSON.stringify({
        action: 'fix_approval',
        plan_id: _planId,
        decision: decision,
        selected_item_ids: ids,
        edited_items: edited
    }));
}
window.toggleAllFix = toggleAllFix;
window.sendFixApproval = sendFixApproval;
");
            sb.Append("})();");
            sb.Append("</script>");

            sb.Append("</div>");

            return sb.ToString();
        }

        /// <summary>
        /// Renders execution results only (no verification data yet).
        /// </summary>
        public static string RenderResult(WebSocket.FixResultPayload result)
        {
            return RenderCombinedResult(result, null);
        }

        /// <summary>
        /// Renders execution + verification results as a combined card.
        /// The outer element uses <c>id='fix-card-{planId}'</c> so the caller can
        /// string-replace the previously rendered plan card inside the conversation HTML.
        /// </summary>
        public static string RenderCombinedResult(
            WebSocket.FixResultPayload result,
            WebSocket.FixVerifyResultPayload? verify)
            => RenderCombinedResult(result, verify, null);

        /// <summary>
        /// Overload that also renders a "post-fix re-validation" section
        /// summarising resolved / still-open / newly introduced violations
        /// per entity. Called after the agent's s5_post_fix_validation step.
        /// </summary>
        public static string RenderCombinedResult(
            WebSocket.FixResultPayload result,
            WebSocket.FixVerifyResultPayload? verify,
            WebSocket.PostFixValidationPayload? postFix)
        {
            var sb = new StringBuilder();
            var planId = result.PlanId ?? verify?.PlanId ?? "";
            var planIdEnc = WebUtility.HtmlEncode(planId);

            sb.Append($"<div id='fix-card-{planIdEnc}' class='fix-result' dir='rtl' style='text-align:right; font-family:\"Segoe UI\",Arial,sans-serif;'>");

            // Header — overall success tint
            string headerIcon = result.Success ? "&#x2705;" : "&#x26A0;&#xFE0F;";
            string headerColor = result.Success ? "#16a34a" : "#dc2626";
            string headerTitle = verify != null ? "תוצאות ואימות תיקונים" : "תוצאות התיקונים";
            sb.Append($"<h3 style='margin:8px 0; color:{headerColor};'>{headerIcon} {headerTitle}</h3>");

            // Build verify lookup (needed by BOTH the summary banner and the per-item table
            // so their counts and rows are computed from the same verification data).
            Dictionary<string, WebSocket.FixVerifyItem>? verifyLookup = null;
            if (verify?.Items != null)
            {
                verifyLookup = new Dictionary<string, WebSocket.FixVerifyItem>();
                foreach (var v in verify.Items)
                {
                    if (!string.IsNullOrEmpty(v.ItemId))
                        verifyLookup[v.ItemId] = v;
                }
            }

            // Execution summary — ONE authoritative, deterministic banner computed from the
            // exact per-item rows rendered below (Services.FixResultClassifier + verify data).
            // Every count here matches the ✔ / ⚠ / ❌ rows in the table, so the banner, table,
            // and on-drawing pins can never disagree. When there are no result rows, fall back
            // to the server's message (covers "no items selected").
            if (result.Results != null && result.Results.Count > 0)
            {
                sb.Append(RenderExecutionSummary(result.Results, verifyLookup));
            }
            else if (!string.IsNullOrWhiteSpace(result.Summary))
            {
                sb.Append($"<p style='margin:4px 0; color:#374151;'>{WebUtility.HtmlEncode(result.Summary)}</p>");
            }

            if (result.Results != null && result.Results.Count > 0)
            {
                sb.Append(@"<table style='width:100%; border-collapse:collapse; margin:8px 0; font-size:13px;'>
                    <tr style='background:#e5e7eb;'>
                        <th style='padding:6px 8px; text-align:right; border:1px solid #d1d5db;'>אובייקט</th>
                        <th style='padding:6px 8px; text-align:right; border:1px solid #d1d5db;'>מיקום</th>
                        <th style='padding:6px 8px; text-align:right; border:1px solid #d1d5db;'>תיאור</th>
                        <th style='padding:6px 8px; text-align:center; border:1px solid #d1d5db;'>לפני</th>
                        <th style='padding:6px 8px; text-align:center; border:1px solid #d1d5db;'>אחרי</th>
                        <th style='padding:6px 8px; text-align:center; border:1px solid #d1d5db;'>תוצאה</th>
                        <th style='padding:6px 8px; text-align:center; border:1px solid #d1d5db;'>אומת</th>
                    </tr>");

                foreach (var item in result.Results)
                {
                    // Bucket via the shared classifier so these ✔/⚠/✖ rows and the summary
                    // banner above can never report different counts.
                    var outcome = Services.FixResultClassifier.Classify(item);

                    // Verification says the applied value is NOT what was asked for — don't
                    // paint that row a clean green "הצליח" next to a red "אומת" cell. Same
                    // predicate the summary banner uses, so the row and the count agree.
                    bool verifyContradicts = VerifyContradicts(verifyLookup, item.ItemId);

                    string execIcon;
                    string execCellDetail;
                    if (outcome == Services.FixOutcome.Applied && verifyContradicts)
                    {
                        execIcon = "&#x26A0;&#xFE0F;"; // ⚠️
                        execCellDetail = "בוצע — האימות נכשל";
                    }
                    else if (outcome == Services.FixOutcome.Applied)
                    {
                        execIcon = "&#x2705;";
                        execCellDetail = "הצליח";
                    }
                    else if (outcome == Services.FixOutcome.Manual)
                    {
                        execIcon = "&#x26A0;&#xFE0F;"; // ⚠️
                        execCellDetail = "נדלג — אינו ניתן לתיקון אוטומטי";
                    }
                    else
                    {
                        execIcon = "&#x274C;";
                        execCellDetail = WebUtility.HtmlEncode(item.ErrorMessage ?? "נכשל");
                    }

                    string prevValue = !string.IsNullOrEmpty(item.PreviousValue)
                        ? WebUtility.HtmlEncode(item.PreviousValue)
                        : "—";
                    string newValue = !string.IsNullOrEmpty(item.NewValue)
                        ? WebUtility.HtmlEncode(item.NewValue)
                        : "—";

                    string verifyCell = RenderVerifyCell(verifyLookup, item.ItemId);

                    string resultLocationCell = RenderLocationCell(
                        item.Location, item.ObjectName, item.Description, null);

                    sb.Append($@"<tr>
                        <td style='padding:6px 8px; border:1px solid #d1d5db;'>{WebUtility.HtmlEncode(GetObjectNameForItem(item, verifyLookup))}</td>
                        <td style='padding:6px 8px; border:1px solid #d1d5db;'>{resultLocationCell}</td>
                        <td style='padding:6px 8px; border:1px solid #d1d5db;'>{WebUtility.HtmlEncode(item.Description ?? "")}</td>
                        <td style='padding:6px 8px; border:1px solid #d1d5db; text-align:center;'>{prevValue}</td>
                        <td style='padding:6px 8px; border:1px solid #d1d5db; text-align:center;'>{newValue}</td>
                        <td style='padding:6px 8px; border:1px solid #d1d5db; text-align:center; direction:rtl;'>{execIcon} <span style='font-size:11px;'>{execCellDetail}</span></td>
                        <td style='padding:6px 8px; border:1px solid #d1d5db; text-align:center;'>{verifyCell}</td>
                    </tr>");
                }

                sb.Append("</table>");
            }

            // NOTE: neither the verify step's free-text Hebrew summary (verify.SummaryText)
            // nor the post-fix re-validation box (postFix: נפתרו/פתוחות/חדשות) is rendered.
            // Both restated the counts on a different axis and disagreed with the deterministic
            // execution banner + per-item table (e.g. "נפתרו 10" vs "9 בוצעו"), which is exactly
            // the inconsistency the engineer reported. The single execution summary above plus
            // the per-item "אומת" column are now the only count sources — and they always agree.
            _ = postFix;

            // Undo All button. The undo count must come from the server-reported
            // applied_count (items with status=='applied' only). When the agent
            // didn't report it (older agents) or nothing was applied, GUESSING a
            // count would undo unrelated user operations — render the button
            // disabled with a Hebrew tooltip instead.
            sb.Append(RenderUndoButton(planIdEnc, result.AppliedCount));

            sb.Append("</div>");

            return sb.ToString();
        }

        /// <summary>
        /// Renders the single authoritative execution-summary banner from the per-item results,
        /// via <see cref="Services.FixResultClassifier"/>. Every count here matches the
        /// ✔ / ⚠ / ❌ rows in the table below exactly — this is the one place the engineer should
        /// read the totals. When verification data is present, an applied row whose read-back
        /// value is wrong is split out of "בוצעו" into its own "בוצע אך האימות נכשל" count, so
        /// the banner never claims more clean successes than the table shows. Internal for tests.
        /// </summary>
        internal static string RenderExecutionSummary(
            List<WebSocket.FixResultItem> results,
            Dictionary<string, WebSocket.FixVerifyItem>? verifyLookup = null)
        {
            int applied = 0, verifyFailed = 0, manual = 0, failed = 0;
            foreach (var item in results)
            {
                switch (Services.FixResultClassifier.Classify(item))
                {
                    case Services.FixOutcome.Applied:
                        if (VerifyContradicts(verifyLookup, item.ItemId)) verifyFailed++;
                        else applied++;
                        break;
                    case Services.FixOutcome.Manual: manual++; break;
                    default: failed++; break;
                }
            }
            int total = applied + verifyFailed + manual + failed;
            bool bad = failed > 0 || verifyFailed > 0;

            string tint = bad ? "#dc2626" : (manual > 0 ? "#d97706" : "#16a34a");
            string bg = bad ? "#fef2f2" : (manual > 0 ? "#fffbeb" : "#f0fdf4");

            var sb = new StringBuilder();
            sb.Append($"<div style='margin:8px 0; padding:10px 14px; background:{bg}; border-right:4px solid {tint}; border-radius:6px; font-size:13px; line-height:1.7;'>");
            sb.Append($"<div style='font-weight:700; color:{tint}; margin-bottom:4px;'>סיכום ביצוע</div>");
            sb.Append($"<div style='color:#374151;'>מתוך {total} תיקונים: ");
            sb.Append($"<b style='color:#16a34a;'>&#x2705; {applied} בוצעו</b>");
            if (verifyFailed > 0)
                sb.Append($" &nbsp;&#183;&nbsp; <b style='color:#dc2626;'>&#x26A0;&#xFE0F; {verifyFailed} בוצע אך האימות נכשל</b>");
            sb.Append($" &nbsp;&#183;&nbsp; <b style='color:#b45309;'>&#x26A0;&#xFE0F; {manual} דורשים טיפול ידני</b>");
            sb.Append($" &nbsp;&#183;&nbsp; <b style='color:#dc2626;'>&#x274C; {failed} נכשלו</b>");
            sb.Append("</div></div>");
            return sb.ToString();
        }

        /// <summary>True when verification read the applied value back as wrong for this item.</summary>
        private static bool VerifyContradicts(
            Dictionary<string, WebSocket.FixVerifyItem>? verifyLookup, string? itemId)
            => verifyLookup != null
               && !string.IsNullOrEmpty(itemId)
               && verifyLookup.TryGetValue(itemId, out var v)
               && v.Verified == false;

        /// <summary>
        /// Renders the Undo button for the fix result card. Enabled only when the
        /// agent reported a positive <c>applied_count</c>; otherwise disabled with
        /// a Hebrew tooltip explaining why. Internal for unit tests.
        /// </summary>
        internal static string RenderUndoButton(string planIdEnc, int? appliedCount)
        {
            if (appliedCount == null)
            {
                return $@"<div style='margin:10px 0; text-align:center;'>
                <button disabled title='ביטול אוטומטי אינו זמין — מספר השינויים שבוצעו לא דווח על ידי השרת. ניתן לבטל ידנית בפקודת UNDO.'
                    style='padding:8px 20px; background:#f3f4f6; border:2px solid #9ca3af; border-radius:6px; color:#9ca3af; font-size:13px; font-weight:600; cursor:not-allowed; font-family:inherit;'>
                    &#x21A9; ביטול כל השינויים (Undo)
                </button>
            </div>";
            }

            if (appliedCount.Value < 1)
            {
                return $@"<div style='margin:10px 0; text-align:center;'>
                <button disabled title='לא בוצעו שינויים בציור — אין מה לבטל.'
                    style='padding:8px 20px; background:#f3f4f6; border:2px solid #9ca3af; border-radius:6px; color:#9ca3af; font-size:13px; font-weight:600; cursor:not-allowed; font-family:inherit;'>
                    &#x21A9; ביטול כל השינויים (Undo)
                </button>
            </div>";
            }

            return $@"<div style='margin:10px 0; text-align:center;'>
                <button onclick=""window.chrome.webview.postMessage(JSON.stringify({{type:'undo_all',plan_id:'{planIdEnc}'}}))""
                    title='יבטל {appliedCount.Value} שינויים שבוצעו בציור'
                    style='padding:8px 20px; background:#fff; border:2px solid #dc2626; border-radius:6px; color:#dc2626; font-size:13px; font-weight:600; cursor:pointer; font-family:inherit;'
                    onmouseover=""this.style.background='#fef2f2'""
                    onmouseout=""this.style.background='#fff'"">
                    &#x21A9; ביטול כל השינויים (Undo) — {appliedCount.Value}
                </button>
            </div>";
        }

        /// <summary>
        /// Renders the itemized skipped-violation reasons as a collapsible Hebrew
        /// list (one line per violation: entity / parameter: reason). Returns an
        /// empty string when there is nothing to show. Internal for unit tests.
        /// </summary>
        internal static string RenderSkippedReasons(List<string>? skippedReasons)
        {
            if (skippedReasons == null || skippedReasons.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.Append("<details class='fix-skipped-reasons' style='margin:8px 0; padding:8px 12px; background:#fffbeb; border-right:4px solid #d97706; border-radius:4px; font-size:12px;'>");
            sb.Append($"<summary style='cursor:pointer; color:#92400e; font-weight:600;'>&#x26A0;&#xFE0F; ממצאים שלא נכללו בתוכנית התיקונים ({skippedReasons.Count})</summary>");
            sb.Append("<ul style='margin:6px 18px 0 0; padding:0; color:#78350f;'>");
            foreach (var reason in skippedReasons)
            {
                if (string.IsNullOrWhiteSpace(reason)) continue;
                sb.Append($"<li style='margin:2px 0;'>{WebUtility.HtmlEncode(reason)}</li>");
            }
            sb.Append("</ul>");
            sb.Append("<p style='margin:6px 0 0; font-size:11px; color:#92400e;'>ממצאים אלה דורשים טיפול ידני או שאינם ניתנים להערכה אוטומטית.</p>");
            sb.Append("</details>");
            return sb.ToString();
        }

        /// <summary>
        /// Extracts the live edited values map previously captured by the plan card's
        /// <c>window.__currentFixEdits</c> store. Used by NewChatControl when switching tabs.
        /// </summary>
        public static string BuildCaptureEditsScript()
        {
            return "JSON.stringify(window.__currentFixEdits || {})";
        }

        /// <summary>
        /// Hebrew labels for placeholder index fields the agent marks as
        /// requiring engineer input (the proposer puts them in
        /// <c>editable_fields</c> only when the index could not be resolved
        /// from the analysis — placeholder value 0 must NOT be sent as-is).
        /// </summary>
        private static readonly Dictionary<string, string> _placeholderFieldLabels = new()
        {
            ["element_index"] = "אינדקס אלמנט",
            ["pvi_index"] = "אינדקס PVI",
        };

        internal static bool IsPlaceholderIndexField(string paramName)
            => _placeholderFieldLabels.ContainsKey(paramName);

        private static string RenderEditableProposedCell(WebSocket.FixPlanItem item)
        {
            // If the agent did not mark any field as editable we render a simple
            // bold read-only value so legacy plans still display.
            if (item.EditableFields == null || item.EditableFields.Count == 0)
            {
                var proposedDisplay = FormatJsonElement(item.ProposedValue);
                return $"<span style='font-weight:bold; color:#1e40af;'>{WebUtility.HtmlEncode(proposedDisplay)}</span>";
            }

            // Order: primary value fields first, then placeholder index fields
            // (element_index / pvi_index). The agent includes an index field in
            // editable_fields ONLY when the engineer must fill it before
            // approving — hiding it (the old behavior) made those plan rows
            // silently send placeholder index 0 to the plugin.
            var primaryFields = new List<KeyValuePair<string, WebSocket.FieldMeta>>();
            var placeholderFields = new List<KeyValuePair<string, WebSocket.FieldMeta>>();
            foreach (var kvp in item.EditableFields)
            {
                if (IsPlaceholderIndexField(kvp.Key))
                    placeholderFields.Add(kvp);
                else
                    primaryFields.Add(kvp);
            }

            var sb = new StringBuilder();
            sb.Append("<div style='display:flex; flex-direction:column; gap:4px; align-items:flex-end;'>");

            foreach (var kvp in primaryFields)
            {
                var paramName = kvp.Key;
                var meta = kvp.Value ?? new WebSocket.FieldMeta();
                string initialValue = ResolveInitialValue(item, paramName);
                string unit = meta.Unit ?? "";

                // Some fixes (e.g. a max-grade violation → modify_profile_grade) deliberately
                // leave the target value blank for the engineer to type. A bare empty box reads
                // as broken, so give it the SAME amber "engineer must enter" affordance as the
                // index placeholders — clearly intentional, with a hint.
                bool needsEngineerValue = string.IsNullOrWhiteSpace(initialValue);
                if (needsEngineerValue)
                {
                    sb.Append("<div style='display:flex; align-items:center; gap:4px; direction:rtl; background:#fffbeb; border:1px solid #d97706; border-radius:4px; padding:2px 6px;' title='דרוש קלט מהנדס — ערך היעד לא חושב אוטומטית'>");
                    sb.Append("<span style='font-size:11px; color:#92400e; font-weight:600;'>&#x26A0;&#xFE0F; הזן ערך:</span>");
                    sb.Append("<span style='direction:ltr;'>");
                    sb.Append(RenderSingleEditableInput(item.Id, paramName, meta, initialValue));
                    sb.Append("</span>");
                    if (!string.IsNullOrEmpty(unit))
                    {
                        sb.Append($"<span style='font-size:11px; color:#92400e;'>{WebUtility.HtmlEncode(unit)}</span>");
                    }
                    sb.Append("</div>");
                    continue;
                }

                sb.Append("<div style='display:flex; align-items:center; gap:4px; direction:ltr;'>");
                sb.Append(RenderSingleEditableInput(item.Id, paramName, meta, initialValue));
                if (!string.IsNullOrEmpty(unit))
                {
                    sb.Append($"<span style='font-size:11px; color:#6b7280;'>{WebUtility.HtmlEncode(unit)}</span>");
                }
                sb.Append("</div>");
            }

            foreach (var kvp in placeholderFields)
            {
                var paramName = kvp.Key;
                var meta = kvp.Value ?? new WebSocket.FieldMeta();
                string initialValue = ResolveInitialValue(item, paramName);
                string label = _placeholderFieldLabels[paramName];
                // Amber warning treatment: the engineer MUST replace the
                // placeholder before approving this row.
                sb.Append("<div style='display:flex; align-items:center; gap:4px; direction:rtl; background:#fffbeb; border:1px solid #d97706; border-radius:4px; padding:2px 6px;' title='דרוש קלט מהנדס — ערך זה לא זוהה אוטומטית'>");
                sb.Append($"<span style='font-size:11px; color:#92400e; font-weight:600;'>&#x26A0;&#xFE0F; {WebUtility.HtmlEncode(label)}:</span>");
                sb.Append("<span style='direction:ltr;'>");
                sb.Append(RenderSingleEditableInput(item.Id, paramName, meta, initialValue));
                sb.Append("</span>");
                sb.Append("</div>");
            }

            sb.Append("</div>");
            return sb.ToString();
        }

        private static string RenderSingleEditableInput(
            string itemId,
            string paramName,
            WebSocket.FieldMeta meta,
            string initialValue)
        {
            var itemIdEnc = WebUtility.HtmlEncode(itemId);
            var paramEnc = WebUtility.HtmlEncode(paramName);
            var initialAttr = WebUtility.HtmlEncode(initialValue);
            var unitAttr = WebUtility.HtmlEncode(meta.Unit ?? "");

            const string baseStyle =
                "border:1px solid #4a90e2; padding:4px; width:74px; min-width:64px; text-align:right; direction:rtl; border-radius:3px; font-family:inherit; font-size:13px; color:#1e40af; font-weight:600; background:#fff;";

            // Select (options) takes precedence when defined
            if (meta.Options != null && meta.Options.Count > 0)
            {
                var sb = new StringBuilder();
                sb.Append($"<select class='fix-item-edit' data-item-id='{itemIdEnc}' data-param-name='{paramEnc}' data-unit='{unitAttr}' data-initial-value='{initialAttr}' style='{baseStyle}'>");
                foreach (var opt in meta.Options)
                {
                    var optEnc = WebUtility.HtmlEncode(opt);
                    var selected = string.Equals(opt, initialValue, StringComparison.Ordinal) ? " selected" : "";
                    sb.Append($"<option value='{optEnc}'{selected}>{optEnc}</option>");
                }
                sb.Append("</select>");
                return sb.ToString();
            }

            string typeAttr;
            string extraAttrs = "";
            switch ((meta.Type ?? "string").ToLowerInvariant())
            {
                case "number":
                    typeAttr = "number";
                    if (meta.Min.HasValue) extraAttrs += $" min='{meta.Min.Value}'";
                    if (meta.Max.HasValue) extraAttrs += $" max='{meta.Max.Value}'";
                    extraAttrs += " step='any'";
                    break;
                case "boolean":
                    typeAttr = "checkbox";
                    extraAttrs = string.Equals(initialValue, "true", StringComparison.OrdinalIgnoreCase) ? " checked" : "";
                    return $"<input type='checkbox' class='fix-item-edit' data-item-id='{itemIdEnc}' data-param-name='{paramEnc}' data-unit='{unitAttr}' data-initial-value='{initialAttr}'{extraAttrs} />";
                default:
                    typeAttr = "text";
                    break;
            }

            return $"<input type='{typeAttr}' class='fix-item-edit' data-item-id='{itemIdEnc}' data-param-name='{paramEnc}' data-unit='{unitAttr}' data-initial-value='{initialAttr}' value='{initialAttr}'{extraAttrs} style='{baseStyle}' />";
        }

        private static string ResolveInitialValue(WebSocket.FixPlanItem item, string paramName)
        {
            // Prefer the explicit tool_params entry for this key
            if (item.ToolParams.HasValue && item.ToolParams.Value.ValueKind == JsonValueKind.Object)
            {
                if (item.ToolParams.Value.TryGetProperty(paramName, out var prop))
                {
                    return JsonElementToString(prop);
                }
            }

            // Fall back to proposed_value (useful when a single-field item has no tool_params map)
            if (item.ProposedValue.HasValue)
            {
                return JsonElementToString(item.ProposedValue.Value);
            }
            return string.Empty;
        }

        private static string JsonElementToString(JsonElement el)
        {
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString() ?? "",
                JsonValueKind.Number => el.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "",
                JsonValueKind.Undefined => "",
                _ => el.GetRawText(),
            };
        }

        private static string FormatJsonElement(JsonElement? el)
        {
            if (!el.HasValue) return "-";
            var v = el.Value;
            if (v.ValueKind == JsonValueKind.Null || v.ValueKind == JsonValueKind.Undefined)
                return "-";
            var s = JsonElementToString(v);
            return string.IsNullOrWhiteSpace(s) ? "-" : s;
        }

        private static string RenderVerifyCell(
            Dictionary<string, WebSocket.FixVerifyItem>? lookup,
            string itemId)
        {
            if (lookup == null || string.IsNullOrEmpty(itemId) || !lookup.TryGetValue(itemId, out var v))
            {
                return "<span style='color:#9ca3af;'>&mdash;</span>";
            }

            string icon;
            string color;
            if (v.Verified == true) { icon = "&#x2705;"; color = "#16a34a"; }
            else if (v.Verified == false) { icon = "&#x274C;"; color = "#dc2626"; }
            else { icon = "&mdash;"; color = "#9ca3af"; }

            var actual = FormatJsonElement(v.ActualValue);
            var expected = FormatJsonElement(v.ExpectedValue);
            string detail;
            if (v.Verified == true)
            {
                detail = $"בפועל: {WebUtility.HtmlEncode(actual)}";
            }
            else if (v.Verified == false)
            {
                detail = $"בפועל {WebUtility.HtmlEncode(actual)} / צפוי {WebUtility.HtmlEncode(expected)}";
            }
            else
            {
                detail = WebUtility.HtmlEncode(v.Note ?? "לא אומת");
            }

            return $"<span style='color:{color};'>{icon}</span> <span style='font-size:11px; color:#374151;'>{detail}</span>";
        }

        private static string GetObjectNameForItem(
            WebSocket.FixResultItem item,
            Dictionary<string, WebSocket.FixVerifyItem>? verifyLookup)
        {
            // The agent carries object_name through from the plan item onto every
            // result row (schemas/fix.py::FixResultItem.object_name). Older agents
            // omit it — fall back to "—" rather than an empty cell.
            _ = verifyLookup;
            return string.IsNullOrWhiteSpace(item.ObjectName) ? "—" : item.ObjectName;
        }

        /// <summary>
        /// Renders the "מיקום" cell for a fix row as the SAME clickable 📍 link the
        /// analysis findings table uses: a <c>mahod-loc-link</c> span carrying
        /// <c>data-align</c> / <c>data-s1</c> / <c>data-s2</c> / <c>data-prob</c> that
        /// posts <c>zoom_to_location</c> to the host, which zooms the viewport and
        /// opens the violation's on-drawing pin (<c>ProblemOverlayService.FocusMarker</c>).
        /// <para>
        /// <paramref name="location"/> is the analysis row's verbatim Hebrew location
        /// text. When it is missing (older agent) or not parseable, we rebuild
        /// <c>"ציר {objectName} — תחנה {K+SSS}"</c> from <c>tool_params.target_station</c>
        /// — the same station <see cref="Services.Overlay.MarkerFixMatcher"/> matches
        /// pins on — so the link still lands on the right pin. Falls back to plain text
        /// (or "—") when neither is available. Internal for unit tests.
        /// </para>
        /// </summary>
        internal static string RenderLocationCell(
            string? location,
            string? objectName,
            string? description,
            JsonElement? toolParams,
            string? objectType = null)
        {
            string text = (location ?? string.Empty).Trim();

            if (!Services.LocationParser.TryParse(text, out string align, out double s1, out double? s2))
            {
                // No parseable location text — synthesize one from the fix's own
                // target station so the row is still clickable.
                double? station = ReadTargetStation(toolParams);
                if (station == null || string.IsNullOrWhiteSpace(objectName))
                {
                    return string.IsNullOrEmpty(text)
                        ? "<span style='color:#9ca3af;'>&mdash;</span>"
                        : WebUtility.HtmlEncode(text);
                }
                align = objectName!.Trim();
                s1 = station.Value;
                s2 = null;
                if (string.IsNullOrEmpty(text))
                {
                    // "ציר" means ALIGNMENT — only an alignment may carry that prefix.
                    // A profile / corridor / pipe-network row prints its own name so
                    // the cell never mislabels the object type.
                    bool isAlignment = string.Equals(
                        objectType?.Trim(), "alignment", StringComparison.OrdinalIgnoreCase);
                    text = isAlignment
                        ? $"ציר {align} — תחנה {FormatStationHe(s1)}"
                        : $"{align} — תחנה {FormatStationHe(s1)}";
                }
            }

            string alignAttr = WebUtility.HtmlEncode(align);
            string s1Attr = s1.ToString(CultureInfo.InvariantCulture);
            string s2Attr = s2.HasValue ? s2.Value.ToString(CultureInfo.InvariantCulture) : "";
            // The description disambiguates which pin to open when one curve carries
            // several violations (FocusMarker scores pins by problem-text overlap).
            string probAttr = WebUtility.HtmlEncode((description ?? string.Empty).Trim());

            return $"<span class='mahod-loc-link' data-align=\"{alignAttr}\" " +
                   $"data-s1=\"{s1Attr}\" data-s2=\"{s2Attr}\" data-prob=\"{probAttr}\" " +
                   "onclick=\"window.mahodZoomTo &amp;&amp; window.mahodZoomTo(this)\" " +
                   "title=\"לחץ למיקוד המקטע בשרטוט\" " +
                   "style=\"cursor:pointer; color:#2e9535; text-decoration:underline;\">" +
                   $"📍 {WebUtility.HtmlEncode(text)}</span>";
        }

        /// <summary>Reads <c>tool_params.target_station</c> (meters) when present and numeric.</summary>
        private static double? ReadTargetStation(JsonElement? toolParams)
        {
            if (toolParams is not JsonElement p || p.ValueKind != JsonValueKind.Object)
                return null;
            if (p.TryGetProperty("target_station", out var ts) && ts.ValueKind == JsonValueKind.Number)
                return ts.GetDouble();
            if (p.TryGetProperty("station", out var st) && st.ValueKind == JsonValueKind.Number)
                return st.GetDouble();
            return null;
        }

        /// <summary>
        /// Formats meters as the Civil 3D K+SSS station string the analysis report uses
        /// (e.g. 374.13 → <c>"0+374.13"</c>), so a synthesized location reads exactly
        /// like one written by the agent.
        /// </summary>
        internal static string FormatStationHe(double meters)
        {
            bool negative = meters < 0;
            double abs = Math.Abs(meters);
            int km = (int)(abs / 1000.0);
            double rem = abs - (km * 1000.0);
            // Guard the 999.995 → "1000.00" rounding carry.
            if (Math.Round(rem, 2) >= 1000.0)
            {
                km += 1;
                rem = 0.0;
            }
            string s = $"{km}+{rem.ToString("000.00", CultureInfo.InvariantCulture)}";
            return negative ? "-" + s : s;
        }

        private static string JsonEncode(string value)
        {
            return JsonSerializer.Serialize(value, _jsOpts);
        }

        private static string GetSeverityColor(string? severity)
        {
            return severity switch
            {
                "critical" => "#dc2626",
                "important" => "#f59e0b",
                "note" => "#6b7280",
                _ => "#6b7280",
            };
        }

        private static string GetSeverityLabel(string? severity)
        {
            return severity switch
            {
                "critical" => "קריטי",
                "important" => "חשוב",
                "note" => "הערה",
                _ => severity ?? "",
            };
        }
    }
}
