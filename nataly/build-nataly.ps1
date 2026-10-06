<#
.SYNOPSIS
    Builds the canonical Mahod_Civil_Delivery_<version>.zip employee package.
    package for the engineer. Installer + illustrated Hebrew guide. No developer material.

.NOTES
    What the engineer sees when she unzips - exactly two files, nothing else:
        Mahod_Civil_Delivery_Setup_<v>.exe                  - double-click to install
        Mahod_Civil_Delivery_<guide, hebrew name>.pdf       - illustrated guide, real screenshots
    The html mirror, the text guides, the package manifest, the checksums and the release
    gate are internal and live in Arthur's handoff. A client package full of build
    artefacts reads as a work in progress, so the builder refuses to produce anything else.
    ASCII-only file.
#>
[CmdletBinding()]
param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repo  = Split-Path -Parent $PSScriptRoot
$out   = Join-Path $PSScriptRoot 'out'
$releaseFile = Join-Path $repo 'release\release.json'
if (-not (Test-Path $releaseFile)) { throw "Release metadata missing: $releaseFile" }
$release = Get-Content $releaseFile -Raw | ConvertFrom-Json
if (-not $Version) { $Version = [string]$release.package_revision }
if ($Version -ne [string]$release.package_revision) {
    throw "Requested package revision $Version does not match release metadata $($release.package_revision)."
}
$policyPath = Join-Path $repo 'release\ReleaseGatePolicy.ps1'
if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) { throw "Release gate policy missing: $policyPath" }
. $policyPath
$statusMatrix = Test-MahodReleaseStatusMatrix -Release $release
if (-not $statusMatrix.Valid) {
    throw ("Invalid Civil host release status matrix: " + (($statusMatrix.Errors) -join ' | '))
}
$employeeEligibility = Get-MahodEmployeeReleaseEligibility -Release $release
if (-not $employeeEligibility.Allowed) {
    throw ("Employee ZIP is release-blocked: " + (($employeeEligibility.Reasons) -join ' | '))
}
foreach ($hostYear in @(2027, 2026)) {
    $hostStatus = if ($hostYear -eq 2027) { [string]$release.civil_2027_status } else { [string]$release.civil_2026_status }
    if ($hostStatus -eq 'COMPILED_GUI_ACCEPTED') {
        $attestation = Test-MahodLiveAcceptanceAttestation -Repo $repo -Release $release -HostYear $hostYear
        if (-not $attestation.Valid) {
            throw ("Civil $hostYear live acceptance is not bound to this exact setup/DLL candidate: " +
                   (($attestation.Errors) -join ' | '))
        }
    }
}
$stage = Join-Path $out ('employee_stage_' + $Version)
$defenderEvidence = Join-Path $out "defender_employee_$Version.json"

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $defenderEvidence -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $out, $stage | Out-Null

# ---------------------------------------------------------------- installer
$setup = Join-Path $repo "installer\out\Mahod_Civil_Delivery_Setup_$Version.exe"
if (-not (Test-Path $setup)) { throw "Installer not found: $setup (run installer\build-setup.ps1 first)" }
$installerManifestPath = Join-Path $repo 'installer\out\installer_manifest.json'
if (-not (Test-Path $installerManifestPath)) { throw "Installer manifest not found: $installerManifestPath" }
$installerManifest = Get-Content $installerManifestPath -Raw | ConvertFrom-Json
if ($installerManifest.version -ne $Version -or
    $installerManifest.civil_2027_component -ne 'COMPILED' -or
    $installerManifest.civil_2026_component -ne 'COMPILED') {
    throw "Installer is not the dual-host $Version candidate: 2027=$($installerManifest.civil_2027_component), 2026=$($installerManifest.civil_2026_component)"
}
Copy-Item $setup $stage

