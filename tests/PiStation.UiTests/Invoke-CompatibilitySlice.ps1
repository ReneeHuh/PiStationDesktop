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
$displayScale = 1.0

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class PiStationCompatibilityWindow
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);
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
    return Invoke-CheckedNative -FilePath 'winapp' -ArgumentList (@('ui') + $Arguments + @(
        '--app', "$script:launchedProcessId", '--json'
    ))
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
    $physicalWidth = [int][Math]::Round($Width * $script:displayScale)
    $physicalHeight = [int][Math]::Round($Height * $script:displayScale)
    $noMoveAndShow = 0x0042
    if (-not [PiStationCompatibilityWindow]::SetWindowPos(
        $process.MainWindowHandle,
        [IntPtr]::Zero,
        0,
        0,
        $physicalWidth,
        $physicalHeight,
        $noMoveAndShow)) {
        throw "Could not resize the packaged app window to ${Width}x${Height} logical pixels."
    }
}

function Assert-ResponsiveGeometry {
    param([Parameter(Mandatory)][string] $LayoutName)

    Wait-UiValue -Selector 'WorkspaceNavigation' -Value "Responsive layout: $LayoutName" `
        -Property 'HelpText'
    foreach ($selector in @('AppSidebar', 'TranscriptList', 'ComposerSurface', 'WorkspaceStatusBar')) {
        Invoke-Ui 'wait-for' $selector '--timeout' '5000' | Out-Null
    }

    $window = Get-UiBounds -Selector 'AppMainWindow'
    $sidebar = Get-UiBounds -Selector 'AppSidebar'
    $transcript = Get-UiBounds -Selector 'TranscriptList'
    $composer = Get-UiBounds -Selector 'ComposerSurface'
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

New-Item -ItemType Directory -Path $projectPath -Force | Out-Null

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
    Invoke-Ui 'wait-for' 'Thread 1' '--timeout' '15000' | Out-Null
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
            Invoke-Ui 'invoke' 'CollapseSidebarButton' | Out-Null
            Invoke-Ui 'wait-for' 'ExpandSidebarButton' '--timeout' '5000' | Out-Null
            $sidebarWidth = (Get-UiBounds -Selector 'AppSidebar').Width
            if ([Math]::Abs($sidebarWidth - (52 * $script:displayScale)) -gt (3 * $script:displayScale)) {
                throw "The narrow collapsed sidebar width was $sidebarWidth instead of 52 logical pixels."
            }
            Assert-ResponsiveGeometry -LayoutName 'Narrow'
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
    Invoke-Ui 'wait-for' 'Thread 1' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'Thread 1' | Out-Null
    Invoke-Ui 'wait-for' 'PiConfigurationPanel' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'RuntimeErrorBanner' '--gone' '--timeout' '3000' | Out-Null
    Set-TestWindowSize -Width 1440 -Height 900
    Assert-ResponsiveGeometry -LayoutName 'Wide'
    Assert-PresentationProfile -Theme 'Light' -TextScalePercent 150
    foreach ($selector in @('PromptInput', 'AttachFilesButton', 'SendPromptButton', 'WorkspaceStatusBar')) {
        Wait-UiValue -Selector $selector -Value 'False' -Property 'IsOffscreen'
    }
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'theme-light-text-150.png') `
        '--focus' | Out-Null

    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'wait-for' 'PiThemeSelector' '--timeout' '5000' | Out-Null
    Select-ComboBoxItem -ComboBox 'PiThemeSelector' -ItemName 'System'
    Wait-UiValue -Selector 'SettingsThemeStatusText' -Value 'Follow Windows'
    Invoke-Ui 'invoke' 'CloseButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsShell' '--gone' '--timeout' '5000' | Out-Null
    Assert-PresentationProfile -Theme 'System' -TextScalePercent 150
    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
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
    Invoke-Ui 'wait-for' 'Thread 1' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'Thread 1' | Out-Null
    Invoke-Ui 'wait-for' 'PiConfigurationPanel' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'RuntimeErrorBanner' '--gone' '--timeout' '3000' | Out-Null
    Set-TestWindowSize -Width 1440 -Height 900
    Assert-ResponsiveGeometry -LayoutName 'Wide'
    Assert-PresentationProfile -Theme 'Dark' -TextScalePercent 200
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
