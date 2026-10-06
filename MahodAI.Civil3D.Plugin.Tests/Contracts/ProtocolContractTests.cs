using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools;
using MahodAI.Civil3D.Plugin.WebSocket;
using Xunit;
using Xunit.Abstractions;

namespace MahodAI.Civil3D.Plugin.Tests.Contracts
{
    /// <summary>
    /// The plugin's half of the executable protocol (PROTOCOL.md v1.14 +
    /// <c>&lt;workspace&gt;/contracts/</c>).
    ///
    /// Golden fixtures generated from PROTOCOL.md are mirrored into this project and
    /// held against the REAL <c>WebSocket/WebSocketMessageTypes.cs</c> DTOs, using the
    /// REAL production serializer (<see cref="WebSocketJson.Options"/> — the same
    /// instance <see cref="MahodWebSocketClient"/> and <see cref="MessageDispatcher"/>
    /// use, so the suite cannot drift from what actually goes on the wire).
    ///
    /// What this layer buys: when someone changes a message shape in ai_agent or web
    /// and re-runs <c>build_fixtures.py</c> + <c>sync_contracts.py</c>, THIS suite goes
    /// red — instead of an engineer discovering it in Civil 3D.
    ///
    /// Pure serialization only: NOTHING here is marked
    /// <c>[Trait("Category","RequiresCivil3D")]</c>; it all runs in the filtered lane.
    /// </summary>
    public class ProtocolContractTests
    {
        private readonly ITestOutputHelper _output;

        public ProtocolContractTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// PROTOCOL.md §Envelope, v1.1: every server→client message carries
        /// <c>session_id</c>. <c>connect_ack</c> is the one legitimate exception — it
        /// answers the pre-auth handshake, before any session exists, and correlates
        /// instead. (Identical exemption to the agent suite.)
        /// </summary>
        private static readonly HashSet<string> SessionStampExempt = new(StringComparer.Ordinal) { "connect_ack" };

        /// <summary>
        /// Fixture-level diagnoses appended to failure messages, so a red run points at
        /// the side that must change instead of leaving the reader to reverse-engineer it.
        /// Verified by hand against BOTH implementations on 2026-08-03.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string> Diagnosis =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_to_server/heartbeat.json"] =
                    "FIXTURE DEFECT: the plugin emits client_timestamp as unix-ms " +
                    "(HeartbeatPayload.ClientTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); " +
                    "the fixture supplies an ISO-8601 string. The backend ignores the field, so nothing is " +
                    "broken in production — but the golden fixture describes a shape no client emits.",

                ["client_to_server/event.objects_changed.json"] =
                    "FIXTURE DEFECT: the plugin emits event.summary as an OBJECT " +
                    "(ChangeSummary {added,modified,deleted}) and changes[] as " +
                    "{object_id,object_type,object_name,change_type}; the fixture supplies a Hebrew STRING " +
                    "summary and {object_type,name,operation}.",

                ["client_to_server/tool_result.error.json"] =
                    "FIXTURE DEFECT: error.details is an OBJECT here, but it is a string on BOTH sides — " +
                    "plugin ToolError.Details (string?, fed from ex.ToString()) and agent " +
                    "ws_helpers.send_error(details: Optional[str]).",

                ["client_to_server/tool_result.v18_rejected.json"] =
                    "FIXTURE DEFECT: violations[].station is a FORMATTED STRING ('0+420.00'); the plugin emits " +
                    "it as a number in metres (EngineeringViolation.Station, double?). error.details is an " +
                    "object where both sides use a string. severity is 'error' where the plugin's vocabulary " +
                    "is 'hard' | 'warning' (Tools/IDrawingTool.cs).",

                ["client_to_server/tool_result.v18_candidate_generated.json"] =
                    "FIXTURE DEFECT: violations[].station is a FORMATTED STRING ('0+960.00'); the plugin emits " +
                    "a number in metres (EngineeringViolation.Station, double?).",

                ["server_to_client/error.json"] =
                    "FIXTURE DEFECT: details is an OBJECT, but the agent sends a string — " +
                    "ws_helpers.send_error(details: Optional[str] = None) — and the plugin models string?. " +
                    "NOTE: if the agent ever DID send an object here, MessageDispatcher.DeserializePayload " +
                    "swallows the JsonException and returns null, so the error would vanish silently.",

