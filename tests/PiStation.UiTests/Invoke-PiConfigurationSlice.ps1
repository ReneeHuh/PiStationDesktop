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
$runRoot = Join-Path $artifactRoot (Join-Path 'pi-configuration-runs' (
    "{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N')))
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

function Wait-PiConfigurationStatus {
    param([Parameter(Mandatory)][string] $Value)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $tree = Invoke-Ui 'inspect' 'ComposerSurface' '--depth' '12' | ConvertFrom-Json -Depth 100
        $matches = @($tree.windows | ForEach-Object { Get-TestThreadNodes -Node $_ } | Where-Object {
            $_.automationId -eq 'PiConfigurationStatusText' -and $_.name -eq $Value
        })
        if ($matches.Count -eq 1) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Pi configuration did not reach '$Value'."
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

function Find-UiElementByName {
    param(
        [Parameter(Mandatory)][object] $Value,
        [Parameter(Mandatory)][string] $Name
    )

    if ($Value -is [pscustomobject]) {
        if ($Value.name -eq $Name -and $Value.isInvokable -eq $true -and
            -not [string]::IsNullOrWhiteSpace($Value.selector)) {
            return $Value
        }

        foreach ($property in $Value.PSObject.Properties) {
            if ($null -eq $property.Value) {
                continue
            }

            $found = Find-UiElementByName -Value $property.Value -Name $Name
            if ($null -ne $found) {
                return $found
            }
        }
    }
    elseif ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        foreach ($item in $Value) {
            if ($null -eq $item) {
                continue
            }

            $found = Find-UiElementByName -Value $item -Name $Name
            if ($null -ne $found) {
                return $found
            }
        }
    }

    return $null
}

function Select-ComboBoxItem {
    param(
        [Parameter(Mandatory)][string] $ComboBox,
        [Parameter(Mandatory)][string] $ItemName
    )

    Invoke-Ui 'invoke' $ComboBox | Out-Null
    $tree = Invoke-Ui 'inspect' '--depth' '10' | ConvertFrom-Json
    $item = Find-UiElementByName -Value $tree -Name $ItemName
    if ($null -eq $item) {
        throw "Could not find the '$ItemName' option after expanding $ComboBox."
    }

    Invoke-Ui 'focus' $item.selector | Out-Null
    Invoke-Ui 'send-keys' 'enter' '--target' $item.selector | Out-Null
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
    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Invoke-Ui 'wait-for' 'fixture-project' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread -Title 'Thread 1' -Timeout 15000
    Invoke-Ui 'wait-for' 'PiConfigurationPanel' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'PiModelSelector' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'PiThinkingLevelSelector' '--timeout' '15000' | Out-Null
    Wait-PiConfigurationStatus -Value 'Fake Standard • Off'
    Invoke-Ui 'wait-for' 'PiRuntimeModeSelector' '--gone' '--timeout' '1000' | Out-Null

    Select-ComboBoxItem -ComboBox 'PiThinkingLevelSelector' -ItemName 'High'
    Wait-PiConfigurationStatus -Value 'Fake Standard • High'

    Select-ComboBoxItem -ComboBox 'PiModelSelector' -ItemName 'Fake Fast'
    Wait-PiConfigurationStatus -Value 'Fake Fast • Off'

    Stop-TestApp
    Start-TestApp
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready'
    Invoke-Ui 'wait-for' 'fixture-project' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'fixture-project' | Out-Null
    Wait-TestThread -Title 'Thread 1' -Timeout 10000
    Select-TestThread -Title 'Thread 1'
    Wait-PiConfigurationStatus -Value 'Fake Fast • Off'

    $tree = Invoke-Ui 'inspect' '--depth' '10'
    Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Value $tree -Encoding utf8NoBOM
    Invoke-Ui 'screenshot' '--output' (Join-Path $runRoot 'pi-configuration-slice.png') '--focus' |
        Out-Null

    Write-Output "Pi configuration slice passed for Pi Station Desktop (PID $launchedProcessId)."
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
