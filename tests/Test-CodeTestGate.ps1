[CmdletBinding()]
param([string] $ProbeLockRoot)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CodeTestGate.psm1') -Force
if ($ProbeLockRoot) {
    try { $probeLease = Open-PiStationTestGate -Root $ProbeLockRoot; $probeLease.Dispose(); exit 0 }
    catch { exit 42 }
}

if (Get-Command dotnet -CommandType Function -ErrorAction SilentlyContinue) { throw 'Run this contract in a fresh PowerShell process.' }
$fixtureRoot = Join-Path (Split-Path -Parent $PSScriptRoot) ('TestResults/code-gate-contract/' + [Guid]::NewGuid().ToString('N'))
$suiteNames = @('ClientRuntime', 'CommandSystem', 'Host', 'PiRpc', 'Protocol')
$originalProcessorCount = $env:DOTNET_PROCESSOR_COUNT
$checks = 0
function Assert-Contract([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}
function New-Fixture {
    $root = [IO.Directory]::CreateDirectory((Join-Path $fixtureRoot ([Guid]::NewGuid().ToString('N') + ' with spaces'))).FullName
    $entries = @()
    foreach ($suiteName in $suiteNames) {
        $name = "PiStation.$suiteName.Tests"
        $folder = [IO.Directory]::CreateDirectory((Join-Path $root "tests/$name")).FullName
        [IO.File]::WriteAllText((Join-Path $folder "$name.csproj"), '<Project />')
        $platform = if ($suiteName -eq 'CommandSystem') { 'x64' } else { 'Any CPU' }
        $entries += "<Project Path='tests/$name/$name.csproj'><Platform Project='$platform' /></Project>"
    }
    [IO.File]::WriteAllText((Join-Path $root 'PiStationDesktop.slnx'), ('<Solution>' + ($entries -join '') + '</Solution>'))
    [IO.File]::Copy((Join-Path $PSScriptRoot 'CodeTests.runsettings'), (Join-Path $root 'tests/CodeTests.runsettings'))
    $global:PiStationCodeGateProbe = @{ Calls = @(); Mode = 'pass'; Root = $root }
    return $root
}
function Read-Summary([string] $Root) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $Root 'TestResults') -Filter summary.json -Recurse)
    Assert-Contract ($files.Count -eq 1) 'Each invocation must produce one distinct summary.'
    return Get-Content -LiteralPath $files[0].FullName -Raw | ConvertFrom-Json
}
function Assert-Fails([scriptblock] $Action) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    Assert-Contract $failed 'The gate unexpectedly accepted a failed/incomplete run.'
}
function global:dotnet {
    $arguments = @($args)
    $probe = $global:PiStationCodeGateProbe
    $probe.Calls += ,$arguments
    $global:LASTEXITCODE = 0
    if ($arguments[0] -eq 'build') {
        if ($probe.Mode -eq 'build-failure') { $global:LASTEXITCODE = 7 }
        return
    }
    $directory = $arguments[[Array]::IndexOf($arguments, '--results-directory') + 1]
    $reportName = $arguments[[Array]::IndexOf($arguments, '--logger') + 1].Substring('trx;LogFileName='.Length)
    $total = 4; $passed = 3; $failed = 0; $outcome = 'Completed'
    if ($probe.Mode -eq 'missing') { return }
    if ($probe.Mode -eq 'exit-failure') { $global:LASTEXITCODE = 1 }
    if ($probe.Mode -eq 'test-failure') { $passed = 2; $failed = 1 }
    if ($probe.Mode -eq 'aborted') { $outcome = 'Aborted' }
    if ($probe.Mode -eq 'empty') { $total = 0; $passed = 0 }
    $skips = if ($total -gt 0) { '<UnitTestResult outcome="NotExecuted" />' } else { '' }
    [IO.File]::WriteAllText((Join-Path $directory $reportName),
        "<TestRun><Results>$skips</Results><ResultSummary outcome='$outcome'><Counters total='$total' passed='$passed' failed='$failed' /></ResultSummary></TestRun>")
}

