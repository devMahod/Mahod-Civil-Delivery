<#
.SYNOPSIS
    Transactionally removes Mahod Civil Delivery without damaging MahodAI.

.DESCRIPTION
    The install state is the authority for the exact bundle that was installed.
    Uninstall never guesses a per-user ApplicationPlugins path. If a platform was
    present before Civil Delivery, its sealed restore inventory is rebuilt. If this
    was a first install, only unchanged package-owned files are removed. Files added
    or changed after installation are retained in both cases.

    A complete sibling candidate is prepared and verified before the live bundle is
    renamed. Any failure before commit puts the old bundle, install state, registry
    entry, and restore point back byte-for-byte.

    ASCII-only file. Windows PowerShell 5.1 compatible.
#>
[CmdletBinding()]
param(
    [switch]$PurgeData,
    [switch]$Quiet,
    # Production ARP context captured by the installer before any UAC boundary.
    [string]$StateRoot,
    [string]$OriginalUserSid,
    # Internal flag added only by this protected script when it relaunches itself.
    [switch]$ElevatedUninstall,
    # Test-only overrides. BundleRootOverride does not choose the bundle; it pins
    # the parent that install_state.bundle_path must name.
    [string]$BundleRootOverride,
    [string]$StateRootOverride,
    [switch]$SkipHostCheck,
    [switch]$SkipRegistry,
    [switch]$SelfTestElevationPlan,
    [ValidateSet('', 'AfterCandidatePrepared', 'AfterBundleMoved', 'AfterBundleSwap', 'AfterStateMoved', 'AfterRegistryRemoved')]
    [string]$TestFailureAt = ''
)

$ErrorActionPreference = 'Stop'

function Info([string]$message) { if (-not $Quiet) { Write-Host $message } }

function Fail([string]$message, [int]$code) {
    $host.UI.WriteErrorLine($message)
    exit $code
}

