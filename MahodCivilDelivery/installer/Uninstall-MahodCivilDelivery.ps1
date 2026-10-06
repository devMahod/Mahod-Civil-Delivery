<#
.SYNOPSIS
    Removes Mahod Civil Delivery for the current user.

.DESCRIPTION
    Removes only this tool's own bundle (Mahod.CivilDelivery.bundle, and the kept .previous copy) after checking that
    each folder really is a Mahod Civil Delivery bundle, and its Add/Remove Programs entry. MahodAI and every other
    plugin are untouched, and so is project data under %LOCALAPPDATA%\MahodAI_Civil3D\civil-delivery (profiles,
    decisions, runs) — reinstalling finds it again.

    Exit codes: 0 removed (or nothing to remove); 2 Civil 3D/AutoCAD is running; 6 a folder could not be removed.
#>
[CmdletBinding()]
param(
    [switch]$ShowDialog,
    [string]$PluginsRootOverride,
    [string]$UninstallKeyOverride,
    [switch]$SkipHostCheck
)

$ErrorActionPreference = 'Stop'
$ProductName = 'Mahod Civil Delivery'

function Show-HeDialog([string]$text, [string]$icon) {
    if (-not $ShowDialog) { return }
    try {
        Add-Type -AssemblyName System.Windows.Forms | Out-Null
        $opts = [System.Windows.Forms.MessageBoxOptions]::RtlReading -bor [System.Windows.Forms.MessageBoxOptions]::RightAlign
        [void][System.Windows.Forms.MessageBox]::Show($text, $ProductName, [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::$icon, [System.Windows.Forms.MessageBoxDefaultButton]::Button1, $opts)
    } catch { }
}

function Test-OwnBundle([string]$folder) {
    $xml = Join-Path $folder 'PackageContents.xml'
    if (-not (Test-Path -LiteralPath $xml)) { return $false }
    try { return ([xml](Get-Content -LiteralPath $xml -Raw -Encoding UTF8)).ApplicationPackage.Name -eq $ProductName } catch { return $false }
}

if (-not $SkipHostCheck -and @(Get-Process -Name acad -ErrorAction SilentlyContinue).Count -gt 0) {
    Show-HeDialog 'Civil 3D / AutoCAD פתוח. יש לסגור אותו ולהריץ את ההסרה שוב. לא הוסר דבר.' 'Warning'
    exit 2
}

$pluginsRoot = if ($PluginsRootOverride) { $PluginsRootOverride } else { Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins' }
$failed = @()
foreach ($name in @('Mahod.CivilDelivery.bundle', 'Mahod.CivilDelivery.previous')) {
    $folder = Join-Path $pluginsRoot $name
    if (-not (Test-Path -LiteralPath $folder)) { continue }
    if (-not (Test-OwnBundle $folder)) { $failed += "$folder (not a $ProductName bundle; left untouched)"; continue }
    try { Remove-Item -LiteralPath $folder -Recurse -Force } catch { $failed += "$folder ($($_.Exception.Message))" }
}
if ($failed.Count -gt 0) {
    Write-Host ('FAIL: ' + ($failed -join '; '))
    Show-HeDialog ("ההסרה לא הושלמה:`n" + ($failed -join "`n")) 'Warning'
    exit 6
}

$uninstallKey = if ($UninstallKeyOverride) { $UninstallKeyOverride } else { 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MahodCivilDelivery' }
if (Test-Path -LiteralPath $uninstallKey) { Remove-Item -LiteralPath $uninstallKey -Recurse -Force }

Write-Host "OK: $ProductName removed"
Show-HeDialog 'Mahod Civil Delivery הוסר. הפרויקטים וההחלטות נשמרו במחשב ויימצאו שוב אם הכלי יותקן מחדש.' 'Information'
exit 0
