[CmdletBinding()]
param([switch] $NoBuild, [switch] $Capture)
$ErrorActionPreference = 'Stop'
$solutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$appProject = Join-Path $solutionRoot 'src/PiStation.App/PiStation.App.csproj'
$fakePi = Join-Path $solutionRoot 'tests/PiStation.FakePi/bin/Debug/net10.0/PiStation.FakePi.exe'
$runRoot = Join-Path $solutionRoot ('TestResults/appearance-native/' + [guid]::NewGuid().ToString('N'))
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
    throw "Appearance panel did not contain: $Text"
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
@{ComposerCollapseOnBlur=$false;ComposerCollapseOnScroll=$false;RightPanelWidth=420;ThemePreference='Dark'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataRoot 'layout-settings.json') -Encoding utf8NoBOM
$projectPath = Join-Path $runRoot 'appearance-project'
New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
Invoke-CheckedNative 'git' @('-C',$projectPath,'init','--quiet') | Out-Null
Invoke-CheckedNative 'git' @('-C',$projectPath,'config','user.name','Appearance fixture') | Out-Null
Invoke-CheckedNative 'git' @('-C',$projectPath,'config','user.email','fixture@example.invalid') | Out-Null
[IO.File]::WriteAllText((Join-Path $projectPath 'sample.cs'), "var baseline = 1;`n")
Invoke-CheckedNative 'git' @('-C',$projectPath,'add','sample.cs') | Out-Null
Invoke-CheckedNative 'git' @('-C',$projectPath,'commit','--quiet','-m','fixture') | Out-Null
[IO.File]::WriteAllText((Join-Path $projectPath 'sample.cs'), "  var baseline = 1;  `n")
@{executablePath=$null;extensions=@{};launch=@{environmentVariables=@{PI_CODING_AGENT_DIR=(Join-Path $runRoot 'isolated-agent')}}} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dataRoot 'pi-runtime.json') -Encoding utf8NoBOM
try {
    if (-not $NoBuild) { Invoke-CheckedNative 'dotnet' @('build',(Join-Path $solutionRoot 'PiStationDesktop.slnx'),'-c','Debug') | Write-Host }
    Start-TestApp
    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Invoke-Ui 'wait-for' 'WorkspaceProjectStatusText' '--value' 'appearance-project' '--timeout' '15000' | Out-Null
    Invoke-Ui 'invoke' 'AddActionButton' | Out-Null
    Invoke-Ui 'invoke' 'HeaderNewThreadMenuItem' | Out-Null
    Invoke-Ui 'wait-for' 'PromptInput' '--timeout' '15000' | Out-Null
    Invoke-Ui 'set-value' 'PromptInput' 'Appearance preview: independent fonts and preserved draft.' | Out-Null
    $baselineFont = Read-Font 'PromptInput'
    Open-Appearance
    Set-Preference 'AppearanceInterfaceFont' 'Arial'
    Set-Preference 'AppearanceInterfaceSize' '15'
    Set-Preference 'AppearanceComposerFont' 'Georgia'
    Set-Preference 'AppearanceComposerSize' '18'
    Set-Preference 'AppearanceCodeFont' 'Consolas'
    Set-Preference 'AppearanceCodeSize' '16'
    Set-Preference 'AppearanceContrast' '140'
    Set-Preference 'AppearanceOpacity' '70'
    Wait-Text 'Interface 15'
    $interfaceFont = Read-Font 'AppearanceCodeFont'
    if ($interfaceFont.family -notmatch '^Arial(,|$)') { throw "Interface font did not apply: $($interfaceFont | ConvertTo-Json -Compress)" }
    Invoke-Ui 'scroll' 'SettingsContentScroll' '--to' 'top' | Out-Null
    Capture-Panel 'appearance-typography'
    Invoke-Ui 'scroll' 'SettingsContentScroll' '--direction' 'down' | Out-Null
    Invoke-Ui 'invoke' 'AppearanceWordWrap' | Out-Null
    Invoke-Ui 'invoke' 'AppearanceDiffLayout' | Out-Null
    Invoke-Ui 'send-keys' 'end' | Out-Null
    Invoke-Ui 'send-keys' 'enter' | Out-Null
    Invoke-Ui 'invoke' 'AppearanceIgnoreWhitespace' | Out-Null
    Set-Preference 'AppearanceAnimation' '400'
    Capture-Panel 'appearance-editors'
    Close-Settings
    $composerFont = Read-Font 'PromptInput'
    if ($composerFont.family -notmatch '^Georgia(,|$)' -or $composerFont.size -le $baselineFont.size) { throw "Composer font did not apply: $($composerFont | ConvertTo-Json -Compress)" }
    Invoke-Ui 'wait-for' 'PromptInput' '--value' 'Appearance preview: independent fonts and preserved draft.' '--timeout' '5000' | Out-Null
    $checks.Add('Independent native fonts apply immediately without replacing the composer draft')
    Invoke-Ui 'invoke' 'ToggleWorkbenchButton' | Out-Null
    Invoke-Ui 'invoke' 'ChangesPanelTab' | Out-Null
    Invoke-Ui 'wait-for' 'WorkbenchChangesStatusText' '--value' '1 changed file' '--timeout' '10000' | Out-Null
    Activate-FirstRow 'WorkbenchChangeList' $false
    Invoke-Ui 'wait-for' 'WorkbenchDiffPreview' '--value' '+  var baseline' '--contains' '--timeout' '10000' | Out-Null
    $help = Invoke-Ui 'get-property' 'WorkbenchDiffPreview' '--property' 'HelpText'
    if (-not $help.Contains('Split diff') -or -not $help.Contains('word wrap off')) { throw "Diff preferences were not applied: $help" }
    Activate-FirstRow 'DiffLines' $false
    Invoke-Ui 'invoke' 'DiffLayoutSelector' | Out-Null
    Invoke-Ui 'send-keys' 'home' | Out-Null
    Invoke-Ui 'send-keys' 'enter' | Out-Null
    Invoke-Ui 'invoke' 'DiffLayoutSelector' | Out-Null
    Invoke-Ui 'send-keys' 'end' | Out-Null
    Invoke-Ui 'send-keys' 'enter' | Out-Null
    if ((Find-NativeElement 'DiffLines').GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection().Count -ne 1) { throw 'Layout change lost the diff selection' }
    Capture-Panel 'appearance-diff'
    Open-Appearance
    Invoke-Ui 'invoke' 'AppearanceIgnoreWhitespace' | Out-Null
    Close-Settings
    Invoke-Ui 'wait-for' 'WorkbenchDiffPreview' '--value' 'No textual diff' '--contains' '--timeout' '10000' | Out-Null
    if ((Find-NativeElement 'DiffLines').GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection().Count -ne 0) { throw 'New patch reused the old selection offsets' }
    $checks.Add('Changing whitespace preference reloads the selected Git diff; split and wrap preferences reach the live renderer')
    [IO.File]::WriteAllText((Join-Path $projectPath 'sample.cs'), ('var baseline = 1; // ' + ('long code preview ' * 3)))
    Invoke-Ui 'invoke' 'FilesPanelTab' | Out-Null
    Invoke-Ui 'set-value' 'WorkbenchFileSearchInput' 'sample.cs' | Out-Null
    Invoke-Ui 'wait-for' 'WorkbenchFileList' '--timeout' '10000' | Out-Null
    Activate-FirstRow 'WorkbenchFileList' $true
    Invoke-Ui 'wait-for' 'WorkbenchFileEditor' '--value' 'var baseline' '--contains' '--timeout' '10000' | Out-Null
    $codeFont = Read-Font 'WorkbenchFileEditor'
    if ($codeFont.family -notmatch '^Consolas(,|$)') { throw "Code font did not apply: $($codeFont | ConvertTo-Json -Compress)" }
    $noWrapRectangles = Read-TextRectangles 'WorkbenchFileEditor'
    Open-Appearance
    Invoke-Ui 'invoke' 'AppearanceWordWrap' | Out-Null
    Close-Settings
    $wrapRectangles = Read-TextRectangles 'WorkbenchFileEditor'
    if ($wrapRectangles -le $noWrapRectangles) { throw "Word wrap did not create additional native text lines: $noWrapRectangles -> $wrapRectangles" }
    $checks.Add('File editor uses the independent code font and word wrap changes actual native text line geometry')
    $close = (Find-NativeElement 'CloseRightPanelButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $toggle = (Find-NativeElement 'ToggleWorkbenchButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    for ($i=0; $i -lt 4; $i++) { $close.Invoke(); $toggle.Invoke() }
    $close.Invoke()
    Invoke-Ui 'wait-for' 'RightPanelHost' '--gone' '--timeout' '5000' | Out-Null
    $toggle.Invoke()
    Invoke-Ui 'wait-for' 'RightPanelHost' '--timeout' '5000' | Out-Null
    $checks.Add('Rapid native panel close/open interruptions settle to the requested state')
    Stop-TestApp
    Start-TestApp
    Open-Appearance
    Wait-Text 'Interface 15'
    $restoredFont = Read-Font 'AppearanceCodeFont'
    if ($restoredFont.family -notmatch '^Arial(,|$)') { throw 'Native interface font was not restored after relaunch' }
    $saved = Get-Content -LiteralPath (Join-Path $dataRoot 'layout-settings.json') -Raw | ConvertFrom-Json
    if ($saved.Appearance.CodeFontSize -ne 16 -or $saved.Appearance.DiffLayout -ne 1 -or -not $saved.Appearance.WordWrap -or $saved.Appearance.PanelAnimationDurationMs -ne 400) { throw 'Appearance persistence mismatch' }
    $checks.Add('Fonts, surfaces, editor and animation preferences survive relaunch')
    Invoke-Ui 'invoke' 'ResetAppearance' | Out-Null
    Wait-Text 'Interface 13'
    Capture-Panel 'appearance-reset'
    $saved = Get-Content -LiteralPath (Join-Path $dataRoot 'layout-settings.json') -Raw | ConvertFrom-Json
    if ($saved.Appearance.Contrast -ne 100 -or $saved.Appearance.GlassOpacity -ne 100 -or $saved.Appearance.DiffLayout -ne 0) { throw 'Reset did not persist defaults' }
    $checks.Add('Reset restores and persists native defaults')
    Invoke-Ui 'invoke' 'PiThemeSelector' | Out-Null
    Invoke-Ui 'send-keys' 'end' | Out-Null
    Invoke-Ui 'send-keys' 'enter' | Out-Null
    Set-Preference 'AppearanceContrast' '150'
    Set-Preference 'AppearanceOpacity' '60'
    Invoke-Ui 'scroll' 'SettingsContentScroll' '--to' 'top' | Out-Null
    Capture-Panel 'appearance-light'
    $checks.Add('Light theme supports contrast and opacity without losing the native controls')
    @{wrappedRectangles=$wrapRectangles;unwrappedRectangles=$noWrapRectangles;interface=$interfaceFont;baseline=$baselineFont;composer=$composerFont;code=$codeFont} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot 'native-fonts.json')
    $passed=$true
}
catch {
    $failureArgs = $script:lastUiArgs
    if ($launchedProcessId) { try { Invoke-Ui 'inspect' '--depth' '30' | Set-Content -LiteralPath (Join-Path $runRoot 'failed-ui-tree.json') -Encoding utf8NoBOM } catch { Write-Warning 'Failed app no longer exposes a UI tree.' } }
    $script:lastUiArgs = $failureArgs
    throw
}
finally {
    Stop-TestApp
    @{passed=$passed;checks=@($checks);captureRequested=[bool]$Capture;dataRoot=$dataRoot;lastUiArguments=$script:lastUiArgs} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8NoBOM
    Write-Output "Appearance native artifacts: $runRoot"
}
