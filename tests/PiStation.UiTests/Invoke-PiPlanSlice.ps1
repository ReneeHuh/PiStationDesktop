[CmdletBinding()]
param([switch] $NoBuild, [switch] $Capture)

$ErrorActionPreference = 'Stop'
$solutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$appProject = Join-Path $solutionRoot 'src/PiStation.App/PiStation.App.csproj'
$fakePi = Join-Path $solutionRoot 'tests/PiStation.FakePi/bin/Debug/net10.0/PiStation.FakePi.exe'
$runRoot = Join-Path $solutionRoot ('TestResults/pi-plan-native/' + [guid]::NewGuid().ToString('N'))
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
        '--fake-pi-scenario', 'plan-workflow', '--log-file', (Join-Path $runRoot 'app.jsonl')) | ConvertFrom-Json
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
function Export-PlanThroughPicker {
    $Format = 'md'
    $windows = Invoke-CheckedNative 'winapp' @('ui', 'list-windows', '--json') | ConvertFrom-Json
    $owner = @($windows | Where-Object { $_.processId -eq $script:launchedProcessId -and $_.ownerHwnd -eq 0 })[0].hwnd
    if (-not $owner) { throw 'The owned app window was not found.' }
    Invoke-Ui 'invoke' 'ExportPlanButton' | Out-Null
    $dialog = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $windows = Invoke-CheckedNative 'winapp' @('ui', 'list-windows', '--json') | ConvertFrom-Json
        $dialog = @($windows | Where-Object { $_.ownerHwnd -eq $owner -and $_.processName -eq 'PickerHost' }) | Select-Object -First 1
        if ($dialog) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $dialog) { throw 'The owned Windows save dialog did not open.' }
    $tree = Invoke-CheckedNative 'winapp' @('ui', 'inspect', '--window', "$($dialog.hwnd)", '--depth', '8', '--json') | ConvertFrom-Json -Depth 100
    $nodes = @($tree.windows | ForEach-Object { Get-TestThreadNodes $_ })
    $fileNameInput = @($nodes | Where-Object { $_.type -eq 'Edit' -and $_.automationId -eq '1001' })[0]
    $save = @($nodes | Where-Object { $_.type -eq 'Button' -and $_.automationId -eq '1' })[0]
    if (-not $fileNameInput -or -not $save) { throw 'The Windows save dialog controls were not found.' }
    $destination = Join-Path $runRoot "native-export.$Format"
    Invoke-CheckedNative 'winapp' @('ui', 'set-value', $fileNameInput.selector, $destination, '--window', "$($dialog.hwnd)", '--json') | Out-Null
    Invoke-CheckedNative 'winapp' @('ui', 'invoke', $save.selector, '--window', "$($dialog.hwnd)", '--json') | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        if (([string](Read-Element 'PiPlanStatusText').name).Contains("Exported plan to $destination")) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    $contents = Get-Content -LiteralPath $destination -Raw
    if (-not $contents.Contains('Edited first step') -or -not $contents.Contains('[x] 1.')) { throw 'The export does not preserve the edited plan and progress.' }
    $checks.Add("Native Markdown export preserves the edited plan and progress")
}


