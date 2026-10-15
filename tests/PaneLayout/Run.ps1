param([Parameter(Mandatory = $true)][string]$AssemblyPath)
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
$manager = $assembly.GetType('KillerShell.Services.ThemeManager')
$themeType = $assembly.GetType('KillerShell.Services.Theme')
$static = [Reflection.BindingFlags]'Static,NonPublic,Public'
$manager.GetMethod('LoadDict', $static).Invoke($null, @([Enum]::Parse($themeType, 'Malaise')))
foreach ($pair in @(@('ListPaneBrush', '#FF303638'), @('TerminalBackgroundBrush', '#FF303638'), @('MenuBackgroundBrush', '#FF262B2D'), @('TabActiveBrush', '#FF232A2B'))) {
    if ($app.FindResource($pair[0]).Color.ToString() -ne $pair[1]) { throw "Malaise surface changed: $($pair[0])" }
}
$type = $assembly.GetType('KillerShell.Shell.MainWindow')
$flags = [Reflection.BindingFlags]'Instance,NonPublic,Public'
$type.GetProperty('DemoMode', $static).SetValue($null, $true, $null)
$window = [Activator]::CreateInstance($type)
$root = $window.Content
function Field($name) { $type.GetField($name, $flags).GetValue($window) }
function Set-Field($name, $value) { $type.GetField($name, $flags).SetValue($window, $value) }
function Call($name, $values = @()) { $type.GetMethod($name, $flags).Invoke($window, $values) }
function Check($condition, $message) { if (-not $condition) { throw $message } }
function Layout($width, [bool]$drain = $true) {
    $root.Measure([Windows.Size]::new($width, 640))
    $root.Arrange([Windows.Rect]::new(0, 0, $width, 640))
    $root.UpdateLayout()
    if ($drain) {
        $root.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ApplicationIdle)
        $root.UpdateLayout()
    }
}
function Set-Dual($dual, $columns = $true) {
    $type.GetField('<DualPane>k__BackingField', $flags).SetValue($window, $dual)
    Set-Field '_paneSideBySide' $columns
    (Field 'RightPane').Visibility = $(if ($dual) { [Windows.Visibility]::Visible } else { [Windows.Visibility]::Collapsed })
    Call 'ApplyPaneLayout' @($false)
}
function Check-Bounds {
    $pane = Field 'RightPane'
    $right = $pane.TranslatePoint([Windows.Point]::new($pane.ActualWidth, 0), $root).X
    $margin = (Field 'RootGrid').Margin.Right + (Field 'WindowFrame').BorderThickness.Right
    Check ($right -le $root.ActualWidth - $margin + 1) "Pane B escaped the viewport: $right"
    Check ((Field 'PaneColA').ActualWidth -ge 299.5) 'Pane A lost its minimum'
    Check ((Field 'PaneColB').ActualWidth -ge 299.5) 'Pane B lost its minimum'
}
Call 'HookPaneRatio'
Set-Field '_inWindowSizeMove' $true
Set-Dual $true
foreach ($scale in @(0.7, 1.0, 1.5, 2.5)) {
    Call 'ApplyAppScale' @([double]$scale, $false)
    foreach ($tree in @(0, 240, 420)) {
        (Field 'TreeCol').MinWidth = 0
        (Field 'TreeCol').Width = [Windows.GridLength]::new($tree)
        (Field 'TreeGapCol').Width = [Windows.GridLength]::new($(if ($tree -eq 0) { 0 } else { 6 }))
        foreach ($search in @(0, 265)) {
            (Field 'SearchCol').MinWidth = 0
            (Field 'SearchCol').Width = [Windows.GridLength]::new($search)
            foreach ($ratio in @(0.05, 0.95)) {
                Layout 4000
                (Field 'PaneColA').Width = [Windows.GridLength]::new([Math]::Max(300, (Field 'PaneHost').ActualWidth * $ratio))
                Layout $window.MinWidth
                Check-Bounds
            }
        }
    }
}
Call 'ApplyAppScale' @([double]1.5, $false)
(Field 'PaneHost').Margin = [Windows.Thickness]::new(7, 0, 11, 0)
(Field 'RootGrid').Margin = [Windows.Thickness]::new(5, 0, 9, 0)
(Field 'WindowFrame').BorderThickness = [Windows.Thickness]::new(2, 0, 3, 0)
Layout 4000
Layout $window.MinWidth
Check-Bounds
Check ([Math]::Abs((Field 'PaneHost').ActualWidth - 604) -le 1) 'Frame or host margins were counted twice'
(Field 'PaneHost').Margin = [Windows.Thickness]::new(0)
(Field 'RootGrid').Margin = [Windows.Thickness]::new(0)
(Field 'WindowFrame').BorderThickness = [Windows.Thickness]::new(0)
Call 'ApplyAppScale' @([double]1.0, $false)
(Field 'TreeCol').Width = [Windows.GridLength]::new(0)
(Field 'TreeGapCol').Width = [Windows.GridLength]::new(0)
(Field 'SearchCol').Width = [Windows.GridLength]::new(0)
Set-Dual $true $false
Layout 1000
Check ($window.MinWidth -eq 600) 'Stacked mode retained the side-by-side minimum'
Set-Dual $true
Layout 900 $false
Set-Dual $false
Layout 1000
Check ($window.MinWidth -eq 600) 'A queued resize changed the single-pane minimum'
Set-Dual $true
Set-Field '_inWindowSizeMove' $false
foreach ($ratio in @(0.05, 0.95)) {
    Set-Field '_paneARatio' ([double]$ratio)
    Layout 1200
    Check-Bounds
    Layout 1000
    Check-Bounds
}
Set-Field '_inWindowSizeMove' $true
Layout 1000
foreach ($side in @('RightPane', 'LeftPane')) {
    Call 'FocusPane' @((Field $side))
    foreach ($name in @('LeftPane', 'RightPane')) {
        $pane = Field $name
        $edge = $pane.GetType().GetField('PaneFocusBottomEdge', $flags).GetValue($pane)
        Check (($edge.Visibility -eq [Windows.Visibility]::Visible) -eq ($name -eq $side)) "Focus ring mismatch: requested=$side pane=$name edge=$($edge.Visibility) focused=$((Field '_focus').Name)"
    }
}
$script:posted = 0
$hook = [Windows.Threading.DispatcherHookEventHandler]{ param($sender, $e) if ($e.Operation.Priority -eq [Windows.Threading.DispatcherPriority]::Background) { $script:posted++ } }
$root.Dispatcher.Hooks.add_OperationPosted($hook)
try { for ($i = 0; $i -lt 25; $i++) { Layout 1000 } }
finally { $root.Dispatcher.Hooks.remove_OperationPosted($hook) }
Check ($posted -eq 0) "Unchanged layouts posted $posted resize callbacks"
Set-Dual $false
Call 'FocusPaneQuiet' @((Field 'RightPane'))
$terminalTab = Call 'CreateDemoTerminalTab' @('C:\Users\Demo', "Pane regression fixture`r`n")
$right = Field 'RightPane'
$strip = $right.GetType().GetField('TabStrip', $flags).GetValue($right)
$strip.ItemsSource = $right.GetType().GetProperty('Tabs', $flags).GetValue($right, $null)
Call 'ActivateTab' @($terminalTab)
Call 'FocusPane' @((Field 'LeftPane'))
Set-Dual $true
Call 'UpdateTabBar'
Layout 1000
$page = $right.GetType().GetField('ResultsPane', $flags).GetValue($right)
Check ($page.CornerRadius.TopLeft -eq 6) 'A hidden tab strip left the terminal corner square'
Check (-not $page.Child.Clip.FillContains([Windows.Point]::new(0.25, 0.25))) 'Terminal content escaped the rounded corner'
function Check-RoundedPixels($element) {
    $effect = $element.Effect
    $element.Effect = $null
    try {
        $width = [int][Math]::Ceiling($element.ActualWidth)
        $height = [int][Math]::Ceiling($element.ActualHeight)
        $visual = [Windows.Media.DrawingVisual]::new()
        $drawing = $visual.RenderOpen()
        $drawing.DrawRectangle([Windows.Media.VisualBrush]::new($element), $null, [Windows.Rect]::new(0, 0, $width, $height))
        $drawing.Close()
        $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($width, $height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($visual)
        $pixels = [byte[]]::new($width * $height * 4)
        $bitmap.CopyPixels($pixels, $width * 4, 0)
        Check ($pixels[3] -lt 64) 'The rendered top-left corner was filled square'
        Check ($pixels[(10 * $width + 10) * 4 + 3] -gt 192) 'The corner render was empty'
    } finally { $element.Effect = $effect }
}
Check-RoundedPixels $page
Call 'CreateTab' | Out-Null
Call 'UpdateTabBar'
Layout 1000
$band = $right.GetType().GetField('TabBar', $flags).GetValue($right)
Check ($strip.Items.Count -eq 1 -and $band.ActualHeight -gt 0) 'The single terminal tab was not rendered beside multiple file tabs'
$container = $strip.ItemContainerGenerator.ContainerFromIndex(0)
$tabBorder = $strip.ItemTemplate.FindName('tabBd', $container)
Check ($tabBorder.CornerRadius.TopLeft -gt 0) 'The visible terminal tab lost its rounded top-left corner'
Check-RoundedPixels $tabBorder
Set-Dual $false
Call 'UpdateTabBar'
$window.Width = 1000
Layout 1000
function Pump($milliseconds) {
    $frame = [Windows.Threading.DispatcherFrame]::new()
    $until = [DateTime]::UtcNow.AddMilliseconds($milliseconds)
    $timer = [Windows.Threading.DispatcherTimer]::new()
    $timer.Interval = [TimeSpan]::FromMilliseconds(10)
    $timer.add_Tick({ Layout $window.Width; if ([DateTime]::UtcNow -ge $until) { $frame.Continue = $false } }.GetNewClosure())
    $timer.Start()
    try { [Windows.Threading.Dispatcher]::PushFrame($frame) } finally { $timer.Stop() }
}
for ($i = 0; $i -lt 8; $i++) {
    Call 'ToggleDualPane'
    Pump 20
}
Pump 250
Layout $window.Width
Check (-not $type.GetProperty('DualPane', $flags).GetValue($window, $null)) 'Interrupted toggles left dual mode enabled'
Check ((Field 'RightPane').Visibility -eq [Windows.Visibility]::Collapsed) 'Interrupted close left pane B visible'
Check (-not (Field 'PaneHost').ClipToBounds) 'Interrupted close left the host clipped'
Check ([double]::IsNaN((Field 'RightPane').Width)) 'Interrupted close left pane B frozen'
Check ($window.MinWidth -eq 600) 'Interrupted close retained the dual minimum'
Check ($null -eq (Field '_paneSlideSettle')) 'Interrupted close never settled'
Call 'DisposeShellEnv'
Write-Output 'PASS: Malaise surfaces, 48 sizing cases, extreme restore ratios, focus, scale and margin bounds, stacked/single restoration, queued callbacks, unchanged-layout scheduling, rounded terminal pixels with hidden/visible tabs, and eight interrupted animated toggles.'
[Environment]::Exit(0)
