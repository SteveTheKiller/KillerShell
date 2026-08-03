using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading;
using System.Threading.Tasks;

using KillerShell.Models;

// The control behind a Processes/Services tab: a live, filterable, sortable list, toggling
// between every process on the machine and every Windows service. Partial to nothing - this is a
// stand-alone control, not a MainWindow partial - but it follows the same "own host, own control,
// MOVED not rebuilt between activations" rule TerminalControl and EditorControl already carry
// (TerminalTabs.cs / EditorTabs.cs): a Processes/Services tab has state too (the filter text, the
// current mode, the grid's own sort and scroll position, the refresh timer), and rebuilding it on
// every tab switch would throw all four away same as it would for a shell or a document.
//
// User-facing label is "Processes/Services" everywhere (rail tooltip, tab title, F1 card) now
// that this shows two views - the underlying mechanism (this class's own name, OpenTaskManager,
// RelaunchElevatedProcesses, the --processes CLI flag) all deliberately kept their existing
// "Process"/"TaskManager" names: renaming those would touch dozens of files for zero user-facing
// benefit and risks breaking the --processes flag other code already depends on.
//
// Built entirely in code rather than a separate .xaml. Shell/ carries no XAML file besides
// MainWindow.xaml - TerminalControl and EditorControl are both code-built subclasses too - so a
// UserControl here would be the first exception to that convention for no real reason: the whole
// surface is a filter box, a mode toggle, one DataGrid and one status line, none of which needs a
// designer.
namespace KillerShell.Shell
{
    internal sealed class ProcessListControl : Grid
    {
        internal enum ViewMode { Processes, Services }

        // ── Refresh cadence ──────────────────────────────────────
        /// <summary>
        /// How often the grid resamples every process (or every service) on the machine.
        /// </summary>
        /// <remarks>
        /// 1.5 seconds: fast enough that a runaway process or a kill you just issued shows up
        /// without the tab feeling stale, slow enough that the WMI query underneath (one round
        /// trip for the whole machine's command lines and paths, not one per process - see
        /// QueryWmiProcesses) does not become the very thing this tab exists to report on. Only
        /// whichever of Processes/Services is currently showing is resampled on a given tick -
        /// see the timer's Tick handler below - so switching to Services does not keep the process
        /// WMI query running in the background for a view nobody is looking at, and vice versa.
        /// </remarks>
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1.5);

        private ViewMode _mode = ViewMode.Processes;

        private readonly DispatcherTimer _timer;
        private readonly ObservableCollection<ProcessInfo> _items = new();
        private readonly ICollectionView _procView;
        private readonly Dictionary<int, ProcessInfo> _byPid = new();

        private readonly ObservableCollection<ServiceInfo> _svcItems = new();
        private readonly ICollectionView _svcView;
        private readonly Dictionary<string, ServiceInfo> _byName = new(StringComparer.OrdinalIgnoreCase);

        // CPU% has to be a DELTA, not a snapshot: Process.TotalProcessorTime is cumulative since
        // the process started, so reading it once and dividing by wall-clock-since-start would
        // report a process's LIFETIME average, not what it is doing right now - a shell that sat
        // idle for an hour after one busy second would show a number nobody could read anything
        // into. Two samples, taken RefreshInterval apart, are how every real task manager gets a
        // live percentage out of the same cumulative counter.
        private readonly Dictionary<int, TimeSpan> _lastCpuTime  = new();
        private readonly Dictionary<int, DateTime> _lastSampleAt = new();

        // Owner lookups are a per-instance WMI method INVOKE (GetOwner), which costs far more
        // than the one bulk SELECT the rest of a row comes from. A process's owner cannot change
        // for its lifetime, so once a PID has answered - even with "-" for "could not tell" - it
        // is never asked again.
        //
        // Concurrent, not a plain Dictionary: BuildSamples reads this on the refresh thread while
        // EnrichOwners below writes it from its OWN separate background thread - see the remark
        // there for why owner lookups were pulled out of BuildSamples in the first place.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _ownerCache = new();

        // PIDs an EnrichOwners pass is already chasing, so a slow WMI answer for one process does
        // not get asked again by the next tick before it has even come back.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _ownerPending = new();

        // E8FD (ViewAll/list): the same glyph ProcessTabs.cs's own ProcessesGlyph and the rail's
        // TaskManagerRailBtn already use for "a live list of what is running now" - reused here
        // rather than picked fresh, since it is the same concept.
        //
        // E90F (Repair - a wrench): NOT E713, the gear this app already spends on Settings
        // (EditorMenu.cs Str_Ed_Settings row, FilePane.xaml EditorGearBtn) - reusing that glyph
        // for Services would break the app's one-glyph-one-meaning rule the moment both buttons
        // were ever visible near each other. A wrench reads as "system services/maintenance"
        // without colliding with anything else in the app. Picked without rendering, same
        // caveat Shell/EventViewerTabs.cs's own EventViewerGlyph carries - worth a visual check
        // before shipping.
        private static readonly string ProcessesToggleGlyph = ((char)0xE8FD).ToString();
        private static readonly string ServicesToggleGlyph  = ((char)0xE90F).ToString();

        private readonly TextBox    _filterBox;
        private readonly Button     _modeToggleBtn;
        private readonly DataGrid   _grid;
        private readonly TextBlock  _statusLine;
        private readonly DispatcherTimer _statusClearTimer;

        // The two column sets a mode-toggle swaps between - see SetMode. Built once in BuildGrid;
        // never rebuilt on a later toggle, only shown/hidden by replacing grid.Columns wholesale.
        private DataGridColumn[] _processColumns = Array.Empty<DataGridColumn>();
        private DataGridColumn[] _serviceColumns = Array.Empty<DataGridColumn>();
        private Services.ColumnVisibilityMenu.Entry[] _processColEntries = Array.Empty<Services.ColumnVisibilityMenu.Entry>();
        private Services.ColumnVisibilityMenu.Entry[] _serviceColEntries = Array.Empty<Services.ColumnVisibilityMenu.Entry>();

        // Cancelled from Shutdown() - see the remark there. New one per control instance, not
        // reset between refreshes: EnrichOwners passes overlap fine (each is its own list of
        // PIDs, guarded from double-chasing the same PID by _ownerPending), so there is never a
        // reason to cancel one early except the whole control going away.
        private readonly CancellationTokenSource _ownerCts = new();

        internal ProcessListControl()
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var toolbar = BuildToolbar(out _filterBox, out _modeToggleBtn);
            SetRow(toolbar, 0);
            Children.Add(toolbar);

            // Processes is the default mode every time the tab is (re)built - there is no
            // persisted "last mode" (a fresh Processes/Services tab always opens on Processes,
            // matching how it behaved before Services existed).
            UpdateModeToggle();
            _modeToggleBtn.Click += (_, _) =>
                SetMode(_mode == ViewMode.Processes ? ViewMode.Services : ViewMode.Processes);

            _procView = CollectionViewSource.GetDefaultView(_items);
            _procView.Filter = ProcessFilterPredicate;

            _svcView = CollectionViewSource.GetDefaultView(_svcItems);
            _svcView.Filter = ServiceFilterPredicate;

            _filterBox.TextChanged += (_, _) =>
            {
                if (_mode == ViewMode.Processes) _procView.Refresh();
                else _svcView.Refresh();
            };

            _grid = BuildGrid();
            _grid.ItemsSource = _procView;
            SetRow(_grid, 1);
            Children.Add(_grid);

            _statusLine = BuildStatusLine();
            SetRow(_statusLine, 2);
            Children.Add(_statusLine);

