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
    param(
        [switch] $Interactive,
        [string] $Template,
        [string] $Receipt,
        [switch] $AllowSmallTerminal,
        [switch] $KeepDesk
    )

    $arguments = @('doctor')
    if ($Interactive) { $arguments += '--interactive' }
    if ($Template) { $arguments += @('--template', $Template) }
    if ($Receipt) { $arguments += @('--receipt', $Receipt) }
    if ($AllowSmallTerminal) { $arguments += '--allow-small-terminal' }
    if ($KeepDesk) { $arguments += '--keep-desk' }
    Invoke-Flyt @arguments
}

function Start-FlytDesk {
    [CmdletBinding()]
    param(
        [string] $Name = "flyt-$(Get-Date -Format 'yyyyMMdd-HHmmss')",
        [string] $Path = (Get-Location).Path,
        [string[]] $Session = @('claude:planner', 'claude:builder', 'codex:reviewer'),
        [string] $Template,
        [switch] $DryRun
    )

    $arguments = @(
        'desk', 'start',
        '--name', $Name,
        '--path', (Resolve-Path -LiteralPath $Path).Path
    )
    if ($Template) {
        $arguments += @('--template', $Template)
    } else {
        $arguments += @('--sessions', ($Session -join ','))
    }
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
        [string] $Pane,
        [switch] $Next
    )

    if ($Next) {
        Invoke-Flyt desk focus --name $Name --next
    } elseif ($Pane) {
        Invoke-Flyt desk focus --name $Name --pane $Pane
    } else {
        throw 'Provide -Pane or -Next.'
    }
}

function Watch-FlytDesk {
    [CmdletBinding()]
    param(
        [string] $Name,
        [string] $Role,
        [string] $Tag,
        [ValidateRange(250, 60000)]
        [int] $IntervalMilliseconds = 1000,
        [switch] $Notify
    )

    $arguments = @('desk', 'watch', '--interval', $IntervalMilliseconds)
    if ($Name) { $arguments += @('--name', $Name) }
    if ($Role) { $arguments += @('--role', $Role) }
    if ($Tag) { $arguments += @('--tag', $Tag) }
    if ($Notify) { $arguments += '--notify' }
    Invoke-Flyt @arguments
}

function Send-FlytDeskText {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Text,
        [string] $Pane,
        [string] $Role,
        [string] $Tag,
        [string] $State,
        [switch] $All,
        [switch] $IncludeShell,
        [switch] $NoSubmit
    )

    $arguments = @('desk', 'send', '--name', $Name, '--text', $Text)
    if ($Pane) { $arguments += @('--pane', $Pane) }
    if ($Role) { $arguments += @('--role', $Role) }
    if ($Tag) { $arguments += @('--tag', $Tag) }
    if ($State) { $arguments += @('--state', $State) }
    if ($All) { $arguments += '--all' }
    if ($IncludeShell) { $arguments += '--include-shell' }
    if ($NoSubmit) { $arguments += '--no-submit' }
    if ($PSCmdlet.ShouldProcess($Name, 'Send text to selected Flyt panes')) {
        $arguments += '--yes'
        Invoke-Flyt @arguments
    }
}

function Repair-FlytDesk {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string] $Name)

    if ($PSCmdlet.ShouldProcess($Name, 'Recover failed or missing Flyt panes')) {
        Invoke-Flyt desk recover --name $Name --apply
    }
}

function Open-FlytDesk {
    [CmdletBinding()]
    param(
        [string] $Name,
        [string] $Path = (Get-Location).Path,
        [string] $Template
    )

    $arguments = @('desk', 'open', '--path', (Resolve-Path -LiteralPath $Path).Path)
    if ($Name) { $arguments += @('--name', $Name) }
    if ($Template) { $arguments += @('--template', $Template) }
    Invoke-Flyt @arguments
}

