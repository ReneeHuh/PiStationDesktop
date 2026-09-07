[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$solutionRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$solutionPath = Join-Path $solutionRoot 'PiStationDesktop.slnx'
$appProject = Join-Path $solutionRoot 'src\PiStation.App\PiStation.App.csproj'
$artifactRoot = Join-Path $PSScriptRoot 'artifacts'
$dataRoot = Join-Path $artifactRoot ("driver-data-{0}" -f [guid]::NewGuid().ToString('N'))
$versions = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tool-versions.json') -Raw | ConvertFrom-Json
$visualContract = Import-PowerShellDataFile -LiteralPath (Join-Path $PSScriptRoot 'visual-contract.psd1')
$launchedProcessId = $null

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [string[]] $ArgumentList
    )

    $output = & $FilePath @ArgumentList 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($ArgumentList -join ' ') failed with exit code $LASTEXITCODE.`n$($output | Out-String)"
    }

    return ($output | Out-String).Trim()
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null

try {
    $winappOutput = Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @('--version')
    $winappVersion = [regex]::Matches($winappOutput, '(?m)^\d+\.\d+\.\d+$') |
        Select-Object -Last 1 |
        ForEach-Object Value
    if ($winappVersion -ne $versions.winappCli) {
        throw "Expected winapp $($versions.winappCli), found '$winappVersion'."
    }

    if (-not $NoBuild) {
        Invoke-CheckedNative -FilePath 'dotnet' -ArgumentList @(
            'build', $solutionPath, '--configuration', $Configuration
        ) | Write-Host
    }

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
        '--data-root', $dataRoot
    )
    $launch = $launchJson | ConvertFrom-Json
    $launchedProcessId = [int]$launch.ProcessId

    $baselineSelectors = @(
        'AppMainWindow',
        'AppLoadingView',
        'AppSidebar',
        'ConnectionStatusText',
        'CollapseSidebarButton',
        'SettingsButton',
        'ProjectSelector',
        'NewProjectButton',
        'ThreadTabList',
        'ThreadSearchInput',
        'ArchivedThreadsToggle',
        'ThreadListStatusText',
        'NewThreadButton',
        'ActiveThreadView',
        'TranscriptList',
        'LatestAssistantMessage',
        'PromptInput',
        'AttachFilesButton',
        'DraftAttachmentList',
        'AttachmentNoticeText',
        'SendPromptButton',
        'StopTurnButton',
        'TurnStatusText'
        'AddActionButton'
        'OpenProjectButton'
        'ToggleWorkbenchButton'
        'WorkspaceStatusBar'
        'WorkspaceProjectStatusText'
        'WorkspaceSourceControlStatusText'
        'NoProjectEmptyState'
    )

    foreach ($selector in $baselineSelectors) {
        Write-Verbose "Waiting for $selector"
        Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
            'ui', 'wait-for', $selector,
            '--app', "$launchedProcessId",
            '--timeout', '10000',
            '--json'
        ) | Out-Null
    }

    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'wait-for', 'RightPanelHost', '--gone', '--app', "$launchedProcessId",
        '--timeout', '5000', '--json'
    ) | Out-Null

    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'invoke', 'SettingsButton', '--app', "$launchedProcessId", '--json'
    ) | Out-Null
    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'wait-for', 'SettingsShell', '--app', "$launchedProcessId",
        '--timeout', '5000', '--json'
    ) | Out-Null
    foreach ($selector in @(
        'PiThemeSelector',
        'SettingsThemeStatusText',
        'SettingsTerminalAppearanceStatusText',
        'TerminalFontFamilySelector',
        'TerminalFontSizeSelector',
        'ResetTerminalAppearanceButton',
        'SettingsLayoutSummaryText',
        'ResetLayoutButton',
        'SettingsConnectionStatusText',
        'SettingsProjectPathText',
        'RemoteConnectionsPanel',
        'StartRemoteSharingButton',
        'RemotePairingLabel',
        'RemotePairingLifetime',
        'RemotePairingInvitations',
        'RevokeRemotePairingLinkButton',
        'CopyRemotePairingLinkButton',
        'RemoteVerificationConfirmed',
        'ApproveRemoteDeviceButton',
        'RemoteIncomingLink',
        'SavedRemoteEnvironments',
        'RemoteConnectionStatus',
        'SshConnectionsPanel',
        'SshTarget',
        'SshServerPath',
        'SavedSshEnvironments',
        'AddSshConnectionButton',
        'CancelSshConnectionButton',
        'SshConnectionStatus'
    )) {
        Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
            'ui', 'wait-for', $selector, '--app', "$launchedProcessId",
            '--timeout', '5000', '--json'
        ) | Out-Null
    }
    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'wait-for', 'SimulateTransportDropButton', '--gone', '--app', "$launchedProcessId",
        '--timeout', '5000', '--json'
    ) | Out-Null
    foreach ($capture in @(
        @{ Selector = 'RemoteListenAddress'; FileName = 'remote-sharing-settings.png' },
        @{ Selector = 'RemoteIncomingLink'; FileName = 'remote-client-settings.png' },
        @{ Selector = 'SshTarget'; FileName = 'ssh-connections-settings.png' }
    )) {
        Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
            'ui', 'focus', $capture.Selector, '--app', "$launchedProcessId", '--json'
        ) | Out-Null
        Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
            'ui', 'wait-for', $capture.Selector, '--app', "$launchedProcessId",
            '--property', 'IsOffscreen', '--value', 'False', '--timeout', '5000', '--json'
        ) | Out-Null
        Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
            'ui', 'screenshot', 'AppMainWindow', '--app', "$launchedProcessId",
            '--output', (Join-Path $artifactRoot $capture.FileName), '--focus', '--json'
        ) | Out-Null
    }
    foreach ($selector in @('RemoteVerificationConfirmed', 'ApproveRemoteDeviceButton', 'RevokeRemotePairingLinkButton', 'CopyRemotePairingLinkButton', 'RevokeRemoteDeviceButton')) {
        Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
            'ui', 'wait-for', $selector, '--app', "$launchedProcessId",
            '--property', 'IsEnabled', '--value', 'False', '--timeout', '5000', '--json'
        ) | Out-Null
    }
    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'focus', 'PiThemeSelector', '--app', "$launchedProcessId", '--json'
    ) | Out-Null
    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'screenshot', 'AppMainWindow', '--app', "$launchedProcessId",
        '--output', (Join-Path $artifactRoot 'settings-open.png'),
        '--focus', '--json'
    ) | Out-Null
    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'invoke', 'CloseButton', '--app', "$launchedProcessId", '--json'
    ) | Out-Null
    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'wait-for', 'SettingsShell', '--gone', '--app', "$launchedProcessId",
        '--timeout', '5000', '--json'
    ) | Out-Null
    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'wait-for', 'SettingsButton', '--property', 'HasKeyboardFocus', '--value', 'True',
        '--app', "$launchedProcessId", '--timeout', '5000', '--json'
    ) | Out-Null

    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'invoke', 'CollapseSidebarButton', '--app', "$launchedProcessId", '--json'
    ) | Out-Null
    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'wait-for', 'ExpandSidebarButton', '--app', "$launchedProcessId",
        '--timeout', '5000', '--json'
    ) | Out-Null
    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'wait-for', 'ThreadSearchInput', '--gone', '--app', "$launchedProcessId",
        '--timeout', '5000', '--json'
    ) | Out-Null
    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'screenshot', 'AppMainWindow', '--app', "$launchedProcessId",
        '--output', (Join-Path $artifactRoot 'sidebar-collapsed.png'),
        '--focus', '--json'
    ) | Out-Null
    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'invoke', 'ExpandSidebarButton', '--app', "$launchedProcessId", '--json'
    ) | Out-Null
    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'wait-for', 'ThreadSearchInput', '--app', "$launchedProcessId",
        '--timeout', '5000', '--json'
    ) | Out-Null

    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'invoke', 'NewProjectButton', '--app', "$launchedProcessId", '--json'
    ) | Out-Null

    foreach ($selector in @('ProjectPathInput', 'AddProjectConfirmButton')) {
        Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
            'ui', 'wait-for', $selector,
            '--app', "$launchedProcessId",
            '--timeout', '5000',
            '--json'
        ) | Out-Null
    }

    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'set-value', 'ProjectPathInput', $solutionRoot,
        '--app', "$launchedProcessId", '--json'
    ) | Out-Null

    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'invoke', 'AddProjectConfirmButton', '--app', "$launchedProcessId", '--json'
    ) | Out-Null
    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'wait-for', 'ProjectPathInput', '--gone', '--app', "$launchedProcessId",
        '--timeout', '5000', '--json'
    ) | Out-Null
    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'wait-for', 'NoThreadEmptyState', '--app', "$launchedProcessId",
        '--timeout', '5000', '--json'
    ) | Out-Null

    $tree = Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'inspect', '--app', "$launchedProcessId", '--depth', '8', '--json'
    )
    foreach ($automationId in $visualContract.ForbiddenNormalLaunchAutomationIds) {
        if ($tree.Contains($automationId, [System.StringComparison]::Ordinal)) {
            throw "Test-only control '$automationId' was exposed in a normal packaged launch."
        }
    }
    Set-Content -LiteralPath (Join-Path $artifactRoot 'ui-tree.json') -Value $tree -Encoding utf8NoBOM

    Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'ui', 'screenshot', 'AppMainWindow', '--app', "$launchedProcessId",
        '--output', (Join-Path $artifactRoot 'driver-contract.png'),
        '--focus', '--json'
    ) | Out-Null

    Write-Output "Driver contract passed for Pi Station Desktop (PID $launchedProcessId)."
}
finally {
    if ($null -ne $launchedProcessId) {
        $ownedProcess = Get-Process -Id $launchedProcessId -ErrorAction SilentlyContinue
        if ($null -ne $ownedProcess -and $ownedProcess.ProcessName -eq 'PiStationDesktop') {
            Stop-Process -Id $launchedProcessId
            Wait-Process -Id $launchedProcessId -Timeout 10 -ErrorAction SilentlyContinue
        }
    }
}
