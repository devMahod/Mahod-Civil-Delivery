<#
.SYNOPSIS
    Builds the single Mahod Civil Delivery installer:
    Mahod_Civil_Delivery_Setup_<version>.exe

.DESCRIPTION
    One user-facing installer. Internally it can carry a per-Civil-version payload:
        Contents\           -> Civil 3D 2027 (net10)
        Contents\2026\      -> Civil 3D 2026 (net8, pinned official baseline API)

    Both hosts are required. The 2026 component uses pinned official Autodesk NuGet
    references, never installed 2027 or updated 2026.1.2/net10 assemblies.

    Output: installer\out\Mahod_Civil_Delivery_Setup_<version>.exe (+ .sha256)
    ASCII-only file (PowerShell 5.1 codepage safety).
#>
[CmdletBinding()]
param(
    [string]$Version,
    # Where Mahod publishes updates: an https:// folder holding latest.json and the setup.
    # The package carries it so the check-for-updates button works on a fresh machine.
    [string]$UpdateChannel = '',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo   = Split-Path -Parent $PSScriptRoot
$out    = Join-Path $PSScriptRoot 'out'
$stage  = Join-Path $PSScriptRoot 'stage'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'

function File-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# One shared file contract for both host payloads.  Project-reference output can
# contain nested framework/data/assets directories as well as documentation/PDBs;
# those are duplicate build remnants, not files the Autodesk loader consumes.
function Get-ShippableBuildFiles([string]$Root) {
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $rootFull -PathType Container)) {
        throw "Build output directory is missing: $rootFull"
    }
    $blockedSegments = @('data', 'assets', 'net8.0-windows', 'net10.0-windows')
    $blockedExtensions = @('.pdb', '.xml', '.nupkg')
    $records = @()
    foreach ($file in Get-ChildItem -LiteralPath $rootFull -Recurse -File -Force) {
        $relative = $file.FullName.Substring($rootFull.Length).TrimStart('\')
        $segments = @($relative -split '[\\/]')
        $blockedDirectory = @($segments | Where-Object { $blockedSegments -contains $_ }).Count -gt 0
        if ($blockedDirectory -or $blockedExtensions -contains $file.Extension.ToLowerInvariant()) { continue }
        $records += [pscustomobject]@{ Relative = $relative; FullName = $file.FullName; Sha256 = File-Sha256 $file.FullName }
    }
    return @($records | Sort-Object Relative)
}

function Copy-ShippableBuild([string]$SourceRoot, [string]$DestinationRoot) {
    $records = @(Get-ShippableBuildFiles $SourceRoot)
    if ($records.Count -eq 0) { throw "No shippable files found under: $SourceRoot" }
    foreach ($record in $records) {
        $target = Join-Path $DestinationRoot $record.Relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $record.FullName -Destination $target -Force -ErrorAction Stop
        if ((File-Sha256 $target) -ne $record.Sha256) {
            throw "Staged file failed SHA256 verification: $($record.Relative)"
        }
    }
    return $records
}

