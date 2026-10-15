param(
    [Parameter(Mandatory = $true)][string]$AssemblyPath,
    [Parameter(Mandatory = $true)][string]$CaptureDirectory,
    [string]$BaselineRef
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
$manager = $assembly.GetType('KillerShell.Services.ThemeManager')
$themeType = $assembly.GetType('KillerShell.Services.Theme')
$static = [Reflection.BindingFlags]'Static,NonPublic,Public'
$flags = [Reflection.BindingFlags]'Instance,NonPublic,Public'
$type = $assembly.GetType('KillerShell.Shell.MainWindow')
$type.GetProperty('DemoMode', $static).SetValue($null, $true, $null)
$window = [Activator]::CreateInstance($type)
$root = $window.Content
$type.GetField('RootGrid', $flags).GetValue($window).Opacity = 1
$tree = $type.GetField('FolderTree', $flags).GetValue($window)
$treeHost = $type.GetField('TreeFadeHost', $flags).GetValue($window)
$treePanel = $type.GetField('TreePanel', $flags).GetValue($window)
$treePanel.Visibility = [Windows.Visibility]::Visible
$treePanel.Width = [double]::NaN
$treePanel.Opacity = 1
$treePanel.HorizontalAlignment = [Windows.HorizontalAlignment]::Stretch
$col = $type.GetField('TreeCol', $flags).GetValue($window)
$col.MinWidth = 0
$col.Width = [Windows.GridLength]::new(240)
$tree.ItemsSource = $null
$tree.Items.Clear()
New-Item -ItemType Directory -Force -Path $CaptureDirectory | Out-Null
function Check($condition, $message) { if (-not $condition) { throw $message } }
function Descendant($obj, [Type]$target) {
    for ($i = 0; $i -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($obj); $i++) {
        $child = [Windows.Media.VisualTreeHelper]::GetChild($obj, $i)
        if ($target.IsInstanceOfType($child)) { return $child }
        $found = Descendant $child $target
        if ($null -ne $found) { return $found }
    }
    return $null
}
function Layout {
    $root.Measure([Windows.Size]::new(1000, 640))
    $root.Arrange([Windows.Rect]::new(0, 0, 1000, 640))
    $root.UpdateLayout()
    $root.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ApplicationIdle)
    $root.UpdateLayout()
    $type.GetMethod('SyncTreeEdgeFades', $flags).Invoke($window, @())
}
function Rows($count, $width) {
    $tree.Items.Clear()
    for ($i = 0; $i -lt $count; $i++) {
        $row = [Windows.Controls.TreeViewItem]::new()
        $row.Style = $app.FindResource('FolderTreeItem')
        $label = [Windows.Controls.TextBlock]::new()
        $label.Text = "Folder $i with a long path that overflows the tree viewport"
        $label.Width = $width
        $label.Height = 24
        $row.Header = $label
        $tree.Items.Add($row) | Out-Null
    }
    Layout
}
function State($thumb, $name, [bool]$value) {
    $owner = $(if ($name -eq 'IsDragging') { [Windows.Controls.Primitives.Thumb] } else { [Windows.UIElement] })
    $key = $owner.GetField($name + 'PropertyKey', $static).GetValue($null)
    $setter = [Windows.DependencyObject].GetMethod('SetValue', $flags, $null,
        [Type[]]@([Windows.DependencyPropertyKey], [object]), $null)
    $setter.Invoke($thumb, @($key, $value))
    Layout
}
function Snapshot($name) {
    $frame = [Windows.Media.Imaging.RenderTargetBitmap]::new(
        [int][Math]::Ceiling($root.ActualWidth), [int][Math]::Ceiling($root.ActualHeight),
        96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $frame.Render($root)
    $origin = $treeHost.TranslatePoint([Windows.Point]::new(0, 0), $root)
    $bitmap = [Windows.Media.Imaging.CroppedBitmap]::new($frame,
        [Windows.Int32Rect]::new([int]$origin.X, [int]$origin.Y,
            [int]$treeHost.ActualWidth, [int]$treeHost.ActualHeight))
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create((Join-Path $CaptureDirectory ($name + '.png')))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
    return $bitmap
}
function BarPixels($bitmap, $bar) {
    $origin = $bar.TranslatePoint([Windows.Point]::new(0, 0), $treeHost)
    $rect = [Windows.Int32Rect]::new([int][Math]::Ceiling($origin.X), [int][Math]::Ceiling($origin.Y),
        [int][Math]::Floor($bar.ActualWidth) - 1, [int][Math]::Floor($bar.ActualHeight) - 1)
    $bytes = [byte[]]::new($rect.Width * $rect.Height * 4)
    $bitmap.CopyPixels($rect, $bytes, $rect.Width * 4, 0)
    return [Convert]::ToBase64String($bytes)
}
$checks = 0
foreach ($theme in @('Malaise', 'Light', '98SE')) {
    $enumName = $(if ($theme -eq '98SE') { 'SE98' } else { $theme })
    $manager.GetMethod('LoadDict', $static).Invoke($null, @([Enum]::Parse($themeType, $enumName)))
    Rows 80 600
    $sv = Descendant $tree ([Windows.Controls.ScrollViewer])
    $h = $sv.Template.FindName('PART_HorizontalScrollBar', $sv)
    $v = $sv.Template.FindName('PART_VerticalScrollBar', $sv)
    $content = $sv.Template.FindName('PART_ScrollContentPresenter', $sv)
    $viewport = $sv.Template.FindName('ViewportFadeHost', $sv)
    Check ($h.Visibility -eq 'Visible' -and $v.Visibility -eq 'Visible') "${theme}: overflow not reproduced"
    $sv.ScrollToHorizontalOffset(100)
    $sv.ScrollToVerticalOffset(100)
    Layout
    Check ($null -eq $tree.OpacityMask -and $null -eq $treeHost.OpacityMask) 'A mask still covers the scrollbars'
    Check ($null -ne $content.OpacityMask -and $null -ne $viewport.OpacityMask) 'Content lost its fades'
    $withFades = Snapshot ($theme + '-idle')
    $sideMask = $content.OpacityMask
    $edgeMask = $viewport.OpacityMask
    $content.OpacityMask = $null
    $viewport.OpacityMask = $null
    $withoutFades = Snapshot ($theme + '-unmasked')
    foreach ($bar in @($h, $v)) {
        Check ((BarPixels $withFades $bar) -eq (BarPixels $withoutFades $bar)) "${theme}: fade changed scrollbar pixels"
    }
    $content.OpacityMask = $sideMask
    $viewport.OpacityMask = $edgeMask
    foreach ($bar in @($h, $v)) {
        $track = $bar.Template.FindName('PART_Track', $bar)
        $thumb = $track.Thumb
        $fill = $thumb.Template.FindName('thumbBorder', $thumb)
        $horizontal = $bar.Orientation -eq 'Horizontal'
        $idle = $(if ($horizontal) { $fill.ActualHeight } else { $fill.ActualWidth })
        # Geometric hit testing works without a native presentation source.
        $hit = [Windows.Media.VisualTreeHelper]::HitTest($thumb, [Windows.Point]::new(1, 1))
        Check ($null -ne $hit) "${theme}: thumb's broad idle hit area is missing"
        State $thumb 'IsMouseOver' $true
        $hover = $(if ($horizontal) { $fill.ActualHeight } else { $fill.ActualWidth })
        if ($theme -ne '98SE') {
            Check ($idle -eq 4 -and $hover -eq 8) "${theme}: $($bar.Orientation) stayed skinny ($idle to $hover)"
        } else { Check ($idle -eq 16 -and $hover -eq 16 -and $null -eq $fill.Effect) '98SE bevel geometry changed' }
        if ($horizontal) { Snapshot ($theme + '-hover-simulated') | Out-Null }
        State $thumb 'IsDragging' $true
        State $thumb 'IsMouseOver' $false
        Check ($(if ($horizontal) { $fill.ActualHeight } else { $fill.ActualWidth }) -eq $hover) 'Leaving during drag shrank the thumb'
        if ($horizontal) { Snapshot ($theme + '-drag-leave-simulated') | Out-Null }
        State $thumb 'IsDragging' $false
        Check ($(if ($horizontal) { $fill.ActualHeight } else { $fill.ActualWidth }) -eq $idle) 'Released thumb did not return to idle'
        $checks += 5
    }
    $htrack = $h.Template.FindName('PART_Track', $h)
    Check (-not $htrack.IsDirectionReversed) 'Horizontal direction reversed'
    Check (($h.Template.FindName('PageBack', $h)).Command -eq [Windows.Controls.Primitives.ScrollBar]::PageLeftCommand) 'Horizontal page-left changed'
    Check (($h.Template.FindName('PageForward', $h)).Command -eq [Windows.Controls.Primitives.ScrollBar]::PageRightCommand) 'Horizontal page-right changed'
    $before = $sv.HorizontalOffset
    [Windows.Controls.Primitives.ScrollBar]::PageRightCommand.Execute($null, $h)
    Layout
    Check ($sv.HorizontalOffset -gt $before) 'Page-right did not scroll right'
    [Windows.Controls.Primitives.ScrollBar]::PageLeftCommand.Execute($null, $h)
    Layout
    Check ($sv.HorizontalOffset -lt $before + $sv.ViewportWidth) 'Page-left did not scroll left'
    $delta = $htrack.ValueFromDistance(20, 0)
    Check ($delta -gt 0) 'Rightward drag distance did not increase horizontal value'
    foreach ($width in @(160, 240, 420)) {
        $col.Width = [Windows.GridLength]::new($width)
        Layout
        Check ($viewport.ActualHeight -le $h.TranslatePoint([Windows.Point]::new(0, 0), $viewport).Y + 0.01) 'Viewport overlaps horizontal scrollbar'
        Check ($viewport.ActualWidth -le $v.TranslatePoint([Windows.Point]::new(0, 0), $viewport).X + 0.01) 'Viewport overlaps vertical scrollbar'
    }
    Rows 3 600
    Check ($h.Visibility -eq 'Visible' -and $v.Visibility -ne 'Visible') 'Horizontal-only overflow failed'
    Rows 80 80
    Check ($h.Visibility -ne 'Visible' -and $v.Visibility -eq 'Visible') 'Vertical-only overflow failed'
    Rows 3 80
    Check ($h.Visibility -ne 'Visible' -and $v.Visibility -ne 'Visible') 'Scrollbars remained after overflow disappeared'
    $col.Width = [Windows.GridLength]::new(240)
    $checks += 14
}
if ($BaselineRef) {
    $manager.GetMethod('LoadDict', $static).Invoke($null, @([Enum]::Parse($themeType, 'Malaise')))
    Rows 80 600
    $sv.ScrollToHorizontalOffset(100)
    $sv.ScrollToVerticalOffset(100)
    Layout
    $original = (& git show "${BaselineRef}:Controls/Controls.xaml") -join "`n"
    Check ($LASTEXITCODE -eq 0) 'Cannot load original control dictionary'
    $original = $original.Replace('xmlns:kui="clr-namespace:KillerShell"', 'xmlns:kui="clr-namespace:KillerShell;assembly=KillerShell"')
    $baseline = [Windows.Markup.XamlReader]::Parse($original)
    $h.Style = $baseline[[Windows.Controls.Primitives.ScrollBar]]
    Layout
    $thumb = $h.Template.FindName('PART_Track', $h).Thumb
    $fill = $thumb.Template.FindName('thumbBorder', $thumb)
    $hit = [Windows.Media.VisualTreeHelper]::HitTest($thumb, [Windows.Point]::new($thumb.ActualWidth / 2, 1))
    Check ($null -eq $hit) 'Original thumb unexpectedly had a broad idle hit area'
    Check ($fill.ActualHeight -eq 4) 'Original horizontal idle thickness differs'
    Snapshot 'original-template-idle' | Out-Null
    State $thumb 'IsMouseOver' $true
    Check ($fill.ActualHeight -eq 8) 'Original hover trigger itself did not expand'
    Snapshot 'original-template-hover-simulated' | Out-Null
    State $thumb 'IsMouseOver' $false
    Write-Output 'BASELINE: original horizontal template renders 4 px idle and 8 px with simulated hover, but has no geometric hit at y=1 inside its 12 px track. Native pointer behavior remains unverified.'
}
$type.GetMethod('DisposeShellEnv', $flags).Invoke($window, @())
Write-Output "PASS: $checks focused assertions. Wide tree, both orientations, full hit area, simulated hover/drag/leave, fade pixel isolation, page commands, drag direction, resize and overflow transitions across Malaise, Light and 98SE. Native pointer and keyboard delivery were not exercised."
[Environment]::Exit(0)
