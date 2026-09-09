Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Open-PiStationTestGate {
    param([Parameter(Mandatory)][string] $Root)
    if (-not (Test-Path -LiteralPath (Join-Path $Root 'PiStationDesktop.slnx'))) {
        throw 'The test gate must run in a PiStation solution checkout.'
    }
    $directory = [IO.Directory]::CreateDirectory((Join-Path $Root 'TestResults')).FullName
    try {
        # Keep the file: deleting a lock file can let a third runner bypass a live lease.
        # The OS releases this exclusive handle even if the owning runner crashes.
        return [IO.File]::Open((Join-Path $directory 'code-tests.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    }
    catch [IO.IOException] {
        throw 'Another PiStation code/PR gate holds this checkout, or its lock is inaccessible. Wait for that run to finish; do not delete the lock file.'
    }
}

function Invoke-PiStationCodeTests {
    param(
        [Parameter(Mandatory)][string] $Root,
        [ValidateSet('Debug', 'Release')][string] $Configuration = 'Debug',
        [switch] $NoBuild,
        [string[]] $Suite,
        [string] $Filter,
        [ValidateRange(1, 30)][int] $HangTimeoutMinutes = 3
    )
    $Suite = @($Suite | ForEach-Object { ($_ -split ',').Trim() })
    [xml] $solution = Get-Content -LiteralPath (Join-Path $Root 'PiStationDesktop.slnx') -Raw
    $projects = @(Get-ChildItem -LiteralPath (Join-Path $Root 'tests') -Directory |
        Where-Object Name -Like 'PiStation.*.Tests' | Sort-Object Name | ForEach-Object {
            $project = Join-Path $_.FullName ($_.Name + '.csproj')
            if (Test-Path -LiteralPath $project) {
                $mapping = @($solution.SelectNodes('//Project') | Where-Object {
                    [IO.Path]::GetFullPath((Join-Path $Root $_.GetAttribute('Path'))) -eq $project
                })
                if ($mapping.Count -ne 1 -or $null -eq $mapping[0].SelectSingleNode('Platform[@Project]')) {
                    throw "Add $project to the solution with an explicit project platform before running the gate."
                }
                $platform = $mapping[0].SelectSingleNode('Platform').GetAttribute('Project')
                if ($platform -eq 'Any CPU') { $platform = 'AnyCPU' }
                [pscustomobject]@{ FullName = $project; BaseName = $_.Name; Platform = $platform }
            }
        })
    if ($Suite) {
        foreach ($name in $Suite) {
            if ('PiStation.' + $name + '.Tests' -notin $projects.BaseName) { throw "Unknown test suite: $name" }
        }
        $projects = @($projects | Where-Object { ($_.BaseName -replace '^PiStation\.|\.Tests$', '') -in $Suite })
    }
    if ($projects.Count -eq 0) { throw 'No code test projects were found.' }
    $runName = 'code-{0}-{1}-{2}' -f $Configuration, [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $directory = [IO.Directory]::CreateDirectory((Join-Path $Root "TestResults/$runName")).FullName
    $summary = [ordered]@{
        configuration = $Configuration; startedUtc = [DateTime]::UtcNow.ToString('o'); finishedUtc = $null
        success = $false; filter = $Filter; testProcessorCount = 2; maxParallelCollections = 2; hangTimeoutMinutes = $HangTimeoutMinutes
        expectedSuites = @($projects.BaseName); suites = @(); error = $null
    }
    $summaryPath = Join-Path $directory 'summary.json'
    function Save-Summary { [IO.File]::WriteAllText($summaryPath, ($summary | ConvertTo-Json -Depth 6)) }
    Save-Summary
    Write-Host "Code-test evidence: $directory"
    try {
        if (-not $NoBuild) {
            & dotnet build (Join-Path $Root 'PiStationDesktop.slnx') --configuration $Configuration -maxcpucount:2
            if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE; no tests were run." }
        }
        foreach ($project in $projects) {
            $reportPath = Join-Path $directory ($project.BaseName + '.trx')
            $arguments = @('test', $project.FullName, '--configuration', $Configuration, ('-p:Platform=' + $project.Platform), '--no-build', '--no-restore',
                '--settings', (Join-Path $Root 'tests/CodeTests.runsettings'),
                '--environment', 'DOTNET_PROCESSOR_COUNT=2',
                '--logger', ('trx;LogFileName=' + $project.BaseName + '.trx'), '--results-directory', $directory,
                '--blame-hang-timeout', "${HangTimeoutMinutes}m", '--blame-hang-dump-type', 'none', '--verbosity', 'minimal')
            if ($Filter) { $arguments += @('--filter', $Filter) }
            Write-Host "Running $($project.BaseName) (one assembly, at most two test collections)."
            & dotnet @arguments
            $exitCode = $LASTEXITCODE
            $result = [ordered]@{ name = $project.BaseName; platform = $project.Platform; exitCode = $exitCode; success = $false; total = 0; passed = 0; failed = 0; skipped = 0; error = $null }
            try {
                [xml] $report = Get-Content -LiteralPath $reportPath -Raw
                $counters = $report.TestRun.ResultSummary.Counters
                $result.total = [int] $counters.total
                $result.passed = [int] $counters.passed
                $result.failed = [int] $counters.failed
                $result.skipped = @($report.TestRun.Results.UnitTestResult | Where-Object outcome -EQ 'NotExecuted').Count
                $result.success = $exitCode -eq 0 -and $result.passed -gt 0 -and $result.failed -eq 0 -and
                    $result.total -eq ($result.passed + $result.skipped) -and
                    $report.TestRun.ResultSummary.outcome -in @('Completed', 'Passed')
                if (-not $result.success) { $result.error = 'Test failure, aborted/incomplete run, or no executed tests. Inspect the TRX/sequence evidence.' }
            }
            catch { $result.error = 'Missing or unreadable TRX report: ' + $_.Exception.Message }
            $summary.suites += $result
            Save-Summary
        }
        if (@($summary.suites | Where-Object { -not $_.success }).Count -gt 0) {
            throw "Code-test gate failed. First-attempt reports were retained without automatic retries: $directory"
        }
        $summary.success = $true
        Write-Host "Code-test gate passed ($($projects.Count) suites). Skips remain explicit in summary.json."
    }
    catch { $summary.error = $_.Exception.Message; throw }
    finally { $summary.finishedUtc = [DateTime]::UtcNow.ToString('o'); Save-Summary }
}

Export-ModuleMember -Function Open-PiStationTestGate, Invoke-PiStationCodeTests
