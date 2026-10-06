<#
.SYNOPSIS
    Assembles the ONE complete audit/handoff ZIP directly in Arthur's Downloads
    folder, so the whole result can be uploaded for external review without access
    to the development session.

.NOTES
    Everything an auditor needs and nothing they must hunt for.
    ASCII-only file.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Downloads = (Join-Path $env:USERPROFILE 'Downloads')
)

$ErrorActionPreference = 'Stop'
$repo  = Split-Path -Parent $PSScriptRoot
$releaseFile = Join-Path $repo 'release\release.json'
if (-not (Test-Path $releaseFile)) { throw "Release metadata missing: $releaseFile" }
$release = Get-Content $releaseFile -Raw | ConvertFrom-Json
if (-not $Version) { $Version = [string]$release.package_revision }
if ($Version -ne [string]$release.package_revision) {
    throw "Requested handoff revision $Version does not match release metadata $($release.package_revision)."
}
$stamp = Get-Date -Format 'yyyy-MM-dd'
$stage = Join-Path $PSScriptRoot ('stage_' + $Version)
$sourceManifest = Join-Path $repo "release\source_manifest_$Version.json"
$sourceManifestBuilder = Join-Path $repo 'release\build-source-manifest.py'
$setupSrc = Join-Path $repo "installer\out\Mahod_Civil_Delivery_Setup_$Version.exe"
$setupSidecar = $setupSrc + '.sha256'
$installerBuildManifestPath = Join-Path $repo 'installer\stage\build_manifest.json'
$installerManifestPath = Join-Path $repo 'installer\out\installer_manifest.json'
$runtimeZip = Join-Path $repo "runtime-gate\out\Mahod_Civil_Delivery_ARTHUR_RUNTIME_GATE_$Version.zip"
$runtimeSidecar = $runtimeZip + '.sha256'
$natalyZip = Join-Path $repo (([string]$release.canonical_employee_package).Replace('/', '\'))
$natalySidecar = $natalyZip + '.sha256'
$packageManifestPath = Join-Path $repo 'nataly\out\package_manifest.json'

function Require-File([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing: $Path"
    }
}

function Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-RequiredJson([string]$Path, [string]$Description) {
    Require-File $Path $Description
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    catch { throw "$Description is invalid JSON: $($_.Exception.Message)" }
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

# Fail closed before replacing any handoff stage. The handoff is permitted only after
# the complete candidate chain (source -> stage -> setup -> runtime -> employee ZIP)
# is present and independently revalidated.
Require-File $sourceManifestBuilder 'Source-manifest verifier'
$sourceManifestJson = Read-RequiredJson $sourceManifest 'Frozen source manifest'
if ([int]$sourceManifestJson.schema_version -ne 1 -or
    [string]$sourceManifestJson.package_revision -ne $Version -or
    [string]$sourceManifestJson.platform_version -ne [string]$release.platform_version -or
    [string]$sourceManifestJson.canonical_employee_package -ne [string]$release.canonical_employee_package -or
    [string]$sourceManifestJson.root_sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
    [int]$sourceManifestJson.file_count -le 0 -or
    [int]$sourceManifestJson.file_count -ne @($sourceManifestJson.files.PSObject.Properties).Count) {
    throw 'Frozen source manifest metadata or inventory is invalid for this release.'
}
$python = Get-Command python.exe -ErrorAction SilentlyContinue
if (-not $python) { throw 'python.exe is required to verify the frozen source manifest.' }
& $python.Source $sourceManifestBuilder $Version --verify
if ($LASTEXITCODE -ne 0) {
    throw "Frozen source manifest does not match the current source tree (exit $LASTEXITCODE)."
}

Assert-Sha256Sidecar $setupSrc $setupSidecar 'Final installer'
$installerBuildManifest = Read-RequiredJson $installerBuildManifestPath 'Installer stage build manifest'
$stagedSourceManifest = Join-Path $repo ("installer\stage\" + [System.IO.Path]::GetFileName($sourceManifest))
Require-File $stagedSourceManifest 'Installer staged frozen source manifest'
if ((Sha256 $stagedSourceManifest) -ne (Sha256 $sourceManifest) -or
    [string]$installerBuildManifest.version -ne $Version -or
    [string]$installerBuildManifest.platform_file_version -ne [string]$release.platform_version -or
    [string]$installerBuildManifest.source_manifest_file -ne [System.IO.Path]::GetFileName($sourceManifest) -or
    [string]$installerBuildManifest.source_manifest_sha256 -ne (Sha256 $sourceManifest) -or
    [string]$installerBuildManifest.source_manifest_root_sha256 -ne [string]$sourceManifestJson.root_sha256 -or
    [int]$installerBuildManifest.source_manifest_file_count -ne [int]$sourceManifestJson.file_count) {
    throw 'Installer stage build manifest/source copy does not bind the exact frozen source candidate.'
}
$installerManifest = Read-RequiredJson $installerManifestPath 'Installer output manifest'
if ([string]$installerManifest.version -ne $Version -or
    [string]$installerManifest.platform_file_version -ne [string]$release.platform_version -or
    [string]$installerManifest.sha256 -ne (Sha256 $setupSrc) -or
    [string]$installerManifest.source_manifest_file -ne [System.IO.Path]::GetFileName($sourceManifest) -or
    [string]$installerManifest.source_manifest_sha256 -ne (Sha256 $sourceManifest) -or
    [string]$installerManifest.source_manifest_root_sha256 -ne [string]$sourceManifestJson.root_sha256 -or
    [int]$installerManifest.source_manifest_file_count -ne [int]$sourceManifestJson.file_count) {
    throw 'Installer output manifest does not bind the exact release/setup/source candidate.'
}
$expectedSetupDefenderRelative = "installer/out/defender_setup_$Version.json"
if (([string]$installerManifest.defender_evidence).Replace('\','/') -ne $expectedSetupDefenderRelative) {
    throw 'Installer output manifest names an unexpected setup Defender receipt.'
}
$setupDefenderPath = Join-Path $repo ($expectedSetupDefenderRelative.Replace('/', '\'))
$setupDefender = Read-RequiredJson $setupDefenderPath 'Setup Defender receipt'
if ([string]$installerManifest.defender_evidence_sha256 -ne (Sha256 $setupDefenderPath) -or
    [string]$setupDefender.verdict -ne 'CLEAN') {
    throw 'Setup Defender receipt is not CLEAN or is not hash-bound by installer_manifest.json.'
}

Assert-Sha256Sidecar $runtimeZip $runtimeSidecar 'Runtime-gate ZIP'
Assert-Sha256Sidecar $natalyZip $natalySidecar 'Canonical employee ZIP'
$packageManifest = Read-RequiredJson $packageManifestPath 'Employee package manifest'
if ([string]$packageManifest.version -ne $Version -or
    [string]$packageManifest.platform_file_version -ne [string]$release.platform_version) {
    throw 'Employee package manifest does not match current release metadata.'
}
$expectedEmployeeDefenderRelative = "nataly/out/defender_employee_$Version.json"
if (([string]$packageManifest.defender_evidence).Replace('\','/') -ne $expectedEmployeeDefenderRelative) {
    throw 'Employee package manifest names an unexpected Defender receipt.'
}
$employeeDefenderPath = Join-Path $repo ($expectedEmployeeDefenderRelative.Replace('/', '\'))
$employeeDefender = Read-RequiredJson $employeeDefenderPath 'Employee Defender receipt'
if ([string]$packageManifest.defender_evidence_sha256 -ne (Sha256 $employeeDefenderPath) -or
    [string]$employeeDefender.verdict -ne 'CLEAN') {
    throw 'Employee Defender receipt is not CLEAN or is not hash-bound by package_manifest.json.'
}

$employeeManifestInventory = @{}
foreach ($property in $packageManifest.file_hashes.PSObject.Properties) {
    $employeeManifestInventory[$property.Name.Replace('\','/')] = ([string]$property.Value).ToLowerInvariant()
}
$employeeCheck = Join-Path $env:TEMP ('mcd_handoff_employee_check_' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path $employeeCheck | Out-Null
    Expand-Archive -LiteralPath $natalyZip -DestinationPath $employeeCheck -Force
    $employeeFiles = @(Get-ChildItem -LiteralPath $employeeCheck -File)
    $employeeDirs = @(Get-ChildItem -LiteralPath $employeeCheck -Directory)
    if ($employeeFiles.Count -ne 2 -or $employeeDirs.Count -ne 0 -or
        $employeeManifestInventory.Count -ne $employeeFiles.Count) {
        throw 'Employee ZIP/package manifest must describe exactly two root files and no directories.'
    }
    foreach ($file in $employeeFiles) {
        if (-not $employeeManifestInventory.ContainsKey($file.Name) -or
            $employeeManifestInventory[$file.Name] -ne (Sha256 $file.FullName)) {
            throw "Employee package manifest does not bind exact ZIP member: $($file.Name)"
        }
    }
}
finally {
    Remove-Item -LiteralPath $employeeCheck -Recurse -Force -ErrorAction SilentlyContinue
}

$candidateIdentity = Join-Path $repo 'installer\Test-CandidateIdentity.ps1'
Require-File $candidateIdentity 'Candidate-identity gate'
$candidateEvidence = Join-Path $repo 'evidence\candidate_identity_evidence.txt'
New-Item -ItemType Directory -Force -Path (Split-Path $candidateEvidence) | Out-Null
& powershell -NoProfile -ExecutionPolicy Bypass -File $candidateIdentity `
    -Version $Version -OutFile $candidateEvidence
if ($LASTEXITCODE -ne 0) {
    throw "Candidate-identity gate failed (exit $LASTEXITCODE); full handoff was not staged."
}

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null

function Section([string]$name) {
    $p = Join-Path $stage $name
    New-Item -ItemType Directory -Force -Path $p | Out-Null
    return $p
}

# ------------------------------------------------------------ top-level docs
Copy-Item (Join-Path $PSScriptRoot '00_READ_ME_FIRST.md') $stage
Copy-Item (Join-Path $PSScriptRoot '01_FINAL_STATUS.md')  $stage
Copy-Item (Join-Path $repo 'runtime-gate\ARTHUR_10_MIN_GUI_GATE_HE.md') `
    (Join-Path $stage '02_ARTHUR_10_MIN_GUI_GATE_HE.md')

# ----------------------------------------------------------------- installer
$inst = Section 'installer'
Copy-Item $setupSrc $inst
Copy-Item $setupSidecar $inst
Copy-Item $installerBuildManifestPath $inst
Copy-Item $installerManifestPath $inst
Copy-Item $setupDefenderPath $inst
Copy-Item (Join-Path $repo 'installer\Install-MahodCivilDelivery.ps1')   $inst
Copy-Item (Join-Path $repo 'installer\Uninstall-MahodCivilDelivery.ps1') $inst
Copy-Item (Join-Path $repo 'installer\build-setup.ps1') $inst
# The verification suites ship too, so an auditor can re-run the proofs rather than
# taking the evidence files on trust.
Copy-Item (Join-Path $repo 'installer\Test-InstallerLifecycle.ps1') $inst
Copy-Item (Join-Path $repo 'installer\Test-CandidateIdentity.ps1') $inst
Copy-Item (Join-Path $repo 'installer\Test-BundleShadowing.ps1') $inst
Copy-Item (Join-Path $repo 'installer\Test-ReleaseMetadata.ps1') $inst
Copy-Item (Join-Path $repo 'installer\Test-Bootstrap.ps1') $inst
Copy-Item (Join-Path $repo 'installer\Test-UninstallerTransaction.ps1') $inst

# --------------------------------------------------------------- runtime gate
$rt = Section 'runtime_gate'
Copy-Item $runtimeZip $rt
Copy-Item $runtimeSidecar $rt
foreach ($f in @('deploy.ps1','uninstall.ps1','make-fixture.ps1','collect-evidence.ps1',
                 'RUNBOOK_HE.md','SCREENSHOT_CHECKLIST_HE.md','build-runtime-gate.ps1')) {
    Copy-Item (Join-Path $repo "runtime-gate\$f") $rt -ErrorAction SilentlyContinue
}

# --------------------------------------------------------------- nataly package
$np = Section 'nataly'
Copy-Item $natalyZip $np
Copy-Item $natalySidecar $np
Copy-Item $packageManifestPath $np
Copy-Item $employeeDefenderPath $np
Copy-Item (Join-Path $repo 'nataly\docs\00_RELEASE_GATE.md') $np

# -------------------------------------------------------------------- evidence
$ev = Section 'evidence'
Copy-Item (Join-Path $repo 'evidence\*') $ev -Recurse -ErrorAction SilentlyContinue

# Live StageLog samples, if any command has run on this machine.
$logs = Join-Path $env:LOCALAPPDATA 'MahodAI_Civil3D\civil-delivery\logs'
if (Test-Path $logs) {
    $dst = Join-Path $ev 'stage_logs'
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item (Join-Path $logs '*') $dst -ErrorAction SilentlyContinue
}
$runs = Join-Path $env:LOCALAPPDATA 'MahodAI_Civil3D\civil-delivery\runs'
if (Test-Path $runs) {
    $dst = Join-Path $ev 'runs'
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item (Join-Path $runs '*') $dst -Recurse -ErrorAction SilentlyContinue
}

# ------------------------------------------------------------- source state
$ss = Section 'source_state'
Copy-Item -LiteralPath $sourceManifest -Destination $ss
Copy-Item -LiteralPath $sourceManifestBuilder -Destination $ss
# Keep any historical source-state notes for context, but the versioned SHA manifest
# above is the only authoritative source identity for this candidate.
Copy-Item (Join-Path $repo 'source-state\*') $ss -Recurse -ErrorAction SilentlyContinue

# ------------------------------------------------------------ compatibility
$cp = Section 'compatibility'
Copy-Item (Join-Path $repo 'docs\civil-delivery\CIVIL_2026_COMPATIBILITY.md') $cp
Copy-Item (Join-Path $repo 'docs\civil-delivery\RUNTIME_GATE_STATUS.md') $cp -ErrorAction SilentlyContinue
Copy-Item (Join-Path $repo 'MahodAI.bundle\PackageContents.xml') $cp

$hostState = [ordered]@{
    generated_utc = (Get-Date).ToUniversalTime().ToString('o')
    package_revision = $Version
    platform_file_version = [string]$release.platform_version
    civil_2027 = [ordered]@{
        refs_path   = 'C:\Program Files\Autodesk\AutoCAD 2027\C3D\AeccDbMgd.dll'
        refs_present= Test-Path 'C:\Program Files\Autodesk\AutoCAD 2027\C3D\AeccDbMgd.dll'
        series      = 'R26.0'
        framework   = 'net10.0-windows'
        status      = [string]$release.civil_2027_status
    }
    civil_2026 = [ordered]@{
        refs_path   = "$env:USERPROFILE\.nuget\packages\civil3d.net\13.8.280\lib\net8.0\AeccDbMgd.dll"
        refs_present= Test-Path "$env:USERPROFILE\.nuget\packages\civil3d.net\13.8.280\lib\net8.0\AeccDbMgd.dll"
        base_acad_present = Test-Path 'C:\Program Files\Autodesk\AutoCAD 2026\acmgd.dll'
        series      = 'R25.1'
        framework   = 'net8.0-windows'
        status      = [string]$release.civil_2026_status
        build_command = 'dotnet build MahodAI.Civil3D.Plugin -c Release -p:AutoCADVersion=2026'
        note        = 'Compiled against local AutoCAD 2026 core 25.1 and Autodesk Civil3D.NET 13.8.280 compile-only references; no Autodesk host DLLs are shipped.'
    }
}
$hostState | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $cp 'host_state.json') -Encoding utf8

# -------------------------------------------------------- materials manifest
$mm = Section 'materials_manifest'
$materials = @()
$fixtureDirs = @(
    (Join-Path $repo 'fixtures\civil-delivery\modiin-6422'),
    (Join-Path $repo 'fixtures\civil-delivery\estimate')
)
foreach ($d in $fixtureDirs) {
    if (-not (Test-Path $d)) { continue }
    foreach ($f in Get-ChildItem $d -File) {
        $materials += [ordered]@{
            file    = $f.Name
            role    = if ($f.Extension -eq '.dwg') { 'project drawing WORKING COPY' } else { 'estimate evidence WORKING COPY' }
            bytes   = $f.Length
            sha256  = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
}
[ordered]@{
    note = 'These are WORKING COPIES used for development and testing. The originals under P:\data\6422 were never opened for write and are not included here. A zero-byte source file (ACAD-HW-CL-M34.dwg) was excluded as unusable.'
    generated_utc = (Get-Date).ToUniversalTime().ToString('o')
    materials = $materials
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $mm 'materials_manifest.json') -Encoding utf8

# ------------------------------------------------------------- SHA256SUMS
$sums = @()
foreach ($f in Get-ChildItem $stage -Recurse -File | Sort-Object FullName) {
    $rel = $f.FullName.Substring($stage.Length + 1).Replace('\', '/')
    $sums += ((Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $rel)
}
$sums -join [Environment]::NewLine | Set-Content (Join-Path $stage 'SHA256SUMS.txt') -Encoding ascii

# -------------------------------------------------------------------- zip
$zipName = "Mahod_Civil_Delivery_ARTHUR_FULL_HANDOFF_${Version}_$stamp.zip"
$zipPath = Join-Path $Downloads $zipName
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $zipName" | Set-Content ($zipPath + '.sha256') -Encoding ascii

# Convenience copies straight in Downloads - each with its hash sidecar, so every
# artifact Arthur can see is independently verifiable.
Copy-Item $setupSrc $Downloads -Force
Copy-Item $setupSidecar $Downloads -Force
Copy-Item $natalyZip $Downloads -Force
Copy-Item $natalySidecar $Downloads -Force

Write-Host ''
Write-Host "FULL HANDOFF : $zipPath"
Write-Host "SHA-256      : $hash"
Write-Host "Size         : $([math]::Round((Get-Item $zipPath).Length / 1MB, 2)) MB"
Write-Host "Files inside : $($sums.Count)"
exit 0
