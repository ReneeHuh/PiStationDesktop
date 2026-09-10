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
$runRoot = Join-Path $artifactRoot (Join-Path 'interaction-runs' ("{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N')))
$dataRoot = Join-Path $runRoot 'data'
$projectPath = Join-Path $dataRoot 'fixture-project'
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

New-Item -ItemType Directory -Path $projectPath -Force | Out-Null

try {
    if (-not $NoBuild) {
        Invoke-CheckedNative -FilePath 'dotnet' -ArgumentList @(
            'build', $solutionPath, '--configuration', $Configuration, '--property', 'Platform=x64'
        ) | Write-Host
    }

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
        '--fake-pi-scenario', 'interactions',
        '--log-file', $logFile
    )
    $launch = $launchJson | ConvertFrom-Json
    $script:launchedProcessId = [int]$launch.ProcessId

    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready'
    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Invoke-Ui 'wait-for' 'fixture-project' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread -Title 'Thread 1' -Timeout 15000
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Idle'

    Invoke-Ui 'set-value' 'PromptInput' 'Exercise inline interactions' | Out-Null
    Invoke-Ui 'invoke' 'SendPromptButton' | Out-Null
    Invoke-Ui 'wait-for' 'ApprovalApproveButton' '--timeout' '15000' | Out-Null
    Wait-UiValue -Selector 'ApprovalApproveButton' -Value 'True' -Property 'IsEnabled'
    Invoke-Ui 'invoke' 'ApprovalApproveButton' | Out-Null
    Invoke-Ui 'wait-for' 'QuestionSubmitButton' '--timeout' '15000' | Out-Null
    Wait-UiValue -Selector 'QuestionSubmitButton' -Value 'True' -Property 'IsEnabled'
    Wait-UiValue -Selector 'ApprovalApproveButton' -Value 'False' -Property 'IsEnabled'
    Invoke-Ui 'invoke' 'QuestionSubmitButton' | Out-Null
    Wait-UiValue -Selector 'LatestAssistantMessage' -Value 'Approved. Selected: Run tests.'
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Idle'
    Wait-UiValue -Selector 'QuestionSubmitButton' -Value 'False' -Property 'IsEnabled'

    $approvalMarker = Join-Path $dataRoot 'sessions\approval-response.txt'
    $questionMarker = Join-Path $dataRoot 'sessions\question-response.txt'
    if ((Get-Content -Raw -LiteralPath $approvalMarker) -ne 'approved') {
        throw 'FakePi did not receive the approval response.'
    }

    if ((Get-Content -Raw -LiteralPath $questionMarker) -ne 'Run tests') {
        throw 'FakePi did not receive the selected question answer.'
    }

    $tree = Invoke-Ui 'inspect' '--depth' '10'
    Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Value $tree -Encoding utf8NoBOM
    Invoke-Ui 'screenshot' '--output' (Join-Path $runRoot 'interaction-slice.png') '--focus' | Out-Null

    Write-Output "Interaction slice passed for Pi Station Desktop (PID $launchedProcessId)."
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
