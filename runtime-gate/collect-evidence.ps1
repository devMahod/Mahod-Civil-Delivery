<#
.SYNOPSIS
    Collects every Mahod Civil Delivery runtime artifact into one zip on the
    Desktop so the runtime-gate results can be sent back in a single file.
    ASCII-only file.
#>
[CmdletBinding()]
param(
    [string]$ScreenshotDir,
    # Overrides exist so the collector itself can be exercised in an isolated test.
    [string]$EvidenceRoot = (Join-Path $env:LOCALAPPDATA 'MahodAI_Civil3D\civil-delivery'),
    [string]$OutputDirectory = ([Environment]::GetFolderPath('Desktop')),
    [string]$PluginRootsOverride
)

$ErrorActionPreference = 'Continue'

$root = $EvidenceRoot
if (-not (Test-Path $root)) {
    Write-Error "No runtime evidence found at $root - did any MHD_* command run?"
    exit 2
}

function File-Sha256([string]$Path) {
    try {
        if ($Path -and (Test-Path -LiteralPath $Path -PathType Leaf)) {
            return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    } catch { }
    return $null
}

function Plugin-FileIdentity([string]$Path, [string]$CivilHost) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $item = Get-Item -LiteralPath $Path
    return [ordered]@{
        host            = $CivilHost
        path            = $item.FullName
        sha256          = File-Sha256 $item.FullName
        file_version    = $item.VersionInfo.FileVersion
        product_version = $item.VersionInfo.ProductVersion
    }
}

