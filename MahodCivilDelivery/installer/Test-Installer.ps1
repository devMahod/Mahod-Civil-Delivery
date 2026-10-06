<#
.SYNOPSIS
    Lifecycle test of the Mahod Civil Delivery installer and uninstaller, in a sandbox.

.DESCRIPTION
    Runs the real Install-/Uninstall-MahodCivilDelivery.ps1 against a temporary ApplicationPlugins root and a
    temporary HKCU key (never the live %APPDATA% bundle or the real Add/Remove Programs entry), with the payload that
    build-setup.ps1 staged. Every refusal must leave the sandbox byte-identical. Exit 0 only if every case passes.
#>
[CmdletBinding()]
param([string]$Payload)

$ErrorActionPreference = 'Stop'
# Defaults here, not in param(): $PSScriptRoot is empty there under Windows PowerShell 5.1.
if (-not $Payload) { $Payload = Join-Path (Split-Path -Parent $PSScriptRoot) 'out\stage\payload' }
$install = Join-Path $PSScriptRoot 'Install-MahodCivilDelivery.ps1'
$uninstall = Join-Path $PSScriptRoot 'Uninstall-MahodCivilDelivery.ps1'
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ('mcd-installer-test-' + [guid]::NewGuid().ToString('N'))
$plugins = Join-Path $sandbox 'ApplicationPlugins'
$key = 'HKCU:\Software\MahodCivilDeliveryInstallerTest\' + [guid]::NewGuid().ToString('N')
$results = New-Object System.Collections.Generic.List[string]
$failed = 0

function Run([string]$script, [string[]]$extra) {
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script, '-PluginsRootOverride', $plugins,
              '-UninstallKeyOverride', $key, '-SkipHostCheck') + $extra
    & $ps @args | Out-Null
    return $LASTEXITCODE
}
function Snapshot() {
    if (-not (Test-Path $plugins)) { return '' }
    $root = (Resolve-Path $plugins).Path.TrimEnd('\') + '\'
    (Get-ChildItem $plugins -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($root.Length) + '=' + (Get-FileHash $_.FullName -Algorithm SHA256).Hash }) -join "`n"
}
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:results.Add("PASS $name") } else { $script:results.Add("FAIL $name $detail"); $script:failed++ }
}

function UninstallValuesSnapshot() {
    if (-not (Test-Path -LiteralPath $key)) { return '<absent>' }
    $values = [ordered]@{}
    (Get-ItemProperty -LiteralPath $key).PSObject.Properties |
        Where-Object { $_.Name -notin @('PSPath','PSParentPath','PSChildName','PSDrive','PSProvider') } |
        Sort-Object Name | ForEach-Object { $values[$_.Name] = $_.Value }
    return ($values | ConvertTo-Json -Depth 8 -Compress)
}

