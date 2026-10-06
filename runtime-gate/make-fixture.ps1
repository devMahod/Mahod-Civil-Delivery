<#
.SYNOPSIS
    Copies the 6422 drawings from the package into a WRITABLE work folder.
    Source project files are never touched. ASCII-only file.
#>
[CmdletBinding()]
param(
    [string]$PayloadDir = (Join-Path $PSScriptRoot 'payload'),
    [string]$Destination = (Join-Path $env:USERPROFILE 'MahodCivilDelivery_Fixtures')
)

$ErrorActionPreference = 'Stop'

$src = Join-Path $PayloadDir 'fixtures'
if (-not (Test-Path $src)) { throw "No fixtures in package: $src" }

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$copied = @()
foreach ($f in Get-ChildItem $src -Filter '*.dwg') {
    $dst = Join-Path $Destination $f.Name
    Copy-Item $f.FullName $dst -Force
    # Clear read-only in case the source copy carried it.
    Set-ItemProperty $dst -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
    $copied += $dst
}

Write-Host ''
Write-Host "Writable fixtures ready in: $Destination"
Write-Host ''
foreach ($c in $copied) {
    $size = [math]::Round((Get-Item $c).Length / 1MB, 2)
    Write-Host ("  {0}  ({1} MB)" -f (Split-Path $c -Leaf), $size)
}
Write-Host ''
Write-Host 'Open these copies in Civil 3D - never the originals under P:\data\6422.'
Write-Host 'Suggested first drawing: HW-CIVIL-CL.dwg (CL + Civil context)'
exit 0
