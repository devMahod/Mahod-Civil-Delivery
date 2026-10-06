using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.Civil3D.Plugin.Tests.Contracts
{
    /// <summary>
    /// The plugin's KNOWN, ACCEPTED protocol gaps — verified 2026-08-03 against
    /// PROTOCOL.md v1.13, the agent source and the plugin DTOs.
    ///
    /// These are pinned as <b>strict xfail</b>, not skipped: the contract theories
    /// exempt a registered gap, and a matching "this gap still exists" test asserts the
    /// gap is STILL there. The moment someone closes a gap, its pin turns red and says
    /// "delete me" — a pin can never rot into a silent lie, and the filtered lane keeps
    /// its 0-failed / 0-skipped signal in the meantime.
    ///
    /// <see cref="ProtocolContractTests.KnownGapRegistry_HasNoStaleEntries"/> additionally
    /// fails if an entry stops matching any fixture (e.g. the contract drops the plugin
    /// from a type's audience), so retired gaps cannot linger here either.
    ///
    /// Every entry states WHERE the fix belongs and WHAT breaks for the user today,
    /// because that is what a reader needs and what PROTOCOL.md "Known debt" mirrors.
    /// </summary>
    public static class ProtocolGapRegistry
    {
        /// <summary>A required payload field that no plugin DTO property is bound to.</summary>
        public sealed class UnmodelledField
        {
            public string Fixture { get; init; } = string.Empty;
            public string Field { get; init; } = string.Empty;
            public string Dto { get; init; } = string.Empty;
            public string Where { get; init; } = string.Empty;
            public string Impact { get; init; } = string.Empty;

            public string Key => $"{Fixture}#{Field}";
        }

        /// <summary>A message type the contract gives the plugin, that the plugin does not handle.</summary>
        public sealed class UnhandledType
        {
            public string Direction { get; init; } = string.Empty;
            public string Type { get; init; } = string.Empty;
            public string Where { get; init; } = string.Empty;
            public string Impact { get; init; } = string.Empty;

            /// <summary>True when the right fix is probably the CONTRACT, not the plugin.</summary>
            public bool ContractLikelyWrong { get; init; }

            public string Key => $"{Direction}/{Type}";
        }

        // ── required fields with no DTO property ───────────────────────────────

        public static readonly IReadOnlyList<UnmodelledField> UnmodelledRequiredFields = new[]
        {
            new UnmodelledField
            {
                Fixture = "server_to_client/check_start.json",
                Field = "entity_type",
                Dto = "CheckStartPayload",
                Where = "WebSocket/WebSocketMessageTypes.cs — add [JsonPropertyName(\"entity_type\")] string EntityType to CheckStartPayload",
                Impact = "The analyze progress line can only say 'בודק <name>' — it cannot say WHAT is being checked " +
                         "(alignment vs profile vs corridor). With same-named entities of different types the engineer " +
                         "cannot tell the two apart while the analysis runs."
            },
            new UnmodelledField
            {
                Fixture = "server_to_client/check_result.json",
                Field = "status",
                Dto = "CheckResultPayload",
                Where = "WebSocket/WebSocketMessageTypes.cs — add [JsonPropertyName(\"status\")] string Status to CheckResultPayload " +
                        "(its current entity_name/findings_count pair is what the agent does NOT send)",
                Impact = "Per-check pass/fail never reaches the plugin. MessageDispatcher deserializes an object whose two " +
                         "fields are absent from the wire, so CheckResultReceived fires with an empty payload — which is " +
                         "consistent with PROTOCOL.md's own 'CheckResultReceived has no subscribers' debt note. Live " +
                         "progress shows checks starting but never their outcome."
            },
            new UnmodelledField
            {
                Fixture = "server_to_client/analysis_complete.json",
                Field = "plan_id",
                Dto = "AnalysisCompletePayload",
                Where = "WebSocket/WebSocketMessageTypes.cs — add PlanId to AnalysisCompletePayload",
                Impact = "The completion banner cannot correlate itself to the plan it finished, so a second analyze " +
                         "started before the first finished cannot be told apart."
            },
            new UnmodelledField
            {
                Fixture = "server_to_client/analysis_complete.json",
                Field = "total_items",
                Dto = "AnalysisCompletePayload",
                Where = "WebSocket/WebSocketMessageTypes.cs — add TotalItems to AnalysisCompletePayload",
                Impact = "The banner reports checks and findings but not how many ITEMS were analyzed — the number the " +
                         "engineer compares against the scope preview they approved."
            },
            new UnmodelledField
            {
                Fixture = "server_to_client/fix_result.json",
                Field = "failed_count",
                Dto = "FixResultPayload",
                Where = "WebSocket/WebSocketMessageTypes.cs — add FailedCount to FixResultPayload",
                Impact = "The plugin re-derives failed/manual counts client-side (FixResultClassifier) instead of using the " +
                         "agent's authoritative numbers. Any disagreement between the two shows the engineer a fix summary " +
                         "that does not match what the server actually did."
            },
            new UnmodelledField
            {
                Fixture = "server_to_client/fix_result.json",
                Field = "manual_required_count",
                Dto = "FixResultPayload",
                Where = "WebSocket/WebSocketMessageTypes.cs — add ManualRequiredCount to FixResultPayload",
                Impact = "Same as failed_count: the 'נדרש טיפול ידני' tally is recomputed from per-row heuristics rather " +
                         "than read from the server, so soft-skips can be miscounted in the summary banner."
            },
        };

        // ── audience:["plugin"] types the plugin does not handle ───────────────

        public static readonly IReadOnlyList<UnhandledType> UnhandledPluginTypes = new[]
        {
            new UnhandledType
            {
                Direction = "server_to_client",
                Type = "warning",
                Where = "MessageTypes (add Warning = \"warning\") + a case in MessageDispatcher.DispatchAsync",
                Impact = "Server warnings — notably DATA_STALE, 'the drawing changed since this analysis' — are dropped " +
                         "into the dispatcher's default branch and only reach Debug output. The engineer acts on a report " +
                         "the server already knows is stale, with nothing on screen saying so."
            },
            new UnhandledType
            {
                Direction = "server_to_client",
                Type = "await_input",
                Where = "MessageTypes (add AwaitInput = \"await_input\") + a case in MessageDispatcher.DispatchAsync, " +
                        "replacing the text sniffing in NewChatControl.BuildApprovalButtonsIfNeeded",
                Impact = "THE most consequential gap. The design flow's approval prompt is never received as a message. " +
                         "The plugin instead guesses from the assistant's Hebrew TEXT: BuildApprovalButtonsIfNeeded shows " +
                         "buttons only if the text contains one of 'מחכה לאישור' / 'await_input' / 'אשר' / 'מוכן' / " +
                         "'ASSEMBLY_SELECT:', AND (for the standard pair) also contains 'בטל'. Rewording a prompt agent-side " +
                         "silently removes the buttons; the bare substring 'אשר' also matches ordinary words such as 'כאשר', " +
                         "so an unrelated answer that happens to contain both 'כאשר' and 'בטל' grows spurious approve/cancel " +
                         "buttons. The buttons reply as free-text chat ('אשר'/'בטל') with no step or plan correlation."
            },
            new UnhandledType
            {
                Direction = "server_to_client",
                Type = "drawing_changed_ack",
                Where = "MessageTypes (add DrawingChangedAck) + a no-op case in MessageDispatcher.DispatchAsync",
                Impact = "Housekeeping only: the ack for the legacy drawing_changed message logs 'Unhandled message type'. " +
                         "Nothing user-visible breaks — this is the cheapest of the gaps and mostly noise removal."
            },
        };

        // RETIRED 2026-08-03 — the contract was the thing that was wrong, and it was fixed
        // upstream in contracts/tools/build_fixtures.py (v1.13), so these are no longer the
        // plugin's gaps and no longer belong here:
        //   client_to_server/drawing_changed  — audience []. Legacy type; the plugin
        //       deliberately sends `event{objects_changed}` instead.
        //   client_to_server/design           — audience []. The agent dispatches the type
        //   client_to_server/program_analysis   (websocket.py → handle_design /
        //       handle_program_analysis) but NO client sends either: the plugin reaches the
        //       design pipeline through `chat` + the agent's classifier, and the web repo has
        //       no sender. Recorded in PROTOCOL.md Known debt.
        // KnownGapRegistry_HasNoStaleEntries is what forced this cleanup: the moment their
        // audience dropped "plugin", it went red naming them.

        public static bool IsKnownUnmodelled(string fixture, string field) =>
            UnmodelledRequiredFields.Any(g => g.Fixture == fixture && g.Field == field);

        public static UnhandledType? FindUnhandled(string direction, string type) =>
            UnhandledPluginTypes.FirstOrDefault(g => g.Direction == direction && g.Type == type);

        public static bool IsKnownUnhandled(string direction, string type) =>
            FindUnhandled(direction, type) != null;
    }
}