function Add-FlytDeskWork {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Pane,
        [Parameter(Mandatory)][string] $Text,
        [switch] $Expensive
    )

    $arguments = @('desk', 'queue', 'add', '--name', $Name, '--pane', $Pane, '--text', $Text)
    if ($Expensive) { $arguments += '--expensive' }
    Invoke-Flyt @arguments
}

function Get-FlytDeskWork {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Name,
        [ValidateSet('Queued', 'Dispatched', 'Completed', 'Failed', 'Cancelled')]
        [string] $Status,
        [ValidateRange(1, 1000)][int] $Limit = 100,
        [switch] $Json
    )

    $arguments = @('desk', 'queue', 'list', '--name', $Name, '--limit', $Limit)
    if ($Status) { $arguments += @('--status', $Status) }
    if ($Json) { $arguments += '--json' }
    Invoke-Flyt @arguments
}

function Invoke-FlytDeskQueue {
    [CmdletBinding()]
    param([string] $Name)

    $arguments = @('desk', 'queue', 'dispatch')
    if ($Name) { $arguments += @('--name', $Name) }
    Invoke-Flyt @arguments
}

function Stop-FlytDeskWork {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][guid] $Id)

    if ($PSCmdlet.ShouldProcess($Id, 'Cancel queued Flyt work')) {
        Invoke-Flyt desk queue cancel --id ($Id.ToString('D'))
    }
}

function Get-FlytDeskHistory {
    [CmdletBinding()]
    param(
        [string] $Name,
        [string] $Search,
        [ValidateRange(1, 1000)][int] $Limit = 100,
        [switch] $Json
    )

    $arguments = @('desk', 'history', '--limit', $Limit)
    if ($Name) { $arguments += @('--name', $Name) }
    if ($Search) { $arguments += @('--search', $Search) }
    if ($Json) { $arguments += '--json' }
    Invoke-Flyt @arguments
}

function Test-FlytDeskNotification {
    [CmdletBinding()]
    param(
        [string] $Title = 'Flyt Desk',
        [string] $Text = 'Confirm that this Windows notification is visible.'
    )

    Invoke-Flyt desk notify --title $Title --text $Text
}

function Get-FlytDeskFailure {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Name, [switch] $Json)

    $arguments = @('desk', 'failures', '--name', $Name)
    if ($Json) { $arguments += '--json' }
    Invoke-Flyt @arguments
}

function New-FlytDeskTemplate {
    [CmdletBinding()]
    param([string] $Path = (Get-Location).Path)

    Invoke-Flyt desk template init --path (Resolve-Path -LiteralPath $Path).Path
}

function New-FlytDeskHandoff {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $From,
        [Parameter(Mandatory)][string] $To,
        [Parameter(Mandatory)][string] $Goal,
        [Parameter(Mandatory)][string] $Action,
        [string[]] $Decision,
        [string[]] $Verification,
        [string[]] $Blocker,
        [switch] $Send
    )

    $arguments = @(
        'desk', 'handoff', '--name', $Name, '--from', $From, '--to', $To,
        '--goal', $Goal, '--action', $Action
    )
    if ($Decision) { $arguments += @('--decisions', ($Decision -join '|')) }
    if ($Verification) { $arguments += @('--verification', ($Verification -join '|')) }
    if ($Blocker) { $arguments += @('--blockers', ($Blocker -join '|')) }
    if ($Send) { $arguments += '--send' }
    Invoke-Flyt @arguments
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
    'Watch-FlytDesk',
    'Send-FlytDeskText',
    'Repair-FlytDesk',
    'Open-FlytDesk',
    'Add-FlytDeskWork',
    'Get-FlytDeskWork',
    'Invoke-FlytDeskQueue',
    'Stop-FlytDeskWork',
    'Get-FlytDeskHistory',
    'Test-FlytDeskNotification',
    'Get-FlytDeskFailure',
    'New-FlytDeskTemplate',
    'New-FlytDeskHandoff',
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
