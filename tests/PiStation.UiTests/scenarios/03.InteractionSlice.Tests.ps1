BeforeAll {
    $interactionSlice = Join-Path $PSScriptRoot '..\Invoke-InteractionSlice.ps1'
}

Describe 'Pi Station Desktop interaction slice' {
    It 'approves and answers FakePi requests through inline timeline controls' {
        { & $interactionSlice } | Should -Not -Throw
    }
}
