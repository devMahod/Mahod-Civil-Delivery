<#
.SYNOPSIS
    Fail-closed Microsoft Defender custom scan for exact release artifacts.

.DESCRIPTION
    Resolves the newest installed MpCmdRun.exe (with the inbox Defender path as a
    fallback), scans every explicitly named file, and writes one SHA256-bound JSON
    receipt outside the employee ZIP.  Missing Defender, timeout, a non-zero tool
    exit, a missing target, or bytes changing during the scan all fail the caller.

    Windows PowerShell 5.1 compatible.  This script never installs or deploys.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$TargetPath,
    [Parameter(Mandatory = $true)][string]$EvidencePath,
    [string]$RepositoryRoot,
    [string]$Purpose = 'Mahod Civil Delivery release artifact scan',
    [ValidateRange(10, 1800)][int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'

function Full-FilePath([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [System.IO.Path]::IsPathRooted($Path)) {
        throw "$Label must be an absolute path."
    }
    if ($Path -match '["\r\n\0]') { throw "$Label contains an invalid character." }
    return [System.IO.Path]::GetFullPath($Path)
}

function Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Iso-Utc($Value) {
    if ($null -eq $Value) { return $null }
    try { return ([DateTime]$Value).ToUniversalTime().ToString('o') }
    catch { return $null }
}

function Relative-Or-Full([string]$Path, [string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Root)) { return $Path }
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $prefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
    if ($Path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $Path.Substring($prefix.Length).Replace('\', '/')
    }
    return $Path
}

function Resolve-MpCmdRun {
    $candidates = @()
    $platformRoot = if ($env:ProgramData) {
        Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform'
    } else {
        'C:\ProgramData\Microsoft\Windows Defender\Platform'
    }
    if (Test-Path -LiteralPath $platformRoot -PathType Container) {
        $candidates += @(Get-ChildItem -LiteralPath $platformRoot -Directory -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            ForEach-Object { Join-Path $_.FullName 'MpCmdRun.exe' })
    }
    if ($env:ProgramFiles) {
        $candidates += (Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe')
    }
    $command = Get-Command MpCmdRun.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command -and $command.Source) { $candidates += [string]$command.Source }

    foreach ($candidate in @($candidates | Select-Object -Unique)) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }
    return $null
}

$startedUtc = (Get-Date).ToUniversalTime().ToString('o')
$scanner = $null
$scannerInfo = $null
$defenderStatus = $null
$statusError = $null
$results = @()
$failure = $null

