[CmdletBinding()]
param(
    [ValidateSet('Debug')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$solutionRoot = $PSScriptRoot
$solution = Join-Path $solutionRoot 'PiStationDesktop.slnx'
$uiTests = Join-Path $solutionRoot 'tests\PiStation.UiTests'
Import-Module (Join-Path $solutionRoot 'tests/CodeTestGate.psm1') -Force

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($ArgumentList -join ' ')"
    }
}

$lease = Open-PiStationTestGate -Root $solutionRoot
Push-Location $solutionRoot
try {
    Invoke-CheckedNative 'dotnet' @('restore', $solution)
    Invoke-CheckedNative 'dotnet' @('build', $solution, '--configuration', $Configuration, '--no-restore')
    & (Join-Path $uiTests 'Test-VisualContract.ps1')
    & (Join-Path $solutionRoot 'tests/Test-CodeTestGate.ps1')
    Invoke-PiStationCodeTests -Root $solutionRoot -Configuration $Configuration -NoBuild

    & (Join-Path $uiTests 'Invoke-DriverContract.ps1') -Configuration $Configuration -NoBuild
    & (Join-Path $uiTests 'Invoke-DraftSlice.ps1') -Configuration $Configuration -NoBuild
    & (Join-Path $uiTests 'Invoke-VerticalSlice.ps1') -Configuration $Configuration -NoBuild
    & (Join-Path $uiTests 'Invoke-RecoverySlice.ps1') -Configuration $Configuration -NoBuild
    & (Join-Path $uiTests 'Invoke-InteractionSlice.ps1') -Configuration $Configuration -NoBuild
    & (Join-Path $uiTests 'Invoke-PiConfigurationSlice.ps1') -Configuration $Configuration -NoBuild
    & (Join-Path $uiTests 'Invoke-PiResourcesSlice.ps1') -NoBuild -Capture
    & (Join-Path $uiTests 'Invoke-PiSessionsSlice.ps1') -NoBuild -Capture
    & (Join-Path $uiTests 'Invoke-TemporarySessionSlice.ps1') -NoBuild
    & (Join-Path $uiTests 'Invoke-ReliabilityDiagnosticsSlice.ps1') -NoBuild -Capture
    & (Join-Path $uiTests 'Invoke-PiPlanSlice.ps1') -NoBuild -Capture
    & (Join-Path $uiTests 'Invoke-PiAgentsSlice.ps1') -NoBuild -Capture
    & (Join-Path $uiTests 'Invoke-PiShellSlice.ps1') -NoBuild -Capture
    & (Join-Path $uiTests 'Invoke-ExternalPromptEditorSlice.ps1') -NoBuild -Capture
    & (Join-Path $uiTests 'Invoke-PiSessionNavigationSlice.ps1') -NoBuild -Capture
    & (Join-Path $uiTests 'Invoke-PiSessionLabelsSlice.ps1') -NoBuild -Capture
    & (Join-Path $uiTests 'Invoke-BrowserAutomationSlice.ps1')
    & (Join-Path $uiTests 'Invoke-ThreadLifecycleSlice.ps1') -Configuration $Configuration -NoBuild
    & (Join-Path $uiTests 'Invoke-InputAccessibilitySlice.ps1') -Configuration $Configuration -NoBuild
    & (Join-Path $uiTests 'Invoke-HardeningSlice.ps1') -Configuration $Configuration -NoBuild
    & (Join-Path $uiTests 'Invoke-WorkbenchSlice.ps1') -Configuration $Configuration -NoBuild
    & (Join-Path $uiTests 'Invoke-CompatibilitySlice.ps1') -Configuration $Configuration -NoBuild

    Write-Output 'Pi Station Desktop pull-request suite passed.'
}
finally {
    Pop-Location
    $lease.Dispose()
}
