<#
.SYNOPSIS
    Builds the illustrated Hebrew user guide for Nataly: a self-contained HTML
    (every screenshot inlined as base64) and a PDF printed from it by Microsoft Edge.

    Source : nataly\guide\GUIDE_HE.html  +  nataly\guide\img\*.png  (real screenshots only)
    Output : nataly\out\guide\MAHOD_CIVIL_DELIVERY_GUIDE_HE.html
             nataly\out\guide\MAHOD_CIVIL_DELIVERY_GUIDE_HE.pdf
#>
[CmdletBinding()]
param(
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$here   = $PSScriptRoot
$repo   = Split-Path -Parent $here
$releaseFile = Join-Path $repo 'release\release.json'
if (-not (Test-Path $releaseFile)) { throw "Release metadata missing: $releaseFile" }
$release = Get-Content $releaseFile -Raw | ConvertFrom-Json
if (-not $Version) { $Version = [string]$release.package_revision }
if ($Version -ne [string]$release.package_revision) {
    throw "Requested guide revision $Version does not match release metadata $($release.package_revision)."
}
$srcDir = Join-Path $here 'guide'
$src    = Join-Path $srcDir 'GUIDE_HE.html'
$outDir = Join-Path $here 'out\guide'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# The official logo asset is embedded in the plugin; the guide uses the same file.
$logo = Join-Path $here '..\MahodAI.Civil3D.Plugin\assets\mahod_logo_white.png'
if (-not (Test-Path $logo)) { throw "logo asset missing: $logo" }
Copy-Item $logo (Join-Path $srcDir 'img\logo.png') -Force

$html = [System.IO.File]::ReadAllText($src, [System.Text.Encoding]::UTF8)
if ($html -notmatch '\{\{PACKAGE_VERSION\}\}') {
    throw 'Guide template is missing the {{PACKAGE_VERSION}} release token.'
}

# Inline every <img src="img/..."> as a data URI so the HTML is one file.
$inlined = [regex]::Replace($html, 'src="img/([^"]+)"', {
    param($m)
    $file = Join-Path $srcDir ('img\' + $m.Groups[1].Value)
    if (-not (Test-Path $file)) { throw "guide image missing: $file" }
    $bytes = [System.IO.File]::ReadAllBytes($file)
    'src="data:image/png;base64,' + [Convert]::ToBase64String($bytes) + '"'
})
$inlined = $inlined.Replace('{{PACKAGE_VERSION}}', $Version)
if ($inlined -notmatch [regex]::Escape("Mahod_Civil_Delivery_Setup_$Version.exe")) {
    throw "Rendered guide does not name the current setup EXE for $Version."
}
$staleSetup = [regex]::Matches($inlined, 'Mahod_Civil_Delivery_Setup_(\d+\.\d+\.\d+)\.exe') |
    Where-Object { $_.Groups[1].Value -ne $Version }
if (@($staleSetup).Count -gt 0) {
    throw "Rendered guide names a stale setup EXE: $($staleSetup[0].Value)"
}

$outHtml = Join-Path $outDir 'MAHOD_CIVIL_DELIVERY_GUIDE_HE.html'
[System.IO.File]::WriteAllText($outHtml, $inlined, (New-Object System.Text.UTF8Encoding($true)))

# PDF via Edge headless (Chromium handles Hebrew RTL correctly; no extra tooling).
$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
$outPdf = Join-Path $outDir 'MAHOD_CIVIL_DELIVERY_GUIDE_HE.pdf'
if ($edge) {
    if (Test-Path $outPdf) { Remove-Item $outPdf -Force }
    $uri = 'file:///' + ($outHtml -replace '\\', '/')
    # Start-Process: Chromium chatters on stderr, which would trip $ErrorActionPreference='Stop'.
    $p = Start-Process -FilePath $edge -ArgumentList @('--headless=new', '--disable-gpu', '--no-pdf-header-footer',
        ('--print-to-pdf="' + $outPdf + '"'), $uri) -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(90000)) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
        throw 'Edge timed out while rendering the guide PDF.'
    }
    $p.Refresh()
    if ($p.ExitCode -ne 0) { throw "Edge guide rendering failed with exit $($p.ExitCode)." }
    if (-not (Test-Path -LiteralPath $outPdf -PathType Leaf)) { throw 'Edge did not produce the PDF.' }
} else {
    throw 'Microsoft Edge not found; a release guide PDF is mandatory.'
}

. (Join-Path $PSScriptRoot 'PdfValidation.ps1')
$pdfValidation = Test-MahodGuidePdf -Path $outPdf
if (-not $pdfValidation.Valid) {
    throw ('Guide PDF validation failed: ' + (($pdfValidation.Errors) -join ' | '))
}

$imgCount = ([regex]::Matches($inlined, 'data:image/png;base64,')).Count
Write-Host "Guide HTML : $outHtml  ($imgCount images inlined)"
if (Test-Path $outPdf) {
    Write-Host "Guide PDF  : $outPdf  ($([math]::Round((Get-Item $outPdf).Length / 1MB, 2)) MB, $($pdfValidation.PageCount) pages)"
}
