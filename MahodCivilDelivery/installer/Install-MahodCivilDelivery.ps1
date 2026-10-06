<#
.SYNOPSIS
    Installs Mahod Civil Delivery for the current user as its own AutoCAD bundle.

.DESCRIPTION
    Mahod Civil Delivery is a separate tool from MahodAI (the AI assistant). It lives in its own bundle,
    %APPDATA%\Autodesk\ApplicationPlugins\Mahod.CivilDelivery.bundle, and never writes MahodAI's files, so a MahodAI
    update can neither replace nor break it. Per-user, no administrator rights.

    Every step is checked before the next one:
      1. Civil 3D / AutoCAD is closed.
      2. The payload matches MANIFEST.json exactly (every file's SHA-256, no extra file, no missing file).
      3. The new bundle is copied next to the installed one and verified again.
      4. Only then the two are swapped; the previous version is kept once (Mahod.CivilDelivery.previous — not a
         .bundle, so AutoCAD never loads it) and restored if the swap fails.
      5. An Add/Remove Programs entry for this user.
    Project data under %LOCALAPPDATA%\MahodAI_Civil3D\civil-delivery (profiles, decisions, runs) is never touched.

    Exit codes: 0 installed; 2 Civil 3D/AutoCAD is running; 3 payload invalid; 4 neither Civil 3D 2026 nor 2027 found;
    6 could not install (nothing changed / previous version restored).
#>
[CmdletBinding()]
param(
    [string]$PayloadDir,
    [switch]$ShowDialog,
    # Test-only seams (Test-Installer.ps1): a sandbox ApplicationPlugins root and uninstall key, no host checks.
    [string]$PluginsRootOverride,
    [string]$UninstallKeyOverride,
    [switch]$SkipHostCheck,
    [switch]$SimulateHostRunning,
    [ValidateSet('', 'AfterStaging', 'AfterSwap')]
    [string]$TestFailureAt = ''
)

$ErrorActionPreference = 'Stop'
# Defaults here, not in param(): $PSScriptRoot is empty there under Windows PowerShell 5.1.
if (-not $PayloadDir) { $PayloadDir = Join-Path $PSScriptRoot 'payload' }
$BundleName = 'Mahod.CivilDelivery.bundle'
$PreviousName = 'Mahod.CivilDelivery.previous'
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

function Stop-Install([int]$code, [string]$hebrew, [string]$detail) {
    Write-Host "FAIL ($code): $detail"
    Show-HeDialog ($hebrew + "`n`n" + $detail) 'Warning'
    exit $code
}

function Get-Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant() }

