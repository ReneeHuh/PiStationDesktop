BeforeAll {
    $piConfigurationSlice = Join-Path $PSScriptRoot '..\Invoke-PiConfigurationSlice.ps1'
}

Describe 'Pi Station Desktop Pi configuration slice' {
    It 'selects and restores capability-driven model and reasoning settings' {
        { & $piConfigurationSlice } | Should -Not -Throw
    }
}
