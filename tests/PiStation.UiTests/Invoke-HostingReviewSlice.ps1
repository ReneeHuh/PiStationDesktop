[CmdletBinding()]
param([switch] $NoBuild, [ValidateSet(100, 150, 200)][int] $TextScalePercent = 100)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runRoot = Join-Path $repositoryRoot ('TestResults/hosting-native/' + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $runRoot 'data'
$fixturePath = Join-Path $dataRoot 'hosting-fixture.json'
$appProject = Join-Path $repositoryRoot 'src/PiStation.App/PiStation.App.csproj'
$fakeProject = Join-Path $repositoryRoot 'tests/PiStation.FakePi/PiStation.FakePi.csproj'
$fakePi = Join-Path $repositoryRoot 'tests/PiStation.FakePi/bin/Debug/net10.0/PiStation.FakePi.exe'
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
    Invoke-CheckedNative 'winapp' (@('ui') + $Arguments + @('--app', "$script:launchedProcessId", '--json'))
}
function Start-TestApp {
    $launch = Invoke-CheckedNative 'winapp' @('run', $appProject, '--configuration', 'Debug', '--arch', 'x64',
        '--property', 'Platform=x64', '--no-build', '--no-restore', '--detach', '--json', '--', '--ui-test',
        '--data-root', $dataRoot, '--pi-executable', $fakePi, '--fake-pi-scenario', 'normal',
        '--ui-test-hosting-fixture', $fixturePath, '--ui-test-text-scale', "$TextScalePercent",
        '--log-file', (Join-Path $runRoot 'app.jsonl')) | ConvertFrom-Json
    $script:launchedProcessId = [int]$launch.ProcessId
    Invoke-Ui 'wait-for' 'ConnectionStatusText' '--value' 'Local • Ready' '--timeout' '20000' | Out-Null
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
function Read-Nodes {
    param([string] $Selector)
    $tree = Invoke-Ui 'inspect' $Selector '--depth' '20' | ConvertFrom-Json -Depth 100
    $seen = [Collections.Generic.HashSet[string]]::new()
    @($tree.windows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object { $_.selector -and $seen.Add($_.selector) })
}
function Read-Element {
    param([string] $Selector)
    $nodes = @(Read-Nodes $Selector | Where-Object automationId -eq $Selector)
    if ($nodes.Count -ne 1) { throw "Expected one '$Selector' control, found $($nodes.Count)." }
    $nodes[0]
}
function Choose-Row {
    param([string] $Selector, [string] $Name)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $rows = @(Read-Nodes $Selector | Where-Object { $_.type -eq 'ListItem' -and ($_.name -eq $Name -or
            @(Get-TestThreadNodes $_ | Where-Object { $_.type -eq 'Text' -and $_.name -eq $Name }).Count -gt 0) })
        if ($rows.Count -eq 1) { break }
        if ($rows.Count -gt 1) { throw "More than one row matched '$Name'." }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($rows.Count -ne 1) { throw "Expected one '$Name' row in '$Selector', found $($rows.Count)." }
    Invoke-Ui 'invoke' $rows[0].selector | Out-Null
}
function Wait-Fixture {
    param([scriptblock] $Predicate)
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $fixture = $null
        try { $fixture = Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json -Depth 100 } catch {}
        if ($null -ne $fixture -and (& $Predicate $fixture)) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The expected change was not persisted by the native fixture.'
}
function Wait-Enabled {
    param([string] $Selector, [bool] $Enabled = $true)
    Invoke-Ui 'wait-for' $Selector '--property' 'IsEnabled' '--value' "$Enabled" '--timeout' '15000' | Out-Null
}
function Capture {
    param([string] $Name)
    $path = Join-Path $runRoot ($Name + '.png')
    Invoke-Ui 'screenshot' 'AppMainWindow' '--output' $path | Out-Null
    # Fail before any interactions when the desktop cannot produce usable pixels.
    & (Join-Path $PSScriptRoot 'Assert-ValidScreenshot.ps1') -Path $path
}
function Open-Review {
    param([string] $Title = 'Native GitHub review fixture')
    Invoke-Ui 'wait-for' 'GitHub UI fixture' '--timeout' '15000' | Out-Null
    Invoke-Ui 'invoke' 'GitHub UI fixture' | Out-Null
    if ((Read-Element 'ToggleWorkbenchButton').name -eq 'Open workbench') { Invoke-Ui 'invoke' 'ToggleWorkbenchButton' | Out-Null }
    Invoke-Ui 'invoke' 'HostingReviewButton' | Out-Null
    Invoke-Ui 'wait-for' 'PullRequestReviewList' '--timeout' '15000' | Out-Null
    Choose-Row 'PullRequestReviewList' $Title
    Invoke-Ui 'invoke' 'PullRequestManagementPanel' | Out-Null
    Invoke-Ui 'wait-for' 'PullRequestEditTitle' '--value' $Title '--timeout' '15000' | Out-Null
}
function Assert-ReviewLayouts {
    # This is used only after Capture has established a rendered desktop, and only on the process launched by this script.
    if (-not ('PiStationHostingWindowSizing' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PiStationHostingWindowSizing {
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    public static bool Resize(IntPtr window, int width, int height) {
        if (window == IntPtr.Zero) return false;
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previous == IntPtr.Zero) return false;
        try {
            var dpi = GetDpiForWindow(window);
            if (dpi == 0) return false;
            return SetWindowPos(window, IntPtr.Zero, 0, 0, (int)Math.Round(width * dpi / 96.0), (int)Math.Round(height * dpi / 96.0), 0x0016);
        } finally { SetThreadDpiAwarenessContext(previous); }
    }
}
'@
    }
    foreach ($size in @(@(720, 900), @(1000, 900), @(1440, 1000), @(1200, 800))) {
        $owned = Get-Process -Id $script:launchedProcessId -ErrorAction Stop
        if ($owned.ProcessName -ne 'PiStationDesktop' -or -not [PiStationHostingWindowSizing]::Resize($owned.MainWindowHandle, $size[0], $size[1])) {
            throw 'Could not resize the owned native fixture window.'
        }
        Invoke-Ui 'focus' 'PullRequestEditTitle' | Out-Null
        $window = Get-Bounds 'AppMainWindow'
        foreach ($selector in @('PullRequestEditTitle', 'PullRequestEditDescription')) {
            $bounds = Get-Bounds $selector
            if ($bounds[2] -le 0 -or $bounds[0] -lt $window[0] -or ($bounds[0] + $bounds[2]) -gt ($window[0] + $window[2] + 3)) {
                throw "The populated review clipped $selector at width $($size[0]) and text scale $TextScalePercent."
            }
        }
        Capture ("details-{0}-text-{1}" -f $size[0], $TextScalePercent)
    }
    $checks.Add('Populated review editors fit four window widths with nonblank screenshots')
}
function Get-Bounds {
    param([string] $Selector)
    $value = Invoke-Ui 'get-property' $Selector '--property' 'BoundingRectangle' | ConvertFrom-Json
    $parts = @(([string]$value.properties.BoundingRectangle) -split ',')
    if ($parts.Count -ne 4) { throw "Bounds unavailable for '$Selector'." }
    @($parts | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) })
}

