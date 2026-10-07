<#
.SYNOPSIS
    CI entry point for Mahod Civil Delivery: version gate -> Hebrew guide PDF ->
    release\<v>\build-release-<v>.ps1 (keyed build for 2026 + 2027, .NET setup, its gates, the
    installer lifecycle test, Defender) -> the portal ZIP + <OutDir>\release.json.
    Run by MahodAI-Plugin's scripts/release/Invoke-ToolRelease.ps1 on the release PC; runs by
    hand the same way.

.DESCRIPTION
    A release needs release\<version>\ prepared by a human (build-release-<v>.ps1 and
    guide-source\), as every release so far: the guide's text is a release decision.

    PUBLIC repository: nothing keyed is written inside the tree - the guide and the ZIP go to
    -OutDir (outside it), and build-release-<v>.ps1 keeps its own outputs in ignored folders.

    The test lanes are NOT run here: they carry known failures (README_DEVELOPERS_HE.md - fixture
    hashes and native runs), and a gate with expected failures is no gate. Make them green, then
    add them to step 2.

    Python: the guide builder needs PyMuPDF (the release PC's ~/.mahod/release-venv) and Edge.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Python = 'py'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

# ---- 1. versions ---------------------------------------------------------------------------
$version = (Get-Content -LiteralPath (Join-Path $repo 'MahodCivilDelivery\VERSION') -Raw).Trim()
$rel = Join-Path $repo "release\$version"
$builder = Join-Path $rel "build-release-$version.ps1"
$guideScript = Join-Path $rel 'guide-source\build_guide.py'
$problems = @()
if (-not (Test-Path $builder)) { $problems += "release\$version\build-release-$version.ps1 is missing - prepare release\$version\ from the previous release" }
if (-not (Test-Path $guideScript)) { $problems += "release\$version\guide-source\build_guide.py is missing" }
else {
    $gv = ([regex]::Match((Get-Content -LiteralPath $guideScript -Raw), '(?m)^VERSION\s*=\s*"([^"]+)"')).Groups[1].Value
    if ($gv -ne $version) { $problems += "guide-source\build_guide.py VERSION = $gv" }
}
$plan = Get-Content -LiteralPath (Join-Path $repo 'MahodAI.Civil3D.Plugin\CivilDelivery\Sections\Services\SectionPlanService.cs') -Raw
$tv = ([regex]::Match($plan, 'ToolVersion\s*=\s*"civil-delivery/([^"]+)"')).Groups[1].Value
if ($tv -ne $version) { $problems += "SectionPlanService.cs ToolVersion = civil-delivery/$tv" }
if ($problems) { $problems | ForEach-Object { Write-Host "  $_" }; throw "Mahod Civil Delivery $version is not consistent - nothing was built" }
Write-Host "Mahod Civil Delivery $version - versions consistent"
if (-not $env:MAHOD_CAD_KEY) { throw 'build-release needs MAHOD_CAD_KEY (the products usage key) in the environment' }

# ---- 2. guide ------------------------------------------------------------------------------
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path
$guideOut = Join-Path $OutDir 'guide'
$sha = (& git -C $repo rev-parse HEAD).Trim()
$env:PYTHONIOENCODING = 'utf-8'
& $Python $guideScript $sha --out $guideOut
if ($LASTEXITCODE -ne 0) { throw "build_guide.py failed with exit code $LASTEXITCODE" }
$guide = Join-Path $guideOut "MAHOD_CIVIL_DELIVERY_GUIDE_HE_$version.pdf"
if (-not (Test-Path $guide)) { throw "build_guide.py did not produce $guide" }
$guideSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $guide).Hash.ToLowerInvariant()

# ---- 3. build, gates, installer test, ZIP --------------------------------------------------
$zipOut = Join-Path $OutDir 'package'
New-Item -ItemType Directory -Force $zipOut | Out-Null
& powershell -NoProfile -ExecutionPolicy Bypass -File $builder $guide $guideSha $zipOut
if ($LASTEXITCODE -ne 0) { throw "build-release-$version.ps1 failed with exit code $LASTEXITCODE" }
$zipName = "Mahod_Civil_Delivery_$version.zip"
if (-not (Test-Path (Join-Path $zipOut $zipName))) { throw "build-release-$version.ps1 did not produce $zipName" }
Copy-Item -LiteralPath (Join-Path $zipOut $zipName) -Destination $OutDir -Force

$release = [ordered]@{ version = $version; zip = $zipName; setup = "Mahod_Civil_Delivery_Setup_$version.exe" }
[IO.File]::WriteAllText((Join-Path $OutDir 'release.json'), ($release | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("ready: {0} ({1:N0} bytes)" -f (Join-Path $OutDir $zipName), (Get-Item (Join-Path $OutDir $zipName)).Length)