function Get-TreeInventory([string]$Root) {
    $inventory = [ordered]@{}
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    foreach ($file in Get-ChildItem -LiteralPath $rootFull -Recurse -File -Force | Sort-Object FullName) {
        $relative = $file.FullName.Substring($rootFull.Length).TrimStart('\').Replace('\', '/')
        $inventory[$relative] = File-Sha256 $file.FullName
    }
    return $inventory
}

function Assert-ExactInventory([string]$Root, $Expected) {
    $actual = Get-TreeInventory $Root
    $missing = @($Expected.Keys | Where-Object { -not $actual.Contains($_) })
    $extra = @($actual.Keys | Where-Object { -not $Expected.Contains($_) })
    $changed = @($Expected.Keys | Where-Object { $actual.Contains($_) -and $actual[$_] -ne $Expected[$_] })
    if ($missing.Count -or $extra.Count -or $changed.Count) {
        throw ("Staged bundle inventory mismatch. missing=[{0}] extra=[{1}] changed=[{2}]" -f `
            ($missing -join ', '), ($extra -join ', '), ($changed -join ', '))
    }
    return $actual
}

$releaseFile = Join-Path $repo 'release\release.json'
if (-not (Test-Path $releaseFile)) { throw "Release metadata missing: $releaseFile" }
$release = Get-Content $releaseFile -Raw | ConvertFrom-Json
if (-not $Version) { $Version = [string]$release.package_revision }
if ($Version -ne [string]$release.package_revision) {
    throw "Requested package revision $Version does not match release metadata $($release.package_revision)."
}
$expectedPlatform = [string]$release.platform_version
$sourceManifestName = "source_manifest_$Version.json"
$sourceManifestPath = Join-Path $repo "release\$sourceManifestName"
$sourceManifestBuilder = Join-Path $repo 'release\build-source-manifest.py'
if (-not (Test-Path -LiteralPath $sourceManifestPath -PathType Leaf)) {
    throw "Frozen source manifest is missing: $sourceManifestPath"
}
$python = Get-Command python.exe -ErrorAction SilentlyContinue
if (-not $python) { throw 'python.exe is required to verify the frozen source manifest.' }
& $python.Source $sourceManifestBuilder $Version --verify
if ($LASTEXITCODE -ne 0) {
    throw "Frozen source manifest does not match the current source tree: $sourceManifestPath"
}
$sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw | ConvertFrom-Json
$sourceManifestHash = File-Sha256 $sourceManifestPath
$setupName = "Mahod_Civil_Delivery_Setup_$Version.exe"
$setupPath = Join-Path $out $setupName
# Never leave a same-version binary from an earlier failed build looking current.
Remove-Item -LiteralPath $setupPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath ($setupPath + '.sha256') -Force -ErrorAction SilentlyContinue
$defenderSetupEvidence = Join-Path $out "defender_setup_$Version.json"
Remove-Item -LiteralPath $defenderSetupEvidence -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $out 'installer_manifest.json') -Force -ErrorAction SilentlyContinue

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $out, $stage | Out-Null

# ------------------------------------------------------------- 2027 build
Write-Host '=== Building Civil 3D 2027 host ==='
$buildOutput2027 = Join-Path $repo "MahodAI.Civil3D.Plugin\bin\$Configuration"
$repoBundleContents = Join-Path $repo 'MahodAI.bundle\Contents'
Remove-Item -LiteralPath $buildOutput2027 -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $repoBundleContents -Recurse -Force -ErrorAction SilentlyContinue
& $dotnet build (Join-Path $repo 'MahodAI.Civil3D.Plugin') -c $Configuration -t:Rebuild --nologo `
    -p:AutoCADVersion=2027 -p:DeployPlugin=false
if ($LASTEXITCODE -ne 0) { throw '2027 build failed' }
$status2027 = 'COMPILED'
$built2027 = Join-Path $buildOutput2027 'MahodAI.Civil3D.Plugin.dll'
if (-not (Test-Path $built2027)) { throw "2027 build produced no platform assembly: $built2027" }
$version2027 = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $built2027)).FileVersion
if ($version2027 -ne $expectedPlatform) {
    throw "2027 platform version mismatch: built=$version2027 release=$expectedPlatform"
}
$builtCore2027 = Join-Path $buildOutput2027 'MahodAI.CivilDelivery.Core.dll'
if (-not (Test-Path $builtCore2027)) { throw "2027 build produced no Civil Delivery core assembly: $builtCore2027" }
$coreVersion2027 = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $builtCore2027)).FileVersion
if ($coreVersion2027 -ne $expectedPlatform) {
    throw "2027 core version mismatch: built=$coreVersion2027 release=$expectedPlatform"
}

# ------------------------------------------------------------- 2026 build
# Both API layers are compile-only official baseline packages in the project:
# AutoCAD.NET [25.1.0] (Core/Model/AdWindows) + Civil3D.NET 13.8.280. Installing
# AutoCAD 2026.1.2 switches its local DLLs to net10; they are not our net8 references.
$refs2026 = 'NuGet AutoCAD.NET [25.1.0] + Civil3D.NET 13.8.280 (compile-only net8 baseline)'
$has2026Refs = $false
$status2026 = 'REQUIRED_COMPATIBILITY_TARGET - BUILD_PENDING_REAL_2026_REFS'
$bin2026 = Join-Path $repo 'MahodAI.Civil3D.Plugin\bin2026'

