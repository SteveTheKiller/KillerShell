param([Parameter(Mandatory = $true)][string]$AssemblyPath, [string]$SavedHeight, [string]$OpenDetails = '0')
$ErrorActionPreference = 'Stop'
if (!$SavedHeight) {
    foreach ($height in '-1', 'NaN', 'Infinity', '-Infinity', '0', '175.5', '1e20') {
        foreach ($open in '0', '1') {
            & powershell.exe -NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File $PSCommandPath -AssemblyPath $AssemblyPath -SavedHeight $height -OpenDetails $open
            if ($LASTEXITCODE -ne 0) { throw "Startup failed for height $height, open $open" }
        }
    }
    Write-Output 'PASS 14 saved-details startup cases (both panes, open and closed)'
    return
}
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml
$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
$references = @($assembly.Location, [Windows.Window].Assembly.Location, [Windows.Media.Visual].Assembly.Location,
    [Windows.Threading.Dispatcher].Assembly.Location, [System.Xaml.XamlReader].Assembly.Location, 'System.dll', 'System.Core.dll')
$source = @'
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using KillerShell.Services;
using KillerShell.Shell;
public static class DetailsSettingsChecks
{
    public static void Run(string height, string open)
    {
        Application.ResourceAssembly = typeof(MainWindow).Assembly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string resource in new[] { "Themes/Dark.xaml", "Controls/Controls.xaml", "Controls/AppStyles.xaml", "Strings/en-US.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/KillerShell;component/" + resource) });
        var settings = new Dictionary<string, string>();
        ThemeManager.GetSetting = key => settings.ContainsKey(key) ? settings[key] : null;
        ThemeManager.SetSetting = (key, value) => settings[key] = value;
        LocaleManager.GetSetting = key => "EnUS";
        LocaleManager.SetSetting = (key, value) => { };
        ThemeManager.Initialize(); LocaleManager.Initialize(); MainWindow.DemoMode = true;
        typeof(MainWindow).GetMethod("PublishGrainTile", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            settings["CheckForUpdatesOnStartup"] = "0";
            settings["CloseToTray"] = "0";
            settings["RememberTabs"] = "0";
            foreach (string pane in new[] { "L", "R" })
            {
                settings["DetailsPaneHeight" + pane] = height;
                settings["DetailsPaneOpen" + pane] = open;
                settings["DetailsPanePosition" + pane] = "bottom";
            }
            var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false };
            window.Close();
        Console.WriteLine("PASS saved height " + height + ", open " + open);
        app.Shutdown();
    }
}
'@
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
$parameters = New-Object System.CodeDom.Compiler.CompilerParameters
$parameters.GenerateInMemory = $true
foreach ($reference in $references) { [void]$parameters.ReferencedAssemblies.Add($reference) }
$compiled = $provider.CompileAssemblyFromSource($parameters, $source)
if ($compiled.Errors.HasErrors) { throw (($compiled.Errors | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine) }
[DetailsSettingsChecks]::Run($SavedHeight, $OpenDetails)
