using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShell.Models;

using KillerShell.Shell;

namespace KillerShell
{
    // Code-behind for one results pane (FilePane.xaml).
    //
    // The pane owns MARKUP AND NOTHING ELSE. Every handler below is a one-line forward to the
    // window, because the logic is the same whichever pane raised it and duplicating any of it
    // here is exactly what the extraction was for.
    //
    // The forwarders exist because WPF resolves a handler name against the class that declares
    // the XAML. Moving the markup out of MainWindow.xaml therefore moved every Click and
    // MouseDown with it, and the only way back to the window's handlers is through this file.
    // MainWindow's handlers are `internal` rather than `private` for the same reason.
    public partial class FilePane : UserControl
    {
        public FilePane()
        {
            InitializeComponent();

            // Clicking anywhere in a pane makes it the one every command acts on. Preview, so
            // it lands before the click is handled by whatever was actually hit - a button in
            // this pane must not run against the other pane's tab.
            PreviewMouseDown += (_, _) => Owner.FocusPane(this);
        }

        // Resolved on first use, not in the constructor: the pane is built during the window's
        // InitializeComponent, before there is a window to find. Every handler runs long after
        // load, so by the time any of them fires the tree is up.
        private MainWindow? _owner;
        internal MainWindow Owner => _owner ??= (MainWindow)Window.GetWindow(this)!;

        // ── Per-pane tab state ───────────────────────────────────
        // A tab belongs to a PANE, not to the window: two panes each show their own strip and
        // their own active search. These used to be MainWindow fields; the window now reaches
        // them through Panes.cs, which resolves to whichever pane has focus. Moving the state
        // here is what makes the second pane a layout change rather than a rewrite.
        internal ObservableCollection<SearchTab> Tabs { get; } = [];

        internal SearchTab Active { get; set; } = null!;   // set before anything reads it

        /// <summary>
        /// Index of the leftmost tab currently in the strip. 0 whenever they all fit.
        /// </summary>
        /// <remarks>
        /// Per pane, like the tabs themselves: the two strips are different widths and hold
        /// different numbers of tabs, so one window index would have each pane scrolling the
        /// other. Owned here rather than in Tabs.cs for the same reason every other bit of
        /// per-pane state is (see ApplyTabWindow).
        /// </remarks>
        internal int TabWindow { get; set; }

        /// <summary>
        /// This pane's location row is collapsed (F10, MenuBar.cs). Per pane and not per tab:
        /// the row is pane chrome, and hiding it per tab would make it jump on every switch.
        /// </summary>
        internal bool MenuBarHidden { get; set; }

        // ── Tab strip ────────────────────────────────────────────
        // These two take the PANE rather than (sender, args): both act on this pane's own strip,
        // and the window's usual "act on whichever pane has focus" is a guess here - the band can
        // be resized by a splitter drag that never touched it.
        private void TabBar_SizeChanged(object s, SizeChangedEventArgs e)  => Owner.TabBarResized(this);
        private void TabOverflow_Click(object s, RoutedEventArgs e)        => Owner.TabOverflowMenu(this);

        // ── Navigation + address bar ─────────────────────────────
        private void NavBack_Click(object s, RoutedEventArgs e)            => Owner.NavBack_Click(s, e);
        private void NavForward_Click(object s, RoutedEventArgs e)         => Owner.NavForward_Click(s, e);
        private void NavUp_Click(object s, RoutedEventArgs e)              => Owner.NavUp_Click(s, e);
        private void ScopeBar_Click(object s, MouseButtonEventArgs e)      => Owner.ScopeBar_Click(s, e);
        private void AddressBox_KeyDown(object s, KeyEventArgs e)          => Owner.AddressBox_KeyDown(s, e);
        private void AddressBox_LostFocus(object s, RoutedEventArgs e)     => Owner.AddressBox_LostFocus(s, e);

