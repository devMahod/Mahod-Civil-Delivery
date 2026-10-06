<#
.SYNOPSIS
    Proves the installer cannot destroy or downgrade an existing MahodAI, and that
    uninstall restores whatever platform was there before.

.DESCRIPTION
    Runs entirely inside a temporary sandbox (BundleRootOverride / StateRootOverride),
    so it never touches the engineer's real Autodesk installation and does not require
    closing a running Civil 3D. The install/uninstall LOGIC exercised is the real
    production code path - only the target folders differ.

    Every scenario asserts on file hashes and platform versions, not on file existence.

    ASCII-only file.
#>
[CmdletBinding()]
param(
    [string]$PayloadDir,
    [string]$OutFile
)

$ErrorActionPreference = 'Continue'

# $PSScriptRoot is not populated in param defaults under Windows PowerShell 5.1.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $PayloadDir) { $PayloadDir = Join-Path $here 'stage\payload' }
if (-not $OutFile)    { $OutFile    = Join-Path (Split-Path $here) 'evidence\installer_lifecycle_evidence.txt' }

$testTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\')
$sandbox     = [System.IO.Path]::GetFullPath((Join-Path $testTempRoot ('mcd_lifecycle_' + [guid]::NewGuid().ToString('N'))))
$pluginsRoot = Join-Path $sandbox 'ApplicationPlugins'
$bundle      = Join-Path $pluginsRoot 'MahodAI.bundle'
$stateRoot   = Join-Path $sandbox 'civil-delivery'
$stateFile   = Join-Path $stateRoot 'install_state.json'

# Validate exact fresh sandbox/cleanup targets before the production test hooks
# can create, replace, move or recursively remove anything. Never reuse a folder
# produced by another concurrent run, and never target the temp/workspace root.
if (-not $sandbox.StartsWith($testTempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path -Leaf $sandbox) -notmatch '^mcd_lifecycle_[a-f0-9]{32}$' -or
    (Test-Path -LiteralPath $sandbox)) {
    throw 'Installer lifecycle sandbox is not a fresh confined test directory.'
}
foreach ($testTarget in @($pluginsRoot, $bundle, $stateRoot, $stateFile)) {
    $testTargetFull = [IO.Path]::GetFullPath($testTarget)
    if (-not $testTargetFull.StartsWith($sandbox + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw ('Installer lifecycle target escaped sandbox: ' + $testTargetFull)
    }
}

$log = New-Object System.Collections.ArrayList
$failures = 0
function L([string]$m) { [void]$log.Add($m); Write-Host $m }
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $line = $(if ($ok) { '  PASS  ' } else { '  FAIL  ' }) + $name + $(if ($detail) { "  [$detail]" } else { '' })
    L $line
    if (-not $ok) { $script:failures++ }
}
function Sha([string]$p) { if (Test-Path $p) { (Get-FileHash $p -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null } }
function Inv([string]$root) {
    $m = @{}
    if (Test-Path $root) {
        foreach ($f in Get-ChildItem $root -Recurse -File) { $m[$f.FullName.Substring($root.Length + 1)] = Sha $f.FullName }
    }
    return $m
}
function SameInv([hashtable]$a, [hashtable]$b) {
    if ($a.Count -ne $b.Count) { return $false }
    foreach ($key in $a.Keys) { if ($b[$key] -ne $a[$key]) { return $false } }
    return $true
}
function StateInventory($jsonObject) {
    $m = @{}
    if ($jsonObject) {
        foreach ($p in $jsonObject.PSObject.Properties) { $m[$p.Name] = ([string]$p.Value).ToLowerInvariant() }
    }
    return $m
}
function PlatVer([string]$root) {
    $dll = Join-Path $root 'Contents\MahodAI.Civil3D.Plugin.dll'
    if (Test-Path $dll) {
        try { return [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $dll)).FileVersion } catch { return $null }
    }
    return $null
}
function CheckReceiptIdentity($state, [string]$context) {
    $actualPackageRevision = if ($state) { [string]$state.package_revision } else { '' }
    $actualPlatformVersion = if ($state) { [string]$state.platform_version } else { '' }
    Check "$context receipt package_revision matches payload AppVersion" `
        ($state -and $actualPackageRevision -eq $script:expectedPackageRevision) `
        "receipt=$actualPackageRevision payload=$($script:expectedPackageRevision)"
    Check "$context receipt platform_version matches payload DLL FileVersion" `
        ($state -and $actualPlatformVersion -eq $script:expectedPlatformVersion) `
        "receipt=$actualPlatformVersion payload=$($script:expectedPlatformVersion)"
}
function RunInstall([string[]]$extra) {
    $a = @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $here 'Install-MahodCivilDelivery.ps1'),
           '-PayloadDir',$PayloadDir,'-BundleRootOverride',$pluginsRoot,'-StateRootOverride',$stateRoot,
           '-SkipHostCheck','-Quiet') + $extra
    $out = & powershell @a 2>&1 | Out-String
    return @{ Output = $out; ExitCode = $LASTEXITCODE }
}
function RunUninstall([string[]]$extra) {
    $a = @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $here 'Uninstall-MahodCivilDelivery.ps1'),
           '-BundleRootOverride',$pluginsRoot,'-StateRootOverride',$stateRoot,'-SkipHostCheck','-Quiet') + $extra
    $out = & powershell @a 2>&1 | Out-String
    return @{ Output = $out; ExitCode = $LASTEXITCODE }
}

