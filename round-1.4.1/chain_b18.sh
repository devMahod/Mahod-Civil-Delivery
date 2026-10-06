#!/usr/bin/env bash
# b18 release chain with exit-code gates (Codex 02:49). Stops at the first failing step; prints the MANIFEST hash only
# after lanes, 2026 build, archive, build-setup and Test-Installer all succeeded and the MANIFEST is newer than the start.
# Usage: bash chain_b18.sh <prev-tag> <tag> [<guide-pdf-windows-path> <guide-sha256>]   (no guide = lanes only)
PREV=$1; TAG=$2; GUIDE=$3; GSHA=$4
ROOT=/c/Users/arthurf/Downloads/Cutz/Work/civil-boq-v2
MCD=$ROOT/MahodCivilDelivery
GATE_LOG=$MCD/tests-1.4.0-$TAG.log
GATE_DIR=$MCD/chain-1.4.0-$TAG
mkdir -p "$GATE_DIR"
: > "$GATE_LOG"
. "$(dirname "$0")/gate.sh"
START=$(date +%s)
echo "START $(date '+%Y-%m-%d %H:%M:%S')" >> "$GATE_LOG"
stop() { echo "CHAIN STOPPED at $1 — no setup / no MANIFEST reported" >> "$GATE_LOG"; cat "$GATE_LOG"; exit 1; }

cd "$ROOT" || stop cd-root
run_step CORE "Passed!" timeout 1800 dotnet test MahodAI.Core.Tests/MahodAI.Core.Tests.csproj -c Release -p:AutoCADVersion=2027 -nologo -v q --filter "Category!=RequiresCivil3D" || stop CORE
run_step PLUGIN "Passed!" timeout 1800 dotnet test MahodAI.Civil3D.Plugin.Tests/MahodAI.Civil3D.Plugin.Tests.csproj -c Release -p:AutoCADVersion=2027 -p:DeployPlugin=false -nologo -v q --filter "Category!=RequiresCivil3D" || stop PLUGIN
cd "$MCD" || stop cd-mcd
run_step STDCORE "Passed!" timeout 1800 dotnet test Mahod.CivilDelivery.Core.Tests/Mahod.CivilDelivery.Core.Tests.csproj -c Release -nologo -v q --filter "Category!=RequiresCivil3D" || stop STDCORE
run_step STD "Passed!" timeout 1800 dotnet test Mahod.CivilDelivery.Tests/Mahod.CivilDelivery.Tests.csproj -c Release -nologo -v q --filter "Category!=RequiresCivil3D" || stop STD
run_step B2026G "Build succeeded" timeout 900 dotnet build Mahod.CivilDelivery/Mahod.CivilDelivery.csproj -c Release -p:AutoCADVersion=2026 -p:MahodCivilDeliveryGuideEnabled=true -nologo -v q || stop B2026G
if [ -z "$GUIDE" ]; then echo "LANES OK - no guide given, setup not built" >> "$GATE_LOG"; cat "$GATE_LOG"; exit 0; fi

ARCH=out/_archive-1.4.0-$PREV
run_step ARCHIVE "" bash -c "mkdir -p '$ARCH' && cp out/stage/payload/MANIFEST.json out/Mahod_Civil_Delivery_Setup_1.4.0.exe out/DEFENDER_SCAN_1.4.0.json '$ARCH/'" || stop ARCHIVE
run_step SETUP "Defender: clean" powershell.exe -NoProfile -ExecutionPolicy Bypass -File installer/build-setup.ps1 -GuidePdf "$GUIDE" -ExpectedGuideSha256 "$GSHA" -ExpectedVersion 1.4.0 || stop SETUP
cp "$GATE_DIR/SETUP.out" "out-build-1.4.0-$TAG.log"
grep -E "Defender:|setup Mahod|2026: plugin|2027: plugin|Guide|guide|payload verification" "$GATE_DIR/SETUP.out" >> "$GATE_LOG"
run_step INSTALLER "[0-9]+ passed, 0 failed" powershell.exe -NoProfile -ExecutionPolicy Bypass -File installer/Test-Installer.ps1 || stop INSTALLER
cp "$GATE_DIR/INSTALLER.out" "test-installer-1.4.0-$TAG.log"
echo "INSTALLER $(tail -1 "$GATE_DIR/INSTALLER.out")" >> "$GATE_LOG"
M=out/stage/payload/MANIFEST.json
[ -f "$M" ] || stop MANIFEST-missing
[ "$(stat -c %Y "$M")" -ge "$START" ] || stop MANIFEST-not-rebuilt
echo "MANIFEST $(sha256sum "$M" | cut -c1-64)" >> "$GATE_LOG"
echo "SETUP $(sha256sum out/Mahod_Civil_Delivery_Setup_1.4.0.exe | cut -c1-64)" >> "$GATE_LOG"
echo "CHAIN OK $(date '+%Y-%m-%d %H:%M:%S')" >> "$GATE_LOG"
cat "$GATE_LOG"
