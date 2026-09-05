@{
    RootModule = ''
    ModuleVersion = '0.1.0'
    GUID = '2d1dfab1-e87e-40e8-b451-dcda062777c3'
    Author = 'Pi Station Desktop contributors'
    Description = 'Pinned PowerShell requirements for Pi Station Desktop UI tests.'
    PowerShellVersion = '7.4'
    RequiredModules = @(
        @{ ModuleName = 'Pester'; RequiredVersion = '5.7.1' }
    )
    FunctionsToExport = @()
    CmdletsToExport = @()
    VariablesToExport = @()
    AliasesToExport = @()
}
