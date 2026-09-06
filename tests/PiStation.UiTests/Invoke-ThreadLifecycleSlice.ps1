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
$runRoot = Join-Path $artifactRoot (Join-Path 'thread-lifecycle-runs' (
    "{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N')))
$dataRoot = Join-Path $runRoot 'data'
$projectPath = Join-Path $dataRoot 'lifecycle-project'
$logFile = Join-Path $dataRoot 'app.jsonl'
$launchedProcessId = $null
$testError = $null
. (Join-Path $PSScriptRoot 'Select-TestThread.ps1')

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

function Invoke-ThreadAction {
    param([Parameter(Mandatory)][string] $Selector)

    $lastFailure = $null
    foreach ($attempt in 1..4) {
        try {
            if ($attempt -gt 1) {
                Invoke-Ui 'send-keys' 'escape' | Out-Null
            }

            Invoke-Ui 'invoke' 'ThreadActionsButton' | Out-Null
            Invoke-Ui 'wait-for' $Selector '--timeout' '3000' | Out-Null
            Invoke-Ui 'invoke' $Selector | Out-Null
            return
        }
        catch {
            $lastFailure = $_
        }
    }

    throw "Could not invoke thread action '$Selector' after four attempts.`n$lastFailure"
}

New-Item -ItemType Directory -Path $projectPath -Force | Out-Null

try {
    if (-not $NoBuild) {
        Invoke-CheckedNative -FilePath 'dotnet' -ArgumentList @(
            'build', $solutionPath, '--configuration', $Configuration, '--property', 'Platform=x64'
        ) | Write-Host
    }

    Start-TestApp
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready'
    foreach ($selector in @(
        'ThreadSearchInput',
        'ArchivedThreadsToggle',
        'ThreadListStatusText'
    )) {
        Invoke-Ui 'wait-for' $selector '--timeout' '5000' | Out-Null
    }

    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Invoke-Ui 'wait-for' 'lifecycle-project' '--timeout' '10000' | Out-Null
    Wait-TestProjectSummary -ProjectName 'lifecycle-project' -Count 0
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread -Title 'Thread 1' -Timeout 15000
    Wait-TestProjectSummary -ProjectName 'lifecycle-project' -Count 1 -ThreadTitle 'Thread 1'

    Invoke-ThreadAction -Selector 'RenameThreadMenuItem'
    Invoke-Ui 'wait-for' 'ThreadRenameInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ThreadRenameInput' 'Roadmap thread' | Out-Null
    Invoke-Ui 'invoke' 'CommitThreadRenameButton' | Out-Null
    Invoke-Ui 'wait-for' 'ThreadRenameInput' '--gone' '--timeout' '5000' | Out-Null
    Wait-TestThread -Title 'Roadmap thread' -Timeout 15000
    Wait-UiValue -Selector 'ThreadLifecycleStatusText' -Value 'Renamed thread to Roadmap thread'
    Wait-TestProjectSummary -ProjectName 'lifecycle-project' -Count 1 -ThreadTitle 'Roadmap thread'

    Invoke-ThreadAction -Selector 'ToggleThreadPinMenuItem'
    Invoke-Ui 'wait-for' 'PinnedThreadIndicator' '--timeout' '15000' | Out-Null
    Wait-UiValue -Selector 'ThreadLifecycleStatusText' -Value 'Pinned Roadmap thread'

    Invoke-Ui 'set-value' 'ThreadSearchInput' 'roadmap' | Out-Null
    Wait-TestThread -Title 'Roadmap thread' -Timeout 10000
    Wait-TestProjectSummary -ProjectName 'lifecycle-project' -Count 1 -ThreadTitle 'Roadmap thread'
    Invoke-Ui 'set-value' 'ThreadSearchInput' 'missing' | Out-Null
    Wait-UiValue -Selector 'ThreadListStatusText' -Value 'No threads match “missing”'
    Wait-TestProjectSummary -ProjectName 'lifecycle-project' -Count 1 -ThreadTitle 'Roadmap thread'
    Invoke-Ui 'invoke' 'ClearThreadSearchButton' | Out-Null
    Wait-TestThread -Title 'Roadmap thread' -Timeout 10000

    Invoke-ThreadAction -Selector 'ToggleThreadArchiveMenuItem'
    Wait-UiValue -Selector 'ThreadListStatusText' -Value 'No threads yet'
    Wait-TestProjectSummary -ProjectName 'lifecycle-project' -Count 0
    Invoke-Ui 'invoke' 'ArchivedThreadsToggle' | Out-Null
    Wait-TestThread -Title 'Roadmap thread' -Timeout 15000
    Wait-TestProjectSummary -ProjectName 'lifecycle-project' -Count 0

    Invoke-Ui 'set-value' 'ThreadSearchInput' 'roadmap' | Out-Null
    Wait-TestThread -Title 'Roadmap thread' -Timeout 10000
    Invoke-Ui 'invoke' 'ClearThreadSearchButton' | Out-Null
    Invoke-ThreadAction -Selector 'ToggleThreadArchiveMenuItem'
    Wait-UiValue -Selector 'ThreadListStatusText' -Value 'No archived threads'
    Wait-TestProjectSummary -ProjectName 'lifecycle-project' -Count 1 -ThreadTitle 'Roadmap thread'
    Invoke-Ui 'invoke' 'ArchivedThreadsToggle' | Out-Null
    Wait-TestThread -Title 'Roadmap thread' -Timeout 15000

    Stop-TestApp
    Start-TestApp
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready'
    Invoke-Ui 'wait-for' 'lifecycle-project' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'lifecycle-project' | Out-Null
    Wait-TestProjectSummary -ProjectName 'lifecycle-project' -Count 1 -ThreadTitle 'Roadmap thread'
    Wait-TestThread -Title 'Roadmap thread' -Timeout 10000
    Invoke-Ui 'wait-for' 'PinnedThreadIndicator' '--timeout' '10000' | Out-Null
    Invoke-ThreadAction -Selector 'ToggleThreadPinMenuItem'
    Invoke-Ui 'wait-for' 'PinnedThreadIndicator' '--gone' '--timeout' '15000' | Out-Null
    Wait-UiValue -Selector 'ThreadLifecycleStatusText' -Value 'Unpinned Roadmap thread'

    $tree = Invoke-Ui 'inspect' '--depth' '12'
    Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Value $tree -Encoding utf8NoBOM
    Invoke-Ui 'screenshot' '--output' (Join-Path $runRoot 'thread-lifecycle-slice.png') '--focus' |
        Out-Null

    Write-Output "Thread lifecycle slice passed for Pi Station Desktop (PID $launchedProcessId)."
    Write-Output "Artifacts: $runRoot"
}
catch {
    $testError = $_
    if ($null -ne $launchedProcessId) {
        try {
            Invoke-Ui 'inspect' '--depth' '12' |
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
