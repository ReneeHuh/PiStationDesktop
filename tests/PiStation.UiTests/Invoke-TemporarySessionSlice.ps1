[CmdletBinding()]
param([switch] $NoBuild, [switch] $Capture)

$ErrorActionPreference = 'Stop'
$solutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$appProject = Join-Path $solutionRoot 'src/PiStation.App/PiStation.App.csproj'
$fakePi = Join-Path $solutionRoot 'tests/PiStation.FakePi/bin/Debug/net10.0/PiStation.FakePi.exe'
$runRoot = Join-Path $solutionRoot ('TestResults/temporary-session-native/' + [guid]::NewGuid().ToString('N'))
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
    $script:lastUiArgs = $Arguments -join ' '
    if ($Arguments -contains 'PromptInput' -and $Arguments[0] -in @('set-value', 'focus', 'click', 'invoke', 'type', 'send-keys', 'inspect', 'get-value', 'get-property', 'wait-for') -and $Arguments -notcontains '--gone') { Expand-TestComposer }
    if ($script:targetWindow) { Invoke-CheckedNative 'winapp' (@('ui') + $Arguments + @('--window', "$script:targetWindow", '--json')) }
    else { Invoke-CheckedNative 'winapp' (@('ui') + $Arguments + @('--app', "$script:launchedProcessId", '--json')) }
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
try {
    if (-not $NoBuild) { Invoke-CheckedNative 'dotnet' @('build', (Join-Path $solutionRoot 'PiStationDesktop.slnx'), '-c', 'Debug') | Write-Host }
    Start-TestApp
    $windows = Invoke-CheckedNative 'winapp' @('ui', 'list-windows', '--json') | ConvertFrom-Json
    $mainWindow = @($windows | Where-Object { $_.processId -eq $script:launchedProcessId -and $_.ownerHwnd -eq 0 })[0].hwnd
    $script:targetWindow = $mainWindow
    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Invoke-Ui 'wait-for' 'WorkspaceProjectStatusText' '--value' 'fixture-project' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread 'Thread 1'
    Invoke-Ui 'set-value' 'PromptInput' 'Keep my original draft.' | Out-Null
    Wait-Draft
    Open-SessionSettings
    Invoke-Ui 'invoke' 'NewTemporarySessionButton' | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $windows = Invoke-CheckedNative 'winapp' @('ui', 'list-windows', '--json') | ConvertFrom-Json
        $temporary = @($windows | Where-Object { $_.processId -eq $script:launchedProcessId -and $_.title -like 'Temporary*' }) | Select-Object -First 1
        if ($temporary) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $temporary) { throw 'Temporary window did not open.' }
    $script:targetWindow = $temporary.hwnd
    Invoke-Ui 'wait-for' 'TemporarySessionNotice' '--timeout' '10000' | Out-Null
    Wait-TestThread 'Thread 1'
    Invoke-Ui 'set-value' 'PromptInput' 'Discard this temporary conversation.' | Out-Null
    Invoke-Ui 'invoke' 'SendPromptButton' | Out-Null
    Invoke-Ui 'wait-for' 'TurnStatusText' '--value' 'Idle' '--timeout' '15000' | Out-Null
    Invoke-Ui 'set-value' 'PromptInput' 'Discard this unsent draft too.' | Out-Null
    Wait-Draft
    $temporaryRoot = @(Get-ChildItem -LiteralPath (Join-Path $dataRoot 'temporary-sessions') -Directory)[0].FullName
    if (-not $temporaryRoot) { throw 'Temporary storage ownership was not established.' }
    if (Test-Path -LiteralPath (Join-Path $temporaryRoot 'host.db')) { throw 'Temporary history created a disk database.' }
    Invoke-Ui 'inspect' '--depth' '10' | Set-Content -LiteralPath (Join-Path $runRoot 'temporary-ui-tree.json') -Encoding utf8NoBOM
    Invoke-Ui 'invoke' 'CloseTemporarySessionButton' | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ((Test-Path -LiteralPath $temporaryRoot) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (Test-Path -LiteralPath $temporaryRoot) { throw 'Closing did not discard temporary storage.' }
    $checks.Add('Closing the temporary window discards conversation, draft and owned storage')
    $script:targetWindow = $mainWindow
    Close-Settings
    Assert-Prompt 'Keep my original draft.'
    $checks.Add('Original window and draft survive temporary-session cleanup')
    $passed = $true
}
catch {
    Write-Output "Failed UI step: $script:lastUiArgs"
    try { Invoke-Ui 'inspect' '--depth' '14' | Set-Content -LiteralPath (Join-Path $runRoot 'failure-ui-tree.json') -Encoding utf8NoBOM } catch {}
    throw
}
finally {
    Stop-TestApp
    [ordered]@{ passed = $passed; checks = @($checks); dataRoot = $dataRoot } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8NoBOM
    Write-Output "Temporary session native artifacts: $runRoot"
}
