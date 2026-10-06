using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using MahodAI.Civil3D.Plugin.ViewModels;
using MahodAI.Civil3D.Plugin.WebSocket;

namespace MahodAI.Civil3D.Plugin.Services
{
    /// <summary>
    /// Manages the fix lifecycle: fix plan parsing, approval handling, and result processing.
    /// </summary>
    public class FixWorkflowService
    {
        private readonly ChatViewModel _vm;

        public FixWorkflowService(ChatViewModel vm)
        {
            _vm = vm;
        }

        /// <summary>
        /// Handle a fix approval action from the WebView.
        /// </summary>
        public async Task HandleFixApprovalFromWebView(
            string planId,
            string decision,
            string userMessage,
            List<string>? selectedItemIds = null,
            List<FixItemEdit>? editedItems = null)
        {
            if (_vm.WsClient == null || !_vm.WsClient.IsConnected)
                return;

            if (decision == "decline")
            {
                _vm.HighlightManager.ClearHighlights();
                _vm.CurrentFixPlan = null;
                _vm.PendingFixEditsByPlan.Remove(planId);
                return;
            }

            // Once approved, the user edits are "committed" — drop the pending buffer.
            _vm.PendingFixEditsByPlan.Remove(planId);

            var sessionId = _vm.ActiveTab?.SessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                System.Diagnostics.Debug.WriteLine("FixApproval skipped: no active session on the active tab");
                return;
            }
            await _vm.WsClient.SendFixApprovalAsync(sessionId!, planId, decision, userMessage, selectedItemIds, editedItems);
        }

        /// <summary>
        /// Parse a fix_approval action from a WebView JSON message and execute the approval.
        /// Returns the parsed data for any additional UI handling needed.
        /// </summary>
        public async Task HandleFixApprovalAction(JsonElement root)
        {
            var planId = root.TryGetProperty("plan_id", out var pid) ? pid.GetString() ?? "" : "";
            var decision = root.TryGetProperty("decision", out var dec) ? dec.GetString() ?? "" : "";

            // Extract selected_item_ids array
            List<string>? selectedItemIds = null;
            if (root.TryGetProperty("selected_item_ids", out var idsArr) && idsArr.ValueKind == JsonValueKind.Array)
            {
                selectedItemIds = new List<string>();
                foreach (var id in idsArr.EnumerateArray())
                {
                    var idStr = id.GetString();
                    if (!string.IsNullOrEmpty(idStr))
                        selectedItemIds.Add(idStr);
                }
            }

            // Extract edited_items
            var editedItems = ParseEditedItems(root);

            await HandleFixApprovalFromWebView(planId, decision, "", selectedItemIds, editedItems);
        }

        /// <summary>
        /// Parses the <c>edited_items</c> array from a WebView-originated JSON message.
        /// Safely tolerates missing/malformed entries.
        /// </summary>
        public static List<FixItemEdit> ParseEditedItems(JsonElement root)
        {
            var result = new List<FixItemEdit>();
            if (!root.TryGetProperty("edited_items", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var entry in arr.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var edit = new FixItemEdit();
                if (entry.TryGetProperty("item_id", out var idProp))
                {
                    edit.ItemId = idProp.GetString() ?? "";
                }
                if (string.IsNullOrEmpty(edit.ItemId)) continue;

                if (entry.TryGetProperty("tool_params", out var paramsProp) &&
                    paramsProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in paramsProp.EnumerateObject())
                    {
                        // Clone to detach from the source JsonDocument
                        edit.ToolParams[prop.Name] = prop.Value.Clone();
                    }
                }
                result.Add(edit);
            }
            return result;
        }

        /// <summary>
        /// Process a fix plan received from the server.
        /// Stores the plan in the ViewModel and returns the rendered HTML.
        /// </summary>
        public string ProcessFixPlanReceived(FixPlanPayload plan)
        {
            _vm.CurrentFixPlan = plan;
            _vm.LastFixResult = null;  // new plan clears any prior result state
            return FixPlanHtmlRenderer.RenderPlan(plan);
        }

        /// <summary>
        /// Process a fix result received from the server.
        /// Returns the rendered HTML for the result card.
        /// </summary>
        public string ProcessFixResultReceived(FixResultPayload result)
        {
            _vm.HighlightManager.ClearHighlights();
            _vm.LastFixResult = result;
            return FixPlanHtmlRenderer.RenderCombinedResult(result, null);
        }

        /// <summary>
        /// Process a fix verify result received from the server.
        /// Returns the rendered HTML for the combined result card.
        /// </summary>
        public string ProcessFixVerifyResultReceived(FixVerifyResultPayload payload)
        {
            var effectiveResult = _vm.LastFixResult ?? new FixResultPayload
            {
                PlanId = payload.PlanId,
                Success = true,
                Results = new List<FixResultItem>()
            };
            _vm.LastFixVerify = payload;
            var mergedHtml = FixPlanHtmlRenderer.RenderCombinedResult(effectiveResult, payload);
            _vm.CurrentFixPlan = null;
            return mergedHtml;
        }

        /// <summary>
        /// Process a post-fix re-validation payload. Merges with the last fix
        /// result + verify payloads so the combined card keeps all three
        /// sections (execution, verification, re-validation) visible.
        /// </summary>
        public string ProcessPostFixValidationReceived(PostFixValidationPayload payload)
        {
            var effectiveResult = _vm.LastFixResult ?? new FixResultPayload
            {
                PlanId = payload.PlanId,
                Success = true,
                Results = new List<FixResultItem>()
            };
            return FixPlanHtmlRenderer.RenderCombinedResult(
                effectiveResult, _vm.LastFixVerify, payload);
        }
    }
}
