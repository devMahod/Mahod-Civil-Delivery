<#
.SYNOPSIS
    Installs the MahodAI Civil 3D platform build that contains Mahod Civil Delivery.

.DESCRIPTION
    ARCHITECTURE NOTE (this drives the whole design):
    Civil Delivery is NOT a separate plugin. Its commands live inside
    MahodAI.Civil3D.Plugin.dll, which is the MahodAI platform assembly. So this is a
    versioned PLATFORM UPGRADE, not a side-by-side feature install, and it must behave
    like one:

      * detect any existing MahodAI install, its platform version and file hashes;
      * REFUSE to silently downgrade a newer or unknown MahodAI;
      * take a managed, registered restore point (not a stray sibling folder);
      * record an ownership manifest of every file this install wrote;
      * leave foreign files in the bundle untouched.

    Uninstall then RESTORES the previous platform rather than deleting the bundle, so
    removing Civil Delivery can never remove unrelated MahodAI functionality.

    Per-user, no admin. ASCII-only file.
#>
[CmdletBinding()]
param(
    [string]$PayloadDir = (Join-Path $PSScriptRoot 'payload'),
    [switch]$AllowDowngrade,
    [switch]$Quiet,
    # Test-only overrides. They let the lifecycle suite exercise the real install
    # logic inside a sandbox instead of the live Autodesk folder, so verification
    # never depends on - or disturbs - the engineer's machine.
    [string]$BundleRootOverride,
    [string]$StateRootOverride,
    [switch]$SkipHostCheck,
    # Engineer-facing Hebrew result dialogs (the setup EXE passes this; tests do not).
    [switch]$ShowDialog,
    # Test-only: stand in for the real ApplicationPlugins roots, highest precedence
    # first, so the shadowing logic can be exercised without touching Program Files.
    # Passed as ONE comma-separated string because 'powershell -File' cannot bind an
    # array parameter - it splits the elements into positional arguments.
    [string]$PluginRootsOverride,
    # The bootstrap captures these values before UAC.  When a different administrator
    # supplies credentials, the elevated process otherwise sees the administrator's
    # HKCU/APPDATA and would install Natalie's per-user state into the wrong profile.
    # All four values are an atomic context: partial input is refused.
    [string]$OriginalUserSid,
    [string]$OriginalUserName,
    [string]$OriginalAppData,
    [string]$OriginalLocalAppData,
    # Test-only failure seam used by Test-InstallerLifecycle.ps1. Production callers
    # never pass it; every injected failure must leave bundle/state byte-identical.
    [ValidateSet('', 'AfterCandidatePrepared', 'AfterBundleSwap', 'AfterProfileWrite', 'AfterStateWrite')]
    [string]$TestFailureAt = ''
)

$ErrorActionPreference = 'Stop'
$packageRevision = '1.2.91'