        // ── View mode, sort, view options ────────────────────────
        private void ViewList_Click(object s, RoutedEventArgs e)           => Owner.ViewList_Click(s, e);
        private void ViewIcons_Click(object s, RoutedEventArgs e)          => Owner.ViewIcons_Click(s, e);
        private void ViewDetails_Click(object s, RoutedEventArgs e)        => Owner.ViewDetails_Click(s, e);
        private void SortMenu_Click(object s, RoutedEventArgs e)           => Owner.SortMenu_Click(s, e);
        private void SortItem_Click(object s, RoutedEventArgs e)           => Owner.SortItem_Click(s, e);
        private void SortDir_Click(object s, RoutedEventArgs e)            => Owner.SortDir_Click(s, e);
        private void ShowHidden_Click(object s, RoutedEventArgs e)         => Owner.ShowHidden_Click(s, e);
        private void FoldersTop_Click(object s, RoutedEventArgs e)         => Owner.FoldersTop_Click(s, e);
        private void ExpandAll_Click(object s, RoutedEventArgs e)          => Owner.ExpandAll_Click(s, e);
        private void FavoriteStar_Click(object s, RoutedEventArgs e)      => Owner.FavoriteStar_Click(s, e);

        // ── Results context menu: shells ─────────────────────────
        private void MenuTerminal_Click(object s, RoutedEventArgs e)       => Owner.MenuTerminal_Click(s, e);
        private void MenuTerminalAdmin_Click(object s, RoutedEventArgs e)  => Owner.MenuTerminalAdmin_Click(s, e);

        // ── Details-view column headers ──────────────────────────
        private void ColName_Click(object s, RoutedEventArgs e)            => Owner.ColName_Click(s, e);
        private void ColFolder_Click(object s, RoutedEventArgs e)          => Owner.ColFolder_Click(s, e);
        private void ColSize_Click(object s, RoutedEventArgs e)            => Owner.ColSize_Click(s, e);
        private void ColModified_Click(object s, RoutedEventArgs e)        => Owner.ColModified_Click(s, e);
        private void ColGrip_MouseDown(object s, MouseButtonEventArgs e)   => Owner.ColGrip_MouseDown(s, e);
        private void ColGrip_MouseMove(object s, MouseEventArgs e)         => Owner.ColGrip_MouseMove(s, e);
        private void ColGrip_MouseUp(object s, MouseButtonEventArgs e)     => Owner.ColGrip_MouseUp(s, e);

        // ── Pipe + export ────────────────────────────────────────
        private void PipeButton_Click(object s, RoutedEventArgs e)         => Owner.PipeButton_Click(s, e);
        private void PipeTab_Click(object s, RoutedEventArgs e)            => Owner.PipeTab_Click(s, e);
        // These two pass THIS pane rather than (sender, args): the reflow acts on the pane that
        // resized, which is not necessarily the focused one.
        // Raised by the location ROW, not the strip: the strip's own width is a consequence of
        // what has been shed, so driving off it made the reflow feed back on itself.
        private void ToolStrip_SizeChanged(object s, SizeChangedEventArgs e) => Owner.ToolStrip_SizeChanged(this);

        /// <summary>
        /// Clip the pane content to the pane's own rounded corners.
        /// </summary>
        /// <remarks>
        /// One of the few things kept in this file rather than forwarded: it is geometry for
        /// THIS pane, with no window state in it at all.
        ///
        /// A Border with a CornerRadius draws a rounded edge but does not clip its child, so
        /// anything that reaches the top of the pane squares those corners off. Nothing ever did
        /// while the location row was always present - the row's own background filled the
        /// corner - so the bug only appeared once F10 could hide it and the terminal and results
        /// list ran straight into the curve.
        ///
        /// Radius 5, not the border's 6: the clip sits INSIDE a 1px border, so it has to follow
        /// the inner curve or it would show a hairline of content outside the stroke.
        /// </remarks>
        private void PaneContent_SizeChanged(object s, SizeChangedEventArgs e)
        {
            if (s is not FrameworkElement el) return;
            el.Clip = new System.Windows.Media.RectangleGeometry(
                new Rect(0, 0, el.ActualWidth, el.ActualHeight), 5, 5);

            // The details columns are pixel widths shared by both panes, so they have to be
            // re-fitted whenever either pane changes size (ResultsView.UpdateColumnFit).
            Owner.UpdateColumnFit();
        }
        private void Overflow_Click(object s, RoutedEventArgs e)            => Owner.Overflow_Click(this);
        // Passes THIS pane, like the two above: the menu drops from this pane's chevron.
        private void Recents_Click(object s, RoutedEventArgs e)             => Owner.Recents_Click(this);
        // Dual pane's two handlers are gone from here: the button moved to the window's icon
        // rail, where it is wired straight to MainWindow and needs no forward.
        private void ExportButton_Click(object s, RoutedEventArgs e)       => Owner.ExportButton_Click(s, e);
        private void Export_Click(object s, RoutedEventArgs e)             => Owner.Export_Click(s, e);
        private void ExportCsv_Click(object s, RoutedEventArgs e)          => Owner.ExportCsv_Click(s, e);

