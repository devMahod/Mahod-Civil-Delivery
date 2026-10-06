<#
Shared, side-effect-free release status and live-acceptance policy.

This file does not build, install, launch Civil or write evidence.  A GUI acceptance
claim is valid only when its attestation binds the exact setup EXE, all four staged
Civil/Core DLLs and a semantic, runtime-manifest-backed functional evidence chain.
#>

$script:MahodStatusPending = 'COMPILED_GUI_ACCEPTANCE_PENDING'
$script:MahodStatusAccepted = 'COMPILED_GUI_ACCEPTED'
$script:MahodStatusCompileOnly = 'COMPILED_ONLY_GUI_NOT_TESTED'

function Test-MahodReleaseStatusMatrix {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Release)

    $errors = New-Object System.Collections.Generic.List[string]
    $status2027 = [string]$Release.civil_2027_status
    $status2026 = [string]$Release.civil_2026_status
    $allowed2027 = @($script:MahodStatusPending, $script:MahodStatusAccepted)
    $allowed2026 = @($script:MahodStatusPending, $script:MahodStatusAccepted, $script:MahodStatusCompileOnly)

    if ($allowed2027 -notcontains $status2027) {
        $errors.Add("civil_2027_status '$status2027' is invalid; expected PENDING or ACCEPTED.")
    }
    if ($allowed2026 -notcontains $status2026) {
        $errors.Add("civil_2026_status '$status2026' is invalid; expected PENDING, ACCEPTED or explicit COMPILED_ONLY_GUI_NOT_TESTED.")
    }
    if ($status2027 -eq $script:MahodStatusAccepted) {
        if ([string]$Release.last_live_candidate -ne [string]$Release.package_revision) {
            $errors.Add("Accepted Civil 2027 status requires last_live_candidate to equal package_revision.")
        }
        $liveDate = [DateTime]::MinValue
        if (-not [DateTime]::TryParseExact([string]$Release.last_live_date, 'yyyy-MM-dd',
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::None, [ref]$liveDate)) {
            $errors.Add('Accepted Civil 2027 status requires last_live_date in yyyy-MM-dd form.')
        }
    }

    [pscustomobject]@{
        Valid = ($errors.Count -eq 0)
        Errors = @($errors)
        Civil2027 = $status2027
        Civil2026 = $status2026
    }
}

function Get-MahodEmployeeReleaseEligibility {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Release)

    $matrix = Test-MahodReleaseStatusMatrix -Release $Release
    $reasons = New-Object System.Collections.Generic.List[string]
    foreach ($errorText in $matrix.Errors) { $reasons.Add($errorText) }

    if ($matrix.Valid) {
        if ($matrix.Civil2027 -ne $script:MahodStatusAccepted) {
            $reasons.Add("Civil 3D 2027 GUI acceptance is not ACCEPTED (actual: $($matrix.Civil2027)).")
        }
        if ($matrix.Civil2026 -ne $script:MahodStatusAccepted -and
            $matrix.Civil2026 -ne $script:MahodStatusCompileOnly) {
            $reasons.Add("Civil 3D 2026 must be ACCEPTED or explicitly COMPILED_ONLY_GUI_NOT_TESTED (actual: $($matrix.Civil2026)).")
        }
    }

    [pscustomobject]@{
        Allowed = ($reasons.Count -eq 0)
        Reasons = @($reasons)
    }
}

function Test-MahodEmployeeReleaseAllowed {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Release)
    return [bool](Get-MahodEmployeeReleaseEligibility -Release $Release).Allowed
}

function Get-MahodReleaseDisplayStatus {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Release)

    $matrix = Test-MahodReleaseStatusMatrix -Release $Release
    if (-not $matrix.Valid) { return 'INVALID_STATUS_MATRIX' }
    if ($matrix.Civil2027 -ne $script:MahodStatusAccepted) { return 'PENDING' }
    if ($matrix.Civil2026 -eq $script:MahodStatusAccepted) { return 'ACCEPTED_2027_2026' }
    if ($matrix.Civil2026 -eq $script:MahodStatusCompileOnly) { return 'ACCEPTED_2027_2026_COMPILED_ONLY' }
    return 'ACCEPTED_2027_2026_PENDING'
}

function Get-MahodAcceptanceAttestationRelativePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$PackageRevision,
        [Parameter(Mandatory = $true)][ValidateSet(2026, 2027)][int]$HostYear
    )
    return "evidence/live-acceptance/$PackageRevision/civil-$HostYear-acceptance.json"
}

function Resolve-MahodRepoRelativeFile {
    param(
        [Parameter(Mandatory = $true)][string]$Repo,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [System.IO.Path]::IsPathRooted($RelativePath)) {
        return $null
    }
    try {
        $root = [System.IO.Path]::GetFullPath($Repo).TrimEnd([char[]]@('\', '/'))
        $candidate = [System.IO.Path]::GetFullPath((Join-Path $root ($RelativePath.Replace('/', '\'))))
        $prefix = $root + [System.IO.Path]::DirectorySeparatorChar
        if (-not $candidate.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $null
        }
        return $candidate
    } catch {
        return $null
    }
}

function Test-MahodSha256Text {
    param([string]$Value)
    return -not [string]::IsNullOrWhiteSpace($Value) -and $Value -match '^[0-9a-fA-F]{64}$'
}

function Get-MahodJsonStringValue {
    <#
    PowerShell 7's ConvertFrom-Json eagerly turns ISO-8601 strings into DateTime.
    Casting that DateTime back to string is culture/time-zone dependent and can turn
    2026-09-02 into 09/02/2026.  Release ordering must therefore be based on the
    original JSON token, never on the deserialized object's display string.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Json,
        [Parameter(Mandatory = $true)][string]$PropertyName
    )

    $escaped = [Regex]::Escape($PropertyName)
    $match = [Regex]::Match(
        $Json,
        '"' + $escaped + '"\s*:\s*"(?<value>(?:\\.|[^"\\])*)"',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) { return $null }

    # Release timestamp tokens are deliberately restricted to ordinary ISO-8601
    # characters. Returning the captured token verbatim is important: feeding it
    # through ConvertFrom-Json here would recreate the PowerShell 7 DateTime coercion
    # this helper exists to avoid.
    return [string]$match.Groups['value'].Value
}

function ConvertFrom-MahodUtcJsonTimestamp {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Json,
        [Parameter(Mandatory = $true)][string]$PropertyName,
        [Parameter(Mandatory = $true)][ref]$Value
    )

    $text = Get-MahodJsonStringValue -Json $Json -PropertyName $PropertyName
    if ([string]::IsNullOrWhiteSpace($text)) { return $false }
    return [DateTimeOffset]::TryParse(
        $text,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind,
        $Value)
}

function Get-MahodObjectPropertyValue {
    [CmdletBinding()]
    param(
        $Object,
        [Parameter(Mandatory = $true)][string[]]$Names
    )

    if ($null -eq $Object) { return $null }
    foreach ($name in $Names) {
        $property = $Object.PSObject.Properties |
            Where-Object { $_.Name -eq $name } | Select-Object -First 1
        if ($property) { return $property.Value }
    }
    return $null
}

function Test-MahodNonEmptyText {
    param($Value)
    return -not [string]::IsNullOrWhiteSpace([string]$Value)
}

function Add-MahodFunctionalEvidenceError {
    param(
        [Parameter(Mandatory = $true)]$Errors,
        [Parameter(Mandatory = $true)][string]$Message
    )
    $Errors.Add("Functional evidence: $Message")
}

function Get-MahodManifestArtifactHash {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [string]$ExpectedPath,
        [string]$ExpectedFileName
    )

    $properties = @($Manifest.artifact_hashes.PSObject.Properties)
    if (-not [string]::IsNullOrWhiteSpace($ExpectedPath)) {
        $normalizedExpected = $ExpectedPath.Trim().Replace('/', '\').TrimEnd('\')
        $exact = @($properties | Where-Object {
            ([string]$_.Name).Replace('/', '\').TrimEnd('\') -ieq $normalizedExpected
        })
        if ($exact.Count -eq 1) { return [string]$exact[0].Value }
        return $null
    }

    $matches = @($properties | Where-Object {
        [System.IO.Path]::GetFileName([string]$_.Name) -ieq $ExpectedFileName
    })
    if ($matches.Count -eq 1) { return [string]$matches[0].Value }
    return $null
}

function Test-MahodManifestArtifactBinding {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)]$Errors,
        [Parameter(Mandatory = $true)][string]$Label,
        [string]$ExpectedPath,
        [string]$ExpectedFileName
    )

    $recorded = Get-MahodManifestArtifactHash `
        -Manifest $Manifest -ExpectedPath $ExpectedPath -ExpectedFileName $ExpectedFileName
    if (-not (Test-MahodSha256Text $recorded) -or $recorded -ine $ExpectedSha256) {
        Add-MahodFunctionalEvidenceError $Errors `
            "$Label is not bound by the runtime manifest to its exact attested SHA-256."
        return $false
    }
    return $true
}

function Test-MahodRuntimeManifestEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Evidence,
        [Parameter(Mandatory = $true)][string]$ExpectedFeature,
        [Parameter(Mandatory = $true)][string[]]$ExpectedOperation,
        [Parameter(Mandatory = $true)][string]$ExpectedRunId,
        [Parameter(Mandatory = $true)][string[]]$ExpectedStatus,
        [Parameter(Mandatory = $true)][string]$ExpectedProfileId,
        [Parameter(Mandatory = $true)][string]$ExpectedProfileHash,
        [Parameter(Mandatory = $true)][string]$PackageRevision,
        [Parameter(Mandatory = $true)][string]$PlatformVersion,
        [Parameter(Mandatory = $true)][string]$PluginSha256,
        [Parameter(Mandatory = $true)][DateTimeOffset]$InstallerBuiltAt,
        [Parameter(Mandatory = $true)][DateTimeOffset]$AcceptedAt,
        [Parameter(Mandatory = $true)]$Errors,
        [Parameter(Mandatory = $true)][string]$Label,
        [string]$ExpectedScope
    )

    $manifest = $Evidence.Document
    $schema = 0
    if (-not [int]::TryParse([string]$manifest.schema_version, [ref]$schema) -or $schema -lt 2) {
        Add-MahodFunctionalEvidenceError $Errors "$Label manifest schema_version must be at least 2."
    }
    if ([string]$manifest.run_id -cne $ExpectedRunId) {
        Add-MahodFunctionalEvidenceError $Errors "$Label manifest run_id does not match its functional artifact."
    }
    if ([string]$manifest.feature -cne $ExpectedFeature -or
        $ExpectedOperation -cnotcontains [string]$manifest.operation) {
        Add-MahodFunctionalEvidenceError $Errors `
            "$Label manifest must be $ExpectedFeature/$($ExpectedOperation -join '|')."
    }
    if ($ExpectedStatus -cnotcontains [string]$manifest.result_status) {
        Add-MahodFunctionalEvidenceError $Errors `
            "$Label manifest result_status must be $($ExpectedStatus -join '|')."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedScope) -and
        [string]$manifest.scope -cne $ExpectedScope) {
        Add-MahodFunctionalEvidenceError $Errors "$Label manifest scope must be $ExpectedScope."
    }
    if ([string]$manifest.project_profile_id -cne $ExpectedProfileId -or
        [string]$manifest.project_profile_hash -ine $ExpectedProfileHash) {
        Add-MahodFunctionalEvidenceError $Errors `
            "$Label manifest profile identity does not match the functional chain."
    }
    if ([string]$manifest.plugin_package_revision -cne $PackageRevision -or
        [string]$manifest.plugin_build_version -cne $PlatformVersion -or
        [string]$manifest.plugin_assembly_sha256 -ine $PluginSha256) {
        Add-MahodFunctionalEvidenceError $Errors `
            "$Label manifest was not emitted by the exact accepted package/DLL."
    }

    $startedAt = [DateTimeOffset]::MinValue
    $completedAt = [DateTimeOffset]::MinValue
    $startedOk = ConvertFrom-MahodUtcJsonTimestamp `
        -Json $Evidence.Raw -PropertyName 'started_at_utc' -Value ([ref]$startedAt)
    $completedOk = ConvertFrom-MahodUtcJsonTimestamp `
        -Json $Evidence.Raw -PropertyName 'completed_at_utc' -Value ([ref]$completedAt)
    if (-not $startedOk -or -not $completedOk -or
        $startedAt.Offset -ne [TimeSpan]::Zero -or $completedAt.Offset -ne [TimeSpan]::Zero) {
        Add-MahodFunctionalEvidenceError $Errors `
            "$Label manifest must contain explicit UTC started_at_utc/completed_at_utc."
    } else {
        if ($startedAt -gt $completedAt) {
            Add-MahodFunctionalEvidenceError $Errors "$Label manifest completes before it starts."
        }
        if ($InstallerBuiltAt -ne [DateTimeOffset]::MinValue -and $startedAt -lt $InstallerBuiltAt) {
            Add-MahodFunctionalEvidenceError $Errors `
                "$Label manifest predates the exact published setup installer."
        }
        if ($AcceptedAt -ne [DateTimeOffset]::MinValue -and $completedAt -gt $AcceptedAt) {
            Add-MahodFunctionalEvidenceError $Errors `
                "$Label manifest completes after accepted_utc."
        }
    }

    $artifacts = @($manifest.artifacts)
    $artifactProperties = @($manifest.artifact_hashes.PSObject.Properties)
    if ($artifacts.Count -eq 0 -or $artifactProperties.Count -ne $artifacts.Count) {
        Add-MahodFunctionalEvidenceError $Errors `
            "$Label manifest artifact inventory is empty or not one-to-one with artifact_hashes."
    }
    foreach ($artifact in $artifacts) {
        $matching = @($artifactProperties | Where-Object { [string]$_.Name -ieq [string]$artifact })
        if ($matching.Count -ne 1 -or -not (Test-MahodSha256Text ([string]$matching[0].Value))) {
            Add-MahodFunctionalEvidenceError $Errors `
                "$Label manifest has an unbound/ambiguous artifact path: $artifact"
        }
    }

    $inputs = @($manifest.input_hashes_by_path.PSObject.Properties)
    if ($inputs.Count -eq 0 -or @($inputs | Where-Object {
        [string]::IsNullOrWhiteSpace([string]$_.Name) -or
        -not (Test-MahodSha256Text ([string]$_.Value))
    }).Count -gt 0) {
        Add-MahodFunctionalEvidenceError $Errors `
            "$Label manifest does not contain a complete hashed input snapshot."
    }

    return [pscustomobject]@{
        Manifest = $manifest
        StartedAt = $startedAt
        CompletedAt = $completedAt
    }
}

