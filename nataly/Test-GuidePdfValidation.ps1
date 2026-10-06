<# Pure TEMP fixture tests for release PDF validation; no guide build or browser launch. #>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PdfValidation.ps1')
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ('mcd-pdf-test-' + [guid]::NewGuid().ToString('N'))

function Check([string]$Name, [bool]$Condition) {
    if (-not $Condition) { throw "FAIL: $Name" }
    Write-Host "PASS: $Name"
}

function Write-MinimalPdf([string]$Path) {
    $encoding = [Text.Encoding]::ASCII
    $parts = New-Object System.Collections.Generic.List[string]
    $parts.Add("%PDF-1.4`n")
    $offsets = @()
    foreach ($objectText in @(
        "1 0 obj`n<< /Type /Catalog /Pages 2 0 R >>`nendobj`n",
        "2 0 obj`n<< /Type /Pages /Kids [3 0 R] /Count 1 >>`nendobj`n",
        "3 0 obj`n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>`nendobj`n",
        "4 0 obj`n<< /Length 0 >>`nstream`n`nendstream`nendobj`n"
    )) {
        $offsets += $encoding.GetByteCount(($parts -join ''))
        $parts.Add($objectText)
    }
    # A legal comment keeps the fixture above the release validator's minimum size.
    $parts.Add('%' + ('fixture-padding-' * 350) + "`n")
    $xrefOffset = $encoding.GetByteCount(($parts -join ''))
    $xref = "xref`n0 5`n0000000000 65535 f `n"
    foreach ($offset in $offsets) { $xref += ('{0:0000000000} 00000 n ' -f $offset) + "`n" }
    $parts.Add($xref)
    $parts.Add("trailer`n<< /Size 5 /Root 1 0 R >>`nstartxref`n$xrefOffset`n%%EOF`n")
    [IO.File]::WriteAllBytes($Path, $encoding.GetBytes(($parts -join '')))
}

try {
    New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
    $validPath = Join-Path $sandbox 'valid.pdf'
    Write-MinimalPdf $validPath
    $valid = Test-MahodGuidePdf -Path $validPath
    if (-not $valid.Valid) { Write-Host ('validator detail: ' + (($valid.Errors) -join ' | ')) }
    Check 'well-formed one-page fixture is accepted' $valid.Valid
    if (Get-Command pdftoppm.exe -ErrorAction SilentlyContinue) {
        Check 'well-formed fixture first page is actually renderable' $valid.RenderVerified
    }
    Check 'page count is positive' ($valid.PageCount -ge 1)

    $truncated = Join-Path $sandbox 'truncated.pdf'
    [IO.File]::WriteAllBytes($truncated, [Text.Encoding]::ASCII.GetBytes('%PDF-1.4 no eof'))
    $bad = Test-MahodGuidePdf -Path $truncated
    Check 'truncated/empty PDF is rejected' (-not $bad.Valid)
}
finally {
    if (Test-Path -LiteralPath $sandbox) {
        Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'ALL GUIDE PDF VALIDATION TESTS PASSED'
