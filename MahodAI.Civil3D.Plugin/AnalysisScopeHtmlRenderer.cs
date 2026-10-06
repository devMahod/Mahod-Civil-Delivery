using System.Collections.Generic;
using System.Net;
using System.Text;

namespace MahodAI.Civil3D.Plugin
{
    /// <summary>
    /// Renders inline scope selection card as RTL Hebrew HTML for WebView2 display.
    /// Replaces the WPF AnalysisScopeDialog with an interactive HTML card in the chat.
    /// </summary>
    public static class AnalysisScopeHtmlRenderer
    {
        private static readonly (string Value, string Label)[] RoadTypes = new[]
        {
            ("", "לא צוין"),
            ("interurban", "דרך בינעירונית"),
            ("urban", "דרך עירונית"),
            ("agricultural", "דרך חקלאית"),
            ("general", "דרך כללית"),
        };

        // Cross-section options (added 2026-05-04 per engineer feedback).
        // Keys must match CROSS_SECTION_TYPES in ai_agent/src/data/standards_israel.py.
        private static readonly (string Value, string Label)[] CrossSectionTypes = new[]
        {
            ("", "לא צוין"),
            ("single_carriageway", "חד-מסלולית"),
            ("dual_carriageway_2lane", "דו-מסלולית, 2 נתיבים"),
            ("dual_carriageway_4lane", "דו-מסלולית, 3+ נתיבים"),
            ("urban_arterial", "עורק עירוני"),
        };

        // Classification options per road type (interurban/urban only). Pending Itamar's final list.
        private static readonly Dictionary<string, (string Value, string Label)[]> ClassificationsByRoadType = new()
        {
            ["interurban"] = new[]
            {
                ("highway", "דרך מהירה"),
                ("primary", "דרך ראשית"),
                ("regional", "דרך אזורית"),
                ("local", "דרך מקומית"),
                ("interchange_ramp", "רמפות במחלף"),
            },
            ["urban"] = new[]
            {
                ("fast_arterial", "דרך עורקית מהירה"),
                ("longitudinal", "דרך אורכית"),
                ("collector_l1", "דרך מאספת רמה 1"),
                ("collector_l2", "דרך מאספת רמה 2"),
                ("local", "דרך מקומית"),
            },
        };

        // Project-wide topography options. Affects max longitudinal slope thresholds.
        private static readonly (string Value, string Label)[] Topographies = new[]
        {
            ("", "לא צוין"),
            ("flat", "מישור"),
            ("hilly", "גבעי"),
            ("mountainous", "הררי"),
        };

