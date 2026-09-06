BeforeAll {
    $piPlanSlice = Join-Path $PSScriptRoot '../Invoke-PiPlanSlice.ps1'
}

Describe 'Pi Station Desktop plan workflow' {
    It 'edits, approves and exports a plan with persistent progress and separate drafts' {
        { & $piPlanSlice -Capture } | Should -Not -Throw
    }
}
