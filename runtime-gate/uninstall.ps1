<#
.SYNOPSIS
    Runs the production managed uninstaller carried by the runtime gate.
#>
[CmdletBinding()]
param([switch]$PurgeData)

$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'Uninstall-MahodCivilDelivery.ps1'
if (-not (Test-Path -LiteralPath $script -PathType Leaf)) {
    throw "Production uninstaller missing from the runtime gate: $script"
}

$arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script)
if ($PurgeData) { $arguments += '-PurgeData' }
& powershell @arguments
exit $LASTEXITCODE