function Get-BundleCandidates([object]$InstallState) {
    $candidates = @()
    if ($PluginRootsOverride) {
        $priority = 0
        foreach ($rootValue in @($PluginRootsOverride -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
            $candidates += [pscustomobject]@{ source="override_$priority"; priority=$priority; path=(Join-Path $rootValue 'MahodAI.bundle') }
            $priority++
        }
    } else {
        if ($env:ProgramFiles) {
            $candidates += [pscustomobject]@{ source='program_files'; priority=0; path=(Join-Path $env:ProgramFiles 'Autodesk\ApplicationPlugins\MahodAI.bundle') }
        }
        if ($env:ProgramData) {
            $candidates += [pscustomobject]@{ source='program_data'; priority=1; path=(Join-Path $env:ProgramData 'Autodesk\ApplicationPlugins\MahodAI.bundle') }
        }
        if ($env:APPDATA) {
            $candidates += [pscustomobject]@{ source='app_data'; priority=2; path=(Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\MahodAI.bundle') }
        }
    }
    if ($InstallState -and $InstallState.bundle_path) {
        $receiptPath = [System.IO.Path]::GetFullPath([string]$InstallState.bundle_path)
        if (-not @($candidates | Where-Object { [string]$_.path -ieq $receiptPath }).Count) {
            $candidates += [pscustomobject]@{ source='install_state_only'; priority=999; path=$receiptPath }
        }
    }
    $seen = @{}
    $result = @()
    foreach ($candidate in @($candidates | Sort-Object priority)) {
        $full = [System.IO.Path]::GetFullPath([string]$candidate.path)
        if ($seen.ContainsKey($full)) { continue }
        $seen[$full] = $true
        $dll = Join-Path $full 'Contents\MahodAI.Civil3D.Plugin.dll'
        $dll2026 = Join-Path $full 'Contents\2026\MahodAI.Civil3D.Plugin.dll'
        $manifest = Join-Path $full 'PackageContents.xml'
        $candidateRevision = $null
        if (Test-Path -LiteralPath $manifest -PathType Leaf) {
            try {
                [xml]$candidateXml = Get-Content -LiteralPath $manifest -Raw
                $candidateRevision = [string]$candidateXml.ApplicationPackage.AppVersion
            } catch { }
        }
        $result += [pscustomobject]@{
            source=$candidate.source; priority=$candidate.priority; path=$full
            active=(Test-Path -LiteralPath $dll -PathType Leaf)
            plugin_sha256=(File-Sha256 $dll)
            plugin_2027_sha256=(File-Sha256 $dll)
            plugin_2026_sha256=(File-Sha256 $dll2026)
            package_revision=$candidateRevision
            package_manifest_sha256=(File-Sha256 $manifest)
        }
    }
    return $result
}

$installStatePath = Join-Path $root 'install_state.json'
$installState = $null
if (Test-Path -LiteralPath $installStatePath -PathType Leaf) {
    try { $installState = Get-Content -LiteralPath $installStatePath -Raw | ConvertFrom-Json } catch { }
}
$bundleCandidates = @(Get-BundleCandidates $installState)
$activeBundles = @($bundleCandidates | Where-Object { $_.active } | Sort-Object priority)
$installedBundle = $activeBundles | Select-Object -First 1
$bundlePath = if ($installedBundle) { [string]$installedBundle.path } else { $null }
$bundleManifest = if ($bundlePath) { Join-Path $bundlePath 'PackageContents.xml' } else { $null }
$bundlePackageRevision = $null
if ($bundleManifest -and (Test-Path -LiteralPath $bundleManifest -PathType Leaf)) {
    try {
        [xml]$bundleXml = Get-Content -LiteralPath $bundleManifest -Raw
        $bundlePackageRevision = [string]$bundleXml.ApplicationPackage.AppVersion
    } catch { }
}

$pluginBinaries = @()
if ($bundlePath) {
    $identity2027 = Plugin-FileIdentity (Join-Path $bundlePath 'Contents\MahodAI.Civil3D.Plugin.dll') 'Civil 3D 2027'
    $identity2026 = Plugin-FileIdentity (Join-Path $bundlePath 'Contents\2026\MahodAI.Civil3D.Plugin.dll') 'Civil 3D 2026'
    if ($identity2027) { $pluginBinaries += $identity2027 }
    if ($identity2026) { $pluginBinaries += $identity2026 }
}

$loadedPluginBinaries = @()
foreach ($process in @(Get-Process acad -ErrorAction SilentlyContinue)) {
    $hostYear = 0
    try {
        if ($process.MainModule.FileName -match 'AutoCAD\s+(?<year>20\d\d)') { $hostYear = [int]$Matches['year'] }
    } catch { }
    try {
        foreach ($module in @($process.Modules)) {
            if ([System.IO.Path]::GetFileName([string]$module.FileName) -ine 'MahodAI.Civil3D.Plugin.dll') { continue }
            $identity = Plugin-FileIdentity ([string]$module.FileName) "Civil 3D $hostYear"
            if ($identity) {
                $identity['civil_host_year'] = $hostYear
                $identity['process_id'] = [int]$process.Id
                $loadedPluginBinaries += $identity
            }
        }
    } catch { }
}
$receiptBundlePath = if ($installState -and $installState.bundle_path) {
    [System.IO.Path]::GetFullPath([string]$installState.bundle_path)
} else { $null }
$winnerPath = if ($installedBundle) { [string]$installedBundle.path } else { $null }
$receiptMatchesWinner = [bool]($receiptBundlePath -and $winnerPath -and $receiptBundlePath -ieq $winnerPath)
$loadedMatchesWinner = [bool]($winnerPath -and @($loadedPluginBinaries | Where-Object {
    ([string]$_.path).StartsWith($winnerPath + '\', [StringComparison]::OrdinalIgnoreCase)
}).Count -gt 0)

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$staging = Join-Path $env:TEMP "mcd_evidence_$stamp"
New-Item -ItemType Directory -Force -Path $staging | Out-Null

Copy-Item $root (Join-Path $staging 'civil-delivery') -Recurse -ErrorAction SilentlyContinue

$excel = Join-Path $env:USERPROFILE 'Documents\MahodCivilDelivery'
if (Test-Path $excel) {
    Copy-Item $excel (Join-Path $staging 'excel') -Recurse -ErrorAction SilentlyContinue
}

if ($ScreenshotDir -and (Test-Path $ScreenshotDir)) {
    Copy-Item $ScreenshotDir (Join-Path $staging 'screenshots') -Recurse -ErrorAction SilentlyContinue
}

# Environment facts that make the evidence interpretable later.
$env_info = [ordered]@{
    collected_utc                 = (Get-Date).ToUniversalTime().ToString('o')
    machine                       = $env:COMPUTERNAME
    user                          = $env:USERNAME
    civil_2027                    = Test-Path 'C:\Program Files\Autodesk\AutoCAD 2027\C3D\AeccDbMgd.dll'
    civil_2026                    = Test-Path 'C:\Program Files\Autodesk\AutoCAD 2026\C3D\AeccDbMgd.dll'
    bundle_present                = [bool]$installedBundle
    bundle_path                   = $bundlePath
    bundle_source                 = if ($installedBundle) { [string]$installedBundle.source } else { $null }
    bundle_manifest_path          = $bundleManifest
    bundle_manifest_sha256        = File-Sha256 $bundleManifest
    bundle_package_revision       = $bundlePackageRevision
    receipt_package_revision      = if ($installState) { [string]$installState.package_revision } else { $null }
    receipt_platform_version      = if ($installState) { [string]$installState.platform_version } else { $null }
    receipt_bundle_matches        = if ($installState -and $bundlePath) {
                                        [string]$installState.bundle_path -eq $bundlePath
                                    } else { $false }
    receipt_package_matches       = if ($installState -and $bundlePackageRevision) {
                                        [string]$installState.package_revision -eq $bundlePackageRevision
                                    } else { $false }
    plugin_binaries               = $pluginBinaries
    active_bundle_count           = $activeBundles.Count
    bundle_candidates             = $bundleCandidates
    loaded_plugin_binaries        = $loadedPluginBinaries
    loaded_matches_winner         = $loadedMatchesWinner
}
$env_info | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $staging 'environment.json') -Encoding utf8

[ordered]@{
    schema_version = 1
    collected_utc = (Get-Date).ToUniversalTime().ToString('o')
    loaded_plugin_binaries = $loadedPluginBinaries
} | ConvertTo-Json -Depth 7 | Set-Content (Join-Path $staging 'installed_loaded_identity.json') -Encoding utf8

[ordered]@{
    schema_version = 1
    collected_utc = (Get-Date).ToUniversalTime().ToString('o')
    active_bundle_count = $activeBundles.Count
    candidates = $bundleCandidates
    winner_path = $winnerPath
    receipt_path = $receiptBundlePath
    receipt_matches_winner = $receiptMatchesWinner
    loaded_matches_winner = $loadedMatchesWinner
} | ConvertTo-Json -Depth 7 | Set-Content (Join-Path $staging 'bundle_winner.json') -Encoding utf8

# Surface any stage log that ended with an unclosed BEGIN - the hang signature.
$logs = Join-Path $root 'logs'
if (Test-Path $logs) {
    $hangReport = @()
    foreach ($log in Get-ChildItem $logs -Filter '*.log') {
        $lines = Get-Content $log.FullName
        $lastBegin = ($lines | Where-Object { $_ -match 'BEGIN ' } | Select-Object -Last 1)
        $lastEnd = ($lines | Where-Object { $_ -match 'END   ' } | Select-Object -Last 1)
        $aborted = ($lines | Where-Object { $_ -match 'ABORT ' } | Select-Object -Last 1)
        $hangReport += [ordered]@{
            log        = $log.Name
            last_begin = $lastBegin
            last_end   = $lastEnd
            aborted    = $aborted
        }
    }
    $hangReport | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $staging 'stage_summary.json') -Encoding utf8
}

if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
}
$zip = Join-Path $OutputDirectory "MahodCivilDelivery_Evidence_$stamp.zip"
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -Force
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host "Evidence package: $zip"
Write-Host ("SHA-256         : " + (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant())
Write-Host ''
Write-Host 'Send this file back together with the screenshots.'
exit 0