Write-Host 'Civil 3D 2026 is a required release target'
Write-Host "=== Building required Civil 3D 2026 host using $refs2026 ==="
Remove-Item -LiteralPath $bin2026 -Recurse -Force -ErrorAction SilentlyContinue
& $dotnet build (Join-Path $repo 'MahodAI.Civil3D.Plugin') -c $Configuration -t:Rebuild --nologo `
    -p:AutoCADVersion=2026 -p:BaseOutputPath="$bin2026\" -p:DeployPlugin=false
if ($LASTEXITCODE -ne 0) { throw '2026 build failed; no partial single-host setup will be produced' }
$has2026Refs = $true
$status2026 = 'COMPILED'
Write-Host "Civil 3D 2026 component: $status2026"

# ----------------------------------------------------------------- stage
Write-Host '=== Staging payload ==='
$payload = Join-Path $stage 'payload'
New-Item -ItemType Directory -Force -Path $payload | Out-Null

$bundleSrc = Join-Path $repo 'MahodAI.bundle'
if (-not (Test-Path (Join-Path $bundleSrc 'PackageContents.xml'))) {
    throw "Bundle manifest missing: $bundleSrc\PackageContents.xml"
}
$stagedBundle = Join-Path $payload 'MahodAI.bundle'
Copy-Item $bundleSrc $stagedBundle -Recurse

# Stage the 2027 slot from the just-cleaned build output, never by overlaying the
# persistent repo bundle.  This makes removed dependencies disappear deterministically.
$stagedContents = Join-Path $stagedBundle 'Contents'
Remove-Item -LiteralPath $stagedContents -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stagedContents | Out-Null
$files2027 = @(Copy-ShippableBuild $buildOutput2027 $stagedContents)

# The registered uninstaller for a machine-wide bundle must itself live under the
# protected bundle, not in user-writable LOCALAPPDATA. This also makes it part of the
# install ownership/hash manifest and the atomic directory swap.
$protectedUninstallerDir = Join-Path $payload 'MahodAI.bundle\Installer'
New-Item -ItemType Directory -Force -Path $protectedUninstallerDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Uninstall-MahodCivilDelivery.ps1') `
    (Join-Path $protectedUninstallerDir 'Uninstall-MahodCivilDelivery.ps1') -Force

$manifestPath = Join-Path $stagedBundle 'PackageContents.xml'
$manifestXml = [xml](Get-Content $manifestPath)
if ([string]$manifestXml.ApplicationPackage.AppVersion -ne $Version) {
    throw "PackageContents.xml AppVersion $($manifestXml.ApplicationPackage.AppVersion) does not match release $Version"
}
if ($status2026 -eq 'COMPILED') {
    $dst2026 = Join-Path $stagedContents '2026'
    Remove-Item -LiteralPath $dst2026 -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $dst2026 | Out-Null
    $files2026 = @(Copy-ShippableBuild (Join-Path $bin2026 $Configuration) $dst2026)
    $staged2026 = Join-Path $dst2026 'MahodAI.Civil3D.Plugin.dll'
    if (-not (Test-Path $staged2026)) { throw "2026 build reported success but produced no assembly: $staged2026" }
    $version2026 = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $staged2026)).FileVersion
    if ($version2026 -ne $expectedPlatform) {
        throw "2026 platform version mismatch: built=$version2026 release=$expectedPlatform"
    }
    $stagedCore2026 = Join-Path $dst2026 'MahodAI.CivilDelivery.Core.dll'
    if (-not (Test-Path $stagedCore2026)) { throw "2026 build produced no Civil Delivery core assembly: $stagedCore2026" }
    $coreVersion2026 = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $stagedCore2026)).FileVersion
    if ($coreVersion2026 -ne $expectedPlatform) {
        throw "2026 core version mismatch: built=$coreVersion2026 release=$expectedPlatform"
    }
    Write-Host "2026 payload staged: $dst2026"
} else {
    # A manifest entry pointing at a module that was not built makes AutoCAD 2026 report a
    # broken plug-in instead of simply not having one. Remove the block with the payload.
    $xml = $manifestXml
    $dropped = 0
    foreach ($node in @($xml.ApplicationPackage.Components)) {
        if ($node.Description -like '*2026*') { [void]$xml.ApplicationPackage.RemoveChild($node); $dropped++ }
    }
    $xml.Save($manifestPath)
    Write-Host "2026 component absent - removed $dropped manifest block(s) so nothing is promised"
}

