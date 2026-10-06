<#
.SYNOPSIS
    Host-free regression test for machine-wide bundle discovery in collect-evidence.ps1.
    Runs entirely below %TEMP%; it does not inspect or mutate a real ApplicationPlugins root.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("mcd-collect-test-" + [Guid]::NewGuid().ToString('N'))
$root = Join-Path $sandbox 'state'
$bundle = Join-Path $sandbox 'Program Files\Autodesk\ApplicationPlugins\MahodAI.bundle'
$output = Join-Path $sandbox 'out'
$expanded = Join-Path $sandbox 'expanded'

function Check([string]$Name, [bool]$Condition) {
    if (-not $Condition) { throw "FAIL: $Name" }
    Write-Host "PASS: $Name"
}

try {
    New-Item -ItemType Directory -Force -Path $root, $output, (Join-Path $bundle 'Contents\2026') | Out-Null
    Set-Content -LiteralPath (Join-Path $bundle 'Contents\MahodAI.Civil3D.Plugin.dll') -Value '2027-test-dll' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $bundle 'Contents\2026\MahodAI.Civil3D.Plugin.dll') -Value '2026-test-dll' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $bundle 'PackageContents.xml') `
        -Value '<ApplicationPackage AppVersion="9.8.7" />' -Encoding utf8
    [ordered]@{
        schema_version = 3
        package_revision = '9.8.7'
        platform_version = '5.4.3.2'
        bundle_path = $bundle
        machine_wide = $true
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'install_state.json') -Encoding utf8

    $collector = Join-Path $PSScriptRoot 'collect-evidence.ps1'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $collector `
        -EvidenceRoot $root -OutputDirectory $output -PluginRootsOverride (Split-Path -Parent $bundle)
    Check 'collector exit code' ($LASTEXITCODE -eq 0)

    $zips = @(Get-ChildItem -LiteralPath $output -Filter 'MahodCivilDelivery_Evidence_*.zip' -File)
    Check 'exactly one evidence zip' ($zips.Count -eq 1)
    Expand-Archive -LiteralPath $zips[0].FullName -DestinationPath $expanded
    $environment = Get-Content -LiteralPath (Join-Path $expanded 'environment.json') -Raw | ConvertFrom-Json

    Check 'receipt-selected machine bundle' ($environment.bundle_path -eq $bundle)
    Check 'bundle source follows explicit precedence override' ($environment.bundle_source -eq 'override_0')
    Check 'bundle is present' ([bool]$environment.bundle_present)
    Check 'package revision read from bundle manifest' ($environment.bundle_package_revision -eq '9.8.7')
    Check 'receipt bundle matches' ([bool]$environment.receipt_bundle_matches)
    Check 'receipt package matches' ([bool]$environment.receipt_package_matches)
    Check 'both Civil host binaries fingerprinted' (@($environment.plugin_binaries).Count -eq 2)
    Check 'binary hashes captured' (-not (@($environment.plugin_binaries) | Where-Object { -not $_.sha256 }))
    Check 'exactly one active bundle candidate' ([int]$environment.active_bundle_count -eq 1)
    $winner = Get-Content -LiteralPath (Join-Path $expanded 'bundle_winner.json') -Raw | ConvertFrom-Json
    Check 'bundle winner receipt matches selected candidate' ($winner.receipt_matches_winner -eq $true)
    $winnerCandidate = @($winner.candidates | Where-Object { $_.active }) | Select-Object -First 1
    Check 'winner candidate records package revision' ($winnerCandidate.package_revision -eq '9.8.7')
    Check 'winner candidate records both host-specific plugin hashes' `
        (-not [string]::IsNullOrWhiteSpace([string]$winnerCandidate.plugin_2027_sha256) -and
         -not [string]::IsNullOrWhiteSpace([string]$winnerCandidate.plugin_2026_sha256))
    Check 'loaded identity artifact is emitted' (Test-Path -LiteralPath (Join-Path $expanded 'installed_loaded_identity.json'))
}
finally {
    $sandboxFull = [System.IO.Path]::GetFullPath($sandbox)
    $tempFull = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($sandboxFull.StartsWith($tempFull, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $sandboxFull).StartsWith('mcd-collect-test-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $sandboxFull -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'ALL COLLECT-EVIDENCE TESTS PASSED'
