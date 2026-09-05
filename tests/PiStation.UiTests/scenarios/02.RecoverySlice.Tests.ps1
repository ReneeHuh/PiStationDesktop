BeforeAll {
    $recoverySlice = Join-Path $PSScriptRoot '..\Invoke-RecoverySlice.ps1'
}

Describe 'Pi Station Desktop recovery slice' {
    It 'distinguishes and recovers from a crashed Pi process' {
        { & $recoverySlice } | Should -Not -Throw
    }
}
