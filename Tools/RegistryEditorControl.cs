using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using KillerShell.Shell;

// The control behind the Registry Editor tab: a real, working regedit-equivalent - browse the
// five hives, view and edit values, create/rename/delete keys and values, search - built entirely
// in code, same stand-alone shape as ProcessListControl.cs/EventViewerControl.cs.
//
// Unlike EventViewerControl there is no background load and no timer anywhere in this file:
// every registry read Microsoft.Win32.RegistryKey does is a fast local call (no network, no
// large per-call payload the way an event log read is), so the whole control runs synchronously
// on the UI thread, the same way FolderTree.cs's lazy children load is the one place in this app
// that DOES go async, because a folder tree has to survive a slow network share. A registry key
// never does.
//
// Reached only through Ctrl+F11 (Elevation.cs RelaunchElevatedRegistryEditor) - see the file
// header on RegistryEditorTabs.cs for why there is no unelevated variant at all.
namespace KillerShell.Tools
{
    // ═══════════════════════════════════════════════════════════
    //  TREE MODEL  -  one node per key, children loaded only when a node is actually expanded.
    //  Same lazy-placeholder shape as FolderTree.cs's FolderNode, adapted to the registry: every
    //  key node optimistically gets a placeholder child so WPF draws an expander arrow, and the
    //  real children replace it on first expand (or on a manual F5 refresh).
    // ═══════════════════════════════════════════════════════════
    internal sealed class RegistryNode : INotifyPropertyChanged
    {
        private static readonly RegistryNode Placeholder = new(string.Empty, string.Empty, isPlaceholder: true);

        public string Name { get; }
        public string FullPath { get; }
        public bool IsRoot { get; }

        /// <summary>Set when a child is created (LoadChildren below) - lets Find/rename/delete
        /// walk back up to the parent key without the control keeping a second, parallel map.</summary>
        internal RegistryNode? Parent { get; set; }

        public ObservableCollection<RegistryNode> Children { get; } = [];

        public bool IsLoaded { get; private set; }

