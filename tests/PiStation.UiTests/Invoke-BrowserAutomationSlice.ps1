[CmdletBinding()]
param()

# Run after Invoke-CodeTests.ps1 has built Debug. Owns a fresh data root and only its captured app PID/job.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runRoot = Join-Path $repoRoot ('TestResults/browser-native-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $runRoot 'data'
$projectPath = Join-Path $runRoot 'browser-project'
New-Item -ItemType Directory -Path $dataRoot, $projectPath -Force | Out-Null
$ownedAppPid = $null
$serverJob = $null
$passed = $false
$failure = $null
. (Join-Path $PSScriptRoot 'Select-TestThread.ps1')

function Invoke-Ui {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $result = & winapp ui @Arguments --app $script:ownedAppPid --json 2>&1
        if ($LASTEXITCODE -eq 0) { return $result }
        # Reacquire a read-only snapshot if the native tree changed during inspection.
        # Never repeat an invoke: a failed response could follow an applied action.
        if ($Arguments[0] -ne 'inspect' -or "$result" -notmatch 'stale_element' -or $attempt -eq 2) {
            throw "UI command failed: $($Arguments -join ' ') $result"
        }
        Start-Sleep -Milliseconds 100
    }
}
function Wait-Condition {
    param([scriptblock] $Condition, [string] $Description, [int] $TimeoutMs = 20000)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $Description"
}
function Set-AgentPermission {
    param([string] $Name)
    Invoke-Ui invoke PreviewAutomationPermissionSelector | Out-Null
    $tree = Invoke-Ui inspect --depth 10 | ConvertFrom-Json -Depth 100
    $item = $tree.windows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object { $_.type -eq 'ListItem' -and $_.name -eq $Name } | Select-Object -First 1
    if (-not $item) { throw "Permission option '$Name' was not exposed." }
    Invoke-Ui invoke $item.selector | Out-Null
}
function Invoke-Browser {
    param([hashtable] $InputData, [switch] $ExpectFailure, [switch] $RevokeAfterSubmit)
    $permission = Get-Content -LiteralPath $script:permissionFile -Raw | ConvertFrom-Json
    $id = [guid]::NewGuid().ToString('D')
    $threadDirectory = Split-Path -Parent $script:permissionFile
    $requestPath = Join-Path $threadDirectory "requests/$id.json"
    $responsePath = Join-Path $threadDirectory "responses/$id.json"
    $request = @{ id = $id; operation = $InputData.action; input = $InputData; createdUtc = [DateTime]::UtcNow.ToString('O'); controllerId = $permission.controllerId }
    $request | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runRoot "$id.request.json")
    [IO.File]::WriteAllText($requestPath + '.tmp', ($request | ConvertTo-Json -Depth 10 -Compress))
    Move-Item -LiteralPath ($requestPath + '.tmp') -Destination $requestPath
    if ($RevokeAfterSubmit) {
        Wait-Condition { Test-Path -LiteralPath ([IO.Path]::ChangeExtension($requestPath, '.claimed')) } 'evaluation claimed before revocation'
        Wait-Condition { Test-Path -LiteralPath (Join-Path $runRoot 'evaluation-started.txt') } 'script execution before revocation' 10000
        Set-AgentPermission 'Agent inspect only'
    }
    Wait-Condition { Test-Path -LiteralPath $responsePath } "browser response for $($InputData.action)" 30000
    $response = Get-Content -LiteralPath $responsePath -Raw | ConvertFrom-Json -Depth 30
    $response | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $runRoot "$id-$($InputData.action).json")
    if ($ExpectFailure) {
        if ($response.success) { throw "Browser $($InputData.action) unexpectedly succeeded." }
    } elseif (-not $response.success) { throw "Browser $($InputData.action): $($response.error)" }
    return $response
}

