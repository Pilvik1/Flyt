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

function Get-FlytDesk {
    [CmdletBinding()]
    param(
        [switch] $Json
    )

    $arguments = @('desk', 'status')
    if ($Json) {
        $arguments += '--json'
    }

    Invoke-Flyt @arguments
}

function Set-FlytDeskPaneFocus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Name,
        [Parameter(Mandatory)]
        [string] $Pane
    )

    Invoke-Flyt desk focus --name $Name --pane $Pane
}

function Restart-FlytDeskPane {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string] $Name,
        [Parameter(Mandatory)]
        [string] $Pane
    )

    if ($PSCmdlet.ShouldProcess("$Name/$Pane", 'Restart Flyt desk pane')) {
        Invoke-Flyt desk restart-pane --name $Name --pane $Pane
    }
}

function Get-FlytDeskPaneLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Name,
        [Parameter(Mandatory)]
        [string] $Pane,
        [ValidateRange(1, 10000)]
        [int] $Lines = 200
    )

    Invoke-Flyt desk logs --name $Name --pane $Pane --lines $Lines
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
        [string] $Path = (Get-Location).Path,
        [ValidateSet('manual', 'bounded', 'auto')]
        [string] $Profile = 'bounded'
    )

    Invoke-Flyt mission create --goal $Goal --path (Resolve-Path -LiteralPath $Path).Path --profile $Profile
}

function Set-FlytMissionPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [guid] $Id,
        [Parameter(Mandatory)]
        [string[]] $AcceptanceCriterion,
        [Parameter(Mandatory)]
        [string[]] $Step,
        [Parameter(Mandatory)]
        [string] $VerificationCommand
    )

    Invoke-Flyt mission plan `
        --id ($Id.ToString('D')) `
        --acceptance ($AcceptanceCriterion -join '|') `
        --steps ($Step -join '|') `
        --verify $VerificationCommand
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

        Invoke-Flyt mission show --id ($Id.ToString('D'))
}

function Approve-FlytMission {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [guid] $Id
    )

    if ($PSCmdlet.ShouldProcess($Id, 'Approve Flyt mission plan')) {
        Invoke-Flyt mission approve --id ($Id.ToString('D'))
    }
}

function Suspend-FlytMission {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][guid] $Id)

    if ($PSCmdlet.ShouldProcess($Id, 'Pause Flyt mission')) {
        Invoke-Flyt mission pause --id ($Id.ToString('D'))
    }
}

function Resume-FlytMission {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][guid] $Id)

    if ($PSCmdlet.ShouldProcess($Id, 'Resume Flyt mission')) {
        Invoke-Flyt mission resume --id ($Id.ToString('D'))
    }
}

function Stop-FlytMission {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][guid] $Id)

    if ($PSCmdlet.ShouldProcess($Id, 'Cancel Flyt mission')) {
        Invoke-Flyt mission cancel --id ($Id.ToString('D'))
    }
}

Export-ModuleMember -Function @(
    'Invoke-Flyt',
    'Test-FlytEnvironment',
    'Start-FlytDesk',
    'Enter-FlytDesk',
    'Get-FlytDesk',
    'Set-FlytDeskPaneFocus',
    'Restart-FlytDeskPane',
    'Get-FlytDeskPaneLog',
    'Stop-FlytDesk',
    'New-FlytMission',
    'Set-FlytMissionPlan',
    'Get-FlytMission',
    'Approve-FlytMission',
    'Suspend-FlytMission',
    'Resume-FlytMission',
    'Stop-FlytMission'
)
