@{
    RootModule           = 'KillerPivot.psm1'
    ModuleVersion        = '1.0.2'
    GUID                 = '0f10d13f-93b6-4bbc-b0ba-9cccf574c630'
    Author               = 'SteveTheKiller'
    CompanyName          = 'Steve the Killer'
    Copyright            = '(c) 2026 SteveTheKiller. All rights reserved.'
    Description          = 'Per-Identity Validated Org-Toggle. Switch between client Microsoft 365 tenants with one command. Verifies the tenant ID after every connect and tears the session down on mismatch, so a wrong-tenant session never survives to the prompt. Stores identifiers only, never credentials.'
    PowerShellVersion    = '5.1'
    CompatiblePSEditions = @('Desktop', 'Core')

    FunctionsToExport = @(
        'Connect-PivotTenant',
        'Disconnect-PivotTenant',
        'Get-PivotContext',
        'Add-PivotTenant',
        'Remove-PivotTenant',
        'Get-PivotTenant'
    )
    AliasesToExport   = @('pivot', 'pvc', 'pvx')
    CmdletsToExport   = @()
    VariablesToExport = @()

    PrivateData = @{
        PSData = @{
            Tags         = @('MSP', 'Microsoft365', 'ExchangeOnline', 'Graph', 'MultiTenant', 'Sysadmin', 'Windows')
            ProjectUri    = 'https://github.com/SteveTheKiller/killer-modules/tree/main/KillerPivot'
            ReleaseNotes = 'Disconnect sessions when connection or tenant validation fails, and report incomplete cleanup.'
        }
    }
}
