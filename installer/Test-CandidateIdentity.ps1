<#
.SYNOPSIS
    Proves that the current 2027 and 2026 Release build outputs are the exact
    binaries carried through the installer stage, runtime gate and employee ZIP.

.DESCRIPTION
    This suite never touches Program Files, AppData ApplicationPlugins or Civil 3D.
    The install script is exercised only against an explicitly named TEMP sandbox.
    It executes only the setup EXE's read-only embedded-payload identity seam, which
    runs before UI/elevation/extraction. It never runs the install path.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutFile
)

$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path $here
$releaseFile = Join-Path $repo 'release\release.json'
if (-not (Test-Path $releaseFile)) { throw "Release metadata missing: $releaseFile" }
$release = Get-Content $releaseFile -Raw | ConvertFrom-Json
$policyPath = Join-Path $repo 'release\ReleaseGatePolicy.ps1'
if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) { throw "Release gate policy missing: $policyPath" }
. $policyPath
$pdfValidationPath = Join-Path $repo 'nataly\PdfValidation.ps1'
if (-not (Test-Path -LiteralPath $pdfValidationPath -PathType Leaf)) { throw "PDF validator missing: $pdfValidationPath" }
. $pdfValidationPath
if (-not $Version) { $Version = [string]$release.package_revision }
if (-not $OutFile) { $OutFile = Join-Path $repo 'evidence\candidate_identity_evidence.txt' }