# Relative file list of a folder, forward slashes, sorted.
function Get-RelativeFiles([string]$root) {
    $full = (Resolve-Path -LiteralPath $root).Path.TrimEnd('\') + '\'
    @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($full.Length).Replace('\', '/') } | Sort-Object)
}

# The bundle folder must hold exactly the manifest's files with exactly its hashes.
function Test-BundleMatches([string]$bundle, $manifest) {
    $expected = @($manifest.files.PSObject.Properties | ForEach-Object { $_.Name } | Sort-Object)
    $actual = Get-RelativeFiles $bundle
    if (($expected -join '|') -ne ($actual -join '|')) { return "file list differs from MANIFEST.json" }
    foreach ($p in $manifest.files.PSObject.Properties) {
        $hash = Get-Sha256 (Join-Path $bundle ($p.Name.Replace('/', '\')))
        if ($hash -ne ([string]$p.Value).ToUpperInvariant()) { return "hash differs: $($p.Name)" }
    }
    return $null
}

# A folder is ours only if its PackageContents.xml names this product.
function Test-OwnBundle([string]$folder) {
    $xml = Join-Path $folder 'PackageContents.xml'
    if (-not (Test-Path -LiteralPath $xml)) { return $false }
    try { return ([xml](Get-Content -LiteralPath $xml -Raw -Encoding UTF8)).ApplicationPackage.Name -eq $ProductName } catch { return $false }
}

# ── 1. host ──────────────────────────────────────────────────────────────────
if ($SimulateHostRunning -or (-not $SkipHostCheck -and @(Get-Process -Name acad -ErrorAction SilentlyContinue).Count -gt 0)) {
    Stop-Install 2 'Civil 3D / AutoCAD פתוח. יש לשמור את העבודה, לסגור אותו ולהריץ את ההתקנה שוב. לא שונה דבר.' 'acad.exe is running'
}
$civilHosts = @('2026', '2027') | Where-Object { Test-Path -LiteralPath "C:\Program Files\Autodesk\AutoCAD $_\C3D\AeccDbMgd.dll" }
if (-not $SkipHostCheck -and @($civilHosts).Count -eq 0) {
    Stop-Install 4 'לא נמצא Civil 3D 2026 או 2027 במחשב. הכלי מיועד ל־Civil 3D 2026 ו־2027. לא שונה דבר.' 'Civil 3D 2026/2027 not found'
}

# ── 2. payload ───────────────────────────────────────────────────────────────
$manifestPath = Join-Path $PayloadDir 'MANIFEST.json'
$sourceBundle = Join-Path $PayloadDir $BundleName
if (-not (Test-Path -LiteralPath $manifestPath) -or -not (Test-Path -LiteralPath $sourceBundle)) {
    Stop-Install 3 'חבילת ההתקנה פגומה. יש להוריד אותה מחדש. לא שונה דבר.' 'MANIFEST.json or the bundle is missing'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.product -ne $ProductName -or [string]::IsNullOrWhiteSpace($manifest.version)) {
    Stop-Install 3 'חבילת ההתקנה פגומה. יש להוריד אותה מחדש. לא שונה דבר.' 'MANIFEST.json does not describe this product'
}
$bad = Test-BundleMatches $sourceBundle $manifest
if ($bad) { Stop-Install 3 'חבילת ההתקנה פגומה. יש להוריד אותה מחדש. לא שונה דבר.' "payload: $bad" }

# ── 3. stage next to the installed bundle ─────────────────────────────────────
$pluginsRoot = if ($PluginsRootOverride) { $PluginsRootOverride } else { Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins' }
$target = Join-Path $pluginsRoot $BundleName
$previous = Join-Path $pluginsRoot $PreviousName
$staging = Join-Path $pluginsRoot ('Mahod.CivilDelivery.installing-' + [guid]::NewGuid().ToString('N'))
$olderPrevious = Join-Path $pluginsRoot ('Mahod.CivilDelivery.previous-old-' + [guid]::NewGuid().ToString('N'))
$movedOlder = $false      # the older kept copy was set aside (deleted only after success)
$movedPrevious = $false   # the installed bundle was renamed to .previous
$swapped = $false         # the staged bundle became the installed one
try {
    New-Item -ItemType Directory -Force -Path $pluginsRoot | Out-Null
    Copy-Item -LiteralPath $sourceBundle -Destination $staging -Recurse -Force
    $bad = Test-BundleMatches $staging $manifest
    if ($bad) { throw "staged copy: $bad" }
    if ($TestFailureAt -eq 'AfterStaging') { throw 'injected failure after staging' }

    # ── 4. swap ──────────────────────────────────────────────────────────────
    if (Test-Path -LiteralPath $target) {
        if (-not (Test-OwnBundle $target)) { throw "$target exists but is not a $ProductName bundle; it was left untouched" }
        if (Test-Path -LiteralPath $previous) {
            if (-not (Test-OwnBundle $previous)) { throw "$previous exists but is not ours; it was left untouched" }
            Rename-Item -LiteralPath $previous -NewName (Split-Path -Leaf $olderPrevious)
            $movedOlder = $true
        }
        Rename-Item -LiteralPath $target -NewName $PreviousName
        $movedPrevious = $true
    }
    Rename-Item -LiteralPath $staging -NewName $BundleName
    $swapped = $true
    if ($TestFailureAt -eq 'AfterSwap') { throw 'injected failure after swap' }
    $bad = Test-BundleMatches $target $manifest
    if ($bad) { throw "installed bundle: $bad" }
    if ($movedOlder) { Remove-Item -LiteralPath $olderPrevious -Recurse -Force -ErrorAction SilentlyContinue }
}
catch {
    $why = $_.Exception.Message
    # Put back exactly what was there: remove what this run wrote, then return the old bundle to its place.
    if ($swapped -and (Test-Path -LiteralPath $target)) { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
    if ($movedPrevious -and (Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $target)) {
        Rename-Item -LiteralPath $previous -NewName $BundleName -ErrorAction SilentlyContinue
    }
    if ($movedOlder -and (Test-Path -LiteralPath $olderPrevious) -and -not (Test-Path -LiteralPath $previous)) {
        Rename-Item -LiteralPath $olderPrevious -NewName $PreviousName -ErrorAction SilentlyContinue
    }
    Stop-Install 6 'ההתקנה לא הושלמה, והגרסה שהייתה מותקנת נשארה כפי שהייתה.' $why
}

# ── 5. Add/Remove Programs (this user) ─────────────────────────────────────────
$uninstallKey = if ($UninstallKeyOverride) { $UninstallKeyOverride } else { 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MahodCivilDelivery' }
$uninstaller = Join-Path $target 'Installer\Uninstall-MahodCivilDelivery.ps1'
try {
    New-Item -Path $uninstallKey -Force | Out-Null
    $values = @{
        DisplayName = $ProductName
        DisplayVersion = [string]$manifest.version
        Publisher = 'Mahod Engineering'
        InstallLocation = $target
        UninstallString = "`"$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe`" -NoProfile -ExecutionPolicy Bypass -File `"$uninstaller`" -ShowDialog"
    }
    foreach ($k in $values.Keys) { Set-ItemProperty -Path $uninstallKey -Name $k -Value $values[$k] }
    Set-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -Type DWord
} catch {
    Write-Host "WARN: the Add/Remove Programs entry could not be written: $($_.Exception.Message)"
}

# ── notes the engineer should see ─────────────────────────────────────────────
$notes = @()
foreach ($root in @($pluginsRoot, 'C:\Program Files\Autodesk\ApplicationPlugins')) {
    $old = Join-Path $root 'MahodAI.bundle\PackageContents.xml'
    if (Test-Path -LiteralPath $old) {
        try {
            if (([xml](Get-Content -LiteralPath $old -Raw -Encoding UTF8)).ApplicationPackage.Name -eq 'MahodAI Civil Delivery') {
                $notes += "נמצאה במחשב גרסת מבחן ישנה של Civil Delivery בתוך MahodAI ($root). היא אינה נדרשת עוד; מומלץ להתקין מחדש את MahodAI מהמתקין הרשמי שלו."
            }
        } catch { }
    }
}

Write-Host "OK: $ProductName $($manifest.version) installed at $target"
$message = "Mahod Civil Delivery $($manifest.version) הותקן.`n`nיש לפתוח את Civil 3D 2026 או 2027: הכפתור Civil Delivery נמצא ברצועת הכלים בלשונית MahodAI, או בלשונית Mahod במחשב ללא MahodAI (וגם בפקודה MCD_CIVIL_DELIVERY).`nהפרויקטים וההחלטות הקיימים נשמרו."
if ($notes.Count -gt 0) { $message += "`n`n" + ($notes -join "`n") }
Show-HeDialog $message 'Information'
exit 0
