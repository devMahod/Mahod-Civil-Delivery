# Plugin test suite

Two xUnit projects, x64 only. Always run **without** `--no-build`.

```powershell
# 1. THE SIGNAL — must be 100% green, every time, on any machine.
& 'C:\Program Files\dotnet\dotnet.exe' test MahodAI.Civil3D.Plugin.sln --filter "Category!=RequiresCivil3D"

# 2. Everything, including the quarantined host tests. Red outside Civil 3D by design.
& 'C:\Program Files\dotnet\dotnet.exe' test MahodAI.Civil3D.Plugin.sln
```

Counts as of 2026-08-03 (net10.0 / AutoCAD 2027), after the protocol contract suite landed:

| Run | Core.Tests | Plugin.Tests |
|---|---|---|
| Filtered (the signal) | 174 / 0 / 0 | **902 passed / 0 failed / 0 skipped** |
| Full | 174 / 0 / 0 | +32 env-dependent failures, +18 skipped |

## `Contracts/` — the executable protocol

`Contracts/Fixtures/` mirrors the workspace `contracts/` tree (50 golden fixtures generated
from `PROTOCOL.md` v1.13). `ProtocolContractTests` holds them against the real
`WebSocket/WebSocketMessageTypes.cs` DTOs using the real production serializer
(`WebSocketJson.Options` — the same instance `MahodWebSocketClient` and `MessageDispatcher`
use, so the suite cannot drift from the wire). Nothing here is `RequiresCivil3D`: it is pure
serialization and runs in the filtered lane.

It checks mirror integrity (sha256 vs `manifest.json`, file set, protocol version),
deserialization into the DTOs, that no REQUIRED payload field is dropped or renamed on a
round trip, that every `audience: ["plugin"]` type has a `MessageTypes` constant AND is
actually dispatched, and the v1.8 commit gate (`outcome == "succeeded" AND
hard_gates_passed`, with a missing `outcome` never read as permission).

Hashes are computed over **LF-normalized** text — the generator hashes the string before
writing it, and on a Windows checkout (`* text=auto`) the file lands with CRLF. The agent's
suite does the same (`ai_agent/tests/contracts/conftest.py::sha256_file`).

### Known gaps are pinned as STRICT xfail, never skipped

The suite's first run found 31 violations: 17 fixture defects (fixed upstream in
`contracts/tools/build_fixtures.py` and re-synced as v1.13) and 14 real plugin gaps.

The plugin gaps live in `Contracts/ProtocolGapRegistry.cs`. Each one is exempted from the
real assertion and pinned by a test that asserts **the gap still exists**
(`KnownGap_RequiredFieldIsStillUnmodelled`, `KnownGap_PluginAudienceTypeIsStillUnhandled`).
That is strict xfail: green while the gap is real, **red the moment someone closes it**, with
a message saying to delete the pin. `KnownGapRegistry_HasNoStaleEntries` fails if an entry
stops matching any fixture. So the lane stays 0-failed / 0-skipped without a single pin able
to rot into a lie — which `[Fact(Skip=…)]` cannot promise, and which would also have broken
the 0-skipped rule below.

Every entry names the missing DTO property or dispatch case, the file the fix belongs in, and
what breaks for the user today. **Nine pins**, all genuine plugin gaps: six required fields
with no DTO property (`check_start.entity_type`, `check_result.status`,
`analysis_complete.plan_id`/`total_items`, `fix_result.failed_count`/`manual_required_count`)
and three undispatched server→client types (`warning`, `await_input`, `drawing_changed_ack`).

The guard has already earned its keep: when v1.13 dropped `plugin` from the audience of
`drawing_changed`, `design` and `program_analysis` — the three cases where the CONTRACT was
what was wrong — `KnownGapRegistry_HasNoStaleEntries` went red naming them, and the pins were
deleted rather than left describing a world that no longer existed.

**Never edit the mirrored fixtures.** Fix `contracts/tools/build_fixtures.py`, regenerate and
re-sync.

## `[Trait("Category", "RequiresCivil3D")]`

The test only passes **inside a real Civil 3D process**. Nearly all of them call a tool's
`ExecuteAsync(Transaction, CivilDocument, ...)`, which JIT-loads the mixed-mode `Acdbmgd` /
`AeccDbMgd`; outside Civil 3D that throws `FileNotFoundException` before any assertion runs.
Applied to **47 methods (50 cases) across 14 files** — per method, not per class, so the
metadata / registration / parameter-validation tests in those same files stay in the signal.

These are not regressions and not "known failures to ignore" — they are not runnable here.
Their real coverage is the in-Civil-3D self-test harness — `MAHOD_SELFTEST` (see
`plugin/SelfTest.md`), the older `MAHOD_SELFTEST_SPIKE`, and the "Civil 3D Debug" launch
profile — which drives the same paths against a live document.

## The rule

**The filtered run must always be 100% green.** A red or flaky test gets fixed or deleted
within 3 working days — never documented as normal, never parked as a "known failure". If it
cannot be made reliable, delete it and say so.

Pinned contract gaps are the one sanctioned exception, and only because the pin itself is a
test that fails when the gap closes. Adding a pin requires the registry entry to name the fix
location and the user impact — never a loosened assertion.

## Standing trap

An **enum value inside `[InlineData]` silently drops tests from discovery.** Measured
2026-08-03: `[InlineData(..., FixOutcome.Applied)]` removed all 4 rows of its `[Theory]` from
the run while the suite still reported green. Pass strings; map to the enum in the test body.
