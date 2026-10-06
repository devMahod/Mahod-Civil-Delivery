<# Pure TEMP-fixture self-tests for release status and hash-keyed acceptance policy. #>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $repo 'release\ReleaseGatePolicy.ps1')
$failures = 0

function Check([string]$Name, [bool]$Pass, [string]$Detail = '') {
    Write-Host ("{0,-4}  {1}{2}" -f $(if ($Pass) { 'PASS' } else { 'FAIL' }), $Name,
        $(if ($Detail) { "  [$Detail]" } else { '' }))
    if (-not $Pass) { $script:failures++ }
}
function Sha([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Write-FixtureText([string]$Root, [string]$Relative, [string]$Value) {
    $path = Join-Path $Root ($Relative.Replace('/', '\'))
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    Set-Content -LiteralPath $path -Value $Value -Encoding utf8
    return $path
}
function Write-FixtureBytes([string]$Root, [string]$Relative, [byte[]]$Value) {
    $path = Join-Path $Root ($Relative.Replace('/', '\'))
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [System.IO.File]::WriteAllBytes($path, $Value)
    return $path
}
function New-FixtureRuntimeManifest {
    param(
        [string]$RunId,
        [string]$Feature,
        [string]$Operation,
        [string]$Status,
        [string]$ProfileId,
        [string]$ProfileHash,
        [string]$PluginSha,
        [DateTimeOffset]$Started,
        [DateTimeOffset]$Completed,
        [System.Collections.IDictionary]$Artifacts,
        [string]$InputPath,
        [string]$InputHash,
        [string]$Scope = $null
    )
    $inputHashes = [ordered]@{}
    $inputHashes[$InputPath] = $InputHash
    return [ordered]@{
        schema_version = 2
        run_id = $RunId
        feature = $Feature
        operation = $Operation
        scope = $Scope
        selected_record_id = $null
        started_at_utc = $Started.ToString('o')
        completed_at_utc = $Completed.ToString('o')
        user = 'fixture-user'
        machine = 'fixture-machine'
        civil_version = 'fixture-2027'
        plugin_git_sha = 'fixture-git'
        plugin_build_version = '9.9.9.0'
        plugin_package_revision = '9.9.9'
        plugin_assembly_sha256 = $PluginSha
        project_profile_id = $ProfileId
        project_profile_hash = $ProfileHash
        input_drawings = @($InputPath)
        input_hashes = @($InputHash)
        input_hashes_by_path = $inputHashes
        result_status = $Status
        record_counts = [ordered]@{ total = 1 }
        finding_counts = [ordered]@{}
        artifacts = @($Artifacts.Keys)
        artifact_hashes = $Artifacts
    }
}

$pending = [pscustomobject]@{
    package_revision = '9.9.9'; platform_version = '9.9.9.0'
    civil_2027_status = 'COMPILED_GUI_ACCEPTANCE_PENDING'
    civil_2026_status = 'COMPILED_GUI_ACCEPTANCE_PENDING'
}
$acceptedCompileOnly = [pscustomobject]@{
    package_revision = '9.9.9'; platform_version = '9.9.9.0'
    civil_2027_status = 'COMPILED_GUI_ACCEPTED'
    civil_2026_status = 'COMPILED_ONLY_GUI_NOT_TESTED'
    last_live_candidate = '9.9.9'; last_live_date = '2026-09-01'
}
$accepted2027Pending2026 = [pscustomobject]@{
    package_revision = '9.9.9'; platform_version = '9.9.9.0'
    civil_2027_status = 'COMPILED_GUI_ACCEPTED'
    civil_2026_status = 'COMPILED_GUI_ACCEPTANCE_PENDING'
    last_live_candidate = '9.9.9'; last_live_date = '2026-09-01'
}
$invalid2027 = [pscustomobject]@{
    package_revision = '9.9.9'; platform_version = '9.9.9.0'
    civil_2027_status = 'COMPILED_ONLY_GUI_NOT_TESTED'
    civil_2026_status = 'COMPILED_ONLY_GUI_NOT_TESTED'
    last_live_candidate = '9.9.9'; last_live_date = '2026-09-01'
}

$expectedRequiredChecks = @(
    'civil_process_healthy',
    'sections_selected_record_ready',
    'sections_selected_apply_committed',
    'sections_selected_verify_passed',
    'sections_selected_replan_idempotent',
    'sections_manual_reuse_preserved',
    'sections_existing_and_design_lines_verified',
    'sections_full_widths_and_strips_verified',
    'sections_signed_slopes_verified',
    'sections_single_datum_verified',
    'sections_office_blocks_verified',
    'sections_traffic_direction_verified',
    'sections_utilities_and_row_verified',
    'sections_reference_plot_verified',
    'estimate_source_scope_verified',
    'estimate_pricebook_identity_verified',
    'estimate_earthworks_decision_verified',
    'estimate_duplicates_and_noise_reviewed',
    'estimate_positive_export_verified',
    'drawing_unchanged',
    'profile_unchanged'
)
$actualRequiredChecks = @(Get-MahodRequiredLiveAcceptanceChecks)
Check 'required live-check contract is exact and ordered' `
    (($actualRequiredChecks -join '|') -eq ($expectedRequiredChecks -join '|')) `
    (($actualRequiredChecks) -join ', ')
Check 'legacy generic vehicle boolean is not the office-block acceptance contract' `
    ($actualRequiredChecks -notcontains 'vehicle_blocks_visually_verified')
$expectedEvidenceKinds = @(
    'screenshot_png',
    'sections_plan_json','sections_plan_manifest_json',
    'sections_apply_json','sections_apply_manifest_json',
    'sections_verify_json','sections_verify_manifest_json',
    'sections_replan_json','sections_replan_manifest_json',
    'estimate_scan_json',
    'estimate_build_json','estimate_build_manifest_json',
    'estimate_export_json','estimate_export_package_json','estimate_export_audit_json','estimate_export_xlsx',
    'installed_loaded_identity_json','bundle_winner_json'
)
$actualEvidenceKinds = @(Get-MahodRequiredLiveEvidenceKinds)
Check 'required live-evidence contract is exact and ordered' `
    (($actualEvidenceKinds -join '|') -eq ($expectedEvidenceKinds -join '|')) `
    (($actualEvidenceKinds) -join ', ')

Check 'pending/pending is a valid in-progress matrix' (Test-MahodReleaseStatusMatrix $pending).Valid
Check 'pending 2027 cannot produce an employee ZIP' (-not (Test-MahodEmployeeReleaseAllowed $pending))
Check 'pending candidate has PENDING documentation marker' `
    ((Get-MahodReleaseDisplayStatus $pending) -eq 'PENDING')
Check 'accepted 2027 with pending 2026 remains employee-blocked' `
    (-not (Test-MahodEmployeeReleaseAllowed $accepted2027Pending2026))
Check 'accepted 2027 plus explicit 2026 compile-only is employee-eligible' `
    (Test-MahodEmployeeReleaseAllowed $acceptedCompileOnly)
Check 'compile-only 2026 is explicit in documentation marker' `
    ((Get-MahodReleaseDisplayStatus $acceptedCompileOnly) -eq 'ACCEPTED_2027_2026_COMPILED_ONLY')
Check '2027 compile-only is not a valid status claim' `
    (-not (Test-MahodReleaseStatusMatrix $invalid2027).Valid)
$acceptedWrongCandidate = [pscustomobject]@{
    package_revision='9.9.9'; platform_version='9.9.9.0'
    civil_2027_status='COMPILED_GUI_ACCEPTED'; civil_2026_status='COMPILED_ONLY_GUI_NOT_TESTED'
    last_live_candidate='9.9.8'; last_live_date='2026-09-01'
}
Check 'accepted status cannot retain a stale last_live_candidate' `
    (-not (Test-MahodReleaseStatusMatrix $acceptedWrongCandidate).Valid)

$fixture = Join-Path $env:TEMP ('mahod_release_policy_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $fixture | Out-Null
try {
    $missing = Test-MahodLiveAcceptanceAttestation -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'ACCEPTED without an attestation is rejected' (-not $missing.Valid)

    $setupRelative = 'installer/out/Mahod_Civil_Delivery_Setup_9.9.9.exe'
    $componentRelatives = [ordered]@{
        civil_2027_plugin = 'installer/stage/payload/MahodAI.bundle/Contents/MahodAI.Civil3D.Plugin.dll'
        civil_2027_core = 'installer/stage/payload/MahodAI.bundle/Contents/MahodAI.CivilDelivery.Core.dll'
        civil_2026_plugin = 'installer/stage/payload/MahodAI.bundle/Contents/2026/MahodAI.Civil3D.Plugin.dll'
        civil_2026_core = 'installer/stage/payload/MahodAI.bundle/Contents/2026/MahodAI.CivilDelivery.Core.dll'
    }
    $setupPath = Write-FixtureText $fixture $setupRelative 'fixture setup bytes'
    $componentEntries = [ordered]@{}
    foreach ($key in $componentRelatives.Keys) {
        $relative = [string]$componentRelatives[$key]
        $path = Write-FixtureText $fixture $relative ("fixture bytes for $key")
        $componentEntries[$key] = [ordered]@{ relative_path = $relative; sha256 = (Sha $path) }
    }
    $fixtureBuilt = [DateTimeOffset]::UtcNow.AddHours(-2)
    $fixtureInstallerBuilt = $fixtureBuilt.AddMinutes(30)
    $fixtureAccepted = $fixtureBuilt.AddHours(1)
    [ordered]@{
        version='9.9.9'; platform_file_version='9.9.9.0'; built_utc=$fixtureBuilt.ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath `
        (Write-FixtureText $fixture 'installer/stage/build_manifest.json' '{}') -Encoding utf8
    [ordered]@{
        version='9.9.9'; platform_file_version='9.9.9.0'; built_utc=$fixtureInstallerBuilt.ToString('o')
        sha256=(Sha $setupPath); size_bytes=(Get-Item -LiteralPath $setupPath).Length
    } | ConvertTo-Json | Set-Content -LiteralPath `
        (Write-FixtureText $fixture 'installer/out/installer_manifest.json' '{}') -Encoding utf8

    $evidenceEntries = @()
    $evidenceRoot = 'evidence/live-acceptance/9.9.9'
    $pngRelative = "$evidenceRoot/overview.png"
    $pngPath = Write-FixtureBytes $fixture $pngRelative `
        ([Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII='))
    $evidenceEntries += [ordered]@{ kind='screenshot_png'; relative_path=$pngRelative; sha256=(Sha $pngPath) }

    $fixtureWinnerPath = 'C:\fixture\MahodAI.bundle'
    $fixtureLoadedHash = [string]$componentEntries.civil_2027_plugin.sha256
    $sectionProfileHash = ('1' * 64)
    $estimateProfileHash = $sectionProfileHash
    $estimateEffectiveHash = ('3' * 64)
    $drawingHash = ('4' * 64)
    $catalogHash = ('5' * 64)
    $itemFingerprint = ('6' * 64)
    $sectionSourceHash = ('7' * 64)
    $fixtureDrawing = 'C:\fixture\6422.dwg'
    $sectionProfileId = 'fixture-project'
    $estimateProfileId = $sectionProfileId
    $planRunId = 'sections-plan-20260902-103500-aaaaaaaa'
    $applyRunId = 'sections-apply-20260902-103700-bbbbbbbb'
    $verifyRunId = 'sections-verify-20260902-103900-cccccccc'
    $replanRunId = 'sections-plan-20260902-104100-dddddddd'
    $estimateRunId = 'estimate-extract-20260902-104300-eeeeeeee'
    $runtimeRoot = 'C:\fixture\runtime\runs'
    $sectionRecordId = 'SEC-1'
    $sectionReviewRecordId = 'SEC-2'
    $estimateRecordId = 'EST-1'

    $sectionPlanDoc = [ordered]@{
        schema_version=1; run_id=$planRunId; project_profile_id=$sectionProfileId
        project_profile_hash=$sectionProfileHash; source_drawing=$fixtureDrawing
        source_database_revision='drawing-revision-before'; source_unit_code=6; source_unit_name='Meters'
        external_sources=@([ordered]@{
            source_path='C:\fixture\cl.dwg'; source_name='cl.dwg'; sha256=$sectionSourceHash
            roles=@('cl'); source_chain='CL'; requires_live_database=$false
        })
        records=@(
            [ordered]@{ record_id=$sectionRecordId; status='Ready'; action='Create' },
            [ordered]@{ record_id=$sectionReviewRecordId; status='ReviewRequired'; action='ReviewRequired' }
        )
        findings=@(); status='ReviewRequired'
    }
    $sectionApplyDoc = [ordered]@{
        run_id=$applyRunId; scope='selected-record'; selected_record_id=$sectionRecordId
        records=@([ordered]@{
            record_id=$sectionRecordId; logical_key='fixture-key'; action_taken='Create'; status='Applied'
        })
        committed=$true; post_apply_database_revision='drawing-revision-after'
        status='Applied'; findings=@()
    }
    $sectionVerifyDoc = [ordered]@{
        run_id=$verifyRunId; scope='selected-record'; selected_record_id=$sectionRecordId
        records=@([ordered]@{
            record_id=$sectionRecordId; logical_key='fixture-key'; status='Verified'
            checks=@([ordered]@{ check='layout_non_overlap'; expected='true'; actual='true'; pass=$true })
        })
        status='Verified'; findings=@()
    }
    $sectionReplanDoc = [ordered]@{
        schema_version=1; run_id=$replanRunId; project_profile_id=$sectionProfileId
        project_profile_hash=$sectionProfileHash; source_drawing=$fixtureDrawing
        source_database_revision='drawing-revision-after'; source_unit_code=6; source_unit_name='Meters'
        external_sources=$sectionPlanDoc.external_sources
        records=@(
            [ordered]@{ record_id=$sectionRecordId; status='Ready'; action='Unchanged' },
            [ordered]@{ record_id=$sectionReviewRecordId; status='ReviewRequired'; action='ReviewRequired' }
        )
        findings=@(); status='ReviewRequired'
    }

    $estimateScanRecord = [ordered]@{
        schema_version=1; record_id=$estimateRecordId; project_profile_id=$estimateProfileId
        run_id=$estimateRunId
        source=[ordered]@{
            drawing='6422.dwg'; drawing_path=$fixtureDrawing; drawing_hash=$drawingHash
            database_revision='estimate-drawing-revision'; handle='ABCD'; layer='WATER'; entity_type='Polyline'
        }
        measurement=[ordered]@{ kind='length'; raw_value=10.0; raw_unit='m'; measured_value=10.0; measured_unit='m' }
        classification=[ordered]@{
            source_class='layer'; rule_key='layer:WATER|length'; candidate_catalog_code='01.001'
            approved_catalog_id='fixture-book'; approved_catalog_hash=$catalogHash
            approved_catalog_item_fingerprint=$itemFingerprint; mapping_approved_by='fixture-user'
            mapping_approved_at_utc=$fixtureInstallerBuilt.AddMinutes(3).ToString('o'); tags=@('water')
        }
        status='Ready'; findings=@()
    }
    $estimateScanDoc = [ordered]@{
        RunId=$estimateRunId; ProjectProfileId=$estimateProfileId
        ProfileSource='C:\fixture\profiles\fixture-estimate.project.json'; SourceDrawing=$fixtureDrawing
        ProjectProfileHash=$estimateProfileHash; ProjectProfileEffectiveHash=$estimateEffectiveHash
        DatabaseRevision='estimate-drawing-revision'; SourceDrawingHash=$drawingHash; SourceDbMod=0
        ExternalSources=@(); Records=@($estimateScanRecord); Findings=@(); ScannedEntities=1
        Status='Ready'; DiscoveryMode=$true
    }
    $estimateLine = [ordered]@{
        line_id='LINE-1'; record_id=$estimateRecordId; source_layer='WATER'; source_drawing='6422.dwg'
        source_drawing_path=$fixtureDrawing; source_drawing_hash=$drawingHash
        source_database_revision='estimate-drawing-revision'; source_handle='ABCD'
        source_entity_type='Polyline'; measurement_method='entity-length'; rule_key='layer:WATER|length'
        mapping_approved_by='fixture-user'; mapping_approved_at_utc=$fixtureInstallerBuilt.AddMinutes(3).ToString('o')
        approved_catalog_id='fixture-book'; approved_catalog_hash=$catalogHash
        approved_catalog_item_fingerprint=$itemFingerprint; catalog_code='01.001'; description='Reviewed water work'
        unit='m'; raw_quantity=10.0; adjustments=@(); boq_quantity=10.0; price=10.0
        price_status='Priced'; price_book_id='fixture-book'; price_decision_source=$catalogHash
        total=100.0; included_in_totals=$true; status='Ready'; findings=@()
    }
    $estimateBuildDoc = [ordered]@{
        schema_version=2; run_id=$estimateRunId; project_profile_id=$estimateProfileId
        project_profile_hash=$estimateProfileHash; project_profile_hash_kind='source-file-sha256'
        project_profile_effective_hash=$estimateEffectiveHash; price_book_id='fixture-book'
        price_book_hash=$catalogHash; source_scope_policy='discover-all'; xref_policy='include-xrefs'
        scope_notice='HOST + XREF — RECURSIVE VERIFIED SOURCES'; source_snapshot_kind='civil-live-saved'
        source_drawing_path=$fixtureDrawing; source_drawing_hash=$drawingHash
        source_database_revision='estimate-drawing-revision'; source_dbmod=0
        external_sources=@(); exclusions=@()
        lines=@($estimateLine); findings=@()
        clean_total=100.0; excluded_line_count=0; status='Ready'
    }

    $xlsxRelative = "$evidenceRoot/estimate.xlsx"
    $xlsxPath = Join-Path $fixture ($xlsxRelative.Replace('/', '\'))
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $xlsxArchive = [IO.Compression.ZipFile]::Open($xlsxPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($part in @('[Content_Types].xml','xl/workbook.xml','xl/worksheets/sheet1.xml')) {
            $entry = $xlsxArchive.CreateEntry($part)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('<fixture/>') } finally { $writer.Dispose() }
        }
    } finally { $xlsxArchive.Dispose() }
    $xlsxSha = Sha $xlsxPath
    $auditDoc = [ordered]@{
        schema_version=2; run_id=$estimateRunId; generated_at_utc=$fixtureInstallerBuilt.AddMinutes(15).ToString('o')
        package_manifest='estimate.package.json'; workbook_file='estimate.xlsx'; workbook_sha256=$xlsxSha
        project_profile_id=$estimateProfileId; project_profile_hash=$estimateProfileHash
        project_profile_hash_kind='source-file-sha256'; project_profile_effective_hash=$estimateEffectiveHash
        price_book_id='fixture-book'; price_book_hash=$catalogHash; source_snapshot_kind='civil-live-saved'
        source_drawing_path=$fixtureDrawing; source_drawing_hash=$drawingHash
        source_database_revision='estimate-drawing-revision'; source_dbmod=0
        source_scope_policy='discover-all'; xref_policy='include-xrefs'
        scope_notice='HOST + XREF — RECURSIVE VERIFIED SOURCES'; external_sources=@()
        clean_total=100.0; boq_groups=@(); exclusions=@(); excluded_line_count=0
        status='Ready'; lines=@($estimateLine); findings=@()
    }
    $auditRelative = "$evidenceRoot/estimate.audit.json"
    $auditPath = Write-FixtureText $fixture $auditRelative ($auditDoc | ConvertTo-Json -Depth 20)
    $auditSha = Sha $auditPath
    $packageDoc = [ordered]@{
        schema_version=1; package_kind='mahod-estimate-export'; run_id=$estimateRunId
        generated_at_utc=$fixtureInstallerBuilt.AddMinutes(15).ToString('o')
        workbook=[ordered]@{ file='estimate.xlsx'; sha256=$xlsxSha }
        audit=[ordered]@{ file='estimate.audit.json'; sha256=$auditSha }
        profile=[ordered]@{ id=$estimateProfileId; source_sha256=$estimateProfileHash; source_hash_kind='source-file-sha256'; effective_sha256=$estimateEffectiveHash }
        catalog=[ordered]@{ id='fixture-book'; sha256=$catalogHash }
        drawing=[ordered]@{ path=$fixtureDrawing; sha256=$drawingHash; live_revision='estimate-drawing-revision'; dbmod=0; snapshot_kind='civil-live-saved' }
        external_sources=@()
    }
    $packageRelative = "$evidenceRoot/estimate.package.json"
    $packagePath = Write-FixtureText $fixture $packageRelative ($packageDoc | ConvertTo-Json -Depth 20)
    $packageSha = Sha $packagePath
    $originalXlsxPath = 'C:\fixture\exports\estimate.xlsx'
    $originalAuditPath = 'C:\fixture\exports\estimate.audit.json'
    $originalPackagePath = 'C:\fixture\exports\estimate.package.json'
    $estimateExportDoc = [ordered]@{
        XlsxPath=$originalXlsxPath; AuditPath=$originalAuditPath; ManifestPath=$originalPackagePath
        XlsxHash=$xlsxSha; AuditHash=$auditSha; ManifestHash=$packageSha
    }

    $documentSpecs = [ordered]@{
        sections_plan_json = @{ File='section_plan.json'; Json=$sectionPlanDoc }
        sections_apply_json = @{ File='apply_result.json'; Json=$sectionApplyDoc }
        sections_verify_json = @{ File='verify_result.json'; Json=$sectionVerifyDoc }
        sections_replan_json = @{ File='section_replan.json'; Json=$sectionReplanDoc }
        estimate_scan_json = @{ File='estimate_scan.json'; Json=$estimateScanDoc }
        estimate_build_json = @{ File='estimate_result.json'; Json=$estimateBuildDoc }
        estimate_export_json = @{ File='export_result.json'; Json=$estimateExportDoc }
    }
    $documentPaths = @{}
    foreach ($kind in $documentSpecs.Keys) {
        $relative = "$evidenceRoot/$($documentSpecs[$kind].File)"
        $path = Write-FixtureText $fixture $relative ($documentSpecs[$kind].Json | ConvertTo-Json -Depth 20)
        $documentPaths[$kind] = $path
        $evidenceEntries += [ordered]@{ kind=$kind; relative_path=$relative; sha256=(Sha $path) }
    }
    $evidenceEntries += [ordered]@{ kind='estimate_export_package_json'; relative_path=$packageRelative; sha256=$packageSha }
    $evidenceEntries += [ordered]@{ kind='estimate_export_audit_json'; relative_path=$auditRelative; sha256=$auditSha }
    $evidenceEntries += [ordered]@{ kind='estimate_export_xlsx'; relative_path=$xlsxRelative; sha256=$xlsxSha }

    $planRuntimePath = "$runtimeRoot\$planRunId\section_plan.json"
    $applyRuntimePath = "$runtimeRoot\$applyRunId\apply_result.json"
    $verifyRuntimePath = "$runtimeRoot\$verifyRunId\verify_result.json"
    $replanRuntimePath = "$runtimeRoot\$replanRunId\section_plan.json"
    $estimateScanRuntimePath = "$runtimeRoot\$estimateRunId\estimate_scan.json"
    $estimateBuildRuntimePath = "$runtimeRoot\$estimateRunId\estimate_result.json"
    $estimateExportRuntimePath = "$runtimeRoot\$estimateRunId\export_result.json"

    $manifestSpecs = [ordered]@{}
    $artifacts = [ordered]@{}; $artifacts[$planRuntimePath] = Sha $documentPaths.sections_plan_json
    $manifestSpecs.sections_plan_manifest_json = @{
        File='section_plan_run_manifest.json'; Json=(New-FixtureRuntimeManifest $planRunId 'sections' 'plan' 'ReviewRequired' $sectionProfileId $sectionProfileHash $fixtureLoadedHash $fixtureInstallerBuilt.AddMinutes(1) $fixtureInstallerBuilt.AddMinutes(2) $artifacts $fixtureDrawing $drawingHash)
    }
    $artifacts = [ordered]@{}; $artifacts[$planRuntimePath] = Sha $documentPaths.sections_plan_json; $artifacts[$applyRuntimePath] = Sha $documentPaths.sections_apply_json
    $manifestSpecs.sections_apply_manifest_json = @{
        File='section_apply_run_manifest.json'; Json=(New-FixtureRuntimeManifest $applyRunId 'sections' 'apply-selected' 'Applied' $sectionProfileId $sectionProfileHash $fixtureLoadedHash $fixtureInstallerBuilt.AddMinutes(3) $fixtureInstallerBuilt.AddMinutes(4) $artifacts $fixtureDrawing $drawingHash 'selected-record')
    }
    $artifacts = [ordered]@{}; $artifacts[$planRuntimePath] = Sha $documentPaths.sections_plan_json; $artifacts[$applyRuntimePath] = Sha $documentPaths.sections_apply_json; $artifacts[$verifyRuntimePath] = Sha $documentPaths.sections_verify_json
    $manifestSpecs.sections_verify_manifest_json = @{
        File='section_verify_run_manifest.json'; Json=(New-FixtureRuntimeManifest $verifyRunId 'sections' 'verify-selected' 'Verified' $sectionProfileId $sectionProfileHash $fixtureLoadedHash $fixtureInstallerBuilt.AddMinutes(5) $fixtureInstallerBuilt.AddMinutes(6) $artifacts $fixtureDrawing $drawingHash 'selected-record')
    }
    $artifacts = [ordered]@{}; $artifacts[$replanRuntimePath] = Sha $documentPaths.sections_replan_json
    $manifestSpecs.sections_replan_manifest_json = @{
        File='section_replan_run_manifest.json'; Json=(New-FixtureRuntimeManifest $replanRunId 'sections' 'plan' 'ReviewRequired' $sectionProfileId $sectionProfileHash $fixtureLoadedHash $fixtureInstallerBuilt.AddMinutes(7) $fixtureInstallerBuilt.AddMinutes(8) $artifacts $fixtureDrawing $drawingHash)
    }
    $artifacts = [ordered]@{}; $artifacts[$estimateScanRuntimePath] = Sha $documentPaths.estimate_scan_json; $artifacts[$estimateBuildRuntimePath] = Sha $documentPaths.estimate_build_json
    $artifacts[$estimateExportRuntimePath] = Sha $documentPaths.estimate_export_json
    $artifacts[$originalXlsxPath] = $xlsxSha
    $artifacts[$originalAuditPath] = $auditSha
    $artifacts[$originalPackagePath] = $packageSha
    $manifestSpecs.estimate_build_manifest_json = @{
        File='estimate_build_run_manifest.json'; Json=(New-FixtureRuntimeManifest $estimateRunId 'estimate' 'export' 'Ready' $estimateProfileId $estimateProfileHash $fixtureLoadedHash $fixtureInstallerBuilt.AddMinutes(9) $fixtureInstallerBuilt.AddMinutes(16) $artifacts $fixtureDrawing $drawingHash)
    }
    $manifestPaths = @{}
    foreach ($kind in $manifestSpecs.Keys) {
        $relative = "$evidenceRoot/$($manifestSpecs[$kind].File)"
        $path = Write-FixtureText $fixture $relative ($manifestSpecs[$kind].Json | ConvertTo-Json -Depth 20)
        $manifestPaths[$kind] = $path
        $evidenceEntries += [ordered]@{ kind=$kind; relative_path=$relative; sha256=(Sha $path) }
    }

    $loadedEvidenceRelative = "$evidenceRoot/installed_loaded_identity.json"
    $loadedEvidencePath = Write-FixtureText $fixture $loadedEvidenceRelative (@{
        loaded_plugin_binaries=@(@{
            civil_host_year=2027; path="$fixtureWinnerPath\Contents\MahodAI.Civil3D.Plugin.dll"
            sha256=$fixtureLoadedHash; file_version='9.9.9.0'
        })
    } | ConvertTo-Json -Depth 10)
    $evidenceEntries += [ordered]@{ kind='installed_loaded_identity_json'; relative_path=$loadedEvidenceRelative; sha256=(Sha $loadedEvidencePath) }
    $winnerRelative = "$evidenceRoot/bundle_winner.json"
    $winnerPath = Write-FixtureText $fixture $winnerRelative (@{
        active_bundle_count=1; winner_path=$fixtureWinnerPath; receipt_path=$fixtureWinnerPath
        receipt_matches_winner=$true; loaded_matches_winner=$true
        candidates=@(@{
            active=$true; priority=0; path=$fixtureWinnerPath; package_revision='9.9.9'
            plugin_2027_sha256=$fixtureLoadedHash
            plugin_2026_sha256=[string]$componentEntries.civil_2026_plugin.sha256
        })
    } | ConvertTo-Json -Depth 10)
    $evidenceEntries += [ordered]@{ kind='bundle_winner_json'; relative_path=$winnerRelative; sha256=(Sha $winnerPath) }
    $attestationRelative = Get-MahodAcceptanceAttestationRelativePath -PackageRevision '9.9.9' -HostYear 2027
    $attestationPath = Join-Path $fixture ($attestationRelative.Replace('/', '\'))
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $attestationPath) | Out-Null
    $acceptanceChecks = [ordered]@{}
    $checkEvidence = [ordered]@{}
    foreach ($checkName in $expectedRequiredChecks) {
        $acceptanceChecks[$checkName] = $true
        $checkEvidence[$checkName] = @(
            foreach ($kind in @(Get-MahodRequiredEvidenceKindsForLiveCheck -CheckName $checkName)) {
                $entry = $evidenceEntries | Where-Object { $_.kind -eq $kind } | Select-Object -First 1
                if ($entry) { [string]$entry.relative_path }
            }
        )
    }
    $attestation = [ordered]@{
        schema_version = 2
        verdict = 'ACCEPTED'
        package_revision = '9.9.9'
        platform_version = '9.9.9.0'
        civil_host_year = 2027
        accepted_utc = $fixtureAccepted.ToString("yyyy-MM-ddTHH:mm:ss'Z'")
        setup = [ordered]@{
            relative_path = $setupRelative
            sha256 = (Sha $setupPath)
            size_bytes = (Get-Item -LiteralPath $setupPath).Length
        }
        components = $componentEntries
        evidence_files = $evidenceEntries
        checks = $acceptanceChecks
        check_evidence = $checkEvidence
    }
    $attestation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $attestationPath -Encoding utf8

    $valid = Test-MahodLiveAcceptanceAttestation -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'exact setup, DLLs and semantic manifest-backed chains satisfy acceptance' `
        $valid.Valid (($valid.Errors) -join ' | ')

    # The former acceptance baseline (Failed + no exported package) must never
    # qualify an employee release, even with every attestation boolean set true.
    $savedPositiveEntries = $attestation.evidence_files
    $attestation.evidence_files = @($savedPositiveEntries | Where-Object { $_.kind -notlike 'estimate_export_*' })
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $missingPositiveExport = Test-MahodLiveAcceptanceAttestation -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'negative-only/no-export evidence cannot authorize employee release' `
        (-not $missingPositiveExport.Valid -and $missingPositiveExport.Errors -contains 'Required live evidence kind is missing: estimate_export_xlsx')
    $attestation.evidence_files = $savedPositiveEntries
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8

    # A coherent estimate chain still cannot be spliced onto section evidence from
    # another project-profile revision.
    $profileSpliceScanEntry = $attestation.evidence_files |
        Where-Object { $_.kind -eq 'estimate_scan_json' } | Select-Object -First 1
    $profileSpliceBuildEntry = $attestation.evidence_files |
        Where-Object { $_.kind -eq 'estimate_build_json' } | Select-Object -First 1
    $profileSpliceManifestEntry = $attestation.evidence_files |
        Where-Object { $_.kind -eq 'estimate_build_manifest_json' } | Select-Object -First 1
    $savedProfileSpliceScan = [System.IO.File]::ReadAllText($documentPaths.estimate_scan_json, [System.Text.UTF8Encoding]::new($false))
    $savedProfileSpliceScanBytes = [System.IO.File]::ReadAllBytes($documentPaths.estimate_scan_json)
    $savedProfileSpliceBuild = [System.IO.File]::ReadAllText($documentPaths.estimate_build_json, [System.Text.UTF8Encoding]::new($false))
    $savedProfileSpliceBuildBytes = [System.IO.File]::ReadAllBytes($documentPaths.estimate_build_json)
    $savedProfileSpliceManifest = [System.IO.File]::ReadAllText($manifestPaths.estimate_build_manifest_json, [System.Text.UTF8Encoding]::new($false))
    $savedProfileSpliceManifestBytes = [System.IO.File]::ReadAllBytes($manifestPaths.estimate_build_manifest_json)
    $otherProfileId = 'fixture-profile-other'
    $otherProfileHash = ('7' * 64)
    $profileSpliceScan = $savedProfileSpliceScan | ConvertFrom-Json
    $profileSpliceScan.ProjectProfileId = $otherProfileId
    $profileSpliceScan.ProjectProfileHash = $otherProfileHash
    $profileSpliceScan.Records[0].project_profile_id = $otherProfileId
    $profileSpliceScan | ConvertTo-Json -Depth 20 | Set-Content `
        -LiteralPath $documentPaths.estimate_scan_json -Encoding utf8
    $profileSpliceBuild = $savedProfileSpliceBuild | ConvertFrom-Json
    $profileSpliceBuild.project_profile_id = $otherProfileId
    $profileSpliceBuild.project_profile_hash = $otherProfileHash
    $profileSpliceBuild | ConvertTo-Json -Depth 20 | Set-Content `
        -LiteralPath $documentPaths.estimate_build_json -Encoding utf8
    $profileSpliceScanEntry.sha256 = Sha $documentPaths.estimate_scan_json
    $profileSpliceBuildEntry.sha256 = Sha $documentPaths.estimate_build_json
    $profileSpliceManifest = $savedProfileSpliceManifest | ConvertFrom-Json
    $profileSpliceManifest.project_profile_id = $otherProfileId
    $profileSpliceManifest.project_profile_hash = $otherProfileHash
    ($profileSpliceManifest.artifact_hashes.PSObject.Properties |
        Where-Object { $_.Name -eq $estimateScanRuntimePath } | Select-Object -First 1).Value = $profileSpliceScanEntry.sha256
    ($profileSpliceManifest.artifact_hashes.PSObject.Properties |
        Where-Object { $_.Name -eq $estimateBuildRuntimePath } | Select-Object -First 1).Value = $profileSpliceBuildEntry.sha256
    $profileSpliceManifest | ConvertTo-Json -Depth 20 | Set-Content `
        -LiteralPath $manifestPaths.estimate_build_manifest_json -Encoding utf8
    $profileSpliceManifestEntry.sha256 = Sha $manifestPaths.estimate_build_manifest_json
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $crossProfileEvidence = Test-MahodLiveAcceptanceAttestation `
        -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'hash-coherent section and estimate chains from different profiles are rejected' `
        (-not $crossProfileEvidence.Valid -and $crossProfileEvidence.Errors -contains `
            'Functional evidence: sections and estimate evidence do not use the same exact project profile.') `
        (($crossProfileEvidence.Errors) -join ' | ')
    [System.IO.File]::WriteAllBytes($documentPaths.estimate_scan_json, $savedProfileSpliceScanBytes)
    [System.IO.File]::WriteAllBytes($documentPaths.estimate_build_json, $savedProfileSpliceBuildBytes)
    [System.IO.File]::WriteAllBytes($manifestPaths.estimate_build_manifest_json, $savedProfileSpliceManifestBytes)
    $profileSpliceScanEntry.sha256 = Sha $documentPaths.estimate_scan_json
    $profileSpliceBuildEntry.sha256 = Sha $documentPaths.estimate_build_json
    $profileSpliceManifestEntry.sha256 = Sha $manifestPaths.estimate_build_manifest_json
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8

    # A stale runtime manifest cannot be made current merely by copying it into the
    # candidate evidence directory and re-hashing the copy in the attestation.
    $planManifestEntry = $attestation.evidence_files |
        Where-Object { $_.kind -eq 'sections_plan_manifest_json' } | Select-Object -First 1
    $savedPlanManifestText = [System.IO.File]::ReadAllText($manifestPaths.sections_plan_manifest_json, [System.Text.UTF8Encoding]::new($false))
    $savedPlanManifestTextBytes = [System.IO.File]::ReadAllBytes($manifestPaths.sections_plan_manifest_json)
    $stalePlanManifest = $savedPlanManifestText | ConvertFrom-Json
    $stalePlanManifest.plugin_package_revision = '9.9.8'
    $stalePlanManifest | ConvertTo-Json -Depth 20 | Set-Content `
        -LiteralPath $manifestPaths.sections_plan_manifest_json -Encoding utf8
    $planManifestEntry.sha256 = Sha $manifestPaths.sections_plan_manifest_json
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $staleCandidateManifest = Test-MahodLiveAcceptanceAttestation `
        -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'a hash-valid manifest emitted by an older package is rejected' `
        (-not $staleCandidateManifest.Valid -and @($staleCandidateManifest.Errors | Where-Object {
            $_ -eq 'Functional evidence: sections PLAN manifest was not emitted by the exact accepted package/DLL.'
        }).Count -eq 1) (($staleCandidateManifest.Errors) -join ' | ')
    [System.IO.File]::WriteAllBytes($manifestPaths.sections_plan_manifest_json, $savedPlanManifestTextBytes)
    $planManifestEntry.sha256 = Sha $manifestPaths.sections_plan_manifest_json
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8

    # Even a re-hashed JSON plus a correspondingly re-hashed runtime manifest cannot
    # turn a failed read-back into Verified evidence.
    $verifyEntry = $attestation.evidence_files |
        Where-Object { $_.kind -eq 'sections_verify_json' } | Select-Object -First 1
    $verifyManifestEntry = $attestation.evidence_files |
        Where-Object { $_.kind -eq 'sections_verify_manifest_json' } | Select-Object -First 1
    $savedVerifyText = [System.IO.File]::ReadAllText($documentPaths.sections_verify_json, [System.Text.UTF8Encoding]::new($false))
    $savedVerifyTextBytes = [System.IO.File]::ReadAllBytes($documentPaths.sections_verify_json)
    $savedVerifyManifestText = [System.IO.File]::ReadAllText($manifestPaths.sections_verify_manifest_json, [System.Text.UTF8Encoding]::new($false))
    $savedVerifyManifestTextBytes = [System.IO.File]::ReadAllBytes($manifestPaths.sections_verify_manifest_json)
    $failedVerify = $savedVerifyText | ConvertFrom-Json
    $failedVerify.records[0].checks[0].pass = $false
    $failedVerify | ConvertTo-Json -Depth 20 | Set-Content `
        -LiteralPath $documentPaths.sections_verify_json -Encoding utf8
    $verifyEntry.sha256 = Sha $documentPaths.sections_verify_json
    $failedVerifyManifest = $savedVerifyManifestText | ConvertFrom-Json
    ($failedVerifyManifest.artifact_hashes.PSObject.Properties |
        Where-Object { $_.Name -eq $verifyRuntimePath } | Select-Object -First 1).Value = $verifyEntry.sha256
    $failedVerifyManifest | ConvertTo-Json -Depth 20 | Set-Content `
        -LiteralPath $manifestPaths.sections_verify_manifest_json -Encoding utf8
    $verifyManifestEntry.sha256 = Sha $manifestPaths.sections_verify_manifest_json
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $falseGreenVerify = Test-MahodLiveAcceptanceAttestation `
        -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 're-hashed VERIFY with one failed check cannot be a false green' `
        (-not $falseGreenVerify.Valid -and @($falseGreenVerify.Errors | Where-Object {
            $_ -like "Functional evidence: sections VERIFY record '$sectionRecordId' is not Verified*"
        }).Count -eq 1) (($falseGreenVerify.Errors) -join ' | ')
    [System.IO.File]::WriteAllBytes($documentPaths.sections_verify_json, $savedVerifyTextBytes)
    [System.IO.File]::WriteAllBytes($manifestPaths.sections_verify_manifest_json, $savedVerifyManifestTextBytes)
    $verifyEntry.sha256 = Sha $documentPaths.sections_verify_json
    $verifyManifestEntry.sha256 = Sha $manifestPaths.sections_verify_manifest_json
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8

    # A build result from another run remains stale even if the BUILD manifest is
    # maliciously updated to bind its new hash.
    $buildEntry = $attestation.evidence_files |
        Where-Object { $_.kind -eq 'estimate_build_json' } | Select-Object -First 1
    $buildManifestEntry = $attestation.evidence_files |
        Where-Object { $_.kind -eq 'estimate_build_manifest_json' } | Select-Object -First 1
    $savedBuildText = [System.IO.File]::ReadAllText($documentPaths.estimate_build_json, [System.Text.UTF8Encoding]::new($false))
    $savedBuildTextBytes = [System.IO.File]::ReadAllBytes($documentPaths.estimate_build_json)
    $savedBuildManifestText = [System.IO.File]::ReadAllText($manifestPaths.estimate_build_manifest_json, [System.Text.UTF8Encoding]::new($false))
    $savedBuildManifestTextBytes = [System.IO.File]::ReadAllBytes($manifestPaths.estimate_build_manifest_json)
    $failedBuild = $savedBuildText | ConvertFrom-Json
    $failedBuild.status = 'Failed'
    $failedBuild.excluded_line_count = 1
    $failedBuild.lines[0].status = 'ReviewRequired'
    $failedBuild.lines[0].included_in_totals = $false
    $failedBuild | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $documentPaths.estimate_build_json -Encoding utf8
    $buildEntry.sha256 = Sha $documentPaths.estimate_build_json
    $failedBuildManifest = $savedBuildManifestText | ConvertFrom-Json
    $failedBuildManifest.operation = 'build'
    $failedBuildManifest.result_status = 'Failed'
    ($failedBuildManifest.artifact_hashes.PSObject.Properties |
        Where-Object { $_.Name -eq $estimateBuildRuntimePath } | Select-Object -First 1).Value = $buildEntry.sha256
    $failedBuildManifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPaths.estimate_build_manifest_json -Encoding utf8
    $buildManifestEntry.sha256 = Sha $manifestPaths.estimate_build_manifest_json
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $failedButSafe = Test-MahodLiveAcceptanceAttestation -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'hash-coherent Failed BUILD plus refused export cannot qualify as usable estimate' `
        (-not $failedButSafe.Valid -and @($failedButSafe.Errors | Where-Object {
            $_ -like 'Functional evidence: estimate BUILD is not the same saved-live*'
        }).Count -gt 0) (($failedButSafe.Errors) -join ' | ')
    [System.IO.File]::WriteAllBytes($documentPaths.estimate_build_json, $savedBuildTextBytes)
    [System.IO.File]::WriteAllBytes($manifestPaths.estimate_build_manifest_json, $savedBuildManifestTextBytes)
    $buildEntry.sha256 = Sha $documentPaths.estimate_build_json
    $buildManifestEntry.sha256 = Sha $manifestPaths.estimate_build_manifest_json
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8

    $savedPackageText = [IO.File]::ReadAllText($packagePath, [Text.UTF8Encoding]::new($false))
    $savedPackageBytes = [IO.File]::ReadAllBytes($packagePath)
    $packageEntry = $attestation.evidence_files | Where-Object { $_.kind -eq 'estimate_export_package_json' } | Select-Object -First 1
    $wrongPackage = $savedPackageText | ConvertFrom-Json
    $wrongPackage.workbook.sha256 = ('f' * 64)
    $wrongPackage | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $packagePath -Encoding utf8
    $packageEntry.sha256 = Sha $packagePath
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $splicedExport = Test-MahodLiveAcceptanceAttestation -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'rehashed export package naming different workbook bytes is rejected' `
        (-not $splicedExport.Valid -and $splicedExport.Errors -contains 'Functional evidence: estimate EXPORT package is not bound to the exact live BUILD and exported files.')
    [IO.File]::WriteAllBytes($packagePath, $savedPackageBytes)
    $packageEntry.sha256 = Sha $packagePath
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8

    $crossRunBuild = $savedBuildText | ConvertFrom-Json
    $crossRunBuild.run_id = 'estimate-extract-20260831-080000-stale000'
    $crossRunBuild | ConvertTo-Json -Depth 20 | Set-Content `
        -LiteralPath $documentPaths.estimate_build_json -Encoding utf8
    $buildEntry.sha256 = Sha $documentPaths.estimate_build_json
    $crossRunManifest = [System.IO.File]::ReadAllText($manifestPaths.estimate_build_manifest_json, [System.Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
    ($crossRunManifest.artifact_hashes.PSObject.Properties |
            Where-Object { $_.Name -eq $estimateBuildRuntimePath } | Select-Object -First 1).Value = $buildEntry.sha256
    $crossRunManifest | ConvertTo-Json -Depth 20 | Set-Content `
        -LiteralPath $manifestPaths.estimate_build_manifest_json -Encoding utf8
    $buildManifestEntry.sha256 = Sha $manifestPaths.estimate_build_manifest_json
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $crossRunEvidence = Test-MahodLiveAcceptanceAttestation `
        -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'hash-coherent estimate artifacts from different run_ids are rejected' `
        (-not $crossRunEvidence.Valid -and @($crossRunEvidence.Errors | Where-Object {
            $_ -like 'Functional evidence: estimate BUILD is not the same saved-live*'
        }).Count -eq 1) (($crossRunEvidence.Errors) -join ' | ')
    [System.IO.File]::WriteAllBytes($documentPaths.estimate_build_json, $savedBuildTextBytes)
    [System.IO.File]::WriteAllBytes($manifestPaths.estimate_build_manifest_json, $savedBuildManifestTextBytes)
    $buildEntry.sha256 = Sha $documentPaths.estimate_build_json
    $buildManifestEntry.sha256 = Sha $manifestPaths.estimate_build_manifest_json
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8

    # Missing per-line source identity is allowed only as an explicit fail-closed
    # condition. Removing the structured blocker while preserving the exclusion
    # must not turn an untraceable line into acceptable evidence.
    $missingHashNoFinding = $savedBuildText | ConvertFrom-Json
    $missingHashNoFinding.lines[0].source_drawing_hash = $null
    $missingHashNoFinding.lines[0].findings = @()
    $missingHashNoFinding.lines[0] | Add-Member -NotePropertyName finding_refs `
        -NotePropertyValue @() -Force
    $missingHashNoFinding | ConvertTo-Json -Depth 20 | Set-Content `
        -LiteralPath $documentPaths.estimate_build_json -Encoding utf8
    $buildEntry.sha256 = Sha $documentPaths.estimate_build_json
    # PowerShell 7.5 turns ISO timestamps into DateTime unless -DateKind String is
    # given; Windows PowerShell 5.1 (the gate host on this machine) keeps them as
    # strings and does not know the parameter. Branch on capability, never on hope.
    $missingHashManifest = if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) {
        $savedBuildManifestText | ConvertFrom-Json -DateKind String
    } else {
        $savedBuildManifestText | ConvertFrom-Json
    }
    ($missingHashManifest.artifact_hashes.PSObject.Properties |
        Where-Object { $_.Name -eq $estimateBuildRuntimePath } | Select-Object -First 1).Value = $buildEntry.sha256
    $missingHashManifest | ConvertTo-Json -Depth 20 | Set-Content `
        -LiteralPath $manifestPaths.estimate_build_manifest_json -Encoding utf8
    $buildManifestEntry.sha256 = Sha $manifestPaths.estimate_build_manifest_json
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $missingHashWithoutBlocker = Test-MahodLiveAcceptanceAttestation `
        -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'blocked line missing source hash without a structured blocker is rejected' `
        (-not $missingHashWithoutBlocker.Valid -and $missingHashWithoutBlocker.Errors -contains `
            "Functional evidence: estimate BUILD line 'LINE-1' is neither export-ready nor safely excluded by a blocking status.") `
        (($missingHashWithoutBlocker.Errors) -join ' | ')
    [System.IO.File]::WriteAllBytes($documentPaths.estimate_build_json, $savedBuildTextBytes)
    [System.IO.File]::WriteAllBytes($manifestPaths.estimate_build_manifest_json, $savedBuildManifestTextBytes)
    $buildEntry.sha256 = Sha $documentPaths.estimate_build_json
    $buildManifestEntry.sha256 = Sha $manifestPaths.estimate_build_manifest_json
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8

    # A maliciously re-hashed BUILD cannot claim a green/exportable result for a
    # SCAN record that is still explicitly unmapped.
    $falseGreenBuild = $savedBuildText | ConvertFrom-Json
    $falseGreenBuild.status = 'Ready'
    $falseGreenBuild.excluded_line_count = 0
    $falseGreenBuild.clean_total = 100
    $falseGreenBuild.lines[0].status = 'Ready'
    $falseGreenBuild.lines[0].source_drawing_hash = $null
    $falseGreenBuild.lines[0].included_in_totals = $true
    $falseGreenBuild.lines[0].price_status = 'Priced'
    $falseGreenBuild.lines[0].price = 10
    $falseGreenBuild.lines[0].total = 100
    $falseGreenBuild.lines[0].catalog_code = '01.001'
    $falseGreenBuild.lines[0].mapping_approved_by = 'fixture-attacker'
    $falseGreenBuild.lines[0].mapping_approved_at_utc = $fixtureInstallerBuilt.AddMinutes(3).ToString('o')
    $falseGreenBuild.lines[0].approved_catalog_id = 'fixture-book'
    $falseGreenBuild.lines[0].approved_catalog_hash = $catalogHash
    $falseGreenBuild.lines[0].approved_catalog_item_fingerprint = $itemFingerprint
    $falseGreenBuild.lines[0].price_decision_source = $catalogHash
    $falseGreenBuild | ConvertTo-Json -Depth 20 | Set-Content `
        -LiteralPath $documentPaths.estimate_build_json -Encoding utf8
    $buildEntry.sha256 = Sha $documentPaths.estimate_build_json
    $falseGreenBuildManifest = $savedBuildManifestText | ConvertFrom-Json
    $falseGreenBuildManifest.result_status = 'Ready'
    ($falseGreenBuildManifest.artifact_hashes.PSObject.Properties |
        Where-Object { $_.Name -eq $estimateBuildRuntimePath } | Select-Object -First 1).Value = $buildEntry.sha256
    $falseGreenBuildManifest | ConvertTo-Json -Depth 20 | Set-Content `
        -LiteralPath $manifestPaths.estimate_build_manifest_json -Encoding utf8
    $buildManifestEntry.sha256 = Sha $manifestPaths.estimate_build_manifest_json
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $falseGreenEstimate = Test-MahodLiveAcceptanceAttestation `
        -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'Ready/included line missing source hash cannot hide behind a structured blocker' `
        (-not $falseGreenEstimate.Valid -and @($falseGreenEstimate.Errors | Where-Object {
            $_ -eq "Functional evidence: estimate BUILD line 'LINE-1' is neither export-ready nor safely excluded by a blocking status."
        }).Count -eq 1) (($falseGreenEstimate.Errors) -join ' | ')
    [System.IO.File]::WriteAllBytes($documentPaths.estimate_build_json, $savedBuildTextBytes)
    [System.IO.File]::WriteAllBytes($manifestPaths.estimate_build_manifest_json, $savedBuildManifestTextBytes)
    $buildEntry.sha256 = Sha $documentPaths.estimate_build_json
    $buildManifestEntry.sha256 = Sha $manifestPaths.estimate_build_manifest_json
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8

    # Candidate identity alone is insufficient: the functional run must happen after
    # publication of the exact setup, not on a previous installation.
    $savedPlanManifestText2 = [System.IO.File]::ReadAllText($manifestPaths.sections_plan_manifest_json, [System.Text.UTF8Encoding]::new($false))
    $savedPlanManifestText2Bytes = [System.IO.File]::ReadAllBytes($manifestPaths.sections_plan_manifest_json)
    $preInstallerPlan = $savedPlanManifestText2 | ConvertFrom-Json
    $preInstallerPlan.started_at_utc = $fixtureBuilt.AddMinutes(5).ToString('o')
    $preInstallerPlan.completed_at_utc = $fixtureBuilt.AddMinutes(6).ToString('o')
    $preInstallerPlan | ConvertTo-Json -Depth 20 | Set-Content `
        -LiteralPath $manifestPaths.sections_plan_manifest_json -Encoding utf8
    $planManifestEntry.sha256 = Sha $manifestPaths.sections_plan_manifest_json
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $preInstallerEvidence = Test-MahodLiveAcceptanceAttestation `
        -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'functional run completed before exact installer publication is rejected' `
        (-not $preInstallerEvidence.Valid -and @($preInstallerEvidence.Errors | Where-Object {
            $_ -eq 'Functional evidence: sections PLAN manifest predates the exact published setup installer.'
        }).Count -eq 1) (($preInstallerEvidence.Errors) -join ' | ')
    [System.IO.File]::WriteAllBytes($manifestPaths.sections_plan_manifest_json, $savedPlanManifestText2Bytes)
    $planManifestEntry.sha256 = Sha $manifestPaths.sections_plan_manifest_json
    $attestation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $attestationPath -Encoding utf8

    $afterAdversarial = Test-MahodLiveAcceptanceAttestation `
        -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'baseline is restored after adversarial stale-artifact tests' `
        $afterAdversarial.Valid (($afterAdversarial.Errors) -join ' | ')

    $attestation.accepted_utc = $fixtureBuilt.AddMinutes(15).ToString("yyyy-MM-ddTHH:mm:ss'Z'")
    $attestation | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $beforeSetupPublication = Test-MahodLiveAcceptanceAttestation -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'acceptance after compile but before exact setup publication is rejected' `
        (-not $beforeSetupPublication.Valid -and
         $beforeSetupPublication.Errors -contains 'accepted_utc predates the exact published setup installer.') `
        (($beforeSetupPublication.Errors) -join ' | ')
    $attestation.accepted_utc = $fixtureAccepted.ToString("yyyy-MM-ddTHH:mm:ss'Z'")
    $attestation | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $attestationPath -Encoding utf8

    $loadedDoc = [System.IO.File]::ReadAllText($loadedEvidencePath, [System.Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
    $loadedDoc.loaded_plugin_binaries[0].sha256 = ('a' * 64)
    $loadedDoc | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $loadedEvidencePath -Encoding utf8
    $loadedEntry = $attestation.evidence_files | Where-Object { $_.kind -eq 'installed_loaded_identity_json' } | Select-Object -First 1
    $loadedEntry.sha256 = Sha $loadedEvidencePath
    $attestation | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $wrongLoadedCandidate = Test-MahodLiveAcceptanceAttestation -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'a hash-valid receipt for the wrong loaded DLL still blocks acceptance' `
        (-not $wrongLoadedCandidate.Valid -and @($wrongLoadedCandidate.Errors | Where-Object {
            $_ -like 'Loaded Civil 2027 plugin does not match the exact attested DLL*'
        }).Count -gt 0) (($wrongLoadedCandidate.Errors) -join ' | ')
    $loadedDoc.loaded_plugin_binaries[0].sha256 = $fixtureLoadedHash
    $loadedDoc | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $loadedEvidencePath -Encoding utf8
    $loadedEntry.sha256 = Sha $loadedEvidencePath
    $attestation | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $attestationPath -Encoding utf8

    $attestation.accepted_utc = $fixtureAccepted.ToString('yyyy-MM-ddTHH:mm:ss+00:00')
    $attestation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $explicitZeroOffset = Test-MahodLiveAcceptanceAttestation -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'explicit +00:00 acceptance timestamp is portable across PowerShell versions' `
        $explicitZeroOffset.Valid (($explicitZeroOffset.Errors) -join ' | ')

    $attestation.accepted_utc = $fixtureAccepted.ToString('yyyy-MM-ddTHH:mm:ss')
    $attestation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $missingUtcOffset = Test-MahodLiveAcceptanceAttestation -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'acceptance timestamp without an explicit UTC offset is rejected' `
        (-not $missingUtcOffset.Valid -and
         $missingUtcOffset.Errors -contains 'accepted_utc must include the UTC (Z/+00:00) offset.') `
        (($missingUtcOffset.Errors) -join ' | ')

    $attestation.accepted_utc = $fixtureAccepted.ToString("yyyy-MM-ddTHH:mm:ss'Z'")
    $attestation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $attestationPath -Encoding utf8

    $newPromiseChecks = @(
        'civil_process_healthy',
        'sections_selected_record_ready',
        'sections_selected_apply_committed',
        'sections_selected_verify_passed',
        'sections_selected_replan_idempotent',
        'sections_manual_reuse_preserved',
        'sections_existing_and_design_lines_verified',
        'sections_full_widths_and_strips_verified',
        'sections_signed_slopes_verified',
        'sections_single_datum_verified',
        'sections_office_blocks_verified',
        'sections_traffic_direction_verified',
        'sections_utilities_and_row_verified',
        'sections_reference_plot_verified',
        'estimate_source_scope_verified',
        'estimate_pricebook_identity_verified',
        'estimate_earthworks_decision_verified',
        'estimate_duplicates_and_noise_reviewed',
        'estimate_positive_export_verified'
    )
    foreach ($checkName in $newPromiseChecks) {
        $attestation.checks[$checkName] = $false
        $attestation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $attestationPath -Encoding utf8
        $missingPromise = Test-MahodLiveAcceptanceAttestation `
            -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
        $expectedError = "Required live check is not true: $checkName"
        Check "false $checkName blocks acceptance" `
            (-not $missingPromise.Valid -and $missingPromise.Errors -contains $expectedError) `
            (($missingPromise.Errors) -join ' | ')
        $attestation.checks[$checkName] = $true
    }

    $attestation.checks['sections_office_blocks_verified'] = $false
    $attestation.checks['vehicle_blocks_visually_verified'] = $true
    $attestation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $legacyVehicleOnly = Test-MahodLiveAcceptanceAttestation `
        -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'legacy vehicle boolean cannot substitute for exact office-block verification' `
        (-not $legacyVehicleOnly.Valid -and
         $legacyVehicleOnly.Errors -contains 'Required live check is not true: sections_office_blocks_verified') `
        (($legacyVehicleOnly.Errors) -join ' | ')
    $attestation.checks['sections_office_blocks_verified'] = $true
    [void]$attestation.checks.Remove('vehicle_blocks_visually_verified')
    $attestation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $restored = Test-MahodLiveAcceptanceAttestation -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'restored exact live-check set satisfies acceptance' $restored.Valid (($restored.Errors) -join ' | ')

    $savedTypedMapping = $attestation.check_evidence['sections_selected_apply_committed']
    $attestation.check_evidence['sections_selected_apply_committed'] = @($pngRelative)
    $attestation | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $wrongTypedMapping = Test-MahodLiveAcceptanceAttestation -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'an unrelated evidence kind cannot substantiate a true live check' `
        (-not $wrongTypedMapping.Valid -and
         $wrongTypedMapping.Errors -contains 'Required live check lacks sections_apply_json evidence: sections_selected_apply_committed')
    $attestation.check_evidence['sections_selected_apply_committed'] = $savedTypedMapping
    $attestation | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $attestationPath -Encoding utf8

    Set-Content -LiteralPath $pngPath -Value 'tampered GUI evidence' -Encoding utf8
    $tamperedEvidence = Test-MahodLiveAcceptanceAttestation -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'tampered live evidence invalidates acceptance' (-not $tamperedEvidence.Valid)

    [System.IO.File]::WriteAllBytes($pngPath,
        [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII='))
    $savedMapping = $attestation.check_evidence['civil_process_healthy']
    $attestation.check_evidence['civil_process_healthy'] = @()
    $attestation | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $unmappedCheck = Test-MahodLiveAcceptanceAttestation -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'a true check without hash-bound evidence mapping is rejected' `
        (-not $unmappedCheck.Valid -and
         $unmappedCheck.Errors -contains 'Required live check has no evidence mapping: civil_process_healthy')
    $attestation.check_evidence['civil_process_healthy'] = $savedMapping

    Set-Content -LiteralPath $setupPath -Value 'tampered setup bytes' -Encoding utf8
    $attestation | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $tamperedSetup = Test-MahodLiveAcceptanceAttestation -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'a different setup invalidates acceptance' (-not $tamperedSetup.Valid)

    Set-Content -LiteralPath $setupPath -Value 'fixture setup bytes' -Encoding utf8
    $attestation.package_revision = '9.9.8'
    $attestation | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $attestationPath -Encoding utf8
    $wrongCandidate = Test-MahodLiveAcceptanceAttestation -Repo $fixture -Release $acceptedCompileOnly -HostYear 2027
    Check 'attestation for another package revision is rejected' (-not $wrongCandidate.Valid)
}
finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($failures -gt 0) {
    Write-Host "RELEASE GATE POLICY SELF-TEST FAILED: $failures check(s)."
    exit 1
}
Write-Host 'RELEASE GATE POLICY SELF-TEST PASSED.'
exit 0
