[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$contractPath = Join-Path $PSScriptRoot 'visual-contract.psd1'
$tokenPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\src\PiStation.App\Themes\T3DesignTokens.xaml'))
$viewRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\src\PiStation.App\Views'))
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

function Assert-Contract {
    param(
        [Parameter(Mandatory)][bool] $Condition,
        [Parameter(Mandatory)][string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Test-XamlKey {
    param(
        [Parameter(Mandatory)][string] $Xaml,
        [Parameter(Mandatory)][string] $Key,
        [int] $ExpectedCount = 1
    )

    $pattern = 'x:Key\s*=\s*["'']' + [regex]::Escape($Key) + '["'']'
    $count = [regex]::Matches($Xaml, $pattern).Count
    Assert-Contract ($count -eq $ExpectedCount) `
        "Expected XAML key '$Key' $ExpectedCount time(s), found $count."
}

Assert-Contract (Test-Path -LiteralPath $contractPath -PathType Leaf) `
    "Visual contract was not found at '$contractPath'."
Assert-Contract (Test-Path -LiteralPath $tokenPath -PathType Leaf) `
    "Design tokens were not found at '$tokenPath'."

$contract = Import-PowerShellDataFile -LiteralPath $contractPath
Assert-Contract ($contract.SchemaVersion -eq 1) 'Unsupported visual-contract schema.'

$referencePath = Join-Path $repositoryRoot ($contract.Reference.RelativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
Assert-Contract (Test-Path -LiteralPath $referencePath -PathType Leaf) `
    "Pinned T3 visual reference was not found at '$referencePath'."
$referenceHash = (Get-FileHash -LiteralPath $referencePath -Algorithm SHA256).Hash
Assert-Contract ($referenceHash -eq $contract.Reference.Sha256) `
    "Pinned T3 visual reference hash changed. Expected $($contract.Reference.Sha256), found $referenceHash."

$expectedSizes = @('1200x800', '1440x900', '1920x1080')
$actualSizes = @($contract.ReviewSizes | ForEach-Object { "$($_.Width)x$($_.Height)" })
Assert-Contract (($actualSizes -join ',') -eq ($expectedSizes -join ',')) `
    "Visual review sizes must be $($expectedSizes -join ', '); found $($actualSizes -join ', ')."
Assert-Contract (($contract.ReviewSizes.Name | Select-Object -Unique).Count -eq $contract.ReviewSizes.Count) `
    'Visual review size names must be unique.'
foreach ($size in $contract.ReviewSizes) {
    Assert-Contract ($size.SidebarWidth -eq 260) `
        "Review size '$($size.Name)' must record the 260px sidebar geometry."
    Assert-Contract ($size.ReadingColumnMaxWidth -eq 720) `
        "Review size '$($size.Name)' must record the 720px reading-column geometry."
    Assert-Contract ($size.WorkbenchPanelWidth -eq 420) `
        "Review size '$($size.Name)' must record the 420px workbench-panel geometry."
}

$expectedResponsiveLayouts = @('Narrow:0:719', 'Compact:720:899', 'Standard:900:1179', 'Wide:1180:0')
$actualResponsiveLayouts = @($contract.ResponsiveLayouts | ForEach-Object {
    "$($_.Name):$($_.MinWidth):$($_.MaxWidth)"
})
Assert-Contract (($actualResponsiveLayouts -join ',') -eq ($expectedResponsiveLayouts -join ',')) `
    "Responsive layouts must be $($expectedResponsiveLayouts -join ', '); found $($actualResponsiveLayouts -join ', ')."
Assert-Contract ((@($contract.TextScaleProfiles) -join ',') -eq '100,150,200') `
    'Text-scale profiles must cover 100, 150, and 200 percent.'

foreach ($responsiveView in @(
    'Controls\WorkspaceShell.xaml',
    'ShellPage.xaml',
    'Controls\ChatHeader.xaml',
    'Controls\ComposerSurface.xaml'
)) {
    $responsiveXaml = Get-Content -LiteralPath (Join-Path $viewRoot $responsiveView) -Raw
    foreach ($layout in $contract.ResponsiveLayouts) {
        Assert-Contract ($responsiveXaml.Contains(
            "x:Name=`"$($layout.Name)Layout`"",
            [System.StringComparison]::Ordinal)) `
            "$responsiveView does not declare the $($layout.Name)Layout visual state."
    }
}

$requiredStates = @(
    'empty', 'completed', 'running-tool', 'tool-details', 'interaction', 'recovery',
    'long-transcript-top', 'long-transcript-bottom', 'attachment-empty', 'draft',
    'sidebar-collapsed', 'right-panel-open', 'settings-open', 'responsive-narrow',
    'responsive-compact', 'responsive-standard', 'responsive-wide', 'responsive-maximized',
    'theme-light-text-150', 'theme-dark-text-200'
)
$stateNames = @($contract.States.Name)
foreach ($stateName in $requiredStates) {
    Assert-Contract ($stateNames -contains $stateName) "Visual state '$stateName' is missing."
}
Assert-Contract (($stateNames | Select-Object -Unique).Count -eq $stateNames.Count) `
    'Visual state names must be unique.'

foreach ($state in $contract.States) {
    Assert-Contract (-not [string]::IsNullOrWhiteSpace($state.Artifact)) `
        "Visual state '$($state.Name)' does not declare an artifact."
    $runnerPath = Join-Path $PSScriptRoot $state.Runner
    Assert-Contract (Test-Path -LiteralPath $runnerPath -PathType Leaf) `
        "Visual state '$($state.Name)' runner was not found at '$runnerPath'."
}

$normalStates = @($contract.States | Where-Object NormalLaunch)
Assert-Contract ($normalStates.Count -gt 0) 'At least one normal-launch visual state is required.'
$driverScript = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Invoke-DriverContract.ps1') -Raw
Assert-Contract ($contract.ForbiddenNormalLaunchAutomationIds.Count -gt 0) `
    'The normal-launch forbidden-control list cannot be empty.'
Assert-Contract (
    $driverScript.Contains('ForbiddenNormalLaunchAutomationIds', [System.StringComparison]::Ordinal) -and
    $driverScript.Contains('$tree.Contains', [System.StringComparison]::Ordinal)
) 'The normal-launch driver does not enforce the forbidden-control list.'

$tokens = Get-Content -LiteralPath $tokenPath -Raw
foreach ($key in $contract.RequiredThemeColorKeys) {
    Test-XamlKey -Xaml $tokens -Key $key -ExpectedCount $contract.ThemeVariants.Count
}
foreach ($key in @($contract.RequiredBrushKeys) + @($contract.RequiredMetricKeys) + @($contract.RequiredStyleKeys)) {
    Test-XamlKey -Xaml $tokens -Key $key
}

foreach ($theme in $contract.ThemeVariants) {
    Test-XamlKey -Xaml $tokens -Key $theme
}

foreach ($requiredInteractionState in @('disabled', 'hover', 'pressed', 'focus', 'selected', 'running', 'warning', 'error')) {
    Assert-Contract ($contract.InteractionStates -contains $requiredInteractionState) `
        "Interaction state '$requiredInteractionState' is missing from the visual contract."
}

$appRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\src\PiStation.App'))
$viewColorLiterals = Get-ChildItem -LiteralPath $appRoot -Recurse -File -Filter '*.xaml' |
    Where-Object { $_.DirectoryName -notlike '*\Themes' } |
    ForEach-Object {
        $file = $_
        [regex]::Matches((Get-Content -LiteralPath $file.FullName -Raw), '#[0-9A-Fa-f]{6,8}') |
            ForEach-Object { [pscustomobject]@{ Color = $_.Value.ToUpperInvariant(); File = $file.FullName } }
    }
$repeatedViewColors = @($viewColorLiterals | Group-Object Color | Where-Object Count -gt 1)
Assert-Contract ($repeatedViewColors.Count -eq 0) `
    "View XAML repeats hard-coded colors: $($repeatedViewColors.Name -join ', '). Promote them to semantic tokens."

$customMotion = @(Get-ChildItem -LiteralPath $viewRoot -Recurse -File -Filter '*.xaml' |
    Select-String -Pattern '<Storyboard|<(Double|Color|Point)Animation')
Assert-Contract ($customMotion.Count -eq 0) `
    'View XAML contains custom animation that is not governed by the Windows reduced-motion setting.'

$conversationXaml = Get-Content -LiteralPath (Join-Path $viewRoot 'Controls\ConversationTimeline.xaml') -Raw
foreach ($checkpointAutomationId in @(
    'TurnCheckpointCard',
    'TurnCheckpointDiffButton',
    'FullThreadCheckpointDiffButton',
    'RevertCheckpointButton'
)) {
    Assert-Contract ($conversationXaml.Contains(
        "AutomationId=`"$checkpointAutomationId`"",
        [System.StringComparison]::Ordinal)) `
        "Conversation timeline is missing checkpoint control '$checkpointAutomationId'."
}

Write-Output (
    "Visual contract passed: $($contract.States.Count) states, " +
    "$($contract.ResponsiveLayouts.Count) responsive layouts, $($contract.TextScaleProfiles.Count) text scales, " +
    "T3 $($contract.Reference.Commit).")
