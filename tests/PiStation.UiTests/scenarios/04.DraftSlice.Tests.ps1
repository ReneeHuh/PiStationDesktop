BeforeAll {
    $draftSlice = Join-Path $PSScriptRoot '..\Invoke-DraftSlice.ps1'
}

Describe 'Pi Station Desktop draft slice' {
    It 'restores per-thread drafts after switching threads and relaunching the app' {
        { & $draftSlice } | Should -Not -Throw
    }
}