function FileSha([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function FullPath([string]$path, [string]$label) {
    if ([string]::IsNullOrWhiteSpace($path) -or -not [System.IO.Path]::IsPathRooted($path)) {
        throw "$label must be an absolute path."
    }
    if ($path -match '["\r\n]') { throw "$label contains invalid characters." }
    $full = [System.IO.Path]::GetFullPath($path).TrimEnd('\')
    $root = [System.IO.Path]::GetPathRoot($full).TrimEnd('\')
    if ($full -eq $root) { throw "$label must not be a drive root." }
    return $full
}

function CanonicalSid([string]$sid, [string]$label) {
    if ([string]::IsNullOrWhiteSpace($sid) -or $sid -match '["\r\n]') { throw "$label is missing or invalid." }
    try { $value = (New-Object System.Security.Principal.SecurityIdentifier($sid)).Value }
    catch { throw "$label is invalid: $($_.Exception.Message)" }
    if ($value -cne $sid) { throw "$label is not canonical." }
    return $value
}

function IsUnder([string]$child, [string]$parent) {
    $prefix = $parent.TrimEnd('\') + '\'
    return $child.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function SafeRelative([string]$relative, [string]$label) {
    if ([string]::IsNullOrWhiteSpace($relative)) { throw "$label contains an empty relative path." }
    $rel = $relative.Replace('/', '\')
    if ([System.IO.Path]::IsPathRooted($rel) -or $rel.Contains(':') -or
        $rel.StartsWith('\') -or $rel.EndsWith('\') -or
        $rel -match '(^|\\)\.\.?($|\\)') {
        throw "$label contains an unsafe relative path: $relative"
    }
    return $rel
}

function Assert-NoReparsePoints([string]$root, [string]$label) {
    $items = @()
    if (Test-Path -LiteralPath $root) {
        $items += Get-Item -LiteralPath $root -Force
        $items += Get-ChildItem -LiteralPath $root -Recurse -Force
    }
    foreach ($item in $items) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$label contains a reparse point: $($item.FullName)"
        }
    }
}

function GetInventory([string]$root) {
    $map = @{}
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { return $map }
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
        $rel = SafeRelative ($file.FullName.Substring($root.Length).TrimStart('\')) 'bundle inventory'
        if ($map.ContainsKey($rel)) { throw "Duplicate inventory path: $rel" }
        $map[$rel] = FileSha $file.FullName
    }
    return $map
}

function SameInventory([hashtable]$expected, [hashtable]$actual) {
    if ($expected.Count -ne $actual.Count) { return $false }
    foreach ($rel in $expected.Keys) {
        if (-not $actual.ContainsKey($rel) -or $actual[$rel] -ne $expected[$rel]) { return $false }
    }
    return $true
}

function ReadHashManifest($jsonObject, [string]$label) {
    if ($null -eq $jsonObject) { throw "$label is missing." }
    $map = @{}
    foreach ($property in $jsonObject.PSObject.Properties) {
        $rel = SafeRelative ([string]$property.Name) $label
        $sha = ([string]$property.Value).ToLowerInvariant()
        if ($sha -notmatch '^[0-9a-f]{64}$') { throw "$label has an invalid SHA256 for $rel." }
        if ($map.ContainsKey($rel)) { throw "$label has a duplicate path: $rel" }
        $map[$rel] = $sha
    }
    return $map
}

function ReadOwnedManifest($jsonObject) {
    if ($null -eq $jsonObject) { throw 'owned_files is missing.' }
    $map = @{}
    foreach ($property in $jsonObject.PSObject.Properties) {
        $rel = SafeRelative ([string]$property.Name) 'owned_files'
        $sha = ([string]$property.Value.sha256).ToLowerInvariant()
        if ($sha -notmatch '^[0-9a-f]{64}$') { throw "owned_files has an invalid SHA256 for $rel." }
        if ($map.ContainsKey($rel)) { throw "owned_files has a duplicate path: $rel" }
        $map[$rel] = $sha
    }
    if ($map.Count -eq 0) { throw 'owned_files is empty.' }
    return $map
}

function CopyRelativeFile([string]$sourceRoot, [string]$targetRoot, [string]$relative, [string]$expectedSha) {
    $source = Join-Path $sourceRoot $relative
    $target = Join-Path $targetRoot $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Source file is missing: $source" }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Force -ErrorAction Stop
    if ((FileSha $target) -ne $expectedSha) { throw "Copied file failed SHA256 verification: $relative" }
}

function ValidateRegistryPath([string]$path) {
    $suffix = '\Software\Microsoft\Windows\CurrentVersion\Uninstall\MahodCivilDelivery'
    if ($path -ieq ('HKCU:' + $suffix)) { return $path }
    if ($path -match '^Registry::HKEY_USERS\\S-1-[0-9-]+\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\MahodCivilDelivery$') {
        return $path
    }
    throw "install_state contains an unsafe uninstall_registry_path: $path"
}

function GetRegistrySnapshot([string]$path) {
    $snapshot = [ordered]@{ Exists = $false; Values = @() }
    if (-not (Test-Path -LiteralPath $path)) { return $snapshot }
    $snapshot.Exists = $true
    $key = Get-Item -LiteralPath $path
    foreach ($name in $key.GetValueNames()) {
        $snapshot.Values += [PSCustomObject]@{
            Name  = $name
            Kind  = $key.GetValueKind($name)
            Value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        }
    }
    return $snapshot
}

function RestoreRegistry([string]$path, $snapshot) {
    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
    if (-not $snapshot.Exists) { return }
    New-Item -Path $path -Force | Out-Null
    $key = Get-Item -LiteralPath $path
    foreach ($entry in $snapshot.Values) { $key.SetValue($entry.Name, $entry.Value, $entry.Kind) }
}

function RegistryFingerprint([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '<absent>' }
    $key = Get-Item -LiteralPath $path
    $parts = @()
    foreach ($name in @($key.GetValueNames() | Sort-Object)) {
        $value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ($value -is [byte[]]) { $encoded = [Convert]::ToBase64String($value) }
        elseif ($value -is [string[]]) { $encoded = ($value | ConvertTo-Json -Compress) }
        else { $encoded = [string]$value }
        $parts += ($name + '|' + [string]$key.GetValueKind($name) + '|' + $encoded)
    }
    return ($parts -join "`n")
}

function IsElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function QuoteProcessArgument([string]$value) {
    if ($value -match '["\r\n]') { throw 'Unsafe process argument.' }
    return '"' + $value + '"'
}

function AssertProtectedForElevation([string[]]$paths, [string]$originalSid) {
    $broadSids = @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545', $originalSid)
    $writeMask = [System.Security.AccessControl.FileSystemRights]::Write -bor
        [System.Security.AccessControl.FileSystemRights]::Modify -bor
        [System.Security.AccessControl.FileSystemRights]::FullControl -bor
        [System.Security.AccessControl.FileSystemRights]::Delete -bor
        [System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [System.Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [System.Security.AccessControl.FileSystemRights]::TakeOwnership
    foreach ($path in $paths) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Protected elevation path is missing: $path" }
        $acl = Get-Acl -LiteralPath $path
        foreach ($rule in $acl.Access) {
            if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
            try { $sid = $rule.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value }
            catch { throw "Could not validate ACL identity on protected elevation path: $path" }
            if ($broadSids -contains $sid -and (($rule.FileSystemRights -band $writeMask) -ne 0)) {
                throw "Machine-wide self-elevation refused because a standard user can modify: $path"
            }
        }
    }
}

if ($StateRoot -and $StateRootOverride) { Fail 'StateRoot and StateRootOverride are mutually exclusive.' 4 }
if ($TestFailureAt -and (-not $BundleRootOverride -or -not $StateRootOverride)) {
    Fail 'TestFailureAt is accepted only with both sandbox path overrides.' 4
}
if (($SkipRegistry -or $SelfTestElevationPlan) -and -not $StateRootOverride) {
    Fail 'SkipRegistry and SelfTestElevationPlan are test-only and require StateRootOverride.' 4
}
if ($ElevatedUninstall -and $StateRootOverride) { Fail 'ElevatedUninstall cannot be used with test overrides.' 4 }

$stateRootInput = if ($StateRootOverride) { $StateRootOverride } elseif ($StateRoot) { $StateRoot } else { Join-Path $env:LOCALAPPDATA 'MahodAI_Civil3D\civil-delivery' }
try { $stateRootResolved = FullPath $stateRootInput 'state root' }
catch { Fail $_.Exception.Message 4 }
$stateFile = Join-Path $stateRootResolved 'install_state.json'

$acad = if ($SkipHostCheck) { $null } else { Get-Process acad -ErrorAction SilentlyContinue }
if ($acad) { Fail 'Close AutoCAD/Civil 3D before uninstalling. Nothing was changed.' 2 }
if (-not (Test-Path -LiteralPath $stateFile -PathType Leaf)) {
    Fail ("No Mahod Civil Delivery install state found at $stateFile. " +
          'Refusing to guess which bundle belongs to this product. Nothing was changed.') 3
}

try {
    $stateSha = FileSha $stateFile
    $state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
    $stateSchema = 0
    if (-not [int]::TryParse([string]$state.schema_version, [ref]$stateSchema) -or
        $stateSchema -lt 2 -or $stateSchema -gt 4) {
        throw "Unsupported install_state schema: $($state.schema_version)"
    }
    if (-not $state.previous_platform) { throw 'previous_platform is missing.' }

    if ($state.state_root) {
        $recordedStateRoot = FullPath ([string]$state.state_root) 'install_state.state_root'
        if ($recordedStateRoot -ine $stateRootResolved) { throw 'StateRoot does not match install_state.state_root.' }
    }

    $stateSid = $null
    if ($state.installed_user_sid) { $stateSid = CanonicalSid ([string]$state.installed_user_sid) 'install_state.installed_user_sid' }
    if ($OriginalUserSid) {
        $argumentSid = CanonicalSid $OriginalUserSid 'OriginalUserSid'
        if ($stateSid -and $argumentSid -cne $stateSid) { throw 'OriginalUserSid does not match install_state.' }
        $stateSid = $argumentSid
    }

    # The state path is authoritative. This fixes machine-wide installs, where the
    # live target is under Program Files rather than the current user APPDATA.
    $bundle = FullPath ([string]$state.bundle_path) 'install_state.bundle_path'
    if ((Split-Path -Leaf $bundle) -ine 'MahodAI.bundle') {
        throw 'install_state.bundle_path must name MahodAI.bundle.'
    }
    $pluginsRoot = Split-Path -Parent $bundle
    if ($BundleRootOverride) {
        $pinnedRoot = FullPath $BundleRootOverride 'BundleRootOverride'
        $pinnedBundle = [System.IO.Path]::GetFullPath((Join-Path $pinnedRoot 'MahodAI.bundle')).TrimEnd('\')
        if ($bundle -ine $pinnedBundle) { throw 'install_state.bundle_path does not match BundleRootOverride.' }
    }
    if (-not (Test-Path -LiteralPath $bundle -PathType Container)) { throw "Installed bundle is missing: $bundle" }
    Assert-NoReparsePoints $bundle 'installed bundle'

    $owned = ReadOwnedManifest $state.owned_files
    $oldInventory = GetInventory $bundle
    $prev = $state.previous_platform
    $previousExisted = [bool]$prev.existed
    $restorePoint = $null
    $restoreExpected = @{}

    if ($previousExisted) {
        $restorePoint = FullPath ([string]$prev.restore_point) 'previous_platform.restore_point'
        $allowedRestoreRoot = [System.IO.Path]::GetFullPath((Join-Path $stateRootResolved 'restore-points')).TrimEnd('\')
        if (-not (IsUnder $restorePoint $allowedRestoreRoot)) {
            throw 'Restore point is outside the managed state restore-points directory.'
        }
        if (-not (Test-Path -LiteralPath $restorePoint -PathType Container)) { throw "Restore point is missing: $restorePoint" }
        Assert-NoReparsePoints $restorePoint 'restore point'
        $restoreExpected = ReadHashManifest $prev.inventory 'previous_platform.inventory'
        if ([int]$prev.file_count -ne $restoreExpected.Count) {
            throw 'Restore point manifest count does not match previous_platform.file_count.'
        }
        $restoreActual = GetInventory $restorePoint
        if (-not (SameInventory $restoreExpected $restoreActual)) {
            throw 'Restore point file set or SHA256 inventory does not match install_state.'
        }
    } else {
        if ($prev.restore_point) { throw 'A first-install state must not name a restore point.' }
        if ([int]$prev.file_count -ne 0) { throw 'A first-install state must have previous_platform.file_count = 0.' }
    }

    $machineWide = [bool]$state.machine_wide
    foreach ($machineBase in @($env:ProgramFiles, $env:ProgramData, ${env:ProgramFiles(x86)})) {
        if (-not [string]::IsNullOrWhiteSpace($machineBase)) {
            $machineBaseFull = [System.IO.Path]::GetFullPath($machineBase).TrimEnd('\')
            if (IsUnder $bundle $machineBaseFull) { $machineWide = $true }
        }
    }
    if ($machineWide -and -not $stateSid) { throw 'Machine-wide uninstall requires installed_user_sid.' }
    $regPath = if ($state.uninstall_registry_path) {
        ValidateRegistryPath ([string]$state.uninstall_registry_path)
    } elseif ($stateSid) {
        "Registry::HKEY_USERS\$stateSid\Software\Microsoft\Windows\CurrentVersion\Uninstall\MahodCivilDelivery"
    } else {
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MahodCivilDelivery'
    }
    if ($machineWide -and -not $BundleRootOverride -and $regPath -notlike "Registry::HKEY_USERS\$stateSid\*") {
        throw 'Machine-wide uninstall registry path does not belong to installed_user_sid.'
    }

    # Every duplicate retired during install is a durable, hash-sealed lifecycle
    # asset.  Validate all receipts before any live mutation; uninstall later restores
    # the original directory names transactionally.
    $retiredDuplicates = @()
    foreach ($receipt in @($state.retired_duplicate_bundles)) {
        if ($null -eq $receipt) { continue }
        $original = FullPath ([string]$receipt.original_path) 'retired duplicate original_path'
        $retired = FullPath ([string]$receipt.retired_path) 'retired duplicate retired_path'
        if ((Split-Path -Leaf $original) -ine 'MahodAI.bundle') {
            throw 'Retired duplicate original_path must name MahodAI.bundle.'
        }
        if ((Split-Path -Parent $original) -ine (Split-Path -Parent $retired) -or
            (Split-Path -Leaf $retired) -notmatch '^MahodAI\.bundle\.disabled-[0-9a-f]{32}$') {
            throw 'Retired duplicate paths are not a same-root managed rename pair.'
        }
        if ($original -ieq $bundle) { throw 'Retired duplicate receipt aliases the primary installed bundle.' }
        if (Test-Path -LiteralPath $original) {
            throw "Cannot restore retired duplicate because its original path is occupied: $original"
        }
        if (-not (Test-Path -LiteralPath $retired -PathType Container)) {
            throw "Retired duplicate is missing: $retired"
        }
        if (-not $BundleRootOverride) {
            $allowedRoots = @()
            if ($env:ProgramFiles) { $allowedRoots += Join-Path $env:ProgramFiles 'Autodesk\ApplicationPlugins' }
            if ($env:ProgramData) { $allowedRoots += Join-Path $env:ProgramData 'Autodesk\ApplicationPlugins' }
            if ($env:APPDATA) { $allowedRoots += Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins' }
            if ($stateSid) {
                try {
                    $profileKey = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$stateSid"
                    $profileRaw = (Get-ItemProperty -LiteralPath $profileKey -Name ProfileImagePath -ErrorAction Stop).ProfileImagePath
                    $profile = [Environment]::ExpandEnvironmentVariables([string]$profileRaw)
                    $allowedRoots += Join-Path $profile 'AppData\Roaming\Autodesk\ApplicationPlugins'
                } catch { }
            }
            $allowed = @($allowedRoots | Where-Object {
                try { (FullPath $_ 'allowed plugin root') -ieq (Split-Path -Parent $original) } catch { $false }
            }).Count -gt 0
            if (-not $allowed) { throw "Retired duplicate is outside supported Autodesk ApplicationPlugins roots: $original" }
        }
        Assert-NoReparsePoints $retired 'retired duplicate'
        $sealed = ReadHashManifest $receipt.inventory 'retired_duplicate_bundles.inventory'
        if ([int]$receipt.file_count -ne $sealed.Count) { throw 'Retired duplicate file_count does not match inventory.' }
        if (-not (SameInventory $sealed (GetInventory $retired))) {
            throw "Retired duplicate failed its sealed inventory: $retired"
        }
        $retiredDuplicates += [pscustomobject]@{ Original=$original; Retired=$retired; Inventory=$sealed }
    }
} catch {
    Fail ("Install state / restore preflight failed. Nothing was changed. $($_.Exception.Message)") 4
}

# Machine-wide removal is elevated before any staging or mutation. The script being
# elevated must itself be inside the protected installed bundle, never LOCALAPPDATA.
$scriptPath = FullPath $MyInvocation.MyCommand.Path 'uninstaller script path'
$protectedScript = [System.IO.Path]::GetFullPath((Join-Path $bundle 'Installer\Uninstall-MahodCivilDelivery.ps1')).TrimEnd('\')
if ($machineWide -and -not $BundleRootOverride) {
    if ($scriptPath -ine $protectedScript) {
        Fail 'Machine-wide elevation refused: uninstaller is not the protected bundle copy. Nothing was changed.' 7
    }
    try {
        AssertProtectedForElevation @($pluginsRoot, $bundle, (Split-Path -Parent $protectedScript), $protectedScript) $stateSid
    } catch { Fail ("Machine-wide elevation safety check failed. Nothing was changed. $($_.Exception.Message)") 7 }
}
if ($SelfTestElevationPlan) {
    if (-not $machineWide) { Fail 'SelfTestElevationPlan expected a machine-wide state.' 4 }
    try {
        $systemPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        Write-Output ("ELEVATION_REQUIRED|powershell=$systemPowerShell|script=$protectedScript|state=$stateRootResolved|sid=$stateSid|bundle=$bundle")
        exit 0
    } catch { Fail ("Elevation-plan self-test failed. $($_.Exception.Message)") 4 }
}
if ($machineWide -and -not (IsElevated) -and -not $BundleRootOverride) {
    try {
        $systemPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        if (-not (Test-Path -LiteralPath $systemPowerShell -PathType Leaf)) { throw "System PowerShell not found: $systemPowerShell" }
        $arguments = @(
            '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', (QuoteProcessArgument $scriptPath),
            '-StateRoot', (QuoteProcessArgument $stateRootResolved), '-OriginalUserSid', (QuoteProcessArgument $stateSid),
            '-ElevatedUninstall'
        )
        if ($PurgeData) { $arguments += '-PurgeData' }
        if ($Quiet) { $arguments += '-Quiet' }
        $child = Start-Process -FilePath $systemPowerShell -ArgumentList ($arguments -join ' ') -Verb RunAs -Wait -PassThru -WindowStyle Hidden
        exit $child.ExitCode
    } catch {
        Fail ("Machine-wide uninstall needs Administrator approval; nothing was changed. $($_.Exception.Message)") 7
    }
}
if ($machineWide -and $ElevatedUninstall -and -not (IsElevated)) {
    Fail 'Elevated uninstall child is not running as Administrator. Nothing was changed.' 7
}

Info "Uninstalling package revision : $($state.package_revision)"
Info "Installed platform version    : $($state.platform_version)"
Info "Bundle recorded by installer  : $bundle"

$transactionId = [guid]::NewGuid().ToString('N')
$candidate = Join-Path $pluginsRoot ("MahodAI.uninstall-candidate-$transactionId")
$rollback = Join-Path $pluginsRoot ("MahodAI.uninstall-rollback-$transactionId")
$stateArchive = $stateFile + '.uninstalled-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $transactionId
$stateHold = Join-Path (Split-Path -Parent $stateRootResolved) ((Split-Path -Leaf $stateRootResolved) + ".uninstall-hold-$transactionId")
$expected = @{}
$bundleMoved = $false
$stateMoved = $false
$registryChanged = $false
$duplicatesRestored = @()
$committed = $false
$registrySnapshot = $null
$registryBefore = '<skipped>'
$skipRegistryEffective = $SkipRegistry -or [bool]$BundleRootOverride

try {
    if (Test-Path -LiteralPath $candidate) { throw "Candidate path already exists: $candidate" }
    if (Test-Path -LiteralPath $rollback) { throw "Rollback path already exists: $rollback" }
    New-Item -ItemType Directory -Path $candidate -ErrorAction Stop | Out-Null

    if ($previousExisted) {
        foreach ($rel in $restoreExpected.Keys) {
            CopyRelativeFile $restorePoint $candidate $rel $restoreExpected[$rel]
            $expected[$rel] = $restoreExpected[$rel]
        }
    }

    # Keep every unowned file and every owned file whose bytes changed after install.
    foreach ($rel in $oldInventory.Keys) {
        $keep = -not $owned.ContainsKey($rel)
        if ($owned.ContainsKey($rel) -and $oldInventory[$rel] -ne $owned[$rel]) { $keep = $true }
        if ($keep) {
            CopyRelativeFile $bundle $candidate $rel $oldInventory[$rel]
            $expected[$rel] = $oldInventory[$rel]
        }
    }

    $candidateInventory = GetInventory $candidate
    if (-not (SameInventory $expected $candidateInventory)) {
        throw 'Prepared uninstall candidate failed exact inventory verification.'
    }
    if ($TestFailureAt -eq 'AfterCandidatePrepared') { throw 'TEST_FAILURE: AfterCandidatePrepared' }

    Move-Item -LiteralPath $bundle -Destination $rollback -ErrorAction Stop
    $bundleMoved = $true
    if ($TestFailureAt -eq 'AfterBundleMoved') { throw 'TEST_FAILURE: AfterBundleMoved' }

    Move-Item -LiteralPath $candidate -Destination $bundle -ErrorAction Stop
    if (-not (SameInventory $expected (GetInventory $bundle))) {
        throw 'Live bundle failed exact post-swap inventory verification.'
    }
    if ($TestFailureAt -eq 'AfterBundleSwap') { throw 'TEST_FAILURE: AfterBundleSwap' }

    # A verified empty first-install candidate means the .bundle directory itself can
    # disappear. It remains rollback-protected until metadata commit.
    if (-not $previousExisted -and $expected.Count -eq 0) {
        Remove-Item -LiteralPath $bundle -Recurse -Force -ErrorAction Stop
    }

    foreach ($duplicate in $retiredDuplicates) {
        Move-Item -LiteralPath $duplicate.Retired -Destination $duplicate.Original -ErrorAction Stop
        $duplicatesRestored += $duplicate
        if (-not (SameInventory $duplicate.Inventory (GetInventory $duplicate.Original))) {
            throw "Restored duplicate failed exact inventory verification: $($duplicate.Original)"
        }
    }

    if ($PurgeData) {
        if (Test-Path -LiteralPath $stateHold) { throw "State hold path already exists: $stateHold" }
        Move-Item -LiteralPath $stateRootResolved -Destination $stateHold -ErrorAction Stop
    } else {
        Move-Item -LiteralPath $stateFile -Destination $stateArchive -ErrorAction Stop
    }
    $stateMoved = $true
    if ($TestFailureAt -eq 'AfterStateMoved') { throw 'TEST_FAILURE: AfterStateMoved' }

    if (-not $skipRegistryEffective) {
        $registrySnapshot = GetRegistrySnapshot $regPath
        $registryBefore = RegistryFingerprint $regPath
        if (Test-Path -LiteralPath $regPath) { Remove-Item -LiteralPath $regPath -Recurse -Force -ErrorAction Stop }
        $registryChanged = $true
    }
    if ($TestFailureAt -eq 'AfterRegistryRemoved') { throw 'TEST_FAILURE: AfterRegistryRemoved' }

    $committed = $true
} catch {
    $failure = $_.Exception.Message

    try {
        if ($registryChanged) { RestoreRegistry $regPath $registrySnapshot }
        if ($stateMoved) {
            if ($PurgeData) {
                if (Test-Path -LiteralPath $stateRootResolved) { Remove-Item -LiteralPath $stateRootResolved -Recurse -Force }
                Move-Item -LiteralPath $stateHold -Destination $stateRootResolved -ErrorAction Stop
            } else {
                Move-Item -LiteralPath $stateArchive -Destination $stateFile -ErrorAction Stop
            }
        }
    } catch { $failure += " | metadata rollback error: $($_.Exception.Message)" }

    try {
        for ($duplicateIndex = $duplicatesRestored.Count - 1; $duplicateIndex -ge 0; $duplicateIndex--) {
            $duplicate = $duplicatesRestored[$duplicateIndex]
            if (Test-Path -LiteralPath $duplicate.Original) {
                Move-Item -LiteralPath $duplicate.Original -Destination $duplicate.Retired -ErrorAction Stop
            }
        }
        if ($bundleMoved -and (Test-Path -LiteralPath $bundle)) {
            Remove-Item -LiteralPath $bundle -Recurse -Force -ErrorAction Stop
        }
        if ($bundleMoved -and (Test-Path -LiteralPath $rollback)) {
            Move-Item -LiteralPath $rollback -Destination $bundle -ErrorAction Stop
        }
    } catch { $failure += " | bundle rollback error: $($_.Exception.Message)" }

    $bundleOk = (Test-Path -LiteralPath $bundle -PathType Container) -and
        (SameInventory $oldInventory (GetInventory $bundle))
    $stateOk = (Test-Path -LiteralPath $stateFile -PathType Leaf) -and ((FileSha $stateFile) -eq $stateSha)
    $restoreOk = $true
    if ($previousExisted) {
        $restoreOk = (Test-Path -LiteralPath $restorePoint -PathType Container) -and
            (SameInventory $restoreExpected (GetInventory $restorePoint))
    }
    $registryOk = $true
    if (-not $skipRegistryEffective -and $registryChanged) {
        $registryOk = (RegistryFingerprint $regPath) -eq $registryBefore
    }
    $duplicatesOk = $true
    foreach ($duplicate in $retiredDuplicates) {
        if ((Test-Path -LiteralPath $duplicate.Original) -or
            -not (Test-Path -LiteralPath $duplicate.Retired -PathType Container) -or
            -not (SameInventory $duplicate.Inventory (GetInventory $duplicate.Retired))) {
            $duplicatesOk = $false
        }
    }

    if ($bundleOk -and $stateOk -and $restoreOk -and $registryOk -and $duplicatesOk) {
        Remove-Item -LiteralPath $candidate -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $stateArchive -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $stateHold -Recurse -Force -ErrorAction SilentlyContinue
        Fail ("Uninstall transaction failed; old bundle/state/registry/restore point were restored and verified. $failure") 9
    }

    $recovery = @($rollback, $candidate, $stateHold) | Where-Object { Test-Path -LiteralPath $_ }
    Fail ("Uninstall failed and rollback did not verify byte-identical. Recovery evidence was retained at: " +
          ($recovery -join '; ') + ". Error: $failure") 10
}

if (-not $committed) { Fail 'Uninstall did not reach a verified commit.' 10 }

# Post-commit cleanup cannot invalidate the verified result. Non-.bundle recovery
# directories are retained with a warning if Windows has them locked.
Remove-Item -LiteralPath $rollback -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $rollback) { Write-Warning "Committed, but old rollback directory is retained at: $rollback" }
Remove-Item -LiteralPath $candidate -Recurse -Force -ErrorAction SilentlyContinue

if ($PurgeData) {
    Remove-Item -LiteralPath $stateHold -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $stateHold) { Write-Warning "Committed, but purged data is retained at: $stateHold" }
    Info 'Civil Delivery state and work products were purged as requested.'
} else {
    Info "Install state archived at: $stateArchive"
    Info 'Project profiles, approved mappings, run evidence, and restore evidence were retained.'
}

Info ''
if ($previousExisted) {
    Info "Previous MahodAI restored transactionally; $($expected.Count) final file(s) verified."
} else {
    Info "Civil Delivery removed transactionally; $($expected.Count) changed/foreign file(s) retained."
}
exit 0
