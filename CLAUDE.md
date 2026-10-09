# CLAUDE.md — Mahod Civil Delivery

Guidance for Claude Code and other coding agents in this repo.

## Releasing Mahod Civil Delivery — through MahodAI (since 2026-10-08)

**Mahod Civil Delivery has no installer of its own any more.** Since MahodAI 2.0 every Mahod tool ships
INSIDE the MahodAI plugin and reaches engineers through MahodAI's signed auto-update feed; the
MahodAI 2.0 setup removes the old standalone installs. So a release of this tool **is** a MahodAI
release:

1. Finish the work here, merge it to **main** and push. A release ships exactly what is on main.
2. From the MahodAI plugin (`C:\WORK\mahod_ai_workspace\plugin`, MahodAI-Plugin):
   `.\scripts\release\Release-Tool.ps1 -Tool civil-delivery` — it syncs this repo into the plugin,
   picks the next MahodAI version, runs the gates, packs and signs, and commits plugin and feed in
   throwaway worktrees. **It pushes nothing.**
3. Show the owner its summary. Only on an explicit go: `.\scripts\release\Release-Tool.ps1 -Tool civil-delivery -Publish`
   pushes, tags this repo `v<version>` and checks the live feed. Engineers have it within the hour
   (or at once with the עדכונים button on the MahodAI ribbon tab).

The full procedure and its traps: `RELEASING.md` in MahodAI-Plugin. Slash command here:
`/release-to-mahodai`.

**Mirrored.** The plugin's copy is a byte mirror of the folders `sync-civil-delivery.ps1` maps on this repo's main,
made only by the plugin's `scripts/sync-civil-delivery.ps1 -CivilDeliveryRepo <this checkout>` (`-Check` lists the
differences and writes nothing). **Never edit the plugin's copy**: a change MahodAI needs goes in
here (behind `(none: the standalone build defines `MAHOD_CD_STANDALONE`; the plugin build does not)` when it must not reach the standalone build), then a release. The
release refuses to run when the plugin's copy was edited since the last sync, because the mirror
would revert that edit for every engineer — carry those plugin commits into this repo first.

In MahodAI the tool is the **Civil Delivery** button on the MahodAI ribbon tab
(panel כלים הנדסיים) and the commands `MHD_CIVIL_DELIVERY` (the panel), `MHD_SETUP`, `MHD_SECTIONS`, `MHD_ESTIMATE` — Civil 3D only (the standalone build answered to `MCD_*`). Its version is `MahodCivilDelivery/VERSION`.

**Its user guide is MahodAI's**: `guides/content/civil_delivery.md` in MahodAI-Plugin, built to
`Mahod_*_Guide.pdf` by `guides/build-guides.py` and shipped in every release (the ribbon's
מדריכים button, the Start menu). A user-visible change here needs that guide updated in the same
release — not a PDF in this repo.

**Retired, do not use:** this repo's own installer and portal ZIPs, and the
`ci/release-pipeline` branch (an automated design superseded by the above — do not merge it).