        // ── Tabs ─────────────────────────────────────────────────
        private void Tab_MouseDown(object s, MouseButtonEventArgs e)       => Owner.Tab_MouseDown(s, e);
        private void Tab_DragDown(object s, MouseButtonEventArgs e)        => Owner.Tab_DragDown(s, e);
        private void Tab_DragMove(object s, MouseEventArgs e)              => Owner.Tab_DragMove(s, e);
        private void Tab_DragUp(object s, MouseButtonEventArgs e)          => Owner.Tab_DragUp(s, e);
        private void CloseTab_Click(object s, RoutedEventArgs e)           => Owner.CloseTab_Click(s, e);

        // ── Results list: rows, gestures, quick filter ───────────
        private void ResultHeader_Click(object s, MouseButtonEventArgs e)     => Owner.ResultHeader_Click(s, e);
        private void ResultHeader_MouseDown(object s, MouseButtonEventArgs e) => Owner.ResultHeader_MouseDown(s, e);
        private void OpenFile_Click(object s, RoutedEventArgs e)            => Owner.OpenFile_Click(s, e);
        private void ShowInExplorer_Click(object s, RoutedEventArgs e)      => Owner.ShowInExplorer_Click(s, e);
        private void ResultFilterBox_TextChanged(object s, TextChangedEventArgs e) => Owner.ResultFilterBox_TextChanged(s, e);
        private void ResultFilterClose_Click(object s, RoutedEventArgs e)   => Owner.ResultFilterClose_Click(s, e);
        private void FilterGrip_MouseDown(object s, MouseButtonEventArgs e) => Owner.FilterGrip_MouseDown(s, e);
        private void FilterGrip_MouseMove(object s, MouseEventArgs e)       => Owner.FilterGrip_MouseMove(s, e);
        private void FilterGrip_MouseUp(object s, MouseButtonEventArgs e)   => Owner.FilterGrip_MouseUp(s, e);

        private void ResultsList_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)  => Owner.ResultsList_PreviewMouseLeftButtonDown(s, e);
        private void ResultsList_PreviewMouseMove(object s, MouseEventArgs e)                  => Owner.ResultsList_PreviewMouseMove(s, e);
        private void ResultsList_PreviewMouseLeftButtonUp(object s, MouseButtonEventArgs e)    => Owner.ResultsList_PreviewMouseLeftButtonUp(s, e);
        private void ResultsList_PreviewMouseRightButtonDown(object s, MouseButtonEventArgs e) => Owner.ResultsList_PreviewMouseRightButtonDown(s, e);
        private void ResultsList_PreviewMouseWheel(object s, MouseWheelEventArgs e)            => Owner.ResultsList_PreviewMouseWheel(s, e);
        private void ResultsList_ContextMenuOpening(object s, ContextMenuEventArgs e)          => Owner.ResultsList_ContextMenuOpening(s, e);
        private void ResultsList_SelectionChanged(object s, SelectionChangedEventArgs e)       => Owner.ResultsList_SelectionChanged(s, e);

        // ── Results context menu ─────────────────────────────────
        private void MenuOpen_Click(object s, RoutedEventArgs e)           => Owner.MenuOpen_Click(s, e);
        private void MenuEdit_Click(object s, RoutedEventArgs e)           => Owner.MenuEdit_Click(s, e);
        private void MenuOpenWith_Click(object s, RoutedEventArgs e)       => Owner.MenuOpenWith_Click(s, e);
        private void MenuOpenAdmin_Click(object s, RoutedEventArgs e)      => Owner.MenuOpenAdmin_Click(s, e);
        private void MenuShowInExplorer_Click(object s, RoutedEventArgs e) => Owner.MenuShowInExplorer_Click(s, e);
        private void MenuFavorite_Click(object s, RoutedEventArgs e)       => Owner.MenuFavorite_Click(s, e);
        private void MenuSearchHere_Click(object s, RoutedEventArgs e)     => Owner.MenuSearchHere_Click(s, e);
        private void MenuExcludeFolder_Click(object s, RoutedEventArgs e)  => Owner.MenuExcludeFolder_Click(s, e);
        private void MenuCopyPath_Click(object s, RoutedEventArgs e)       => Owner.MenuCopyPath_Click(s, e);
        private void MenuCopyName_Click(object s, RoutedEventArgs e)       => Owner.MenuCopyName_Click(s, e);
        private void MenuCopyFolder_Click(object s, RoutedEventArgs e)     => Owner.MenuCopyFolder_Click(s, e);
        private void MenuCopyLines_Click(object s, RoutedEventArgs e)      => Owner.MenuCopyLines_Click(s, e);
        private void MenuCopyHash_Click(object s, RoutedEventArgs e)       => Owner.MenuCopyHash_Click(s, e);
        private void MenuProperties_Click(object s, RoutedEventArgs e)     => Owner.MenuProperties_Click(s, e);
        private void MenuShell_Click(object s, RoutedEventArgs e)          => Owner.MenuShell_Click(s, e);

