<# Host-free release test: scans one harmless TEMP file; no build/install/Civil. #>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ('mcd-defender-test-' + [guid]::NewGuid().ToString('N'))
$target = Join-Path $sandbox 'clean-release-fixture.txt'
$evidence = Join-Path $sandbox 'defender.json'

function Check([string]$Name, [bool]$Condition) {
    if (-not $Condition) { throw "FAIL: $Name" }
    Write-Host "PASS: $Name"
}

try {
    New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
    Set-Content -LiteralPath $target -Value 'Mahod Civil Delivery Defender scan fixture' -Encoding utf8
    $expectedSha = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    & (Join-Path $PSScriptRoot 'Invoke-MahodDefenderScan.ps1') `
        -TargetPath @($target) -EvidencePath $evidence -RepositoryRoot $sandbox `
        -Purpose 'Defender scanner regression fixture' | Out-Null

    $receipt = Get-Content -LiteralPath $evidence -Raw | ConvertFrom-Json
    Check 'receipt verdict is CLEAN' ([string]$receipt.verdict -eq 'CLEAN')
    Check 'exactly one target was scanned' (@($receipt.scans).Count -eq 1)
    Check 'target hash is exact' ([string]$receipt.scans[0].sha256 -eq $expectedSha)
    Check 'target relative path is stable' ([string]$receipt.scans[0].relative_path -eq 'clean-release-fixture.txt')
    Check 'scanner stdout is serialized as a plain JSON string' ($receipt.scans[0].stdout -is [string])
    Check 'scanner stderr is serialized as a plain JSON string' ($receipt.scans[0].stderr -is [string])
    Check 'Defender evidence stays bounded' ((Get-Item -LiteralPath $evidence).Length -lt 1048576)
    Check 'scanner exited zero without timeout' `
        ($receipt.scans[0].clean -eq $true -and [int]$receipt.scans[0].exit_code -eq 0 -and
         $receipt.scans[0].timed_out -eq $false)
    Check 'scanner identity is SHA256-bound' ([string]$receipt.scanner.sha256 -match '^[0-9a-f]{64}$')
    Check 'Defender signature metadata is recorded' `
        (-not [string]::IsNullOrWhiteSpace([string]$receipt.defender_status.antivirus_signature_version))

    Set-Content -LiteralPath $target -Value 'tampered after scan' -Encoding utf8
    $tamperedSha = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    Check 'post-scan tampering no longer matches receipt' ($tamperedSha -ne [string]$receipt.scans[0].sha256)
}
finally {
    if (Test-Path -LiteralPath $sandbox) {
        Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'ALL DEFENDER SCAN TESTS PASSED'
