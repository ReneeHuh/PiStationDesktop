$script:expandingTestComposer = $false

function Get-TestThreadNodes {
    param($Node)
    if ($null -eq $Node) { return }
    Write-Output $Node
    foreach ($child in @($Node.children)) {
        if ($null -ne $child) { Get-TestThreadNodes -Node $child }
    }
    foreach ($element in @($Node.elements)) {
        if ($null -ne $element) { Get-TestThreadNodes -Node $element }
    }
}

function Select-TestThread {
    param([Parameter(Mandatory)][string] $Title)

    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $tree = Invoke-Ui 'inspect' 'ThreadTabList' '--depth' '8' | ConvertFrom-Json -Depth 100
        $matches = @($tree.windows | ForEach-Object { Get-TestThreadNodes -Node $_ } | Where-Object {
            $_.type -eq 'ListItem' -and @(
                Get-TestThreadNodes -Node $_ | Where-Object { $_.type -eq 'Text' -and $_.name -eq $Title }
            ).Count -gt 0
        })
        if ($matches.Count -eq 1) {
            Invoke-Ui 'invoke' $matches[0].selector | Out-Null
            return
        }
        if ($matches.Count -gt 1) { throw "Multiple thread-list rows matched '$Title'." }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The thread list did not expose '$Title'."
}

function Wait-TestThread {
    param(
        [Parameter(Mandatory)][string] $Title,
        [int] $Timeout = 15000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($Timeout)
    do {
        $tree = Invoke-Ui 'inspect' 'ThreadTabList' '--depth' '8' | ConvertFrom-Json -Depth 100
        $matches = @($tree.windows | ForEach-Object { Get-TestThreadNodes -Node $_ } | Where-Object {
            $_.type -eq 'ListItem' -and @(
                Get-TestThreadNodes -Node $_ | Where-Object { $_.type -eq 'Text' -and $_.name -eq $Title }
            ).Count -gt 0
        })
        if ($matches.Count -eq 1) { return }
        if ($matches.Count -gt 1) { throw "Multiple thread-list rows matched '$Title'." }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The thread list did not expose '$Title'."
}

function Wait-TestProjectSummary {
    param(
        [Parameter(Mandatory)][string] $ProjectName,
        [Parameter(Mandatory)][int] $Count,
        [string] $ThreadTitle,
        [int] $Timeout = 5000
    )
    $deadline = [DateTime]::UtcNow.AddMilliseconds($Timeout)
    do {
        $tree = Invoke-Ui 'inspect' 'ThreadTabList' '--depth' '8' | ConvertFrom-Json -Depth 100
        $rows = @($tree.windows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object {
            $_.type -eq 'ListItem' -and @(Get-TestThreadNodes $_ | Where-Object {
                $_.type -eq 'Text' -and $_.name -eq $ProjectName
            }).Count -gt 0
        })
        $hasTitle = -not $ThreadTitle -or @($rows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object {
            $_.type -eq 'Text' -and $_.name -eq $ThreadTitle
        }).Count -gt 0
        if ($rows.Count -eq $Count -and $hasTitle) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The inbox did not expose $Count rows with thread '$ThreadTitle' for '$ProjectName'."
}

# Called before legacy journeys interact with the prompt; inspecting the resting state is not a mutation.
function Expand-TestComposer {
    if ($script:expandingTestComposer) { return }
    $script:expandingTestComposer = $true
    try {
        $tree = Invoke-Ui 'inspect' '--depth' '14' | ConvertFrom-Json -Depth 100
        $button = @($tree.windows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object {
            $_.automationId -eq 'ExpandComposerButton' -and $_.isOffscreen -ne $true
        })
        if ($button.Count -eq 1) {
            Invoke-Ui 'invoke' 'ExpandComposerButton' | Out-Null
            Invoke-Ui 'wait-for' 'PromptInput' '--timeout' '5000' | Out-Null
        }
    } finally { $script:expandingTestComposer = $false }
}

function Select-TestProject {
    param([string] $Name = 'fixture-project')
    Invoke-Ui 'invoke' 'ProjectFilterButton' | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $tree = Invoke-Ui 'inspect' 'ProjectSelector' '--depth' '12' | ConvertFrom-Json -Depth 100
        $button = @($tree.windows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object { $_.type -eq 'ListItem' -and @(Get-TestThreadNodes $_ | Where-Object { $_.type -eq 'Text' -and $_.name -eq $Name }).Count -gt 0 }) | Select-Object -First 1
        if ($button) { Invoke-Ui 'invoke' $button.selector | Out-Null; return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Project not found: $Name"
}
