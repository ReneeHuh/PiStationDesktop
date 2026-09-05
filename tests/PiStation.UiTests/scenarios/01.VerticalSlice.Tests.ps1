BeforeAll {
    $verticalSlice = Join-Path $PSScriptRoot '..\Invoke-VerticalSlice.ps1'
}

Describe 'Pi Station Desktop vertical slice' {
    It 'runs, reopens, and restores a FakePi tool turn through the UI' {
        { & $verticalSlice } | Should -Not -Throw
    }
}