try {
    $root = New-Fixture
    $lease = Open-PiStationTestGate -Root $root
    try {
        Assert-Fails { Open-PiStationTestGate -Root $root }
        & pwsh -NoProfile -File $PSCommandPath -ProbeLockRoot $root
        Assert-Contract ($LASTEXITCODE -eq 42) 'A second process bypassed the live checkout lease.'
    }
    finally { $lease.Dispose() }
    $replacement = Open-PiStationTestGate -Root $root
    $replacement.Dispose()
    Assert-Contract (Test-Path -LiteralPath (Join-Path $root 'TestResults/code-tests.lock')) 'The lock file must remain after release.'
    $checks++

    $root = New-Fixture
    Invoke-PiStationCodeTests -Root $root -NoBuild
    $summary = Read-Summary $root
    Assert-Contract ($summary.success -and $summary.suites.Count -eq 5) 'The gate did not include every test project.'
    Assert-Contract ($global:PiStationCodeGateProbe.Calls.Count -eq 5) 'The gate rebuilt or retried tests.'
    foreach ($call in $global:PiStationCodeGateProbe.Calls) {
        foreach ($argument in @('--no-build', '--no-restore', '--settings', 'DOTNET_PROCESSOR_COUNT=2', '3m', 'none')) {
            Assert-Contract ($argument -in $call) "Required test bound missing: $argument"
        }
        $platform = if ($call[1] -like '*CommandSystem*') { 'x64' } else { 'AnyCPU' }
        Assert-Contract ("-p:Platform=$platform" -in $call) 'Tests did not use the solution build platform (stale/missing binary risk).'
    }
    Assert-Contract ($summary.suites[0].skipped -eq 1 -and $summary.suites[0].passed -eq 3) 'Skipped tests were reported as executed.'
    Assert-Contract ($env:DOTNET_PROCESSOR_COUNT -eq $originalProcessorCount) 'The runner changed its parent process environment.'
    $checks++

    $root = New-Fixture
    Invoke-PiStationCodeTests -Root $root -NoBuild -Suite Host -Configuration Release -Filter 'Category!=RealPi' -HangTimeoutMinutes 15
    Assert-Contract ($global:PiStationCodeGateProbe.Calls.Count -eq 1) 'The suite filter selected the wrong projects.'
    Assert-Contract ('Release' -in $global:PiStationCodeGateProbe.Calls[0] -and 'Category!=RealPi' -in $global:PiStationCodeGateProbe.Calls[0]) 'Configuration/filter were not forwarded.'
    Assert-Contract ('15m' -in $global:PiStationCodeGateProbe.Calls[0] -and (Read-Summary $root).hangTimeoutMinutes -eq 15) 'The explicit opt-in hang cutoff was not recorded/forwarded.'
    $checks++

    $root = New-Fixture
    Invoke-PiStationCodeTests -Root $root -NoBuild -Suite 'Host, Protocol'
    Assert-Contract ($global:PiStationCodeGateProbe.Calls.Count -eq 2) 'Comma-separated CLI suites were not accepted.'
    $checks++

    $root = New-Fixture
    Invoke-PiStationCodeTests -Root $root
    Assert-Contract ($global:PiStationCodeGateProbe.Calls.Count -eq 6 -and $global:PiStationCodeGateProbe.Calls[0][0] -eq 'build') 'Build did not finish before the test commands.'
    $checks++

    foreach ($mode in @('exit-failure', 'test-failure', 'missing', 'aborted', 'empty', 'build-failure')) {
        $root = New-Fixture
        $global:PiStationCodeGateProbe.Mode = $mode
        Assert-Fails { Invoke-PiStationCodeTests -Root $root }
        $summary = Read-Summary $root
        Assert-Contract (-not $summary.success -and $summary.error -and $summary.finishedUtc) "Failure evidence lost for $mode."
        $expectedCalls = if ($mode -eq 'build-failure') { 1 } else { 6 }
        Assert-Contract ($global:PiStationCodeGateProbe.Calls.Count -eq $expectedCalls) "The gate retried or silently omitted suites in $mode."
        $checks++
    }

    $root = New-Fixture
    Assert-Fails { Invoke-PiStationCodeTests -Root $root -Suite DoesNotExist -NoBuild }
    Assert-Contract ($global:PiStationCodeGateProbe.Calls.Count -eq 0) 'Unknown suite names started a build/test.'
    $checks++

    $root = New-Fixture
    [IO.File]::WriteAllText((Join-Path $root 'PiStationDesktop.slnx'), '<Solution />')
    Assert-Fails { Invoke-PiStationCodeTests -Root $root -NoBuild }
    Assert-Contract ($global:PiStationCodeGateProbe.Calls.Count -eq 0) 'A test project outside the build manifest was accepted.'
    $checks++

    [xml] $settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'CodeTests.runsettings') -Raw
    Assert-Contract ($settings.RunSettings.RunConfiguration.MaxCpuCount -eq '1' -and $settings.RunSettings.xUnit.MaxParallelThreads -eq '2' -and $settings.RunSettings.xUnit.ParallelAlgorithm -eq 'conservative') 'Concurrency policy drifted.'
    $checks++
    Write-Host "Code-test runner contract passed: $checks checks. Fixtures retained at $fixtureRoot"
}
finally {
    Remove-Item -LiteralPath Function:/dotnet
    Remove-Variable -Name PiStationCodeGateProbe -Scope Global
}
