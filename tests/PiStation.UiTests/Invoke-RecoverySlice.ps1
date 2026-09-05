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
$runRoot = Join-Path $artifactRoot (Join-Path 'recovery-runs' ("{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N')))
$dataRoot = Join-Path $runRoot 'data'
$projectPath = Join-Path $dataRoot 'recovery-project'
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
        throw "$FilePath failed with exit code $LASTEXITCODE.`n$($output | Out-String)"
    }

    return ($output | Out-String).Trim()
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
            'build', $solutionPath, '--configuration', $Configuration
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
        '--fake-pi-scenario', 'crash-once',
        '--log-file', $logFile
    )
    $launch = $launchJson | ConvertFrom-Json
    $launchedProcessId = [int]$launch.ProcessId
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready'

    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Invoke-Ui 'wait-for' 'recovery-project' '--timeout' '10000' | Out-Null
    Wait-UiValue -Selector 'NewThreadButton' -Value 'True' -Property 'IsEnabled'
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Idle'

    Invoke-Ui 'set-value' 'PromptInput' 'Crash once' | Out-Null
    Wait-UiValue -Selector 'SendPromptButton' -Value 'True' -Property 'IsEnabled'
    Invoke-Ui 'invoke' 'SendPromptButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiCrashBanner' '--timeout' '15000' | Out-Null
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Pi crashed'
    Wait-UiValue -Selector 'RestartPiButton' -Value 'True' -Property 'IsEnabled'
    Invoke-Ui 'wait-for' 'TransportErrorBanner' '--gone' '--timeout' '1000' | Out-Null

    Invoke-Ui 'invoke' 'RestartPiButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiCrashBanner' '--gone' '--timeout' '15000' | Out-Null
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Idle'

    Invoke-Ui 'set-value' 'PromptInput' 'Continue after restart' | Out-Null
    Wait-UiValue -Selector 'SendPromptButton' -Value 'True' -Property 'IsEnabled'
    Invoke-Ui 'invoke' 'SendPromptButton' | Out-Null
    Wait-UiValue -Selector 'LatestAssistantMessage' -Value 'Hello from Fake Pi 👽'
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Idle'

    $tree = Invoke-Ui 'inspect' '--depth' '10'
    Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Value $tree -Encoding utf8NoBOM
    Invoke-Ui 'screenshot' '--output' (Join-Path $runRoot 'recovery-slice.png') '--focus' | Out-Null
    Write-Output "Recovery slice passed for Pi Station Desktop (PID $launchedProcessId)."
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
    $canonicalRoot = [System.IO.Path]::GetFullPath($dataRoot)
    $ownedFiles = @(Get-ChildItem -LiteralPath $dataRoot -Recurse -File -ErrorAction SilentlyContinue |
        ForEach-Object { [System.IO.Path]::GetFullPath($_.FullName) })
    [ordered]@{
        dataRoot = $canonicalRoot
        databasePath = Join-Path $canonicalRoot 'host.db'
        sessionRoot = Join-Path $canonicalRoot 'sessions'
        logFile = $logFile
        files = $ownedFiles
    } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $runRoot 'run-manifest.json') -Encoding utf8NoBOM
}

if ($null -ne $testError) {
    throw $testError
}
