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
$runRoot = Join-Path $artifactRoot (Join-Path 'workbench-runs' ("{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N')))
$dataRoot = Join-Path $runRoot 'data'
$projectPath = Join-Path $dataRoot 'workbench-project'
$logFile = Join-Path $dataRoot 'app.jsonl'
$layoutSettingsPath = Join-Path $dataRoot 'layout-settings.json'
$launchedProcessId = $null
$previewServerJob = $null
$previewServerPort = $null
$testError = $null

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class PiStationWorkbenchWindow
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
                $client = $listener.AcceptTcpClient()
                try {
                    $stream = $client.GetStream()
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

function Set-TestWindowSize {
    param(
        [Parameter(Mandatory)][int] $Width,
        [Parameter(Mandatory)][int] $Height
    )

    $process = Get-Process -Id $script:launchedProcessId -ErrorAction Stop
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'The packaged app did not expose a main window handle.'
    }

    $noMoveAndShow = 0x0042
    $didResize = [PiStationWorkbenchWindow]::SetWindowPos(
        $process.MainWindowHandle,
        [IntPtr]::Zero,
        0,
        0,
        $Width,
        $Height,
        $noMoveAndShow)
    if (-not $didResize) {
        throw "Could not resize the packaged app window to ${Width}x${Height}."
    }
}

function Wait-ForWindowWidth {
    param(
        [Parameter(Mandatory)][double] $Threshold,
        [Parameter(Mandatory)][ValidateSet('Below', 'Above')][string] $Direction,
        [int] $Timeout = 5000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($Timeout)
    do {
        $bounds = Get-UiBounds -Selector 'AppMainWindow'
        if (($Direction -eq 'Below' -and $bounds.Width -lt $Threshold) -or
            ($Direction -eq 'Above' -and $bounds.Width -gt $Threshold)) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "The app window width did not move $Direction $Threshold pixels."
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
    '-C', $projectPath, 'add', 'README.md', 'src/WorkbenchPreview.cs'
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
    Invoke-Ui 'wait-for' 'Thread 1' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'ThreadEmptyState' '--timeout' '5000' | Out-Null

    Invoke-Ui 'set-value' 'PromptInput' 'Capture the workbench checkpoint' | Out-Null
    Invoke-Ui 'invoke' 'SendPromptButton' | Out-Null
    Wait-UiValue -Selector 'LatestAssistantMessage' -Value 'Hello from Fake Pi 👽'
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
    Invoke-Ui 'set-value' 'CommandPaletteQuery' 'Hello from Fake Pi 👽' | Out-Null
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
        'WorkbenchDiffStatusText'
    )) {
        Invoke-Ui 'wait-for' $selector '--timeout' '5000' | Out-Null
    }

    Wait-UiValue -Selector 'GitBranchNameText' -Value 'main'
    Wait-UiValue -Selector 'GitBranchDetailText' -Value 'Local branch'
    Wait-UiValue -Selector 'WorkbenchChangesStatusText' -Value '3 changed files'
    Wait-UiValue -Selector 'WorkspaceSourceControlStatusText' -Value 'main • 3 changes • +3 −1'
    foreach ($changedFile in @('README.md', 'Staged.cs', 'notes.txt')) {
        Invoke-Ui 'wait-for' $changedFile '--timeout' '5000' | Out-Null
    }
    foreach ($changeArea in @('WORKTREE', 'STAGED', 'UNTRACKED')) {
        Invoke-Ui 'wait-for' $changeArea '--timeout' '5000' | Out-Null
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
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'changes-workbench.png') '--focus' |
        Out-Null
    Invoke-Ui 'invoke' 'RefreshChangesButton' | Out-Null
    Wait-UiValue -Selector 'WorkbenchChangesStatusText' -Value '3 changed files'

    Invoke-Ui 'invoke' 'FilesPanelTab' | Out-Null
    foreach ($selector in @(
        'FileWorkbenchSurface',
        'WorkbenchFileSearchInput',
        'WorkbenchFileSearchMode',
        'RefreshWorkbenchFilesButton',
        'WorkspaceFileTree',
        'WorkbenchFileStatusText',
        'WorkbenchFileTabs'
    )) {
        Invoke-Ui 'wait-for' $selector '--timeout' '5000' | Out-Null
    }
    Wait-UiValue -Selector 'WorkbenchFileStatusText' -Value '4 files • 1 folder'
    Invoke-Ui 'wait-for' 'WorkbenchPreview.cs' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'WorkbenchPreview.cs' | Out-Null
    Wait-UiValue -Selector 'WorkbenchFilePreviewPathText' -Value 'src/WorkbenchPreview.cs'
    Wait-UiValue -Selector 'WorkbenchFileEditor' -Value 'public static class WorkbenchPreview { }'

    Invoke-Ui 'set-value' 'WorkbenchFileSearchInput' 'readme' | Out-Null
    Wait-UiValue -Selector 'WorkbenchFileStatusText' -Value '1 project file'
    Invoke-Ui 'wait-for' 'README.md' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'README.md' | Out-Null
    Wait-UiValue -Selector 'WorkbenchFilePreviewPathText' -Value 'README.md'
    Wait-UiValue -Selector 'WorkbenchFileEditor' -Value 'Pi Station workbench file preview (modified)'
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'files-workbench.png') '--focus' |
        Out-Null
    Invoke-Ui 'invoke' 'RefreshWorkbenchFilesButton' | Out-Null
    Wait-UiValue -Selector 'WorkbenchFileStatusText' -Value '1 project file'

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
    Invoke-Ui 'invoke' 'TerminalSearchNextButton' | Out-Null
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
        -Value 'Terminal output — Terminal font Consolas at 14 pixels' -Property 'Name'

    Wait-UiValue -Selector 'TerminalPaneSummaryText' -Value 'Single pane'
    Invoke-Ui 'invoke' 'SplitTerminalRightButton' | Out-Null
    Invoke-Ui 'wait-for' 'SecondaryTerminalPane' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalOutputSecondary' '--timeout' '15000' | Out-Null
    Invoke-Ui 'wait-for' 'TerminalPaneResizeHandle' '--timeout' '5000' | Out-Null
    Wait-UiValue -Selector 'TerminalPaneSummaryText' -Value 'Pane 2 of 2 • split right' -Timeout 15000
    Wait-UiValue -Selector 'TerminalOutputSecondary' `
        -Value 'Terminal output — Terminal font Consolas at 14 pixels' -Property 'Name' -Timeout 15000
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
        -Value 'Terminal output — Terminal font Consolas at 14 pixels' -Property 'Name' -Timeout 15000
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
        'PreviewBrowserHost'
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
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'preview-workbench.png') '--focus' |
        Out-Null

    Invoke-Ui 'invoke' 'AgentsPanelTab' | Out-Null
    Wait-UiValue -Selector 'WorkbenchEmptyStateText' -Value 'Agent observability is not connected yet.'

    $resizedWidth = 480
    Invoke-Ui 'set-value' 'RightPanelResizeHandle' "$resizedWidth" | Out-Null
    Wait-UiValue -Selector 'RightPanelResizeHandle' `
        -Value "Workbench width $resizedWidth pixels" -Property 'HelpText'

    Set-TestWindowSize -Width 800 -Height 800
    Wait-ForWindowWidth -Threshold 900 -Direction 'Below'
    $sidebarBounds = Get-UiBounds -Selector 'AppSidebar'
    $composerBounds = Get-UiBounds -Selector 'ComposerSurface'
    $panelBounds = Get-UiBounds -Selector 'RightPanelHost'
    if ($panelBounds.X -ge ($composerBounds.X + $composerBounds.Width)) {
        throw 'The narrow-window workbench was docked instead of overlaying the conversation.'
    }
    if ($panelBounds.X -lt ($sidebarBounds.X + $sidebarBounds.Width - 2)) {
        throw 'The narrow-window workbench overlaid the project sidebar.'
    }
    Wait-UiValue -Selector 'CloseRightPanelButton' -Value 'False' -Property 'IsOffscreen'
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'workbench-overlay.png') '--focus' |
        Out-Null
    Set-TestWindowSize -Width 1200 -Height 800
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
        @($previewTabs | Where-Object { $_.ViewportPreset -eq 'Tablet' }).Count -ne 1) {
        throw 'The persisted preview workspace did not retain both tabs, the active tab, and device viewport.'
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
    Wait-UiValue -Selector 'WorkbenchEmptyStateText' -Value 'Agent observability is not connected yet.'
    Wait-UiValue -Selector 'RightPanelResizeHandle' -Value "Workbench width $resizedWidth pixels" -Property 'HelpText'

    Invoke-Ui 'invoke' 'TerminalPanelTab' | Out-Null
    Wait-UiValue -Selector 'TerminalStatusText' -Value 'No terminal sessions yet' -Timeout 15000
    Wait-UiValue -Selector 'TerminalPaneSummaryText' -Value 'Single pane'
    Invoke-Ui 'wait-for' 'SecondaryTerminalPane' '--gone' '--timeout' '5000' | Out-Null

    $tree = Invoke-Ui 'inspect' '--depth' '10'
    Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Value $tree -Encoding utf8NoBOM
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' (Join-Path $runRoot 'workbench-open.png') '--focus' | Out-Null

    Invoke-Ui 'invoke' 'SettingsButton' | Out-Null
    Invoke-Ui 'wait-for' 'SettingsShell' '--timeout' '5000' | Out-Null
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
                Set-Content -LiteralPath (Join-Path $runRoot 'failure-ui-tree.json') -Encoding utf8NoBOM
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
        Set-Content -LiteralPath (Join-Path $runRoot 'run-manifest.json') -Encoding utf8NoBOM
}

if ($null -ne $testError) {
    throw $testError
}
