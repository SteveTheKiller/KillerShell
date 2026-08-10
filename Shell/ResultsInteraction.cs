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
        // ── Drag diagnostics ─────────────────────────────────────
        /// <summary>
        /// Turn on to get the [DragDiag] trace back. OFF by default.
        ///
        /// These are Debug.WriteLine, so they compile out of Release entirely - but in a Debug
        /// build under a debugger each one is a synchronous write the debugger has to receive,
        /// and two of these sat on the hottest paths in the app: every mouse-move while a drag
        /// is armed, and every DragOver, which fire dozens of times a second. That is a real
        /// part of "everything is sluggish" while debugging.
        /// Kept rather than deleted because the drag-out investigation still needs them
        /// (BACKLOG.md) - flip this to true, reproduce, flip it back.
        /// </summary>
        // Explicitly initialized, not just declared: nothing assigns it in normal operation - you
        // set it in the debugger or edit this line - and a bare declaration is CS0649,
        // "never assigned to, and will always have its default value false".
        internal static bool DragDiagEnabled = false;

        [System.Diagnostics.Conditional("DEBUG")]
        private static void DragTrace(string msg)
        {
            if (DragDiagEnabled) System.Diagnostics.Debug.WriteLine("[DragDiag] " + msg);
        }

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
        // unwatched, showing the file it no longer has until the user clicked back into it,
        // which read as the drag leaving a stale copy behind. Used by
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
            DragTrace($"PressDown: OriginalSource={e.OriginalSource?.GetType().Name}, item={(item == null ? "NULL (marquee branch)" : "found (drag-armed)")}");
            if (item != null)
            {
                // On an item: arm a possible drag-out. Selection itself is left to the ListBox,
                // which already does Ctrl and Shift the way everyone expects.
                _dragArmed    = true;
                _dragSeedItem = item;
                _dragSeed     = DataFor(item);
                DragTrace($"PressDown: seed={(_dragSeed == null ? "NULL (would have failed the old DataContext read too)" : _dragSeed.FilePath)}");
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
            DragTrace($"MouseMove while armed: dx={dx:0.0}, dy={dy:0.0}, thresholdX={SystemParameters.MinimumHorizontalDragDistance}, thresholdY={SystemParameters.MinimumVerticalDragDistance}");

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
        // always caught up. See _dragSeedItem / StartFileDrag for the re-resolve - it is what
        // makes click-and-drag work immediately, without having to click, stop, then
        // click+drag.
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
                return [seed.FilePath];

            if (selected.Count > 0) return [.. selected.Select(r => r.FilePath)];
            return seed != null ? [seed.FilePath] : [];
        }

        private void StartFileDrag()
        {
            // Captured before anything about focus can change - see the field comment.
            _dragSourcePane = Pane;

            // Re-try the same lookup PressDown already did. If the container's generator
            // mapping/DataContext were not caught up yet at press time, the time spent clearing
            // the move threshold (several MouseMove ticks) is enough for them to have settled by
            // now - see the comment above DataFor.
            _dragSeed ??= DataFor(_dragSeedItem);
            System.Diagnostics.Debug.WriteLine($"[DragDiag] StartFileDrag: late-resolved seed={_dragSeed?.FilePath ?? "still null"}");

            // Nothing inside an archive exists on disk, so the File.Exists filter would remove
            // every row and the drag would do nothing at all. Dragging out extracts temp copies
            // first and drags those instead (ArchiveEdit.cs).
            bool fromArchive = InArchive(Pane);

            var paths = fromArchive
                ? ExtractForDragOut(FilesForCommand(_dragSeed))
                : [.. FilesForCommand(_dragSeed).Where(File.Exists)];
            System.Diagnostics.Debug.WriteLine($"[DragDiag] StartFileDrag: seed={_dragSeed?.FilePath ?? "null"}, resolvedPaths={paths.Length}");
            if (paths.Length == 0) return;

            // A real native-COM IDataObject, not System.Windows.Forms.DataObject: the WinForms
            // one's IDataObject.SetData throws NotImplementedException (it is written only to be
            // read FROM as a drag source, never written TO), so the shell's IDragSourceHelper
            // cannot write its own drag-image formats onto it and InitializeFromBitmap fails
            // outright - confirmed here by DragImage's own trace: hr=0x80004001 (E_NOTIMPL),
            // exactly the HRESULT a NotImplementedException becomes crossing the COM boundary.
            // Services.NativeDataObject implements SetData for real.
            var data = new Services.NativeDataObject();
            try
            {
                data.SetHGlobal(Services.NativeDataObject.CF_HDROP, Services.NativeDataObject.BuildHDrop(paths));
                data.SetHGlobal(Services.NativeDataObject.CF_UNICODETEXT,
                    Services.NativeDataObject.BuildUnicodeText(string.Join(Environment.NewLine, paths)));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DragDiag] StartFileDrag: BuildHDrop/BuildUnicodeText THREW: {ex}");
                data.ReleaseAll();   // the first SetHGlobal may already hold a global
                return;  // Bail BEFORE attaching drag-image so no orphaned shell window is left behind
            }

            // The file's own icon at half opacity, following the cursor - the same thing Explorer
            // shows, and the whole reason a plain DoDragDrop reads as just a text cursor.
            // One icon even for a multi-file drag: which file's icon to show for a
            // dozen mixed types is not worth guessing at, and Explorer itself falls back the same
            // way for a mixed selection.
            var dragIcon = Services.IconCache.For(paths[0], 48);
            var dragHelper = Services.DragImage.Attach(data, dragIcon);

            // Native ole32 DoDragDrop, not System.Windows.DragDrop.DoDragDrop: WPF re-wraps
            // whatever it is handed in its own System.Windows.DataObject, which would throw away
            // the SetData behavior NativeDataObject exists for. Calling ole32 directly keeps the
            // shell's drag-image writes and our own data on the exact same object the drag loop
            // uses. Copy AND Move offered: the drop target decides, so holding Shift while
            // dropping into Explorer moves the files instead of copying them. Offering Copy alone
            // made KillerShell the one place a Shift-drag silently did the wrong thing.
            //
            // OUT OF AN ARCHIVE IS THE ONE EXCEPTION: Copy only, never Move. What is being
            // dragged there is already a temp extract, so a target that took it as a move would
            // delete the temp copy and leave the archive untouched - the file would look moved
            // and not be. A real move out means extract plus delete-from-archive, two steps that
            // cannot be made one, and a failure between them either loses the file or silently
            // leaves it behind. Explorer treats zip drag-out as a copy for the same reason.
            try
            {
                const int DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2;
                int allowed = fromArchive ? DROPEFFECT_COPY : DROPEFFECT_COPY | DROPEFFECT_MOVE;
                int hr = Services.NativeDragDrop.DoDragDrop(data, new Services.SimpleDropSource(),
                    allowed, out int finalEffect);
                System.Diagnostics.Debug.WriteLine($"[DragDiag] DoDragDrop returned: hr=0x{hr:X8}, effect={finalEffect}");

                // Said out loud rather than left to be inferred: what landed is a copy, and the
                // archive still holds the entry.
                if (fromArchive && finalEffect != 0)
                    SetTabStatusKey(_active, "Str_Status_ArchiveDragCopy");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DragDiag] DoDragDrop THREW: {ex}"); }
            finally
            {
                // Only after DoDragDrop returns, and in this order. The helper wrapper goes first;
                // then ReleaseAll drops every medium the shell stored on the data object during
                // the drag, and THAT is what destroys the layered drag-image window - the shell
                // object that owns the window is referenced through those media's pUnkForRelease,
                // so until they are released its refcount never reaches zero and the drag icon
                // sits on screen until the app exits. That was the ghost, on every external drop.
                // Disposing the helper alone never fixed it because the helper wrapper holds a
                // different reference from the ones SetData stored (NativeDataObject.ReleaseAll).
                dragHelper?.Dispose();
                data.ReleaseAll();
                System.Diagnostics.Debug.WriteLine($"[DragDiag] Disposed drag-image helper and released data object media");
            }
        }

        // ── Accepting a drop ─────────────────────────────────────
        // Folders set the scope, files become a piped list to search inside. Both are things you
        // would otherwise reach through the picker or a second search, so dropping is a shortcut
        // rather than a new capability.

        // Lives for one DragEnter..Drop/DragLeave span - see Services.DropTargetHelper. Without
        // this, KillerShell's own AllowDrop plumbing never calls IDropTargetHelper, so the shell
        // drag image DragImage.Attach wrote onto the data object never got drawn for a drag that
        // stayed inside KillerShell (pane to pane, or window to window) - only a drop onto real
        // Explorer, whose own drop target DOES call the helper, ever showed it.
        private Services.DropTargetHelper? _dropImageHelper;

        // Set by every DragEnter/DragOver, cleared by DragLeave: how a deferred check tells a
        // REAL exit from the window apart from WPF's synthetic child-element crossings. See
        // Window_DragLeave.
        private bool _dragOverWindow;

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
            // AccessViolationException. So: only call Enter once per actual
            // drag - if a helper is already active, this is just another crossing within the same
            // session and gets treated as an Over, not a fresh Enter.
            _dragOverWindow = true;

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
            // This fires on every WPF-level crossing out of a child element too (leaving the
            // Image just before entering the Border beneath it), not only when the drag actually
            // leaves the window - so the helper must NOT be torn down synchronously here.
            // Disposing per crossing, with Window_DragEnter immediately opening a second Enter
            // session for the next element, is the Enter-while-already-open desync that threw
            // AccessViolationException.
            //
            // But a REAL exit does have to Leave() the helper. This method's previous body was a
            // comment asserting that when the drag moves on to another app, that app's own drop
            // target takes over the drag image - which is only true of targets that call
            // IDropTargetHelper, like Explorer. Telegram, GIMP and most non-shell apps never do,
            // so nothing ever told the shell the cursor left this window and the drag image
            // stayed painted at the last Over() position, sitting on the window edge until the
            // app exited. That was the ghost icon's second half (the first was the data object
            // never releasing what the shell stored on it - NativeDataObject.ReleaseAll).
            //
            // Telling the two apart: a synthetic child crossing raises the matching DragEnter
            // SYNCHRONOUSLY, in this same dispatcher pass, before any queued operation can run. A
            // real exit raises nothing. So: clear the flag, let the queued check run after the
            // current pass, and only tear down if no Enter/Over has set the flag again by then.
            // Re-entry later is fine - Window_DragEnter builds a fresh helper and a fresh Enter,
            // which is a legitimate new session after a Leave().
            _dragOverWindow = false;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_dragOverWindow || _dropImageHelper == null) return;
                _dropImageHelper.Leave();
                _dropImageHelper.Dispose();
                _dropImageHelper = null;
                DragTrace("Window_DragLeave: real exit - drag image released");
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            _dragOverWindow = true;   // see Window_DragLeave - proves the drag is still inside

            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                DragTrace("Window_DragOver: no FileDrop data present - Effects=None");
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
            // Inside a WRITABLE archive a drop is a real add (ArchiveEdit.cs). Copy only: what
            // goes into an archive is always a copy of what is on disk, and offering Move would
            // promise to delete the source, which adding to an archive does not do.
            else if (ArchiveDropTarget(PaneUnder(e.OriginalSource as DependencyObject) ?? Pane,
                                       e.OriginalSource as DependencyObject, out _, out _))
                e.Effects = DragDropEffects.Copy;
            // Inside a read-only one - a tar, a tgz, a lone gzip - there is nowhere to put
            // anything. Without this the drop fell through to the SEARCH gesture below and
            // quietly piped the files into a search, which looks like the app doing something
            // random rather than refusing.
            else if (InArchive(PaneUnder(e.OriginalSource as DependencyObject) ?? Pane))
                e.Effects = DragDropEffects.None;
            else e.Effects = DragDropEffects.Link;            // search tab: scope or pipe, as before

            DragTrace($"Window_DragOver: OriginalSource={e.OriginalSource?.GetType().Name}, target={overTarget ?? "null"}, Effects={e.Effects}");
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
        /// pane's own folder instead.
        /// </summary>
        private string? DropTarget(DragEventArgs e)
        {
            var src = e.OriginalSource as DependencyObject;

            // The SIDEBAR is a drop target too: a folder in the tree and a saved place are both
            // "a folder you can see", and dropping onto one has to mean the same thing dropping
            // onto a folder ROW does. Neither used to be checked at all, so a drop on either
            // fell through to the focused pane's own folder and the files landed somewhere the
            // pointer was never over. Checked FIRST, because a tree node and a bookmark row are
            // more specific than "whatever pane this is inside".
            if (FolderNodeUnder(src) is { } node && Directory.Exists(node.Path)) return node.Path;
            if (BookmarkUnder(src) is { } bm && Directory.Exists(bm.Path)) return bm.Path;

            if (DataFor(ItemUnder(src)) is { } sr &&
                sr.IsDirectory && Directory.Exists(sr.FilePath))
                return sr.FilePath;

            var pane = PaneUnder(src) ?? Pane;
            return TargetFolder(pane);
        }

        /// <summary>The tree node a visual element sits in, or null. Walks to the TreeViewItem
        /// and reads its DataContext rather than hit-testing the model directly, so the row's
        /// icon, label and padding all count as the same target.</summary>
        private static FolderNode? FolderNodeUnder(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is TreeViewItem tvi) return tvi.DataContext as FolderNode;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        /// <summary>The saved place a visual element sits in, or null. The bookmark rows are
        /// plain Borders in a ListBox, so this reads the DataContext off whatever carries a
        /// Bookmark - the row template's own Border does.</summary>
        private static Bookmark? BookmarkUnder(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is FrameworkElement { DataContext: Bookmark b }) return b;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        /// <summary>True when the pointer is over a sidebar drop target - a tree node or a
        /// saved place. Used by the drag-over feedback and by the bookmarks drawer, which has
        /// to know whether a drop means "file operation" or "save this folder".</summary>
        internal static bool OverSidebarFolder(DependencyObject? d)
            => FolderNodeUnder(d) != null || BookmarkUnder(d) != null;

        /// <summary>True when a pane is browsing INSIDE an archive of any kind. Whether that
        /// archive can also be WRITTEN is a separate question, and ArchiveDropTarget asks it
        /// (Services/ArchiveWriter.cs) - so the two callers below fall back to this only for the
        /// read-only formats.</summary>
        private static bool InArchive(FilePane pane)
            => pane.Active is { IsBrowsing: true } t
               && Services.ArchiveProvider.TrySplit(t.CurrentFolder, out _, out _);

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
                // dropped into.
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
                // the one-drag-late bug: a dragged file disappeared and only reappeared after
                // the NEXT drag. Awaiting the drop's own refresh here means
                // it has already landed before the source pane's focus is touched at all.
                await DropOntoFolder(incoming, target, e.AllowedEffects, ctrl, shift);
                await RefreshSourcePaneIfStale(_dragSourcePane);
                return;
            }

            // A drop INTO a writable archive is a real add (ArchiveEdit.cs). Same pane rule as
            // the folder drop above: focus never moves during a drag, so the pane that was
            // dropped into has to be focused before anything reports a status or re-lists.
            var archivePane = PaneUnder(e.OriginalSource as DependencyObject) ?? Pane;
            if (ArchiveDropTarget(archivePane, e.OriginalSource as DependencyObject,
                                  out string dropArchive, out string dropFolder))
            {
                // No source-pane refresh: adding to an archive copies, so nothing left the
                // folder the files were dragged from and its listing is still correct.
                if (archivePane != Pane) FocusPane(archivePane);   // Panes.cs
                await ArchiveAdd(dropped, dropArchive, dropFolder);
                return;
            }

            // See Window_DragOver: a tar, a tgz or a lone gzip cannot be written by this build,
            // so say so rather than falling into the search gesture below.
            if (InArchive(archivePane))
            {
                SetTabStatusKey(_active, "Str_Status_ArchiveReadOnly");
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
            t.PipeArgs  = [files.Count.ToString("N0"), Loc("Str_Scope_Dropped"), string.Empty];
            t.PipeLabel = string.Format(Loc("Str_Pipe_Scope"), t.PipeArgs);
            t.Title     = string.Format(Loc("Str_Tab_Dropped"), files.Count.ToString("N0"));

            ActivateTab(t);
        }
    }
}