function Get-MahodRequiredLiveAcceptanceChecks {
    [CmdletBinding()]
    param()

    return @(
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
}

function Get-MahodRequiredLiveEvidenceKinds {
    [CmdletBinding()]
    param()

    return @(
        'screenshot_png',
        'sections_plan_json',
        'sections_plan_manifest_json',
        'sections_apply_json',
        'sections_apply_manifest_json',
        'sections_verify_json',
        'sections_verify_manifest_json',
        'sections_replan_json',
        'sections_replan_manifest_json',
        'estimate_scan_json',
        'estimate_build_json',
        'estimate_build_manifest_json',
        'estimate_export_json',
        'estimate_export_package_json',
        'estimate_export_audit_json',
        'estimate_export_xlsx',
        'installed_loaded_identity_json',
        'bundle_winner_json'
    )
}

function Get-MahodRequiredEvidenceKindsForLiveCheck {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$CheckName)

    switch ($CheckName) {
        'civil_process_healthy'                         { return @('installed_loaded_identity_json','bundle_winner_json') }
        'sections_selected_record_ready'                { return @('sections_plan_json','sections_plan_manifest_json') }
        'sections_selected_apply_committed'             { return @('sections_apply_json','sections_apply_manifest_json') }
        'sections_selected_verify_passed'               { return @('sections_verify_json','sections_verify_manifest_json') }
        'sections_selected_replan_idempotent'           { return @('sections_replan_json','sections_replan_manifest_json') }
        'sections_manual_reuse_preserved'               { return @('sections_verify_json','sections_verify_manifest_json') }
        'sections_existing_and_design_lines_verified'   { return @('sections_verify_json','sections_verify_manifest_json','screenshot_png') }
        'sections_full_widths_and_strips_verified'      { return @('sections_verify_json','sections_verify_manifest_json','screenshot_png') }
        'sections_signed_slopes_verified'               { return @('sections_verify_json','sections_verify_manifest_json','screenshot_png') }
        'sections_single_datum_verified'                { return @('sections_verify_json','sections_verify_manifest_json','screenshot_png') }
        'sections_office_blocks_verified'               { return @('sections_verify_json','sections_verify_manifest_json','screenshot_png') }
        'sections_traffic_direction_verified'           { return @('sections_verify_json','sections_verify_manifest_json','screenshot_png') }
        'sections_utilities_and_row_verified'           { return @('sections_verify_json','sections_verify_manifest_json','screenshot_png') }
        'sections_reference_plot_verified'              { return @('sections_verify_json','sections_verify_manifest_json','screenshot_png') }
        'estimate_source_scope_verified'                { return @('estimate_scan_json','estimate_build_manifest_json') }
        'estimate_pricebook_identity_verified'          { return @('estimate_build_json','estimate_build_manifest_json') }
        'estimate_earthworks_decision_verified'         { return @('estimate_scan_json','estimate_build_json','estimate_build_manifest_json') }
        'estimate_duplicates_and_noise_reviewed'        { return @('estimate_scan_json','estimate_build_json','estimate_build_manifest_json') }
        'estimate_export_fail_closed_verified'          { return @('estimate_build_json','estimate_build_manifest_json','screenshot_png') }
        'estimate_unknown_names_fail_closed_verified'   { return @('estimate_scan_json','estimate_build_json','estimate_build_manifest_json') }
        'estimate_positive_export_verified'            { return @('estimate_scan_json','estimate_build_json','estimate_build_manifest_json','estimate_export_json','estimate_export_package_json','estimate_export_audit_json','estimate_export_xlsx','screenshot_png') }
        'drawing_unchanged'                             { return @('sections_verify_json','sections_verify_manifest_json') }
        'profile_unchanged'                             { return @('sections_verify_json','sections_verify_manifest_json') }
        default { return @() }
    }
}

