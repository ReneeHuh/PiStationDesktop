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
$artifactRoot = Join-Path $PSScriptRoot 'artifacts'
$runRoot = Join-Path $artifactRoot (Join-Path 'draft-runs' ("{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N')))
$dataRoot = Join-Path $runRoot 'data'
$projectPath = Join-Path $dataRoot 'fixture-project'
$logFile = Join-Path $dataRoot 'app.jsonl'
$launchedProcessId = $null
$testError = $null

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $ArgumentList
    )

    $output = & $FilePath @ArgumentList 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($ArgumentList -join ' ') failed with exit code $LASTEXITCODE.`n$($output | Out-String)"
    }

    return ($output | Out-String).Trim()
}

function Start-TestApp {
    $launchJson = Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
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
        '--fake-pi-scenario', 'normal',
        '--log-file', $logFile
    )
    $launch = $launchJson | ConvertFrom-Json
    $script:launchedProcessId = [int]$launch.ProcessId
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

function Invoke-Ui {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)
    return Invoke-CheckedNative -FilePath 'winapp' -ArgumentList (@('ui') + $Arguments + @(
        '--app', "$script:launchedProcessId", '--json'
    ))
}

function Wait-UiValue {
    param(
        [Parameter(Mandatory)][string] $Selector,
        [Parameter(Mandatory)][string] $Value,
        [int] $Timeout = 15000,
        [string] $Property
    )

    $arguments = @('wait-for', $Selector, '--timeout', "$Timeout", '--value', $Value)
    if (-not [string]::IsNullOrWhiteSpace($Property)) {
        $arguments += @('--property', $Property)
    }

    Invoke-Ui @arguments | Out-Null
}

New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
$sourcePath = Join-Path $projectPath 'src'
New-Item -ItemType Directory -Path $sourcePath -Force | Out-Null
Set-Content -LiteralPath (Join-Path $sourcePath 'SearchTarget.cs') -Value 'class SearchTarget;' -Encoding utf8NoBOM

try {
    if (-not $NoBuild) {
        Invoke-CheckedNative -FilePath 'dotnet' -ArgumentList @(
            'build', $solutionPath, '--configuration', $Configuration, '--property', 'Platform=x64'
        ) | Write-Host
    }

    Start-TestApp
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready'
    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Invoke-Ui 'wait-for' 'fixture-project' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Invoke-Ui 'wait-for' 'Thread 1' '--timeout' '15000' | Out-Null
    Wait-UiValue -Selector 'DraftStatusText' -Value 'Saved'

    Invoke-Ui 'set-value' 'PromptInput' '@SearchTarget' | Out-Null
    Invoke-Ui 'send-keys' 'end' '--target' 'PromptInput' | Out-Null
    Invoke-Ui 'wait-for' 'FileMentionSuggestionsPanel' '--timeout' '10000' | Out-Null
    Wait-UiValue -Selector 'FileMentionStatusText' -Value '1 project file'
    Invoke-Ui 'wait-for' 'FileMentionSuggestionButton' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'FileMentionSuggestionButton' | Out-Null
    Wait-UiValue -Selector 'PromptInput' -Value '@src/SearchTarget.cs ' -Property 'Value'
    Wait-UiValue -Selector 'DraftStatusText' -Value 'Saved'

    Invoke-Ui 'set-value' 'PromptInput' 'Draft for thread one' | Out-Null
    Wait-UiValue -Selector 'DraftStatusText' -Value 'Saved'
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Invoke-Ui 'wait-for' 'Thread 2' '--timeout' '15000' | Out-Null
    Wait-UiValue -Selector 'DraftStatusText' -Value 'Saved'

    Invoke-Ui 'set-value' 'PromptInput' 'Draft for thread two' | Out-Null
    Wait-UiValue -Selector 'DraftStatusText' -Value 'Saved'
    Invoke-Ui 'invoke' 'Thread 1' | Out-Null
    Wait-UiValue -Selector 'PromptInput' -Value 'Draft for thread one' -Property 'Value'
    Invoke-Ui 'invoke' 'Thread 2' | Out-Null
    Wait-UiValue -Selector 'PromptInput' -Value 'Draft for thread two' -Property 'Value'

    Stop-TestApp
    Start-TestApp
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready'
    Invoke-Ui 'wait-for' 'fixture-project' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'fixture-project' | Out-Null
    Invoke-Ui 'wait-for' 'Thread 2' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'Thread 2' | Out-Null
    Wait-UiValue -Selector 'DraftStatusText' -Value 'Saved'
    Wait-UiValue -Selector 'PromptInput' -Value 'Draft for thread two' -Property 'Value'

    Invoke-Ui 'invoke' 'ToggleWorkbenchButton' | Out-Null
    Invoke-Ui 'invoke' 'FilesPanelTab' | Out-Null
    Invoke-Ui 'set-value' 'WorkbenchFileSearchInput' 'SearchTarget' | Out-Null
    Wait-UiValue -Selector 'WorkbenchFileStatusText' -Value '1 project file'
    Invoke-Ui 'invoke' 'SearchTarget.cs' | Out-Null
    Wait-UiValue -Selector 'WorkbenchFilePreviewPathText' -Value 'src/SearchTarget.cs'
    Invoke-Ui 'set-value' 'WorkbenchFileEditor' 'class RecoveredLocalEdits;' | Out-Null
    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'invoke' 'SimulateTransportDropButton' | Out-Null
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Disconnected'
    Invoke-Ui 'set-value' 'PromptInput' 'Offline draft recovered after closing' | Out-Null

    # Exercise the normal window-close path while the host cannot acknowledge either edit.
    $closing = [Diagnostics.Process]::GetProcessById($script:launchedProcessId)
    try {
        if (-not $closing.CloseMainWindow()) { throw 'The fixture window did not accept Close.' }
        if (-not $closing.WaitForExit(15000)) { throw 'The fixture did not close after preserving local edits.' }
    } finally { $closing.Dispose() }
    $script:launchedProcessId = $null
    if ([IO.File]::ReadAllText((Join-Path $sourcePath 'SearchTarget.cs')).Trim() -ne 'class SearchTarget;') {
        throw 'Unsaved recovery text was incorrectly written into the host workspace.'
    }
    Start-TestApp
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready'
    Invoke-Ui 'invoke' 'fixture-project' | Out-Null
    Invoke-Ui 'wait-for' 'Thread 2' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'Thread 2' | Out-Null
    Wait-UiValue -Selector 'PromptInput' -Value 'Offline draft recovered after closing' -Property 'Value'
    Invoke-Ui 'invoke' 'FilesPanelTab' | Out-Null
    Wait-UiValue -Selector 'WorkbenchFileEditor' -Value 'class RecoveredLocalEdits;' -Property 'Value'

    $tree = Invoke-Ui 'inspect' '--depth' '10'
    Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Value $tree -Encoding utf8NoBOM
    Invoke-Ui 'screenshot' '--output' (Join-Path $runRoot 'draft-slice.png') '--focus' | Out-Null

    Write-Output "Draft slice passed for Pi Station Desktop (PID $launchedProcessId)."
    Write-Output "Artifacts: $runRoot"
}
catch {
    $testError = $_
    if ($null -ne $launchedProcessId) {
        try {
            Invoke-Ui 'inspect' '--depth' '10' |
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