try {
    if (-not $NoBuild) {
        Invoke-CheckedNative 'dotnet' @('build', $fakeProject, '-c', 'Debug') | Write-Host
        Invoke-CheckedNative 'dotnet' @('build', $appProject, '-c', 'Debug', '-p:Platform=x64') | Write-Host
    }
    Invoke-CheckedNative 'dotnet' @('run', '--project', (Join-Path $repositoryRoot 'tests/PiStation.HostingVerification/PiStation.HostingVerification.csproj'),
        '--', 'prepare-ui', $runRoot) | Write-Host
    Start-TestApp
    Capture 'initial-desktop'
    Open-Review
    Capture 'populated-details'
    Assert-ReviewLayouts
    Wait-Enabled 'PullRequestSaveDetails' $false
    Invoke-Ui 'set-value' 'PullRequestReviewSummaryInput' 'Keep this unsent native review.' | Out-Null
    Invoke-Ui 'set-value' 'PullRequestEditTitle' 'Verified native title' | Out-Null
    Invoke-Ui 'set-value' 'PullRequestEditDescription' 'Verified native description' | Out-Null
    Wait-Enabled 'PullRequestSaveDetails'
    Invoke-Ui 'invoke' 'PullRequestSaveDetails' | Out-Null
    Wait-Fixture { param($f) $f.data.repository.pullRequest.title -eq 'Verified native title' -and $f.data.repository.pullRequest.body -eq 'Verified native description' }
    Wait-Enabled 'PullRequestSaveDetails' $false
    Invoke-Ui 'wait-for' 'PullRequestReviewSummaryInput' '--value' 'Keep this unsent native review.' '--timeout' '10000' | Out-Null
    $checks.Add('Detail edits persist and preserve the unsent review; save is disabled when clean')
    foreach ($draft in @($true, $false)) {
        Wait-Enabled 'PullRequestToggleDraft'
        Invoke-Ui 'invoke' 'PullRequestToggleDraft' | Out-Null
        Wait-Fixture { param($f) $f.data.repository.pullRequest.isDraft -eq $draft }
    }
    $checks.Add('Draft and ready controls round-trip')
    Choose-Row 'PullRequestCommentToEdit' 'A comment'
    Invoke-Ui 'set-value' 'PullRequestEditComment' 'Verified native comment' | Out-Null
    Wait-Enabled 'PullRequestSaveComment'
    Invoke-Ui 'invoke' 'PullRequestSaveComment' | Out-Null
    Wait-Fixture { param($f) $f.data.repository.pullRequest.reviewThreads.nodes[0].comments.nodes[0].body -eq 'Verified native comment' }
    Choose-Row 'PullRequestCommentToEdit' 'Verified native comment'
    Wait-Enabled 'PullRequestDeleteComment'
    Invoke-Ui 'invoke' 'PullRequestDeleteComment' | Out-Null
    Invoke-Ui 'wait-for' 'Confirm deletion of the selected comment first.' '--timeout' '5000' | Out-Null
    Wait-Fixture { param($f) $f.data.repository.pullRequest.reviewThreads.nodes[0].comments.nodes.Count -eq 1 }
    Invoke-Ui 'invoke' 'PullRequestConfirmDeleteComment' | Out-Null
    Invoke-Ui 'invoke' 'PullRequestDeleteComment' | Out-Null
    Wait-Fixture { param($f) $f.data.repository.pullRequest.reviewThreads.nodes[0].comments.nodes.Count -eq 0 }
    $checks.Add('Comment edits persist and deletion requires confirmation')
    Invoke-Ui 'invoke' 'PullRequestAdvancedPanel' | Out-Null
    Wait-Enabled 'PullRequestRevert' $false
    foreach ($action in @('Enable', 'Disable')) {
        Wait-Enabled ('PullRequest' + $action + 'AutoMerge')
        Invoke-Ui 'invoke' ('PullRequest' + $action + 'AutoMerge') | Out-Null
        Wait-Fixture { param($f) ($null -ne $f.data.repository.pullRequest.autoMergeRequest) -eq ($action -eq 'Enable') }
    }
    $checks.Add('Automatic merge enables and disables; revert is disabled before merge')
    Wait-Enabled 'PullRequestReactionHeart'
    Invoke-Ui 'invoke' 'PullRequestReactionHeart' | Out-Null
    Wait-Fixture { param($f) -not $f.data.repository.pullRequest.reactionGroups[0].viewerHasReacted -and $f.data.repository.pullRequest.reactionGroups[0].reactors.totalCount -eq 2 }
    $checks.Add('Reaction toggle updates the saved count and viewer state')
    Invoke-Ui 'invoke' 'PullRequestLoadWorkflows' | Out-Null
    Invoke-Ui 'wait-for' 'CI from fork · Run #91 · Awaiting approval' '--timeout' '10000' | Out-Null
    Choose-Row 'PullRequestWorkflows' 'CI from fork · Run #91 · Awaiting approval'
    Invoke-Ui 'invoke' 'PullRequestApproveWorkflow' | Out-Null
    Invoke-Ui 'wait-for' 'Allow the selected fork workflow to run first.' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'PullRequestConfirmWorkflow' | Out-Null
    Invoke-Ui 'invoke' 'PullRequestApproveWorkflow' | Out-Null
    Wait-Fixture { param($f) $f.workflows[0].status -eq 'queued' }
    $checks.Add('Workflow approval requires confirmation and persists its result')
    Invoke-Ui 'focus' 'PullRequestMergeMethod' | Out-Null
    Capture 'populated-advanced'
    Wait-Enabled 'PullRequestUpdateBranch'
    Invoke-Ui 'invoke' 'PullRequestUpdateBranch' | Out-Null
    Wait-Fixture { param($f) $f.data.repository.pullRequest.headRefOid -eq ('c' * 40) }
    # Updating the head must disable writes until an explicit reload.
    Wait-Enabled 'PullRequestMerge' $false
    Invoke-Ui 'invoke' 'Reload review' | Out-Null
    Wait-Enabled 'PullRequestMerge'
    Invoke-Ui 'invoke' 'PullRequestMerge' | Out-Null
    Invoke-Ui 'wait-for' 'Confirm merging this pull request first.' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'PullRequestConfirmMerge' | Out-Null
    Invoke-Ui 'invoke' 'PullRequestMerge' | Out-Null
    Wait-Fixture { param($f) $f.data.repository.pullRequest.state -eq 'MERGED' }
    Wait-Enabled 'PullRequestRevert'
    Invoke-Ui 'invoke' 'PullRequestRevert' | Out-Null
    Invoke-Ui 'wait-for' 'Confirm creating a revert pull request first.' '--timeout' '5000' | Out-Null
    Invoke-Ui 'invoke' 'PullRequestConfirmRevert' | Out-Null
    Invoke-Ui 'invoke' 'PullRequestRevert' | Out-Null
    Invoke-Ui 'wait-for' 'Created revert pull request:' '--timeout' '10000' | Out-Null
    $checks.Add('Head changes disable writes until reload; merge and revert require confirmation')
    Invoke-Ui 'invoke' 'CloseButton' | Out-Null
    Invoke-Ui 'wait-for' 'PullRequestReviewList' '--gone' '--timeout' '5000' | Out-Null
    Stop-TestApp
    Start-TestApp
    Open-Review 'Verified native title'
    Invoke-Ui 'wait-for' 'PullRequestReviewSummaryInput' '--value' 'Keep this unsent native review.' '--timeout' '10000' | Out-Null
    Capture 'restored-review'
    $checks.Add('Saved review text survives closing the dialog and restarting the app')
    Invoke-Ui 'inspect' '--depth' '25' | Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Encoding utf8NoBOM
    $passed = $true
}
finally {
    Stop-TestApp
    if (Test-Path -LiteralPath $runRoot) {
        [ordered]@{ passed = $passed; checks = @($checks); textScalePercent = $TextScalePercent; dataRoot = $dataRoot } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'native-result.json') -Encoding utf8NoBOM
    }
    Write-Output "Hosting native artifacts: $runRoot"
}
