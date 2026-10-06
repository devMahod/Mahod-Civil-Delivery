<#
.SYNOPSIS
    Launches the exact setup EXE carried by the runtime gate.

.DESCRIPTION
    The former gate-specific copy routine could bypass the production installer's
    shadowing, restore-point and dual-host checks. This wrapper deliberately has no
    second deployment implementation: the candidate installer is the deploy path.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

if (Get-Process acad -ErrorAction SilentlyContinue) {
    Write-Error 'Close AutoCAD/Civil 3D before installing the candidate.'
    exit 2
}

$setups = @(Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter 'Mahod_Civil_Delivery_Setup_*.exe')
if ($setups.Count -ne 1) {
    throw "Expected exactly one setup EXE next to deploy.ps1; found $($setups.Count)."
}

Write-Host "Launching the exact runtime-gate candidate: $($setups[0].FullName)"
Write-Host 'The setup may request UAC when the MahodAI bundle loaded by AutoCAD is machine-wide.'
$process = Start-Process -FilePath $setups[0].FullName -PassThru
$process.WaitForExit()
Write-Host "Setup process closed (exit $($process.ExitCode)). Review its Hebrew result dialog before continuing."
exit $process.ExitCode