try {
    $evidenceFull = Full-FilePath $EvidencePath 'EvidencePath'
    $evidenceParent = Split-Path -Parent $evidenceFull
    if (-not (Test-Path -LiteralPath $evidenceParent -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $evidenceParent | Out-Null
    }
    Remove-Item -LiteralPath $evidenceFull -Force -ErrorAction SilentlyContinue

    $scanner = Resolve-MpCmdRun
    if (-not $scanner) { throw 'Microsoft Defender MpCmdRun.exe is unavailable.' }
    $scannerItem = Get-Item -LiteralPath $scanner
    $scannerInfo = [ordered]@{
        path            = $scannerItem.FullName
        sha256          = Sha256 $scannerItem.FullName
        file_version    = [string]$scannerItem.VersionInfo.FileVersion
        product_version = [string]$scannerItem.VersionInfo.ProductVersion
    }

    try {
        $status = Get-MpComputerStatus -ErrorAction Stop
        $defenderStatus = [ordered]@{
            antivirus_enabled                 = [bool]$status.AntivirusEnabled
            realtime_protection_enabled       = [bool]$status.RealTimeProtectionEnabled
            antivirus_signature_version       = [string]$status.AntivirusSignatureVersion
            antivirus_signature_updated_utc   = Iso-Utc $status.AntivirusSignatureLastUpdated
            antispyware_signature_version      = [string]$status.AntispywareSignatureVersion
            engine_version                     = [string]$status.AMEngineVersion
            product_version                    = [string]$status.AMProductVersion
        }
    } catch {
        $statusError = $_.Exception.Message
    }

    foreach ($requested in $TargetPath) {
        $target = Full-FilePath $requested 'TargetPath'
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
            throw "Defender scan target is missing: $target"
        }
        $beforeItem = Get-Item -LiteralPath $target
        $beforeSha = Sha256 $target
        $scanStarted = (Get-Date).ToUniversalTime().ToString('o')
        $stdoutPath = Join-Path ([System.IO.Path]::GetTempPath()) ('mcd-defender-' + [guid]::NewGuid().ToString('N') + '.out.txt')
        $stderrPath = $stdoutPath + '.err.txt'
        $timedOut = $false
        $exitCode = -1
        [string]$stdout = ''
        [string]$stderr = ''
        try {
            $arguments = @('-Scan', '-ScanType', '3', '-File', ('"' + $target + '"'))
            $process = Start-Process -FilePath $scanner -ArgumentList $arguments -PassThru `
                -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                $timedOut = $true
                try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { }
                try { $process.WaitForExit(5000) | Out-Null } catch { }
            } else {
                $process.Refresh()
                $exitCode = [int]$process.ExitCode
            }
            if (Test-Path -LiteralPath $stdoutPath) { $stdout = [string](Get-Content -LiteralPath $stdoutPath -Raw -ErrorAction SilentlyContinue) }
            if (Test-Path -LiteralPath $stderrPath) { $stderr = [string](Get-Content -LiteralPath $stderrPath -Raw -ErrorAction SilentlyContinue) }
        } finally {
            Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
        }

        $targetStillExists = Test-Path -LiteralPath $target -PathType Leaf
        $afterSha = if ($targetStillExists) { Sha256 $target } else { $null }
        $clean = -not $timedOut -and $exitCode -eq 0 -and $targetStillExists -and $afterSha -eq $beforeSha
        $results += [ordered]@{
            relative_path    = Relative-Or-Full $target $RepositoryRoot
            sha256           = $beforeSha
            size_bytes       = [long]$beforeItem.Length
            scan_started_utc = $scanStarted
            scan_finished_utc = (Get-Date).ToUniversalTime().ToString('o')
            exit_code        = $exitCode
            timed_out        = $timedOut
            bytes_unchanged  = [bool]($targetStillExists -and $afterSha -eq $beforeSha)
            clean            = [bool]$clean
            stdout           = [string]$(if ($stdout.Length -gt 8000) { $stdout.Substring($stdout.Length - 8000) } else { $stdout })
            stderr           = [string]$(if ($stderr.Length -gt 8000) { $stderr.Substring($stderr.Length - 8000) } else { $stderr })
        }
        if ($timedOut) { throw "Microsoft Defender timed out scanning: $target" }
        if ($exitCode -ne 0) { throw "Microsoft Defender returned exit $exitCode scanning: $target" }
        if (-not $targetStillExists -or $afterSha -ne $beforeSha) {
            throw "Microsoft Defender scan target disappeared or changed bytes: $target"
        }
    }
} catch {
    $failure = $_.Exception.Message
}

$evidence = [ordered]@{
    schema_version      = 1
    purpose             = $Purpose
    verdict             = if ($failure) { 'FAILED' } else { 'CLEAN' }
    started_utc         = $startedUtc
    completed_utc       = (Get-Date).ToUniversalTime().ToString('o')
    timeout_seconds     = $TimeoutSeconds
    scanner             = $scannerInfo
    defender_status     = $defenderStatus
    defender_status_error = $statusError
    scans               = $results
    failure             = $failure
}

try {
    $evidenceFull = Full-FilePath $EvidencePath 'EvidencePath'
    $evidenceParent = Split-Path -Parent $evidenceFull
    if (-not (Test-Path -LiteralPath $evidenceParent -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $evidenceParent | Out-Null
    }
    $candidate = $evidenceFull + '.candidate-' + [guid]::NewGuid().ToString('N')
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $candidate -Encoding utf8
    Move-Item -LiteralPath $candidate -Destination $evidenceFull -Force
} catch {
    if (-not $failure) { $failure = "Could not write Defender evidence: $($_.Exception.Message)" }
}

if ($failure) { throw "Defender release scan failed closed. $failure" }
Write-Host "Microsoft Defender: CLEAN ($($results.Count) exact file(s)); evidence: $evidenceFull"
return [pscustomobject]$evidence
