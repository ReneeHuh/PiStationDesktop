[CmdletBinding()]
param(
    [string] $RunRoot = (Join-Path ([IO.Path]::GetTempPath()) ('PiStation.HostingUi-' + [Guid]::NewGuid().ToString('N'))),
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runDirectory = [IO.Path]::GetFullPath($RunRoot)
$appProject = Join-Path $repositoryRoot 'src\PiStation.App\PiStation.App.csproj'
$fakeProject = Join-Path $repositoryRoot 'tests\PiStation.FakePi\PiStation.FakePi.csproj'
$verificationProject = Join-Path $repositoryRoot 'tests\PiStation.HostingVerification\PiStation.HostingVerification.csproj'
$fakeExecutable = Join-Path $repositoryRoot 'tests\PiStation.FakePi\bin\Debug\net10.0\PiStation.FakePi.exe'

if (-not (Get-Command winapp -ErrorAction SilentlyContinue)) {
    throw 'The winapp CLI is required to launch the packaged native fixture.'
}
if (-not $NoBuild) {
    & dotnet build $fakeProject --configuration Debug --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'FakePi build failed.' }
    & dotnet build $appProject --configuration Debug -p:Platform=x64 --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Native app build failed.' }
}
if (-not (Test-Path -LiteralPath $fakeExecutable -PathType Leaf)) {
    throw 'Build FakePi before launching with -NoBuild.'
}

# The preparer rejects a nonempty directory. No existing application data is reused.
& dotnet run --project $verificationProject --configuration Debug -- prepare-ui $runDirectory
if ($LASTEXITCODE -ne 0) { throw 'Hosting fixture preparation failed.' }
$dataRoot = Join-Path $runDirectory 'data'
$fixturePath = Join-Path $dataRoot 'hosting-fixture.json'
& winapp run $appProject --configuration Debug --arch x64 --property Platform=x64 --no-build --no-restore --detach --json -- --ui-test --data-root $dataRoot --pi-executable $fakeExecutable --fake-pi-scenario normal --ui-test-hosting-fixture $fixturePath --log-file (Join-Path $runDirectory 'app.jsonl')
if ($LASTEXITCODE -ne 0) { throw 'Native fixture launch failed.' }
Write-Host "Fixture launched. Select 'GitHub UI fixture', open the workbench and pull request review, then select PR #7. Run data: $runDirectory"
