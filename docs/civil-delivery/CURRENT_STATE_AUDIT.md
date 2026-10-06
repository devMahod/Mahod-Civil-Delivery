# CURRENT STATE AUDIT — MahodAI-Plugin @ Gate 0

**Date:** 2026-08-18
**Checkout:** `C:\Users\arthurf\Downloads\MahodAI-Plugin`
**Branch:** `feature/civil-delivery`
**Base SHA:** `7df13b589764a2e5ae8bfc495e0df90a3b63d390` (== plan-lock expected SHA, verified against origin after fetch)

## Repository state vs plan §1.1

- Plan snapshot SHA and `origin/main` are identical → the plan's current-state audit (§1) remains authoritative.
- Local `main` was 162 commits behind (at `5ac09f0`, 2026-04-20) with a clean tree; fast-forwarded with `--ff-only`. No commits, no pushes.
- Remote branches present: `create-stop-button`, `feature/create-alignment-tool`, `feature/cross-section-improvements`, `feature/day2-plugin-fixes`, `feature/stage2`, `stage3-finish`, `test`, `upgrade-the-chat`.
- Notable additions since April on main: `global.json` (SDK 10.0.301, rollForward latestFeature), `scripts/run-selftest.ps1`.

## §1.2 key components — re-verified present at 7df13b5

| Component | Path |
|---|---|
| ToolRegistry | `MahodAI.Civil3D.Plugin/Tools/ToolRegistry.cs` |
| CreateSampleLinesTool | `MahodAI.Civil3D.Plugin/Tools/Creation/CreateSampleLinesTool.cs` |
| CreateSampleLineGroupTool | `MahodAI.Civil3D.Plugin/Tools/Creation/CreateSampleLineGroupTool.cs` |
| CreateSectionViewsTool (custom-block path — NOT the target) | `MahodAI.Civil3D.Plugin/Tools/Creation/CreateSectionViewsTool.cs` |
| SampleCrossSectionsTool | `MahodAI.Civil3D.Plugin/Tools/Corridor/SampleCrossSectionsTool.cs` |
| AuditCrossSectionsTool (+ CrossSection workflow) | `MahodAI.Civil3D.Plugin/Tools/CrossSection/` |
| DrawingSummaryExtractor | `MahodAI.Civil3D.Plugin/DrawingSummaryExtractor.cs` |
| SerialToolQueue | `MahodAI.Civil3D.Plugin/Tools/SerialToolQueue.cs` |
| Test projects | `MahodAI.Core.Tests/`, `MahodAI.Civil3D.Plugin.Tests/` |

## Baseline test suite

**GREEN (2026-08-18).** After Arthur-authorized .NET 10 SDK install (10.0.400 via winget):
signal lane `Category!=RequiresCivil3D`: Core 369/369, Plugin 1045/1045, 0 failed, 0 skipped — matches
plan §1.1 exactly. Full run: +32 env-dependent Acdbmgd-load failures +18 skips outside Civil 3D,
exactly as documented in `MahodAI.Civil3D.Plugin.Tests/README.md` ("red outside Civil 3D by design").

## Toolchain / environment observed

| Item | State |
|---|---|
| dotnet SDKs | 8.0.415, 9.0.306, **10.0.400** (installed 2026-08-18, Arthur-authorized) |
| AutoCAD | 2026 + 2027 both installed (`C:\Program Files\Autodesk\`) |
| Civil 3D | 2027 vertical present (`AutoCAD 2027\C3D\AeccDbMgd.dll`); 2026 vertical NOT installed (base AutoCAD 2026 only) |
| Version auto-detect | `Directory.Build.props`: prefers 2027/net10.0, falls back 2026/net8.0, override `/p:AutoCADVersion=2026` |
| In-host test harness | `MAHOD_SELFTEST` + `scripts/run-selftest.ps1` (real Civil GUI, JSON report) — base for MHD smoke |
| Python | 3.14.0 + openpyxl 3.1.5 (discovery tooling) |
| gh CLI | not installed |
| P: drive | mapped; `P:\data\6422` treated as read-only source — untouched |
