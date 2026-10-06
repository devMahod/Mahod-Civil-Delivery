<# Read-only static release-contract gate. Does not build, package, install or launch Civil. #>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$policyPath = Join-Path $repo 'release\ReleaseGatePolicy.ps1'
if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) { throw "Release gate policy missing: $policyPath" }
. $policyPath
$failures = 0
function Check([string]$Name, [bool]$Pass, [string]$Detail = '') {
    $prefix = if ($Pass) { 'PASS' } else { 'FAIL' }
    Write-Host ("{0,-4}  {1}{2}" -f $prefix, $Name, $(if ($Detail) { "  [$Detail]" } else { '' }))
    if (-not $Pass) { $script:failures++ }
}
function Text([string]$Relative) {
    $path = Join-Path $repo $Relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Check "required file exists: $Relative" $false $path
        return $null
    }
    Check "required file exists: $Relative" $true
    return Get-Content -LiteralPath $path -Raw
}
function Has([string]$Value, [string]$Pattern) {
    return $null -ne $Value -and [regex]::IsMatch($Value, $Pattern)
}

$releaseText = Text 'release\release.json'
if (-not $releaseText) { exit 1 }
try { $release = $releaseText | ConvertFrom-Json; Check 'release JSON parses' $true }
catch { Check 'release JSON parses' $false $_.Exception.Message; exit 1 }
$package = [string]$release.package_revision
$platform = [string]$release.platform_version
$ep = [regex]::Escape($package)
$ef = [regex]::Escape($platform)

Check 'package revision is numeric semver' ($package -match '^\d+\.\d+\.\d+$') $package
Check 'platform version is numeric four-part' ($platform -match '^\d+\.\d+\.\d+\.\d+$') $platform
$statusMatrix = Test-MahodReleaseStatusMatrix -Release $release
Check 'Civil host status matrix is valid' $statusMatrix.Valid (($statusMatrix.Errors) -join ' | ')
$displayStatus = Get-MahodReleaseDisplayStatus -Release $release
$es = [regex]::Escape($displayStatus)
$policyText = Text 'release\ReleaseGatePolicy.ps1'
Check 'acceptance policy requires typed evidence and per-check hash mappings' `
    ((Has $policyText 'Get-MahodRequiredLiveEvidenceKinds') -and
     (Has $policyText 'Get-MahodRequiredEvidenceKindsForLiveCheck') -and
     (Has $policyText 'check_evidence') -and (Has $policyText 'Required live check has no evidence mapping') -and
     (Has $policyText 'Required live check lacks \$requiredKind evidence'))
Check 'acceptance policy orders acceptance after exact build and binds last_live_candidate' `
    ((Has $policyText 'accepted_utc predates the exact installer build') -and
     (Has $policyText 'accepted_utc predates the exact published setup installer') -and
     (Has $policyText 'last_live_candidate to equal package_revision'))
Check 'acceptance policy binds the actually loaded module to the exact attested winner DLL' `
    ((Has $policyText 'does not match the exact attested DLL hash/version') -and
     (Has $policyText 'does not match the attested winning bundle') -and
     (Has $policyText 'plugin_2027_sha256') -and (Has $policyText 'plugin_2026_sha256'))
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
$canonical = ([string]$release.canonical_employee_package).Replace('\', '/')
Check 'canonical employee path is metadata-derived' `
    ($canonical -eq "nataly/out/Mahod_Civil_Delivery_$package.zip") $canonical