            _timer = new DispatcherTimer { Interval = RefreshInterval };
            _timer.Tick += (_, _) =>
            {
                if (_mode == ViewMode.Processes) Refresh();
                else RefreshServices();
            };

            // Started on Loaded / stopped on Unloaded rather than for the tab's whole lifetime:
            // the control is MOVED between the visual tree and nowhere as tabs switch (like the
            // terminal and the editor), and Loaded/Unloaded fire on exactly that move - so a
            // Processes/Services tab sitting in the background costs nothing until it is looked
            // at again.
            Loaded   += (_, _) => { Refresh(); _timer.Start(); };
            Unloaded += (_, _) => _timer.Stop();

            _statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _statusClearTimer.Tick += (_, _) => { _statusClearTimer.Stop(); ShowStatus(string.Empty, error: false); };
        }

        /// <summary>
        /// Torn down when the tab closes (Shell/ProcessTabs.cs CloseProcessList), AND when the
        /// whole window closes with this tab still open (Shell/Session.cs OnClosing, via
        /// ProcessTabs.ShutdownAllProcessLists) - a Processes/Services tab left open at quit used
        /// to crash the app on exit with the same RaceOnRCWCleanup Managed Debugging Assistant
        /// error the first-load freeze fix already hit once (Steve, 2026-08-02): EnrichOwners'
        /// background thread can still be mid-WMI-call when the process starts tearing down, and
        /// a COM RCW finalized while another thread still has it live is exactly what that MDA
        /// exists to catch. Cancelling here stops EnrichOwners between PIDs rather than mid-call,
        /// so its thread runs out naturally instead of being torn down with a live WMI wrapper on
        /// it.
        /// </summary>
        internal void Shutdown()
        {
            _timer.Stop();
            _statusClearTimer.Stop();
            _ownerCts.Cancel();
        }

        /// <summary>Focus the filter box. Called after a tab switch lands on this control.</summary>
        internal void FocusFilter() => _filterBox.Focus();

        // ═══════════════════════════════════════════════════════════
        //  BUILD
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// The filter box plus the Processes/Services mode toggle, in the same toolbar row
        /// (Steve: "we need some sort of toggle up above near the search box"). A Grid rather
        /// than the bare TextBox row 0 used to be - the toggle button lives in a fixed-width
        /// Auto column to the left, the filter box takes the remaining width.
        /// </summary>
        /// <remarks>
        /// One icon button that switches modes on click, not two separate text buttons (Steve:
        /// "maybe it should be one button that just switches instead of two buttons") - its
        /// glyph is the only visual indicator of which mode is showing besides the column
        /// headers, so UpdateModeToggle keeps both the icon AND the tooltip in sync with _mode.
        /// </remarks>
        private static Grid BuildToolbar(out TextBox filterBox, out Button modeToggle)
        {
            var bar = new Grid { Margin = new Thickness(8, 8, 8, 6) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Same 26x26 / Padding(0) / SurfaceButton shape as every other icon-only button in
            // the app (Controls/EventDetailsDialog.xaml footer buttons, etc.) - Padding(0) is
            // required or a SurfaceButton this small clips its own glyph.
            modeToggle = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Margin = new Thickness(0, 0, 6, 0),
                Style = (Style)FindResourceStatic("SurfaceButton"),
            };
            SetColumn(modeToggle, 0);
            bar.Children.Add(modeToggle);

            // The implicit TextBox style (Controls.xaml: <Style TargetType="TextBox"
            // BasedOn="{StaticResource DarkTextBox}"/>) themes this the moment it lands in the
            // visual tree - nothing here has to name it.
            filterBox = new TextBox
            {
                Height = 26,
                Padding = new Thickness(6, 0, 6, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 12,
            };
            filterBox.SetResourceReference(ToolTipProperty, "Str_TT_ProcFilter");
            SetColumn(filterBox, 1);
            bar.Children.Add(filterBox);

            return bar;
        }

        /// <summary>
        /// Syncs the toggle button's glyph and tooltip to the CURRENT mode - the icon is the
        /// mode indicator (Processes glyph while showing Processes, Services glyph while showing
        /// Services), and the tooltip names what clicking will switch TO next, same "describe
        /// what happens next" shape the raw-XML toggle in Controls/EventDetailsDialog.xaml.cs
        /// (XmlToggle_Click) already uses for its own two-state tooltip swap.
        /// </summary>
        private void UpdateModeToggle()
        {
            bool showingProcesses = _mode == ViewMode.Processes;
            _modeToggleBtn.Content = showingProcesses ? ProcessesToggleGlyph : ServicesToggleGlyph;
            _modeToggleBtn.SetResourceReference(ToolTipProperty,
                showingProcesses ? "Str_TT_ProcToggleToServices" : "Str_TT_ProcToggleToProcesses");
        }

        private DataGrid BuildGrid()
        {
            var grid = new DataGrid
            {
                Margin = new Thickness(8, 0, 8, 8),
                AutoGenerateColumns = false,
                IsReadOnly = true,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                CanUserSortColumns = true,
                CanUserResizeColumns = true,
                SelectionMode = DataGridSelectionMode.Extended,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                ColumnHeaderHeight = 26,
                RowHeight = 24,
                AlternationCount = 2,
            };
            grid.SetResourceReference(DataGrid.ForegroundProperty, "TextBrush");
            grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "PaneBorderBrush");
            grid.RowStyle          = (Style)FindResourceStatic("DarkDataGridRow");
            grid.CellStyle         = (Style)FindResourceStatic("DarkDataGridCell");
            grid.ColumnHeaderStyle = (Style)FindResourceStatic("DarkDataGridColumnHeader");

            BuildProcessColumns();
            BuildServiceColumnsSet();

            // Restore both sets' persisted visibility up front, regardless of which mode is
            // showing first - a column hidden last session should stay hidden the moment its
            // mode is switched to, not only after the FIRST right-click rebuilds it.
            Services.ColumnVisibilityMenu.RestoreVisibility("Processes", _processColEntries);
            Services.ColumnVisibilityMenu.RestoreVisibility("Services",  _serviceColEntries);

            foreach (var c in _processColumns) grid.Columns.Add(c);

            // One shared handler rather than ColumnVisibilityMenu.AttachTo (which would wire its
            // own handler per call) - AttachTo assumes one column set per grid for its whole
            // life; this grid swaps between two, so it needs to pick the CURRENT mode's entries
            // at click time rather than the set that happened to be attached first. See the
            // BuildEntries/HandleHeaderRightClick split added to ColumnVisibilityMenu.cs for this.
            grid.PreviewMouseRightButtonUp += (_, e) => Services.ColumnVisibilityMenu.HandleHeaderRightClick(
                e, _mode == ViewMode.Processes ? "Processes" : "Services",
                _mode == ViewMode.Processes ? _processColEntries : _serviceColEntries);

            grid.ContextMenuOpening += Grid_ContextMenuOpening;
            grid.MouseDoubleClick   += Grid_MouseDoubleClick;
            grid.PreviewKeyDown     += Grid_PreviewKeyDown;
            return grid;
        }

        /// <summary>Builds _processColumns/_processColEntries - split out of the old BuildGrid so
        /// the Services set (BuildServiceColumnsSet) can sit right beside it and both can be built
        /// before either is actually added to the grid.</summary>
        private void BuildProcessColumns()
        {
            var name   = Col("Str_Col_ProcName", "Name", 180);
            var pid    = Col("Str_Col_ProcPid", "Pid", 70);
            var user   = Col("Str_Col_ProcUser", "User", 110);
            var cpu    = Col("Str_Col_ProcCpu", "CpuLabel", 70, sortMember: "CpuPercent");
            var memory = Col("Str_Col_ProcMemory", "MemoryLabel", 90, sortMember: "MemoryBytes");
            // Command line's own Visibility starts Collapsed here for the SAME reason the old
            // hardcoded default was: Name/Pid/User/Cpu/Memory/Path already fit one normal window
            // width, and Command line is the one column wide enough to force horizontal
            // scrolling on the rest. RestoreVisibility (BuildGrid) immediately overwrites this
            // with whatever was last persisted (or this same default, the first time).
            var cmd    = Col("Str_Col_ProcCommandLine", "CommandLine", 320, visible: false);
            var path   = Col("Str_Col_ProcPath", "Path", 320);

            _processColumns = new DataGridColumn[] { name, pid, user, cpu, memory, cmd, path };
            _processColEntries = Services.ColumnVisibilityMenu.BuildEntries(
                (name,   "Name",        "Str_Col_ProcName",        true),
                (pid,    "Pid",         "Str_Col_ProcPid",         true),
                (user,   "User",        "Str_Col_ProcUser",        true),
                (cpu,    "Cpu",         "Str_Col_ProcCpu",         true),
                (memory, "Memory",      "Str_Col_ProcMemory",      true),
                (cmd,    "CommandLine", "Str_Col_ProcCommandLine", false),
                (path,   "Path",        "Str_Col_ProcPath",        true));
        }

        /// <summary>Builds _serviceColumns/_serviceColEntries - the Services view's own column
        /// set, genuinely separate from the process one rather than forced onto ProcessInfo's
        /// columns, since the two data shapes do not overlap (Steve's own call, per the task).</summary>
        private void BuildServiceColumnsSet()
        {
            var name    = Col("Str_Col_SvcName",        "Name", 180);
            var display = Col("Str_Col_SvcDisplayName", "DisplayName", 220);
            var status  = Col("Str_Col_SvcStatus",      "Status", 110);
            var start   = Col("Str_Col_SvcStartType",   "StartupType", 110);
            var logon   = Col("Str_Col_SvcLogOnAs",     "LogOnAs", 150);
            var path    = Col("Str_Col_SvcPath",        "Path", 320);

            _serviceColumns = new DataGridColumn[] { name, display, status, start, logon, path };
            _serviceColEntries = Services.ColumnVisibilityMenu.BuildEntries(
                (name,    "Name",        "Str_Col_SvcName",        true),
                (display, "DisplayName", "Str_Col_SvcDisplayName", true),
                (status,  "Status",      "Str_Col_SvcStatus",      true),
                (start,   "StartType",   "Str_Col_SvcStartType",   true),
                (logon,   "LogOnAs",     "Str_Col_SvcLogOnAs",     true),
                (path,    "Path",        "Str_Col_SvcPath",        true));
        }

        /// <summary>Swaps the grid between the Processes and Services views: which column set is
        /// shown, which collection it is bound to, the filter box's tooltip, and which of the two
        /// toggle buttons reads as active. Called from both toggle buttons' Click handlers.</summary>
        private void SetMode(ViewMode mode)
        {
            if (_mode == mode) return;
            _mode = mode;

            UpdateModeToggle();

            _grid.Columns.Clear();
            foreach (var c in mode == ViewMode.Processes ? _processColumns : _serviceColumns)
                _grid.Columns.Add(c);

            _grid.ItemsSource = mode == ViewMode.Processes ? _procView : _svcView;

            _filterBox.SetResourceReference(ToolTipProperty,
                mode == ViewMode.Processes ? "Str_TT_ProcFilter" : "Str_TT_SvcFilter");

            // Refresh immediately rather than waiting up to RefreshInterval for the next tick -
            // switching modes and staring at a stale or empty grid for over a second reads as
            // broken.
            if (mode == ViewMode.Processes) Refresh();
            else RefreshServices();
        }

        private static DataGridTextColumn Col(string headerKey, string bindingPath, double width,
                                              string? sortMember = null, bool visible = true)
        {
            var header = new TextBlock();
            header.SetResourceReference(TextBlock.TextProperty, headerKey);
            return new DataGridTextColumn
            {
                Header         = header,
                Binding        = new Binding(bindingPath),
                Width          = new DataGridLength(width),
                SortMemberPath = sortMember ?? bindingPath,
                Visibility     = visible ? Visibility.Visible : Visibility.Collapsed,
            };
        }

        private static TextBlock BuildStatusLine()
        {
            var tb = new TextBlock
            {
                Margin = new Thickness(8, 0, 8, 6),
                FontSize = 11,
                Visibility = Visibility.Collapsed,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            return tb;
        }

        // Resolved through the application's merged dictionaries (Controls.xaml is merged in
        // App.xaml) rather than this.FindResource - at construction time the control has not
        // been added to a visual tree yet, so a resource lookup rooted on "this" would fail.
        private static object FindResourceStatic(string key)
            => Application.Current.TryFindResource(key)
               ?? throw new InvalidOperationException($"Missing resource: {key}");

        // ═══════════════════════════════════════════════════════════
        //  FILTER
        // ═══════════════════════════════════════════════════════════
        /// <summary>Broadened (Steve: "the search doesnt work well") to match Name OR Path OR
        /// User, not just Name - three plain IndexOf calls per row per keystroke, cheap even on a
        /// machine with hundreds of processes; nothing more expensive (no regex) was added.</summary>
        private bool ProcessFilterPredicate(object obj)
        {
            if (obj is not ProcessInfo p) return false;
            string q = _filterBox.Text;
            if (q.Length == 0) return true;

            return p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || p.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || p.User.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Same broadening, applied to the Services view: Name OR Display Name OR Path
        /// OR the Log On As account.</summary>
        private bool ServiceFilterPredicate(object obj)
        {
            if (obj is not ServiceInfo s) return false;
            string q = _filterBox.Text;
            if (q.Length == 0) return true;

            return s.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || s.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || s.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || s.LogOnAs.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ═══════════════════════════════════════════════════════════
        //  REFRESH - PROCESSES
        // ═══════════════════════════════════════════════════════════
        // True while a refresh's background half is in flight. The timer's Tick runs on the
        // UI thread's Dispatcher, and an async Refresh() hands that thread back the moment it
        // awaits BuildSamples below - if RefreshInterval elapses again before the FIRST
        // refresh's UI-thread half (ApplySamples) has run, a naive Tick handler would kick off
        // a second one overlapping the first. This guard is what stops that.
        private bool _refreshing;

        /// <summary>
        /// Kicks off one refresh of the Processes view. The actual work happens in two halves:
        /// BuildSamples, which does everything slow (Process enumeration, the WMI queries) on a
        /// background thread, and ApplySamples, which is the only part allowed to touch _items -
        /// it is an ObservableCollection bound to the grid, and a WPF collection may only be
        /// mutated from the UI thread.
        /// </summary>
        /// <remarks>
        /// This used to be synchronous, and it is why opening the tab froze the window: on the
        /// very first refresh every process is "new", so QueryOwner ran once PER PROCESS - a
        /// separate synchronous WMI round trip each - directly on the UI thread, for however
        /// many hundred processes happened to be running. Nothing here was a deadlock; it was
        /// just several seconds of the UI thread doing real, blocking work with nothing else
        /// able to run in the meantime, which looks exactly like a freeze from the outside.
        /// </remarks>
        private async void Refresh()
        {
            if (_refreshing) return;
            _refreshing = true;

            // Only the very first refresh is worth announcing - everything after that lands in
            // under a second now that owner lookups (see EnrichOwners) no longer sit in this
            // path, and a "Loading..." that flickered on and off every 1.5 seconds forever would
            // be worse than the silence it replaces.
            bool firstLoad = _items.Count == 0;
            if (firstLoad) ShowStatus(MainWindow.LocStatic("Str_Proc_Loading"), error: false, sticky: true);

            try
            {
                var now = DateTime.UtcNow;
                (List<ProcessSample> samples, HashSet<int> seen, List<int> needOwner) built;
                try
                {
                    // A dedicated (LongRunning) thread, not a pooled one from plain Task.Run.
                    // WMI's ManagementObjectSearcher/ManagementObject are COM RCWs, and the
                    // ThreadPool REUSES its threads - if the thread that created one of those
                    // wrappers gets handed to unrelated work while a finalizer is still
                    // cleaning the wrapper up, you get exactly the crash this used to throw:
                    // "RaceOnRCWCleanup - An attempt has been made to free an RCW that is in
                    // use." A LongRunning task gets its own thread that exits (and takes its
                    // RCWs' lifetime with it) when this one call is done, never handed to
                    // anything else.
                    built = await Task.Factory.StartNew(() => BuildSamples(now),
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                catch (Exception ex)
                {
                    // Surfaced rather than swallowed: a refresh that fails outright used to
                    // leave the grid silently empty forever with no way to tell why. Now at
                    // least the status line says something rather than nothing.
                    ShowStatus(string.Format(MainWindow.LocStatic("Str_Proc_RefreshFailed"), ex.Message),
                               error: true);
                    return;
                }

                ApplySamples(built.samples, built.seen);
                if (firstLoad) ShowStatus(string.Empty, error: false);
                if (built.needOwner.Count > 0) EnrichOwners(built.needOwner);
            }
            finally { _refreshing = false; }
        }

        /// <summary>
        /// Everything slow about a refresh, run off the UI thread: Process enumeration, the
        /// bulk WMI query, and any owner lookups a newly-seen PID still needs.
        /// </summary>
        /// <remarks>
        /// _lastCpuTime/_lastSampleAt are read and written here even though this runs on a
        /// background thread - safe only because _refreshing above guarantees a single Refresh()
        /// is ever in flight at once, so there is never a second reader or writer to race
        /// against. _ownerCache/_ownerPending are the exception: EnrichOwners below writes them
        /// from ITS OWN separate background thread, which can still be running when the next
        /// Refresh()'s BuildSamples starts - that is exactly why they are ConcurrentDictionary
        /// and not plain Dictionary.
        /// </remarks>
        private (List<ProcessSample> samples, HashSet<int> seen, List<int> needOwner) BuildSamples(DateTime now)
        {
            var live = Process.GetProcesses();
            var seen = new HashSet<int>(live.Length);
            var samples = new List<ProcessSample>(live.Length);
            var needOwner = new List<int>();

            // One bulk WMI query for the whole machine's command lines, executable paths AND
            // parent PIDs, rather than one query per process - the difference between a refresh
            // that costs a single round trip and one that costs a few hundred. ExecutablePath
            // comes back null/empty for a process we cannot see into rather than throwing, which
            // is exactly why WMI is used for this instead of Process.MainModule.FileName (that
            // throws Win32Exception for an elevated/protected process when this app is not
            // elevated). ParentProcessId rides along on the SAME query - it costs nothing extra
            // to ask WMI for one more column, unlike a second per-row lookup would.
            Dictionary<int, (string cmd, string path, string parentPid)> wmi = QueryWmiProcesses();

            // Process.GetProcesses() hands back live handles, not a snapshot struct - each one
            // has to be disposed or a tab left open for hours slowly leaks kernel handles, one
            // set every 1.5 seconds. try/finally rather than a using-per-iteration so a thrown
            // exception mid-row still reaches the dispose.
            foreach (var proc in live)
            {
                try
                {
                    int pid = proc.Id;
                    seen.Add(pid);

                    string name;
                    try { name = proc.ProcessName; }
                    catch { continue; }   // exited between GetProcesses and here

                    long mem;
                    try { mem = proc.WorkingSet64; }
                    catch { mem = 0; }

                    // CPU%: swallowed per-process, not per-refresh - a system process we cannot
                    // query for its processor time must not blank out every OTHER row's number.
                    double cpuPercent = 0;
                    try
                    {
                        var cpuTime = proc.TotalProcessorTime;
                        if (_lastCpuTime.TryGetValue(pid, out var prevCpu) &&
                            _lastSampleAt.TryGetValue(pid, out var prevAt))
                        {
                            double elapsedMs = (now - prevAt).TotalMilliseconds;
                            if (elapsedMs > 0)
                            {
                                double deltaMs = (cpuTime - prevCpu).TotalMilliseconds;
                                cpuPercent = Math.Max(0, Math.Round(
                                    deltaMs / elapsedMs / Environment.ProcessorCount * 100.0, 1));
                            }
                        }
                        _lastCpuTime[pid]  = cpuTime;
                        _lastSampleAt[pid] = now;
                    }
                    catch { /* cpuPercent stays 0 */ }

                    // Process.StartTime throws for a protected/elevated process this app is not
                    // running as - read defensively, per-process, no WMI round trip needed since
                    // Process already exposes it directly (unlike ParentProcessId).
                    string startTime = "-";
                    try { startTime = proc.StartTime.ToString("yyyy-MM-dd HH:mm:ss"); }
                    catch { /* startTime stays "-" */ }

                    string cmd = string.Empty, path = string.Empty, parentPid = "-";
                    if (wmi.TryGetValue(pid, out var info))
                    {
                        cmd       = info.cmd       ?? string.Empty;
                        path      = info.path      ?? string.Empty;
                        parentPid = info.parentPid ?? "-";
                    }

                    // Owner is the one field NOT worth waiting on here (see EnrichOwners) - a
                    // per-PID synchronous WMI method-invoke, once for every process, is what
                    // used to make the very first refresh take the better part of a minute with
                    // nothing on screen to show for it. Show "-" now, and let EnrichOwners fill
                    // it in as its own answers come back, one row at a time.
                    string owner = _ownerCache.TryGetValue(pid, out string? cached) ? cached : "-";
                    if (cached == null && _ownerPending.TryAdd(pid, 0)) needOwner.Add(pid);

                    samples.Add(new ProcessSample(pid, name, mem, cpuPercent, cmd, path, owner, parentPid, startTime));
                }
                finally { proc.Dispose(); }
            }

            return (samples, seen, needOwner);
        }

        /// <summary>
        /// The slow half of a row: one WMI method-invoke per still-unanswered PID, run on its own
        /// dedicated background thread so it never blocks a refresh tick or the grid's first
        /// paint. Fire-and-forget from Refresh() - it outlives the tick that started it, and the
        /// next several ticks' BuildSamples calls just keep showing "-" for whichever PIDs this
        /// has not gotten to yet.
        /// </summary>
        /// <remarks>
        /// LongRunning for the same COM-RCW-threading reason BuildSamples' own WMI work is: see
        /// the remark on Refresh(). Each row's answer is applied to the grid as soon as IT
        /// resolves, rather than batched until every PID in the list is done, so the User column
        /// fills in progressively instead of jumping all at once after the slowest process
        /// answers.
        /// </remarks>
        private void EnrichOwners(List<int> pids)
        {
            var token = _ownerCts.Token;
            Task.Factory.StartNew(() =>
            {
                foreach (int pid in pids)
                {
                    // Checked BEFORE the next WMI call, not after - Shutdown() (app close with
                    // this tab still open) wants this thread to run out cleanly between PIDs
                    // rather than being torn down mid-call with a live COM RCW still on it,
                    // which is what threw RaceOnRCWCleanup in the first place (Shutdown remark).
                    if (token.IsCancellationRequested) return;

                    string owner = QueryOwner(pid);
                    _ownerCache[pid] = owner;
                    _ownerPending.TryRemove(pid, out _);
                    if (token.IsCancellationRequested) return;   // window may be gone by now

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_byPid.TryGetValue(pid, out var row)) row.User = owner;
                    }));
                }
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>
        /// The only part of a refresh allowed to touch _items - back on the UI thread, applying
        /// what BuildSamples worked out on a background one.
        /// </summary>
        private void ApplySamples(List<ProcessSample> samples, HashSet<int> seen)
        {
            foreach (var s in samples)
            {
                if (!_byPid.TryGetValue(s.Pid, out var row))
                {
                    row = new ProcessInfo(s.Pid);
                    _byPid[s.Pid] = row;
                    _items.Add(row);
                }
                row.Name          = s.Name;
                row.MemoryBytes    = s.MemoryBytes;
                row.CpuPercent     = s.CpuPercent;
                row.CommandLine    = s.CommandLine;
                row.Path           = s.Path;
                row.User           = s.User;
                row.ParentPid      = s.ParentPid;
                row.StartTimeLabel = s.StartTimeLabel;
            }

            // Drop rows for processes that exited since the last tick, and their per-pid
            // bookkeeping with them - otherwise a PID reused later would inherit a stale CPU
            // baseline and report an absurd spike on its very first sample.
            foreach (int gone in _byPid.Keys.Where(pid => !seen.Contains(pid)).ToList())
            {
                _items.Remove(_byPid[gone]);
                _byPid.Remove(gone);
                _lastCpuTime.Remove(gone);
                _lastSampleAt.Remove(gone);
                _ownerCache.TryRemove(gone, out _);
                _ownerPending.TryRemove(gone, out _);
            }
        }

        /// <summary>
        /// Everything BuildSamples worked out about one process, carried from the background
        /// thread back to ApplySamples on the UI thread. Plain data - not ProcessInfo itself,
        /// which is a notifying model that ApplySamples mutates in place so the grid does not
        /// lose its selection/sort/scroll on every tick.
        /// </summary>
        private readonly struct ProcessSample
        {
            internal readonly int    Pid;
            internal readonly string Name;
            internal readonly long   MemoryBytes;
            internal readonly double CpuPercent;
            internal readonly string CommandLine;
            internal readonly string Path;
            internal readonly string User;
            internal readonly string ParentPid;
            internal readonly string StartTimeLabel;

            internal ProcessSample(int pid, string name, long memoryBytes, double cpuPercent,
                                   string commandLine, string path, string user,
                                   string parentPid, string startTimeLabel)
            {
                Pid = pid; Name = name; MemoryBytes = memoryBytes; CpuPercent = cpuPercent;
                CommandLine = commandLine; Path = path; User = user;
                ParentPid = parentPid; StartTimeLabel = startTimeLabel;
            }
        }

        private static Dictionary<int, (string cmd, string path, string parentPid)> QueryWmiProcesses()
        {
            var result = new Dictionary<int, (string, string, string)>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId, CommandLine, ExecutablePath FROM Win32_Process");
                using var rows = searcher.Get();
                foreach (ManagementObject row in rows)
                {
                    using (row)
                    {
                        int pid = Convert.ToInt32(row["ProcessId"]);
                        string cmd  = row["CommandLine"]    as string ?? string.Empty;
                        string path = row["ExecutablePath"] as string ?? string.Empty;
                        string parentPid = row["ParentProcessId"] is { } pp
                            ? Convert.ToInt32(pp).ToString(System.Globalization.CultureInfo.InvariantCulture)
                            : "-";
                        result[pid] = (cmd, path, parentPid);
                    }
                }
            }
            catch { /* WMI unavailable/locked down - every row just shows empty cmd/path/parent */ }
            return result;
        }

        /// <summary>
        /// "DOMAIN\user", or "-" when WMI's GetOwner() cannot answer (a system process, or one
        /// this process cannot see into without elevation). Called once per PID ever - see the
        /// cache comment on _ownerCache.
        /// </summary>
        private static string QueryOwner(int pid)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT Handle FROM Win32_Process WHERE ProcessId = {pid}");
                using var rows = searcher.Get();
                foreach (ManagementObject row in rows)
                {
                    using (row)
                    {
                        var args = new object[2];
                        uint hr = (uint)row.InvokeMethod("GetOwner", args);
                        if (hr == 0 && args[0] is string user && !string.IsNullOrEmpty(user))
                        {
                            string domain = args[1] as string ?? string.Empty;
                            return domain.Length > 0 ? domain + "\\" + user : user;
                        }
                    }
                }
            }
            catch { /* swallow - "-" is the answer for "could not tell" */ }
            return "-";
        }

        // ═══════════════════════════════════════════════════════════
        //  REFRESH - SERVICES
        // ═══════════════════════════════════════════════════════════
        private bool _svcRefreshing;

        /// <summary>Same two-halves shape as Refresh() above: BuildServiceSamples does the slow
        /// enumeration off the UI thread, ApplyServiceSamples is the only part allowed to touch
        /// _svcItems.</summary>
        private async void RefreshServices()
        {
            if (_svcRefreshing) return;
            _svcRefreshing = true;

            bool firstLoad = _svcItems.Count == 0;
            if (firstLoad) ShowStatus(MainWindow.LocStatic("Str_Svc_Loading"), error: false, sticky: true);

            try
            {
                (List<ServiceSample> samples, HashSet<string> seen) built;
                try
                {
                    // LongRunning for the same reason Refresh()'s BuildSamples is: WMI's
                    // ManagementObjectSearcher/ManagementObject are COM RCWs that must not be
                    // handed to a pooled ThreadPool thread that might get reused mid-cleanup.
                    built = await Task.Factory.StartNew(BuildServiceSamples,
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                catch (Exception ex)
                {
                    ShowStatus(string.Format(MainWindow.LocStatic("Str_Svc_RefreshFailed"), ex.Message),
                               error: true);
                    return;
                }

                ApplyServiceSamples(built.samples, built.seen);
                if (firstLoad) ShowStatus(string.Empty, error: false);
            }
            finally { _svcRefreshing = false; }
        }

        /// <summary>
        /// Everything slow about a services refresh, run off the UI thread:
        /// ServiceController.GetServices() for the base list (Name/DisplayName/Status/CanStop),
        /// cross-referenced against ONE bulk WMI Win32_Service query (QueryWmiServices) for the
        /// three fields ServiceController does not expose at all - StartMode, PathName, StartName
        /// - the same "one bulk query, not one per row" discipline BuildSamples already follows
        /// for processes.
        /// </summary>
        private (List<ServiceSample> samples, HashSet<string> seen) BuildServiceSamples()
        {
            var wmi = QueryWmiServices();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var samples = new List<ServiceSample>();

            ServiceController[] controllers = ServiceController.GetServices();
            foreach (var sc in controllers)
            {
                try
                {
                    string name;
                    try { name = sc.ServiceName; }
                    catch { continue; }
                    seen.Add(name);

                    string status;
                    try { status = sc.Status.ToString(); }
                    catch { status = "-"; }

                    bool canStop;
                    try { canStop = sc.CanStop; }
                    catch { canStop = false; }

                    string displayName;
                    try { displayName = sc.DisplayName; }
                    catch { displayName = name; }

                    string startupType = string.Empty, path = string.Empty, logOnAs = string.Empty, description = string.Empty;
                    if (wmi.TryGetValue(name, out var info))
                    {
                        startupType = FriendlyStartMode(info.startMode);
                        path        = info.path;
                        logOnAs     = info.logOnAs;
                        description = info.description;
                    }

                    samples.Add(new ServiceSample(name, displayName, status, startupType, logOnAs, path, description, canStop));
                }
                finally { sc.Dispose(); }
            }

            return (samples, seen);
        }

        private void ApplyServiceSamples(List<ServiceSample> samples, HashSet<string> seen)
        {
            foreach (var s in samples)
            {
                if (!_byName.TryGetValue(s.Name, out var row))
                {
                    row = new ServiceInfo(s.Name);
                    _byName[s.Name] = row;
                    _svcItems.Add(row);
                }
                row.DisplayName = s.DisplayName;
                row.Status      = s.Status;
                row.StartupType = s.StartupType;
                row.LogOnAs     = s.LogOnAs;
                row.Path        = s.Path;
                row.Description = s.Description;
                row.CanStop     = s.CanStop;
            }

            // Drop rows for services no longer present (uninstalled between ticks) - rare, but
            // the same tidy-up ApplySamples already does for exited processes.
            foreach (string gone in _byName.Keys.Where(n => !seen.Contains(n)).ToList())
            {
                _svcItems.Remove(_byName[gone]);
                _byName.Remove(gone);
            }
        }

        private readonly struct ServiceSample
        {
            internal readonly string Name;
            internal readonly string DisplayName;
            internal readonly string Status;
            internal readonly string StartupType;
            internal readonly string LogOnAs;
            internal readonly string Path;
            internal readonly string Description;
            internal readonly bool   CanStop;

            internal ServiceSample(string name, string displayName, string status, string startupType,
                                   string logOnAs, string path, string description, bool canStop)
            {
                Name = name; DisplayName = displayName; Status = status; StartupType = startupType;
                LogOnAs = logOnAs; Path = path; Description = description; CanStop = canStop;
            }
        }

        /// <summary>One bulk Win32_Service query for the whole machine's startup type, path and
        /// log-on account - never one per row. Keyed by service Name (case-insensitive, matching
        /// ServiceController.ServiceName's own comparison), not by any numeric id - a service has
        /// no PID-equivalent identity the way a process does.</summary>
        private static Dictionary<string, (string startMode, string path, string logOnAs, string description)> QueryWmiServices()
        {
            var result = new Dictionary<string, (string, string, string, string)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, StartMode, PathName, StartName, Description FROM Win32_Service");
                using var rows = searcher.Get();
                foreach (ManagementObject row in rows)
                {
                    using (row)
                    {
                        string name = row["Name"] as string ?? string.Empty;
                        if (name.Length == 0) continue;
                        string startMode   = row["StartMode"]   as string ?? string.Empty;
                        string path        = row["PathName"]    as string ?? string.Empty;
                        string logOnAs     = row["StartName"]   as string ?? string.Empty;
                        string description = row["Description"] as string ?? string.Empty;
                        result[name] = (startMode, path, logOnAs, description);
                    }
                }
            }
            catch { /* WMI unavailable/locked down - every row falls back to ServiceController alone */ }
            return result;
        }

        /// <summary>Win32_Service.StartMode comes back as "Auto"/"Manual"/"Disabled"/"Boot"/
        /// "System" - the last two are kernel driver states Win32_Service should not actually
        /// report for a real service, kept here only so an unexpected value still shows
        /// something readable instead of the raw WMI string. Does not distinguish "Automatic"
        /// from "Automatic (Delayed Start)" - that needs a registry read WMI does not expose,
        /// and plain "Automatic" is accurate as far as it goes.</summary>
        private static string FriendlyStartMode(string raw) => raw switch
        {
            "Auto"     => "Automatic",
            "Manual"   => "Manual",
            "Disabled" => "Disabled",
            "Boot"     => "Boot",
            "System"   => "System",
            _          => raw,
        };

        /// <summary>The exe path out of a service's (often argument-carrying) PathName - "svchost
        /// -k netsvcs" and quoted-with-arguments paths both need this before GetDirectoryName can
        /// find the actual folder. Same leading-token-strip technique ExtractArguments below uses
        /// for a process's command line, just keeping the FIRST token instead of discarding it.</summary>
        private static string ExtractExePath(string pathName)
        {
            if (string.IsNullOrEmpty(pathName)) return string.Empty;
            string s = pathName.Trim();
            if (s.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = s.IndexOf('"', 1);
                return end > 0 ? s.Substring(1, end - 1) : s.Trim('"');
            }
            int sp = s.IndexOf(' ');
            return sp > 0 ? s.Substring(0, sp) : s;
        }

        // ═══════════════════════════════════════════════════════════
        //  STATUS LINE  -  the themed stand-in for a Win32 message box
        // ═══════════════════════════════════════════════════════════
        private void ShowStatus(string text, bool error, bool sticky = false)
        {
            _statusLine.Text = text;
            _statusLine.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _statusLine.SetResourceReference(TextBlock.ForegroundProperty,
                error ? "DangerRed" : "MutedTextBrush");

            // Errors stick until something else replaces them - a refresh failing on every
            // 1.5-second tick would otherwise flash the message and clear it again well before
            // anyone reading the screen a moment later ever saw it. The "Loading..." message
            // passes sticky for the same reason: Refresh() clears it itself the moment the first
            // batch of rows lands, so the 4-second auto-clear would either race that or do
            // nothing useful either way.
            _statusClearTimer.Stop();
            if (text.Length > 0 && !error && !sticky) _statusClearTimer.Start();
        }

        // ═══════════════════════════════════════════════════════════
        //  ACTIONS  -  right-click / double-click / keyboard on a row
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Fired for "Open file location" - process OR service. Handled in Shell/ProcessTabs.cs,
        /// which is the only place that can act on it - it has to create/activate a browse tab,
        /// and this control has no idea which pane or window it lives in.
        /// </summary>
        internal event Action<string>? OpenFileLocationRequested;

        private void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (_mode == ViewMode.Processes)
            {
                if (_grid.SelectedItem is not ProcessInfo p) { e.Handled = true; return; }
                BuildProcessContextMenu(p);
            }
            else
            {
                if (_grid.SelectedItem is not ServiceInfo s) { e.Handled = true; return; }
                BuildServiceContextMenu(s);
            }
            e.Handled = true;
        }

        /// <summary>Shared MenuItem-builder shape, used by both the process and the service
        /// context menus - same icon-in-a-Viewbox pattern EventViewerControl's own menu uses,
        /// with an added InputGestureText parameter so the local keyboard shortcut (see
        /// Grid_PreviewKeyDown) shows up right-aligned on the row, the same column KillerPDF's
        /// own MenuItems use for their shortcuts (Controls/Controls.xaml).</summary>
        private static MenuItem AddMenuItem(ContextMenu menu, string headerKey, string glyph,
                                            RoutedEventHandler click, bool enabled = true, string gesture = "")
        {
            var item = new MenuItem { IsEnabled = enabled, InputGestureText = gesture };
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, headerKey);
            var icon = new TextBlock { Text = glyph };
            icon.SetResourceReference(FrameworkElement.StyleProperty, "MenuGlyph");
            var iconBox = new Viewbox { Width = 14, Height = 14, Stretch = Stretch.Uniform, Child = icon };
            item.Icon = iconBox;
            item.Click += click;
            menu.Items.Add(item);
            return item;
        }

        private void BuildProcessContextMenu(ProcessInfo p)
        {
            var menu = new ContextMenu { PlacementTarget = _grid };

            // Codepoints, not literal PUA characters - a literal glyph does not survive tooling
            // (CLAUDE.md), which is exactly why every other glyph in this app is set this way.

            // E838: the same folder glyph the rest of the family uses for "show me where this
            // lives" (KillerNotes' data-folder button, Killendar's dialog data-folder row).
            AddMenuItem(menu, "Str_Menu_ProcOpenLocation", ((char)0xE838).ToString(), (_, _) =>
            {
                if (p.HasPath) OpenFileLocationRequested?.Invoke(
                    System.IO.Path.GetDirectoryName(p.Path) ?? string.Empty);
            }, enabled: p.HasPath, gesture: "Ctrl+O");

            // E711: a cross, the same glyph a dead shell's tab wears when its process exits
            // (TerminalTabs.cs) - so "this ends the process" reads the same way twice in the app.
            AddMenuItem(menu, "Str_Menu_ProcKill", ((char)0xE711).ToString(), (_, _) => KillWithConfirm(p),
                gesture: "Del");

            // E777 (Refresh/ReplyMirrored reads as "start over"): kill then relaunch from Path.
            AddMenuItem(menu, "Str_Menu_ProcRestart", ((char)0xE777).ToString(), (_, _) => RestartProcess(p, elevated: false),
                enabled: p.HasPath, gesture: "Ctrl+R");

            // E7EF: the shield, the same elevation glyph the terminal rail's admin rows use
            // (MainWindow.xaml TermAdminBtn / RailShellPsAdmin_Click) - one glyph means one thing
            // everywhere in the app.
            AddMenuItem(menu, "Str_Menu_ProcRunAsAdmin", ((char)0xE7EF).ToString(), (_, _) => RestartProcess(p, elevated: true),
                enabled: p.HasPath, gesture: "Ctrl+Shift+A");

            menu.IsOpen = true;
        }

        private void BuildServiceContextMenu(ServiceInfo s)
        {
            var menu = new ContextMenu { PlacementTarget = _grid };

            // E838: same folder glyph and same event as the process menu's "Open file location" -
            // conceptually identical (open the folder containing the service's executable).
            AddMenuItem(menu, "Str_Menu_ProcOpenLocation", ((char)0xE838).ToString(), (_, _) =>
            {
                if (s.HasPath) OpenFileLocationRequested?.Invoke(
                    System.IO.Path.GetDirectoryName(ExtractExePath(s.Path)) ?? string.Empty);
            }, enabled: s.HasPath, gesture: "Ctrl+O");

            // E768 (Play): starts a stopped service. Disabled while already running.
            AddMenuItem(menu, "Str_Menu_SvcStart", ((char)0xE768).ToString(), (_, _) => StartServiceWithConfirm(s),
                enabled: !s.IsRunning, gesture: "Ctrl+S");

            // E71A (Stop): stops a running, stoppable service. Disabled while not running, or
            // while ServiceController.CanStop says Windows will refuse anyway.
            AddMenuItem(menu, "Str_Menu_SvcStop", ((char)0xE71A).ToString(), (_, _) => StopServiceWithConfirm(s),
                enabled: s.IsRunning && s.CanStop, gesture: "Del");

            // E777: the SAME "start over" glyph the process menu's Restart already uses - one
            // glyph, one meaning, everywhere in the app (Steve's own rule for this pass).
            AddMenuItem(menu, "Str_Menu_ProcRestart", ((char)0xE777).ToString(), (_, _) => RestartServiceWithConfirm(s),
                enabled: s.IsRunning && s.CanStop, gesture: "Ctrl+R");

            menu.IsOpen = true;
        }

        /// <summary>Double-click a row - process or service - opens the matching details dialog,
        /// mirroring EventViewerControl.Grid_MouseDoubleClick exactly: hands the dialog the FULL
        /// ordered list straight off the current view (its current sort/filter, not a stale
        /// unsorted copy) plus the clicked row's index into it, so the dialog's own Previous/Next
        /// buttons page through exactly what the grid was showing.</summary>
        private void Grid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_mode == ViewMode.Processes) OpenProcessDetails();
            else OpenServiceDetails();
        }

        private void OpenProcessDetails()
        {
            if (_grid.SelectedItem is not ProcessInfo entry) return;
            var ordered = _procView.Cast<ProcessInfo>().ToList();
            int index = ordered.IndexOf(entry);
            if (index < 0) index = 0;

            var dlg = new ProcessDetailsDialog(ordered, index) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
        }

        private void OpenServiceDetails()
        {
            if (_grid.SelectedItem is not ServiceInfo entry) return;
            var ordered = _svcView.Cast<ServiceInfo>().ToList();
            int index = ordered.IndexOf(entry);
            if (index < 0) index = 0;

            var dlg = new ServiceDetailsDialog(ordered, index) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
        }

        /// <summary>
        /// Local keyboard shortcuts for every right-click action, plus the Processes/Services
        /// mode toggle, scoped to the grid itself (like the editor's local Ctrl+G/Ctrl+S) rather
        /// than MainWindow's global table - the row-specific ones only fire while a row is
        /// selected and the event actually reached the grid. Safe against the window-level
        /// table: MainWindow.xaml.cs's Window_PreviewKeyDown already bows out for every
        /// non-window chord while ProcessListHasFocus is true (see the remark there), so none of
        /// Delete/Ctrl+R/Ctrl+Shift+A/Ctrl+O/Ctrl+S/Ctrl+. ever reach the window's own handler
        /// first - verified against MainWindow's IsWindowChord list, none of these six chords
        /// are in it.
        /// </summary>
        private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl  = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            bool none  = Keyboard.Modifiers == ModifierKeys.None;

            // Ctrl+Tab was the first choice - it is the app's own "next tab" chord
            // (MainWindow.xaml.cs Window_PreviewKeyDown, IsWindowChord in TerminalTabs.cs), so it
            // was already taken before this shortcut existed. Ctrl+. is free, local to this grid
            // like the five below, and does not need a selected row - it works whichever mode is
            // showing, with nothing selected at all.
            if (ctrl && !shift && e.Key == Key.OemPeriod)
            {
                SetMode(_mode == ViewMode.Processes ? ViewMode.Services : ViewMode.Processes);
                e.Handled = true;
                return;
            }

            if (_mode == ViewMode.Processes)
            {
                if (_grid.SelectedItem is not ProcessInfo p) return;

                if (none && e.Key == Key.Delete) { KillWithConfirm(p); e.Handled = true; return; }
                if (ctrl && !shift && e.Key == Key.R) { if (p.HasPath) RestartProcess(p, elevated: false); e.Handled = true; return; }
                if (ctrl && shift  && e.Key == Key.A) { if (p.HasPath) RestartProcess(p, elevated: true);  e.Handled = true; return; }
                if (ctrl && !shift && e.Key == Key.O)
                {
                    if (p.HasPath) OpenFileLocationRequested?.Invoke(System.IO.Path.GetDirectoryName(p.Path) ?? string.Empty);
                    e.Handled = true;
                    return;
                }
                if (none && (e.Key == Key.Enter || e.Key == Key.Space)) { OpenProcessDetails(); e.Handled = true; return; }
            }
            else
            {
                if (_grid.SelectedItem is not ServiceInfo s) return;

                if (none && e.Key == Key.Delete) { if (s.IsRunning && s.CanStop) StopServiceWithConfirm(s); e.Handled = true; return; }
                if (ctrl && !shift && e.Key == Key.R) { if (s.IsRunning && s.CanStop) RestartServiceWithConfirm(s); e.Handled = true; return; }
                if (ctrl && !shift && e.Key == Key.S) { if (!s.IsRunning) StartServiceWithConfirm(s); e.Handled = true; return; }
                if (ctrl && !shift && e.Key == Key.O)
                {
                    if (s.HasPath) OpenFileLocationRequested?.Invoke(System.IO.Path.GetDirectoryName(ExtractExePath(s.Path)) ?? string.Empty);
                    e.Handled = true;
                    return;
                }
                if (none && (e.Key == Key.Enter || e.Key == Key.Space)) { OpenServiceDetails(); e.Handled = true; return; }
            }
        }

        /// <summary>
        /// A themed confirm before ending a process - this app has no stock Win32 message boxes
        /// left (CHANGELOG 1.0.2), and a kill is the one destructive action this tab offers.
        /// </summary>
        private void KillWithConfirm(ProcessInfo p)
        {
            string msg = string.Format(MainWindow.LocStatic("Str_Dlg_ProcKillMsg"), p.Name, p.Pid);
            var owner = Window.GetWindow(this);
            var dlg = new ConfirmDialog(msg, p.HasPath ? p.Path : null,
                                        MainWindow.LocStatic("Str_Menu_ProcKill")) { Owner = owner };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            try
            {
                using var proc = Process.GetProcessById(p.Pid);
                proc.Kill();
                ShowStatus(string.Format(MainWindow.LocStatic("Str_Proc_Killed"), p.Name), error: false);
            }
            catch (ArgumentException)
            {
                // Already gone by the time Kill() ran - not an error worth surfacing.
                ShowStatus(string.Format(MainWindow.LocStatic("Str_Proc_AlreadyExited"), p.Name), error: false);
            }
            catch (Exception ex) when (ex is Win32Exception
                                             or InvalidOperationException
                                             or NotSupportedException)
            {
                ShowStatus(string.Format(MainWindow.LocStatic("Str_Proc_KillFailed"), p.Name, ex.Message),
                           error: true);
            }
        }

        /// <summary>
        /// Kill the current instance and relaunch from its own path - optionally elevated. Not
        /// offered when Path is empty: there is nothing here to relaunch FROM, and a menu item
        /// that fails every time it is clicked is worse than one that is disabled.
        /// </summary>
        /// <remarks>
        /// "Run as administrator" only makes sense here as a way to LAUNCH the replacement
        /// elevated, not as an operation on the process that is already running - Windows has no
        /// API to hand an existing process a higher token, so the only honest meaning of
        /// "run this as admin" in a task manager is "end it and start it again, elevated".
        /// </remarks>
        private void RestartProcess(ProcessInfo p, bool elevated)
        {
            if (!p.HasPath) return;

            string path = p.Path;
            string args = ExtractArguments(p.CommandLine);

            try
            {
                using (var proc = Process.GetProcessById(p.Pid)) proc.Kill();
            }
            catch (ArgumentException) { /* already gone - fine, still relaunch */ }
            catch (Exception ex) when (ex is Win32Exception
                                             or InvalidOperationException
                                             or NotSupportedException)
            {
                ShowStatus(string.Format(MainWindow.LocStatic("Str_Proc_KillFailed"), p.Name, ex.Message),
                           error: true);
                return;
            }

            try
            {
                var psi = new ProcessStartInfo(path, args) { UseShellExecute = true };
                if (elevated) psi.Verb = "runas";
                Process.Start(psi);
                ShowStatus(string.Format(MainWindow.LocStatic("Str_Proc_Restarted"), p.Name), error: false);
            }
            catch (Exception ex) when (ex is Win32Exception
                                             or InvalidOperationException)
            {
                // Covers a canceled UAC prompt (ERROR_CANCELLED, Win32Exception) as well as a
                // launch failure - both land here rather than crashing the refresh loop.
                ShowStatus(string.Format(MainWindow.LocStatic("Str_Proc_RestartFailed"), p.Name, ex.Message),
                           error: true);
            }
        }

        /// <summary>Best-effort: the command line minus its own leading exe path/token.</summary>
        private static string ExtractArguments(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return string.Empty;

            // The first token is usually the exe itself, quoted or not. Anything after it is
            // "arguments" in the loose sense a relaunch needs - exact reconstruction is not
            // possible from a flattened command line, and this is a best-effort restart, not a
            // guarantee of identical arguments.
            string rest = commandLine;
            if (rest.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = rest.IndexOf('"', 1);
                rest = end > 0 ? rest.Substring(end + 1) : string.Empty;
            }
            else
            {
                int sp = rest.IndexOf(' ');
                rest = sp > 0 ? rest.Substring(sp + 1) : string.Empty;
            }
            return rest.Trim();
        }

        // ═══════════════════════════════════════════════════════════
        //  ACTIONS  -  SERVICES  -  Start/Stop/Restart, each behind the same themed confirm
        //  dialog KillWithConfirm already uses, each running off the UI thread since
        //  ServiceController.Start/Stop/WaitForStatus can block for real.
        // ═══════════════════════════════════════════════════════════
        private void StartServiceWithConfirm(ServiceInfo s)
        {
            string msg = string.Format(MainWindow.LocStatic("Str_Dlg_SvcStartMsg"), s.DisplayName, s.Name);
            var dlg = new ConfirmDialog(msg, null, MainWindow.LocStatic("Str_Menu_SvcStart"))
                { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            RunServiceAction(s, sc => sc.Start(), ServiceControllerStatus.Running,
                "Str_Svc_Started", "Str_Svc_StartFailed");
        }

        private void StopServiceWithConfirm(ServiceInfo s)
        {
            string msg = string.Format(MainWindow.LocStatic("Str_Dlg_SvcStopMsg"), s.DisplayName, s.Name);
            var dlg = new ConfirmDialog(msg, null, MainWindow.LocStatic("Str_Menu_SvcStop"))
                { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            RunServiceAction(s, sc => sc.Stop(), ServiceControllerStatus.Stopped,
                "Str_Svc_Stopped", "Str_Svc_StopFailed");
        }

        /// <summary>Stop then start - not ServiceController.Stop()+Start() run back to back
        /// without waiting, which would ask Windows to start a service still mid-shutdown and
        /// fail. Its own method rather than two RunServiceAction calls chained: it needs to wait
        /// for Stopped BEFORE issuing Start, which a single fire-and-forget action delegate
        /// cannot express.</summary>
        private void RestartServiceWithConfirm(ServiceInfo s)
        {
            string msg = string.Format(MainWindow.LocStatic("Str_Dlg_SvcRestartMsg"), s.DisplayName, s.Name);
            var dlg = new ConfirmDialog(msg, null, MainWindow.LocStatic("Str_Menu_ProcRestart"))
                { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            string name = s.Name, display = s.DisplayName;
            Task.Factory.StartNew(() =>
            {
                try
                {
                    using var sc = new ServiceController(name);
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    }
                    sc.Refresh();
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() => ShowStatus(
                        string.Format(MainWindow.LocStatic("Str_Svc_RestartFailed"), display, ex.Message), error: true)));
                    return;
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ShowStatus(string.Format(MainWindow.LocStatic("Str_Svc_Restarted"), display), error: false);
                    RefreshServices();
                }));
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>Shared Start/Stop runner: opens its OWN ServiceController by name (the row's
        /// own controller was only ever used to read CanStop during the bulk enumeration, and is
        /// disposed by the time an action runs) on a background thread, waits for the target
        /// status, then reports back and pulls a fresh row rather than waiting for the next
        /// 1.5-second tick.</summary>
        private void RunServiceAction(ServiceInfo s, Action<ServiceController> action,
                                      ServiceControllerStatus waitFor, string successKey, string failKey)
        {
            string name = s.Name, display = s.DisplayName;
            Task.Factory.StartNew(() =>
            {
                try
                {
                    using var sc = new ServiceController(name);
                    action(sc);
                    sc.WaitForStatus(waitFor, TimeSpan.FromSeconds(15));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() => ShowStatus(
                        string.Format(MainWindow.LocStatic(failKey), display, ex.Message), error: true)));
                    return;
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ShowStatus(string.Format(MainWindow.LocStatic(successKey), display), error: false);
                    RefreshServices();
                }));
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }
}
