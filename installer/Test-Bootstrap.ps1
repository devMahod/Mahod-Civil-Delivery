<#
.SYNOPSIS
    Offline gate for the employee-facing setup bootstrapper.

.DESCRIPTION
    Copies only the final setup EXE into an otherwise empty TEMP directory, then
    invokes a pre-install probe that launches a harmless child process and returns
    its exit code.  The probe executes before UI, payload extraction, elevation or
    access to any Autodesk/ApplicationPlugins path.
#>
[CmdletBinding()]
param(
    [string]$SetupExe,
    [string]$OutFile
)

$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path $here
$release = Get-Content (Join-Path $repo 'release\release.json') -Raw | ConvertFrom-Json
$version = [string]$release.package_revision
if (-not $SetupExe) {
    $SetupExe = Join-Path $here "out\Mahod_Civil_Delivery_Setup_$version.exe"
}
if (-not $OutFile) {
    $OutFile = Join-Path $repo 'evidence\bootstrap_evidence.txt'
}

$log = New-Object System.Collections.ArrayList
$failures = 0
function L([string]$message) { [void]$log.Add($message); Write-Host $message }
function Check([string]$name, [bool]$pass, [string]$detail = '') {
    L ($(if ($pass) { '  PASS  ' } else { '  FAIL  ' }) + $name + $(if ($detail) { "  [$detail]" } else { '' }))
    if (-not $pass) { $script:failures++ }
}

$sandbox = Join-Path $env:TEMP ('mcd_bootstrap_probe_' + (Get-Date -Format 'yyyyMMddHHmmssfff'))
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

try {
    $present = Test-Path -LiteralPath $SetupExe -PathType Leaf
    Check 'final setup EXE exists' $present $SetupExe
    if (-not $present) { throw "Setup EXE not found: $SetupExe" }

    $probeExe = Join-Path $sandbox (Split-Path $SetupExe -Leaf)
    Copy-Item -LiteralPath $SetupExe -Destination $probeExe
    $filesBefore = @(Get-ChildItem -LiteralPath $sandbox -File)
    Check 'probe sandbox contains only the setup EXE' ($filesBefore.Count -eq 1) `
        (($filesBefore | ForEach-Object Name) -join ', ')

    $project = [xml](Get-Content (Join-Path $here 'bootstrap\MahodCivilDeliverySetup.csproj'))
    $properties = @($project.Project.PropertyGroup)
    $target = [string]($properties.TargetFramework | Where-Object { $_ } | Select-Object -First 1)
    $selfContained = [string]($properties.SelfContained | Where-Object { $_ } | Select-Object -First 1)
    $singleFile = [string]($properties.PublishSingleFile | Where-Object { $_ } | Select-Object -First 1)
    $native = [string]($properties.IncludeNativeLibrariesForSelfExtract | Where-Object { $_ } | Select-Object -First 1)
    $compressed = [string]($properties.EnableCompressionInSingleFile | Where-Object { $_ } | Select-Object -First 1)
    Check 'bootstrap target is net8.0-windows' ($target -eq 'net8.0-windows') $target
    Check 'bootstrap declares SelfContained=true' ($selfContained -eq 'true') $selfContained
    Check 'bootstrap declares PublishSingleFile=true' ($singleFile -eq 'true') $singleFile
    Check 'bootstrap includes native libraries in one-file publish' ($native -eq 'true') $native
    Check 'bootstrap compresses its embedded runtime' ($compressed -eq 'true') $compressed

    $payloadZip = Join-Path $here 'mcd_payload.zip'
    $payloadPresent = Test-Path -LiteralPath $payloadZip -PathType Leaf
    Check 'embedded payload ZIP exists for size comparison' $payloadPresent $payloadZip
    if ($payloadPresent) {
        $exeBytes = (Get-Item -LiteralPath $probeExe).Length
        $payloadBytes = (Get-Item -LiteralPath $payloadZip).Length
        $runtimeMargin = 30MB
        Check 'one-file EXE materially exceeds payload (embedded runtime is present)' `
            ($exeBytes -ge ($payloadBytes + $runtimeMargin)) `
            "exe=$exeBytes payload=$payloadBytes margin=$runtimeMargin"
    }

    $expectedExit = 37
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $probeExe
    $psi.Arguments = "--self-test-child-exit=$expectedExit"
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $missingRuntime = Join-Path $sandbox 'runtime-does-not-exist'
    $psi.EnvironmentVariables['DOTNET_ROOT'] = $missingRuntime
    $psi.EnvironmentVariables['DOTNET_ROOT_X64'] = $missingRuntime
    $psi.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $psi.EnvironmentVariables['DOTNET_DISABLE_GUI_ERRORS'] = '1'
    $process = [System.Diagnostics.Process]::Start($psi)
    $finished = $process.WaitForExit(30000)
    Check 'standalone bootstrap probe exits within 30 seconds' $finished
    if ($finished) {
        Check 'bootstrap propagates the harmless child exit code' `
            ($process.ExitCode -eq $expectedExit) "expected=$expectedExit actual=$($process.ExitCode)"
    } else {
        try { $process.Kill() } catch { }
    }
    $process.Dispose()

    # Pure, read-only context-resolution behavior.  The setup validates the current
    # SID against machine-owned ProfileList data, accepts the real profile roots, and
    # refuses a forged/partial context before UI, UAC or extraction.
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $contextArgs = @(
        '--self-test-validate-original-context',
        "--original-user-sid=$sid",
        "--original-user-name=$env:USERNAME",
        "--original-appdata=$env:APPDATA",
        "--original-localappdata=$env:LOCALAPPDATA"
    )
    function Invoke-ContextProbe([string[]]$arguments) {
        $contextPsi = New-Object System.Diagnostics.ProcessStartInfo
        $contextPsi.FileName = $probeExe
        # Values are profile paths/SIDs with no quotes (validated by production code).
        # Explicit quoting keeps paths with spaces a single native argument on PS 5.1.
        $contextPsi.Arguments = (($arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' ')
        $contextPsi.UseShellExecute = $false
        $contextPsi.CreateNoWindow = $true
        $contextProcess = [System.Diagnostics.Process]::Start($contextPsi)
        [void]$contextProcess.WaitForExit(30000)
        $exitCode = $contextProcess.ExitCode
        $contextProcess.Dispose()
        return $exitCode
    }
    $contextExit = Invoke-ContextProbe $contextArgs
    Check 'bootstrap resolves the real original-user context without writes' ($contextExit -eq 0) "exit=$contextExit"

    $partialExit = Invoke-ContextProbe @('--self-test-validate-original-context', "--original-user-sid=$sid")
    Check 'bootstrap refuses partial/forged original-user context' ($partialExit -ne 0) "exit=$partialExit"

    $filesAfter = @(Get-ChildItem -LiteralPath $sandbox -File)
    Check 'probe creates no installer files beside the EXE' ($filesAfter.Count -eq 1) `
        (($filesAfter | ForEach-Object Name) -join ', ')
}
catch {
    Check 'bootstrap probe completed without exception' $false $_.Exception.Message
}
finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

L ''
if ($failures -eq 0) { L 'BOOTSTRAP GATE PASSED' }
else { L "BOOTSTRAP GATE FAILED: $failures check(s)" }
New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
$log -join [Environment]::NewLine | Set-Content -LiteralPath $OutFile -Encoding utf8
L "evidence written: $OutFile"
if ($failures -gt 0) { exit 1 }
exit 0