L '=== Installer lifecycle evidence - MahodAI platform safety ==='
L ("generated_utc : " + (Get-Date).ToUniversalTime().ToString('o'))
L ("machine       : $env:COMPUTERNAME  user: $env:USERNAME")
L ("payload       : $PayloadDir")
L ("sandbox       : $sandbox")
L ''
L 'The real %APPDATA% Autodesk folder is NOT touched by this suite. The production'
L 'install/uninstall code runs against a temporary bundle root instead.'
L ''

$payloadBundle = Join-Path $PayloadDir 'MahodAI.bundle'
$payloadManifestPath = Join-Path $payloadBundle 'PackageContents.xml'
$script:expectedPackageRevision = $null
if (Test-Path -LiteralPath $payloadManifestPath -PathType Leaf) {
    try {
        $payloadManifest = [xml](Get-Content -LiteralPath $payloadManifestPath -Raw)
        $script:expectedPackageRevision = [string]$payloadManifest.ApplicationPackage.AppVersion
    } catch {
        Check 'payload PackageContents.xml parses' $false $_.Exception.Message
    }
} else {
    Check 'payload PackageContents.xml present' $false $payloadManifestPath
}
Check 'payload AppVersion identified for receipt verification' `
    (-not [string]::IsNullOrWhiteSpace($script:expectedPackageRevision)) `
    "AppVersion=$($script:expectedPackageRevision)"
$script:expectedPlatformVersion = PlatVer $payloadBundle
Check 'payload DLL FileVersion identified for receipt verification' `
    (-not [string]::IsNullOrWhiteSpace($script:expectedPlatformVersion)) `
    "FileVersion=$($script:expectedPlatformVersion)"
L ''

New-Item -ItemType Directory -Force -Path $pluginsRoot, $stateRoot | Out-Null

