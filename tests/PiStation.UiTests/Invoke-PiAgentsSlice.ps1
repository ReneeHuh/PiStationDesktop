[CmdletBinding()]
param([switch] $NoBuild, [switch] $Capture)

$ErrorActionPreference = 'Stop'
$solutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$appProject = Join-Path $solutionRoot 'src/PiStation.App/PiStation.App.csproj'
$fakePi = Join-Path $solutionRoot 'tests/PiStation.FakePi/bin/Debug/net10.0/PiStation.FakePi.exe'
$runRoot = Join-Path $solutionRoot ('TestResults/pi-agents-native/' + [guid]::NewGuid().ToString('N'))
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
    if ($Arguments -contains 'PromptInput' -and $Arguments[0] -in @('set-value', 'focus', 'click', 'invoke', 'type', 'send-keys', 'inspect', 'get-value', 'get-property', 'wait-for') -and $Arguments -notcontains '--gone') { Expand-TestComposer }
    Invoke-CheckedNative 'winapp' (@('ui') + $Arguments + @('--app', "$script:launchedProcessId", '--json'))
}
function Start-TestApp {
    $launch = Invoke-CheckedNative 'winapp' @('run', $appProject, '--configuration', 'Debug',
        '--arch', 'x64', '--property', 'Platform=x64', '--no-build', '--no-restore', '--detach', '--json',
        '--', '--ui-test', '--data-root', $dataRoot, '--pi-executable', $fakePi,
        '--fake-pi-scenario', 'agent-workflow', '--log-file', (Join-Path $runRoot 'app.jsonl')) | ConvertFrom-Json
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

function Read-Nodes {
    param([string] $Selector = 'AgentsWorkbenchSurface')
    $tree = Invoke-Ui 'inspect' $Selector '--depth' '20' | ConvertFrom-Json -Depth 100
    $seen = [Collections.Generic.HashSet[string]]::new()
    @($tree.windows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object { $_.selector -and $seen.Add($_.selector) })
}
function Choose-Option {
    param([string] $Selector, [string] $Name)
    Invoke-Ui 'invoke' $Selector | Out-Null
    $tree = Invoke-Ui 'inspect' '--depth' '18' | ConvertFrom-Json -Depth 100
    $options = @($tree.windows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq $Name } | Sort-Object selector -Unique)
    if ($options.Count -ne 1) { throw "Expected one option '$Name', found $($options.Count)." }
    Invoke-Ui 'invoke' $options[0].selector | Out-Null
}
function Set-Task {
    param([int] $Index, [string] $Text)
    $inputs = @(Read-Nodes | Where-Object automationId -eq 'AgentTaskTextBox')
    if ($Index -ge $inputs.Count) { throw 'Expected task editor not found.' }
    Invoke-Ui 'set-value' $inputs[$Index].selector $Text | Out-Null
}
function Wait-Completed {
    Invoke-Ui 'wait-for' 'SendPromptButton' '--property' 'IsEnabled' '--value' 'true' '--timeout' '20000' | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $summary = [string](Read-Element 'AgentActivitySummaryText').name
        if ($summary.Contains('completed') -and -not $summary.Contains('live')) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Workflow did not finish: $summary"
}
function Open-Agents {
    if ((Read-Element 'ToggleWorkbenchButton').name -eq 'Open workbench') { Invoke-Ui 'invoke' 'ToggleWorkbenchButton' | Out-Null }
    Invoke-Ui 'wait-for' 'AgentsPanelTab' '--timeout' '15000' | Out-Null
    Invoke-Ui 'invoke' 'AgentsPanelTab' | Out-Null
    Invoke-Ui 'wait-for' 'AgentWorkflowExpander' '--timeout' '15000' | Out-Null
}
function Read-ThreadTitle {
    $tree = Invoke-Ui 'inspect' 'ThreadTabList' '--depth' '8' | ConvertFrom-Json -Depth 100
    $row = @($tree.windows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object type -eq 'ListItem')[0]
    return @(Get-TestThreadNodes $row | Where-Object type -eq 'Text')[0].name
}
New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
try {
    if (-not $NoBuild) { Invoke-CheckedNative 'dotnet' @('build', (Join-Path $solutionRoot 'PiStationDesktop.slnx'), '-c', 'Debug', '-p:Platform=x64') | Write-Host }
    Start-TestApp
    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Invoke-Ui 'wait-for' 'fixture-project' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread 'Thread 1'
    Open-Agents
    Invoke-Ui 'set-value' 'RightPanelResizeHandle' '600' | Out-Null
    Invoke-Ui 'invoke' 'AgentWorkflowExpander' | Out-Null
    Invoke-Ui 'invoke' 'AgentSetupExpander' | Out-Null
    Invoke-Ui 'invoke' 'NewAgentPresetButton' | Out-Null
    Invoke-Ui 'set-value' 'AgentPresetNameTextBox' 'custom-agent' | Out-Null
    Invoke-Ui 'set-value' 'AgentPresetDescriptionTextBox' 'Native fixture preset' | Out-Null
    Invoke-Ui 'set-value' 'AgentPresetInstructionsTextBox' 'Inspect the delegated task and report evidence.' | Out-Null
    Invoke-Ui 'invoke' 'SaveAgentPresetButton' | Out-Null
    Invoke-Ui 'wait-for' 'AgentSetupSummaryText' '--value' 'Bundled integration · enabled · 5 presets' '--timeout' '15000' | Out-Null
    $checks.Add('Native agent setup saves a reusable custom preset')
    Invoke-Ui 'invoke' 'AgentSetupExpander' | Out-Null
    Invoke-Ui 'set-value' 'PromptInput' 'Keep my unsent draft.' | Out-Null
    Wait-Draft
    Choose-Option 'AgentTaskPresetComboBox' 'custom-agent'
    Set-Task 0 'READ the fixture'
    Invoke-Ui 'invoke' 'RunAgentWorkflowButton' | Out-Null
    Wait-Completed
    $threadTitle = Read-ThreadTitle
    Assert-Prompt 'Keep my unsent draft.'
    Invoke-Ui 'invoke' 'AgentDetailsButton' | Out-Null
    Invoke-Ui 'wait-for' 'AgentTranscriptText' '--timeout' '5000' | Out-Null
    if (-not ([string](Read-Element 'AgentTranscriptText').name).Contains('Child session evidence')) { throw 'Child transcript is missing.' }
    $checks.Add('Native single delegation exposes the child transcript and preserves the draft')
    Invoke-Ui 'invoke' 'Continue child' | Out-Null
    Invoke-Ui 'wait-for' 'AgentContinuationTaskTextBox' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'AgentContinuationTaskTextBox' 'CONTINUE with the saved context' | Out-Null
    Invoke-Ui 'invoke' 'Run continuation' | Out-Null
    Wait-Completed
    Assert-Prompt 'Keep my unsent draft.'
    $checks.Add('Native child continuation submits a follow-up against saved context')
    Invoke-Ui 'invoke' 'AgentWorkflowExpander' | Out-Null
    Choose-Option 'AgentWorkflowModeComboBox' 'parallel'
    Set-Task 0 'WAIT ONE'
    Invoke-Ui 'invoke' 'AddAgentTaskButton' | Out-Null
    Set-Task 1 'WAIT TWO'
    Invoke-Ui 'invoke' 'RunAgentWorkflowButton' | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $stops = @(Read-Nodes | Where-Object { $_.automationId -eq 'InterruptAgentButton' -and $_.name -eq 'Stop child' })
        if ($stops.Count -eq 2) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($stops.Count -ne 2) { throw 'The two child controls were not exposed.' }
    Invoke-Ui 'invoke' $stops[0].selector | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $stops = @(Read-Nodes | Where-Object { $_.automationId -eq 'InterruptAgentButton' -and $_.name -eq 'Stop child' })
        if ($stops.Count -eq 1) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($stops.Count -ne 1 -or -not (Read-Element 'StopTurnButton').isEnabled) { throw 'Targeted Stop did not leave the sibling and parent running.' }
    if ($Capture) {
        Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'pi-agents-active.png') | Out-Null
        & (Join-Path $PSScriptRoot 'Assert-ValidScreenshot.ps1') -Path (Join-Path $runRoot 'pi-agents-active.png') | Out-Null
    }
    Invoke-Ui 'invoke' 'StopTurnButton' | Out-Null
    Wait-Completed
    $checks.Add('Native parallel workflow stops one child independently and then stops the parent')
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread 'Thread 2'
    Invoke-Ui 'wait-for' 'AgentEmptyStateText' '--timeout' '5000' | Out-Null
    Select-TestThread $threadTitle
    Assert-Prompt 'Keep my unsent draft.'
    Stop-TestApp
    Start-TestApp
    Invoke-Ui 'wait-for' 'fixture-project' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'fixture-project' | Out-Null
    Select-TestThread $threadTitle
    Open-Agents
    Assert-Prompt 'Keep my unsent draft.'
    $buttons = @(Read-Nodes | Where-Object automationId -eq 'AgentDetailsButton')
    if ($buttons.Count -lt 3) { throw 'Child history was lost after restart.' }
    Invoke-Ui 'invoke' $buttons[0].selector | Out-Null
    Invoke-Ui 'wait-for' 'AgentTranscriptText' '--timeout' '5000' | Out-Null
    if (-not ([string](Read-Element 'AgentTranscriptText').name).Contains('Child session evidence')) { throw 'The restored child transcript is missing.' }
    Invoke-Ui 'invoke' 'CloseButton' | Out-Null
    $checks.Add('Agent history and transcripts remain thread-scoped and survive app restart')
    Invoke-Ui 'inspect' '--depth' '20' | Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Encoding utf8NoBOM
    if ($Capture) {
        Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'pi-agents.png') | Out-Null
        & (Join-Path $PSScriptRoot 'Assert-ValidScreenshot.ps1') -Path (Join-Path $runRoot 'pi-agents.png') | Out-Null
    }
    $passed = $true
}
catch {
    if ($null -ne $launchedProcessId) {
        try { Invoke-Ui 'inspect' '--depth' '20' | Set-Content -LiteralPath (Join-Path $runRoot 'failure-ui-tree.json') -Encoding utf8NoBOM } catch {}
        try { Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'failure.png') | Out-Null } catch {}
    }
    throw
}
finally {
    Stop-TestApp
    [ordered]@{ passed = $passed; checks = @($checks); visualCaptureRequested = [bool]$Capture; dataRoot = $dataRoot } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8NoBOM
    Write-Output "Pi agents native artifacts: $runRoot"
}
