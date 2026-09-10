[CmdletBinding()]
param(
    [ValidateSet('Debug')]
    [string] $Configuration = 'Debug',

    [switch] $NoBuild,

    [ValidateSet('All', 'Changes', 'Visual')]
    [string] $Scope = 'All'
)

$ErrorActionPreference = 'Stop'
$solutionRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$solutionPath = Join-Path $solutionRoot 'PiStationDesktop.slnx'
$appProject = Join-Path $solutionRoot 'src\PiStation.App\PiStation.App.csproj'
$fakePi = Join-Path $solutionRoot "tests\PiStation.FakePi\bin\$Configuration\net10.0\PiStation.FakePi.exe"
$artifactRoot = Join-Path $PSScriptRoot 'artifacts'
$runRoot = Join-Path $artifactRoot (Join-Path 'workbench-runs' ("{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N')))
$dataRoot = Join-Path $runRoot 'data'
$projectPath = Join-Path $dataRoot 'workbench-project'
$logFile = Join-Path $dataRoot 'app.jsonl'
$layoutSettingsPath = Join-Path $dataRoot 'layout-settings.json'
$launchedProcessId = $null
$previewServerJob = $null
$previewServerPort = $null
$testError = $null
. (Join-Path $PSScriptRoot 'Select-TestThread.ps1')
$launchedWindowHandle = [IntPtr]::Zero

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class PiStationWorkbenchWindow
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    public static bool ResizeLogicalPixels(IntPtr window, int width, int height)
    {
        // SetWindowPos otherwise virtualizes coordinates in a DPI-unaware PowerShell host.
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
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $stateJson = & winapp ui inspect --depth 1 --app $script:launchedProcessId --json 2>$null
        if ($LASTEXITCODE -eq 0) {
            $state = $stateJson | ConvertFrom-Json
            $window = @($state.windows | Where-Object { $_.title -eq 'Pi Station Desktop' }) |
                Select-Object -First 1
            if ($null -ne $window -and [long]$window.hwnd -ne 0) {
                $script:launchedWindowHandle = [IntPtr][long]$window.hwnd
            }
        }

        if ($script:launchedWindowHandle -ne [IntPtr]::Zero) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'The packaged app did not expose its main window after launch.'
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
    $script:launchedWindowHandle = [IntPtr]::Zero
}

function Start-PreviewServer {
    $script:previewServerJob = Start-Job -ScriptBlock {
        $listener = $null
        try {
            foreach ($candidate in @(5175, 5500, 9000, 8081)) {
                try {
                    $listener = [System.Net.Sockets.TcpListener]::new(
                        [System.Net.IPAddress]::Loopback,
                        $candidate)
                    $listener.Start()
                    Write-Output "READY:$candidate"
                    break
                }
                catch [System.Net.Sockets.SocketException] {
                    if ($null -ne $listener) {
                        $listener.Stop()
                    }
                    $listener = $null
                }
            }

            if ($null -eq $listener) {
                throw 'No known development preview port was available.'
            }

            while ($true) {
                # Keep the job cancellable while no preview has connected yet.
                if (-not $listener.Pending()) {
                    Start-Sleep -Milliseconds 100
                    continue
                }

                $client = $listener.AcceptTcpClient()
                $client.ReceiveTimeout = 2000
                try {
                    $stream = $client.GetStream()
                    $stream.ReadTimeout = 5000
                    $reader = [System.IO.StreamReader]::new(
                        $stream,
                        [System.Text.Encoding]::ASCII,
                        $false,
                        1024,
                        $true)
                    while (($line = $reader.ReadLine()) -ne $null -and $line.Length -gt 0) {
                    }
                    $reader.Dispose()
                    $body = [System.Text.Encoding]::UTF8.GetBytes(
                        '<!doctype html><html><head><title>PiStation Preview Test</title></head><body><h1>Preview ready</h1></body></html>')
                    $header = [System.Text.Encoding]::ASCII.GetBytes(
                        "HTTP/1.1 200 OK`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n")
                    $stream.Write($header, 0, $header.Length)
                    $stream.Write($body, 0, $body.Length)
                    $stream.Flush()
                }
                catch [System.IO.IOException] {
                    # Browsers can open speculative connections without a request
                    # or close a tab mid-response. Keep serving later requests.
                }
                catch [System.Net.Sockets.SocketException] {
                }
                finally {
                    $client.Dispose()
                }
            }
        }
        finally {
            if ($null -ne $listener) {
                $listener.Stop()
            }
        }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline) {
        $ready = @(Receive-Job -Job $script:previewServerJob -Keep) |
            Where-Object { [string]$_ -match '^READY:(\d+)$' } |
            Select-Object -First 1
        if ($null -ne $ready -and [string]$ready -match '^READY:(\d+)$') {
            $script:previewServerPort = [int]$Matches[1]
            return
        }

        if ($script:previewServerJob.State -eq 'Failed') {
            $failure = Receive-Job -Job $script:previewServerJob -Keep 2>&1 | Out-String
            throw "Preview test server failed to start.`n$failure"
        }

        Start-Sleep -Milliseconds 100
    }

    throw 'Preview test server did not report its listening port.'
}

function Stop-PreviewServer {
    if ($null -eq $script:previewServerJob) {
        return
    }

    Stop-Job -Job $script:previewServerJob -ErrorAction SilentlyContinue
    Remove-Job -Job $script:previewServerJob -Force -ErrorAction SilentlyContinue
    $script:previewServerJob = $null
    $script:previewServerPort = $null
}

function Invoke-Ui {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)
    if ($Arguments -contains 'PromptInput' -and $Arguments[0] -in @('set-value', 'focus', 'click', 'invoke', 'type', 'send-keys', 'inspect', 'get-value', 'get-property', 'wait-for') -and $Arguments -notcontains '--gone') { Expand-TestComposer }
    for ($readAttempt = 0; ; $readAttempt++) {
        try {
            $result = Invoke-CheckedNative -FilePath 'winapp' -ArgumentList (@('ui') + $Arguments + @(
                '--app', "$script:launchedProcessId", '--json'
            ))
            if ($Arguments[0] -eq 'screenshot' -and ($outputIndex = [Array]::IndexOf($Arguments, '--output')) -ge 0) {
                $fallbackResult = & (Join-Path $PSScriptRoot 'Invoke-ValidatedScreenshot.ps1') -FilePath 'winapp' -ArgumentList (@('ui') + $Arguments + @('--app', "$script:launchedProcessId", '--json')); if ($fallbackResult) { $result = $fallbackResult }
            }
            return $result
        }
        catch {
            $canRetryRead = $Arguments[0] -in @('get-property', 'inspect', 'wait-for') -and
                $_.Exception.Message -match '"code"\s*:\s*"(stale_element|element_not_found)"'
            if (-not $canRetryRead -or $readAttempt -ge 2) { throw }
            # Pane rebuilding briefly replaces UIA peers. Reacquire reads by their
            # semantic selector; never replay an input, click, or host mutation here.
            Start-Sleep -Milliseconds 100
        }
    }
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

function Wait-UiPropertyContains {
    param(
        [Parameter(Mandatory)][string] $Selector,
        [Parameter(Mandatory)][string] $Expected,
        [Parameter(Mandatory)][string] $Property,
        [int] $Timeout = 10000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($Timeout)
    do {
        $result = Invoke-Ui 'get-property' $Selector '--property' $Property | ConvertFrom-Json
        $value = [string]$result.properties.$Property
        if ($value.Contains($Expected, [System.StringComparison]::OrdinalIgnoreCase)) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    $preview = if ($value.Length -gt 600) { $value.Substring($value.Length - 600) } else { $value }
    throw "'$Selector' property '$Property' did not contain '$Expected'. Tail: '$preview'."
}

function Wait-UiPropertyEmpty {
    param(
        [Parameter(Mandatory)][string] $Selector,
        [Parameter(Mandatory)][string] $Property,
        [int] $Timeout = 5000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($Timeout)
    do {
        $result = Invoke-Ui 'get-property' $Selector '--property' $Property | ConvertFrom-Json
        $value = [string]$result.properties.$Property
        if ([string]::IsNullOrEmpty($value)) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "'$Selector' property '$Property' did not become empty."
}

function Wait-TerminalSearchResults {
    param([int] $Timeout = 10000)

    $deadline = [DateTime]::UtcNow.AddMilliseconds($Timeout)
    do {
        $result = Invoke-Ui 'get-property' 'TerminalSearchCountText' '--property' 'Name' |
            ConvertFrom-Json
        $value = [string]$result.properties.Name
        if ($value -match '^[1-9][0-9]* of [1-9][0-9]*$') {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Terminal search did not report a result. Last count: '$value'."
}

function Get-TerminalPaneLeafCount {
    param([Parameter(Mandatory)] $Node)

    if ($null -eq $Node.First -or $null -eq $Node.Second) {
        return 1
    }

    return (Get-TerminalPaneLeafCount -Node $Node.First) +
        (Get-TerminalPaneLeafCount -Node $Node.Second)
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

function Get-UiBounds {
    param([Parameter(Mandatory)][string] $Selector)

    $result = Invoke-Ui 'get-property' $Selector '--property' 'BoundingRectangle' |
        ConvertFrom-Json
    $parts = @($result.properties.BoundingRectangle -split ',')
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

function Update-TestWindowHandle {
    $state = Invoke-Ui 'inspect' '--depth' '1' | ConvertFrom-Json
    $window = @($state.windows | Where-Object { $_.title -eq 'Pi Station Desktop' }) |
        Select-Object -First 1
    if ($null -eq $window -or [long]$window.hwnd -eq 0) {
        throw 'The UI driver did not report the Pi Station Desktop window.'
    }

    $script:launchedWindowHandle = [IntPtr][long]$window.hwnd
}

function Set-TestWindowSize {
    param(
        [Parameter(Mandatory)][int] $Width,
        [Parameter(Mandatory)][int] $Height
    )

    Update-TestWindowHandle

    $didResize = [PiStationWorkbenchWindow]::ResizeLogicalPixels(
        $script:launchedWindowHandle,
        $Width,
        $Height)
    if (-not $didResize) {
        throw "Could not resize the packaged app window to ${Width}x${Height}."
    }
}

function Wait-ForWindowWidth {
    param(
        [Parameter(Mandatory)][double] $Threshold,
        [Parameter(Mandatory)][ValidateSet('Below', 'Above')][string] $Direction,
        [int] $Timeout = 15000
    )

    if ($script:launchedWindowHandle -eq [IntPtr]::Zero) {
        throw 'The packaged app did not expose a main window handle.'
    }

    $dpi = [PiStationWorkbenchWindow]::GetDpiForWindow($script:launchedWindowHandle)
    $scaledThreshold = $Threshold * ([Math]::Max(96, $dpi) / 96.0)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($Timeout)
    do {
        $rect = New-Object PiStationWorkbenchWindow+Rect
        if (-not [PiStationWorkbenchWindow]::GetWindowRect($script:launchedWindowHandle, [ref]$rect)) {
            throw 'Could not read the packaged app window bounds.'
        }

        $windowWidth = $rect.Right - $rect.Left
        $layoutResult = Invoke-Ui 'get-property' 'WorkspaceNavigation' '--property' 'HelpText' |
            ConvertFrom-Json
        $layout = [string]$layoutResult.properties.HelpText
        $layoutSettled = if ($Direction -eq 'Below') {
            $layout -match 'Responsive layout: (Narrow|Compact)'
        }
        else {
            $layout -match 'Responsive layout: (Standard|Wide)'
        }
        $geometrySettled = $true
        if ($layoutSettled -and $Direction -eq 'Below') {
            $sidebar = Get-UiBounds -Selector 'AppSidebar'
            $composer = Get-UiBounds -Selector 'ComposerSurface'
            $panel = Get-UiBounds -Selector 'AgentsWorkbenchSurface'
            $geometrySettled = $panel.X -lt ($composer.X + $composer.Width) -and
                $panel.X -ge ($sidebar.X + $sidebar.Width - 2)
        }

        if ($layoutSettled -and $geometrySettled -and
            (($Direction -eq 'Below' -and $windowWidth -lt $scaledThreshold) -or
             ($Direction -eq 'Above' -and $windowWidth -gt $scaledThreshold))) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "The app window width $windowWidth did not move $Direction $scaledThreshold physical pixels."
}

New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
$sourcePath = Join-Path $projectPath 'src'
New-Item -ItemType Directory -Path $sourcePath -Force | Out-Null
[System.IO.File]::WriteAllText(
    (Join-Path $sourcePath 'WorkbenchPreview.cs'),
    'public static class WorkbenchPreview { }')
[System.IO.File]::WriteAllText(
    (Join-Path $projectPath 'README.md'),
    'Pi Station workbench file preview')
[System.IO.File]::WriteAllText(
    (Join-Path $projectPath 'preview.html'),
    '<!doctype html><html><body><h1>Rendered safely</h1></body></html>')
Invoke-CheckedNative -FilePath 'git' -ArgumentList @(
    '-C', $projectPath, 'init', '--quiet', '--initial-branch=main'
) | Out-Null
Invoke-CheckedNative -FilePath 'git' -ArgumentList @(
    '-C', $projectPath, 'config', 'user.email', 'pistation@example.invalid'
) | Out-Null
Invoke-CheckedNative -FilePath 'git' -ArgumentList @(
    '-C', $projectPath, 'config', 'user.name', 'Pi Station UI Tests'
) | Out-Null
Invoke-CheckedNative -FilePath 'git' -ArgumentList @(
    '-C', $projectPath, 'add', 'README.md', 'preview.html', 'src/WorkbenchPreview.cs'
) | Out-Null
Invoke-CheckedNative -FilePath 'git' -ArgumentList @(
    '-C', $projectPath, 'commit', '--quiet', '-m', 'baseline'
) | Out-Null
[System.IO.File]::WriteAllText(
    (Join-Path $projectPath 'README.md'),
    'Pi Station workbench file preview (modified)')
[System.IO.File]::WriteAllText(
    (Join-Path $sourcePath 'Staged.cs'),
    'public static class StagedChange { }')
Invoke-CheckedNative -FilePath 'git' -ArgumentList @(
    '-C', $projectPath, 'add', 'src/Staged.cs'
) | Out-Null
[System.IO.File]::WriteAllText(
    (Join-Path $projectPath 'notes.txt'),
    'untracked workbench note')

try {
    Start-PreviewServer
    if (-not $NoBuild) {
        Invoke-CheckedNative -FilePath 'dotnet' -ArgumentList @(
            'build', $solutionPath, '--configuration', $Configuration, '--property', 'Platform=x64'
        ) | Write-Host
    }

    Start-TestApp
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready'
    # The initial workbench assertions inspect all three change rows. Start tall
    # enough to realize them; the overlay layout is exercised explicitly below.
    Set-TestWindowSize -Width 1400 -Height 1100
    Wait-ForWindowWidth -Threshold 1000 -Direction 'Above'
    foreach ($selector in @(
        'ToggleWorkbenchButton',
        'WorkspaceStatusBar',
        'WorkspaceProjectStatusText',
        'WorkspaceSourceControlStatusText'
    )) {
        Invoke-Ui 'wait-for' $selector '--timeout' '5000' | Out-Null
    }

    Invoke-Ui 'wait-for' 'RightPanelHost' '--gone' '--timeout' '5000' | Out-Null

    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Invoke-Ui 'wait-for' 'workbench-project' '--timeout' '10000' | Out-Null

    Invoke-Ui 'invoke' 'AddActionButton' | Out-Null
    Invoke-Ui 'wait-for' 'HeaderNewThreadMenuItem' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'HeaderNewThreadMenuItem' | Out-Null
    Wait-TestThread -Title 'Thread 1' -Timeout 15000
    Invoke-Ui 'wait-for' 'ThreadEmptyState' '--timeout' '5000' | Out-Null

    Invoke-Ui 'set-value' 'PromptInput' 'Capture the workbench checkpoint' | Out-Null
    Invoke-Ui 'invoke' 'SendPromptButton' | Out-Null
    $fakePiGreeting = 'Hello from Fake Pi ' + [char]::ConvertFromUtf32(0x1F47D)
    Wait-UiValue -Selector 'LatestAssistantMessage' -Value $fakePiGreeting
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Idle'
    Invoke-Ui 'wait-for' 'TurnCheckpointCard' '--timeout' '10000' | Out-Null

    Invoke-Ui 'invoke' 'AddActionButton' | Out-Null
    Invoke-Ui 'wait-for' 'HeaderCommandPaletteMenuItem' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'HeaderCommandPaletteMenuItem' | Out-Null
    foreach ($selector in @(
        'CommandPaletteDialog',
        'CommandPaletteQuery',
        'CommandPaletteResults',
        'CommandPaletteStatusText'
    )) {
        Invoke-Ui 'wait-for' $selector '--timeout' '5000' | Out-Null
    }
    Invoke-Ui 'set-value' 'CommandPaletteQuery' $fakePiGreeting | Out-Null
    Wait-UiValue -Selector 'CommandPaletteStatusText' -Value '1 results' -Timeout 15000
    Invoke-Ui 'send-keys' 'esc' '--target' 'CommandPaletteQuery' '--via' 'post-message' | Out-Null
    Invoke-Ui 'wait-for' 'CommandPaletteDialog' '--gone' '--timeout' '5000' | Out-Null

    Invoke-Ui 'invoke' 'AddActionButton' | Out-Null
    Invoke-Ui 'wait-for' 'HeaderCommandPaletteMenuItem' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'HeaderCommandPaletteMenuItem' | Out-Null
    Invoke-Ui 'wait-for' 'CommandPaletteQuery' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'CommandPaletteQuery' '>Show Changes' | Out-Null
    Wait-UiValue -Selector 'CommandPaletteStatusText' -Value '1 command'
    Invoke-Ui 'send-keys' 'enter' '--target' 'CommandPaletteQuery' '--via' 'post-message' | Out-Null
    Invoke-Ui 'wait-for' 'CommandPaletteDialog' '--gone' '--timeout' '5000' | Out-Null
    foreach ($selector in @(
        'RightPanelHost',
        'RightPanelResizeHandle',
        'RightPanelTabs',
        'ChangesPanelTab',
        'FilesPanelTab',
        'TerminalPanelTab',
        'PreviewPanelTab',
        'AgentsPanelTab',
        'CloseRightPanelButton',
        'RightPanelContent',
        'ChangesWorkbenchSurface',
        'GitBranchNameText',
        'GitBranchDetailText',
        'RefreshChangesButton',
        'WorkbenchChangesStatusText',
        'WorkbenchChangeList',
        'WorkbenchDiffPathText',
        'WorkbenchDiffStatusText',
        'AddDiffSelectionToComposerButton'
    )) {
        Invoke-Ui 'wait-for' $selector '--timeout' '5000' | Out-Null
    }

    Wait-UiValue -Selector 'GitBranchNameText' -Value 'main'
    Wait-UiValue -Selector 'GitBranchDetailText' -Value 'Local branch'
    Wait-UiValue -Selector 'WorkbenchChangesStatusText' -Value '3 changed files'
    Wait-UiValue -Selector 'WorkspaceSourceControlStatusText' `
        -Value ('main • 3 changes • +3 ' + [char]0x2212 + '1')
    foreach ($changedFile in @('README.md', 'Staged.cs', 'notes.txt')) {
        Invoke-Ui 'wait-for' $changedFile '--timeout' '5000' | Out-Null
    }
    # Read nested row labels from the list subtree instead of the driver's
    # shallower window-wide selector search.
    $changeTree = Invoke-Ui 'inspect' 'WorkbenchChangeList' '--depth' '8'
    foreach ($changeArea in @('WORKTREE', 'STAGED', 'UNTRACKED')) {
        if ($changeTree -notmatch ('"name"\s*:\s*"' + [regex]::Escape($changeArea) + '"')) {
            throw "The Changes workbench did not expose the $changeArea row label."
        }
    }
    Invoke-Ui 'invoke' 'README.md' | Out-Null
    Wait-UiValue -Selector 'WorkbenchDiffPathText' -Value 'README.md'
    Wait-UiValue -Selector 'WorkbenchDiffStatusText' -Value 'working tree'
    $readmeDiff = Invoke-Ui 'get-property' 'WorkbenchDiffPreview' '--property' 'Value' | ConvertFrom-Json
    if (-not ([string]$readmeDiff.properties.Value).Contains(
        '+Pi Station workbench file preview (modified)',
        [System.StringComparison]::Ordinal)) {
        throw 'The Changes workbench did not render the selected working-tree diff.'
    }
    $diffTreeJson = Invoke-Ui 'inspect' 'WorkbenchDiffPreview' '--depth' '12'
    Set-Content -LiteralPath (Join-Path $runRoot 'diff-ui-tree.json') -Value $diffTreeJson -Encoding UTF8
    $diffTree = $diffTreeJson | ConvertFrom-Json -Depth 100
    $pendingDiffNodes = [System.Collections.Generic.Queue[object]]::new()
    foreach ($window in $diffTree.windows) { $pendingDiffNodes.Enqueue($window) }
    $visibleDiffLines = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    while ($pendingDiffNodes.Count -gt 0) {
        $node = $pendingDiffNodes.Dequeue()
        if ($node.className -eq 'RichTextBlock' -and -not $node.isOffscreen -and $node.width -gt 0 -and $node.height -gt 0) {
            $visibleDiffLines.Add(([string]$node.name).TrimEnd()) | Out-Null
        }
        foreach ($child in (@($node.children) + @($node.elements))) {
            if ($null -ne $child) { $pendingDiffNodes.Enqueue($child) }
        }
    }
    foreach ($expectedLine in @('-Pi Station workbench file preview', '+Pi Station workbench file preview (modified)')) {
        if (-not $visibleDiffLines.Contains($expectedLine)) {
            throw "The diff value was loaded, but the visible code row '$expectedLine' was missing."
        }
    }
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'changes-workbench.png') '--focus' |
        Out-Null
    foreach ($color in @('2A1D1D', '192720')) {
        & (Join-Path $PSScriptRoot 'Assert-ScreenshotPalette.ps1') -Path (Join-Path $runRoot 'changes-workbench.png') `
            -Color $color -Left 0.7 -Top 0.5 -MinimumSamples 200 | Out-Null
    }
    if ($Scope -eq 'Changes') {
        Write-Output "Changes slice passed for Pi Station Desktop (PID $launchedProcessId)."
        Write-Output "Artifacts: $runRoot"
        return
    }
    Invoke-Ui 'invoke' 'RefreshChangesButton' | Out-Null
    Wait-UiValue -Selector 'WorkbenchChangesStatusText' -Value '3 changed files'

    Invoke-Ui 'invoke' 'FilesPanelTab' | Out-Null
    foreach ($selector in @(
        'FileWorkbenchSurface',
        'WorkbenchFileSearchInput',
        'WorkbenchFileSearchMode',
        'RefreshWorkbenchFilesButton',
        'OpenExternalReadOnlyFileButton',
        'WorkspaceFileTree',
        'WorkbenchFileStatusText',
        'WorkbenchFileTabs'
    )) {
        Invoke-Ui 'wait-for' $selector '--timeout' '5000' | Out-Null
    }
    Wait-UiValue -Selector 'WorkbenchFileStatusText' -Value '5 files • 1 folder'
    Invoke-Ui 'wait-for' 'WorkbenchPreview.cs' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'WorkbenchPreview.cs' | Out-Null
    Wait-UiValue -Selector 'WorkbenchFilePreviewPathText' -Value 'src/WorkbenchPreview.cs'
    Wait-UiValue -Selector 'WorkbenchFileEditor' -Value 'public static class WorkbenchPreview { }'
    $fileTree = Get-UiBounds -Selector 'WorkspaceFileTree'
    Invoke-Ui 'wait-for' 'AddFileSelectionToComposerButton' '--timeout' '5000' | Out-Null

    Invoke-Ui 'wait-for' 'preview.html' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'preview.html' | Out-Null
    Wait-UiValue -Selector 'WorkbenchFilePreviewPathText' -Value 'preview.html'
    Wait-UiValue -Selector 'WorkbenchRenderedFilePreview' -Value 'False' -Property 'IsOffscreen'
    Invoke-Ui 'invoke' 'WorkbenchMarkdownToggle' | Out-Null
    Wait-UiValue -Selector 'WorkbenchFileEditor' `
        -Value '<!doctype html><html><body><h1>Rendered safely</h1></body></html>'

    Invoke-Ui 'set-value' 'WorkbenchFileSearchInput' 'readme' | Out-Null
    Wait-UiValue -Selector 'WorkbenchFileStatusText' -Value '1 project file'
    Invoke-Ui 'wait-for' 'README.md' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'README.md' | Out-Null
    Wait-UiValue -Selector 'WorkbenchFilePreviewPathText' -Value 'README.md'
    Wait-UiValue -Selector 'WorkbenchFileEditor' -Value 'Pi Station workbench file preview (modified)'
    $filePanel = Get-UiBounds -Selector 'FileWorkbenchSurface'
    $fileEditor = Get-UiBounds -Selector 'WorkbenchFileEditor'
    if ($fileEditor.Y -lt ($fileTree.Y + $fileTree.Height) -or $fileEditor.Width -lt ($filePanel.Width * 0.8)) {
        throw 'The default Files panel must stack the tree above a full-width editor.'
    }
    foreach ($selector in @('SaveWorkbenchFileButton', 'OpenWorkbenchFileInEditorButton', 'AddFileSelectionToComposerButton')) {
        Wait-UiValue -Selector $selector -Value 'False' -Property 'IsOffscreen'
        $actionBounds = Get-UiBounds -Selector $selector
        if ($actionBounds.X -lt $filePanel.X -or ($actionBounds.X + $actionBounds.Width) -gt ($filePanel.X + $filePanel.Width + 2)) {
            throw "The Files action '$selector' extends outside its panel."
        }
    }
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'files-workbench.png') '--focus' |
        Out-Null
    Invoke-Ui 'set-value' 'RightPanelResizeHandle' '640' | Out-Null
    Wait-UiValue -Selector 'RightPanelResizeHandle' -Value 'Workbench width 640 pixels' -Property 'HelpText'
    $wideFileList = Get-UiBounds -Selector 'WorkbenchFileList'
    $wideFileEditor = Get-UiBounds -Selector 'WorkbenchFileEditor'
    if ($wideFileEditor.X -lt ($wideFileList.X + $wideFileList.Width)) {
        throw 'A wide Files panel must place the editor beside the file list.'
    }
    $wideComposer = Get-UiBounds -Selector 'ComposerSurface'
    $wideStatus = Get-UiBounds -Selector 'WorkspaceStatusBar'
    foreach ($selector in @('SendPromptButton', 'StopTurnButton')) {
        $action = Get-UiBounds -Selector $selector
        if ($action.Width -le 0 -or $action.Height -le 0 -or
            $action.X -lt $wideStatus.X -or $action.Y -lt $wideComposer.Y -or
            ($action.X + $action.Width) -gt ($wideStatus.X + $wideStatus.Width + 2) -or
            ($action.Y + $action.Height) -gt ($wideComposer.Y + $wideComposer.Height + 2)) {
            throw "The wide Files panel clipped the composer's $selector."
        }
    }
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'files-workbench-wide.png') '--focus' | Out-Null
    Invoke-Ui 'set-value' 'RightPanelResizeHandle' '420' | Out-Null
    Wait-UiValue -Selector 'RightPanelResizeHandle' -Value 'Workbench width 420 pixels' -Property 'HelpText'
    Invoke-Ui 'invoke' 'RefreshWorkbenchFilesButton' | Out-Null
    Wait-UiValue -Selector 'WorkbenchFileStatusText' -Value '1 project file'

    if ($Scope -eq 'All') {
    Invoke-Ui 'invoke' 'TerminalPanelTab' | Out-Null
    foreach ($selector in @(
        'TerminalWorkbenchSurface',
        'TerminalShellSelector',
        'NewTerminalButton',
        'TerminalSessionSelector',
        'StopTerminalButton',
        'RestartTerminalButton',
        'ClearTerminalButton',
        'CloseTerminalButton',
        'TerminalSessionSummaryText',
        'TerminalPaneSummaryText',
        'SplitTerminalRightButton',
        'SplitTerminalDownButton',
        'CloseTerminalPaneButton',
        'PrimaryTerminalPane',
        'TerminalStatusText',
        'TerminalOutput',
        'TerminalInput',
        'SendTerminalInputButton'
    )) {
        Invoke-Ui 'wait-for' $selector '--timeout' '5000' | Out-Null
    }
    Wait-UiValue -Selector 'TerminalStatusText' -Value 'No terminal sessions yet'
    Invoke-Ui 'invoke' 'NewTerminalButton' | Out-Null
    Wait-UiValue -Selector 'TerminalSessionSummaryText' -Value 'PowerShell • running' -Timeout 15000
    Invoke-Ui 'set-value' 'TerminalInput' "Write-Output ('pistation-terminal-' + 'executed')" | Out-Null
    Invoke-Ui 'invoke' 'SendTerminalInputButton' | Out-Null
    Wait-UiPropertyContains -Selector 'TerminalOutput' `
        -Property 'Value' -Expected 'pistation-terminal-executed' -Timeout 15000
    Invoke-Ui 'focus' 'TerminalOutput' | Out-Null
    Invoke-Ui 'send-keys' "Write-Output ('pistation-keyboard-' + 'executed')" `
        '--target' 'TerminalOutput' '--via' 'post-message' '--verbatim' | Out-Null
    Wait-UiPropertyContains -Selector 'TerminalOutput' `
        -Property 'Value' -Expected 'pistation-keybo' -Timeout 15000
    Invoke-Ui 'set-value' 'TerminalInput' ';' | Out-Null
    Invoke-Ui 'invoke' 'SendTerminalInputButton' | Out-Null
    Wait-UiPropertyContains -Selector 'TerminalOutput' `
        -Property 'Value' -Expected 'pistation-keyboard-executed' -Timeout 15000
    $sgrCommand = 'Write-Host "$([char]27)[1;3;4;9;38;2;76;141;255;48;5;236mSGR-STYLED$([char]27)[0m"'
    Invoke-Ui 'set-value' 'TerminalInput' $sgrCommand | Out-Null
    Invoke-Ui 'invoke' 'SendTerminalInputButton' | Out-Null
    Wait-UiPropertyContains -Selector 'TerminalOutput' `
        -Property 'Value' -Expected 'SGR-STYLED' -Timeout 15000
    $escapedFakePi = $fakePi.Replace("'", "''")
    $mouseCommand = "& '$escapedFakePi' --terminal-mouse-probe --transition-only"
    Invoke-Ui 'set-value' 'TerminalInput' $mouseCommand | Out-Null
    Invoke-Ui 'invoke' 'SendTerminalInputButton' | Out-Null
    Wait-UiPropertyContains -Selector 'TerminalOutput' `
        -Property 'Value' -Expected 'MOUSE-READY' -Timeout 15000
    Wait-UiValue -Selector 'TerminalOutput' `
        -Value 'Application mouse input is active. Hold Shift while selecting terminal text.' `
        -Property 'HelpText'
    Wait-UiPropertyContains -Selector 'TerminalOutput' `
        -Property 'Value' -Expected 'MOUSE-MODE-RESTORED' -Timeout 15000
    Wait-UiValue -Selector 'TerminalOutput' `
        -Value 'Focus the terminal output to send interactive keyboard input.' `
        -Property 'HelpText'
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'terminal-workbench.png') '--focus' |
        Out-Null

    Invoke-Ui 'invoke' 'TerminalOutput' | Out-Null
    foreach ($menuItem in @(
        'TerminalFindMenuItem',
        'TerminalCopyMenuItem',
        'TerminalPasteMenuItem',
        'TerminalSelectAllMenuItem',
        'TerminalClearMenuItem'
    )) {
        Invoke-Ui 'wait-for' $menuItem '--timeout' '5000' | Out-Null
    }
    Invoke-Ui 'invoke' 'TerminalFindMenuItem' | Out-Null
    foreach ($searchControl in @(
        'TerminalSearchOverlay',
        'TerminalSearchTextBox',
        'TerminalSearchCountText',
        'TerminalSearchMatchCaseToggle',
        'TerminalSearchWholeWordToggle',
        'TerminalSearchPreviousButton',
        'TerminalSearchNextButton',
        'TerminalSearchCloseButton'
    )) {
        Invoke-Ui 'wait-for' $searchControl '--timeout' '5000' | Out-Null
    }
    Invoke-Ui 'set-value' 'TerminalSearchTextBox' 'PISTATION-TERMINAL-EXECUTED' | Out-Null
    Wait-TerminalSearchResults
    Wait-UiValue -Selector 'TerminalSearchNextButton' -Value 'True' -Property 'IsEnabled'
    $searchBefore = Invoke-Ui 'get-property' 'TerminalSearchCountText' '--property' 'Name' | ConvertFrom-Json
    $searchCount = [regex]::Match([string]$searchBefore.properties.Name, '^(\d+) of (\d+)$')
    if (-not $searchCount.Success -or [int]$searchCount.Groups[2].Value -eq 0) { throw 'Terminal search lost its results before navigation.' }
    $expectedSearchCount = '{0} of {1}' -f (([int]$searchCount.Groups[1].Value % [int]$searchCount.Groups[2].Value) + 1), $searchCount.Groups[2].Value
    try { Invoke-Ui 'invoke' 'TerminalSearchNextButton' | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch '"code"\s*:\s*"stale_element"') { throw }
        # winapp can lose the peer while reporting an already completed navigation.
        # Verify its effect instead of issuing the action again.
    }
    Wait-UiValue -Selector 'TerminalSearchCountText' -Value $expectedSearchCount
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'terminal-search.png') '--focus' |
        Out-Null
    Invoke-Ui 'invoke' 'TerminalSearchMatchCaseToggle' | Out-Null
    Wait-UiValue -Selector 'TerminalSearchCountText' -Value '0 of 0'
    Invoke-Ui 'invoke' 'TerminalSearchMatchCaseToggle' | Out-Null
    Wait-TerminalSearchResults
    Invoke-Ui 'invoke' 'TerminalSearchCloseButton' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalSearchOverlay' '--gone' '--timeout' '5000' | Out-Null

    Invoke-Ui 'invoke' 'TerminalOutput' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalSelectAllMenuItem' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'TerminalSelectAllMenuItem' | Out-Null
    Start-Sleep -Milliseconds 250
    Invoke-Ui 'invoke' 'TerminalOutput' | Out-Null
    Wait-UiValue -Selector 'TerminalCopyMenuItem' -Value 'True' -Property 'IsEnabled'
    Invoke-Ui 'invoke' 'TerminalClearMenuItem' | Out-Null
    Wait-UiPropertyEmpty -Selector 'TerminalOutput' -Property 'Value'
    $clearedTerminal = Invoke-Ui 'get-property' 'TerminalOutput' '--property' 'Value' | ConvertFrom-Json
    if (-not [string]::IsNullOrEmpty([string]$clearedTerminal.properties.Value)) {
        throw 'Clearing the terminal did not remove its displayed output.'
    }

    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsShell' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'SettingsAppearanceNavItem' | Out-Null
    foreach ($keybindingSetting in @(
        'KeybindingSummaryText',
        'KeybindingCommandSelector',
        'KeybindingGestureInput',
        'KeybindingWhenInput',
        'SaveKeybindingButton',
        'ResetAllKeybindingsButton',
        'KeybindingValidationText',
        'KeybindingOverridesList'
    )) {
        Invoke-Ui 'wait-for' $keybindingSetting '--timeout' '5000' | Out-Null
    }
    Select-ComboBoxItem -ComboBox 'KeybindingCommandSelector' -ItemName 'Toggle Sidebar'
    Invoke-Ui 'set-value' 'KeybindingGestureInput' 'Ctrl+K' | Out-Null
    Invoke-Ui 'invoke' 'SaveKeybindingButton' | Out-Null
    Wait-UiPropertyContains -Selector 'KeybindingValidationText' `
        -Property 'Name' -Expected 'overlaps Show Command Palette'
    Invoke-Ui 'set-value' 'KeybindingGestureInput' 'Ctrl+Alt+B' | Out-Null
    Invoke-Ui 'invoke' 'SaveKeybindingButton' | Out-Null
    Wait-UiValue -Selector 'KeybindingValidationText' -Value 'Saved Ctrl+Alt+B for Toggle Sidebar.'
    Wait-UiPropertyContains -Selector 'KeybindingSummaryText' `
        -Property 'Name' -Expected '1 custom overrides'
    foreach ($terminalSetting in @(
        'SettingsTerminalAppearanceStatusText',
        'TerminalFontFamilySelector',
        'TerminalFontSizeSelector',
        'ResetTerminalAppearanceButton'
    )) {
        Invoke-Ui 'wait-for' $terminalSetting '--timeout' '5000' | Out-Null
    }
    Select-ComboBoxItem -ComboBox 'TerminalFontFamilySelector' -ItemName 'Consolas'
    Select-ComboBoxItem -ComboBox 'TerminalFontSizeSelector' -ItemName '14 px'
    Wait-UiValue -Selector 'SettingsTerminalAppearanceStatusText' -Value 'Consolas • 14 px'
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'terminal-font-settings.png') '--focus' |
        Out-Null
    Invoke-Ui 'invoke' 'CloseButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsShell' '--gone' '--timeout' '5000' | Out-Null
    Wait-UiValue -Selector 'TerminalOutput' `
        -Value ('Terminal output ' + [char]0x2014 + ' Terminal font Consolas at 14 pixels') -Property 'Name'

    Wait-UiValue -Selector 'TerminalPaneSummaryText' -Value 'Single pane'
    Invoke-Ui 'invoke' 'SplitTerminalRightButton' | Out-Null
    Invoke-Ui 'wait-for' 'SecondaryTerminalPane' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalOutputSecondary' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalPaneResizeHandle' '--timeout' '5000' | Out-Null
    Wait-UiValue -Selector 'TerminalPaneSummaryText' -Value 'Pane 2 of 2 • split right' -Timeout 15000
    Wait-UiValue -Selector 'TerminalOutputSecondary' `
        -Value ('Terminal output ' + [char]0x2014 + ' Terminal font Consolas at 14 pixels') -Property 'Name' -Timeout 15000
    Invoke-Ui 'set-value' 'TerminalInput' "Write-Output ('PANE2' + 'OK')" | Out-Null
    Invoke-Ui 'invoke' 'SendTerminalInputButton' | Out-Null
    Wait-UiPropertyContains -Selector 'TerminalOutputSecondary' `
        -Property 'Value' -Expected 'PANE2OK' -Timeout 15000
    $primaryBeforeFocus = Invoke-Ui 'get-property' 'TerminalOutput' '--property' 'Value' | ConvertFrom-Json
    if ([string]$primaryBeforeFocus.properties.Value -match 'PANE2OK') {
        throw 'The secondary terminal output was incorrectly rendered in the primary pane.'
    }

    Invoke-Ui 'invoke' 'SplitTerminalDownButton' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalPane3' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalOutput3' '--timeout' '15000' | Out-Null
    Wait-UiValue -Selector 'TerminalPaneSummaryText' -Value 'Pane 3 of 3 • nested splits' -Timeout 15000
    Wait-UiValue -Selector 'TerminalOutput3' `
        -Value ('Terminal output ' + [char]0x2014 + ' Terminal font Consolas at 14 pixels') -Property 'Name' -Timeout 15000
    Invoke-Ui 'set-value' 'TerminalInput' "Write-Output ('PANE3' + 'OK')" | Out-Null
    Wait-UiValue -Selector 'SendTerminalInputButton' -Value 'True' -Property 'IsEnabled' -Timeout 15000
    Invoke-Ui 'invoke' 'SendTerminalInputButton' | Out-Null
    Invoke-Ui 'invoke' 'TerminalOutput3' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalFindMenuItem' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'TerminalFindMenuItem' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalSearchTextBox' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'TerminalSearchTextBox' 'PANE3OK' | Out-Null
    Wait-TerminalSearchResults -Timeout 15000
    Invoke-Ui 'invoke' 'TerminalSearchCloseButton' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalSearchOverlay' '--gone' '--timeout' '5000' | Out-Null
    foreach ($otherPane in @('TerminalOutput', 'TerminalOutputSecondary')) {
        $otherPaneOutput = Invoke-Ui 'get-property' $otherPane '--property' 'Value' | ConvertFrom-Json
        if ([string]$otherPaneOutput.properties.Value -match 'PANE3OK') {
            throw "The third terminal output was incorrectly rendered in '$otherPane'."
        }
    }

    Invoke-Ui 'invoke' 'SplitTerminalRightButton' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalPane4' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalOutput4' '--timeout' '15000' | Out-Null
    Wait-UiValue -Selector 'TerminalPaneSummaryText' -Value 'Pane 4 of 4 • nested splits' -Timeout 15000
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'terminal-split-nested-four.png') '--focus' |
        Out-Null

    Invoke-Ui 'invoke' 'CloseTerminalButton' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalPane4' '--gone' '--timeout' '15000' | Out-Null
    Wait-UiValue -Selector 'TerminalPaneSummaryText' -Value 'Pane 3 of 3 • nested splits' -Timeout 15000
    Invoke-Ui 'invoke' 'CloseTerminalButton' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalPane3' '--gone' '--timeout' '15000' | Out-Null
    Wait-UiValue -Selector 'TerminalPaneSummaryText' -Value 'Pane 2 of 2 • split right' -Timeout 15000

    Invoke-Ui 'set-value' 'TerminalPaneResizeHandle' '65' | Out-Null
    Wait-UiValue -Selector 'TerminalPaneResizeHandle' `
        -Value '65 percent assigned to the left pane' -Property 'HelpText'

    Invoke-Ui 'focus' 'TerminalOutput' | Out-Null
    Wait-UiValue -Selector 'TerminalPaneSummaryText' -Value 'Pane 1 of 2 • split right'
    Invoke-Ui 'set-value' 'TerminalInput' "Write-Output ('PANE1' + 'OK')" | Out-Null
    Invoke-Ui 'invoke' 'SendTerminalInputButton' | Out-Null
    Wait-UiPropertyContains -Selector 'TerminalOutput' `
        -Property 'Value' -Expected 'PANE1OK' -Timeout 15000
    $secondaryAfterPrimary = Invoke-Ui 'get-property' 'TerminalOutputSecondary' '--property' 'Value' | ConvertFrom-Json
    if ([string]$secondaryAfterPrimary.properties.Value -match 'PANE1OK') {
        throw 'The primary terminal output was incorrectly rendered in the secondary pane.'
    }

    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'terminal-split-right.png') '--focus' |
        Out-Null
    Invoke-Ui 'invoke' 'CloseTerminalPaneButton' | Out-Null
    Wait-UiValue -Selector 'TerminalPaneSummaryText' -Value 'Single pane'
    Invoke-Ui 'wait-for' 'SecondaryTerminalPane' '--gone' '--timeout' '5000' | Out-Null

    Invoke-Ui 'invoke' 'SplitTerminalDownButton' | Out-Null
    Invoke-Ui 'wait-for' 'SecondaryTerminalPane' '--timeout' '15000' | Out-Null
    Wait-UiValue -Selector 'TerminalPaneSummaryText' -Value 'Pane 2 of 2 • split down' -Timeout 15000
    Wait-UiValue -Selector 'TerminalPaneResizeHandle' `
        -Value '65 percent assigned to the top pane' -Property 'HelpText'
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'terminal-split-down.png') '--focus' |
        Out-Null
    Invoke-Ui 'invoke' 'CloseTerminalButton' | Out-Null
    Wait-UiValue -Selector 'TerminalPaneSummaryText' -Value 'Single pane' -Timeout 15000
    Invoke-Ui 'wait-for' 'SecondaryTerminalPane' '--gone' '--timeout' '15000' | Out-Null

    Invoke-Ui 'invoke' 'StopTerminalButton' | Out-Null
    Wait-UiPropertyContains -Selector 'TerminalSessionSummaryText' `
        -Property 'Name' -Expected 'exited' -Timeout 15000
    Invoke-Ui 'invoke' 'RestartTerminalButton' | Out-Null
    Wait-UiValue -Selector 'TerminalSessionSummaryText' -Value 'PowerShell • running' -Timeout 15000
    Invoke-Ui 'invoke' 'CloseTerminalButton' | Out-Null
    Wait-UiValue -Selector 'TerminalStatusText' -Value 'No terminal sessions yet' -Timeout 15000

    Invoke-Ui 'invoke' 'NewTerminalButton' | Out-Null
    Wait-UiValue -Selector 'TerminalSessionSummaryText' -Value 'PowerShell • running' -Timeout 15000
    Invoke-Ui 'invoke' 'SplitTerminalDownButton' | Out-Null
    Wait-UiValue -Selector 'TerminalPaneSummaryText' -Value 'Pane 2 of 2 • split down' -Timeout 15000
    Invoke-Ui 'set-value' 'TerminalPaneResizeHandle' '60' | Out-Null
    Wait-UiValue -Selector 'TerminalPaneResizeHandle' `
        -Value '60 percent assigned to the top pane' -Property 'HelpText'
    Invoke-Ui 'invoke' 'SplitTerminalRightButton' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalPane3' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalOutput3' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalPaneResizeHandle2' '--timeout' '5000' | Out-Null
    Wait-UiValue -Selector 'TerminalPaneSummaryText' -Value 'Pane 3 of 3 • nested splits' -Timeout 15000
    Invoke-Ui 'set-value' 'TerminalPaneResizeHandle2' '55' | Out-Null
    Wait-UiValue -Selector 'TerminalPaneResizeHandle2' `
        -Value '55 percent assigned to the left pane' -Property 'HelpText'
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'terminal-split-nested-persisted.png') '--focus' |
        Out-Null

    }
    Invoke-Ui 'invoke' 'PreviewPanelTab' | Out-Null
    foreach ($selector in @(
        'WorkbenchPreviewPanel',
        'PreviewBackButton',
        'PreviewForwardButton',
        'PreviewReloadStopButton',
        'PreviewAddressBox',
        'PreviewOpenExternalButton',
        'PreviewTabStrip',
        'AddPreviewTabButton',
        'PreviewRefreshServersButton',
        'PreviewServerList'
    )) {
        Invoke-Ui 'wait-for' $selector '--timeout' '10000' | Out-Null
    }
    $previewUrl = "http://127.0.0.1:$previewServerPort/"
    Invoke-Ui 'set-value' 'PreviewAddressBox' $previewUrl | Out-Null
    Invoke-Ui 'send-keys' 'enter' '--target' 'PreviewAddressBox' | Out-Null
    Wait-UiValue -Selector 'PreviewAddressBox' -Value $previewUrl -Timeout 15000
    Invoke-Ui 'wait-for' 'PreviewBrowserSurface' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'PreviewFailureOverlay' '--gone' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'PreviewLoadingProgress' '--gone' '--timeout' '15000' | Out-Null
    foreach ($selector in @(
        'PreviewViewportSelector',
        'RotatePreviewViewportButton',
        'PreviewAnnotateButton',
        'PreviewCaptureButton',
        'PreviewBrowserHost',
        'PreviewProfileSelector',
        'AddPreviewProfileButton',
        'SetDefaultPreviewProfileButton',
        'PreviewRecentUrlsButton',
        'PreviewColorSchemeSelector',
        'PreviewZoomOutButton',
        'PreviewZoomDescriptionText',
        'PreviewZoomInButton',
        'PreviewZoomResetButton',
        'PreviewDevToolsPolicyToggle',
        'PreviewOpenDevToolsButton',
        'PreviewImportCookiesButton',
        'PreviewRecordingButton',
        'PreviewPictureInPictureButton',
        'PreviewAutomationPermissionSelector'
    )) {
        Invoke-Ui 'wait-for' $selector '--timeout' '10000' | Out-Null
    }
    Select-ComboBoxItem -ComboBox 'PreviewViewportSelector' -ItemName 'Tablet 768 × 1024'
    Wait-UiValue -Selector 'PreviewViewportDescriptionText' -Value 'Tablet • 768 × 1024'
    Wait-UiValue -Selector 'PreviewCaptureButton' -Value 'True' -Property 'IsEnabled' -Timeout 15000
    Invoke-Ui 'invoke' 'PreviewCaptureButton' | Out-Null
    Wait-UiPropertyContains -Selector 'PreviewCaptureStatusText' `
        -Property 'Name' -Expected 'Screenshot saved' -Timeout 15000
    $captureStatus = Invoke-Ui 'get-property' 'PreviewCaptureStatusText' '--property' 'Name' | ConvertFrom-Json
    $capturePath = ([string]$captureStatus.properties.Name -split ' • ', 2)[1]
    if ([string]::IsNullOrWhiteSpace($capturePath) -or -not (Test-Path -LiteralPath $capturePath -PathType Leaf)) {
        throw "The preview screenshot was not saved at '$capturePath'."
    }
    Invoke-Ui 'invoke' 'AddPreviewTabButton' | Out-Null
    Wait-UiPropertyEmpty -Selector 'PreviewAddressBox' -Property 'Value'
    $secondPreviewUrl = "http://127.0.0.1:$previewServerPort/second"
    Invoke-Ui 'set-value' 'PreviewAddressBox' $secondPreviewUrl | Out-Null
    Invoke-Ui 'send-keys' 'enter' '--target' 'PreviewAddressBox' | Out-Null
    Wait-UiValue -Selector 'PreviewAddressBox' -Value $secondPreviewUrl -Timeout 15000
    Invoke-Ui 'wait-for' 'PreviewLoadingProgress' '--gone' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'PreviewFailureOverlay' '--gone' '--timeout' '15000' | Out-Null
    Invoke-Ui 'invoke' 'PreviewZoomInButton' | Out-Null
    Wait-UiValue -Selector 'PreviewZoomDescriptionText' -Value '110%'
    Select-ComboBoxItem -ComboBox 'PreviewColorSchemeSelector' -ItemName 'Dark'
    Invoke-Ui 'invoke' 'PreviewDevToolsPolicyToggle' | Out-Null
    Wait-UiValue -Selector 'PreviewOpenDevToolsButton' -Value 'True' -Property 'IsEnabled'
    Select-ComboBoxItem -ComboBox 'PreviewAutomationPermissionSelector' -ItemName 'Agent inspect only'
    Invoke-Ui 'invoke' 'PreviewRecordingButton' | Out-Null
    Wait-UiPropertyContains -Selector 'PreviewCaptureStatusText' `
        -Property 'Name' -Expected 'Recording preview' -Timeout 15000
    Start-Sleep -Milliseconds 1500
    Invoke-Ui 'invoke' 'PreviewRecordingButton' | Out-Null
    Wait-UiPropertyContains -Selector 'PreviewCaptureStatusText' `
        -Property 'Name' -Expected 'Recording saved' -Timeout 30000
    $recordingStatus = Invoke-Ui 'get-property' 'PreviewCaptureStatusText' '--property' 'Name' |
        ConvertFrom-Json
    $recordingPath = ([string]$recordingStatus.properties.Name -split ' • ', 2)[1]
    if ([string]::IsNullOrWhiteSpace($recordingPath) -or
        -not (Test-Path -LiteralPath $recordingPath -PathType Leaf)) {
        throw "The preview recording was not saved at '$recordingPath'."
    }
    Invoke-Ui 'invoke' 'PreviewPictureInPictureButton' | Out-Null
    Wait-UiPropertyContains -Selector 'PreviewCaptureStatusText' `
        -Property 'Name' -Expected 'Picture in picture opened' -Timeout 20000
    Invoke-Ui 'invoke' 'PreviewPictureInPictureButton' | Out-Null
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'preview-workbench.png') '--focus' |
        Out-Null
    & (Join-Path $PSScriptRoot 'Assert-ScreenshotPalette.ps1') -Path (Join-Path $runRoot 'preview-workbench.png') `
        -Color '151515' -Left 0.2 -Right 0.65 -Top 0.15 -Bottom 0.8 -MinimumSamples 10000 | Out-Null
    # The fixture has unstyled HTML: the browser must retain its white page
    # backing so default black text stays readable inside the dark app chrome.
    & (Join-Path $PSScriptRoot 'Assert-ScreenshotPalette.ps1') -Path (Join-Path $runRoot 'preview-workbench.png') `
        -Color 'FFFFFF' -Left 0.72 -Top 0.3 -Bottom 0.9 -MinimumSamples 10000 | Out-Null

    if ($Scope -eq 'Visual') {
        Write-Output "Visual workbench slice passed for Pi Station Desktop (PID $launchedProcessId)."
        Write-Output "Artifacts: $runRoot"
        return
    }

    Invoke-Ui 'invoke' 'AgentsPanelTab' | Out-Null
    Wait-UiValue -Selector 'AgentEmptyStateText' -Value 'Subagents and workflows started by Pi will appear here with live status, usage, and results.'

    $resizedWidth = 480
    Invoke-Ui 'set-value' 'RightPanelResizeHandle' "$resizedWidth" | Out-Null
    Wait-UiValue -Selector 'RightPanelResizeHandle' `
        -Value "Workbench width $resizedWidth pixels" -Property 'HelpText'

    Set-TestWindowSize -Width 800 -Height 800
    Wait-ForWindowWidth -Threshold 900 -Direction 'Below'
    $sidebarBounds = Get-UiBounds -Selector 'AppSidebar'
    $composerBounds = Get-UiBounds -Selector 'ComposerSurface'
    $panelBounds = Get-UiBounds -Selector 'AgentsWorkbenchSurface'
    if ($panelBounds.X -ge ($composerBounds.X + $composerBounds.Width)) {
        throw 'The narrow-window workbench was docked instead of overlaying the conversation.'
    }
    if ($panelBounds.X -lt ($sidebarBounds.X + $sidebarBounds.Width - 2)) {
        throw 'The narrow-window workbench overlaid the project sidebar.'
    }
    Wait-UiValue -Selector 'CloseRightPanelButton' -Value 'False' -Property 'IsOffscreen'
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'workbench-overlay.png') '--focus' |
        Out-Null
    Set-TestWindowSize -Width 1400 -Height 800
    Wait-ForWindowWidth -Threshold 1000 -Direction 'Above'

    Invoke-Ui 'invoke' 'CollapseSidebarButton' | Out-Null
    Invoke-Ui 'wait-for' 'ExpandSidebarButton' '--timeout' '5000' | Out-Null

    Stop-TestApp

    if (-not (Test-Path -LiteralPath $layoutSettingsPath -PathType Leaf)) {
        throw "The shell did not persist layout settings to '$layoutSettingsPath'."
    }

    $layoutSettings = Get-Content -LiteralPath $layoutSettingsPath -Raw | ConvertFrom-Json
    if (-not $layoutSettings.IsSidebarCollapsed -or -not $layoutSettings.IsRightPanelOpen) {
        throw 'The persisted layout did not retain the collapsed sidebar and open workbench.'
    }
    if ($layoutSettings.SelectedPanel -ne 'Agents') {
        throw "Expected persisted panel Agents, found '$($layoutSettings.SelectedPanel)'."
    }
    if ([int][Math]::Round([double]$layoutSettings.RightPanelWidth) -ne $resizedWidth) {
        throw "Expected persisted workbench width $resizedWidth, found $($layoutSettings.RightPanelWidth)."
    }
    if ($layoutSettings.TerminalFontFamily -ne 'Consolas' -or
        [int][Math]::Round([double]$layoutSettings.TerminalFontSize) -ne 14) {
        throw 'The persisted terminal appearance did not retain Consolas at 14 px.'
    }
    $commandKeybindings = @($layoutSettings.CommandKeybindings)
    if ($commandKeybindings.Count -ne 1 -or
        $commandKeybindings[0].CommandId -ne 'sidebar.toggle' -or
        $commandKeybindings[0].Gesture -ne 'Ctrl+Alt+B') {
        throw 'The shell did not persist the custom Toggle Sidebar shortcut.'
    }
    $previewWorkspaces = @($layoutSettings.PreviewWorkspaces.PSObject.Properties)
    if ($previewWorkspaces.Count -ne 1) {
        throw "Expected one persisted per-thread preview workspace, found $($previewWorkspaces.Count)."
    }
    $previewWorkspace = $previewWorkspaces[0].Value
    $previewTabs = @($previewWorkspace.Tabs)
    if ($previewTabs.Count -ne 2 -or
        [string]$previewTabs[-1].Url -ne $secondPreviewUrl -or
        [string]$previewWorkspace.ActiveTabId -ne [string]$previewTabs[-1].TabId -or
        @($previewTabs | Where-Object { $_.ViewportPreset -eq 'Tablet' }).Count -ne 1 -or
        [Math]::Abs([double]$previewTabs[-1].ZoomFactor - 1.1) -gt 0.001 -or
        [string]$previewTabs[-1].ColorScheme -ne 'Dark' -or
        @($previewWorkspace.RecentUrls).Count -ne 2) {
        throw 'The persisted preview workspace did not retain tabs, viewport, zoom, color, and recent addresses.'
    }
    if ([string]$layoutSettings.PreviewDevToolsPolicy -ne 'UserInitiated') {
        throw 'The shell did not persist the explicit DevTools policy.'
    }
    $previewPermissions = @($layoutSettings.PreviewAutomationPermissions.PSObject.Properties)
    if ($previewPermissions.Count -ne 1 -or [string]$previewPermissions[0].Value -ne 'Inspect') {
        throw 'The shell did not persist the thread-scoped inspect-only browser permission.'
    }
    $terminalPaneLayouts = @($layoutSettings.TerminalPaneLayouts.PSObject.Properties)
    if ($terminalPaneLayouts.Count -ne 1) {
        throw "Expected one persisted per-project terminal layout, found $($terminalPaneLayouts.Count)."
    }
    $terminalPaneLayout = $terminalPaneLayouts[0].Value
    if (-not $terminalPaneLayout.IsSplit -or
        $terminalPaneLayout.SplitOrientation -ne 'Down' -or
        [int][Math]::Round([double]$terminalPaneLayout.SplitRatio * 100) -ne 60 -or
        [int]$terminalPaneLayout.ActivePaneIndex -ne 2 -or
        [string]::IsNullOrWhiteSpace([string]$terminalPaneLayout.PrimarySessionId) -or
        [string]::IsNullOrWhiteSpace([string]$terminalPaneLayout.SecondarySessionId)) {
        throw 'The persisted terminal pane layout did not retain the down split, ratio, active pane, and sessions.'
    }
    if ($null -eq $terminalPaneLayout.Root -or
        (Get-TerminalPaneLeafCount -Node $terminalPaneLayout.Root) -ne 3 -or
        $terminalPaneLayout.Root.SplitOrientation -ne 'Down' -or
        [int][Math]::Round([double]$terminalPaneLayout.Root.SplitRatio * 100) -ne 60 -or
        $terminalPaneLayout.Root.Second.SplitOrientation -ne 'Right' -or
        [int][Math]::Round([double]$terminalPaneLayout.Root.Second.SplitRatio * 100) -ne 55) {
        throw 'The persisted terminal pane tree did not retain all three panes and their nested split ratios.'
    }

    Start-TestApp
    Invoke-Ui 'wait-for' 'AppMainWindow' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'ExpandSidebarButton' '--timeout' '5000' | Out-Null
    Invoke-Ui 'wait-for' 'RightPanelHost' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'ExpandSidebarButton' | Out-Null
    Invoke-Ui 'wait-for' 'workbench-project' '--timeout' '15000' | Out-Null
    Invoke-Ui 'invoke' 'workbench-project' | Out-Null
    Wait-UiValue -Selector 'WorkspaceProjectStatusText' -Value 'workbench-project' -Timeout 15000
    Wait-UiValue -Selector 'AgentEmptyStateText' -Value 'Subagents and workflows started by Pi will appear here with live status, usage, and results.'
    Wait-UiValue -Selector 'RightPanelResizeHandle' -Value "Workbench width $resizedWidth pixels" -Property 'HelpText'

    Invoke-Ui 'invoke' 'TerminalPanelTab' | Out-Null
    Wait-UiValue -Selector 'TerminalStatusText' -Value 'No terminal sessions yet' -Timeout 15000
    Wait-UiValue -Selector 'TerminalPaneSummaryText' -Value 'Single pane'
    Invoke-Ui 'wait-for' 'SecondaryTerminalPane' '--gone' '--timeout' '5000' | Out-Null

    $tree = Invoke-Ui 'inspect' '--depth' '10'
    Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Value $tree -Encoding UTF8
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'workbench-open.png') '--focus' | Out-Null

    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsShell' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'SettingsAppearanceNavItem' | Out-Null
    Wait-UiPropertyContains -Selector 'KeybindingSummaryText' `
        -Property 'Name' -Expected '1 custom overrides'
    Invoke-Ui 'invoke' 'ResetAllKeybindingsButton' | Out-Null
    Wait-UiPropertyContains -Selector 'KeybindingSummaryText' `
        -Property 'Name' -Expected '0 custom overrides'
    Wait-UiValue -Selector 'KeybindingValidationText' -Value 'Restored all default shortcuts.'
    Wait-UiValue -Selector 'SettingsTerminalAppearanceStatusText' -Value 'Consolas • 14 px'
    Invoke-Ui 'invoke' 'ResetTerminalAppearanceButton' | Out-Null
    Wait-UiValue -Selector 'SettingsTerminalAppearanceStatusText' -Value 'Cascadia Mono • 12 px'
    Invoke-Ui 'invoke' 'ResetLayoutButton' | Out-Null
    Wait-UiValue -Selector 'SettingsLayoutSummaryText' `
        -Value 'Sidebar expanded • Workbench closed • 420 px'
    Invoke-Ui 'invoke' 'CloseButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsShell' '--gone' '--timeout' '5000' | Out-Null
    Invoke-Ui 'wait-for' 'CollapseSidebarButton' '--timeout' '5000' | Out-Null
    Invoke-Ui 'wait-for' 'RightPanelHost' '--gone' '--timeout' '5000' | Out-Null

    Write-Output "Workbench slice passed for Pi Station Desktop (PID $launchedProcessId)."
    Write-Output "Artifacts: $runRoot"
}
catch {
    $testError = $_
    if ($null -ne $launchedProcessId) {
        try {
            Invoke-Ui 'inspect' '--depth' '10' |
                Set-Content -LiteralPath (Join-Path $runRoot 'failure-ui-tree.json') -Encoding UTF8
            Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'failure.png') '--focus' | Out-Null
        }
        catch {
            # Preserve the original failure when diagnostic capture is unavailable.
        }
    }
}
finally {
    Stop-TestApp
    Stop-PreviewServer
    $manifest = [ordered]@{
        dataRoot = [System.IO.Path]::GetFullPath($dataRoot)
        layoutSettingsPath = $layoutSettingsPath
        databasePath = Join-Path $dataRoot 'host.db'
        sessionRoot = Join-Path $dataRoot 'sessions'
        logFile = $logFile
    }
    $manifest | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $runRoot 'run-manifest.json') -Encoding UTF8
}

if ($null -ne $testError) {
    throw $testError
}
