[CmdletBinding()]
param([switch] $NoBuild, [switch] $Capture)
$ErrorActionPreference = 'Stop'
$solutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$appProject = Join-Path $solutionRoot 'src/PiStation.App/PiStation.App.csproj'
$fakePi = Join-Path $solutionRoot 'tests/PiStation.FakePi/bin/Debug/net10.0/PiStation.FakePi.exe'
$runRoot = Join-Path $solutionRoot ('TestResults/usage-limits-native/' + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $runRoot 'data'
$launchedProcessId = $null
$fixtureProcess = $null
$checks = [Collections.Generic.List[string]]::new()
$passed = $false
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
function Invoke-CheckedNative {
    param([string] $FilePath, [string[]] $ArgumentList)
    $output = & $FilePath @ArgumentList 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$FilePath failed: $($output | Out-String)" }
    return ($output | Out-String).Trim()
}
function Invoke-Ui {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)
    $script:lastUiArgs = $Arguments -join ' '
    Invoke-CheckedNative 'winapp' (@('ui') + $Arguments + @('--app', "$script:launchedProcessId", '--json'))
}
function Start-TestApp {
    $launch = Invoke-CheckedNative 'winapp' @('run', $appProject, '--configuration', 'Debug', '--arch', 'x64', '--property', 'Platform=x64', '--no-build', '--no-restore', '--detach', '--json',
        '--', '--ui-test', '--data-root', $dataRoot, '--pi-executable', $fakePi, '--fake-pi-scenario', 'normal', '--log-file', (Join-Path $runRoot 'app.jsonl')) | ConvertFrom-Json
    $script:launchedProcessId = [int]$launch.ProcessId
    Invoke-Ui 'wait-for' 'ConnectionStatusText' '--value' 'Local • Ready' '--timeout' '15000' | Out-Null
    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'invoke' 'SettingsLimitsNavItem' | Out-Null
}
function Stop-TestApp {
    if ($null -eq $script:launchedProcessId) { return }
    $owned = Get-Process -Id $script:launchedProcessId -ErrorAction SilentlyContinue
    if ($owned -and $owned.ProcessName -eq 'PiStationDesktop') { Stop-Process -Id $script:launchedProcessId; Wait-Process -Id $script:launchedProcessId -Timeout 10 -ErrorAction SilentlyContinue }
    $script:launchedProcessId = $null
}
function Wait-Text {
    param([string] $Text)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $tree = Invoke-Ui 'inspect' '--depth' '30'
        if ($tree.Contains($Text)) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Limits panel did not contain: $Text"
}
function Capture-Panel {
    param([string] $Name)
    Invoke-Ui 'scroll' 'SettingsContentScroll' '--to' 'top' | Out-Null
    Invoke-Ui 'inspect' '--depth' '30' | Set-Content -LiteralPath (Join-Path $runRoot "$Name.json") -Encoding utf8NoBOM
    if ($Capture) { & (Join-Path $PSScriptRoot 'Invoke-ValidatedScreenshot.ps1') -FilePath 'winapp' -ArgumentList @('ui','screenshot','--app',"$launchedProcessId",'--output',(Join-Path $runRoot "$Name.png"),'--json') | Out-Null }
}
$fixtureScript = Join-Path $runRoot 'hub.cjs'
@'
const http = require('node:http'); const fs = require('node:fs'); const path = require('node:path');
const root = process.argv[2];
const server = http.createServer((req, res) => {
  if (req.url !== '/v0/management/quota-scheduler/status' || req.headers.authorization !== 'Bearer isolated-native-quota-key') { res.writeHead(401); res.end(); return; }
  fs.appendFileSync(path.join(root, 'requests.txt'), 'authenticated quota read\n');
  const state = fs.readFileSync(path.join(root, 'state.txt'), 'utf8').trim();
  if (state === 'fail') { res.writeHead(503); res.end('private upstream details must not reach UI'); return; }
  if (state === 'invalid') { res.end('{partial'); return; }
  const now = Date.now();
  res.setHeader('content-type', 'application/json');
  res.end(JSON.stringify({accounts: {
    'codex-native-fixture.json': {provider: 'codex', plan: 'pro', fetched_at: new Date(now).toISOString(),
      five_hour: {used_percent: 70, reset_at: new Date(now + 9000000).toISOString()}, weekly: {used_percent: 20, reset_at: new Date(now + 302400000).toISOString()}},
    'claude-native-fixture.json': {provider: 'claude', fetched_at: new Date(now).toISOString(), five_hour: {used_percent: 12, hard_limited: true, reset_at: new Date(now + 3600000).toISOString()}}
  }}));
});
server.listen(0, '127.0.0.1', () => fs.writeFileSync(path.join(root, 'port.txt'), String(server.address().port)));
'@ | Set-Content -LiteralPath $fixtureScript -Encoding utf8NoBOM
'ok' | Set-Content -LiteralPath (Join-Path $runRoot 'state.txt') -Encoding utf8NoBOM
@{executablePath=$null;extensions=@{};launch=@{environmentVariables=@{PI_CODING_AGENT_DIR=(Join-Path $runRoot 'isolated-agent')}}} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dataRoot 'pi-runtime.json') -Encoding utf8NoBOM
try {
    if (-not $NoBuild) { Invoke-CheckedNative 'dotnet' @('build', (Join-Path $solutionRoot 'PiStationDesktop.slnx'), '-c', 'Debug') | Write-Host }
    $fixtureProcess = Start-Process -FilePath 'node' -ArgumentList @("`"$fixtureScript`"", "`"$runRoot`"") -WindowStyle Hidden -PassThru
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath (Join-Path $runRoot 'port.txt'))) { if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Fixture hub did not start.' }; Start-Sleep -Milliseconds 100 }
    $port = (Get-Content -LiteralPath (Join-Path $runRoot 'port.txt') -Raw).Trim()
    Start-TestApp
    Wait-Text 'Unsupported until a Pi extension publishes quota data.'
    $checks.Add('Unavailable Pi quota data stays unsupported instead of displaying zero usage')
    Invoke-Ui 'invoke' 'LimitsManageExpander' | Out-Null
    Invoke-Ui 'set-value' 'LimitSourceLabel' 'Native fixture hub' | Out-Null
    Invoke-Ui 'set-value' 'LimitSourceUrl' "http://127.0.0.1:$port" | Out-Null
    Invoke-Ui 'set-value' 'LimitManagementKey' 'isolated-native-quota-key' | Out-Null
    Invoke-Ui 'invoke' 'LimitSaveSource' | Out-Null
    Invoke-Ui 'wait-for' 'LimitsActionStatus' '--value' 'Quota hub saved securely' '--contains' '--timeout' '10000' | Out-Null
    $stored = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes((Join-Path $dataRoot 'usage-limit-sources.protected')))
    if ($stored.Contains('isolated-native-quota-key')) { throw 'Management key was stored as plaintext.' }
    Invoke-Ui 'invoke' 'LimitsRefresh' | Out-Null
    Wait-Text '70% used'
    Wait-Text '100% used'
    Wait-Text 'Ahead of pace'
    Wait-Text 'Under pace'
    Wait-Text 'Resets in'
    Capture-Panel 'limits-overview'
    $checks.Add('Native hub settings, encrypted key, authenticated polling, quota bars, resets and pacing work')
    'fail' | Set-Content -LiteralPath (Join-Path $runRoot 'state.txt') -Encoding utf8NoBOM
    Invoke-Ui 'invoke' 'LimitsRefresh' | Out-Null
    Wait-Text 'HTTP 503'
    Wait-Text 'Stale readings'
    Wait-Text '70% used'
    Capture-Panel 'limits-stale'
    $checks.Add('Failed hub refresh retains stale bars and exposes a bounded error')
    'ok' | Set-Content -LiteralPath (Join-Path $runRoot 'state.txt') -Encoding utf8NoBOM
    Stop-TestApp
    Start-TestApp
    Wait-Text '70% used'
    $checks.Add('Relaunch decrypts the key on the host and refreshes the same hub')
    Invoke-Ui 'invoke' 'LimitsManageExpander' | Out-Null
    Invoke-Ui 'invoke' 'LimitSourceSelector' | Out-Null
    Invoke-Ui 'send-keys' 'home' | Out-Null
    Invoke-Ui 'send-keys' 'enter' | Out-Null
    Invoke-Ui 'invoke' 'LimitRemoveSource' | Out-Null
    Invoke-Ui 'wait-for' 'LimitsActionStatus' '--value' 'Quota hub removed' '--contains' '--timeout' '10000' | Out-Null
    Stop-TestApp
    Start-TestApp
    Wait-Text 'Unsupported until a Pi extension publishes quota data.'
    $tree = Invoke-Ui 'inspect' '--depth' '30'
    if ($tree.Contains('Native fixture hub')) { throw 'Removed hub reappeared on relaunch.' }
    $checks.Add('Removing a hub removes its saved key and does not resurrect it after relaunch')
    $feedScript = Join-Path $runRoot 'publish.mjs'
    @'
