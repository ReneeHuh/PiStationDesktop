[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Bootstrap-UiTestTools.ps1')

$configuration = New-PesterConfiguration
$configuration.Run.Path = Join-Path $PSScriptRoot 'scenarios'
$configuration.Output.Verbosity = 'Detailed'
$configuration.Run.Exit = $true
Invoke-Pester -Configuration $configuration
