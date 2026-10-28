@{
    RootModule = 'Flyt.psm1'
    ModuleVersion = '0.5.8'
    GUID = '6f86803f-14bf-483b-80fd-b6376b775481'
    Author = 'Håkon Pilvik'
    CompanyName = 'HomeBase'
    Copyright = '(c) 2026 Håkon Pilvik. MIT licensed.'
    Description = 'PowerShell command surface for the Flyt local multi-agent console.'
    PowerShellVersion = '7.0'
    FunctionsToExport = @(
        'Invoke-Flyt',
        'Enable-FlytCompletion',
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
    CmdletsToExport = @()
    VariablesToExport = @()
    AliasesToExport = @()
    PrivateData = @{
        PSData = @{
            Tags = @('AI', 'Agents', 'Claude', 'Codex', 'psmux')
            LicenseUri = 'https://github.com/Pilvik1/Flyt/blob/main/LICENSE'
            ProjectUri = 'https://github.com/Pilvik1/Flyt'
        }
    }
}