try {
    & git -C $projectPath init --quiet --initial-branch=main
    if ($LASTEXITCODE -ne 0) { throw 'Fixture git init failed.' }
    [IO.File]::WriteAllText((Join-Path $projectPath 'README.md'), 'Browser automation fixture')
    & git -C $projectPath add README.md
    & git -C $projectPath -c user.name='PiStation Tests' -c user.email='tests@example.invalid' commit --quiet -m fixture
    if ($LASTEXITCODE -ne 0) { throw 'Fixture git commit failed.' }
    $serverJob = Start-Job -ArgumentList $runRoot {
        param($evidenceRoot)
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        Write-Output $listener.LocalEndpoint.Port
        try {
            while ($true) {
                if (-not $listener.Pending()) { Start-Sleep -Milliseconds 50; continue }
                $client = $listener.AcceptTcpClient()
                try {
                    $stream = $client.GetStream()
                    $stream.ReadTimeout = 5000
                    $buffer = [byte[]]::new(8192)
                    $read = $stream.Read($buffer, 0, $buffer.Length)
                    $requestLine = [Text.Encoding]::ASCII.GetString($buffer, 0, $read).Split("`r`n")[0]
                    if ($requestLine -like '* /evaluation-started *') { [IO.File]::WriteAllText((Join-Path $evidenceRoot 'evaluation-started.txt'), 'The agent script started its request.') }
                    $status = if ($requestLine -like 'GET /missing*') { '404 Not Found' } else { '200 OK' }
                    $html = '<!doctype html><html><head><title>Browser automation fixture</title><style>body{font:24px sans-serif;background:white;color:black}@media(prefers-color-scheme:dark){body{background:#181818;color:white}}</style></head><body><h1>Browser automation fixture</h1><button id="set" onclick="document.querySelector(''#state'').textContent=''Document retained''">Keep state</button><p id="state">Initial document</p></body></html>'
                    $body = [Text.Encoding]::UTF8.GetBytes($html)
                    $header = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 $status`r`nContent-Type: text/html; charset=utf-8`r`nContent-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n")
                    $stream.Write($header); $stream.Write($body)
                } catch [IO.IOException] { } finally { $client.Dispose() }
            }
        } finally { $listener.Stop() }
    }
    Wait-Condition { $script:serverPort = @(Receive-Job -Job $serverJob -Keep)[0]; $null -ne $script:serverPort } 'fixture server'
    $serverPort = [int]$serverPort
    $launchJson = & winapp run (Join-Path $repoRoot 'src/PiStation.App/PiStation.App.csproj') --configuration Debug --arch x64 --property Platform=x64 --no-build --no-restore --detach --json -- --ui-test --data-root $dataRoot --pi-executable (Join-Path $repoRoot 'tests/PiStation.FakePi/bin/Debug/net10.0/PiStation.FakePi.exe') --fake-pi-scenario normal --log-file (Join-Path $runRoot 'app.jsonl')
    if ($LASTEXITCODE -ne 0) { throw "Native app launch failed: $launchJson" }
    $ownedAppPid = [int](($launchJson | ConvertFrom-Json).ProcessId)
    if ($ownedAppPid -le 0) { throw 'Launch returned no owned app PID.' }
    @{ appPid = $ownedAppPid; dataRoot = $dataRoot; serverPort = $serverPort } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'launch.json')
    Invoke-Ui wait-for ConnectionStatusText --timeout 15000 | Out-Null
    Wait-Condition {
        $status = Invoke-Ui get-property ConnectionStatusText --property Name | ConvertFrom-Json
        $status.properties.Name -eq 'Local • Ready'
    } 'local host readiness' 30000
    Invoke-Ui wait-for NewProjectButton --timeout 15000 | Out-Null
    Invoke-Ui invoke NewProjectButton | Out-Null
    Invoke-Ui wait-for ProjectPathInput --timeout 5000 | Out-Null
    Invoke-Ui set-value ProjectPathInput $projectPath | Out-Null
    Invoke-Ui invoke AddProjectConfirmButton | Out-Null
    Wait-Condition {
        $tree = Invoke-Ui inspect ProjectTabList --depth 8 | ConvertFrom-Json -Depth 100
        @($tree.windows | ForEach-Object { Get-TestThreadNodes $_ } | Where-Object { $_.name -eq 'browser-project' }).Count -gt 0
    } 'project sidebar row'
    Invoke-Ui invoke AddActionButton | Out-Null
    Invoke-Ui invoke HeaderNewThreadMenuItem | Out-Null
    Wait-TestThread -Title 'Thread 1'
    Invoke-Ui invoke ToggleWorkbenchButton | Out-Null
    Invoke-Ui invoke PreviewPanelTab | Out-Null
    Invoke-Ui invoke AddPreviewTabButton | Out-Null
    Set-AgentPermission 'Agent interact'
    Wait-Condition {
        $script:permissionFile = Get-ChildItem -LiteralPath (Join-Path $dataRoot 'browser-automation') -Filter permission.json -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
        $null -ne $script:permissionFile
    } 'thread browser permission'
    $opened = Invoke-Browser @{ action = 'open'; reuseExistingTab = $false; open = $false; url = "http://127.0.0.1:$serverPort"; timeoutMs = 20000 }
    $tabId = $opened.data.tabId
    if (-not $tabId) { throw 'Open returned no stable tab ID.' }
    Invoke-Browser @{ action = 'wait'; condition = 'loaded'; timeoutMs = 20000 } | Out-Null
    Invoke-Browser @{ action = 'click'; selector = '#set' } | Out-Null
    $resized = Invoke-Browser @{ action = 'resize'; mode = 'preset'; preset = 'phone'; orientation = 'landscape'; timeoutMs = 10000 }
    if ([Math]::Abs($resized.data.viewport.width - 844) -gt 1 -or [Math]::Abs($resized.data.viewport.height - 390) -gt 1) { throw 'Preset viewport did not render within native pixel rounding.' }
    Invoke-Browser @{ action = 'set_appearance'; colorScheme = 'dark' } | Out-Null

    Invoke-Ui invoke AddActionButton | Out-Null
    Invoke-Ui invoke HeaderNewThreadMenuItem | Out-Null
    Wait-TestThread -Title 'Thread 2'
    Invoke-Browser @{ action = 'wait'; selector = '#state'; condition = 'text'; value = 'Document retained' } | Out-Null
    $background = Invoke-Browser @{ action = 'status' }
    if ($background.data.tabId -ne $tabId -or $background.data.visible) { throw 'Background automation changed its target or stole presentation.' }
    $resized = Invoke-Browser @{ action = 'resize'; mode = 'freeform'; width = 1024; height = 768 }
    if ([Math]::Abs($resized.data.viewport.width - 1024) -gt 1 -or [Math]::Abs($resized.data.viewport.height - 768) -gt 1) { throw 'Background viewport did not render within native pixel rounding.' }
    $capture = Invoke-Browser @{ action = 'screenshot' }
    [IO.File]::WriteAllBytes((Join-Path $runRoot 'background-browser.png'), [Convert]::FromBase64String($capture.screenshotPng))
    $evaluated = Invoke-Browser @{ action = 'evaluate'; expression = 'Promise.resolve({answer:42,title:document.title})' }
    if ($evaluated.data.value.answer -ne 42 -or $evaluated.data.value.title -ne 'Browser automation fixture') { throw 'Evaluation did not await and serialize its result.' }
    Invoke-Browser @{ action = 'evaluate'; expression = 'Promise.resolve(42)'; awaitPromise = $false } -ExpectFailure | Out-Null
    Invoke-Browser @{ action = 'evaluate'; expression = 'document.body' } -ExpectFailure | Out-Null
    $synchronous = Invoke-Browser @{ action = 'evaluate'; expression = '({items:[1,true,null],answer:42})'; awaitPromise = $false }
    if ($synchronous.data.value.answer -ne 42 -or $synchronous.data.value.items.Count -ne 3) { throw 'Synchronous JSON objects did not survive evaluation.' }
    foreach ($expression in @('(() => { throw new Error("fixture exception") })()', 'NaN', '42n', '(() => { const a = {}; a.self = a; return a; })()', '"x".repeat(65000)')) {
        Invoke-Browser @{ action = 'evaluate'; expression = $expression } -ExpectFailure | Out-Null
    }
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    Invoke-Browser @{ action = 'evaluate'; expression = '(() => { while(true) {} })()'; timeoutMs = 500 } -ExpectFailure | Out-Null
    if ($elapsed.ElapsedMilliseconds -gt 6000) { throw 'Infinite evaluation exceeded its bounded termination window.' }
    $recovered = Invoke-Browser @{ action = 'evaluate'; expression = '6 * 7' }
    if ($recovered.data.value -ne 42) { throw 'Browser did not recover after script timeout.' }
    Invoke-Browser @{ action = 'evaluate'; expression = 'console.error("automation diagnostic"); fetch("/missing").then(r => r.status)' } | Out-Null
    Invoke-Browser @{ action = 'resize'; mode = 'freeform'; width = 1440; height = 900 } | Out-Null
    $snapshot = Invoke-Browser @{ action = 'snapshot'; timeoutMs = 15000 }
    if ($snapshot.data.title -ne 'Browser automation fixture' -or $snapshot.data.visibleText -notmatch 'Document retained') { throw 'Rich snapshot lost the retained page.' }
    if (-not @($snapshot.data.interactiveElements | Where-Object { $_.selector -eq '#set' -and $_.width -gt 0 }).Count) { throw 'Snapshot lacks semantic element selectors and bounds.' }
    if (-not @($snapshot.data.accessibilityTree.nodes | Where-Object { $_.role -eq 'button' -and $_.name -eq 'Keep state' }).Count) { throw 'Snapshot lacks the native accessibility tree.' }
    if (-not @($snapshot.data.consoleEntries | Where-Object { $_.text -match 'automation diagnostic' }).Count) { throw 'Snapshot lacks console diagnostics.' }
    if (-not @($snapshot.data.networkEntries | Where-Object { $_.status -eq 404 -and $_.url -match '/missing' }).Count) { throw 'Snapshot lacks failed request diagnostics.' }
    if (-not @($snapshot.data.actionTimeline | Where-Object { $_.action -eq 'evaluate' -and $_.status -eq 'failed' }).Count) { throw 'Snapshot lacks failed action history.' }
    if ($snapshot.data.screenshot.width -ne 1280 -or $snapshot.data.screenshot.height -ne 800) { throw 'Snapshot image was not resized with its aspect ratio preserved.' }
    [IO.File]::WriteAllBytes((Join-Path $runRoot 'rich-snapshot.png'), [Convert]::FromBase64String($snapshot.screenshotPng))
    Invoke-Ui invoke ToggleWorkbenchButton | Out-Null
    Invoke-Browser @{ action = 'wait'; selector = '#state'; condition = 'text'; value = 'Document retained' } | Out-Null
    Invoke-Browser @{ action = 'set_appearance'; colorScheme = 'light' } | Out-Null
    Invoke-Browser @{ action = 'set_appearance'; colorScheme = 'system' } | Out-Null
    Select-TestThread -Title 'Thread 1'
    Invoke-Browser @{ action = 'open'; tabId = $tabId } | Out-Null
    Invoke-Browser @{ action = 'wait'; selector = '#state'; condition = 'text'; value = 'Document retained' } | Out-Null
    Invoke-Browser @{ action = 'evaluate'; expression = '(() => { navigator.sendBeacon("/evaluation-started", "started"); while(true) {} })()'; timeoutMs = 20000 } -ExpectFailure -RevokeAfterSubmit | Out-Null
    Wait-Condition { (Get-Content -LiteralPath $permissionFile -Raw | ConvertFrom-Json).mode -eq 'inspect' } 'inspect-only lease'
    Invoke-Browser @{ action = 'resize'; mode = 'fill' } -ExpectFailure | Out-Null
    Invoke-Browser @{ action = 'evaluate'; expression = '1 + 1' } -ExpectFailure | Out-Null
    $inspected = Invoke-Browser @{ action = 'snapshot'; timeoutMs = 15000 }
    if (-not $inspected.screenshotPng -or $inspected.data.title -ne 'Browser automation fixture') { throw 'Inspect snapshot failed after cancellation.' }
    Set-AgentPermission 'Agent interact'
    Wait-Condition { (Get-Content -LiteralPath $permissionFile -Raw | ConvertFrom-Json).mode -eq 'interact' } 'restored interact lease'
    Invoke-Browser @{ action = 'open'; reuseExistingTab = $false; open = $false; url = "http://127.0.0.1:$serverPort" } | Out-Null
    Invoke-Browser @{ action = 'wait'; condition = 'loaded'; timeoutMs = 15000 } | Out-Null
    $elapsed.Restart()
    Invoke-Browser @{ action = 'evaluate'; expression = 'new Promise(() => {})'; timeoutMs = 500 } -ExpectFailure | Out-Null
    if ($elapsed.ElapsedMilliseconds -gt 6000) { throw 'Unresolved promise exceeded its bounded termination window.' }
    $retained = Invoke-Browser @{ action = 'evaluate'; tabId = $tabId; expression = 'document.querySelector("#state").textContent' }
    if ($retained.data.value -ne 'Document retained') { throw 'Cancelling another tab damaged the retained document.' }
    $passed = $true
    Write-Output "Browser native slice passed. Evidence: $runRoot"
} catch {
    $failure = $_.ToString()
    if ($ownedAppPid) { & winapp ui inspect --depth 10 --app $ownedAppPid --json | Set-Content -LiteralPath (Join-Path $runRoot 'failure-ui.json') }
    throw
} finally {
    if ($ownedAppPid -gt 0) { Stop-Process -Id $ownedAppPid -ErrorAction SilentlyContinue }
    if ($serverJob) { Stop-Job -Job $serverJob; Remove-Job -Job $serverJob }
    @{ passed = $passed; error = $failure; appPid = $ownedAppPid; dataRoot = $dataRoot } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'summary.json')
}
