[CmdletBinding()]
param(
    [string] $WorkspacePath = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string] $DeskName = "flyt-phase0-$(Get-Date -Format 'yyyyMMdd-HHmmss')",
    [switch] $LaunchDesk,
    [string] $EvidencePath = (Join-Path $env:LOCALAPPDATA "FlytV1/phase0/$DeskName.json")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$required = @('pwsh', 'git', 'psmux', 'claude', 'codex', 'dotnet')
$results = @($required | ForEach-Object {
    $name = $_
    $command = Get-Command $name -ErrorAction SilentlyContinue
    [pscustomobject]@{
        Tool = $name
        Found = [bool]$command
        Path = if ($command) { $command.Source } else { $null }
        Version = if ($command) {
            ((& $command.Source --version 2>&1) | Out-String).Trim()
        } else {
            $null
        }
    }
})

$results | Format-Table -AutoSize
if ($results.Found -contains $false) {
    throw 'Phase 0 prerequisites are missing. Install or repair them before the live psmux proof.'
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repositoryRoot
try {
    dotnet restore FlytV1.slnx
    dotnet build FlytV1.slnx --configuration Release --no-restore
    dotnet test FlytV1.slnx --configuration Release --no-build

    $flytDll = Join-Path $repositoryRoot 'src/Flyt.Cli/bin/Release/net10.0/flyt.dll'
    $sessions = 'claude:planner,claude:builder,claude:researcher,codex:reviewer,codex:verifier,shell:ops'
    dotnet $flytDll desk start `
        --name $DeskName `
        --path $WorkspacePath `
        --sessions $sessions `
        --dry-run

    if ($LaunchDesk) {
        dotnet $flytDll desk start `
            --name $DeskName `
            --path $WorkspacePath `
            --sessions $sessions
    }
}
finally {
    Pop-Location
}

$evidence = [ordered]@{
    CapturedAt = (Get-Date).ToUniversalTime().ToString('O')
    Machine = $env:COMPUTERNAME
    PowerShell = $PSVersionTable.PSVersion.ToString()
    Repository = $repositoryRoot
    Workspace = (Resolve-Path -LiteralPath $WorkspacePath).Path
    DeskName = $DeskName
    DeskLaunched = [bool]$LaunchDesk
    Tools = $results
    LiveGates = [ordered]@{
        SixSessionsVisible = $null
        ClaudeOnlyTaskCompleted = $null
        MixedProviderTaskCompleted = $null
        DetachReattachSucceeded = $null
        RestartReadoptionSucceeded = $null
        InterventionCount = $null
        ProviderFailures = $null
        ConflictingWrites = $null
        RecoveryMinutes = $null
        Notes = $null
    }
}

$evidenceDirectory = Split-Path -Parent $EvidencePath
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
$evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $EvidencePath -Encoding utf8

Write-Host "Automated Phase 0 checks passed. Evidence template: $EvidencePath"
if ($LaunchDesk) {
    Write-Warning 'Complete the live gates in the evidence file after two real tasks, detach/reattach, and controller restart. Null is not a pass.'
} else {
    Write-Host 'Re-run with -LaunchDesk to start the six-session live proof.'
}