$originalContextValues = @($OriginalUserSid, $OriginalUserName, $OriginalAppData, $OriginalLocalAppData)
$originalContextCount = @($originalContextValues | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
if ($originalContextCount -ne 0 -and $originalContextCount -ne 4) {
    throw 'Original user context is incomplete; SID, username, APPDATA and LOCALAPPDATA must be supplied together.'
}
if ($originalContextCount -eq 4) {
    try {
        $validatedSid = New-Object System.Security.Principal.SecurityIdentifier($OriginalUserSid)
        if ($validatedSid.Value -cne $OriginalUserSid) { throw 'SID is not canonical.' }
    } catch { throw "Original user SID is invalid: $($_.Exception.Message)" }
    if ($OriginalUserName -match '[\r\n]' -or [string]::IsNullOrWhiteSpace($OriginalUserName)) {
        throw 'Original username is invalid.'
    }
    foreach ($candidatePath in @($OriginalAppData, $OriginalLocalAppData)) {
        if (-not [System.IO.Path]::IsPathRooted($candidatePath)) { throw 'Original user profile paths must be absolute.' }
        try { [void][System.IO.Path]::GetFullPath($candidatePath) }
        catch { throw "Original user profile path is invalid: $candidatePath" }
    }
    $effectiveUserSid = $validatedSid.Value
    $effectiveUserName = $OriginalUserName
    $effectiveAppData = [System.IO.Path]::GetFullPath($OriginalAppData)
    $effectiveLocalAppData = [System.IO.Path]::GetFullPath($OriginalLocalAppData)
    $hasExplicitOriginalUser = $true
} else {
    $effectiveUserSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $effectiveUserName = $env:USERNAME
    $effectiveAppData = $env:APPDATA
    $effectiveLocalAppData = $env:LOCALAPPDATA
    $hasExplicitOriginalUser = $false
}

function Info([string]$m) { if (-not $Quiet) { Write-Host $m } }

# Refusals must reach the caller as a documented exit code. Write-Error under
# $ErrorActionPreference='Stop' aborts the script first and collapses every refusal
# to exit 1, which hides WHY the installer declined.
# Hebrew dialogs for the engineer (only when -ShowDialog, i.e. launched from the setup EXE).
function Show-HeDialog([string]$text, [string]$title, [string]$icon) {
    if (-not $ShowDialog) { return }
    try {
        Add-Type -AssemblyName System.Windows.Forms | Out-Null
        $ic = [System.Windows.Forms.MessageBoxIcon]::$icon
        $opts = [System.Windows.Forms.MessageBoxOptions]::RtlReading -bor [System.Windows.Forms.MessageBoxOptions]::RightAlign
        [void][System.Windows.Forms.MessageBox]::Show($text, $title, [System.Windows.Forms.MessageBoxButtons]::OK, $ic, [System.Windows.Forms.MessageBoxDefaultButton]::Button1, $opts)
    } catch { }
}

function HeReason([int]$code) {
    switch ($code) {
        2 { return 'Civil 3D / AutoCAD פתוח. יש לסגור אותו ולהריץ את ההתקנה שוב.' }
        3 { return 'לא נמצא Civil 3D נתמך (2026/2027) במחשב. ההתקנה לא שינתה דבר.' }
        4 { return 'במחשב יש Civil 3D 2026 בלבד, והחבילה מכילה את רכיב 2027 בלבד. ההתקנה לא שינתה דבר.' }
        5 { return 'במחשב מותקנת גרסת MahodAI חדשה יותר (או לא מזוהה). ההתקנה לא שינתה דבר.' }
        7 { return 'MahodAI מותקן לכל המשתמשים במחשב — נדרש אישור מנהל (UAC). יש להריץ שוב ולאשר.' }
        8 { return 'קיים עותק אחר של MahodAI שייטען לפני העותק שהותקן. ההתקנה לא הושלמה.' }
        default { return 'ההתקנה לא הושלמה.' }
    }
}

function Fail([string]$message, [int]$code) {
    $host.UI.WriteErrorLine($message)
    Show-HeDialog ((HeReason $code) + "`n`n" + $message) 'Mahod Civil Delivery — ההתקנה לא בוצעה' 'Warning'
    exit $code
}

function FileSha([string]$p) { (Get-FileHash $p -Algorithm SHA256).Hash.ToLowerInvariant() }

function CanonicalPath([string]$p) {
    if ([string]::IsNullOrWhiteSpace($p) -or -not [System.IO.Path]::IsPathRooted($p)) {
        throw "Path is not absolute: '$p'"
    }
    [System.IO.Path]::GetFullPath($p).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
}

function SamePath([string]$a, [string]$b) {
    [string]::Equals((CanonicalPath $a), (CanonicalPath $b), [System.StringComparison]::OrdinalIgnoreCase)
}

function PathIsWithin([string]$root, [string]$candidate) {
    $prefix = (CanonicalPath $root) + [System.IO.Path]::DirectorySeparatorChar
    (CanonicalPath $candidate).StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function BundleRelativePath([string]$root, [string]$relative) {
    if ([string]::IsNullOrWhiteSpace($relative) -or [System.IO.Path]::IsPathRooted($relative)) {
        throw "Unsafe bundle-relative path in lifecycle state: '$relative'"
    }
    $target = [System.IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not (PathIsWithin $root $target)) {
        throw "Escaping bundle-relative path in lifecycle state: '$relative'"
    }
    return $target
}

function Get-PlatformVersion([string]$dll) {
    if (-not (Test-Path $dll)) { return $null }
    try { return [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $dll)).FileVersion }
    catch { return $null }
}

function Get-Inventory([string]$root) {
    $map = @{}
    if (-not (Test-Path $root)) { return $map }
    foreach ($f in Get-ChildItem $root -Recurse -File) {
        $map[$f.FullName.Substring($root.Length + 1)] = FileSha $f.FullName
    }
    return $map
}

# ---------------------------------------------------- where AutoCAD really looks
# AutoCAD scans several ApplicationPlugins roots. If MahodAI.bundle exists in more
# than one, only ONE of them is loaded - and on a real machine the machine-wide copy
# won over the per-user copy. Installing per-user while a machine-wide bundle exists
# means the engineer keeps running the old build while every hash check passes.
# Highest precedence first.
function Get-PluginRoots {
    if ($PluginRootsOverride) { return @($PluginRootsOverride -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
    @(
        (Join-Path $env:ProgramFiles 'Autodesk\ApplicationPlugins'),
        (Join-Path $env:ProgramData  'Autodesk\ApplicationPlugins'),
        (Join-Path $effectiveAppData 'Autodesk\ApplicationPlugins')
    )
}

function Get-ExistingBundles {
    $found = @()
    foreach ($r in (Get-PluginRoots)) {
        $b = Join-Path $r 'MahodAI.bundle'
        if (Test-Path (Join-Path $b 'Contents\MahodAI.Civil3D.Plugin.dll')) {
            $found += [PSCustomObject]@{
                Root    = $r
                Bundle  = $b
                Version = Get-PlatformVersion (Join-Path $b 'Contents\MahodAI.Civil3D.Plugin.dll')
                Machine = if ($PluginRootsOverride) { $r -ne ((Get-PluginRoots) | Select-Object -Last 1) }
                          else { $r -notlike "$effectiveAppData*" }
            }
        }
    }
    return $found
}

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-Writable([string]$dir) {
    try {
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir -EA Stop | Out-Null }
        $probe = Join-Path $dir (".mcd_write_probe_" + [guid]::NewGuid().ToString('N'))
        Set-Content -Path $probe -Value 'x' -EA Stop
        Remove-Item $probe -Force -EA SilentlyContinue
        return $true
    } catch { return $false }
}

# ------------------------------------------------------------------ paths
# Resolution order:
#   1. -BundleRootOverride  : the caller pins the target (tests; the shadow check below
#                              still runs against the plugin roots, so a pinned target
#                              that would be shadowed is refused, not honoured blindly).
#   2. an existing bundle   : upgrade the one AutoCAD actually loads.
#   3. per-user root        : a clean machine.
if ($BundleRootOverride) {
    $pluginsRoot = $BundleRootOverride
} else {
    $existing = @(Get-ExistingBundles)
    $winner = $existing | Select-Object -First 1   # highest-precedence root wins
    if ($winner) {
        $pluginsRoot = $winner.Root
        if ($winner.Machine) {
            Info "An existing MahodAI is installed for ALL USERS and is the one AutoCAD loads:"
            Info "  $($winner.Bundle)  (platform $($winner.Version))"
            Info 'Targeting that location so the upgrade is the build that actually runs.'
            if (-not (Test-Elevated) -and -not (Test-Writable $pluginsRoot)) {
                Fail ("MahodAI is installed for ALL USERS at:`n  $($winner.Bundle)`n" +
                      "That copy is the one AutoCAD loads, and updating it needs Administrator rights.`n" +
                      "Installing per-user instead would be silently ignored, so nothing was changed.`n`n" +
                      "Re-run this installer as Administrator (right-click > Run as administrator).") 7
            }
        }
    } else {
        # Clean machine: the lowest-precedence (per-user) root, whatever the roots are.
        $pluginsRoot = (Get-PluginRoots) | Select-Object -Last 1
    }
}
$bundle       = Join-Path $pluginsRoot 'MahodAI.bundle'
$pluginDll    = Join-Path $bundle 'Contents\MahodAI.Civil3D.Plugin.dll'
$stateRoot    = if ($StateRootOverride) { $StateRootOverride } else { Join-Path $effectiveLocalAppData 'MahodAI_Civil3D\civil-delivery' }
$stateFile    = Join-Path $stateRoot 'install_state.json'
$restoreRoot  = Join-Path $stateRoot 'restore-points'
$uninstallerTarget = Join-Path $bundle 'Installer\Uninstall-MahodCivilDelivery.ps1'
$perUserPluginsRoot = Join-Path $effectiveAppData 'Autodesk\ApplicationPlugins'
try { $machineWideInstall = -not (SamePath $pluginsRoot $perUserPluginsRoot) }
catch { $machineWideInstall = $true }

# ------------------------------------------------------------- preflight
$acad = if ($SkipHostCheck) { $null } else { Get-Process acad -ErrorAction SilentlyContinue }
if ($acad) {
    Fail ("AutoCAD/Civil 3D is running (pid " + ($acad.Id -join ', ') +
                 "). Close it and run the installer again. Nothing was changed.") 2
}

# Every Civil 3D on the machine is listed, not only the ones we ship for. A year we do
# not carry loads nothing and says nothing - the engineer sees a successful install and an
# empty ribbon (2026-08-26). Naming it here turns that silence into one sentence.
$civilYears = @()
foreach ($y in 2024, 2025, 2026, 2027, 2028) {
    if (Test-Path "C:\Program Files\Autodesk\AutoCAD $y\C3D\AeccDbMgd.dll") { $civilYears += $y }
}
$civil2027 = $civilYears -contains 2027
$civil2026 = $civilYears -contains 2026
Info ("Civil 3D found : " + $(if ($civilYears) { $civilYears -join ', ' } else { '(none)' }))

$incomingDll = Join-Path $PayloadDir 'MahodAI.bundle\Contents\MahodAI.Civil3D.Plugin.dll'
if (-not (Test-Path $incomingDll)) { throw "Payload incomplete: $incomingDll not found" }
$has2026Payload = Test-Path (Join-Path $PayloadDir 'MahodAI.bundle\Contents\2026\MahodAI.Civil3D.Plugin.dll')

if (-not ($civil2027 -or $civil2026)) {
    Fail 'No supported Civil 3D (2026/2027) found. Nothing was changed.' 3
}
if ($civil2026 -and -not $civil2027 -and -not $has2026Payload) {
    Fail ('This package carries the Civil 3D 2027 component only. Civil 3D 2026 support ' +
                 'needs a package built on a machine with the real Civil 3D 2026 references. ' +
                 'Nothing was changed.') 4
}

# ------------------------------------------- existing platform + downgrade gate
$incomingVersion = Get-PlatformVersion $incomingDll
$existingVersion = Get-PlatformVersion $pluginDll
$existingInventory = Get-Inventory $bundle
$hadExisting = $existingInventory.Count -gt 0

# The engineer reads PACKAGE revisions (1.2.x); platform assembly versions (1.3.x)
# look like a different, older product. Name the package first, everywhere.
$priorPackageRevision = $null
if (Test-Path $stateFile -PathType Leaf) {
    try {
        $priorPackageRevision = [string](([System.IO.File]::ReadAllText(
            $stateFile, [System.Text.UTF8Encoding]::new($false)) | ConvertFrom-Json).package_revision)
    } catch { $priorPackageRevision = $null }
}
$priorPackageText = if ($priorPackageRevision) { "Mahod Civil Delivery $priorPackageRevision" } else { '(no package recorded)' }
Info ''
Info "Incoming package          : Mahod Civil Delivery $packageRevision (platform $incomingVersion)"
Info "Installed package         : $priorPackageText (platform $(if ($existingVersion) { $existingVersion } else { '(none)' }))"
Info "Incoming MahodAI platform : $incomingVersion"
Info "Installed MahodAI platform: $(if ($existingVersion) { $existingVersion } else { '(none)' })"

# The gate keys off FILES PRESENT, not off a readable version. An installed bundle
# whose version cannot be determined is the most dangerous case, not the safest:
# treating it as "nothing installed" is exactly how a working MahodAI gets silently
# overwritten.
if ($hadExisting) {
    $ev = $null; $iv = $null
    $evOk = $existingVersion -and [Version]::TryParse($existingVersion, [ref]$ev)
    $ivOk = $incomingVersion -and [Version]::TryParse($incomingVersion, [ref]$iv)

    if (-not $evOk -or -not $ivOk) {
        if (-not $AllowDowngrade) {
            Fail ("A MahodAI bundle is already installed ($($existingInventory.Count) file(s)) but its " +
                         "platform version could not be determined (installed='$existingVersion', " +
                         "incoming='$incomingVersion'). Refusing to overwrite an unknown build - it may provide " +
                         "functionality this package does not. Nothing was changed. " +
                         "Re-run with -AllowDowngrade only if you are certain.") 5
        }
        Info 'WARNING: existing MahodAI version unreadable - proceeding because -AllowDowngrade was given.'
    }
    elseif ($ev -gt $iv) {
        if (-not $AllowDowngrade) {
            Fail ("A NEWER MahodAI platform is already installed ($existingVersion > $incomingVersion). " +
                         "Installing would downgrade it and could remove functionality that build provides. " +
                         "Nothing was changed. Re-run with -AllowDowngrade only if this is deliberate.") 5
        }
        Info "WARNING: downgrading MahodAI platform $existingVersion -> $incomingVersion (explicitly allowed)."
    }
    elseif ($ev -eq $iv) { Info 'Same platform version - performing a repair/reinstall.' }
    else { Info "Upgrading to Mahod Civil Delivery $packageRevision (from $priorPackageText; platform $existingVersion -> $incomingVersion)" }
}

# ---------------------------------------------------------------- transaction
# A live bundle is never overlaid file-by-file.  Build and verify a complete sibling
# directory first, then switch directory names on the same volume.  Any later required
# profile/state failure restores the exact old directory before returning an error.
$payloadBundle = Join-Path $PayloadDir 'MahodAI.bundle'
if (-not (Test-Path $payloadBundle -PathType Container)) {
    Fail "Payload bundle missing: $payloadBundle. Nothing was changed." 6
}

# A valid earlier install state is the only authority for the ORIGINAL pre-Civil
# restore chain.  A repair/reinstall must never replace it with a snapshot of the
# candidate that happens to be installed today.
$priorState = $null
$previousPlatform = $null
if (Test-Path $stateFile -PathType Leaf) {
    try { $priorState = Get-Content $stateFile -Raw | ConvertFrom-Json }
    catch { Fail "Existing install state is unreadable; refusing to lose its restore chain. Nothing was changed." 9 }
    $priorSchema = [int]$priorState.schema_version
    if ($priorSchema -lt 2 -or $priorSchema -gt 4) {
        Fail "Existing install state schema '$priorSchema' is not an owned/supported lifecycle record. Nothing was changed." 9
    }
    try { $samePriorBundle = SamePath ([string]$priorState.bundle_path) $bundle }
    catch { $samePriorBundle = $false }
    if (-not $samePriorBundle) {
        Fail "Existing install state belongs to a different bundle path ('$($priorState.bundle_path)' vs '$bundle'). Nothing was changed." 9
    }
    if ($priorState.profile_path) {
        try { $priorProfileOwned = PathIsWithin $stateRoot ([string]$priorState.profile_path) }
        catch { $priorProfileOwned = $false }
        if (-not $priorProfileOwned) {
            Fail "Existing install state contains a profile path outside its owned state root. Nothing was changed." 9
        }
    }
    if (-not $priorState.previous_platform) {
        Fail "Existing install state has no previous_platform record. Nothing was changed." 9
    }
    if ([bool]$priorState.previous_platform.existed) {
        $priorRestore = [string]$priorState.previous_platform.restore_point
        if (-not $priorRestore -or -not (Test-Path $priorRestore -PathType Container)) {
            Fail "Existing install state points to a missing restore point. Nothing was changed." 9
        }
        $priorRestoreInventory = Get-Inventory $priorRestore
        $priorRecordedInventory = [ordered]@{}
        if ($priorState.previous_platform.inventory) {
            foreach ($property in $priorState.previous_platform.inventory.PSObject.Properties) {
                $priorRecordedInventory[$property.Name] = ([string]$property.Value).ToLowerInvariant()
            }
            if ($priorRecordedInventory.Count -ne $priorRestoreInventory.Count) {
                Fail "Existing restore-point inventory count does not match its recorded manifest. Nothing was changed." 9
            }
            foreach ($rel in $priorRecordedInventory.Keys) {
                if ($priorRestoreInventory[$rel] -ne $priorRecordedInventory[$rel]) {
                    Fail "Existing restore point failed its recorded SHA256 manifest at '$rel'. Nothing was changed." 9
                }
            }
        } else {
            # One-time migration from the earlier schema: file_count was the only
            # manifest.  Require that count, then seal the actual restore bytes into
            # the new full SHA256 inventory before any live bundle mutation.
            if ([int]$priorState.previous_platform.file_count -ne $priorRestoreInventory.Count) {
                Fail "Legacy restore-point file count does not match its state. Nothing was changed." 9
            }
            foreach ($rel in @($priorRestoreInventory.Keys | Sort-Object)) {
                $priorRecordedInventory[$rel] = $priorRestoreInventory[$rel]
            }
        }
    } else {
        $priorRecordedInventory = [ordered]@{}
    }
    $previousPlatform = [ordered]@{
        existed       = [bool]$priorState.previous_platform.existed
        version       = [string]$priorState.previous_platform.version
        file_count    = [int]$priorState.previous_platform.file_count
        restore_point = [string]$priorState.previous_platform.restore_point
        inventory     = $priorRecordedInventory
    }
}

$transactionId = [guid]::NewGuid().ToString('N')
$candidateBundle = Join-Path $pluginsRoot ("MahodAI.candidate-$transactionId")
$swapBackup = Join-Path $pluginsRoot ("MahodAI.rollback-$transactionId")
$transactionStateDir = Join-Path $stateRoot (".install-txn-$transactionId")
$stateRootExisted = Test-Path $stateRoot -PathType Container
$stateBackups = @{}
$disabled = @()
$priorRetiredDuplicates = @()
$couldNotDisable = @()
$written = @{}
$foreign = @()
$retiredStaleOwned = @()
$preservedModifiedOwned = @()
$createdRestorePoint = $null
$oldMovedAside = $false
$bundleSwapped = $false
$transactionCommitted = $false
$publishRegistry = -not ($BundleRootOverride -or $PluginRootsOverride -or $StateRootOverride)
$regPath = if ($hasExplicitOriginalUser) {
    "Registry::HKEY_USERS\$effectiveUserSid\Software\Microsoft\Windows\CurrentVersion\Uninstall\MahodCivilDelivery"
} else {
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MahodCivilDelivery'
}
$registryNames = @('DisplayName','DisplayVersion','Publisher','InstallLocation','NoModify','NoRepair','UninstallString')
$registryExisted = $false
$registryBackup = @{}

if ($priorState -and $priorState.retired_duplicate_bundles) {
    foreach ($receipt in @($priorState.retired_duplicate_bundles)) {
        # Preserve durable duplicate receipts across repairs.  The production
        # uninstaller validates their paths and sealed inventories before use.
        $priorRetiredDuplicates += $receipt
    }
}

function Backup-StateFile([string]$target) {
    if ($script:stateBackups.ContainsKey($target)) { return }
    if (Test-Path $target -PathType Leaf) {
        New-Item -ItemType Directory -Force -Path $script:transactionStateDir | Out-Null
        $backup = Join-Path $script:transactionStateDir ([guid]::NewGuid().ToString('N') + '.bak')
        Copy-Item -LiteralPath $target -Destination $backup -Force -ErrorAction Stop
        $script:stateBackups[$target] = $backup
    } else {
        $script:stateBackups[$target] = $null
    }
}

function Restore-StateFiles {
    foreach ($entry in $script:stateBackups.GetEnumerator()) {
        $target = [string]$entry.Key
        if ($entry.Value) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            Copy-Item -LiteralPath ([string]$entry.Value) -Destination $target -Force -ErrorAction SilentlyContinue
        } else {
            Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
        }
    }
}

try {
    New-Item -ItemType Directory -Force -Path $pluginsRoot, $stateRoot, $restoreRoot | Out-Null

    # Retire any higher/lower-precedence duplicates BEFORE touching the target.  If
    # even one cannot be renamed, restore those already renamed and refuse with 8.
    if (-not $BundleRootOverride -or $PluginRootsOverride) {
        foreach ($other in @(Get-ExistingBundles)) {
            if ($other.Bundle -ieq $bundle) { continue }
            $aside = $other.Bundle + ".disabled-$transactionId"
            try {
                $otherInventory = Get-Inventory $other.Bundle
                Rename-Item -LiteralPath $other.Bundle -NewName (Split-Path $aside -Leaf) -ErrorAction Stop
                $sealedInventory = [ordered]@{}
                foreach ($relative in @($otherInventory.Keys | Sort-Object)) {
                    $sealedInventory[$relative] = $otherInventory[$relative]
                }
                $disabled += [PSCustomObject]@{
                    From = $other.Bundle; To = $aside; Version = $other.Version
                    FileCount = $otherInventory.Count; Inventory = $sealedInventory
                }
                Info 'Retired duplicate MahodAI (renamed aside, not deleted):'
                Info "  $($other.Bundle)  (platform $($other.Version))"
            } catch {
                $couldNotDisable += $other
                throw "SHADOWING: a second MahodAI could not be renamed: $($other.Bundle)"
            }
        }
    }

    # Preserve the true pre-Civil platform once.  Repairs reuse the prior state's
    # restore_point and never snapshot the current candidate over that chain.
    if (-not $previousPlatform) {
        if ($hadExisting) {
            $createdRestorePoint = Join-Path $restoreRoot ("MahodAI.bundle-$transactionId")
            Copy-Item -LiteralPath $bundle -Destination $createdRestorePoint -Recurse -ErrorAction Stop
            $restoreInventory = Get-Inventory $createdRestorePoint
            if ($restoreInventory.Count -ne $existingInventory.Count) { throw 'Restore point file-set mismatch.' }
            foreach ($rel in $existingInventory.Keys) {
                if ($restoreInventory[$rel] -ne $existingInventory[$rel]) { throw "Restore point hash mismatch: $rel" }
            }
            $originalInventory = [ordered]@{}
            foreach ($rel in @($restoreInventory.Keys | Sort-Object)) {
                $originalInventory[$rel] = $restoreInventory[$rel]
            }
            $previousPlatform = [ordered]@{
                existed       = $true
                version       = $existingVersion
                file_count    = $existingInventory.Count
                restore_point = $createdRestorePoint
                inventory     = $originalInventory
            }
            Info 'Previous MahodAI preserved as a managed restore point:'
            Info "  $createdRestorePoint"
        } else {
            $previousPlatform = [ordered]@{
                existed=$false; version=$null; file_count=0; restore_point=$null; inventory=[ordered]@{}
            }
        }
    }

    # Prepare a complete non-.bundle sibling, preserving foreign files from the
    # existing platform and overlaying only this package's payload.
    if ($hadExisting) {
        Copy-Item -LiteralPath $bundle -Destination $candidateBundle -Recurse -ErrorAction Stop
    } else {
        New-Item -ItemType Directory -Force -Path $candidateBundle | Out-Null
    }
    $payloadFiles = @(Get-ChildItem $payloadBundle -Recurse -File)
    $payloadRelSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($src in $payloadFiles) {
        [void]$payloadRelSet.Add($src.FullName.Substring($payloadBundle.Length + 1))
    }

    # Files owned by an earlier package but intentionally removed from this payload
    # must not survive forever. Delete from the candidate only when the live byte hash
    # still equals the earlier ownership manifest; a locally modified byte is foreign
    # work and is preserved/reported instead.
    if ($priorState -and $priorState.owned_files) {
        foreach ($property in $priorState.owned_files.PSObject.Properties) {
            $rel = [string]$property.Name
            if ($payloadRelSet.Contains($rel)) { continue }
            $candidateOld = BundleRelativePath $candidateBundle $rel
            if (-not (Test-Path $candidateOld -PathType Leaf)) { continue }
            $priorHash = ([string]$property.Value.sha256).ToLowerInvariant()
            if ($priorHash -match '^[0-9a-f]{64}$' -and (FileSha $candidateOld) -eq $priorHash) {
                Remove-Item -LiteralPath $candidateOld -Force -ErrorAction Stop
                $retiredStaleOwned += $rel
            } else {
                $preservedModifiedOwned += $rel
            }
        }
    }

    foreach ($src in $payloadFiles) {
        $rel = $src.FullName.Substring($payloadBundle.Length + 1)
        $dst = Join-Path $candidateBundle $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
        Copy-Item -LiteralPath $src.FullName -Destination $dst -Force -ErrorAction Stop
        $actual = FileSha $dst
        $expected = FileSha $src.FullName
        if ($actual -ne $expected) { throw "Candidate payload hash mismatch: $rel" }
        $written[$rel] = @{
            sha256             = $actual
            overwrote_existing = $existingInventory.ContainsKey($rel)
            previous_sha256    = if ($existingInventory.ContainsKey($rel)) { $existingInventory[$rel] } else { $null }
        }
    }
    if (-not (Test-Path (Join-Path $candidateBundle 'PackageContents.xml') -PathType Leaf)) {
        throw 'PackageContents.xml missing from prepared candidate.'
    }
    $foreign = @($existingInventory.Keys | Where-Object {
        -not $written.ContainsKey($_) -and $retiredStaleOwned -notcontains $_
    })
    foreach ($rel in $foreign) {
        $candidateForeign = Join-Path $candidateBundle $rel
        if (-not (Test-Path $candidateForeign -PathType Leaf) -or (FileSha $candidateForeign) -ne $existingInventory[$rel]) {
            throw "Foreign file was not preserved in candidate: $rel"
        }
    }
    if ($TestFailureAt -eq 'AfterCandidatePrepared') { throw 'TEST_FAILURE: AfterCandidatePrepared' }

    # Same-volume rename switch.  If the second rename fails, catch restores the
    # exact original directory from $swapBackup.
    if ($hadExisting) {
        Move-Item -LiteralPath $bundle -Destination $swapBackup -ErrorAction Stop
        $oldMovedAside = $true
    }
    Move-Item -LiteralPath $candidateBundle -Destination $bundle -ErrorAction Stop
    $bundleSwapped = $true
    if ($TestFailureAt -eq 'AfterBundleSwap') { throw 'TEST_FAILURE: AfterBundleSwap' }

    if (-not $BundleRootOverride -or $PluginRootsOverride) {
        $winner = @(Get-ExistingBundles) | Select-Object -First 1
        if ($winner -and $winner.Bundle -ine $bundle) {
            throw "SHADOWING: AutoCAD would load $($winner.Bundle) instead of $bundle"
        }
    }

    # Required profile/evidence files participate in the same rollback boundary.
    $profileTarget = Join-Path $stateRoot 'profiles\6422'
    New-Item -ItemType Directory -Force -Path $profileTarget | Out-Null
    $existingProfile = Join-Path $profileTarget 'project-profile.yaml'
    if (Test-Path $existingProfile) {
        Info 'Existing project profile kept (approved CL layers / mappings preserved).'
    } else {
        Backup-StateFile $existingProfile
        $newestBak = Get-ChildItem $profileTarget -Filter 'project-profile.yaml.bak-*' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($newestBak) {
            Copy-Item -LiteralPath $newestBak.FullName -Destination $existingProfile -ErrorAction Stop
            Info "Project profile was missing; RESTORED the engineer's newest backup: $($newestBak.Name)"
        } else {
            Copy-Item -LiteralPath (Join-Path $PayloadDir 'profiles\6422\project-profile.yaml') -Destination $existingProfile -ErrorAction Stop
            Info 'Project profile 6422 installed (first install).'
        }
    }
    foreach ($ref in @('nti-urban-082025.xlsx', 'judgment2-golden.xlsx')) {
        $sourceRef = Join-Path $PayloadDir "profiles\6422\$ref"
        if (Test-Path $sourceRef -PathType Leaf) {
            $targetRef = Join-Path $profileTarget $ref
            Backup-StateFile $targetRef
            Copy-Item -LiteralPath $sourceRef -Destination $targetRef -Force -ErrorAction Stop
        }
    }
    $channelSeed = Join-Path $PayloadDir 'update-channel.json'
    if (Test-Path $channelSeed -PathType Leaf) {
        $channelTarget = Join-Path $stateRoot 'update-channel.json'
        if (Test-Path $channelTarget) {
            Info "Update channel already configured on this machine (kept): $channelTarget"
        } else {
            Backup-StateFile $channelTarget
            Copy-Item -LiteralPath $channelSeed -Destination $channelTarget -ErrorAction Stop
            Info "Update channel configured: $channelTarget"
        }
    }
    if ($TestFailureAt -eq 'AfterProfileWrite') { throw 'TEST_FAILURE: AfterProfileWrite' }

    $state = [ordered]@{
        schema_version          = 4
        package_revision        = $packageRevision
        installed_utc           = (Get-Date).ToUniversalTime().ToString('o')
        installed_by            = $effectiveUserName
        installed_user_sid      = $effectiveUserSid
        machine                 = $env:COMPUTERNAME
        machine_wide            = $machineWideInstall
        platform_version        = $incomingVersion
        previous_platform       = $previousPlatform
        bundle_path             = $bundle
        state_root              = $stateRoot
        uninstall_registry_path = $regPath
        profile_path            = $profileTarget
        owned_files             = $written
        preserved_foreign_files = $foreign
        retired_stale_owned_files = $retiredStaleOwned
        preserved_modified_owned_files = $preservedModifiedOwned
        retired_duplicate_bundles = @(
            @($priorRetiredDuplicates) + @($disabled | ForEach-Object {
                [ordered]@{
                    original_path = $_.From
                    retired_path  = $_.To
                    version       = $_.Version
                    file_count    = $_.FileCount
                    inventory     = $_.Inventory
                }
            })
        )
        civil_2027_present      = $civil2027
        civil_2026_present      = $civil2026
        payload_2026_included   = $has2026Payload
    }
    Backup-StateFile $stateFile
    New-Item -ItemType Directory -Force -Path $transactionStateDir | Out-Null
    $stateCandidate = Join-Path $transactionStateDir 'install_state.candidate.json'
    $state | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $stateCandidate -Encoding utf8 -ErrorAction Stop
    Move-Item -LiteralPath $stateCandidate -Destination $stateFile -Force -ErrorAction Stop
    Info "Install state recorded: $stateFile"
    if ($TestFailureAt -eq 'AfterStateWrite') { throw 'TEST_FAILURE: AfterStateWrite' }

    # Registry publication is last and is rolled back with the rest of the transaction.
    if ($publishRegistry) {
        if (-not (Test-Path $uninstallerTarget -PathType Leaf)) {
            throw "Protected uninstaller is missing from the deployed bundle: $uninstallerTarget"
        }
        $systemPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        if (-not (Test-Path $systemPowerShell -PathType Leaf)) {
            throw "Protected Windows PowerShell executable is missing: $systemPowerShell"
        }
        $registryExisted = Test-Path $regPath
        if ($registryExisted) {
            $oldReg = Get-ItemProperty -Path $regPath
            foreach ($name in $registryNames) {
                if ($oldReg.PSObject.Properties.Name -contains $name) { $registryBackup[$name] = $oldReg.$name }
            }
        }
        New-Item -Path $regPath -Force | Out-Null
        Set-ItemProperty -Path $regPath -Name DisplayName     -Value 'Mahod Civil Delivery (MahodAI Civil 3D platform)'
        Set-ItemProperty -Path $regPath -Name DisplayVersion  -Value $packageRevision
        Set-ItemProperty -Path $regPath -Name Publisher       -Value 'Mahod Engineering'
        Set-ItemProperty -Path $regPath -Name InstallLocation -Value $bundle
        New-ItemProperty -Path $regPath -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $regPath -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
        Set-ItemProperty -Path $regPath -Name UninstallString -Value `
            ('"' + $systemPowerShell + '" -NoProfile -ExecutionPolicy Bypass -File "' +
             $uninstallerTarget + '" -StateRoot "' + $stateRoot + '" -OriginalUserSid "' +
             $effectiveUserSid + '"')
    }

    $transactionCommitted = $true
}
catch {
    $failure = $_.Exception.Message

    # State/profile rollback first, while the transaction backups still exist.
    Restore-StateFiles
    if ($publishRegistry) {
        try {
            if (-not $registryExisted) {
                Remove-Item -LiteralPath $regPath -Recurse -Force -ErrorAction SilentlyContinue
            } elseif (Test-Path $regPath) {
                foreach ($name in $registryNames) {
                    if ($registryBackup.ContainsKey($name)) {
                        Set-ItemProperty -Path $regPath -Name $name -Value $registryBackup[$name] -ErrorAction SilentlyContinue
                    } else {
                        Remove-ItemProperty -Path $regPath -Name $name -ErrorAction SilentlyContinue
                    }
                }
            }
        } catch { }
    }

    # Restore the old bundle directory exactly; the candidate never shared files
    # with it, so rollback cannot leave a mixed version.
    if ($bundleSwapped -and (Test-Path $bundle)) {
        Remove-Item -LiteralPath $bundle -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($oldMovedAside -and (Test-Path $swapBackup)) {
        Move-Item -LiteralPath $swapBackup -Destination $bundle -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $candidateBundle -Recurse -Force -ErrorAction SilentlyContinue

    $bundleRollbackVerified = $true
    if ($hadExisting) {
        $rollbackInventory = Get-Inventory $bundle
        if ($rollbackInventory.Count -ne $existingInventory.Count) { $bundleRollbackVerified = $false }
        foreach ($rel in $existingInventory.Keys) {
            if ($rollbackInventory[$rel] -ne $existingInventory[$rel]) { $bundleRollbackVerified = $false }
        }
    } elseif (Test-Path $bundle) {
        $bundleRollbackVerified = $false
    }
    $stateRollbackVerified = $true
    foreach ($entry in $stateBackups.GetEnumerator()) {
        if ($entry.Value) {
            if (-not (Test-Path $entry.Key -PathType Leaf) -or
                (FileSha $entry.Key) -ne (FileSha ([string]$entry.Value))) { $stateRollbackVerified = $false }
        } elseif (Test-Path $entry.Key) {
            $stateRollbackVerified = $false
        }
    }

    $duplicatesRollbackVerified = $true
    for ($disabledIndex = $disabled.Count - 1; $disabledIndex -ge 0; $disabledIndex--) {
        $d = $disabled[$disabledIndex]
        if (Test-Path $d.To) {
            Move-Item -LiteralPath $d.To -Destination $d.From -ErrorAction SilentlyContinue
        }
        if (-not (Test-Path -LiteralPath $d.From -PathType Container) -or (Test-Path -LiteralPath $d.To)) {
            $duplicatesRollbackVerified = $false
            continue
        }
        $restoredDuplicateInventory = Get-Inventory $d.From
        if ($restoredDuplicateInventory.Count -ne $d.FileCount) { $duplicatesRollbackVerified = $false }
        foreach ($relative in $d.Inventory.Keys) {
            if ($restoredDuplicateInventory[$relative] -ne $d.Inventory[$relative]) {
                $duplicatesRollbackVerified = $false
            }
        }
    }
    if ($bundleRollbackVerified -and $createdRestorePoint -and (Test-Path $createdRestorePoint)) {
        Remove-Item -LiteralPath $createdRestorePoint -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $transactionStateDir -Recurse -Force -ErrorAction SilentlyContinue
    if (-not $stateRootExisted -and (Test-Path $stateRoot)) {
        Remove-Item -LiteralPath $stateRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (-not $bundleRollbackVerified -or -not $stateRollbackVerified -or -not $duplicatesRollbackVerified) {
        $recovery = if (Test-Path $swapBackup) { $swapBackup } else { $createdRestorePoint }
        Fail ("Installation failed and automatic rollback did not verify byte-identical. " +
              "Recovery evidence was retained at '$recovery'. Original error: $failure") 10
    }
    if ($failure -like 'SHADOWING:*') {
        if ($couldNotDisable.Count -gt 0) {
            Info 'WARNING: a second MahodAI exists and could not be renamed (needs Administrator):'
            foreach ($other in $couldNotDisable) { Info "  $($other.Bundle)  (platform $($other.Version))" }
        }
        Fail ("Install wrote nothing because AutoCAD will load a different MahodAI first. $failure") 8
    }
    Fail ("Installation transaction failed and the previous bundle/state were restored. $failure") 9
}

# Commit cleanup: the durable restore point is separate; this same-root rollback
# directory only protected the in-flight rename transaction.
Remove-Item -LiteralPath $swapBackup -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $candidateBundle -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $transactionStateDir -Recurse -Force -ErrorAction SilentlyContinue
Info "Platform deployed atomically: $bundle  ($($written.Count) files written)"
if ($foreign.Count -gt 0) {
    Info "Preserved $($foreign.Count) pre-existing file(s) not part of this package (left untouched)."
}

# Legacy stray backups from the previous installer design are reported, not trusted.
$strays = @(Get-ChildItem $pluginsRoot -Directory -Filter 'MahodAI.bundle.backup-*' -ErrorAction SilentlyContinue)
if ($strays.Count -gt 0) {
    Info ''
    Info "NOTE: $($strays.Count) stray backup folder(s) from an older installer exist under:"
    Info "  $pluginsRoot"
    Info '  They are no longer used for recovery and can be deleted once you are satisfied.'
}

Info ''
Info '=== Mahod Civil Delivery installed ==='
Info "MahodAI platform version : $incomingVersion"
Info "Installed to             : $bundle"
Info "Package revision         : $packageRevision"
if ([bool]$previousPlatform.existed) { Info 'Previous MahodAI is restorable via uninstall (managed restore point).' }
Info 'Open Civil 3D: ribbon MahodAI > Civil Delivery (or type MHD_CIVIL_DELIVERY).'
$supportedYears = @(2027)
if ($has2026Payload) { $supportedYears += 2026 }
$unsupported = @($civilYears | Where-Object { $supportedYears -notcontains $_ })
if ($civil2026 -and -not $has2026Payload) {
    Info ''
    Info 'NOTE: Civil 3D 2026 detected but this package ships the 2027 component only.'
}
# What the engineer is told is what actually happened on HER machine - detected hosts,
# what was already installed, what was retired, where this went.
$hosts = @()
if ($civil2027) { $hosts += 'Civil 3D 2027' }
if ($civil2026) { $hosts += 'Civil 3D 2026' }

$report = @()
$report += "הותקנה: Mahod Civil Delivery $packageRevision (פלטפורמה $incomingVersion)"
$report += 'ההתקנה הושלמה בהצלחה.'
$report += ''
$report += ('נמצא במחשב: ' + $(if ($hosts.Count) { $hosts -join ', ' } else { 'לא נמצא Civil 3D' }))
if ($hadExisting) {
    $report += ('הוחלפה התקנה קודמת: ' +
        $(if ($priorPackageRevision) { "Mahod Civil Delivery $priorPackageRevision" } else { 'חבילה לא מזוהה' }) +
        ' (' + $(if ($existingVersion) { "פלטפורמה $existingVersion" } else { 'גרסה לא מזוהה' }) + ')')
    $report += "הגרסה שנטענת עכשיו: Mahod Civil Delivery $packageRevision. ניתן לשחזר את הקודמת דרך ההסרה."
} else {
    $report += 'לא נמצאה התקנה קודמת - זו התקנה ראשונה.'
}
if ($disabled.Count -gt 0) {
    $report += ''
    $report += ("הושבתו $($disabled.Count) עותקים כפולים שהיו עלולים להיטען במקום החדש:")
    foreach ($d in $disabled) { $report += ('  ' + $d.From) }
    $report += '(הם שונו בשם ולא נמחקו.)'
}
if ($couldNotDisable.Count -gt 0) {
    $report += ''
    $report += 'שים לב: קיים עותק נוסף של MahodAI שלא ניתן היה להשבית ללא הרשאת מנהל:'
    foreach ($d in $couldNotDisable) { $report += ('  ' + $d.Bundle) }
    $report += 'אם הכלי ייראה כמו הגרסה הקודמת - יש להריץ את ההתקנה כמנהל.'
}
$report += ''
$report += "Mahod Civil Delivery $packageRevision (פלטפורמה $incomingVersion)"
$report += ('הותקן אל: ' + $bundle)
$report += 'ההגדרות והמיפויים הקיימים נשמרו.'
if ($unsupported.Count -gt 0) {
    $report += ''
    $report += ('שים לב: במחשב זוהו גם גרסאות שהחבילה הזו אינה תומכת בהן: ' +
                (($unsupported | ForEach-Object { "Civil 3D $_" }) -join ', ') + '.')
    $report += 'פתיחה שלהן לא תציג את לשונית MahodAI, והפקודה תחזיר Unknown command.'
    $report += ('הכלי נטען ב: ' + (($supportedYears | Sort-Object | ForEach-Object { "Civil 3D $_" }) -join ', ') + '.')
}
# The package carries the 2027 component only. On a machine that also has 2026, opening
# THAT one shows no ribbon and an unknown command - the exact way this looked like a failed
# install at an engineer's desk (2026-08-26). Say it before it happens.
if ($civil2026 -and -not $has2026Payload) {
    $report += ''
    $report += 'חשוב: הכלי נטען רק ב-Civil 3D 2027.'
    $report += 'במחשב הזה זוהה גם Civil 3D 2026 - פתיחה שלו לא תציג את לשונית MahodAI,'
    $report += 'והפקודה תחזיר Unknown command. זו לא תקלה בהתקנה.'
}
$report += ''
$report += 'לפתוח Civil 3D: ריבון MahodAI ← Civil Delivery.'
$report += 'לבדיקה שהגרסה החדשה נטענה: בלשונית "חתכים" יש כפתור «בחר קובץ CL…».'

Show-HeDialog ($report -join "`n") 'Mahod Civil Delivery' 'Information'
exit 0
