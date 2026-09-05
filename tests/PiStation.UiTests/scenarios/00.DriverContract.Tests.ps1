BeforeAll {
    $driverContract = Join-Path $PSScriptRoot '..\Invoke-DriverContract.ps1'
}

Describe 'Pi Station Desktop driver contract' {
    It 'launches the packaged shell and exposes stable Automation IDs' {
        { & $driverContract } | Should -Not -Throw
    }
}
