[CmdletBinding(SupportsShouldProcess)]
param(
    [switch] $Install
)

$ErrorActionPreference = 'Stop'
$versions = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tool-versions.json') -Raw | ConvertFrom-Json

$winapp = Get-Command winapp -ErrorAction SilentlyContinue
if ($null -eq $winapp) {
    throw 'winapp is missing. Install it with: winget install --id Microsoft.WinAppCli'
}

$winappOutput = (& winapp --version 2>&1 | Out-String)
$winappVersion = [regex]::Matches($winappOutput, '(?m)^\d+\.\d+\.\d+$') |
    Select-Object -Last 1 |
    ForEach-Object Value
if ($winappVersion -ne $versions.winappCli) {
    throw "Expected winapp $($versions.winappCli), found '$winappVersion'."
}

$pester = Get-Module -ListAvailable Pester |
    Where-Object Version -EQ ([version]$versions.pester) |
    Select-Object -First 1

if ($null -eq $pester -and $Install) {
    if ($PSCmdlet.ShouldProcess("CurrentUser PowerShell modules", "Install Pester $($versions.pester)")) {
        Install-Module Pester -RequiredVersion $versions.pester -Scope CurrentUser -Force
        $pester = Get-Module -ListAvailable Pester |
            Where-Object Version -EQ ([version]$versions.pester) |
            Select-Object -First 1
    }
}

if ($null -eq $pester) {
    throw "Pester $($versions.pester) is missing. Re-run with -Install to install it for CurrentUser."
}

Write-Output "winapp $winappVersion and Pester $($pester.Version) are ready."
