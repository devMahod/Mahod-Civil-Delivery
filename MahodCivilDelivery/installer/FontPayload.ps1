# Fixed, reviewed Rubik/OFL payload. Never copy an arbitrary build subdirectory.
function Get-CivilDeliveryFontPins {
    [ordered]@{
        'OFL.txt' = 'ADC65FD853D8A24E2CA8ABA1F9483198CB436203AD9C9EF27E08C11FE27E25A4'
        'Rubik-Bold.ttf' = '2FB105E3A66C3540E2308321E7C5BD786CE98A232930CD31C62E20D47747AD20'
        'Rubik-Medium.ttf' = 'F8A3689E56C32D880491B0E11C0A1C16AB51F6BD010CFBF7FFE6DA52AE6737BD'
        'Rubik-Regular.ttf' = '6B94E23ADEDAB4522E1B65D21158E83C75F377FC99F916C58F8EC40ECC7DD035'
        'Rubik-SemiBold.ttf' = '3645E8B65EFCDD3BCCDE6AFEB1E482FBDC34C1A810EE2F19FB9E6E8837744938'
    }
}

function Assert-CivilDeliveryFonts([string]$Folder) {
    if (-not (Test-Path -LiteralPath $Folder -PathType Container)) { throw "Missing font folder: $Folder" }
    $pins = Get-CivilDeliveryFontPins
    $files = @(Get-ChildItem -LiteralPath $Folder -File -Force | ForEach-Object Name | Sort-Object)
    if (@(Get-ChildItem -LiteralPath $Folder -Directory -Force).Count -ne 0 -or
        ($files -join '|') -cne (($pins.Keys | Sort-Object) -join '|')) {
        throw "Unexpected font payload: $Folder"
    }
    foreach ($name in $pins.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $Folder $name) -Algorithm SHA256).Hash -cne $pins[$name]) {
            throw "Font/license hash mismatch: $name"
        }
    }
}

function Copy-CivilDeliveryFonts([string]$Source, [string]$Destination) {
    Assert-CivilDeliveryFonts $Source
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($name in (Get-CivilDeliveryFontPins).Keys) {
        Copy-Item -LiteralPath (Join-Path $Source $name) -Destination (Join-Path $Destination $name)
    }
    Assert-CivilDeliveryFonts $Destination
}
