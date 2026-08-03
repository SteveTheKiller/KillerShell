using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace KillerShell.Shell
{
    // Results pane view modes: 0 list (the expandable cards), 1 icons (tile grid), 2 details
    // (flat rows under sortable column headers). Partial of MainWindow.
    //
    // Sorting and filtering are untouched by any of this. Both live on the collection view
    // (Results.cs ApplySort / ApplyFilter), so they keep working across every layout for free -
    // switching view swaps the panel and the template and nothing else.

    /// <summary>
    /// Bindable tile/row/column geometry for ONE pane (FilePane.ViewState). The wrap panel binds
    /// its ItemWidth/ItemHeight here and every tile binds its icon size here, so one property
    /// change resizes the whole grid.
    /// </summary>
    /// <remarks>
    /// Used to be a single app-wide instance (Steve, 2026-08-03 - "when I zoom in and out in one
    /// pane, it does it in the other"): every binding in FilePane.xaml pointed at the same
    /// static <c>Current</c>, so tile size, row icon size, density and every dragged column width
    /// were shared by both panes whether you wanted that or not. Each pane now owns its own.
    /// </remarks>
    public sealed class ResultsViewState : INotifyPropertyChanged
    {
        // The sizes the shell can actually serve well. 32 and 48 map to real shell icon sizes;
        // above that comes from the jumbo list (see Services/IconCache.cs).
        public static readonly int[] Steps = { 32, 48, 64, 96, 128, 192, 256 };

        private int _tileSize = 96;

        public int TileSize
        {
            get => _tileSize;
            set
            {
                int v = Math.Max(Steps[0], Math.Min(Steps[Steps.Length - 1], value));
                if (v == _tileSize) return;
                _tileSize = v;
                Notify();
                Notify(nameof(TileWidth));
                Notify(nameof(TileHeight));
            }
        }

        // Room for the art plus two wrapped lines of filename. The width floor keeps small
        // icon sizes from squeezing names down to three characters and an ellipsis.
        //
        // The added number is the horizontal gap between tiles: at 96px art and the Comfortable
        // +16 that is a 112px cell, so icons sit 16px apart. It used to be a flat +44, which
        // spread the grid out with far more air between columns than Explorer uses. Longer
        // names trim sooner as a result - that is the trade, and the tooltip still carries the
        // full path. Density drives it now, so tightening pulls the columns together instead of
        // leaving the same gaps around smaller tiles.
        public double TileWidth  => Math.Max(96, _tileSize + TileExtraW[_density]);

        // The trailing number is everything under the art: the tile's own vertical padding, the
        // gap above the name, and two lines of it. Those three shrink with density, so the cell
        // has to shrink by the same amount or the space just moves from inside the tile to
        // between the rows and nothing looks any tighter.
        public double TileHeight => _tileSize + TileExtraH[_density];

        // ═══════════════════════════════════════════════════════════
        //  ROW ICONS
        // ═══════════════════════════════════════════════════════════
        // Ctrl+wheel is the same gesture in all three views, but a row is a line of text with a
        // picture on it, so it gets its own much shorter ladder: past 64px the icon stops being
        // a marker and starts setting the row height, which is what the tile grid is for. One
        // value drives both row layouts, so a size picked in the cards is still there in
        // details - the way TileSize carries across tabs.
        public static readonly int[] RowIconSteps = { 16, 20, 24, 32, 40, 48, 64 };

        private int _rowIconSize = 16;

        public int RowIconSize
        {
            get => _rowIconSize;
            set
            {
                int v = Math.Max(RowIconSteps[0],
                                 Math.Min(RowIconSteps[RowIconSteps.Length - 1], value));
                if (v == _rowIconSize) return;
                _rowIconSize = v;
                Notify();
                Notify(nameof(CardIconSize));
                Notify(nameof(RowIconColumn));
            }
        }

        /// <summary>The list card's icon: two px up on the details row's, as it always was.</summary>
        public int CardIconSize => _rowIconSize + 2;

        /// <summary>
        /// The details row's icon column - the art plus the gap to the name. Duplicated into
        /// DetailsHeader, which has to keep the same width or every row sits off its heading.
        /// </summary>
        public GridLength RowIconColumn => new GridLength(_rowIconSize + 10);

        // ═══════════════════════════════════════════════════════════
        //  DETAILS COLUMNS
        // ═══════════════════════════════════════════════════════════
        // EVERY column is a pixel width, and a star FILLER sits after the last one to soak up
        // whatever is left over. That is what a details list is, and copying it exactly is the
        // point: a divider resizes the column to its LEFT and nothing else, and the columns to
        // the right of it simply slide. One drag, one column.
        //
        // The first two attempts made Name a star column so it would absorb, which meant a drag
        // always had to move a SECOND column to keep the arithmetic balanced - and when that
        // second column hit its floor the overflow landed on Name and the whole row shifted
        // sideways. Both times the complaint was the same and correct: something moved that was
        // not the thing being dragged. A filler column has no opinion, so nothing has to.
        //
        // These live here rather than on the tab because the header Grid and every row's Grid
        // need the same values, and a DataTemplate's Grid can only reach a shared source. That
        // is also why the header's ColumnDefinitions and the row template's have to be kept in
        // step by hand (FilePane.xaml says so in both places).

        internal const double ColMinWidth = 44;
        internal const double ColMaxWidth = 900;

        internal const double DefaultNameWidth     = 260;
        internal const double DefaultLocationWidth = 240;
        internal const double DefaultSizeWidth     = 86;
        internal const double DefaultModifiedWidth = 128;

        private double _namePx     = DefaultNameWidth;
        private double _locationPx = DefaultLocationWidth;
        private double _sizePx     = DefaultSizeWidth;
        private double _modifiedPx = DefaultModifiedWidth;

        // Zero while browsing, where the location repeats the folder you are already standing in
        // on every single row; restored for search results, which is the one case where rows come
        // from different places and the column earns its width. The dragged width is remembered
        // across that, so coming back from a folder listing does not reset it.
        private bool _locationHidden;

        public bool LocationHidden
        {
            get => _locationHidden;
            set
            {
                if (_locationHidden == value) return;
                _locationHidden = value;
                Notify();
                Notify(nameof(LocationWidth));
                Notify(nameof(LocationGripVisibility));

                // The other three share out the width the location column just gave up or took
                // back, so the fit factor changes for all of them.
                Notify(nameof(NameWidth));
                Notify(nameof(SizeWidth));
                Notify(nameof(ModifiedWidth));
            }
        }

        /// <summary>The location column's resize grip, which follows the column out of sight.</summary>
        /// <remarks>
        /// A Visibility rather than a bool plus a converter: it is the only place in this file
        /// that would need one, and a converter resource exists to be reused.
        /// </remarks>
        public Visibility LocationGripVisibility =>
            _locationHidden ? Visibility.Collapsed : Visibility.Visible;

        // User-toggleable via the details header's right-click menu (ResultsView.cs
        // DetailsHeader_MouseRightButtonUp, Services/ColumnVisibilityMenu.cs) - unlike
        // LocationHidden above, which the app itself drives by context (browsing vs. search).
        // Name is deliberately NOT toggleable here, the same way Explorer's own column chooser
        // never lets you hide Name: it is the row's identity, not a fact about it.
        private bool _sizeVisible = true;

        public bool SizeVisible
        {
            get => _sizeVisible;
            set
            {
                if (_sizeVisible == value) return;
                _sizeVisible = value;
                Notify();
                Notify(nameof(SizeWidth));
                Notify(nameof(NameWidth));
                Notify(nameof(LocationWidth));
                Notify(nameof(ModifiedWidth));
            }
        }

        private bool _modifiedVisible = true;

        public bool ModifiedVisible
        {
            get => _modifiedVisible;
            set
            {
                if (_modifiedVisible == value) return;
                _modifiedVisible = value;
                Notify();
                Notify(nameof(ModifiedWidth));
                Notify(nameof(NameWidth));
                Notify(nameof(LocationWidth));
                Notify(nameof(SizeWidth));
            }
        }

        // ── Fitting the columns to the pane ──────────────────────
        // The widths above are what the USER set. What gets drawn is those scaled down when the
        // pane is too narrow to hold them, so the last column can never be cut off the right
        // edge - the rows do not scroll horizontally, so off the edge means gone, not reachable.
        //
        // Scaled rather than clamped: taking the shortfall out of one column would make the
        // window robbing whichever column happened to be last, and the proportions you chose
        // would come back wrong when it widened again. A factor keeps the relative widths and
        // is perfectly reversible - drag the window back out and the columns return to exactly
        // the widths you set, because nothing was written back.
        private double _availableWidth;

        public double AvailableWidth
        {
            set
            {
                if (Math.Abs(_availableWidth - value) < 0.5) return;
                _availableWidth = value;
                Notify(nameof(NameWidth));
                Notify(nameof(LocationWidth));
                Notify(nameof(SizeWidth));
                Notify(nameof(ModifiedWidth));
            }
        }

        /// <summary>1 when everything fits; below 1 when the pane is too narrow.</summary>
        private double FitFactor
        {
            get
            {
                if (_availableWidth <= 0) return 1;              // not measured yet
                double want = _namePx
                            + (_sizeVisible ? _sizePx : 0)
                            + (_modifiedVisible ? _modifiedPx : 0)
                            + (_locationHidden ? 0 : _locationPx);
                if (want <= _availableWidth || want <= 0) return 1;
                return _availableWidth / want;
            }
        }

        // A floor per column even after scaling, so a very narrow pane leaves something to read
        // rather than four slivers. Below this the last column does get clipped, which at that
        // width is unavoidable and is what Explorer does too.
        private double Fit(double px) => Math.Max(24, px * FitFactor);

        public GridLength NameWidth     => new GridLength(Fit(_namePx));
        public GridLength LocationWidth => _locationHidden ? new GridLength(0) : new GridLength(Fit(_locationPx));
        public GridLength SizeWidth     => _sizeVisible ? new GridLength(Fit(_sizePx)) : new GridLength(0);
        public GridLength ModifiedWidth => _modifiedVisible ? new GridLength(Fit(_modifiedPx)) : new GridLength(0);

        /// <summary>Set one column's width, clamped. Returns what it actually became.</summary>
        /// <remarks>
        /// Indexed by the column's Grid.Column so the drag handler can carry one number from the
        /// grip's Tag rather than a switch at every call site.
        /// </remarks>
        public double SetColumnWidth(int column, double px)
        {
            px = Math.Max(ColMinWidth, Math.Min(ColMaxWidth, px));
            switch (column)
            {
                case 1: _namePx     = px; Notify(nameof(NameWidth));     break;
                case 2: _locationPx = px; Notify(nameof(LocationWidth)); break;
                case 3: _sizePx     = px; Notify(nameof(SizeWidth));     break;
                case 4: _modifiedPx = px; Notify(nameof(ModifiedWidth)); break;
            }
            return px;
        }

        /// <summary>The current width of one column, for seeding a drag.</summary>
        public double GetColumnWidth(int column) => column switch
        {
            1 => _namePx,
            2 => _locationPx,
            3 => _sizePx,
            _ => _modifiedPx,
        };

        /// <summary>Back to the shipped width. Double-clicking a divider does this.</summary>
        public double DefaultColumnWidth(int column) => column switch
        {
            1 => DefaultNameWidth,
            2 => DefaultLocationWidth,
            3 => DefaultSizeWidth,
            _ => DefaultModifiedWidth,
        };

        /// <summary>
        /// What every column EXCEPT <paramref name="column"/> is taking up right now.
        /// </summary>
        /// <remarks>
        /// The drag uses this to stop the columns growing past the pane. Without a ceiling the
        /// last one runs off the right edge and is simply cut off - the rows are not horizontally
        /// scrollable, so anything past the edge is gone rather than reachable. A hidden location
        /// column counts as nothing, which is what it is.
        /// </remarks>
        public double TotalWidthExcept(int column)
        {
            double t = 0;
            if (column != 1) t += _namePx;
            if (column != 2 && !_locationHidden) t += _locationPx;
            if (column != 3 && _sizeVisible) t += _sizePx;
            if (column != 4 && _modifiedVisible) t += _modifiedPx;
            return t;
        }

        // ═══════════════════════════════════════════════════════════
        //  DENSITY
        // ═══════════════════════════════════════════════════════════
        // 0 Roomy, 1 Comfortable, 2 Compact, 3 Tight, 4 Minimal. Every view's padding is derived
        // here rather than hardcoded in its template, so one property change retightens all
        // three at once.
        //
        // Exposed as properties on this shared object rather than stamped onto every result the
        // way KillerNotes stamps its notes: KillerShell's list can hold six figures of rows and
        // walking them to set a padding would be absurd, while the templates already bind here
        // for the tile size. Changing a property repaints; nothing is re-listed.
        /// <summary>Number of density levels. The cycle and the status captions both key off it.</summary>
        public const int DensityLevels = 5;

        // Comfortable, not Roomy, is where a fresh install lands: it is the spacing every
        // screenshot and every previous build used, and level 0 exists to go LOOSER than that.
        private int _density = 1;

        public int Density
        {
            get => _density;
            set
            {
                int v = value < 0 ? 0 : value > DensityLevels - 1 ? DensityLevels - 1 : value;
                if (v == _density) return;
                _density = v;

                // Everything derived, in one go. TileHeight is in here because a tighter tile is
                // a SHORTER cell, not just a smaller picture - without it the grid keeps the old
                // row pitch and the padding comes off the inside of an unchanged box.
                Notify();
                Notify(nameof(TilePad));
                Notify(nameof(TileMargin));
                Notify(nameof(TileNamePad));
                Notify(nameof(TileWidth));
                Notify(nameof(TileHeight));
                Notify(nameof(RowPad));
                Notify(nameof(CardPad));
                Notify(nameof(HeaderPad));
            }
        }

        // The ladder, one row per level, written as tables rather than switch arms: with five
        // levels the point of the numbers is how they step, and a column you can read down
        // catches a value out of order in a way five separate expressions never would.
        //
        // Index: 0 Roomy, 1 Comfortable, 2 Compact, 3 Tight, 4 Minimal.

        // Tiles. The name keeps its two lines at every level - trimming a file name to one line
        // is lost information, which is the opposite of what density is for.
        private static readonly double[] TileExtraW = { 30, 16, 12, 8, 4 };
        private static readonly double[] TileExtraH = { 62, 52, 46, 40, 34 };

        private static readonly Thickness[] TilePads =
        {
            new Thickness(6, 8, 6, 8), new Thickness(4, 6, 4, 6), new Thickness(3, 4, 3, 4),
            new Thickness(2, 2, 2, 2), new Thickness(1, 1, 1, 1),
        };

        private static readonly Thickness[] TileMargins =
        {
            new Thickness(6), new Thickness(3), new Thickness(2), new Thickness(1), new Thickness(0),
        };

        private static readonly Thickness[] TileNamePads =
        {
            new Thickness(2, 8, 2, 0), new Thickness(2, 6, 2, 0), new Thickness(2, 4, 2, 0),
            new Thickness(2, 2, 2, 0), new Thickness(2, 1, 2, 0),
        };

        public Thickness TilePad     => TilePads[_density];
        public Thickness TileMargin  => TileMargins[_density];
        public Thickness TileNamePad => TileNamePads[_density];

        // Details rows and list cards. The side padding moves with density too, so a tight level
        // wins width as well as height - but the right number never drops below 22, because
        // that gap is what keeps the last column out from under the scrollbar rather than
        // decoration. The left number is shared with the column headers, which is why HeaderPad
        // exists: the two have to step together or every row sits off its own heading.
        private static readonly Thickness[] RowPads =
        {
            new Thickness(20, 5, 36, 5), new Thickness(14, 3, 30, 3), new Thickness(10, 1, 26, 1),
            new Thickness(8, 1, 24, 1),  new Thickness(6, 0, 22, 0),
        };

        private static readonly Thickness[] CardPads =
        {
            new Thickness(20, 9, 20, 9), new Thickness(14, 6, 14, 6), new Thickness(10, 4, 10, 4),
            new Thickness(8, 3, 8, 3),   new Thickness(6, 2, 6, 2),
        };

        // The header's own vertical padding is fixed - it is a band of type, not a row of
        // results, and letting it shrink would just make the column names harder to hit.
        private static readonly Thickness[] HeaderPads =
        {
            new Thickness(20, 4, 36, 4), new Thickness(14, 4, 30, 4), new Thickness(10, 4, 26, 4),
            new Thickness(8, 4, 24, 4),  new Thickness(6, 4, 22, 4),
        };

        public Thickness RowPad    => RowPads[_density];
        public Thickness CardPad   => CardPads[_density];
        public Thickness HeaderPad => HeaderPads[_density];

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>
    /// Attached behavior that puts the right picture in a tile's Image.
    /// <para>
    /// A plain <c>{Binding Icon}</c> cannot do this job. The image depends on two things that
    /// change independently - the file and the current tile size - and containers are recycled,
    /// so the same Image element is handed a different file as the grid scrolls. Setting Source
    /// from a callback on both attached properties covers all of it: a rebind and a size change
    /// look the same from here, and whichever fires last wins.
    /// </para>
    /// </summary>
    public static class TileArt
    {
        public static readonly DependencyProperty PathProperty =
            DependencyProperty.RegisterAttached("Path", typeof(string), typeof(TileArt),
                new PropertyMetadata(null, OnChanged));

        public static readonly DependencyProperty SizeProperty =
            DependencyProperty.RegisterAttached("Size", typeof(int), typeof(TileArt),
                new PropertyMetadata(0, OnChanged));

        // Folders and drives have no extension to key on, so without this the extension-only
        // fast path answers every one of them with the generic unknown-file page. It is what
        // picks up a custom folder icon too, and what makes a drive at This PC look like a
        // drive rather than a document.
        public static readonly DependencyProperty IsDirectoryProperty =
            DependencyProperty.RegisterAttached("IsDirectory", typeof(bool), typeof(TileArt),
                new PropertyMetadata(false, OnChanged));

        public static bool GetIsDirectory(DependencyObject d) => (bool)d.GetValue(IsDirectoryProperty);
        public static void SetIsDirectory(DependencyObject d, bool v) => d.SetValue(IsDirectoryProperty, v);

        public static string? GetPath(DependencyObject d) => (string?)d.GetValue(PathProperty);
        public static void   SetPath(DependencyObject d, string? v) => d.SetValue(PathProperty, v);

        public static int  GetSize(DependencyObject d) => (int)d.GetValue(SizeProperty);
        public static void SetSize(DependencyObject d, int v) => d.SetValue(SizeProperty, v);

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Image img) return;

            string? path = GetPath(img);
            int     size = GetSize(img);

            if (string.IsNullOrEmpty(path) || size <= 0) { img.Source = null; return; }

            // Synchronous and cheap: the shell icon is cached per extension and per size, so a
            // screen of tiles costs a handful of shell calls no matter how many results there are.
            img.Source = Services.IconCache.For(path!, size, GetIsDirectory(img));
        }
    }

    public partial class MainWindow
    {
        /// <summary>
        /// The selected item's full path, in the footer.
        /// </summary>
        /// <remarks>
        /// Every view trims a long name to fit its row or its tile, and the part it trims is
        /// usually the part that tells two files apart - two exports of the same report differ
        /// in the tail, not the head. The footer is the one place in the window sized for an
        /// unbounded string, and ElideFooterStatus cuts it from the FRONT, so what survives a
        /// narrow window is the file name rather than the drive letter.
        ///
        /// A selection dropping to nothing deliberately leaves the line alone. Clearing a
        /// selection is not news, and blanking here would eat the "Done - 3 item(s)" a file
        /// operation just put there.
        /// </remarks>
        internal void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox list) return;

            int count = list.SelectedItems.Count;
            if (count == 0) return;

            if (count == 1 && list.SelectedItem is Models.SearchResult one)
                SetFooterStatus(one.FilePath);
            else
                SetFooterStatus(string.Format(Loc("Str_Status_Selected"), count.ToString("N0")));
        }

        // The card template stays inline on the ListBox in MainWindow.xaml rather than becoming a
        // keyed resource like the other two: it is ninety lines of nested markup and moving it
        // would be a large diff for no gain. Grab it once on the way past instead.
        private DataTemplate? _listTemplate;

        private void InitResultsView()
        {
            _listTemplate ??= Pane.ResultsList.ItemTemplate;

            // Tile/row zoom, density and the dragged column widths are per-PANE now (Steve,
            // 2026-08-03 - see the remark on ResultsViewState), so both panes are restored here
            // independently under their own settings keys - LeftPane and RightPane both exist
            // from InitializeComponent regardless of whether the second pane is currently open,
            // so there is always something to restore into.
            foreach (var p in new[] { LeftPane, RightPane })
            {
                string key = PaneKey(p);

                if (int.TryParse(Services.ThemeManager.GetSetting("ResultsView" + key), out int v) && v >= 0 && v <= 2)
                    p.ViewMode = v;

                if (int.TryParse(Services.ThemeManager.GetSetting("ResultsTileSize" + key), NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out int px))
                    p.ViewState.TileSize = px;

                if (int.TryParse(Services.ThemeManager.GetSetting("ResultsRowIconSize" + key), NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out int rowPx))
                    p.ViewState.RowIconSize = rowPx;

                if (int.TryParse(Services.ThemeManager.GetSetting(SetDensity + key), out int d))
                    p.ViewState.Density = d;

                // Dragged details-column widths. Each restored independently, so one bad or
                // missing value leaves the others alone rather than resetting the whole row.
                foreach (int col in new[] { 1, 2, 3, 4 })
                    if (double.TryParse(Services.ThemeManager.GetSetting(ColSettingKey(col) + key), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out double w))
                        p.ViewState.SetColumnWidth(col, w);

                // Size/Modified show-or-hide (right-click the details header - Services/
                // ColumnVisibilityMenu.cs), persisted the same way the dragged widths above are.
                // Name is not offered (see the remark on ResultsViewState.SizeVisible); Location
                // already has its own context-driven show/hide and is left out of this menu so
                // the two mechanisms cannot fight over the same column.
                Services.ColumnVisibilityMenu.RestoreVisibility("ResultsDetails" + key, DetailsColumnEntries(p));
            }

            ApplyResultsView();
        }

        /// <summary>The Size/Modified toggle entries for ONE pane, rebuilt on demand rather than
        /// cached: the getter/setter closures capture <paramref name="pane"/>.ViewState directly,
        /// so rebuilding costs nothing and there is no stale closure to worry about.</summary>
        private static Services.ColumnVisibilityMenu.Entry[] DetailsColumnEntries(FilePane pane) => new[]
        {
            new Services.ColumnVisibilityMenu.Entry("Size", "Str_Col_Size", true,
                () => pane.ViewState.SizeVisible,     v => pane.ViewState.SizeVisible = v),
            new Services.ColumnVisibilityMenu.Entry("Modified", "Str_Col_Modified", true,
                () => pane.ViewState.ModifiedVisible, v => pane.ViewState.ModifiedVisible = v),
        };

        /// <summary>Right-click the details column-header band: the same shared toggle menu the
        /// Event Viewer and Processes grids use (Services/ColumnVisibilityMenu.cs), anchored to
        /// the header itself, not to whichever button happened to be under the pointer. Acts on
        /// whichever pane the header belongs to - FilePane's own PreviewMouseDown already moved
        /// focus there before this fires, so `Pane` is correct.</summary>
        internal void DetailsHeader_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el) return;
            Services.ColumnVisibilityMenu.ShowFor(el, "ResultsDetails" + PaneKey(Pane), DetailsColumnEntries(Pane));
            e.Handled = true;
        }

        internal void ViewList_Click(object sender, RoutedEventArgs e)    => SetResultsView(0);
        internal void ViewIcons_Click(object sender, RoutedEventArgs e)   => SetResultsView(1);
        internal void ViewDetails_Click(object sender, RoutedEventArgs e) => SetResultsView(2);

        private void SetResultsView(int mode)
        {
            if (Pane.ViewMode == mode) return;
            Pane.ViewMode = mode;
            ApplyResultsViewToPane();
            Services.ThemeManager.SetSetting("ResultsView" + PaneKey(Pane), mode.ToString(CultureInfo.InvariantCulture));
        }

        // Each pane keeps its own view mode now (Steve, 2026-08-03 - "i changed one pane into
        // details list view and both panes changed"). ApplyResultsView still exists for the
        // places that need to (re)apply BOTH panes' own current mode at once - startup (before
        // dual pane may even be open) and DualPane.cs revealing the second pane for the first
        // time, whose ItemsPanel/ItemTemplate have never been set up yet and would otherwise sit
        // on whatever its XAML happened to default to.
        private void ApplyResultsView() => ForEachPane(ApplyResultsViewToPane);

        // Swap the panel and the template, then light the button that is now active. Same shape
        // as the folder picker's ApplyView, which is where the pattern comes from.
        private void ApplyResultsViewToPane()
        {
            // Captured HERE and not only in InitResultsView, because this runs first and would
            // otherwise destroy the thing InitResultsView is trying to capture.
            //
            // The card template is declared inline on the ListBox in FilePane.xaml, so the only
            // reference to it is the one the pane starts with. ActivateTab reaches this method
            // (via ApplyTerminalView -> ApplyPaneToolbarMode) during the Loaded handler, several
            // lines BEFORE InitResultsView runs - and at that point Pane.ViewMode is still
            // whatever its field default is (1, icons - see FilePane.ViewMode), so on a build
            // where that default was 0 (list) the assignment below wrote a null _listTemplate
            // over the real template. InitResultsView then captured the null as "the list
            // template", and every later switch to list view rendered rows as
            // KillerShell.Models.SearchResult in the default black, because a ListBox with no
            // ItemTemplate falls back to ToString(). It survived this long because startup in
            // icons or details view looks perfectly fine; only switching TO list view shows it.
            _listTemplate ??= Pane.ResultsList.ItemTemplate;

            int mode = Pane.ViewMode;

            Pane.ResultsList.ItemsPanel = (ItemsPanelTemplate)Pane.ResultsList.FindResource(
                mode == 1 ? "PanelWrap" : "PanelStack");

            Pane.ResultsList.ItemTemplate =
                mode == 1 ? (DataTemplate)Pane.ResultsList.FindResource("TileTemplate") :
                mode == 2 ? (DataTemplate)Pane.ResultsList.FindResource("DetailsRowTemplate")
                          : _listTemplate;

            // Column headers belong to details view; expand/collapse-all only means anything for
            // the cards, which are the only layout with something to expand.
            //
            // Hidden, not Collapsed: the button sits in the header's right-hand strip, and a
            // collapsed element gives up its width, so every other control in that strip slid
            // sideways each time the view changed. Hidden keeps the slot.
            //
            // Also gated on ResultsList actually being visible (Steve, 2026-08-03: a restored
            // terminal tab in Details view showed the column-heading row above its empty prompt).
            // ApplyPaneToolbarMode (TerminalTabs.cs) already collapses DetailsHeader correctly
            // the moment a shell/editor/registry/processes/events tab activates - but at startup,
            // InitResultsView's own ApplyResultsView call runs AFTER tab restore has already
            // activated the saved tab, and this method used to set DetailsHeader purely from the
            // view mode with no idea a terminal was on screen, undoing that collapse. Every
            // non-listing tab kind sets Pane.ResultsList.Visibility = Collapsed on activation
            // (TerminalTabs.cs/EditorTabs.cs/RegistryEditorTabs.cs/ProcessTabs.cs/
            // EventViewerTabs.cs/PerformanceTabs.cs all do this already, so it is the one signal
            // every one of them already agrees on), so it doubles as "is a listing even showing".
            bool showingListing = Pane.ResultsList.Visibility == Visibility.Visible;
            Pane.DetailsHeader.Visibility   = mode == 2 && showingListing ? Visibility.Visible : Visibility.Collapsed;
            Pane.ExpandAllButton.Visibility = mode == 0 ? Visibility.Visible : Visibility.Hidden;

            Pane.ViewListBtn.Tag    = mode == 0 ? "on" : null;
            Pane.ViewIconsBtn.Tag   = mode == 1 ? "on" : null;
            Pane.ViewDetailsBtn.Tag = mode == 2 ? "on" : null;

            UpdateColumnArrows();
        }

        // ── Sortable column headers (details view) ───────────────
        // These drive the same SortIndex / SortAsc the combo does, so the two controls are always
        // showing the same thing and ApplySort stays the single place sorting happens.
        internal void ColName_Click(object sender, RoutedEventArgs e)     => SetColumnSort(1);
        internal void ColFolder_Click(object sender, RoutedEventArgs e)   => SetColumnSort(2);
        internal void ColSize_Click(object sender, RoutedEventArgs e)     => SetColumnSort(3);
        internal void ColModified_Click(object sender, RoutedEventArgs e) => SetColumnSort(4);

        private void SetColumnSort(int index)
        {
            if (_active == null) return;

            if (_active.SortIndex == index)
            {
                _active.SortAsc = !_active.SortAsc;
            }
            else
            {
                _active.SortIndex = index;
                // Text sorts want A first; size and date want the biggest and newest first, which
                // is what you are looking for when you click those.
                _active.SortAsc = index == 1 || index == 2;
            }

            ApplySort(_active);
        }

        // Same MDL2 chevrons the sort-direction button uses, built from codepoints so the
        // source stays ASCII (the convention across this project).
        private static readonly string ArrowUp   = ((char)0xE70E).ToString();
        private static readonly string ArrowDown = ((char)0xE70D).ToString();

        private void UpdateColumnArrows()
        {
            if (_active == null) return;
            string a = _active.SortAsc ? ArrowUp : ArrowDown;
            Pane.ColNameArrow.Text   = _active.SortIndex == 1 ? a : string.Empty;
            Pane.ColFolderArrow.Text = _active.SortIndex == 2 ? a : string.Empty;
            Pane.ColSizeArrow.Text   = _active.SortIndex == 3 ? a : string.Empty;
            Pane.ColModArrow.Text    = _active.SortIndex == 4 ? a : string.Empty;
        }

        // ── Icon sizing (Ctrl+wheel over the results pane) ───────
        // Explorer's gesture, and it is free here: the app-wide zoom is the wheel over the
        // title-bar wordmark with no modifier (AppScale.cs), so the two never meet. Steps are
        // discrete because the shell only has a few real icon sizes to give - sliding smoothly
        // between them would just be resampling the same bitmap.
        internal void ResultsList_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;

            // Each view steps its OWN ladder. The tile grid's art is the content, so it climbs to
            // 256; a card or a details row is text with a marker beside it, so it stops at 64.
            // Both are the same gesture in the same place, which is the part that matters.
            var  state = Pane.ViewState;
            bool tiles = Pane.ViewMode == 1;
            var  steps = tiles ? ResultsViewState.Steps : ResultsViewState.RowIconSteps;
            int  now   = tiles ? state.TileSize : state.RowIconSize;

            int i = Array.IndexOf(steps, now);
            if (i < 0)
            {
                // Restored from a setting that is not on the ladder: snap to the nearest step.
                i = 0;
                for (int k = 1; k < steps.Length; k++)
                    if (Math.Abs(steps[k] - now) < Math.Abs(steps[i] - now)) i = k;
            }

            i = Math.Max(0, Math.Min(steps.Length - 1, i + (e.Delta > 0 ? 1 : -1)));

            if (tiles) state.TileSize    = steps[i];
            else       state.RowIconSize = steps[i];

            Services.ThemeManager.SetSetting((tiles ? "ResultsTileSize" : "ResultsRowIconSize") + PaneKey(Pane),
                steps[i].ToString(CultureInfo.InvariantCulture));

            e.Handled = true;   // do not also scroll the list
        }

        // ── Column resizing (details view) ───────────────────────
        // Hand-rolled rather than GridSplitter. A splitter resizes the grid it is IN, and the
        // header and the rows are separate Grids in separate templates that merely agree on
        // their widths - so a splitter in the header would have moved the header's columns and
        // left every row where it was. Writing the widths to the shared state instead moves
        // both, and moves the second pane's rows too, which is the behavior you want anyway:
        // the columns are a view preference, not a property of one listing.
        //
        // ONE RULE: a divider resizes the column to its LEFT and NOTHING ELSE. The columns to
        // its right keep the widths they had and simply slide over. That is what a divider does
        // in Explorer, and it is the only rule under which the thing that moves is always the
        // thing you were pointing at.
        //
        // Two earlier attempts had Name as a star column so it could absorb, which forced every
        // drag to move a SECOND column to keep the sum right - and when that one hit its floor
        // the remainder went to Name and the whole row shifted. That is why a drag on the right
        // of Size appeared to widen Location: nothing was wrong with the wiring, the model was
        // wrong. Every column carries its own width now and a star FILLER after the last one
        // takes the slack, so no column has to answer for another.
        //
        // The grip's Tag is the column it resizes, which is also the column it sits inside.

        private int    _colDrag = -1;   // column being resized, -1 for none
        private double _colDragX;       // x where the drag started
        private double _colDragStart;   // that column's width at the start

        private static string ColSettingKey(int column) => column switch
        {
            1 => "ResultsColName",
            2 => "ResultsColLocation",
            3 => "ResultsColSize",
            _ => "ResultsColModified",
        };

        private void SaveColumn(FilePane pane, int column)
            => Services.ThemeManager.SetSetting(ColSettingKey(column) + PaneKey(pane),
                   pane.ViewState.GetColumnWidth(column).ToString("0.#", CultureInfo.InvariantCulture));

        /// <summary>
        /// Tell each pane's OWN column state how much room ITS pane actually has.
        /// </summary>
        /// <remarks>
        /// Driven off the pane's CONTENT grid rather than the details header: the header is
        /// collapsed in list and icon view, so it stops reporting a width exactly when you might
        /// switch back to details at the new size.
        ///
        /// This is also what stops the window chrome being clipped. Fixed pixel columns give the
        /// details Grid a large minimum desired width, and WPF propagates that all the way up -
        /// so a window dragged narrower than the columns' total pushed its own title bar and
        /// footer off the right edge. Scaling the columns keeps that minimum below the window.
        /// Each pane fits itself now that column widths are per-pane (Steve, 2026-08-03) - there
        /// is no longer a shared state that had to fit the narrowest of the two.
        /// </remarks>
        internal void UpdateColumnFit()
        {
            foreach (var p in LivePanes())                       // Panes.cs
            {
                double w = p.PaneContent.ActualWidth;
                if (w <= 0) continue;                             // nothing measured yet

                // What the columns can have: the pane's content minus the icon column and the
                // row's own left and right padding (RowPad, which also clears the scrollbar).
                var state = p.ViewState;
                double icon = state.RowIconColumn.Value;
                double pad  = state.RowPad.Left + state.RowPad.Right;
                state.AvailableWidth = Math.Max(0, w - icon - pad);
            }
        }

        /// <summary>
        /// How wide the dragged column is allowed to get before it would push the last one off
        /// the right edge.
        /// </summary>
        /// <remarks>
        /// The rows do not scroll horizontally, so anything past the pane's edge is not
        /// off-screen, it is gone. The ceiling is what the header actually has minus what every
        /// other column is using, with a little kept back so the filler never reaches zero and
        /// the last heading always has somewhere to sit.
        /// </remarks>
        private double MaxWidthFor(int column)
        {
            double band = Pane.DetailsHeader.ActualWidth;
            if (band <= 0) return ResultsViewState.ColMaxWidth;   // not measured yet

            double icon = Pane.ViewState.RowIconColumn.Value;
            double room = band - icon - 48;                       // padding + the filler's floor
            return Math.Max(ResultsViewState.ColMinWidth,
                            room - Pane.ViewState.TotalWidthExcept(column));
        }

        internal void ColGrip_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement grip) return;
            if (!int.TryParse(grip.Tag as string, NumberStyles.Integer, CultureInfo.InvariantCulture,
                              out int column)) return;

            var state = Pane.ViewState;

            // Double-click a divider to put its column back to the width it shipped with. There
            // is no content-measuring auto-fit here: the list is virtualized and can hold six
            // figures of rows, so "fit the widest value" would mean walking every one of them.
            if (e.ClickCount == 2)
            {
                state.SetColumnWidth(column, state.DefaultColumnWidth(column));
                SaveColumn(Pane, column);
                e.Handled = true;
                return;
            }

            _colDrag      = column;
            _colDragStart = state.GetColumnWidth(column);
            _colDragX     = e.GetPosition(this).X;

            // Captured on the GRIP, not the window: the pointer leaves a 6px strip immediately
            // and without capture the very first move would be delivered to whatever it crossed.
            grip.CaptureMouse();
            e.Handled = true;
        }

        internal void ColGrip_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_colDrag < 0 || sender is not FrameworkElement grip || !grip.IsMouseCaptured) return;

            // Against the drag's own start, never the previous move. Accumulating deltas drifts
            // once the width clamps: the pointer keeps travelling past the limit and the column
            // then has to be dragged all the way back before it moves at all.
            double dx = e.GetPosition(this).X - _colDragX;

            double want = Math.Min(_colDragStart + dx, MaxWidthFor(_colDrag));
            Pane.ViewState.SetColumnWidth(_colDrag, want);
        }

        internal void ColGrip_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement grip) grip.ReleaseMouseCapture();
            if (_colDrag < 0) return;

            // Written once at the end rather than on every move - a drag is a hundred mouse
            // events and each Set would be a settings write.
            SaveColumn(Pane, _colDrag);
            _colDrag = -1;
        }
    }
}
