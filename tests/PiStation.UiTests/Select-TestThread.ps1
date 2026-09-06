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
        $tree = Invoke-Ui 'inspect' 'ProjectSelector' '--depth' '12' | ConvertFrom-Json -Depth 100
        $summary = if ($Count -eq 1) { '1 task' } else { "$Count tasks" }
        $matches = @($tree.windows | ForEach-Object { Get-TestThreadNodes -Node $_ } | Where-Object {
            $_.type -eq 'ListItem' -and
            (@(Get-TestThreadNodes -Node $_ | Where-Object { $_.name -eq $ProjectName }).Count -gt 0) -and
            (@(Get-TestThreadNodes -Node $_ | Where-Object { $_.type -eq 'Text' -and $_.name -eq $summary }).Count -gt 0) -and
            (-not $ThreadTitle -or (@(Get-TestThreadNodes -Node $_ | Where-Object { $_.type -eq 'Text' -and $_.name -eq $ThreadTitle }).Count -gt 0))
        })
        if ($matches.Count -eq 1) { return }
        if ($matches.Count -gt 1) { throw "Multiple project rows matched '$ProjectName'." }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The project sidebar did not expose '$summary' with thread '$ThreadTitle' for '$ProjectName'."
}
