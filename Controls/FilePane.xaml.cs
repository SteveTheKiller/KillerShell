using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

            // Every tile/row/card DataTemplate reaches ViewState through ResultsList.Tag rather
            // than a RelativeSource walk up to this UserControl, or the icons fail to load.
            // RelativeSource AncestorType=local:FilePane is what the first cut
            // of the per-pane view-state change used, and it left every icon blank: the bindings
            // reaching DIRECT, non-templated descendants (DetailsHeader) resolved fine, but the
            // ones inside the ListBox's item templates - realized and recycled by the custom
            // VirtualizingWrapPanel/VirtualizingStackPanel - never did, so TileArt.Size stayed at
            // its 0 default and TileArt.OnChanged's own `size <= 0` guard blanked every image.
            // ResultsList itself is the shallow, non-recycled ancestor every item container sits
            // under regardless of virtualization, and Tag is a plain DependencyProperty a
            // RelativeSource binding can reach the same reliable way {Binding X, Source=...} used
            // to reach the old static ResultsViewState.Current.
            ResultsList.Tag = ViewState;
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

        // ── Per-pane results view state ──────────────────────────
        // Tile/row icon size, density, and the details-view column widths used to be ONE
        // instance shared by both panes (ResultsViewState.Current) - zooming one pane zoomed
        // both, since there was only ever one object for every tile/row/column binding in
        // FilePane.xaml to read. Each pane now owns its own, the same way it
        // owns Tabs/Active above.
        public ResultsViewState ViewState { get; } = new();

        /// <summary>0 list, 1 icons, 2 details - which of the three result layouts this pane is
        /// showing. Used to be one MainWindow field mirrored into both panes on every change,
        /// so switching one pane's layout switched both; the panes need to be independent, so
        /// now each pane keeps its own, the same as
        /// ViewState above. Defaults to 1 (icons) - a fresh install has no saved
        /// "ResultsView{L,R}" setting to restore, so this field's own default IS what a first
        /// run shows, and icon view is the intended default.</summary>
        internal int ViewMode { get; set; } = 1;

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

        /// <summary>
        /// Bumped on every selection change (Shell/DetailsPane.cs). An async stat/decode captures
        /// the value before it starts and checks it again before touching the UI, so a selection
        /// made while an earlier one is still resolving never lands its result over the new one.
        /// </summary>
        internal int DetailsGen;

        /// <summary>Whether THIS pane's details strip is currently showing its thin collapsed
        /// line - per pane, not per window, since each pane tracks its own selection (Shell/
        /// DetailsPane.cs SyncDetailsPaneCollapse).</summary>
        internal bool DetailsPaneCollapsed;

        // ── Per-pane details/preview strip open state ────────────
        // Used to be one MainWindow-wide bool/height mirrored into both panes on every open,
        // close and drag, so toggling one pane's strip toggled both when they should be
        // independent. The strip's CONTENT already read this
        // pane's own selection; only whether it was open, how tall, and whether the user had
        // ever dragged it were still shared. Same fix shape as ViewMode/ViewState above: each
        // pane now opens, closes and remembers its own height on its own.
        internal bool DetailsPaneOpen { get; set; }

        /// <summary>Whether THIS pane's user has ever dragged its details grip - once true, its
        /// height is remembered rather than auto-fit to content (Shell/DetailsPane.cs).</summary>
        internal bool DetailsPaneUserSized { get; set; }

        /// <summary>THIS pane's details strip height, once dragged. Mirrors DetailsPane.cs's old
        /// DetailsPaneHeightDefault constant (160) as the starting point before anything real has
        /// been measured.</summary>
        internal double DetailsPaneHeight { get; set; } = 160;

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
        // Passes THIS pane, like ToolStrip_SizeChanged above: the strip that opens/closes belongs
        // to whichever pane's button was clicked, not necessarily the focused one.
        private void DetailsPane_Click(object s, RoutedEventArgs e)        => Owner.DetailsPaneToggle_Click(this);
        private void DetailsPaneContent_SizeChanged(object s, SizeChangedEventArgs e) => Owner.CorrectDetailsPaneHeight(this);
        private void DetailsPaneGrip_DragDelta(object s, DragDeltaEventArgs e)         => Owner.DetailsPaneGrip_DragDelta(this, e);
        private void DetailsPaneGrip_DragCompleted(object s, DragCompletedEventArgs e) => Owner.DetailsPaneGrip_DragCompleted(this, e);
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
        private void DetailsHeader_MouseRightButtonUp(object s, MouseButtonEventArgs e) => Owner.DetailsHeader_MouseRightButtonUp(s, e);

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
        /// <summary>
        /// Recompute the pane clip after a THEME change. The clip is only rebuilt on SizeChanged,
        /// and a theme switch does not resize anything, so without this the pane keeps the previous
        /// theme's corner radius until the window is dragged - which is how a flat theme could come
        /// up with rounded corners. Called from MainWindow's ThemeChanged.
        /// `e` is unused by the handler, so passing null is safe.
        /// </summary>
        internal void RefreshPaneClip() => PaneContent_SizeChanged(PaneContent, null!);

        private void PaneContent_SizeChanged(object s, SizeChangedEventArgs e)
        {
            if (s is not FrameworkElement el) return;
            // PER-CORNER now, mirroring ResultsPane.CornerRadius - which Tabs.cs squares on the
            // top corner under a first/last ACTIVE tab. The old uniform RectangleGeometry kept
            // clipping the bar's top-right ROUND while the pane's own border squared, which left
            // a tiny rounded bit of the menubar visible below the tab whenever the rightmost tab
            // was the active one. Tabs.cs calls RefreshPaneClip whenever it
            // re-syncs the corners, so the clip can never lag them. (This also still covers the
            // 98SE case: its CornerRadius is 0 everywhere, so the geometry is a plain rect.)
            var cr = ResultsPane.CornerRadius;
            double w = el.ActualWidth, h = el.ActualHeight;
            if (w <= 0 || h <= 0) return;
            double tl = cr.TopLeft, tr = cr.TopRight, br = cr.BottomRight, bl = cr.BottomLeft;
            var g = new System.Windows.Media.StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(tl, 0), true, true);
                c.LineTo(new Point(w - tr, 0), false, false);
                if (tr > 0) c.ArcTo(new Point(w, tr), new Size(tr, tr), 0, false, System.Windows.Media.SweepDirection.Clockwise, false, false);
                c.LineTo(new Point(w, h - br), false, false);
                if (br > 0) c.ArcTo(new Point(w - br, h), new Size(br, br), 0, false, System.Windows.Media.SweepDirection.Clockwise, false, false);
                c.LineTo(new Point(bl, h), false, false);
                if (bl > 0) c.ArcTo(new Point(0, h - bl), new Size(bl, bl), 0, false, System.Windows.Media.SweepDirection.Clockwise, false, false);
                c.LineTo(new Point(0, tl), false, false);
                if (tl > 0) c.ArcTo(new Point(tl, 0), new Size(tl, tl), 0, false, System.Windows.Media.SweepDirection.Clockwise, false, false);
            }
            g.Freeze();
            el.Clip = g;

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
        private void ResultsList_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            Owner.ResultsList_SelectionChanged(s, e);
            Owner.UpdateDetailsPaneForSelection(this);   // Shell/DetailsPane.cs - no-ops when closed
        }

        // ── Results context menu ─────────────────────────────────
        private void MenuOpen_Click(object s, RoutedEventArgs e)           => Owner.MenuOpen_Click(s, e);
        private void MenuEdit_Click(object s, RoutedEventArgs e)           => Owner.MenuEdit_Click(s, e);
        private void MenuOpenWith_Click(object s, RoutedEventArgs e)       => Owner.MenuOpenWith_Click(s, e);
        private void MenuOpenAdmin_Click(object s, RoutedEventArgs e)      => Owner.MenuOpenAdmin_Click(s, e);
        private void MenuShowInExplorer_Click(object s, RoutedEventArgs e) => Owner.MenuShowInExplorer_Click(s, e);
        private void MenuFavorite_Click(object s, RoutedEventArgs e)       => Owner.MenuFavorite_Click(s, e);
        private void MenuSearchHere_Click(object s, RoutedEventArgs e)     => Owner.MenuSearchHere_Click(s, e);
        private void MenuAnalyze_Click(object s, RoutedEventArgs e)        => Owner.MenuAnalyze_Click(s, e);
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
        private void EdPath_Click(object s, MouseButtonEventArgs e)        => Owner.EdPath_Click(s, e);
        private void EditorPathBox_KeyDown(object s, KeyEventArgs e)       => Owner.EditorPathBox_KeyDown(s, e);
        private void EditorPathBox_LostFocus(object s, RoutedEventArgs e)  => Owner.EditorPathBox_LostFocus(s, e);
        private void EdOptLineNumbers_Click(object s, RoutedEventArgs e)   => Owner.EdOptLineNumbers_Click(s, e);
        private void EdOptCurrentLine_Click(object s, RoutedEventArgs e)   => Owner.EdOptCurrentLine_Click(s, e);
        private void EdOptWhitespace_Click(object s, RoutedEventArgs e)    => Owner.EdOptWhitespace_Click(s, e);
        private void EdOptSpaces_Click(object s, RoutedEventArgs e)        => Owner.EdOptSpaces_Click(s, e);
        private void EdIndent_Click(object s, RoutedEventArgs e)           => Owner.EdIndent_Click(s, e);
        private void EdOptFonts_Click(object s, RoutedEventArgs e)         => Owner.EdOptFonts_Click(s, e);
        private void EditorEncoding_Click(object s, RoutedEventArgs e)     => Owner.EditorEncoding_Click(s, e);
        private void EdEncoding_Click(object s, RoutedEventArgs e)         => Owner.EdEncoding_Click(s, e);

        // ── Shell bar (TerminalBar.cs) ───────────────────────────
        private void TermCwd_Click(object s, MouseButtonEventArgs e)       => Owner.TermCwd_Click(s, e);
        private void TermCwdBox_KeyDown(object s, KeyEventArgs e)          => Owner.TermCwdBox_KeyDown(s, e);
        private void TermCwdBox_LostFocus(object s, RoutedEventArgs e)     => Owner.TermCwdBox_LostFocus(s, e);
        private void TermNew_Click(object s, RoutedEventArgs e)            => Owner.TermNew_Click(s, e);
        private void TermAdmin_Click(object s, RoutedEventArgs e)          => Owner.TermAdmin_Click(s, e);
        private void TermFolder_Click(object s, RoutedEventArgs e)         => Owner.TermFolder_Click(s, e);
        private void TermClear_Click(object s, RoutedEventArgs e)          => Owner.TermClear_Click(s, e);
        private void TermFonts_Click(object s, RoutedEventArgs e)          => Owner.TermFonts_Click(s, e);
    }
}
