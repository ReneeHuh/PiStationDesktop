[CmdletBinding()]
param(
    [ValidateSet('Server', 'Desktop')][string] $Kind = 'Server',
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
    [Parameter(Mandatory)][version] $Version,
    [string] $CertificateThumbprint,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\remote-updates')
)
$ErrorActionPreference = 'Stop'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$runRoot = Join-Path $outputRoot ([guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
function Invoke-Checked([string] $Command, [string[]] $Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
}
if ($Version.Revision -lt 0) { throw 'Use a four-part version, for example 1.0.1.0.' }
if ($Kind -eq 'Server') {
    $runtime = Join-Path $runRoot 'host'
    Invoke-Checked 'dotnet' @('publish', (Join-Path $PSScriptRoot 'src\PiStation.Server\PiStation.Server.csproj'), '-c', $Configuration,
        '-r', 'win-x64', '--self-contained', 'true', '-p:PublishTrimmed=false', "-p:Version=$Version", "-p:AssemblyVersion=$Version", '-o', $runtime)
    $manifest = & (Join-Path $runtime 'PiStation.Server.exe') update-manifest
    if ($LASTEXITCODE -ne 0) { throw 'The published host could not produce its update manifest.' }
    [IO.File]::WriteAllText((Join-Path $runtime 'pistation-update.json'), ($manifest -join [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    $artifact = Join-Path $runRoot "PiStation.Server-$Version-win-x64.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($runtime, $artifact)
} else {
    if ($CertificateThumbprint -notmatch '^[A-Fa-f0-9]{40}$') { throw 'Desktop updates require the installed publisher signing certificate thumbprint.' }
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
    if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) { throw 'The signing certificate must be valid and have a private key.' }
    [xml] $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\PiStation.App\Package.appxmanifest') -Raw
    if ($certificate.Subject -ne $manifest.Package.Identity.Publisher) { throw 'The certificate subject must match the app publisher.' }
    $manifest.Package.Identity.Version = $Version.ToString()
    $versionedManifest = Join-Path $runRoot 'Package.appxmanifest'
    $manifest.Save($versionedManifest)
    Invoke-Checked 'dotnet' @('publish', (Join-Path $PSScriptRoot 'src\PiStation.App\PiStation.App.csproj'), '-c', $Configuration, '-r', 'win-x64',
        '-p:PublishTrimmed=false', '-p:GenerateAppxPackageOnBuild=true', '-p:AppxBundle=Never', '-p:UapAppxPackageBuildMode=SideloadOnly',
        '-p:AppxPackageSigningEnabled=true', "-p:PackageCertificateThumbprint=$CertificateThumbprint", "-p:Version=$Version", "-p:AssemblyVersion=$Version",
        "-p:RemoteUpdateManifest=$versionedManifest", "-p:AppxPackageDir=$runRoot\")
    $packages = @(Get-ChildItem -LiteralPath $runRoot -Recurse -File -Filter '*.msix' | Where-Object { $_.FullName -notmatch '[\\/]Dependencies[\\/]' })
    if ($packages.Count -ne 1) { throw 'Expected one signed desktop MSIX artifact.' }
    $artifact = $packages[0].FullName
    $signature = Get-AuthenticodeSignature -LiteralPath $artifact
    if ($signature.Status -ne 'Valid') { throw 'Windows did not validate the generated package signature.' }
}
$digest = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash
[IO.File]::WriteAllText(($artifact + '.sha256'), $digest, [Text.UTF8Encoding]::new($false))
Write-Output "Update artifact: $artifact"
Write-Output "SHA256: $digest"
