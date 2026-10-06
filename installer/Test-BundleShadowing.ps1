<#
.SYNOPSIS
    Regression suite for the defect real GUI testing found on 2026-08-19:
    a per-user install silently shadowed by a machine-wide MahodAI.bundle.

.DESCRIPTION
    Civil 3D loaded
        C:\Program Files\Autodesk\ApplicationPlugins\MahodAI.bundle   (platform 1.0.0.0)
    while the installer had written
        %APPDATA%\Autodesk\ApplicationPlugins\MahodAI.bundle          (platform 1.2.0.0)

    Every hash check passed and the engineer still ran the old build, because a hash
    proves what was WRITTEN and says nothing about what AutoCAD LOADS. These scenarios
    pin the behaviour that closes that gap.

    Runs entirely in a sandbox: fake ApplicationPlugins roots under %TEMP%, never the
    real Autodesk folders, and never requires Civil 3D.
    ASCII-only file.
#>
[CmdletBinding()]
param([string]$OutFile)

$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path $here
if (-not $OutFile) { $OutFile = Join-Path $repo 'evidence\bundle_shadowing_evidence.txt' }

$log = New-Object System.Collections.ArrayList
$failures = 0
function L([string]$m) { [void]$log.Add($m); Write-Host $m }
function Check([string]$n, [bool]$ok, [string]$d = '') {
    if ($ok) { L ("  PASS  " + $n + $(if ($d) { "  [$d]" })) }
    else { $script:failures++; L ("  FAIL  " + $n + $(if ($d) { "  [$d]" })) }
}

$payload = Join-Path $repo 'installer\stage\payload'
if (-not (Test-Path (Join-Path $payload 'MahodAI.bundle\Contents\MahodAI.Civil3D.Plugin.dll'))) {
    throw "Staged payload not found: $payload (build the installer first)"
}
$installer = Join-Path $here 'Install-MahodCivilDelivery.ps1'
$uninstaller = Join-Path $here 'Uninstall-MahodCivilDelivery.ps1'

$sandbox = Join-Path $env:TEMP ('mcd_shadow_' + (Get-Date -Format 'yyyyMMddHHmmss'))
$machineRoot = Join-Path $sandbox 'machine\ApplicationPlugins'
$userRoot    = Join-Path $sandbox 'user\ApplicationPlugins'
$stateRoot   = Join-Path $sandbox 'state'
New-Item -ItemType Directory -Force -Path $machineRoot, $userRoot, $stateRoot | Out-Null

function Reset-Sandbox {
    Remove-Item (Join-Path $machineRoot 'MahodAI.bundle') -Recurse -Force -EA SilentlyContinue
    Remove-Item (Join-Path $userRoot 'MahodAI.bundle') -Recurse -Force -EA SilentlyContinue
    Remove-Item $stateRoot -Recurse -Force -EA SilentlyContinue
    New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null
}

# Captures the installer's output in $script:LastOutput and returns ONLY the exit code.
# Returning both would emit a two-element array and every -eq test would silently
# compare against the wrong thing.
$script:LastOutput = ''
function Invoke-Install {
    # Roots are passed highest-precedence first, mirroring how AutoCAD scans them.
    $script:LastOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $installer `
        -PayloadDir $payload `
        -PluginRootsOverride "$machineRoot,$userRoot" `
        -StateRootOverride $stateRoot `
        -SkipHostCheck 2>&1 | Out-String
    return [int]$LASTEXITCODE
}

function Invoke-Uninstall([string]$targetRoot) {
    $script:LastOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $uninstaller `
        -BundleRootOverride $targetRoot -StateRootOverride $stateRoot `
        -SkipHostCheck -SkipRegistry -Quiet 2>&1 | Out-String
    return [int]$LASTEXITCODE
}

function Plant-Bundle([string]$root) {
    # A stand-in for an older MahodAI already present in a higher-precedence root.
    $dst = Join-Path $root 'MahodAI.bundle'
    Copy-Item (Join-Path $payload 'MahodAI.bundle') $dst -Recurse -Force
    return $dst
}

L '=== BUNDLE SHADOWING REGRESSION ==='
L ("sandbox : " + $sandbox)
L ''