try {
    # =====================================================================
    L '--- SCENARIO 1: a pre-existing MahodAI survives the Civil Delivery install ---'
    New-Item -ItemType Directory -Force -Path (Join-Path $bundle 'Contents') | Out-Null
    Copy-Item (Join-Path $PayloadDir 'MahodAI.bundle\*') $bundle -Recurse -Force

    # A file this package does NOT ship, standing in for unrelated MahodAI functionality.
    $foreignFile = Join-Path $bundle 'Contents\MahodAI.OtherFeature.dll'
    Set-Content $foreignFile -Value 'pretend unrelated MahodAI tool' -Encoding ascii
    $foreignSha = Sha $foreignFile
    $preInv = Inv $bundle
    $preVer = PlatVer $bundle

    $r = RunInstall @()
    Check 'install succeeded' ($r.ExitCode -eq 0) "exit=$($r.ExitCode)"
    Check 'bundle still present' (Test-Path $bundle)
    Check 'unrelated MahodAI file preserved byte-identical' ((Sha $foreignFile) -eq $foreignSha)
    Check 'install state recorded' (Test-Path $stateFile)

    if (Test-Path $stateFile) {
        $st = Get-Content $stateFile -Raw | ConvertFrom-Json
        CheckReceiptIdentity $st 'upgrade install'
        Check 'previous platform detected' ($st.previous_platform.existed -eq $true) "version=$($st.previous_platform.version)"
        Check 'managed restore point created' ([bool]$st.previous_platform.restore_point -and (Test-Path $st.previous_platform.restore_point))
        $recordedRestoreInv = StateInventory $st.previous_platform.inventory
        Check 'original restore point has a complete SHA256 inventory' (SameInv $preInv $recordedRestoreInv) `
            "expected=$($preInv.Count) recorded=$($recordedRestoreInv.Count)"
        Check 'foreign file recorded as preserved' ($st.preserved_foreign_files -contains 'Contents\MahodAI.OtherFeature.dll')
        Check 'ownership manifest written' ($st.owned_files.PSObject.Properties.Name.Count -gt 0) "$($st.owned_files.PSObject.Properties.Name.Count) files owned"
    }
    Check 'plugin dll deployed' (Test-Path (Join-Path $bundle 'Contents\MahodAI.Civil3D.Plugin.dll'))
    Check 'PackageContents at bundle root' (Test-Path (Join-Path $bundle 'PackageContents.xml'))
    L ''

    # =====================================================================
    L '--- SCENARIO 2: uninstall RESTORES the previous MahodAI platform ---'
    $r = RunUninstall @()
    Check 'uninstall succeeded' ($r.ExitCode -eq 0) "exit=$($r.ExitCode) output=$($r.Output.Trim())"
    Check 'bundle NOT deleted' (Test-Path $bundle)
    Check 'unrelated MahodAI file still intact' ((Sha $foreignFile) -eq $foreignSha)
    Check 'previous platform version restored' ((PlatVer $bundle) -eq $preVer) "before=$preVer after=$(PlatVer $bundle)"

    $postInv = Inv $bundle
    $diff = 0
    foreach ($k in $preInv.Keys) { if ($postInv[$k] -ne $preInv[$k]) { $diff++ } }
    Check 'previous platform byte-identical after restore' ($diff -eq 0) "$diff of $($preInv.Count) file(s) differ"
    L ''

    # =====================================================================
    L '--- SCENARIO 3: clean machine - uninstall removes ONLY our files ---'
    Remove-Item $bundle -Recurse -Force -EA SilentlyContinue
    Remove-Item $stateRoot -Recurse -Force -EA SilentlyContinue
    New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null

    $r = RunInstall @()
    Check 'installed on clean machine' (Test-Path (Join-Path $bundle 'Contents\MahodAI.Civil3D.Plugin.dll')) "exit=$($r.ExitCode) output=$($r.Output.Trim())"
    if (Test-Path $stateFile) {
        $st2 = Get-Content $stateFile -Raw | ConvertFrom-Json
        CheckReceiptIdentity $st2 'clean install'
        Check 'no previous platform recorded' ($st2.previous_platform.existed -eq $false)
        Check 'clean install records an empty original inventory' `
            ((StateInventory $st2.previous_platform.inventory).Count -eq 0)
    }

    # Something the installer does not own must survive uninstall.
    $strayFile = Join-Path $bundle 'Contents\SomeoneElses.txt'
    Set-Content $strayFile -Value 'not ours' -Encoding ascii

    $r = RunUninstall @()
    Check 'uninstall succeeded' ($r.ExitCode -eq 0) "exit=$($r.ExitCode) output=$($r.Output.Trim())"
    Check 'foreign file NOT removed' (Test-Path $strayFile)
    Check 'our plugin dll removed' (-not (Test-Path (Join-Path $bundle 'Contents\MahodAI.Civil3D.Plugin.dll')))
    L ''

    # =====================================================================
    L '--- SCENARIO 4: refuses to overwrite an unknown / newer MahodAI ---'
    Remove-Item $bundle -Recurse -Force -EA SilentlyContinue
    Remove-Item $stateRoot -Recurse -Force -EA SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $bundle 'Contents'), $stateRoot | Out-Null
    Copy-Item (Join-Path $PayloadDir 'MahodAI.bundle\*') $bundle -Recurse -Force

    # An installed platform whose version cannot be read must not be overwritten blind.
    $unknownDll = Join-Path $bundle 'Contents\MahodAI.Civil3D.Plugin.dll'
    Set-Content $unknownDll -Value 'not a real assembly - version unreadable' -Encoding ascii
    $unknownSha = Sha $unknownDll

    $r = RunInstall @()
    Check 'refuses to overwrite an unknown MahodAI build' ($r.ExitCode -eq 5) "exit=$($r.ExitCode)"
    Check 'nothing was changed on refusal' ((Sha $unknownDll) -eq $unknownSha)
    Check 'no install state written on refusal' (-not (Test-Path $stateFile))

    $r = RunInstall @('-AllowDowngrade')
    Check 'explicit -AllowDowngrade proceeds' ($r.ExitCode -eq 0) "exit=$($r.ExitCode)"
    Check 'platform installed after override' ((PlatVer $bundle) -ne $null) "version=$(PlatVer $bundle)"
    L ''

    # =====================================================================
    L '--- SCENARIO 5: engineer work products survive reinstall ---'
    $profDir = Join-Path $stateRoot 'profiles\6422'
    New-Item -ItemType Directory -Force -Path $profDir | Out-Null
    $profFile = Join-Path $profDir 'project-profile.yaml'
    Set-Content $profFile -Value "schema_version: 1`nprofile_id: `"6422`"`n# ENGINEER APPROVED MAPPINGS" -Encoding utf8
    $profSha = Sha $profFile

    $r = RunInstall @('-AllowDowngrade')
    Check 'reinstall succeeded' ($r.ExitCode -eq 0) "exit=$($r.ExitCode)"
    Check 'approved project profile NOT overwritten' ((Sha $profFile) -eq $profSha)
    L ''

    # =====================================================================
    L '--- SCENARIO 6: every injected transaction failure restores exact bytes ---'
    foreach ($failurePoint in @('AfterCandidatePrepared','AfterBundleSwap','AfterProfileWrite','AfterStateWrite')) {
        Remove-Item $bundle -Recurse -Force -EA SilentlyContinue
        Remove-Item $stateRoot -Recurse -Force -EA SilentlyContinue
        New-Item -ItemType Directory -Force -Path $pluginsRoot | Out-Null
        Copy-Item (Join-Path $PayloadDir 'MahodAI.bundle') $bundle -Recurse -Force
        Set-Content (Join-Path $bundle 'Contents\ORIGINAL-FOREIGN.txt') -Value "original-$failurePoint" -Encoding ascii
        $beforeFailure = Inv $bundle

        $r = RunInstall @('-TestFailureAt', $failurePoint)
        $afterFailure = Inv $bundle
        Check "$failurePoint returns failure" ($r.ExitCode -eq 9) "exit=$($r.ExitCode)"
        Check "$failurePoint restores bundle byte-identical" (SameInv $beforeFailure $afterFailure) `
            "before=$($beforeFailure.Count) after=$($afterFailure.Count)"
        Check "$failurePoint leaves no install state" (-not (Test-Path $stateFile))
        $txnDirs = @(Get-ChildItem $pluginsRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like 'MahodAI.candidate-*' -or $_.Name -like 'MahodAI.rollback-*' })
        Check "$failurePoint leaves no transaction bundle" ($txnDirs.Count -eq 0) "$($txnDirs.Count) found"
    }
    L ''

    # =====================================================================
    L '--- SCENARIO 7: repeated repair preserves the ORIGINAL uninstall chain ---'
    Remove-Item $bundle -Recurse -Force -EA SilentlyContinue
    Remove-Item $stateRoot -Recurse -Force -EA SilentlyContinue
    New-Item -ItemType Directory -Force -Path $pluginsRoot | Out-Null
    Copy-Item (Join-Path $PayloadDir 'MahodAI.bundle') $bundle -Recurse -Force
    Set-Content (Join-Path $bundle 'Contents\ORIGINAL-FOREIGN.txt') -Value 'original-before-civil-delivery' -Encoding ascii
    $originalBeforeRepairs = Inv $bundle

    $firstRepair = RunInstall @()
    Check 'first install in repair-chain scenario succeeds' ($firstRepair.ExitCode -eq 0) "exit=$($firstRepair.ExitCode)"
    $firstState = if (Test-Path $stateFile) { Get-Content $stateFile -Raw | ConvertFrom-Json } else { $null }
    $originalRestorePath = if ($firstState) { [string]$firstState.previous_platform.restore_point } else { $null }
    $originalRestoreInv = if ($originalRestorePath -and (Test-Path $originalRestorePath)) { Inv $originalRestorePath } else { @{} }
    $originalRecordedInv = if ($firstState) { StateInventory $firstState.previous_platform.inventory } else { @{} }

    # Simulate two files owned by an earlier package but no longer shipped. The
    # untouched one must retire; the locally modified one must become foreign.
    $obsoleteRel = 'Contents\obsolete-owned.dll'
    $modifiedRel = 'Contents\modified-owned.dll'
    $obsoletePath = Join-Path $bundle $obsoleteRel
    $modifiedPath = Join-Path $bundle $modifiedRel
    Set-Content $obsoletePath -Value 'old package byte' -Encoding ascii
    Set-Content $modifiedPath -Value 'old package byte before local edit' -Encoding ascii
    $firstState.owned_files | Add-Member -NotePropertyName $obsoleteRel -NotePropertyValue `
        ([pscustomobject]@{ sha256=(Sha $obsoletePath); overwrote_existing=$false; previous_sha256=$null }) -Force
    $firstState.owned_files | Add-Member -NotePropertyName $modifiedRel -NotePropertyValue `
        ([pscustomobject]@{ sha256=(Sha $modifiedPath); overwrote_existing=$false; previous_sha256=$null }) -Force
    $firstState | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $stateFile -Encoding utf8
    Set-Content $modifiedPath -Value 'engineer changed this byte' -Encoding ascii
    $modifiedSha = Sha $modifiedPath

    $secondRepair = RunInstall @()
    Check 'second repair succeeds' ($secondRepair.ExitCode -eq 0) "exit=$($secondRepair.ExitCode)"
    $secondState = if (Test-Path $stateFile) { Get-Content $stateFile -Raw | ConvertFrom-Json } else { $null }
    Check 'repair preserves original restore-point identity' `
        ($secondState -and [string]$secondState.previous_platform.restore_point -eq $originalRestorePath) `
        "first=$originalRestorePath second=$([string]$secondState.previous_platform.restore_point)"
    $restoreAfterRepair = if ($originalRestorePath -and (Test-Path $originalRestorePath)) { Inv $originalRestorePath } else { @{} }
    Check 'repair preserves original restore-point bytes' (SameInv $originalRestoreInv $restoreAfterRepair) `
        "$($restoreAfterRepair.Count) files"
    $secondRecordedInv = if ($secondState) { StateInventory $secondState.previous_platform.inventory } else { @{} }
    Check 'repair preserves original restore-point SHA manifest' (SameInv $originalRecordedInv $secondRecordedInv) `
        "$($secondRecordedInv.Count) files"
    Check 'repair retires unchanged files owned only by the older package' (-not (Test-Path $obsoletePath))
    Check 'repair preserves locally modified formerly-owned file as foreign' `
        ((Sha $modifiedPath) -eq $modifiedSha -and $secondState.preserved_foreign_files -contains $modifiedRel)

    # The preservation assertion above is complete. Remove that synthetic foreign
    # test byte before the independent restore-chain assertion, whose contract is an
    # exact original inventory when no later foreign file remains.
    Remove-Item -LiteralPath $modifiedPath -Force

    $r = RunUninstall @()
    Check 'uninstall after repeated repair succeeds' ($r.ExitCode -eq 0) "exit=$($r.ExitCode) output=$($r.Output.Trim())"
    $afterRepeatedRepairUninstall = Inv $bundle
    Check 'uninstall after repeated repair restores ORIGINAL byte-identical bundle' `
        (SameInv $originalBeforeRepairs $afterRepeatedRepairUninstall) `
        "original=$($originalBeforeRepairs.Count) restored=$($afterRepeatedRepairUninstall.Count)"
    L ''

    # =====================================================================
    L '--- SCENARIO 8: stale state for another bundle path is refused pre-mutation ---'
    $fresh = RunInstall @()
    Check 'setup for stale-state path regression succeeds' ($fresh.ExitCode -eq 0) "exit=$($fresh.ExitCode)"
    $mismatched = Get-Content $stateFile -Raw | ConvertFrom-Json
    $mismatched.bundle_path = Join-Path $sandbox 'different\MahodAI.bundle'
    $mismatched | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $stateFile -Encoding utf8
    $beforeMismatch = Inv $bundle
    $refusedMismatch = RunInstall @()
    Check 'state belonging to another bundle path is refused' ($refusedMismatch.ExitCode -eq 9) `
        "exit=$($refusedMismatch.ExitCode)"
    Check 'bundle remains byte-identical on stale-state refusal' (SameInv $beforeMismatch (Inv $bundle))
    L ''
}
finally {
    Remove-Item $sandbox -Recurse -Force -EA SilentlyContinue
    L "sandbox removed: $sandbox"
}

L ''
L '=== RESULT ==='
if ($failures -eq 0) {
    L 'ALL SCENARIOS PASS'
    L ''
    L 'Proven:'
    L '  * a pre-existing MahodAI is never destroyed by installing Civil Delivery'
    L '  * uninstall RESTORES the previous platform byte-identically (not a stray backup)'
    L '  * files this package does not own are never removed'
    L '  * an unknown or newer MahodAI is refused instead of silently overwritten'
    L '  * approved project profiles / mappings survive reinstall'
    L '  * failures before/after bundle swap and state writes roll back byte-identically'
    L '  * repeated repairs retain the original pre-Civil uninstall restore chain'
} else {
    L "$failures CHECK(S) FAILED"
}
L ''
L 'NOT proven here: the plugin loading inside Civil 3D. That belongs to the GUI gate.'

New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
$log -join [Environment]::NewLine | Set-Content $OutFile -Encoding utf8
Write-Host ''
Write-Host "evidence written: $OutFile"
if ($failures -gt 0) { exit 1 }
exit 0
