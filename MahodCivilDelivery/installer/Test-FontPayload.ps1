# Host-free regression checks for the real staging functions. Writes only to out.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'FontPayload.ps1')
$root = Split-Path $PSScriptRoot -Parent
$repo = Split-Path $root -Parent
$source = Join-Path $repo 'MahodAI.Civil3D.Plugin\CivilDelivery\UI\Fonts'
$sandbox = Join-Path $root ('out\font-payload-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
$passed = 0
function Expect-Rejected([string]$Name, [scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw "FAIL $Name" }
    Write-Host "PASS $Name"
    $script:passed++
}
Assert-CivilDeliveryFonts $source
Write-Host 'PASS source font/license pins'
$passed++
$destination = Join-Path $sandbox 'valid'
Copy-CivilDeliveryFonts $source $destination
Assert-CivilDeliveryFonts $destination
Write-Host 'PASS copy stages all five exact files'
$passed++
Expect-Rejected 'missing font folder' { Assert-CivilDeliveryFonts (Join-Path $sandbox 'absent') }
$missing = Join-Path $sandbox 'missing-license'
Copy-Item -LiteralPath $source -Destination $missing -Recurse
Move-Item -LiteralPath (Join-Path $missing 'OFL.txt') -Destination (Join-Path $sandbox 'held-license.txt')
Expect-Rejected 'missing license' { Assert-CivilDeliveryFonts $missing }
$corrupt = Join-Path $sandbox 'corrupt-font'
Copy-Item -LiteralPath $source -Destination $corrupt -Recurse
[IO.File]::AppendAllText((Join-Path $corrupt 'Rubik-Regular.ttf'), 'changed')
Expect-Rejected 'changed font bytes' { Assert-CivilDeliveryFonts $corrupt }
$extra = Join-Path $sandbox 'extra'
Copy-Item -LiteralPath $source -Destination $extra -Recurse
Set-Content -LiteralPath (Join-Path $extra 'private.json') -Value '{}'
Expect-Rejected 'extra private file' { Assert-CivilDeliveryFonts $extra }
$nested = Join-Path $sandbox 'nested'
Copy-Item -LiteralPath $source -Destination $nested -Recurse
New-Item -ItemType Directory -Path (Join-Path $nested 'private') | Out-Null
Expect-Rejected 'nested directory' { Assert-CivilDeliveryFonts $nested }
$badDestination = Join-Path $sandbox 'not-written'
Expect-Rejected 'invalid source rejected before destination write' { Copy-CivilDeliveryFonts $extra $badDestination }
if (Test-Path -LiteralPath $badDestination) { throw 'Invalid copy wrote destination' }
Write-Host "$passed passed, 0 failed; evidence sandbox: $sandbox"