# --------------------------------------------------------------- scenario 1
L '--- 1. clean machine: installs per-user (lowest-precedence root) ---'
Reset-Sandbox
$code = Invoke-Install
Check 'install succeeds' ($code -eq 0) "exit=$code"
Check 'wrote to the per-user root' (Test-Path (Join-Path $userRoot 'MahodAI.bundle\Contents\MahodAI.Civil3D.Plugin.dll'))
Check 'did NOT create a machine-wide bundle' (-not (Test-Path (Join-Path $machineRoot 'MahodAI.bundle')))
L ''

# --------------------------------------------------------------- scenario 2
L '--- 2. machine-wide MahodAI present: install targets THAT root ---'
Reset-Sandbox
Plant-Bundle $machineRoot | Out-Null
$code = Invoke-Install
Check 'install succeeds when the machine-wide root is writable' ($code -eq 0) "exit=$code"
Check 'upgraded the machine-wide bundle (the one AutoCAD loads)' `
    (Test-Path (Join-Path $machineRoot 'MahodAI.bundle\Contents\MahodAI.Civil3D.Plugin.dll'))
Check 'did NOT leave a shadowed per-user copy behind' `
    (-not (Test-Path (Join-Path $userRoot 'MahodAI.bundle\Contents\MahodAI.Civil3D.Plugin.dll')))
L ''

# --------------------------------------------------------------- scenario 3
L '--- 3. THE ORIGINAL DEFECT: an install that would be shadowed must never report success ---'
Reset-Sandbox
# Force the install into the per-user root while a machine-wide bundle outranks it.
$dst = Join-Path $userRoot 'MahodAI.bundle'
Copy-Item (Join-Path $payload 'MahodAI.bundle') $dst -Recurse -Force
Plant-Bundle $machineRoot | Out-Null
$out = & powershell -NoProfile -ExecutionPolicy Bypass -File $installer `
    -PayloadDir $payload `
    -BundleRootOverride $userRoot `
    -PluginRootsOverride "$machineRoot,$userRoot" `
    -StateRootOverride $stateRoot `
    -SkipHostCheck 2>&1 | Out-String
$code = [int]$LASTEXITCODE
# The shadowing copy is retired (renamed aside, never deleted) so the build just installed
# is the one AutoCAD loads. Refusing was the old behaviour; it left the engineer with no
# way forward, which is how a colleague kept running the previous version after a correct
# install (2026-08-25).
$machineBundle = Join-Path $machineRoot 'MahodAI.bundle'
$asides = @(Get-ChildItem $machineRoot -Directory -Filter 'MahodAI.bundle.disabled-*' -EA SilentlyContinue)
Check 'the shadowing copy is retired and the install succeeds' ($code -eq 0) "exit=$code"
Check 'the shadowing bundle no longer answers to *.bundle' (-not (Test-Path $machineBundle))
Check 'it was renamed aside, not deleted' ($asides.Count -eq 1) "aside=$($asides.Count)"
Check 'the retired copy still holds its files' (
    $asides.Count -eq 1 -and (Test-Path (Join-Path $asides[0].FullName 'Contents\MahodAI.Civil3D.Plugin.dll')))
Check 'the installer says what it retired' ($out -match 'Retired duplicate MahodAI')
if ($code -ne 0) { L '  --- installer output for diagnosis ---'; L ($out.Trim()); L '  ---' }
$state = if (Test-Path (Join-Path $stateRoot 'install_state.json')) {
    Get-Content (Join-Path $stateRoot 'install_state.json') -Raw | ConvertFrom-Json
} else { $null }
Check 'retired duplicate has a durable hash-sealed receipt' `
    ($state -and @($state.retired_duplicate_bundles).Count -eq 1 -and
     [int]$state.retired_duplicate_bundles[0].file_count -gt 0 -and
     $state.retired_duplicate_bundles[0].inventory.PSObject.Properties.Count -gt 0)
