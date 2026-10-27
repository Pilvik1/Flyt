[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $FlytDll,
    [string] $WorkspacePath = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string] $DeskName = 'ci-live-proof'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:FLYT_STATE_DIR = Join-Path $env:RUNNER_TEMP 'flyt-live-state'

function Invoke-LiveFlyt {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)

    $output = & dotnet $FlytDll @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -notin @(0, 2)) {
        throw "flyt $($Arguments -join ' ') failed ($LASTEXITCODE): $output"
    }

    return $output.Trim()
}

$configurationWorkspace = Join-Path $env:RUNNER_TEMP 'flyt-config-workspace'
$configurationRepos = @(
    (Join-Path $configurationWorkspace 'backend'),
    (Join-Path $configurationWorkspace 'frontend')
)
$null = New-Item -ItemType Directory -Path $configurationRepos -Force
Invoke-LiveFlyt desk configure `
    --path $configurationWorkspace `
    --name ci-multi-repo `
    --panes 2 `
    --providers 'shell,shell' `
    --working-directories ($configurationRepos -join '|') `
    --force | Write-Host
$configuredTemplate = Get-Content (Join-Path $configurationWorkspace '.flyt/desk.json') -Raw | ConvertFrom-Json
$configuredPaths = @($configuredTemplate.Panes | ForEach-Object WorkingDirectory)
if ($configuredTemplate.Name -ne 'ci-multi-repo' `
    -or $configuredPaths.Count -ne 2 `
    -or $configuredPaths[0] -ne $configurationRepos[0] `
    -or $configuredPaths[1] -ne $configurationRepos[1]) {
    throw "Guided configuration did not preserve per-pane repositories: $($configuredPaths -join ', ')"
}

$templatePath = Join-Path $env:RUNNER_TEMP 'flyt-ci-desk.json'
$roles = @(
    @{ Name = 'planner'; Role = 'planner'; Task = 'Plan'; Tags = @('planning') },
    @{ Name = 'builder'; Role = 'builder'; Task = 'Build'; Tags = @('write', 'expensive') },
    @{ Name = 'researcher'; Role = 'researcher'; Task = 'Research'; Tags = @('research') },
    @{ Name = 'reviewer'; Role = 'reviewer'; Task = 'Review'; Tags = @('review') },
    @{ Name = 'verifier'; Role = 'verifier'; Task = 'Verify'; Tags = @('verify', 'expensive') },
    @{ Name = 'ops'; Role = 'ops'; Task = 'Operate'; Tags = @('shell') },
    @{ Name = 'shell-2'; Role = 'shell'; Task = 'Shell 2'; Tags = @('shell') },
    @{ Name = 'shell-3'; Role = 'shell'; Task = 'Shell 3'; Tags = @('shell') },
    @{ Name = 'shell-4'; Role = 'shell'; Task = 'Shell 4'; Tags = @('shell') },
    @{ Name = 'shell-5'; Role = 'shell'; Task = 'Shell 5'; Tags = @('shell') }
)
$roleIndex = 0
[ordered]@{
    Name = 'ci-full-review'
    Panes = @($roles | ForEach-Object {
        $workingDirectory = $configurationRepos[$roleIndex % $configurationRepos.Count]
        $roleIndex++
        [ordered]@{
            Provider = 'shell'
            Name = $_.Name
            Role = $_.Role
            Task = $_.Task
            WorkingDirectory = $workingDirectory
            Command = "Write-Output 'FLYT_READY'"
            Color = $null
            Tags = $_.Tags
            ProviderSessionId = $null
        }
    })
    Tags = @('ci', 'full-review')
    MaxConcurrentExpensiveTasks = 1
    MemoryWarningMegabytes = 12000
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $templatePath -Encoding utf8

try {
    Invoke-LiveFlyt desk start `
        --name $DeskName `
        --path $WorkspacePath `
        --template $templatePath | Write-Host

    Start-Sleep -Milliseconds 750
    Invoke-LiveFlyt desk open --name $DeskName --no-attach | Write-Host
    $borderStatus = (& psmux show-options -t $DeskName -g -v pane-border-status 2>&1 | Out-String).Trim()
    $borderLines = (& psmux show-options -t $DeskName -g -v pane-border-lines 2>&1 | Out-String).Trim()
    $borderFormat = (& psmux show-options -t $DeskName -g -v pane-border-format 2>&1 | Out-String).Trim()
    $inactiveStyle = (& psmux show-options -t $DeskName -g -v window-style 2>&1 | Out-String).Trim()
    $statusLeft = (& psmux show-options -t $DeskName -g -v status-left 2>&1 | Out-String).Trim()
    $bindings = (& psmux list-keys 2>&1 | Out-String)
    if ($borderStatus -ne 'top' `
        -or $borderLines -ne 'heavy' `
        -or $borderFormat -notmatch 'fg=blue' `
        -or $borderFormat -notmatch 'bg=blue' `
        -or $borderFormat -notmatch 'fg=magenta' `
        -or $borderFormat -notmatch 'bg=magenta' `
        -or $borderFormat -notmatch 'pane_active' `
        -or $inactiveStyle -notmatch 'dim=12' `
        -or $statusLeft -notmatch 'FLYT' `
        -or $bindings -notmatch 'desk action.+--action send' `
        -or $bindings -notmatch '(?m)^bind-key\s+-T\s+prefix\s+n\s+display-popup.+--action add-pane' `
        -or $bindings -notmatch '(?m)^bind-key\s+-T\s+prefix\s+N\s+display-popup.+--action add-pane' `
        -or $bindings -notmatch 'M-6.+select-pane' `
        -or $bindings -notmatch 'M-0.+select-pane') {
        throw "Integrated cockpit chrome/controls were not configured: borderStatus=$borderStatus border=$borderLines format=$borderFormat window=$inactiveStyle status=$statusLeft"
    }
    psmux list-panes -t $DeskName -F '#{pane_id}|#{pane_title}|#{pane_current_command}|#{pane_dead}' |
        ForEach-Object { Write-Host "PSMUX: $_" }
    $status = Invoke-LiveFlyt desk status --json | ConvertFrom-Json
    $desk = @($status.registered | Where-Object sessionName -eq $DeskName)
    if ($desk.Count -ne 1 -or @($desk[0].panes).Count -ne 10) {
        throw 'Live psmux proof did not re-adopt exactly ten panes.'
    }
    $registeredPaths = @($desk[0].panes | ForEach-Object workingDirectory | Sort-Object -Unique)
    $livePaths = @(& psmux list-panes -t $DeskName -F '#{pane_current_path}' | Sort-Object -Unique)
    foreach ($expectedPath in $configurationRepos) {
        if ($expectedPath -notin $registeredPaths -or $expectedPath -notin $livePaths) {
            throw "Live multi-repository proof did not launch panes in $expectedPath. Registered: $($registeredPaths -join ', '); live: $($livePaths -join ', ')"
        }
    }
    $unhealthy = @($desk[0].panes | Where-Object { $_.status -in @('Missing', 'Failed', 'Exited', 'Stopped') })
    if ($unhealthy.Count -gt 0) {
        throw "Live psmux proof found unhealthy panes: $($unhealthy.name -join ', ')."
    }
    $expectedColors = @{ planner = 'cyan'; builder = 'blue'; reviewer = 'magenta'; verifier = 'green' }
    foreach ($role in $expectedColors.Keys) {
        $pane = @($desk[0].panes | Where-Object role -eq $role)
        if ($pane.Count -ne 1 -or $pane[0].color -ne $expectedColors[$role]) {
            throw "Role colour proof failed for $role."
        }
    }
    if (@($desk[0].panes | Where-Object name -eq 'builder')[0].task -ne 'Build') {
        throw 'Cockpit pane identity did not preserve the builder task.'
    }

    $builderPaneId = @($desk[0].panes | Where-Object name -eq 'builder')[0].paneId
    Invoke-LiveFlyt desk action `
        --name $DeskName `
        --pane-id $builderPaneId `
        --action send `
        --text "Write-Output 'FLYT_ACTION_PROOF'" `
        --yes | Write-Host
    Start-Sleep -Milliseconds 500
    $actionLogs = Invoke-LiveFlyt desk logs --name $DeskName --pane builder --lines 80
    if ($actionLogs -notmatch 'FLYT_ACTION_PROOF') {
        throw 'Cockpit popup action path did not deliver targeted text.'
    }

    Invoke-LiveFlyt desk send `
        --name $DeskName `
        --pane builder `
        --include-shell `
        --text "Write-Output 'FLYT_PSMUX_PROOF'" | Write-Host
    Start-Sleep -Milliseconds 500
    $logs = Invoke-LiveFlyt desk logs --name $DeskName --pane builder --lines 80
    if ($logs -notmatch 'FLYT_PSMUX_PROOF') {
        throw 'Safe targeted send did not appear in pane output.'
    }

    Invoke-LiveFlyt desk queue add `
        --name $DeskName `
        --pane verifier `
        --text "Write-Output 'FLYT_QUEUE_PROOF'" `
        --expensive | Write-Host
    Invoke-LiveFlyt desk queue dispatch --name $DeskName | Write-Host
    Start-Sleep -Milliseconds 500
    $queueLogs = Invoke-LiveFlyt desk logs --name $DeskName --pane verifier --lines 80
    if ($queueLogs -notmatch 'FLYT_QUEUE_PROOF') {
        throw 'Queued work was not dispatched to the selected pane.'
    }

    Invoke-LiveFlyt desk handoff `
        --name $DeskName `
        --from builder `
        --to reviewer `
        --goal 'CI handoff proof' `
        --action 'Read the generated bundle' `
        --verification 'targeted send passed' `
        --send | Write-Host
    Invoke-LiveFlyt desk handoff `
        --name $DeskName `
        --from reviewer `
        --to verifier `
        --goal 'CI verification handoff proof' `
        --action 'Verify the supplied evidence' `
        --verification 'review handoff delivered' `
        --send | Write-Host
    $handoffs = @(Get-ChildItem (Join-Path $WorkspacePath '.flyt/handoffs') -Filter '*.json')
    if ($handoffs.Count -lt 2) {
        throw 'Builder to reviewer to verifier handoff artifacts were not created.'
    }

    $history = Invoke-LiveFlyt desk history --name $DeskName --search handoff --json | ConvertFrom-Json
    if (@($history).Count -lt 2) {
        throw 'Searchable Desk history did not return both handoff events.'
    }

    Invoke-LiveFlyt desk focus --name $DeskName --pane reviewer | Write-Host
    Invoke-LiveFlyt desk open --name $DeskName --no-attach | Write-Host
    Invoke-LiveFlyt desk stop --name $DeskName | Write-Host
    Invoke-LiveFlyt desk reopen --name $DeskName | Write-Host
    Start-Sleep -Milliseconds 750
    Invoke-LiveFlyt desk open --name $DeskName --no-attach | Write-Host
    $reopened = Invoke-LiveFlyt desk status --json | ConvertFrom-Json
    $reopenedDesk = @($reopened.registered | Where-Object sessionName -eq $DeskName)
    if ($reopenedDesk.Count -ne 1 -or @($reopenedDesk[0].panes).Count -ne 10) {
        throw 'Stopped Desk did not reopen with all ten pane identities.'
    }
    $activeTitle = (& psmux list-panes -t $DeskName -F '#{pane_active}|#{pane_title}' |
        Where-Object { $_ -like '1|*' } | Out-String).Trim()
    if ($activeTitle -notmatch '^1\|reviewer · REVIEWER') {
        throw "Focused pane was not restored after reopen: $activeTitle"
    }

    & psmux kill-session -t $DeskName
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not simulate unexpected psmux session loss.'
    }
    $missing = Invoke-LiveFlyt desk status --json | ConvertFrom-Json
    $missingDesk = @($missing.registered | Where-Object sessionName -eq $DeskName)
    if ($missingDesk.Count -ne 1 -or $missingDesk[0].status -ne 'Missing') {
        throw 'Unexpected psmux loss was not reconciled to Missing.'
    }
    Invoke-LiveFlyt desk reopen --name $DeskName | Write-Host
    Start-Sleep -Milliseconds 750
    $recoveredPaneIds = @(& psmux list-panes -t $DeskName -F '#{pane_id}')
    if ($recoveredPaneIds.Count -ne 10 -or @($recoveredPaneIds | Sort-Object -Unique).Count -ne 10) {
        throw 'Unexpected-loss recovery did not restore ten unique pane IDs.'
    }
    $recoveredActiveTitle = (& psmux list-panes -t $DeskName -F '#{pane_active}|#{pane_title}' |
        Where-Object { $_ -like '1|*' } | Out-String).Trim()
    if ($recoveredActiveTitle -notmatch '^1\|reviewer · REVIEWER') {
        throw "Unexpected-loss recovery did not restore focus: $recoveredActiveTitle"
    }
}
finally {
    try {
        Invoke-LiveFlyt desk stop --name $DeskName | Write-Host
    }
    catch {
        Write-Warning "Cleanup failed: $_"
    }
}

Write-Host 'Live Windows psmux proof passed: ten panes across multiple repositories, re-adoption, send, queue, handoffs, history, stop/reopen, and unexpected-loss recovery.'
