# Received over the verified SSH channel. No download service or administrator rights needed.
# Inputs are literal values prepended by the desktop: bundleHash, packageSize, hostArgs.
$ErrorActionPreference = 'Stop'
$cacheRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PiStation\ssh-hosts'))
$versionRoot = Join-Path $cacheRoot $bundleHash
$serverExe = Join-Path $versionRoot 'PiStation.Server.exe'
$marker = Join-Path $versionRoot 'complete.hash'
$stageRoot = $null
$archivePath = $null
function Test-InstalledHost {
    return (Test-Path -LiteralPath $serverExe -PathType Leaf) -and
        (Test-Path -LiteralPath $marker -PathType Leaf) -and
        ([IO.File]::ReadAllText($marker) -eq $bundleHash)
}
function Remove-TransferPath([string] $path) {
    if ($path -and [IO.Path]::GetFullPath($path).StartsWith($cacheRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
}
try {
    if (-not (Test-InstalledHost)) {
        [IO.Directory]::CreateDirectory($cacheRoot) | Out-Null
        $transferId = [Guid]::NewGuid().ToString('N')
        $stageRoot = Join-Path $cacheRoot ('pending-' + $transferId)
        $archivePath = Join-Path $cacheRoot ($transferId + '.zip')
        $file = [IO.File]::Open($archivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            [Console]::Out.WriteLine('PISTATION_PACKAGE ' + $bundleHash)
            [Console]::Out.Flush()
            $received = 0L
            while ($received -lt $packageSize) {
                $line = [Console]::In.ReadLine()
                if ($null -eq $line -or $line -eq 'stop' -or $line.Length -gt 32768) { throw 'Transfer canceled or invalid.' }
                $bytes = [Convert]::FromBase64String($line)
                if ($bytes.Length -eq 0 -or ($received + $bytes.Length) -gt $packageSize) { throw 'Invalid transfer length.' }
                $file.Write($bytes, 0, $bytes.Length)
                $received += $bytes.Length
            }
        } finally { $file.Dispose() }
        if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $bundleHash) { throw 'Package checksum mismatch.' }
        Expand-Archive -LiteralPath $archivePath -DestinationPath $stageRoot
        foreach ($required in @('PiStation.Server.exe', 'PiStation.Server.dll', 'PiStation.Server.runtimeconfig.json', 'PiStation.Host.dll', 'coreclr.dll')) {
            if (-not (Test-Path -LiteralPath (Join-Path $stageRoot $required) -PathType Leaf)) { throw 'Incomplete host package.' }
        }
        [IO.File]::WriteAllText((Join-Path $stageRoot 'complete.hash'), $bundleHash)
        try { [IO.Directory]::Move($stageRoot, $versionRoot) }
        catch { if (-not (Test-InstalledHost)) { throw } } # A concurrent installer may finish first.
    }
} catch {
    [Console]::Error.WriteLine('PISTATION_INSTALL_FAILED')
    exit 1
} finally {
    Remove-TransferPath $archivePath
    Remove-TransferPath $stageRoot
}
# Never replace a running executable, touch environment data, or stop an external host.
# attach discovers the current user's desktop/server first and otherwise owns its new server.
& $serverExe @hostArgs
exit $LASTEXITCODE
