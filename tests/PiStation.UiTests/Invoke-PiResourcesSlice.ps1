[CmdletBinding()]
param([switch] $NoBuild, [switch] $Capture)

$ErrorActionPreference = 'Stop'
$solutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$appProject = Join-Path $solutionRoot 'src/PiStation.App/PiStation.App.csproj'
$fakePi = Join-Path $solutionRoot 'tests/PiStation.FakePi/bin/Debug/net10.0/PiStation.FakePi.exe'
$runRoot = Join-Path $solutionRoot ('TestResults/pi-resources-native/' + [guid]::NewGuid().ToString('N'))
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
    if ($Arguments -contains 'PromptInput' -and $Arguments[0] -in @('set-value', 'focus', 'click', 'invoke', 'type', 'send-keys', 'inspect', 'get-value', 'get-property', 'wait-for') -and $Arguments -notcontains '--gone') { Expand-TestComposer }
    Invoke-CheckedNative 'winapp' (@('ui') + $Arguments + @('--app', "$script:launchedProcessId", '--json'))
}
function Start-TestApp {
    $launch = Invoke-CheckedNative 'winapp' @('run', $appProject, '--configuration', 'Debug',
        '--arch', 'x64', '--property', 'Platform=x64', '--no-build', '--no-restore', '--detach', '--json',
        '--', '--ui-test', '--data-root', $dataRoot, '--pi-executable', $fakePi,
        '--fake-pi-scenario', 'resource-management', '--log-file', (Join-Path $runRoot 'app.jsonl')) | ConvertFrom-Json
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
function Open-ResourceSettings {
    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsPiResourcesNavItem' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'SettingsPiResourcesNavItem' | Out-Null
    Invoke-Ui 'invoke' 'RefreshPiResourcesButton' | Out-Null
    Invoke-Ui 'wait-for' 'TogglePiResourceButton' '--property' 'IsEnabled' '--value' 'True' '--timeout' '15000' | Out-Null
}
function Assert-ResourceText {
    param([string] $Text)
    $tree = Invoke-Ui 'inspect' '--depth' '14' | ConvertFrom-Json -Depth 100
    $names = ($tree.windows | ForEach-Object { Get-TestThreadNodes $_ } | ForEach-Object { $_.name }) -join "`n"
    if (-not $names.Contains($Text)) { throw "Resource inventory is missing: $Text" }
}
function Close-Settings {
    Invoke-Ui 'invoke' 'CloseButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsDialog' '--gone' '--timeout' '5000' | Out-Null
}

New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
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
    Invoke-Ui 'set-value' 'PromptInput' 'Keep this draft while managing Pi.' | Out-Null
    Wait-Draft
    Open-ResourceSettings
    Assert-ResourceText 'managed-fixture'
    Assert-ResourceText 'Confirmed in this runtime'
    Invoke-Ui 'wait-for' 'PiProviderSummaryText' '--value' 'Fake Provider (fake) · 2 models · credential configured (runtime)' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'TogglePiResourceButton' | Out-Null
    Invoke-Ui 'wait-for' 'TogglePiResourceButton' '--value' 'Enable' '--timeout' '10000' | Out-Null
    Assert-ResourceText 'Confirmed in this runtime · disabled in Pi settings'
    $checks.Add('Inventory distinguishes saved resource settings from the active runtime')
    Invoke-Ui 'invoke' 'TrustPiProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiProjectTrustText' '--value' 'This runtime: project not trusted. Saved decision: trust.' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'RestartResourcePiButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiProjectTrustText' '--value' 'This runtime: project trusted. Saved decision: trust.' '--timeout' '15000' | Out-Null
    Assert-ResourceText 'Not confirmed in this runtime · disabled in Pi settings'
    $checks.Add('Resource and trust settings take effect on explicit idle restart')
    Invoke-Ui 'invoke' 'CustomPiModelExpander' | Out-Null
    Invoke-Ui 'wait-for' 'CustomPiProviderInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'CustomPiProviderInput' 'native-local' | Out-Null
    Invoke-Ui 'set-value' 'CustomPiModelInput' 'native-test' | Out-Null
    Invoke-Ui 'set-value' 'CustomPiEndpointInput' 'http://127.0.0.1:18080/v1' | Out-Null
    Invoke-Ui 'invoke' 'CustomPiKeylessToggle' | Out-Null
    Invoke-Ui 'invoke' 'SaveCustomPiModelButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiResourcesStatusText' '--value' 'Configuration saved. Restart to apply.' '--timeout' '10000' | Out-Null
    $checks.Add('Native custom-model fields reach the host with current input values')
    Close-Settings
    Assert-Prompt 'Keep this draft while managing Pi.'
    Stop-TestApp
    Start-TestApp
    Select-TestProject 'fixture-project'
    Wait-TestThread 'Thread 1'
    Select-TestThread 'Thread 1'
    Assert-Prompt 'Keep this draft while managing Pi.'
    Open-ResourceSettings
    Assert-ResourceText 'Not confirmed in this runtime · disabled in Pi settings'
    Invoke-Ui 'wait-for' 'PiProjectTrustText' '--value' 'This runtime: project trusted. Saved decision: trust.' '--timeout' '10000' | Out-Null
    $provider = [string](Read-Element 'PiProviderSummaryText').name
    if (-not $provider.Contains('native-local')) { throw 'Custom provider did not survive relaunch.' }
    $checks.Add('Resource settings, project trust, custom provider and unsent draft survive app relaunch')
    Invoke-Ui 'inspect' '--depth' '14' | Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Encoding utf8NoBOM
    if ($Capture) {
        Invoke-Ui 'screenshot' 'AppMainWindow' '--capture-screen' '--output' (Join-Path $runRoot 'pi-resources.png') | Out-Null
        & (Join-Path $PSScriptRoot 'Assert-ValidScreenshot.ps1') -Path (Join-Path $runRoot 'pi-resources.png') | Out-Null
    }
    Invoke-Ui 'invoke' 'OpenPiLoginButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsDialog' '--gone' '--timeout' '5000' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalWorkbenchSurface' '--timeout' '15000' | Out-Null
    $checks.Add('Guided Pi login opens an owned integrated terminal')
    $passed = $true
}
catch {
    if ($null -ne $launchedProcessId) {
        try { Invoke-Ui 'inspect' '--depth' '14' | Set-Content -LiteralPath (Join-Path $runRoot 'failure-ui-tree.json') -Encoding utf8NoBOM } catch {}
    }
    throw
}
finally {
    Stop-TestApp
    [ordered]@{ passed = $passed; checks = @($checks); visualCaptureRequested = [bool]$Capture; dataRoot = $dataRoot } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8NoBOM
    Write-Output "Pi resource native artifacts: $runRoot"
}
