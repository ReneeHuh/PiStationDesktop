BeforeAll {
    $hardeningSlice = Join-Path $PSScriptRoot '..\Invoke-HardeningSlice.ps1'
}

Describe 'Pi Station Desktop hardening slice' {
    It 'stops isolated threads and recovers transport and uncertain dispatch states' {
        { & $hardeningSlice } | Should -Not -Throw
    }
}
