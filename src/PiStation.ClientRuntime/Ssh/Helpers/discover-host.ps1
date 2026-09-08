# Read the running host's discovery pipe over SSH. This script never starts a host.
$ErrorActionPreference = 'Stop'
$pipe = $null
$reader = $null
try {
    if (-not $dataRoot) {
        # Match HostDataPaths.ResolveDefaultRoot, including older MSIX data locations.
        $local = [Environment]::GetFolderPath('LocalApplicationData')
        $normal = [IO.Path]::Combine($local, 'PiStationDesktop')
        $candidates = @($normal)
        $packages = [IO.Path]::Combine($local, 'Packages')
        if ([IO.Directory]::Exists($packages)) {
            foreach ($package in [IO.Directory]::EnumerateDirectories($packages, '584BC26F-2CB5-42F0-A9E5-6DB195B0890E_*')) {
                $candidates += [IO.Path]::Combine($package, 'LocalCache\Local\PiStationDesktop')
            }
        }
        $existing = @($candidates | Where-Object { [IO.File]::Exists([IO.Path]::Combine($_, 'host.db')) } | Select-Object -Unique)
        if ($existing.Count -gt 1) { throw 'PISTATION_HOST_AMBIGUOUS' }
        $dataRoot = if ($existing.Count -eq 1) { $existing[0] } else { $normal }
    }
    $root = [IO.Path]::GetFullPath($dataRoot)
    # Preserve the separator on a drive/UNC root, as Path.TrimEndingDirectorySeparator does.
    if ($root.Length -gt [IO.Path]::GetPathRoot($root).Length) { $root = $root.TrimEnd([char[]]'\/') }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($root.ToUpperInvariant()))).Replace('-', '') }
    finally { $sha.Dispose() }
    # Windows PowerShell uses .NET Framework, which lacks PipeOptions.CurrentUserOnly.
    # Identification prevents impersonation; verify the pipe owner before reading credentials.
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', ('PiStation.Ssh.' + $hash),
        [IO.Pipes.PipeDirection]::In, [IO.Pipes.PipeOptions]::Asynchronous,
        [Security.Principal.TokenImpersonationLevel]::Identification)
    try { $pipe.Connect(1500) } catch [TimeoutException] { throw 'PISTATION_HOST_NOT_RUNNING' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if ($pipe.GetAccessControl().GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $identity.User.Value) {
            throw 'PISTATION_HOST_WRONG_OWNER'
        }
    } finally { $identity.Dispose() }
    $reader = [IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8)
    $buffer = [char[]]::new(8193)
    $length = 0
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($length -lt $buffer.Length) {
        $read = $reader.ReadAsync($buffer, $length, $buffer.Length - $length)
        $remaining = 15000 - [int]$timer.ElapsedMilliseconds
        if ($remaining -le 0 -or -not $read.Wait($remaining)) { throw 'PISTATION_HOST_DISCOVERY_FAILED' }
        if ($read.Result -eq 0) { break }
        $length += $read.Result
    }
    if ($length -eq 0 -or $length -gt 8192) { throw 'PISTATION_HOST_DISCOVERY_FAILED' }
    $json = [string]::new($buffer, 0, $length)
    $info = $json | ConvertFrom-Json
    if ($info.startedByConnection -ne $false) { throw 'PISTATION_HOST_DISCOVERY_FAILED' }
    [Console]::Out.WriteLine('PISTATION_SSH ' + ($info | ConvertTo-Json -Compress -Depth 8))
    [Console]::Out.Flush()
} catch {
    $marker = $_.Exception.Message
    if ($marker -notin @('PISTATION_HOST_NOT_RUNNING', 'PISTATION_HOST_AMBIGUOUS', 'PISTATION_HOST_WRONG_OWNER')) {
        $marker = 'PISTATION_HOST_DISCOVERY_FAILED'
    }
    [Console]::Error.WriteLine($marker)
    exit 1
} finally {
    if ($reader) { $reader.Dispose() }
    elseif ($pipe) { $pipe.Dispose() }
}
# Only this discovery session waits for the client; the already-running host is independent.
$null = [Console]::In.ReadLine()
exit 0