$log = New-Object System.Collections.ArrayList
$failures = 0
function L([string]$m) { [void]$log.Add($m); Write-Host $m }
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    L ($(if ($ok) { '  PASS  ' } else { '  FAIL  ' }) + $name + $(if ($detail) { "  [$detail]" } else { '' }))
    if (-not $ok) { $script:failures++ }
}
function Sha([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Compare-RequiredFile([string]$name, [string]$expected, [string]$actual) {
    $expectedExists = Test-Path -LiteralPath $expected -PathType Leaf
    $actualExists = Test-Path -LiteralPath $actual -PathType Leaf
    Check "$name expected file present" $expectedExists $expected
    Check "$name compared file present" $actualExists $actual
    if ($expectedExists -and $actualExists) {
        $expectedSha = Sha $expected
        $actualSha = Sha $actual
        Check "$name byte-identical" ($expectedSha -eq $actualSha) "expected=$expectedSha actual=$actualSha"
    }
}
function Read-Json([string]$path, [string]$name) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Check "$name present" $false $path
        return $null
    }
    Check "$name present" $true $path
    try { return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    catch {
        Check "$name parses as JSON" $false $_.Exception.Message
        return $null
    }
}
function Tree-Inventory([string]$root) {
    $map = @{}
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { return $map }
    $rootFull = [System.IO.Path]::GetFullPath($root).TrimEnd('\')
    foreach ($file in Get-ChildItem -LiteralPath $rootFull -Recurse -File -Force) {
        $relative = $file.FullName.Substring($rootFull.Length).TrimStart('\').Replace('\', '/')
        $map[$relative] = Sha $file.FullName
    }
    return $map
}
function Manifest-Inventory($value) {
    $map = @{}
    if ($null -eq $value) { return $map }
    foreach ($property in $value.PSObject.Properties) {
        $map[$property.Name.Replace('\','/')] = ([string]$property.Value).ToLowerInvariant()
    }
    return $map
}
function Check-ExactInventory([string]$name, $expected, $actual) {
    $missingOrChanged = @($expected.Keys | Where-Object {
        -not $actual.ContainsKey($_) -or $actual[$_] -ne $expected[$_]
    })
    $extra = @($actual.Keys | Where-Object { -not $expected.ContainsKey($_) })
    Check $name `
        ($expected.Count -eq $actual.Count -and $missingOrChanged.Count -eq 0 -and $extra.Count -eq 0) `
        "expected=$($expected.Count) actual=$($actual.Count) missing_or_changed=$($missingOrChanged.Count) extra=$($extra.Count)"
}
function Validate-Sha256Sidecar([string]$target, [string]$sidecar, [string]$name) {
    $targetPresent = Test-Path -LiteralPath $target -PathType Leaf
    $sidecarPresent = Test-Path -LiteralPath $sidecar -PathType Leaf
    Check "$name target present" $targetPresent $target
    Check "$name sidecar present" $sidecarPresent $sidecar
    if (-not ($targetPresent -and $sidecarPresent)) { return }
    $line = (Get-Content -LiteralPath $sidecar -Raw).Trim()
    $match = [regex]::Match($line, '^(?<sha>[0-9a-fA-F]{64})\s{2}(?<name>[^\\/\r\n]+)$')
    Check "$name sidecar format is canonical" $match.Success $line
    if (-not $match.Success) { return }
    Check "$name sidecar names exact target" `
        ($match.Groups['name'].Value -eq [System.IO.Path]::GetFileName($target)) `
        $match.Groups['name'].Value
    Check "$name sidecar binds exact bytes" `
        ($match.Groups['sha'].Value.ToLowerInvariant() -eq (Sha $target)) `
        "recorded=$($match.Groups['sha'].Value) actual=$(Sha $target)"
}
function Shippable-BuildFiles([string]$root) {
    $map = @{}
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { return $map }
    $rootFull = [System.IO.Path]::GetFullPath($root).TrimEnd('\')
    $blockedSegments = @('data','assets','net8.0-windows','net10.0-windows')
    $blockedExtensions = @('.pdb','.xml','.nupkg')
    foreach ($file in Get-ChildItem -LiteralPath $rootFull -Recurse -File -Force) {
        $relative = $file.FullName.Substring($rootFull.Length).TrimStart('\')
        if ($blockedExtensions -contains $file.Extension.ToLowerInvariant()) { continue }
        if (@($relative -split '[\\/]' | Where-Object { $blockedSegments -contains $_ }).Count) { continue }
        $map[$relative.Replace('\','/')] = Sha $file.FullName
    }
    return $map
}
function Validate-DefenderEvidence([string]$relativeEvidence, [string[]]$expectedTargets, [string]$name) {
    $path = Join-Path $repo ($relativeEvidence.Replace('/','\'))
    $doc = Read-Json $path $name
    if (-not $doc) { return }
    Check "$name verdict CLEAN" ([string]$doc.verdict -eq 'CLEAN') ([string]$doc.verdict)
    Check "$name scanner SHA256 present" (Test-MahodSha256Text ([string]$doc.scanner.sha256)) ([string]$doc.scanner.path)
    Check "$name scanner file version present" (-not [string]::IsNullOrWhiteSpace([string]$doc.scanner.file_version))
    $statusAvailable = $null -ne $doc.defender_status
    Check "$name records Defender status or the exact status-query error" `
        (($statusAvailable -and
          -not [string]::IsNullOrWhiteSpace([string]$doc.defender_status.antivirus_signature_version)) -or
         (-not $statusAvailable -and
          -not [string]::IsNullOrWhiteSpace([string]$doc.defender_status_error)))
    if ($statusAvailable) {
        Check "$name records Defender real-time status" `
            ($null -ne $doc.defender_status.realtime_protection_enabled)
        Check "$name records Defender signature update UTC" `
            (-not [string]::IsNullOrWhiteSpace([string]$doc.defender_status.antivirus_signature_updated_utc))
    }
    $scans = @($doc.scans)
    Check "$name has exact scan count" ($scans.Count -eq $expectedTargets.Count) "actual=$($scans.Count) expected=$($expectedTargets.Count)"
    foreach ($expectedRelative in $expectedTargets) {
        $normalized = $expectedRelative.Replace('\','/')
        $scan = $scans | Where-Object { ([string]$_.relative_path).Replace('\','/') -eq $normalized } | Select-Object -First 1
        Check "$name includes $normalized" ($null -ne $scan)
        if ($scan) {
            $target = Join-Path $repo ($normalized.Replace('/','\'))
            $targetExists = Test-Path -LiteralPath $target -PathType Leaf
            Check "$name target exists: $normalized" $targetExists
            if ($targetExists) {
                Check "$name hash binds $normalized" ((Sha $target) -eq ([string]$scan.sha256).ToLowerInvariant())
                Check "$name size binds $normalized" ((Get-Item -LiteralPath $target).Length -eq [long]$scan.size_bytes)
            }
            Check "$name clean result for $normalized" `
                ($scan.clean -eq $true -and $scan.bytes_unchanged -eq $true -and
                 $scan.timed_out -eq $false -and [int]$scan.exit_code -eq 0)
        }
    }
}
function Invoke-SetupProbe([string]$exe, [string]$argument, [int]$timeoutMilliseconds = 120000) {
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $exe
        # Windows paths cannot contain a double quote; quoting the whole argument keeps
        # paths with spaces as one argv element under Windows PowerShell 5.1.
        $psi.Arguments = '"' + $argument + '"'
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $process = [System.Diagnostics.Process]::Start($psi)
        if (-not $process.WaitForExit($timeoutMilliseconds)) {
            try { $process.Kill() } catch { }
            return -1
        }
        return $process.ExitCode
    } catch {
        L ("  probe exception: " + $_.Exception.Message)
        return -2
    }
}

$expectedPlatform = [string]$release.platform_version
$sourceManifestName = "source_manifest_$Version.json"
$sourceManifestPath = Join-Path $repo "release\$sourceManifestName"
$sourceManifest = Read-Json $sourceManifestPath 'frozen source manifest'
$sourceManifestHash = Sha $sourceManifestPath
$sourceManifestBuilder = Join-Path $repo 'release\build-source-manifest.py'
$python = Get-Command python.exe -ErrorAction SilentlyContinue
Check 'source-manifest verifier available' ($null -ne $python) 'python.exe'
if ($python -and $sourceManifest) {
    & $python.Source $sourceManifestBuilder $Version --verify 2>&1 | ForEach-Object { L ("  source: " + $_) }
    Check 'frozen source manifest exactly matches current source' ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"
    Check 'source manifest revision is current' ([string]$sourceManifest.package_revision -eq $Version)
    Check 'source manifest platform version is current' ([string]$sourceManifest.platform_version -eq $expectedPlatform)
    Check 'source manifest SHA256 is valid' (Test-MahodSha256Text $sourceManifestHash) $sourceManifestHash
    Check 'source manifest root SHA256 is valid' (Test-MahodSha256Text ([string]$sourceManifest.root_sha256))
    Check 'source manifest has files' ([int]$sourceManifest.file_count -gt 0) "count=$($sourceManifest.file_count)"
    Check 'source manifest file_count matches complete recorded inventory' `
        ([int]$sourceManifest.file_count -eq @($sourceManifest.files.PSObject.Properties).Count) `
        "recorded=$($sourceManifest.file_count) actual=$(@($sourceManifest.files.PSObject.Properties).Count)"
    Check 'source manifest binds canonical employee-package path' `
        ([string]$sourceManifest.canonical_employee_package -eq [string]$release.canonical_employee_package)
}
$csproj = Join-Path $repo 'MahodAI.Civil3D.Plugin\MahodAI.Civil3D.Plugin.csproj'
$projectPlatform = ([xml](Get-Content -LiteralPath $csproj)).Project.PropertyGroup.FileVersion |
    Where-Object { $_ } | Select-Object -First 1
$coreCsproj = Join-Path $repo 'MahodAI.CivilDelivery.Core\MahodAI.CivilDelivery.Core.csproj'
$coreProjectPlatform = ([xml](Get-Content -LiteralPath $coreCsproj)).Project.PropertyGroup.FileVersion |
    Where-Object { $_ } | Select-Object -First 1

$components = @(
    [pscustomobject]@{
        Name = '2027 platform'; Relative = 'MahodAI.bundle\Contents\MahodAI.Civil3D.Plugin.dll'
        Build = 'MahodAI.Civil3D.Plugin\bin\Release\MahodAI.Civil3D.Plugin.dll'
    },
    [pscustomobject]@{
        Name = '2027 core'; Relative = 'MahodAI.bundle\Contents\MahodAI.CivilDelivery.Core.dll'
        Build = 'MahodAI.CivilDelivery.Core\bin\Release\net10.0-windows\MahodAI.CivilDelivery.Core.dll'
    },
    [pscustomobject]@{
        Name = '2026 platform'; Relative = 'MahodAI.bundle\Contents\2026\MahodAI.Civil3D.Plugin.dll'
        Build = 'MahodAI.Civil3D.Plugin\bin2026\Release\MahodAI.Civil3D.Plugin.dll'
    },
    [pscustomobject]@{
        Name = '2026 core'; Relative = 'MahodAI.bundle\Contents\2026\MahodAI.CivilDelivery.Core.dll'
        Build = 'MahodAI.Civil3D.Plugin\bin2026\Release\net8.0-windows\MahodAI.CivilDelivery.Core.dll'
    }
)

$work = Join-Path $env:TEMP ('mcd_candidate_verification_sandbox_' + (Get-Date -Format 'yyyyMMddHHmmssfff'))
New-Item -ItemType Directory -Force -Path $work | Out-Null

L '=== Release candidate chain verification ==='
L ("generated_utc          : " + (Get-Date).ToUniversalTime().ToString('o'))
L ("package revision       : $Version")
L ("platform file version  : $expectedPlatform")
L ("temporary sandbox only : $work")
L ''

try {
    Check 'requested revision matches release metadata' ($Version -eq [string]$release.package_revision) `
        "requested=$Version metadata=$($release.package_revision)"
    $statusMatrix = Test-MahodReleaseStatusMatrix -Release $release
    Check 'Civil host status matrix is valid' $statusMatrix.Valid (($statusMatrix.Errors) -join ' | ')
    $employeeEligibility = Get-MahodEmployeeReleaseEligibility -Release $release
    Check 'project FileVersion matches release metadata' ([string]$projectPlatform -eq $expectedPlatform) `
        "project=$projectPlatform metadata=$expectedPlatform"
    Check 'core project FileVersion matches release metadata' ([string]$coreProjectPlatform -eq $expectedPlatform) `
        "project=$coreProjectPlatform metadata=$expectedPlatform"

    $payload = Join-Path $repo 'installer\stage\payload'
    $sourceInstallScript = Join-Path $here 'Install-MahodCivilDelivery.ps1'
    $stagedInstallScript = Join-Path $repo 'installer\stage\Install-MahodCivilDelivery.ps1'
    $buildManifestPath = Join-Path $repo 'installer\stage\build_manifest.json'
    $buildManifestJson = if (Test-Path -LiteralPath $buildManifestPath -PathType Leaf) {
        Get-Content -LiteralPath $buildManifestPath -Raw
    } else { $null }
    $buildManifest = Read-Json $buildManifestPath 'installer build manifest'
    if ($buildManifest) {
        Check 'installer manifest revision is current' ($buildManifest.version -eq $Version) "version=$($buildManifest.version)"
        Check 'installer manifest says Release' ($buildManifest.configuration -eq 'Release') "configuration=$($buildManifest.configuration)"
        Check 'installer manifest says 2027 COMPILED' ($buildManifest.civil_2027_component -eq 'COMPILED') "status=$($buildManifest.civil_2027_component)"
        Check 'installer manifest says 2026 COMPILED' ($buildManifest.civil_2026_component -eq 'COMPILED') "status=$($buildManifest.civil_2026_component)"
        Check 'installer manifest platform version is current' ($buildManifest.platform_file_version -eq $expectedPlatform) `
            "version=$($buildManifest.platform_file_version)"
        $stagedSourceManifest = Join-Path $repo ("installer\stage\" + $sourceManifestName)
        Compare-RequiredFile 'staged frozen source manifest' $sourceManifestPath $stagedSourceManifest
        Check 'build manifest binds source-manifest filename' `
            ([string]$buildManifest.source_manifest_file -eq $sourceManifestName)
        Check 'build manifest binds source-manifest bytes' `
            ([string]$buildManifest.source_manifest_sha256 -eq $sourceManifestHash) `
            "recorded=$($buildManifest.source_manifest_sha256) actual=$sourceManifestHash"
        if ($sourceManifest) {
            Check 'build manifest binds source root hash' `
                ([string]$buildManifest.source_manifest_root_sha256 -eq [string]$sourceManifest.root_sha256)
            Check 'build manifest binds source file count' `
                ([int]$buildManifest.source_manifest_file_count -eq [int]$sourceManifest.file_count)
        }
        $recordedInventory = @{}
        if ($buildManifest.bundle_file_inventory) {
            foreach ($property in $buildManifest.bundle_file_inventory.PSObject.Properties) {
                $recordedInventory[$property.Name.Replace('\','/')] = ([string]$property.Value).ToLowerInvariant()
            }
        }
        $stagedBundleRoot = Join-Path $payload 'MahodAI.bundle'
        $actualInventory = Tree-Inventory $stagedBundleRoot
        Check 'build manifest records every staged bundle file' `
            ($recordedInventory.Count -eq $actualInventory.Count -and
             @($recordedInventory.Keys | Where-Object {
                 -not $actualInventory.ContainsKey($_) -or $actualInventory[$_] -ne $recordedInventory[$_]
             }).Count -eq 0) "recorded=$($recordedInventory.Count) actual=$($actualInventory.Count)"
        Check 'build manifest bundle_file_count is exact' ([int]$buildManifest.bundle_file_count -eq $actualInventory.Count)

        $expectedInventory = @{}
        $manifestFile = Join-Path $stagedBundleRoot 'PackageContents.xml'
        $uninstallerFile = Join-Path $stagedBundleRoot 'Installer\Uninstall-MahodCivilDelivery.ps1'
        if (Test-Path -LiteralPath $manifestFile -PathType Leaf) { $expectedInventory['PackageContents.xml'] = Sha $manifestFile }
        if (Test-Path -LiteralPath $uninstallerFile -PathType Leaf) {
            $expectedInventory['Installer/Uninstall-MahodCivilDelivery.ps1'] = Sha $uninstallerFile
        }
        foreach ($entry in (Shippable-BuildFiles (Join-Path $repo 'MahodAI.Civil3D.Plugin\bin\Release')).GetEnumerator()) {
            $expectedInventory['Contents/' + $entry.Key] = $entry.Value
        }
        foreach ($entry in (Shippable-BuildFiles (Join-Path $repo 'MahodAI.Civil3D.Plugin\bin2026\Release')).GetEnumerator()) {
            $expectedInventory['Contents/2026/' + $entry.Key] = $entry.Value
        }
        Check 'staged bundle has exact current-output inventory with no stale extras' `
            ($expectedInventory.Count -eq $actualInventory.Count -and
             @($expectedInventory.Keys | Where-Object {
                 -not $actualInventory.ContainsKey($_) -or $actualInventory[$_] -ne $expectedInventory[$_]
             }).Count -eq 0) "expected=$($expectedInventory.Count) actual=$($actualInventory.Count)"
    }

    $finalSetup = Join-Path $repo "installer\out\Mahod_Civil_Delivery_Setup_$Version.exe"
    $finalSetupPresent = Test-Path -LiteralPath $finalSetup -PathType Leaf
    Check 'final setup EXE present for embedded-payload verification' $finalSetupPresent $finalSetup
    Validate-Sha256Sidecar $finalSetup ($finalSetup + '.sha256') 'final setup EXE'
    if ($finalSetupPresent) {
        $payloadProbeExit = Invoke-SetupProbe $finalSetup `
            ("--self-test-verify-payload-dir=" + (Join-Path $repo 'installer\stage'))
        Check 'setup embedded payload exactly matches installer stage' ($payloadProbeExit -eq 0) `
            "exit=$payloadProbeExit"

        $mismatchDir = Join-Path $work 'deliberately-incomplete-payload'
        New-Item -ItemType Directory -Force -Path $mismatchDir | Out-Null
        Set-Content -LiteralPath (Join-Path $mismatchDir 'unexpected.txt') -Value 'identity gate must reject this' -Encoding ascii
        $mismatchExit = Invoke-SetupProbe $finalSetup `
            ("--self-test-verify-payload-dir=" + $mismatchDir)
        Check 'setup embedded-payload gate rejects a deliberate mismatch' ($mismatchExit -ne 0) `
            "exit=$mismatchExit"
    }

    $setupDefenderRelative = "installer/out/defender_setup_$Version.json"
    Validate-DefenderEvidence $setupDefenderRelative `
        @("installer/out/Mahod_Civil_Delivery_Setup_$Version.exe") 'setup Defender evidence'

    $installerManifestPath = Join-Path $repo 'installer\out\installer_manifest.json'
    $installerManifestJson = if (Test-Path -LiteralPath $installerManifestPath -PathType Leaf) {
        Get-Content -LiteralPath $installerManifestPath -Raw
    } else { $null }
    $installerManifest = Read-Json $installerManifestPath 'installer output manifest'
    if ($installerManifest) {
        $setupDefenderPath = Join-Path $repo ($setupDefenderRelative.Replace('/','\'))
        Check 'installer output manifest revision is current' `
            ([string]$installerManifest.version -eq $Version) ([string]$installerManifest.version)
        Check 'installer output manifest platform version is current' `
            ([string]$installerManifest.platform_file_version -eq $expectedPlatform) `
            ([string]$installerManifest.platform_file_version)
        Check 'installer output manifest binds source-manifest filename' `
            ([string]$installerManifest.source_manifest_file -eq $sourceManifestName)
        Check 'installer output manifest binds source-manifest bytes' `
            ([string]$installerManifest.source_manifest_sha256 -eq $sourceManifestHash) `
            "recorded=$($installerManifest.source_manifest_sha256) actual=$sourceManifestHash"
        if ($sourceManifest) {
            Check 'installer output manifest binds source root hash' `
                ([string]$installerManifest.source_manifest_root_sha256 -eq [string]$sourceManifest.root_sha256)
            Check 'installer output manifest binds source file count' `
                ([int]$installerManifest.source_manifest_file_count -eq [int]$sourceManifest.file_count)
        }
        $installerBuiltAt = [DateTimeOffset]::MinValue
        $installerBuiltText = if ($installerManifestJson) {
            Get-MahodJsonStringValue -Json $installerManifestJson -PropertyName 'built_utc'
        } else { $null }
        $installerBuiltValid = $installerManifestJson -and
            (ConvertFrom-MahodUtcJsonTimestamp -Json $installerManifestJson `
                -PropertyName 'built_utc' -Value ([ref]$installerBuiltAt))
        Check 'installer output manifest records setup publication UTC' $installerBuiltValid `
            $installerBuiltText
        if ($installerBuiltValid -and $buildManifest -and $buildManifestJson) {
            $stageBuiltAt = [DateTimeOffset]::MinValue
            $stageBuiltValid = ConvertFrom-MahodUtcJsonTimestamp -Json $buildManifestJson `
                -PropertyName 'built_utc' -Value ([ref]$stageBuiltAt)
            Check 'setup publication does not predate staged build' `
                ($stageBuiltValid -and $installerBuiltAt -ge $stageBuiltAt)
        }
        Check 'installer output manifest binds exact setup hash' `
            ($finalSetupPresent -and ([string]$installerManifest.sha256).ToLowerInvariant() -eq (Sha $finalSetup))
        Check 'installer output manifest binds exact setup size' `
            ($finalSetupPresent -and [long]$installerManifest.size_bytes -eq (Get-Item -LiteralPath $finalSetup).Length)
        Check 'installer output manifest names exact setup Defender receipt' `
            (([string]$installerManifest.defender_evidence).Replace('\','/') -eq $setupDefenderRelative)
        Check 'installer output manifest binds exact Defender receipt hash' `
            ((Test-Path -LiteralPath $setupDefenderPath -PathType Leaf) -and
             ([string]$installerManifest.defender_evidence_sha256).ToLowerInvariant() -eq (Sha $setupDefenderPath))
        if ($buildManifest) {
            Check 'installer output manifest bundle count matches build manifest' `
                ([int]$installerManifest.bundle_file_count -eq [int]$buildManifest.bundle_file_count)
        }
    }

    L ''
    L '--- current Release build outputs -> installer stage ---'
    Compare-RequiredFile 'staged installer script' $sourceInstallScript $stagedInstallScript
    foreach ($component in $components) {
        $buildComponent = Join-Path $repo $component.Build
        $stagedComponent = Join-Path $payload $component.Relative
        Compare-RequiredFile $component.Name $buildComponent $stagedComponent
        foreach ($candidate in @($buildComponent, $stagedComponent)) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $candidate)).FileVersion
                Check "$($component.Name) has platform version $expectedPlatform" `
                    ($fileVersion -eq $expectedPlatform) "path=$candidate version=$fileVersion"
            }
        }
    }

    L ''
    L '--- live GUI acceptance claims -> exact setup/staged DLL/evidence hashes ---'
    foreach ($hostYear in @(2027, 2026)) {
        $hostStatus = if ($hostYear -eq 2027) { [string]$release.civil_2027_status } else { [string]$release.civil_2026_status }
        if ($hostStatus -eq 'COMPILED_GUI_ACCEPTED') {
            $attestation = Test-MahodLiveAcceptanceAttestation -Repo $repo -Release $release -HostYear $hostYear
            Check "Civil $hostYear ACCEPTED claim has exact hash-keyed live attestation" `
                $attestation.Valid (($attestation.Errors) -join ' | ')
        } else {
            Check "Civil $hostYear does not claim unproved GUI acceptance" $true $hostStatus
        }
    }

    $manifestPath = Join-Path $payload 'MahodAI.bundle\PackageContents.xml'
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        $bundleManifest = [xml](Get-Content -LiteralPath $manifestPath)
        Check 'bundle AppVersion is current' ([string]$bundleManifest.ApplicationPackage.AppVersion -eq $Version) `
            "AppVersion=$($bundleManifest.ApplicationPackage.AppVersion)"
        $modules = @($bundleManifest.ApplicationPackage.Components.ComponentEntry | ForEach-Object { [string]$_.ModuleName })
        Check 'bundle declares Civil 3D 2027 module' ($modules -contains './Contents/MahodAI.Civil3D.Plugin.dll')
        Check 'bundle declares Civil 3D 2026 module' ($modules -contains './Contents/2026/MahodAI.Civil3D.Plugin.dll')
    } else {
        Check 'bundle manifest present' $false $manifestPath
    }

    L ''
    L '--- runtime gate package (both host payloads) ---'
    $gateZip = Join-Path $repo "runtime-gate\out\Mahod_Civil_Delivery_ARTHUR_RUNTIME_GATE_$Version.zip"
    if (Test-Path -LiteralPath $gateZip -PathType Leaf) {
        Check 'runtime gate package present' $true $gateZip
        Validate-Sha256Sidecar $gateZip ($gateZip + '.sha256') 'runtime gate ZIP'
        $gateDir = Join-Path $work 'runtime-gate'
        Expand-Archive -LiteralPath $gateZip -DestinationPath $gateDir -Force
        foreach ($component in $components) {
            Compare-RequiredFile "runtime gate $($component.Name)" `
                (Join-Path $payload $component.Relative) `
                (Join-Path (Join-Path $gateDir 'payload') $component.Relative)
        }
        $gateManifest = Read-Json (Join-Path $gateDir 'build_manifest.json') 'runtime gate manifest'
        if ($gateManifest) {
            Check 'runtime gate revision is current' ($gateManifest.version -eq $Version) "version=$($gateManifest.version)"
            Check 'runtime gate says Release' ($gateManifest.configuration -eq 'Release') "configuration=$($gateManifest.configuration)"
            Check 'runtime gate says 2027 COMPILED' ($gateManifest.civil_2027_component -eq 'COMPILED') "status=$($gateManifest.civil_2027_component)"
            Check 'runtime gate says 2026 COMPILED' ($gateManifest.civil_2026_component -eq 'COMPILED') "status=$($gateManifest.civil_2026_component)"
            Check 'runtime gate binds source-manifest bytes' `
                ([string]$gateManifest.source_manifest_sha256 -eq $sourceManifestHash)
            Check 'runtime gate names exact source manifest' `
                ([string]$gateManifest.source_manifest_file -eq $sourceManifestName) `
                ([string]$gateManifest.source_manifest_file)
            if ($sourceManifest) {
                Check 'runtime gate binds source root hash' `
                    ([string]$gateManifest.source_manifest_root_sha256 -eq [string]$sourceManifest.root_sha256)
                Check 'runtime gate binds source file count' `
                    ([int]$gateManifest.source_manifest_file_count -eq [int]$sourceManifest.file_count)
            }

            $actualGatePayload = Tree-Inventory (Join-Path $gateDir 'payload')
            $recordedGatePayload = Manifest-Inventory $gateManifest.payload_file_inventory
            Check-ExactInventory 'runtime manifest binds complete payload inventory' `
                $recordedGatePayload $actualGatePayload
            Check 'runtime manifest payload_file_count is exact' `
                ([int]$gateManifest.payload_file_count -eq $actualGatePayload.Count) `
                "recorded=$($gateManifest.payload_file_count) actual=$($actualGatePayload.Count)"

            $expectedGatePayload = Tree-Inventory $payload
            $fixtureSource = Join-Path $repo 'fixtures\civil-delivery\modiin-6422'
            if (Test-Path -LiteralPath $fixtureSource -PathType Container) {
                foreach ($fixture in Get-ChildItem -LiteralPath $fixtureSource -Filter '*.dwg' -File) {
                    $expectedGatePayload['fixtures/' + $fixture.Name] = Sha $fixture.FullName
                }
            }
            Check-ExactInventory 'runtime payload is exact installer payload plus declared fixture drawings' `
                $expectedGatePayload $actualGatePayload

            $actualGateFiles = Tree-Inventory $gateDir
            [void]$actualGateFiles.Remove('build_manifest.json')
            $recordedGateFiles = Manifest-Inventory $gateManifest.gate_file_inventory
            Check-ExactInventory 'runtime manifest binds every non-self gate file with no extras' `
                $recordedGateFiles $actualGateFiles
            Check 'runtime manifest gate_file_count is exact' `
                ([int]$gateManifest.gate_file_count -eq $actualGateFiles.Count) `
                "recorded=$($gateManifest.gate_file_count) actual=$($actualGateFiles.Count)"

            Check 'runtime manifest binds installer build-manifest bytes' `
                ([string]$gateManifest.installer_build_manifest_sha256 -eq (Sha $buildManifestPath))
            Check 'runtime manifest binds installer output-manifest bytes' `
                ([string]$gateManifest.installer_output_manifest_sha256 -eq (Sha $installerManifestPath))
            Check 'runtime manifest binds setup Defender receipt bytes' `
                ([string]$gateManifest.setup_defender_receipt_sha256 -eq (Sha (Join-Path $repo $setupDefenderRelative.Replace('/','\'))))
        }
        Compare-RequiredFile 'runtime gate frozen source manifest' $sourceManifestPath `
            (Join-Path $gateDir $sourceManifestName)
        $gateSetup = Join-Path $gateDir "Mahod_Civil_Delivery_Setup_$Version.exe"
        $realSetup = Join-Path $repo "installer\out\Mahod_Civil_Delivery_Setup_$Version.exe"
        Compare-RequiredFile 'runtime gate setup EXE' $realSetup $gateSetup
        $realSetupSidecar = $realSetup + '.sha256'
        $gateSetupSidecar = $gateSetup + '.sha256'
        Compare-RequiredFile 'runtime gate setup SHA256 sidecar' $realSetupSidecar $gateSetupSidecar
        Validate-Sha256Sidecar $gateSetup $gateSetupSidecar 'runtime gate embedded setup'
        if ($gateManifest) {
            Check 'runtime manifest binds exact embedded setup bytes' `
                ([string]$gateManifest.setup_sha256 -eq (Sha $gateSetup))
            Check 'runtime manifest binds exact embedded setup-sidecar bytes' `
                ([string]$gateManifest.setup_sidecar_sha256 -eq (Sha $gateSetupSidecar))
        }
    } else {
        Check 'runtime gate package present' $false $gateZip
    }

    L ''
    L '--- canonical employee ZIP (exactly setup EXE + PDF) ---'
    $canonicalRelative = ([string]$release.canonical_employee_package).Replace('/', '\')
    $employeeZip = Join-Path $repo $canonicalRelative
    if (Test-Path -LiteralPath $employeeZip -PathType Leaf) {
        Check 'canonical employee ZIP present' $true $employeeZip
        Validate-Sha256Sidecar $employeeZip ($employeeZip + '.sha256') 'canonical employee ZIP'
        Check 'canonical employee ZIP is allowed by release statuses' $employeeEligibility.Allowed `
            (($employeeEligibility.Reasons) -join ' | ')
        $employeeDir = Join-Path $work 'employee-package'
        Expand-Archive -LiteralPath $employeeZip -DestinationPath $employeeDir -Force
        $rootFiles = @(Get-ChildItem -LiteralPath $employeeDir -File)
        $rootDirs = @(Get-ChildItem -LiteralPath $employeeDir -Directory)
        Check 'employee ZIP has exactly two root files' ($rootFiles.Count -eq 2) "$($rootFiles.Count) files"
        Check 'employee ZIP has no folders' ($rootDirs.Count -eq 0) "$($rootDirs.Count) folders"
        $employeeSetup = Join-Path $employeeDir "Mahod_Civil_Delivery_Setup_$Version.exe"
        $pdfs = @($rootFiles | Where-Object { $_.Extension -ieq '.pdf' })
        Compare-RequiredFile 'employee setup EXE' `
            (Join-Path $repo "installer\out\Mahod_Civil_Delivery_Setup_$Version.exe") $employeeSetup
        Check 'employee ZIP has exactly one PDF guide' ($pdfs.Count -eq 1) (($pdfs | ForEach-Object Name) -join ', ')
        if ($pdfs.Count -eq 1) {
            $pdfValidation = Test-MahodGuidePdf -Path $pdfs[0].FullName
            Check 'employee PDF has valid header/EOF and at least one page' `
                $pdfValidation.Valid (($pdfValidation.Errors) -join ' | ')
        }
        Validate-DefenderEvidence "nataly/out/defender_employee_$Version.json" `
            @("installer/out/Mahod_Civil_Delivery_Setup_$Version.exe", $canonicalRelative.Replace('\','/')) `
            'employee Defender evidence'
    } else {
        Check 'canonical employee ZIP present' $false $employeeZip
    }

    L ''
    L '--- sandbox deployment of Install-MahodCivilDelivery.ps1 ---'
    L '    (this is NOT a Program Files install and does NOT execute the setup EXE)'
    $sandbox = Join-Path $work 'installer-script-sandbox'
    $pluginsRoot = Join-Path $sandbox 'ApplicationPlugins'
    $stateRoot = Join-Path $sandbox 'state'
    New-Item -ItemType Directory -Force -Path $pluginsRoot, $stateRoot | Out-Null
    $installOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $stagedInstallScript `
        -PayloadDir $payload -BundleRootOverride $pluginsRoot -StateRootOverride $stateRoot `
        -SkipHostCheck -Quiet 2>&1 | Out-String
    $installExit = $LASTEXITCODE
    Check 'staged installer script succeeded in TEMP sandbox' ($installExit -eq 0) "exit=$installExit $($installOutput.Trim())"
    $sandboxState = Read-Json (Join-Path $stateRoot 'install_state.json') 'sandbox install state'
    if ($sandboxState) {
        Check 'sandbox receipt package revision is current' `
            ([string]$sandboxState.package_revision -eq $Version) `
            "receipt=$($sandboxState.package_revision) expected=$Version"
        Check 'sandbox receipt platform version is current' `
            ([string]$sandboxState.platform_version -eq $expectedPlatform) `
            "receipt=$($sandboxState.platform_version) expected=$expectedPlatform"
    }
    foreach ($component in $components) {
        Compare-RequiredFile "sandbox-deployed $($component.Name)" `
            (Join-Path $payload $component.Relative) `
            (Join-Path $pluginsRoot $component.Relative)
    }
    foreach ($component in $components) {
        $relative = $component.Relative
        $dll = Join-Path $pluginsRoot $relative
        if (Test-Path -LiteralPath $dll -PathType Leaf) {
            $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $dll)).FileVersion
            Check "sandbox-deployed $relative has platform version $expectedPlatform" ($fileVersion -eq $expectedPlatform) "version=$fileVersion"
        }
    }

    L ''
    L '--- active documentation release metadata ---'
    $escapedVersion = [regex]::Escape($Version)
    $escapedPlatform = [regex]::Escape($expectedPlatform)
    $escapedDisplayStatus = [regex]::Escape((Get-MahodReleaseDisplayStatus -Release $release))
    $docRules = @(
        [pscustomobject]@{ Path='docs/civil-delivery/RUNTIME_GATE_STATUS.md'; Patterns=@(
            "(?m)^\*\*Package revision:\*\* $escapedVersion\s*$",
            "(?m)^\*\*MahodAI platform version:\*\* $escapedPlatform\s*$",
            "(?m)^\*\*Current candidate GUI status:\*\* ``$escapedDisplayStatus``\s*$") },
        [pscustomobject]@{ Path='handoff/00_READ_ME_FIRST.md'; Patterns=@(
            "(?m)^\*\*Version:\*\* $escapedVersion\s*$",
            "(?m)^\*\*Platform assembly:\*\* $escapedPlatform\s*$",
            "(?m)^\*\*Current candidate GUI status:\*\* ``$escapedDisplayStatus``\s*$") },
        [pscustomobject]@{ Path='handoff/01_FINAL_STATUS.md'; Patterns=@(
            "(?m)^# Candidate status - Mahod Civil Delivery $escapedVersion\s*$",
            "(?m)^\*\*Platform assembly:\*\* $escapedPlatform\s*$",
            "(?m)^\*\*GUI acceptance for this candidate:\*\* ``$escapedDisplayStatus``\s*$") },
        [pscustomobject]@{ Path='nataly/docs/01_START_HERE_HE.md'; Patterns=@(
            "(?m)^\*\*[^:\r\n]+:\*\* $escapedVersion\s*$",
            "Mahod_Civil_Delivery_Setup_$escapedVersion\.exe") },
        [pscustomobject]@{ Path='nataly/docs/04_KNOWN_LIMITATIONS_HE.md'; Patterns=@(
            "(?m)^# [^\r\n]+ $escapedVersion\s*$") },
        [pscustomobject]@{ Path='nataly/guide/GUIDE_HE.html'; Patterns=@(
            '\{\{PACKAGE_VERSION\}\}') },
        [pscustomobject]@{ Path='runtime-gate/ARTHUR_10_MIN_GUI_GATE_HE.md'; Patterns=@(
            "(?m)^\*\*[^\r\n]+$escapedVersion[^\r\n]+$escapedPlatform\s*$",
            "Mahod_Civil_Delivery_Setup_$escapedVersion\.exe",
            "(?m)^> \*\*[^:\r\n]+:\*\* ``$escapedDisplayStatus``") }
    )
    foreach ($rule in $docRules) {
        $full = Join-Path $repo $rule.Path
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            Check "doc present: $($rule.Path)" $false $full
            continue
        }
        $text = Get-Content -LiteralPath $full -Raw
        foreach ($pattern in $rule.Patterns) {
            Check "$($rule.Path) matches current release contract" ([regex]::IsMatch($text, $pattern)) $pattern
        }
    }
    $guideSource = Get-Content -LiteralPath (Join-Path $repo 'nataly\guide\GUIDE_HE.html') -Raw
    Check 'guide template has no hard-coded setup revision' `
        (-not [regex]::IsMatch($guideSource, 'Mahod_Civil_Delivery_Setup_\d+\.\d+\.\d+\.exe'))

    L ''
    L '--- payload hygiene ---'
    $pdbs = @(Get-ChildItem -LiteralPath $payload -Filter '*.pdb' -Recurse -ErrorAction SilentlyContinue)
    Check 'no .pdb files in shipped payload' ($pdbs.Count -eq 0) "$($pdbs.Count) found"
    $autodeskRuntime = @(Get-ChildItem -LiteralPath (Join-Path $payload 'MahodAI.bundle\Contents') -File -Recurse |
        Where-Object { $_.Name -match '^(accoremgd|acdbmgd|acmgd|AdWindows|Aecc.*Mgd|Aec.*Mgd)\.dll$' })
    Check 'no Autodesk host assemblies leak into either payload' ($autodeskRuntime.Count -eq 0) "$($autodeskRuntime.Count) found"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

L ''
L '=== RESULT ==='
if ($failures -eq 0) {
    L 'CANDIDATE CHAIN PROVEN'
    L 'The current 2027 and 2026 Release outputs are byte-identical through the staged'
    L 'payload, runtime gate, canonical employee package and TEMP sandbox deployment.'
    L 'Not proven here: setup-EXE execution or loading/behaviour inside Civil 3D.'
} else {
    L "$failures CHECK(S) FAILED - candidate chain is NOT proven."
}

New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
$log -join [Environment]::NewLine | Set-Content -LiteralPath $OutFile -Encoding utf8
L ''
L "evidence written: $OutFile"
if ($failures -gt 0) { exit 1 }
exit 0