        /// <summary>
        /// Renders the scope selection card HTML.
        /// </summary>
        public static string Render(EntityCounts counts)
        {
            var sb = new StringBuilder();

            sb.Append(@"<div class='scope-card' dir='rtl' style='text-align:right; font-family:""Segoe UI"",Arial,sans-serif;'>");

            // Header
            sb.Append("<h3 style='margin:8px 0; color:#1e40af;'>&#x1f4ca; בחר את תחומי הניתוח</h3>");

            // Project-wide topography picker (single dropdown, applies to all alignments)
            sb.Append(@"<div style='margin:8px 0; padding:6px 8px; background:#f8fafc; border:1px solid #e2e8f0; border-radius:6px;'>
                <label style='font-size:13px; font-weight:600; color:#334155; margin-left:8px;'>טופוגרפיה:</label>
                <select id='topography-select' style='font-size:12px; padding:3px 6px; border:1px solid #d1d5db; border-radius:4px;'>");
            foreach (var (value, label) in Topographies)
            {
                sb.Append($"<option value='{WebUtility.HtmlEncode(value)}'>{WebUtility.HtmlEncode(label)}</option>");
            }
            sb.Append("</select></div>");

            // Categories table
            sb.Append(@"<table style='width:100%; border-collapse:collapse; margin:8px 0; font-size:13px;'>
                <tr style='background:#e5e7eb;'>
                    <th style='padding:6px 8px; text-align:center; border:1px solid #d1d5db; width:30px;'></th>
                    <th style='padding:6px 8px; text-align:right; border:1px solid #d1d5db;'>תחום</th>
                    <th style='padding:6px 8px; text-align:right; border:1px solid #d1d5db;'>כמות</th>
                </tr>");

            AddCategoryRow(sb, "horizontal_geometry", "גאומטריה אופקית", counts.Alignments, "צירים");
            AddCategoryRow(sb, "vertical_geometry", "גאומטריה אנכית", counts.DesignProfiles, "פרופילים תכנוניים");
            AddCategoryRow(sb, "signs_markings", "תמרורים וסימון", counts.Signs + counts.Markings, "תמרורים/סימונים");
            AddCategoryRow(sb, "cross_sections", "חתכים רוחביים", counts.Corridors, "מסדרונות");
            AddCategoryRow(sb, "ramps", "רמפות", counts.Ramps, "רמפות");
            AddCategoryRow(sb, "drainage", "ניקוז", counts.PipeNetworks, "רשתות ניקוז");

            sb.Append("</table>");

            // Alignment sub-list (only if alignments exist)
            if (counts.AlignmentDetails != null && counts.AlignmentDetails.Count > 0)
            {
                sb.Append("<div style='margin:8px 0;'>");
                sb.Append("<p style='font-size:13px; font-weight:600; color:#334155; margin:4px 0;'>צירים לניתוח:</p>");

                sb.Append(@"<table style='width:100%; border-collapse:collapse; margin:4px 0; font-size:12px;'>
                    <tr style='background:#f1f5f9;'>
                        <th style='padding:4px 6px; text-align:center; border:1px solid #d1d5db; width:30px;'></th>
                        <th style='padding:4px 6px; text-align:right; border:1px solid #d1d5db;'>שם</th>
                        <th style='padding:4px 6px; text-align:right; border:1px solid #d1d5db;'>אורך</th>
                        <th style='padding:4px 6px; text-align:right; border:1px solid #d1d5db;'>סוג דרך</th>
                        <th style='padding:4px 6px; text-align:right; border:1px solid #d1d5db;'>סיווג</th>
                        <th style='padding:4px 6px; text-align:right; border:1px solid #d1d5db;'>חתך לרוחב</th>
                    </tr>");

                foreach (var a in counts.AlignmentDetails)
                {
                    string safeName = WebUtility.HtmlEncode(a.Name);
                    string lengthStr = a.Length > 0 ? $"{a.Length:F0}m" : "—";

                    sb.Append($@"<tr>
                        <td style='padding:4px 6px; border:1px solid #d1d5db; text-align:center;'><input type='checkbox' class='align-cb' data-name='{safeName}' checked onchange='onAlignmentToggle()' /></td>
                        <td style='padding:4px 6px; border:1px solid #d1d5db;'>{safeName}</td>
                        <td style='padding:4px 6px; border:1px solid #d1d5db; direction:ltr; text-align:center;'>{lengthStr}</td>
                        <td style='padding:4px 6px; border:1px solid #d1d5db;'>
                            <select class='road-type-select' data-name='{safeName}' onchange='refreshClassification(this)' style='font-size:11px; padding:2px 4px; border:1px solid #d1d5db; border-radius:4px;'>");

                    foreach (var (value, label) in RoadTypes)
                    {
                        sb.Append($"<option value='{WebUtility.HtmlEncode(value)}'>{WebUtility.HtmlEncode(label)}</option>");
                    }

                    sb.Append($@"</select>
                        </td>
                        <td style='padding:4px 6px; border:1px solid #d1d5db;'>
                            <select class='classification-select' data-name='{safeName}' style='display:none; font-size:11px; padding:2px 4px; border:1px solid #d1d5db; border-radius:4px;'></select>
                        </td>
                        <td style='padding:4px 6px; border:1px solid #d1d5db;'>
                            <select class='cross-section-select' data-name='{safeName}' style='font-size:11px; padding:2px 4px; border:1px solid #d1d5db; border-radius:4px;' title='סוג חתך לרוחב הדרך — משפיע על ערכי הסף לפי טבלאות 5.x / 6.3'>");

                    foreach (var (value, label) in CrossSectionTypes)
                    {
                        sb.Append($"<option value='{WebUtility.HtmlEncode(value)}'>{WebUtility.HtmlEncode(label)}</option>");
                    }

                    sb.Append(@"</select>
                        </td>
                    </tr>");
                }

                sb.Append("</table>");
                sb.Append("</div>");
            }

            // Profile sub-list (only if design profiles exist)
            if (counts.ProfileDetails != null && counts.ProfileDetails.Count > 0)
            {
                sb.Append("<div style='margin:8px 0;'>");
                sb.Append("<p style='font-size:13px; font-weight:600; color:#334155; margin:4px 0;'>פרופילים תכנוניים לניתוח:</p>");

                sb.Append(@"<table style='width:100%; border-collapse:collapse; margin:4px 0; font-size:12px;'>
                    <tr style='background:#f1f5f9;'>
                        <th style='padding:4px 6px; text-align:center; border:1px solid #d1d5db; width:30px;'></th>
                        <th style='padding:4px 6px; text-align:right; border:1px solid #d1d5db;'>שם</th>
                        <th style='padding:4px 6px; text-align:right; border:1px solid #d1d5db;'>ציר</th>
                    </tr>");

                foreach (var p in counts.ProfileDetails)
                {
                    string safePName = WebUtility.HtmlEncode(p.Name);
                    string safeAln = WebUtility.HtmlEncode(p.AlignmentName);

                    sb.Append($@"<tr class='profile-row' data-alignment='{safeAln}'>
                        <td style='padding:4px 6px; border:1px solid #d1d5db; text-align:center;'><input type='checkbox' class='profile-cb' data-name='{safePName}' data-alignment='{safeAln}' checked onchange='onProfileToggle()' /></td>
                        <td style='padding:4px 6px; border:1px solid #d1d5db;'>{safePName}</td>
                        <td style='padding:4px 6px; border:1px solid #d1d5db;'>{safeAln}</td>
                    </tr>");
                }

                sb.Append("</table>");
                sb.Append("</div>");
            }

            // Action buttons: analyze (green, disableable), toggle (blue), cancel (gray)
            sb.Append(@"<div style='margin:12px 0; display:flex; gap:8px; justify-content:flex-end;'>
                <button id='scope-analyze-btn' onclick=""submitScope('analyze')"" style='background:#16a34a; color:white; border:none; padding:8px 20px; border-radius:6px; cursor:pointer; font-size:14px; font-weight:bold;'>&#x1f50d; ניתוח</button>
                <button id='scope-toggle-btn' onclick='toggleScopeSelection()' style='background:#2563eb; color:white; border:none; padding:8px 20px; border-radius:6px; cursor:pointer; font-size:14px;'>בטל בחירה</button>
                <button onclick=""submitScope('cancel')"" style='background:#6b7280; color:white; border:none; padding:8px 20px; border-radius:6px; cursor:pointer; font-size:14px;'>&#x274C; ביטול</button>
            </div>");

            // JavaScript — emit classification map first, then helpers
            var classificationsJson = new StringBuilder("{");
            bool firstKey = true;
            foreach (var kv in ClassificationsByRoadType)
            {
                if (!firstKey) classificationsJson.Append(",");
                firstKey = false;
                classificationsJson.Append($"\"{kv.Key}\":[");
                bool firstOpt = true;
                foreach (var (value, label) in kv.Value)
                {
                    if (!firstOpt) classificationsJson.Append(",");
                    firstOpt = false;
                    var safeVal = WebUtility.HtmlEncode(value).Replace("\"", "\\\"");
                    var safeLab = WebUtility.HtmlEncode(label).Replace("\"", "\\\"");
                    classificationsJson.Append($"[\"{safeVal}\",\"{safeLab}\"]");
                }
                classificationsJson.Append("]");
            }
            classificationsJson.Append("}");

            sb.Append("<script>");
            sb.Append($"var CLASSIFICATIONS_BY_ROAD_TYPE = {classificationsJson};");
            sb.Append(@"
function refreshClassification(roadTypeSelect) {
    var name = roadTypeSelect.getAttribute('data-name');
    var row = roadTypeSelect.closest('tr');
    if (!row) return;
    var classSelect = row.querySelector('.classification-select');
    if (!classSelect) return;
    var options = CLASSIFICATIONS_BY_ROAD_TYPE[roadTypeSelect.value];
    classSelect.innerHTML = '';
    if (!options || options.length === 0) {
        classSelect.style.display = 'none';
        return;
    }
    var placeholder = document.createElement('option');
    placeholder.value = '';
    placeholder.textContent = 'סיווג…'; // סיווג…
    classSelect.appendChild(placeholder);
    for (var i = 0; i < options.length; i++) {
        var opt = document.createElement('option');
        opt.value = options[i][0];
        opt.textContent = options[i][1];
        classSelect.appendChild(opt);
    }
    classSelect.style.display = '';
}
function onCategoryToggle(catCb) {
    // Categories that drive a sub-table cascade.
    var area = catCb.getAttribute('data-area');
    var newState = !!catCb.checked;
    if (area === 'horizontal_geometry') {
        var acbs = document.querySelectorAll('.align-cb');
        for (var i = 0; i < acbs.length; i++) acbs[i].checked = newState;
    } else if (area === 'vertical_geometry') {
        var pcbs = document.querySelectorAll('.profile-cb');
        for (var i = 0; i < pcbs.length; i++) pcbs[i].checked = newState;
    }
    updateScopeButtons();
}
function onAlignmentToggle() {
    // Mirror the parent category state to whether ANY alignment is checked.
    var acbs = document.querySelectorAll('.align-cb');
    var any = false;
    for (var i = 0; i < acbs.length; i++) { if (acbs[i].checked) { any = true; break; } }
    var cat = document.querySelector('.scope-cat-cb[data-area=""horizontal_geometry""]');
    if (cat && !cat.disabled) cat.checked = any;
    updateScopeButtons();
}
function onProfileToggle() {
    var pcbs = document.querySelectorAll('.profile-cb');
    var any = false;
    for (var i = 0; i < pcbs.length; i++) { if (pcbs[i].checked) { any = true; break; } }
    var cat = document.querySelector('.scope-cat-cb[data-area=""vertical_geometry""]');
    if (cat && !cat.disabled) cat.checked = any;
    updateScopeButtons();
}
function toggleScopeSelection() {
    var cbs = document.querySelectorAll('.scope-cat-cb:not(:disabled)');
    var acbs = document.querySelectorAll('.align-cb');
    var pcbs = document.querySelectorAll('.profile-cb');
    var anyChecked = false;
    for (var i = 0; i < cbs.length; i++) { if (cbs[i].checked) { anyChecked = true; break; } }
    if (!anyChecked) { for (var i = 0; i < acbs.length; i++) { if (acbs[i].checked) { anyChecked = true; break; } } }
    if (!anyChecked) { for (var i = 0; i < pcbs.length; i++) { if (pcbs[i].checked) { anyChecked = true; break; } } }
    var newState = !anyChecked;
    for (var i = 0; i < cbs.length; i++) cbs[i].checked = newState;
    for (var i = 0; i < acbs.length; i++) acbs[i].checked = newState;
    for (var i = 0; i < pcbs.length; i++) pcbs[i].checked = newState;
    updateScopeButtons();
}
function updateScopeButtons() {
    var cbs = document.querySelectorAll('.scope-cat-cb');
    var acbs = document.querySelectorAll('.align-cb');
    var pcbs = document.querySelectorAll('.profile-cb');
    var anyChecked = false;
    for (var i = 0; i < cbs.length; i++) { if (cbs[i].checked) { anyChecked = true; break; } }
    if (!anyChecked) { for (var i = 0; i < acbs.length; i++) { if (acbs[i].checked) { anyChecked = true; break; } } }
    if (!anyChecked) { for (var i = 0; i < pcbs.length; i++) { if (pcbs[i].checked) { anyChecked = true; break; } } }
    var toggleBtn = document.getElementById('scope-toggle-btn');
    if (toggleBtn) toggleBtn.textContent = anyChecked ? '\u05D1\u05D8\u05DC \u05D1\u05D7\u05D9\u05E8\u05D4' : '\u05D1\u05D7\u05E8 \u05D4\u05DB\u05DC';
    var analyzeBtn = document.getElementById('scope-analyze-btn');
    if (analyzeBtn) { analyzeBtn.disabled = !anyChecked; analyzeBtn.style.opacity = anyChecked ? '1' : '0.5'; analyzeBtn.style.cursor = anyChecked ? 'pointer' : 'not-allowed'; }
}
function submitScope(mode) {
    if (mode === 'cancel') {
        window.chrome.webview.postMessage(JSON.stringify({ action: 'scope_selection', mode: 'cancel' }));
        return;
    }
    var analyzeBtn = document.getElementById('scope-analyze-btn');
    if (analyzeBtn && analyzeBtn.disabled) return;
    var focusAreas = [];
    var cbs = document.querySelectorAll('.scope-cat-cb');
    for (var i = 0; i < cbs.length; i++) {
        if (cbs[i].checked) focusAreas.push(cbs[i].getAttribute('data-area'));
    }
    var selectedAlignments = [];
    var acbs = document.querySelectorAll('.align-cb');
    for (var i = 0; i < acbs.length; i++) {
        if (acbs[i].checked) selectedAlignments.push(acbs[i].getAttribute('data-name'));
    }
    var roadTypeOverrides = {};
    var selects = document.querySelectorAll('.road-type-select');
    for (var i = 0; i < selects.length; i++) {
        if (selects[i].value) roadTypeOverrides[selects[i].getAttribute('data-name')] = selects[i].value;
    }
    var classificationOverrides = {};
    var classSelects = document.querySelectorAll('.classification-select');
    for (var i = 0; i < classSelects.length; i++) {
        if (classSelects[i].style.display !== 'none' && classSelects[i].value) {
            classificationOverrides[classSelects[i].getAttribute('data-name')] = classSelects[i].value;
        }
    }
    var crossSectionOverrides = {};
    var csSelects = document.querySelectorAll('.cross-section-select');
    for (var i = 0; i < csSelects.length; i++) {
        if (csSelects[i].value) {
            crossSectionOverrides[csSelects[i].getAttribute('data-name')] = csSelects[i].value;
        }
    }
    var selectedProfiles = [];
    var pcbs = document.querySelectorAll('.profile-cb');
    for (var i = 0; i < pcbs.length; i++) {
        if (pcbs[i].checked) selectedProfiles.push(pcbs[i].getAttribute('data-name'));
    }
    var topoEl = document.getElementById('topography-select');
    var topography = (topoEl && topoEl.value) ? topoEl.value : null;
    window.chrome.webview.postMessage(JSON.stringify({
        action: 'scope_selection',
        mode: 'analyze',
        focus_areas: focusAreas,
        selected_alignments: selectedAlignments,
        selected_profiles: selectedProfiles,
        road_type_overrides: Object.keys(roadTypeOverrides).length > 0 ? roadTypeOverrides : null,
        road_classification_overrides: Object.keys(classificationOverrides).length > 0 ? classificationOverrides : null,
        cross_section_overrides: Object.keys(crossSectionOverrides).length > 0 ? crossSectionOverrides : null,
        topography: topography
    }));
}
</script>");

            sb.Append("</div>");
            return sb.ToString();
        }

        private static void AddCategoryRow(StringBuilder sb, string focusArea, string label, int count, string entityLabel)
        {
            bool enabled = count > 0;
            string disabledAttr = enabled ? "" : " disabled";
            string checkedAttr = enabled ? " checked" : "";
            string rowStyle = enabled ? "" : " color:#9ca3af;";

            sb.Append($@"<tr style='{rowStyle}'>
                <td style='padding:6px 8px; border:1px solid #d1d5db; text-align:center;'><input type='checkbox' class='scope-cat-cb' data-area='{focusArea}'{checkedAttr}{disabledAttr} onchange='onCategoryToggle(this)' /></td>
                <td style='padding:6px 8px; border:1px solid #d1d5db;'>{WebUtility.HtmlEncode(label)}</td>
                <td style='padding:6px 8px; border:1px solid #d1d5db;'>{count} {WebUtility.HtmlEncode(entityLabel)}</td>
            </tr>");
        }
    }
}
