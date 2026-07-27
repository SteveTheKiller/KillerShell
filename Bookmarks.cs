using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KillerShell
{
    // ═══════════════════════════════════════════════════════════
    //  FAVORITES  -  saved locations, in a slide-up under the tree
    // ═══════════════════════════════════════════════════════════
    // The Killculator arrangement from KillerNotes: docked in the row BELOW the tree, so the
    // tree shrinks and stays visible above it rather than being covered. Height animates 0 ->
    // open, so it rises out of the sidebar's bottom edge.
    //
    // Deliberately bare - no header, no close button. The rail star opens and closes it and the
    // rows are the only content, which is the whole point of a shortcut list.
    public sealed class Bookmark
    {
        public string Path { get; set; } = string.Empty;

        public string Name => MainWindow.IsThisPc(Path)             // Browse.cs
                            ? MainWindow.LocStatic("Str_Nav_ThisPc")
                            : System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n
                                ? n
                                : Path.TrimEnd('\\');   // a drive root has no file name part

        // This PC has no path for the shell to resolve, so it borrows the computer icon from
        // imageres. Everything else is a real folder and answers for itself.
        public ImageSource? Icon => MainWindow.IsThisPc(Path)
                                  ? Services.IconCache.ForComputer(16)
                                  : Services.IconCache.For(Path, 16, isDirectory: true);
    }

    public partial class MainWindow
    {
        private readonly ObservableCollection<Bookmark> _bookmarks = [];

        private bool _bookmarksOpen;

        // Where the drawer opens to. Not measured from content: the list scrolls inside, so a
        // natural height would jump every time an entry was added. Dragged instead, and kept.
        private double _bookmarksHeight = BookmarksHeightDefault;

        private const double BookmarksHeightDefault = 168;
        private const double BookmarksHeightMin     = 90;
        private const double BookmarksHeightMax     = 420;

        // What the tree above is never allowed to shrink below, whatever the drawer is dragged
        // to. Without this the drawer could swallow the sidebar on a short window.
        private const double TreeMinVisible = 120;

        // Paths are joined with a character that cannot appear in one, so no escaping is needed.
        private const char BookmarkSep = '|';

        private void InitBookmarks()
        {
            BookmarksList.ItemsSource = _bookmarks;

            string? saved = Services.ThemeManager.GetSetting("Bookmarks");

            // NULL means never configured, which is not the same as an EMPTY string. An empty
            // one means the user removed every favorite they had, and re-seeding then would
            // put back exactly what they just deleted, every launch.
            if (saved == null) SeedBookmarks();
            else
                foreach (string p in saved.Split([BookmarkSep], StringSplitOptions.RemoveEmptyEntries))
                {
                    // Somewhere that no longer exists is dropped rather than shown as a dead
                    // row - a favorite that cannot be opened is worse than one that quietly
                    // went away. This PC is not a directory, so it is exempt from that check.
                    if (IsThisPc(p) || Directory.Exists(p)) _bookmarks.Add(new Bookmark { Path = p });
                }

            // Invariant culture on the round trip, as with the tree width - a stored "168.5"
            // must not become unparseable under a comma decimal separator.
            string h = Services.ThemeManager.GetSetting("BookmarksHeight") ?? string.Empty;
            if (double.TryParse(h, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                _bookmarksHeight = ClampBookmarks(parsed);

            ApplyBookmarksPanel(animate: false);
            UpdateFavoriteStar();
        }

        // ── Resize ───────────────────────────────────────────────
        // Dragging UP grows the drawer, so the delta is subtracted: a Thumb reports downward
        // movement as positive.
        private void BookmarksGrip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            if (!_bookmarksOpen) return;

            double next = ClampBookmarks(BookmarksPanel.ActualHeight - e.VerticalChange);
            if (Math.Abs(next - _bookmarksHeight) < 0.5) return;

            _bookmarksHeight = next;

            // Straight to the height, no tween: an animation would lag the pointer, and the
            // open/close animation writes this same property.
            BookmarksPanel.BeginAnimation(FrameworkElement.HeightProperty, null);
            BookmarksPanel.Height = _bookmarksHeight;
        }

        private void BookmarksGrip_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
            => Services.ThemeManager.SetSetting("BookmarksHeight",
                   _bookmarksHeight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>
        /// Clamps to the fixed range, and additionally to whatever the sidebar can spare so the
        /// tree keeps at least a few rows visible on a short window.
        /// </summary>
        private double ClampBookmarks(double h)
        {
            double ceiling = BookmarksHeightMax;

            // TreePanel has no height before the first layout pass; fall back to the fixed max
            // rather than clamping everything to a negative ceiling on startup.
            if (TreePanel.ActualHeight > TreeMinVisible + BookmarksHeightMin)
                ceiling = Math.Min(ceiling, TreePanel.ActualHeight - TreeMinVisible);

            return Math.Max(BookmarksHeightMin, Math.Min(ceiling, h));
        }

        private void SaveBookmarks()
            => Services.ThemeManager.SetSetting("Bookmarks",
                   string.Join(BookmarkSep.ToString(), _bookmarks.Select(b => b.Path)));

        /// <summary>
        /// First run: This PC, then Home. Two entries rather than none, because an empty
        /// favorites drawer teaches nobody what the drawer is for, and these are the two
        /// places every file browser starts from.
        /// </summary>
        /// <remarks>
        /// Saved immediately, so the setting stops being null. That is what makes removing
        /// them stick: the next launch sees an empty string rather than a missing key and
        /// leaves the drawer alone.
        /// </remarks>
        private void SeedBookmarks()
        {
            _bookmarks.Add(new Bookmark { Path = ThisPc });        // Browse.cs
            if (Directory.Exists(HomeFolder))                      // AddressBar.cs
                _bookmarks.Add(new Bookmark { Path = HomeFolder });

            SaveBookmarks();
        }

        // ── Membership ───────────────────────────────────────────
        private bool IsBookmarked(string? path)
            => !string.IsNullOrEmpty(path)
            && _bookmarks.Any(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase));

        private void AddBookmark(string? path)
        {
            // This PC passes the existence check by exemption: it is a place you can navigate
            // to but not a directory on disk, so Directory.Exists says no and the star did
            // nothing at all when you clicked it there.
            if (string.IsNullOrEmpty(path)) return;
            if (!IsThisPc(path) && !Directory.Exists(path)) return;   // Browse.cs
            if (IsBookmarked(path)) return;

            _bookmarks.Add(new Bookmark { Path = path! });
            SaveBookmarks();
            UpdateFavoriteStar();
        }

        private void RemoveBookmark(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;

            var hit = _bookmarks.FirstOrDefault(
                b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return;

            _bookmarks.Remove(hit);
            SaveBookmarks();
            UpdateFavoriteStar();
        }

        // ── The star in the location bar ─────────────────────────
        // Browser convention: it reflects and toggles wherever you currently are. Filled when
        // this folder is saved, outline when not.
        internal void FavoriteStar_Click(object sender, RoutedEventArgs e)
        {
            // This PC is bookmarkable like any other place. It is not a folder, but it IS
            // somewhere you navigate to, and the sentinel round-trips through NavigateTo the
            // same as a path would (Browse.cs).
            string? here = _active.CurrentFolder;
            if (string.IsNullOrEmpty(here)) return;

            if (IsBookmarked(here)) RemoveBookmark(here);
            else                    AddBookmark(here);
        }

        /// <summary>
        /// Repoints the star at the active tab's folder. Called from navigation as well as from
        /// add/remove, since moving to a new folder changes the answer without touching the list.
        /// </summary>
        internal void UpdateFavoriteStar()
        {
            bool on = _active != null && _active.IsBrowsing && IsBookmarked(_active.CurrentFolder);

            // E735 filled, E734 outline.
            Pane.FavoriteStarBtn.Content = ((char)(on ? 0xE735 : 0xE734)).ToString();
            Pane.FavoriteStarBtn.Tag     = on ? "on" : null;
        }

        // ── The slide-up ─────────────────────────────────────────
        private void BookmarksBtn_Click(object sender, RoutedEventArgs e)
        {
            _bookmarksOpen = !_bookmarksOpen;

            // It lives inside the tree sidebar, so there is nowhere for it to appear while that
            // is collapsed. Opening it opens the sidebar with it.
            if (_bookmarksOpen && !_treeOpen) ToggleTreePanel();   // TreePanel.cs

            ApplyBookmarksPanel(animate: true);
        }

        /// <summary>
        /// Alt+1..9 and Alt+0 for the tenth. Out-of-range is a no-op rather than an error - the
        /// chord is reserved for a slot whether or not anything is saved in it yet.
        /// </summary>
        internal void JumpToBookmark(int oneBased)
        {
            if (oneBased < 1 || oneBased > _bookmarks.Count) return;
            _ = NavigateTo(_bookmarks[oneBased - 1].Path);   // Browse.cs
        }

        private void ApplyBookmarksPanel(bool animate)
        {
            BookmarksBtn.Tag = _bookmarksOpen ? "on" : null;

            // Re-clamped on every open: the window may have been resized while it was shut.
            if (_bookmarksOpen) _bookmarksHeight = ClampBookmarks(_bookmarksHeight);
            double target = _bookmarksOpen ? _bookmarksHeight : 0;

            if (_bookmarksOpen) BookmarksPanel.Visibility = Visibility.Visible;

            if (!animate)
            {
                BookmarksPanel.BeginAnimation(FrameworkElement.HeightProperty, null);
                BookmarksPanel.Height = target;
                if (!_bookmarksOpen) BookmarksPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var anim = new DoubleAnimation
            {
                From = BookmarksPanel.ActualHeight,
                To   = target,
                Duration = new Duration(TimeSpan.FromMilliseconds(160)),
                EasingFunction = new QuadraticEase
                    { EasingMode = _bookmarksOpen ? EasingMode.EaseOut : EasingMode.EaseIn },
            };
            anim.Completed += (_, _) =>
            {
                BookmarksPanel.BeginAnimation(FrameworkElement.HeightProperty, null);
                BookmarksPanel.Height = target;
                if (!_bookmarksOpen) BookmarksPanel.Visibility = Visibility.Collapsed;
            };
            BookmarksPanel.BeginAnimation(FrameworkElement.HeightProperty, anim);
        }

        // ── Rows ─────────────────────────────────────────────────
        private void Bookmark_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Bookmark b)
                _ = NavigateTo(b.Path);   // Browse.cs
        }

        // Right-click removes. The panel carries no buttons, so this is the only way out - and
        // it matches how the results list resolves a right-click to what is under the pointer.
        private void BookmarkRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Bookmark b)
                RemoveBookmark(b.Path);
        }

        // ── Drop ─────────────────────────────────────────────────
        // Folders dropped on the open panel are saved. Files are ignored rather than having
        // their parent saved: dropping a file here is far more likely to be a miss than an
        // instruction to bookmark whatever folder it happened to be in.
        private void BookmarksPanel_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DroppedFolders(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void BookmarksPanel_Drop(object sender, DragEventArgs e)
        {
            foreach (string f in DroppedFolders(e)) AddBookmark(f);
            e.Handled = true;
        }

        private static List<string> DroppedFolders(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return [];
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return [];
            return [.. paths.Where(Directory.Exists)];
        }
    }
}
