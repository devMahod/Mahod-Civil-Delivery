<#
.SYNOPSIS
    Builds the Mahod Civil Delivery setup: the separate plugin, its bundle, a verified payload and the setup EXE.

.DESCRIPTION
    Output (MahodCivilDelivery\out\):
      stage\payload\Mahod.CivilDelivery.bundle\   the exact bundle that gets installed
      stage\payload\MANIFEST.json                 SHA-256 of every bundle file (the installer checks it)
      Mahod_Civil_Delivery_Setup_<version>.exe    per-user setup, payload embedded

    The staged bundle is verified from its bytes, without loading anything:
      - for each host (2026: net8, AutoCAD.NET 25.1.0 + Civil3D.NET 13.8.280 packages; 2027: net10, installed API):
        exactly the expected five files in Contents\<year>;
      - each plugin's embedded Core identity names the SHA-256 of the Core beside it, for that year's framework;
      - the four Civil Delivery assets are embedded byte-for-byte and equal their pinned SHA-256;
      - plugins and Cores report this VERSION; PackageContents.xml has exactly one component per year (R25.1 → 2026, R26.0 → 2027).
    Then the setup EXE is asked to prove its embedded payload equals the staged one.
    Windows PowerShell 5.1 compatible.
#>
[CmdletBinding()]
param(
    [switch]$SkipDefender,
    [string]$GuidePdf,
    [string]$ExpectedGuideSha256,
    [string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'

# The guide is an explicit build input, not a default/fallback or a release approval.
function Assert-GuideLocalPath {
    param([string]$Path)
    if ($Path -notmatch '\A[A-Za-z]:[\\/]') { throw 'Guide paths must be absolute local drive paths (no UNC/device/relative paths).' }
    $full = [IO.Path]::GetFullPath($Path)
    $drive = New-Object IO.DriveInfo ([IO.Path]::GetPathRoot($full))
    if ($drive.DriveType -eq [IO.DriveType]::Network) { throw 'Network guide paths are not allowed.' }
    $chain = New-Object 'System.Collections.Generic.List[string]'
    $cursor = $full
    while ($cursor) {
        $chain.Insert(0, $cursor)
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) { break }
        $cursor = $parent.FullName
    }
    foreach ($part in $chain) {
        # Test-Path misses a dangling reparse leaf; inspect attributes directly.
        try { $attributes = [IO.File]::GetAttributes($part) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse guide path refused: $part" }
    }
    return $full
}

function Get-GuideBytesSha256 {
    param([byte[]]$Bytes)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function Assert-GuideCleanupTargets {
    param([string]$OutputRoot, [string[]]$Targets)
    $output = (Assert-GuideLocalPath $OutputRoot).TrimEnd('\') + '\'
    # Validate the complete inventory before the caller deletes even the first item.
    foreach ($target in $Targets) {
        $full = [IO.Path]::GetFullPath($target)
        if (-not $full.StartsWith($output, [StringComparison]::OrdinalIgnoreCase) -or $full.TrimEnd('\') -ieq $output.TrimEnd('\')) {
            throw "Cleanup target must be an absolute child of standalone out: $target"
        }
        Assert-GuideLocalPath $target | Out-Null
    }
}

function Read-ApprovedGuide {
    param([string]$Source, [string]$ExpectedSha256, [string]$ExpectedVersion,
          [string]$BuildVersion, [string]$OutputRoot)
    if ($ExpectedSha256 -cnotmatch '\A[0-9A-Fa-f]{64}\z') { throw 'ExpectedGuideSha256 must be 64 ASCII hexadecimal characters.' }
    if ($ExpectedVersion -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z' -or $ExpectedVersion -cne $BuildVersion) {
        throw 'ExpectedVersion must exactly match the standalone VERSION.'
    }
    $path = Assert-GuideLocalPath $Source
    $output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\') + '\'
    if ($path.StartsWith($output, [StringComparison]::OrdinalIgnoreCase) -or $path.TrimEnd('\') -ieq $output.TrimEnd('\')) {
        throw 'Guide source must be outside the standalone out directory; build cleanup must not delete the source.'
    }
    if ([IO.Path]::GetFileName($path) -cne "MAHOD_CIVIL_DELIVERY_GUIDE_HE_$ExpectedVersion.pdf") {
        throw 'Guide filename must be MAHOD_CIVIL_DELIVERY_GUIDE_HE_<ExpectedVersion>.pdf.'
    }
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Length -lt 5 -or $stream.Length -gt 25MB) { throw 'Guide must be 5 bytes..25 MiB.' }
        $bytes = New-Object byte[] ([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -eq 0) { throw 'Guide read ended before the declared length.' }
            $offset += $read
        }
        if ($stream.ReadByte() -ne -1) { throw 'Guide length changed during read.' }
    } finally { $stream.Dispose() }
    if ([Text.Encoding]::ASCII.GetString($bytes, 0, 5) -cne '%PDF-') { throw 'Guide must start with the exact %PDF- header.' }
    $sha = Get-GuideBytesSha256 $bytes
    if ($sha -cne $ExpectedSha256.ToUpperInvariant()) { throw 'Guide SHA256 does not match ExpectedGuideSha256.' }
    # Keep the verified snapshot; never reopen an unpinned source for staging.
    return [pscustomobject]@{ Path = $path; Sha256 = $sha; Version = $ExpectedVersion; Bytes = $bytes }
}

function Stage-ApprovedGuide {
    param($Guide, [string]$Bundle)
    if ((Get-GuideBytesSha256 $Guide.Bytes) -cne $Guide.Sha256) { throw 'Approved guide snapshot was modified.' }
    $destination = Assert-GuideLocalPath (Join-Path $Bundle 'Help\MAHOD_CIVIL_DELIVERY_GUIDE_HE.pdf')
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    $destination = Assert-GuideLocalPath $destination
    $stream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Guide.Bytes, 0, $Guide.Bytes.Length) }
    finally { $stream.Dispose() }
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -cne $Guide.Sha256) {
        throw 'Staged Help asset differs from the approved guide snapshot.'
    }
    return $destination
}

$root = Split-Path -Parent $PSScriptRoot                     # MahodCivilDelivery
$repo = Split-Path -Parent $root                             # source92
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION must be MAJOR.MINOR.PATCH, found '$version'" }

$out = Join-Path $root 'out'
# Fail before deleting any prior output or invoking a build. No implicit guide selection.
$approvedGuide = Read-ApprovedGuide $GuidePdf $ExpectedGuideSha256 $ExpectedVersion $version $out
$guideBuildProperty = '-p:MahodCivilDeliveryGuideEnabled=true'
$guideProofPath = Assert-GuideLocalPath (Join-Path $out "SETUP_PAYLOAD_VERIFICATION_$version.json")
# Both Civil 3D hosts Mahod users run (Arthur 30.09.2026): 2026 = R25.1 / .NET 8, 2027 = R26.0 / .NET 10.
$hosts = [ordered]@{ '2026' = @{ series = 'R25.1'; tfm = 'net8.0-windows' }; '2027' = @{ series = 'R26.0'; tfm = 'net10.0-windows' } }
$stage = Join-Path $out 'stage'
$zipContent = Join-Path $out 'zip-content'
$builds = @($hosts.Keys | ForEach-Object { Join-Path $out "build-$_" })
$publish = Join-Path $out 'publish'
Assert-GuideCleanupTargets $out @($builds + $stage + $zipContent + $publish)
if (Test-Path -LiteralPath $guideProofPath) {
    if (-not (Test-Path -LiteralPath $guideProofPath -PathType Leaf)) { throw 'Previous guide proof is not a file.' }
    Remove-Item -LiteralPath $guideProofPath
}
foreach ($d in @($builds + $stage, $zipContent)) { if (Test-Path $d) { [System.IO.Directory]::Delete($d, $true) } }
New-Item -ItemType Directory -Force -Path ($builds + $stage + $zipContent) | Out-Null

Write-Host "Mahod Civil Delivery $version" -ForegroundColor Cyan

# ── 1. build ──────────────────────────────────────────────────────────────────
foreach ($year in $hosts.Keys) {
    & $dotnet build (Join-Path $root 'Mahod.CivilDelivery\Mahod.CivilDelivery.csproj') -c Release --nologo -v quiet `
        "-p:AutoCADVersion=$year" $guideBuildProperty -o (Join-Path $out "build-$year")
    if ($LASTEXITCODE -ne 0) { throw "the $year plugin build failed" }
}

# ── 2. stage the bundle ───────────────────────────────────────────────────────
$bundle = Join-Path $stage 'payload\Mahod.CivilDelivery.bundle'
New-Item -ItemType Directory -Force -Path (Join-Path $bundle 'Installer') | Out-Null
$expected = @('Mahod.CivilDelivery.Core.dll', 'Mahod.CivilDelivery.deps.json', 'Mahod.CivilDelivery.dll', 'NetTopologySuite.dll', 'YamlDotNet.dll')
# The referenced Core project's own deps.json lands in -o as well; the host reads only the plugin's.
$notShipped = @('Mahod.CivilDelivery.Core.deps.json')
foreach ($year in $hosts.Keys) {
    $build = Join-Path $out "build-$year"
    $contents = Join-Path $bundle "Contents\$year"
    New-Item -ItemType Directory -Force -Path $contents | Out-Null
    $built = @(Get-ChildItem $build -File | Where-Object { $_.Extension -in '.dll', '.json' -and $notShipped -notcontains $_.Name } |
        ForEach-Object Name | Sort-Object)
    if (($built -join '|') -ne (($expected | Sort-Object) -join '|')) {
        throw "unexpected $year build output: $($built -join ', ') (expected $($expected -join ', '))"
    }
    foreach ($f in $expected) { Copy-Item (Join-Path $build $f) (Join-Path $contents $f) }
}
Copy-Item (Join-Path $PSScriptRoot 'Uninstall-MahodCivilDelivery.ps1') (Join-Path $bundle 'Installer\Uninstall-MahodCivilDelivery.ps1')

$packageContents = @"
<?xml version="1.0" encoding="utf-8"?>
<!-- GENERATED by MahodCivilDelivery\installer\build-setup.ps1 - do not edit by hand. Mahod Civil Delivery $version.
     A separate plugin from MahodAI: its own bundle, assemblies and MCD_* commands. Civil 3D 2026 (R25.1, net8) and 2027 (R26.0, net10). -->
<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="AutoCAD" ProductType="Application"
    Name="Mahod Civil Delivery" AppVersion="$version" Author="Mahod Engineering"
    Description="Mahod Civil Delivery - sections from CL and bill of quantities from the drawing"
    ProductCode="{6D8A3C2E-5B1F-4E7A-9C3D-2F4E6A8B0C12}" UpgradeCode="{9E2B4D6F-8A1C-4F3E-B5D7-1C3E5A7B9D24}">
  <CompanyDetails Name="Mahod Engineering" Url="https://www.mahodeng.co.il" />
  <Components Description="Civil 3D 2026">
    <RuntimeRequirements OS="Win64" Platform="AutoCAD*" SeriesMin="R25.1" SeriesMax="R25.1" />
    <ComponentEntry AppName="MahodCivilDelivery2026" Version="$version"
        ModuleName="./Contents/2026/Mahod.CivilDelivery.dll" AppType=".NET"
        LoadOnCommandInvocation="False" LoadOnAutoCADStartup="True" LoadOnProxy="False" />
  </Components>
  <Components Description="Civil 3D 2027">
    <RuntimeRequirements OS="Win64" Platform="AutoCAD*" SeriesMin="R26.0" SeriesMax="R26.0" />
    <ComponentEntry AppName="MahodCivilDelivery2027" Version="$version"
        ModuleName="./Contents/2027/Mahod.CivilDelivery.dll" AppType=".NET"
        LoadOnCommandInvocation="False" LoadOnAutoCADStartup="True" LoadOnProxy="False" />
  </Components>
</ApplicationPackage>
"@
[System.IO.File]::WriteAllText((Join-Path $bundle 'PackageContents.xml'), ($packageContents -replace "`r?`n", "`r`n"),
    (New-Object System.Text.UTF8Encoding $false))

# ── 3. verify the staged bytes ─────────────────────────────────────────────────
$failures = @()
$latin1 = [System.Text.Encoding]::GetEncoding(28591)
foreach ($year in $hosts.Keys) {
$contents = Join-Path $bundle "Contents\$year"
$pluginPath = Join-Path $contents 'Mahod.CivilDelivery.dll'
$corePath = Join-Path $contents 'Mahod.CivilDelivery.Core.dll'
$pluginText = $latin1.GetString([System.IO.File]::ReadAllBytes($pluginPath))
$coreHash = (Get-FileHash $corePath -Algorithm SHA256).Hash

$ids = [regex]::Matches($pluginText, 'schema=1\r?\nname=Mahod\.CivilDelivery\.Core\r?\nsha256=([0-9A-Fa-f]{64})\r?\ntfm=([A-Za-z0-9.\-]+)')
if ($ids.Count -ne 1) { $failures += "$year the plugin carries $($ids.Count) Core identity blocks (expected exactly one)" }
elseif ($ids[0].Groups[1].Value.ToUpperInvariant() -ne $coreHash) { $failures += "$year the embedded Core identity names $($ids[0].Groups[1].Value), the Core beside it is $coreHash" }
elseif ($ids[0].Groups[2].Value -ne $hosts[$year].tfm) { $failures += "$year the embedded Core identity is for $($ids[0].Groups[2].Value)" }
if ($pluginText -match 'name=MahodAI\.CivilDelivery\.Core') { $failures += "$year the plugin names MahodAI's Core" }

$pins = [ordered]@{
    'mahod_logo_white.png' = '53ECFA0015AE0B6BF47CAD89E58D4C94EB9CD34ED74C1A39337C9C053162D1DB'
    'HW-CARFRBK-01.dwg'    = 'E2823D50D85E2F669944CDB68B2CC87F44090801CDA75A86F66A6CC11EA38968'
    'HW-CARFRFRW-01.dwg'   = '412E0E6F226271AA1A66A2749F598C2A2F13A237D94D0EB567E07EC206924428'
    'HW-ARRW-01.dwg'       = '3E18667013887CA89A64AA043B5EB4FCB7195631FDD18D841FE24E46F96B2943'
}
foreach ($name in $pins.Keys) {
    $asset = Join-Path $repo "MahodAI.Civil3D.Plugin\assets\$name"
    if ((Get-FileHash $asset -Algorithm SHA256).Hash -ne $pins[$name]) { $failures += "asset $name no longer has its pinned SHA-256"; continue }
    $assetText = $latin1.GetString([System.IO.File]::ReadAllBytes($asset))
    $at = $pluginText.IndexOf($assetText, [System.StringComparison]::Ordinal)
    if ($at -lt 0) { $failures += "asset $name is not embedded byte-for-byte" }
    elseif ($pluginText.IndexOf($assetText, $at + 1, [System.StringComparison]::Ordinal) -ge 0) { $failures += "asset $name is embedded twice" }
}

foreach ($dll in @($pluginPath, $corePath)) {
    $pv = ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion -split '\+')[0]
    if ($pv -ne $version) { $failures += "$year $(Split-Path -Leaf $dll) reports $pv, VERSION is $version" }
}
}
$xml = [xml](Get-Content (Join-Path $bundle 'PackageContents.xml') -Raw -Encoding UTF8)
$components = @($xml.ApplicationPackage.Components)
$byYear = @{}
foreach ($c in $components) {
    $e = @($c.ComponentEntry); $rr = $c.RuntimeRequirements
    if ($e.Count -eq 1) { $byYear[$rr.SeriesMin + '|' + $rr.SeriesMax] = $e[0].ModuleName }
}
if ($components.Count -ne $hosts.Count -or $xml.ApplicationPackage.AppVersion -ne $version) { $failures += 'PackageContents.xml must have exactly one component per host of this version' }
foreach ($year in $hosts.Keys) {
    $s = $hosts[$year].series
    if ($byYear["$s|$s"] -ne "./Contents/$year/Mahod.CivilDelivery.dll") { $failures += "PackageContents.xml does not load Contents/$year/Mahod.CivilDelivery.dll for $s" }
}
if ($failures.Count) { $failures | ForEach-Object { Write-Host "  FAIL $_" -ForegroundColor Red }; throw "$($failures.Count) payload check(s) failed" }
Write-Host '  payload verified (2026 + 2027): Core pairs, 4 pinned assets, versions, one component per host'

# ── 4. MANIFEST.json + payload.zip ─────────────────────────────────────────────
$stagedGuide = Stage-ApprovedGuide $approvedGuide $bundle
$full = (Resolve-Path $bundle).Path.TrimEnd('\') + '\'
$files = [ordered]@{}
Get-ChildItem $bundle -Recurse -File | Sort-Object FullName | ForEach-Object {
    $files[$_.FullName.Substring($full.Length).Replace('\', '/')] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
}
$manifest = [ordered]@{ product = 'Mahod Civil Delivery'; version = $version; runtimes = @($hosts.Keys); files = $files }
[System.IO.File]::WriteAllText((Join-Path $stage 'payload\MANIFEST.json'), ($manifest | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding $false))
Copy-Item (Join-Path $PSScriptRoot 'Install-MahodCivilDelivery.ps1') (Join-Path $stage 'Install-MahodCivilDelivery.ps1')

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$payloadZip = Join-Path $PSScriptRoot 'bootstrap\payload.zip'
if (Test-Path $payloadZip) { [System.IO.File]::Delete($payloadZip) }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
[System.IO.Compression.ZipFile]::ExtractToDirectory($payloadZip, $zipContent)

# ── 5. setup EXE ───────────────────────────────────────────────────────────────
Assert-GuideCleanupTargets $out @($publish)
if (Test-Path $publish) { [System.IO.Directory]::Delete($publish, $true) }
& $dotnet publish (Join-Path $PSScriptRoot 'bootstrap\MahodCivilDeliverySetup.csproj') -c Release --nologo -v quiet -o $publish
$published = $LASTEXITCODE
[System.IO.File]::Delete($payloadZip)
if ($published -ne 0) { throw 'the setup EXE did not build' }
$setup = Join-Path $out "Mahod_Civil_Delivery_Setup_$version.exe"
if (Test-Path $setup) { [System.IO.File]::Delete($setup) }
Move-Item (Join-Path $publish 'Mahod_Civil_Delivery_Setup.exe') $setup
# Bind the actual EXE invocation to unchanged setup/manifest/Help bytes, including the extracted payload.
$proofPaths = [ordered]@{
    setup = $setup
    manifest = (Join-Path $stage 'payload\MANIFEST.json')
    guide = $stagedGuide
    extractedManifest = (Join-Path $zipContent 'payload\MANIFEST.json')
    extractedGuide = (Join-Path $zipContent 'payload\Mahod.CivilDelivery.bundle\Help\MAHOD_CIVIL_DELIVERY_GUIDE_HE.pdf')
}
$proofBefore = @{}
foreach ($key in $proofPaths.Keys) { $proofBefore[$key] = (Get-FileHash -LiteralPath $proofPaths[$key] -Algorithm SHA256).Hash }
if ($proofBefore.guide -cne $approvedGuide.Sha256 -or $proofBefore.extractedGuide -cne $approvedGuide.Sha256 -or
    $proofBefore.manifest -cne $proofBefore.extractedManifest) { throw 'Guide/manifest changed before setup payload self-test.' }
$proof = Start-Process -FilePath $setup -ArgumentList ('--self-test-verify-payload-dir="{0}"' -f $zipContent) -WindowStyle Hidden -Wait -PassThru
if ($proof.ExitCode -ne 0) { throw "the setup EXE's embedded payload differs from the staged one (code $($proof.ExitCode))" }
foreach ($key in $proofPaths.Keys) {
    if ((Get-FileHash -LiteralPath $proofPaths[$key] -Algorithm SHA256).Hash -cne $proofBefore[$key]) {
        throw "$key bytes changed during setup payload self-test."
    }
}
# This receipt is emitted only after the real EXE returned success; source review/synthetic tests are not this proof.
$guideProof = [ordered]@{
    schema = 'mahod-setup-payload-verification/v1'; synthetic = $false; version = $version
    command = '--self-test-verify-payload-dir'; exitCode = $proof.ExitCode
    setupSha256 = $proofBefore.setup; payloadManifestSha256 = $proofBefore.manifest; guideSha256 = $proofBefore.guide
    completedUtc = [DateTime]::UtcNow.ToString('o')
}
[IO.File]::WriteAllText($guideProofPath, ($guideProof | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
Assert-GuideCleanupTargets $out @($publish)
[System.IO.Directory]::Delete($publish, $true)

# ── 6. Defender ────────────────────────────────────────────────────────────────
if (-not $SkipDefender) {
    # Throws unless Microsoft Defender exits 0 and the file is unchanged afterwards; writes its evidence JSON.
    $scan = & (Join-Path $repo 'installer\Invoke-MahodDefenderScan.ps1') -TargetPath @($setup) `
        -EvidencePath (Join-Path $out "DEFENDER_SCAN_$version.json") -RepositoryRoot $repo `
        -Purpose 'Mahod Civil Delivery (separate plugin) setup scan'
    Write-Host '  Defender: clean'
}

$sha = (Get-FileHash $setup -Algorithm SHA256).Hash
Write-Host ("  setup {0}  {1:N1} MB  SHA-256 {2}" -f (Split-Path -Leaf $setup), ((Get-Item $setup).Length / 1MB), $sha) -ForegroundColor Green
foreach ($year in $hosts.Keys) {
    $c = Join-Path $bundle "Contents\$year"
    Write-Host ("  {0}: plugin {1}  core {2}" -f $year, (Get-FileHash (Join-Path $c 'Mahod.CivilDelivery.dll') -Algorithm SHA256).Hash,
        (Get-FileHash (Join-Path $c 'Mahod.CivilDelivery.Core.dll') -Algorithm SHA256).Hash)
}
