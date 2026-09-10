[CmdletBinding()]
param([switch] $NoBuild, [switch] $Capture)

$ErrorActionPreference = 'Stop'
$solutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$appProject = Join-Path $solutionRoot 'src/PiStation.App/PiStation.App.csproj'
$fakePi = Join-Path $solutionRoot 'tests/PiStation.FakePi/bin/Debug/net10.0/PiStation.FakePi.exe'
$runRoot = Join-Path $solutionRoot ('TestResults/pi-sessions-native/' + [guid]::NewGuid().ToString('N'))
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

function Export-SessionThroughPicker {
    param([string] $Format)
    $windows = Invoke-CheckedNative 'winapp' @('ui', 'list-windows', '--json') | ConvertFrom-Json
    $owner = @($windows | Where-Object { $_.processId -eq $script:launchedProcessId -and $_.ownerHwnd -eq 0 })[0].hwnd
    if (-not $owner) { throw 'The owned app window was not found.' }
    Invoke-Ui 'invoke' $(if ($Format -eq 'html') { 'ExportPiSessionHtmlButton' } else { 'ExportPiSessionJsonlButton' }) | Out-Null
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
        if (([string](Read-Element 'PiSessionsStatusText').name).Contains("bytes to $destination")) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    $contents = Get-Content -LiteralPath $destination -Raw
    if (-not $contents.Contains('First CLI answer') -or $contents.Contains('Second CLI answer')) { throw 'The export does not match the forked conversation.' }
    $checks.Add("Native $Format export through the Windows save dialog matches the selected session")
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
    if (-not $NoBuild) {
        Invoke-CheckedNative 'dotnet' @('build', (Join-Path $solutionRoot 'PiStationDesktop.slnx'), '-c', 'Debug', '-p:Platform=x64') | Write-Host
    }
    Start-TestApp
    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Select-TestProject 'fixture-project'
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread 'Thread 1'
    Invoke-Ui 'set-value' 'PromptInput' 'Original workspace draft.' | Out-Null
    Wait-Draft
    Open-SessionSettings
    Invoke-Ui 'set-value' 'PiSessionDirectoryInput' $importDirectory | Out-Null
    Invoke-Ui 'invoke' 'BrowsePiSessionsButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiSessionsStatusText' '--value' 'Found 1 sessions. 0 unreadable or unsupported files skipped.' '--timeout' '15000' | Out-Null
    Select-SessionRow 'PiSessionCandidateList' 'First CLI question'
    Invoke-Ui 'set-value' 'PiSessionTitleInput' 'Imported CLI' | Out-Null
    Invoke-Ui 'invoke' 'ImportSelectedPiSessionButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiSessionsStatusText' '--value' 'Created Imported CLI. The original session and draft are preserved.' '--timeout' '15000' | Out-Null
    Close-Settings
    Wait-TestThread 'Imported CLI'
    Assert-Prompt ''
    Invoke-Ui 'set-value' 'PromptInput' 'Imported thread draft.' | Out-Null
    Wait-Draft
    $checks.Add('CLI import creates a separate thread with an empty draft')
    Open-SessionSettings
    Invoke-Ui 'invoke' 'InspectPiSessionButton' | Out-Null
    Assert-SessionSummary 4
    Select-SessionRow 'PiSessionTreeList' 'assistant · First CLI answer'
    Invoke-Ui 'invoke' 'FoldPiSessionEntryButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiSessionsStatusText' '--value' 'Showing 2 of 2 matching entries. Select an entry to edit its bookmark or switch branches.' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'Expand all branches' | Out-Null
    Invoke-Ui 'wait-for' 'PiSessionsStatusText' '--value' 'Showing 4 of 4 matching entries. Select an entry to edit its bookmark or switch branches.' '--timeout' '10000' | Out-Null
    Select-SessionRow 'PiSessionTreeList' 'assistant · First CLI answer'
    $checks.Add('Native tree folding and expanding retain the selected conversation')
    Invoke-Ui 'set-value' 'PiSessionTitleInput' 'Forked CLI' | Out-Null
    Invoke-Ui 'invoke' 'ForkPiSessionButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiSessionsStatusText' '--value' 'Created Forked CLI. The original session and draft are preserved.' '--timeout' '15000' | Out-Null
    Close-Settings
    Wait-TestThread 'Forked CLI'
    Assert-Prompt ''
    Open-SessionSettings
    Invoke-Ui 'invoke' 'InspectPiSessionButton' | Out-Null
    Assert-SessionSummary 2
    if (-not ([string](Read-Element 'PiSessionSummaryText').name).Contains('fake-fast')) { throw 'Fork lost its model.' }
    $checks.Add('Selected-response fork retains its branch and model with a separate draft')
    Close-Settings
    Select-TestThread 'Imported CLI'
    Assert-Prompt 'Imported thread draft.'
    Select-TestThread 'Thread 1'
    Assert-Prompt 'Original workspace draft.'
    Stop-TestApp
    Start-TestApp
    Select-TestProject 'fixture-project'
    Wait-TestThread 'Imported CLI'
    Select-TestThread 'Imported CLI'
    Assert-Prompt 'Imported thread draft.'
    Open-SessionSettings
    Invoke-Ui 'invoke' 'InspectPiSessionButton' | Out-Null
    Assert-SessionSummary 4
    Close-Settings
    Select-TestThread 'Forked CLI'
    Assert-Prompt ''
    Open-SessionSettings
    Invoke-Ui 'invoke' 'InspectPiSessionButton' | Out-Null
    Assert-SessionSummary 2
    $checks.Add('Both sessions and their separate drafts survive app relaunch')
    if ((Get-FileHash -LiteralPath $source).Hash -ne $sourceHash) { throw 'Import changed the original CLI session.' }
    $checks.Add('Original CLI session remains byte-for-byte unchanged')
    Export-SessionThroughPicker 'jsonl'
    Export-SessionThroughPicker 'html'
    Invoke-Ui 'invoke' 'SharePiSessionButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiSessionShareDialog' '--timeout' '15000' | Out-Null
    $shareTree = Invoke-Ui 'inspect' 'PiSessionShareDialog' '--depth' '8' | ConvertFrom-Json -Depth 100
    $shareNodes = @($shareTree.windows | ForEach-Object { Get-TestThreadNodes $_ })
    $publish = @($shareNodes | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Create gist' })[0]
    $cancel = @($shareNodes | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Cancel' })[0]
    if (-not $publish -or $publish.isEnabled -ne $false -or -not $cancel) { throw 'Gist sharing did not require review before publication.' }
    Invoke-Ui 'invoke' $cancel.selector | Out-Null
    Invoke-Ui 'wait-for' 'PiSessionShareDialog' '--gone' '--timeout' '5000' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsDialog' '--timeout' '5000' | Out-Null
    $checks.Add('Gist review opens from Settings, requires review before publication and cancels without sharing')
    Invoke-Ui 'inspect' '--depth' '14' | Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Encoding utf8NoBOM
    if ($Capture) {
        Invoke-Ui 'screenshot' 'AppMainWindow' '--capture-screen' '--output' (Join-Path $runRoot 'pi-sessions.png') | Out-Null
        & (Join-Path $PSScriptRoot 'Assert-ValidScreenshot.ps1') -Path (Join-Path $runRoot 'pi-sessions.png') | Out-Null
    }
    $passed = $true
}
catch {
    Write-Output "Last UI request: $script:lastUiArgs"
    if ($null -ne $launchedProcessId) {
        try { Invoke-Ui 'inspect' '--depth' '14' | Set-Content -LiteralPath (Join-Path $runRoot 'failure-ui-tree.json') -Encoding utf8NoBOM } catch {}
    }
    throw
}
finally {
    Stop-TestApp
    [ordered]@{ passed = $passed; checks = @($checks); visualCaptureRequested = [bool]$Capture; dataRoot = $dataRoot } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8NoBOM
    Write-Output "Pi session native artifacts: $runRoot"
}
