<# Side-effect-free PDF structure validation shared by guide build and release identity. #>

function Test-MahodGuidePdf {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    $errors = New-Object System.Collections.Generic.List[string]
    $pageCount = 0
    $renderVerified = $false
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $errors.Add("PDF is missing: $Path")
        return [pscustomobject]@{ Valid=$false; Errors=@($errors); PageCount=0; SizeBytes=0; RenderVerified=$false }
    }
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -lt 4096) { $errors.Add("PDF is implausibly small: $($item.Length) bytes") }
    $bytes = [System.IO.File]::ReadAllBytes($item.FullName)
    $headerLength = [Math]::Min(8, $bytes.Length)
    $header = if ($headerLength) { [Text.Encoding]::ASCII.GetString($bytes, 0, $headerLength) } else { '' }
    if (-not $header.StartsWith('%PDF-', [StringComparison]::Ordinal)) { $errors.Add('PDF header is missing.') }
    $tailStart = [Math]::Max(0, $bytes.Length - 4096)
    $tail = if ($bytes.Length) { [Text.Encoding]::ASCII.GetString($bytes, $tailStart, $bytes.Length - $tailStart) } else { '' }
    if ($tail -notmatch '%%EOF\s*$') { $errors.Add('PDF EOF marker is missing or truncated.') }

    $pdfInfo = Get-Command pdfinfo.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pdfInfo) {
        $priorPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $output = & $pdfInfo.Source $item.FullName 2>&1 | Out-String
            $pdfInfoExit = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $priorPreference
        }
        if ($pdfInfoExit -ne 0) {
            $errors.Add("pdfinfo rejected the guide (exit $pdfInfoExit): $($output.Trim())")
        } elseif ($output -match '(?m)^Pages:\s+(?<pages>\d+)\s*$') {
            $pageCount = [int]$Matches['pages']
        } else {
            $errors.Add('pdfinfo did not report a page count.')
        }
    } else {
        # Edge normally leaves page dictionaries readable even when content streams
        # are compressed.  This is a strict fallback when Poppler is unavailable.
        $latin = [Text.Encoding]::GetEncoding(28591).GetString($bytes)
        $pageCount = [regex]::Matches($latin, '/Type\s*/Page(?!s)\b').Count
    }
    if ($pageCount -lt 1) { $errors.Add('PDF contains no verifiable page.') }

    # When Poppler's renderer is available, require a real first-page decode too.
    # This catches structurally plausible but unreadable/corrupt PDFs that merely
    # contain the right header, EOF marker and page dictionaries.
    $renderer = Get-Command pdftoppm.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($renderer -and $errors.Count -eq 0) {
        $renderRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('mcd-pdf-render-' + [guid]::NewGuid().ToString('N'))
        $renderPrefix = Join-Path $renderRoot 'page'
        try {
            New-Item -ItemType Directory -Force -Path $renderRoot | Out-Null
            if ($item.FullName -match '"' -or $renderPrefix -match '"') { throw 'PDF render path contains a quote.' }
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $renderer.Source
            $psi.Arguments = '-f 1 -singlefile -png "' + $item.FullName + '" "' + $renderPrefix + '"'
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow = $true
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $process = [System.Diagnostics.Process]::Start($psi)
            if (-not $process.WaitForExit(30000)) {
                try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { }
                $errors.Add('pdftoppm timed out rendering the first guide page.')
            } else {
                $stdout = $process.StandardOutput.ReadToEnd()
                $stderr = $process.StandardError.ReadToEnd()
                $exitCode = [int]$process.ExitCode
                $rendered = $renderPrefix + '.png'
                if ($exitCode -ne 0) {
                    $detail = (([string]$stderr) + ' ' + ([string]$stdout)).Trim()
                    $errors.Add("pdftoppm rejected the first guide page (exit $exitCode): $detail")
                } elseif (-not (Test-Path -LiteralPath $rendered -PathType Leaf)) {
                    $errors.Add('pdftoppm reported success but produced no first-page PNG.')
                } else {
                    $png = [System.IO.File]::ReadAllBytes($rendered)
                    $magic = [byte[]](0x89,0x50,0x4e,0x47,0x0d,0x0a,0x1a,0x0a)
                    $validMagic = $png.Length -gt 8
                    for ($i = 0; $validMagic -and $i -lt $magic.Length; $i++) {
                        if ($png[$i] -ne $magic[$i]) { $validMagic = $false }
                    }
                    if (-not $validMagic) { $errors.Add('Rendered first page is not a valid PNG stream.') }
                    else { $renderVerified = $true }
                }
            }
        } catch {
            $errors.Add("Could not render the first guide page: $($_.Exception.Message) $($_.InvocationInfo.PositionMessage)")
        } finally {
            if (Test-Path -LiteralPath $renderRoot -PathType Container) {
                Remove-Item -LiteralPath $renderRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    return [pscustomobject]@{
        Valid = ($errors.Count -eq 0)
        Errors = @($errors)
        PageCount = $pageCount
        SizeBytes = [long]$item.Length
        RenderVerified = [bool]$renderVerified
    }
}
