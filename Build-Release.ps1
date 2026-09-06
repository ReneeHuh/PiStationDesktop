[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string] $Version = '1.0.0.0',
    [string] $Publisher = 'CN=AppPublisher',
    [string] $PublisherDisplayName = 'Pi Station Desktop (development)',
    [string] $UpdateFeedUrl,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\release')
)

$ErrorActionPreference = 'Stop'
function Invoke-ReleaseCommand([string] $Command, [string[]] $Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Release command failed: $Command (exit $LASTEXITCODE)" }
}
if ($UpdateFeedUrl -and ([uri]$UpdateFeedUrl).Scheme -ne 'https') { throw 'The update feed must use HTTPS.' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
# Each invocation owns a fresh staging directory; previous artifacts are preserved.
$runRoot = Join-Path $outputRoot ("{0}-{1}" -f $Version, [guid]::NewGuid().ToString('N'))
$layout = Join-Path $runRoot 'layout'
New-Item -ItemType Directory -Path $layout -Force | Out-Null
$project = Join-Path $PSScriptRoot 'src\PiStation.App\PiStation.App.csproj'
Invoke-ReleaseCommand 'dotnet' @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:WindowsAppSDKSelfContained=true', '-p:PublishTrimmed=false', '-p:Platform=x64', '-o', $layout)

[xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\PiStation.App\Package.appxmanifest') -Raw
$manifest.Package.Identity.Version = $Version
$manifest.Package.Identity.Publisher = $Publisher
$manifest.Package.Identity.SetAttribute('ProcessorArchitecture', 'x64')
$manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
$manifest.Package.Applications.Application.Executable = 'PiStationDesktop.exe'
$manifest.Package.Applications.Application.EntryPoint = 'Windows.FullTrustApplication'
$manifest.Package.Resources.Resource.Language = 'en-US'
$manifestPath = Join-Path $layout 'AppxManifest.xml'
$manifest.Save($manifestPath)
$packagePath = Join-Path $runRoot "PiStationDesktop_$($Version)_x64.msix"
Invoke-ReleaseCommand 'winapp' @('package', $layout, '--manifest', $manifestPath, '--exe', 'PiStationDesktop.exe', '--output', $packagePath)

if ($UpdateFeedUrl) {
    $feedUri = [uri]$UpdateFeedUrl
    $packageUri = [uri]::new($feedUri, [IO.Path]::GetFileName($packagePath)).AbsoluteUri
    $escapedPublisher = [Security.SecurityElement]::Escape($Publisher)
    $escapedFeed = [Security.SecurityElement]::Escape($feedUri.AbsoluteUri)
    $escapedPackage = [Security.SecurityElement]::Escape($packageUri)
    $identity = $manifest.Package.Identity.Name
    @"
<?xml version="1.0" encoding="utf-8"?>
<AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2018" Uri="$escapedFeed" Version="$Version">
  <MainPackage Name="$identity" Publisher="$escapedPublisher" Version="$Version" ProcessorArchitecture="x64" Uri="$escapedPackage" />
  <UpdateSettings><OnLaunch HoursBetweenUpdateChecks="24" ShowPrompt="true" /><AutomaticBackgroundTask /></UpdateSettings>
</AppInstaller>
"@ | Set-Content -LiteralPath (Join-Path $runRoot 'PiStationDesktop.appinstaller') -Encoding utf8
}
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
"$hash  $([IO.Path]::GetFileName($packagePath))" | Set-Content -LiteralPath (Join-Path $runRoot 'SHA256SUMS.txt')
Write-Output "Unsigned Release package: $packagePath"
Write-Output 'TODO before distribution: configure publisher identity, sign with a matching trusted certificate, publish the HTTPS feed, and validate install/upgrade on a second machine.'