        /// <summary>Set by LoadChildren when the last enumeration failed - read once, right after,
        /// by whoever triggered the load, so it can put the reason on the status line.</summary>
        internal string? LoadError { get; private set; }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded != value) { _isExpanded = value; Raise(); } }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; Raise(); } }
        }

        public RegistryNode(string fullPath, string name, bool isRoot = false, bool isPlaceholder = false)
        {
            FullPath = fullPath;
            Name = name;
            IsRoot = isRoot;
            if (!isPlaceholder) Children.Add(Placeholder);
        }

        /// <summary>Replaces the placeholder with the real subkeys. Never enumerates anything
        /// beyond this one level - a key with a thousand subkeys of its own stays untouched until
        /// each is individually expanded, the same restraint FolderTree.cs applies to the disk.</summary>
        public void LoadChildren()
        {
            if (IsLoaded) return;
            IsLoaded = true;
            LoadError = null;

            var kids = new List<RegistryNode>();

            // --demo never touches the real registry - see Services\DemoRegistry.cs. RegistryKey
            // is sealed, so there is no faking one; this reads the fabricated table instead of
            // ever calling RegistryPathHelper.OpenKey.
            if (MainWindow.DemoMode)
            {
                foreach (var name in KillerShell.Services.DemoRegistry.ChildrenOf(FullPath))
                    kids.Add(new RegistryNode(FullPath + "\\" + name, name) { Parent = this });
                Children.Clear();
                foreach (var k in kids) Children.Add(k);
                return;
            }

            try
            {
                using var key = RegistryPathHelper.OpenKey(FullPath, writable: false);
                if (key != null)
                    foreach (var name in key.GetSubKeyNames()
                                              .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                        kids.Add(new RegistryNode(FullPath + "\\" + name, name) { Parent = this });
            }
            catch (SecurityException)
            {
                LoadError = MainWindow.LocStatic("Str_RegEd_AccessDenied");
            }
            catch (UnauthorizedAccessException)
            {
                LoadError = MainWindow.LocStatic("Str_RegEd_AccessDenied");
            }
            catch (IOException)
            {
                // Deleted or unmounted between the tree showing it and the click that expanded
                // it - not worth a status message, the row is about to disappear on next refresh.
            }
            catch (ArgumentException)
            {
                // HKEY_CLASSES_ROOT is a merged HKLM+HKCU view and routinely carries a stray
                // subkey some installer's COM registration left with an invalid name (embedded
                // NUL, over-length). GetSubKeyNames() throws ArgumentException on the whole call
                // rather than skipping just that one entry - same crash class the rest of this
                // file already guards against (see the rename/delete/create catches below), just
                // missing here. No dedicated status string for this - it is the same "nothing to
                // show" outcome as a key vanishing mid-read (IOException, above), not a real
                // access problem, so it stays silent the same way.
            }

            Children.Clear();
            foreach (var k in kids) Children.Add(k);
        }

        /// <summary>Re-reads this node's own children from scratch - F5 (RegistryEditorControl.cs
        /// RefreshCurrent). Deliberately simpler than FolderTree.RefreshAsync's in-place
        /// reconciliation: a rebuilt child keeps its own name-derived FullPath and gets its OWN
        /// fresh placeholder, so anything nested more than one level below the refreshed key
        /// collapses back to unloaded. Registry trees are shallower and far cheaper to re-expand
        /// than a deep folder tree, so that trade is the right one here.</summary>
        public void Refresh()
        {
            IsLoaded = false;
            LoadChildren();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    // ═══════════════════════════════════════════════════════════
    //  PATH RESOLUTION  -  the five hives this tab roots at, and turning a "HIVE\Sub\Key" string
    //  (the tree's own FullPath, and whatever the address bar's Enter hands back) into a real,
    //  freshly-opened RegistryKey. Local machine only - no RegistryView/remote-registry handling,
    //  by design (out of scope, see the file header on RegistryEditorTabs.cs).
    // ═══════════════════════════════════════════════════════════
    internal static class RegistryPathHelper
    {
        internal static readonly (string Name, RegistryKey Root)[] Hives =
        [
            ("HKEY_CLASSES_ROOT",   Registry.ClassesRoot),
            ("HKEY_CURRENT_USER",   Registry.CurrentUser),
            ("HKEY_LOCAL_MACHINE",  Registry.LocalMachine),
            ("HKEY_USERS",          Registry.Users),
            ("HKEY_CURRENT_CONFIG", Registry.CurrentConfig),
        ];

        /// <summary>Opens <paramref name="fullPath"/> fresh - the caller disposes it. Returns null
        /// for an unknown hive name or a key that no longer exists; never throws for that case,
        /// only for a genuine access failure (caller catches SecurityException/
        /// UnauthorizedAccessException around the call, same as LoadChildren above).</summary>
        internal static RegistryKey? OpenKey(string fullPath, bool writable)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;

            int sep = fullPath.IndexOf('\\');
            string hiveName = sep < 0 ? fullPath : fullPath[..sep];
            string sub      = sep < 0 ? string.Empty : fullPath[(sep + 1)..];

            foreach (var (Name, Root) in Hives)
                if (string.Equals(Name, hiveName, StringComparison.OrdinalIgnoreCase))
                    return sub.Length == 0 ? Root : Root.OpenSubKey(sub, writable);

            return null;
        }

        internal static string ParentPath(string fullPath)
        {
            int idx = fullPath.LastIndexOf('\\');
            return idx < 0 ? string.Empty : fullPath[..idx];
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  VALUE FORMATTING  -  Name/Type/Data exactly the way regedit itself shows them: a DWORD/
    //  QWORD as both hex and decimal, binary as space-separated hex byte pairs, a multi-string
    //  with its entries visibly separated rather than run together.
    // ═══════════════════════════════════════════════════════════
    internal static class RegistryValueFormat
    {
        internal static string KindLabel(RegistryValueKind k) => k switch
        {
            RegistryValueKind.String       => "REG_SZ",
            RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
            RegistryValueKind.Binary       => "REG_BINARY",
            RegistryValueKind.DWord        => "REG_DWORD",
            RegistryValueKind.MultiString  => "REG_MULTI_SZ",
            RegistryValueKind.QWord        => "REG_QWORD",
            _                              => "REG_NONE",
        };

        // HKEY_CLASSES_ROOT is a merged HKLM+HKCU view and, past the ArgumentException fix for
        // malformed names, can also carry a value whose DATA is pathologically large (some stray
        // COM registration blob stored as REG_SZ/REG_BINARY instead of the small string regedit
        // expects). A DataGrid cell has to measure and lay out whatever string it is handed, and
        // WPF's text layout is not linear in string length - a multi-megabyte cell freezes the UI
        // thread for a long time with nothing to catch, which reads as "app hung", not "app
        // crashed". Cap what ever reaches the grid; Modify still reads and edits the real,
        // untruncated value, this only bounds what gets displayed.
        private const int MaxDisplayChars = 4000;

        private static string Truncate(string s)
            => s.Length <= MaxDisplayChars
                ? s
                : s[..MaxDisplayChars] + $"...  ({s.Length} chars total)";

        internal static string DataLabel(object? value, RegistryValueKind kind)
        {
            switch (kind)
            {
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString:
                    return Truncate(value as string ?? string.Empty);

                case RegistryValueKind.DWord:
                {
                    uint v = unchecked((uint)Convert.ToInt64(value ?? 0, System.Globalization.CultureInfo.InvariantCulture));
                    return $"0x{v:x8} ({v})";
                }

                case RegistryValueKind.QWord:
                {
                    ulong v = unchecked((ulong)Convert.ToInt64(value ?? 0L, System.Globalization.CultureInfo.InvariantCulture));
                    return $"0x{v:x16} ({v})";
                }

                case RegistryValueKind.Binary:
                {
                    var bytes = value as byte[] ?? [];
                    if (bytes.Length == 0) return string.Empty;
                    // Cap the byte count BEFORE building the hex string, not after - a many-MB
                    // blob turned into a "XX XX XX ..." string first would already have paid the
                    // cost Truncate exists to avoid.
                    int shown = Math.Min(bytes.Length, MaxDisplayChars / 3);
                    var hex = string.Join(" ", bytes.Take(shown).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
                    return shown < bytes.Length ? hex + $" ...  ({bytes.Length} bytes total)" : hex;
                }

                case RegistryValueKind.MultiString:
                {
                    var arr = value as string[] ?? [];
                    // " | " between entries, not the raw NUL/CRLF regedit's own file format uses -
                    // this is a grid cell, and entries have to stay visibly separated on one line.
                    return Truncate(string.Join("  |  ", arr));
                }

                default:
                    return Truncate(value?.ToString() ?? string.Empty);
            }
        }
    }

    /// <summary>One row of the value grid.</summary>
    internal sealed class RegistryValueRow
    {
        /// <summary>Raw value name - empty string for the unnamed default value.</summary>
        internal string Name { get; }
        public string DisplayName => Name.Length == 0 ? "(Default)" : Name;
        internal RegistryValueKind Kind { get; }
        public string KindLabel => RegistryValueFormat.KindLabel(Kind);
        internal object? RawValue { get; }
        public string DataLabel => RegistryValueFormat.DataLabel(RawValue, Kind);

        internal RegistryValueRow(string name, RegistryValueKind kind, object? rawValue)
        {
            Name = name;
            Kind = kind;
            RawValue = rawValue;
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  THE CONTROL
    // ═══════════════════════════════════════════════════════════
    internal sealed class RegistryEditorControl : Grid
    {
        private readonly ObservableCollection<RegistryNode> _roots = [];
        private readonly ObservableCollection<RegistryValueRow> _values = [];

        private readonly TreeView _tree;
        private readonly DataGrid _grid;

        private readonly Border _pathDisplayHost;
        private readonly TextBlock _pathDisplay;
        private readonly TextBox _pathEdit;

        private readonly Border _findBar;
        private readonly TextBox _findBox;

        private readonly TextBlock _statusLine;
        private readonly DispatcherTimer _statusClearTimer;

        private RegistryNode? _selectedNode;

        // Where Find Next resumes from - cleared whenever the query text changes, so editing the
        // search term always starts a fresh pass from the top rather than resuming mid-way
        // through a match set that no longer applies.
        private RegistryNode? _lastFindNode;
        private string? _lastFindValue;

        internal RegistryEditorControl()
        {
            // PaneBrush, painted here on the control's own root Grid: this
            // sits inside ResultsSurface on MenuBackgroundBrush, a full step darker still than
            // PaneBrush - the toolbar/find-bar/split/status-line children below are all built
            // with an 8px outer Margin (never edge-to-edge the way the editor bar and its slot
            // are), so without this the margin gaps around every one of them showed the raw
            // MenuBackgroundBrush through, which read as the whole tab sitting on black. Same
            // fix in spirit as EditorControl painting PaneBrush on itself.
            SetResourceReference(BackgroundProperty, "PaneBrush");

            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // address bar
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // find bar (collapsed)
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // status line

            // Grain over the opaque PaneBrush root, spanning all four rows. The opaque root is
            // what hides PaneContent's own grain layer, so this tab was the flat, textureless
            // surface in the window - it has to repaint the texture itself, the way the folder
            // LocationRow does over its own opaque face. Added before the row content, so the
            // toolbar, tree, grid and status line all paint above it. 0 opacity on 98SE.
            var grain = ToolTabChrome.Grain();
            SetRowSpan(grain, 4);
            Children.Add(grain);

            // ToolTabChrome: the address row rides the RAISED menu-bar tier on 98SE, and the
            // tree and value grid below sit in their own sunken WHITE wells (BuildSplit) -
            // content sunken, menu bar raised, with the reg tree sidebar and the right side
            // each their own sunken white pane. All of it inert on the ordinary themes.
            var toolbar = ToolTabChrome.WrapBar(BuildToolbar(out _pathDisplayHost, out _pathDisplay, out _pathEdit));
            SetRow(toolbar, 0);
            Children.Add(toolbar);

            _findBar = BuildFindBar(out _findBox);
            SetRow(_findBar, 1);
            Children.Add(_findBar);

            var split = BuildSplit(out _tree, out _grid);
            SetRow(split, 2);
            Children.Add(split);

            _statusLine = BuildStatusLine();
            SetRow(_statusLine, 3);
            Children.Add(_statusLine);

            _statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _statusClearTimer.Tick += (_, _) => { _statusClearTimer.Stop(); ShowStatus(string.Empty, error: false); };

            // Cheap: five nodes, no registry access until one is actually expanded. Never deferred
            // to Loaded the way EventViewerControl's first query is - there is nothing here worth
            // waiting for.
            foreach (var h in RegistryPathHelper.Hives)
                _roots.Add(new RegistryNode(h.Name, h.Name, isRoot: true));
            _tree.ItemsSource = _roots;

            PreviewKeyDown += Control_PreviewKeyDown;

            Services.ThemeManager.ThemeChanged += OnThemeChanged;
        }

        /// <summary>Torn down when the tab closes, and when the whole window closes with this tab
        /// still open (Session.cs OnClosing, via ShutdownAllRegistryEditors).</summary>
        internal void Shutdown()
        {
            _statusClearTimer.Stop();
            Services.ThemeManager.ThemeChanged -= OnThemeChanged;
        }

        private void OnThemeChanged() => ApplyTreeChevronMask(_tree);

        /// <summary>
        /// Point this tree's chevron mask at the surface it actually sits on.
        /// A local Resources entry, because the chevron template resolves the key with a
        /// DynamicResource and that walks up from the element - so this wins inside this tree
        /// and changes nothing in the folder sidebar.
        /// Re-applied on ThemeChanged: a ResourceDictionary entry holds a brush, not a live
        /// reference to one, so it would otherwise keep the outgoing theme's color.
        /// </summary>
        /// <remarks>
        /// The mask's whole job is to vanish into what is behind the chevron, so it has to be
        /// that exact color. The tree sits inside ToolTabChrome.WrapContent's well, whose face
        /// is ToolTreeBrush painted over this control's own PaneBrush root. Those are two
        /// different answers depending on the theme:
        ///   - on the twelve rounded themes ToolTreeBrush is TRANSPARENT, so the color behind
        ///     the chevron really is PaneBrush, which is what this always used;
        ///   - on the flat theme the well is an opaque WHITE client area, while PaneBrush there
        ///     is the #c0c0c0 window face - so a PaneBrush mask drew a gray wedge straight
        ///     through the middle of every chevron in a white tree.
        /// Take the well's own face whenever it is opaque, and the pane underneath it when it
        /// is not, so both cases resolve to the pixels actually being covered.
        /// </remarks>
        private static void ApplyTreeChevronMask(TreeView tree)
        {
            if (tree == null) return;
            var app = Application.Current;
            if (app == null) return;

            var surface = app.TryFindResource("ToolTreeBrush") as Brush;
            // A transparent well is not a surface - the pane shows through it, so read the pane.
            // Tested on the brush rather than on the theme name: nothing else in this file
            // branches on which theme is loaded, and a future theme that fills the well gets the
            // right answer without being added to a list.
            if (surface == null || (surface is SolidColorBrush s && s.Color.A == 0))
                surface = app.TryFindResource("PaneBrush") as Brush;

            if (surface != null) tree.Resources["TreeChevronMaskBrush"] = surface;
        }

        // ═══════════════════════════════════════════════════════════
        //  BUILD - toolbar / find bar / split / status line
        // ═══════════════════════════════════════════════════════════
        private Grid BuildToolbar(out Border pathHost, out TextBlock pathDisplay, out TextBox pathEdit)
        {
            var bar = new Grid { Margin = new Thickness(8, 8, 8, 6) };
            // No background of its own. It carried an opaque PaneBrush to cover the darker
            // MenuBackgroundBrush that used to show in its margins, but the control's root Grid
            // paints PaneBrush behind everything already, and WrapBar's face is PaneBrush too -
            // so the only thing this opaque layer still did was sit on top of WrapBar's grain
            // and leave the address bar as the one textureless strip on the tab.
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Click-to-edit address field: display mode is a plain TextBlock, edit mode swaps in a
            // TextBox with the same interaction shape as the terminal bar's own editable location
            // field - click to edit, Enter to navigate/commit, Escape to cancel, staying in edit
            // mode with a status message when the typed path does not resolve.
            pathHost = new Border
            {
                Padding = new Thickness(8, 4, 8, 4),
                Cursor = Cursors.IBeam,
            };
            // TextFieldBrush + InputEdgeBrush - the theme's own text-field rule (DarkTextBox,
            // Controls.xaml), not PaneBrush + CardBorderBrush: this IS a text input, it just
            // happens to be built as a click-to-edit Border, and painting it as a card left it
            // the one field in the app not dressed like one. TextFieldBrush is derived SOLID on
            // the gradient themes (ThemeManager), so no gradient re-ramp inside the box.
            pathHost.SetResourceReference(Border.BackgroundProperty, "TextFieldBrush");
            pathHost.SetResourceReference(Border.BorderBrushProperty, "InputEdgeBrush");
            pathHost.BorderThickness = new Thickness(1);
            pathHost.CornerRadius = new CornerRadius(KillerShell.Services.ThemeManager.Radius("SmallCornerRadius", 3));

            pathDisplay = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            pathDisplay.SetResourceReference(TextBlock.TextProperty, "Str_RegEd_ComputerRoot");
            pathDisplay.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            pathEdit = new TextBox
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Visibility = Visibility.Collapsed,
            };
            pathEdit.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");

            var pathGrid = new Grid();
            pathGrid.Children.Add(pathDisplay);
            pathGrid.Children.Add(pathEdit);
            pathHost.Child = pathGrid;

            pathHost.MouseLeftButtonDown += (_, _) => BeginEditPath();
            pathEdit.KeyDown += PathEdit_KeyDown;
            pathEdit.LostFocus += (_, _) => CancelEditPath();

            SetColumn(pathHost, 0);
            bar.Children.Add(pathHost);

            // E721 - the same "find" glyph the document editor's own Find command uses
            // (Editing/EditorMenu.cs): one glyph means one action everywhere in the app.
            var findBtn = IconButton(0xE721, "Str_TT_RegFind");
            findBtn.Margin = new Thickness(6, 0, 0, 0);
            findBtn.Click += (_, _) => ToggleFindBar();
            SetColumn(findBtn, 1);
            bar.Children.Add(findBtn);

            // E72C (Refresh): re-reads the currently selected key's children and values from
            // disk - same glyph and meaning as Event Viewer's own refresh button.
            var refreshBtn = IconButton(0xE72C, "Str_TT_RegRefresh");
            refreshBtn.Margin = new Thickness(6, 0, 0, 0);
            refreshBtn.Click += (_, _) => RefreshCurrent();
            SetColumn(refreshBtn, 2);
            bar.Children.Add(refreshBtn);

            return bar;
        }

        private static Button IconButton(int glyph, string tooltipKey)
        {
            var btn = new Button
            {
                Content = ((char)glyph).ToString(),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                Style = (Style)FindResourceStatic("SurfaceButton"),
            };
            btn.SetResourceReference(ToolTipProperty, tooltipKey);
            return btn;
        }

        private Border BuildFindBar(out TextBox findBox)
        {
            var bar = new Border
            {
                Margin = new Thickness(8, 0, 8, 6),
                Padding = new Thickness(6, 4, 6, 4),
                Visibility = Visibility.Collapsed,
            };
            bar.SetResourceReference(Border.BackgroundProperty, "PaneBrush");
            bar.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            bar.BorderThickness = new Thickness(1);
            bar.CornerRadius = new CornerRadius(KillerShell.Services.ThemeManager.Radius("SmallCornerRadius", 3));

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            findBox = new TextBox
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
            };
            findBox.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
            findBox.TextChanged += (_, _) => { _lastFindNode = null; _lastFindValue = null; };
            findBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { FindNext(); e.Handled = true; }
                else if (e.Key == Key.Escape) { CloseFindBar(); e.Handled = true; }
            };
            SetColumn(findBox, 0);
            row.Children.Add(findBox);

            var nextBtn = new Button
            {
                Content = ((char)0xE721).ToString(),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                Style = (Style)FindResourceStatic("SurfaceButton"),
            };
            nextBtn.SetResourceReference(ToolTipProperty, "Str_RegEd_FindNext");
            nextBtn.Click += (_, _) => FindNext();
            SetColumn(nextBtn, 1);
            row.Children.Add(nextBtn);

            var closeBtn = new Button
            {
                Content = ((char)0x2715).ToString(),   // multiplication-X, same close glyph ConfirmDialog.xaml uses
                FontSize = 11,
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0),
                Style = (Style)FindResourceStatic("SurfaceButton"),
            };
            closeBtn.Click += (_, _) => CloseFindBar();
            SetColumn(closeBtn, 2);
            row.Children.Add(closeBtn);

            bar.Child = row;
            return bar;
        }

        private Grid BuildSplit(out TreeView tree, out DataGrid grid)
        {
            var split = new Grid();
            // RegSplitMargin = the 8,0,8,6 this always was, 0 on 98SE: the wells then run flush
            // to the pane edges so the tree's left edge lines up under the menu bar's white left
            // line and the right edge is the same thin line as the left.
            split.SetResourceReference(FrameworkElement.MarginProperty, "RegSplitMargin");
            // A fixed default width, narrower than the folder browser's own 240 (TreePanel.cs
            // TreeWidthDefault) - registry key names run shorter than folder paths, so the tree
            // does not need as much room at rest, and the value grid's Data column (a REG_BINARY
            // hex string, a long REG_SZ path) is what actually needs the space. Same MinWidth as
            // the folder tree so it never collapses unreadably; still a normal splitter drag from
            // here, same as every other split in this app.
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180), MinWidth = 160, MaxWidth = 420 });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });

            tree = new TreeView
            {
                Style = (Style)FindResourceStatic("FolderTreeView"),
                Focusable = true,
            };
            // ToolTreeBrush: Transparent on the ordinary themes (ThemeManager), so the tree
            // shows this control's own PaneBrush root and grain and matches the value grid
            // beside it; 98SE states WHITE - the reg tree is a sunken white list well there,
            // like Explorer's. It used to mirror BackgroundBrush to copy the folder tree's
            // window-background look, but that brush is a gradient on five themes and re-ramped
            // inside this narrow column - the pink sidebar on Delirium.
            tree.SetResourceReference(TreeView.BackgroundProperty, "ToolTreeBrush");

            // The chevron's mask fill (Controls.xaml FolderTreeItem) exists to hide the
            // connecting line, so it has to be the surface the tree actually sits on. The app
            // default is SolidBackgroundBrush - right for the folder sidebar, which shows the
            // window - but this tree sits on the control's PaneBrush, so the default painted a
            // wedge of window color into every chevron. On the gradient themes that is the
            // ramp's first stop, which is nothing like the pane: the pink arrows.
            ApplyTreeChevronMask(tree);

            // Same visual chrome the folder sidebar uses (expander arrows, connecting lines), with
            // IsExpanded/IsSelected two-way bound to RegistryNode so the tree and the model agree
            // in both directions - the model drives Find/rename/delete's own expand-and-select,
            // and the user's own clicks drive the model back.
            var itemStyle = new Style(typeof(TreeViewItem), (Style)FindResourceStatic("FolderTreeItem"));
            itemStyle.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty,
                new Binding("IsExpanded") { Mode = BindingMode.TwoWay }));
            itemStyle.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty,
                new Binding("IsSelected") { Mode = BindingMode.TwoWay }));
            tree.ItemContainerStyle = itemStyle;

            var template = new HierarchicalDataTemplate(typeof(RegistryNode)) { ItemsSource = new Binding("Children") };
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            tb.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
            tb.SetValue(TextBlock.FontSizeProperty, 12.0);
            template.VisualTree = tb;
            tree.ItemTemplate = template;

            tree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(Tree_Expanded));
            tree.SelectedItemChanged += Tree_SelectedItemChanged;
            tree.ContextMenuOpening += Tree_ContextMenuOpening;
            tree.PreviewKeyDown += Tree_PreviewKeyDown;
            // Sunken well around the tree - see the note on the toolbar in the constructor.
            var treeHost = ToolTabChrome.WrapContent(tree, "ToolTreeBrush");
            SetColumn(treeHost, 0);
            split.Children.Add(treeHost);

            var splitter = new GridSplitter
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = Brushes.Transparent,
                FocusVisualStyle = null,   // same dotted-focus-rectangle fix as the pane splitters
            };
            // RegSplitterWidth: the 5 it always was, 3 on 98SE - the regedit divider reads
            // better skinnier there.
            splitter.SetResourceReference(FrameworkElement.WidthProperty, "RegSplitterWidth");
            SetColumn(splitter, 1);
            split.Children.Add(splitter);

            grid = BuildValueGrid();
            // Its own sunken WHITE well, separate from the tree's, with the skinny splitter
            // between them as the divider.
            var gridHost = ToolTabChrome.WrapContent(grid, "ToolContentBrush");
            SetColumn(gridHost, 2);
            split.Children.Add(gridHost);

            return split;
        }

        private DataGrid BuildValueGrid()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                CanUserSortColumns = true,
                CanUserResizeColumns = true,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                ColumnHeaderHeight = 26,
                RowHeight = 24,
                AlternationCount = 2,
                ItemsSource = _values,
                // Transparent, matching ProcessListControl's own value grid (Tools/ProcessListControl.cs
                // - the Task Manager tab) rather than the SurfaceBrush a
                // prior round put here: SurfaceBrush painted a second, flat panel behind the header's own
                // TableHeaderBrush band instead of letting this control's own PaneBrush root (set in
                // the constructor above) show through, so the header read as floating on an extra
                // layer instead of sitting directly on the tab.
                Background = Brushes.Transparent
            };
            // RegGridMargin: the 6,0,0,0 gap to the splitter it always had, 0 on 98SE - inside
            // its own sunken well the gap read as a slim white border to the left of the
            // table; the skinny splitter is the divider now.
            grid.SetResourceReference(FrameworkElement.MarginProperty, "RegGridMargin");
            grid.CanUserReorderColumns = false;   // keeps Name/Data as the fixed first/last columns the header rounding below assumes
            grid.SetResourceReference(DataGrid.ForegroundProperty, "TextBrush");
            grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "PaneBorderBrush");
            grid.RowStyle          = (Style)FindResourceStatic("DarkDataGridRow");
            grid.CellStyle         = (Style)FindResourceStatic("DarkDataGridCell");
            grid.ColumnHeaderStyle = (Style)FindResourceStatic("DarkDataGridColumnHeader");

            // Name and Type are short by nature (a value name, a "REG_*" label) so they get fixed
            // widths sized for typical content; Data - the column that actually needs the room for
            // a long REG_BINARY hex string or REG_SZ path - is the flexible Star column that
            // absorbs whatever width the narrower default tree (BuildSplit above) leaves it.
            var name = Col("Str_Col_RegName", nameof(RegistryValueRow.DisplayName), 140);
            var kind = Col("Str_Col_RegType", nameof(RegistryValueRow.KindLabel), 90);
            var data = Col("Str_Col_RegData", nameof(RegistryValueRow.DataLabel), new DataGridLength(1, DataGridLengthUnitType.Star));

            // Rounded top corners on the OUTER two headers only, so the row reads as one radius-5
            // band with rounded top corners - the
            // family standard documented against Killendar's MonthView/TimeGridView day-name strip
            // (CalendarChrome.cs / MonthView.xaml: TableHeaderBrush, CornerRadius 5,5,0,0,
            // HeaderLineBrush bottom border). A DataGrid draws one DataGridColumnHeader per
            // column rather than a single band Border, so the shared DarkDataGridColumnHeader
            // style (Controls.xaml, still flat/square and used as-is by Task Manager/Event Viewer -
            // untouched here) is only overridden on the leftmost and rightmost columns via their
            // own HeaderStyle; the middle Type column keeps the grid's plain ColumnHeaderStyle.
            var hr = KillerShell.Services.ThemeManager.Radius("BarCornerRadiusValue", 5);
            name.HeaderStyle = BuildValueHeaderStyle(new CornerRadius(hr, 0, 0, 0));
            data.HeaderStyle = BuildValueHeaderStyle(new CornerRadius(0, hr, 0, 0));

            // A long binary/multi-string value clips at the column edge just like the Processes/
            // Event Viewer grids do - the edit dialog (double-click / Enter / Modify...) is where
            // the full value actually lives, but the tooltip still carries it for a quick look.
            var dataStyle = new Style(typeof(TextBlock));
            dataStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(RegistryValueRow.DataLabel))));
            data.ElementStyle = dataStyle;

            grid.Columns.Add(name);
            grid.Columns.Add(kind);
            grid.Columns.Add(data);

            grid.ContextMenuOpening += Grid_ContextMenuOpening;
            grid.MouseDoubleClick += Grid_MouseDoubleClick;
            grid.PreviewKeyDown += Grid_PreviewKeyDown;
            return grid;
        }

        private static DataGridTextColumn Col(string headerKey, string bindingPath, double width)
            => Col(headerKey, bindingPath, new DataGridLength(width));

        private static DataGridTextColumn Col(string headerKey, string bindingPath, DataGridLength width)
        {
            var header = new TextBlock();
            header.SetResourceReference(TextBlock.TextProperty, headerKey);
            return new DataGridTextColumn
            {
                Header  = header,
                Binding = new Binding(bindingPath),
                Width   = width,
            };
        }

        /// <summary>Rebuilds the shared DarkDataGridColumnHeader template (Controls.xaml) with one
        /// difference: the header Border's CornerRadius, so the value grid's outer two columns can
        /// carry a rounded top corner without touching the shared style every other DataGrid in the
        /// app (Task Manager, Event Viewer) still uses unmodified. Colors, fonts, the sort glyph and
        /// the resize grippers are identical to that shared template.</summary>
        private static Style BuildValueHeaderStyle(CornerRadius corner)
        {
            var style = new Style(typeof(DataGridColumnHeader));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("TableHeaderBrush")));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("MutedTextBrush")));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 11.0));
            style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Normal));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("HeaderLineBrush")));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(Control.TemplateProperty, BuildValueHeaderTemplate(corner)));
            return style;
        }

        private static ControlTemplate BuildValueHeaderTemplate(CornerRadius corner)
        {
            var template = new ControlTemplate(typeof(DataGridColumnHeader));

            var outerGrid = new FrameworkElementFactory(typeof(Grid));

            var border = new FrameworkElementFactory(typeof(Border), "hdrBg");
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetValue(Border.SnapsToDevicePixelsProperty, true);
            border.SetValue(Border.CornerRadiusProperty, corner);

            var innerGrid = new FrameworkElementFactory(typeof(Grid));
            var col0 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col0.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            var col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col1.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            innerGrid.AppendChild(col0);
            innerGrid.AppendChild(col1);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(Grid.ColumnProperty, 0);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetBinding(ContentPresenter.HorizontalAlignmentProperty, new Binding("HorizontalContentAlignment") { RelativeSource = RelativeSource.TemplatedParent });
            content.SetBinding(ContentPresenter.SnapsToDevicePixelsProperty, new Binding("SnapsToDevicePixels") { RelativeSource = RelativeSource.TemplatedParent });

            var sortGlyph = new FrameworkElementFactory(typeof(TextBlock), "sortGlyph");
            sortGlyph.SetValue(Grid.ColumnProperty, 1);
            sortGlyph.SetValue(TextBlock.TextProperty, ((char)0x25B2).ToString());
            sortGlyph.SetValue(TextBlock.FontSizeProperty, 8.0);
            sortGlyph.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            sortGlyph.SetValue(TextBlock.MarginProperty, new Thickness(6, 1, 0, 0));
            sortGlyph.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("PrimaryBrush"));
            sortGlyph.SetValue(TextBlock.VisibilityProperty, Visibility.Collapsed);

            innerGrid.AppendChild(content);
            innerGrid.AppendChild(sortGlyph);
            border.AppendChild(innerGrid);

            var leftGripper = new FrameworkElementFactory(typeof(Thumb), "PART_LeftHeaderGripper");
            leftGripper.SetValue(Thumb.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            leftGripper.SetValue(Thumb.WidthProperty, 8.0);
            leftGripper.SetValue(Thumb.MarginProperty, new Thickness(-4, 0, 0, 0));
            leftGripper.SetValue(Thumb.CursorProperty, Cursors.SizeWE);
            leftGripper.SetValue(Thumb.TemplateProperty, GripperTemplate());

            var rightGripper = new FrameworkElementFactory(typeof(Thumb), "PART_RightHeaderGripper");
            rightGripper.SetValue(Thumb.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            rightGripper.SetValue(Thumb.WidthProperty, 8.0);
            rightGripper.SetValue(Thumb.MarginProperty, new Thickness(0, 0, -4, 0));
            rightGripper.SetValue(Thumb.CursorProperty, Cursors.SizeWE);
            rightGripper.SetValue(Thumb.TemplateProperty, GripperTemplate());

            outerGrid.AppendChild(border);
            outerGrid.AppendChild(leftGripper);
            outerGrid.AppendChild(rightGripper);
            template.VisualTree = outerGrid;

            var ascTrigger = new Trigger { Property = DataGridColumnHeader.SortDirectionProperty, Value = System.ComponentModel.ListSortDirection.Ascending };
            ascTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible) { TargetName = "sortGlyph" });
            ascTrigger.Setters.Add(new Setter(TextBlock.TextProperty, ((char)0x25B2).ToString()) { TargetName = "sortGlyph" });
            template.Triggers.Add(ascTrigger);

            var descTrigger = new Trigger { Property = DataGridColumnHeader.SortDirectionProperty, Value = System.ComponentModel.ListSortDirection.Descending };
            descTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible) { TargetName = "sortGlyph" });
            descTrigger.Setters.Add(new Setter(TextBlock.TextProperty, ((char)0x25BC).ToString()) { TargetName = "sortGlyph" });
            template.Triggers.Add(descTrigger);

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("RowHoverBrush")) { TargetName = "hdrBg" });
            hoverTrigger.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrush")));
            template.Triggers.Add(hoverTrigger);

            var pressedTrigger = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("RowSelectedBrush")) { TargetName = "hdrBg" });
            template.Triggers.Add(pressedTrigger);

            return template;
        }

        private static ControlTemplate GripperTemplate()
        {
            var template = new ControlTemplate(typeof(Thumb));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            template.VisualTree = border;
            return template;
        }

        private static TextBlock BuildStatusLine()
        {
            var tb = new TextBlock
            {
                Margin = new Thickness(8, 0, 8, 6),
                FontSize = 11,
                Visibility = Visibility.Collapsed,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            return tb;
        }

        // Resolved through the application's merged dictionaries rather than this.FindResource -
        // at construction time the control has not been added to a visual tree yet (same remark
        // as ProcessListControl.FindResourceStatic / EventViewerControl.FindResourceStatic).
        private static object FindResourceStatic(string key)
            => Application.Current.TryFindResource(key)
               ?? throw new InvalidOperationException($"Missing resource: {key}");

        // ═══════════════════════════════════════════════════════════
        //  ADDRESS BAR
        // ═══════════════════════════════════════════════════════════
        private void BeginEditPath()
        {
            _pathEdit.Text = _selectedNode?.FullPath ?? string.Empty;
            _pathDisplay.Visibility = Visibility.Collapsed;
            _pathEdit.Visibility = Visibility.Visible;
            _pathEdit.Focus();
            _pathEdit.SelectAll();
        }

        private void CancelEditPath()
        {
            _pathEdit.Visibility = Visibility.Collapsed;
            _pathDisplay.Visibility = Visibility.Visible;
        }

        private void PathEdit_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitPath(_pathEdit.Text.Trim());
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelEditPath();
            }
        }

        /// <summary>Navigates to a typed path. An invalid path shows a status message and stays in
        /// edit mode - the same "type a path, Enter to commit, invalid stays editable" shape the
        /// terminal bar's own location field and the document editor's path field already use.</summary>
        private void CommitPath(string path)
        {
            if (path.Length == 0) { CancelEditPath(); return; }

            var segments = path.Split('\\').Where(s => s.Length > 0).ToArray();
            if (segments.Length == 0) { ShowStatus(MainWindow.LocStatic("Str_RegEd_PathNotFound"), error: true); return; }

            var root = _roots.FirstOrDefault(r => string.Equals(r.Name, segments[0], StringComparison.OrdinalIgnoreCase));
            if (root == null) { ShowStatus(MainWindow.LocStatic("Str_RegEd_PathNotFound"), error: true); return; }

            var current = root;
            current.LoadChildren();

            for (int i = 1; i < segments.Length; i++)
            {
                var next = current.Children.FirstOrDefault(
                    c => string.Equals(c.Name, segments[i], StringComparison.OrdinalIgnoreCase));
                if (next == null) { ShowStatus(MainWindow.LocStatic("Str_RegEd_PathNotFound"), error: true); return; }
                current = next;
                current.LoadChildren();
            }

            SelectNode(current, expandAncestors: true);
            CancelEditPath();
        }

        private void UpdatePathDisplay(string fullPath) => _pathDisplay.Text = fullPath;

        // ═══════════════════════════════════════════════════════════
        //  TREE
        // ═══════════════════════════════════════════════════════════
        private void Tree_Expanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not TreeViewItem tvi) return;
            if (tvi.DataContext is not RegistryNode node) return;
            node.LoadChildren();
            if (node.LoadError != null) ShowStatus(node.LoadError, error: true);
        }

        private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is not RegistryNode node) return;
            _selectedNode = node;
            UpdatePathDisplay(node.FullPath);
            LoadValues(node);
        }

        private static RegistryNode? NodeUnder(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is TreeViewItem tvi) return tvi.DataContext as RegistryNode;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        /// <summary>Expands the chain up to (and including) <paramref name="node"/> and selects
        /// it, same shape as FolderTree.RevealInTree - only ever expands, never collapses
        /// anything the user had open.</summary>
        private void SelectNode(RegistryNode node, bool expandAncestors)
        {
            if (expandAncestors)
            {
                var chain = new List<RegistryNode>();
                for (var p = node.Parent; p != null; p = p.Parent) chain.Add(p);
                chain.Reverse();
                foreach (var a in chain) { a.LoadChildren(); a.IsExpanded = true; }
            }
            node.IsSelected = true;
        }

        // ═══════════════════════════════════════════════════════════
        //  VALUES
        // ═══════════════════════════════════════════════════════════
        private void LoadValues(RegistryNode node)
        {
            _values.Clear();

            if (MainWindow.DemoMode)
            {
                PopulateDemoValues(node);
                return;
            }

            try
            {
                using var key = RegistryPathHelper.OpenKey(node.FullPath, writable: false);
                if (key == null) return;

                var names = key.GetValueNames();
                var rows = new List<RegistryValueRow>();
                bool sawDefault = false;

                foreach (var n in names)
                {
                    if (n.Length == 0) sawDefault = true;
                    RegistryValueKind kind;
                    try { kind = key.GetValueKind(n); } catch { kind = RegistryValueKind.Unknown; }
                    object? val = null;
                    try { val = key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames); } catch { }
                    rows.Add(new RegistryValueRow(n, kind, val));
                }

                // The unnamed default value always gets a row, set or not - matching regedit's
                // own convention of always showing "(Default)" as the first row.
                if (!sawDefault) rows.Insert(0, new RegistryValueRow(string.Empty, RegistryValueKind.String, null));

                foreach (var r in rows.OrderBy(r => r.Name.Length != 0)   // default first
                                       .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                    _values.Add(r);
            }
            catch (SecurityException)      { ShowStatus(MainWindow.LocStatic("Str_RegEd_AccessDenied"), error: true); }
            catch (UnauthorizedAccessException) { ShowStatus(MainWindow.LocStatic("Str_RegEd_AccessDenied"), error: true); }
            catch (IOException) { /* the key vanished between selection and read - grid is just empty */ }
            catch (ArgumentException) { /* a value name in this key is malformed (same HKCR merged-view
                                           issue as LoadChildren above) - GetValueNames() throws for the
                                           whole key instead of skipping the bad entry; grid is just empty */ }
        }

        /// <summary>--demo's version of the block above - same default-first/alphabetical
        /// ordering, read from Services\DemoRegistry.cs instead of a real RegistryKey.</summary>
        private void PopulateDemoValues(RegistryNode node)
        {
            var rows = new List<RegistryValueRow>();
            bool sawDefault = false;

            foreach (var v in KillerShell.Services.DemoRegistry.ValuesOf(node.FullPath))
            {
                if (v.Name.Length == 0) sawDefault = true;
                rows.Add(new RegistryValueRow(v.Name, v.Kind, v.Value));
            }

            if (!sawDefault) rows.Insert(0, new RegistryValueRow(string.Empty, RegistryValueKind.String, null));

            foreach (var r in rows.OrderBy(r => r.Name.Length != 0)
                                   .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                _values.Add(r);
        }

        private void RefreshCurrent()
        {
            if (_selectedNode == null) { ShowStatus(MainWindow.LocStatic("Str_RegEd_NothingSelected"), error: false); return; }
            _selectedNode.Refresh();
            LoadValues(_selectedNode);
            ShowStatus(MainWindow.LocStatic("Str_RegEd_Refreshed"), error: false);
        }

        // ═══════════════════════════════════════════════════════════
        //  FIND (Ctrl+F) - walks the already-loaded portion of the tree only (key names, and the
        //  value names of each loaded key), never an unbounded walk of the whole registry. See the
        //  file header on RegistryEditorTabs.cs / the task's own scope note: real regedit's own
        //  search blocks the UI too, and a full walk of HKEY_LOCAL_MACHINE would be a genuinely
        //  slow, cancel-worthy operation this simpler version does not need.
        // ═══════════════════════════════════════════════════════════
        private void ToggleFindBar()
        {
            if (_findBar.Visibility == Visibility.Visible) { CloseFindBar(); return; }
            _findBar.Visibility = Visibility.Visible;
            _findBox.Focus();
            _findBox.SelectAll();
        }

        private void CloseFindBar()
        {
            _findBar.Visibility = Visibility.Collapsed;
            _tree.Focus();
        }

        private void FindNext()
        {
            string q = _findBox.Text;
            if (q.Length == 0) return;

            var flat = new List<(RegistryNode Node, string? ValueName)>();
            void Walk(RegistryNode n)
            {
                flat.Add((n, null));
                if (!n.IsLoaded) return;
                try
                {
                    using var key = RegistryPathHelper.OpenKey(n.FullPath, writable: false);
                    if (key != null)
                        foreach (var vn in key.GetValueNames())
                            flat.Add((n, vn));
                }
                catch { /* a key we cannot open cannot have its values searched - skip it */ }
                foreach (var c in n.Children) Walk(c);
            }
            foreach (var r in _roots) Walk(r);

            int start = 0;
            if (_lastFindNode != null)
            {
                int idx = flat.FindIndex(x => ReferenceEquals(x.Node, _lastFindNode) && x.ValueName == _lastFindValue);
                if (idx >= 0) start = idx + 1;
            }

            for (int pass = 0; pass < 2; pass++)   // second pass wraps to the top once
            {
                for (int i = start; i < flat.Count; i++)
                {
                    var (node, valueName) = flat[i];
                    bool nameMatch = valueName == null && node.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool valMatch  = valueName != null && valueName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!nameMatch && !valMatch) continue;

                    _lastFindNode = node;
                    _lastFindValue = valueName;
                    SelectNode(node, expandAncestors: true);

                    if (valueName != null)
                    {
                        var row = _values.FirstOrDefault(v => v.Name == valueName);
                        if (row != null) _grid.SelectedItem = row;
                    }
                    ShowStatus(string.Empty, error: false);
                    return;
                }
                start = 0;
            }

            ShowStatus(MainWindow.LocStatic("Str_RegEd_NoMatches"), error: false);
        }

        // ═══════════════════════════════════════════════════════════
        //  CREATE / RENAME / DELETE - keys
        // ═══════════════════════════════════════════════════════════
        private static string? ValidateName(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return MainWindow.LocStatic("Str_RegEd_NameEmpty");
            if (v.IndexOf('\\') >= 0) return MainWindow.LocStatic("Str_RegEd_NameHasBackslash");
            return null;
        }

        private void CreateNewKey(RegistryNode parentNode)
        {
            var dlg = new RegistryInputDialog(
                MainWindow.LocStatic("Str_Dlg_RegNewKeyMsg"),
                MainWindow.LocStatic("Str_RegEd_NewKeyDefaultName"),
                MainWindow.LocStatic("Str_Menu_RegNewKey"), ValidateName)
                { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            string name = dlg.Value;
            try
            {
                using var parent = RegistryPathHelper.OpenKey(parentNode.FullPath, writable: true);
                if (parent == null) { ShowStatus(MainWindow.LocStatic("Str_RegEd_AccessDenied"), error: true); return; }
                using var created = parent.CreateSubKey(name);
                if (created == null) { ShowStatus(MainWindow.LocStatic("Str_RegEd_AccessDenied"), error: true); return; }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
            {
                ShowStatus(string.Format(MainWindow.LocStatic("Str_RegEd_CreateFailed"), ex.Message), error: true);
                return;
            }

            parentNode.Refresh();
            parentNode.IsExpanded = true;
            var newNode = parentNode.Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (newNode != null) newNode.IsSelected = true;
        }

        private void RenameKeyNode(RegistryNode node)
        {
            if (node.IsRoot) return;   // a hive is not renamable

            var dlg = new RegistryInputDialog(
                string.Format(MainWindow.LocStatic("Str_Dlg_RegRenameKeyMsg"), node.Name),
                node.Name, MainWindow.LocStatic("Str_Menu_RegRename"), ValidateName)
                { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            string newName = dlg.Value;
            if (string.Equals(newName, node.Name, StringComparison.Ordinal)) return;

            string parentPath = RegistryPathHelper.ParentPath(node.FullPath);
            try
            {
                using var parent = RegistryPathHelper.OpenKey(parentPath, writable: true);
                if (parent == null) { ShowStatus(MainWindow.LocStatic("Str_RegEd_AccessDenied"), error: true); return; }
                if (parent.GetSubKeyNames().Any(n => string.Equals(n, newName, StringComparison.OrdinalIgnoreCase)))
                {
                    ShowStatus(MainWindow.LocStatic("Str_RegEd_NameExists"), error: true);
                    return;
                }

                // The registry API has no atomic rename for a key - copy the whole subtree to the
                // new name, then delete the old one. Correct even for a key with many nested
                // subkeys/values, just not the single fast call a real rename would be.
                using var src = parent.OpenSubKey(node.Name, writable: false);
                using var dst = parent.CreateSubKey(newName);
                if (src == null || dst == null) { ShowStatus(MainWindow.LocStatic("Str_RegEd_AccessDenied"), error: true); return; }
                CopyKeyContents(src, dst);
                parent.DeleteSubKeyTree(node.Name);
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
            {
                ShowStatus(string.Format(MainWindow.LocStatic("Str_RegEd_RenameFailed"), ex.Message), error: true);
                return;
            }

            var parentNode = node.Parent;
            parentNode?.Refresh();
            var newNode = parentNode?.Children.FirstOrDefault(c => string.Equals(c.Name, newName, StringComparison.OrdinalIgnoreCase));
            if (newNode != null) newNode.IsSelected = true;
        }

        private static void CopyKeyContents(RegistryKey src, RegistryKey dst)
        {
            foreach (var vn in src.GetValueNames())
            {
                var kind = src.GetValueKind(vn);
                var val = src.GetValue(vn, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (val != null) dst.SetValue(vn, val, kind);
            }
            foreach (var sk in src.GetSubKeyNames())
            {
                using var srcChild = src.OpenSubKey(sk, writable: false);
                using var dstChild = dst.CreateSubKey(sk);
                if (srcChild != null && dstChild != null) CopyKeyContents(srcChild, dstChild);
            }
        }

        /// <summary>
        /// Deleting a KEY takes the whole subtree beneath it with it - the confirm dialog says so
        /// explicitly, in the same "no stock Win32 message box, wording matches the stakes" shape
        /// ProcessListControl.KillWithConfirm already established for this app, but written more
        /// seriously: this one really can break a program, or the machine, if the wrong key goes.
        /// </summary>
        private void DeleteKeyWithConfirm(RegistryNode node)
        {
            if (node.IsRoot) return;   // a hive cannot be deleted

            string msg = string.Format(MainWindow.LocStatic("Str_Dlg_RegDeleteKeyMsg"), node.Name);
            var dlg = new ConfirmDialog(msg, node.FullPath, MainWindow.LocStatic("Str_Menu_RegDelete"))
                { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            try
            {
                string parentPath = RegistryPathHelper.ParentPath(node.FullPath);
                using var parent = RegistryPathHelper.OpenKey(parentPath, writable: true);
                if (parent == null) { ShowStatus(MainWindow.LocStatic("Str_RegEd_AccessDenied"), error: true); return; }
                parent.DeleteSubKeyTree(node.Name);
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
            {
                ShowStatus(string.Format(MainWindow.LocStatic("Str_RegEd_DeleteFailed"), ex.Message), error: true);
                return;
            }

            var parentNode = node.Parent;
            parentNode?.Children.Remove(node);
            if (parentNode != null) { parentNode.IsSelected = true; _selectedNode = parentNode; }
            ShowStatus(string.Format(MainWindow.LocStatic("Str_RegEd_KeyDeleted"), node.Name), error: false);
        }

        // ═══════════════════════════════════════════════════════════
        //  CREATE / RENAME / DELETE / MODIFY - values
        // ═══════════════════════════════════════════════════════════
        private string NextAvailableValueName(RegistryNode node)
        {
            string baseName = MainWindow.LocStatic("Str_RegEd_NewValueDefaultName");
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var key = RegistryPathHelper.OpenKey(node.FullPath, writable: false);
                if (key != null) foreach (var n in key.GetValueNames()) existing.Add(n);
            }
            catch { /* best-effort suggestion only - the create call re-validates for real */ }

            if (!existing.Contains(baseName)) return baseName;
            for (int i = 1; ; i++)
            {
                string candidate = baseName + " #" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!existing.Contains(candidate)) return candidate;
            }
        }

        private void CreateNewValue(RegistryNode node, RegistryValueKind kind)
        {
            var dlg = new RegistryInputDialog(
                MainWindow.LocStatic("Str_Dlg_RegNewValueMsg"),
                NextAvailableValueName(node),
                MainWindow.LocStatic("Str_Menu_RegModify"), ValidateName)
                { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            string name = dlg.Value;
            object defaultData = kind switch
            {
                RegistryValueKind.String or RegistryValueKind.ExpandString => string.Empty,
                RegistryValueKind.MultiString => Array.Empty<string>(),
                RegistryValueKind.Binary      => Array.Empty<byte>(),
                RegistryValueKind.QWord       => 0L,
                _                              => 0,   // DWord
            };

            try
            {
                using var key = RegistryPathHelper.OpenKey(node.FullPath, writable: true);
                if (key == null) { ShowStatus(MainWindow.LocStatic("Str_RegEd_AccessDenied"), error: true); return; }
                if (key.GetValueNames().Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                {
                    ShowStatus(MainWindow.LocStatic("Str_RegEd_NameExists"), error: true);
                    return;
                }
                key.SetValue(name, defaultData, kind);
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
            {
                ShowStatus(string.Format(MainWindow.LocStatic("Str_RegEd_CreateFailed"), ex.Message), error: true);
                return;
            }

            LoadValues(node);
            var row = _values.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (row == null) return;
            _grid.SelectedItem = row;

            // Straight into the edit dialog so setting the new value's real data is one flow, not
            // two - still an explicit Save click in that dialog, never an auto-write of the blank
            // default (RegistryValueEditDialog.xaml.cs).
            ModifyValueRow(node, row);
        }

        private void ModifyValueRow(RegistryNode node, RegistryValueRow row)
        {
            var kind = row.Kind is RegistryValueKind.Unknown or RegistryValueKind.None
                ? RegistryValueKind.String : row.Kind;
            var dlg = new RegistryValueEditDialog(row.DisplayName, kind, row.RawValue)
                { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            try
            {
                using var key = RegistryPathHelper.OpenKey(node.FullPath, writable: true);
                if (key == null) { ShowStatus(MainWindow.LocStatic("Str_RegEd_AccessDenied"), error: true); return; }
                key.SetValue(row.Name, dlg.ResultValue!, kind);
                ShowStatus(string.Format(MainWindow.LocStatic("Str_RegEd_ValueSaved"), row.DisplayName), error: false);
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
            {
                ShowStatus(string.Format(MainWindow.LocStatic("Str_RegEd_SaveFailed"), ex.Message), error: true);
                return;
            }

            LoadValues(node);
        }

        private void RenameValueRow(RegistryNode node, RegistryValueRow row)
        {
            if (row.Name.Length == 0) return;   // the unnamed default value cannot be renamed

            var dlg = new RegistryInputDialog(
                string.Format(MainWindow.LocStatic("Str_Dlg_RegRenameValueMsg"), row.Name),
                row.Name, MainWindow.LocStatic("Str_Menu_RegRename"), ValidateName)
                { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            string newName = dlg.Value;
            if (string.Equals(newName, row.Name, StringComparison.Ordinal)) return;

            try
            {
                using var key = RegistryPathHelper.OpenKey(node.FullPath, writable: true);
                if (key == null) { ShowStatus(MainWindow.LocStatic("Str_RegEd_AccessDenied"), error: true); return; }
                if (key.GetValueNames().Any(n => string.Equals(n, newName, StringComparison.OrdinalIgnoreCase)))
                {
                    ShowStatus(MainWindow.LocStatic("Str_RegEd_NameExists"), error: true);
                    return;
                }
                var kind = key.GetValueKind(row.Name);
                var val = key.GetValue(row.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                key.SetValue(newName, val!, kind);
                key.DeleteValue(row.Name);
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
            {
                ShowStatus(string.Format(MainWindow.LocStatic("Str_RegEd_RenameFailed"), ex.Message), error: true);
                return;
            }

            LoadValues(node);
        }

        private void DeleteValueWithConfirm(RegistryNode node, RegistryValueRow row)
        {
            string msg = string.Format(MainWindow.LocStatic("Str_Dlg_RegDeleteValueMsg"), row.DisplayName);
            var dlg = new ConfirmDialog(msg, node.FullPath, MainWindow.LocStatic("Str_Menu_RegDelete"))
                { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            try
            {
                using var key = RegistryPathHelper.OpenKey(node.FullPath, writable: true);
                if (key == null) { ShowStatus(MainWindow.LocStatic("Str_RegEd_AccessDenied"), error: true); return; }
                key.DeleteValue(row.Name, throwOnMissingValue: false);
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
            {
                ShowStatus(string.Format(MainWindow.LocStatic("Str_RegEd_DeleteFailed"), ex.Message), error: true);
                return;
            }

            LoadValues(node);
            ShowStatus(string.Format(MainWindow.LocStatic("Str_RegEd_ValueDeleted"), row.DisplayName), error: false);
        }

        // ═══════════════════════════════════════════════════════════
        //  CONTEXT MENUS
        // ═══════════════════════════════════════════════════════════
        private static MenuItem AddMenuItem(ContextMenu menu, string headerKey, int glyph,
                                            RoutedEventHandler click, bool enabled = true, string gesture = "")
        {
            var item = new MenuItem { IsEnabled = enabled, InputGestureText = gesture };
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, headerKey);
            var icon = new TextBlock { Text = ((char)glyph).ToString() };
            icon.SetResourceReference(FrameworkElement.StyleProperty, "MenuGlyph");
            var iconBox = new Viewbox { Width = 14, Height = 14, Stretch = Stretch.Uniform, Child = icon };
            item.Icon = iconBox;
            item.Click += click;
            menu.Items.Add(item);
            return item;
        }

        private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var node = NodeUnder(Mouse.DirectlyOver as DependencyObject) ?? _selectedNode;
            if (node == null) { e.Handled = true; return; }
            if (!node.IsSelected) node.IsSelected = true;

            var menu = new ContextMenu { PlacementTarget = _tree };

            var newMenu = new MenuItem();
            newMenu.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Menu_RegNew");
            var newIcon = new TextBlock { Text = ((char)0xE710).ToString() };
            newIcon.SetResourceReference(FrameworkElement.StyleProperty, "MenuGlyph");
            newMenu.Icon = new Viewbox { Width = 14, Height = 14, Stretch = Stretch.Uniform, Child = newIcon };

            void NewItem(string headerKey, RoutedEventHandler click)
            {
                var mi = new MenuItem();
                mi.SetResourceReference(HeaderedItemsControl.HeaderProperty, headerKey);
                mi.Click += click;
                newMenu.Items.Add(mi);
            }
            NewItem("Str_Menu_RegNewKey",          (_, _) => CreateNewKey(node));
            newMenu.Items.Add(new Separator());
            NewItem("Str_Menu_RegNewString",       (_, _) => CreateNewValue(node, RegistryValueKind.String));
            NewItem("Str_Menu_RegNewExpandString", (_, _) => CreateNewValue(node, RegistryValueKind.ExpandString));
            NewItem("Str_Menu_RegNewMultiString",  (_, _) => CreateNewValue(node, RegistryValueKind.MultiString));
            NewItem("Str_Menu_RegNewBinary",       (_, _) => CreateNewValue(node, RegistryValueKind.Binary));
            NewItem("Str_Menu_RegNewDWord",        (_, _) => CreateNewValue(node, RegistryValueKind.DWord));
            NewItem("Str_Menu_RegNewQWord",        (_, _) => CreateNewValue(node, RegistryValueKind.QWord));
            menu.Items.Add(newMenu);

            menu.Items.Add(new Separator());

            // E8AC (Segoe MDL2 "Rename"), E74D (Segoe MDL2 "Delete"), E8C8 (the same "Copy" glyph
            // TerminalMenu.cs/EditorMenu.cs already use elsewhere in this app).
            AddMenuItem(menu, "Str_Menu_RegRename", 0xE8AC, (_, _) => RenameKeyNode(node), enabled: !node.IsRoot, gesture: "F2");
            AddMenuItem(menu, "Str_Menu_RegDelete", 0xE74D, (_, _) => DeleteKeyWithConfirm(node), enabled: !node.IsRoot, gesture: "Del");
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "Str_Menu_RegCopyName", 0xE8C8, (_, _) => CopyToClipboard(node.Name, "Str_RegEd_NameCopied"));
            AddMenuItem(menu, "Str_Menu_RegCopyPath", 0xE8C8, (_, _) => CopyToClipboard(node.FullPath, "Str_RegEd_PathCopied"), gesture: "Ctrl+C");

            menu.IsOpen = true;
            e.Handled = true;
        }

        private void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (_selectedNode == null || _grid.SelectedItem is not RegistryValueRow row) { e.Handled = true; return; }

            var menu = new ContextMenu { PlacementTarget = _grid };
            var node = _selectedNode;

            AddMenuItem(menu, "Str_Menu_RegModify", 0xE70F, (_, _) => ModifyValueRow(node, row), gesture: "Enter");
            AddMenuItem(menu, "Str_Menu_RegRename", 0xE8AC, (_, _) => RenameValueRow(node, row),
                enabled: row.Name.Length != 0, gesture: "F2");
            AddMenuItem(menu, "Str_Menu_RegDelete", 0xE74D, (_, _) => DeleteValueWithConfirm(node, row), gesture: "Del");
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "Str_Menu_RegCopyName", 0xE8C8,
                (_, _) => CopyToClipboard(row.DisplayName, "Str_RegEd_NameCopied"), gesture: "Ctrl+C");

            menu.IsOpen = true;
            e.Handled = true;
        }

        private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_selectedNode == null || _grid.SelectedItem is not RegistryValueRow row) return;
            ModifyValueRow(_selectedNode, row);
        }

        // ═══════════════════════════════════════════════════════════
        //  KEYBOARD - control-wide (Ctrl+F / F5), then tree-local and grid-local
        // ═══════════════════════════════════════════════════════════
        private void Control_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

            if (ctrl && e.Key == Key.F) { ToggleFindBar(); e.Handled = true; return; }
            if (e.Key == Key.F5) { RefreshCurrent(); e.Handled = true; return; }
            if (e.Key == Key.Escape && _findBar.Visibility == Visibility.Visible)
            {
                CloseFindBar();
                e.Handled = true;
            }
        }

        private void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_selectedNode == null) return;
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

            if (e.Key == Key.F2 && !_selectedNode.IsRoot) { RenameKeyNode(_selectedNode); e.Handled = true; }
            else if (e.Key == Key.Delete && !_selectedNode.IsRoot) { DeleteKeyWithConfirm(_selectedNode); e.Handled = true; }
            else if (ctrl && e.Key == Key.C) { CopyToClipboard(_selectedNode.FullPath, "Str_RegEd_PathCopied"); e.Handled = true; }
        }

        private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_selectedNode == null || _grid.SelectedItem is not RegistryValueRow row) return;
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

            if (e.Key == Key.Enter) { ModifyValueRow(_selectedNode, row); e.Handled = true; }
            else if (e.Key == Key.F2 && row.Name.Length != 0) { RenameValueRow(_selectedNode, row); e.Handled = true; }
            else if (e.Key == Key.Delete) { DeleteValueWithConfirm(_selectedNode, row); e.Handled = true; }
            else if (ctrl && e.Key == Key.C) { CopyToClipboard(row.DisplayName, "Str_RegEd_NameCopied"); e.Handled = true; }
        }

        // ═══════════════════════════════════════════════════════════
        //  STATUS LINE / CLIPBOARD
        // ═══════════════════════════════════════════════════════════
        private void ShowStatus(string text, bool error, bool sticky = false)
        {
            _statusLine.Text = text;
            _statusLine.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _statusLine.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerRed" : "MutedTextBrush");

            _statusClearTimer.Stop();
            if (text.Length > 0 && !error && !sticky) _statusClearTimer.Start();
        }

        private void CopyToClipboard(string text, string statusKey)
        {
            try
            {
                Clipboard.SetText(text.Length == 0 ? " " : text);
                ShowStatus(MainWindow.LocStatic(statusKey), error: false);
            }
            catch (Exception ex)
            {
                ShowStatus(string.Format(MainWindow.LocStatic("Str_RegEd_CopyFailed"), ex.Message), error: true);
            }
        }
    }
}
