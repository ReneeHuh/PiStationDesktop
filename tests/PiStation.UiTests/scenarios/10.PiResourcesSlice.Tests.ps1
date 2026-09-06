BeforeAll {
    $piResourcesSlice = Join-Path $PSScriptRoot '../Invoke-PiResourcesSlice.ps1'
}

Describe 'Pi Station Desktop resources and provider setup' {
    It 'manages resources, trust and custom models while preserving the draft through relaunch' {
        { & $piResourcesSlice -Capture } | Should -Not -Throw
    }
}
