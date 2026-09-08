BeforeAll {
    $hostingSlice = Join-Path $PSScriptRoot '../Invoke-HostingReviewSlice.ps1'
}

Describe 'Pi Station populated native GitHub review' {
    It 'verifies native edits, confirmation controls, refresh and persistence at <Scale>% text scale' -TestCases @(
        @{ Scale = 100 }, @{ Scale = 150 }, @{ Scale = 200 }
    ) {
        param($Scale)
        { & $hostingSlice -TextScalePercent $Scale -NoBuild:($Scale -ne 100) } | Should -Not -Throw
    }
}