function Test-MahodFunctionalEvidenceChain {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$EvidenceByKind,
        [Parameter(Mandatory = $true)][string]$PackageRevision,
        [Parameter(Mandatory = $true)][string]$PlatformVersion,
        [Parameter(Mandatory = $true)][string]$PluginSha256,
        [Parameter(Mandatory = $true)][DateTimeOffset]$InstallerBuiltAt,
        [Parameter(Mandatory = $true)][DateTimeOffset]$AcceptedAt,
        [Parameter(Mandatory = $true)]$Errors
    )

    $semanticKinds = @(
        'sections_plan_json','sections_plan_manifest_json',
        'sections_apply_json','sections_apply_manifest_json',
        'sections_verify_json','sections_verify_manifest_json',
        'sections_replan_json','sections_replan_manifest_json',
        'estimate_scan_json','estimate_build_json','estimate_build_manifest_json',
        'estimate_export_json','estimate_export_package_json','estimate_export_audit_json','estimate_export_xlsx'
    )
    if (@($semanticKinds | Where-Object { -not $EvidenceByKind.ContainsKey($_) }).Count -gt 0) {
        # The ordinary required-kind diagnostics name every missing item. Avoid a
        # cascade of null-property errors until the complete semantic set exists.
        return
    }

    $planEvidence = $EvidenceByKind['sections_plan_json']
    $applyEvidence = $EvidenceByKind['sections_apply_json']
    $verifyEvidence = $EvidenceByKind['sections_verify_json']
    $replanEvidence = $EvidenceByKind['sections_replan_json']
    $plan = $planEvidence.Document
    $apply = $applyEvidence.Document
    $verify = $verifyEvidence.Document
    $replan = $replanEvidence.Document

    # ----------------------------------------------------------- sections artifacts
    $planSchema = 0
    if (-not [int]::TryParse([string]$plan.schema_version, [ref]$planSchema) -or $planSchema -lt 1) {
        Add-MahodFunctionalEvidenceError $Errors 'sections PLAN schema_version must be at least 1.'
    }
    $planRunId = [string]$plan.run_id
    $sectionProfileId = [string]$plan.project_profile_id
    $sectionProfileHash = [string]$plan.project_profile_hash
    if (-not (Test-MahodNonEmptyText $planRunId) -or
        -not (Test-MahodNonEmptyText $sectionProfileId) -or
        -not (Test-MahodSha256Text $sectionProfileHash) -or
        -not (Test-MahodNonEmptyText $plan.source_drawing) -or
        -not (Test-MahodNonEmptyText $plan.source_database_revision)) {
        Add-MahodFunctionalEvidenceError $Errors `
            'sections PLAN lacks run/profile/drawing/revision identity.'
    }
    if (@('Ready','ReviewRequired') -cnotcontains [string]$plan.status) {
        Add-MahodFunctionalEvidenceError $Errors `
            'sections PLAN status must be Ready or ReviewRequired for a selected-record live gate.'
    }
    $planRecords = @($plan.records)
    $planIds = @{}
    if ($planRecords.Count -eq 0) {
        Add-MahodFunctionalEvidenceError $Errors 'sections PLAN must contain at least one record.'
    }
    foreach ($record in $planRecords) {
        $id = [string]$record.record_id
        if (-not (Test-MahodNonEmptyText $id) -or $planIds.ContainsKey($id)) {
            Add-MahodFunctionalEvidenceError $Errors `
                "sections PLAN contains a blank or duplicate record_id: '$id'."
        } else { $planIds[$id] = $true }
        if (@('Ready','ReviewRequired') -cnotcontains [string]$record.status -or
            @('Create','Update','Replace','Unchanged','ReviewRequired','Excluded') -cnotcontains [string]$record.action) {
            Add-MahodFunctionalEvidenceError $Errors `
                "sections PLAN record '$id' has an invalid selected-gate planning state."
        }
        if ([string]$record.action -eq 'Excluded') {
            $exclusion = $record.explicit_exclusion
            if (-not (Test-MahodNonEmptyText $exclusion.finding_code) -or
                -not (Test-MahodNonEmptyText $exclusion.reason) -or
                -not (Test-MahodNonEmptyText $exclusion.approved_by) -or
                $null -eq $exclusion.approved_at_utc) {
                Add-MahodFunctionalEvidenceError $Errors `
                    "sections PLAN exclusion '$id' is not signed and complete."
            }
        }
    }
    foreach ($external in @($plan.external_sources)) {
        if (-not (Test-MahodNonEmptyText $external.source_path) -or
            -not (Test-MahodSha256Text ([string]$external.sha256))) {
            Add-MahodFunctionalEvidenceError $Errors `
                'sections PLAN contains an unbound external source.'
        }
    }

    $applyRunId = [string]$apply.run_id
    $applyRecords = @($apply.records)
    $selectedRecordId = [string]$apply.selected_record_id
    if (-not (Test-MahodNonEmptyText $applyRunId) -or $applyRunId -ceq $planRunId -or
        [string]$apply.scope -cne 'selected-record' -or
        -not (Test-MahodNonEmptyText $selectedRecordId) -or
        $apply.committed -ne $true -or
        -not (Test-MahodNonEmptyText $apply.post_apply_database_revision) -or
        [string]$apply.status -cne 'Applied') {
        Add-MahodFunctionalEvidenceError $Errors `
            'sections APPLY must be a distinct, committed selected-record run with Applied status and post-state revision.'
    }
    $selectedPlanRecord = @($planRecords | Where-Object {
        [string]$_.record_id -ceq $selectedRecordId
    })
    if ($selectedPlanRecord.Count -ne 1 -or
        [string]$selectedPlanRecord[0].status -cne 'Ready' -or
        @('Create','Update','Replace','Unchanged') -cnotcontains [string]$selectedPlanRecord[0].action) {
        Add-MahodFunctionalEvidenceError $Errors `
            'the selected section target is not one unique, independently Ready PLAN record.'
    }
    $applyIds = @{}
    $appliedCount = 0
    foreach ($record in $applyRecords) {
        $id = [string]$record.record_id
        if (-not (Test-MahodNonEmptyText $id) -or $applyIds.ContainsKey($id)) {
            Add-MahodFunctionalEvidenceError $Errors `
                "sections APPLY contains a blank or duplicate record_id: '$id'."
        } else { $applyIds[$id] = $true }
        if ([string]$record.status -eq 'Applied' -and $id -ceq $selectedRecordId) { $appliedCount++ }
        else {
            Add-MahodFunctionalEvidenceError $Errors `
                "sections APPLY record '$id' is not the one selected Applied target."
        }
    }
    if ($applyRecords.Count -ne 1 -or -not $applyIds.ContainsKey($selectedRecordId)) {
        Add-MahodFunctionalEvidenceError $Errors `
            'sections APPLY must contain exactly the selected record and cannot claim omitted PLAN rows.'
    }
    if ($appliedCount -eq 0) {
        Add-MahodFunctionalEvidenceError $Errors `
            'sections APPLY proves only exclusions; at least one real section must be applied in the live gate.'
    }

    $verifyRunId = [string]$verify.run_id
    $verifyRecords = @($verify.records)
    if (-not (Test-MahodNonEmptyText $verifyRunId) -or
        @($planRunId,$applyRunId) -contains $verifyRunId -or
        [string]$verify.scope -cne 'selected-record' -or
        [string]$verify.selected_record_id -cne $selectedRecordId -or
        [string]$verify.status -cne 'Verified') {
        Add-MahodFunctionalEvidenceError $Errors `
            'sections VERIFY must be a distinct selected-record result for the exact applied target.'
    }
    $verifyIds = @{}
    foreach ($record in $verifyRecords) {
        $id = [string]$record.record_id
        if (-not (Test-MahodNonEmptyText $id) -or $verifyIds.ContainsKey($id)) {
            Add-MahodFunctionalEvidenceError $Errors `
                "sections VERIFY contains a blank or duplicate record_id: '$id'."
        } else { $verifyIds[$id] = $true }
        $checks = @($record.checks)
        if ([string]$record.status -cne 'Verified' -or $checks.Count -eq 0 -or
            @($checks | Where-Object {
                -not (Test-MahodNonEmptyText $_.check) -or $_.pass -ne $true
            }).Count -gt 0) {
            Add-MahodFunctionalEvidenceError $Errors `
                "sections VERIFY record '$id' is not Verified with a non-empty all-pass check set."
        }
    }
    if ($verifyRecords.Count -ne 1 -or -not $verifyIds.ContainsKey($selectedRecordId)) {
        Add-MahodFunctionalEvidenceError $Errors `
            'sections VERIFY must contain exactly the selected record and cannot turn omitted PLAN rows green.'
    }

    $replanRunId = [string]$replan.run_id
    $replanRecords = @($replan.records)
    if (-not (Test-MahodNonEmptyText $replanRunId) -or
        @($planRunId,$applyRunId,$verifyRunId) -contains $replanRunId -or
        [string]$replan.project_profile_id -cne $sectionProfileId -or
        [string]$replan.project_profile_hash -ine $sectionProfileHash -or
        [string]$replan.source_drawing -cne [string]$plan.source_drawing -or
        @('Ready','ReviewRequired') -cnotcontains [string]$replan.status) {
        Add-MahodFunctionalEvidenceError $Errors `
            'sections re-PLAN must be a distinct run on the same drawing/profile.'
    }
    $replanIds = @{}
    foreach ($record in $replanRecords) {
        $id = [string]$record.record_id
        if (-not (Test-MahodNonEmptyText $id) -or $replanIds.ContainsKey($id)) {
            Add-MahodFunctionalEvidenceError $Errors `
                "sections re-PLAN contains a blank or duplicate record_id: '$id'."
        } else { $replanIds[$id] = $true }
        if ($id -ceq $selectedRecordId -and
            ([string]$record.status -cne 'Ready' -or [string]$record.action -cne 'Unchanged')) {
            Add-MahodFunctionalEvidenceError $Errors `
                "sections re-PLAN selected record '$id' is not idempotent (expected Ready/Unchanged)."
        }
    }
    if (-not $replanIds.ContainsKey($selectedRecordId)) {
        Add-MahodFunctionalEvidenceError $Errors `
            'sections re-PLAN omits the selected accepted record.'
    }

    $planManifest = Test-MahodRuntimeManifestEvidence `
        -Evidence $EvidenceByKind['sections_plan_manifest_json'] `
        -ExpectedFeature 'sections' -ExpectedOperation 'plan' -ExpectedRunId $planRunId `
        -ExpectedStatus @('Ready','ReviewRequired') -ExpectedProfileId $sectionProfileId `
        -ExpectedProfileHash $sectionProfileHash -PackageRevision $PackageRevision `
        -PlatformVersion $PlatformVersion -PluginSha256 $PluginSha256 `
        -InstallerBuiltAt $InstallerBuiltAt -AcceptedAt $AcceptedAt -Errors $Errors `
        -Label 'sections PLAN'
    $applyManifest = Test-MahodRuntimeManifestEvidence `
        -Evidence $EvidenceByKind['sections_apply_manifest_json'] `
        -ExpectedFeature 'sections' -ExpectedOperation 'apply-selected' -ExpectedRunId $applyRunId `
        -ExpectedStatus 'Applied' -ExpectedProfileId $sectionProfileId `
        -ExpectedProfileHash $sectionProfileHash -PackageRevision $PackageRevision `
        -PlatformVersion $PlatformVersion -PluginSha256 $PluginSha256 `
        -InstallerBuiltAt $InstallerBuiltAt -AcceptedAt $AcceptedAt -Errors $Errors `
        -Label 'sections APPLY' -ExpectedScope 'selected-record'
    $verifyManifest = Test-MahodRuntimeManifestEvidence `
        -Evidence $EvidenceByKind['sections_verify_manifest_json'] `
        -ExpectedFeature 'sections' -ExpectedOperation 'verify-selected' -ExpectedRunId $verifyRunId `
        -ExpectedStatus 'Verified' -ExpectedProfileId $sectionProfileId `
        -ExpectedProfileHash $sectionProfileHash -PackageRevision $PackageRevision `
        -PlatformVersion $PlatformVersion -PluginSha256 $PluginSha256 `
        -InstallerBuiltAt $InstallerBuiltAt -AcceptedAt $AcceptedAt -Errors $Errors `
        -Label 'sections VERIFY' -ExpectedScope 'selected-record'
    $replanManifest = Test-MahodRuntimeManifestEvidence `
        -Evidence $EvidenceByKind['sections_replan_manifest_json'] `
        -ExpectedFeature 'sections' -ExpectedOperation 'plan' -ExpectedRunId $replanRunId `
        -ExpectedStatus @('Ready','ReviewRequired') -ExpectedProfileId $sectionProfileId `
        -ExpectedProfileHash $sectionProfileHash -PackageRevision $PackageRevision `
        -PlatformVersion $PlatformVersion -PluginSha256 $PluginSha256 `
        -InstallerBuiltAt $InstallerBuiltAt -AcceptedAt $AcceptedAt -Errors $Errors `
        -Label 'sections re-PLAN'

    [void](Test-MahodManifestArtifactBinding -Manifest $planManifest.Manifest `
        -ExpectedSha256 $planEvidence.Sha -ExpectedFileName 'section_plan.json' `
        -Errors $Errors -Label 'sections PLAN artifact')
    [void](Test-MahodManifestArtifactBinding -Manifest $applyManifest.Manifest `
        -ExpectedSha256 $planEvidence.Sha -ExpectedFileName 'section_plan.json' `
        -Errors $Errors -Label 'sections APPLY prerequisite PLAN')
    [void](Test-MahodManifestArtifactBinding -Manifest $applyManifest.Manifest `
        -ExpectedSha256 $applyEvidence.Sha -ExpectedFileName 'apply_result.json' `
        -Errors $Errors -Label 'sections APPLY artifact')
    [void](Test-MahodManifestArtifactBinding -Manifest $verifyManifest.Manifest `
        -ExpectedSha256 $planEvidence.Sha -ExpectedFileName 'section_plan.json' `
        -Errors $Errors -Label 'sections VERIFY prerequisite PLAN')
    [void](Test-MahodManifestArtifactBinding -Manifest $verifyManifest.Manifest `
        -ExpectedSha256 $applyEvidence.Sha -ExpectedFileName 'apply_result.json' `
        -Errors $Errors -Label 'sections VERIFY prerequisite APPLY')
    [void](Test-MahodManifestArtifactBinding -Manifest $verifyManifest.Manifest `
        -ExpectedSha256 $verifyEvidence.Sha -ExpectedFileName 'verify_result.json' `
        -Errors $Errors -Label 'sections VERIFY artifact')
    [void](Test-MahodManifestArtifactBinding -Manifest $replanManifest.Manifest `
        -ExpectedSha256 $replanEvidence.Sha -ExpectedFileName 'section_plan.json' `
        -Errors $Errors -Label 'sections re-PLAN artifact')
    if ($planManifest.CompletedAt -gt $applyManifest.StartedAt -or
        $applyManifest.CompletedAt -gt $verifyManifest.StartedAt -or
        $verifyManifest.CompletedAt -gt $replanManifest.StartedAt) {
        Add-MahodFunctionalEvidenceError $Errors `
            'sections manifest chronology is not PLAN -> APPLY -> VERIFY -> re-PLAN.'
    }

    # ------------------------------------------------------------ estimate artifacts
    $scanEvidence = $EvidenceByKind['estimate_scan_json']
    $buildEvidence = $EvidenceByKind['estimate_build_json']
    $scan = $scanEvidence.Document
    $build = $buildEvidence.Document
    $estimateRunId = [string](Get-MahodObjectPropertyValue $scan @('RunId','run_id'))
    $estimateProfileId = [string](Get-MahodObjectPropertyValue $scan @('ProjectProfileId','project_profile_id'))
    $estimateProfileHash = [string](Get-MahodObjectPropertyValue $scan @('ProjectProfileHash','project_profile_hash'))
    $estimateEffectiveHash = [string](Get-MahodObjectPropertyValue $scan @('ProjectProfileEffectiveHash','project_profile_effective_hash'))
    $estimateDrawing = [string](Get-MahodObjectPropertyValue $scan @('SourceDrawing','source_drawing'))
    $estimateDrawingHash = [string](Get-MahodObjectPropertyValue $scan @('SourceDrawingHash','source_drawing_hash'))
    $estimateRevision = [string](Get-MahodObjectPropertyValue $scan @('DatabaseRevision','database_revision'))
    $scanDbMod = Get-MahodObjectPropertyValue $scan @('SourceDbMod','source_dbmod')
    if (-not (Test-MahodNonEmptyText $estimateRunId) -or
        -not (Test-MahodNonEmptyText $estimateProfileId) -or
        -not (Test-MahodSha256Text $estimateProfileHash) -or
        -not (Test-MahodSha256Text $estimateEffectiveHash) -or
        -not (Test-MahodNonEmptyText $estimateDrawing) -or
        -not (Test-MahodSha256Text $estimateDrawingHash) -or
        -not (Test-MahodNonEmptyText $estimateRevision) -or
        $null -eq $scanDbMod -or [int]$scanDbMod -ne 0 -or
        @('Ready','ReviewRequired','1','20') -cnotcontains [string](Get-MahodObjectPropertyValue $scan @('Status','status')) -or
        (Get-MahodObjectPropertyValue $scan @('DiscoveryMode','discovery_mode')) -ne $true) {
        Add-MahodFunctionalEvidenceError $Errors `
            'estimate SCAN is not a saved, discovery-mode live scan eligible for a positive export.'
    }
    if ((Test-MahodNonEmptyText $estimateProfileId) -and
        (Test-MahodNonEmptyText $sectionProfileId) -and
        ($estimateProfileId -cne $sectionProfileId -or
         $estimateProfileHash -ine $sectionProfileHash)) {
        Add-MahodFunctionalEvidenceError $Errors `
            'sections and estimate evidence do not use the same exact project profile.'
    }
    $scanRecords = @(Get-MahodObjectPropertyValue $scan @('Records','records'))
    $scanIds = @{}
    if ($scanRecords.Count -eq 0) {
        Add-MahodFunctionalEvidenceError $Errors 'estimate SCAN must contain at least one quantity record.'
    }
    $unmappedScanIds = @{}
    foreach ($record in $scanRecords) {
        $id = [string]$record.record_id
        if (-not (Test-MahodNonEmptyText $id) -or $scanIds.ContainsKey($id)) {
            Add-MahodFunctionalEvidenceError $Errors `
                "estimate SCAN contains a blank or duplicate record_id: '$id'."
        } else { $scanIds[$id] = $true }
        $classification = $record.classification
        $recordStatus = [string]$record.status
        $mappingComplete = (Test-MahodNonEmptyText $classification.rule_key) -and
            (Test-MahodNonEmptyText $classification.mapping_approved_by) -and
            $null -ne $classification.mapping_approved_at_utc -and
            (Test-MahodNonEmptyText $classification.approved_catalog_id) -and
            (Test-MahodSha256Text ([string]$classification.approved_catalog_hash)) -and
            (Test-MahodSha256Text ([string]$classification.approved_catalog_item_fingerprint))
        if (-not $mappingComplete) { $unmappedScanIds[$id] = $true }
        if ([string]$record.run_id -cne $estimateRunId -or
            [string]$record.project_profile_id -cne $estimateProfileId -or
            @('Ready','ReviewRequired','Failed') -cnotcontains $recordStatus -or
            ($recordStatus -eq 'Ready' -and -not $mappingComplete) -or
            -not (Test-MahodSha256Text ([string]$record.source.drawing_hash)) -or
            -not (Test-MahodNonEmptyText $record.source.handle)) {
            Add-MahodFunctionalEvidenceError $Errors `
                "estimate SCAN record '$id' has an invalid status/mapping/source contract."
        }
    }
    if ([string]$build.run_id -cne $estimateRunId -or
        [string]$build.project_profile_id -cne $estimateProfileId -or
        [string]$build.project_profile_hash -ine $estimateProfileHash -or
        [string]$build.project_profile_effective_hash -ine $estimateEffectiveHash -or
        [string]$build.source_drawing_path -cne $estimateDrawing -or
        [string]$build.source_drawing_hash -ine $estimateDrawingHash -or
        [string]$build.source_database_revision -cne $estimateRevision -or
        $null -eq $build.source_dbmod -or [int]$build.source_dbmod -ne 0 -or
        [string]$build.project_profile_hash_kind -cne 'source-file-sha256' -or
        [string]$build.source_snapshot_kind -cne 'civil-live-saved' -or
        [string]$build.source_scope_policy -cne 'discover-all' -or
        [string]$build.xref_policy -cne 'include-xrefs' -or
        [string]$build.scope_notice -cne 'HOST + XREF — RECURSIVE VERIFIED SOURCES' -or
        [string]$build.status -cne 'Ready' -or
        $null -eq $build.excluded_line_count -or [int]$build.excluded_line_count -ne 0 -or
        -not (Test-MahodNonEmptyText $build.price_book_id) -or
        -not (Test-MahodSha256Text ([string]$build.price_book_hash))) {
        Add-MahodFunctionalEvidenceError $Errors `
            'estimate BUILD is not the same saved-live, approved-scope/profile/catalog snapshot with a positive Ready result.'
    }
    $buildLines = @($build.lines)
    $lineIds = @{}
    [decimal]$calculatedTotal = 0
    $blockedLineCount = 0
    $unmappedLineCount = 0
    if ($buildLines.Count -eq 0) {
        Add-MahodFunctionalEvidenceError $Errors 'estimate BUILD must contain at least one quantity/result line.'
    }
    foreach ($line in $buildLines) {
        $lineId = [string]$line.line_id
        if (-not (Test-MahodNonEmptyText $lineId) -or $lineIds.ContainsKey($lineId)) {
            Add-MahodFunctionalEvidenceError $Errors `
                "estimate BUILD contains a blank or duplicate line_id: '$lineId'."
        } else { $lineIds[$lineId] = $true }
        $lineSourceHashValid = Test-MahodSha256Text ([string]$line.source_drawing_hash)
        $lineReady = [string]$line.status -ceq 'Ready' -and
            $line.included_in_totals -eq $true -and
            @('Priced','ProjectOverride') -ccontains [string]$line.price_status -and
            $null -ne $line.price -and $null -ne $line.total -and
            (Test-MahodNonEmptyText $line.catalog_code) -and
            (Test-MahodNonEmptyText $line.unit) -and
            (Test-MahodNonEmptyText $line.mapping_approved_by) -and
            $null -ne $line.mapping_approved_at_utc -and
            [string]$line.approved_catalog_id -ceq [string]$build.price_book_id -and
            [string]$line.approved_catalog_hash -ieq [string]$build.price_book_hash -and
            (Test-MahodSha256Text ([string]$line.approved_catalog_item_fingerprint)) -and
            $lineSourceHashValid -and
            (Test-MahodNonEmptyText $line.price_decision_source)
        $lineBlocked = [string]$line.status -cne 'Ready' -and $line.included_in_totals -ne $true
        $lineFindingRefs = @($line.finding_refs | Where-Object { Test-MahodNonEmptyText $_ })
        $lineBlockingFindings = @($line.findings | Where-Object {
            (Test-MahodNonEmptyText $_.code) -and
            @('ReviewRequired','Error','2','3') -ccontains [string]$_.severity
        })
        if ($lineFindingRefs.Count -gt 0) {
            $lineBlockingFindings += @($build.findings | Where-Object {
                $lineFindingRefs -ccontains [string]$_.finding_id -and
                (Test-MahodNonEmptyText $_.code) -and
                @('ReviewRequired','Error','2','3') -ccontains [string]$_.severity
            })
        }
        # Real 6422 discovery rows can lack a stable source DWG hash.  That is a
        # legitimate fail-closed result only when the row is already excluded and
        # carries a structured blocking finding; it can never qualify as Ready.
        $sourceHashSafelyBlocked = -not $lineSourceHashValid -and
            $lineBlocked -and $lineBlockingFindings.Count -gt 0
        if ($lineBlocked) { $blockedLineCount++ }
        if ($unmappedScanIds.ContainsKey([string]$line.record_id) -and $lineBlocked) {
            $unmappedLineCount++
        }
        if (-not $scanIds.ContainsKey([string]$line.record_id) -or
            -not $lineReady -or $unmappedScanIds.ContainsKey([string]$line.record_id) -or
            -not (Test-MahodNonEmptyText $line.unit) -or
            -not (Test-MahodNonEmptyText $line.source_drawing_path) -or
            -not $lineSourceHashValid -or
            -not (Test-MahodNonEmptyText $line.source_database_revision) -or
            -not (Test-MahodNonEmptyText $line.source_handle) -or
            -not (Test-MahodNonEmptyText $line.measurement_method) -or
            -not (Test-MahodNonEmptyText $line.rule_key)) {
            Add-MahodFunctionalEvidenceError $Errors `
                "estimate BUILD line '$lineId' is neither export-ready nor safely excluded by a blocking status."
        }
        if ($line.included_in_totals -eq $true -and $null -ne $line.total) {
            $calculatedTotal += [decimal]$line.total
        }
    }
    $coveredRecords = @{}
    foreach ($line in $buildLines) {
        if ($coveredRecords.ContainsKey([string]$line.record_id)) {
            Add-MahodFunctionalEvidenceError $Errors 'estimate BUILD prices the same SCAN record more than once.'
        }
        $coveredRecords[[string]$line.record_id] = $true
    }
    foreach ($exclusion in @($build.exclusions)) {
        if (-not (Test-MahodNonEmptyText $exclusion.rule_key) -or
            -not (Test-MahodNonEmptyText $exclusion.reason) -or
            -not (Test-MahodNonEmptyText $exclusion.approved_by) -or
            $null -eq $exclusion.approved_at_utc -or @($exclusion.sources).Count -eq 0) {
            Add-MahodFunctionalEvidenceError $Errors 'estimate BUILD has an exclusion without explicit engineering authority.'
        }
        foreach ($source in @($exclusion.sources)) {
            $original = @($scanRecords | Where-Object { [string]$_.record_id -ceq [string]$source.record_id })
            if ($original.Count -ne 1 -or $coveredRecords.ContainsKey([string]$source.record_id) -or
                [string]$source.drawing_hash -ine [string]$original[0].source.drawing_hash -or
                [string]$source.handle -cne [string]$original[0].source.handle) {
                Add-MahodFunctionalEvidenceError $Errors 'estimate BUILD exclusion does not identify one unpriced SCAN record.'
            }
            $coveredRecords[[string]$source.record_id] = $true
        }
    }
    foreach ($id in $scanIds.Keys) {
        if (-not $coveredRecords.ContainsKey($id)) {
            Add-MahodFunctionalEvidenceError $Errors "estimate BUILD silently omitted SCAN record '$id'."
        }
    }
    foreach ($finding in @($build.findings) + @($buildLines | ForEach-Object { $_.findings })) {
        if (@('ReviewRequired','Error','2','3') -ccontains [string]$finding.severity -and
            ($null -eq $finding.resolved_at_utc -or
             -not (Test-MahodNonEmptyText $finding.resolved_by) -or
             -not (Test-MahodNonEmptyText $finding.resolution))) {
            $affected = @($finding.affected_record_ids)
            if ($affected.Count -eq 0 -or @($buildLines | Where-Object {
                $affected -ccontains [string]$_.record_id
            }).Count -gt 0) {
                Add-MahodFunctionalEvidenceError $Errors 'estimate BUILD retains an unresolved blocking finding.'
            }
        }
    }
    if ($blockedLineCount -ne 0 -or $unmappedLineCount -ne 0) {
        Add-MahodFunctionalEvidenceError $Errors `
            'estimate BUILD still has unresolved lines; export refusal is not employee-release acceptance.'
    }
    foreach ($external in @($build.external_sources)) {
        if (-not (Test-MahodNonEmptyText $external.drawing_path) -or
            -not (Test-MahodSha256Text ([string]$external.drawing_hash)) -or
            -not (Test-MahodNonEmptyText $external.xref_chain) -or
            -not (Test-MahodNonEmptyText $external.reference_handle_path)) {
            Add-MahodFunctionalEvidenceError $Errors `
                'estimate BUILD contains an incomplete XREF provenance record.'
        }
    }

    $buildManifest = Test-MahodRuntimeManifestEvidence `
        -Evidence $EvidenceByKind['estimate_build_manifest_json'] `
        -ExpectedFeature 'estimate' -ExpectedOperation 'export' -ExpectedRunId $estimateRunId `
        -ExpectedStatus @('Ready') -ExpectedProfileId $estimateProfileId `
        -ExpectedProfileHash $estimateProfileHash -PackageRevision $PackageRevision `
        -PlatformVersion $PlatformVersion -PluginSha256 $PluginSha256 `
        -InstallerBuiltAt $InstallerBuiltAt -AcceptedAt $AcceptedAt -Errors $Errors `
        -Label 'estimate BUILD'
    [void](Test-MahodManifestArtifactBinding -Manifest $buildManifest.Manifest `
        -ExpectedSha256 $scanEvidence.Sha -ExpectedFileName 'estimate_scan.json' `
        -Errors $Errors -Label 'estimate BUILD prerequisite SCAN')
    [void](Test-MahodManifestArtifactBinding -Manifest $buildManifest.Manifest `
        -ExpectedSha256 $buildEvidence.Sha -ExpectedFileName 'estimate_result.json' `
        -Errors $Errors -Label 'estimate BUILD artifact')

    # EXPORT replaces the run's BUILD manifest, but must retain both prerequisite
    # artifacts and bind the published XLSX + audit + package manifest. A screenshot
    # of an export refusal is a useful optional diagnostic, never a positive gate.
    $exportEvidence = $EvidenceByKind['estimate_export_json']
    $packageEvidence = $EvidenceByKind['estimate_export_package_json']
    $auditEvidence = $EvidenceByKind['estimate_export_audit_json']
    $xlsxEvidence = $EvidenceByKind['estimate_export_xlsx']
    $export = $exportEvidence.Document
    $package = $packageEvidence.Document
    $audit = $auditEvidence.Document
    foreach ($timed in @($auditEvidence, $packageEvidence)) {
        $generated = [DateTimeOffset]::MinValue
        if (-not (ConvertFrom-MahodUtcJsonTimestamp -Json $timed.Raw -PropertyName 'generated_at_utc' -Value ([ref]$generated)) -or
            $generated.Offset -ne [TimeSpan]::Zero -or
            $generated -lt $InstallerBuiltAt -or $generated -gt $buildManifest.CompletedAt) {
            Add-MahodFunctionalEvidenceError $Errors 'estimate EXPORT package/audit timestamp is outside its live runtime operation.'
        }
    }
    [void](Test-MahodManifestArtifactBinding -Manifest $buildManifest.Manifest `
        -ExpectedSha256 $exportEvidence.Sha -ExpectedFileName 'export_result.json' `
        -Errors $Errors -Label 'estimate EXPORT result')
    foreach ($artifact in @(
        @{ Path=$export.XlsxPath; Hash=$export.XlsxHash; Evidence=$xlsxEvidence; Label='workbook' },
        @{ Path=$export.AuditPath; Hash=$export.AuditHash; Evidence=$auditEvidence; Label='audit' },
        @{ Path=$export.ManifestPath; Hash=$export.ManifestHash; Evidence=$packageEvidence; Label='package' }
    )) {
        if (-not (Test-MahodNonEmptyText $artifact.Path) -or
            -not (Test-MahodSha256Text ([string]$artifact.Hash)) -or
            [string]$artifact.Hash -ine [string]$artifact.Evidence.Sha) {
            Add-MahodFunctionalEvidenceError $Errors "estimate EXPORT $($artifact.Label) identity is invalid."
        } else {
            [void](Test-MahodManifestArtifactBinding -Manifest $buildManifest.Manifest `
                -ExpectedSha256 $artifact.Evidence.Sha -ExpectedPath $artifact.Path `
                -Errors $Errors -Label "estimate EXPORT $($artifact.Label)")
        }
    }
    if ([string]$package.package_kind -cne 'mahod-estimate-export' -or
        [string]$package.run_id -cne $estimateRunId -or
        [string]$package.workbook.sha256 -ine $xlsxEvidence.Sha -or
        [string]$package.audit.sha256 -ine $auditEvidence.Sha -or
        [string]$package.workbook.file -cne [IO.Path]::GetFileName([string]$export.XlsxPath) -or
        [string]$package.audit.file -cne [IO.Path]::GetFileName([string]$export.AuditPath) -or
        [string]$package.profile.id -cne $estimateProfileId -or
        [string]$package.profile.source_sha256 -ine $estimateProfileHash -or
        [string]$package.profile.effective_sha256 -ine $estimateEffectiveHash -or
        [string]$package.profile.source_hash_kind -cne 'source-file-sha256' -or
        [string]$package.catalog.id -cne [string]$build.price_book_id -or
        [string]$package.catalog.sha256 -ine [string]$build.price_book_hash -or
        [string]$package.drawing.path -cne $estimateDrawing -or
        [string]$package.drawing.sha256 -ine $estimateDrawingHash -or
        [string]$package.drawing.live_revision -cne $estimateRevision -or
        [string]$package.drawing.snapshot_kind -cne 'civil-live-saved' -or
        $null -eq $package.drawing.dbmod -or [int]$package.drawing.dbmod -ne 0) {
        Add-MahodFunctionalEvidenceError $Errors 'estimate EXPORT package is not bound to the exact live BUILD and exported files.'
    }
    foreach ($field in @('run_id','project_profile_id','project_profile_hash',
        'project_profile_effective_hash','project_profile_hash_kind','price_book_id','price_book_hash',
        'source_snapshot_kind','source_drawing_path','source_drawing_hash','source_database_revision',
        'source_dbmod','source_scope_policy','xref_policy','scope_notice','status','excluded_line_count','clean_total')) {
        if ([string]$audit.$field -cne [string]$build.$field) {
            Add-MahodFunctionalEvidenceError $Errors "estimate EXPORT audit differs from BUILD: $field."
        }
    }
    if ([string]$audit.workbook_sha256 -ine $xlsxEvidence.Sha -or
        [string]$audit.workbook_file -cne [IO.Path]::GetFileName([string]$export.XlsxPath) -or
        [string]$audit.package_manifest -cne [IO.Path]::GetFileName([string]$export.ManifestPath) -or
        @($audit.lines).Count -ne $buildLines.Count) {
        Add-MahodFunctionalEvidenceError $Errors 'estimate EXPORT audit does not bind the workbook/package and complete BUILD line set.'
    }
    foreach ($line in $buildLines) {
        $matching = @($audit.lines | Where-Object { [string]$_.line_id -ceq [string]$line.line_id })
        if ($matching.Count -ne 1) {
            Add-MahodFunctionalEvidenceError $Errors 'estimate EXPORT audit line identity is missing or ambiguous.'
            continue
        }
        foreach ($field in @('record_id','source_drawing_path','source_drawing_hash','source_database_revision',
            'source_handle','rule_key','catalog_code','unit','boq_quantity','price','total','status',
            'included_in_totals','mapping_approved_by','approved_catalog_hash','approved_catalog_item_fingerprint')) {
            if ([string]$matching[0].$field -cne [string]$line.$field) {
                Add-MahodFunctionalEvidenceError $Errors "estimate EXPORT audit line differs from BUILD: $($line.line_id)/$field."
            }
        }
    }
    # BOQ groups, not independently rounded per-object totals, define the payable
    # total. This matches the product's one-row-per-catalog-item workbook semantics.
    $expectedTotal = [decimal]0
    foreach ($group in @($buildLines | Group-Object catalog_code,unit,price)) {
        $quantity = [decimal]0
        foreach ($line in $group.Group) { $quantity += [decimal]$line.boq_quantity }
        $expectedTotal += [Math]::Round($quantity * [decimal]$group.Group[0].price, 2, [MidpointRounding]::AwayFromZero)
    }
    if ($null -eq $build.clean_total -or [decimal]$build.clean_total -ne $expectedTotal) {
        Add-MahodFunctionalEvidenceError $Errors 'estimate BUILD total does not reconcile with priced BOQ groups.'
    }
}

function Test-MahodLiveAcceptanceAttestation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Repo,
        [Parameter(Mandatory = $true)]$Release,
        [Parameter(Mandatory = $true)][ValidateSet(2026, 2027)][int]$HostYear
    )

    $errors = New-Object System.Collections.Generic.List[string]
    $packageRevision = [string]$Release.package_revision
    $platformVersion = [string]$Release.platform_version
    $relativeAttestation = Get-MahodAcceptanceAttestationRelativePath -PackageRevision $packageRevision -HostYear $HostYear
    $attestationPath = Resolve-MahodRepoRelativeFile -Repo $Repo -RelativePath $relativeAttestation
    $attestation = $null
    $attestationJson = $null
    $installerManifestForAcceptance = $null
    $acceptedAtForEvidence = [DateTimeOffset]::MinValue
    $installerBuiltAtForEvidence = [DateTimeOffset]::MinValue

    if (-not $attestationPath -or -not (Test-Path -LiteralPath $attestationPath -PathType Leaf)) {
        $errors.Add("Live acceptance attestation is missing: $relativeAttestation")
    } else {
        try {
            $attestationJson = [System.IO.File]::ReadAllText($attestationPath, [System.Text.UTF8Encoding]::new($false))
            $attestation = $attestationJson | ConvertFrom-Json
        } catch {
            $errors.Add("Live acceptance attestation is not valid JSON: $($_.Exception.Message)")
        }
    }

    if ($attestation) {
        $schemaVersion = 0
        if (-not [int]::TryParse([string]$attestation.schema_version, [ref]$schemaVersion) -or $schemaVersion -ne 2) {
            $errors.Add('schema_version must be 2.')
        }
        if ([string]$attestation.verdict -ne 'ACCEPTED') { $errors.Add('verdict must be ACCEPTED.') }
        if ([string]$attestation.package_revision -ne $packageRevision) {
            $errors.Add("package_revision does not match release metadata ($($attestation.package_revision) != $packageRevision).")
        }
        if ([string]$attestation.platform_version -ne $platformVersion) {
            $errors.Add("platform_version does not match release metadata ($($attestation.platform_version) != $platformVersion).")
        }
        $attestedHostYear = 0
        if (-not [int]::TryParse([string]$attestation.civil_host_year, [ref]$attestedHostYear) -or
            $attestedHostYear -ne $HostYear) {
            $errors.Add("civil_host_year does not match the claimed host ($($attestation.civil_host_year) != $HostYear).")
        }
        # PowerShell 7 may eagerly deserialize an ISO JSON string into DateTime and
        # then `[string]` converts it using local culture/time zone. Validate the
        # original JSON token instead, so a literal Z/+00:00 remains provable on
        # both Windows PowerShell 5.1 and modern PowerShell.
        $acceptedText = if ($attestationJson) {
            Get-MahodJsonStringValue -Json $attestationJson -PropertyName 'accepted_utc'
        } else { $null }
        $acceptedAt = [DateTimeOffset]::MinValue
        if ([string]::IsNullOrWhiteSpace($acceptedText) -or
            -not [DateTimeOffset]::TryParse($acceptedText, [ref]$acceptedAt)) {
            $errors.Add('accepted_utc must be a valid timestamp.')
        } elseif ($acceptedAt.Offset -ne [TimeSpan]::Zero -or
                  $acceptedText -notmatch '(?:Z|\+00:00)$') {
            $errors.Add('accepted_utc must include the UTC (Z/+00:00) offset.')
        } else {
            $acceptedAtForEvidence = $acceptedAt
            $buildManifestPath = Resolve-MahodRepoRelativeFile -Repo $Repo -RelativePath 'installer/stage/build_manifest.json'
            $builtAt = [DateTimeOffset]::MinValue
            if (-not $buildManifestPath -or -not (Test-Path -LiteralPath $buildManifestPath -PathType Leaf)) {
                $errors.Add('installer/stage/build_manifest.json is required to order live acceptance after the build.')
            } else {
                try {
                    $buildManifestJson = [System.IO.File]::ReadAllText($buildManifestPath, [System.Text.UTF8Encoding]::new($false))
                    $buildManifest = $buildManifestJson | ConvertFrom-Json
                    if ([string]$buildManifest.version -ne $packageRevision -or
                        [string]$buildManifest.platform_file_version -ne $platformVersion) {
                        $errors.Add('Build manifest identity does not match the accepted release.')
                    }
                    if (-not (ConvertFrom-MahodUtcJsonTimestamp `
                            -Json $buildManifestJson -PropertyName 'built_utc' -Value ([ref]$builtAt))) {
                        $errors.Add('Build manifest built_utc is invalid.')
                    } elseif ($acceptedAt -lt $builtAt) {
                        $errors.Add('accepted_utc predates the exact installer build.')
                    }
                } catch {
                    $errors.Add("Build manifest is unreadable: $($_.Exception.Message)")
                }
            }
            if ($acceptedAt -gt [DateTimeOffset]::UtcNow.AddMinutes(15)) {
                $errors.Add('accepted_utc is implausibly in the future.')
            }

            $installerManifestPath = Resolve-MahodRepoRelativeFile -Repo $Repo -RelativePath 'installer/out/installer_manifest.json'
            if (-not $installerManifestPath -or -not (Test-Path -LiteralPath $installerManifestPath -PathType Leaf)) {
                $errors.Add('installer/out/installer_manifest.json is required to order acceptance after setup publication.')
            } else {
                try {
                    $installerManifestJson = [System.IO.File]::ReadAllText($installerManifestPath, [System.Text.UTF8Encoding]::new($false))
                    $installerManifestForAcceptance = $installerManifestJson | ConvertFrom-Json
                    if ([string]$installerManifestForAcceptance.version -ne $packageRevision -or
                        [string]$installerManifestForAcceptance.platform_file_version -ne $platformVersion) {
                        $errors.Add('Installer manifest identity does not match the accepted release.')
                    }
                    $installerBuiltAt = [DateTimeOffset]::MinValue
                    if (-not (ConvertFrom-MahodUtcJsonTimestamp `
                            -Json $installerManifestJson -PropertyName 'built_utc' -Value ([ref]$installerBuiltAt))) {
                        $errors.Add('Installer manifest built_utc is invalid.')
                    } else {
                        $installerBuiltAtForEvidence = $installerBuiltAt
                        if ($builtAt -ne [DateTimeOffset]::MinValue -and $installerBuiltAt -lt $builtAt) {
                            $errors.Add('Installer built_utc predates its staged build manifest.')
                        }
                        if ($acceptedAt -lt $installerBuiltAt) {
                            $errors.Add('accepted_utc predates the exact published setup installer.')
                        }
                    }
                } catch {
                    $errors.Add("Installer manifest is unreadable: $($_.Exception.Message)")
                }
            }
        }

        $expectedSetupRelative = "installer/out/Mahod_Civil_Delivery_Setup_$packageRevision.exe"
        if (-not $attestation.setup) {
            $errors.Add('setup identity is missing.')
        } else {
            $setupRelative = ([string]$attestation.setup.relative_path).Replace('\', '/')
            if ($setupRelative -ne $expectedSetupRelative) {
                $errors.Add("setup.relative_path must be $expectedSetupRelative.")
            }
            $setupPath = Resolve-MahodRepoRelativeFile -Repo $Repo -RelativePath $setupRelative
            $setupSha = [string]$attestation.setup.sha256
            if (-not (Test-MahodSha256Text $setupSha)) {
                $errors.Add('setup.sha256 must be a 64-character SHA-256 value.')
            } elseif (-not $setupPath -or -not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
                $errors.Add("Attested setup file is missing: $setupRelative")
            } else {
                $actualSetupSha = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
                if ($actualSetupSha -ine $setupSha) { $errors.Add("Setup SHA-256 mismatch: attested=$setupSha actual=$actualSetupSha") }
                $actualSize = (Get-Item -LiteralPath $setupPath).Length
                $attestedSize = [long]0
                if (-not [long]::TryParse([string]$attestation.setup.size_bytes, [ref]$attestedSize) -or
                    $attestedSize -ne $actualSize) {
                    $errors.Add("Setup size mismatch: attested=$($attestation.setup.size_bytes) actual=$actualSize")
                }
                if ($installerManifestForAcceptance) {
                    if ([string]$installerManifestForAcceptance.sha256 -ine $actualSetupSha -or
                        [long]$installerManifestForAcceptance.size_bytes -ne $actualSize) {
                        $errors.Add('Installer manifest does not bind the exact attested setup bytes.')
                    }
                }
            }
        }

        $expectedComponents = [ordered]@{
            civil_2027_plugin = 'installer/stage/payload/MahodAI.bundle/Contents/MahodAI.Civil3D.Plugin.dll'
            civil_2027_core = 'installer/stage/payload/MahodAI.bundle/Contents/MahodAI.CivilDelivery.Core.dll'
            civil_2026_plugin = 'installer/stage/payload/MahodAI.bundle/Contents/2026/MahodAI.Civil3D.Plugin.dll'
            civil_2026_core = 'installer/stage/payload/MahodAI.bundle/Contents/2026/MahodAI.CivilDelivery.Core.dll'
        }
        foreach ($componentKey in $expectedComponents.Keys) {
            $componentProperty = $null
            if ($attestation.components) {
                $componentProperty = $attestation.components.PSObject.Properties |
                    Where-Object { $_.Name -eq $componentKey } | Select-Object -First 1
            }
            if (-not $componentProperty) {
                $errors.Add("Component identity is missing: $componentKey")
                continue
            }
            $component = $componentProperty.Value
            $expectedRelative = [string]$expectedComponents[$componentKey]
            $componentRelative = ([string]$component.relative_path).Replace('\', '/')
            if ($componentRelative -ne $expectedRelative) {
                $errors.Add("$componentKey.relative_path must be $expectedRelative.")
            }
            $componentPath = Resolve-MahodRepoRelativeFile -Repo $Repo -RelativePath $componentRelative
            $componentSha = [string]$component.sha256
            if (-not (Test-MahodSha256Text $componentSha)) {
                $errors.Add("$componentKey.sha256 must be a 64-character SHA-256 value.")
            } elseif (-not $componentPath -or -not (Test-Path -LiteralPath $componentPath -PathType Leaf)) {
                $errors.Add("Attested component file is missing: $componentRelative")
            } else {
                $actualComponentSha = (Get-FileHash -LiteralPath $componentPath -Algorithm SHA256).Hash
                if ($actualComponentSha -ine $componentSha) {
                    $errors.Add("$componentKey SHA-256 mismatch: attested=$componentSha actual=$actualComponentSha")
                }
            }
        }

        $requiredChecks = @(Get-MahodRequiredLiveAcceptanceChecks)
        foreach ($checkName in $requiredChecks) {
            $checkProperty = $null
            if ($attestation.checks) {
                $checkProperty = $attestation.checks.PSObject.Properties |
                    Where-Object { $_.Name -eq $checkName } | Select-Object -First 1
            }
            if (-not $checkProperty -or $checkProperty.Value -ne $true) {
                $errors.Add("Required live check is not true: $checkName")
            }
        }

        $evidenceFiles = @($attestation.evidence_files)
        $requiredKinds = @(Get-MahodRequiredLiveEvidenceKinds)
        $evidenceByPath = @{}
        $seenKinds = @{}
        $evidenceByKind = @{}
        $loadedIdentityEvidence = $null
        $bundleWinnerEvidence = $null
        $evidencePrefix = "evidence/live-acceptance/$packageRevision/"
        if ($evidenceFiles.Count -eq 0 -or $null -eq $attestation.evidence_files) {
            $errors.Add('Hash-keyed, typed live evidence_files entries are required.')
        } else {
            foreach ($evidence in $evidenceFiles) {
                $kind = [string]$evidence.kind
                $evidenceRelative = ([string]$evidence.relative_path).Replace('\', '/')
                $evidenceSha = [string]$evidence.sha256
                $evidencePath = Resolve-MahodRepoRelativeFile -Repo $Repo -RelativePath $evidenceRelative
                if ($requiredKinds -notcontains $kind) {
                    $errors.Add("Evidence kind is missing or unsupported: '$kind' ($evidenceRelative)")
                }
                if (-not $evidenceRelative.StartsWith($evidencePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    $errors.Add("Live evidence must be stored under $evidencePrefix : $evidenceRelative")
                }
                if ($evidenceByPath.ContainsKey($evidenceRelative)) {
                    $errors.Add("Duplicate evidence path: $evidenceRelative")
                } else {
                    $evidenceByPath[$evidenceRelative] = $kind
                }
                if ($kind) {
                    $seenKinds[$kind] = 1 + $(if ($seenKinds.ContainsKey($kind)) { [int]$seenKinds[$kind] } else { 0 })
                    if ($kind -ne 'screenshot_png' -and [int]$seenKinds[$kind] -gt 1) {
                        $errors.Add("Duplicate singleton evidence kind is ambiguous: $kind")
                    }
                }
                if ($evidenceRelative -eq $relativeAttestation) {
                    $errors.Add('The attestation cannot list itself as evidence.')
                } elseif (-not (Test-MahodSha256Text $evidenceSha)) {
                    $errors.Add("Evidence SHA-256 is invalid: $evidenceRelative")
                } elseif (-not $evidencePath -or -not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
                    $errors.Add("Attested evidence file is missing or outside the repository: $evidenceRelative")
                } else {
                    $actualEvidenceSha = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash
                    if ($actualEvidenceSha -ine $evidenceSha) {
                        $errors.Add("Evidence SHA-256 mismatch for ${evidenceRelative}: attested=$evidenceSha actual=$actualEvidenceSha")
                    }
                    if ($kind -eq 'screenshot_png') {
                        $bytes = [System.IO.File]::ReadAllBytes($evidencePath)
                        $pngHeader = @(137,80,78,71,13,10,26,10)
                        if ($bytes.Length -lt 8 -or (0..7 | Where-Object { $bytes[$_] -ne $pngHeader[$_] }).Count -gt 0) {
                            $errors.Add("Screenshot evidence is not a PNG file: $evidenceRelative")
                        }
                    } elseif ($kind -eq 'estimate_export_xlsx') {
                        try {
                            Add-Type -AssemblyName System.IO.Compression
                            Add-Type -AssemblyName System.IO.Compression.FileSystem
                            $archive = [IO.Compression.ZipFile]::OpenRead($evidencePath)
                            try {
                                foreach ($part in @('[Content_Types].xml','xl/workbook.xml','xl/worksheets/sheet1.xml')) {
                                    if ($null -eq $archive.GetEntry($part)) { throw "Missing XLSX part $part" }
                                }
                            } finally { $archive.Dispose() }
                            $evidenceByKind[$kind] = [pscustomobject]@{
                                Sha=$evidenceSha; Relative=$evidenceRelative; Path=$evidencePath
                            }
                        } catch { $errors.Add("Estimate workbook is not a readable XLSX package: $evidenceRelative") }
                    } elseif ($kind -like '*_json') {
                        try {
                            $rawEvidenceJson = [System.IO.File]::ReadAllText($evidencePath, [System.Text.UTF8Encoding]::new($false))
                            $evidenceJson = $rawEvidenceJson | ConvertFrom-Json
                            if (-not $evidenceByKind.ContainsKey($kind)) {
                                $evidenceByKind[$kind] = [pscustomobject]@{
                                    Document = $evidenceJson; Raw = $rawEvidenceJson; Sha = $evidenceSha
                                    Relative = $evidenceRelative; Path = $evidencePath
                                }
                            }
                            if ($kind -eq 'installed_loaded_identity_json') {
                                $loadedIdentityEvidence = $evidenceJson
                                $loaded = @($evidenceJson.loaded_plugin_binaries)
                                $hostLoaded = @($loaded | Where-Object { [int]$_.civil_host_year -eq $HostYear })
                                if ($hostLoaded.Count -eq 0 -or @($hostLoaded | Where-Object {
                                    -not (Test-MahodSha256Text ([string]$_.sha256)) -or
                                    [string]::IsNullOrWhiteSpace([string]$_.path)
                                }).Count -gt 0) {
                                    $errors.Add("Loaded-identity evidence does not prove Civil $HostYear loaded a hashed plugin: $evidenceRelative")
                                } else {
                                    $loadedComponentName = if ($HostYear -eq 2027) { 'civil_2027_plugin' } else { 'civil_2026_plugin' }
                                    $loadedComponentProperty = $attestation.components.PSObject.Properties |
                                        Where-Object { $_.Name -eq $loadedComponentName } | Select-Object -First 1
                                    $expectedLoadedSha = if ($loadedComponentProperty) { [string]$loadedComponentProperty.Value.sha256 } else { $null }
                                    if (-not (Test-MahodSha256Text $expectedLoadedSha) -or @($hostLoaded | Where-Object {
                                        ([string]$_.sha256) -ine $expectedLoadedSha -or
                                        ([string]$_.file_version) -ne $platformVersion
                                    }).Count -gt 0) {
                                        $errors.Add("Loaded Civil $HostYear plugin does not match the exact attested DLL hash/version: $evidenceRelative")
                                    }
                                }
                            } elseif ($kind -eq 'bundle_winner_json') {
                                $bundleWinnerEvidence = $evidenceJson
                                if ([int]$evidenceJson.active_bundle_count -ne 1 -or
                                    $evidenceJson.receipt_matches_winner -ne $true -or
                                    $evidenceJson.loaded_matches_winner -ne $true) {
                                    $errors.Add("Bundle-winner evidence does not prove one receipt/loaded winner: $evidenceRelative")
                                }
                            }
                        } catch {
                            $errors.Add("JSON evidence is unreadable ($evidenceRelative): $($_.Exception.Message)")
                        }
                    }
                }
            }
        }
        foreach ($kind in $requiredKinds) {
            if (-not $seenKinds.ContainsKey($kind)) { $errors.Add("Required live evidence kind is missing: $kind") }
        }
        if ($loadedIdentityEvidence -and $bundleWinnerEvidence) {
            $winnerPath = ([string]$bundleWinnerEvidence.winner_path).TrimEnd('\', '/')
            $receiptPath = ([string]$bundleWinnerEvidence.receipt_path).TrimEnd('\', '/')
            $activeCandidates = @($bundleWinnerEvidence.candidates | Where-Object { $_.active -eq $true })
            $winnerCandidate = $activeCandidates | Sort-Object { [int]$_.priority } | Select-Object -First 1
            if ([string]::IsNullOrWhiteSpace($winnerPath) -or $receiptPath -ine $winnerPath -or
                $activeCandidates.Count -ne 1 -or -not $winnerCandidate -or
                ([string]$winnerCandidate.path).TrimEnd('\', '/') -ine $winnerPath -or
                [string]$winnerCandidate.package_revision -ne $packageRevision) {
                $errors.Add('Bundle-winner paths/candidates do not independently prove one current receipt winner.')
            }

            $loadedComponentName = if ($HostYear -eq 2027) { 'civil_2027_plugin' } else { 'civil_2026_plugin' }
            $loadedComponentProperty = $attestation.components.PSObject.Properties |
                Where-Object { $_.Name -eq $loadedComponentName } | Select-Object -First 1
            $expectedLoadedSha = if ($loadedComponentProperty) { [string]$loadedComponentProperty.Value.sha256 } else { $null }
            $winnerHash = if ($HostYear -eq 2027) {
                [string]$winnerCandidate.plugin_2027_sha256
            } else {
                [string]$winnerCandidate.plugin_2026_sha256
            }
            if (-not (Test-MahodSha256Text $expectedLoadedSha) -or $winnerHash -ine $expectedLoadedSha) {
                $errors.Add("Bundle winner does not contain the exact attested Civil $HostYear plugin hash.")
            }

            $expectedSuffix = if ($HostYear -eq 2027) {
                '\Contents\MahodAI.Civil3D.Plugin.dll'
            } else {
                '\Contents\2026\MahodAI.Civil3D.Plugin.dll'
            }
            $hostLoaded = @($loadedIdentityEvidence.loaded_plugin_binaries |
                Where-Object { [int]$_.civil_host_year -eq $HostYear })
            if ($hostLoaded.Count -eq 0 -or @($hostLoaded | Where-Object {
                $loadedPath = [string]$_.path
                ([string]::IsNullOrWhiteSpace($winnerPath)) -or
                -not $loadedPath.StartsWith($winnerPath + '\', [StringComparison]::OrdinalIgnoreCase) -or
                -not $loadedPath.EndsWith($expectedSuffix, [StringComparison]::OrdinalIgnoreCase) -or
                ([string]$_.sha256) -ine $expectedLoadedSha
            }).Count -gt 0) {
                $errors.Add("Loaded Civil $HostYear module path/hash does not match the attested winning bundle.")
            }
        }
        $functionalPluginName = if ($HostYear -eq 2027) {
            'civil_2027_plugin'
        } else {
            'civil_2026_plugin'
        }
        $functionalPluginProperty = if ($attestation.components) {
            $attestation.components.PSObject.Properties |
                Where-Object { $_.Name -eq $functionalPluginName } | Select-Object -First 1
        } else { $null }
        $functionalPluginSha = if ($functionalPluginProperty) {
            [string]$functionalPluginProperty.Value.sha256
        } else { $null }
        if ((Test-MahodSha256Text $functionalPluginSha) -and
            $acceptedAtForEvidence -ne [DateTimeOffset]::MinValue -and
            $installerBuiltAtForEvidence -ne [DateTimeOffset]::MinValue) {
            Test-MahodFunctionalEvidenceChain `
                -EvidenceByKind $evidenceByKind `
                -PackageRevision $packageRevision `
                -PlatformVersion $platformVersion `
                -PluginSha256 $functionalPluginSha `
                -InstallerBuiltAt $installerBuiltAtForEvidence `
                -AcceptedAt $acceptedAtForEvidence `
                -Errors $errors
        }
        foreach ($checkName in $requiredChecks) {
            $mapping = $null
            if ($attestation.check_evidence) {
                $mapping = $attestation.check_evidence.PSObject.Properties |
                    Where-Object { $_.Name -eq $checkName } | Select-Object -First 1
            }
            $mappedPaths = if ($mapping) { @($mapping.Value) } else { @() }
            if ($mappedPaths.Count -eq 0) {
                $errors.Add("Required live check has no evidence mapping: $checkName")
                continue
            }
            foreach ($mappedPathValue in $mappedPaths) {
                $mappedPath = ([string]$mappedPathValue).Replace('\', '/')
                if (-not $evidenceByPath.ContainsKey($mappedPath)) {
                    $errors.Add("Live check maps to unlisted evidence: $checkName -> $mappedPath")
                }
            }
            $mappedKinds = @($mappedPaths | ForEach-Object {
                $mappedPath = ([string]$_).Replace('\', '/')
                if ($evidenceByPath.ContainsKey($mappedPath)) { [string]$evidenceByPath[$mappedPath] }
            } | Select-Object -Unique)
            foreach ($requiredKind in @(Get-MahodRequiredEvidenceKindsForLiveCheck -CheckName $checkName)) {
                if ($mappedKinds -notcontains $requiredKind) {
                    $errors.Add("Required live check lacks $requiredKind evidence: $checkName")
                }
            }
        }
    }

    [pscustomobject]@{
        Valid = ($errors.Count -eq 0)
        Errors = @($errors)
        Path = $attestationPath
        RelativePath = $relativeAttestation
        HostYear = $HostYear
    }
}
