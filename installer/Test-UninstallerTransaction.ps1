<#
.SYNOPSIS
    Isolated regression suite for transactional uninstall and routing.

.DESCRIPTION
    Runs the production uninstaller only against GUID-named directories under TEMP.
    It never opens Civil 3D, touches a real ApplicationPlugins root, writes HKCU, or
    triggers UAC. ASCII-only file. Windows PowerShell 5.1 compatible.
#>
[CmdletBinding()]
param(
    [string]$PayloadDir,
    [string]$OutFile
)

$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $here
if (-not $PayloadDir) { $PayloadDir = Join-Path $here 'stage\payload' }
if (-not $OutFile) { $OutFile = Join-Path $repo 'evidence\uninstaller_transaction_evidence.txt' }
$PayloadDir = [System.IO.Path]::GetFullPath($PayloadDir)

$sandbox = Join-Path $env:TEMP ('mcd_uninstaller_' + [guid]::NewGuid().ToString('N'))
$pluginsRoot = Join-Path $sandbox 'ApplicationPlugins'
$bundle = Join-Path $pluginsRoot 'MahodAI.bundle'
$stateRoot = Join-Path $sandbox 'civil-delivery'
$stateFile = Join-Path $stateRoot 'install_state.json'
$installer = Join-Path $here 'Install-MahodCivilDelivery.ps1'
$uninstaller = Join-Path $here 'Uninstall-MahodCivilDelivery.ps1'

