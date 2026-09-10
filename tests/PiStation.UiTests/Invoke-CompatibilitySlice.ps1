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
$runRoot = Join-Path $artifactRoot (Join-Path 'compatibility-runs' (
    "{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N')))
$dataRoot = Join-Path $runRoot 'data'
$projectPath = Join-Path $dataRoot 'compatibility-project'
$logFile = Join-Path $dataRoot 'app.jsonl'
$layoutSettingsPath = Join-Path $dataRoot 'layout-settings.json'
$launchedProcessId = $null
$testError = $null
. (Join-Path $PSScriptRoot 'Select-TestThread.ps1')
$displayScale = 1.0

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class PiStationCompatibilityWindow
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    public static bool ResizeLogicalPixels(IntPtr window, int width, int height)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previous == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            var scale = GetDpiForWindow(window) / 96.0;
            return SetWindowPos(window, IntPtr.Zero, 0, 0,
                (int)Math.Round(width * scale), (int)Math.Round(height * scale), 0x0042);
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    public static bool ResizePhysical(IntPtr hWnd, int width, int height)
    {
        // The caller already converts logical dimensions to physical pixels.
        // Prevent PowerShell's DPI virtualization from applying that scale twice.
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previous == IntPtr.Zero)
            throw new InvalidOperationException("Could not establish a DPI-aware resize context.");
        try
        {
            return SetWindowPos(hWnd, IntPtr.Zero, 0, 0, width, height, 0x0042);
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }
}
'@

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
    param([Parameter(Mandatory)][ValidateSet(100, 150, 200)][int] $TextScalePercent)

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
        '--ui-test-text-scale', "$TextScalePercent",
        '--log-file', $logFile
    )
    $launch = $launchJson | ConvertFrom-Json
    $script:launchedProcessId = [int]$launch.ProcessId
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $process = Get-Process -Id $script:launchedProcessId -ErrorAction Stop
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            break
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'The packaged app did not expose a main window handle.'
    }

    $dpi = [PiStationCompatibilityWindow]::GetDpiForWindow($process.MainWindowHandle)
    $script:displayScale = if ($dpi -gt 0) { $dpi / 96.0 } else { 1.0 }
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
        [int] $Timeout = 10000,
        [string] $Property
    )

    $arguments = @('wait-for', $Selector, '--timeout', "$Timeout", '--value', $Value)
    if (-not [string]::IsNullOrWhiteSpace($Property)) {
        $arguments += @('--property', $Property)
    }

    Invoke-Ui @arguments | Out-Null
}

function Get-UiProperty {
    param(
        [Parameter(Mandatory)][string] $Selector,
        [Parameter(Mandatory)][string] $Property
    )

    $result = Invoke-Ui 'get-property' $Selector '--property' $Property | ConvertFrom-Json
    return [string]$result.properties.$Property
}

function Get-UiBounds {
    param([Parameter(Mandatory)][string] $Selector)

    $parts = @((Get-UiProperty -Selector $Selector -Property 'BoundingRectangle') -split ',')
    if ($parts.Count -ne 4) {
        throw "Could not read bounds for '$Selector'."
    }

    return [pscustomobject]@{
        X = [double]$parts[0]
        Y = [double]$parts[1]
        Width = [double]$parts[2]
        Height = [double]$parts[3]
    }
}

function Set-TestWindowSize {
    param(
        [Parameter(Mandatory)][int] $Width,
        [Parameter(Mandatory)][int] $Height
    )

    $process = Get-Process -Id $script:launchedProcessId -ErrorAction Stop
    if (-not [PiStationCompatibilityWindow]::ResizeLogicalPixels(
        $process.MainWindowHandle,
        $Width,
        $Height)) {
        throw "Could not resize the packaged app window to ${Width}x${Height} logical pixels."
    }
}

