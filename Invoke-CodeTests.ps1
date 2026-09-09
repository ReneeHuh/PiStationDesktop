[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Debug',
    [switch] $NoBuild,
    [string[]] $Suite,
    [string] $Filter,
    [ValidateRange(1, 30)][int] $HangTimeoutMinutes = 3
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'tests/CodeTestGate.psm1') -Force
$lease = Open-PiStationTestGate -Root $PSScriptRoot
Push-Location $PSScriptRoot
try {
    Invoke-PiStationCodeTests -Root $PSScriptRoot -Configuration $Configuration -NoBuild:$NoBuild -Suite $Suite -Filter $Filter -HangTimeoutMinutes $HangTimeoutMinutes
}
finally { Pop-Location; $lease.Dispose() }