                ["server_to_client/fix_plan.json"] =
                    "FIXTURE DEFECT: rag_references and skipped_reasons are list[str] in the agent " +
                    "(ai_agent/src/api/schemas/fix.py::FixPlan) and List<string> in the plugin, but the fixture " +
                    "supplies objects. Items also key on item_id where both sides use id, and editable_fields " +
                    "is a dict of FieldMeta on both sides.",

                ["server_to_client/fix_plan.spiral_no_default_length.json"] =
                    "FIXTURE DEFECT: editable_fields is a LIST here but dict[str, FieldMeta] in the agent " +
                    "schema and Dictionary<string, FieldMeta> in the plugin DTO; items key on item_id where " +
                    "both sides use id.",

                ["server_to_client/fix_result.json"] =
                    "FIXTURE DEFECT: results[].previous_value / new_value are NUMBERS here but " +
                    "Optional[str] in the agent (schemas/fix.py::FixResultItem) and string? in the plugin. " +
                    "SEPARATE PLUGIN GAP behind it: FixResultPayload models neither failed_count nor " +
                    "manual_required_count, both of which the agent sends and the contract marks required.",

                ["server_to_client/post_fix_validation.json"] =
                    "FIXTURE DEFECT (and PROTOCOL.md prose defect): totals.resolved/still_open/new are " +
                    "INTEGER counters in the emitter (fix/steps/s5_post_fix_validation.py builds " +
                    "summary_totals as ints) and int in the plugin DTO — the fixture makes them lists of " +
                    "'check@station' keys, following the PROTOCOL.md v1.2 line that says they 'carry " +
                    "check_id@station keys'. The prose is what drifted. entities[] likewise drop " +
                    "entity_type/status/resolved_count/still_open_count/new_count, which the emitter does send.",
            };

        private static string Diagnose(string relativePath) =>
            Diagnosis.TryGetValue(relativePath, out var note) ? $"{Environment.NewLine}      ⤷ {note}" : string.Empty;

        // ── theory data ────────────────────────────────────────────────────────

        public static TheoryData<string> AllFixtures
        {
            get
            {
                var data = new TheoryData<string>();
                foreach (var f in ProtocolFixtures.All) data.Add(f.RelativePath);
                return data;
            }
        }

        public static TheoryData<string> ManifestFiles
        {
            get
            {
                var data = new TheoryData<string>();
                foreach (var e in ProtocolFixtures.ManifestEntries) data.Add(e.File);
                return data;
            }
        }

