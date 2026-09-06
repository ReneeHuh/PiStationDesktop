[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Path,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^#?[0-9a-fA-F]{6}$')]
    [string] $Color,

    [ValidateRange(1, [int]::MaxValue)]
    [int] $MinimumSamples = 50,

    [ValidateRange(0.0, 1.0)]
    [double] $Left = 0,

    [ValidateRange(0.0, 1.0)]
    [double] $Top = 0,

    [ValidateRange(0.0, 1.0)]
    [double] $Right = 1,

    [ValidateRange(0.0, 1.0)]
    [double] $Bottom = 1
)

Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Screenshot was not found: $Path"
}
if ($Left -ge $Right -or $Top -ge $Bottom) {
    throw 'The normalized rectangle must have Left < Right and Top < Bottom.'
}

$hex = $Color.TrimStart('#')
$expectedR = [Convert]::ToInt32($hex.Substring(0, 2), 16)
$expectedG = [Convert]::ToInt32($hex.Substring(2, 2), 16)
$expectedB = [Convert]::ToInt32($hex.Substring(4, 2), 16)

Add-Type -AssemblyName System.Drawing
$bitmap = $null
try {
    $bitmap = [System.Drawing.Bitmap]::new((Resolve-Path -LiteralPath $Path).Path)
    if ($bitmap.Width -lt 1 -or $bitmap.Height -lt 1) {
        throw "Screenshot has invalid dimensions: $($bitmap.Width)x$($bitmap.Height)"
    }

    $xStart = [Math]::Max(0, [Math]::Floor($Left * $bitmap.Width))
    $yStart = [Math]::Max(0, [Math]::Floor($Top * $bitmap.Height))
    $xEnd = [Math]::Min($bitmap.Width, [Math]::Ceiling($Right * $bitmap.Width))
    $yEnd = [Math]::Min($bitmap.Height, [Math]::Ceiling($Bottom * $bitmap.Height))
    $matches = 0
    $samples = 0

    for ($y = $yStart; $y -lt $yEnd; $y += 2) {
        for ($x = $xStart; $x -lt $xEnd; $x += 2) {
            $pixel = $bitmap.GetPixel($x, $y)
            $samples++
            if ([Math]::Abs($pixel.R - $expectedR) -le 2 -and
                [Math]::Abs($pixel.G - $expectedG) -le 2 -and
                [Math]::Abs($pixel.B - $expectedB) -le 2) {
                $matches++
            }
        }
    }

    if ($matches -lt $MinimumSamples) {
        throw "Expected at least $MinimumSamples samples near #$hex, found $matches of $samples in normalized rectangle ($Left,$Top)-($Right,$Bottom)."
    }

    Write-Output "Palette assertion passed: #$hex matched $matches of $samples samples."
}
finally {
    if ($bitmap -is [System.IDisposable]) {
        $bitmap.Dispose()
    }
}
