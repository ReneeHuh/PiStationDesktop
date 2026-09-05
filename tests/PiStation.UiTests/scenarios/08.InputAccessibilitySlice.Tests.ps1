Describe 'Pi Station input and accessibility slice' {
    $inputAccessibilitySlice = Join-Path $PSScriptRoot '..\Invoke-InputAccessibilitySlice.ps1'

    It 'covers keyboard, dialog, rich-input, scrolling, and accessibility behavior' {
        & $inputAccessibilitySlice -Configuration Debug -NoBuild
        $LASTEXITCODE | Should -BeIn @(0, $null)
    }
}
