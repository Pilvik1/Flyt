@{
    RootModule = 'Flyt.psm1'
    ModuleVersion = '0.1.0'
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
        'Stop-FlytDesk',
        'New-FlytMission',
        'Get-FlytMission',
        'Approve-FlytMission'
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

