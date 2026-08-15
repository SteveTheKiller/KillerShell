using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KillerShell.Models;   // SearchTab - CanShowListing tests what kind of tab is active

namespace KillerShell.Shell
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

        // The drawer ALWAYS sizes itself to its content (up to BookmarksCeiling): the resize
        // grip and its remembered user height were removed 2026-08-15 - the clamp made the grip
        // undraggable in practice, and a drawer that just fits its places (returning the rest of
        // the sidebar to the tree) is the behavior the grip's auto-fit default already had.
        private const double BookmarksHeightMin     = 90;
        // 420 was a hard stop the drawer hit long before it ran out of places to show, so a
        // longer list could never be opened far enough to see (2026-08-09). The REAL limit is
        // the one below it - the sidebar has to keep TreeMinVisible pixels of tree - so this
        // only needs to be high enough to stay out of the way on a tall window; the tree's own
        // floor is what actually protects the layout.
        private const double BookmarksHeightMax     = 2000;

        // Exact row height from the item template: Border Padding="4,3" (6 total) around a
        // 16px icon, which is taller than the 11px text line beside it. Fixed rather than
        // measured live - a live ActualHeight/Measure() read depends on a layout pass having
        // already happened, and how far along it is varies by call site (startup, a drag tick,
        // right after a reorder), which is what left a sliver of extra scrollable space under
        // the last row no matter how the measurement was taken. A constant sidesteps the timing
        // question entirely; it only needs revisiting if the row template's own metrics change.
        // 18 = a 16px icon inside the row template's 4,1 padding. It was 22 for a 4,3 padding;
        // the two MUST agree, because this is what the open animation and the ceiling are
        // computed from - a stale value here means the drawer opens at the wrong height and
        // only converges once CorrectBookmarksOverflow measures the real rows.
        private const double BookmarksRowHeight = 18;

        // What the tree above is never allowed to shrink below, whatever the drawer is dragged
        // to. Without this the drawer could swallow the sidebar on a short window.
        private const double TreeMinVisible = 120;

        // Paths are joined with a character that cannot appear in one, so no escaping is needed.
        private const char BookmarkSep = '|';

        // BookmarksPanel is a Border with a 1px top hairline (BorderThickness="0,1,0,0"), which
        // eats into its own content area - the ListBox inside gets exactly this much LESS room
        // than BookmarksPanel's own Height. Every conversion from "what the list's rows need"
        // to "what to set BookmarksPanel.Height to" has to add it back, or the list ends up a
        // pixel short of its content and a genuine, if tiny, sliver of scrollbar shows even once
        // everything else lines up exactly.
        private double BookmarksBorderExtra
            => BookmarksPanel.BorderThickness.Top + BookmarksPanel.BorderThickness.Bottom;

        private void InitBookmarks()
        {
            BookmarksList.ItemsSource = _bookmarks;

            // Both edge fades follow the scroll position, the same as the tree's
            // (TreePanel.cs). ScrollChanged is handled at the ListBox rather than dug out of
            // its template, since it bubbles. SizeChanged covers the drawer's own open/close
            // animation - it opens from a height of 0, so without this the fades would pop in
            // at whatever opacity the LAST open left them at instead of ramping with the slide.
            BookmarksList.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
                new System.Windows.Controls.ScrollChangedEventHandler((_, _) => SyncBookmarksEdgeFades()));
            BookmarksList.SizeChanged  += (_, _) =>
                { SyncBookmarksEdgeFades(); CorrectBookmarksOverflow(); SyncBookmarksScrollbar(); };
            BookmarksPanel.SizeChanged += (_, _) => { SyncBookmarksEdgeFades(); SyncBookmarksScrollbar(); };

            // --demo gets its own fixed set of places and never reads or writes the real one
            // (Shell/DemoMode.cs). Two separate reasons, both of which the saved-list path below
            // would break: the fabricated places are not on disk, so the Directory.Exists check
            // below would drop every one of them, and loading the real list would put the actual
            // machine's folder names in a marketing screenshot - which is the whole thing demo
            // mode exists to prevent. The drawer is opened too, because a shut drawer shows none
            // of it. Returns here rather than falling through: the remembered height and
            // user-sized flag belong to the real list, and a height dragged small for a two-row
            // list would hide most of a fifteen-row one.
            if (DemoMode)
            {
                SeedDemoBookmarks();
                _bookmarksOpen = true;
                ApplyBookmarksPanel(animate: false);
                UpdateFavoriteStar();
                return;
            }

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

            // Whether the drawer itself was left open - same "1"/not-"1" convention as
            // ShowHidden/FoldersTop/DetailsPaneOpen, so a drawer left open survives a restart
            // instead of always coming back shut.
            _bookmarksOpen = Services.ThemeManager.GetSetting("BookmarksOpen") == "1";

            // BookmarksHeight / BookmarksUserSized are deliberately no longer read: the drawer
            // auto-fits its content now (see the note at BookmarksHeightMin). Stale values a
            // pre-grip-removal build saved are simply ignored.

            ApplyBookmarksPanel(animate: false);
            UpdateFavoriteStar();
        }

        // How much room the sidebar can spare for the drawer, whatever its content needs -
        // the fixed max, or less on a short window so the tree keeps a few rows visible.
        // Content is NOT folded in here; CorrectBookmarksOverflow is what makes the drawer fit
        // its content exactly, from a real measurement rather than anything computed here.
        private double BookmarksCeiling()
        {
            double ceiling = BookmarksHeightMax;

            // TreePanel has no height before the first layout pass; fall back to the fixed max
            // rather than clamping everything to a negative ceiling on startup.
            if (TreePanel.ActualHeight > TreeMinVisible + BookmarksHeightMin)
                ceiling = Math.Min(ceiling, TreePanel.ActualHeight - TreeMinVisible);

            return ceiling;
        }

        /// <summary>
        /// Fade each edge only while there is something PAST it - none at the very top, none
        /// at the very bottom, both ramped over the fade's own height. Same treatment and same
        /// reasoning as TreePanel.SyncTreeEdgeFades. Uses the ScrollViewer's own numbers - the
        /// fade only needs to be roughly right while actively scrolling, not exact.
        /// </summary>
        private void SyncBookmarksEdgeFades()
        {
            var sv = FindDescendant<System.Windows.Controls.ScrollViewer>(BookmarksList);
            if (sv == null) return;

            // The fades are the list's own OpacityMask stops now (MainWindow.xaml,
            // BmFade*): the outer stop's ALPHA drops toward transparent as rows slide past
            // that edge, and the inner offsets track the list's live height so the band stays
            // roughly 18/22px whatever the drawer's height is. Same ramp inputs as before.
            double top    = Ramp(sv.VerticalOffset, 18, 18);
            double bottom = Ramp(sv.ExtentHeight - sv.ViewportHeight - sv.VerticalOffset, 22, 22);

            BmFadeTopOuter.Color = System.Windows.Media.Color.FromArgb(
                (byte)(255 - (int)(top * 255)), 0, 0, 0);
            BmFadeBotOuter.Color = System.Windows.Media.Color.FromArgb(
                (byte)(255 - (int)(bottom * 255)), 0, 0, 0);

            double h = BookmarksList.ActualHeight;
            if (h > 40)
            {
                BmFadeTopInner.Offset = 18.0 / h;
                BmFadeBotInner.Offset = 1.0 - 22.0 / h;
            }
        }

        /// <summary>
        /// Shows or hides the scrollbar from the SAME real per-row measurement
        /// CorrectBookmarksOverflow uses, compared straight against BookmarksList's own
        /// ActualHeight (the viewport) - not against the ScrollViewer's ScrollableHeight, and
        /// not through ComputedVerticalScrollBarVisibility/Auto. Both of those route through
        /// the ScrollViewer's own extent bookkeeping, and something in that bookkeeping was
        /// still showing a scrollbar with real drag room even once the drawer's own height had
        /// converged on the measured content - i.e. two numbers that should have agreed were
        /// not agreeing. Comparing my own measurement to my own viewport number here means the
        /// visibility decision no longer depends on that bookkeeping being right at all.
        /// </summary>
        private void SyncBookmarksScrollbar()
        {
            if (!TryMeasureBookmarksContent(out double real)) return;

            var sv = FindDescendant<System.Windows.Controls.ScrollViewer>(BookmarksList);
            if (sv == null) return;

            if (FindVerticalBar(sv) is { } bar)
                bar.Visibility = BookmarksList.ActualHeight < real - 0.5
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Blocks the wheel outright when the drawer already shows every row - it should not
        /// move at all in that state (2026-08-02). SyncBookmarksScrollbar hides the bar
        /// in this case, but hiding the bar was never enough on its own: the ScrollViewer's own
        /// ExtentHeight comes from ITS layout of the rows, not from BookmarksPanel.Height, and
        /// CorrectBookmarksOverflow's convergence leaves a sub-pixel gap between the two (the
        /// same rounding BookmarksBorderExtra/Math.Ceiling exist to paper over elsewhere). That
        /// residual was real ScrollableHeight the ScrollViewer was still happy to spend on a
        /// wheel notch even with no visible bar and nowhere sensible for the content to go -
        /// the rows would shift a pixel and immediately hit the far end, reading as a "wiggle".
        /// Uses the exact same real-vs-viewport comparison as SyncBookmarksScrollbar so the two
        /// decisions can never disagree.
        /// </summary>
        private void BookmarksList_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (!TryMeasureBookmarksContent(out double real)) return;
            if (BookmarksList.ActualHeight >= real - 0.5) e.Handled = true;
        }

        // Same technique as TreePanel.FindHorizontalBar, the other orientation: a ScrollViewer
        // hosts two ScrollBars, so the search has to check which one it found.
        private static System.Windows.Controls.Primitives.ScrollBar? FindVerticalBar(DependencyObject root)
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (c is System.Windows.Controls.Primitives.ScrollBar b
                    && b.Orientation == System.Windows.Controls.Orientation.Vertical) return b;

                var deeper = FindVerticalBar(c);
                if (deeper != null) return deeper;
            }
            return null;
        }

        /// <summary>
        /// The height the list actually needs to show every row with nothing left over. Used
        /// as an ESTIMATE only, before anything has laid out (startup, mid-drag) - see
        /// CorrectBookmarksOverflow for the exact, measured version that runs once real rows
        /// exist and squares away whatever this guessed wrong.
        /// </summary>
        private double BookmarksContentHeight()
            => _bookmarks.Count == 0
             ? 0
             : BookmarksRowHeight * _bookmarks.Count
             + BookmarksList.Padding.Top + BookmarksList.Padding.Bottom;

        /// <summary>
        /// Sums the ACTUAL rendered height of every bookmark row, straight from their
        /// containers - not an estimate, not a pre-layout Measure() call. False means not every
        /// row has rendered yet (e.g. mid open-animation, before the panel has grown enough to
        /// lay them all out); callers should just wait for the next SizeChanged pass.
        /// </summary>
        private bool TryMeasureBookmarksContent(out double real)
        {
            real = 0;
            if (_bookmarks.Count == 0) return false;

            foreach (var b in _bookmarks)
            {
                if (BookmarkContainer(b) is not { ActualHeight: > 0 } row) { real = 0; return false; }
                real += row.ActualHeight;
            }
            real += BookmarksList.Padding.Top + BookmarksList.Padding.Bottom;

            // Rounded UP rather than left as a fractional pixel value - a panel even a hair
            // SHORTER than its content leaves a hair of real scrollable room. Rounding up
            // guarantees nothing measured against this ever comes out short.
            real = Math.Ceiling(real);
            return true;
        }

        /// <summary>
        /// Converges the drawer onto the EXACT height its rows measured out at, once they have
        /// actually rendered. Driven off SizeChanged rather than the open animation's Completed
        /// event on purpose - Completed firing is not guaranteed (a second toggle before the
        /// tween finishes cancels it without ever raising it), where SizeChanged fires on every
        /// layout pass the animation itself produces, so this keeps catching up regardless of
        /// whether the tween ever "finished" in that sense. ApplyBookmarksPanel's target height
        /// is only ever an ESTIMATE for this to animate toward; this is what turns it exact.
        /// </summary>
        private void CorrectBookmarksOverflow()
        {
            if (!_bookmarksOpen || !TryMeasureBookmarksContent(out double listNeeds)) return;

            // +BookmarksBorderExtra: listNeeds is what the LIST needs; BookmarksPanel.Height is
            // the OUTER border, which has its own 1px top hairline eating into that same space.
            // Without adding it back, the list ends up exactly 1px short of its own content -
            // small, but a real, permanently-draggable sliver of scrollbar, not a rounding
            // artifact that a tolerance should be swallowing.
            double panelNeeds = listNeeds + BookmarksBorderExtra;

            // Free to move either way - the estimate ApplyBookmarksPanel animates toward can
            // land short as easily as long, and a drawer that settled too SHORT would clip the
            // last row rather than show extra space, which is just as wrong.
            // Undershoot is never tolerated, even by a fraction of a pixel: a panel a hair
            // SHORT of its content clips the bottom sliver of the last row against the
            // ScrollViewer's own extent, which is exactly what read as the bottom bookmark
            // being cut off (2026-08-02) - the hover highlight's bottom edge and
            // rounded corners were the only thing making a sub-pixel gap visible. Overshoot
            // keeps a small tolerance so this does not fight the sub-pixel jitter a
            // DoubleAnimation leaves behind once it lands on its target.
            double diff = BookmarksPanel.ActualHeight - panelNeeds;
            if (diff <= 0.5 && diff >= 0) return;

            BookmarksPanel.BeginAnimation(FrameworkElement.HeightProperty, null);
            BookmarksPanel.Height = panelNeeds;
        }

        /// <summary>
        /// Persists the list. A no-op in --demo: the drawer is holding fabricated places there,
        /// and writing them would overwrite the real saved list with paths that do not exist, so
        /// the next ordinary launch would come up with an empty drawer. Guarded here rather than
        /// at each call site so adding, removing and reordering a demo place are all covered by
        /// one check - the rows still respond, the change just does not outlive the process.
        /// </summary>
        private void SaveBookmarks()
        {
            if (DemoMode) return;
            Services.ThemeManager.SetSetting("Bookmarks",
                string.Join(BookmarkSep.ToString(), _bookmarks.Select(b => b.Path)));
        }

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

        /// <summary>
        /// --demo's places: one per icon in the brand pack, so a capture of the drawer shows the
        /// whole set of folder art at once.
        /// </summary>
        /// <remarks>
        /// The drawer is the only surface in the app that shows fifteen folder icons stacked in a
        /// column, which makes it the one place the icon pack can actually be photographed. Every
        /// name below is a real Windows shell folder and every path exists on the fabricated
        /// machine (Services/DemoFileSystem.cs), which is what makes them resolve to their own art
        /// rather than the generic folder - see DemoFs.ArtFor. Clicking any of them browses to a
        /// listing that is really there, so the drawer is not a row of dead rows either.
        ///
        /// No Directory.Exists filter, unlike the saved-list path: none of these are on disk, and
        /// that check is exactly what would empty the drawer. No SaveBookmarks either - see the
        /// guard on it.
        ///
        /// Ordered the way a person would keep them rather than by icon: This PC and Home first,
        /// then the profile's own folders, then the machine-wide ones, the field drive, and one
        /// ordinary working folder last to show what a plain folder looks like beside the rest.
        /// </remarks>
        private void SeedDemoBookmarks()
        {
            void Place(string path) => _bookmarks.Add(new Bookmark { Path = path });

            Place(ThisPc);                                 // my_pc_icon
            Place(@"C:\Users\Demo");                       // home_folder_icon
            Place(@"C:\Users\Demo\Desktop");               // desktop_folder_icon
            Place(@"C:\Users\Demo\Documents");             // documents_folder_icon
            Place(@"C:\Users\Demo\Downloads");             // downloads_folder_icon
            Place(@"C:\Users\Demo\Pictures");              // pictures_folder_icon
            Place(@"C:\Users\Demo\Music");                 // music_folder_icon
            Place(@"C:\Users\Demo\Videos");                // videos_folder_icon
            Place(@"C:\Users\Demo\Favorites");             // favorites_folder_icon
            Place(@"C:\Users\Demo\Searches");              // search_results_icon
            Place(@"C:\Users\Demo\Recent");                // recents_icon
            Place(@"C:\Program Files");                    // program_files_icon
            Place(@"C:\Windows");                          // windows_folder_icon
            Place(@"D:\");                                 // drive_icon
            Place(@"C:\Users\Demo\code\killer-scripts");   // folder_icon, the generic one
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
            // A fabricated folder is exempt for the same reason This PC is: it is somewhere you
            // can navigate to that Directory.Exists says no about, so without this the star was a
            // dead control everywhere in --demo, on the one screen whose saved places are the
            // thing being demonstrated.
            if (string.IsNullOrEmpty(path)) return;
            if (!IsThisPc(path) && !Directory.Exists(path)
                && !(DemoMode && Services.DemoFs.Has(path!))) return; // Browse.cs, DemoFileSystem.cs
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

            UpdateBookmarksSelection();
        }

        /// <summary>
        /// Highlights whichever bookmark row matches where the active tab actually is, the same
        /// "reflects wherever you currently are" rule the star follows - riding the star's own
        /// refresh points rather than a call site of its own, so the two can never drift apart.
        /// </summary>
        internal void UpdateBookmarksSelection()
        {
            string? here = _active != null && _active.IsBrowsing ? _active.CurrentFolder : null;

            foreach (var b in _bookmarks)
            {
                bool on = !string.IsNullOrEmpty(here)
                    && string.Equals(b.Path, here, StringComparison.OrdinalIgnoreCase);

                if (BookmarkContainer(b) is { } container
                    && FindDescendant<System.Windows.Controls.Border>(container) is { } row)
                    row.Tag = on ? "on" : null;
            }
        }

        // ── The slide-up ─────────────────────────────────────────
        private void BookmarksBtn_Click(object sender, RoutedEventArgs e)
        {
            _bookmarksOpen = !_bookmarksOpen;
            Services.ThemeManager.SetSetting("BookmarksOpen", _bookmarksOpen ? "1" : "0");

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

            // The ceiling (sidebar space, fixed max) is what stops the drawer from growing past
            // what the tree can spare.
            BookmarksPanel.MaxHeight = _bookmarksOpen ? BookmarksCeiling() : double.PositiveInfinity;

            // The estimate. CorrectBookmarksOverflow (SizeChanged-driven, so it fires on every
            // layout pass this animation produces, not just once the animation's own Completed
            // event happens to fire) is what turns this into an exact number afterward - it
            // does not depend on this guess being right, only close enough to animate toward.
            // Opening always shows everything that fits the ceiling; a list too long for the
            // sidebar scrolls inside it.
            double fit = BookmarksContentHeight() + BookmarksBorderExtra;
            double target = !_bookmarksOpen ? 0 : Math.Min(fit, BookmarksPanel.MaxHeight);

            if (_bookmarksOpen) BookmarksPanel.Visibility = Visibility.Visible;

            void Settle()
            {
                BookmarksPanel.BeginAnimation(FrameworkElement.HeightProperty, null);
                BookmarksPanel.Height = target;
                if (!_bookmarksOpen) BookmarksPanel.Visibility = Visibility.Collapsed;
            }

            if (!animate) { Settle(); return; }

            var anim = new DoubleAnimation
            {
                From = BookmarksPanel.ActualHeight,
                To   = target,
                Duration = new Duration(TimeSpan.FromMilliseconds(160)),
                EasingFunction = new QuadraticEase
                    { EasingMode = _bookmarksOpen ? EasingMode.EaseOut : EasingMode.EaseIn },
            };
            anim.Completed += (_, _) => Settle();
            BookmarksPanel.BeginAnimation(FrameworkElement.HeightProperty, anim);
        }

        // ── Rows ─────────────────────────────────────────────────
        /// <summary>
        /// Where a click (as opposed to a drag - see Bookmark_DragUp) actually goes.
        /// </summary>
        /// <remarks>
        /// A terminal tab cannot be navigated - there is no channel to push a directory change
        /// into a running shell, so NavigateTo would only rewrite the tab's own bookkeeping
        /// (CurrentFolder/IsBrowsing) out from under it: the menubar would claim the folder
        /// changed while the shell itself never moved. An editor tab has the same problem from
        /// the other direction - NavigateTo would stomp its CurrentFolder/IsBrowsing bookkeeping
        /// while a document is open in it, which is what issue #4 reported (bookmark opened "in
        /// the style of tab that was focused" instead of the file browser). So when the focused
        /// tab is either one, a bookmark opens a new browse tab instead of hijacking it.
        /// </remarks>
        private void OpenBookmark(Bookmark b) => GoToFolder(b.Path);

        /// <summary>
        /// Show a folder, making somewhere to show it if the current tab cannot. The one route
        /// every sidebar surface uses - a saved place, a tree node - so they can never disagree
        /// about what a click does.
        /// </summary>
        /// <remarks>
        /// The test is "can this tab browse", NOT a list of tab kinds. It used to name Terminal
        /// and Editor explicitly, so the four TOOL tabs added since (Processes, Event Viewer,
        /// Performance, Registry Editor - and now Storage Analyzer) fell through it: with one of
        /// those open, clicking a saved place or a tree node navigated a tab that shows no
        /// listing, and nothing appeared to happen at all (2026-08-09). Anything that is not an
        /// ordinary browse or search tab gets a NEW tab, which is also what the user meant -
        /// they asked to see a folder, not to close what they were looking at.
        /// </remarks>
        internal void GoToFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (_active == null || !CanShowListing(_active))
            {
                // Only when there IS an outgoing tab. This branch is reached two ways - a tab that
                // cannot show a listing, or no tab at all - and in the second case there is
                // nothing to capture; CaptureTab takes a non-null SearchTab (CS8604).
                if (_active != null) CaptureTab(_active);   // Tabs.cs - outgoing tab keeps its state
                ActivateTab(CreateTab());
            }
            _ = NavigateTo(path);           // Browse.cs
        }

        /// <summary>True when a tab is an ordinary browse/search tab, so a folder can be shown
        /// IN it rather than needing a new one.</summary>
        private static bool CanShowListing(SearchTab t)
            => !t.IsTerminal && !t.IsEditor && !t.IsProcessList && !t.IsEventViewer
            && !t.IsPerformanceMonitor && !t.IsRegistryEditor && !t.IsStorageAnalyzer;

        private void BookmarkTerminal_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Bookmark b)
                OpenShell(Terminal.TerminalProfile.PowerShell(elevated: false), b.Path);   // TerminalTabs.cs
        }

        private void BookmarkTerminalAdmin_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Bookmark b)
                OpenShell(Terminal.TerminalProfile.PowerShell(elevated: true), b.Path);   // TerminalTabs.cs
        }

        private void BookmarkAnalyze_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Bookmark b)
                AnalyzeFolder(b.Path);   // StorageTabs.cs
        }

        private void BookmarkProperties_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not Bookmark b) return;
            if (!Directory.Exists(b.Path)) return;   // This PC is a sentinel, not a folder

            // Deferred until the menu has closed, the same as the tree's own Properties row -
            // the shell dialog is modal and opening it under a menu that is still dismissing
            // leaves the menu painted on top of it (ResultsMenu.cs).
            AfterMenuCloses(() =>
            {
                if (!Services.ShellContextMenu.ShowProperties(b.Path))
                    SetTabStatusKey(_active, "Str_Status_ShellFailed");
            });
        }

        // Remove. The panel carries no buttons of its own, so the row's context menu is the
        // only way out - matches how the results list resolves a right-click to what is under
        // the pointer.
        private void BookmarkRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Bookmark b)
                RemoveBookmark(b.Path);
        }

        // ── Reorder (drag) ──────────────────────────────────────────
        // Same mouse-capture + midpoint-crossing technique as the tab strip (Tabs.cs), turned
        // through 90 degrees for a vertical list. No render-transform slide animation - this is
        // a short list of small rows, and an immediate swap reads fine at that scale.
        private Bookmark? _bmDragBookmark;
        private bool      _bmDragging;
        private Point     _bmDragStart;

        internal void Bookmark_DragDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not Bookmark b) return;
            _bmDragBookmark = b;
            _bmDragStart    = e.GetPosition(BookmarksList);
            _bmDragging     = false;
            fe.CaptureMouse();

            // Without this the event keeps tunnelling/bubbling into ListBoxItem's own
            // MouseLeftButtonDown handling, which tries to capture the mouse for its own
            // click-tracking and steals the capture straight back out from under the drag.
            e.Handled = true;
        }

        internal void Bookmark_DragMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is not FrameworkElement fe || !fe.IsMouseCaptured || _bmDragBookmark is null) return;

            var pos = e.GetPosition(BookmarksList);
            if (!_bmDragging && Math.Abs(pos.Y - _bmDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _bmDragging = true;

            int cur = _bookmarks.IndexOf(_bmDragBookmark);
            if (cur < 0) return;

            if (cur + 1 < _bookmarks.Count && BookmarkContainer(_bookmarks[cur + 1]) is { } below
                && pos.Y > MidYInList(below))
            {
                _bookmarks.Move(cur + 1, cur);
            }
            else if (cur - 1 >= 0 && BookmarkContainer(_bookmarks[cur - 1]) is { } above
                && pos.Y < MidYInList(above))
            {
                _bookmarks.Move(cur - 1, cur);
            }
        }

        internal void Bookmark_DragUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.IsMouseCaptured) fe.ReleaseMouseCapture();

            bool wasDragging = _bmDragging;
            var  b = _bmDragBookmark;
            _bmDragBookmark = null;
            _bmDragging     = false;

            if (wasDragging) { SaveBookmarks(); return; }   // reordered - persist, do not navigate

            if (b != null) OpenBookmark(b);
        }

        private FrameworkElement? BookmarkContainer(Bookmark b)
            => BookmarksList.ItemContainerGenerator.ContainerFromItem(b) as FrameworkElement;

        // TranslatePoint rather than LayoutInformation.GetLayoutSlot: the row sits inside the
        // ListBox's own ScrollViewer/panel chain, and TranslatePoint walks that whole visual
        // tree to give a midpoint in BookmarksList's OWN coordinate space - the same space
        // e.GetPosition(BookmarksList) reports in - so the two compare correctly regardless of
        // the ListBox's padding or an internal panel offset.
        private double MidYInList(FrameworkElement fe)
            => fe.TranslatePoint(new Point(0, fe.ActualHeight / 2), BookmarksList).Y;

        // ── Drop ─────────────────────────────────────────────────
        // Folders dropped on the open panel are saved. Files are ignored rather than having
        // their parent saved: dropping a file here is far more likely to be a miss than an
        // instruction to bookmark whatever folder it happened to be in.
        // A drop ON A SAVED PLACE means the file operation - copy or move the payload INTO that
        // folder, exactly as dropping on a folder row in the listing does. Only a drop on the
        // drawer's EMPTY space still means "save this folder as a place". Both readings are
        // natural and they never collide, because one is over a row and the other is not.
        // Leaving e.Handled false is what hands the row case to the window's own drop pipeline
        // (ResultsInteraction.cs Window_DragOver / Window_Drop -> DropTarget), so the effects,
        // the conflict prompts, the drop image and the post-drop refresh are the SAME code the
        // listing uses rather than a second implementation living here.
        private void BookmarksPanel_DragOver(object sender, DragEventArgs e)
        {
            if (OverSidebarFolder(e.OriginalSource as DependencyObject)) return;   // ResultsInteraction.cs

            e.Effects = DroppedFolders(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void BookmarksPanel_Drop(object sender, DragEventArgs e)
        {
            if (OverSidebarFolder(e.OriginalSource as DependencyObject)) return;

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
