using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerShell.Models;

namespace KillerShell.Shell
{
    // Mouse gestures over the results list: rubber-band selection, dragging files out, and
    // accepting things dropped in. Partial of MainWindow.
    //
    // Marquee and drag-out both begin as a left press and a move, so something has to tell them
    // apart. Explorer already settled it and this follows: a drag that starts ON an item drags
    // those files out, a drag that starts on EMPTY SPACE draws the rectangle. Deciding at press
    // time means neither gesture has to guess later.
    public partial class MainWindow
    {
        // ── Gesture state ────────────────────────────────────────
        private Point       _pressAt;
        private bool        _marqueeOn;
        private bool        _dragArmed;      // pressed on an item, waiting to clear the threshold
        private SearchResult? _dragSeed;     // the item that was pressed
        private ListBoxItem?  _dragSeedItem; // its container, kept for a late re-resolve - see StartFileDrag
        private bool        _marqueeAdditive;

        // Which pane a drag-out started from, captured in StartFileDrag while that pane still
        // has real focus. Only the ACTIVE tab is watched for filesystem changes
        // (BrowseWatcher.cs), and Window_Drop switches focus to the DROP pane before the move
        // runs (FocusPane(dropPane), below) - so a cross-pane move left the SOURCE pane, now
        // unwatched, showing the file it no longer has until the user clicked back into it
        // (Steve, 2026-08-03: "it left a copy in the other folder... the old one disappeared
        // when i clicked back into that folder - something needs to update"). Used by
        // RefreshSourcePaneIfStale after the drop to re-list that pane's folder directly rather
        // than waiting on a watcher it no longer has.
        private FilePane? _dragSourcePane;

        internal void ResultsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _pressAt      = e.GetPosition(Pane.ResultsList);
            _marqueeOn    = false;
            _dragArmed    = false;
            _dragSeed     = null;
            _dragSeedItem = null;

            // The scrollbar is not a row, so it used to fall through to the marquee branch
            // below - which captures the mouse and leaves the scrollbar with nothing to drag.
            // Clicking or dragging it did nothing at all. It is not "empty space" either; it is
            // the one piece of chrome inside this control that owns its own clicks.
            if (InScrollBar(e.OriginalSource as DependencyObject)) return;

            var item = ItemUnder(e.OriginalSource as DependencyObject);
            System.Diagnostics.Debug.WriteLine($"[DragDiag] PressDown: OriginalSource={e.OriginalSource?.GetType().Name}, item={(item == null ? "NULL (marquee branch)" : "found (drag-armed)")}");
            if (item != null)
            {
                // On an item: arm a possible drag-out. Selection itself is left to the ListBox,
                // which already does Ctrl and Shift the way everyone expects.
                _dragArmed    = true;
                _dragSeedItem = item;
                _dragSeed     = DataFor(item);
                System.Diagnostics.Debug.WriteLine($"[DragDiag] PressDown: seed={(_dragSeed == null ? "NULL (would have failed the old DataContext read too)" : _dragSeed.FilePath)}");
                return;
            }

            // Empty space: rubber band. Ctrl means add to what is already selected.
            _marqueeAdditive = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (!_marqueeAdditive) Pane.ResultsList.SelectedItems.Clear();

            _marqueeOn = true;
            Pane.MarqueeRect.Visibility = Visibility.Visible;
            PlaceMarquee(_pressAt, _pressAt);
            Pane.ResultsList.CaptureMouse();
        }

        internal void ResultsList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(Pane.ResultsList);

            if (_marqueeOn)
            {
                PlaceMarquee(_pressAt, now);
                SelectWithin(new Rect(_pressAt, now));
                return;
            }

            if (!_dragArmed) return;

            double dx = Math.Abs(now.X - _pressAt.X), dy = Math.Abs(now.Y - _pressAt.Y);
            System.Diagnostics.Debug.WriteLine($"[DragDiag] MouseMove while armed: dx={dx:0.0}, dy={dy:0.0}, thresholdX={SystemParameters.MinimumHorizontalDragDistance}, thresholdY={SystemParameters.MinimumVerticalDragDistance}");

