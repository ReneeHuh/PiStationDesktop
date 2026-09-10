[CmdletBinding()]
param([switch] $NoBuild, [switch] $Capture)

$ErrorActionPreference = 'Stop'
$solutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$appProject = Join-Path $solutionRoot 'src/PiStation.App/PiStation.App.csproj'
$fakePi = Join-Path $solutionRoot 'tests/PiStation.FakePi/bin/Debug/net10.0/PiStation.FakePi.exe'
$runRoot = Join-Path $solutionRoot ('TestResults/pi-integration-native/' + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $runRoot 'data'
$projectPath = Join-Path $runRoot 'fixture-project'
$extensionPath = Join-Path $runRoot 'trusted-extension.ts'
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
        '--fake-pi-scenario', 'extension-ui', '--log-file', (Join-Path $runRoot 'app.jsonl')) | ConvertFrom-Json
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
function Open-RuntimeSettings {
    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsRuntimeNavItem' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'SettingsRuntimeNavItem' | Out-Null
    Invoke-Ui 'wait-for' 'PiExtensionPathsInput' '--timeout' '5000' | Out-Null
}
function Close-Settings {
    Invoke-Ui 'invoke' 'CloseButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsDialog' '--gone' '--timeout' '5000' | Out-Null
}

New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
Set-Content -LiteralPath $extensionPath -Value 'export default function(pi) {}' -Encoding utf8NoBOM
try {
    if (-not $NoBuild) {
        Invoke-CheckedNative 'dotnet' @('build', (Join-Path $solutionRoot 'PiStationDesktop.slnx'), '-c', 'Debug', '-p:Platform=x64') | Write-Host
    }
    Start-TestApp
    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Invoke-Ui 'wait-for' 'fixture-project' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread 'Thread 1'
    Invoke-Ui 'wait-for' 'PiExtensionPanel' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'PiExtensionBelowWidget' '--value' 'Extension footer' '--timeout' '15000' | Out-Null
    $extensionTree = Invoke-Ui 'inspect' 'PiExtensionPanel' '--depth' '8'
    foreach ($text in @('Review assistant', 'Extension connected', 'Ready to review', 'Review changes', 'Run focused checks')) {
        if (-not $extensionTree.Contains($text)) { throw "Extension panel is missing: $text" }
    }
    Invoke-Ui 'set-value' 'PromptInput' 'Keep my existing draft.' | Out-Null
    Invoke-Ui 'invoke' 'InsertExtensionTextButton' | Out-Null
    $firstDraft = "Keep my existing draft.`nReview this project using " + '$skill:fake-skill.'
    Assert-Prompt $firstDraft
    Wait-Draft
    $checks.Add('Extension UI and explicit insertion preserve existing draft')
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread 'Thread 2'
    Assert-Prompt ''
    Invoke-Ui 'wait-for' 'InsertExtensionTextButton' '--timeout' '10000' | Out-Null
    Invoke-Ui 'set-value' 'PromptInput' 'Second thread draft.' | Out-Null
    Wait-Draft
    Select-TestThread 'Thread 1'
    Assert-Prompt $firstDraft
    Invoke-Ui 'wait-for' 'InsertExtensionTextButton' '--gone' '--timeout' '5000' | Out-Null
    $checks.Add('Thread switching preserves separate drafts without automatic insertion')
    Open-RuntimeSettings
    Invoke-Ui 'set-value' 'PiExtensionPathsInput' $extensionPath | Out-Null
    Invoke-Ui 'invoke' 'DiscoverPiExtensionsToggle' | Out-Null
    Invoke-Ui 'invoke' 'ConfigurePiRuntimeButton' | Out-Null
    $settingsPath = Join-Path $dataRoot 'pi-runtime.json'
    $saved = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        if (Test-Path -LiteralPath $settingsPath) {
            $saved = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            if ($saved.extensions.discoverInstalled -and $saved.extensions.paths -contains $extensionPath) { break }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $saved.extensions.discoverInstalled -or $saved.extensions.paths -notcontains $extensionPath) { throw 'Runtime settings were not saved from the current UI values.' }
    Invoke-Ui 'wait-for' 'RestartConfiguredPiButton' '--property' 'IsEnabled' '--value' 'True' '--timeout' '15000' | Out-Null
    Invoke-Ui 'invoke' 'RestartConfiguredPiButton' | Out-Null
    Close-Settings
    Invoke-Ui 'wait-for' 'PiExtensionPanel' '--timeout' '15000' | Out-Null
    Assert-Prompt $firstDraft
    $checks.Add('Runtime settings save and idle restart preserve the draft')
    Stop-TestApp
    Start-TestApp
    Invoke-Ui 'wait-for' 'fixture-project' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'fixture-project' | Out-Null
    Wait-TestThread 'Thread 1'
    Select-TestThread 'Thread 1'
    Assert-Prompt $firstDraft
    Select-TestThread 'Thread 2'
    Assert-Prompt 'Second thread draft.'
    Open-RuntimeSettings
    Invoke-Ui 'wait-for' 'PiExtensionPathsInput' '--property' 'Value' '--value' $extensionPath '--timeout' '10000' | Out-Null
    $checks.Add('Settings and both thread drafts survive process relaunch')
    Invoke-Ui 'inspect' '--depth' '14' | Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Encoding utf8NoBOM
    if ($Capture) {
        Invoke-Ui 'screenshot' 'AppMainWindow' '--capture-screen' '--output' (Join-Path $runRoot 'runtime-settings.png') | Out-Null
        & (Join-Path $PSScriptRoot 'Assert-ValidScreenshot.ps1') -Path (Join-Path $runRoot 'runtime-settings.png') | Out-Null
    }
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
    Write-Output "Pi integration native artifacts: $runRoot"
}
