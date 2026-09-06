BeforeAll {
    $piSessionsSlice = Join-Path $PSScriptRoot '../Invoke-PiSessionsSlice.ps1'
}

Describe 'Pi Station Desktop session management' {
    It 'imports and forks independent sessions with separate drafts through relaunch' {
        { & $piSessionsSlice -Capture } | Should -Not -Throw
    }
}
