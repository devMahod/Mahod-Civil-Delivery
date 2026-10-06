<#
.SYNOPSIS
    Builds Mahod_Civil_Delivery_ARTHUR_RUNTIME_GATE_<version>.zip - the internal
    package for the real Civil 3D 2026/2027 GUI gate.

.DESCRIPTION
    Contents:
      deploy.ps1            launches the exact setup EXE carried by the gate
      uninstall.ps1         delegates to the production managed uninstaller
      make-fixture.ps1      copies the 6422 DWGs to a writable work folder
      collect-evidence.ps1  zips every report/log/screenshot location
      RUNBOOK_HE.md         the step-by-step Hebrew runbook
      SCREENSHOT_CHECKLIST_HE.md
      payload\              exact current feature build + profile + price book
      build_manifest.json   base SHA, file hashes, test counts, version statuses

    ASCII-only file (PowerShell 5.1 codepage safety).
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $PSScriptRoot 'out'
$releaseFile = Join-Path $repo 'release\release.json'
if (-not (Test-Path $releaseFile)) { throw "Release metadata missing: $releaseFile" }
$release = Get-Content $releaseFile -Raw | ConvertFrom-Json
if (-not $Version) { $Version = [string]$release.package_revision }
if ($Version -ne [string]$release.package_revision) {
    throw "Requested package revision $Version does not match release metadata $($release.package_revision)."
}
$stage = Join-Path $PSScriptRoot ('stage_' + $Version)

function Require-File([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing: $Path"
    }
}

function Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-Sha256Sidecar([string]$TargetPath, [string]$SidecarPath, [string]$Description) {
    Require-File $TargetPath $Description
    Require-File $SidecarPath "$Description SHA256 sidecar"
    $line = (Get-Content -LiteralPath $SidecarPath -Raw).Trim()
    $match = [regex]::Match($line, '^(?<sha>[0-9a-fA-F]{64})\s{2}(?<name>[^\\/\r\n]+)$')
    if (-not $match.Success) { throw "$Description SHA256 sidecar has invalid format: $SidecarPath" }
    if ($match.Groups['name'].Value -ne [System.IO.Path]::GetFileName($TargetPath)) {
        throw "$Description SHA256 sidecar names the wrong file: $($match.Groups['name'].Value)"
    }
    $actual = Sha256 $TargetPath
    if ($match.Groups['sha'].Value.ToLowerInvariant() -ne $actual) {
        throw "$Description SHA256 sidecar mismatch: recorded=$($match.Groups['sha'].Value) actual=$actual"
    }
}