        // File operations (FileCommands.cs)
        private void MenuCut_Click(object s, RoutedEventArgs e)            => Owner.MenuCut_Click(s, e);
        private void MenuCopy_Click(object s, RoutedEventArgs e)           => Owner.MenuCopy_Click(s, e);
        private void MenuPaste_Click(object s, RoutedEventArgs e)          => Owner.MenuPaste_Click(s, e);
        private void MenuRename_Click(object s, RoutedEventArgs e)         => Owner.MenuRename_Click(s, e);
        private void MenuDelete_Click(object s, RoutedEventArgs e)         => Owner.MenuDelete_Click(s, e);
        private void MenuNewFolder_Click(object s, RoutedEventArgs e)      => Owner.MenuNewFolder_Click(s, e);

        // ── Document bar + gear (EditorBar.cs) ───────────────────
        private void EditorSave_Click(object s, RoutedEventArgs e)         => Owner.EditorSave_Click(s, e);
        private void EditorUndo_Click(object s, RoutedEventArgs e)         => Owner.EditorUndo_Click(s, e);
        private void EditorRedo_Click(object s, RoutedEventArgs e)         => Owner.EditorRedo_Click(s, e);
        private void EditorFind_Click(object s, RoutedEventArgs e)         => Owner.EditorFind_Click(s, e);
        private void EditorGoto_Click(object s, RoutedEventArgs e)         => Owner.EditorGoto_Click(s, e);
        private void EditorGotoBox_KeyDown(object s, KeyEventArgs e)       => Owner.EditorGotoBox_KeyDown(s, e);
        private void EditorWrap_Click(object s, RoutedEventArgs e)         => Owner.EditorWrap_Click(s, e);
        private void EditorGear_Click(object s, RoutedEventArgs e)         => Owner.EditorGear_Click(s, e);
        private void EdOptLineNumbers_Click(object s, RoutedEventArgs e)   => Owner.EdOptLineNumbers_Click(s, e);
        private void EdOptCurrentLine_Click(object s, RoutedEventArgs e)   => Owner.EdOptCurrentLine_Click(s, e);
        private void EdOptWhitespace_Click(object s, RoutedEventArgs e)    => Owner.EdOptWhitespace_Click(s, e);
        private void EdOptSpaces_Click(object s, RoutedEventArgs e)        => Owner.EdOptSpaces_Click(s, e);
        private void EdIndent_Click(object s, RoutedEventArgs e)           => Owner.EdIndent_Click(s, e);
        private void EdOptFonts_Click(object s, RoutedEventArgs e)         => Owner.EdOptFonts_Click(s, e);

        // ── Shell bar (TerminalBar.cs) ───────────────────────────
        private void TermCwd_Click(object s, MouseButtonEventArgs e)       => Owner.TermCwd_Click(s, e);
        private void TermNew_Click(object s, RoutedEventArgs e)            => Owner.TermNew_Click(s, e);
        private void TermAdmin_Click(object s, RoutedEventArgs e)          => Owner.TermAdmin_Click(s, e);
        private void TermFolder_Click(object s, RoutedEventArgs e)         => Owner.TermFolder_Click(s, e);
        private void TermClear_Click(object s, RoutedEventArgs e)          => Owner.TermClear_Click(s, e);
        private void TermFonts_Click(object s, RoutedEventArgs e)          => Owner.TermFonts_Click(s, e);
    }
}
