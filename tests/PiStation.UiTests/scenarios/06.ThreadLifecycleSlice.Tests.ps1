BeforeAll {
    $threadLifecycleSlice = Join-Path $PSScriptRoot '..\Invoke-ThreadLifecycleSlice.ps1'
}

Describe 'Pi Station Desktop thread lifecycle slice' {
    It 'searches, renames, pins, archives, restores, and reloads a thread' {
        { & $threadLifecycleSlice } | Should -Not -Throw
    }
}