function Tree-Inventory([string]$Root) {
    $inventory = [ordered]@{}
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    foreach ($file in Get-ChildItem -LiteralPath $rootFull -Recurse -File -Force | Sort-Object FullName) {
        $relative = $file.FullName.Substring($rootFull.Length).TrimStart('\').Replace('\', '/')
        $inventory[$relative] = Sha256 $file.FullName
    }
    return $inventory
}

# ---------------------------------------------------------------- payload
$payload = Join-Path $stage 'payload'

$installerStage = Join-Path $repo 'installer\stage'
$installerPayload = Join-Path $installerStage 'payload'
$installerManifestPath = Join-Path $installerStage 'build_manifest.json'
Require-File $installerManifestPath 'Installer build manifest (build the installer first)'
$installerManifest = Get-Content $installerManifestPath -Raw | ConvertFrom-Json
if ($installerManifest.version -ne $Version -or $installerManifest.configuration -ne $Configuration) {
    throw "Installer stage does not match runtime gate: version=$($installerManifest.version), configuration=$($installerManifest.configuration)"
}
if ($installerManifest.civil_2027_component -ne 'COMPILED' -or
    $installerManifest.civil_2026_component -ne 'COMPILED') {
    throw "Both host payloads are required: 2027=$($installerManifest.civil_2027_component), 2026=$($installerManifest.civil_2026_component)"
}
$sourceManifestName = [string]$installerManifest.source_manifest_file
if ([string]::IsNullOrWhiteSpace($sourceManifestName) -or
    [System.IO.Path]::GetFileName($sourceManifestName) -ne $sourceManifestName) {
    throw 'Installer manifest does not name one safe frozen source-manifest file.'
}
$sourceManifestPath = Join-Path $installerStage $sourceManifestName
Require-File $sourceManifestPath 'Installer stage source manifest'
$sourceManifestHash = Sha256 $sourceManifestPath
if ($sourceManifestHash -ne [string]$installerManifest.source_manifest_sha256) {
    throw 'Installer stage source manifest does not match its build-manifest SHA256.'
}
$repoSourceManifestPath = Join-Path $repo "release\$sourceManifestName"
Require-File $repoSourceManifestPath 'Repository frozen source manifest'
if ((Sha256 $repoSourceManifestPath) -ne $sourceManifestHash) {
    throw 'Installer stage source manifest is not byte-identical to the repository frozen source manifest.'
}
$sourceManifest = Get-Content -LiteralPath $repoSourceManifestPath -Raw | ConvertFrom-Json
if ([int]$sourceManifest.schema_version -ne 1 -or
    [string]$sourceManifest.package_revision -ne $Version -or
    [string]$sourceManifest.platform_version -ne [string]$release.platform_version -or
    [string]$sourceManifest.canonical_employee_package -ne [string]$release.canonical_employee_package -or
    [string]$sourceManifest.root_sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
    [int]$sourceManifest.file_count -le 0 -or
    [int]$sourceManifest.file_count -ne @($sourceManifest.files.PSObject.Properties).Count) {
    throw 'Frozen source manifest metadata or inventory is invalid for this release.'
}
if ([string]$installerManifest.source_manifest_root_sha256 -ne [string]$sourceManifest.root_sha256 -or
    [int]$installerManifest.source_manifest_file_count -ne [int]$sourceManifest.file_count) {
    throw 'Installer build manifest does not bind the exact frozen source root/count.'
}
$sourceManifestBuilder = Join-Path $repo 'release\build-source-manifest.py'
Require-File $sourceManifestBuilder 'Source-manifest verifier'
$python = Get-Command python.exe -ErrorAction SilentlyContinue
if (-not $python) { throw 'python.exe is required to verify the frozen source manifest.' }
& $python.Source $sourceManifestBuilder $Version --verify
if ($LASTEXITCODE -ne 0) {
    throw "Frozen source manifest does not match the current source tree (exit $LASTEXITCODE)."
}

$requiredPayloadFiles = @(
    'MahodAI.bundle\Contents\MahodAI.Civil3D.Plugin.dll',
    'MahodAI.bundle\Contents\MahodAI.CivilDelivery.Core.dll',
    'MahodAI.bundle\Contents\2026\MahodAI.Civil3D.Plugin.dll',
    'MahodAI.bundle\Contents\2026\MahodAI.CivilDelivery.Core.dll'
)
foreach ($relative in $requiredPayloadFiles) {
    $full = Join-Path $installerPayload $relative
    if (-not (Test-Path $full -PathType Leaf)) { throw "Installer payload incomplete: $full" }
}

$installerPdbs = @(Get-ChildItem -LiteralPath $installerPayload -Filter '*.pdb' -Recurse -File -ErrorAction SilentlyContinue)
if ($installerPdbs.Count -ne 0) {
    throw "Installer stage contains $($installerPdbs.Count) PDB file(s); runtime gate must carry the exact clean payload."
}

# All validation above is read-only. Only now replace the runtime staging directory.
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $out, $stage | Out-Null
New-Item -ItemType Directory -Force -Path $payload | Out-Null

# The gate installs the EXACT final installer, so what Arthur tests is what ships.
$setupExe = Join-Path $repo ("installer\out\Mahod_Civil_Delivery_Setup_$Version.exe")
$setupSidecar = $setupExe + '.sha256'
Assert-Sha256Sidecar $setupExe $setupSidecar 'Final installer'
$installerOutputManifestPath = Join-Path $repo 'installer\out\installer_manifest.json'
Require-File $installerOutputManifestPath 'Installer output manifest'
$installerOutputManifest = Get-Content -LiteralPath $installerOutputManifestPath -Raw | ConvertFrom-Json
if ([string]$installerOutputManifest.version -ne $Version -or
    [string]$installerOutputManifest.platform_file_version -ne [string]$release.platform_version -or
    [string]$installerOutputManifest.sha256 -ne (Sha256 $setupExe) -or
    [long]$installerOutputManifest.size_bytes -ne (Get-Item -LiteralPath $setupExe).Length -or
    [string]$installerOutputManifest.source_manifest_file -ne $sourceManifestName -or
    [string]$installerOutputManifest.source_manifest_sha256 -ne $sourceManifestHash -or
    [string]$installerOutputManifest.source_manifest_root_sha256 -ne [string]$sourceManifest.root_sha256 -or
    [int]$installerOutputManifest.source_manifest_file_count -ne [int]$sourceManifest.file_count) {
    throw 'Installer output manifest does not bind the exact setup and frozen source candidate.'
}
$expectedSetupDefenderRelative = "installer/out/defender_setup_$Version.json"
if (([string]$installerOutputManifest.defender_evidence).Replace('\','/') -ne $expectedSetupDefenderRelative) {
    throw 'Installer output manifest does not name the exact expected setup Defender receipt.'
}
$setupDefenderRelative = $expectedSetupDefenderRelative.Replace('/', '\')
$setupDefenderPath = Join-Path $repo $setupDefenderRelative
Require-File $setupDefenderPath 'Setup Defender receipt'
if ([string]$installerOutputManifest.defender_evidence_sha256 -ne (Sha256 $setupDefenderPath)) {
    throw 'Installer output manifest does not bind the exact setup Defender receipt.'
}
$setupDefender = Get-Content -LiteralPath $setupDefenderPath -Raw | ConvertFrom-Json
$setupScans = @($setupDefender.scans)
if ([string]$setupDefender.verdict -ne 'CLEAN' -or $setupScans.Count -ne 1 -or
    ([string]$setupScans[0].relative_path).Replace('\','/') -ne "installer/out/Mahod_Civil_Delivery_Setup_$Version.exe" -or
    ([string]$setupScans[0].sha256).ToLowerInvariant() -ne (Sha256 $setupExe) -or
    [long]$setupScans[0].size_bytes -ne (Get-Item -LiteralPath $setupExe).Length -or
    $setupScans[0].clean -ne $true -or $setupScans[0].bytes_unchanged -ne $true) {
    throw 'Setup Defender receipt does not prove the exact setup bytes CLEAN and unchanged.'
}
Copy-Item $setupExe $stage
Copy-Item $setupSidecar $stage
Copy-Item -LiteralPath $sourceManifestPath -Destination (Join-Path $stage $sourceManifestName)

Copy-Item (Join-Path $installerPayload '*') $payload -Recurse
$installerPayloadInventory = Tree-Inventory $installerPayload
$copiedPayloadInventory = Tree-Inventory $payload
if ($installerPayloadInventory.Count -ne $copiedPayloadInventory.Count -or
    @($installerPayloadInventory.Keys | Where-Object {
        -not $copiedPayloadInventory.Contains($_) -or
        $copiedPayloadInventory[$_] -ne $installerPayloadInventory[$_]
    }).Count -ne 0) {
    throw 'Runtime-gate payload is not an exact full-inventory copy of installer stage payload.'
}

# 6422 drawings: the fixture copies the engineer will actually open.
$dwgDst = Join-Path $payload 'fixtures'
New-Item -ItemType Directory -Force -Path $dwgDst | Out-Null
$dwgSrc = Join-Path $repo 'fixtures\civil-delivery\modiin-6422'
if (Test-Path $dwgSrc) {
    Copy-Item (Join-Path $dwgSrc '*.dwg') $dwgDst -ErrorAction SilentlyContinue
}
$dwgCount = (Get-ChildItem $dwgDst -Filter '*.dwg' -ErrorAction SilentlyContinue).Count
Write-Host "Fixture drawings included: $dwgCount"
$runtimePayloadInventory = Tree-Inventory $payload

# ---------------------------------------------------------------- scripts
Copy-Item (Join-Path $PSScriptRoot 'deploy.ps1') $stage
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $stage
Copy-Item (Join-Path $repo 'installer\Uninstall-MahodCivilDelivery.ps1') $stage
Copy-Item (Join-Path $PSScriptRoot 'make-fixture.ps1') $stage
Copy-Item (Join-Path $PSScriptRoot 'collect-evidence.ps1') $stage
Copy-Item (Join-Path $PSScriptRoot 'ARTHUR_10_MIN_GUI_GATE_HE.md') $stage
Copy-Item (Join-Path $PSScriptRoot 'RUNBOOK_HE.md') $stage
Copy-Item (Join-Path $PSScriptRoot 'SCREENSHOT_CHECKLIST_HE.md') $stage

# --------------------------------------------------------------- manifest
# Candidate90 isolated snapshot: no Git provenance; source_manifest is authoritative.
$baseSha = $null
$branch = $null

$hashes = @{}
foreach ($relative in $requiredPayloadFiles) {
    $hashes[$relative.Replace('\', '/')] =
        (Get-FileHash (Join-Path $payload $relative) -Algorithm SHA256).Hash.ToLowerInvariant()
}

# Bind every file carried by the gate except build_manifest.json itself. This detects
# stale/extra files after extraction without creating a self-referential hash.
$gateFileInventory = Tree-Inventory $stage

$manifest = [ordered]@{
    package                 = 'Mahod_Civil_Delivery_ARTHUR_RUNTIME_GATE'
    version                 = $Version
    built_utc               = (Get-Date).ToUniversalTime().ToString('o')
    built_by                = $env:USERNAME
    repo_branch             = $branch
    repo_base_sha           = $baseSha
    working_tree_committed  = $false
    configuration           = $Configuration
    platform_file_version   = [string]$release.platform_version
    civil_2027_component    = $installerManifest.civil_2027_component
    civil_2026_component    = $installerManifest.civil_2026_component
    civil_2026_reference_source = 'AutoCAD 2026 core 25.1 + Autodesk Civil3D.NET 13.8.280 compile-only package'
    gui_status_2027         = [string]$release.civil_2027_status
    gui_status_2026         = [string]$release.civil_2026_status
    last_live_candidate     = [string]$release.last_live_candidate
    last_live_date          = [string]$release.last_live_date
    binary_hashes           = $hashes
    payload_file_inventory  = $runtimePayloadInventory
    payload_file_count      = $runtimePayloadInventory.Count
    gate_file_inventory     = $gateFileInventory
    gate_file_count         = $gateFileInventory.Count
    setup_sha256            = Sha256 $setupExe
    setup_sidecar_sha256    = Sha256 $setupSidecar
    installer_build_manifest_sha256 = Sha256 $installerManifestPath
    installer_output_manifest_sha256 = Sha256 $installerOutputManifestPath
    setup_defender_receipt  = $expectedSetupDefenderRelative
    setup_defender_receipt_sha256 = Sha256 $setupDefenderPath
    source_manifest_file    = $sourceManifestName
    source_manifest_sha256  = $sourceManifestHash
    source_manifest_root_sha256 = [string]$installerManifest.source_manifest_root_sha256
    source_manifest_file_count = [int]$installerManifest.source_manifest_file_count
    fixture_drawings        = $dwgCount
    commands                = @('MHD_SETUP', 'MHD_SECTIONS', 'MHD_ESTIMATE',
                                'MHD_SMOKE_DISCOVER', 'MHD_SMOKE_SECTIONS', 'MHD_SMOKE_ESTIMATE')
    start_here              = 'ARTHUR_10_MIN_GUI_GATE_HE.md'
    evidence_locations      = @{
        reports    = '%LOCALAPPDATA%\MahodAI_Civil3D\civil-delivery\'
        stage_logs = '%LOCALAPPDATA%\MahodAI_Civil3D\civil-delivery\logs\'
        runs       = '%LOCALAPPDATA%\MahodAI_Civil3D\civil-delivery\runs\'
        profile    = '%LOCALAPPDATA%\MahodAI_Civil3D\civil-delivery\profiles\6422\'
        excel      = '%USERPROFILE%\Documents\MahodCivilDelivery\6422\'
    }
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $stage 'build_manifest.json') -Encoding utf8

# -------------------------------------------------------------------- zip
$zipName = "Mahod_Civil_Delivery_ARTHUR_RUNTIME_GATE_$Version.zip"
$zipPath = Join-Path $out $zipName
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath

$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$zipHash  $zipName" | Set-Content ($zipPath + '.sha256') -Encoding ascii

Write-Host ''
Write-Host "Package : $zipPath"
Write-Host "SHA-256 : $zipHash"
Write-Host "Size    : $([math]::Round((Get-Item $zipPath).Length / 1MB, 2)) MB"
exit 0
