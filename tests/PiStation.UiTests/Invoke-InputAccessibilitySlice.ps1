[CmdletBinding()]
param(
    [ValidateSet('Debug')]
    [string] $Configuration = 'Debug',

    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$solutionRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$solutionPath = Join-Path $solutionRoot 'PiStationDesktop.slnx'
$appProject = Join-Path $solutionRoot 'src\PiStation.App\PiStation.App.csproj'
$fakePi = Join-Path $solutionRoot "tests\PiStation.FakePi\bin\$Configuration\net10.0\PiStation.FakePi.exe"
$runRoot = Join-Path $PSScriptRoot (Join-Path 'artifacts\input-accessibility-runs' (
    "{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N')))
$dataRoot = Join-Path $runRoot 'data'
$projectPath = Join-Path $dataRoot 'input-project'
$logFile = Join-Path $dataRoot 'app.jsonl'
$launchedProcessId = $null
$testError = $null
. (Join-Path $PSScriptRoot 'Select-TestThread.ps1')

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][AllowEmptyString()][string[]] $ArgumentList
    )

    $output = & $FilePath @ArgumentList 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE.`n$($output | Out-String)"
    }

    return ($output | Out-String).Trim()
}

function Invoke-Ui {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)
    if ($Arguments -contains 'PromptInput' -and $Arguments[0] -in @('set-value', 'focus', 'click', 'invoke', 'type', 'send-keys', 'inspect', 'get-value', 'get-property', 'wait-for') -and $Arguments -notcontains '--gone') { Expand-TestComposer }

    $result = Invoke-CheckedNative -FilePath 'winapp' -ArgumentList (@('ui') + $Arguments + @(
        '--app', "$script:launchedProcessId", '--json'
    ))
    if ($Arguments[0] -eq 'screenshot' -and ($outputIndex = [Array]::IndexOf($Arguments, '--output')) -ge 0) {
        $fallbackResult = & (Join-Path $PSScriptRoot 'Invoke-ValidatedScreenshot.ps1') -FilePath 'winapp' -ArgumentList (@('ui') + $Arguments + @('--app', "$script:launchedProcessId", '--json')); if ($fallbackResult) { $result = $fallbackResult }
    }
    return $result
}

function Wait-UiValue {
    param(
        [Parameter(Mandatory)][string] $Selector,
        [Parameter(Mandatory)][AllowEmptyString()][string] $Value,
        [int] $Timeout = 15000,
        [string] $Property
    )

    $arguments = @('wait-for', $Selector, '--timeout', "$Timeout", '--value', $Value)
    if (-not [string]::IsNullOrWhiteSpace($Property)) {
        $arguments += @('--property', $Property)
    }

    try {
        Invoke-Ui @arguments | Out-Null
    }
    catch {
        $propertySuffix = if ([string]::IsNullOrWhiteSpace($Property)) { '' } else { "; property='$Property'" }
        throw "Timed out waiting for selector='$Selector', value='$Value'$propertySuffix. $($_.Exception.Message)"
    }
}

function Invoke-TransportDropDiagnostic {
    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsShell' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'SettingsDiagnosticsNavItem' | Out-Null
    Invoke-Ui 'wait-for' 'SimulateTransportDropButton' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'SimulateTransportDropButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsShell' '--gone' '--timeout' '5000' | Out-Null
}

function Get-UiProperty {
    param(
        [Parameter(Mandatory)][string] $Selector,
        [Parameter(Mandatory)][string] $Property
    )

    $result = Invoke-Ui 'get-property' $Selector '--property' $Property | ConvertFrom-Json
    return [string]$result.properties.$Property
}

function Assert-AccessibleElement {
    param(
        [Parameter(Mandatory)][string] $Selector,
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $ControlType,
        [string] $IsEnabled = 'True',
        [string] $IsKeyboardFocusable = 'True'
    )

    $snapshot = Invoke-Ui 'get-property' $Selector | ConvertFrom-Json
    $expected = [ordered]@{
        AutomationId = $Selector
        Name = $Name
        ControlType = $ControlType
        IsEnabled = $IsEnabled
        IsKeyboardFocusable = $IsKeyboardFocusable
    }
    foreach ($property in $expected.Keys) {
        $actual = [string]$snapshot.properties.$property
        if ($actual -ne $expected[$property]) {
            throw "Expected $Selector.$property to be '$($expected[$property])'; found '$actual'."
        }
    }
}

function Assert-KeyboardFocus {
    param([Parameter(Mandatory)][string] $Selector)

    Wait-UiValue -Selector $Selector -Value 'True' -Property 'HasKeyboardFocus' -Timeout 5000
}

function Get-FakePiCommands {
    $commandLog = Join-Path $dataRoot 'sessions\command-log.jsonl'
    if (-not (Test-Path -LiteralPath $commandLog -PathType Leaf)) {
        return @()
    }

    return @(Get-Content -LiteralPath $commandLog | ForEach-Object {
        if (-not [string]::IsNullOrWhiteSpace($_)) {
            $_ | ConvertFrom-Json
        }
    })
}

function Wait-FakePiCommandCount {
    param(
        [Parameter(Mandatory)][string] $Command,
        [Parameter(Mandatory)][int] $Count,
        [int] $Timeout = 15000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($Timeout)
    do {
        $actual = @(Get-FakePiCommands | Where-Object command -EQ $Command).Count
        if ($actual -ge $Count) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Timed out waiting for $Count FakePi '$Command' commands; found $actual."
}

function Stop-TestApp {
    if ($null -eq $script:launchedProcessId) {
        return
    }

    $ownedProcess = Get-Process -Id $script:launchedProcessId -ErrorAction SilentlyContinue
    if ($null -ne $ownedProcess -and $ownedProcess.ProcessName -eq 'PiStationDesktop') {
        Stop-Process -Id $script:launchedProcessId
        Wait-Process -Id $script:launchedProcessId -Timeout 10 -ErrorAction SilentlyContinue
    }

    $script:launchedProcessId = $null
}

function Assert-TailIsNotVisible {
    $arguments = @(
        'ui', 'get-property', 'Pi: Long transcript tail marker', '--property', 'IsOffscreen',
        '--app', "$script:launchedProcessId", '--json'
    )
    $output = & winapp @arguments 2>&1
    if ($LASTEXITCODE -eq 0) {
        $result = ($output | Out-String) | ConvertFrom-Json
        if ([string]$result.properties.IsOffscreen -ne 'True') {
            throw 'Expected the transcript tail to be offscreen after scrolling to the top.'
        }
    }
}

New-Item -ItemType Directory -Path (Join-Path $projectPath 'src') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $projectPath 'src\SearchTarget.cs') `
    -Value 'class SearchTarget;' -Encoding utf8NoBOM
$uiGatePath = Join-Path $projectPath '.pistation-ui-tool-gates'
New-Item -ItemType Directory -Path $uiGatePath -Force | Out-Null

try {
    if (-not $NoBuild) {
        Invoke-CheckedNative -FilePath 'dotnet' -ArgumentList @(
            'build', $solutionPath, '--configuration', $Configuration, '--property', 'Platform=x64'
        ) | Write-Host
    }

    if (-not (Test-Path -LiteralPath $fakePi -PathType Leaf)) {
        throw "FakePi was not built at '$fakePi'."
    }

    $launch = Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'run', $appProject,
        '--configuration', $Configuration,
        '--arch', 'x64',
        '--property', 'Platform=x64',
        '--no-build',
        '--no-restore',
        '--detach',
        '--json',
        '--',
        '--ui-test',
        '--data-root', $dataRoot,
        '--pi-executable', $fakePi,
        '--fake-pi-scenario', 'ui-input',
        '--log-file', $logFile
    ) | ConvertFrom-Json
    $script:launchedProcessId = [int]$launch.ProcessId

    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready'
    Assert-AccessibleElement -Selector 'NewProjectButton' -Name 'Add project' -ControlType 'Button'
    Assert-AccessibleElement -Selector 'NewThreadButton' -Name 'Create thread' -ControlType 'Button' `
        -IsEnabled 'False' -IsKeyboardFocusable 'False'
    Assert-AccessibleElement -Selector 'PromptInput' -Name 'Prompt for Pi' -ControlType 'Edit'
    Assert-AccessibleElement -Selector 'AttachFilesButton' -Name 'Attach files to draft' `
        -ControlType 'Button' -IsEnabled 'False' -IsKeyboardFocusable 'False'
    Assert-AccessibleElement -Selector 'SendPromptButton' -Name 'Send prompt' -ControlType 'Button' `
        -IsEnabled 'False' -IsKeyboardFocusable 'False'
    Assert-AccessibleElement -Selector 'StopTurnButton' -Name 'Stop current turn' -ControlType 'Button' `
        -IsEnabled 'False' -IsKeyboardFocusable 'False'

    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Assert-AccessibleElement -Selector 'ProjectPathInput' -Name 'Project directory path' -ControlType 'Edit'
    Assert-AccessibleElement -Selector 'AddProjectConfirmButton' -Name 'Confirm add project' -ControlType 'Button'
    Assert-AccessibleElement -Selector 'CloseButton' -Name 'Cancel' -ControlType 'Button'
    Assert-KeyboardFocus -Selector 'ProjectPathInput'

    Invoke-Ui 'send-keys' 'tab' '--target' 'ProjectPathInput' '--via' 'post-message' | Out-Null
    Assert-KeyboardFocus -Selector 'AddProjectConfirmButton'
    Invoke-Ui 'send-keys' 'tab' '--target' 'AddProjectConfirmButton' '--via' 'post-message' | Out-Null
    Assert-KeyboardFocus -Selector 'CloseButton'
    Invoke-Ui 'send-keys' 'esc' '--target' 'AddProjectConfirmButton' '--via' 'post-message' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--gone' '--timeout' '5000' | Out-Null
    Assert-KeyboardFocus -Selector 'NewProjectButton'
    Invoke-Ui 'wait-for' 'input-project' '--gone' '--timeout' '2000' | Out-Null

    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'send-keys' 'enter' '--target' 'AddProjectConfirmButton' '--via' 'post-message' | Out-Null
    Invoke-Ui 'wait-for' 'input-project' '--timeout' '10000' | Out-Null
    Wait-UiValue -Selector 'NewThreadButton' -Value 'True' -Property 'IsEnabled'

    Invoke-Ui 'focus' 'NewThreadButton' | Out-Null
    Assert-KeyboardFocus -Selector 'NewThreadButton'
    Invoke-Ui 'send-keys' 'tab' '--target' 'NewThreadButton' '--via' 'post-message' | Out-Null
    Assert-KeyboardFocus -Selector 'NewProjectButton'
    Invoke-Ui 'screenshot' '--output' (Join-Path $runRoot 'keyboard-focus.png') '--focus' | Out-Null

    Invoke-Ui 'send-keys' 'enter' '--target' 'NewThreadButton' '--via' 'post-message' | Out-Null
    Wait-TestThread -Title 'Thread 1' -Timeout 15000
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Idle'
    Assert-AccessibleElement -Selector 'ProjectSelector' -Name 'Projects' -ControlType 'List' `
        -IsKeyboardFocusable 'False'
    Assert-AccessibleElement -Selector 'ThreadTabList' -Name 'Threads' -ControlType 'List' `
        -IsKeyboardFocusable 'False'
    Assert-AccessibleElement -Selector 'TranscriptList' -Name 'Conversation transcript' -ControlType 'List' `
        -IsKeyboardFocusable 'False'
    Assert-AccessibleElement -Selector 'PromptInput' -Name 'Prompt for Pi' -ControlType 'Edit'
    Assert-AccessibleElement -Selector 'AttachFilesButton' -Name 'Attach files to draft' -ControlType 'Button'

    $promptPrefix = 'Unicode π 👽 and "quoted text"'
    Invoke-Ui 'set-value' 'PromptInput' $promptPrefix | Out-Null
    Invoke-Ui 'send-keys' 'end' '--target' 'PromptInput' '--via' 'post-message' | Out-Null
    Invoke-Ui 'send-keys' 'shift+enter' '--target' 'PromptInput' '--via' 'post-message' | Out-Null
    Invoke-Ui 'send-keys' 'Second line' '--target' 'PromptInput' '--via' 'post-message' '--verbatim' | Out-Null
    $multilineValue = (Get-UiProperty -Selector 'PromptInput' -Property 'Value') -replace "`r`n?", "`n"
    if ($multilineValue -ne "$promptPrefix`nSecond line") {
        throw "Shift+Enter did not preserve multiline input. Found '$multilineValue'."
    }

    $expectedPrompt = "$multilineValue`nLong payload: " + ('x' * 2048)
    # The first prompt replaces the placeholder title. Mirror the host's
    # whitespace normalization and 72-character generated-title bound.
    $expectedThreadTitle = ($expectedPrompt -replace '\s+', ' ').Trim()
    if ($expectedThreadTitle.Length -gt 72) {
        $expectedThreadTitle = $expectedThreadTitle.Substring(0, 69).TrimEnd() + '…'
    }
    Invoke-Ui 'set-value' 'PromptInput' $expectedPrompt | Out-Null
    $actualPrompt = (Get-UiProperty -Selector 'PromptInput' -Property 'Value') -replace "`r`n?", "`n"
    if ($actualPrompt -ne $expectedPrompt) {
        throw 'The prompt input did not preserve Unicode, quotes, multiline text, and the long payload.'
    }

    Wait-UiValue -Selector 'SendPromptButton' -Value 'True' -Property 'IsEnabled'
    Invoke-Ui 'send-keys' 'enter' '--target' 'PromptInput' '--via' 'post-message' | Out-Null
    Wait-FakePiCommandCount -Command 'prompt' -Count 1
    Wait-UiValue -Selector 'StopTurnButton' -Value 'True' -Property 'IsEnabled'
    Wait-UiValue -Selector 'SendPromptButton' -Value 'False' -Property 'IsEnabled'
    # A running turn permits preparing attachments for the next draft.
    Wait-UiValue -Selector 'AttachFilesButton' -Value 'True' -Property 'IsEnabled'
    Wait-UiValue -Selector 'PromptInput' -Value '' -Property 'Value'

    # Hold the fixture after turn_start so native property and accessibility
    # assertions observe the running state before the transcript is emitted.
    New-Item -ItemType File -Path (Join-Path $uiGatePath 'input-ready') -Force | Out-Null

    Wait-UiValue -Selector 'TurnStatusText' -Value 'Idle' -Timeout 30000
    Wait-UiValue -Selector 'StopTurnButton' -Value 'False' -Property 'IsEnabled'
    Wait-UiValue -Selector 'AttachFilesButton' -Value 'True' -Property 'IsEnabled'
    Invoke-Ui 'scroll' 'TranscriptList' '--to' 'bottom' | Out-Null
    Invoke-Ui 'scroll-into-view' 'Pi: Long transcript tail marker' | Out-Null
    Wait-UiValue -Selector 'Pi: Long transcript tail marker' -Value 'False' -Property 'IsOffscreen'
    $promptCommands = @(Get-FakePiCommands | Where-Object command -EQ 'prompt')
    if ($promptCommands.Count -ne 1 -or (($promptCommands[0].message -replace "`r`n?", "`n") -ne $expectedPrompt)) {
        throw 'Expected Enter to dispatch the rich prompt exactly once without modifying its content.'
    }

    Invoke-Ui 'screenshot' '--output' (Join-Path $runRoot 'transcript-bottom.png') '--focus' | Out-Null
    Invoke-Ui 'scroll' 'TranscriptList' '--to' 'top' | Out-Null
    Invoke-Ui 'scroll-into-view' 'Pi: Long transcript head marker' | Out-Null
    Wait-UiValue -Selector 'Pi: Long transcript head marker' -Value 'False' -Property 'IsOffscreen'
    Assert-TailIsNotVisible
    Invoke-Ui 'screenshot' '--output' (Join-Path $runRoot 'transcript-top.png') '--focus' | Out-Null
    Invoke-Ui 'scroll' 'TranscriptList' '--to' 'bottom' | Out-Null
    Invoke-Ui 'scroll-into-view' 'Pi: Long transcript tail marker' | Out-Null
    Wait-UiValue -Selector 'Pi: Long transcript tail marker' -Value 'False' -Property 'IsOffscreen'

    Invoke-Ui 'invoke' 'ThreadActionsButton' | Out-Null
    Invoke-Ui 'wait-for' 'RenameThreadMenuItem' '--timeout' '5000' | Out-Null
    Assert-AccessibleElement -Selector 'RenameThreadMenuItem' -Name 'Rename thread' -ControlType 'MenuItem'
    Assert-AccessibleElement -Selector 'ToggleThreadPinMenuItem' -Name 'Change thread pin' -ControlType 'MenuItem'
    Assert-AccessibleElement -Selector 'ToggleThreadArchiveMenuItem' -Name 'Change thread archive state' `
        -ControlType 'MenuItem'
    Invoke-Ui 'invoke' 'RenameThreadMenuItem' | Out-Null
    Invoke-Ui 'wait-for' 'ThreadRenameInput' '--timeout' '5000' | Out-Null
    Assert-KeyboardFocus -Selector 'ThreadRenameInput'
    Invoke-Ui 'set-value' 'ThreadRenameInput' 'This rename must be cancelled' | Out-Null
    Invoke-Ui 'send-keys' 'esc' '--target' 'ThreadRenameInput' '--via' 'post-message' | Out-Null
    Invoke-Ui 'wait-for' 'ThreadRenameInput' '--gone' '--timeout' '5000' | Out-Null
    Wait-TestThread -Title $expectedThreadTitle -Timeout 5000

    Invoke-Ui 'set-value' 'PromptInput' '@SearchTarget' | Out-Null
    Invoke-Ui 'send-keys' 'end' '--target' 'PromptInput' '--via' 'post-message' | Out-Null
    Invoke-Ui 'wait-for' 'FileMentionSuggestionsPanel' '--timeout' '10000' | Out-Null
    Invoke-Ui 'send-keys' 'esc' '--target' 'PromptInput' '--via' 'post-message' | Out-Null
    Invoke-Ui 'wait-for' 'FileMentionSuggestionsPanel' '--gone' '--timeout' '5000' | Out-Null
    Wait-UiValue -Selector 'PromptInput' -Value '@SearchTarget' -Property 'Value'

    Invoke-Ui 'set-value' 'PromptInput' 'Draft remains local while disconnected' | Out-Null
    Wait-UiValue -Selector 'SendPromptButton' -Value 'True' -Property 'IsEnabled'
    Wait-UiValue -Selector 'DraftStatusText' -Value 'Saved'
    Invoke-TransportDropDiagnostic
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Disconnected'
    foreach ($selector in @(
        'NewProjectButton',
        'NewThreadButton',
        'AttachFilesButton',
        'SendPromptButton',
        'StopTurnButton'
    )) {
        Wait-UiValue -Selector $selector -Value 'False' -Property 'IsEnabled'
    }

    Invoke-Ui 'invoke' 'ReconnectButton' | Out-Null
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready' -Timeout 30000
    Wait-UiValue -Selector 'NewProjectButton' -Value 'True' -Property 'IsEnabled'
    Wait-UiValue -Selector 'NewThreadButton' -Value 'True' -Property 'IsEnabled'
    Wait-UiValue -Selector 'AttachFilesButton' -Value 'True' -Property 'IsEnabled'
    Wait-UiValue -Selector 'SendPromptButton' -Value 'True' -Property 'IsEnabled'
    Wait-UiValue -Selector 'PromptInput' -Value 'Draft remains local while disconnected' -Property 'Value'
    Invoke-Ui 'set-value' 'PromptInput' '' | Out-Null

    Invoke-Ui 'inspect' '--depth' '14' |
        Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Encoding utf8NoBOM
    Invoke-Ui 'screenshot' '--output' (Join-Path $runRoot 'input-accessibility.png') '--focus' | Out-Null

    Write-Output "Input/accessibility slice passed for Pi Station Desktop (PID $launchedProcessId)."
    Write-Output "Artifacts: $runRoot"
}
catch {
    $testError = $_
    if ($null -ne $launchedProcessId) {
        try {
            Invoke-Ui 'inspect' '--depth' '14' |
                Set-Content -LiteralPath (Join-Path $runRoot 'failure-ui-tree.json') -Encoding utf8NoBOM
            Invoke-Ui 'screenshot' '--output' (Join-Path $runRoot 'failure.png') '--focus' | Out-Null
        }
        catch {
            # Preserve the original failure when diagnostic capture is unavailable.
        }
    }
}
finally {
    Stop-TestApp
    $manifest = [ordered]@{
        dataRoot = [System.IO.Path]::GetFullPath($dataRoot)
        databasePath = Join-Path $dataRoot 'host.db'
        sessionRoot = Join-Path $dataRoot 'sessions'
        logFile = $logFile
    }
    $manifest | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $runRoot 'run-manifest.json') -Encoding utf8NoBOM
}

if ($null -ne $testError) {
    throw $testError
}
