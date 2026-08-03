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
        private bool        _marqueeAdditive;

        internal void ResultsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _pressAt   = e.GetPosition(Pane.ResultsList);
            _marqueeOn = false;
            _dragArmed = false;
            _dragSeed  = null;

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
                _dragArmed = true;
                _dragSeed  = DataFor(item);
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

        // A freshly-realized container's DataContext binding is not guaranteed to have run by
        // the exact tick input events fire on it - the generator's own item/container map has
        // no such race (it is a plain dictionary the generator maintains synchronously the
        // moment the container exists), so it is asked first and DataContext is only a
        // fallback. This was the actual cause of "click first, then click and drag": the first
        // press on a row could see a real ListBoxItem (item != null) while its DataContext
        // still read null, so _dragSeed came back null and StartFileDrag silently did nothing
        // (Steve, 2026-08-04, confirmed via a [DragDiag] trace: item=found but seed=null).
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
            var paths = FilesForCommand(_dragSeed).Where(File.Exists).ToArray();
            System.Diagnostics.Debug.WriteLine($"[DragDiag] StartFileDrag: seed={_dragSeed?.FilePath ?? "null"}, resolvedPaths={paths.Length}");
            if (paths.Length == 0) return;

            // A real System.Windows.Forms.DataObject, not System.Windows.DataObject: the WPF one's
            // COM interop implementation is not a fully-functional native IDataObject, so the
            // shell's IDragSourceHelper cannot do its own bookkeeping on it and InitializeFromBitmap
            // fails outright (E_NOTIMPL) - a documented WPF gotcha, confirmed here by DragImage's
            // own trace. WinForms' DataObject is the real, complete native-COM-backed
            // implementation (already referenced - UseWindowsForms is on for the Task Manager tab's
            // WMI calls), and WPF's own DragDrop.DoDragDrop happily accepts one directly.
            var data = new System.Windows.Forms.DataObject();
            data.SetData(DataFormats.FileDrop, paths);          // what Explorer and mail clients read
            data.SetData(DataFormats.UnicodeText, string.Join(Environment.NewLine, paths));

            // The file's own icon at half opacity, following the cursor - the same thing Explorer
            // shows, and the whole reason a plain DoDragDrop reads as "just a cursor" (Steve,
            // 2026-08-04). One icon even for a multi-file drag: which file's icon to show for a
            // dozen mixed types is not worth guessing at, and Explorer itself falls back the same
            // way for a mixed selection.
            var dragIcon = Services.IconCache.For(paths[0], 48);
            Services.DragImage.Attach(data, dragIcon);

            // Copy AND Move offered: the drop target decides, so holding Shift while dropping
            // into Explorer moves the files instead of copying them. Offering Copy alone made
            // KillerShell the one place a Shift-drag silently did the wrong thing.
            try
            {
                var finalEffect = DragDrop.DoDragDrop(Pane.ResultsList, data, DragDropEffects.Copy | DragDropEffects.Move);
                System.Diagnostics.Debug.WriteLine($"[DragDiag] DoDragDrop returned: {finalEffect}");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DragDiag] DoDragDrop THREW: {ex}"); }
        }

        // ── Accepting a drop ─────────────────────────────────────
        // Folders set the scope, files become a piped list to search inside. Both are things you
        // would otherwise reach through the picker or a second search, so dropping is a shortcut
        // rather than a new capability.
        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                System.Diagnostics.Debug.WriteLine("[DragDiag] Window_DragOver: no FileDrop data present - Effects=None");
                e.Effects = DragDropEffects.None;
                e.Handled = true;
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
        /// pane's own folder instead (Steve, 2026-08-04).
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

        private void Window_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            System.Diagnostics.Debug.WriteLine($"[DragDiag] Window_Drop: fired, OriginalSource={e.OriginalSource?.GetType().Name}");
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
                // dropped into (Steve, 2026-08-04).
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
                DropOntoFolder(incoming, target, e.AllowedEffects, ctrl, shift);
                return;
            }

            var folders = dropped.Where(Directory.Exists).ToList();
            var files   = dropped.Where(File.Exists).ToList();

            // Files win when both are present: dropping a mixed bag reads as "search these",
            // and a folder in that bag is ambiguous rather than useful.
            if (files.Count > 0) { PipeDroppedFiles(files); return; }
            if (folders.Count > 0) ScopeToFolder(folders[0]);
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
