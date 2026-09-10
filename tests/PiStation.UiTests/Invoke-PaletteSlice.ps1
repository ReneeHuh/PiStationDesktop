[CmdletBinding()]
param([switch] $NoBuild, [switch] $Capture)
$ErrorActionPreference = 'Stop'
$solutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$appProject = Join-Path $solutionRoot 'src/PiStation.App/PiStation.App.csproj'
$fakePi = Join-Path $solutionRoot 'tests/PiStation.FakePi/bin/Debug/net10.0/PiStation.FakePi.exe'
$runRoot = Join-Path $solutionRoot ('TestResults/palette-native/' + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $runRoot 'data'
$launchedProcessId = $null
$fixtureProcess = $null
$checks = [Collections.Generic.List[string]]::new()
$passed = $false
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
function Invoke-CheckedNative {
    param([string] $FilePath, [string[]] $ArgumentList)
    $output = & $FilePath @ArgumentList 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$FilePath $($ArgumentList -join ' ') failed: $($output | Out-String)" }
    return ($output | Out-String).Trim()
}
function Invoke-Ui {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)
    $script:lastUiArgs = $Arguments -join ' '
    Write-Host $script:lastUiArgs
    Invoke-CheckedNative 'winapp' (@('ui') + $Arguments + @('--app', "$script:launchedProcessId", '--json'))
}
function Start-TestApp {
    $launch = Invoke-CheckedNative 'winapp' @('run', $appProject, '--configuration', 'Debug', '--arch', 'x64', '--property', 'Platform=x64', '--no-build', '--no-restore', '--detach', '--json',
        '--', '--ui-test', '--data-root', $dataRoot, '--pi-executable', $fakePi, '--fake-pi-scenario', 'normal', '--log-file', (Join-Path $runRoot 'app.jsonl')) | ConvertFrom-Json
    $script:launchedProcessId = [int]$launch.ProcessId
    Invoke-Ui 'wait-for' 'ConnectionStatusText' '--value' 'Local • Ready' '--timeout' '15000' | Out-Null

}
function Stop-TestApp {
    if ($null -eq $script:launchedProcessId) { return }
    $owned = Get-Process -Id $script:launchedProcessId -ErrorAction SilentlyContinue
    if ($owned -and $owned.ProcessName -eq 'PiStationDesktop') { Stop-Process -Id $script:launchedProcessId; Wait-Process -Id $script:launchedProcessId -Timeout 10 -ErrorAction SilentlyContinue }
    $script:launchedProcessId = $null
}
function Wait-Text {
    param([string] $Text)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $tree = Invoke-Ui 'inspect' '--depth' '30'
        if ($tree.Contains($Text)) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Palette panel did not contain: $Text"
}
function Capture-Panel {
    param([string] $Name)
    Invoke-Ui 'inspect' '--depth' '30' | Set-Content -LiteralPath (Join-Path $runRoot "$Name.json") -Encoding utf8NoBOM
    if ($Capture) { & (Join-Path $PSScriptRoot 'Invoke-ValidatedScreenshot.ps1') -FilePath 'winapp' -ArgumentList @('ui','screenshot','--app',"$launchedProcessId",'--output',(Join-Path $runRoot "$Name.png"),'--json') | Out-Null }
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
function Find-NativeElement([string] $Id) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
    do {
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$launchedProcessId))
        foreach ($window in $windows) {
            $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id))
            if ($element) { return $element }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Missing native element: $Id"
}
function Activate-FirstRow([string] $Id, [bool] $Invoke) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        $row = (Find-NativeElement $Id).FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem))
        if ($row) {
            if ($Invoke) { $row.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
            else { $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
            return
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "No row in $Id"
}
function Read-Font([string] $Id) {
    $pattern = (Find-NativeElement $Id).GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    $range = $pattern.DocumentRange
    return @{family=$range.GetAttributeValue([System.Windows.Automation.TextPattern]::FontNameAttribute);size=$range.GetAttributeValue([System.Windows.Automation.TextPattern]::FontSizeAttribute)}
}
function Read-TextRectangles([string] $Id) {
    # Closing settings invalidates the editor's layout. Reacquire the range until
    # the native provider has completed its next layout pass.
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
    do {
        try {
            $range = (Find-NativeElement $Id).GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange
            $count = $range.GetBoundingRectangles().Count
            if ($count -gt 0) { return $count }
        }
        catch {
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Native text geometry did not become available: $Id"
}
function Open-Appearance {
    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'invoke' 'SettingsAppearanceNavItem' | Out-Null
    Invoke-Ui 'scroll' 'SettingsContentScroll' '--to' 'top' | Out-Null
}
function Close-Settings {
    Invoke-Ui 'invoke' 'Done' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsShell' '--gone' '--timeout' '5000' | Out-Null
}
function Set-Preference([string] $Id, [string] $Value) {
    Invoke-Ui 'set-value' $Id $Value | Out-Null
    Invoke-Ui 'send-keys' 'tab' '--target' $Id | Out-Null
}
function Select-Choice([string] $Id, [string] $Choice) {
    Invoke-Ui 'invoke' $Id | Out-Null
    $combo = Find-NativeElement $Id
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Choice),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
    $item = $combo.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (-not $item) { throw "Missing option $Choice in $Id" }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
}
@{ComposerCollapseOnBlur=$false;ComposerCollapseOnScroll=$false;ThemePreference='Dark'} | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $dataRoot 'layout-settings.json') -Encoding utf8NoBOM
@{executablePath=$null;extensions=@{};launch=@{environmentVariables=@{PI_CODING_AGENT_DIR=(Join-Path $runRoot 'isolated-agent')}}} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dataRoot 'pi-runtime.json') -Encoding utf8NoBOM
$projectPath = Join-Path $runRoot 'palette-project'
New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $projectPath 'sample.cs'), 'var color = "palette preview";')
$libraryPath = Join-Path $dataRoot 'theme-library.json'
try {
    if (-not $NoBuild) { Invoke-CheckedNative 'dotnet' @('build',(Join-Path $solutionRoot 'PiStationDesktop.slnx'),'-c','Debug') | Write-Host }
    Start-TestApp
    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Invoke-Ui 'wait-for' 'WorkspaceProjectStatusText' '--value' 'palette-project' '--timeout' '15000' | Out-Null
    Open-Appearance
    Invoke-Ui 'invoke' 'PaletteNew' | Out-Null
    Invoke-Ui 'set-value' 'PaletteName' 'Native lagoon' | Out-Null
    Select-Choice 'PaletteRole' 'codeForeground'
    Invoke-Ui 'set-value' 'PaletteColor' '#25a7d9' | Out-Null
    Invoke-Ui 'invoke' 'PaletteApplyColor' | Out-Null
    Invoke-Ui 'wait-for' 'PaletteStatus' '--value' 'Preview updated: codeForeground' '--contains' '--timeout' '5000' | Out-Null
    if (Test-Path -LiteralPath $libraryPath) { throw 'Unsaved preview wrote the library' }
    Close-Settings
    Invoke-Ui 'wait-for' 'ThemePreviewNotice' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'ToggleWorkbenchButton' | Out-Null
    Invoke-Ui 'invoke' 'FilesPanelTab' | Out-Null
    Invoke-Ui 'set-value' 'WorkbenchFileSearchInput' 'sample.cs' | Out-Null
    Activate-FirstRow 'WorkbenchFileList' $true
    Invoke-Ui 'wait-for' 'WorkbenchFileEditor' '--value' 'var color' '--contains' '--timeout' '10000' | Out-Null
    $foreground = (Find-NativeElement 'WorkbenchFileEditor').GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetAttributeValue([System.Windows.Automation.TextPattern]::ForegroundColorAttribute)
    if ([int]$foreground -ne 0xD9A725) { throw "Native code color did not apply: $foreground" }
    $checks.Add('Unsaved preview changes actual native editor text color without persisting the library')
    Open-Appearance
    Invoke-Ui 'invoke' 'PaletteSave' | Out-Null
    Invoke-Ui 'wait-for' 'PaletteStatus' '--value' 'Saved and applied Native lagoon.' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'PaletteEdit' | Out-Null
    Invoke-Ui 'set-value' 'PaletteColor' 'var(--invalid)' | Out-Null
    Invoke-Ui 'invoke' 'PaletteApplyColor' | Out-Null
    Invoke-Ui 'wait-for' 'PaletteStatus' '--value' 'Invalid literal color' '--contains' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'PaletteCancel' | Out-Null
    $saved = Get-Content -LiteralPath $libraryPath -Raw | ConvertFrom-Json
    if ($saved.themes[0].colors.codeForeground -ne '#25a7d9') { throw 'Invalid edit changed the saved palette' }
    $checks.Add('Save commits a palette; invalid edits and cancellation preserve saved colors')
    Invoke-Ui 'invoke' 'PaletteInspect' | Out-Null
    Invoke-Ui 'wait-for' 'ThemeInspectorPaint' '--timeout' '5000' | Out-Null
    Select-Choice 'ThemeInspectorArea' 'Sidebar'
    Invoke-Ui 'invoke' 'ThemeInspectSelectedArea' | Out-Null
    Invoke-Ui 'wait-for' 'PaletteStatus' '--value' 'Inspected sidebar' '--contains' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'PaletteCancel' | Out-Null
    $checks.Add('Keyboard area inspection returns to the editor with the mapped palette role')
    $themeJson = '{"version":1,"id":"native-dual","name":"Native dual","appearance":"dark","colors":{"canvas":"oklch(0.2 0.02 250)","terminalBackground":"#112233"},"variants":{"light":{"canvas":"#fafafa","text":"#112233"}},"collection":{"id":"test.family","label":"Test family"}}'
    Invoke-Ui 'set-value' 'PaletteJson' $themeJson | Out-Null
    Invoke-Ui 'invoke' 'PaletteImport' | Out-Null
    Invoke-Ui 'invoke' 'PaletteSave' | Out-Null
    Invoke-Ui 'invoke' 'PaletteExport' | Out-Null
    $exported = (Find-NativeElement 'PaletteJson').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value | ConvertFrom-Json
    if ($exported.variants.light.canvas -ne '#fafafa' -or $exported.collection.id -ne 'test.family') { throw 'Theme export lost variants or collection metadata' }
    $checks.Add('T3 import/export preserves dual appearances and collection metadata')
    Select-Choice 'PaletteImportFormat' 'VS Code theme'
    Invoke-Ui 'set-value' 'PaletteJson' '{"name":"Imported slate","colors":{"editor.background":"#121212","sideBar.background":"#222222","focusBorder":"#6699ff","terminal.selectionBackground":"#ffffff80"}}' | Out-Null
    Invoke-Ui 'invoke' 'PaletteImport' | Out-Null
    Invoke-Ui 'invoke' 'PaletteSave' | Out-Null
    Invoke-Ui 'wait-for' 'PaletteStatus' '--value' 'Saved and applied Imported slate.' '--timeout' '5000' | Out-Null
    Select-Choice 'PaletteImportFormat' 'Pi terminal theme'
    Invoke-Ui 'set-value' 'PaletteJson' '{"name":"Pi blue","vars":{"brand":33},"colors":{"accent":"brand","text":"","userMessageBg":"#182838"}}' | Out-Null
    Invoke-Ui 'invoke' 'PaletteImport' | Out-Null
    Invoke-Ui 'invoke' 'PaletteSave' | Out-Null
    $saved = Get-Content -LiteralPath $libraryPath -Raw | ConvertFrom-Json
    if (($saved.themes | Where-Object name -eq 'Pi blue').colors.accent -ne '#0087ff') { throw 'Pi ANSI variable did not convert' }
    $checks.Add('VS Code and Pi imports convert and save through the native editor')
    Capture-Panel 'palette-editor'
    Stop-TestApp
    Start-TestApp
    Open-Appearance
    Wait-Text 'Saved theme: Pi blue'
    Invoke-Ui 'invoke' 'PaletteDefaults' | Out-Null
    $saved = Get-Content -LiteralPath $libraryPath -Raw | ConvertFrom-Json
    if ($null -ne $saved.activeId -or $saved.themes.Count -ne 4) { throw 'Native defaults discarded themes or retained the custom selection' }
    $checks.Add('Relaunch restores the selected theme; native reset retains the library')
    Stop-TestApp
    [IO.File]::WriteAllText($libraryPath, 'broken fixture library')
    Start-TestApp
    Open-Appearance
    Invoke-Ui 'wait-for' 'PaletteStatus' '--value' 'Theme library could not be loaded' '--contains' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'PaletteRecover' | Out-Null
    Invoke-Ui 'wait-for' 'PaletteStatus' '--value' 'Restored the previous theme library.' '--timeout' '5000' | Out-Null
    $saved = Get-Content -LiteralPath $libraryPath -Raw | ConvertFrom-Json
    if ($saved.themes.Count -ne 4) { throw 'Native recovery lost saved themes' }
    $checks.Add('A damaged local library opens safely and restores the previous library through the native recovery control')
    @{codeForeground=$foreground;expectedCodeForeground=0xD9A725;themeCount=$saved.themes.Count} | ConvertTo-Json | Set-Content (Join-Path $runRoot 'native-palette.json')
    $passed = $true
}
catch {
    if ($launchedProcessId) { try { Invoke-Ui 'inspect' '--depth' '30' | Set-Content -LiteralPath (Join-Path $runRoot 'failed-ui-tree.json') -Encoding utf8NoBOM } catch {} }
    throw
}
finally {
    Stop-TestApp
    @{passed=$passed;checks=@($checks);captureRequested=[bool]$Capture;dataRoot=$dataRoot;lastUiArguments=$script:lastUiArgs} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8NoBOM
    Write-Output "Palette native artifacts: $runRoot"
}
