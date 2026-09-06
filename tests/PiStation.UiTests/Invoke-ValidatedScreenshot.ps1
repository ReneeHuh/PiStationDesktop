[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $FilePath,

    [Parameter(Mandatory = $true)]
    [string[]] $ArgumentList
)

Set-StrictMode -Version Latest

$outputIndex = [Array]::IndexOf($ArgumentList, '--output')
if ($outputIndex -lt 0 -or $outputIndex + 1 -ge $ArgumentList.Count) {
    throw 'Screenshot validation requires an --output path.'
}

$outputPath = $ArgumentList[$outputIndex + 1]
try {
    & (Join-Path $PSScriptRoot 'Assert-ValidScreenshot.ps1') -Path $outputPath
}
catch {
    if ($_.Exception.Message -notmatch 'Blank or invalid screenshot') {
        throw
    }

    $retry = [System.Collections.Generic.List[string]]::new()
    $retry.AddRange($ArgumentList)
    if (-not $retry.Contains('--capture-screen')) {
        $retry.Add('--capture-screen')
    }

    $screenshotIndex = $retry.IndexOf('screenshot')
    if ($screenshotIndex -lt 0) {
        throw
    }

    $hasSelector = $screenshotIndex + 1 -lt $retry.Count -and
        -not $retry[$screenshotIndex + 1].StartsWith('-', [StringComparison]::Ordinal)
    if (-not $hasSelector) {
        $retry.Insert($screenshotIndex + 1, 'AppMainWindow')
    }

    $retryOutput = & $FilePath $retry.ToArray() 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Fallback screenshot failed with exit code $LASTEXITCODE.`n$($retryOutput | Out-String)"
    }

    & (Join-Path $PSScriptRoot 'Assert-ValidScreenshot.ps1') -Path $outputPath
    ($retryOutput | Out-String).Trim()
}
