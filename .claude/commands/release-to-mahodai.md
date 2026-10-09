---
description: Release Mahod Civil Delivery to every engineer through the MahodAI updater (stage; publish only on the owner's go)
---

# Release Mahod Civil Delivery through MahodAI

Mahod Civil Delivery ships inside the MahodAI plugin since MahodAI 2.0: a release of this tool is a MahodAI
release, delivered by the MahodAI updater. There is no separate installer, ZIP or portal upload.
Read `RELEASING.md` in MahodAI-Plugin (`C:\WORK\mahod_ai_workspace\plugin`) before you start.

1. **This repo**
   - The work is merged to `main` and pushed; `main` is checked out; `git status` is clean.
   - The version is bumped (`MahodCivilDelivery/VERSION`).
   - In the plugin: `.\scripts\sync-civil-delivery.ps1 -CivilDeliveryRepo C:\WORK\Mahod-Civil-Delivery -Check` lists what the release will bring in.
   - User-visible change? Update `guides/content/civil_delivery.md` in MahodAI-Plugin (and its help
     pages) in the same release.
2. **Stage** — from MahodAI-Plugin:
   ```powershell
   .\scripts\release\Release-Tool.ps1 -Tool civil-delivery
   ```
   Nothing is pushed. If it stops, read why: a dirty or unmerged repo, the live feed ahead of the
   plugin, plugin-side edits a mirror would revert, a red gate. Fix the cause, never the check.
3. **Stop.** Show the owner the summary (version, tool version and SHA, package size, notes).
4. **Publish** — only after an explicit go in this conversation:
   ```powershell
   .\scripts\release\Release-Tool.ps1 -Tool civil-delivery -Publish
   ```
   It pushes MahodAI-Plugin and the feed (the agent's production deploy is the publish), tags this
   repo `v<version>`, and waits until the live feed serves the new manifest.
5. Report what shipped. Machines install it within the hour; the עדכונים button on the MahodAI
   ribbon tab installs it at once (it loads at the next AutoCAD start).

Never: build or publish this repo's standalone installer, merge `ci/release-pipeline`, push with
force or `--no-verify`, or edit the plugin's copy of this tool by hand.

$ARGUMENTS
