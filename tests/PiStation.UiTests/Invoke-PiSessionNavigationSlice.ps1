[CmdletBinding()]
param([switch] $NoBuild, [switch] $Capture)

$ErrorActionPreference = 'Stop'
$solutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$appProject = Join-Path $solutionRoot 'src/PiStation.App/PiStation.App.csproj'
$fakePi = Join-Path $solutionRoot 'tests/PiStation.FakePi/bin/Debug/net10.0/PiStation.FakePi.exe'
$runRoot = Join-Path $solutionRoot ('TestResults/pi-session-navigation-native/' + [guid]::NewGuid().ToString('N'))
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
        $_.type -eq 'ListItem' -and @(Get-TestThreadNodes $_ | Where-Object { $_.name -eq $Text }).Count -gt 0
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
$source = Join-Path $importDirectory 'cli.jsonl'
$header = @{type='session';version=3;id='33333333333333333333333333333333';cwd=$projectPath;timestamp='2026-09-06T00:00:00Z'} | ConvertTo-Json -Compress
$body = @'
{"type":"model_change","id":"model","parentId":null,"provider":"fake","modelId":"fake-fast"}
{"type":"thinking_level_change","id":"thinking","parentId":"model","thinkingLevel":"off"}
{"type":"message","id":"u1","parentId":"thinking","timestamp":"2026-09-06T00:00:01Z","message":{"role":"user","content":"First CLI question"}}
{"type":"message","id":"a1","parentId":"u1","timestamp":"2026-09-06T00:00:02Z","message":{"role":"assistant","content":[{"type":"text","text":"First CLI answer"}],"stopReason":"stop"}}
{"type":"message","id":"u2","parentId":"a1","timestamp":"2026-09-06T00:00:03Z","message":{"role":"user","content":"Second CLI question"}}
{"type":"message","id":"a2","parentId":"u2","timestamp":"2026-09-06T00:00:04Z","message":{"role":"assistant","content":[{"type":"text","text":"Second CLI answer"}],"stopReason":"stop"}}
'@
Set-Content -LiteralPath $source -Value ($header + "`n" + $body) -Encoding utf8NoBOM
$sourceHash = (Get-FileHash -LiteralPath $source).Hash
try {
    if (-not $NoBuild) { Invoke-CheckedNative 'dotnet' @('build', (Join-Path $solutionRoot 'PiStationDesktop.slnx'), '-c', 'Debug', '-p:Platform=x64') | Write-Host }
    Start-TestApp
    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Invoke-Ui 'wait-for' 'NewThreadButton' '--property' 'IsEnabled' '--value' 'true' '--timeout' '15000' | Out-Null
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread 'Thread 1'
    Open-SessionSettings
    Invoke-Ui 'set-value' 'PiSessionDirectoryInput' $importDirectory | Out-Null
    Invoke-Ui 'invoke' 'BrowsePiSessionsButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiSessionsStatusText' '--value' 'Found 1 sessions. 0 unreadable or unsupported files skipped.' '--timeout' '15000' | Out-Null
    Select-SessionRow 'PiSessionCandidateList' 'First CLI question'
    Invoke-Ui 'set-value' 'PiSessionTitleInput' 'Branch navigation' | Out-Null
    Invoke-Ui 'invoke' 'ImportSelectedPiSessionButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiSessionsStatusText' '--value' 'Created Branch navigation. The original session and draft are preserved.' '--timeout' '15000' | Out-Null
    Close-Settings
    Wait-TestThread 'Branch navigation'
    Invoke-Ui 'set-value' 'PromptInput' 'Keep my unsent draft.' | Out-Null
    Wait-Draft
    Open-SessionSettings
    Invoke-Ui 'invoke' 'InspectPiSessionButton' | Out-Null
    Assert-SessionSummary 4
    Select-SessionRow 'PiSessionTreeList' 'assistant · First CLI answer'
    Invoke-Ui 'invoke' 'NavigatePiSessionButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiSessionsStatusText' '--value' 'Branch switched. Your draft and workspace files are preserved.' '--timeout' '15000' | Out-Null
    Assert-SessionSummary 2
    Close-Settings
    Assert-Prompt 'Keep my unsent draft.'
    $checks.Add('Switching to an earlier branch preserves the current thread and draft')
    Write-Output $checks[$checks.Count - 1]
    Stop-TestApp
    Start-TestApp
    $tree = Invoke-Ui 'inspect' 'ProjectSelector' '--depth' '12' | ConvertFrom-Json -Depth 100
    $projectButton = @($tree.windows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'fixture-project' })[0]
    Invoke-Ui 'invoke' $projectButton.selector | Out-Null
    Select-TestThread 'Branch navigation'
    Assert-Prompt 'Keep my unsent draft.'
    Open-SessionSettings
    Invoke-Ui 'invoke' 'InspectPiSessionButton' | Out-Null
    Assert-SessionSummary 2
    $checks.Add('The chosen branch survives app restart before another prompt is sent')
    Write-Output $checks[$checks.Count - 1]
    Select-SessionRow 'PiSessionTreeList' 'assistant · Second CLI answer'
    Invoke-Ui 'invoke' 'NavigatePiSessionButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiSessionsStatusText' '--value' 'Branch switched. Your draft and workspace files are preserved.' '--timeout' '15000' | Out-Null
    Assert-SessionSummary 4
    Select-SessionRow 'PiSessionTreeList' 'user · First CLI question'
    Invoke-Ui 'invoke' 'NavigatePiSessionButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiNavigationPrompt' '--value' 'First CLI question' '--timeout' '15000' | Out-Null
    Assert-SessionSummary 0
    Invoke-Ui 'inspect' '--depth' '20' | Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Encoding utf8NoBOM
    if ($Capture) {
        & (Join-Path $PSScriptRoot 'Invoke-ValidatedScreenshot.ps1') -FilePath 'winapp' -ArgumentList @(
            'ui', 'screenshot', 'AppMainWindow', '--output', (Join-Path $runRoot 'navigation.png'), '--app', "$script:launchedProcessId", '--json') | Out-Null
    }
    Close-Settings
    Assert-Prompt 'Keep my unsent draft.'
    $checks.Add('Alternate branches remain selectable; selecting a user message exposes its prompt without replacing the draft')
    Write-Output $checks[$checks.Count - 1]
    if ((Get-FileHash -LiteralPath $source).Hash -ne $sourceHash) { throw 'The original imported file changed.' }
    $passed = $true
}
catch {
    if ($null -ne $launchedProcessId) {
        try { Invoke-Ui 'inspect' '--depth' '20' | Set-Content -LiteralPath (Join-Path $runRoot 'failure-ui-tree.json') -Encoding utf8NoBOM } catch {}
    }
    throw
}
finally {
    Stop-TestApp
    [ordered]@{ passed = $passed; checks = @($checks); visualCaptureRequested = [bool]$Capture; dataRoot = $dataRoot } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8NoBOM
    Write-Output "Session navigation artifacts: $runRoot"
}