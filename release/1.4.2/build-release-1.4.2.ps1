<#
.SYNOPSIS
    Mahod Civil Delivery 1.4.2: the setup, its usage-key and version gates, the installer lifecycle test and the
    portal ZIP (setup + Hebrew guide, nothing else).

.DESCRIPTION
    1.4.2 = 1.4.0 b41 + usage counts for Mahod Impact. The steps are the repository's own:
      1. MahodCivilDelivery\installer\build-setup.ps1 (unchanged): both hosts (2026 net8, 2027 net10), payload
         verification, MANIFEST, the setup EXE's payload self-test, Defender.
      2. Gates added for 1.4.2, read from the staged bundle bytes: each host's Mahod.CivilDelivery.dll and Core are
         assembly 1.4.2.0, carry the usage endpoint, and carry the products' key (checked as a boolean, never printed).
      3. MahodCivilDelivery\installer\Test-Installer.ps1 (sandboxed install/upgrade/uninstall lifecycle).
      4. The portal ZIP: exactly the setup EXE and the Hebrew guide PDF (the two-file rule of nataly\build-nataly.ps1,
         whose release.json gate still pins the 1.2.x lane), Defender-scanned, with SHA-256 sidecars and a report.

    The products' key comes ONLY from the MAHOD_CAD_KEY environment variable of this process: run it through the
    owner's with-usage-key.ps1, which sets it from the private key file. The repository is public: the key, the built
    DLLs, the setup and the ZIP never enter git (.gitignore: MahodCivilDelivery/out/, *.zip).

    Positional arguments (with-usage-key forwards an array):
      0  GuidePdf      absolute path of MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.4.2.pdf (release\1.4.2\build_guide_142.py)
      1  GuideSha256   its SHA-256 (build-setup.ps1 pins the guide it embeds)
      2  OutDir        where the ZIP, the sidecars and release-report.json go
    Windows PowerShell 5.1 compatible. ASCII-only file.
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$GuidePdf,
    [Parameter(Mandatory = $true, Position = 1)][string]$GuideSha256,
    [Parameter(Mandatory = $true, Position = 2)][string]$OutDir
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$mcd = Join-Path $repo 'MahodCivilDelivery'
$version = (Get-Content (Join-Path $mcd 'VERSION') -Raw).Trim()
if ($version -ne '1.4.2') { throw "VERSION is '$version'; this script releases 1.4.2" }
$key = $env:MAHOD_CAD_KEY
if ([string]::IsNullOrWhiteSpace($key) -or $key -notmatch '^[A-Za-z0-9_-]{16,128}$') {
    throw 'MAHOD_CAD_KEY is not set in this process: run through with-usage-key.ps1'
}
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$git = 'git'

# -- 0. source: the committed tree, nothing modified but the fork's rebuilt test bundle ------------------------
$commit = (& $git -C $repo rev-parse HEAD).Trim()
$dirty = @(& $git -C $repo status --porcelain --untracked-files=no | Where-Object { $_ -notmatch ' MahodAI\.bundle/' })
if ($dirty.Count -gt 0) { throw "tracked files are modified; commit first: $($dirty -join '; ')" }
Write-Host "Mahod Civil Delivery $version from $commit"

