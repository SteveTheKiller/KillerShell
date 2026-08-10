using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KillerShell.Shell;

// The control behind a Storage Analyzer tab: pick a folder or drive, scan it, and see every
// byte as a WizTree/WinDirStat-style treemap - a rectangle per file, area proportional to
// size, folders as nested outlines, drawn in KillerShell's own retro language.
//
// Scanning is a parallel directory walk over FindFirstFileExW (basic info + large fetch),
// one worker per logical processor pulling directories off a shared queue. It works on any
// filesystem and any account; folders the account cannot open are skipped and counted rather
// than failing the scan. An NTFS MFT fast path can slot in behind the same tree later.
//
// Rendering is done by hand on one surface, the same reasoning as the terminal: a scan of a
// system drive is easily 200k+ visible rectangles, and one OnRender pass over frozen brushes
// is fast where 200k elements would not be. Rects smaller than a couple of pixels are pruned
// - their bytes are already inside their parent's rectangle.
//
// Same "own host, own control, MOVED not rebuilt between activations" rule as
// ProcessListControl/EventViewerControl/PerformanceMonitorControl (Shell/StorageTabs.cs): the
// scan result is the state, and a rebuild would throw a whole scan away on a tab switch.
namespace KillerShell.Tools
{
    internal sealed class StorageAnalyzerControl : Grid
    {
        // ── The scanned tree ─────────────────────────────────────
        private sealed class FsNode
        {
            internal string Name = "";
            internal long Size;                 // files: the file's bytes; dirs: aggregated after the scan
            internal bool IsDir;
            internal FsNode? Parent;
            internal List<FsNode>? Children;    // dirs only, sorted largest-first after the scan
            internal Rect Rect;                 // where the last render put it (see _gen)
            internal int Gen;                   // render generation Rect belongs to - stale rects must not hit-test
        }

        private FsNode? _root;                  // the scan result
        private FsNode? _zoomRoot;              // what the map currently shows (a dir under _root)
        private string _rootPath = "";
        private FsNode? _hover;
        private FsNode? _selected;
        private int _gen;

        // ── Scan machinery ───────────────────────────────────────
        private CancellationTokenSource? _cts;
        private ConcurrentQueue<(string Path, FsNode Node)>? _queue;
        private int _pending;                    // directories queued but not fully processed
        private long _pFiles, _pDirs, _pBytes, _pSkipped;
        private bool _scanning;
        private readonly DispatcherTimer _progressTimer;

        // ── UI ───────────────────────────────────────────────────
        private readonly TextBox _targetBox;
        private readonly Button _scanBtn;
        private readonly StackPanel _breadcrumb;

        /// <summary>
        /// Where scan progress, the finished summary and errors go: the WINDOW's own status
        /// bar, via the tab's per-tab status line (StorageTabs.cs wires this to SetTabStatus).
        /// It was an inline TextBlock in the target bar first, and every progress update
        /// resized the target box beside it - status text belongs in the status bar.
        /// </summary>
        internal Action<string>? ReportStatus;
        private readonly TreemapSurface _map;
        private readonly TextBlock _footerLeft;
        private readonly TextBlock _footerRight;

        // Color mode, persisted app-wide: "cat" = by extension category, "folder" = by
        // top-level folder hue. Toggled from the toolbar pair or the map's context menu.
        private bool _colorByFolder;
        private readonly Border _colorTypeBtn = null!;
        private readonly Border _colorFolderBtn = null!;

        // ── View controls (toolbar, right of Scan) ───────────────
        // Both are VIEW filters over the existing tree, never rescans: changing either just
        // re-lays the map out, so they are instant even on a million-file scan.
        //
        // DEPTH: how many folder levels below the zoom root get their own rectangles. 0 is
        // unlimited (every file individually). At 1, each immediate child folder is ONE solid
        // block - which is the "where did my space actually go" read, and the fastest possible
        // paint. A depth-capped folder is drawn as a single rect carrying its whole subtree's
        // bytes, so no space ever goes missing from the picture, it is only summarised.
        private int _depthLimit;
        private static readonly int[] DepthChoices = [0, 1, 2, 3, 4];
        private readonly Border _depthBtn = null!;
        private readonly TextBlock _depthBadge = null!;

        // MIN SIZE: anything smaller is folded away rather than drawn. The bytes are NOT lost -
        // a hidden child still counts inside its parent's rectangle - so the map stays honest
        // while the thousands of sub-pixel rects that cost the most to draw disappear.
        private long _minSize;
        private static readonly long[] MinSizeChoices = [0, 1L << 20, 10L << 20, 100L << 20];
        private readonly Border _minSizeBtn = null!;
        private readonly TextBlock _minBadge = null!;

        internal StorageAnalyzerControl(string? initialPath)
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // target + scan bar
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // breadcrumb
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // the map
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // storage-info footer

            this.SetResourceReference(BackgroundProperty, "PaneBrush");
            // Grain over the opaque root, all four rows - same as the other tool tabs: an
            // opaque face hides the pane's own grain layer, so it repaints its own. The map
            // and its well are opaque surfaces above this; the bar and footer rows show it.
            var rootGrain = ToolTabChrome.Grain();
            SetRowSpan(rootGrain, 4);
            Children.Add(rootGrain);