            // Not every wobble is a drag. Wait for the system's own threshold so a click that
            // moves a pixel still reads as a click.
            if (Math.Abs(now.X - _pressAt.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(now.Y - _pressAt.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            _dragArmed = false;
            StartFileDrag();
        }

        internal void ResultsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _dragArmed = false;
            if (!_marqueeOn) return;

            _marqueeOn = false;
            Pane.MarqueeRect.Visibility = Visibility.Collapsed;
            Pane.ResultsList.ReleaseMouseCapture();
        }

        private void PlaceMarquee(Point a, Point b)
        {
            var r = new Rect(a, b);
            Canvas.SetLeft(Pane.MarqueeRect, r.X);
            Canvas.SetTop(Pane.MarqueeRect, r.Y);
            Pane.MarqueeRect.Width  = r.Width;
            Pane.MarqueeRect.Height = r.Height;
        }

        // Only realized containers are considered, which is exactly right rather than a shortcut:
        // the band cannot reach past the viewport because there is no auto-scroll, so anything
        // unrealized is by definition outside the rectangle.
        private void SelectWithin(Rect band)
        {
            var host = ItemsHost();
            if (host == null) return;

            foreach (var child in host.Children.OfType<ListBoxItem>())
            {
                Rect bounds;
                try
                {
                    bounds = child.TransformToAncestor(Pane.ResultsList)
                                  .TransformBounds(new Rect(child.RenderSize));
                }
                catch { continue; }   // mid-recycle, no transform yet

                bool hit = band.IntersectsWith(bounds);

                if (hit) child.IsSelected = true;
                else if (!_marqueeAdditive) child.IsSelected = false;
            }
        }

        // The panel that actually holds the containers. Walking to it beats asking the generator
        // for every index: the list can hold six figures and almost none of them are realized.
        private Panel? ItemsHost()
        {
            var presenter = FindDescendant<ItemsPresenter>(Pane.ResultsList);
            if (presenter == null) return null;
            if (VisualTreeHelper.GetChildrenCount(presenter) == 0) return null;
            return VisualTreeHelper.GetChild(presenter, 0) as Panel;
        }

        /// <summary>True when the press landed on the list's scrollbar rather than its content.</summary>
        private static bool InScrollBar(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is System.Windows.Controls.Primitives.ScrollBar) return true;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        // OriginalSource can be a non-visual (a Run inside a TextBlock, say), and asking
        // VisualTreeHelper for its parent throws, so step through the logical tree for those.
        private static ListBoxItem? ItemUnder(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is ListBoxItem li) return li;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        // A freshly-realized container's generator mapping AND its DataContext binding can both
        // still read null at the exact tick PreviewMouseLeftButtonDown fires on it - a
        // [DragDiag] trace caught it happening to BOTH at once on the very first press of a
        // session (item != null, but this returned null either way), so neither one is a
        // reliable fallback for the other at press time. What actually settles it is time: by
        // the time StartFileDrag re-tries this same call (after the drag has cleared the
        // system's move threshold, i.e. after several MouseMove ticks), the container has
        // always caught up. See _dragSeedItem / StartFileDrag for the re-resolve
        // (Steve, 2026-08-03: "i need to be able to click and drag immediately without having
        // to click, stop, then click+drag").
        private static SearchResult? DataFor(ListBoxItem? item)
        {
            if (item == null) return null;
            var owner = ItemsControl.ItemsControlFromItemContainer(item);
            if (owner?.ItemContainerGenerator.ItemFromContainer(item) is SearchResult sr) return sr;
            return item.DataContext as SearchResult;
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is T hit) return hit;
                var deeper = FindDescendant<T>(c);
                if (deeper != null) return deeper;
            }
            return null;
        }

        // ── Dragging files out ───────────────────────────────────
        /// <summary>
        /// The files a file command should act on: the whole selection when the pressed item is
        /// part of it, otherwise just the item under the pointer. Explorer's rule, and it is what
        /// stops a drag on an unselected file from silently dragging something else.
        /// </summary>
        private List<string> FilesForCommand(SearchResult? seed)
        {
            var selected = Pane.ResultsList.SelectedItems.OfType<SearchResult>().ToList();

            if (seed != null && !selected.Contains(seed))
                return new List<string> { seed.FilePath };

            if (selected.Count > 0) return selected.Select(r => r.FilePath).ToList();
            return seed != null ? new List<string> { seed.FilePath } : new List<string>();
        }

        private void StartFileDrag()
        {
            // Captured before anything about focus can change - see the field comment.
            _dragSourcePane = Pane;

            // Re-try the same lookup PressDown already did. If the container's generator
            // mapping/DataContext were not caught up yet at press time, the time spent clearing
            // the move threshold (several MouseMove ticks) is enough for them to have settled by
            // now - see the comment above DataFor.
            if (_dragSeed == null) _dragSeed = DataFor(_dragSeedItem);
            System.Diagnostics.Debug.WriteLine($"[DragDiag] StartFileDrag: late-resolved seed={_dragSeed?.FilePath ?? "still null"}");

            var paths = FilesForCommand(_dragSeed).Where(File.Exists).ToArray();
            System.Diagnostics.Debug.WriteLine($"[DragDiag] StartFileDrag: seed={_dragSeed?.FilePath ?? "null"}, resolvedPaths={paths.Length}");
            if (paths.Length == 0) return;

            // A real native-COM IDataObject, not System.Windows.Forms.DataObject: the WinForms
            // one's IDataObject.SetData throws NotImplementedException (it is written only to be
            // read FROM as a drag source, never written TO), so the shell's IDragSourceHelper
            // cannot write its own drag-image formats onto it and InitializeFromBitmap fails
            // outright - confirmed here by DragImage's own trace: hr=0x80004001 (E_NOTIMPL),
            // exactly the HRESULT a NotImplementedException becomes crossing the COM boundary
            // (Steve, 2026-08-03). Services.NativeDataObject implements SetData for real.
            var data = new Services.NativeDataObject();
            data.SetHGlobal(Services.NativeDataObject.CF_HDROP, Services.NativeDataObject.BuildHDrop(paths));
            data.SetHGlobal(Services.NativeDataObject.CF_UNICODETEXT,
                Services.NativeDataObject.BuildUnicodeText(string.Join(Environment.NewLine, paths)));

            // The file's own icon at half opacity, following the cursor - the same thing Explorer
            // shows, and the whole reason a plain DoDragDrop reads as "just a cursor" (Steve,
            // 2026-08-03). One icon even for a multi-file drag: which file's icon to show for a
            // dozen mixed types is not worth guessing at, and Explorer itself falls back the same
            // way for a mixed selection.
            var dragIcon = Services.IconCache.For(paths[0], 48);
            Services.DragImage.Attach(data, dragIcon);

            // Native ole32 DoDragDrop, not System.Windows.DragDrop.DoDragDrop: WPF re-wraps
            // whatever it is handed in its own System.Windows.DataObject, which would throw away
            // the SetData behavior NativeDataObject exists for. Calling ole32 directly keeps the
            // shell's drag-image writes and our own data on the exact same object the drag loop
            // uses. Copy AND Move offered: the drop target decides, so holding Shift while
            // dropping into Explorer moves the files instead of copying them. Offering Copy alone
            // made KillerShell the one place a Shift-drag silently did the wrong thing.
            try
            {
                const int DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2;
                int hr = Services.NativeDragDrop.DoDragDrop(data, new Services.SimpleDropSource(),
                    DROPEFFECT_COPY | DROPEFFECT_MOVE, out int finalEffect);
                System.Diagnostics.Debug.WriteLine($"[DragDiag] DoDragDrop returned: hr=0x{hr:X8}, effect={finalEffect}");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DragDiag] DoDragDrop THREW: {ex}"); }
        }

        // ── Accepting a drop ─────────────────────────────────────
        // Folders set the scope, files become a piped list to search inside. Both are things you
        // would otherwise reach through the picker or a second search, so dropping is a shortcut
        // rather than a new capability.

        // Lives for one DragEnter..Drop/DragLeave span - see Services.DropTargetHelper. Without
        // this, KillerShell's own AllowDrop plumbing never calls IDropTargetHelper, so the shell
        // drag image DragImage.Attach wrote onto the data object never got drawn for a drag that
        // stayed inside KillerShell (pane to pane, or window to window) - only a drop onto real
        // Explorer, whose own drop target DOES call the helper, ever showed it (Steve, 2026-08-03).
        private Services.DropTargetHelper? _dropImageHelper;

        private static int EffectsToNative(DragDropEffects effects)
        {
            int e = 0;
            if ((effects & DragDropEffects.Copy) != 0) e |= 1;
            if ((effects & DragDropEffects.Move) != 0) e |= 2;
            if ((effects & DragDropEffects.Link) != 0) e |= 4;
            return e;
        }

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            // WPF raises its OWN DragEnter/DragLeave per element as the pointer crosses child
            // elements within the same window (Image -> Border, pane -> pane), but there is only
            // ONE real native OLE drag session for the whole window - confirmed in the trace: a
            // second "DropTargetHelper.Enter: hr=0x00000000" fired mid-drag with no real
            // DragLeave from the OS in between. The shell's IDropTargetHelper expects exactly one
            // Enter, then Over*, then Drop/Leave for a session; calling Enter a second time while
            // it still considers the first session open desyncs its internal state machine, and
            // the very next call into that corrupted state is what threw
            // AccessViolationException (Steve, 2026-08-03). So: only call Enter once per actual
            // drag - if a helper is already active, this is just another crossing within the same
            // session and gets treated as an Over, not a fresh Enter.
            if (_dropImageHelper != null)
            {
                _dropImageHelper.Over(PointToScreen(e.GetPosition(this)), EffectsToNative(e.Effects));
                return;
            }

            if (e.Data is not System.Runtime.InteropServices.ComTypes.IDataObject comData) return;
            if (PresentationSource.FromVisual(this) is not System.Windows.Interop.HwndSource hwndSource) return;

            var screenPt = PointToScreen(e.GetPosition(this));
            _dropImageHelper = new Services.DropTargetHelper();
            _dropImageHelper.Enter(hwndSource.Handle, comData, screenPt, EffectsToNative(e.Effects));
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            // Do NOT tear the helper down here: this fires on every WPF-level crossing out of a
            // child element too (leaving the Image just before entering the Border beneath it),
            // not only when the drag actually leaves the window. Disposing here just to have
            // Window_DragEnter immediately build a second Enter session for the next element is
            // the same Enter-while-already-open desync that threw AccessViolationException.
            // Window_Drop is where the real end of a KillerShell-hosted drag is Leave()'d and
            // disposed; if the drag instead lands on another app, that app's own drop target
            // calls IDropTargetHelper on ITS OWN COM instance, so nothing here needs to react to
            // this window losing the drag either.
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                System.Diagnostics.Debug.WriteLine("[DragDiag] Window_DragOver: no FileDrop data present - Effects=None");
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                _dropImageHelper?.Over(PointToScreen(e.GetPosition(this)), EffectsToNative(e.Effects));
                return;
            }

            // Browsing a folder means a drop is real file work, so the cursor has to say which
            // kind before the mouse comes up - Link would promise a shortcut and then copy.
            // Shift forces move, Ctrl forces copy, and with neither it follows the volume: a
            // move within one drive, a copy across drives, which is Explorer's rule.
            string? overTarget = DropTarget(e);                // FileCommands.cs / below
            if (overTarget != null)
            {
                bool ctrl  = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
                bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey)   != 0;
                e.Effects = shift ? DragDropEffects.Move
                          : ctrl  ? DragDropEffects.Copy
                          : DragDropEffects.Copy | DragDropEffects.Move;
            }
            else e.Effects = DragDropEffects.Link;            // search tab: scope or pipe, as before

            System.Diagnostics.Debug.WriteLine($"[DragDiag] Window_DragOver: OriginalSource={e.OriginalSource?.GetType().Name}, target={overTarget ?? "null"}, Effects={e.Effects}");
            e.Handled = true;
            _dropImageHelper?.Over(PointToScreen(e.GetPosition(this)), EffectsToNative(e.Effects));
        }

        /// <summary>
        /// Where a file drop actually lands. A folder ROW under the pointer wins over the
        /// browsed folder - without this, dragging one item onto another folder icon just
        /// dropped it back into the current directory (the same folder it started in), which
        /// the "already there" filter below then silently swallowed as a no-op. That is the
        /// whole point of dragging files around in a file browser, so it has to check the row
        /// before falling back to the browsed folder.
        ///
        /// The fallback has to ask the PANE UNDER THE POINTER for its browsed folder, not the
        /// window-wide TargetFolder() - that reads the FOCUSED pane, and dragging never moves
        /// focus off the pane you picked the file up FROM. Falling back to TargetFolder() here
        /// meant a drop into empty space in the OTHER pane silently landed back in the source
        /// pane's own folder instead (Steve, 2026-08-03).
        /// </summary>
        private string? DropTarget(DragEventArgs e)
        {
            if (DataFor(ItemUnder(e.OriginalSource as DependencyObject)) is { } sr &&
                sr.IsDirectory && Directory.Exists(sr.FilePath))
                return sr.FilePath;

            var pane = PaneUnder(e.OriginalSource as DependencyObject) ?? Pane;
            return TargetFolder(pane);
        }

        /// <summary>The FilePane a visual element sits inside, or null if it is not in either one
        /// (e.g. the tab strip, a title-bar button).</summary>
        private static FilePane? PaneUnder(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is FilePane fp) return fp;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            System.Diagnostics.Debug.WriteLine($"[DragDiag] Window_Drop: fired, OriginalSource={e.OriginalSource?.GetType().Name}");

            if (e.Data is System.Runtime.InteropServices.ComTypes.IDataObject comData)
                _dropImageHelper?.Drop(comData, PointToScreen(e.GetPosition(this)), EffectsToNative(e.Effects));
            _dropImageHelper?.Dispose();
            _dropImageHelper = null;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0)
            {
                System.Diagnostics.Debug.WriteLine("[DragDiag] Window_Drop: no FileDrop data - bailing");
                return;
            }

            // Browsing? Then this is a file operation, not a search gesture (FileCommands.cs).
            string? target = DropTarget(e);
            System.Diagnostics.Debug.WriteLine($"[DragDiag] Window_Drop: dropped.Length={dropped.Length}, target={target ?? "null"}");
            if (target != null)
            {
                // Everything past this point - the status message, the refresh, the navigate-
                // and-select after a move - reads and writes through the FOCUSED pane (_active /
                // Pane). Focus never moves during a drag on its own, so without this a cross-pane
                // drop kept acting on the pane you dragged FROM instead of the one you actually
                // dropped into (Steve, 2026-08-03).
                if (PaneUnder(e.OriginalSource as DependencyObject) is { } dropPane && dropPane != Pane)
                    FocusPane(dropPane);   // Panes.cs

                // Anything already sitting in the target folder is dropped onto itself - that is
                // a no-op in Explorer, not a name collision, so it is filtered out before the
                // conflict prompt gets a chance to ask about it.
                var incoming = dropped
                    .Where(p => !string.Equals(System.IO.Path.GetDirectoryName(p), target,
                                               StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                System.Diagnostics.Debug.WriteLine($"[DragDiag] Window_Drop: incoming after self-filter={incoming.Length}");
                if (incoming.Length == 0) return;

                bool ctrl  = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
                bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey)   != 0;

                // Awaited in sequence, not run concurrently: DropOntoFolder's own post-move
                // refresh (RunCopyMove -> NavigateToAndSelectAll) reads/writes the FOCUSED pane,
                // and so does RefreshSourcePaneIfStale below. NavigateTo bails out if focus moved
                // out from under it mid-listing (Browse.cs's "tab != _active" guard, there so a
                // slow listing cannot land on top of a folder you have since moved away from) -
                // so running these two back to back unawaited let whichever one's listing was
                // still in flight when the other flipped focus get silently discarded. That was
                // the one-drag-late bug (Steve, 2026-08-03: "i drag box.png and it disappears i
                // drag box1 and box finally appears"). Awaiting the drop's own refresh here means
                // it has already landed before the source pane's focus is touched at all.
                await DropOntoFolder(incoming, target, e.AllowedEffects, ctrl, shift);
                await RefreshSourcePaneIfStale(_dragSourcePane);
                return;
            }

            var folders = dropped.Where(Directory.Exists).ToList();
            var files   = dropped.Where(File.Exists).ToList();

            // Files win when both are present: dropping a mixed bag reads as "search these",
            // and a folder in that bag is ambiguous rather than useful.
            if (files.Count > 0) { PipeDroppedFiles(files); return; }
            if (folders.Count > 0) ScopeToFolder(folders[0]);
        }

        /// <summary>
        /// Re-list the pane a drag-out started from if a cross-pane move just left it stale -
        /// see the _dragSourcePane field comment. A no-op for the ordinary case (drag stayed in
        /// the same pane, or that pane isn't even browsing a real folder anymore).
        /// </summary>
        private async System.Threading.Tasks.Task RefreshSourcePaneIfStale(FilePane? sourcePane)
        {
            if (sourcePane == null || sourcePane == Pane) return;
            if (!DualPane && sourcePane == RightPane) return;   // not actually on screen

            var tab = sourcePane.Active;
            if (tab == null || !tab.IsBrowsing || string.IsNullOrEmpty(tab.CurrentFolder)) return;

            var keep = Pane;
            FocusPaneQuiet(sourcePane);
            // Awaited before restoring focus - NavigateTo checks "tab != _active" once its
            // listing comes back (Browse.cs), so quiet-restoring focus before this finishes
            // made it discard its own results, which is why the pane looked like it refreshed
            // a whole drag late instead of right away.
            await NavigateTo(tab.CurrentFolder, record: false);   // Browse.cs
            FocusPaneQuiet(keep);
        }

        private void ScopeToFolder(string folder)
        {
            _active.PipeFiles = null;             // a real folder scope replaces any piped list
            _active.PipeLabel = string.Empty;
            _active.PipeArgs  = null;
            _active.RootPath  = folder;

            Pane.RootPathBox.Text    = folder;
            Pane.ScopePathLabel.Text = folder;
            SetTabStatus(_active, folder);
        }

        // Same shape as PipeIntoNewTab (Results.cs), but the file list comes from the drop rather
        // than from a previous search, so the breadcrumb says where it came from instead of
        // naming a source tab.
        private void PipeDroppedFiles(List<string> files)
        {
            CaptureTab(_active);
            var t = CreateTab();

            t.PipeFiles = files;
            t.RootPath  = System.IO.Path.GetDirectoryName(files[0]) ?? string.Empty;
            t.PipeArgs  = new object[] { files.Count.ToString("N0"), Loc("Str_Scope_Dropped"), string.Empty };
            t.PipeLabel = string.Format(Loc("Str_Pipe_Scope"), t.PipeArgs);
            t.Title     = string.Format(Loc("Str_Tab_Dropped"), files.Count.ToString("N0"));

            ActivateTab(t);
        }
    }
}
