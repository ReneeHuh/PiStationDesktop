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
$runRoot = Join-Path $PSScriptRoot (Join-Path 'artifacts\hardening-runs' (
    "{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N')))
$launchedProcessId = $null
$dataRoot = $null
$projectPath = $null
$logFile = $null
$testError = $null
. (Join-Path $PSScriptRoot 'Select-TestThread.ps1')

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

function Invoke-TransportDropDiagnostic {
    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsShell' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'SettingsDiagnosticsNavItem' | Out-Null
    Invoke-Ui 'wait-for' 'SimulateTransportDropButton' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'SimulateTransportDropButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsShell' '--gone' '--timeout' '5000' | Out-Null
}

function Get-UiNodes {
    param([AllowNull()][object] $Node)

    if ($null -eq $Node) {
        return
    }

    if ($Node.PSObject.Properties.Name -contains 'type') {
        $Node
    }

    foreach ($child in @($Node.children)) {
        if ($null -ne $child) {
            Get-UiNodes -Node $child
        }
    }

    foreach ($element in @($Node.elements)) {
        if ($null -ne $element) {
            Get-UiNodes -Node $element
        }
    }
}

function Assert-CurrentTranscript {
    param(
        [Parameter(Mandatory)][string] $ExpectedPrompt,
        [Parameter(Mandatory)][string] $ExcludedPrompt
    )

    $tree = Invoke-Ui 'inspect' 'TranscriptList' '--depth' '14' |
        ConvertFrom-Json -Depth 100
    $nodes = @($tree.windows | ForEach-Object { Get-UiNodes -Node $_ })
    $expected = @($nodes | Where-Object {
        $_.type -eq 'Text' -and $_.name.TrimEnd() -eq $ExpectedPrompt
    })
    $excluded = @($nodes | Where-Object {
        $_.type -eq 'Text' -and $_.name.TrimEnd() -eq $ExcludedPrompt
    })
    if ($expected.Count -ne 1 -or $excluded.Count -ne 0) {
        throw "Expected only '$ExpectedPrompt' in the selected transcript; " +
            "found expected=$($expected.Count), excluded=$($excluded.Count)."
    }
}

function Get-FakePiCommands {
    $commandLog = Join-Path $script:dataRoot 'sessions\command-log.jsonl'
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

function Wait-FakePiSettlementMarker {
    param([int] $Timeout = 20000)

    $sessionRoot = Join-Path $script:dataRoot 'sessions'
    $deadline = [DateTime]::UtcNow.AddMilliseconds($Timeout)
    do {
        $markers = @(Get-ChildItem -LiteralPath $sessionRoot -Filter '*.settled' -File -Force `
            -ErrorAction SilentlyContinue)
        if ($markers.Count -gt 0) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'Timed out waiting for FakePi to settle while the client was disconnected.'
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

function Start-TestApp {
    param(
        [Parameter(Mandatory)][string] $CaseName,
        [Parameter(Mandatory)][string] $Scenario,
        [int] $JournalEventLimit
    )

    Stop-TestApp
    $script:dataRoot = Join-Path $runRoot "$CaseName\data"
    $script:projectPath = Join-Path $script:dataRoot "$CaseName-project"
    $script:logFile = Join-Path $script:dataRoot 'app.jsonl'
    New-Item -ItemType Directory -Path $script:projectPath -Force | Out-Null

    $arguments = @(
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
        '--data-root', $script:dataRoot,
        '--pi-executable', $fakePi,
        '--fake-pi-scenario', $Scenario,
        '--log-file', $script:logFile
    )
    if ($JournalEventLimit -gt 0) {
        $arguments += @('--ui-test-journal-event-limit', "$JournalEventLimit")
    }

    $launch = Invoke-CheckedNative -FilePath 'winapp' -ArgumentList $arguments |
        ConvertFrom-Json
    $script:launchedProcessId = [int]$launch.ProcessId
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready'
    Wait-UiValue -Selector 'SettingsButton' -Value 'True' -Property 'IsEnabled'
}

function Initialize-Workspace {
    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $script:projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    $projectName = Split-Path -Leaf $script:projectPath
    Invoke-Ui 'wait-for' $projectName '--timeout' '10000' | Out-Null
    Wait-UiValue -Selector 'NewThreadButton' -Value 'True' -Property 'IsEnabled'
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread -Title 'Thread 1' -Timeout 15000
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Idle'
}

function Save-CaseArtifacts {
    param([Parameter(Mandatory)][string] $CaseName)

    $caseRoot = Join-Path $runRoot $CaseName
    Invoke-Ui 'inspect' '--depth' '12' |
        Set-Content -LiteralPath (Join-Path $caseRoot 'ui-tree.json') -Encoding utf8NoBOM
    Invoke-Ui 'screenshot' '--output' (Join-Path $caseRoot "$CaseName.png") '--focus' | Out-Null
}

function Test-StopAndThreadIsolation {
    $firstPrompt = 'Thread one remains isolated'
    $secondPrompt = 'Thread two remains isolated'

    Start-TestApp -CaseName 'stop-isolation' -Scenario 'stop'
    Initialize-Workspace

    Invoke-Ui 'set-value' 'PromptInput' $firstPrompt | Out-Null
    Invoke-Ui 'invoke' 'SendPromptButton' | Out-Null
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Pi is working'
    Wait-UiValue -Selector 'StopTurnButton' -Value 'True' -Property 'IsEnabled'
    Wait-UiValue -Selector 'NewThreadButton' -Value 'True' -Property 'IsEnabled'

    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread -Title 'Thread 2' -Timeout 15000
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Idle'
    Invoke-Ui 'set-value' 'PromptInput' $secondPrompt | Out-Null
    Invoke-Ui 'invoke' 'SendPromptButton' | Out-Null
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Pi is working'
    Wait-UiValue -Selector 'StopTurnButton' -Value 'True' -Property 'IsEnabled'

    # Automatic naming uses the first prompt once the turn starts.
    Select-TestThread -Title $firstPrompt
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Pi is working'
    Assert-CurrentTranscript -ExpectedPrompt $firstPrompt -ExcludedPrompt $secondPrompt
    Invoke-Ui 'invoke' 'StopTurnButton' | Out-Null
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Idle'
    Wait-UiValue -Selector 'StopTurnButton' -Value 'False' -Property 'IsEnabled'

    Select-TestThread -Title $secondPrompt
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Pi is working'
    Assert-CurrentTranscript -ExpectedPrompt $secondPrompt -ExcludedPrompt $firstPrompt
    Invoke-Ui 'invoke' 'StopTurnButton' | Out-Null
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Idle'

    Wait-FakePiCommandCount -Command 'prompt' -Count 2
    Wait-FakePiCommandCount -Command 'clear_queue' -Count 2
    Wait-FakePiCommandCount -Command 'abort' -Count 2
    $commands = Get-FakePiCommands
    foreach ($command in @('prompt', 'clear_queue', 'abort')) {
        $actual = @($commands | Where-Object command -EQ $command).Count
        if ($actual -ne 2) {
            throw "Expected exactly two '$command' commands across the isolated threads; found $actual."
        }
    }

    Save-CaseArtifacts -CaseName 'stop-isolation'
    Stop-TestApp
}

function Test-TransportReconnectAndResync {
    Start-TestApp -CaseName 'transport-resync' -Scenario 'ui-resync' -JournalEventLimit 2
    Initialize-Workspace

    Invoke-Ui 'set-value' 'PromptInput' 'Recover the running turn from a snapshot' | Out-Null
    Invoke-Ui 'invoke' 'SendPromptButton' | Out-Null
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Pi is working'
    Invoke-Ui 'wait-for' 'ReasoningExpander' '--timeout' '10000' | Out-Null
    Invoke-TransportDropDiagnostic
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Disconnected'
    Invoke-Ui 'wait-for' 'TransportErrorBanner' '--timeout' '5000' | Out-Null
    Invoke-Ui 'wait-for' 'PiCrashBanner' '--gone' '--timeout' '1000' | Out-Null
    Wait-UiValue -Selector 'SendPromptButton' -Value 'False' -Property 'IsEnabled'

    Wait-FakePiSettlementMarker
    Invoke-Ui 'invoke' 'ReconnectButton' | Out-Null
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready' -Timeout 30000
    Invoke-Ui 'wait-for' 'TransportErrorBanner' '--gone' '--timeout' '5000' | Out-Null
    Wait-UiValue -Selector 'LatestAssistantMessage' -Value 'Tool finished.' -Timeout 30000
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Idle'
    Wait-UiValue -Selector 'StopTurnButton' -Value 'False' -Property 'IsEnabled'

    $tree = Invoke-Ui 'inspect' 'TranscriptList' '--depth' '16' |
        ConvertFrom-Json -Depth 100
    $finalMessages = @($tree.windows | ForEach-Object { Get-UiNodes -Node $_ } | Where-Object {
        $_.className -eq 'RichTextBlock' -and $_.name.TrimEnd() -eq 'Tool finished.'
    })
    if ($finalMessages.Count -ne 1) {
        throw "Expected one recovered final response after snapshot resync; found $($finalMessages.Count)."
    }

    $promptCount = @(Get-FakePiCommands | Where-Object command -EQ 'prompt').Count
    if ($promptCount -ne 1) {
        throw "Reconnect resent the accepted prompt; expected one prompt command, found $promptCount."
    }

    Save-CaseArtifacts -CaseName 'transport-resync'
    Stop-TestApp
}

function Test-UncertainDispatch {
    Start-TestApp -CaseName 'uncertain-dispatch' -Scenario 'command-timeout'
    Initialize-Workspace

    Invoke-Ui 'set-value' 'PromptInput' 'Do not resend this uncertain turn' | Out-Null
    Invoke-Ui 'invoke' 'SendPromptButton' | Out-Null
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Pi is working'
    Wait-FakePiCommandCount -Command 'prompt' -Count 1
    Invoke-TransportDropDiagnostic
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Disconnected'
    Invoke-Ui 'wait-for' 'UncertainCommandBanner' '--timeout' '15000' | Out-Null
    Wait-UiValue -Selector 'SendPromptButton' -Value 'False' -Property 'IsEnabled'

    Invoke-Ui 'invoke' 'ReconnectButton' | Out-Null
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready' -Timeout 30000
    Invoke-Ui 'wait-for' 'UncertainCommandBanner' '--timeout' '5000' | Out-Null
    Wait-UiValue -Selector 'ResolveCommandButton' -Value 'True' -Property 'IsEnabled'
    Invoke-Ui 'invoke' 'ResolveCommandButton' | Out-Null
    Invoke-Ui 'wait-for' 'UncertainCommandBanner' '--timeout' '5000' | Out-Null

    $promptCount = @(Get-FakePiCommands | Where-Object command -EQ 'prompt').Count
    if ($promptCount -ne 1) {
        throw "Uncertain dispatch was resent; expected one prompt command, found $promptCount."
    }

    Save-CaseArtifacts -CaseName 'uncertain-dispatch'
    Stop-TestApp
}

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

try {
    if (-not $NoBuild) {
        Invoke-CheckedNative -FilePath 'dotnet' -ArgumentList @(
            'build', $solutionPath, '--configuration', $Configuration
        ) | Write-Host
    }

    if (-not (Test-Path -LiteralPath $fakePi -PathType Leaf)) {
        throw "FakePi was not built at '$fakePi'."
    }

    Test-StopAndThreadIsolation
    Test-TransportReconnectAndResync
    Test-UncertainDispatch
    Write-Output 'Hardening slice passed: stop/isolation, reconnect/resync, and uncertain dispatch.'
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
    $dataRoots = @(Get-ChildItem -LiteralPath $runRoot -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'data' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container } |
        ForEach-Object { [System.IO.Path]::GetFullPath($_) })
    $ownedFiles = @(foreach ($root in $dataRoots) {
        Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue |
            ForEach-Object { [System.IO.Path]::GetFullPath($_.FullName) }
    })
    [ordered]@{
        dataRoots = $dataRoots
        files = $ownedFiles
    } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $runRoot 'run-manifest.json') -Encoding utf8NoBOM
}

if ($null -ne $testError) {
    throw $testError
}