            // Focusable so the tab can actually OWN the keyboard: StorageAnalyzerHasFocus walks
            // up from the focused element, and the local single-key shortcuts below need
            // something inside this control to be focused for that walk to succeed. A click
            // anywhere that is not an input lands here.
            // No Background assignment here: a local `Background = Brushes.Transparent` beats a
            // SetResourceReference in the dependency-property system, so the line that lived
            // here silently CANCELLED the PaneBrush above - the tab showed the darker
            // MenuBackgroundBrush through on every theme where the two differ, which is twelve
            // of the thirteen. The opaque PaneBrush is every bit as hit-testable, so the
            // click-to-focus below lost nothing.
            Focusable = true;
            FocusVisualStyle = null;
            PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.OriginalSource is not TextBox) Focus();
            };

            _colorByFolder = Services.ThemeManager.GetSetting("StorageColorMode") == "folder";
            _depthLimit = ReadIntSetting("StorageDepth", 0, DepthChoices);
            _minSize    = ReadLongSetting("StorageMinSize", 0, MinSizeChoices);

            // ── Row 0: target box, browse, scan/stop, view controls ───
            var bar = new Grid();
            bar.SetResourceReference(MarginProperty, "MonitorInfoMargin");
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _targetBox = new TextBox
            {
                VerticalContentAlignment = VerticalAlignment.Center,
                Text = !string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath)
                     ? initialPath!
                     : (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\"),
            };
            _targetBox.SetResourceReference(TextBox.FontFamilyProperty, "MonoFont");
            SetColumn(_targetBox, 0);

            var browse = new Button { Width = 26, Height = 24, Margin = new Thickness(4, 0, 0, 0), Content = ((char)0xE838).ToString() };
            browse.SetResourceReference(FrameworkElement.StyleProperty, "ViewToggleBtn");
            browse.SetResourceReference(ToolTipProperty, "Str_Storage_Browse");
            browse.Click += (_, _) =>
            {
                var dlg = new KillerShell.FolderPickerDialog(_targetBox.Text) { Owner = Window.GetWindow(this) };
                if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.SelectedPath))
                    _targetBox.Text = dlg.SelectedPath;
            };
            SetColumn(browse, 1);

            _scanBtn = new Button { MinWidth = 64, Height = 24, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 0, 10, 0) };
            // OutlineButton, not SurfaceButton: Scan/Stop is the MAIN action of this tab, and the
            // family's main button is an accent outline at rest that fills solid on hover.
            // SurfaceButton is the secondary tier - a filled chip face - which put a flat grey
            // slab on the one control the tab exists to be driven by.
            _scanBtn.SetResourceReference(FrameworkElement.StyleProperty, "OutlineButton");
            _scanBtn.SetResourceReference(ContentControl.ContentProperty, "Str_Storage_Scan");
            _scanBtn.Click += (_, _) => { if (_scanning) CancelScan(); else StartScan(); };
            SetColumn(_scanBtn, 2);

            // The view controls, grouped right of Scan behind a divider so they read as
            // "how the map is drawn" rather than as more of "what to scan".
            var viewGroup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };

            var divider = new Border { Width = 1, Margin = new Thickness(0, 3, 8, 3), Opacity = 0.5 };
            divider.SetResourceReference(Border.BackgroundProperty, "PaneBorderBrush");
            viewGroup.Children.Add(divider);

            // E81E MapLayers for depth, E71C Filter (the funnel PipeBtn already uses, so it is
            // render-proven) for min size. Glyphs with tooltips rather than captions, the way
            // the rest of the app's toolbars read.
            // GLYPH CHECK, per the family rule that these are found by RENDERING candidates and
            // never by trusting the docs: E71C is proven in this repo, E81E is NOT used anywhere
            // else yet. If it draws a blank box, swap it for E7C1 or E71D - one constant here.
            _depthBtn = MakeFilterButton(0xE81E, "Str_Storage_DepthTip", out _depthBadge, BuildDepthMenu);
            viewGroup.Children.Add(_depthBtn);

            _minSizeBtn = MakeFilterButton(0xE71C, "Str_Storage_MinSizeTip", out _minBadge, BuildMinSizeMenu);
            viewGroup.Children.Add(_minSizeBtn);

            // The color pair: two glyph toggles wearing ViewToggleBtn, so the active one lights
            // with Tag="on" exactly like the pane's own view-mode strip. E790 (paint roller) for
            // by-type, E8B7 (folder) for by-folder.
            _colorTypeBtn   = MakeColorToggle(0xE790, "Str_Storage_ColorByType",   byFolder: false);
            _colorFolderBtn = MakeColorToggle(0xE8B7, "Str_Storage_ColorByFolder", byFolder: true);
            viewGroup.Children.Add(_colorTypeBtn);
            viewGroup.Children.Add(_colorFolderBtn);

            SetColumn(viewGroup, 3);

            bar.Children.Add(_targetBox);
            bar.Children.Add(browse);
            bar.Children.Add(_scanBtn);
            bar.Children.Add(viewGroup);
            SetRow(bar, 0);
            Children.Add(bar);

            SyncViewButtons();

            // ── Row 1: breadcrumb of the zoom root ───────────────
            _breadcrumb = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 4), Visibility = Visibility.Collapsed };
            SetRow(_breadcrumb, 1);
            Children.Add(_breadcrumb);

            // ── Row 2: the treemap ───────────────────────────────
            _map = new TreemapSurface(this);
            var mapWell = new Border
            {
                BorderThickness = new Thickness(1),
                Child = _map,
            };
            mapWell.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
            mapWell.SetResourceReference(Border.BackgroundProperty, "MonitorCellBrush");

            // The sunken tier is the MAP, not the whole tab: StorageHost deliberately carries
            // no host-level well (a well around everything wrapped the target bar too, and on
            // 98SE its dark top bevel made the bar read as recessed chrome). The four bevel
            // Borders here are the same crossed sunken pair + inner pair FilePane's wells use,
            // all token-driven and inert off 98SE.
            var mapArea = new Grid();
            mapArea.SetResourceReference(MarginProperty, "MonitorGridMargin");
            mapArea.Children.Add(mapWell);
            void AddBevel(string brushKey, string thickKey, bool inner)
            {
                var b = new Border { IsHitTestVisible = false };
                b.SetResourceReference(Border.BorderBrushProperty, brushKey);
                b.SetResourceReference(Border.BorderThicknessProperty, thickKey);
                if (inner) b.SetResourceReference(MarginProperty, "PaneBevelInnerMargin");
                Panel.SetZIndex(b, 5);
                mapArea.Children.Add(b);
            }
            AddBevel("PaneBevelDarkBrush",   "PaneBevelLightThickness", inner: false);
            AddBevel("PaneBevelLightBrush",  "PaneBevelDarkThickness",  inner: false);
            AddBevel("PaneBevelDark2Brush",  "PaneBevel2LightThickness", inner: true);
            AddBevel("PaneBevelLight2Brush", "PaneBevel2DarkThickness",  inner: true);
            SetRow(mapArea, 2);
            Children.Add(mapArea);

            _map.MouseMove += Map_MouseMove;
            _map.MouseLeave += (_, _) => { _hover = null; UpdateFooterLeft(); _map.InvalidateOverlay(); };
            _map.MouseLeftButtonDown += Map_MouseLeftButtonDown;
            _map.MouseRightButtonUp += (_, e) => { Map_RightClick(e); e.Handled = true; };

            // ── Row 3: the storage-info footer ───────────────────
            var footer = new Grid { Margin = new Thickness(10, 4, 10, 6) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // NO TextTrimming: that ellipsizes the TAIL, which on this line is the file name,
            // the size and the percentage - everything worth reading. ElideFooterLeft trims from
            // the FRONT instead and SizeChanged re-cuts it, so a resize re-measures against the
            // untrimmed original. Same treatment as the window footer's status line.
            _footerLeft = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            _footerLeft.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            _footerLeft.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            _footerLeft.SizeChanged += (_, _) => ElideFooterLeft();
            _footerRight = new TextBlock { FontSize = 11, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            _footerRight.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            _footerRight.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            SetColumn(_footerLeft, 0);
            SetColumn(_footerRight, 1);
            footer.Children.Add(_footerLeft);
            footer.Children.Add(_footerRight);
            SetRow(footer, 3);
            Children.Add(footer);

            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _progressTimer.Tick += (_, _) => UpdateProgressText();

            // Repaint on theme change: brushes here are frozen snapshots for speed, so a theme
            // switch has to drop the caches and redraw - same shape as the terminal's rebuild.
            Services.ThemeManager.ThemeChanged += OnThemeChanged;
        }

        private void OnThemeChanged()
        {
            _penCache = null;
            _map.InvalidateMap();
            _map.InvalidateOverlay();
        }

        // ═══════════════════════════════════════════════════════════
        //  KEYBOARD  -  local to this tab
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Single-key shortcuts while the Storage tab has focus, the app's established
        /// local-shortcut convention (the CPU graph's bare L, the Processes grid's row keys).
        /// The window hands the keyboard over whenever StorageAnalyzerHasFocus is true and the
        /// chord is not a window chord (MainWindow.xaml.cs), so these never fight a global.
        ///
        /// The target box is a genuine text-editing surface: a bare D typed into a path must
        /// stay a D. Anything focused inside a TextBox therefore falls straight through.
        /// </summary>
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (e.Handled) return;
            if (Keyboard.FocusedElement is TextBox) return;

            var mods = Keyboard.Modifiers;
            bool plain = mods == ModifierKeys.None;

            // Ctrl+Enter starts a scan from anywhere in the tab, including the target box, so
            // typing a path and running it never needs the mouse. Checked before the plain
            // keys, and deliberately NOT gated on the TextBox return above - it is a chord.
            if (e.Key == Key.Return && mods == ModifierKeys.Control)
            {
                if (!_scanning) StartScan();
                e.Handled = true;
                return;
            }

            if (!plain) return;

            switch (e.Key)
            {
                case Key.D:      SetDepth(NextChoice(DepthChoices, _depthLimit)); break;
                case Key.M:      SetMinSize(NextChoice(MinSizeChoices, _minSize)); break;
                case Key.C:      SetColorMode(!_colorByFolder); break;
                case Key.F5:     if (!_scanning) StartScan(); break;
                case Key.Escape: if (_scanning) CancelScan(); else return; break;

                // Zooming, mirroring the browse pane's own navigation keys: Backspace climbs
                // one level, Home jumps to the scan root, Enter descends into what is selected.
                case Key.Back:
                    if (_zoomRoot?.Parent is { } up) ZoomTo(up);
                    break;
                case Key.Home:
                    if (_root != null && !ReferenceEquals(_zoomRoot, _root)) ZoomTo(_root);
                    break;
                case Key.Return:
                    if (_selected is { IsDir: true } dir) ZoomTo(dir);
                    else if (_selected?.Parent is { } parent && !ReferenceEquals(parent, _zoomRoot)) ZoomTo(parent);
                    break;

                case Key.Delete:
                    if (_selected != null) RecycleNode(_selected, FullPath(_selected));
                    break;

                default: return;   // not ours - let it bubble
            }
            e.Handled = true;
        }

        // ── View-control plumbing ────────────────────────────────
        private static int ReadIntSetting(string key, int fallback, int[] allowed)
        {
            string? s = Services.ThemeManager.GetSetting(key);
            if (s != null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                && Array.IndexOf(allowed, v) >= 0) return v;
            return fallback;
        }

        private static long ReadLongSetting(string key, long fallback, long[] allowed)
        {
            string? s = Services.ThemeManager.GetSetting(key);
            if (s != null && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)
                && Array.IndexOf(allowed, v) >= 0) return v;
            return fallback;
        }

        /// <summary>Next value in a cycling choice list, wrapping. An unknown current value
        /// restarts at the head rather than sticking.</summary>
        private static T NextChoice<T>(T[] choices, T current)
        {
            int i = Array.IndexOf(choices, current);
            return choices[(i + 1) % choices.Length];
        }

        /// <summary>
        /// A filter button: bare glyph, tooltip, and a value flyout on click. The current value
        /// shows as a small badge beside the glyph ONLY while the filter is doing something -
        /// at its default the button is a clean glyph like the rest of the toolbar, and an
        /// active filter announces itself rather than hiding in a tooltip.
        /// </summary>
        private Border MakeFilterButton(int glyph, string tipKey, out TextBlock badge, Func<ContextMenu> buildMenu)
        {
            var g = new TextBlock
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Text = ((char)glyph).ToString(),
                VerticalAlignment = VerticalAlignment.Center,
            };
            badge = new TextBlock
            {
                FontSize = 9.5, Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            badge.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(g);
            row.Children.Add(badge);

            var b = new Border
            {
                Height = 24, MinWidth = 26, Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand, Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(KillerShell.Services.ThemeManager.Radius("SmallCornerRadius", 3)),
                Child = row,
            };
            b.SetResourceReference(ToolTipProperty, tipKey);

            var capturedGlyph = g;
            b.MouseEnter += (_, _) => { if (b.Tag as string != "on") capturedGlyph.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush"); };
            b.MouseLeave += (_, _) => { if (b.Tag as string != "on") capturedGlyph.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); };
            b.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                var menu = buildMenu();
                // ANCHORED TO ITS OWN BUTTON, the locked family flyout rule - a toolbar button
                // opens Bottom with a 4px gap and nothing hand-computed; WPF nudges it back on
                // screen by itself when it would overflow.
                menu.PlacementTarget = b;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.VerticalOffset = 4;
                menu.IsOpen = true;
            };
            return b;
        }

        private ContextMenu BuildDepthMenu()
        {
            var menu = new ContextMenu();
            foreach (int d in DepthChoices)
            {
                int captured = d;
                var item = new MenuItem
                {
                    IsCheckable = true,
                    IsChecked = _depthLimit == d,
                    Header = d == 0 ? MainWindow.LocStatic("Str_Storage_DepthAll")
                                    : MainWindow.LocStatic("Str_Storage_Depth") + " " + d.ToString(CultureInfo.InvariantCulture),
                };
                item.Click += (_, _) => SetDepth(captured);
                menu.Items.Add(item);
            }
            return menu;
        }

        private ContextMenu BuildMinSizeMenu()
        {
            var menu = new ContextMenu();
            foreach (long m in MinSizeChoices)
            {
                long captured = m;
                var item = new MenuItem
                {
                    IsCheckable = true,
                    IsChecked = _minSize == m,
                    Header = m == 0 ? MainWindow.LocStatic("Str_Storage_MinAll") : FormatSize(m),
                };
                item.Click += (_, _) => SetMinSize(captured);
                menu.Items.Add(item);
            }
            return menu;
        }

        private void SetDepth(int depth)
        {
            _depthLimit = depth;
            Services.ThemeManager.SetSetting("StorageDepth", depth.ToString(CultureInfo.InvariantCulture));
            SyncViewButtons();
            _map.InvalidateMap();
            _map.InvalidateOverlay();
        }

        private void SetMinSize(long min)
        {
            _minSize = min;
            Services.ThemeManager.SetSetting("StorageMinSize", min.ToString(CultureInfo.InvariantCulture));
            SyncViewButtons();
            _map.InvalidateMap();
            _map.InvalidateOverlay();
        }

        /// <summary>One half of the color-mode pair. A Border, not a Button, for the same
        /// reason the perfmon width toggle is: a Button with a transparent background keeps
        /// WPF's default template and its system-blue hover. Tag="on" marks the active mode,
        /// the family's own selected-toggle convention.</summary>
        private Border MakeColorToggle(int glyph, string tipKey, bool byFolder)
        {
            var text = new TextBlock
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Text = ((char)glyph).ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var b = new Border
            {
                Width = 26, Height = 24, Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand, Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(KillerShell.Services.ThemeManager.Radius("SmallCornerRadius", 3)),
                Child = text,
            };
            b.SetResourceReference(ToolTipProperty, tipKey);
            b.MouseEnter += (_, _) => { if (b.Tag as string != "on") text.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush"); };
            b.MouseLeave += (_, _) => { if (b.Tag as string != "on") text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); };
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; SetColorMode(byFolder); };
            return b;
        }

        /// <summary>Repaints all four view controls from live state - captions for the two
        /// cycling buttons, lit/unlit for the color pair.</summary>
        private void SyncViewButtons()
        {
            // A filter at its default reads as a bare glyph; an ACTIVE one lights and shows its
            // value, so the toolbar can never quietly be hiding half the map.
            SyncFilter(_depthBtn, _depthBadge, _depthLimit != 0,
                       _depthLimit.ToString(CultureInfo.InvariantCulture));
            SyncFilter(_minSizeBtn, _minBadge, _minSize != 0, ShortSize(_minSize));

            SyncToggle(_colorTypeBtn, !_colorByFolder);
            SyncToggle(_colorFolderBtn, _colorByFolder);
        }

        private static void SyncFilter(Border b, TextBlock badge, bool active, string value)
        {
            b.Tag = active ? "on" : null;
            badge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            badge.Text = value;

            if (active) b.SetResourceReference(Border.BackgroundProperty, "SelectionBg");
            else b.Background = Brushes.Transparent;

            string fg = active ? "SelectionFg" : "TextBrush";
            badge.SetResourceReference(TextBlock.ForegroundProperty, fg);
            if (b.Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock glyph)
                glyph.SetResourceReference(TextBlock.ForegroundProperty, fg);
        }

        private static void SyncToggle(Border b, bool on)
        {
            b.Tag = on ? "on" : null;
            if (on) b.SetResourceReference(Border.BackgroundProperty, "SelectionBg");
            else b.Background = Brushes.Transparent;
            if (b.Child is TextBlock t)
                t.SetResourceReference(TextBlock.ForegroundProperty, on ? "SelectionFg" : "TextBrush");
        }

        /// <summary>Badge-sized size label - "10M" rather than "10.0 MB", because it rides a
        /// 26px button beside a glyph.</summary>
        private static string ShortSize(long bytes)
        {
            const long mb = 1L << 20, gb = 1L << 30;
            if (bytes >= gb) return (bytes / gb).ToString(CultureInfo.InvariantCulture) + "G";
            if (bytes >= mb) return (bytes / mb).ToString(CultureInfo.InvariantCulture) + "M";
            return (bytes / 1024).ToString(CultureInfo.InvariantCulture) + "K";
        }

        /// <summary>Torn down when the tab closes (StorageTabs.cs) and on window close.</summary>
        internal void Shutdown()
        {
            CancelScan();
            _progressTimer.Stop();
            Services.ThemeManager.ThemeChanged -= OnThemeChanged;
        }

        // ═══════════════════════════════════════════════════════════
        //  SCAN - parallel directory walk
        // ═══════════════════════════════════════════════════════════
        /// <summary>Point the tab at a folder and scan it now. The "Analyze storage" entry
        /// point from the listing, the tree and the saved places (StorageTabs.cs).</summary>
        internal void ScanFolder(string folder)
        {
            if (_scanning) CancelScan();
            _targetBox.Text = folder;
            StartScan();
        }

        private void StartScan()
        {
            string target = _targetBox.Text.Trim();
            if (target.Length == 0 || !Directory.Exists(target))
            {
                ReportStatus?.Invoke(MainWindow.LocStatic("Str_Storage_BadTarget"));
                return;
            }

            _rootPath = target.TrimEnd('\\');
            if (_rootPath.EndsWith(":", StringComparison.Ordinal)) _rootPath += "\\";   // drive root stays rooted

            _root = new FsNode { Name = _rootPath, IsDir = true, Children = [] };
            _zoomRoot = _root;
            _hover = null; _selected = null;
            _pFiles = 0; _pDirs = 0; _pBytes = 0; _pSkipped = 0;
            _pending = 1;
            _scanning = true;
            _cts = new CancellationTokenSource();
            _queue = new ConcurrentQueue<(string, FsNode)>();
            _queue.Enqueue((_rootPath, _root));

            _scanBtn.SetResourceReference(ContentControl.ContentProperty, "Str_Storage_Stop");
            _progressTimer.Start();
            RebuildBreadcrumb();
            _map.InvalidateMap();
            _map.InvalidateOverlay();
            UpdateFooterRight();

            var token = _cts.Token;
            var root = _root;
            int workers = Math.Max(2, Environment.ProcessorCount);
            for (int i = 0; i < workers; i++)
            {
                var t = new Thread(() => ScanWorker(token, root)) { IsBackground = true, Name = "StorageScan" };
                t.Start();
            }
        }

        private void CancelScan()
        {
            _cts?.Cancel();
            // FinishScan(aborted) runs from the worker that notices; nothing else to do here.
        }

        private void ScanWorker(CancellationToken token, FsNode root)
        {
            var queue = _queue!;
            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    // First worker to notice ends the scan; the rest fall out on the empty queue.
                    if (Interlocked.Exchange(ref _pending, 0) > 0) FinishScan(root, aborted: true);
                    return;
                }

                if (!queue.TryDequeue(out var item))
                {
                    if (Volatile.Read(ref _pending) == 0) return;
                    Thread.Sleep(1);
                    continue;
                }

                ScanDirectory(item.Path, item.Node, queue, token);

                // The LAST directory out finishes the scan - aggregation runs on this worker,
                // off the UI thread, and only the finished result is dispatched.
                if (Interlocked.Decrement(ref _pending) == 0)
                    FinishScan(root, aborted: false);
            }
        }

        private void ScanDirectory(string path, FsNode node, ConcurrentQueue<(string, FsNode)> queue, CancellationToken token)
        {
            // \\?\ so paths past MAX_PATH enumerate instead of erroring - real on any dev drive.
            IntPtr h = FindFirstFileExW("\\\\?\\" + path + "\\*", 1 /*FindExInfoBasic*/, out var fd,
                                        0 /*FindExSearchNameMatch*/, IntPtr.Zero, 2 /*FIND_FIRST_EX_LARGE_FETCH*/);
            if (h == new IntPtr(-1)) { Interlocked.Increment(ref _pSkipped); return; }

            var children = node.Children!;
            try
            {
                do
                {
                    if (token.IsCancellationRequested) return;
                    string name = fd.cFileName;
                    if (name == "." || name == "..") continue;

                    bool isDir = (fd.dwFileAttributes & 0x10) != 0;         // FILE_ATTRIBUTE_DIRECTORY
                    bool reparse = (fd.dwFileAttributes & 0x400) != 0;      // FILE_ATTRIBUTE_REPARSE_POINT

                    if (isDir)
                    {
                        // Junctions and symlinks are NOT followed: following them double-counts
                        // whole subtrees and can cycle (WinDirStat's own rule).
                        if (reparse) continue;
                        var child = new FsNode { Name = name, IsDir = true, Parent = node, Children = [] };
                        lock (children) children.Add(child);
                        Interlocked.Increment(ref _pDirs);
                        Interlocked.Increment(ref _pending);
                        queue.Enqueue((path + "\\" + name, child));
                    }
                    else
                    {
                        if (reparse) continue;
                        long size = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                        var child = new FsNode { Name = name, Size = size, Parent = node };
                        lock (children) children.Add(child);
                        Interlocked.Increment(ref _pFiles);
                        Interlocked.Add(ref _pBytes, size);
                    }
                }
                while (FindNextFileW(h, out fd));
            }
            finally { FindClose(h); }
        }

        private void FinishScan(FsNode root, bool aborted)
        {
            // Aggregate + sort on the worker, so a million-node tree never stalls the UI.
            Aggregate(root);

            Dispatcher.BeginInvoke((Action)(() =>
            {
                _scanning = false;
                _progressTimer.Stop();
                _scanBtn.SetResourceReference(ContentControl.ContentProperty, "Str_Storage_Scan");
                ReportStatus?.Invoke(aborted
                    ? MainWindow.LocStatic("Str_Storage_Stopped") + "  -  " + ProgressSummary()
                    : ProgressSummary());
                RebuildBreadcrumb();
                UpdateFooterRight();
                UpdateFooterLeft();
                _map.InvalidateMap();
                _map.InvalidateOverlay();
            }));
        }

        private static long Aggregate(FsNode n)
        {
            if (n.Children == null) return n.Size;
            long total = 0;
            foreach (var c in n.Children) total += Aggregate(c);
            n.Size = total;
            n.Children.Sort((a, b) => b.Size.CompareTo(a.Size));   // squarify wants largest-first
            return total;
        }

        private void UpdateProgressText() => ReportStatus?.Invoke(ProgressSummary());

        private string ProgressSummary()
            => Interlocked.Read(ref _pDirs).ToString("N0", CultureInfo.InvariantCulture) + " dirs  " +
               Interlocked.Read(ref _pFiles).ToString("N0", CultureInfo.InvariantCulture) + " files  " +
               FormatSize(Interlocked.Read(ref _pBytes)) +
               (Interlocked.Read(ref _pSkipped) > 0
                   ? "  (" + Interlocked.Read(ref _pSkipped).ToString("N0", CultureInfo.InvariantCulture) + " " + MainWindow.LocStatic("Str_Storage_Skipped") + ")"
                   : "");

        // ═══════════════════════════════════════════════════════════
        //  TREEMAP - layout + paint
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// The drawing surface. Split from the control so the map (heavy, redrawn on data or
        /// zoom changes) and the hover/selection rings (light, redrawn on every mouse move)
        /// each invalidate on their own - a full 200k-rect repaint per mouse move would not
        /// keep up.
        /// </summary>
        private sealed class TreemapSurface : Grid
        {
            private readonly StorageAnalyzerControl _owner;
            private readonly MapLayer _mapLayer;
            private readonly OverlayLayer _overlayLayer;

            internal TreemapSurface(StorageAnalyzerControl owner)
            {
                _owner = owner;
                ClipToBounds = true;
                Background = Brushes.Transparent;   // hit-testable everywhere, including pruned areas
                _mapLayer = new MapLayer(owner);
                _overlayLayer = new OverlayLayer(owner) { IsHitTestVisible = false };
                Children.Add(_mapLayer);
                Children.Add(_overlayLayer);
            }

            // Its own name, NOT a `new` hiding of UIElement.InvalidateVisual - hiding invites a
            // base-typed call silently invalidating the wrong element.
            internal void InvalidateMap() => _mapLayer.InvalidateVisual();
            internal void InvalidateOverlay() => _overlayLayer.InvalidateVisual();

            private sealed class MapLayer : FrameworkElement
            {
                private readonly StorageAnalyzerControl _o;
                internal MapLayer(StorageAnalyzerControl o) => _o = o;
                protected override void OnRender(DrawingContext dc) => _o.RenderMap(dc, RenderSize);
            }

            private sealed class OverlayLayer : FrameworkElement
            {
                private readonly StorageAnalyzerControl _o;
                internal OverlayLayer(StorageAnalyzerControl o) => _o = o;
                protected override void OnRender(DrawingContext dc) => _o.RenderOverlay(dc);
            }
        }

        private Pen? _penCache;
        private readonly Dictionary<string, SolidColorBrush> _brushCache = [];

        private Pen BorderPen()
        {
            if (_penCache == null)
            {
                var p = new Pen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), 1);
                p.Freeze();
                _penCache = p;
            }
            return _penCache;
        }

        private SolidColorBrush CachedBrush(string key, Color c)
        {
            if (_brushCache.TryGetValue(key, out var b)) return b;
            b = new SolidColorBrush(c);
            b.Freeze();
            _brushCache[key] = b;
            return b;
        }

        private void RenderMap(DrawingContext dc, Size size)
        {
            // Never walk the tree while workers are still adding to it - the child lists are
            // only locked on the WRITE side, so a mid-scan enumeration here would race them.
            // _scanning flips on the UI thread only, after aggregation is fully done.
            if (_scanning) return;
            if (_zoomRoot == null || _zoomRoot.Size <= 0 || size.Width < 4 || size.Height < 4) return;

            _gen++;
            var rect = new Rect(0, 0, size.Width, size.Height);
            _zoomRoot.Rect = rect;
            _zoomRoot.Gen = _gen;
            LayoutAndDraw(dc, _zoomRoot, rect, 1);
        }

        /// <summary>
        /// Lays out and paints one folder's children, recursing into subfolders. Both view
        /// filters apply HERE rather than to the tree, so neither ever loses bytes: a
        /// depth-capped folder is drawn as one solid rect carrying its whole subtree, and a
        /// child under the min-size threshold stays inside its parent's rectangle, just not
        /// outlined on its own.
        /// </summary>
        private void LayoutAndDraw(DrawingContext dc, FsNode dir, Rect rect, int depth)
        {
            if (dir.Children == null || dir.Children.Count == 0 || dir.Size <= 0) return;
            if (rect.Width < 3 || rect.Height < 3) return;

            // Children are sorted largest-first, so the first one under the threshold means
            // every one after it is too - the filtered view is a PREFIX of the list, and the
            // squarify pass gets the sublist rather than a copy with holes in it.
            var children = dir.Children;
            if (_minSize > 0)
            {
                int keep = 0;
                while (keep < children.Count && children[keep].Size >= _minSize) keep++;
                if (keep == 0) return;                       // nothing here is big enough to draw
                if (keep < children.Count) children = children.GetRange(0, keep);
            }

            // Squarify against the PARENT's full size, not the kept children's sum: the kept
            // rectangles then keep their true proportion of the parent, and what is filtered
            // out simply leaves the parent's own fill showing. Scaling to the subset instead
            // would inflate every survivor and misrepresent the disk.
            Squarify(children, dir.Size, rect);

            foreach (var child in children)
            {
                var r = child.Rect;
                if (r.Width < 1 || r.Height < 1 || child.Gen != _gen) continue;

                bool atDepthCap = _depthLimit > 0 && depth >= _depthLimit;

                if (child.IsDir && !atDepthCap)
                {
                    // A folder is an outline whose interior the children fill; the 1px inset is
                    // what makes nesting readable at a glance.
                    dc.DrawRectangle(DirFill(), BorderPen(), r);
                    var inner = new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2));
                    LayoutAndDraw(dc, child, inner, depth + 1);
                }
                else
                {
                    // At the cap a FOLDER paints as one block in its own summary color, so the
                    // flattened view still reads as "this folder is the big one".
                    var fill = child.IsDir ? FolderSummaryBrush(child) : FileBrush(child);
                    dc.DrawRectangle(fill, r.Width > 2 && r.Height > 2 ? BorderPen() : null, r);
                }
            }
        }

        /// <summary>
        /// Squarified treemap (Bruls, Huizing, van Wijk): children (already largest-first) are
        /// laid in greedy rows along the shorter side, each row accepted only while it improves
        /// the worst aspect ratio. Writes every child's Rect + Gen; children too small to see
        /// get a degenerate rect the render loop then prunes.
        /// </summary>
        private void Squarify(List<FsNode> children, long total, Rect rect)
        {
            double x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height;
            double scale = w * h / Math.Max(1, total);   // pixels per byte

            int i = 0;
            while (i < children.Count)
            {
                bool horizontalRow = w < h;                     // rows lie along the SHORTER side
                double side = horizontalRow ? w : h;
                if (side < 1) { for (; i < children.Count; i++) { children[i].Rect = Rect.Empty; children[i].Gen = _gen; } return; }

                // Grow the row while the worst aspect ratio keeps improving.
                int start = i;
                double rowArea = 0, rowMax = 0, rowMin = double.MaxValue, worst = double.MaxValue;
                int end = i;
                while (end < children.Count)
                {
                    double a = Math.Max(0.0001, children[end].Size * scale);
                    double na = rowArea + a;
                    double nMax = Math.Max(rowMax, a), nMin = Math.Min(rowMin, a);
                    double nWorst = Math.Max(side * side * nMax / (na * na), na * na / (side * side * nMin));
                    if (nWorst > worst && end > start) break;
                    rowArea = na; rowMax = nMax; rowMin = nMin; worst = nWorst;
                    end++;
                }

                double thickness = rowArea / side;
                double along = horizontalRow ? x : y;
                for (int k = start; k < end; k++)
                {
                    double a = Math.Max(0.0001, children[k].Size * scale);
                    double len = a / Math.Max(0.0001, thickness);
                    children[k].Rect = horizontalRow
                        ? new Rect(along, y, len, thickness)
                        : new Rect(x, along, thickness, len);
                    children[k].Gen = _gen;
                    along += len;
                }

                if (horizontalRow) { y += thickness; h -= thickness; }
                else               { x += thickness; w -= thickness; }
                i = end;
                if (w < 0.5 || h < 0.5)
                {
                    for (; i < children.Count; i++) { children[i].Rect = Rect.Empty; children[i].Gen = _gen; }
                    return;
                }
            }
        }

        private void RenderOverlay(DrawingContext dc)
        {
            if (_selected != null && _selected.Gen == _gen && _selected.Rect.Width > 0)
            {
                var b = TryFindResource("PrimaryBrush") as Brush ?? Brushes.White;
                dc.DrawRectangle(null, new Pen(b, 2), _selected.Rect);
            }
            if (_hover != null && !ReferenceEquals(_hover, _selected) && _hover.Gen == _gen && _hover.Rect.Width > 0)
            {
                var b = TryFindResource("PrimaryBrush") as Brush ?? Brushes.White;
                dc.DrawRectangle(null, new Pen(b, 1), _hover.Rect);
            }
        }

        // ── Colors ───────────────────────────────────────────────
        private SolidColorBrush DirFill()
            // Folders stay near-invisible so the FILES carry the color; the outline pen and the
            // 1px inset are what say "folder". One fill for every folder, so it takes no node.
            => CachedBrush("dir", Color.FromArgb(26, 255, 255, 255));

        /// <summary>A folder drawn as ONE block because the depth cap stopped the recursion:
        /// its own top-branch hue in folder mode, and a neutral slate in type mode - there is
        /// no single "file type" for a whole subtree, and borrowing one would lie.</summary>
        private SolidColorBrush FolderSummaryBrush(FsNode dir)
        {
            if (_colorByFolder)
            {
                int idx = TopBranchIndex(dir);
                return CachedBrush("h" + idx, HsvToRgb((idx * 137.508) % 360.0, 0.55, 0.72));
            }
            return CachedBrush("dirsum", Color.FromRgb(0x8A, 0x8F, 0x98));
        }

        private SolidColorBrush FileBrush(FsNode file)
        {
            if (_colorByFolder)
            {
                // Hue per top-level branch of the SCAN root (stable while zooming), stepped by
                // the golden angle so neighbors differ.
                int idx = TopBranchIndex(file);
                return CachedBrush("h" + idx, HsvToRgb((idx * 137.508) % 360.0, 0.55, 0.72));
            }
            return CategoryBrush(file.Name);
        }

        private int TopBranchIndex(FsNode n)
        {
            var cur = n;
            while (cur.Parent != null && !ReferenceEquals(cur.Parent, _root)) cur = cur.Parent;
            if (cur.Parent == null || _root?.Children == null) return 0;
            int i = _root.Children.IndexOf(cur);
            return i < 0 ? 0 : i;
        }

        private static Color HsvToRgb(double h, double s, double v)
        {
            double c = v * s, m = v - c;
            double hp = h / 60.0, xx = c * (1 - Math.Abs(hp % 2 - 1));
            double r = 0, g = 0, b = 0;
            if (hp < 1) { r = c; g = xx; } else if (hp < 2) { r = xx; g = c; }
            else if (hp < 3) { g = c; b = xx; } else if (hp < 4) { g = xx; b = c; }
            else if (hp < 5) { r = xx; b = c; } else { r = c; b = xx; }
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }

        // Extension -> category color, the family neon set so the map reads in the same voice
        // as the shortcuts overlays. "Other" is deliberately gray: color means "identified".
        private static readonly Dictionary<string, string> ExtCategory = BuildExtCategories();
        private static Dictionary<string, string> BuildExtCategories()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(string cat, string exts) { foreach (var e in exts.Split(' ')) d[e] = cat; }
            Add("img", "png jpg jpeg gif bmp webp ico svg tif tiff raw heic psd xcf");
            Add("vid", "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts vob");
            Add("aud", "mp3 wav flac ogg m4a wma aac opus mid");
            Add("doc", "pdf doc docx xls xlsx ppt pptx odt ods odp txt md rtf csv epub one");
            Add("arc", "zip rar 7z tar gz bz2 xz iso cab wim vhd vhdx img");
            Add("code", "cs js ts py cpp c h hpp html css xaml json xml yml yaml sql ps1 psm1 sh bat cmd java rs go rb lua");
            Add("sys", "exe dll sys msi ocx drv efi mui winmd pdb lib obj");
            return d;
        }

        private SolidColorBrush CategoryBrush(string fileName)
        {
            string ext = "";
            int dot = fileName.LastIndexOf('.');
            if (dot >= 0 && dot < fileName.Length - 1) ext = fileName[(dot + 1)..];
            ExtCategory.TryGetValue(ext, out string? cat);
            return cat switch
            {
                "img"  => CachedBrush("img",  Color.FromRgb(0xF2, 0x22, 0xFF)),
                "vid"  => CachedBrush("vid",  Color.FromRgb(0x8C, 0x1E, 0xFF)),
                "aud"  => CachedBrush("aud",  Color.FromRgb(0x00, 0xC8, 0xC3)),
                "doc"  => CachedBrush("doc",  Color.FromRgb(0xFF, 0xD3, 0x19)),
                "arc"  => CachedBrush("arc",  Color.FromRgb(0xFF, 0x8C, 0x00)),
                "code" => CachedBrush("code", Color.FromRgb(0x39, 0xC8, 0x14)),
                "sys"  => CachedBrush("sys",  Color.FromRgb(0xC8, 0x3C, 0x38)),
                _      => CachedBrush("oth",  Color.FromRgb(0x6E, 0x6E, 0x6E)),
            };
        }

        // ═══════════════════════════════════════════════════════════
        //  INTERACTION
        // ═══════════════════════════════════════════════════════════
        /// <summary>Deepest FILE at a point, or the deepest laid-out node when the point only
        /// hits pruned children - descending by rectangle from the zoom root, O(depth).</summary>
        private FsNode? NodeAt(Point p)
        {
            if (_scanning) return null;   // same tree-walk-during-scan race RenderMap avoids
            var cur = _zoomRoot;
            if (cur == null || cur.Gen != _gen || !cur.Rect.Contains(p)) return null;
            int depth = 1;
            while (cur.Children != null)
            {
                // Stop where the PAINT stopped: past the depth cap a folder is one solid block,
                // so hit-testing deeper would report a child the user cannot see. Gen already
                // guards the min-size filter - a filtered child never got a rect this pass.
                if (_depthLimit > 0 && depth > _depthLimit) break;

                FsNode? next = null;
                foreach (var c in cur.Children)
                    if (c.Gen == _gen && c.Rect.Width > 0 && c.Rect.Contains(p)) { next = c; break; }
                if (next == null) break;
                cur = next;
                depth++;
            }
            return ReferenceEquals(cur, _zoomRoot) ? null : cur;
        }

        private void Map_MouseMove(object sender, MouseEventArgs e)
        {
            var n = NodeAt(e.GetPosition(_map));
            if (ReferenceEquals(n, _hover)) return;
            _hover = n;
            UpdateFooterLeft();
            _map.InvalidateOverlay();
        }

        private void Map_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var n = NodeAt(e.GetPosition(_map));
            if (e.ClickCount == 2)
            {
                // Double-click zooms: into the clicked node's FOLDER (or the folder itself).
                var dir = n == null ? null : n.IsDir ? n : n.Parent;
                if (dir != null && !ReferenceEquals(dir, _zoomRoot)) ZoomTo(dir);
                return;
            }
            _selected = n;
            UpdateFooterLeft();
            PinSelectionToWindowStatus(n);
            _map.InvalidateOverlay();
        }

        // How long a clicked path stays in the window's status bar before the scan summary
        // takes it back. Long enough to read a long path, short enough not to look stuck.
        private static readonly TimeSpan PinnedPathHold = TimeSpan.FromSeconds(5);
        private DispatcherTimer? _pinnedPathTimer;

        /// <summary>
        /// Put the clicked node's full path in the WINDOW status bar for a few seconds.
        /// The pane footer already shows it, but that follows the CURSOR - so the moment you
        /// move the mouse to read a long path it has already changed to whatever you passed
        /// over. Clicking pins it somewhere that does not move.
        /// The window status line trims from the FRONT (ElideFooterStatus), so a path too long
        /// for the bar keeps its file name rather than losing it.
        /// </summary>
        private void PinSelectionToWindowStatus(FsNode? n)
        {
            _pinnedPathTimer?.Stop();

            // Nothing clicked - a click on empty map space deselects, and pinning the last
            // path there would report something that is no longer selected.
            if (n == null) { ReportStatus?.Invoke(ProgressSummary()); return; }

            ReportStatus?.Invoke(FullPath(n));

            // Not restarted from scratch each click: one timer, stopped and re-started, so
            // clicking five squares in a row does not leave five pending reverts racing.
            _pinnedPathTimer ??= new DispatcherTimer();
            _pinnedPathTimer.Interval = PinnedPathHold;
            _pinnedPathTimer.Tick -= PinnedPathExpired;
            _pinnedPathTimer.Tick += PinnedPathExpired;
            _pinnedPathTimer.Start();
        }

        private void PinnedPathExpired(object? sender, EventArgs e)
        {
            _pinnedPathTimer?.Stop();
            // Back to whatever the tab would otherwise be saying - the scan summary, or the
            // progress line if a scan is running.
            ReportStatus?.Invoke(ProgressSummary());
        }

        private void ZoomTo(FsNode dir)
        {
            _zoomRoot = dir;
            _hover = null;
            RebuildBreadcrumb();
            _map.InvalidateMap();
            _map.InvalidateOverlay();
        }

        private void RebuildBreadcrumb()
        {
            _breadcrumb.Children.Clear();
            if (_root == null || _zoomRoot == null) { _breadcrumb.Visibility = Visibility.Collapsed; return; }
            _breadcrumb.Visibility = Visibility.Visible;

            var chain = new List<FsNode>();
            for (var cur = _zoomRoot; cur != null; cur = cur.Parent) chain.Insert(0, cur);

            for (int i = 0; i < chain.Count; i++)
            {
                var node = chain[i];
                bool last = i == chain.Count - 1;
                var tb = new TextBlock
                {
                    Text = node.Name,
                    FontSize = 11.5,
                    Cursor = last ? Cursors.Arrow : Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                // The crumb you are AT is accent (it is the title); the ones you can go back to
                // rest on plain text and go accent on hover - the family hover language.
                tb.SetResourceReference(TextBlock.ForegroundProperty, last ? "PrimaryBrush" : "TextBrush");
                if (!last)
                {
                    var captured = node;
                    tb.MouseEnter += (_, _) => tb.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
                    tb.MouseLeave += (_, _) => tb.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                    tb.MouseLeftButtonUp += (_, _) => ZoomTo(captured);
                }
                _breadcrumb.Children.Add(tb);

                if (!last)
                {
                    var sep = new TextBlock { Text = "  \\  ", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
                    sep.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                    sep.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                    _breadcrumb.Children.Add(sep);
                }
            }
        }

        // ── Context menu ─────────────────────────────────────────
        internal Action<string>? OpenFolderInNewTab;   // wired by StorageTabs.cs

        private void Map_RightClick(MouseButtonEventArgs e)
        {
            var n = NodeAt(e.GetPosition(_map));
            if (n != null) { _selected = n; UpdateFooterLeft(); _map.InvalidateOverlay(); }

            var menu = new ContextMenu();

            if (n != null)
            {
                string path = FullPath(n);

                var openTab = MakeItem("Str_Storage_OpenInTab", 0xE8DA);
                openTab.Click += (_, _) => OpenFolderInNewTab?.Invoke(n.IsDir ? path : (Path.GetDirectoryName(path) ?? path));
                menu.Items.Add(openTab);

                var reveal = MakeItem("Str_Storage_RevealExplorer", 0xE838);
                reveal.Click += (_, _) =>
                {
                    try
                    {
                        if (n.IsDir) System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
                        else System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                    }
                    catch { /* explorer refusing to start is not this tab's problem to explain */ }
                };
                menu.Items.Add(reveal);

                var copy = MakeItem("Str_Storage_CopyPath", 0xE8C8);
                copy.Click += (_, _) => { try { Clipboard.SetText(path); } catch { } };
                menu.Items.Add(copy);

                menu.Items.Add(new Separator());

                var del = MakeItem("Str_Storage_Delete", 0xE74D);
                del.Click += (_, _) => RecycleNode(n, path);
                menu.Items.Add(del);

                menu.Items.Add(new Separator());
            }

            // The color-mode toggle lives here per the design decision - checked radios, so the
            // current mode is visible at a glance.
            var byType = new MenuItem { IsCheckable = true, IsChecked = !_colorByFolder };
            byType.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Storage_ColorByType");
            byType.Click += (_, _) => SetColorMode(byFolder: false);
            menu.Items.Add(byType);

            var byFolder = new MenuItem { IsCheckable = true, IsChecked = _colorByFolder };
            byFolder.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Storage_ColorByFolder");
            byFolder.Click += (_, _) => SetColorMode(byFolder: true);
            menu.Items.Add(byFolder);

            if (_root != null)
            {
                menu.Items.Add(new Separator());
                var rescan = MakeItem("Str_Storage_Rescan", 0xE72C);
                rescan.Click += (_, _) => { if (!_scanning) StartScan(); };
                menu.Items.Add(rescan);
            }

            menu.PlacementTarget = _map;
            menu.IsOpen = true;
        }

        private MenuItem MakeItem(string headerKey, int glyph)
        {
            var item = new MenuItem();
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, headerKey);
            var icon = new TextBlock { Text = ((char)glyph).ToString() };
            icon.SetResourceReference(FrameworkElement.StyleProperty, "MenuGlyph");
            item.Icon = icon;
            return item;
        }

        private void SetColorMode(bool byFolder)
        {
            if (_colorByFolder == byFolder) return;
            _colorByFolder = byFolder;
            Services.ThemeManager.SetSetting("StorageColorMode", byFolder ? "folder" : "cat");
            SyncViewButtons();   // the toolbar pair mirrors this, whichever surface set it
            _map.InvalidateMap();
        }

        // ── Recycle-bin delete (the ONLY destructive action, v1) ─
        private void RecycleNode(FsNode n, string path)
        {
            // FOF_ALLOWUNDO and NO FOF_NOCONFIRMATION: the shell's own recycle confirmation
            // stays on, and nothing here can permanently delete.
            var op = new SHFILEOPSTRUCT
            {
                wFunc = 3,                       // FO_DELETE
                pFrom = path + "\0\0",           // double-null-terminated list of one
                fFlags = 0x40,                   // FOF_ALLOWUNDO
            };
            int rc = SHFileOperationW(ref op);
            if (rc != 0 || op.fAnyOperationsAborted) return;

            // Peel the node out of the tree and roll its bytes out of every ancestor - no
            // rescan needed for the map to be truthful again.
            long delta = n.Size;
            if (n.Parent?.Children != null) n.Parent.Children.Remove(n);
            for (var cur = n.Parent; cur != null; cur = cur.Parent) cur.Size -= delta;

            // Deleting the zoom root (or something above it) zooms back to the survivor chain.
            for (var cur = _zoomRoot; cur != null; cur = cur.Parent)
                if (ReferenceEquals(cur, n)) { _zoomRoot = n.Parent ?? _root; RebuildBreadcrumb(); break; }

            if (ReferenceEquals(_selected, n)) _selected = null;
            if (ReferenceEquals(_hover, n)) _hover = null;
            UpdateFooterLeft();
            UpdateFooterRight();
            _map.InvalidateMap();
            _map.InvalidateOverlay();
        }

        // ── Footer ───────────────────────────────────────────────
        private string FullPath(FsNode n)
        {
            var parts = new List<string>();
            for (var cur = n; cur != null; cur = cur.Parent) parts.Insert(0, cur.Name);
            // The root's Name IS the root path, so joining from it rebuilds the absolute path.
            string joined = parts[0].TrimEnd('\\');
            for (int i = 1; i < parts.Count; i++) joined += "\\" + parts[i];
            return joined;
        }

        private void UpdateFooterLeft()
        {
            var n = _hover ?? _selected;
            if (n == null || _zoomRoot == null || _zoomRoot.Size <= 0)
            {
                _footerFull = "";
                _footerLeft.Text = "";
                return;
            }

            double pct = 100.0 * n.Size / _zoomRoot.Size;
            _footerFull = FullPath(n) + "  -  " + FormatSize(n.Size)
                + "  -  " + pct.ToString("0.0", CultureInfo.InvariantCulture) + "%";
            ElideFooterLeft();
        }

        // The untrimmed line. Kept whole so a resize can re-cut it from the original rather than
        // from an already-trimmed copy, which would eat more of it every time the window moved.
        private string _footerFull = "";
        private TextBlock? _footerMeasure;

        // Ellipsis from its codepoint - these sources stay 0 non-ASCII bytes.
        private static readonly string FooterEllipsis = ((char)0x2026).ToString();

        /// <summary>
        /// Trim the footer line from the FRONT, pinning its END to the right. The line is
        /// "path - size - percent", so a tail trim (TextTrimming.CharacterEllipsis, what this
        /// used to be) threw away the file name, the size AND the percentage and left nothing
        /// but the directories you already knew you were in. The tail is the whole point.
        /// Same binary search over measured widths as MainWindow's ElideFooterStatus.
        /// </summary>
        private void ElideFooterLeft()
        {
            if (_footerFull.Length == 0) { _footerLeft.Text = ""; return; }

            double avail = _footerLeft.ActualWidth;
            if (avail <= 0) { _footerLeft.Text = _footerFull; return; }   // pre-layout
            if (Measure(_footerFull) <= avail) { _footerLeft.Text = _footerFull; return; }

            // Longest SUFFIX that still fits behind a leading ellipsis.
            int lo = 0, hi = _footerFull.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;   // drop `mid` characters off the front
                if (Measure(FooterEllipsis + _footerFull[mid..]) <= avail) hi = mid;
                else lo = mid + 1;
            }
            _footerLeft.Text = FooterEllipsis + _footerFull[Math.Min(lo, _footerFull.Length)..];

            double Measure(string s)
            {
                // Font properties are re-copied each call: both ride DynamicResources that a
                // theme or locale switch can change underneath us.
                _footerMeasure ??= new TextBlock();
                _footerMeasure.FontFamily  = _footerLeft.FontFamily;
                _footerMeasure.FontSize    = _footerLeft.FontSize;
                _footerMeasure.FontStyle   = _footerLeft.FontStyle;
                _footerMeasure.FontWeight  = _footerLeft.FontWeight;
                _footerMeasure.FontStretch = _footerLeft.FontStretch;
                TextOptions.SetTextFormattingMode(
                    _footerMeasure, TextOptions.GetTextFormattingMode(_footerLeft));
                _footerMeasure.Text = s;
                _footerMeasure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return _footerMeasure.DesiredSize.Width;
            }
        }

        private void UpdateFooterRight()
        {
            try
            {
                string? root = Path.GetPathRoot(_rootPath.Length > 0 ? _rootPath : _targetBox.Text);
                if (string.IsNullOrEmpty(root)) { _footerRight.Text = ""; return; }
                var di = new DriveInfo(root!);
                _footerRight.Text = root + "  " + FormatSize(di.TotalSize) + " " + MainWindow.LocStatic("Str_Storage_Total")
                    + "  -  " + FormatSize(di.TotalFreeSpace) + " " + MainWindow.LocStatic("Str_Storage_Free");
            }
            catch { _footerRight.Text = ""; }
        }

        private static string FormatSize(long bytes)
        {
            const double kb = 1024, mb = kb * 1024, gb = mb * 1024, tb = gb * 1024;
            if (bytes >= tb) return (bytes / tb).ToString("0.00", CultureInfo.InvariantCulture) + " TB";
            if (bytes >= gb) return (bytes / gb).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            if (bytes >= mb) return (bytes / mb).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (bytes >= kb) return (bytes / kb).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        // ═══════════════════════════════════════════════════════════
        //  P/Invoke - enumeration and recycle
        // ═══════════════════════════════════════════════════════════
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]  public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileExW(string lpFileName, int fInfoLevelId,
            out WIN32_FIND_DATAW lpFindFileData, int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATAW lpFindFileData);

        [DllImport("kernel32.dll")]
        private static extern bool FindClose(IntPtr hFindFile);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);
    }
}