function Assert-ResponsiveGeometry {
    param(
        [Parameter(Mandatory)][string] $LayoutName,
        [double] $VisibleSidebarWidth = 0
    )

    Wait-UiValue -Selector 'WorkspaceNavigation' -Value "Responsive layout: $LayoutName" `
        -Property 'HelpText'
    foreach ($selector in @('AppSidebar', 'TranscriptList', 'ComposerSurface', 'WorkspaceStatusBar')) {
        Invoke-Ui 'wait-for' $selector '--timeout' '5000' | Out-Null
    }

    $window = Get-UiBounds -Selector 'AppMainWindow'
    $sidebar = Get-UiBounds -Selector 'AppSidebar'
    if ($VisibleSidebarWidth -gt 0) {
        $sidebar.Width = $VisibleSidebarWidth
    }
    $transcript = Get-UiBounds -Selector 'TranscriptList'
    $composer = Get-UiBounds -Selector 'ComposerSurface'
    $prompt = Get-UiBounds -Selector 'PromptInput'
    # The synthetic group peer includes off-viewport toolbar content. The real
    # TextBox bounds and symmetric surface padding describe the visible card.
    $composer.Width = $prompt.Width + (2 * ($prompt.X - $composer.X))
    $status = Get-UiBounds -Selector 'WorkspaceStatusBar'
    $tolerance = 3 * $script:displayScale
    if ($composer.X -lt ($sidebar.X + $sidebar.Width - $tolerance)) {
        throw "$LayoutName layout allowed the composer to overlap the sidebar."
    }
    if (($composer.X + $composer.Width) -gt ($window.X + $window.Width + $tolerance)) {
        throw "$LayoutName layout placed the composer outside the app window."
    }
    if ($composer.Width -gt ((720 * $script:displayScale) + $tolerance)) {
        throw "$LayoutName layout exceeded the 720px reading-column maximum."
    }
    if ([Math]::Abs($composer.X - $status.X) -gt $tolerance -or
        [Math]::Abs($composer.Width - $status.Width) -gt $tolerance) {
        throw "$LayoutName layout did not align the composer and workspace status rail."
    }
    foreach ($selector in @('SendPromptButton', 'StopTurnButton')) {
        $action = Get-UiBounds -Selector $selector
        if ($action.Width -le 0 -or $action.X -lt $status.X -or
            ($action.X + $action.Width) -gt ($status.X + $status.Width + $tolerance)) {
            throw "$LayoutName layout clipped $selector."
        }
    }
    if ($transcript.Width -gt ((720 * $script:displayScale) + $tolerance)) {
        throw "$LayoutName layout exceeded the transcript reading-column maximum."
    }
}

function Wait-ForComposerGrowth {
    param(
        [Parameter(Mandatory)][double] $InitialHeight,
        [int] $Timeout = 5000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($Timeout)
    do {
        $height = (Get-UiBounds -Selector 'ComposerSurface').Height
        if ($height -gt ($InitialHeight + (8 * $script:displayScale))) {
            return $height
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'The composer did not grow for multiline content.'
}

function Find-UiElementByName {
    param(
        [Parameter(Mandatory)] $Value,
        [Parameter(Mandatory)][string] $Name
    )

    if ($Value -is [pscustomobject]) {
        if ($Value.name -eq $Name -and $Value.isInvokable -eq $true -and
            -not [string]::IsNullOrWhiteSpace($Value.selector)) {
            return $Value
        }

        foreach ($property in $Value.PSObject.Properties) {
            if ($null -ne $property.Value) {
                $found = Find-UiElementByName -Value $property.Value -Name $Name
                if ($null -ne $found) {
                    return $found
                }
            }
        }
    }
    elseif ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        foreach ($item in $Value) {
            if ($null -ne $item) {
                $found = Find-UiElementByName -Value $item -Name $Name
                if ($null -ne $found) {
                    return $found
                }
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

function Assert-PresentationProfile {
    param(
        [Parameter(Mandatory)][string] $Theme,
        [Parameter(Mandatory)][int] $TextScalePercent
    )

    $helpText = Get-UiProperty -Selector 'AppMainWindow' -Property 'HelpText'
    $expected = "Presentation: $Theme theme • $TextScalePercent% text profile •"
    if (-not $helpText.StartsWith($expected, [System.StringComparison]::Ordinal)) {
        throw "Expected presentation profile '$expected', found '$helpText'."
    }
}

function Assert-ScaledFilesWorkbench {
    param([Parameter(Mandatory)][string] $CaptureName)

    Invoke-Ui 'invoke' 'ToggleWorkbenchButton' | Out-Null
    Invoke-Ui 'wait-for' 'RightPanelHost' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'FilesPanelTab' | Out-Null
    Invoke-Ui 'wait-for' 'FileWorkbenchSurface' '--timeout' '5000' | Out-Null
    Invoke-Ui 'wait-for' 'ScaleSample.cs' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'ScaleSample.cs' | Out-Null
    Invoke-Ui 'wait-for' 'WorkbenchFileEditor' '--timeout' '5000' | Out-Null

    $surface = Get-UiBounds -Selector 'FileWorkbenchSurface'
    $editor = Get-UiBounds -Selector 'WorkbenchFileEditor'
    if ($editor.Width -lt ($surface.Width * 0.9)) {
        throw "The scaled Files editor was only $($editor.Width)px wide inside a $($surface.Width)px workbench."
    }
    if ($editor.Height -lt (80 * $script:displayScale)) {
        throw 'The scaled Files editor has no usable height.'
    }

    $content = Get-UiProperty -Selector 'WorkbenchFileEditor' -Property 'Value'
    Invoke-Ui 'set-value' 'WorkbenchFileEditor' ($content + "`n// $CaptureName") | Out-Null
    Wait-UiValue -Selector 'SaveWorkbenchFileButton' -Value 'True' -Property 'IsEnabled'
    foreach ($selector in @('SaveWorkbenchFileButton', 'OpenWorkbenchFileInEditorButton', 'AddFileSelectionToComposerButton')) {
        Invoke-Ui 'focus' $selector | Out-Null
        Wait-UiValue -Selector $selector -Value 'False' -Property 'IsOffscreen' -Timeout 5000
    }
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot $CaptureName) '--focus' | Out-Null
    Invoke-Ui 'invoke' 'SaveWorkbenchFileButton' | Out-Null
    Wait-UiValue -Selector 'SaveWorkbenchFileButton' -Value 'False' -Property 'IsEnabled'
    if (-not (Get-Content -LiteralPath (Join-Path $projectPath 'ScaleSample.cs') -Raw).Contains("// $CaptureName")) {
        throw 'Saving the scaled Files fixture did not persist its edited contents.'
    }
    Invoke-Ui 'invoke' 'CloseRightPanelButton' | Out-Null
    Invoke-Ui 'wait-for' 'FileWorkbenchSurface' '--gone' '--timeout' '5000' | Out-Null
}