$projectText = Text 'MahodAI.Civil3D.Plugin\MahodAI.Civil3D.Plugin.csproj'
if ($projectText) {
    [xml]$project = $projectText
    $fv = [string]($project.Project.PropertyGroup.FileVersion | Where-Object { $_ } | Select-Object -First 1)
    $av = [string]($project.Project.PropertyGroup.AssemblyVersion | Where-Object { $_ } | Select-Object -First 1)
    $v = [string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    Check 'plugin FileVersion matches metadata' ($fv -eq $platform) $fv
    Check 'plugin AssemblyVersion matches metadata' ($av -eq $platform) $av
    Check 'plugin Version matches platform major/minor/patch' ($v -eq ([Version]$platform).ToString(3)) $v
}

$coreProjectText = Text 'MahodAI.CivilDelivery.Core\MahodAI.CivilDelivery.Core.csproj'
if ($coreProjectText) {
    [xml]$coreProject = $coreProjectText
    $coreFv = [string]($coreProject.Project.PropertyGroup.FileVersion | Where-Object { $_ } | Select-Object -First 1)
    $coreAv = [string]($coreProject.Project.PropertyGroup.AssemblyVersion | Where-Object { $_ } | Select-Object -First 1)
    $coreV = [string]($coreProject.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    Check 'core FileVersion matches metadata' ($coreFv -eq $platform) $coreFv
    Check 'core AssemblyVersion matches metadata' ($coreAv -eq $platform) $coreAv
    Check 'core Version matches platform major/minor/patch' ($coreV -eq ([Version]$platform).ToString(3)) $coreV
}

$sectionPlanService = Text 'MahodAI.Civil3D.Plugin\CivilDelivery\Sections\Services\SectionPlanService.cs'
Check 'section engine tool version matches package revision' `
    (Has $sectionPlanService ("public\s+const\s+string\s+ToolVersion\s*=\s*`"civil-delivery/" + $ep + "`"\s*;"))
$projectionLogic = Text 'MahodAI.CivilDelivery.Core\Shared\SectionProjectionLogic.cs'
Check 'section annotation layout identity is current generation' `
    (Has $projectionLogic 'public\s+const\s+string\s+AnnotationLayoutVersion\s*=\s*"layout-v15-content-band-closed-chain"\s*;')
$measuredPlacement = Text 'MahodAI.Civil3D.Plugin\CivilDelivery\Sections\Services\SectionAnnotationPlacementContract.cs'
Check 'measured layout uses native extents and checks all text ink pairs' `
    ((Has $measuredPlacement 'entity.GeometricExtents') -and
     (Has $measuredPlacement 'FindTextInkConflicts') -and
     (Has $measuredPlacement 'TryLayoutLabelsDown'))

$officeAssets = @(
    @{ File = 'HW-CARFRBK-01.dwg'; Sha = 'E2823D50D85E2F669944CDB68B2CC87F44090801CDA75A86F66A6CC11EA38968' },
    @{ File = 'HW-CARFRFRW-01.dwg'; Sha = '412E0E6F226271AA1A66A2749F598C2A2F13A237D94D0EB567E07EC206924428' },
    @{ File = 'HW-ARRW-01.dwg'; Sha = '3E18667013887CA89A64AA043B5EB4FCB7195631FDD18D841FE24E46F96B2943' }
)
foreach ($asset in $officeAssets) {
    $relative = "MahodAI.Civil3D.Plugin\assets\$($asset.File)"
    $assetPath = Join-Path $repo $relative
    $assetExists = Test-Path -LiteralPath $assetPath -PathType Leaf
    Check "required file exists: $relative" $assetExists $assetPath
    $actualSha = if ($assetExists) {
        (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash
    } else { '' }
    Check "approved office asset bytes are exact: $($asset.File)" `
        ($actualSha -eq $asset.Sha) $actualSha
}
$pluginProjectRaw = Get-Content -LiteralPath (Join-Path $repo 'MahodAI.Civil3D.Plugin\MahodAI.Civil3D.Plugin.csproj') -Raw
foreach ($asset in $officeAssets) {
    $filePattern = [regex]::Escape("assets\$($asset.File)")
    $logicalPattern = [regex]::Escape("MahodAI.Civil3D.Plugin.assets.sections.$($asset.File)")
    Check "approved office asset is embedded: $($asset.File)" `
        ((Has $pluginProjectRaw $filePattern) -and (Has $pluginProjectRaw $logicalPattern))
}

$bundleText = Text 'MahodAI.bundle\PackageContents.xml'
if ($bundleText) {
    [xml]$bundle = $bundleText
    Check 'bundle AppVersion matches metadata' ([string]$bundle.ApplicationPackage.AppVersion -eq $package)
    $modules = @($bundle.ApplicationPackage.Components.ComponentEntry | ForEach-Object { [string]$_.ModuleName })
    Check 'bundle declares 2027 module' ($modules -contains './Contents/MahodAI.Civil3D.Plugin.dll')
    Check 'bundle declares 2026 module' ($modules -contains './Contents/2026/MahodAI.Civil3D.Plugin.dll')
}

$bootstrap = Text 'installer\bootstrap\MahodCivilDeliverySetup.csproj'
Check 'bootstrap version matches package revision' (Has $bootstrap "<Version>$ep</Version>")
Check 'bootstrap targets the Civil 2026-compatible runtime' (Has $bootstrap '<TargetFramework>net8\.0-windows</TargetFramework>')
Check 'bootstrap is self-contained' (Has $bootstrap '<SelfContained>true</SelfContained>')
Check 'bootstrap is a win-x64 single file' `
    ((Has $bootstrap '<RuntimeIdentifier>win-x64</RuntimeIdentifier>') -and
     (Has $bootstrap '<PublishSingleFile>true</PublishSingleFile>'))
Check 'bootstrap includes native runtime libraries' `
    (Has $bootstrap '<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>')
Check 'bootstrap compresses the self-contained runtime' `
    (Has $bootstrap '<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>')
$bootstrapProgram = Text 'installer\bootstrap\Program.cs'
Check 'bootstrap does not hide installer completion behind NoExit' `
    (-not (Has $bootstrapProgram '(?im)^\s*var\s+psArgs\s*=.*-NoExit'))
Check 'bootstrap propagates the installer child exit code' `
    (Has $bootstrapProgram 'return\s+(?:p|process|child)\.ExitCode\s*;')
Check 'bootstrap exposes a pre-install child-exit probe' `
    ((Has $bootstrapProgram '--self-test-child-exit=') -and
     (Has $bootstrapProgram 'return\s+RunSelfTestChild\(selfTestExitCode\)\s*;'))
Check 'bootstrap exposes a read-only embedded-payload identity probe' `
    ((Has $bootstrapProgram '--self-test-verify-payload-dir=') -and
     (Has $bootstrapProgram 'VerifyEmbeddedPayload\(expectedPayloadDirectory'))
$elevationDecision = if ($bootstrapProgram) { $bootstrapProgram.IndexOf('if (!internalElevated && MachineWideBundleExists()', [StringComparison]::Ordinal) } else { -1 }
$firstExtraction = if ($bootstrapProgram) { $bootstrapProgram.IndexOf('extract = CreateUniqueExtractionDirectory', [StringComparison]::Ordinal) } else { -1 }
Check 'bootstrap completes self-elevation before any extraction' `
    ($elevationDecision -ge 0 -and $firstExtraction -gt $elevationDecision) `
    "elevation-index=$elevationDecision extraction-index=$firstExtraction"
Check 'bootstrap uses unique extraction roots and no predictable legacy TEMP path' `
    ((Has $bootstrapProgram 'Guid\.NewGuid\(\)') -and
     -not (Has $bootstrapProgram 'MahodCivilDelivery_Install'))
Check 'elevated extraction is protected by an administrator-only ACL' `
    ((Has $bootstrapProgram 'ApplyAdministratorOnlyAcl') -and
     (Has $bootstrapProgram 'SetAccessRuleProtection\(isProtected:\s*true'))
Check 'bootstrap invokes absolute System32 Windows PowerShell' `
    ((Has $bootstrapProgram 'Environment\.SpecialFolder\.System') -and
     (Has $bootstrapProgram '"WindowsPowerShell",\s*"v1\.0",\s*"powershell\.exe"'))
Check 'bootstrap carries and validates original-user SID/profile context' `
    ((Has $bootstrapProgram '--internal-elevated-install') -and
     (Has $bootstrapProgram '--original-user-sid=') -and
     (Has $bootstrapProgram 'ProfileList') -and
     (Has $bootstrapProgram 'APPDATA/LOCALAPPDATA are outside'))
Check 'bootstrap refuses ambiguous externally elevated starts' `
    (Has $bootstrapProgram "Run as administrator")
$appManifest = Text 'installer\bootstrap\app.manifest'
Check 'bootstrap application identity matches package revision' (Has $appManifest "version=`"$ep\.0`"")
$install = Text 'installer\Install-MahodCivilDelivery.ps1'
Check 'installer state revision matches metadata' `
    (Has $install ("(?m)^\`$packageRevision\s*=\s*'" + $ep + "'\s*$"))
Check 'installer initializes its result report before unsupported-host warnings' `
    (Has $install '(?s)\$report\s*=\s*@\(\).*?if\s*\(\$unsupported\.Count\s*-gt\s*0\)')
Check 'installer targets original-user APPDATA/LOCALAPPDATA and HKEY_USERS after UAC' `
    ((Has $install '\$effectiveAppData') -and (Has $install '\$effectiveLocalAppData') -and
     (Has $install 'Registry::HKEY_USERS\\\$effectiveUserSid'))
Check 'installer seals original restore bytes in a SHA256 inventory' `
    ((Has $install 'previous_platform') -and (Has $install 'inventory\s*=\s*\$originalInventory'))
Check 'installer rejects lifecycle state belonging to another bundle path' `
    (Has $install 'Existing install state belongs to a different bundle path')
Check 'installer retires only unchanged stale package-owned files' `
    ((Has $install 'retiredStaleOwned') -and (Has $install 'preservedModifiedOwned'))
Check 'installer persists exact user/state/registry uninstall context' `
    ((Has $install 'installed_user_sid') -and (Has $install 'state_root') -and
     (Has $install 'uninstall_registry_path'))
Check 'installer persists hash-sealed duplicate receipts and verifies duplicate rollback' `
    ((Has $install 'retired_duplicate_bundles') -and (Has $install 'FileCount = \$otherInventory\.Count') -and
     (Has $install 'duplicatesRollbackVerified'))
Check 'installer registers absolute System32 PowerShell to protected bundle uninstaller' `
    ((Has $install "System32\\WindowsPowerShell\\v1\.0\\powershell\.exe") -and
     (Has $install "Installer\\Uninstall-MahodCivilDelivery\.ps1"))

$setup = Text 'installer\build-setup.ps1'
Check 'setup builder locks revision to metadata' (Has $setup 'does not match release metadata')
Check 'setup builder checks platform FileVersion' (Has $setup 'platform version mismatch')
Check 'setup builder checks core FileVersion for both hosts' `
    ((Has $setup '2027 core version mismatch') -and (Has $setup '2026 core version mismatch'))
Check 'setup builder disables live deploy for both host builds' `
    (([regex]::Matches($setup, '-p:DeployPlugin=false')).Count -eq 2)
Check 'setup builder forces clean rebuilds for both host payloads' `
    (([regex]::Matches($setup, '-t:Rebuild')).Count -eq 2)
Check 'setup builder fails closed instead of producing a partial single-host release' `
    ((Has $setup 'Civil 3D 2026 is a required release target') -and
     (Has $setup 'no partial single-host setup will be produced'))
Check 'setup builder removes stale same-version outputs before compiling' `
    ((Has $setup "Remove-Item -LiteralPath \`$setupPath") -and
     (Has $setup "Remove-Item -LiteralPath \(\`$setupPath \+ '\.sha256'\)"))
Check 'setup builder stages 2026 payload' (Has $setup 'Contents\\2026')
Check 'setup builder explicitly publishes a self-contained win-x64 bootstrap' `
    ((Has $setup '-r win-x64') -and (Has $setup '--self-contained true'))
Check 'setup builder stages uninstaller inside the protected bundle' `
    (Has $setup "MahodAI\.bundle\\Installer")
Check 'setup builder verifies and embeds the exact frozen source manifest' `
    ((Has $setup 'build-source-manifest\.py') -and
     (Has $setup '\-\-verify') -and
     (Has $setup 'source_manifest_sha256') -and
     (Has $setup 'source_manifest_root_sha256'))
Check 'setup builder cleans generated host outputs and reconstructs exact staged inventory' `
    ((Has $setup 'Remove-Item -LiteralPath \$repoBundleContents') -and
     (Has $setup 'Copy-ShippableBuild') -and
     (Has $setup 'Assert-ExactInventory') -and
     (Has $setup 'bundle_file_inventory'))
Check 'setup builder excludes debug/docs and duplicate framework/data/assets remnants' `
    ((Has $setup "blockedExtensions\s*=\s*@\('\.pdb', '\.xml', '\.nupkg'\)") -and
     (Has $setup "blockedSegments\s*=\s*@\('data', 'assets', 'net8\.0-windows', 'net10\.0-windows'\)"))
$bundleProject = Text 'MahodAI.Civil3D.Plugin\MahodAI.Civil3D.Plugin.csproj'
Check 'repo bundle refresh is clean rather than overlay-only' `
    ((Has $bundleProject '<RemoveDir Directories="\$\(BundleDir\)"') -and
     (Has $bundleProject 'net10\.0-windows\\\*\*'))
$defender = Text 'installer\Invoke-MahodDefenderScan.ps1'
Check 'Defender scanner fails closed on unavailable/nonzero/timeout' `
    ((Has $defender 'MpCmdRun\.exe is unavailable') -and
     (Has $defender 'timed out scanning') -and (Has $defender 'returned exit'))
Check 'Defender receipt binds tool/status and exact target bytes' `
    ((Has $defender 'antivirus_signature_version') -and
     (Has $defender 'realtime_protection_enabled') -and
     (Has $defender 'bytes_unchanged') -and (Has $defender 'sha256'))
Check 'setup and employee builders invoke exact Defender scans' `
    ((Has $setup 'Invoke-MahodDefenderScan\.ps1') -and
     (Has (Text 'nataly\build-nataly.ps1') 'defender_employee_\$Version\.json'))
$bootstrapGate = Text 'installer\Test-Bootstrap.ps1'
Check 'bootstrap gate poisons all external .NET runtime discovery' `
    ((Has $bootstrapGate 'DOTNET_ROOT') -and (Has $bootstrapGate 'DOTNET_ROOT_X64') -and
     (Has $bootstrapGate 'DOTNET_MULTILEVEL_LOOKUP') -and (Has $bootstrapGate 'DOTNET_DISABLE_GUI_ERRORS'))
Check 'bootstrap gate behavior-tests original-user context resolution' `
    (Has $bootstrapGate '--self-test-validate-original-context')
$identity = Text 'installer\Test-CandidateIdentity.ps1'
Check 'candidate identity validates the release status matrix' `
    (Has $identity 'Test-MahodReleaseStatusMatrix')
Check 'candidate identity verifies source tree and binds it to both installer manifests' `
    ((Has $identity 'build-source-manifest\.py') -and
     (Has $identity 'frozen source manifest exactly matches current source') -and
     (Has $identity 'build manifest binds source-manifest bytes') -and
     (Has $identity 'installer output manifest binds source-manifest bytes'))
Check 'candidate identity validates hash-keyed live acceptance' `
    (Has $identity 'Test-MahodLiveAcceptanceAttestation')
Check 'candidate identity invokes the embedded-payload probe' `
    (Has $identity '--self-test-verify-payload-dir=')
Check 'candidate identity includes a deliberate embedded-payload mismatch probe' `
    (Has $identity 'deliberately-incomplete-payload')
Check 'candidate identity byte-compares the staged installer script' `
    ((Has $identity "Compare-RequiredFile 'staged installer script'") -and
     (Has $identity '\-File\s+\$stagedInstallScript'))
Check 'candidate identity verifies sandbox receipt release identity' `
    ((Has $identity 'sandbox receipt package revision is current') -and
     (Has $identity 'sandbox receipt platform version is current'))
Check 'candidate identity verifies exact inventory, Defender receipts and PDF structure' `
    ((Has $identity 'no stale extras') -and (Has $identity 'Validate-DefenderEvidence') -and
     (Has $identity 'Test-MahodGuidePdf') -and
     (Has $identity 'installer output manifest binds exact Defender receipt hash') -and
      (Has $identity 'setup publication does not predate staged build'))
Check 'candidate identity verifies runtime ZIP sidecar and full extracted inventories' `
    ((Has $identity 'Validate-Sha256Sidecar \$gateZip') -and
     (Has $identity 'runtime manifest binds complete payload inventory') -and
     (Has $identity 'runtime manifest binds every non-self gate file with no extras') -and
     (Has $identity 'runtime gate embedded setup'))
$lifecycle = Text 'installer\Test-InstallerLifecycle.ps1'
Check 'installer lifecycle verifies receipt identity against payload metadata' `
    ((Has $lifecycle 'receipt package_revision matches payload AppVersion') -and
     (Has $lifecycle 'receipt platform_version matches payload DLL FileVersion'))
$uninstaller = Text 'installer\Uninstall-MahodCivilDelivery.ps1'
Check 'uninstaller validates and transactionally reconciles retired duplicates' `
    ((Has $uninstaller 'retired_duplicate_bundles\.inventory') -and
     (Has $uninstaller 'duplicatesRestored') -and (Has $uninstaller 'SameInventory \$duplicate\.Inventory'))
$shadowing = Text 'installer\Test-BundleShadowing.ps1'
Check 'shadowing regression covers durable receipt, uninstall restore and failure rollback' `
    ((Has $shadowing 'durable hash-sealed receipt') -and
     (Has $shadowing 'uninstall restores the duplicate original path') -and
     (Has $shadowing 'verified rollback leaves no disabled duplicate residue'))
$runtime = Text 'runtime-gate\build-runtime-gate.ps1'
Check 'runtime builder consumes installer stage' (Has $runtime 'installer\\stage')
Check 'runtime builder requires both hosts compiled' `
    (Has $runtime "civil_2027_component -ne 'COMPILED'[\s\S]*civil_2026_component -ne 'COMPILED'")
Check 'runtime builder validates 2026 platform payload' `
    (Has $runtime 'MahodAI\.bundle\\Contents\\2026\\MahodAI\.Civil3D\.Plugin\.dll')
Check 'runtime builder carries the installer-bound frozen source manifest' `
    ((Has $runtime 'source_manifest_sha256') -and
     (Has $runtime 'Installer stage source manifest does not match'))
Check 'runtime builder re-verifies current source and binds complete gate inventories' `
    ((Has $runtime 'build-source-manifest\.py') -and (Has $runtime '\-\-verify') -and
     (Has $runtime 'payload_file_inventory') -and (Has $runtime 'gate_file_inventory') -and
     (Has $runtime 'setup_sidecar_sha256') -and
     (Has $runtime 'installer_output_manifest_sha256'))
$runtimeVerifyIndex = if ($runtime) { $runtime.IndexOf('--verify', [StringComparison]::Ordinal) } else { -1 }
$runtimeStageMutationIndex = if ($runtime) { $runtime.IndexOf('Remove-Item $stage', [StringComparison]::Ordinal) } else { -1 }
Check 'runtime source verification runs before runtime-stage mutation' `
    ($runtimeVerifyIndex -ge 0 -and $runtimeStageMutationIndex -gt $runtimeVerifyIndex) `
    "verify-index=$runtimeVerifyIndex mutation-index=$runtimeStageMutationIndex"
$collector = Text 'runtime-gate\collect-evidence.ps1'
Check 'runtime collector records winner revision and both host-specific hashes' `
    ((Has $collector 'package_revision=\$candidateRevision') -and
     (Has $collector 'plugin_2027_sha256') -and (Has $collector 'plugin_2026_sha256') -and
     (Has $collector 'loaded_plugin_binaries'))
$employee = Text 'nataly\build-nataly.ps1'
Check 'employee builder locks revision to metadata' (Has $employee 'does not match release metadata')
Check 'employee builder requires both hosts compiled' `
    (Has $employee "civil_2027_component -ne 'COMPILED'[\s\S]*civil_2026_component -ne 'COMPILED'")
Check 'employee builder enforces employee-release status eligibility' `
    (Has $employee 'Get-MahodEmployeeReleaseEligibility')
Check 'employee builder validates exact hash-keyed live acceptance' `
    (Has $employee 'Test-MahodLiveAcceptanceAttestation')
$eligibilityIndex = if ($employee) { $employee.IndexOf('Get-MahodEmployeeReleaseEligibility', [StringComparison]::Ordinal) } else { -1 }
$stageMutationIndex = if ($employee) { $employee.IndexOf('Remove-Item $stage', [StringComparison]::Ordinal) } else { -1 }
Check 'employee status gate runs before employee-stage mutation' `
    ($eligibilityIndex -ge 0 -and $stageMutationIndex -gt $eligibilityIndex) `
    "gate-index=$eligibilityIndex mutation-index=$stageMutationIndex"
Check 'employee builder enforces exactly two files' (Has $employee 'exactly 2 files')
Check 'employee builder reads canonical path' (Has $employee 'canonical_employee_package')
Check 'employee builder keeps Defender evidence outside the exact two-file ZIP' `
    ((Has $employee 'defender_employee_\$Version\.json') -and
     (Has $employee 'exactly 2 files') -and (Has $employee 'TargetPath @\(\$setup, \$zipPath\)'))

$builders = @(
    'installer\build-setup.ps1', 'runtime-gate\build-runtime-gate.ps1',
    'nataly\build-guide.ps1', 'nataly\build-nataly.ps1',
    'nataly\build-client-zip.py', 'nataly\build-employee-zip.py',
    'handoff\build-full-handoff.ps1'
)
foreach ($relative in $builders) {
    $value = Text $relative
    Check "$relative has no machine-specific ephemeral dependency" `
        (-not (Has $value '(?i)AppData[\\/]Local[\\/]Temp|claude_desktop|\\\.claude'))
}
$handoffBuilder = Text 'handoff\build-full-handoff.ps1'
Check 'full handoff requires complete candidate identity and all release receipts' `
    ((Has $handoffBuilder 'Test-CandidateIdentity\.ps1') -and
     (Has $handoffBuilder 'Candidate-identity gate failed') -and
     (Has $handoffBuilder 'installer_manifest\.json') -and
     (Has $handoffBuilder 'package_manifest\.json') -and
     (Has $handoffBuilder 'Setup Defender receipt') -and
     (Has $handoffBuilder 'Employee Defender receipt') -and
     (Has $handoffBuilder 'Assert-Sha256Sidecar \$natalyZip'))
$handoffGateIndex = if ($handoffBuilder) { $handoffBuilder.IndexOf('Candidate-identity gate failed', [StringComparison]::Ordinal) } else { -1 }
$handoffStageMutationIndex = if ($handoffBuilder) { $handoffBuilder.IndexOf('Remove-Item $stage', [StringComparison]::Ordinal) } else { -1 }
Check 'full handoff gate runs before handoff-stage mutation' `
    ($handoffGateIndex -ge 0 -and $handoffStageMutationIndex -gt $handoffGateIndex) `
    "gate-index=$handoffGateIndex mutation-index=$handoffStageMutationIndex"

$rules = @(
    @{ P='docs\civil-delivery\RUNTIME_GATE_STATUS.md'; R=@(
        "(?m)^\*\*Package revision:\*\* $ep\s*$",
        "(?m)^\*\*MahodAI platform version:\*\* $ef\s*$",
        "(?m)^\*\*Current candidate GUI status:\*\* ``$es``\s*$") },
    @{ P='handoff\00_READ_ME_FIRST.md'; R=@(
        "(?m)^\*\*Version:\*\* $ep\s*$", "(?m)^\*\*Platform assembly:\*\* $ef\s*$",
        "(?m)^\*\*Current candidate GUI status:\*\* ``$es``\s*$") },
    @{ P='handoff\01_FINAL_STATUS.md'; R=@(
        "(?m)^# Candidate status - Mahod Civil Delivery $ep\s*$",
        "(?m)^\*\*GUI acceptance for this candidate:\*\* ``$es``\s*$") },
    @{ P='nataly\docs\01_START_HERE_HE.md'; R=@(
        "(?m)^\*\*[^:\r\n]+:\*\* $ep\s*$", "Mahod_Civil_Delivery_Setup_$ep\.exe") },
    @{ P='nataly\docs\04_KNOWN_LIMITATIONS_HE.md'; R=@("(?m)^# [^\r\n]+ $ep\s*$") },
    @{ P='runtime-gate\ARTHUR_10_MIN_GUI_GATE_HE.md'; R=@(
        "(?m)^\*\*[^\r\n]+$ep[^\r\n]+$ef\s*$",
        "(?m)^> \*\*[^:\r\n]+:\*\* ``$es``") }
)
foreach ($rule in $rules) {
    $value = Text $rule.P
    foreach ($pattern in $rule.R) { Check "$($rule.P) is numerically current" (Has $value $pattern) $pattern }
}

$guide = Text 'nataly\guide\GUIDE_HE.html'
Check 'guide derives package revision from placeholder' (Has $guide '\{\{PACKAGE_VERSION\}\}')
Check 'guide declares section-source contract' `
    (Has $guide 'RELEASE_ASSERT: sections=EG\+selected-design; foreign-surfaces=excluded')
Check 'guide declares approval-required mappings' (Has $guide 'mappings=approval-required')
Check 'guide declares screenshots historical' (Has $guide 'screenshots=historical')
$guideBuilder = Text 'nataly\build-guide.ps1'
$pdfValidator = Text 'nataly\PdfValidation.ps1'
Check 'guide builder fails on Edge timeout/nonzero and validates final PDF' `
    ((Has $guideBuilder 'WaitForExit\(90000\)') -and (Has $guideBuilder 'ExitCode -ne 0') -and
     (Has $guideBuilder 'Test-MahodGuidePdf'))
Check 'PDF validator requires structure and renders a real page when Poppler is available' `
    ((Has $pdfValidator 'PDF header is missing') -and (Has $pdfValidator 'PDF EOF marker') -and
     (Has $pdfValidator 'PDF contains no verifiable page') -and
     (Has $pdfValidator 'pdftoppm.exe') -and (Has $pdfValidator 'Rendered first page is not a valid PNG stream'))

Write-Host ''
if ($failures) { Write-Host "RELEASE METADATA GATE FAILED: $failures check(s)."; exit 1 }
Write-Host 'RELEASE METADATA GATE PASSED.'
exit 0