        /// <summary>Distinct (direction, type) pairs whose audience includes the plugin.</summary>
        public static TheoryData<string, string> PluginAudienceTypes
        {
            get
            {
                var data = new TheoryData<string, string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var f in ProtocolFixtures.All.Where(f => f.IsForPlugin))
                {
                    if (seen.Add($"{f.Direction}/{f.Type}")) data.Add(f.Direction, f.Type);
                }
                return data;
            }
        }

        // ══ 1. Fixtures reach the test runtime ═════════════════════════════════

        [Fact]
        public void Fixtures_AreCopiedToTheTestOutputDirectory()
        {
            Directory.Exists(ProtocolFixtures.Root).Should().BeTrue(
                "the .csproj copy glob must place Contracts/Fixtures next to the test DLL");

            File.Exists(ProtocolFixtures.ManifestPath).Should().BeTrue("manifest.json is part of the mirror");

            ProtocolFixtures.All.Should().NotBeEmpty();
            ProtocolFixtures.All.Count.Should().Be(
                ProtocolFixtures.ManifestFixtureCount,
                "every fixture listed in manifest.json must have been copied to the output directory");

            ProtocolFixtures.All.Select(f => f.Direction).Distinct()
                .Should().BeEquivalentTo(new[] { "client_to_server", "server_to_client" });
        }

        // ══ 2. Mirror integrity — THE drift alarm ══════════════════════════════

        [Theory]
        [MemberData(nameof(ManifestFiles))]
        public void Fixture_Sha256_MatchesTheManifest(string relativePath)
        {
            var entry = ProtocolFixtures.ManifestEntries.Single(e => e.File == relativePath);
            var fixture = ProtocolFixtures.Get(relativePath);

            ProtocolFixtures.Sha256OfNormalizedText(fixture.RawText).Should().Be(
                entry.Sha256,
                "the mirrored fixture must be byte-identical to the canonical one in <workspace>/contracts. " +
                "Never 'fix' this by refreshing the digest — re-run " +
                "`py contracts/tools/build_fixtures.py && py contracts/tools/sync_contracts.py`.");
        }

        [Fact]
        public void ManifestFileSet_MatchesTheFixtureFilesExactly()
        {
            var onDisk = ProtocolFixtures.All.Select(f => f.RelativePath).OrderBy(x => x, StringComparer.Ordinal);
            var inManifest = ProtocolFixtures.ManifestEntries.Select(e => e.File).OrderBy(x => x, StringComparer.Ordinal);

            onDisk.Should().BeEquivalentTo(inManifest,
                "an unlisted fixture (or a listed-but-missing one) means the mirror was hand-edited or a sync was skipped");
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Fixture_ProtocolVersion_AgreesWithTheManifest(string relativePath)
        {
            var fixture = ProtocolFixtures.Get(relativePath);
            fixture.ProtocolVersion.Should().Be(
                ProtocolFixtures.ManifestProtocolVersion,
                "a fixture from an older protocol revision must not survive a regeneration");
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Fixture_ManifestMetadata_MatchesTheContractBlock(string relativePath)
        {
            var fixture = ProtocolFixtures.Get(relativePath);
            var entry = ProtocolFixtures.ManifestEntries.Single(e => e.File == relativePath);

            entry.Type.Should().Be(fixture.Type);
            entry.Direction.Should().Be(fixture.Direction);
            entry.Audience.Should().BeEquivalentTo(fixture.Audience);
        }

        // ══ 3. Envelope invariants ═════════════════════════════════════════════

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Fixture_Envelope_DeserializesIntoWebSocketMessage(string relativePath)
        {
            var fixture = ProtocolFixtures.Get(relativePath);

            var envelope = JsonSerializer.Deserialize<WebSocketMessage>(
                fixture.Message.GetRawText(), WebSocketJson.Options);

            envelope.Should().NotBeNull();
            envelope!.Type.Should().Be(fixture.Type, "the envelope type must match the $contract block");
            envelope.Id.Should().NotBeNullOrWhiteSpace();
            envelope.Timestamp.Should().NotBeNullOrWhiteSpace();
            envelope.Payload.Should().NotBeNull("every fixture carries a payload object, even an empty one");

            if (fixture.IsServerToClient && !SessionStampExempt.Contains(fixture.Type))
            {
                envelope.SessionId.Should().NotBeNullOrWhiteSpace(
                    "PROTOCOL v1.1: every server→client message is session-stamped so multi-tab clients route by it");
            }
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void ToolCallEnvelope_CorrelatesOnToolCallId(string relativePath)
        {
            var fixture = ProtocolFixtures.Get(relativePath);
            if (fixture.Type != MessageTypes.ToolCall) return;

            var envelope = JsonSerializer.Deserialize<WebSocketMessage>(
                fixture.Message.GetRawText(), WebSocketJson.Options)!;
            var payload = JsonSerializer.Deserialize<ToolCallPayload>(
                fixture.PayloadJson, WebSocketJson.Options)!;

            envelope.CorrelationId.Should().Be(payload.ToolCallId,
                "PROTOCOL §Server → client: tool_call envelope correlation_id == payload.tool_call_id");
        }

        // ══ 4. Deserialization into the REAL DTOs ══════════════════════════════

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Fixture_Payload_DeserializesIntoThePluginDto(string relativePath)
        {
            var fixture = ProtocolFixtures.Get(relativePath);

            PluginProtocolSurface.PayloadDtos.Should().ContainKey(
                fixture.Type,
                "every fixture type must be accounted for in PluginProtocolSurface.PayloadDtos — " +
                "a new protocol message type has appeared and nobody mapped it");

            var dtoType = PluginProtocolSurface.PayloadDtos[fixture.Type];
            if (dtoType == null)
            {
                // No DTO. Legitimate only for empty-payload / non-plugin types; the
                // coverage theories below own the "the plugin should model this" verdict.
                return;
            }

            object? dto;
            try
            {
                dto = JsonSerializer.Deserialize(fixture.PayloadJson, dtoType, WebSocketJson.Options);
            }
            catch (JsonException ex)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{relativePath}: payload does NOT deserialize into {dtoType.Name} using the production " +
                    $"serializer.{Environment.NewLine}      {ex.Message}{Diagnose(relativePath)}");
            }

            dto.Should().NotBeNull($"{relativePath} must produce a {dtoType.Name}");
        }

        // ══ 5. Required fields must exist on the DTO, and survive a round trip ══

        /// <summary>
        /// Value-independent structural check: does the DTO have a property bound to
        /// each REQUIRED payload field at all? This is the half that still fires when
        /// a fixture's values are wrong, and it names the gap precisely
        /// (the round-trip theory below then only judges fields that ARE modelled,
        /// so one gap is never reported twice).
        /// </summary>
        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Fixture_RequiredPayloadFields_AreModelledByTheDto(string relativePath)
        {
            var fixture = ProtocolFixtures.Get(relativePath);
            if (fixture.RequiredPayloadFields.Count == 0) return;

            var dtoType = PluginProtocolSurface.PayloadDtos[fixture.Type];
            if (dtoType == null) return; // owned by the coverage theories

            var unmodelled = fixture.RequiredPayloadFields
                .Where(field => !DtoModelsField(dtoType, field))
                // Accepted gaps are pinned instead — see ProtocolGapRegistry and
                // KnownGap_RequiredFieldIsStillUnmodelled, which fails when one is fixed.
                .Where(field => !ProtocolGapRegistry.IsKnownUnmodelled(relativePath, field))
                .ToList();

            unmodelled.Should().BeEmpty(
                $"{relativePath}: {dtoType.Name} has no property bound to required payload " +
                $"field(s) [{string.Join(", ", unmodelled)}]. The plugin cannot read them, so they are " +
                $"dropped the moment the message arrives.{Diagnose(relativePath)}");
        }

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void Fixture_RoundTrip_KeepsEveryRequiredPayloadField(string relativePath)
        {
            var fixture = ProtocolFixtures.Get(relativePath);
            if (fixture.RequiredPayloadFields.Count == 0) return;

            var dtoType = PluginProtocolSurface.PayloadDtos[fixture.Type];
            if (dtoType == null) return; // owned by the coverage theories

            object? dto;
            try
            {
                dto = JsonSerializer.Deserialize(fixture.PayloadJson, dtoType, WebSocketJson.Options);
            }
            catch (JsonException)
            {
                // Already reported (and diagnosed) by Fixture_Payload_DeserializesIntoThePluginDto.
                // Failing twice for one cause just makes a red run harder to read.
                return;
            }

            using var after = JsonDocument.Parse(JsonSerializer.Serialize(dto, dtoType, WebSocketJson.Options));

            var dropped = new List<string>();
            var mangled = new List<string>();

            foreach (var field in fixture.RequiredPayloadFields)
            {
                fixture.Payload.TryGetProperty(field, out var before).Should().BeTrue(
                    $"the fixture itself must contain its declared required field '{field}'");

                // A field the DTO does not model at all is reported once, by
                // Fixture_RequiredPayloadFields_AreModelledByTheDto.
                if (!DtoModelsField(dtoType, field)) continue;

                if (!after.RootElement.TryGetProperty(field, out var round))
                {
                    dropped.Add(field);
                    continue;
                }

                if (!ScalarEquals(before, round))
                {
                    mangled.Add($"{field} ({before.ValueKind}:{Truncate(before)} → {round.ValueKind}:{Truncate(round)})");
                }
            }

            (dropped.Count + mangled.Count).Should().Be(0,
                $"{relativePath} ({dtoType.Name}) must round-trip every REQUIRED payload field. " +
                $"Dropped/renamed: [{string.Join(", ", dropped)}]. Value-mangled: [{string.Join(", ", mangled)}]. " +
                $"A silently-dropped required field is exactly the bug class this layer exists to catch." +
                Diagnose(relativePath));
        }

        // ══ 6. Message-type coverage ═══════════════════════════════════════════

        [Theory]
        [MemberData(nameof(PluginAudienceTypes))]
        public void PluginAudienceType_HasAMessageTypesConstant(string direction, string type)
        {
            if (ProtocolGapRegistry.IsKnownUnhandled(direction, type)) return; // pinned below

            PluginProtocolSurface.HasConstant(type).Should().BeTrue(
                $"'{type}' ({direction}) has audience ['plugin'] in the contract, so the plugin must at least " +
                $"declare it in WebSocket/WebSocketMessageTypes.cs::MessageTypes. " +
                $"Known constants: {string.Join(", ", PluginProtocolSurface.MessageTypeConstants.Keys.OrderBy(k => k, StringComparer.Ordinal))}");
        }

        [Theory]
        [MemberData(nameof(PluginAudienceTypes))]
        public void PluginAudienceType_IsActuallyHandledByThePlugin(string direction, string type)
        {
            if (ProtocolGapRegistry.IsKnownUnhandled(direction, type)) return; // pinned below

            if (direction == "server_to_client")
            {
                PluginProtocolSurface.IsDispatched(type).Should().BeTrue(
                    $"the contract says the plugin consumes '{type}', but MessageDispatcher.DispatchAsync has no " +
                    $"case for it — it falls through to the default branch, which only writes " +
                    $"\"Unhandled message type\" to Debug output. The message is silently discarded at runtime.");
            }
            else
            {
                PluginProtocolSurface.IsEmittedByClient(type).Should().BeTrue(
                    $"the contract says the plugin sends '{type}', but MahodWebSocketClient never builds a " +
                    $"message with that type constant.");
            }
        }

        // ══ 6b. Known plugin gaps — pinned as STRICT xfail ═════════════════════
        //
        // xUnit has no xfail, and [Fact(Skip=...)] is the wrong tool twice over: it
        // breaks this suite's 0-skipped rule, and a skipped test keeps "passing" long
        // after the gap is fixed, so the pin rots into a lie. Instead each accepted gap
        // is asserted to STILL EXIST. That is strict xfail: green while the gap is real,
        // red the moment someone closes it — and the failure message tells them the only
        // correct response is to delete the pin.
        //
        // Every entry names the missing DTO property / dispatch case, the file it belongs
        // in, and what breaks for the user today (ProtocolGapRegistry).

        public static TheoryData<string, string> KnownUnmodelledFields
        {
            get
            {
                var data = new TheoryData<string, string>();
                foreach (var gap in ProtocolGapRegistry.UnmodelledRequiredFields) data.Add(gap.Fixture, gap.Field);
                return data;
            }
        }

        public static TheoryData<string, string> KnownUnhandledTypes
        {
            get
            {
                var data = new TheoryData<string, string>();
                foreach (var gap in ProtocolGapRegistry.UnhandledPluginTypes) data.Add(gap.Direction, gap.Type);
                return data;
            }
        }

        [Theory]
        [MemberData(nameof(KnownUnmodelledFields))]
        public void KnownGap_RequiredFieldIsStillUnmodelled(string relativePath, string field)
        {
            var gap = ProtocolGapRegistry.UnmodelledRequiredFields.Single(g => g.Fixture == relativePath && g.Field == field);
            var fixture = ProtocolFixtures.Get(relativePath);
            var dtoType = PluginProtocolSurface.PayloadDtos[fixture.Type];

            dtoType.Should().NotBeNull($"the pin for {relativePath}#{field} assumes {gap.Dto} exists");
            dtoType!.Name.Should().Be(gap.Dto, "the pin names the DTO it was written against");

            fixture.RequiredPayloadFields.Should().Contain(field,
                $"the pin for {relativePath}#{field} is stale — the contract no longer requires that field. " +
                "Delete the entry from ProtocolGapRegistry.");

            DtoModelsField(dtoType, field).Should().BeFalse(
                $"GAP CLOSED — {gap.Dto} now models '{field}'. Delete this pin from ProtocolGapRegistry so the real " +
                $"assertion in Fixture_RequiredPayloadFields_AreModelledByTheDto takes over.{Environment.NewLine}" +
                $"      fix location: {gap.Where}{Environment.NewLine}" +
                $"      user impact while open: {gap.Impact}");
        }

        [Theory]
        [MemberData(nameof(KnownUnhandledTypes))]
        public void KnownGap_PluginAudienceTypeIsStillUnhandled(string direction, string type)
        {
            var gap = ProtocolGapRegistry.FindUnhandled(direction, type)!;

            var handled = direction == "server_to_client"
                ? PluginProtocolSurface.IsDispatched(type)
                : PluginProtocolSurface.IsEmittedByClient(type);

            var closed = PluginProtocolSurface.HasConstant(type) && handled;

            closed.Should().BeFalse(
                $"GAP CLOSED — the plugin now declares AND handles '{type}' ({direction}). Delete this pin from " +
                $"ProtocolGapRegistry so PluginAudienceType_* guards it for real.{Environment.NewLine}" +
                $"      fix location: {gap.Where}{Environment.NewLine}" +
                $"      user impact while open: {gap.Impact}");
        }

        /// <summary>
        /// Anti-rot guard for the pins themselves. A gap that no longer applies to any
        /// fixture — because the contract dropped "plugin" from a type's audience, or the
        /// fixture was removed — must be deleted from the registry, not left sitting there
        /// describing a world that no longer exists.
        /// </summary>
        [Fact]
        public void KnownGapRegistry_HasNoStaleEntries()
        {
            var stale = new List<string>();

            foreach (var gap in ProtocolGapRegistry.UnmodelledRequiredFields)
            {
                if (ProtocolFixtures.All.All(f => f.RelativePath != gap.Fixture))
                    stale.Add($"{gap.Key} (no such fixture)");
            }

            var pluginTypes = ProtocolFixtures.All
                .Where(f => f.IsForPlugin)
                .Select(f => $"{f.Direction}/{f.Type}")
                .ToHashSet(StringComparer.Ordinal);

            foreach (var gap in ProtocolGapRegistry.UnhandledPluginTypes)
            {
                if (!pluginTypes.Contains(gap.Key))
                    stale.Add($"{gap.Key} (no fixture of that type has the plugin in its audience any more)");
            }

            stale.Should().BeEmpty(
                "a pinned gap that no longer applies must be deleted from ProtocolGapRegistry — " +
                "stale pins are how a 'known issue' list turns into fiction");
        }

        // ══ 7. v1.8 outcome contract / commit gate ═════════════════════════════

        /// <summary>
        /// The v1.8 commit gate, verbatim from PROTOCOL.md §Client → server:
        /// "<c>success</c> stays for back-compat but is NO LONGER the commit gate — the
        /// plugin commits a production object only when <c>outcome==succeeded</c> AND
        /// <c>hard_gates_passed</c>."
        ///
        /// Absence is NOT permission: a legacy payload carries neither field, so the gate
        /// must evaluate false rather than fall back to <c>success</c>. Evaluated off the
        /// real <see cref="ToolResultPayload"/> DTO — if a field is renamed or dropped
        /// from the DTO, the gate silently stops permitting anything, and these tests say so.
        /// </summary>
        private static bool MayCommitProductionObject(ToolResultPayload p) =>
            p.Outcome == "succeeded" && p.HardGatesPassed == true;

        [Theory]
        [InlineData("client_to_server/tool_result.v18_succeeded.json", true)]
        [InlineData("client_to_server/tool_result.v18_cancelled.json", false)]
        [InlineData("client_to_server/tool_result.v18_candidate_generated.json", false)]
        [InlineData("client_to_server/tool_result.v18_rejected.json", false)]
        [InlineData("client_to_server/tool_result.error.json", false)]
        [InlineData("client_to_server/tool_result.legacy_no_outcome.json", false)]
        public void ToolResultFixture_CommitGate_MatchesTheV18Contract(string relativePath, bool mayCommit)
        {
            var fixture = ProtocolFixtures.Get(relativePath);
            var payload = JsonSerializer.Deserialize<ToolResultPayload>(fixture.PayloadJson, WebSocketJson.Options)!;

            MayCommitProductionObject(payload).Should().Be(mayCommit, fixture.Description);
        }

        [Fact]
        public void LegacyToolResult_WithoutOutcome_IsNeverReadAsPermissionToCommit()
        {
            var fixture = ProtocolFixtures.Get("client_to_server/tool_result.legacy_no_outcome.json");
            var payload = JsonSerializer.Deserialize<ToolResultPayload>(fixture.PayloadJson, WebSocketJson.Options)!;

            // The trap this guards: success:true with no v1.8 fields at all.
            payload.Success.Should().BeTrue("the legacy fixture reports a successful tool");
            payload.Outcome.Should().BeNull();
            payload.HardGatesPassed.Should().BeNull();

            MayCommitProductionObject(payload).Should().BeFalse(
                "absence of the v1.8 fields must never be read as permission to commit a production object");
        }

        [Theory]
        [InlineData("client_to_server/tool_result.v18_succeeded.json", "succeeded")]
        [InlineData("client_to_server/tool_result.v18_cancelled.json", "cancelled")]
        [InlineData("client_to_server/tool_result.v18_candidate_generated.json", "candidate_generated")]
        [InlineData("client_to_server/tool_result.v18_rejected.json", "rejected")]
        [InlineData("client_to_server/tool_result.error.json", "failed")]
        public void ToolResultOutcome_RoundTripsThroughThePluginsOwnEnum(string relativePath, string wire)
        {
            // Ties the wire vocabulary to ToolExecutor's enum mapping: a fixture outcome
            // the plugin cannot parse would silently degrade to the success-flag fallback.
            // (Enum values are deliberately NOT passed via InlineData — see the suite README.)
            var fixture = ProtocolFixtures.Get(relativePath);
            var payload = JsonSerializer.Deserialize<ToolResultPayload>(fixture.PayloadJson, WebSocketJson.Options)!;

            payload.Outcome.Should().Be(wire);

            var parsed = ToolExecutor.ParseOutcome(payload.Outcome, payload.Success);
            ToolExecutor.OutcomeWireValue(parsed).Should().Be(wire,
                "the plugin must parse and re-emit every v1.8 outcome value unchanged");
        }

        [Fact]
        public void RejectedToolResult_CarriesStructuredViolations()
        {
            var fixture = ProtocolFixtures.Get("client_to_server/tool_result.v18_rejected.json");
            var payload = JsonSerializer.Deserialize<ToolResultPayload>(fixture.PayloadJson, WebSocketJson.Options)!;

            payload.Success.Should().BeFalse();
            payload.HardGatesPassed.Should().BeFalse();
            payload.Violations.Should().NotBeNullOrEmpty(
                "a hard-gate rejection must carry the structured findings the engineer sees");
            payload.Violations![0].Code.Should().NotBeNullOrWhiteSpace();
            payload.Error.Should().NotBeNull("a rejected result also reports an error code");
        }

        // ══ 8. The production serializer really is the one under test ══════════

        [Fact]
        public void ContractSuite_UsesTheProductionSerializerOptions()
        {
            // Guards against the suite quietly hand-rolling its own options and passing
            // while production emits something else.
            WebSocketJson.Options.PropertyNamingPolicy.Should().BeSameAs(JsonNamingPolicy.SnakeCaseLower);
            WebSocketJson.Options.DefaultIgnoreCondition.Should()
                .Be(System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull);

            var probe = JsonSerializer.Serialize(
                new ToolCallPayload { ToolCallId = "tc-1", ToolName = "list_layers", TimeoutSeconds = 30 },
                WebSocketJson.Options);
            probe.Should().Contain("\"tool_call_id\"").And.Contain("\"timeout_seconds\"");
        }

        // ══ 9. Diagnostics — what the C# DTOs do not model ═════════════════════

        /// <summary>
        /// Never fails. Prints every payload key the plugin's DTOs silently drop, so the
        /// unmodelled surface is visible in the run log instead of being folded into a
        /// pass. Optional fields the plugin has no use for are legitimate; required ones
        /// are failures owned by <see cref="Fixture_RoundTrip_KeepsEveryRequiredPayloadField"/>.
        /// </summary>
        [Fact]
        public void Report_FieldsThePluginDtosDoNotModel()
        {
            var report = new StringBuilder();
            report.AppendLine("Payload keys present in a fixture but absent after a plugin-DTO round trip:");

            foreach (var fixture in ProtocolFixtures.All)
            {
                var dtoType = PluginProtocolSurface.PayloadDtos[fixture.Type];
                if (dtoType == null)
                {
                    var reason = PluginProtocolSurface.NoDtoReason.TryGetValue(fixture.Type, out var r)
                        ? r
                        : "NO PLUGIN DTO — the payload cannot be read at all";
                    report.AppendLine($"  {fixture.RelativePath}: {reason}");
                    continue;
                }

                string roundTripped;
                try
                {
                    var dto = JsonSerializer.Deserialize(fixture.PayloadJson, dtoType, WebSocketJson.Options);
                    roundTripped = JsonSerializer.Serialize(dto, dtoType, WebSocketJson.Options);
                }
                catch (JsonException ex)
                {
                    report.AppendLine($"  {fixture.RelativePath}: DOES NOT DESERIALIZE into {dtoType.Name} — {ex.Message}");
                    var blind = fixture.RequiredPayloadFields.Where(f => !DtoModelsField(dtoType, f)).ToList();
                    if (blind.Count > 0)
                        report.AppendLine($"      …and {dtoType.Name} models none of: {string.Join(", ", blind)}  [REQUIRED]");
                    continue;
                }

                using var after = JsonDocument.Parse(roundTripped);
                var beforePaths = KeyPaths(fixture.Payload).ToList();
                var afterPaths = new HashSet<string>(KeyPaths(after.RootElement), StringComparer.Ordinal);

                var missing = beforePaths.Where(p => !afterPaths.Contains(p)).ToList();
                if (missing.Count == 0) continue;

                var required = new HashSet<string>(fixture.RequiredPayloadFields, StringComparer.Ordinal);
                var annotated = missing.Select(p => required.Contains(p) ? p + "  [REQUIRED]" : p);
                report.AppendLine($"  {fixture.RelativePath} ({dtoType.Name}): {string.Join(", ", annotated)}");
            }

            _output.WriteLine(report.ToString());
        }

        // ── helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// True when <paramref name="dtoType"/> has a property bound to the wire field
        /// <paramref name="wireName"/> — either via an explicit [JsonPropertyName] or via
        /// the production naming policy (SnakeCaseLower).
        /// </summary>
        private static bool DtoModelsField(Type dtoType, string wireName)
        {
            foreach (var prop in dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = prop.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>();
                var bound = attr?.Name
                            ?? WebSocketJson.Options.PropertyNamingPolicy?.ConvertName(prop.Name)
                            ?? prop.Name;

                if (string.Equals(bound, wireName, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Flattened key paths; list elements collapse to the first item as "[]".</summary>
        private static IEnumerable<string> KeyPaths(JsonElement element, string prefix = "")
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in element.EnumerateObject())
                    {
                        var path = prefix.Length == 0 ? prop.Name : $"{prefix}.{prop.Name}";
                        // A null value is dropped on the way out by WhenWritingNull, which is
                        // by design — do not report it as an unmodelled field.
                        if (prop.Value.ValueKind != JsonValueKind.Null) yield return path;
                        foreach (var nested in KeyPaths(prop.Value, path)) yield return nested;
                    }
                    break;

                case JsonValueKind.Array:
                    var first = element.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind != JsonValueKind.Undefined)
                    {
                        foreach (var nested in KeyPaths(first, $"{prefix}.[]")) yield return nested;
                    }
                    break;
            }
        }

        private static bool ScalarEquals(JsonElement before, JsonElement after) => before.ValueKind switch
        {
            JsonValueKind.String => after.ValueKind == JsonValueKind.String && before.GetString() == after.GetString(),
            JsonValueKind.Number => after.ValueKind == JsonValueKind.Number && before.GetDouble() == after.GetDouble(),
            JsonValueKind.True => after.ValueKind == JsonValueKind.True,
            JsonValueKind.False => after.ValueKind == JsonValueKind.False,
            // Structural values (objects/arrays) are asserted by presence: their inner
            // shape is reported by Report_FieldsThePluginDtosDoNotModel.
            _ => true
        };

        private static string Truncate(JsonElement e)
        {
            var raw = e.GetRawText();
            return raw.Length <= 40 ? raw : raw.Substring(0, 40) + "…";
        }
    }
}
