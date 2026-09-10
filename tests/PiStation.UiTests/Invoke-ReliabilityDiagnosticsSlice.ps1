[CmdletBinding()]
param([switch] $NoBuild, [switch] $Capture)

$ErrorActionPreference = 'Stop'
$solutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$appProject = Join-Path $solutionRoot 'src/PiStation.App/PiStation.App.csproj'
$fakePi = Join-Path $solutionRoot 'tests/PiStation.FakePi/bin/Debug/net10.0/PiStation.FakePi.exe'
$runRoot = Join-Path $solutionRoot ('TestResults/reliability-diagnostics-native/' + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $runRoot 'data'
$projectPath = Join-Path $runRoot 'fixture-project'
$launchedProcessId = $null
$checks = [Collections.Generic.List[string]]::new()
$passed = $false
. (Join-Path $PSScriptRoot 'Select-TestThread.ps1')

function Invoke-CheckedNative {
    param([string] $FilePath, [string[]] $ArgumentList)
    $output = & $FilePath @ArgumentList 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$FilePath failed: $($output | Out-String)" }
    return ($output | Out-String).Trim()
}
function Invoke-Ui {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)
    $script:lastUiArgs = $Arguments -join ' ';
    if ($Arguments -contains 'PromptInput' -and $Arguments[0] -in @('set-value', 'focus', 'click', 'invoke', 'type', 'send-keys', 'inspect', 'get-value', 'get-property', 'wait-for') -and $Arguments -notcontains '--gone') { Expand-TestComposer }
    Invoke-CheckedNative 'winapp' (@('ui') + $Arguments + @('--app', "$script:launchedProcessId", '--json'))
}
function Start-TestApp {
    $launch = Invoke-CheckedNative 'winapp' @('run', $appProject, '--configuration', 'Debug',
        '--arch', 'x64', '--property', 'Platform=x64', '--no-build', '--no-restore', '--detach', '--json',
        '--', '--ui-test', '--data-root', $dataRoot, '--pi-executable', $fakePi,
        '--fake-pi-scenario', 'normal', '--log-file', (Join-Path $runRoot 'app.jsonl')) | ConvertFrom-Json
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
function Read-Element {
    param([string] $Selector)
    $tree = Invoke-Ui 'inspect' $Selector '--depth' '1' | ConvertFrom-Json -Depth 100
    @($tree.windows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object { $_.automationId -eq $Selector })[0]
}
function Assert-Prompt {
    param([AllowEmptyString()][string] $Expected)
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $actual = [string](Read-Element 'PromptInput').value
        if ($actual.Replace("`r`n", "`n").Replace("`r", "`n") -eq $Expected) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Draft mismatch. Expected: $Expected; actual: $actual"
}
function Wait-Draft {
    Invoke-Ui 'wait-for' 'DraftStatusText' '--value' 'Saved' '--timeout' '15000' | Out-Null
}
function Open-SessionSettings {
    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsPiSessionsNavItem' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'SettingsPiSessionsNavItem' | Out-Null
}
function Select-SessionRow {
    param([string] $List, [string] $Text)
    $tree = Invoke-Ui 'inspect' $List '--depth' '8' | ConvertFrom-Json -Depth 100
    $matches = @($tree.windows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object {
        $_.type -eq 'ListItem' -and @(Get-TestThreadNodes $_ | Where-Object { ($_.name -replace '^[▶▼·] ', '') -eq $Text }).Count -gt 0
    })
    if ($matches.Count -ne 1) { throw "Expected one row for $Text; found $($matches.Count)." }
    Invoke-Ui 'invoke' $matches[0].selector | Out-Null
}
function Assert-SessionSummary {
    param([int] $Messages)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $summary = [string](Read-Element 'PiSessionSummaryText').name
        if ($summary.StartsWith("$Messages messages in the active branch")) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Wrong session summary: $summary"
}
function Close-Settings {
    Invoke-Ui 'invoke' 'CloseButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsDialog' '--gone' '--timeout' '5000' | Out-Null
}

New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
$importDirectory = Join-Path $runRoot 'cli-sessions'
New-Item -ItemType Directory -Path $importDirectory -Force | Out-Null
$source = Join-Path $importDirectory 'history.jsonl'
$lines = [Collections.Generic.List[string]]::new()
$lines.Add((@{type='session'; version=3; id=[guid]::NewGuid().ToString('N'); cwd=$projectPath; timestamp='2026-09-10T00:00:00Z'} | ConvertTo-Json -Compress))
for ($index=0; $index -lt 80; $index++) {
    $lines.Add((@{type='message'; id="entry-$index"; parentId=$(if ($index -eq 0) {$null} else {"entry-$($index-1)"}); timestamp='2026-09-10T00:00:01Z';
        message=@{role=$(if ($index % 2 -eq 0) {'user'} else {'assistant'}); content="History message $index"; stopReason='stop'}} | ConvertTo-Json -Depth 5 -Compress))
}
[IO.File]::WriteAllLines($source, $lines)
$sourceHash = (Get-FileHash -LiteralPath $source).Hash
try {
    if (-not $NoBuild) { Invoke-CheckedNative 'dotnet' @('build', (Join-Path $solutionRoot 'PiStationDesktop.slnx'), '-c', 'Debug') | Write-Host }
    Start-TestApp
    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Select-TestProject 'fixture-project'
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread 'Thread 1'
    Open-SessionSettings
    Invoke-Ui 'set-value' 'PiSessionDirectoryInput' $importDirectory | Out-Null
    Invoke-Ui 'invoke' 'BrowsePiSessionsButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiSessionsStatusText' '--value' 'Found 1 sessions. 0 unreadable or unsupported files skipped.' '--timeout' '15000' | Out-Null
    Select-SessionRow 'PiSessionCandidateList' 'History message 0'
    Invoke-Ui 'set-value' 'PiSessionTitleInput' 'Long history' | Out-Null
    Invoke-Ui 'invoke' 'ImportSelectedPiSessionButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiSessionsStatusText' '--value' 'Created Long history. The original session and draft are preserved.' '--timeout' '15000' | Out-Null
    Close-Settings
    Wait-TestThread 'Long history'
    Invoke-Ui 'set-value' 'PromptInput' 'Draft survives history paging.' | Out-Null
    Wait-Draft
    Invoke-Ui 'invoke' 'LoadEarlierHistoryButton' | Out-Null
    Invoke-Ui 'wait-for' 'ThreadHistoryStatus' '--value' 'Earlier messages loaded.' '--timeout' '15000' | Out-Null
    Invoke-Ui 'invoke' 'LoadEarlierHistoryButton' | Out-Null
    Invoke-Ui 'wait-for' 'LoadEarlierHistoryButton' '--gone' '--timeout' '15000' | Out-Null
    Assert-Prompt 'Draft survives history paging.'
    $checks.Add('A 40-turn imported conversation pages to the beginning without changing its draft')
    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'invoke' 'SettingsDiagnosticsNavItem' | Out-Null
    Invoke-Ui 'wait-for' 'RefreshRuntimeHealthButton' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'RefreshRuntimeHealthButton' | Out-Null
    $tree = Invoke-Ui 'inspect' 'DiagnosticProcessList' '--depth' '8' | ConvertFrom-Json -Depth 100
    $rows = @($tree.windows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object { $_.type -eq 'ListItem' })
    $piRow = @($rows | Where-Object { @(Get-TestThreadNodes $_ | Where-Object { $_.name -like '*PID*' -and $_.name -like '* · pi' }).Count -gt 0 })[0]
    if (-not $piRow) { throw 'No owned Pi process appeared in diagnostics.' }
    Invoke-Ui 'invoke' $piRow.selector | Out-Null
    Invoke-Ui 'invoke' 'TerminateDiagnosticProcessButton' | Out-Null
    Invoke-Ui 'wait-for' 'DiagnosticProcessStopDialog' '--timeout' '10000' | Out-Null
    $tree = Invoke-Ui 'inspect' 'DiagnosticProcessStopDialog' '--depth' '6' | ConvertFrom-Json -Depth 100
    $cancel = @($tree.windows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Cancel' })[0]
    Invoke-Ui 'invoke' $cancel.selector | Out-Null
    Invoke-Ui 'wait-for' 'SettingsDialog' '--timeout' '5000' | Out-Null
    $checks.Add('Owned process tree and resource history render; targeted stop review cancels safely')
    Invoke-Ui 'invoke' 'RuntimeHealthSettingsExpander' | Out-Null
    Invoke-Ui 'invoke' 'DiagnosticsTracingToggle' | Out-Null
    Invoke-Ui 'invoke' 'SaveRuntimeHealthButton' | Out-Null
    $settingsFile = Join-Path $dataRoot 'runtime-health.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do { if ((Test-Path -LiteralPath $settingsFile) -and (Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).tracingEnabled) { break }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $deadline)
    if (-not (Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).tracingEnabled) { throw 'Trace settings were not saved.' }
    $checks.Add('Native observability settings save to the selected host')
    Invoke-Ui 'inspect' '--depth' '14' | Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Encoding utf8NoBOM
    if ($Capture) { & (Join-Path $PSScriptRoot 'Invoke-ValidatedScreenshot.ps1') -FilePath 'winapp' -ArgumentList @('ui','screenshot','--app',"$launchedProcessId",'--output',(Join-Path $runRoot 'diagnostics.png'),'--json') | Out-Null }
    Close-Settings
    Stop-TestApp
    Start-TestApp
    Select-TestProject 'fixture-project'
    Select-TestThread 'Long history'
    Assert-Prompt 'Draft survives history paging.'
    Invoke-Ui 'wait-for' 'LoadEarlierHistoryButton' '--timeout' '15000' | Out-Null
    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'invoke' 'SettingsDiagnosticsNavItem' | Out-Null
    Invoke-Ui 'invoke' 'RuntimeHealthSettingsExpander' | Out-Null
    $toggle = Read-Element 'DiagnosticsTracingToggle'
    if ($toggle.toggleState -notin @('On', 1)) { throw "Tracing did not reload: $($toggle | ConvertTo-Json -Compress)" }
    $checks.Add('App relaunch recovers a paged conversation, its draft, and saved diagnostics settings')
    if ((Get-FileHash -LiteralPath $source).Hash -ne $sourceHash) { throw 'History loading changed the imported source.' }
    $passed = $true
}
finally {
    Stop-TestApp
    @{ passed=$passed; checks=@($checks); captureRequested=[bool]$Capture; dataRoot=$dataRoot; lastUiArguments=$script:lastUiArgs } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8NoBOM
    Write-Output "Reliability and diagnostics native artifacts: $runRoot"
}
