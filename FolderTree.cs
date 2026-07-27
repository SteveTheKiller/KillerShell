using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerShell
{
    // ═══════════════════════════════════════════════════════════
    //  FOLDER TREE  -  the left sidebar, rooted at This PC
    // ═══════════════════════════════════════════════════════════
    // One node per folder, children loaded only when a node is actually expanded. A tree that
    // eagerly walked the disk would hang on the first drive with a deep tree on it, and users
    // point this at network shares.
    //
    // The lazy load is the standard placeholder trick: every node that might have children gets
    // a single dummy child so WPF draws an expander arrow, and the real children replace it on
    // first expand. "Might have children" is deliberately optimistic - proving a folder is empty
    // costs an enumeration, which is exactly the work being deferred, so an occasional arrow
    // that opens onto nothing is the right trade.
    public sealed class FolderNode : INotifyPropertyChanged
    {
        private static readonly FolderNode Placeholder = new FolderNode("", "", false);

        public string Path { get; }
        public string Name { get; }

        // Drives get their own treatment: they are always expandable, they never disappear
        // mid-session, and their label is "Local Disk (C:)" rather than a bare folder name.
        public bool IsDrive { get; }

        public ObservableCollection<FolderNode> Children { get; } = new ObservableCollection<FolderNode>();

        public FolderNode(string path, string name, bool mayHaveChildren)
        {
            Path = path;
            Name = name;
            if (mayHaveChildren) Children.Add(Placeholder);
        }

        public FolderNode(DriveInfo d)
        {
            Path    = d.RootDirectory.FullName;
            IsDrive = true;
            Name    = DriveLabel(d);
            Children.Add(Placeholder);
        }

        /// <summary>
        /// "Local Disk (C:)" style label, or the bare letter when the volume cannot be read.
        /// Internal because the This PC listing (Browse.cs) shows the same drives and must
        /// name them the same way the tree does.
        /// </summary>
        internal static string DriveLabel(DriveInfo d)
        {
            string letter = d.Name.TrimEnd('\\');
            try
            {
                // VolumeLabel throws on a drive that is not ready (empty optical, disconnected
                // share), which is exactly when we still want to show the letter.
                if (d.IsReady && !string.IsNullOrWhiteSpace(d.VolumeLabel))
                    return d.VolumeLabel + " (" + letter + ")";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return letter;
        }

        public bool IsLoaded { get; private set; }

        public ImageSource? Icon => Services.IconCache.For(Path, 16, isDirectory: true);

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

        /// <summary>
        /// Replaces the placeholder with the real subfolders. Enumeration happens off the UI
        /// thread - a slow or disconnected network drive would otherwise freeze the window for
        /// as long as the SMB timeout takes.
        /// </summary>
        public async Task LoadChildrenAsync()
        {
            if (IsLoaded) return;
            IsLoaded = true;

            string path = Path;
            List<FolderNode> kids = await Task.Run(() => EnumerateChildren(path)).ConfigureAwait(true);

            Children.Clear();
            foreach (var k in kids) Children.Add(k);
        }

        /// <summary>
        /// Re-enumerates this node's children in place, keeping whatever the user had open.
        /// </summary>
        /// <remarks>
        /// Used when the show-hidden filter changes, which invalidates every cached listing.
        /// Rebuilding the tree from its drive roots was the obvious way to do that and the wrong
        /// one: it threw away expansion state, so the whole sidebar visibly collapsed and
        /// reflowed on a toggle that has nothing to do with what is open.
        ///
        /// Nodes that were never opened are skipped - there is nothing cached to correct, and
        /// touching them would enumerate the disk for no reason.
        /// </remarks>
        internal async Task RefreshAsync()
        {
            if (!IsLoaded) return;

            string path = Path;
            var fresh = await Task.Run(() => EnumerateChildren(path)).ConfigureAwait(true);

            // Reconciled IN PLACE rather than cleared and refilled. Clear() removes the container
            // holding the tree's selection, and WPF answers a lost selection by selecting the
            // PARENT node - so any file op that refreshed the tree raised SelectedItemChanged for
            // the folder above and navigated the pane up one. Deleting a file in Documents landed
            // you in your home folder. Removing only what actually left the disk keeps the
            // selected node and its container exactly where they were.
            var byName = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in fresh) byName[n.Name] = n;

            for (int i = Children.Count - 1; i >= 0; i--)
                if (!byName.ContainsKey(Children[i].Name)) Children.RemoveAt(i);

            var have = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Children) have[c.Name] = c;

            for (int i = 0; i < fresh.Count; i++)
            {
                if (have.TryGetValue(fresh[i].Name, out var existing))
                {
                    // Anything the user has opened stays the SAME node object, so its own subtree
                    // and IsExpanded survive; only genuinely new entries get fresh nodes.
                    int at = Children.IndexOf(existing);
                    if (at != i) Children.Move(at, i);
                    await existing.RefreshAsync();   // its children are stale for the same reason
                }
                else Children.Insert(i, fresh[i]);
            }
        }

        private static List<FolderNode> EnumerateChildren(string path)
        {
            var list = new List<FolderNode>();

            // Demo mode reads the fabricated machine instead of the disk (DemoFileSystem.cs).
            // This is the ONLY place children are produced, for every node at every depth, so
            // one branch here is the whole tree - and it is where the hidden-folder toggle is
            // already reached out of, so reaching MainWindow again is nothing new. Folders only,
            // the same as the real branch below: the tree has never shown files. The sort is
            // repeated rather than shared so the disk path underneath is left exactly as it was.
            if (MainWindow.DemoMode)
            {
                foreach (var e in DemoFs.Children(path))
                    if (e.IsDir)
                        list.Add(new FolderNode(System.IO.Path.Combine(path, e.Name), e.Name,
                                                mayHaveChildren: true));

                list.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase));
                return list;
            }

            try
            {
                foreach (var d in new DirectoryInfo(path).EnumerateDirectories())
                {
                    // Hidden and system folders follow the same toggle the results list uses
                    // (ViewOptions.cs), so the two never disagree about what exists. System is
                    // grouped with hidden here rather than given its own switch: Explorer's
                    // separate "protected operating system files" option guards a handful of
                    // roots that nobody browses to on purpose.
                    var a = d.Attributes;
                    if (!MainWindow.ShowHidden &&
                        ((a & FileAttributes.Hidden) != 0 || (a & FileAttributes.System) != 0)) continue;

                    list.Add(new FolderNode(d.FullName, d.Name, mayHaveChildren: true));
                }
            }
            catch (UnauthorizedAccessException) { /* show what we can see */ }
            catch (IOException) { }

            list.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public partial class MainWindow
    {
        private readonly ObservableCollection<FolderNode> _treeRoots = new ObservableCollection<FolderNode>();

        // Guards the two-way sync between tree and results pane. Selecting a node navigates, and
        // navigating selects a node; without this they ping-pong.
        private bool _treeSyncing;

        private void InitFolderTree()
        {
            FolderTree.ItemsSource = _treeRoots;
            LoadDriveRoots();
        }

        // Ready drives only. An empty optical drive or a dropped mapping would otherwise sit
        // there as a node that throws the moment anyone touches it.
        private void LoadDriveRoots()
        {
            _treeRoots.Clear();

            // Demo mode roots the tree at the fabricated machine (DemoFileSystem.cs) so a capture
            // never shows the real volumes, and so the tree, the browse listings and the search
            // results all describe one place. Branched HERE rather than refilled after the fact:
            // RefreshTreeAsync and RevealInTree both walk this same collection, and a second
            // filling would leave them working from whichever version won.
            if (DemoMode)
            {
                foreach (var root in DemoFs.Drives)
                    _treeRoots.Add(new FolderNode(root, DemoFs.DriveLabel(root), mayHaveChildren: true));
                return;
            }

            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (IOException) { return; }

            foreach (var d in drives)
            {
                bool ready;
                try { ready = d.IsReady; }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (ready) _treeRoots.Add(new FolderNode(d));
            }
        }

        /// <summary>Re-enumerates every already-loaded node, keeping expansion state.</summary>
        internal async Task RefreshTreeAsync()
        {
            foreach (var r in _treeRoots.ToList()) await r.RefreshAsync();
        }

        // ── Context menu ─────────────────────────────────────────
        // Hung off the TreeView itself rather than the item template, for the same reason the
        // results menu is: one ContextMenu instance assigned to every realized container has a
        // single owner, and virtualization unparents it. The node is resolved from what is under
        // the pointer as the menu opens.
        private FolderNode? _treeMenuNode;

        private void FolderTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            _treeMenuNode = NodeUnder(Mouse.DirectlyOver as DependencyObject)
                         ?? NodeUnder(e.OriginalSource as DependencyObject);

            // Right-clicking a node selects it first, so the menu and the tree agree about what
            // is being acted on. Guarded, or this would navigate on every right-click.
            if (_treeMenuNode != null && !_treeMenuNode.IsSelected)
            {
                _treeSyncing = true;
                _treeMenuNode.IsSelected = true;
                _treeSyncing = false;
            }

            if (_treeMenuNode == null) { e.Handled = true; return; }   // empty space: no menu

            if (_treeMenuItem == null)
                _treeMenuItem = FolderTree.ContextMenu?.Items.OfType<MenuItem>()
                                          .FirstOrDefault(m => (m.Tag as string) == "fav");
            if (_treeMenuItem != null)
                _treeMenuItem.Header = Loc(IsBookmarked(_treeMenuNode.Path)
                    ? "Str_Menu_RemoveFavorite"
                    : "Str_Menu_AddFavorite");
        }

        private MenuItem? _treeMenuItem;

        private static FolderNode? NodeUnder(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is TreeViewItem tvi) return tvi.DataContext as FolderNode;
                d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                    : System.Windows.LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        private string? TreeMenuPath() => _treeMenuNode?.Path;

        private void TreeOpenNewTab_Click(object sender, RoutedEventArgs e)
        {
            string? p = TreeMenuPath();
            if (p == null) return;
            CaptureTab(_active);              // Tabs.cs
            ActivateTab(CreateTab());
            _ = NavigateTo(p);
        }

        private void TreeShowInExplorer_Click(object sender, RoutedEventArgs e)
        {
            string? p = TreeMenuPath();
            if (p != null && Directory.Exists(p))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{p.TrimEnd('\\')}\"");
        }

        private void TreeSearchHere_Click(object sender, RoutedEventArgs e)
        {
            string? p = TreeMenuPath();
            if (p == null) return;
            Pane.RootPathBox.Text    = p;
            Pane.ScopePathLabel.Text = p;
            _active.RootPath    = p;
            if (!_searchOpen) ToggleSearchPanel();   // SearchPanel.cs - nothing to type into otherwise
        }

        private void TreeFavorite_Click(object sender, RoutedEventArgs e)
        {
            string? p = TreeMenuPath();
            if (p == null) return;
            if (IsBookmarked(p)) RemoveBookmark(p); else AddBookmark(p);   // Bookmarks.cs
        }

        private void TreeCopyPath_Click(object sender, RoutedEventArgs e)
        {
            string? p = TreeMenuPath();
            if (p != null) PutOnClipboard(p, "Str_Status_Copied", "1");   // ResultsMenu.cs
        }

        private void TreeProperties_Click(object sender, RoutedEventArgs e)
        {
            string? p = TreeMenuPath();
            if (p == null || !Directory.Exists(p)) return;
            AfterMenuCloses(() =>                                   // ResultsMenu.cs
            {
                if (!Services.ShellContextMenu.ShowProperties(p))
                    SetTabStatusKey(_active, "Str_Status_ShellFailed");
            });
        }

        private void TreeShellMenu_Click(object sender, RoutedEventArgs e)
        {
            string? p = TreeMenuPath();
            if (p == null || !Directory.Exists(p)) return;
            AfterMenuCloses(() =>
            {
                if (!Services.ShellContextMenu.Show(this, new[] { p }))
                    SetTabStatusKey(_active, "Str_Status_ShellFailed");
            });
        }

        // TreeViewItem.Expanded is attached at the TreeView, so this fires for every node at any
        // depth - which is the point: one handler drives the whole lazy load.
        private async void FolderTree_Expanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not TreeViewItem tvi) return;
            if (tvi.DataContext is not FolderNode node) return;
            await node.LoadChildrenAsync();
            SaveTreeExpansion();
        }

        // Only here to record the change. Nothing is unloaded on collapse: the children stay
        // cached so reopening the same branch is instant, which is also why RefreshAsync has
        // something to re-enumerate.
        private void FolderTree_Collapsed(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not TreeViewItem) return;
            SaveTreeExpansion();
        }

        // ── Expansion memory ─────────────────────────────────────
        // Which branches were open, so the tree comes back the way it was left instead of as a
        // row of bare drive letters. The alternative - reopening only the active tab's folder -
        // was rejected because the tree is not a breadcrumb: the branches you keep open are the
        // three or four places you actually work, and most of them are not where you are now.

        private const string TreeExpandedKey = "TreeExpanded";

        // A ceiling rather than a real limit. Nobody keeps two hundred branches open on purpose,
        // but a registry value has to stay a registry value.
        private const int TreeExpandedMax = 200;

        // Set while the saved branches are being reopened. Each of those reopens raises Expanded,
        // and saving from inside the restore would write the half-finished list back out.
        private bool _treeRestoring;

        /// <summary>Record the open branches, deepest-visible-first order preserved.</summary>
        /// <remarks>
        /// Recursion stops at a closed node. A child of a collapsed parent can still carry
        /// IsExpanded - WPF keeps the flag so reopening the parent shows the branch as it was -
        /// but it is not open on screen, and restoring it would mean force-opening the parent
        /// the user deliberately shut.
        /// </remarks>
        private void SaveTreeExpansion()
        {
            if (_treeRestoring || DemoMode) return;

            var open = new List<string>();

            void Walk(FolderNode n)
            {
                if (!n.IsExpanded || open.Count >= TreeExpandedMax) return;
                if (!string.IsNullOrEmpty(n.Path)) open.Add(n.Path);
                foreach (var c in n.Children) Walk(c);
            }

            foreach (var r in _treeRoots) Walk(r);

            Services.ThemeManager.SetSetting(TreeExpandedKey, string.Join("|", open));
        }

        /// <summary>Reopen the branches that were open when the window last closed.</summary>
        /// <remarks>
        /// Shortest path first, so a parent is loaded before the child that has to be matched
        /// against its listing. Anything that has since been renamed, deleted, unmounted or
        /// hidden simply stops that one chain - a stale entry is not an error, and the rest of
        /// the tree still comes back.
        /// </remarks>
        internal async Task RestoreTreeExpansionAsync()
        {
            if (DemoMode) return;

            string saved = Services.ThemeManager.GetSetting(TreeExpandedKey) ?? string.Empty;
            if (saved.Length == 0) return;

            _treeRestoring = true;
            try
            {
                foreach (string p in saved.Split('|')
                                          .Where(s => s.Length > 0)
                                          .OrderBy(s => s.Length))
                    await ExpandTreePath(p);
            }
            finally { _treeRestoring = false; }
        }

        /// <summary>
        /// Expand the chain down to <paramref name="folder"/> INCLUDING the folder itself.
        /// </summary>
        /// <remarks>
        /// The one difference from RevealInTree, which deliberately leaves the destination's own
        /// expander alone because the user is navigating INTO it. Here the destination is a
        /// branch that was open, so it has to end up open.
        /// </remarks>
        private async Task ExpandTreePath(string folder)
        {
            string full;
            try { full = System.IO.Path.GetFullPath(folder); }
            catch { return; }

            var root = _treeRoots.FirstOrDefault(
                r => full.StartsWith(r.Path, StringComparison.OrdinalIgnoreCase));
            if (root == null) return;   // drive gone since last run

            var current = root;
            await current.LoadChildrenAsync();
            current.IsExpanded = true;

            foreach (string seg in RelativeSegments(root.Path, full))
            {
                var next = current.Children.FirstOrDefault(
                    c => string.Equals(c.Name, seg, StringComparison.OrdinalIgnoreCase));
                if (next == null) return;   // renamed, deleted, or hidden by the current filter

                current = next;
                await current.LoadChildrenAsync();
                current.IsExpanded = true;
            }
        }

        private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_treeSyncing) return;
            if (e.NewValue is not FolderNode node) return;
            if (string.IsNullOrEmpty(node.Path)) return;   // the placeholder, mid-load

            _ = NavigateTo(node.Path);   // Browse.cs
        }

        /// <summary>
        /// Points the tree at a folder that was reached from somewhere else - a double-click, the
        /// history buttons, the location box. Expands the chain down to it and selects it.
        /// </summary>
        /// <remarks>
        /// Only ever expands. Collapsing anything the user opened on their way somewhere else
        /// would make the tree feel like it was fighting them.
        /// </remarks>
        internal async Task RevealInTree(string folder)
        {
            if (!_treeOpen || string.IsNullOrEmpty(folder)) return;

            string full;
            try { full = System.IO.Path.GetFullPath(folder); }
            catch { return; }

            // Root first, then each ancestor in turn. Every step has to await its own load or
            // the next segment has nothing to match against.
            var root = _treeRoots.FirstOrDefault(
                r => full.StartsWith(r.Path, StringComparison.OrdinalIgnoreCase));
            if (root == null) return;

            var segments = RelativeSegments(root.Path, full).ToList();

            var current = root;
            if (segments.Count > 0)
            {
                await current.LoadChildrenAsync();
                current.IsExpanded = true;
            }

            for (int i = 0; i < segments.Count; i++)
            {
                var next = current.Children.FirstOrDefault(
                    c => string.Equals(c.Name, segments[i], StringComparison.OrdinalIgnoreCase));
                if (next == null) return;   // hidden, or gone since the listing

                current = next;

                // The DESTINATION's own expander is left exactly as the user had it. Only the
                // ancestors are opened, and only because the chain has to be walked to reach it.
                //
                // This used to expand every node including the leaf and then force the leaf shut
                // again. Going UP made that visibly wrong: the folder you are moving INTO is the
                // one already expanded showing where you came from, so slamming it closed
                // collapsed the branch under the cursor and the whole tree jumped. Dual pane only
                // made it obvious - it did the same thing with one pane.
                if (i == segments.Count - 1) break;

                await current.LoadChildrenAsync();   // needed to match the NEXT segment
                current.IsExpanded = true;
            }

            _treeSyncing = true;
            current.IsSelected = true;
            _treeSyncing = false;
        }

        private static IEnumerable<string> RelativeSegments(string rootPath, string fullPath)
        {
            string rest = fullPath.Substring(rootPath.Length);
            return rest.Split(new[] { System.IO.Path.DirectorySeparatorChar,
                                      System.IO.Path.AltDirectorySeparatorChar },
                              StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