import { pathToFileURL } from 'node:url';
const { publishQuotaFeed } = await import(pathToFileURL(process.argv[2]).href);
publishQuotaFeed(process.argv[3], { id: 'native-pi-publisher', label: 'Pi extension fixture',
  accounts: [{id:'native', provider:'pi-fixture', label:'Offline subscription', windows:[{
    id:'session', kind:'session', label:'Session', usedPercent:79,
    resetsAt:new Date(Date.now()+7200000).toISOString(), windowDurationMins:300
  }]}] });
'@ | Set-Content -LiteralPath $feedScript -Encoding utf8NoBOM
    Invoke-CheckedNative 'node' @($feedScript, (Join-Path $solutionRoot 'src/PiStation.App/PiExtensions/pistation-quotas.mjs'), (Join-Path $dataRoot 'quota-feeds')) | Out-Null
    Invoke-Ui 'invoke' 'LimitsRefresh' | Out-Null
    Wait-Text 'Pi extension fixture'
    Wait-Text '79% used'
    Capture-Panel 'limits-pi-extension'
    $checks.Add('Shipped Pi publisher feeds native subscription bars without another agent runtime')
    $passed = $true
}
catch {
    if ($launchedProcessId) { Invoke-Ui 'inspect' '--depth' '30' | Set-Content -LiteralPath (Join-Path $runRoot 'failed-ui-tree.json') -Encoding utf8NoBOM }
    throw
}
finally {
    Stop-TestApp
    if ($fixtureProcess -and -not $fixtureProcess.HasExited) { Stop-Process -Id $fixtureProcess.Id }
    @{passed=$passed;checks=@($checks);captureRequested=[bool]$Capture;dataRoot=$dataRoot;lastUiArguments=$script:lastUiArgs} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8NoBOM
    Write-Output "Usage limits native artifacts: $runRoot"
}