$manifest = Get-Content (Join-Path $Payload 'MANIFEST.json') -Raw | ConvertFrom-Json
$bundle = Join-Path $plugins 'Mahod.CivilDelivery.bundle'
$previous = Join-Path $plugins 'Mahod.CivilDelivery.previous'
try {
    New-Item -ItemType Directory -Force -Path $plugins | Out-Null
    # A neighbour that must never be touched (stands in for MahodAI).
    $neighbour = Join-Path $plugins 'MahodAI.bundle'
    New-Item -ItemType Directory -Force -Path (Join-Path $neighbour 'Contents') | Out-Null
    Set-Content (Join-Path $neighbour 'PackageContents.xml') '<ApplicationPackage Name="MahodAI" />'
    Set-Content (Join-Path $neighbour 'Contents\MahodAI.Civil3D.Plugin.dll') 'neighbour'
    $neighbourHash = (Get-FileHash (Join-Path $neighbour 'Contents\MahodAI.Civil3D.Plugin.dll')).Hash

    # 1. fresh install
    $code = Run $install @('-PayloadDir', $Payload)
    $files = @(Get-ChildItem $bundle -Recurse -File).Count
    Check 'fresh install exits 0' ($code -eq 0) "code=$code"
    Check 'fresh install writes exactly the manifest files' ($files -eq @($manifest.files.PSObject.Properties).Count) "files=$files"
    Check 'fresh install leaves no staging folder' (@(Get-ChildItem $plugins -Directory -Filter 'Mahod.CivilDelivery.installing-*').Count -eq 0)
    Check 'fresh install registers Add/Remove Programs' ((Get-ItemProperty $key).DisplayVersion -eq $manifest.version)
    Check 'fresh install keeps no previous copy' (-not (Test-Path $previous))

    # 2. reinstall keeps exactly one previous copy
    $code = Run $install @('-PayloadDir', $Payload)
    Check 'reinstall exits 0 and keeps the previous version once' ($code -eq 0 -and (Test-Path $previous)) "code=$code"
    $installed = Snapshot

    # 3. refusals leave everything byte-identical
    $code = Run $install @('-PayloadDir', $Payload, '-SimulateHostRunning')
    Check 'Civil running: exit 2, nothing changed' ($code -eq 2 -and (Snapshot) -eq $installed) "code=$code"

    $tampered = Join-Path $sandbox 'tampered'
    Copy-Item $Payload $tampered -Recurse
    $dll = Join-Path $tampered 'Mahod.CivilDelivery.bundle\Contents\2027\Mahod.CivilDelivery.dll'
    $bytes = [System.IO.File]::ReadAllBytes($dll); $bytes[$bytes.Length - 16] = $bytes[$bytes.Length - 16] -bxor 1
    [System.IO.File]::WriteAllBytes($dll, $bytes)
    $code = Run $install @('-PayloadDir', $tampered)
    Check 'one changed payload byte: exit 3, nothing changed' ($code -eq 3 -and (Snapshot) -eq $installed) "code=$code"

    # Help is an ordinary MANIFEST entry. This suite requires the new guide payload.
    $helpRelative = 'Help/MAHOD_CIVIL_DELIVERY_GUIDE_HE.pdf'
    $helpMember = @($manifest.files.PSObject.Properties | Where-Object { $_.Name -ceq $helpRelative })
    if ($helpMember.Count -ne 1) { throw 'Test payload MANIFEST must include the fixed Help path' }
    $helpWithinPayload = 'Mahod.CivilDelivery.bundle\Help\MAHOD_CIVIL_DELIVERY_GUIDE_HE.pdf'
    $installedHelp = Join-Path $bundle 'Help\MAHOD_CIVIL_DELIVERY_GUIDE_HE.pdf'
    Check 'fresh/reinstall includes exact guide bytes' ((Test-Path -LiteralPath $installedHelp) -and
        (Get-FileHash -LiteralPath $installedHelp -Algorithm SHA256).Hash -eq [string]$helpMember[0].Value)
    $installedReg = UninstallValuesSnapshot

    $missingHelp = Join-Path $sandbox 'missing-help'
    Copy-Item -LiteralPath $Payload -Destination $missingHelp -Recurse
    # Exact file in this run's existing temporary sandbox; no live path.
    [System.IO.File]::Delete((Join-Path $missingHelp $helpWithinPayload))
    $code = Run $install @('-PayloadDir', $missingHelp)
    Check 'missing Help: exit 3, bundle/previous/neighbour/registry unchanged' ($code -eq 3 -and
        (Snapshot) -eq $installed -and (UninstallValuesSnapshot) -eq $installedReg) "code=$code"

    $changedHelp = Join-Path $sandbox 'changed-help'
    Copy-Item -LiteralPath $Payload -Destination $changedHelp -Recurse
    $changedHelpFile = Join-Path $changedHelp $helpWithinPayload
    $helpBytes = [System.IO.File]::ReadAllBytes($changedHelpFile)
    if ($helpBytes.Length -lt 5) { throw 'Test payload Help is too short' }
    $helpBytes[$helpBytes.Length - 1] = $helpBytes[$helpBytes.Length - 1] -bxor 1
    [System.IO.File]::WriteAllBytes($changedHelpFile, $helpBytes)
    $code = Run $install @('-PayloadDir', $changedHelp)
    Check 'one changed Help byte: exit 3, bundle/previous/neighbour/registry unchanged' ($code -eq 3 -and
        (Snapshot) -eq $installed -and (UninstallValuesSnapshot) -eq $installedReg) "code=$code"

    $extra = Join-Path $sandbox 'extra'
    Copy-Item $Payload $extra -Recurse
    Set-Content (Join-Path $extra 'Mahod.CivilDelivery.bundle\Contents\2027\extra.dll') 'x'
    $code = Run $install @('-PayloadDir', $extra)
    Check 'a file not in the manifest: exit 3, nothing changed' ($code -eq 3 -and (Snapshot) -eq $installed) "code=$code"

    $code = Run $install @('-PayloadDir', $Payload, '-TestFailureAt', 'AfterStaging')
    Check 'failure after staging: exit 6, nothing changed' ($code -eq 6 -and (Snapshot) -eq $installed) "code=$code"
    $code = Run $install @('-PayloadDir', $Payload, '-TestFailureAt', 'AfterSwap')
    Check 'failure after the swap: exit 6, the installed version is back' ($code -eq 6 -and (Snapshot) -eq $installed) "code=$code"

    # 4. a folder with our name that is not ours is never replaced
    $foreignRoot = Join-Path $sandbox 'foreign'
    New-Item -ItemType Directory -Force -Path (Join-Path $foreignRoot 'Mahod.CivilDelivery.bundle') | Out-Null
    Set-Content (Join-Path $foreignRoot 'Mahod.CivilDelivery.bundle\PackageContents.xml') '<ApplicationPackage Name="Someone Else" />'
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $install, '-PluginsRootOverride', $foreignRoot,
              '-UninstallKeyOverride', $key, '-SkipHostCheck', '-PayloadDir', $Payload)
    & $ps @args | Out-Null
    Check 'a foreign folder with our name: exit 6, untouched' ($LASTEXITCODE -eq 6 -and
        (Get-Content (Join-Path $foreignRoot 'Mahod.CivilDelivery.bundle\PackageContents.xml') -Raw) -match 'Someone Else') "code=$LASTEXITCODE"

    # 5. uninstall removes only ours
    $code = Run $uninstall @()
    Check 'uninstall exits 0' ($code -eq 0) "code=$code"
    Check 'uninstall removes the bundle and the previous copy' (-not (Test-Path $bundle) -and -not (Test-Path $previous))
    Check 'uninstall removes the Add/Remove Programs entry' (-not (Test-Path $key))
    Check 'uninstall never touches the neighbour' ((Get-FileHash (Join-Path $neighbour 'Contents\MahodAI.Civil3D.Plugin.dll')).Hash -eq $neighbourHash)
    $code = Run $uninstall @()
    Check 'uninstall with nothing installed exits 0' ($code -eq 0) "code=$code"
}
finally {
    if (Test-Path $key) { Remove-Item $key -Recurse -Force }
    $parent = 'HKCU:\Software\MahodCivilDeliveryInstallerTest'
    if ((Test-Path $parent) -and @(Get-ChildItem $parent).Count -eq 0) { Remove-Item $parent -Force }
    if (Test-Path $sandbox) { [System.IO.Directory]::Delete($sandbox, $true) }
}
$results | ForEach-Object { Write-Host $_ }
Write-Host ("{0} passed, {1} failed" -f ($results.Count - $failed), $failed)
exit ([int]($failed -gt 0))
