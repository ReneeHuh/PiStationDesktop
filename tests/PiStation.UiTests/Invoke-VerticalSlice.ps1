[CmdletBinding()]
param(
    [ValidateSet('Debug')]
    [string] $Configuration = 'Debug',

    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$solutionRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$solutionPath = Join-Path $solutionRoot 'PiStationDesktop.slnx'
$appProject = Join-Path $solutionRoot 'src\PiStation.App\PiStation.App.csproj'
$fakePi = Join-Path $solutionRoot "tests\PiStation.FakePi\bin\$Configuration\net10.0\PiStation.FakePi.exe"
$artifactRoot = Join-Path $PSScriptRoot 'artifacts'
$runRoot = Join-Path $artifactRoot (Join-Path 'runs' ("{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N')))
$dataRoot = Join-Path $runRoot 'data'
$projectPath = Join-Path $dataRoot 'fixture-project'
$logFile = Join-Path $dataRoot 'app.jsonl'
$launchedProcessId = $null
$testError = $null

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [string[]] $ArgumentList
    )

    $output = & $FilePath @ArgumentList 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE.`n$($output | Out-String)"
    }

    return ($output | Out-String).Trim()
}

function Start-TestApp {
    $launchJson = Invoke-CheckedNative -FilePath 'winapp' -ArgumentList @(
        'run', $appProject,
        '--configuration', $Configuration,
        '--arch', 'x64',
        '--property', 'Platform=x64',
        '--no-build',
        '--no-restore',
        '--detach',
        '--json',
        '--',
        '--ui-test',
        '--data-root', $dataRoot,
        '--pi-executable', $fakePi,
        '--fake-pi-scenario', 'ui-tool',
        '--log-file', $logFile
    )
    $launch = $launchJson | ConvertFrom-Json
    $script:launchedProcessId = [int]$launch.ProcessId
}

function Stop-TestApp {
    if ($null -eq $script:launchedProcessId) {
        return
    }

    $ownedProcess = Get-Process -Id $script:launchedProcessId -ErrorAction SilentlyContinue
    if ($null -ne $ownedProcess -and $ownedProcess.ProcessName -eq 'PiStationDesktop') {
        Stop-Process -Id $script:launchedProcessId
        Wait-Process -Id $script:launchedProcessId -Timeout 10 -ErrorAction SilentlyContinue
    }

    $script:launchedProcessId = $null
}

function Invoke-Ui {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)
    return Invoke-CheckedNative -FilePath 'winapp' -ArgumentList (@('ui') + $Arguments + @(
        '--app', "$script:launchedProcessId", '--json'
    ))
}

function Set-TranscriptScrollPosition {
    param([ValidateSet('top', 'bottom')][string] $Position)

    try {
        Invoke-Ui 'scroll' 'TranscriptList' '--to' $Position | Out-Null
    }
    catch {
        if ($_.Exception.Message -notmatch 'cannot scroll vertically') {
            throw
        }
    }
}

function Wait-UiValue {
    param(
        [Parameter(Mandatory)][string] $Selector,
        [Parameter(Mandatory)][string] $Value,
        [int] $Timeout = 15000,
        [string] $Property
    )

    $arguments = @('wait-for', $Selector, '--timeout', "$Timeout", '--value', $Value)
    if (-not [string]::IsNullOrWhiteSpace($Property)) {
        $arguments += @('--property', $Property)
    }

    Invoke-Ui @arguments | Out-Null
}

function Get-UiNodes {
    param([AllowNull()][object] $Node)

    if ($null -eq $Node) {
        return
    }

    if ($Node.PSObject.Properties.Name -contains 'type') {
        $Node
    }

    foreach ($child in @($Node.children)) {
        if ($null -ne $child) {
            Get-UiNodes -Node $child
        }
    }

    foreach ($element in @($Node.elements)) {
        if ($null -ne $element) {
            Get-UiNodes -Node $element
        }
    }
}

function Wait-UiAutomationNode {
    param(
        [Parameter(Mandatory)][string] $AutomationId,
        [int] $Timeout = 15000,
        [switch] $RequireVisible
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($Timeout)
    do {
        $treeJson = Invoke-Ui 'inspect' 'TranscriptList' '--depth' '20'
        $tree = $treeJson | ConvertFrom-Json -Depth 100
        $matches = @($tree.windows | ForEach-Object {
            Get-UiNodes -Node $_
        } | Where-Object {
            $_.automationId -eq $AutomationId -and
            (-not $RequireVisible -or -not $_.isOffscreen)
        })
        if ($matches.Count -gt 0) {
            return $matches[0]
        }

        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Timed out waiting for AutomationId '$AutomationId' in the transcript tree."
}

function Assert-TurnMetricsNode {
    param([Parameter(Mandatory)][object] $Node)

    if (-not $Node.name.Contains('2 tokens', [StringComparison]::Ordinal) -or
        -not $Node.name.Contains('2 / 100,000 context', [StringComparison]::Ordinal)) {
        throw "Expected reported token and context metadata; found '$($Node.name)'."
    }

    if ($Node.name -notmatch '(^| • )(\d+ ms|\d+\.\d s|\d+m \d{2}s)( • |$)') {
        throw "Expected elapsed time in the turn metadata; found '$($Node.name)'."
    }
}

New-Item -ItemType Directory -Path $projectPath -Force | Out-Null

try {
    if (-not $NoBuild) {
        Invoke-CheckedNative -FilePath 'dotnet' -ArgumentList @(
            'build', $solutionPath, '--configuration', $Configuration
        ) | Write-Host
    }

    if (-not (Test-Path -LiteralPath $fakePi -PathType Leaf)) {
        throw "FakePi was not built at '$fakePi'."
    }

    Start-TestApp
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready'

    Invoke-Ui 'invoke' 'NewProjectButton' | Out-Null
    Invoke-Ui 'wait-for' 'ProjectPathInput' '--timeout' '5000' | Out-Null
    Invoke-Ui 'set-value' 'ProjectPathInput' $projectPath | Out-Null
    Invoke-Ui 'invoke' 'AddProjectConfirmButton' | Out-Null
    Invoke-Ui 'wait-for' 'fixture-project' '--timeout' '10000' | Out-Null
    Wait-UiValue -Selector 'NewThreadButton' -Value 'True' -Property 'IsEnabled'

    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Invoke-Ui 'wait-for' 'Thread 1' '--timeout' '15000' | Out-Null
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Idle'

    Invoke-Ui 'set-value' 'PromptInput' 'Exercise the UI tool stream' | Out-Null
    Wait-UiValue -Selector 'SendPromptButton' -Value 'True' -Property 'IsEnabled'
    Invoke-Ui 'invoke' 'SendPromptButton' | Out-Null
    Wait-UiValue -Selector 'StopTurnButton' -Value 'True' -Property 'IsEnabled'
    Invoke-Ui 'wait-for' 'Running' '--timeout' '10000' | Out-Null
    Invoke-Ui 'screenshot' '--output' (Join-Path $runRoot 'activity-running.png') '--focus' | Out-Null
    Wait-UiValue -Selector 'LatestAssistantMessage' -Value 'Tool'
    Wait-UiValue -Selector 'LatestAssistantMessage' -Value 'Tool finished.'
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Idle'
    Wait-UiValue -Selector 'StopTurnButton' -Value 'False' -Property 'IsEnabled'
    Set-TranscriptScrollPosition -Position 'bottom'
    $turnMetricsNode = Wait-UiAutomationNode -AutomationId 'TurnMetricsText' -RequireVisible
    Assert-TurnMetricsNode -Node $turnMetricsNode

    $transcriptJson = Invoke-Ui 'inspect' 'TranscriptList' '--depth' '8'
    $transcript = $transcriptJson | ConvertFrom-Json -Depth 100
    $finalMessages = @($transcript.windows | ForEach-Object {
        Get-UiNodes -Node $_
    } | Where-Object { $_.type -eq 'Text' -and $_.name.TrimEnd() -eq 'Tool finished.' })
    if ($finalMessages.Count -ne 1) {
        throw "Expected one final assistant message in the transcript; found $($finalMessages.Count)."
    }

    Set-TranscriptScrollPosition -Position 'top'
    $activityJson = Invoke-Ui 'inspect' 'TranscriptList' '--depth' '20'
    Set-Content -LiteralPath (Join-Path $runRoot 'activity-ui-tree.json') -Value $activityJson -Encoding utf8NoBOM
    $activityTree = $activityJson | ConvertFrom-Json -Depth 100
    $activityNodes = @($activityTree.windows | ForEach-Object { Get-UiNodes -Node $_ })
    $toolGroups = @($activityNodes | Where-Object {
        $_.automationId -eq 'ToolActivityGroup' -and $_.name -eq 'Tools, 2 tools, completed'
    })
    if ($toolGroups.Count -ne 1) {
        throw 'Expected the two consecutive tool calls in one completed activity group.'
    }

    $toolExpanders = @($activityNodes | Where-Object {
        $_.automationId -eq 'ToolActivityExpander'
    })
    if ($toolExpanders.Count -ne 2) {
        throw "Expected two individually expandable tool rows; found $($toolExpanders.Count)."
    }

    $reasoningExpanders = @($activityNodes | Where-Object {
        $_.automationId -eq 'ReasoningExpander' -and $_.name -eq 'Reasoning, completed'
    })
    if ($reasoningExpanders.Count -ne 1) {
        throw 'Expected one completed reasoning expander.'
    }

    Invoke-Ui 'scroll-into-view' 'ToolActivityGroup' | Out-Null
    Invoke-Ui 'screenshot' '--output' (Join-Path $runRoot 'activity-collapsed.png') '--focus' | Out-Null
    Invoke-Ui 'invoke' 'ReasoningExpander' | Out-Null
    Wait-UiAutomationNode -AutomationId 'ReasoningText' | Out-Null
    Invoke-Ui 'scroll-into-view' 'ReasoningText' | Out-Null
    $reasoningNode = Wait-UiAutomationNode -AutomationId 'ReasoningText' -RequireVisible
    if ($reasoningNode.name -ne "I’ll inspect the request, run the command, and verify the result.") {
        throw 'Expected the completed reasoning body after expanding it.'
    }

    Invoke-Ui 'invoke' 'ToolActivityExpander' | Out-Null
    Wait-UiAutomationNode -AutomationId 'ToolArgumentsText' | Out-Null
    Invoke-Ui 'scroll-into-view' 'ToolArgumentsText' | Out-Null
    Wait-UiAutomationNode -AutomationId 'ToolArgumentsText' -RequireVisible | Out-Null
    Wait-UiAutomationNode -AutomationId 'ToolOutputText' | Out-Null
    Invoke-Ui 'scroll-into-view' 'ToolOutputText' | Out-Null
    Wait-UiAutomationNode -AutomationId 'ToolOutputText' -RequireVisible | Out-Null
    $expandedToolJson = Invoke-Ui 'inspect' 'ToolActivityGroup' '--depth' '20'
    $expandedToolTree = $expandedToolJson | ConvertFrom-Json -Depth 100
    $expandedToolNodes = @($expandedToolTree.windows | ForEach-Object { Get-UiNodes -Node $_ })
    $argumentNodes = @($expandedToolNodes | Where-Object {
        $_.automationId -eq 'ToolArgumentsText' -and $_.name -eq '{"command":"echo hi"}'
    })
    $outputNodes = @($expandedToolNodes | Where-Object {
        $_.automationId -eq 'ToolOutputText' -and ($_.name -replace "`r`n", "`n") -eq "one`ntwo`ncomplete"
    })
    if ($argumentNodes.Count -ne 1 -or $outputNodes.Count -ne 1) {
        throw 'Expected the expanded tool row to expose its real arguments and complete bounded output.'
    }
    Invoke-Ui 'screenshot' '--output' (Join-Path $runRoot 'activity-expanded.png') '--focus' | Out-Null

    Invoke-Ui 'invoke' 'NewThreadButton' | Out-Null
    Invoke-Ui 'wait-for' 'Thread 2' '--timeout' '15000' | Out-Null
    Wait-UiValue -Selector 'TurnStatusText' -Value 'Idle'
    Invoke-Ui 'set-value' 'PromptInput' 'Render the Markdown fixture' | Out-Null
    Wait-UiValue -Selector 'SendPromptButton' -Value 'True' -Property 'IsEnabled'
    Invoke-Ui 'invoke' 'SendPromptButton' | Out-Null
    Wait-UiAutomationNode -AutomationId 'MarkdownCodeBlock' | Out-Null
    Wait-UiValue -Selector 'StopTurnButton' -Value 'False' -Property 'IsEnabled'
    Set-TranscriptScrollPosition -Position 'bottom'
    Wait-UiAutomationNode -AutomationId 'MarkdownCodeBlock' -RequireVisible | Out-Null
    $markdownJson = Invoke-Ui 'inspect' 'TranscriptList' '--depth' '20'
    Set-Content -LiteralPath (Join-Path $runRoot 'markdown-ui-tree.json') -Value $markdownJson -Encoding utf8NoBOM
    $markdownTree = $markdownJson | ConvertFrom-Json -Depth 100
    $markdownNodes = @($markdownTree.windows | ForEach-Object { Get-UiNodes -Node $_ })
    foreach ($requiredText in @(
        'Markdown fixture',
        'Native bold, italic, and inline code with a safe link.',
        'Quoted guidance stays visually distinct.',
        'First numbered item',
        'Second numbered item'
    )) {
        $textMatches = @($markdownNodes | Where-Object {
            $_.type -eq 'Text' -and $_.name.TrimEnd() -eq $requiredText
        })
        if ($textMatches.Count -ne 1) {
            throw "Expected rendered Markdown text '$requiredText' exactly once."
        }
    }

    $safeLinks = @($markdownNodes | Where-Object {
        $_.type -eq 'Hyperlink' -and $_.name -eq 'Open link safe link'
    })
    if ($safeLinks.Count -ne 1) {
        throw 'Expected one accessible safe Markdown hyperlink.'
    }

    $languageNodes = @($markdownNodes | Where-Object {
        $_.automationId -eq 'MarkdownCodeLanguage' -and $_.name -eq 'C#'
    })
    if ($languageNodes.Count -ne 1) {
        throw 'Expected one C# language label on the Markdown code block.'
    }

    $expectedCode = "public static string Greet(string name)`n{`n    return `$`"Hello, {name}!`";`n}"
    $codeNodes = @($markdownNodes | Where-Object { $_.automationId -eq 'MarkdownCodeText' })
    if ($codeNodes.Count -ne 1 -or
        (($codeNodes[0].name -replace "`r`n", "`n").TrimEnd()) -ne $expectedCode) {
        throw 'Expected one accessible Markdown code block with the complete C# fixture.'
    }

    $literalHtmlNodes = @($markdownNodes | Where-Object {
        $_.type -eq 'Text' -and $_.name.TrimEnd() -eq "<script>alert('not executed')</script>"
    })
    if ($literalHtmlNodes.Count -ne 1) {
        throw 'Expected raw HTML to render exactly once as inert text.'
    }

    Invoke-Ui 'invoke' 'MarkdownCodeCopyButton' | Out-Null
    $copiedJson = Invoke-Ui 'inspect' 'MarkdownCodeBlock' '--depth' '8'
    $copiedTree = $copiedJson | ConvertFrom-Json -Depth 100
    $copiedButtons = @($copiedTree.windows | ForEach-Object {
        Get-UiNodes -Node $_
    } | Where-Object {
        $_.automationId -eq 'MarkdownCodeCopyButton' -and $_.name -eq 'Copied C# code'
    })
    if ($copiedButtons.Count -ne 1) {
        throw 'Expected the code-copy action to announce completion.'
    }

    Wait-UiValue -Selector 'TurnStatusText' -Value 'Idle'
    Invoke-Ui 'screenshot' '--output' (Join-Path $runRoot 'markdown-code-slice.png') '--focus' | Out-Null
    Set-TranscriptScrollPosition -Position 'top'
    Invoke-Ui 'screenshot' '--output' (Join-Path $runRoot 'markdown-slice.png') '--focus' | Out-Null
    Invoke-Ui 'invoke' 'Thread 1' | Out-Null
    Wait-UiValue -Selector 'LatestAssistantMessage' -Value 'Tool finished.'

    Stop-TestApp
    Start-TestApp
    Wait-UiValue -Selector 'ConnectionStatusText' -Value 'Local • Ready'
    Invoke-Ui 'wait-for' 'fixture-project' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'fixture-project' | Out-Null
    Invoke-Ui 'wait-for' 'Thread 1' '--timeout' '10000' | Out-Null
    Invoke-Ui 'invoke' 'Thread 1' | Out-Null
    Wait-UiValue -Selector 'LatestAssistantMessage' -Value 'Tool finished.'
    Set-TranscriptScrollPosition -Position 'bottom'
    $restoredTurnMetricsNode = Wait-UiAutomationNode -AutomationId 'TurnMetricsText' -RequireVisible
    Assert-TurnMetricsNode -Node $restoredTurnMetricsNode

    $tree = Invoke-Ui 'inspect' '--depth' '10'
    Set-Content -LiteralPath (Join-Path $runRoot 'ui-tree.json') -Value $tree -Encoding utf8NoBOM
    Invoke-Ui 'screenshot' '--output' (Join-Path $runRoot 'vertical-slice.png') '--focus' | Out-Null

    Write-Output "Vertical slice passed for Pi Station Desktop (PID $launchedProcessId)."
    Write-Output "Artifacts: $runRoot"
}
catch {
    $testError = $_
    if ($null -ne $launchedProcessId) {
        try {
            Invoke-Ui 'inspect' '--depth' '10' |
                Set-Content -LiteralPath (Join-Path $runRoot 'failure-ui-tree.json') -Encoding utf8NoBOM
            Invoke-Ui 'screenshot' '--output' (Join-Path $runRoot 'failure.png') '--focus' | Out-Null
        }
        catch {
            # Preserve the original failure when diagnostic capture is unavailable.
        }
    }
}
finally {
    Stop-TestApp
    $canonicalRoot = [System.IO.Path]::GetFullPath($dataRoot)
    $requiredPrefix = $canonicalRoot + [System.IO.Path]::DirectorySeparatorChar
    $ownedFiles = @(Get-ChildItem -LiteralPath $dataRoot -Recurse -File -ErrorAction SilentlyContinue |
        ForEach-Object { [System.IO.Path]::GetFullPath($_.FullName) })
    foreach ($file in $ownedFiles) {
        if (-not $file.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "An app-owned file escaped the isolated data root: $file"
        }
    }

    $manifest = [ordered]@{
        dataRoot = $canonicalRoot
        databasePath = Join-Path $canonicalRoot 'host.db'
        sessionRoot = Join-Path $canonicalRoot 'sessions'
        logFile = $logFile
        files = $ownedFiles
    }
    $manifest | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $runRoot 'run-manifest.json') -Encoding utf8NoBOM
}

if ($null -ne $testError) {
    throw $testError
}
