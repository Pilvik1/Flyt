@{
    RootModule = 'Flyt.psm1'
    ModuleVersion = '0.2.0'
    GUID = '6f86803f-14bf-483b-80fd-b6376b775481'
    Author = 'Håkon Pilvik'
    CompanyName = 'HomeBase'
    Copyright = '(c) 2026 Håkon Pilvik. All rights reserved.'
    Description = 'PowerShell command surface for the FlytV1 local multi-agent console.'
    PowerShellVersion = '7.0'
    FunctionsToExport = @(
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
    CmdletsToExport = @()
    VariablesToExport = @()
    AliasesToExport = @()
    PrivateData = @{
        PSData = @{
            Tags = @('AI', 'Agents', 'Claude', 'Codex', 'psmux')
            ProjectUri = 'https://github.com/Pilvik1/FlytV1'
        }
    }
}
