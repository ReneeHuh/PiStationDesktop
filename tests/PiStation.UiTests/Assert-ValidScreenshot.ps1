[CmdletBinding()]
param([Parameter(Mandatory)][string] $Path)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$bitmap = [System.Drawing.Bitmap]::new([System.IO.Path]::GetFullPath($Path))
try {
    if ($bitmap.Width -lt 200 -or $bitmap.Height -lt 150) { throw "Screenshot is too small: $Path" }
    $minimum = 255
    $maximum = 0
    $changed = 0
    $samples = 0
    $first = $bitmap.GetPixel(0, 0).ToArgb()
    for ($y = 0; $y -lt $bitmap.Height; $y += [Math]::Max(1, [int]($bitmap.Height / 150))) {
        for ($x = 0; $x -lt $bitmap.Width; $x += [Math]::Max(1, [int]($bitmap.Width / 200))) {
            $pixel = $bitmap.GetPixel($x, $y)
            $brightness = [int](($pixel.R + $pixel.G + $pixel.B) / 3)
            $minimum = [Math]::Min($minimum, $brightness)
            $maximum = [Math]::Max($maximum, $brightness)
            if ($pixel.ToArgb() -ne $first) { $changed++ }
            $samples++
        }
    }
    if ($maximum - $minimum -lt 12 -or $changed / $samples -lt 0.005) {
        throw "Blank or invalid screenshot: $Path. Keep the test window rendered on an unlocked desktop and recapture."
    }
}
finally { $bitmap.Dispose() }