New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
Set-Content -LiteralPath (Join-Path $projectPath 'ScaleSample.cs') -Value @'
namespace ScaleSample;

public static class Sample
{
    public static string Message => "Text scaling fixture";
}
'@ -Encoding utf8NoBOM

try {
    if (-not $NoBuild) {
        Invoke-CheckedNative -FilePath 'dotnet' -ArgumentList @(
            'build', $solutionPath, '--configuration', $Configuration, '--property', 'Platform=x64'
        ) | Write-Host
    }

    Start-TestApp -TextScalePercent 100
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready'
    Assert-PresentationProfile -Theme 'Dark' -TextScalePercent 100

    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Invoke-Ui 'wait-for' 'compatibility-project' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Wait-TestThread -Title 'Thread 1' -Timeout 15000
    Invoke-Ui 'wait-for' 'PiConfigurationPanel' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'RuntimeErrorBanner' '--gone' '--timeout' '3000' | Out-Null

    $layoutCases = @(
        @{ Name = 'Wide'; Width = 1440; Height = 900; Artifact = 'responsive-wide.png' },
        @{ Name = 'Standard'; Width = 1040; Height = 800; Artifact = 'responsive-standard.png' },
        @{ Name = 'Compact'; Width = 800; Height = 720; Artifact = 'responsive-compact.png' },
        @{ Name = 'Narrow'; Width = 640; Height = 680; Artifact = 'responsive-narrow.png' }
    )
    foreach ($case in $layoutCases) {
        Set-TestWindowSize -Width $case.Width -Height $case.Height
        Assert-ResponsiveGeometry -LayoutName $case.Name
        if ($case.Name -eq 'Narrow') {
            $expandedSidebarWidth = (Get-UiBounds -Selector 'AppSidebar').Width
            $expandedInputWidth = (Get-UiBounds -Selector 'PromptInput').Width
            Invoke-Ui 'invoke' 'CollapseSidebarButton' | Out-Null
            Invoke-Ui 'wait-for' 'ExpandSidebarButton' '--timeout' '5000' | Out-Null
            # The group peer retains bounds of hidden expanded-pane children.
            # Verify the space actually returned to the visible input instead.
            $sidebarWidth = $expandedSidebarWidth - (
                (Get-UiBounds -Selector 'PromptInput').Width - $expandedInputWidth)
            if ([Math]::Abs($sidebarWidth - (52 * $script:displayScale)) -gt (3 * $script:displayScale)) {
                throw "The narrow collapsed sidebar width was $sidebarWidth instead of 52 logical pixels."
            }
            Assert-ResponsiveGeometry -LayoutName 'Narrow' -VisibleSidebarWidth $sidebarWidth
        }

        Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot $case.Artifact) `
            '--focus' | Out-Null
    }

    Invoke-Ui 'invoke' 'ExpandSidebarButton' | Out-Null
    $mainWindowHandle = (Get-Process -Id $script:launchedProcessId -ErrorAction Stop).MainWindowHandle
    [PiStationCompatibilityWindow]::ShowWindow($mainWindowHandle, 3) | Out-Null
    Assert-ResponsiveGeometry -LayoutName 'Wide'
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'responsive-maximized.png') `
        '--focus' | Out-Null
    [PiStationCompatibilityWindow]::ShowWindow($mainWindowHandle, 9) | Out-Null
    Set-TestWindowSize -Width 1280 -Height 800
    Assert-ResponsiveGeometry -LayoutName 'Wide'
    $initialComposerHeight = (Get-UiBounds -Selector 'ComposerSurface').Height
    $multilinePrompt = (1..10 | ForEach-Object { "Responsive composer line $_" }) -join "`n"
    Invoke-Ui 'set-value' 'PromptInput' $multilinePrompt | Out-Null
    $grownComposerHeight = Wait-ForComposerGrowth -InitialHeight $initialComposerHeight
    if ($grownComposerHeight -gt ((240 * $script:displayScale) + (4 * $script:displayScale))) {
        throw "The composer grew beyond its 240px bound to $grownComposerHeight."
    }
    Invoke-Ui 'send-keys' 'ctrl+a' '--target' 'PromptInput' '--via' 'post-message' | Out-Null
    Invoke-Ui 'send-keys' 'backspace' '--target' 'PromptInput' '--via' 'post-message' | Out-Null

    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsShell' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'SettingsAppearanceNavItem' | Out-Null
    Invoke-Ui 'wait-for' 'PiThemeSelector' '--timeout' '5000' | Out-Null
    Select-ComboBoxItem -ComboBox 'PiThemeSelector' -ItemName 'Light'
    Wait-UiValue -Selector 'SettingsThemeStatusText' -Value 'Light theme'
    Invoke-Ui 'invoke' 'CloseButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsShell' '--gone' '--timeout' '5000' | Out-Null
    Wait-UiValue -Selector 'SettingsButton' -Value 'True' -Property 'HasKeyboardFocus'
    Assert-PresentationProfile -Theme 'Light' -TextScalePercent 100
    Stop-TestApp

    $savedLayout = Get-Content -LiteralPath $layoutSettingsPath -Raw | ConvertFrom-Json
    if ($savedLayout.ThemePreference -ne 'Light') {
        throw "Expected the Light theme to persist, found '$($savedLayout.ThemePreference)'."
    }

    Start-TestApp -TextScalePercent 150
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready'
    Invoke-Ui 'wait-for' 'compatibility-project' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'compatibility-project' | Out-Null
    Wait-TestThread -Title 'Thread 1' -Timeout 10000
    Select-TestThread -Title 'Thread 1'
    Invoke-Ui 'wait-for' 'PiConfigurationPanel' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'RuntimeErrorBanner' '--gone' '--timeout' '3000' | Out-Null
    Set-TestWindowSize -Width 1440 -Height 900
    Assert-ResponsiveGeometry -LayoutName 'Wide'
    Assert-PresentationProfile -Theme 'Light' -TextScalePercent 150
    Assert-ScaledFilesWorkbench -CaptureName 'files-light-text-150.png'
    foreach ($selector in @('PromptInput', 'AttachFilesButton', 'SendPromptButton', 'WorkspaceStatusBar')) {
        Wait-UiValue -Selector $selector -Value 'False' -Property 'IsOffscreen'
    }
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'theme-light-text-150.png') `
        '--focus' | Out-Null

    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsShell' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'SettingsAppearanceNavItem' | Out-Null
    Invoke-Ui 'wait-for' 'PiThemeSelector' '--timeout' '5000' | Out-Null
    Select-ComboBoxItem -ComboBox 'PiThemeSelector' -ItemName 'System'
    Wait-UiValue -Selector 'SettingsThemeStatusText' -Value 'Follow Windows'
    Invoke-Ui 'invoke' 'CloseButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsShell' '--gone' '--timeout' '5000' | Out-Null
    Assert-PresentationProfile -Theme 'System' -TextScalePercent 150
    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsShell' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'SettingsAppearanceNavItem' | Out-Null
    Invoke-Ui 'wait-for' 'PiThemeSelector' '--timeout' '5000' | Out-Null
    Select-ComboBoxItem -ComboBox 'PiThemeSelector' -ItemName 'Dark'
    Wait-UiValue -Selector 'SettingsThemeStatusText' -Value 'Dark theme'
    Invoke-Ui 'invoke' 'CloseButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsShell' '--gone' '--timeout' '5000' | Out-Null
    Stop-TestApp

    Start-TestApp -TextScalePercent 200
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready'
    Invoke-Ui 'wait-for' 'compatibility-project' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'compatibility-project' | Out-Null
    Wait-TestThread -Title 'Thread 1' -Timeout 10000
    Select-TestThread -Title 'Thread 1'
    Invoke-Ui 'wait-for' 'PiConfigurationPanel' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'RuntimeErrorBanner' '--gone' '--timeout' '3000' | Out-Null
    Set-TestWindowSize -Width 1440 -Height 900
    Assert-ResponsiveGeometry -LayoutName 'Wide'
    Assert-PresentationProfile -Theme 'Dark' -TextScalePercent 200
    Assert-ScaledFilesWorkbench -CaptureName 'files-dark-text-200.png'
    foreach ($selector in @('PromptInput', 'AttachFilesButton', 'SendPromptButton', 'WorkspaceStatusBar')) {
        Wait-UiValue -Selector $selector -Value 'False' -Property 'IsOffscreen'
    }
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'theme-dark-text-200.png') `
        '--focus' | Out-Null

    $tree = Invoke-Ui 'inspect' '--depth' '10'
    Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Value $tree -Encoding utf8NoBOM
    Write-Output "Compatibility slice passed for Pi Station Desktop (PID $launchedProcessId)."
    Write-Output "Artifacts: $runRoot"
}
catch {
    $testError = $_
    if ($null -ne $launchedProcessId) {
        try {
            Invoke-Ui 'inspect' '--depth' '10' |
                Set-Content -LiteralPath (Join-Path $runRoot 'failure-ui-tree.json') -Encoding utf8NoBOM
            Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'failure.png') `
                '--focus' | Out-Null
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
        layoutSettingsPath = $layoutSettingsPath
        displayScale = $displayScale
        textScaleProfiles = @(100, 150, 200)
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