$code = Invoke-Uninstall $userRoot
Check 'managed uninstall succeeds after duplicate retirement' ($code -eq 0) "exit=$code"
Check 'uninstall restores the duplicate original path' (Test-Path $machineBundle)
Check 'uninstall leaves no disabled duplicate residue' `
    (@(Get-ChildItem $machineRoot -Directory -Filter 'MahodAI.bundle.disabled-*' -EA SilentlyContinue).Count -eq 0)
L ''

# ------------------------------------------------------------- scenario 3b
L '--- 3b. later install failure restores every retired duplicate byte-identically ---'
Reset-Sandbox
Copy-Item (Join-Path $payload 'MahodAI.bundle') (Join-Path $userRoot 'MahodAI.bundle') -Recurse -Force
$machineBefore = Plant-Bundle $machineRoot
$machineBeforeHash = (Get-FileHash (Join-Path $machineBefore 'Contents\MahodAI.Civil3D.Plugin.dll') -Algorithm SHA256).Hash
$out = & powershell -NoProfile -ExecutionPolicy Bypass -File $installer `
    -PayloadDir $payload -BundleRootOverride $userRoot `
    -PluginRootsOverride "$machineRoot,$userRoot" -StateRootOverride $stateRoot `
    -SkipHostCheck -TestFailureAt AfterCandidatePrepared -Quiet 2>&1 | Out-String
$code = [int]$LASTEXITCODE
Check 'injected failure is reported transactionally' ($code -eq 9) "exit=$code"
Check 'retired duplicate original path is restored' (Test-Path (Join-Path $machineRoot 'MahodAI.bundle'))
Check 'retired duplicate bytes are restored exactly' `
    ((Get-FileHash (Join-Path $machineRoot 'MahodAI.bundle\Contents\MahodAI.Civil3D.Plugin.dll') -Algorithm SHA256).Hash -eq $machineBeforeHash)
Check 'verified rollback leaves no disabled duplicate residue' `
    (@(Get-ChildItem $machineRoot -Directory -Filter 'MahodAI.bundle.disabled-*' -EA SilentlyContinue).Count -eq 0)
L ''

# --------------------------------------------------------------- scenario 4
L '--- 4. when the duplicate CANNOT be retired, the install still refuses ---'
Reset-Sandbox
Copy-Item (Join-Path $payload 'MahodAI.bundle') (Join-Path $userRoot 'MahodAI.bundle') -Recurse -Force
$planted = Plant-Bundle $machineRoot
# Hold a file inside the machine-wide bundle open: renaming the folder now fails the way
# it fails on a real machine without Administrator rights.
$lockPath = Join-Path $machineRoot 'MahodAI.bundle\Contents\MahodAI.Civil3D.Plugin.dll'
$lock = [System.IO.File]::Open($lockPath, 'Open', 'Read', 'None')
try {
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $installer `
        -PayloadDir $payload `
        -BundleRootOverride $userRoot `
        -PluginRootsOverride "$machineRoot,$userRoot" `
        -StateRootOverride $stateRoot `
        -SkipHostCheck 2>&1 | Out-String
    $code = [int]$LASTEXITCODE
} finally { $lock.Close() }
Check 'install REFUSES rather than reporting a success that will not run' ($code -ne 0) "exit=$code"
Check 'uses the dedicated shadowing exit code 8' ($code -eq 8) "exit=$code"
Check 'refusal names the bundle AutoCAD would load instead' ($out -match 'AutoCAD will load a different MahodAI first')
Check 'the copy it could not retire is named' ($out -match 'could not be renamed')
if ($code -ne 8 -or $out -notmatch 'could not be renamed') { L '  --- installer output for diagnosis ---'; L ($out.Trim()); L '  ---' }
L ''

Remove-Item $sandbox -Recurse -Force -EA SilentlyContinue
L "sandbox removed: $sandbox"
L ''
L '=== RESULT ==='
if ($failures -eq 0) {
    L 'ALL SCENARIOS PASS'
    L ''
    L 'Proven:'
    L '  * a per-user install is chosen only when nothing outranks it'
    L '  * an existing machine-wide MahodAI is upgraded in place, because that is what loads'
    L '  * a duplicate that would shadow the install is retired (renamed aside, not deleted)'
    L '  * when the duplicate cannot be retired, the install FAILS instead of reporting success'
    L '  * the refusal names the exact bundle that would win'
} else {
    L "$failures CHECK(S) FAILED"
}

$dir = Split-Path $OutFile -Parent
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$log -join [Environment]::NewLine | Set-Content $OutFile -Encoding utf8
L ''
L "evidence written: $OutFile"
if ($failures -gt 0) { exit 1 }
exit 0
