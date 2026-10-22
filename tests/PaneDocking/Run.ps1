param([Parameter(Mandatory = $true)][string]$AssemblyPath, [string]$Output = '', [switch]$VerifySquareTabs)
$ErrorActionPreference = 'Stop'
$bios = Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS'
if ([Environment]::MachineName -ne 'KILLERLAB-W11' -or
    $bios.SystemManufacturer -ne 'Microsoft Corporation' -or $bios.SystemProductName -ne 'Virtual Machine') {
    throw 'Run pane docking checks only in KillerLab-W11.'
}
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml, UIAutomationTypes, UIAutomationProvider
$assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
[Windows.Application].GetField('_resourceAssembly', [Reflection.BindingFlags]'Static,NonPublic').SetValue($null, $assembly)
$app = New-Object Windows.Application
$app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
foreach ($source in 'Themes/Dark.xaml', 'Controls/Controls.xaml', 'Controls/AppStyles.xaml', 'Strings/en-US.xaml') {
    $app.Resources.MergedDictionaries.Add([Windows.Application]::LoadComponent([Uri]::new('/KillerShell;component/' + $source, [UriKind]::Relative)))
}
$refs = @($AssemblyPath, [Windows.Application].Assembly.Location, [Windows.Media.Visual].Assembly.Location,
    [Windows.DependencyObject].Assembly.Location, [System.Xaml.XamlReader].Assembly.Location,
    [Windows.Automation.AutomationElementIdentifiers].Assembly.Location,
    [Windows.Automation.Provider.IInvokeProvider].Assembly.Location, 'System.Core.dll')
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
$parameters = New-Object System.CodeDom.Compiler.CompilerParameters
$parameters.GenerateInMemory = $true
foreach ($reference in $refs + 'System.dll') { [void]$parameters.ReferencedAssemblies.Add($reference) }
$compiled = $provider.CompileAssemblyFromFile($parameters, (Join-Path $PSScriptRoot 'Checks.cs.txt'))
if ($compiled.Errors.HasErrors) { throw (($compiled.Errors | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine) }
[PaneDockingChecks]::Run($Output, [bool]$VerifySquareTabs)
