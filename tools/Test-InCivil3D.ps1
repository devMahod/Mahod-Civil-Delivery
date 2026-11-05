<#
.SYNOPSIS
  Loads this checkout's Civil Delivery build into Civil 3D 2026 and opens a drawing with it, for a local check.

.DESCRIPTION
  Builds the standalone plugin (MahodCivilDelivery\Mahod.CivilDelivery), copies the output to
  %USERPROFILE%\MahodCivilDelivery-test\plugin (outside OneDrive, no spaces: Civil 3D keeps the DLLs locked while it
  runs), then NETLOADs it into the running Civil 3D 2026 - or starts one - opens the drawing and opens the panel
  (MCD_CIVIL_DELIVERY). Nothing is installed: the next Civil 3D start is clean again.

.EXAMPLE
  .\tools\Test-InCivil3D.ps1 -Drawing "P:\data\6426\Civil3d\DD\CIVIL-6426-designid-mikta4.dwg"
  .\tools\Test-InCivil3D.ps1 -SkipBuild           # reuse the last build, current drawing
#>
param(
    [string]$Drawing,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'MahodCivilDelivery\Mahod.CivilDelivery\Mahod.CivilDelivery.csproj'
$bin = Join-Path $repo 'MahodCivilDelivery\Mahod.CivilDelivery\bin\Release'
$test = Join-Path $env:USERPROFILE 'MahodCivilDelivery-test\plugin'

if (-not $SkipBuild) {
    # dotnet is run from TEMP: the repo's global.json asks for an SDK this machine may not have
    Push-Location $env:TEMP
    try {
        & dotnet build $project -c Release -p:AutoCADVersion=2026 -nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "The plugin build failed." }
    }
    finally { Pop-Location }
}
if (-not (Test-Path (Join-Path $bin 'Mahod.CivilDelivery.dll'))) { throw "No build in $bin" }
New-Item -ItemType Directory -Force -Path $test | Out-Null
Copy-Item -Path (Join-Path $bin '*') -Destination $test -Recurse -Force
$dll = Join-Path $test 'Mahod.CivilDelivery.dll'

# the running Civil 3D, or a new Civil 3D 2026 Metric
$acad = $null
try { $acad = [Runtime.InteropServices.Marshal]::GetActiveObject('AutoCAD.Application') } catch { }
if (-not $acad) {
    $exe = 'C:\Program Files\Autodesk\AutoCAD 2026\acad.exe'
    $acadArgs = @('/ld', '"C:\Program Files\Autodesk\AutoCAD 2026\AecBase.dbx"', '/p', '"<<C3D_Metric>>"', '/product', 'C3D', '/language', 'en-US')
    Start-Process -FilePath $exe -ArgumentList $acadArgs | Out-Null
    $deadline = (Get-Date).AddMinutes(4)
    while (-not $acad -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        try { $acad = [Runtime.InteropServices.Marshal]::GetActiveObject('AutoCAD.Application') } catch { }
    }
    if (-not $acad) { throw "Civil 3D did not come up within 4 minutes." }
}

function Invoke-Acad([scriptblock]$Call) {
    # AutoCAD rejects COM calls while it is busy (opening a drawing, a running command): retry for a while
    $deadline = (Get-Date).AddMinutes(6)
    while ($true) {
        try { return (& $Call) }
        catch { if ((Get-Date) -gt $deadline) { throw }; Start-Sleep -Seconds 2 }
    }
}

if ($Drawing) {
    if (-not (Test-Path $Drawing)) { throw "Drawing not found: $Drawing" }
    $doc = Invoke-Acad { $acad.Documents.Open($Drawing) }
} else {
    $doc = Invoke-Acad { $acad.ActiveDocument }
}
$lispPath = $dll -replace '\\', '/'
Invoke-Acad { $doc.SendCommand("(command `"_.NETLOAD`" `"$lispPath`") ") } | Out-Null
Start-Sleep -Seconds 3
Invoke-Acad { $doc.SendCommand("MCD_CIVIL_DELIVERY ") } | Out-Null
"Loaded $dll into Civil 3D 2026 and asked for the Civil Delivery panel (MCD_CIVIL_DELIVERY)."
"Drawing: $($doc.FullName)"