# ------------------------------------------------------------------- guides
# The illustrated guide is built from real screenshots (nataly\guide) every time.
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-guide.ps1') -Version $Version
if ($LASTEXITCODE -ne 0) { throw 'build-guide.ps1 failed' }
$guideDir = Join-Path $PSScriptRoot 'out\guide'
# The engineer opens a folder with exactly two things: the installer and the guide.
# Everything else (html mirror, text guides, manifest, checksums) stays internal, in
# Arthur's handoff - a client package full of build artefacts reads as a work in progress.
$guideName = [string]([char]0x05DE + [char]0x05D3 + [char]0x05E8 + [char]0x05D9 + [char]0x05DA) + '_' +
             [string]([char]0x05DC + [char]0x05DE + [char]0x05E9 + [char]0x05EA + [char]0x05DE + [char]0x05E9 + [char]0x05EA)
Copy-Item (Join-Path $guideDir 'MAHOD_CIVIL_DELIVERY_GUIDE_HE.pdf') (Join-Path $stage "Mahod_Civil_Delivery_$guideName.pdf")

# --------------------------------------------------------------- manifest
$files = Get-ChildItem $stage -Recurse -File
$hashes = @{}
foreach ($f in $files) {
    $rel = $f.FullName.Substring($stage.Length + 1)
    $hashes[$rel] = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
@{
    package      = 'Mahod_Civil_Delivery_EMPLOYEE'
    version      = $Version
    platform_file_version = [string]$release.platform_version
    built_utc    = (Get-Date).ToUniversalTime().ToString('o')
    civil_2027   = [string]$release.civil_2027_status
    civil_2026   = [string]$release.civil_2026_status
    last_live_candidate = [string]$release.last_live_candidate
    last_live_date = [string]$release.last_live_date
    authenticode_status = [string](Get-AuthenticodeSignature -LiteralPath $setup).Status
    file_hashes  = $hashes
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'package_manifest.json') -Encoding utf8

# Exactly two files reach the engineer - nothing else may creep in.
$rootFiles = @(Get-ChildItem $stage -File)
$rootDirs  = @(Get-ChildItem $stage -Directory)
if ($rootFiles.Count -ne 2 -or $rootDirs.Count -ne 0) {
    throw ("Client package must contain exactly 2 files and no folders; found " +
           "$($rootFiles.Count) files / $($rootDirs.Count) folders: " +
           (($rootFiles + $rootDirs | ForEach-Object { $_.Name }) -join ', '))
}

# -------------------------------------------------------------------- zip
$zipName = "Mahod_Civil_Delivery_$Version.zip"
$zipPath = Join-Path $out $zipName
$canonicalPath = Join-Path $repo (([string]$release.canonical_employee_package).Replace('/', '\'))
if ([System.IO.Path]::GetFullPath($zipPath) -ne [System.IO.Path]::GetFullPath($canonicalPath)) {
    throw "Employee package path drift: builder=$zipPath metadata=$canonicalPath"
}
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
Remove-Item ($zipPath + '.sha256') -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$defenderScript = Join-Path $repo 'installer\Invoke-MahodDefenderScan.ps1'
try {
    # Scan in this order: the exact already-attested setup, then the final two-file ZIP.
    & $defenderScript -TargetPath @($setup, $zipPath) -EvidencePath $defenderEvidence `
        -RepositoryRoot $repo -Purpose "employee package $Version" | Out-Null
} catch {
    Remove-Item -LiteralPath $zipPath, ($zipPath + '.sha256') -Force -ErrorAction SilentlyContinue
    throw
}
"$hash  $zipName" | Set-Content ($zipPath + '.sha256') -Encoding ascii

$packageManifestPath = Join-Path $out 'package_manifest.json'
$packageManifest = Get-Content -LiteralPath $packageManifestPath -Raw | ConvertFrom-Json
$packageManifest | Add-Member -NotePropertyName defender_evidence `
    -NotePropertyValue "nataly/out/defender_employee_$Version.json" -Force
$packageManifest | Add-Member -NotePropertyName defender_evidence_sha256 `
    -NotePropertyValue ((Get-FileHash -LiteralPath $defenderEvidence -Algorithm SHA256).Hash.ToLowerInvariant()) -Force
$packageManifest | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $packageManifestPath -Encoding utf8

Write-Host ''
Write-Host "Canonical employee package : $zipPath"
Write-Host "SHA-256      : $hash"
Write-Host "Size         : $([math]::Round((Get-Item $zipPath).Length / 1MB, 2)) MB"
exit 0
