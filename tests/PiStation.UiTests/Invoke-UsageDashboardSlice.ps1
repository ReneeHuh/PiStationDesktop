[CmdletBinding()]
param([switch] $NoBuild, [switch] $Capture)

$ErrorActionPreference = 'Stop'
$solutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$appProject = Join-Path $solutionRoot 'src/PiStation.App/PiStation.App.csproj'
$fakePi = Join-Path $solutionRoot 'tests/PiStation.FakePi/bin/Debug/net10.0/PiStation.FakePi.exe'
$runRoot = Join-Path $solutionRoot ('TestResults/usage-dashboard-native/' + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $runRoot 'data'
$launchedProcessId = $null
$checks = [Collections.Generic.List[string]]::new()
$passed = $false
$previousAgentDirectory = $env:PI_CODING_AGENT_DIR
. (Join-Path $PSScriptRoot 'Select-TestThread.ps1')
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
}
function Stop-TestApp {
    if ($null -eq $script:launchedProcessId) { return }
    $owned = Get-Process -Id $script:launchedProcessId -ErrorAction SilentlyContinue
    if ($owned -and $owned.ProcessName -eq 'PiStationDesktop') {
        Stop-Process -Id $script:launchedProcessId
        Wait-Process -Id $script:launchedProcessId -Timeout 10 -ErrorAction SilentlyContinue
    }
    $script:launchedProcessId = $null
}
function Select-Combo {
    param([string] $Selector, [int] $Index)
    Invoke-Ui 'invoke' $Selector | Out-Null
    Invoke-Ui 'send-keys' 'home' | Out-Null
    for ($item = 0; $item -lt $Index; $item++) { Invoke-Ui 'send-keys' 'down' | Out-Null }
    Invoke-Ui 'send-keys' 'enter' | Out-Null
}
function Assert-Tokens {
    param([int] $Count)
    Invoke-Ui 'wait-for' 'UsageSummary' '--value' "$Count tokens" '--contains' '--timeout' '25000' | Out-Null
}
function Entry {
    param([int] $Index, [int] $DaysAgo = 0, [string] $Provider = 'fixture-a')
    @{type='message'; id="usage-$Index"; message=@{role='assistant'; provider=$Provider; model='fixture-model'; timestamp=$script:fixtureNow.AddDays(-$DaysAgo).AddSeconds($Index).ToUnixTimeMilliseconds();
        content=@(@{type='text';text="Fixture answer $Index"}); usage=@{input=100;output=20;cacheRead=30;cacheWrite=10;totalTokens=160;reasoning=5;cost=@{total=0.1}}}} | ConvertTo-Json -Depth 8 -Compress
}
function Write-Session {
    param([string] $Path, [string] $Identity, [string[]] $Entries)
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    $header = @{type='session';version=3;id=$Identity;cwd=$runRoot} | ConvertTo-Json -Compress
    [IO.File]::WriteAllLines($Path, @($header) + $Entries)
}
$fixtureNow = [DateTimeOffset]::UtcNow.AddMinutes(-5)
$env:PI_CODING_AGENT_DIR = Join-Path $runRoot 'isolated-agent'
New-Item -ItemType Directory -Path (Join-Path $env:PI_CODING_AGENT_DIR 'sessions') -Force | Out-Null
$parent = @((Entry 1), (Entry 2 2 'fixture-b'), (Entry 3 40))
Write-Session (Join-Path $dataRoot 'sessions/parent.jsonl') 'parent' $parent
Write-Session (Join-Path $dataRoot 'sessions/fork.jsonl') 'fork' $parent
Write-Session (Join-Path $dataRoot 'agents/fixture-thread/child/child.jsonl') 'child' @((Entry 4))
Write-Session (Join-Path $dataRoot 'agents/fixture-thread/resumed/child.jsonl') 'child' @((Entry 4), (Entry 5))
@{executablePath=$null;extensions=@{};launch=@{environmentVariables=@{PI_CODING_AGENT_DIR=$env:PI_CODING_AGENT_DIR}}} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dataRoot 'pi-runtime.json') -Encoding utf8NoBOM
@{UpdatedUtc=[DateTimeOffset]::UtcNow; Rates=@{'fixture-a/fixture-model'=@{Input=0.001;Output=0.002;CacheRead=0.0001;CacheWrite=0.0015}; 'fixture-b/fixture-model'=@{Input=0.001;Output=0.002;CacheRead=0.0001;CacheWrite=0.0015}}} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dataRoot 'usage-pricing-v1.json') -Encoding utf8NoBOM
try {
    if (-not $NoBuild) { Invoke-CheckedNative 'dotnet' @('build', (Join-Path $solutionRoot 'PiStationDesktop.slnx'), '-c', 'Debug') | Write-Host }
    Start-TestApp
    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'invoke' 'SettingsUsageNavItem' | Out-Null
    Assert-Tokens 640
    Invoke-Ui 'wait-for' 'UsageCoverage' '--value' '2 child records' '--contains' '--timeout' '10000' | Out-Null
    Invoke-Ui 'wait-for' 'UsageSummary' '--value' '$0.1080' '--contains' '--timeout' '10000' | Out-Null
    $checks.Add('Native dashboard reconciles parent/fork and resumed-child history with cache savings')
    if ($Capture) { & (Join-Path $PSScriptRoot 'Invoke-ValidatedScreenshot.ps1') -FilePath 'winapp' -ArgumentList @('ui','screenshot','--app',"$launchedProcessId",'--output',(Join-Path $runRoot 'usage-overview.png'),'--json') | Out-Null }
    Invoke-Ui 'invoke' 'UsageFiltersExpander' | Out-Null
    Select-Combo 'UsageDateRange' 3
    Invoke-Ui 'invoke' 'UsageRefresh' | Out-Null
    Assert-Tokens 800
    $checks.Add('90-day range includes older history')
    Select-Combo 'UsageProvider' 1
    Invoke-Ui 'invoke' 'UsageRefresh' | Out-Null
    Assert-Tokens 640
    Select-Combo 'UsageModel' 1
    Invoke-Ui 'invoke' 'UsageRefresh' | Out-Null
    Assert-Tokens 640
    Select-Combo 'UsageMetric' 1
    Invoke-Ui 'scroll' 'SettingsContentScroll' '--to' 'bottom' | Out-Null
    Invoke-Ui 'scroll' 'UsageTimelineScroll' '--to' 'bottom' | Out-Null
    Invoke-Ui 'inspect' '--depth' '30' | Set-Content -LiteralPath (Join-Path $runRoot 'cost-chart.json') -Encoding utf8NoBOM
    if ($Capture) { & (Join-Path $PSScriptRoot 'Invoke-ValidatedScreenshot.ps1') -FilePath 'winapp' -ArgumentList @('ui','screenshot','--app',"$launchedProcessId",'--output',(Join-Path $runRoot 'usage-charts.png'),'--json') | Out-Null }
    $checks.Add('Provider/model filters and cost charts operate through native controls')
    Invoke-Ui 'invoke' 'UsageRescan' | Out-Null
    Invoke-Ui 'wait-for' 'UsageCoverage' '--value' '(0 cached)' '--contains' '--timeout' '25000' | Out-Null
    Assert-Tokens 640
    $checks.Add('Explicit rescan preserves deduplicated totals')
    Invoke-Ui 'invoke' 'UsageFiltersExpander' | Out-Null
    Invoke-Ui 'inspect' '--depth' '16' | Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Encoding utf8NoBOM
    if ($Capture) { & (Join-Path $PSScriptRoot 'Invoke-ValidatedScreenshot.ps1') -FilePath 'winapp' -ArgumentList @('ui','screenshot','--app',"$launchedProcessId",'--output',(Join-Path $runRoot 'usage.png'),'--json') | Out-Null }
    Stop-TestApp
    Start-TestApp
    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'invoke' 'SettingsUsageNavItem' | Out-Null
    Assert-Tokens 640
    Invoke-Ui 'wait-for' 'UsageCoverage' '--value' '(4 cached)' '--contains' '--timeout' '25000' | Out-Null
    $checks.Add('Relaunch loads durable scan and pricing caches')
    Invoke-Ui 'invoke' 'UsageFiltersExpander' | Out-Null
    Invoke-Ui 'invoke' 'UsageRefreshPricing' | Out-Null
    Invoke-Ui 'wait-for' 'UsagePricing' '--value' 'Refresh does not change reported costs.' '--contains' '--timeout' '25000' | Out-Null
    Assert-Tokens 640
    Invoke-Ui 'wait-for' 'UsageSummary' '--value' '$0.4000' '--contains' '--timeout' '5000' | Out-Null
    $checks.Add('Native pricing refresh downloads public rates and preserves Pi-reported costs')
    $passed = $true
}
catch {
    if ($launchedProcessId) { Invoke-Ui 'inspect' '--depth' '16' | Set-Content -LiteralPath (Join-Path $runRoot 'failed-ui-tree.json') -Encoding utf8NoBOM }
    throw
}
finally {
    Stop-TestApp
    $env:PI_CODING_AGENT_DIR = $previousAgentDirectory
    @{passed=$passed;checks=@($checks);captureRequested=[bool]$Capture;dataRoot=$dataRoot;lastUiArguments=$script:lastUiArgs} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8NoBOM
    Write-Output "Usage dashboard native artifacts: $runRoot"
}
