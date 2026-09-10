[CmdletBinding()]
param([switch] $NoBuild, [switch] $Capture)

$ErrorActionPreference = 'Stop'
$solutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$appProject = Join-Path $solutionRoot 'src/PiStation.App/PiStation.App.csproj'
$fakePi = Join-Path $solutionRoot 'tests/PiStation.FakePi/bin/Debug/net10.0/PiStation.FakePi.exe'
$runRoot = Join-Path $solutionRoot ('TestResults/pi-shell-native/' + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $runRoot 'data'
$projectPath = Join-Path $runRoot 'fixture-project'
$launchedProcessId = $null
$passed = $false
$functionalPassed = $false
. (Join-Path $PSScriptRoot 'Select-TestThread.ps1')

function Invoke-CheckedNative {
    param([string] $FilePath, [string[]] $ArgumentList)
    $output = & $FilePath @ArgumentList 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$FilePath $($ArgumentList -join ' ') failed: $($output | Out-String)" }
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
function Wait-ShellState {
    param([string] $State)
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if (([string](Read-Element 'PiShellResultStatus').name).StartsWith($State, [StringComparison]::Ordinal)) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Shell did not reach $State."
}
function Assert-Draft {
    if ((Read-Element 'PromptInput').value -ne 'Keep my unsent prompt.') { throw 'Shell changed the prompt draft.' }
}
New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
try {
    if (-not $NoBuild) { Invoke-CheckedNative 'dotnet' @('build', (Join-Path $solutionRoot 'PiStationDesktop.slnx'), '-c', 'Debug', '-p:Platform=x64') | Write-Host }
    Start-TestApp
    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    $projectDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        if ((Read-Element 'NewThreadButton').isEnabled) { break }
        if ([DateTime]::UtcNow -gt $projectDeadline) { throw 'The project did not become available for a thread.' }
        Start-Sleep -Milliseconds 100
    } while ($true)
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread 'Thread 1'
    Invoke-Ui 'set-value' 'PromptInput' 'Keep my unsent prompt.' | Out-Null
    Invoke-Ui 'wait-for' 'DraftStatusText' '--value' 'Saved' '--timeout' '15000' | Out-Null
    Invoke-Ui 'invoke' 'PiShellButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiShellCommandInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'PiShellCommandInput' 'wait' | Out-Null
    Invoke-Ui 'invoke' 'PiShellIncludeContext' | Out-Null
    Invoke-Ui 'invoke' 'PiShellRunButton' | Out-Null
    Wait-ShellState 'Running'
    # WinUI TextBox exposes CR line endings through UI Automation.
    Invoke-Ui 'wait-for' 'PiShellOutput' '--value' "started 😀`r" '--timeout' '15000' | Out-Null
    Invoke-Ui 'invoke' 'CloseButton' | Out-Null
    Assert-Draft
    Invoke-Ui 'invoke' 'PiShellButton' | Out-Null
    Wait-ShellState 'Running'
    Invoke-Ui 'invoke' 'PiShellCancelButton' | Out-Null
    Wait-ShellState 'Cancelled'
    if (-not ([string](Read-Element 'PiShellResultStatus').name).Contains('Exclude output')) { throw 'Context choice was lost.' }
    Invoke-Ui 'set-value' 'PiShellCommandInput' 'fail' | Out-Null
    Invoke-Ui 'invoke' 'PiShellRunButton' | Out-Null
    Wait-ShellState 'Failed'
    if (-not ([string](Read-Element 'PiShellResultStatus').name).Contains('Exit 7')) { throw 'Exit status was lost.' }
    Invoke-Ui 'invoke' 'CloseButton' | Out-Null
    Assert-Draft
    Stop-TestApp
    Start-TestApp
    $projectTree = Invoke-Ui 'inspect' 'ProjectSelector' '--depth' '12' | ConvertFrom-Json -Depth 100
    $projectButton = @($projectTree.windows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'fixture-project' })[0]
    Invoke-Ui 'invoke' $projectButton.selector | Out-Null
    Select-TestThread 'Thread 1'
    Assert-Draft
    Invoke-Ui 'invoke' 'PiShellButton' | Out-Null
    Wait-ShellState 'Failed'
    Invoke-Ui 'inspect' '--depth' '20' | Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Encoding utf8NoBOM
    $functionalPassed = $true
    if ($Capture) {
        Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'pi-shell.png') | Out-Null
        & (Join-Path $PSScriptRoot 'Invoke-ValidatedScreenshot.ps1') -FilePath 'winapp' -ArgumentList @(
            'ui', 'screenshot', 'AppMainWindow', '--output', (Join-Path $runRoot 'pi-shell.png'),
            '--app', "$script:launchedProcessId", '--json') | Out-Null
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
    [ordered]@{ passed = $passed; functionalPassed = $functionalPassed; visualCaptureRequested = [bool]$Capture; dataRoot = $dataRoot } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8NoBOM
    Write-Output "Pi shell native artifacts: $runRoot"
}
