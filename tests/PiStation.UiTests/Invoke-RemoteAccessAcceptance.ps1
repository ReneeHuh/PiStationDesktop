[CmdletBinding()]
param(
    [ValidateSet('Start', 'Smoke', 'Verify', 'Stop')][string] $Mode = 'Start',
    [string] $RunRoot,
    [switch] $NoBuild,
    [switch] $NoLaunch,
    [switch] $ReadOnly
)

$ErrorActionPreference = 'Stop'
$solutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$resultsRoot = Join-Path $solutionRoot 'TestResults/remote-native'
if (-not $RunRoot) {
    if ($Mode -in @('Verify', 'Stop')) { throw "$Mode requires -RunRoot from a Start run." }
    $RunRoot = Join-Path $resultsRoot ([guid]::NewGuid().ToString('N'))
}
$RunRoot = [IO.Path]::GetFullPath($RunRoot)
if (-not $RunRoot.StartsWith($resultsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Fixture roots must be inside $resultsRoot."
}
$fixtureProject = Join-Path $solutionRoot 'tests/PiStation.RemoteUiFixture/PiStation.RemoteUiFixture.csproj'
$fixtureExe = Join-Path $solutionRoot 'tests/PiStation.RemoteUiFixture/bin/Debug/net10.0-windows/PiStation.RemoteUiFixture.exe'
$fakePi = Join-Path $solutionRoot 'tests/PiStation.FakePi/bin/Debug/net10.0/PiStation.FakePi.exe'

function Invoke-CheckedNative {
    param([string] $FilePath, [string[]] $ArgumentList)
    $output = & $FilePath @ArgumentList 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$FilePath failed: $($output | Out-String)" }
    return ($output | Out-String).Trim()
}

if ($Mode -eq 'Stop') {
    if (-not (Test-Path -LiteralPath (Join-Path $RunRoot 'ready.json'))) { throw 'This is not a ready fixture root.' }
    [IO.File]::WriteAllText((Join-Path $RunRoot 'stop'), '')
    Write-Host 'Requested fixture host shutdown. Close the isolated test app windows when finished.'
    return
}
if ($Mode -eq 'Verify') {
    Invoke-CheckedNative $fixtureExe @('verify', $RunRoot) | Write-Host
    return
}
if (Test-Path -LiteralPath $RunRoot) { throw 'Start and Smoke require a new, unused fixture root.' }
New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null
if (-not $NoBuild) {
    $target = if ($Mode -eq 'Start' -and -not $NoLaunch) { Join-Path $solutionRoot 'PiStationDesktop.slnx' } else { $fixtureProject }
    Invoke-CheckedNative 'dotnet' @('build', $target, '-c', 'Debug', '-v', 'minimal') | Write-Host
}
if ($Mode -eq 'Smoke') {
    Invoke-CheckedNative $fixtureExe @('smoke', $RunRoot, $fakePi) | Write-Host
    Write-Host "Evidence: $RunRoot/fixture-smoke.json. Native and physical acceptance remain pending."
    return
}

$fixtureProcess = $null
$appProcessId = $null
try {
    # Windows paths cannot contain a double quote. Quote each value for Start-Process's flattened argument string.
    $fixtureProcess = Start-Process -FilePath $fixtureExe -ArgumentList @('serve', ('"' + $RunRoot + '"'), ('"' + $fakePi + '"')) `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput ($RunRoot + '.host.log') -RedirectStandardError ($RunRoot + '.host-error.log')
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while (-not (Test-Path -LiteralPath (Join-Path $RunRoot 'ready.json'))) {
        if ($fixtureProcess.HasExited) { throw "Fixture exited. See $RunRoot.host-error.log." }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Fixture did not become ready within 45 seconds.' }
        Start-Sleep -Milliseconds 200
    }
    $dataRoot = Join-Path $RunRoot $(if ($ReadOnly) { 'client-read-only' } else { 'client' })
    if (-not $NoLaunch) {
        $launch = Invoke-CheckedNative 'winapp' @('run', (Join-Path $solutionRoot 'src/PiStation.App/PiStation.App.csproj'),
            '--configuration', 'Debug', '--arch', 'x64', '--property', 'Platform=x64', '--no-build', '--no-restore', '--detach', '--json',
            '--', '--ui-test', '--data-root', $dataRoot, '--pi-executable', $fakePi, '--fake-pi-scenario', 'normal',
            '--log-file', (Join-Path $RunRoot 'app.jsonl')) | ConvertFrom-Json
        $appProcessId = [int]$launch.ProcessId
    }
    $checks = @(
        @{ id = 'settings'; status = 'pending'; steps = 'Open Connections > Remote acceptance host > Open. In the remote window, open Settings > Pi / runtime > Advanced runtime settings. Confirm fixture-host arguments, host variables, extension path, timeouts 75 and 8. Change only command timeout to 90; Save and connect, close and reopen settings; confirm all other values remain.' },
        @{ id = 'host-browser'; status = 'pending'; steps = "Browse Pi on the host. Enter $RunRoot\host-project\paged and Open folder: 200 entries, then Load more: 205. Use Up and Enter on a folder. Editing the folder field must disable Select and Load more until opened. Cancel preserves the executable. Select the FakePi executable once and confirm its host path." },
        @{ id = 'host-icon'; status = 'pending'; steps = "Select the fixture project > Settings > Projects > Icon and scripts > Choose image on host. Select $RunRoot\host-project\host-icon.png, Save, and confirm the sidebar logo renders. Canceling a different selection must preserve it." },
        @{ id = 'window-ownership'; status = 'pending'; steps = 'Close the local app window, keep the remote window open. Diagnostics and image file pickers must still open attached to the remote window; repeat cancel and reopen.' },
        @{ id = 'upload'; status = 'pending'; steps = "In remote project customization, upload $RunRoot\client-files\client-icon.png. Cancel once and confirm the old icon remains; upload again and Save. Confirm the sidebar image renders. The file must persist under the host data root." },
        @{ id = 'diagnostics'; status = 'pending'; steps = "Export redacted diagnostics from remote Settings > Diagnostics to $RunRoot\client-files\diagnostics.json using the native save picker. Repeat export to test replacement. Then run this script with -Mode Verify -RunRoot '$RunRoot'." },
        @{ id = 'read-only'; status = 'pending'; steps = 'Repeat Start with -ReadOnly for a separate read-only fixture. Confirm the host icon renders and runtime saving, host browsing, and diagnostics export cannot expose host settings or modify data.' },
        @{ id = 'physical-windows'; status = 'pending'; steps = 'On two physical Windows computers, manually update both builds, then test direct HTTPS and actual OpenSSH: compare pairing codes, operate/read-only access, revoke, restart, sleep/wake, network loss/recovery, files, terminal, previews, and reconnect after a manual host update. Repeat direct HTTPS using existing Tailscale IP/MagicDNS. This loopback fixture does not qualify those checks.' }
    )
    if ($ReadOnly) {
        $checks = @(@{ id = 'read-only'; status = 'pending'; steps = 'Open Connections > Remote acceptance host > Open. Confirm the project logo renders in the remote window. Runtime Save and connect, host browsing, and diagnostics export must not expose host settings or modify data. This read-only run does not use -Mode Verify, which checks the operate workflow.' })
    }
    [ordered]@{ runRoot = $RunRoot; hostProcessId = $fixtureProcess.Id; appProcessId = $appProcessId; checks = $checks } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $RunRoot 'acceptance.json') -Encoding utf8
    Write-Host "Remote acceptance fixture ready: $RunRoot"
    foreach ($check in $checks) { Write-Host ("[{0}] {1}" -f $check.id, $check.steps) }
    Write-Host "Fixture expires after one hour. Stop early: ./tests/PiStation.UiTests/Invoke-RemoteAccessAcceptance.ps1 -Mode Stop -RunRoot '$RunRoot'"
} catch {
    if (Test-Path -LiteralPath $RunRoot) { [IO.File]::WriteAllText((Join-Path $RunRoot 'stop'), '') }
    if ($null -ne $fixtureProcess -and -not $fixtureProcess.HasExited) {
        # Only the exact Process object created above is eligible for cleanup.
        if (-not $fixtureProcess.WaitForExit(5000)) { $fixtureProcess.Kill(); $fixtureProcess.WaitForExit(5000) | Out-Null }
    }
    throw
}
