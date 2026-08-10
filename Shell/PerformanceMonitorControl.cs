using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

// The control behind a Performance Monitor tab: a scrolling TWO-COLUMN GRID of full-size cells,
// one per monitored item - CPU, RAM, one per PHYSICAL disk, one per network adapter, one per
// GPU - each enumerated from the machine rather than assumed to be exactly one, in KillerShell's
// own retro-terminal language: MonitorCellBrush cards, MonoFont readouts. Every cell carries its
// own big live graph(s) and numeric fields, all live at once - no master/detail, no selection
// (a grid of full-size cells with the graph and info inside each, replacing the old
// Task-Manager-style tile list + detail panel).
//
// The grid is the user's to arrange: each cell is 1 or 2 columns wide (the header's width toggle),
// and dragging a cell's header reorders the cells live. Order and widths persist app-wide in ONE
// setting ("PerfLayout" - "id:span|id:span|...", ids stable per metric, see CellId) so the layout
// survives restarts and unknown/new hardware simply appends in natural order.
//
// Same "own host, own control, MOVED not rebuilt between activations" rule ProcessListControl and
// EventViewerControl already carry (Shell/ProcessTabs.cs / Shell/EventViewerTabs.cs): a
// Performance tab has state too (the refresh timer, and - more than either of those - a whole
// window of sparkline history per metric, per tile, that a rebuild would throw away every time
// you switched back to it).
//
// Built entirely in code rather than a separate .xaml, same convention every other Shell/ control
// follows (see the file header on ProcessListControl.cs) - there is nothing here a designer would
// help with.
namespace KillerShell.Shell
{
    internal sealed class PerformanceMonitorControl : Grid
    {
        // ── Refresh cadence ──────────────────────────────────────
        /// <summary>
        /// How often the live gauges resample. One second: this is the one tab in the app whose
        /// whole point is to look "live" the way a classic Task Manager graph does - much slower
        /// and the trace would read as sluggish, much faster and the PerformanceCounter reads
        /// (cheap, but not free four-plus times over every tick) would be the very overhead this
        /// tab exists to report on.
        /// </summary>
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

        /// <summary>How many samples each sparkline keeps - one minute of history at a
        /// one-second interval, the same rough window a classic Task Manager graph shows.</summary>
        private const int HistorySamples = 60;

        private readonly DispatcherTimer _timer;

        private readonly TextBlock _staticInfoText;
        private readonly TextBlock _statusLine;
        private readonly DispatcherTimer _statusClearTimer;

        // ── Cell-grid state ──────────────────────────────────────
        private Grid _cellsGrid = null!;

        // _tiles is the DISPLAY ORDER (drag-to-reorder rearranges it); _gpuTiles keeps its
        // BUILD order forever - SampleGpus pairs sorted LUIDs against it by index, so letting a
        // drag reorder it would relabel one adapter's activity as another's.
        private readonly List<MetricTile> _tiles = [];
        private readonly List<MetricTile> _gpuTiles = [];

        // Drag-to-reorder state: the cell whose header is held, where the press started (grid
        // space), and whether the press has travelled far enough to count as a drag.
        private MetricTile? _dragTile;
        private Point _dragStart;
        private bool _dragActive;

        private bool _countersAvailable;

        // GPU Engine / GPU Adapter Memory instances are per-PROCESS, not per-adapter, so unlike
        // every other counter here they cannot be created once up front - they are created and
        // torn down as processes come and go, inside SampleGpus. IMPORTANT: the instance-name
        // LIST itself (PerformanceCounterCategory.GetInstanceNames()) is only re-read every
        // GpuRescanIntervalTicks ticks, not every tick - see the long remark on SampleGpus for
        // why (calling GetInstanceNames() on these two extensible/dynamic categories once a
        // second was spamming the Windows Application log with unrelated Perflib provider-reload
        // errors, real bug caught 2026-08-02). Already-created counters still get .NextValue()
        // every tick regardless, so the live number keeps its 1-second cadence.
        private readonly Dictionary<string, PerformanceCounter> _gpuEngineCounters = [];
        private readonly Dictionary<string, PerformanceCounter> _gpuMemDedicatedCounters = [];
        private readonly Dictionary<string, PerformanceCounter> _gpuMemSharedCounters = [];
        private bool _gpuEngineAvailable;
        private bool _gpuMemoryAvailable;

        /// <summary>How many 1-second ticks between GetInstanceNames() re-scans of the GPU
        /// Engine / GPU Adapter Memory categories. 8 seconds: fast enough to pick up a new
        /// GPU-consuming process within a few graph updates, slow enough to stay well clear of
        /// the once-a-second cadence that triggers OS-wide Perflib provider-reload churn.</summary>
        private const int GpuRescanIntervalTicks = 8;
        private int _gpuRescanCountdown;
        private string[] _cachedGpuEngineInstances = [];
        private string[] _cachedGpuMemInstances = [];

        // Filled once by GatherStaticInfo on a background thread; RAM total is also read into
        // this so the live RAM percent has something to divide against.
        private double _totalRamGb;

        internal PerformanceMonitorControl()
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // static hardware panel
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // tiles + detail
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // status line

            // PaneBrush, the same tier as the file browser's location row and the ACTIVE TAB, so
            // the tab floats down into this surface and the two read as one. The cards and tiles
            // on top of it take the menu tier, exactly as the file listing and the terminal do -
            // one rule for every tab: the tab's own surface is PaneBrush, its content is
            // MenuBackgroundBrush.
            this.SetResourceReference(Grid.BackgroundProperty, "PaneBrush");

            var staticPanel = BuildStaticInfoPanel(out _staticInfoText);
            SetRow(staticPanel, 0);
            Children.Add(staticPanel);

            var cellsPanel = BuildCellsPanel();
            SetRow(cellsPanel, 1);
            Children.Add(cellsPanel);

            _statusLine = BuildStatusLine();
            SetRow(_statusLine, 2);
            Children.Add(_statusLine);

            _timer = new DispatcherTimer { Interval = RefreshInterval };
            _timer.Tick += (_, _) => SampleLiveMetrics();

            _statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _statusClearTimer.Tick += (_, _) => { _statusClearTimer.Stop(); ShowStatus(string.Empty, error: false); };

            // Started on Loaded / stopped on Unloaded, same reason ProcessListControl's own timer
            // is: the control is MOVED between the visual tree and nowhere as tabs switch, so a
            // Performance tab sitting in the background costs nothing until it is looked at again.
            Loaded   += Control_Loaded;
            Unloaded += (_, _) => _timer.Stop();
        }

        private bool _staticGathered;