$log = New-Object System.Collections.ArrayList
$failures = 0
function L([string]$message) { [void]$log.Add($message); Write-Host $message }
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $line = $(if ($ok) { '  PASS  ' } else { '  FAIL  ' }) + $name + $(if ($detail) { "  [$detail]" } else { '' })
    L $line
    if (-not $ok) { $script:failures++ }
}
function Sha([string]$path) {
    if (Test-Path -LiteralPath $path -PathType Leaf) { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    return $null
}
function Inv([string]$root) {
    $map = @{}
    if (Test-Path -LiteralPath $root -PathType Container) {
        foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
            $map[$file.FullName.Substring($root.Length + 1)] = Sha $file.FullName
        }
    }
    return $map
}
function SameInv([hashtable]$expected, [hashtable]$actual) {
    if ($expected.Count -ne $actual.Count) { return $false }
    foreach ($rel in $expected.Keys) {
        if (-not $actual.ContainsKey($rel) -or $actual[$rel] -ne $expected[$rel]) { return $false }
    }
    return $true
}
function Put([string]$path, [string]$content) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    Set-Content -LiteralPath $path -Value $content -Encoding ascii
}
function ResetSandbox {
    if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $pluginsRoot, $stateRoot | Out-Null
}
function RunInstall([string[]]$extra) {
    $args = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$installer,
        '-PayloadDir',$PayloadDir,'-BundleRootOverride',$pluginsRoot,
        '-StateRootOverride',$stateRoot,'-SkipHostCheck','-Quiet') + $extra
    $output = & powershell @args 2>&1 | Out-String
    return @{ ExitCode = [int]$LASTEXITCODE; Output = $output }
}
function RunUninstall([string[]]$extra, [switch]$NoBundlePin) {
    $args = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$uninstaller,
        '-StateRootOverride',$stateRoot,'-SkipHostCheck','-SkipRegistry','-Quiet')
    if (-not $NoBundlePin) { $args += @('-BundleRootOverride',$pluginsRoot) }
    $args += $extra
    $output = & powershell @args 2>&1 | Out-String
    return @{ ExitCode = [int]$LASTEXITCODE; Output = $output }
}
function WriteSyntheticState(
    [string]$targetBundle,
    [hashtable]$ownedHashes,
    [bool]$previousExisted,
    [string]$restorePoint,
    [hashtable]$restoreInventory,
    [bool]$machineWide = $false
) {
    $owned = @{}
    foreach ($rel in $ownedHashes.Keys) {
        $owned[$rel] = [ordered]@{ sha256 = $ownedHashes[$rel]; overwrote_existing = $false; previous_sha256 = $null }
    }
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $state = [ordered]@{
        schema_version = 3
        package_revision = 'test'
        platform_version = '0.0.0.0'
        installed_user_sid = $sid
        machine_wide = $machineWide
        state_root = $stateRoot
        uninstall_registry_path = $(if ($machineWide) {
            "Registry::HKEY_USERS\$sid\Software\Microsoft\Windows\CurrentVersion\Uninstall\MahodCivilDelivery"
        } else { 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MahodCivilDelivery' })
        bundle_path = $targetBundle
        previous_platform = [ordered]@{
            existed = $previousExisted
            version = $(if ($previousExisted) { '0.0.0.0' } else { $null })
            file_count = $restoreInventory.Count
            restore_point = $(if ($previousExisted) { $restorePoint } else { $null })
            inventory = $restoreInventory
        }
        owned_files = $owned
        preserved_foreign_files = @()
    }
    New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null
    $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $stateFile -Encoding utf8
}

L '=== UNINSTALLER TRANSACTION REGRESSION ==='
L ("generated_utc : " + (Get-Date).ToUniversalTime().ToString('o'))
L ("sandbox       : $sandbox")
L 'No Civil, real ApplicationPlugins, HKCU mutation, Program Files mutation, or UAC is used.'
L ''

try {
    if (-not (Test-Path -LiteralPath (Join-Path $PayloadDir 'MahodAI.bundle\PackageContents.xml') -PathType Leaf)) {
        throw "Complete staged payload not found: $PayloadDir"
    }

    # =====================================================================
    L '--- 1. two repairs then uninstall restores the original byte-identically ---'
    ResetSandbox
    Copy-Item -LiteralPath (Join-Path $PayloadDir 'MahodAI.bundle') -Destination $bundle -Recurse -Force
    Put (Join-Path $bundle 'Contents\ORIGINAL-FOREIGN.txt') 'original-before-civil-delivery'
    $original = Inv $bundle

    $first = RunInstall @()
    $second = RunInstall @()
    Check 'first install succeeds' ($first.ExitCode -eq 0) "exit=$($first.ExitCode)"
    Check 'second repair succeeds' ($second.ExitCode -eq 0) "exit=$($second.ExitCode)"
    $un = RunUninstall @()
    Check 'uninstall after two repairs succeeds' ($un.ExitCode -eq 0) "exit=$($un.ExitCode)"
    Check 'original bundle restored with exact file set and SHA256 values' (SameInv $original (Inv $bundle)) `
        "expected=$($original.Count) actual=$((Inv $bundle).Count)"
    L ''

    # =====================================================================
    L '--- 2. every failure seam restores live bundle, state, and restore point ---'
    foreach ($point in @('AfterCandidatePrepared','AfterBundleMoved','AfterBundleSwap','AfterStateMoved','AfterRegistryRemoved')) {
        ResetSandbox
        $restore = Join-Path $stateRoot 'restore-points\original'
        Put (Join-Path $restore 'Contents\platform.bin') 'platform-before-install'
        Put (Join-Path $restore 'Contents\foreign.bin') 'foreign-before-install'
        Copy-Item -LiteralPath $restore -Destination $bundle -Recurse -Force
        Put (Join-Path $bundle 'Contents\platform.bin') 'civil-delivery-platform'
        Put (Join-Path $bundle 'Contents\post-install.txt') 'post-install-foreign'
        $ownedHashes = @{ 'Contents\platform.bin' = (Sha (Join-Path $bundle 'Contents\platform.bin')) }
        WriteSyntheticState $bundle $ownedHashes $true $restore (Inv $restore)
        $oldBundle = Inv $bundle
        $oldRestore = Inv $restore
        $oldStateSha = Sha $stateFile

        $result = RunUninstall @('-TestFailureAt',$point)
        Check "$point returns transactional failure" ($result.ExitCode -eq 9) "exit=$($result.ExitCode)"
        Check "$point restores old live bundle byte-identical" (SameInv $oldBundle (Inv $bundle))
        Check "$point restores install_state byte-identical" ((Sha $stateFile) -eq $oldStateSha)
        Check "$point keeps restore point byte-identical" (SameInv $oldRestore (Inv $restore))
        $left = @(Get-ChildItem -LiteralPath $pluginsRoot -Directory -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like 'MahodAI.uninstall-candidate-*' -or $_.Name -like 'MahodAI.uninstall-rollback-*' })
        Check "$point leaves no transaction sibling after verified rollback" ($left.Count -eq 0) "$($left.Count) found"
    }
    L ''

    # =====================================================================
    L '--- 3. first-install removal keeps changed owned and foreign files ---'
    ResetSandbox
    $unchanged = Join-Path $bundle 'Contents\owned-unchanged.bin'
    $changed = Join-Path $bundle 'Contents\owned-changed.bin'
    $foreign = Join-Path $bundle 'Contents\foreign-after-install.txt'
    Put $unchanged 'package-owned-unchanged'
    Put $changed 'engineer-changed-this-file'
    Put $foreign 'another-tool-file'
    $packageOriginal = Join-Path $sandbox 'package-original.bin'
    Put $packageOriginal 'package-original-before-edit'
    $ownedHashes = @{
        'Contents\owned-unchanged.bin' = (Sha $unchanged)
        'Contents\owned-changed.bin' = (Sha $packageOriginal)
    }
    WriteSyntheticState $bundle $ownedHashes $false $null @{}
    $changedSha = Sha $changed
    $foreignSha = Sha $foreign

    $result = RunUninstall @()
    Check 'first-install uninstall succeeds' ($result.ExitCode -eq 0) "exit=$($result.ExitCode)"
    Check 'unchanged package-owned file is removed' (-not (Test-Path -LiteralPath $unchanged))
    Check 'changed formerly-owned file is retained byte-identical' ((Sha $changed) -eq $changedSha)
    Check 'foreign file is retained byte-identical' ((Sha $foreign) -eq $foreignSha)
    Check 'final bundle inventory contains only changed/foreign files' ((Inv $bundle).Count -eq 2) "count=$((Inv $bundle).Count)"
    L ''

    # =====================================================================
    L '--- 4. state.bundle_path routes a machine-location sandbox, not APPDATA ---'
    ResetSandbox
    $machinePlugins = Join-Path $sandbox 'fake-program-files\Autodesk\ApplicationPlugins'
    $machineBundle = Join-Path $machinePlugins 'MahodAI.bundle'
    $fakeAppData = Join-Path $sandbox 'fake-user-appdata'
    $decoy = Join-Path $fakeAppData 'Autodesk\ApplicationPlugins\MahodAI.bundle'
    Put (Join-Path $machineBundle 'Contents\owned.bin') 'owned-machine-target'
    Put (Join-Path $decoy 'Contents\decoy.bin') 'must-never-be-touched'
    $decoyBefore = Inv $decoy
    $ownedHashes = @{ 'Contents\owned.bin' = (Sha (Join-Path $machineBundle 'Contents\owned.bin')) }
    WriteSyntheticState $machineBundle $ownedHashes $false $null @{} $false
    $savedAppData = $env:APPDATA
    try {
        $env:APPDATA = $fakeAppData
        $result = RunUninstall @() -NoBundlePin
    } finally { $env:APPDATA = $savedAppData }
    Check 'state-routed uninstall succeeds' ($result.ExitCode -eq 0) "exit=$($result.ExitCode)"
    Check 'state.bundle_path target was removed' (-not (Test-Path -LiteralPath $machineBundle))
    Check 'APPDATA decoy remained byte-identical' (SameInv $decoyBefore (Inv $decoy))
    L ''

    # =====================================================================
    L '--- 5. machine-wide elevation plan is explicit, protected, and non-mutating ---'
    ResetSandbox
    Put (Join-Path $bundle 'Contents\owned.bin') 'machine-wide-owned'
    New-Item -ItemType Directory -Force -Path (Join-Path $bundle 'Installer') | Out-Null
    Copy-Item -LiteralPath $uninstaller -Destination (Join-Path $bundle 'Installer\Uninstall-MahodCivilDelivery.ps1') -Force
    $ownedHashes = @{ 'Contents\owned.bin' = (Sha (Join-Path $bundle 'Contents\owned.bin')) }
    WriteSyntheticState $bundle $ownedHashes $false $null @{} $true
    $beforePlanBundle = Inv $bundle
    $beforePlanState = Sha $stateFile
    $result = RunUninstall @('-SelfTestElevationPlan')
    Check 'elevation-plan seam succeeds without opening UAC' ($result.ExitCode -eq 0) "exit=$($result.ExitCode)"
    Check 'plan uses absolute System32 Windows PowerShell' ($result.Output -match 'System32\\WindowsPowerShell\\v1.0\\powershell.exe')
    Check 'plan names protected bundle uninstaller' ($result.Output -match 'MahodAI.bundle\\Installer\\Uninstall-MahodCivilDelivery.ps1')
    Check 'plan carries original SID and state root' ($result.Output -match 'sid=S-1-' -and $result.Output -match [regex]::Escape($stateRoot))
    Check 'elevation plan leaves live bundle exact' (SameInv $beforePlanBundle (Inv $bundle))
    Check 'elevation plan leaves state exact' ((Sha $stateFile) -eq $beforePlanState)
    $sourceText = Get-Content -LiteralPath $uninstaller -Raw
    Check 'production relaunch uses RunAs' ($sourceText -match 'Start-Process.+-Verb RunAs')
    Check 'production relaunch rejects user-writable script path' ($sourceText -match 'uninstaller is not the protected bundle copy')
    L ''
}
finally {
    if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue }
    L "sandbox removed: $sandbox"
}

L ''
L '=== RESULT ==='
if ($failures -eq 0) {
    L 'ALL SCENARIOS PASS'
    L 'Proven: exact restore after repairs; atomic rollback at every seam; ownership-safe removal; state-path routing; protected self-elevation plan.'
} else {
    L "$failures CHECK(S) FAILED"
}

$outDir = Split-Path -Parent $OutFile
if ($outDir -and -not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
$log -join [Environment]::NewLine | Set-Content -LiteralPath $OutFile -Encoding utf8
L "evidence written: $OutFile"
if ($failures -gt 0) { exit 1 }
exit 0