# -- 1. setup (the repository's builder, unchanged) ---------------------------------------------------------------
& $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $mcd 'installer\build-setup.ps1') `
    -GuidePdf $GuidePdf -ExpectedGuideSha256 $GuideSha256 -ExpectedVersion $version
if ($LASTEXITCODE -ne 0) { throw "build-setup.ps1 failed ($LASTEXITCODE)" }
$out = Join-Path $mcd 'out'
$setup = Join-Path $out "Mahod_Civil_Delivery_Setup_$version.exe"
$bundle = Join-Path $out 'stage\payload\Mahod.CivilDelivery.bundle'
if (-not (Test-Path -LiteralPath $setup)) { throw "setup not built: $setup" }

# -- 2. 1.4.2 gates on the staged bytes ----------------------------------------------------------------------------
function Contains-Bytes([byte[]]$hay, [byte[]]$needle) {
    $text = [System.Text.Encoding]::GetEncoding(28591).GetString($hay)
    return $text.IndexOf([System.Text.Encoding]::GetEncoding(28591).GetString($needle), [StringComparison]::Ordinal) -ge 0
}
$utf16 = [System.Text.Encoding]::Unicode
$hosts = @{}
foreach ($year in @('2026', '2027')) {
    $contents = Join-Path $bundle "Contents\$year"
    $plugin = Join-Path $contents 'Mahod.CivilDelivery.dll'
    $core = Join-Path $contents 'Mahod.CivilDelivery.Core.dll'
    $bytes = [System.IO.File]::ReadAllBytes($plugin)
    $h = [ordered]@{
        plugin_assembly_version = [Reflection.AssemblyName]::GetAssemblyName($plugin).Version.ToString()
        core_assembly_version = [Reflection.AssemblyName]::GetAssemblyName($core).Version.ToString()
        plugin_sha256 = (Get-FileHash $plugin -Algorithm SHA256).Hash
        core_sha256 = (Get-FileHash $core -Algorithm SHA256).Hash
        usage_endpoint = (Contains-Bytes $bytes $utf16.GetBytes('https://dev.mahodeng.co.il/api/v1/usage'))
        usage_key_baked = (Contains-Bytes $bytes $utf16.GetBytes($key))
    }
    foreach ($name in @('plugin_assembly_version', 'core_assembly_version')) {
        if ($h[$name] -ne "$version.0") { throw "$year $name is $($h[$name]), expected $version.0" }
    }
    if (-not $h.usage_endpoint) { throw "$year plugin does not carry the usage endpoint" }
    if (-not $h.usage_key_baked) { throw "$year plugin does not carry the products' usage key" }
    $hosts[$year] = $h
    Write-Host "  $year : plugin/core $($h.plugin_assembly_version), usage endpoint yes, usage key baked yes"
}

# -- 3. installer lifecycle (sandbox) --------------------------------------------------------------------------------
$lifecycle = & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $mcd 'installer\Test-Installer.ps1')
if ($LASTEXITCODE -ne 0) { $lifecycle | ForEach-Object { Write-Host $_ }; throw 'Test-Installer.ps1 failed' }
$lifecycleSummary = @($lifecycle)[-1]
Write-Host "  Test-Installer: $lifecycleSummary"

# -- 4. the portal ZIP: setup + Hebrew guide, nothing else ----------------------------------------------------------
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$stage = Join-Path $OutDir ('zip-stage-' + $version)
if (Test-Path $stage) { [System.IO.Directory]::Delete($stage, $true) }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item -LiteralPath $setup -Destination $stage
# The guide's name in the package, as nataly\build-nataly.ps1 names it: Mahod_Civil_Delivery_<"user guide" in Hebrew>.pdf
$guideName = [string]([char]0x05DE + [char]0x05D3 + [char]0x05E8 + [char]0x05D9 + [char]0x05DA) + '_' +
             [string]([char]0x05DC + [char]0x05DE + [char]0x05E9 + [char]0x05EA + [char]0x05DE + [char]0x05E9 + [char]0x05EA)
$guideInZip = Join-Path $stage "Mahod_Civil_Delivery_$guideName.pdf"
Copy-Item -LiteralPath $GuidePdf -Destination $guideInZip
if ((Get-FileHash -LiteralPath $guideInZip -Algorithm SHA256).Hash -ne $GuideSha256.ToUpperInvariant()) { throw 'guide copy differs' }
$files = @(Get-ChildItem -LiteralPath $stage -File)
if ($files.Count -ne 2 -or @(Get-ChildItem -LiteralPath $stage -Directory).Count -ne 0) { throw 'the package must hold exactly the setup and the guide' }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = Join-Path $OutDir "Mahod_Civil_Delivery_$version.zip"
if (Test-Path -LiteralPath $zip) { [System.IO.File]::Delete($zip) }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try { $entries = @($archive.Entries | ForEach-Object { $_.FullName } | Sort-Object) } finally { $archive.Dispose() }
$expectedEntries = @($files | ForEach-Object Name | Sort-Object)
if (($entries -join '|') -ne ($expectedEntries -join '|')) { throw "ZIP entries differ: $($entries -join ', ')" }

& (Join-Path $repo 'installer\Invoke-MahodDefenderScan.ps1') -TargetPath @($zip) `
    -EvidencePath (Join-Path $OutDir "DEFENDER_SCAN_ZIP_$version.json") -RepositoryRoot $repo `
    -Purpose "Mahod Civil Delivery $version portal package" | Out-Null

$artifacts = [ordered]@{}
foreach ($f in @($zip, $setup, $GuidePdf)) {
    $hash = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash
    $name = Split-Path -Leaf $f
    $artifacts[$name] = [ordered]@{ bytes = (Get-Item -LiteralPath $f).Length; sha256 = $hash }
    [System.IO.File]::WriteAllText((Join-Path $OutDir "$name.sha256"), "$($hash.ToLowerInvariant())  $name`n", (New-Object System.Text.UTF8Encoding $false))
}
$report = [ordered]@{
    product = 'Mahod Civil Delivery'; version = $version; source_commit = $commit
    built_utc = [DateTime]::UtcNow.ToString('o')
    zip_entries = $entries
    hosts = $hosts
    installer_lifecycle = $lifecycleSummary
    payload_manifest_sha256 = (Get-FileHash (Join-Path $out 'stage\payload\MANIFEST.json') -Algorithm SHA256).Hash
    artifacts = $artifacts
}
[System.IO.File]::WriteAllText((Join-Path $OutDir "release-report-$version.json"), ($report | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding $false))
[System.IO.Directory]::Delete($stage, $true)
foreach ($name in $artifacts.Keys) { Write-Host ("  {0}  {1} bytes  SHA-256 {2}" -f $name, $artifacts[$name].bytes, $artifacts[$name].sha256) -ForegroundColor Green }
exit 0
