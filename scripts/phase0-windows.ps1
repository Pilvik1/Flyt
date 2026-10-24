[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$required = @('pwsh', 'git', 'psmux', 'claude', 'codex', 'dotnet')
$results = foreach ($name in $required) {
    $command = Get-Command $name -ErrorAction SilentlyContinue
    [pscustomobject]@{
        Tool = $name
        Found = [bool]$command
        Path = if ($command) { $command.Source } else { $null }
    }
}

$results | Format-Table -AutoSize
if ($results.Found -contains $false) {
    throw 'Phase 0 prerequisites are missing. Install or repair them before the live psmux proof.'
}

Write-Host 'Prerequisites found. Next: run Test-FlytEnvironment and the six-session Desk smoke test.'

