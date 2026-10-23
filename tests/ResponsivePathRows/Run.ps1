param(
    [Parameter(Mandatory = $true)][string]$AssemblyPath,
    [Parameter(Mandatory = $true)][string]$CaptureDirectory
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
[Windows.Application].GetField('_resourceAssembly', [Reflection.BindingFlags]'Static,NonPublic').SetValue($null, $assembly)
# A plain Application with App.xaml's dictionaries, not KillerShell.App: App queues OnStartup,
# which builds and shows a real main window and reads and writes the user's saved settings.
$app = New-Object System.Windows.Application
$appXaml = [xml](Get-Content -Raw (Join-Path $PSScriptRoot '..\..\App.xaml'))
$appNs = New-Object System.Xml.XmlNamespaceManager($appXaml.NameTable)
$appNs.AddNamespace('p', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$appResources = New-Object System.Windows.ResourceDictionary
foreach ($source in $appXaml.SelectNodes('//p:ResourceDictionary.MergedDictionaries/p:ResourceDictionary/@Source', $appNs)) {
    $appResources.MergedDictionaries.Add([Windows.Application]::LoadComponent([Uri]::new('/KillerShell;component/' + $source.Value, [UriKind]::Relative)))
}
$app.Resources = $appResources
$app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
$static = [Reflection.BindingFlags]'Static,NonPublic,Public'
$flags = [Reflection.BindingFlags]'Instance,NonPublic,Public'
$manager = $assembly.GetType('KillerShell.Services.ThemeManager')
$themeType = $assembly.GetType('KillerShell.Services.Theme')
$localeManager = $assembly.GetType('KillerShell.Services.LocaleManager')
$localeType = $assembly.GetType('KillerShell.Services.Locale')
$manager.GetMethod('LoadDict', $static).Invoke($null, @([Enum]::Parse($themeType, 'Malaise')))
$type = $assembly.GetType('KillerShell.Shell.MainWindow')
$type.GetProperty('DemoMode', $static).SetValue($null, $true, $null)
$window = [Activator]::CreateInstance($type)
$root = $window.Content
$script:checks = 0
function Field($name) { $type.GetField($name, $flags).GetValue($window) }
function PaneField($pane, $name) { $pane.GetType().GetField($name, $flags).GetValue($pane) }
function Call($name, $values = @()) { $type.GetMethod($name, $flags).Invoke($window, $values) }
function Check($condition, $message) {
    if (-not $condition) { throw $message }
    $script:checks++
}
function Layout($width) {
    for ($pass = 0; $pass -lt 4; $pass++) {
        $root.Measure([Windows.Size]::new($width, 640))
        $root.Arrange([Windows.Rect]::new(0, 0, $width, 640))
        $root.UpdateLayout()
        $root.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ApplicationIdle)
    }
}
function Set-Dual($dual, $columns = $true) {
    $type.GetField('<DualPane>k__BackingField', $flags).SetValue($window, $dual)
    $type.GetField('_paneSideBySide', $flags).SetValue($window, $columns)
    (Field 'RightPane').Visibility = $(if ($dual) { [Windows.Visibility]::Visible } else { [Windows.Visibility]::Collapsed })
    Call 'ApplyPaneLayout' @($false)
}
function Capture($name, $element) {
    $width = [int][Math]::Ceiling($element.ActualWidth)
    $height = [int][Math]::Ceiling($element.ActualHeight)
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.DrawRectangle([Windows.Media.VisualBrush]::new($element), $null, [Windows.Rect]::new(0, 0, $width, $height))
    $drawing.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($width, $height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create((Join-Path $CaptureDirectory "$name.png"))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}
New-Item -ItemType Directory -Force -Path $CaptureDirectory | Out-Null
$type.GetField('_inWindowSizeMove', $flags).SetValue($window, $true)
foreach ($name in @('TreeCol', 'TreeGapCol', 'SearchCol')) {
    (Field $name).MinWidth = 0
    (Field $name).Width = [Windows.GridLength]::new(0)
}
$left = Field 'LeftPane'
$right = Field 'RightPane'
$path = PaneField $left 'ScopeBar'
$label = PaneField $left 'ScopePathLabel'
$address = PaneField $left 'AddressBox'
$strip = PaneField $left 'ToolStrip'
$overflow = PaneField $left 'OverflowPanel'
$label.Text = 'C:\Users\Demo\Documents\Quarterly reports\2026 final report'
$fileButtons = @('ViewListBtn', 'ViewIconsBtn', 'ViewDetailsBtn', 'FileDensityBtn', 'SortControlsHost', 'ExpandAllButton', 'ShowHiddenBtn', 'FoldersTopBtn', 'DetailsPaneBtn', 'PipeBtn', 'ExportBtn')
$originals = @{}
foreach ($name in $fileButtons) { $originals[$name] = PaneField $left $name }
Set-Dual $false
Layout 1200
Check ([Windows.Controls.Grid]::GetRow($path) -eq 0) 'Wide file pane did not use one row'
Check ($label.ActualWidth -ge 259) 'Wide filename lost its minimum readable width'
Check ([Math]::Abs((PaneField $left 'LocationRow').ActualHeight - 32) -lt 1) 'Wide file toolbar height changed'
Capture 'file-wide' (PaneField $left 'LocationRow')
Layout 600
Check ([Windows.Controls.Grid]::GetRow($path) -eq 0) 'Narrow file pane stopped using one row'
Check ($overflow.Children.Count -gt 0) 'Narrow toolbar did not move secondary commands into overflow'
Check ($label.ActualWidth -ge 259) 'Narrow toolbar squeezed the address before its commands overflowed'
Check ($label.FontSize -eq 12) 'Filename font was shrunk'
Check ([Math]::Abs((PaneField $left 'LocationRow').ActualHeight - 32) -lt 1) 'Narrow file toolbar height changed'
Capture 'file-narrow' (PaneField $left 'LocationRow')
Call 'BeginEditAddress'
# BeginEditAddress queues its own focus and select-all; let that run before setting the edit state.
$root.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ApplicationIdle)
$address.Text = 'C:\Users\Demo\Documents\draft path'
$address.Select(8, 5)
[Windows.Input.FocusManager]::SetFocusedElement($window, $address)
foreach ($width in @(1200, 600, 1200, 600)) { Layout $width }
Check ([object]::ReferenceEquals($address, (PaneField $left 'AddressBox'))) 'Resize replaced the address editor'
Check ($address.Text -eq 'C:\Users\Demo\Documents\draft path' -and $address.SelectionStart -eq 8 -and $address.SelectionLength -eq 5) 'Resize changed the address edit'
Check ([object]::ReferenceEquals([Windows.Input.FocusManager]::GetFocusedElement($window), $address)) 'Resize lost logical address focus'
Call 'EndEditAddress'
for ($round = 0; $round -lt 8; $round++) {
    foreach ($width in @(600, 820, 800, 820, 1200)) {
        Layout $width
        $row = [Windows.Controls.Grid]::GetRow($path)
        for ($pass = 0; $pass -lt 5; $pass++) {
            Layout $width
            Check ([Windows.Controls.Grid]::GetRow($path) -eq $row) 'Repeated layout oscillated between path rows'
        }
        foreach ($name in $fileButtons) {
            $button = PaneField $left $name
            Check ([object]::ReferenceEquals($button, $originals[$name])) "Command instance was replaced: $name"
            Check (($strip.Children.Contains($button) -or $overflow.Children.Contains($button))) "Command disappeared: $name"
        }
    }
}
Check ((PaneField $left 'SortChoice') -is [Windows.Controls.ComboBox]) 'Sort options are not a standard dropdown'
foreach ($theme in @('Malaise', 'Light', 'SE98')) {
    $manager.GetMethod('LoadDict', $static).Invoke($null, @([Enum]::Parse($themeType, $theme)))
    foreach ($locale in @('EnUS', 'De', 'ZhTW')) {
        $localeManager.GetMethod('Apply', $static).Invoke($null, @([Enum]::Parse($localeType, $locale)))
        foreach ($scale in @(0.7, 1.0, 1.5, 2.5)) {
            Call 'ApplyAppScale' @([double]$scale, $false)
            Layout 2400
            Check ($label.FontSize -eq 12 -and $address.FontSize -eq 12) 'Scale or locale changed path typography'
            # App scale is a layout transform, so 600 logical pixels need 600 * scale on screen.
            Layout (600 * $scale)
            Check ([Windows.Controls.Grid]::GetRow($path) -eq 0) "Single-row path failed for $theme/$locale/$scale"
            Check ($overflow.Children.Count -gt 0) 'Scaled narrow toolbar did not overflow'
        }
    }
}
$localeManager.GetMethod('Apply', $static).Invoke($null, @([Enum]::Parse($localeType, 'EnUS')))
$manager.GetMethod('LoadDict', $static).Invoke($null, @([Enum]::Parse($themeType, 'Malaise')))
Call 'ApplyAppScale' @([double]1.0, $false)
Call 'FocusPaneQuiet' @($right)
$terminalTab = Call 'CreateDemoTerminalTab' @('C:\Users\Demo\Documents\Quarterly reports', "Responsive path fixture`r`n")
(PaneField $right 'TabStrip').ItemsSource = $right.GetType().GetProperty('Tabs', $flags).GetValue($right, $null)
Call 'ActivateTab' @($terminalTab, $true)
Call 'FocusPane' @($left)
Set-Dual $true
Call 'UpdateTabBar'
Layout 1600
$terminalPath = PaneField $right 'TermPathHost'
Check ([Windows.Controls.Grid]::GetRow($terminalPath) -eq 0) 'Wide terminal did not use one row'
Capture 'terminal-wide' (PaneField $right 'TerminalBarRow')
Layout 650
Check ([Windows.Controls.Grid]::GetRow($terminalPath) -eq 0) 'Narrow terminal stopped using one row'
Check ((PaneField $right 'TermCwdText').FontSize -eq 12) 'Terminal cwd font was shrunk'
Capture 'terminal-narrow' (PaneField $right 'TerminalBarRow')
Capture 'mixed-narrow' $root
foreach ($columns in @($true, $false)) {
    Set-Dual $true $columns
    foreach ($width in @(650, 1600, 650, 1600)) {
        Layout $width
        foreach ($pane in @($left, $right)) { Call 'FocusPane' @($pane) }
        Check ([object]::ReferenceEquals((Field '_focus'), $right)) 'Active pane switching failed after reflow'
    }
}
# Restore the single-row terminal bar in a narrow side-by-side pane.
Set-Dual $true $true
Call 'SetMenuBar' @($right, $true, $false, $false)
Call 'SetMenuBar' @($right, $false, $false, $false)
Layout 650
Check ([double]::IsNaN((PaneField $right 'TerminalBarRow').Height)) 'Alt+M restoration froze responsive terminal height'
Check ([Math]::Abs((PaneField $right 'TerminalBarRow').ActualHeight - 32) -lt 1) 'Restored terminal stopped using one row'
Check ((PaneField $left 'SortChoice') -is [Windows.Controls.ComboBox]) 'Sort dropdown was replaced during reflow'
Check ([object]::ReferenceEquals((PaneField $left 'ExportPopup').PlacementTarget, (PaneField $left 'ExportBtn'))) 'Export flyout lost its button anchor'
Call 'DisposeShellEnv'
Write-Output "PASS: $checks checks; file/terminal, single/dual/stacked, overflow, 36 theme/locale/scale cases, command identity, address edit/logical focus, dropdown and flyout anchors, and bar restoration. Native keyboard and popup placement remain unverified."
[Environment]::Exit(0)