        private async void Control_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_staticGathered)
            {
                _staticGathered = true;
                ShowStatus(MainWindow.LocStatic("Str_Perf_Gathering"), error: false, sticky: true);

                HardwareInfo info;
                try
                {
                    // LongRunning, not a pooled Task.Run - GatherStaticInfo does WMI work, and
                    // WMI's ManagementObjectSearcher/ManagementObject are COM RCWs that must run
                    // out their whole call on the SAME thread rather than a ThreadPool thread that
                    // might get reused mid-cleanup (see the long remark on this in
                    // ProcessListControl.cs Refresh() - it is the exact crash this app already hit
                    // once for real).
                    info = await Task.Factory.StartNew(GatherStaticInfo,
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                catch (Exception ex)
                {
                    ShowStatus(string.Format(MainWindow.LocStatic("Str_Perf_GatherFailed"), ex.Message), error: true);
                    info = HardwareInfo.Empty;
                }

                ApplyStaticInfo(info);
                ShowStatus(string.Empty, error: false);
            }

            SetUpCountersIfNeeded();
            SampleLiveMetrics();
            _timer.Start();
        }

        /// <summary>
        /// Torn down when the tab closes (Shell/PerformanceTabs.cs ClosePerformanceMonitor), AND
        /// when the whole window closes with this tab still open (Session.cs OnClosing via
        /// ShutdownAllPerformanceMonitors).
        /// </summary>
        internal void Shutdown()
        {
            _timer.Stop();
            _statusClearTimer.Stop();
            DisposeCounters();
        }

        private void DisposeCounters()
        {
            foreach (var tile in _tiles)
            {
                switch (tile.Kind)
                {
                    case MetricKind.Cpu:
                        var cs = (CpuState)tile.State!;
                        cs.Total?.Dispose();
                        foreach (var c in cs.CoreCounters) c.Dispose();
                        cs.CoreCounters = [];
                        cs.Total = null;
                        break;
                    case MetricKind.Ram:
                        var rs = (RamState)tile.State!;
                        rs.Avail?.Dispose();
                        rs.Committed?.Dispose();
                        rs.Avail = null;
                        rs.Committed = null;
                        break;
                    case MetricKind.Disk:
                        var ds = (DiskState)tile.State!;
                        ds.PercentTime?.Dispose(); ds.ReadBytes?.Dispose(); ds.WriteBytes?.Dispose();
                        ds.PercentTime = null; ds.ReadBytes = null; ds.WriteBytes = null;
                        break;
                    case MetricKind.Network:
                        var ns = (NetState)tile.State!;
                        ns.Sent?.Dispose(); ns.Recv?.Dispose();
                        ns.Sent = null; ns.Recv = null;
                        break;
                    case MetricKind.Gpu:
                        break;   // nothing persistent - see the GPU dictionaries below
                }
            }

            foreach (var c in _gpuEngineCounters.Values) c.Dispose();
            _gpuEngineCounters.Clear();
            foreach (var c in _gpuMemDedicatedCounters.Values) c.Dispose();
            _gpuMemDedicatedCounters.Clear();
            foreach (var c in _gpuMemSharedCounters.Values) c.Dispose();
            _gpuMemSharedCounters.Clear();
        }

        // ═══════════════════════════════════════════════════════════
        //  STATIC HARDWARE INFO  -  fetched once, never re-queried
        // ═══════════════════════════════════════════════════════════
        private readonly struct DiskInfo
        {
            internal readonly string InstanceName;
            internal readonly string Model;
            /// <summary>Drive letter(s) (e.g. "C:") that live on this physical disk, from the
            /// Win32_DiskDrive -> Win32_DiskPartition -> Win32_LogicalDisk association walk in
            /// GatherStaticInfo. Empty for an unpartitioned disk or one holding only a hidden
            /// partition with no letter - never null.</summary>
            internal readonly List<string> DriveLetters;
            internal DiskInfo(string instanceName, string model, List<string> driveLetters)
            {
                InstanceName = instanceName; Model = model; DriveLetters = driveLetters;
            }
        }

        private readonly struct HardwareInfo
        {
            internal readonly string Cpu;
            internal readonly string Ram;
            internal readonly string Gpu;
            internal readonly string Network;
            internal readonly double TotalRamGb;
            internal readonly int CpuCores;
            internal readonly int CpuThreads;
            internal readonly int CpuBaseMhz;
            internal readonly List<DiskInfo> Disks;
            internal readonly List<string> Gpus;
            internal readonly List<string> NetAdapters;

            internal HardwareInfo(string cpu, string ram, string gpu, string network, double totalRamGb,
                int cpuCores, int cpuThreads, int cpuBaseMhz,
                List<DiskInfo> disks, List<string> gpus, List<string> netAdapters)
            {
                Cpu = cpu; Ram = ram; Gpu = gpu; Network = network; TotalRamGb = totalRamGb;
                CpuCores = cpuCores; CpuThreads = cpuThreads; CpuBaseMhz = cpuBaseMhz;
                Disks = disks; Gpus = gpus; NetAdapters = netAdapters;
            }

            internal static HardwareInfo Empty => new("-", "-", "-", "-", 0, 0, 0, 0,
                [], [], []);
        }

        /// <summary>
        /// Everything about the machine that never changes for the life of the tab, gathered with
        /// one bulk WMI query per fact - the same "one query for the whole machine, not one per
        /// item" discipline Processes/Event Viewer already follow. Also enumerates the
        /// PerformanceCounter instance names for disks and network adapters here (off the UI
        /// thread, same as the WMI calls) so the tile-building and counter-creation passes both
        /// use the exact same instance identifiers this machine actually has - no separate
        /// re-enumeration later that could drift from what was shown.
        /// </summary>
        private static HardwareInfo GatherStaticInfo()
        {
            string cpu = "-", ram = "-", gpu = "-", net = "-";
            double totalRamGb = 0;
            int cpuCores = 0, cpuThreads = 0, cpuBaseMhz = 0;

            // CPU: model name plus core/thread count plus base clock. Multi-socket machines are
            // rare on the desktops this app targets, but summed rather than assumed-one so a
            // workstation with more than one physical CPU still reports an honest total.
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
                using var rows = searcher.Get();
                var names = new List<string>();
                foreach (ManagementObject row in rows.Cast<ManagementObject>())
                {
                    using (row)
                    {
                        string name = (row["Name"] as string ?? string.Empty).Trim();
                        if (name.Length > 0 && !names.Contains(name)) names.Add(name);
                        if (row["NumberOfCores"] is { } c) cpuCores += Convert.ToInt32(c);
                        if (row["NumberOfLogicalProcessors"] is { } t) cpuThreads += Convert.ToInt32(t);
                        if (cpuBaseMhz == 0 && row["MaxClockSpeed"] is { } mhz) cpuBaseMhz = Convert.ToInt32(mhz);
                    }
                }
                if (names.Count > 0)
                    cpu = string.Join(" + ", names) + $" ({cpuCores}C / {cpuThreads}T)";
            }
            catch { /* WMI unavailable/locked down - "-" stands in */ }

            // RAM: total installed, from ComputerSystem rather than summing PhysicalMemory
            // sticks - one row instead of N, and it is the number Windows itself calls "installed
            // RAM".
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                using var rows = searcher.Get();
                foreach (ManagementObject row in rows.Cast<ManagementObject>())
                {
                    using (row)
                    {
                        if (row["TotalPhysicalMemory"] is { } t)
                        {
                            totalRamGb = Convert.ToInt64(t) / 1024.0 / 1024.0 / 1024.0;
                            ram = totalRamGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB installed";
                        }
                    }
                }
            }
            catch { /* "-" stands in */ }

            // GPU: every video controller Windows enumerates - a laptop with integrated +
            // discrete graphics genuinely has two, and both get their own tile below rather than
            // guessing which one is "the" GPU.
            var gpus = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                using var rows = searcher.Get();
                foreach (ManagementObject row in rows.Cast<ManagementObject>())
                {
                    using (row)
                    {
                        string name = (row["Name"] as string ?? string.Empty).Trim();
                        if (name.Length > 0) gpus.Add(name);
                    }
                }
                if (gpus.Count > 0) gpu = string.Join(", ", gpus);
            }
            catch { /* "-" stands in, gpus stays empty - no GPU tiles */ }

            // Disks: cross-reference Win32_DiskDrive (index -> model, and index -> drive
            // letter(s) via the association walk below) with the PhysicalDisk perf-counter
            // category's own instance names (index-prefixed, e.g. "0 C:"), so every disk tile
            // points at a counter instance this machine actually exposes.
            var diskModels = new Dictionary<int, string>();
            var diskDriveLetters = new Dictionary<int, List<string>>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Index, Model FROM Win32_DiskDrive");
                using var rows = searcher.Get();
                foreach (ManagementObject row in rows.Cast<ManagementObject>())
                {
                    using (row)
                    {
                        if (row["Index"] is { } idxObj)
                        {
                            int idx = Convert.ToInt32(idxObj);
                            string model = (row["Model"] as string ?? string.Empty).Trim();
                            if (model.Length > 0) diskModels[idx] = model;

                            // Walk Win32_DiskDrive -> Win32_DiskPartition -> Win32_LogicalDisk to
                            // find which drive letter(s), if any, live on this physical disk - a
                            // disk can have zero (unpartitioned, or only a hidden/system
                            // partition), one, or several (multiple partitions each lettered).
                            // One-time cost, done here alongside the rest of this bulk gather -
                            // never repeated per refresh tick.
                            var letters = new List<string>();
                            try
                            {
                                using var partitions = row.GetRelated("Win32_DiskPartition");
                                foreach (ManagementObject partition in partitions.Cast<ManagementObject>())
                                {
                                    using (partition)
                                    {
                                        using var logicalDisks = partition.GetRelated("Win32_LogicalDisk");
                                        foreach (ManagementObject logicalDisk in logicalDisks.Cast<ManagementObject>())
                                        {
                                            using (logicalDisk)
                                            {
                                                string letter = (logicalDisk["DeviceID"] as string ?? string.Empty).Trim();
                                                if (letter.Length > 0) letters.Add(letter);
                                            }
                                        }
                                    }
                                }
                            }
                            catch { /* association walk failed - tile just shows "Disk N" with no letters */ }
                            letters.Sort(StringComparer.OrdinalIgnoreCase);
                            diskDriveLetters[idx] = letters;
                        }
                    }
                }
            }
            catch { /* falls back to bare instance names below */ }

            var disks = new List<DiskInfo>();
            try
            {
                foreach (string inst in new PerformanceCounterCategory("PhysicalDisk").GetInstanceNames())
                {
                    if (inst == "_Total") continue;
                    int spaceIdx = inst.IndexOf(' ');
                    string indexPart = spaceIdx > 0 ? inst[..spaceIdx] : inst;
                    bool haveIdx = int.TryParse(indexPart, out int idx);
                    string model = haveIdx && diskModels.TryGetValue(idx, out var m) ? m : inst;
                    List<string> letters = haveIdx && diskDriveLetters.TryGetValue(idx, out var l) ? l : [];
                    disks.Add(new DiskInfo(inst, model, letters));
                }
            }
            catch { /* no PhysicalDisk category on this machine - no disk tiles, not fatal */ }

            // Network: name + link speed for every adapter that is actually up, for the overview
            // panel text - a machine can list a dozen virtual/disabled adapters (VPN clients,
            // Hyper-V switches, disabled Bluetooth PAN), and none of those are what anyone reading
            // this tab wants to see.
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, Speed FROM Win32_NetworkAdapter WHERE NetEnabled = TRUE AND PhysicalAdapter = TRUE");
                using var rows = searcher.Get();
                var parts = new List<string>();
                foreach (ManagementObject row in rows.Cast<ManagementObject>())
                {
                    using (row)
                    {
                        string name = (row["Name"] as string ?? string.Empty).Trim();
                        if (name.Length == 0) continue;
                        string speed = row["Speed"] is { } s ? FormatLinkSpeed(Convert.ToUInt64(s)) : string.Empty;
                        parts.Add(speed.Length > 0 ? $"{name} - {speed}" : name);
                    }
                }
                if (parts.Count > 0) net = string.Join("; ", parts);
            }
            catch { /* "-" stands in */ }

            // Network adapter TILES key off the "Network Interface" perf-counter category's own
            // instance names instead - those are what Bytes Sent/sec and Bytes Received/sec
            // actually key on, and trying to line them up with the WMI names above is fragile
            // (perf-counter instance names sanitize characters WMI's Name does not), so each tile
            // just uses its own instance name as its description too.
            var netAdapters = new List<string>();
            try
            {
                foreach (string inst in new PerformanceCounterCategory("Network Interface").GetInstanceNames())
                {
                    if (inst.IndexOf("Loopback", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        inst.IndexOf("isatap", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    netAdapters.Add(inst);
                }
            }
            catch { /* no Network Interface category - no network tiles, not fatal */ }

            return new HardwareInfo(cpu, ram, gpu, net, totalRamGb, cpuCores, cpuThreads, cpuBaseMhz,
                disks, gpus, netAdapters);
        }

        private static string FormatLinkSpeed(ulong bitsPerSecond)
        {
            if (bitsPerSecond <= 0) return string.Empty;
            double gbps = bitsPerSecond / 1_000_000_000.0;
            if (gbps >= 1.0) return gbps.ToString("0.#", CultureInfo.InvariantCulture) + " Gbps";
            double mbps = bitsPerSecond / 1_000_000.0;
            return mbps.ToString("0", CultureInfo.InvariantCulture) + " Mbps";
        }

        private void ApplyStaticInfo(HardwareInfo info)
        {
            _totalRamGb = info.TotalRamGb;
            _staticInfoText.Text =
                "CPU   " + info.Cpu + "\n" +
                "RAM   " + info.Ram + "\n" +
                "GPU   " + info.Gpu + "\n" +
                "NET   " + info.Network;

            BuildTiles(info);
        }

        // ═══════════════════════════════════════════════════════════
        //  BUILD - static info panel
        // ═══════════════════════════════════════════════════════════
        private static Border BuildStaticInfoPanel(out TextBlock text)
        {
            text = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Text = "CPU   -\nRAM   -\nGPU   -\nNET   -",
            };
            text.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            // Monitor* text brushes, not TextBrush/MutedTextBrush: everything in this control
            // that sits ON a MonitorCellBrush surface uses them. They mirror the plain text
            // brushes on every ordinary theme, but 98SE paints its cells BLACK (little CRT
            // readouts) while its TextBrush is black too - invisible. There they are the retro
            // phosphor greens instead.
            text.SetResourceReference(TextBlock.ForegroundProperty, "MonitorTextBrush");

            var panel = new Border
            {
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(KillerShell.Services.ThemeManager.Radius("ChartCornerRadius", 4)),
                BorderThickness = new Thickness(1),
                Child = text,
            };
            // PaneBrush, not BackgroundBrush: an info panel is CONTENT sitting on the tab's
            // chrome, so it takes the same pane color as a terminal or a file listing. On the
            // window tier it also inherited the full-window gradient on five of the themes and
            // re-ramped it inside the panel.
            panel.SetResourceReference(Border.BackgroundProperty, "MonitorCellBrush");
            panel.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
            // Monitor*Margin tokens: the literals the panel/scrollers/tiles always carried on
            // every ordinary theme, collapsed to 2px seams on 98SE.
            panel.SetResourceReference(FrameworkElement.MarginProperty, "MonitorInfoMargin");
            return panel;
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

        // ═══════════════════════════════════════════════════════════
        //  BUILD - the cell grid shell
        // ═══════════════════════════════════════════════════════════
        private UIElement BuildCellsPanel()
        {
            _cellsGrid = new Grid();
            _cellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _cellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var scroller = new ScrollViewer
            {
                Content = _cellsGrid,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            // MonitorGridMargin: 2 a side so the cells' own 6px MonitorTileMargin lands their
            // edges at 8, flush with the info panel above; 0 on 98SE so the cells run to the
            // pane edge like every other well (the tab strip above runs to the window edge, and
            // anything short of it reads as the right edge being off).
            scroller.SetResourceReference(FrameworkElement.MarginProperty, "MonitorGridMargin");
            return scroller;
        }

        /// <summary>
        /// Flows the cells into the two-column grid in _tiles order: a 1-wide cell takes the
        /// next free half-row, a 2-wide cell takes a whole row (starting a fresh one if the
        /// current row is half full). Rebuilds Grid.Row/Column/ColumnSpan only - the cell
        /// elements themselves are built ONCE and keep their graph history across every
        /// re-layout, which is the whole reason this tab survives reordering without wiping
        /// a minute of samples.
        /// </summary>
        private void LayoutCells()
        {
            _cellsGrid.Children.Clear();
            _cellsGrid.RowDefinitions.Clear();

            int row = 0, col = 0;
            foreach (var tile in _tiles)
            {
                int span = tile.ColSpan;
                if (span == 2 && col == 1) { row++; col = 0; }   // full-width starts its own row
                if (col == 0) _cellsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                SetRow(tile.CellBorder, row);
                SetColumn(tile.CellBorder, col);
                SetColumnSpan(tile.CellBorder, span);
                _cellsGrid.Children.Add(tile.CellBorder);

                col += span;
                if (col >= 2) { row++; col = 0; }
            }
        }

        // ── Layout persistence ───────────────────────────────────
        // ONE setting, "id:span|id:span|..." in display order. Ids are stable per metric
        // (CellId), so the layout survives restarts; hardware this machine grew since the
        // setting was written simply appends in natural build order, and entries for hardware
        // that went away are dropped on the next save.
        private const string SetPerfLayout = "PerfLayout";

        private void ApplySavedLayout()
        {
            string saved = Services.ThemeManager.GetSetting(SetPerfLayout) ?? string.Empty;
            if (saved.Length == 0) return;

            var order = new List<MetricTile>();
            foreach (string entry in saved.Split(['|'], StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = entry.LastIndexOf(':');
                string id = colon > 0 ? entry[..colon] : entry;
                var tile = _tiles.FirstOrDefault(t => t.Id == id);
                if (tile == null || order.Contains(tile)) continue;

                if (colon > 0 && int.TryParse(entry[(colon + 1)..], out int span))
                    tile.ColSpan = span == 2 ? 2 : 1;
                order.Add(tile);
            }
            foreach (var t in _tiles) if (!order.Contains(t)) order.Add(t);

            _tiles.Clear();
            _tiles.AddRange(order);
        }

        private void SaveLayout()
            => Services.ThemeManager.SetSetting(SetPerfLayout,
                   string.Join("|", _tiles.Select(t => t.Id + ":" + t.ColSpan)));

        private static TextBlock BuildGraphCaption(string key)
        {
            var tb = new TextBlock { FontSize = 10.5, Margin = new Thickness(0, 6, 0, 2) };
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "MonitorMutedBrush");
            tb.SetResourceReference(TextBlock.TextProperty, key);
            return tb;
        }

        /// <summary>Small color-dot + label legend under a two-tone graph - the tile's own
        /// one-line summary already spells out which number is which, so this is a quick visual
        /// key, not the only place the mapping is written down.</summary>
        private static UIElement BuildLegend(params (string BrushKey, string LabelKey)[] entries)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
            foreach (var (brushKey, labelKey) in entries)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
                var dot = new Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(KillerShell.Services.ThemeManager.Radius("ChartCornerRadius", 4)),
                    Margin = new Thickness(0, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                dot.SetResourceReference(Border.BackgroundProperty, brushKey);
                var text = new TextBlock { FontSize = 10.5 };
                text.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                text.SetResourceReference(TextBlock.ForegroundProperty, "MonitorMutedBrush");
                text.SetResourceReference(TextBlock.TextProperty, labelKey);
                item.Children.Add(dot);
                item.Children.Add(text);
                panel.Children.Add(item);
            }
            return panel;
        }

        private static UIElement BuildField(string labelKey, out TextBlock valueBlock)
        {
            var label = new TextBlock { FontSize = 10 };
            label.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            label.SetResourceReference(TextBlock.ForegroundProperty, "MonitorMutedBrush");
            label.SetResourceReference(TextBlock.TextProperty, labelKey);

            var value = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0), Text = "-" };
            value.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            value.SetResourceReference(TextBlock.ForegroundProperty, "MonitorTextBrush");

            var stack = new StackPanel { Margin = new Thickness(0, 0, 22, 10), MinWidth = 130 };
            stack.Children.Add(label);
            stack.Children.Add(value);
            valueBlock = value;
            return stack;
        }

        // ═══════════════════════════════════════════════════════════
        //  TILES  -  model + build
        // ═══════════════════════════════════════════════════════════
        private enum MetricKind { Cpu, Ram, Disk, Network, Gpu }

        private sealed class MetricTile
        {
            internal MetricKind Kind;
            internal string Id = "";           // stable per metric ("cpu", "disk0", ...) - the layout setting's key
            internal int ColSpan = 1;          // 1 = half row, 2 = full row; the header toggle flips it
            internal string Label = "";
            internal string Description = "-";
            internal Border CellBorder = null!;
            internal TextBlock TileSummaryText = null!;   // the live one-liner beside the cell title
            internal TextBlock[] FieldValueBlocks = [];   // per-cell, built with the cell, always live
            internal Sparkline[] BigGraphs = [];
            internal string[] GraphCaptionKeys = [];
            internal string[] FieldLabelKeys = [];
            internal string[] FieldValues = [];
            internal object? State;
        }

        private sealed class CpuState
        {
            internal PerformanceCounter? Total;
            internal PerformanceCounter[] CoreCounters = [];
            internal Sparkline[] CoreGraphs = [];
            // Built ONCE (BuildCoreGrid) and reused on every later toggle rather than rebuilt -
            // a fresh UniformGrid every toggle would try to re-add the SAME g.Host elements that
            // are still logical children of the PREVIOUS UniformGrid instance (GraphArea.Child
            // only detaches whichever grid is currently showing, not one sitting unused off to
            // the side), which threw "Specified element is already the logical child of another
            // element" the second time per-core view was toggled back on.
            internal UniformGrid? CoreGrid;
            internal Sparkline AggregateGraph = null!;
            internal Border GraphArea = null!;
            internal bool ShowCores;
            internal int PhysicalCores;
            internal int LogicalProcessors;
            internal int BaseMhz;
        }

        private sealed class RamState
        {
            internal PerformanceCounter? Avail;
            internal PerformanceCounter? Committed;
        }

        private sealed class DiskState
        {
            internal string InstanceName = "";
            internal PerformanceCounter? PercentTime;
            internal PerformanceCounter? ReadBytes;
            internal PerformanceCounter? WriteBytes;
        }

        private sealed class NetState
        {
            internal string InstanceName = "";
            internal PerformanceCounter? Sent;
            internal PerformanceCounter? Recv;
        }

        private sealed class GpuState
        {
            internal bool MemoryAvailable;
        }

        private void BuildTiles(HardwareInfo info)
        {
            _tiles.Clear();
            _gpuTiles.Clear();
            _cellsGrid.Children.Clear();
            _cellsGrid.RowDefinitions.Clear();

            // Local, cheap, no WMI: whether these two counter categories exist at all on this
            // machine, checked once so SampleGpus doesn't have to probe (and possibly throw)
            // every single tick.
            _gpuEngineAvailable = SafeCategoryExists("GPU Engine");
            _gpuMemoryAvailable = _gpuEngineAvailable && SafeCategoryExists("GPU Adapter Memory");

            _tiles.Add(BuildCpuTile(info));
            _tiles.Add(BuildRamTile(info));

            for (int i = 0; i < info.Disks.Count; i++)
                _tiles.Add(BuildDiskTile(info.Disks[i], i));

            for (int i = 0; i < info.NetAdapters.Count; i++)
                _tiles.Add(BuildNetworkTile(info.NetAdapters[i], i, info.NetAdapters.Count));

            for (int i = 0; i < info.Gpus.Count; i++)
            {
                var t = BuildGpuTile(info.Gpus[i], i);
                _tiles.Add(t);
                _gpuTiles.Add(t);
            }

            ApplySavedLayout();
            foreach (var tile in _tiles) BuildCell(tile);
            LayoutCells();

            // The CPU graph area's context menu wants real keyboard focus for WPF's built-in
            // Shift+F10 / Menu-key handling - deferred so it lands after the grid has laid out.
            if (_tiles.FirstOrDefault(t => t.Kind == MetricKind.Cpu)?.State is CpuState focusCs)
                Dispatcher.BeginInvoke(new Action(() => focusCs.GraphArea.Focus()), DispatcherPriority.Background);
        }

        private static bool SafeCategoryExists(string category)
        {
            try { return PerformanceCounterCategory.Exists(category); }
            catch { return false; }
        }

        private MetricTile BuildCpuTile(HardwareInfo info)
        {
            var tile = new MetricTile
            {
                Kind = MetricKind.Cpu,
                Id = "cpu",
                Label = MainWindow.LocStatic("Str_Perf_Cpu"),
                Description = info.Cpu,
            };

            var cs = new CpuState
            {
                PhysicalCores = info.CpuCores,
                LogicalProcessors = info.CpuThreads,
                BaseMhz = info.CpuBaseMhz,
                AggregateGraph = new Sparkline(HistorySamples, 100, "PrimaryBrush"),
            };
            cs.GraphArea = new Border { Focusable = true, Background = Brushes.Transparent, Child = cs.AggregateGraph.Host };

            // "needs the option to graph logical cores, from context menu maybe. that also needs
            // a keyboard shortcut for the context menu, and a checkmark on the left." - the
            // IsCheckable MenuItem pattern here is copied exactly from
            // Services/ColumnVisibilityMenu.cs (the app's one other checkable-menu user), so the
            // checkmark renders through the same, already-proven themed MenuItem template. The
            // Shift+F10 / Menu-key open comes free from WPF once this element is Focusable and
            // actually has focus (initial focus in BuildTiles, click-to-focus below); the
            // checked state is set fresh in Opened
            // rather than baked in once, matching ColumnVisibilityMenu.ShowFor's own "read
            // GetVisible() fresh" habit. "L" is ALSO wired directly as a local single-key
            // shortcut while the CPU graph has focus, per this app's own established local-
            // shortcut convention (Processes/Services tab's right-click actions this session).
            var menu = new ContextMenu();
            var toggleItem = new MenuItem { IsCheckable = true, InputGestureText = "L" };
            toggleItem.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Perf_ShowLogicalProcessors");
            var capturedTile = tile;
            menu.Opened += (_, _) => toggleItem.IsChecked = cs.ShowCores;
            toggleItem.Click += (_, _) => ToggleCpuCoreView(capturedTile);
            menu.Items.Add(toggleItem);
            cs.GraphArea.ContextMenu = menu;
            cs.GraphArea.KeyDown += (_, e) =>
            {
                if (e.Key == Key.L) { ToggleCpuCoreView(capturedTile); e.Handled = true; }
            };
            // No selection to hand focus over anymore (the old SelectTile did this), so a click
            // on the graph itself is what arms the L shortcut and Shift+F10.
            cs.GraphArea.MouseLeftButtonDown += (_, _) => cs.GraphArea.Focus();

            tile.State = cs;
            tile.FieldLabelKeys = ["Str_Perf_Utilization", "Str_Perf_LogicalProcessors", "Str_Perf_Cores", "Str_Perf_BaseSpeed"];
            tile.FieldValues =
            [
                "-",
                info.CpuThreads > 0 ? info.CpuThreads.ToString(CultureInfo.InvariantCulture) : "-",
                info.CpuCores > 0 ? info.CpuCores.ToString(CultureInfo.InvariantCulture) : "-",
                info.CpuBaseMhz > 0 ? (info.CpuBaseMhz / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " GHz" : "-",
            ];

            return tile;
        }

        private MetricTile BuildRamTile(HardwareInfo info)
        {
            var tile = new MetricTile
            {
                Kind = MetricKind.Ram,
                Id = "ram",
                Label = MainWindow.LocStatic("Str_Perf_Ram"),
                Description = info.Ram,
                State = new RamState(),
                GraphCaptionKeys = ["Str_Perf_Utilization"],
                BigGraphs = [new Sparkline(HistorySamples, 100, "PrimaryBrush")],
                FieldLabelKeys = ["Str_Perf_InUse", "Str_Perf_Available", "Str_Perf_Committed"],
                FieldValues = ["-", "-", "-"],
            };

            return tile;
        }

        private MetricTile BuildDiskTile(DiskInfo d, int index)
        {
            // "Disk 0 (C:)" / "Disk 1 (C:, D:)" / bare "Disk N" with no parentheses when the
            // physical disk has no lettered volume (unpartitioned, or only a hidden/system
            // partition) - the exact format Windows Task Manager itself uses.
            string label = MainWindow.LocStatic("Str_Perf_Disk") + " " + index;
            if (d.DriveLetters.Count > 0)
                label += " (" + string.Join(", ", d.DriveLetters) + ")";

            var tile = new MetricTile
            {
                Kind = MetricKind.Disk,
                Id = "disk" + index,
                Label = label,
                Description = d.Model,
                State = new DiskState { InstanceName = d.InstanceName },
                GraphCaptionKeys = ["Str_Perf_ActiveTime", "Str_Perf_TransferRate"],
                // Read = accent (the app's own "primary flow" color everywhere else), Write = the
                // family's second bright, theme-stable color (TypeWindows, reused from KillerScan's
                // device-type palette) - same two-color convention as the network graph below.
                BigGraphs =
                [
                    new Sparkline(HistorySamples, 100, "PrimaryBrush"),
                    new Sparkline(HistorySamples, 0, "PrimaryBrush", "TypeWindows"),
                ],
                FieldLabelKeys = ["Str_Perf_ActiveTime", "Str_Perf_ReadSpeed", "Str_Perf_WriteSpeed"],
                FieldValues = ["-", "-", "-"],
            };

            return tile;
        }

        private MetricTile BuildNetworkTile(string instanceName, int index, int totalCount)
        {
            string label = MainWindow.LocStatic("Str_Perf_Network") + (totalCount > 1 ? " " + index : "");
            var tile = new MetricTile
            {
                Kind = MetricKind.Network,
                Id = "net" + index,
                Label = label,
                Description = instanceName,
                State = new NetState { InstanceName = instanceName },
                GraphCaptionKeys = ["Str_Perf_Throughput"],
                // Send = TypeWindows (blue), Receive = PrimaryBrush (accent) - two clearly distinct
                // shades, not two near-identical greens, matching the tile's own "S: x R: y" summary.
                BigGraphs = [new Sparkline(HistorySamples, 0, "TypeWindows", "PrimaryBrush")],
                FieldLabelKeys = ["Str_Perf_Send", "Str_Perf_Receive"],
                FieldValues = ["-", "-"],
            };

            return tile;
        }

        private MetricTile BuildGpuTile(string name, int index)
        {
            var gs = new GpuState { MemoryAvailable = _gpuMemoryAvailable };
            var tile = new MetricTile
            {
                Kind = MetricKind.Gpu,
                Id = "gpu" + index,
                Label = MainWindow.LocStatic("Str_Perf_Gpu") + " " + index,
                Description = name,
                State = gs,
            };

            var utilGraph = new Sparkline(HistorySamples, 100, "PrimaryBrush");
            if (gs.MemoryAvailable)
            {
                var dedicated = new Sparkline(HistorySamples, 0, "PrimaryBrush");
                var shared = new Sparkline(HistorySamples, 0, "TypeWindows");
                tile.BigGraphs = [utilGraph, dedicated, shared];
                tile.GraphCaptionKeys = ["Str_Perf_Utilization", "Str_Perf_DedicatedMemory", "Str_Perf_SharedMemory"];
                tile.FieldLabelKeys = ["Str_Perf_Utilization", "Str_Perf_DedicatedMemory", "Str_Perf_SharedMemory"];
                tile.FieldValues = ["-", "-", "-"];
            }
            else
            {
                tile.BigGraphs = [utilGraph];
                tile.GraphCaptionKeys = ["Str_Perf_Utilization"];
                tile.FieldLabelKeys = ["Str_Perf_Utilization"];
                tile.FieldValues = ["-"];
            }

            return tile;
        }

        // ═══════════════════════════════════════════════════════════
        //  CELLS  -  build, width toggle, drag-to-reorder
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Builds the tile's CELL: header (title + live summary left, description right, width
        /// toggle at the edge - and the header is the DRAG HANDLE), then the big graph(s),
        /// legend and numeric fields, all always live. Built ONCE per tile; LayoutCells only
        /// ever re-parents the finished Border, so graph history survives every reorder and
        /// width change.
        /// </summary>
        private void BuildCell(MetricTile tile)
        {
            var title = new TextBlock
            { FontSize = 16, FontWeight = FontWeights.Bold, Text = tile.Label, VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            title.SetResourceReference(TextBlock.ForegroundProperty, "MonitorTextBrush");

            var summary = new TextBlock
            { FontSize = 11, Text = "-", Margin = new Thickness(10, 0, 0, 2), VerticalAlignment = VerticalAlignment.Bottom };
            summary.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            summary.SetResourceReference(TextBlock.ForegroundProperty, "MonitorMutedBrush");
            tile.TileSummaryText = summary;

            // Ellipsis, not wrap: a half-width cell cannot spare three lines for a chipset's
            // full marketing name - the full string rides the tooltip.
            var description = new TextBlock
            {
                FontSize = 11, Text = tile.Description, ToolTip = tile.Description,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(10, 0, 8, 2),
            };
            description.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            description.SetResourceReference(TextBlock.ForegroundProperty, "MonitorMutedBrush");

            var widthBtn = BuildWidthToggle(tile);

            // Transparent Background is load-bearing: without it the Grid's empty stretches are
            // not hit-testable and the drag handle only worked when the press landed on text.
            var header = new Grid { Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            SetColumn(title, 0);
            SetColumn(summary, 1);
            SetColumn(description, 2);
            SetColumn(widthBtn, 3);
            header.Children.Add(title);
            header.Children.Add(summary);
            header.Children.Add(description);
            header.Children.Add(widthBtn);
            WireCellDrag(header, tile);

            var body = new StackPanel();
            body.Children.Add(header);

            if (tile.Kind == MetricKind.Cpu)
            {
                var cs = (CpuState)tile.State!;
                body.Children.Add(BuildGraphCaption("Str_Perf_Utilization"));
                // BOTH heights, not just the area's: the Host keeps its constructed 52 unless
                // set, and a 52px well centered in a 160px Border reads as a band of dead
                // space above and below the graph. The area stays fixed at 160 so the
                // per-core toggle cannot change the cell's height.
                cs.GraphArea.Height = 160;
                cs.AggregateGraph.Host.Height = 160;
                body.Children.Add(cs.GraphArea);
            }
            else
            {
                for (int i = 0; i < tile.BigGraphs.Length; i++)
                {
                    if (i < tile.GraphCaptionKeys.Length)
                        body.Children.Add(BuildGraphCaption(tile.GraphCaptionKeys[i]));
                    tile.BigGraphs[i].Host.Height = tile.BigGraphs.Length > 1 ? 90 : 160;
                    body.Children.Add(tile.BigGraphs[i].Host);
                }
            }

            if (tile.Kind == MetricKind.Network)
                body.Children.Add(BuildLegend(("TypeWindows", "Str_Perf_Send"), ("PrimaryBrush", "Str_Perf_Receive")));
            else if (tile.Kind == MetricKind.Disk)
                body.Children.Add(BuildLegend(("PrimaryBrush", "Str_Perf_ReadSpeed"), ("TypeWindows", "Str_Perf_WriteSpeed")));
            else if (tile.Kind == MetricKind.Gpu && tile.BigGraphs.Length >= 3)
                body.Children.Add(BuildLegend(("PrimaryBrush", "Str_Perf_DedicatedMemory"), ("TypeWindows", "Str_Perf_SharedMemory")));

            var fields = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            tile.FieldValueBlocks = new TextBlock[tile.FieldLabelKeys.Length];
            for (int i = 0; i < tile.FieldLabelKeys.Length; i++)
            {
                fields.Children.Add(BuildField(tile.FieldLabelKeys[i], out var valueBlock));
                tile.FieldValueBlocks[i] = valueBlock;
            }
            body.Children.Add(fields);

            var cell = new Border
            {
                Padding = new Thickness(14, 12, 14, 12),
                CornerRadius = new CornerRadius(KillerShell.Services.ThemeManager.Radius("ChartCornerRadius", 4)),
                BorderThickness = new Thickness(1),
                // Top, not the default Stretch: a grid row is as tall as its tallest cell, and
                // a stretched shorter neighbor pads its own inside out to match - the "cells
                // are too tall" complaint. Hugging the content leaves the gap OUTSIDE the
                // card, where it reads as layout instead of dead space.
                VerticalAlignment = VerticalAlignment.Top,
                Child = body,
            };
            cell.SetResourceReference(Border.BackgroundProperty, "MonitorCellBrush");
            cell.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
            cell.SetResourceReference(FrameworkElement.MarginProperty, "MonitorTileMargin");
            tile.CellBorder = cell;

            RefreshDetailFieldValues(tile);
        }

        /// <summary>
        /// The header's 1-column / 2-column toggle: E740 (expand) on a half-width cell, E73F
        /// (back to half) on a full-width one. A bare Border + glyph rather than a Button - a
        /// Button with Background=Transparent keeps WPF's default template and its system-blue
        /// hover. Rest neutral, hover accent: the family icon hover language.
        /// </summary>
        private Border BuildWidthToggle(MetricTile tile)
        {
            var glyph = new TextBlock
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Text = ((char)(tile.ColSpan == 2 ? 0xE73F : 0xE740)).ToString(),
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "MonitorMutedBrush");

            var btn = new Border
            {
                Background = Brushes.Transparent,   // hit-testable; hover recolors the GLYPH only
                Padding = new Thickness(5, 3, 5, 3),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Child = glyph,
            };
            btn.SetResourceReference(FrameworkElement.ToolTipProperty, "Str_Perf_ToggleWidth");

            btn.MouseEnter += (_, _) => glyph.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
            btn.MouseLeave += (_, _) => glyph.SetResourceReference(TextBlock.ForegroundProperty, "MonitorMutedBrush");

            // Handled DOWN keeps the header's drag handler out of a toggle click.
            btn.MouseLeftButtonDown += (_, e) => e.Handled = true;
            btn.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                tile.ColSpan = tile.ColSpan == 1 ? 2 : 1;
                glyph.Text = ((char)(tile.ColSpan == 2 ? 0xE73F : 0xE740)).ToString();
                LayoutCells();
                SaveLayout();
            };
            return btn;
        }

        /// <summary>
        /// Drag the header to reorder cells, live: past a 4px threshold the cell dims, and
        /// whenever the pointer is over ANOTHER cell the dragged one takes that cell's slot in
        /// _tiles and the grid re-flows immediately - the reorder IS the drag preview. Saved
        /// once on release. Stable against oscillation because after a move the pointer sits
        /// over the dragged cell itself, which CellAt ignores.
        /// </summary>
        private void WireCellDrag(Grid header, MetricTile tile)
        {
            header.MouseLeftButtonDown += (_, e) =>
            {
                _dragTile = tile;
                _dragActive = false;
                _dragStart = e.GetPosition(_cellsGrid);
                header.CaptureMouse();
            };
            header.MouseMove += (_, e) =>
            {
                if (!ReferenceEquals(_dragTile, tile) || !header.IsMouseCaptured
                    || e.LeftButton != MouseButtonState.Pressed) return;

                var pos = e.GetPosition(_cellsGrid);
                if (!_dragActive)
                {
                    if (Math.Abs(pos.X - _dragStart.X) < 4 && Math.Abs(pos.Y - _dragStart.Y) < 4) return;
                    _dragActive = true;
                    tile.CellBorder.Opacity = 0.65;
                }

                if (CellAt(pos, tile) is { } target)
                {
                    int from = _tiles.IndexOf(tile), to = _tiles.IndexOf(target);
                    if (from >= 0 && to >= 0 && from != to)
                    {
                        _tiles.RemoveAt(from);
                        _tiles.Insert(to, tile);
                        LayoutCells();
                    }
                }
            };
            header.MouseLeftButtonUp += (_, _) =>
            {
                if (header.IsMouseCaptured) header.ReleaseMouseCapture();
                if (ReferenceEquals(_dragTile, tile) && _dragActive) SaveLayout();
                tile.CellBorder.Opacity = 1.0;
                _dragTile = null;
                _dragActive = false;
            };
            // Capture can be torn away (alt-tab, a popup) - never leave a cell dimmed.
            header.LostMouseCapture += (_, _) => tile.CellBorder.Opacity = 1.0;
        }

        /// <summary>The cell under a grid-space point, ignoring the dragged one.</summary>
        private MetricTile? CellAt(Point gridPoint, MetricTile ignore)
        {
            foreach (var t in _tiles)
            {
                if (ReferenceEquals(t, ignore)) continue;
                var cell = t.CellBorder;
                if (cell == null || cell.ActualWidth <= 0) continue;

                Point topLeft = cell.TranslatePoint(new Point(0, 0), _cellsGrid);
                if (gridPoint.X >= topLeft.X && gridPoint.X <= topLeft.X + cell.ActualWidth
                 && gridPoint.Y >= topLeft.Y && gridPoint.Y <= topLeft.Y + cell.ActualHeight)
                    return t;
            }
            return null;
        }

        /// <summary>Writes a tile's current FieldValues into its cell's own value blocks -
        /// every cell is live all the time now, there is no selected-tile gate.</summary>
        private static void RefreshDetailFieldValues(MetricTile tile)
        {
            for (int i = 0; i < tile.FieldValueBlocks.Length && i < tile.FieldValues.Length; i++)
                tile.FieldValueBlocks[i].Text = tile.FieldValues[i];
        }

        // ═══════════════════════════════════════════════════════════
        //  CPU per-core toggle
        // ═══════════════════════════════════════════════════════════
        private void ToggleCpuCoreView(MetricTile cpuTile)
        {
            var cs = (CpuState)cpuTile.State!;
            cs.ShowCores = !cs.ShowCores;
            if (cs.ShowCores) SetUpCoreCountersIfNeeded(cs);
            cs.GraphArea.Child = cs.ShowCores ? BuildCoreGrid(cs) : cs.AggregateGraph.Host;
        }

        private static UIElement BuildCoreGrid(CpuState cs)
        {
            // Built once and cached on the CpuState (cs.CoreGrid) - cs.CoreGraphs itself is only
            // ever created once too (SetUpCoreCountersIfNeeded's own early-return guard), so
            // there is never a second set of Host elements to add and never a reason to build a
            // second UniformGrid. ToggleCpuCoreView just reassigns GraphArea.Child between this
            // and cs.AggregateGraph.Host on every press.
            if (cs.CoreGrid != null) return cs.CoreGrid;

            // Rows/Columns left at 0 (WPF default): UniformGrid auto-arranges into a near-square
            // grid from the child count alone, so this works the same whether the machine has 4
            // logical processors or 32 - no per-core-count layout logic needed.
            var grid = new UniformGrid();
            foreach (var g in cs.CoreGraphs)
            {
                g.Host.Height = 50;
                g.Host.Margin = new Thickness(2);
                grid.Children.Add(g.Host);
            }
            cs.CoreGrid = grid;
            return grid;
        }

        private static void SetUpCoreCountersIfNeeded(CpuState cs)
        {
            if (cs.CoreCounters.Length > 0) return;
            try
            {
                var names = new List<string>();
                foreach (string inst in new PerformanceCounterCategory("Processor").GetInstanceNames())
                    if (inst != "_Total" && int.TryParse(inst, out _)) names.Add(inst);
                names.Sort((a, b) => int.Parse(a, CultureInfo.InvariantCulture).CompareTo(int.Parse(b, CultureInfo.InvariantCulture)));

                var counters = new PerformanceCounter[names.Count];
                var graphs = new Sparkline[names.Count];
                for (int i = 0; i < names.Count; i++)
                {
                    counters[i] = new PerformanceCounter("Processor", "% Processor Time", names[i]);
                    counters[i].NextValue();   // rate counter - first read has no baseline, discarded
                    graphs[i] = new Sparkline(HistorySamples, 100, "PrimaryBrush");
                }
                cs.CoreCounters = counters;
                cs.CoreGraphs = graphs;
            }
            catch { /* leave empty - the toggled view just shows nothing rather than throw */ }
        }

        // ═══════════════════════════════════════════════════════════
        //  LIVE COUNTERS  -  setup
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Creates every per-tile PerformanceCounter this tab samples, once. Wrapped so a machine
        /// with the Performance Counter service disabled (rare, but real - some locked-down or
        /// stripped Windows images ship that way) degrades to "no live data" rather than throwing
        /// on every tick.
        /// </summary>
        private void SetUpCountersIfNeeded()
        {
            if (_countersAvailable) return;

            try
            {
                foreach (var tile in _tiles)
                {
                    switch (tile.Kind)
                    {
                        case MetricKind.Cpu:
                        {
                            var cs = (CpuState)tile.State!;
                            cs.Total = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                            cs.Total.NextValue();
                            break;
                        }
                        case MetricKind.Ram:
                        {
                            var rs = (RamState)tile.State!;
                            rs.Avail = new PerformanceCounter("Memory", "Available MBytes");
                            rs.Committed = TryCreateCounterNoInstance("Memory", "Committed Bytes");
                            break;
                        }
                        case MetricKind.Disk:
                        {
                            var ds = (DiskState)tile.State!;
                            ds.PercentTime = TryCreateCounter("PhysicalDisk", "% Disk Time", ds.InstanceName);
                            ds.ReadBytes = TryCreateCounter("PhysicalDisk", "Disk Read Bytes/sec", ds.InstanceName);
                            ds.WriteBytes = TryCreateCounter("PhysicalDisk", "Disk Write Bytes/sec", ds.InstanceName);
                            ds.PercentTime?.NextValue();
                            ds.ReadBytes?.NextValue();
                            ds.WriteBytes?.NextValue();
                            break;
                        }
                        case MetricKind.Network:
                        {
                            var ns = (NetState)tile.State!;
                            ns.Sent = TryCreateCounter("Network Interface", "Bytes Sent/sec", ns.InstanceName);
                            ns.Recv = TryCreateCounter("Network Interface", "Bytes Received/sec", ns.InstanceName);
                            ns.Sent?.NextValue();
                            ns.Recv?.NextValue();
                            break;
                        }
                        case MetricKind.Gpu:
                            break;   // GPU Engine instances are per-process - created lazily in SampleGpus
                    }
                }

                _countersAvailable = true;
            }
            catch (Exception ex)
            {
                ShowStatus(string.Format(MainWindow.LocStatic("Str_Perf_CountersUnavailable"), ex.Message), error: true, sticky: true);
                DisposeCounters();
                _countersAvailable = false;
            }
        }

        private static PerformanceCounter? TryCreateCounter(string category, string counter, string instance)
        {
            try { return new PerformanceCounter(category, counter, instance); }
            catch { return null; }
        }

        private static PerformanceCounter? TryCreateCounterNoInstance(string category, string counter)
        {
            try { return new PerformanceCounter(category, counter); }
            catch { return null; }
        }

        // ═══════════════════════════════════════════════════════════
        //  LIVE COUNTERS  -  per-tick sampling
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// One tick, every tile. Each kind's own sampling method wraps its own try/catch - a
        /// single stale counter (a network adapter unplugged, a disk removed mid-session) now
        /// only blanks that ONE tile instead of stopping every gauge in the tab, which is what a
        /// single shared try/catch would otherwise do across this many independent tiles.
        /// </summary>
        private void SampleLiveMetrics()
        {
            if (!_countersAvailable) return;

            foreach (var tile in _tiles)
            {
                switch (tile.Kind)
                {
                    case MetricKind.Cpu: SampleCpuTile(tile); break;
                    case MetricKind.Ram: SampleRamTile(tile); break;
                    case MetricKind.Disk: SampleDiskTile(tile); break;
                    case MetricKind.Network: SampleNetworkTile(tile); break;
                    case MetricKind.Gpu: break;   // handled together below - needs every GPU tile at once
                }
            }

            SampleGpus();
        }

        private void SampleCpuTile(MetricTile tile)
        {
            var cs = (CpuState)tile.State!;
            if (cs.Total == null) return;
            try
            {
                double pct = Math.Min(100, Math.Max(0, cs.Total.NextValue()));
                tile.TileSummaryText.Text = pct.ToString("0.0", CultureInfo.InvariantCulture) + " %";
                cs.AggregateGraph.Push(pct);
                tile.FieldValues[0] = pct.ToString("0.0", CultureInfo.InvariantCulture) + " %";

                if (cs.ShowCores)
                    for (int i = 0; i < cs.CoreCounters.Length; i++)
                    {
                        double c = Math.Min(100, Math.Max(0, cs.CoreCounters[i].NextValue()));
                        cs.CoreGraphs[i].Push(c);
                    }

                RefreshDetailFieldValues(tile);
            }
            catch { /* counter went stale - leave last-known values in place */ }
        }

        private void SampleRamTile(MetricTile tile)
        {
            var rs = (RamState)tile.State!;
            if (rs.Avail == null) return;
            try
            {
                double availMb = rs.Avail.NextValue();
                if (_totalRamGb > 0)
                {
                    double availGb = availMb / 1024.0;
                    double usedGb = Math.Max(0, _totalRamGb - availGb);
                    double pct = usedGb / _totalRamGb * 100.0;
                    tile.TileSummaryText.Text = usedGb.ToString("0.0", CultureInfo.InvariantCulture) + "/" +
                        _totalRamGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB (" +
                        pct.ToString("0", CultureInfo.InvariantCulture) + "%)";
                    double clamped = Math.Min(100, pct);
                    if (tile.BigGraphs.Length > 0) tile.BigGraphs[0].Push(clamped);
                    tile.FieldValues[0] = usedGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
                    tile.FieldValues[1] = availGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
                }
                else
                {
                    tile.TileSummaryText.Text = availMb.ToString("0", CultureInfo.InvariantCulture) + " MB free";
                }

                if (rs.Committed != null)
                {
                    double committedMb = rs.Committed.NextValue() / 1024.0 / 1024.0;
                    tile.FieldValues[2] = (committedMb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
                }

                RefreshDetailFieldValues(tile);
            }
            catch { }
        }

        private void SampleDiskTile(MetricTile tile)
        {
            var ds = (DiskState)tile.State!;
            try
            {
                double activePct = ds.PercentTime != null ? Math.Min(100, Math.Max(0, ds.PercentTime.NextValue())) : 0;
                double readBps = ds.ReadBytes?.NextValue() ?? 0;
                double writeBps = ds.WriteBytes?.NextValue() ?? 0;

                tile.TileSummaryText.Text = activePct.ToString("0", CultureInfo.InvariantCulture) + " %";
                tile.BigGraphs[0].Push(activePct);
                tile.BigGraphs[1].Push(readBps, writeBps);
                tile.FieldValues[0] = activePct.ToString("0", CultureInfo.InvariantCulture) + " %";
                tile.FieldValues[1] = FormatThroughput(readBps);
                tile.FieldValues[2] = FormatThroughput(writeBps);

                RefreshDetailFieldValues(tile);
            }
            catch { }
        }

        private void SampleNetworkTile(MetricTile tile)
        {
            var ns = (NetState)tile.State!;
            try
            {
                double sent = ns.Sent?.NextValue() ?? 0;
                double recv = ns.Recv?.NextValue() ?? 0;

                tile.TileSummaryText.Text = "S: " + FormatThroughput(sent) + "  R: " + FormatThroughput(recv);
                tile.BigGraphs[0].Push(sent, recv);
                tile.FieldValues[0] = FormatThroughput(sent);
                tile.FieldValues[1] = FormatThroughput(recv);

                RefreshDetailFieldValues(tile);
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════
        //  LIVE COUNTERS  -  GPU
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Live GPU utilization without a vendor SDK, using the same "GPU Engine" counter
        /// category technique Task Manager itself (and several open-source system monitors) uses
        /// to build its own "GPU 0 ... 3D" figure: sum the "Utilization Percentage" counter
        /// across every instance whose name contains "engtype_3D", clamp to 0-100. GPU Engine
        /// instances are per-PROCESS (they appear and disappear as programs start/stop using the
        /// GPU), so - unlike every other counter in this tab - they cannot be created once and
        /// kept forever.
        ///
        /// IMPORTANT (real bug fixed 2026-08-02): the instance-NAME-LIST enumeration
        /// (PerformanceCounterCategory("GPU Engine").GetInstanceNames()) is the expensive,
        /// side-effecting part, NOT the per-counter NextValue() read. GPU Engine and GPU Adapter
        /// Memory are both extensible/dynamic Perflib categories (backed by the GPU scheduler,
        /// not a simple fixed provider), and calling GetInstanceNames() on an extensible category
        /// once a second is a well-documented trigger for the OS's Perflib subsystem to tear down
        /// and reinitialize EVERY registered extensible counter provider system-wide - this was
        /// caught for real spamming the Windows Application log with unrelated
        /// Microsoft-Windows-Perflib warnings/errors for WmiApRpl, MSDTC and Lsa/Secur32.dll,
        /// roughly every 20-25 minutes, purely from this tab's own polling. So the instance list
        /// is now only re-read every GpuRescanIntervalTicks ticks (_gpuRescanCountdown), cached in
        /// _cachedGpuEngineInstances / _cachedGpuMemInstances, and reused on the ticks in between.
        /// Already-created PerformanceCounter objects still get NextValue() called on them EVERY
        /// tick regardless of whether this is a rescan tick - that call is cheap and side-effect
        /// free, so the on-screen number keeps its 1-second live cadence even though the
        /// underlying instance list only refreshes periodically. A stale counter (the process
        /// using that GPU engine slot exited) is only detected and disposed on a rescan tick,
        /// which is fine - .NextValue() on a still-cached-but-now-invalid counter throws, which is
        /// caught per-instance below and simply drops that instance's contribution for the tick
        /// until the next rescan prunes it.
        ///
        /// GPU Adapter Memory (Dedicated/Shared Usage) is a separate, much cheaper category -
        /// those are instantaneous raw counters with no warm-up needed - added on if
        /// _gpuMemoryAvailable (checked once, not per tick), and gets the exact same
        /// cache-and-periodically-rescan treatment as GPU Engine above.
        ///
        /// Multi-GPU separation: instance names encode a LUID (e.g.
        /// "...luid_0x00000000_0x0000CAFE_phys_0_eng_0_engtype_3D"). There is no cheap, reliable
        /// way to map a LUID to a specific Win32_VideoController row without a vendor/DXGI call,
        /// so this uses a best-effort heuristic: when the number of DISTINCT LUIDs actually
        /// observed this tick equals the number of GPU tiles built from WMI, they are paired up
        /// in sorted-LUID / WMI-enumeration order. That is exactly right on the overwhelming
        /// majority of machines (one LUID, one tile). When it doesn't match, everything is
        /// summed into tile 0 only and every other GPU tile shows its static name with no live
        /// number, rather than risk mislabeling one adapter's activity as another's.
        /// </summary>
        private void SampleGpus()
        {
            if (!_gpuEngineAvailable || _gpuTiles.Count == 0) return;

            // Only re-enumerate the instance-name LISTS periodically (see the class-level remark
            // on GpuRescanIntervalTicks and the long remark above) - NextValue() on already-cached
            // counters still happens every tick further down, regardless of this flag.
            bool rescan = _gpuRescanCountdown <= 0;
            _gpuRescanCountdown = rescan ? GpuRescanIntervalTicks : _gpuRescanCountdown - 1;

            try
            {
                if (rescan)
                {
                    try { _cachedGpuEngineInstances = new PerformanceCounterCategory("GPU Engine").GetInstanceNames(); }
                    catch { _cachedGpuEngineInstances = []; }

                    var seen = new HashSet<string>(_cachedGpuEngineInstances, StringComparer.Ordinal);
                    var stale = new List<string>();
                    foreach (var key in _gpuEngineCounters.Keys) if (!seen.Contains(key)) stale.Add(key);
                    foreach (var key in stale) { _gpuEngineCounters[key].Dispose(); _gpuEngineCounters.Remove(key); }
                }
                string[] engineInstances = _cachedGpuEngineInstances;

                var luidUtil = new Dictionary<string, double>(StringComparer.Ordinal);
                var allLuids = new SortedSet<string>(StringComparer.Ordinal);

                foreach (string inst in engineInstances)
                {
                    string luid = ExtractGpuLuid(inst);
                    allLuids.Add(luid);
                    if (inst.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    if (!_gpuEngineCounters.TryGetValue(inst, out var pc))
                    {
                        try
                        {
                            pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                            pc.NextValue();   // no baseline yet - this tick's reading is meaningless
                            _gpuEngineCounters[inst] = pc;
                        }
                        catch { /* instance vanished between GetInstanceNames and here - skip it */ }
                        continue;
                    }

                    try { luidUtil.TryGetValue(luid, out double cur); luidUtil[luid] = cur + pc.NextValue(); }
                    catch { /* stale - drop this tick's contribution */ }
                }

                var luidDedicated = new Dictionary<string, double>(StringComparer.Ordinal);
                var luidShared = new Dictionary<string, double>(StringComparer.Ordinal);
                if (_gpuMemoryAvailable)
                {
                    if (rescan)
                    {
                        try { _cachedGpuMemInstances = new PerformanceCounterCategory("GPU Adapter Memory").GetInstanceNames(); }
                        catch { _cachedGpuMemInstances = []; }

                        var seenMem = new HashSet<string>(_cachedGpuMemInstances, StringComparer.Ordinal);
                        var staleMem = new List<string>();
                        foreach (var key in _gpuMemDedicatedCounters.Keys) if (!seenMem.Contains(key)) staleMem.Add(key);
                        foreach (var key in staleMem)
                        {
                            _gpuMemDedicatedCounters[key].Dispose(); _gpuMemDedicatedCounters.Remove(key);
                            if (_gpuMemSharedCounters.TryGetValue(key, out var sc)) { sc.Dispose(); _gpuMemSharedCounters.Remove(key); }
                        }
                    }
                    string[] memInstances = _cachedGpuMemInstances;

                    foreach (string inst in memInstances)
                    {
                        string luid = ExtractGpuLuid(inst);
                        allLuids.Add(luid);
                        try
                        {
                            if (!_gpuMemDedicatedCounters.TryGetValue(inst, out var dedPc))
                            {
                                dedPc = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", inst, true);
                                _gpuMemDedicatedCounters[inst] = dedPc;
                            }
                            if (!_gpuMemSharedCounters.TryGetValue(inst, out var shrPc))
                            {
                                shrPc = new PerformanceCounter("GPU Adapter Memory", "Shared Usage", inst, true);
                                _gpuMemSharedCounters[inst] = shrPc;
                            }
                            double ded = dedPc.NextValue();
                            double shr = shrPc.NextValue();
                            luidDedicated.TryGetValue(luid, out double curD); luidDedicated[luid] = curD + ded;
                            luidShared.TryGetValue(luid, out double curS); luidShared[luid] = curS + shr;
                        }
                        catch { /* this adapter's memory counters unavailable this tick */ }
                    }
                }

                var luidList = new List<string>(allLuids);
                if (luidList.Count == _gpuTiles.Count && luidList.Count > 0)
                {
                    for (int i = 0; i < _gpuTiles.Count; i++)
                    {
                        string luid = luidList[i];
                        luidUtil.TryGetValue(luid, out double util);
                        luidDedicated.TryGetValue(luid, out double ded);
                        luidShared.TryGetValue(luid, out double shr);
                        ApplyGpuSample(_gpuTiles[i], util, ded, shr);
                    }
                }
                else if (_gpuTiles.Count > 0)
                {
                    double totalUtil = 0, totalDed = 0, totalShr = 0;
                    foreach (var v in luidUtil.Values) totalUtil += v;
                    foreach (var v in luidDedicated.Values) totalDed += v;
                    foreach (var v in luidShared.Values) totalShr += v;
                    ApplyGpuSample(_gpuTiles[0], totalUtil, totalDed, totalShr);
                }
            }
            catch
            {
                // A GPU read going bad (driver reset, adapter removed) should not take the rest of
                // the tab's counters down with it - swallow and try again next tick.
            }
        }

        private static string ExtractGpuLuid(string instanceName)
        {
            int i = instanceName.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return instanceName;
            int start = i + 5;
            int end = instanceName.IndexOf("_phys", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) end = instanceName.Length;
            return instanceName[start..end];
        }

        private void ApplyGpuSample(MetricTile tile, double util, double dedicatedBytes, double sharedBytes)
        {
            util = Math.Min(100, Math.Max(0, util));
            var gs = (GpuState)tile.State!;

            tile.BigGraphs[0].Push(util);
            tile.TileSummaryText.Text = util.ToString("0", CultureInfo.InvariantCulture) + " %";
            tile.FieldValues[0] = util.ToString("0", CultureInfo.InvariantCulture) + " %";

            if (gs.MemoryAvailable && tile.BigGraphs.Length >= 3)
            {
                tile.BigGraphs[1].Push(dedicatedBytes);
                tile.BigGraphs[2].Push(sharedBytes);
                tile.FieldValues[1] = FormatBytes(dedicatedBytes);
                tile.FieldValues[2] = FormatBytes(sharedBytes);
            }

            RefreshDetailFieldValues(tile);
        }

        // ═══════════════════════════════════════════════════════════
        //  FORMATTING
        // ═══════════════════════════════════════════════════════════
        private static string FormatThroughput(double bytesPerSec)
        {
            const double kb = 1024, mb = kb * 1024;
            if (bytesPerSec >= mb) return (bytesPerSec / mb).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
            if (bytesPerSec >= kb) return (bytesPerSec / kb).ToString("0.0", CultureInfo.InvariantCulture) + " KB/s";
            return bytesPerSec.ToString("0", CultureInfo.InvariantCulture) + " B/s";
        }

        private static string FormatBytes(double bytes)
        {
            const double mb = 1024 * 1024, gb = mb * 1024;
            if (bytes >= gb) return (bytes / gb).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            if (bytes >= mb) return (bytes / mb).ToString("0", CultureInfo.InvariantCulture) + " MB";
            return bytes.ToString("0", CultureInfo.InvariantCulture) + " B";
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

            _statusClearTimer.Stop();
            if (text.Length > 0 && !error && !sticky) _statusClearTimer.Start();
        }

        // ═══════════════════════════════════════════════════════════
        //  SPARKLINE  -  a small scrolling trace, the "retro oscilloscope" treatment
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// A fixed window of recent samples drawn as one Polyline PER SERIES (1 for most metrics,
        /// 2 for network send/receive and disk read/write), right-aligned so the newest sample
        /// sits at the right edge and the trace scrolls left as new samples arrive - the same
        /// reading direction a classic Task Manager graph uses. fixedScaleMax of 0 means
        /// auto-scale: the vertical range tracks the highest recent sample across ALL of this
        /// sparkline's series (so two series sharing one graph share one scale), with 20%
        /// headroom so a peak never touches the top edge.
        /// </summary>
        private sealed class Sparkline
        {
            internal readonly Border Host;
            private readonly Canvas _canvas;
            private readonly Polyline[] _lines;
            private readonly List<double>[] _seriesSamples;
            private readonly int _maxSamples;
            private readonly bool _autoScale;
            private double _scaleMax;

            internal Sparkline(int maxSamples, double fixedScaleMax, params string[] brushKeys)
            {
                _maxSamples = maxSamples;
                _autoScale = fixedScaleMax <= 0;
                _scaleMax = fixedScaleMax > 0 ? fixedScaleMax : 1;

                int n = Math.Max(1, brushKeys.Length);
                _lines = new Polyline[n];
                _seriesSamples = new List<double>[n];

                _canvas = new Canvas { ClipToBounds = true };
                for (int i = 0; i < n; i++)
                {
                    _seriesSamples[i] = [];
                    var line = new Polyline { StrokeThickness = 1.5 };
                    line.SetResourceReference(Shape.StrokeProperty, i < brushKeys.Length ? brushKeys[i] : "PrimaryBrush");
                    _lines[i] = line;
                    _canvas.Children.Add(line);
                }
                _canvas.SizeChanged += (_, _) => Redraw();

                Host = new Border
                {
                    Height = 52,
                    CornerRadius = new CornerRadius(KillerShell.Services.ThemeManager.Radius("SmallCornerRadius", 3)),
                    BorderThickness = new Thickness(1),
                    Child = _canvas,
                };
                Host.SetResourceReference(Border.BackgroundProperty, "MonitorCellBrush");
                Host.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
            }

            /// <summary>One value per series, in the same order the brush keys were given.</summary>
            internal void Push(params double[] values)
            {
                for (int i = 0; i < values.Length && i < _seriesSamples.Length; i++)
                {
                    var list = _seriesSamples[i];
                    list.Add(values[i]);
                    while (list.Count > _maxSamples) list.RemoveAt(0);
                }

                if (_autoScale)
                {
                    double max = 1;
                    foreach (var list in _seriesSamples)
                        foreach (double s in list)
                            if (s > max) max = s;
                    _scaleMax = max * 1.2;
                }

                Redraw();
            }

            private void Redraw()
            {
                double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
                double stepX = _maxSamples > 1 ? w / (_maxSamples - 1) : 0;

                for (int s = 0; s < _lines.Length; s++)
                {
                    var samples = _seriesSamples[s];
                    if (w <= 0 || h <= 0 || samples.Count == 0) { _lines[s].Points = []; continue; }

                    var pts = new PointCollection(samples.Count);
                    int startIndex = _maxSamples - samples.Count;   // newest sample lands at the right edge
                    for (int i = 0; i < samples.Count; i++)
                    {
                        double x = (startIndex + i) * stepX;
                        double frac = _scaleMax > 0 ? Math.Min(1.0, samples[i] / _scaleMax) : 0;
                        double y = h - frac * h;
                        pts.Add(new Point(x, y));
                    }
                    _lines[s].Points = pts;
                }
            }
        }
    }
}
