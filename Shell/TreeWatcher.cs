using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace KillerShell.Shell
{
    // Keeps the folder tree in step with the disk. Partial of MainWindow.
    //
    // The browse watcher (BrowseWatcher.cs) follows only the folder the active tab is showing,
    // so a branch open anywhere else in the tree kept the children it read when it was expanded:
    // a folder another program or computer added, renamed or deleted there never showed. Each
    // open branch now gets its own folders-only watch. A collapsed branch is not watched,
    // because it shows nothing that could be stale and it is refreshed when it is opened again
    // (FolderTree_Expanded). The count is capped so a tree with hundreds of open branches cannot
    // run the process out of handles; branches past the cap simply refresh the old way.
    public partial class MainWindow
    {
        private readonly Dictionary<string, FileSystemWatcher> _treeWatchers = new(StringComparer.OrdinalIgnoreCase);

        // Folders whose watch failed or broke. Not retried while they stay open: recreating a
        // watch the provider refuses would fail again on every sync. Forgotten once the branch
        // closes or leaves the tree, so a later reopen gets a fresh attempt.
        private readonly HashSet<string> _treeWatchFailed = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _treeTouched = new(StringComparer.OrdinalIgnoreCase);
        private DispatcherTimer? _treeWatchDebounce;

        private const int TreeWatchMax = 64;

        /// <summary>
        /// Watch exactly the branches that are open on screen. Safe to call at any time; it only
        /// adds and removes the difference.
        /// </summary>
        internal void SyncTreeWatchers()
        {
            var wanted = new List<string>();
            if (_treeOpen && !DemoMode)
            {
                // Same walk as SaveTreeExpansion: a child of a collapsed parent can still carry
                // IsExpanded, but it is not on screen.
                void Walk(FolderNode n)
                {
                    if (!n.IsExpanded || wanted.Count >= TreeWatchMax) return;
                    // WSL paths are left out, to stay clear of the Ubuntu folder refresh loop
                    // fixed in 1.3.2.
                    if (!n.IsGroup && n.IsLoaded && !string.IsNullOrEmpty(n.Path)
                        && !Services.WslDistributions.TryParsePath(n.Path, out _, out _))
                        wanted.Add(n.Path);
                    foreach (var c in n.Children) Walk(c);
                }
                foreach (var r in _treeRoots) Walk(r);
            }

            var keep = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
            foreach (string path in _treeWatchers.Keys.Where(p => !keep.Contains(p)).ToList())
            {
                DisposeTreeWatcher(_treeWatchers[path]);
                _treeWatchers.Remove(path);
            }
            _treeWatchFailed.RemoveWhere(p => !keep.Contains(p));

            foreach (string path in wanted)
            {
                if (_treeWatchers.ContainsKey(path) || _treeWatchFailed.Contains(path)) continue;
                FileSystemWatcher? watcher = CreateTreeWatcher(path);
                if (watcher == null) _treeWatchFailed.Add(path);
                else _treeWatchers[path] = watcher;
            }
        }

        private FileSystemWatcher? CreateTreeWatcher(string folder)
        {
            try
            {
                // Folders only. The tree never shows files, so file churn in a busy folder (a
                // download, a log) must not re-read its branch.
                var watcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false,
                };
                watcher.Created += OnTreeFsEvent;
                watcher.Deleted += OnTreeFsEvent;
                watcher.Renamed += OnTreeFsEvent;
                watcher.Error   += OnTreeFsError;
                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch { return null; }
        }

        private void DisposeTreeWatcher(FileSystemWatcher watcher)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnTreeFsEvent;
                watcher.Deleted -= OnTreeFsEvent;
                watcher.Renamed -= OnTreeFsEvent;
                watcher.Error   -= OnTreeFsError;
                watcher.Dispose();
            }
            catch { /* already gone */ }
        }

        // These arrive on a threadpool thread; the queue and the tree belong to the dispatcher.
        private void OnTreeFsEvent(object sender, FileSystemEventArgs e) => QueueTreeRefresh(sender, broken: false);

        // Windows ends a watch with an error when the watched folder itself is deleted (see
        // BrowseWatcher.cs), so the branch above it is re-read too. Either way the watch is
        // finished and is not recreated while the branch stays open.
        private void OnTreeFsError(object sender, ErrorEventArgs e) => QueueTreeRefresh(sender, broken: true);

        private void QueueTreeRefresh(object sender, bool broken)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (sender is not FileSystemWatcher watcher) return;
                string folder = _treeWatchers.FirstOrDefault(kv => ReferenceEquals(kv.Value, watcher)).Key;
                if (folder == null) return;   // closed or replaced since the event was raised

                _treeTouched.Add(folder);
                if (broken)
                {
                    if (ParentOf(folder) is { } parent) _treeTouched.Add(parent);   // Browse.cs
                    DisposeTreeWatcher(watcher);
                    _treeWatchers.Remove(folder);
                    _treeWatchFailed.Add(folder);
                }

                // Long enough to fold the several events one folder copy raises into one re-read.
                _treeWatchDebounce ??= CreateTreeWatchTimer();
                _treeWatchDebounce.Stop();
                _treeWatchDebounce.Start();
            }, DispatcherPriority.Background);
        }

        private DispatcherTimer CreateTreeWatchTimer()
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background)
                { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) => { timer.Stop(); FlushTreeWatch(); };
            return timer;
        }

        private async void FlushTreeWatch()
        {
            // Parents first, so a deleted branch is dropped before anything tries to re-read it.
            var folders = _treeTouched.OrderBy(f => f.Length).ToList();
            _treeTouched.Clear();
            if (!_treeOpen) return;

            foreach (string folder in folders)
                if (FindLoadedNode(folder) is { } node)   // FolderTree.cs
                    await node.RefreshAsync(deep: false);

            SyncTreeWatchers();
        }
    }
}
