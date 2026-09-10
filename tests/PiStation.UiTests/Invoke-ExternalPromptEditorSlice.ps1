[CmdletBinding()]
param([switch] $NoBuild, [switch] $Capture)

$ErrorActionPreference = 'Stop'
$solutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$appProject = Join-Path $solutionRoot 'src/PiStation.App/PiStation.App.csproj'
$fakePi = Join-Path $solutionRoot 'tests/PiStation.FakePi/bin/Debug/net10.0/PiStation.FakePi.exe'
$runRoot = Join-Path $solutionRoot ('TestResults/external-prompt-native/' + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $runRoot 'data'
$projectPath = Join-Path $runRoot 'fixture-project'
$launchedProcessId = $null
$passed = $false
$keyboardVerified = $false
$checks = [Collections.Generic.List[string]]::new()
$limitations = [Collections.Generic.List[string]]::new()
. (Join-Path $PSScriptRoot 'Select-TestThread.ps1')

function Invoke-CheckedNative {
    param([string] $FilePath, [string[]] $ArgumentList)
    $output = & $FilePath @ArgumentList 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$FilePath $($ArgumentList -join ' ') failed: $($output | Out-String)" }
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
function Wait-EditorStatus {
    param([string] $Text)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        if (([string](Read-Element 'ExternalEditorStatus').name).Contains($Text, [StringComparison]::Ordinal)) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "External editor did not report '$Text'."
}
function Assert-Draft {
    param([string] $Text)
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $actual = ([string](Read-Element 'PromptInput').value).Replace("`r`n", "`n").Replace("`r", "`n")
        if ($actual -eq $Text) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Draft mismatch: $actual"
}
function Write-EditorText {
    param([string] $Path, [string] $Text)
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($runRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The editor file escaped the test directory.'
    }
    [IO.File]::WriteAllText($resolved, $Text, [Text.UTF8Encoding]::new($false))
}
function Open-Editor { Invoke-Ui 'invoke' 'ExternalPromptEditorButton' | Out-Null; Invoke-Ui 'wait-for' 'ExternalEditorStatus' '--timeout' '10000' | Out-Null }
function Close-Editor { Invoke-Ui 'invoke' 'CloseButton' | Out-Null }

New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
$editorRoot = Join-Path $dataRoot 'external-prompts'
New-Item -ItemType Directory -Path $editorRoot -Force | Out-Null
@{ Executable = $fakePi; Arguments = '--prompt-editor-probe --unchanged' } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $editorRoot 'editor.json') -Encoding utf8NoBOM
try {
    if (-not $NoBuild) { Invoke-CheckedNative 'dotnet' @('build', (Join-Path $solutionRoot 'PiStationDesktop.slnx'), '-c', 'Debug', '-p:Platform=x64') | Write-Host }
    Start-TestApp
    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Invoke-Ui 'wait-for' 'NewThreadButton' '--property' 'IsEnabled' '--value' 'true' '--timeout' '15000' | Out-Null
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread 'Thread 1'
    Invoke-Ui 'set-value' 'PromptInput' 'Original prompt.' | Out-Null
    Invoke-Ui 'wait-for' 'DraftStatusText' '--value' 'Saved' '--timeout' '15000' | Out-Null
    try {
        Invoke-Ui 'send-keys' 'ctrl+g' '--target' 'PromptInput' '--via' 'send-input' | Out-Null
        Wait-EditorStatus 'Editor launched'
        $keyboardVerified = $true
        $checks.Add('Ctrl+G opens the external editor from the composer')
    }
    catch {
        if (-not $_.Exception.Message.Contains('no_interactive_desktop', [StringComparison]::Ordinal)) { throw }
        $limitations.Add('Ctrl+G acceptance requires an unlocked interactive desktop; this run used the UIA composer action.')
        Open-Editor
    }
    Wait-EditorStatus 'Editor launched'
    $editPath = [string](Read-Element 'ExternalEditorFilePath').name
    Write-EditorText $editPath "Edited outside PiStation.`nSecond line 😀"
    Invoke-Ui 'invoke' 'ExternalEditorApply' | Out-Null
    Wait-EditorStatus 'Saved to the composer'
    Invoke-Ui 'invoke' 'ExternalEditorDiscard' | Out-Null
    Close-Editor
    Assert-Draft "Edited outside PiStation.`nSecond line 😀"
    $checks.Add('The composer action launches the configured executable and imports Unicode text')

    Open-Editor
    Wait-EditorStatus 'Editor launched'
    $editPath = [string](Read-Element 'ExternalEditorFilePath').name
    Close-Editor
    Invoke-Ui 'set-value' 'PromptInput' 'New composer work.' | Out-Null
    Invoke-Ui 'wait-for' 'DraftStatusText' '--value' 'Saved' '--timeout' '15000' | Out-Null
    Write-EditorText $editPath 'Conflicting external text.'
    Open-Editor
    Wait-EditorStatus 'Recovered external edit'
    Invoke-Ui 'invoke' 'ExternalEditorApply' | Out-Null
    Wait-EditorStatus 'The draft changed'
    Invoke-Ui 'invoke' 'ExternalEditorDiscard' | Out-Null
    Wait-EditorStatus 'External edit discarded'
    Close-Editor
    Assert-Draft 'New composer work.'
    $checks.Add('A newer composer draft survives a conflict and explicit discard')

    Open-Editor
    Wait-EditorStatus 'Editor launched'
    $editPath = [string](Read-Element 'ExternalEditorFilePath').name
    Write-EditorText $editPath 'Recovered after restart.'
    Stop-TestApp
    Start-TestApp
    $tree = Invoke-Ui 'inspect' 'ProjectSelector' '--depth' '12' | ConvertFrom-Json -Depth 100
    $projectButton = @($tree.windows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'fixture-project' })[0]
    Invoke-Ui 'invoke' $projectButton.selector | Out-Null
    Select-TestThread 'Thread 1'
    Assert-Draft 'New composer work.'
    Open-Editor
    Wait-EditorStatus 'Recovered external edit'
    Invoke-Ui 'invoke' 'ExternalEditorApply' | Out-Null
    Wait-EditorStatus 'Saved to the composer'
    Close-Editor
    Assert-Draft 'Recovered after restart.'
    $checks.Add('Unfinished external edits survive app restart and remain scoped to their draft')

    Open-Editor
    Wait-EditorStatus 'Recovered external edit'
    Invoke-Ui 'set-value' 'ExternalEditorArguments' '--prompt-editor-probe --fail' | Out-Null
    Invoke-Ui 'invoke' 'ExternalEditorLaunch' | Out-Null
    Wait-EditorStatus 'code 23'
    Invoke-Ui 'inspect' '--depth' '20' | Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Encoding utf8NoBOM
    if ($Capture) {
        Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'external-editor.png') | Out-Null
        & (Join-Path $PSScriptRoot 'Invoke-ValidatedScreenshot.ps1') -FilePath 'winapp' -ArgumentList @(
            'ui', 'screenshot', 'AppMainWindow', '--output', (Join-Path $runRoot 'external-editor.png'), '--app', "$script:launchedProcessId", '--json') | Out-Null
    }
    Invoke-Ui 'invoke' 'ExternalEditorDiscard' | Out-Null
    Close-Editor
    Assert-Draft 'Recovered after restart.'
    $checks.Add('Failed editor exit is reported without changing the draft')
    $passed = $true
}
catch {
    if ($null -ne $launchedProcessId) {
        try { Invoke-Ui 'inspect' '--depth' '20' | Set-Content -LiteralPath (Join-Path $runRoot 'failure-ui-tree.json') -Encoding utf8NoBOM } catch {}
    }
    throw
}
finally {
    Stop-TestApp
    [ordered]@{ passed = $passed; checks = @($checks); keyboardVerified = $keyboardVerified; limitations = @($limitations);
        visualCaptureRequested = [bool]$Capture; dataRoot = $dataRoot } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8NoBOM
    Write-Output "External prompt editor artifacts: $runRoot"
}
