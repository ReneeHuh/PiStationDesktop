BeforeAll {
    $piAgentsSlice = Join-Path $PSScriptRoot '../Invoke-PiAgentsSlice.ps1'
}

Describe 'Pi Station Desktop agent workflows' {
    It 'configures presets, runs and stops children, and restores their transcripts' {
        { & $piAgentsSlice -Capture } | Should -Not -Throw
    }
}