function Assert-Plan {
    param([string] $Mode, [string] $Counts)
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $label = [string](Read-Element 'PiPlanPanel').name
        if ($label.Contains("Plan · $Mode · $Counts steps") -and (Read-Element 'RefreshPlanButton').isEnabled) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Unexpected plan state: $label"
}
function Open-Plan {
    Invoke-Ui 'wait-for' 'PiPlanPanel' '--timeout' '15000' | Out-Null
    Invoke-Ui 'invoke' 'PiPlanPanel' | Out-Null
    Invoke-Ui 'wait-for' 'PiPlanEditor' '--timeout' '5000' | Out-Null
}
New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
try {
    if (-not $NoBuild) {
        Invoke-CheckedNative 'dotnet' @('build', (Join-Path $solutionRoot 'PiStationDesktop.slnx'), '-c', 'Debug', '-p:Platform=x64') | Write-Host
    }
    Start-TestApp
    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Invoke-Ui 'wait-for' 'fixture-project' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread 'Thread 1'
    Open-Plan
    Assert-Plan 'off' '0/0'
    Invoke-Ui 'invoke' 'StartPlanningButton' | Out-Null
    Assert-Plan 'planning' '0/0'
    Invoke-Ui 'set-value' 'PromptInput' 'Create a plan for this fixture' | Out-Null
    Invoke-Ui 'invoke' 'SendPromptButton' | Out-Null
    Assert-Plan 'ready' '0/2'
    $threadTree = Invoke-Ui 'inspect' 'ThreadTabList' '--depth' '8' | ConvertFrom-Json -Depth 100
    $threadTitle = @($threadTree.windows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object { $_.type -eq 'ListItem' })[0]
    $threadTitle = @(Get-TestThreadNodes $threadTitle | Where-Object { $_.type -eq 'Text' })[0].name
    Invoke-Ui 'set-value' 'PromptInput' 'Preserve the original draft.' | Out-Null
    Wait-Draft
    $edited = "Plan:`n1. Edited first step`n2. Verify the change"
    Invoke-Ui 'set-value' 'PiPlanEditor' $edited | Out-Null
    Invoke-Ui 'invoke' 'SavePlanButton' | Out-Null
    Invoke-Ui 'wait-for' 'ExecutePlanButton' '--property' 'IsEnabled' '--value' 'true' '--timeout' '15000' | Out-Null
    Invoke-Ui 'invoke' 'ExecutePlanButton' | Out-Null
    Assert-Plan 'paused' '1/2'
    Assert-Prompt 'Preserve the original draft.'
    $checks.Add('Native planning, plan editing and approved execution preserve the composer draft')
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread 'Thread 2'
    Assert-Plan 'off' '0/0'
    Select-TestThread $threadTitle
    Assert-Plan 'paused' '1/2'
    Assert-Prompt 'Preserve the original draft.'
    $checks.Add('Plan and progress are scoped to the selected thread')
    Stop-TestApp
    Start-TestApp
    Invoke-Ui 'wait-for' 'fixture-project' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'fixture-project' | Out-Null
    Select-TestThread $threadTitle
    Open-Plan
    Assert-Plan 'paused' '1/2'
    Assert-Prompt 'Preserve the original draft.'
    if (-not ([string](Read-Element 'PiPlanEditor').value).Contains('Edited first step')) { throw 'The edited plan was lost on restart.' }
    $checks.Add('App relaunch restores the edited plan, partial progress and separate draft')
    Export-PlanThroughPicker
    Invoke-Ui 'invoke' 'ExecutePlanButton' | Out-Null
    Assert-Plan 'completed' '2/2'
    Assert-Prompt 'Preserve the original draft.'
    $checks.Add('Approving remaining steps completes the plan without resubmitting the composer draft')
    Invoke-Ui 'inspect' '--depth' '14' | Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Encoding utf8NoBOM
    if ($Capture) {
        Invoke-Ui 'screenshot' 'AppMainWindow' '--capture-screen' '--output' (Join-Path $runRoot 'pi-plan.png') | Out-Null
        & (Join-Path $PSScriptRoot 'Assert-ValidScreenshot.ps1') -Path (Join-Path $runRoot 'pi-plan.png') | Out-Null
    }
    $passed = $true
}
catch {
    if ($null -ne $launchedProcessId) {
        try { Invoke-Ui 'inspect' '--depth' '14' | Set-Content -LiteralPath (Join-Path $runRoot 'failure-ui-tree.json') -Encoding utf8NoBOM } catch {}
    }
    throw
}
finally {
    Stop-TestApp
    [ordered]@{ passed = $passed; checks = @($checks); visualCaptureRequested = [bool]$Capture; dataRoot = $dataRoot } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8NoBOM
    Write-Output "Pi plan native artifacts: $runRoot"
}