# Independently derive the only legal bundle inventory from the two clean build
# outputs plus the manifest and protected uninstaller.  Any extra file is stale by
# definition and blocks setup publication.
$expectedBundleInventory = [ordered]@{}
$expectedBundleInventory['PackageContents.xml'] = File-Sha256 $manifestPath
$expectedBundleInventory['Installer/Uninstall-MahodCivilDelivery.ps1'] = `
    (File-Sha256 (Join-Path $protectedUninstallerDir 'Uninstall-MahodCivilDelivery.ps1'))
foreach ($record in $files2027) {
    $expectedBundleInventory[('Contents/' + $record.Relative.Replace('\', '/'))] = $record.Sha256
}
if ($status2026 -eq 'COMPILED') {
    foreach ($record in $files2026) {
        $expectedBundleInventory[('Contents/2026/' + $record.Relative.Replace('\', '/'))] = $record.Sha256
    }
}
$bundleInventory = Assert-ExactInventory $stagedBundle $expectedBundleInventory

# Project profile + pinned price book + reference estimate (evidence for proposals)
$profDst = Join-Path $payload 'profiles\6422'
New-Item -ItemType Directory -Force -Path $profDst | Out-Null
Copy-Item (Join-Path $repo 'profiles\civil-delivery\6422\project-profile.yaml') $profDst
Copy-Item (Join-Path $repo 'fixtures\civil-delivery\estimate\nti-urban-082025.xlsx') $profDst
Copy-Item (Join-Path $repo 'fixtures\civil-delivery\estimate\judgment2-golden.xlsx') $profDst

# Update channel: ships inside the payload so the check-for-updates button works on a
# fresh machine without anyone configuring anything. HTTPS only - a channel reachable
# over plain HTTP can be redirected by anyone on the network into another installer.
if ($UpdateChannel) {
    if ($UpdateChannel -notmatch '^https://') {
        throw "UpdateChannel must be an https:// URL (got '$UpdateChannel')."
    }
    @{ channel = $UpdateChannel } | ConvertTo-Json |
        Set-Content (Join-Path $payload 'update-channel.json') -Encoding utf8
    Write-Host "Update channel baked into the package: $UpdateChannel"
} else {
    Write-Host 'NOTE: no update channel baked in - check-for-updates will report it as not configured.'
}

Copy-Item (Join-Path $PSScriptRoot 'Install-MahodCivilDelivery.ps1')   $stage
Copy-Item (Join-Path $PSScriptRoot 'Uninstall-MahodCivilDelivery.ps1') $stage
Copy-Item -LiteralPath $sourceManifestPath -Destination (Join-Path $stage $sourceManifestName)

$buildManifest = [ordered]@{
    version              = $Version
    platform_file_version = $expectedPlatform
    built_utc            = (Get-Date).ToUniversalTime().ToString('o')
    configuration        = $Configuration
    civil_2027_component = $status2027
    civil_2026_component = $status2026
    civil_2026_refs_seen = $has2026Refs
    civil_2026_reference_source = $refs2026
    gui_status_2027      = [string]$release.civil_2027_status
    gui_status_2026      = [string]$release.civil_2026_status
    bundle_file_count    = $bundleInventory.Count
    bundle_file_inventory = $bundleInventory
    source_manifest_file = $sourceManifestName
    source_manifest_sha256 = $sourceManifestHash
    source_manifest_root_sha256 = [string]$sourceManifest.root_sha256
    source_manifest_file_count = [int]$sourceManifest.file_count
}
$buildManifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $stage 'build_manifest.json') -Encoding utf8

# ------------------------------------------------------------------- zip
$zipPath = Join-Path $PSScriptRoot 'mcd_payload.zip'
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath

# ------------------------------------------------------ native bootstrapper
# IExpress extracted its payload and then never launched the install command on a
# real machine (2026-08-19), and it cannot elevate for a machine-wide bundle. The
# setup EXE is now a standalone self-contained .NET app that embeds mcd_payload.zip,
# extracts it, and runs Install-MahodCivilDelivery.ps1 in a visible window -
# elevated only when the bundle AutoCAD loads lives under Program Files.
$boot = Join-Path $PSScriptRoot 'bootstrap'
Copy-Item $zipPath (Join-Path $boot 'payload.zip') -Force

Write-Host '=== Publishing native setup bootstrapper ==='
$pubDir = Join-Path $boot 'publish'
Remove-Item $pubDir -Recurse -Force -ErrorAction SilentlyContinue
& $dotnet publish (Join-Path $boot 'MahodCivilDeliverySetup.csproj') -c Release --nologo `
    -r win-x64 --self-contained true -o $pubDir `
    -p:Version=$Version -p:InformationalVersion=$Version 2>&1 | Where-Object { $_ -match 'error|Mahod_Civil_Delivery_Setup' }
if ($LASTEXITCODE -ne 0) { throw 'Bootstrapper publish failed' }
$built = Join-Path $pubDir 'Mahod_Civil_Delivery_Setup.exe'
if (-not (Test-Path $built)) { throw "Bootstrapper EXE not produced at $built" }
Copy-Item $built $setupPath -Force
Remove-Item (Join-Path $boot 'payload.zip') -Force -ErrorAction SilentlyContinue

$hash = (Get-FileHash $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $setupName" | Set-Content ($setupPath + '.sha256') -Encoding ascii

# Scan the exact setup that will be attested and later copied into the employee ZIP.
# Evidence is deliberately external to the two-file employee package.
$defenderScript = Join-Path $PSScriptRoot 'Invoke-MahodDefenderScan.ps1'
try {
    & $defenderScript -TargetPath @($setupPath) -EvidencePath $defenderSetupEvidence `
        -RepositoryRoot $repo -Purpose "setup candidate $Version" | Out-Null
} catch {
    Remove-Item -LiteralPath $setupPath, ($setupPath + '.sha256') -Force -ErrorAction SilentlyContinue
    throw
}
$defenderEvidenceHash = File-Sha256 $defenderSetupEvidence

$installerManifest = [ordered]@{
    setup_exe            = $setupPath
    sha256               = $hash
    size_bytes           = (Get-Item $setupPath).Length
    built_utc            = (Get-Date).ToUniversalTime().ToString('o')
    version              = $Version
    platform_file_version = $expectedPlatform
    civil_2027_component = $status2027
    civil_2026_component = $status2026
    gui_status_2027      = [string]$release.civil_2027_status
    gui_status_2026      = [string]$release.civil_2026_status
    bundle_file_count    = $bundleInventory.Count
    defender_evidence   = "installer/out/defender_setup_$Version.json"
    defender_evidence_sha256 = $defenderEvidenceHash
    source_manifest_file = $sourceManifestName
    source_manifest_sha256 = $sourceManifestHash
    source_manifest_root_sha256 = [string]$sourceManifest.root_sha256
    source_manifest_file_count = [int]$sourceManifest.file_count
}
$installerManifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $out 'installer_manifest.json') -Encoding utf8

Write-Host ''
Write-Host "Setup EXE : $setupPath"
Write-Host "SHA-256   : $hash"
Write-Host "Size      : $([math]::Round((Get-Item $setupPath).Length / 1MB, 2)) MB"
Write-Host "2026      : $status2026"
exit 0
