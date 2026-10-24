Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FlytLaunchSpec {
    if ($env:FLYTV1_EXE) {
        $resolved = (Resolve-Path -LiteralPath $env:FLYTV1_EXE).Path
        if ([IO.Path]::GetExtension($resolved) -eq '.dll') {
            return [pscustomobject]@{ FileName = 'dotnet'; Prefix = @($resolved) }
        }

        return [pscustomobject]@{ FileName = $resolved; Prefix = @() }
    }

    $packaged = Join-Path $PSScriptRoot 'tools/flyt.exe'
    if (Test-Path -LiteralPath $packaged) {
        return [pscustomobject]@{ FileName = $packaged; Prefix = @() }
    }

    $command = Get-Command 'flyt' -ErrorAction SilentlyContinue
    if ($command) {
        return [pscustomobject]@{ FileName = $command.Source; Prefix = @() }
    }

    throw 'Flyt executable not found. Set FLYTV1_EXE to flyt.exe or flyt.dll.'
}

function Invoke-Flyt {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromRemainingArguments)]
        [string[]] $Arguments
    )

    $launch = Get-FlytLaunchSpec
    $allArguments = @($launch.Prefix) + @($Arguments)
    & $launch.FileName @allArguments
    if ($LASTEXITCODE -notin @(0, 2)) {
        throw "Flyt exited with code $LASTEXITCODE."
    }
}

function Test-FlytEnvironment {
    [CmdletBinding()]
    param()

    Invoke-Flyt doctor
}

function Start-FlytDesk {
    [CmdletBinding()]
    param(
        [string] $Name = "flyt-$(Get-Date -Format 'yyyyMMdd-HHmmss')",
        [string] $Path = (Get-Location).Path,
        [string[]] $Session = @('claude:planner', 'claude:builder', 'codex:reviewer'),
        [switch] $DryRun
    )

    $arguments = @(
        'desk', 'start',
        '--name', $Name,
        '--path', (Resolve-Path -LiteralPath $Path).Path,
        '--sessions', ($Session -join ',')
    )
    if ($DryRun) {
        $arguments += '--dry-run'
    }

    Invoke-Flyt @arguments
}

function Enter-FlytDesk {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Name
    )

    Invoke-Flyt desk attach --name $Name
}

function Stop-FlytDesk {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string] $Name
    )

    if ($PSCmdlet.ShouldProcess($Name, 'Stop Flyt desk')) {
        Invoke-Flyt desk stop --name $Name
    }
}

function New-FlytMission {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Goal,
        [string] $Path = (Get-Location).Path
    )

    Invoke-Flyt mission create --goal $Goal --path (Resolve-Path -LiteralPath $Path).Path
}

function Get-FlytMission {
    [CmdletBinding()]
    param(
        [guid] $Id
    )

    if ($Id -eq [guid]::Empty) {
        Invoke-Flyt mission list
        return
    }

    Invoke-Flyt mission show --id $Id.ToString('D')
}

function Approve-FlytMission {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [guid] $Id
    )

    if ($PSCmdlet.ShouldProcess($Id, 'Approve Flyt mission plan')) {
        Invoke-Flyt mission approve --id $Id.ToString('D')
    }
}

Export-ModuleMember -Function @(
    'Invoke-Flyt',
    'Test-FlytEnvironment',
    'Start-FlytDesk',
    'Enter-FlytDesk',
    'Stop-FlytDesk',
    'New-FlytMission',
    'Get-FlytMission',
    'Approve-FlytMission'
)
