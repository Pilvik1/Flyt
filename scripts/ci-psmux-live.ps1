[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $FlytDll,
    [string] $WorkspacePath = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string] $DeskName = 'ci-live-proof'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:FLYTV1_STATE_DIR = Join-Path $env:RUNNER_TEMP 'flyt-live-state'

function Invoke-LiveFlyt {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)

    $output = & dotnet $FlytDll @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -notin @(0, 2)) {
        throw "flyt $($Arguments -join ' ') failed ($LASTEXITCODE): $output"
    }

    return $output.Trim()
}

$templatePath = Join-Path $env:RUNNER_TEMP 'flyt-ci-desk.json'
$roles = @(
    @{ Name = 'planner'; Role = 'planner'; Task = 'Plan'; Tags = @('planning') },
    @{ Name = 'builder'; Role = 'builder'; Task = 'Build'; Tags = @('write', 'expensive') },
    @{ Name = 'researcher'; Role = 'researcher'; Task = 'Research'; Tags = @('research') },
    @{ Name = 'reviewer'; Role = 'reviewer'; Task = 'Review'; Tags = @('review') },
    @{ Name = 'verifier'; Role = 'verifier'; Task = 'Verify'; Tags = @('verify', 'expensive') },
    @{ Name = 'ops'; Role = 'ops'; Task = 'Operate'; Tags = @('shell') }
)
[ordered]@{
    Name = 'ci-full-review'
    Panes = @($roles | ForEach-Object {
        [ordered]@{
            Provider = 'shell'
            Name = $_.Name
            Role = $_.Role
            Task = $_.Task
            WorkingDirectory = $WorkspacePath
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
    $status = Invoke-LiveFlyt desk status --json | ConvertFrom-Json
    $desk = @($status.registered | Where-Object sessionName -eq $DeskName)
    if ($desk.Count -ne 1 -or @($desk[0].panes).Count -ne 6) {
        throw 'Live psmux proof did not re-adopt exactly six panes.'
    }
    $unhealthy = @($desk[0].panes | Where-Object status -in @('Missing', 'Failed', 'Exited', 'Stopped'))
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

    Invoke-LiveFlyt desk stop --name $DeskName | Write-Host
    Invoke-LiveFlyt desk reopen --name $DeskName | Write-Host
    Start-Sleep -Milliseconds 750
    $reopened = Invoke-LiveFlyt desk status --json | ConvertFrom-Json
    $reopenedDesk = @($reopened.registered | Where-Object sessionName -eq $DeskName)
    if ($reopenedDesk.Count -ne 1 -or @($reopenedDesk[0].panes).Count -ne 6) {
        throw 'Stopped Desk did not reopen with all six pane identities.'
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

Write-Host 'Live Windows psmux proof passed: six panes, re-adoption, send, queue, two-stage handoff, history, stop, and reopen.'
