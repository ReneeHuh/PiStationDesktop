BeforeAll {
    $remoteAcceptance = Join-Path $PSScriptRoot '../Invoke-RemoteAccessAcceptance.ps1'
}

Describe 'Remote native acceptance fixture' {
    It 'checks isolated pinned HTTPS settings, icon upload, and diagnostics without claiming visual acceptance' {
        { & $remoteAcceptance -Mode Smoke } | Should -Not -Throw
    }
}
