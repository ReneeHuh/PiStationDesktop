BeforeAll {
    $piIntegrationSlice = Join-Path $PSScriptRoot '../Invoke-PiIntegrationSlice.ps1'
}

Describe 'Pi Station Desktop extension integration' {
    It 'preserves separate drafts and runtime settings through extension updates and relaunch' {
        { & $piIntegrationSlice -Capture } | Should -Not -Throw
    }
}
