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

// The control behind a Performance Monitor tab: a master/detail layout modeled on Windows' own
// Task Manager Performance page (that is the explicit reference this was built against), redone
// in KillerShell's own retro-terminal language - PaneBrush cards, MonoFont readouts, the app's
// own SelectionBg/SelectionFg accent for "this tile is selected" - rather than a Fluent clone.
//
// Left: a narrow scrolling column of small TILES, one per monitored item - CPU, RAM, one per
// PHYSICAL disk, one per network adapter, one per GPU - each enumerated from the machine rather
// than assumed to be exactly one. Right: a detail panel for whichever tile is selected, with one
// or more big live graphs and a grid of numeric fields underneath.
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

        // ── Master/detail state ──────────────────────────────────
        private StackPanel _tileListPanel = null!;
        private TextBlock _detailTitle = null!;
        private TextBlock _detailDescription = null!;
        private StackPanel _graphsHost = null!;
        private WrapPanel _fieldsHost = null!;
        private TextBlock[]? _fieldValueBlocks;

        private readonly List<MetricTile> _tiles = [];
        private readonly List<MetricTile> _gpuTiles = [];
        private MetricTile? _selectedTile;

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
            // MenuBackgroundBrush (Steve, 2026-08-08).
            this.SetResourceReference(Grid.BackgroundProperty, "PaneBrush");

            var staticPanel = BuildStaticInfoPanel(out _staticInfoText);
            SetRow(staticPanel, 0);
            Children.Add(staticPanel);

            var masterDetail = BuildMasterDetailPanel();
            SetRow(masterDetail, 1);
            Children.Add(masterDetail);

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
            // phosphor greens (Steve, 2026-08-09: "text color in the black squares... maybe a
            // retro green would look cool").
            text.SetResourceReference(TextBlock.ForegroundProperty, "MonitorTextBrush");

            var panel = new Border
            {
                Margin = new Thickness(8, 8, 8, 8),
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(KillerShell.Services.ThemeManager.Radius("ChartCornerRadius", 4)),
                BorderThickness = new Thickness(1),
                Child = text,
            };
            // PaneBrush, not BackgroundBrush: an info panel is CONTENT sitting on the tab's
            // chrome, so it takes the same pane color as a terminal or a file listing. On the
            // window tier it also inherited the full-window gradient on five of the themes and
            // re-ramped it inside the panel (Steve, 2026-08-08).
            panel.SetResourceReference(Border.BackgroundProperty, "MonitorCellBrush");
            panel.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
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
        //  BUILD - master/detail shell
        // ═══════════════════════════════════════════════════════════
        private UIElement BuildMasterDetailPanel()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _tileListPanel = new StackPanel();
            var tileScroller = new ScrollViewer
            {
                Content = _tileListPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(8, 0, 4, 8),
            };
            SetColumn(tileScroller, 0);

            var detailScroller = new ScrollViewer
            {
                Content = BuildDetailPanel(),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            // MonitorDetailMargin, not the literal 4,0,8,8: the 8px right gutter is right on the
            // rounded themes, but on 98SE the tab strip above runs to the window edge and the
            // detail card stopped 8px short of it - the "right edge is off" gap (Steve,
            // 2026-08-09). 98SE closes it to 2 so the card lines up with the tabs.
            detailScroller.SetResourceReference(FrameworkElement.MarginProperty, "MonitorDetailMargin");
            SetColumn(detailScroller, 1);

            grid.Children.Add(tileScroller);
            grid.Children.Add(detailScroller);
            return grid;
        }

        private UIElement BuildDetailPanel()
        {
            _detailTitle = new TextBlock { FontSize = 16, FontWeight = FontWeights.Bold };
            _detailTitle.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            _detailTitle.SetResourceReference(TextBlock.ForegroundProperty, "MonitorTextBrush");

            _detailDescription = new TextBlock
            {
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            _detailDescription.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            _detailDescription.SetResourceReference(TextBlock.ForegroundProperty, "MonitorMutedBrush");

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            SetColumn(_detailTitle, 0);
            SetColumn(_detailDescription, 1);
            header.Children.Add(_detailTitle);
            header.Children.Add(_detailDescription);

            _graphsHost = new StackPanel { Margin = new Thickness(0, 10, 0, 4) };
            _fieldsHost = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };

            var body = new StackPanel();
            body.Children.Add(header);
            body.Children.Add(_graphsHost);
            body.Children.Add(_fieldsHost);

            var card = new Border
            {
                Padding = new Thickness(14, 12, 14, 12),
                CornerRadius = new CornerRadius(KillerShell.Services.ThemeManager.Radius("ChartCornerRadius", 4)),
                BorderThickness = new Thickness(1),
                Child = body,
            };
            // PaneBrush - see BuildStaticInfoPanel above; a card is content on the tab's chrome.
            card.SetResourceReference(Border.BackgroundProperty, "MonitorCellBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
            return card;
        }

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
            internal string Label = "";
            internal string Description = "-";
            internal Border TileBorder = null!;
            internal Border AccentBar = null!;
            internal TextBlock TileSummaryText = null!;
            internal Sparkline ThumbGraph = null!;
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
            _tileListPanel.Children.Clear();

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

            foreach (var tile in _tiles) _tileListPanel.Children.Add(tile.TileBorder);

            if (_tiles.Count > 0) SelectTile(_tiles[0]);
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
            // actually has focus (done in SelectTile); the checked state is set fresh in Opened
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

            tile.State = cs;
            tile.FieldLabelKeys = ["Str_Perf_Utilization", "Str_Perf_LogicalProcessors", "Str_Perf_Cores", "Str_Perf_BaseSpeed"];
            tile.FieldValues =
            [
                "-",
                info.CpuThreads > 0 ? info.CpuThreads.ToString(CultureInfo.InvariantCulture) : "-",
                info.CpuCores > 0 ? info.CpuCores.ToString(CultureInfo.InvariantCulture) : "-",
                info.CpuBaseMhz > 0 ? (info.CpuBaseMhz / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " GHz" : "-",
            ];

            FinishTile(tile, 100);
            return tile;
        }

        private MetricTile BuildRamTile(HardwareInfo info)
        {
            var tile = new MetricTile
            {
                Kind = MetricKind.Ram,
                Label = MainWindow.LocStatic("Str_Perf_Ram"),
                Description = info.Ram,
                State = new RamState(),
                GraphCaptionKeys = ["Str_Perf_Utilization"],
                BigGraphs = [new Sparkline(HistorySamples, 100, "PrimaryBrush")],
                FieldLabelKeys = ["Str_Perf_InUse", "Str_Perf_Available", "Str_Perf_Committed"],
                FieldValues = ["-", "-", "-"],
            };

            FinishTile(tile, 100);
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

            FinishTile(tile, 100);
            return tile;
        }

        private MetricTile BuildNetworkTile(string instanceName, int index, int totalCount)
        {
            string label = MainWindow.LocStatic("Str_Perf_Network") + (totalCount > 1 ? " " + index : "");
            var tile = new MetricTile
            {
                Kind = MetricKind.Network,
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

            FinishTile(tile, 0, "TypeWindows", "PrimaryBrush");
            return tile;
        }

        private MetricTile BuildGpuTile(string name, int index)
        {
            var gs = new GpuState { MemoryAvailable = _gpuMemoryAvailable };
            var tile = new MetricTile
            {
                Kind = MetricKind.Gpu,
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

            FinishTile(tile, 100);
            return tile;
        }

        /// <summary>Builds the tile's own small on-screen Border (label, one-line summary, thumb
        /// sparkline) - shared by every MetricKind. <paramref name="thumbSeriesBrushKeys"/> empty
        /// means a single-series thumbnail in the accent color.</summary>
        private void FinishTile(MetricTile tile, double thumbFixedScaleMax, params string[] thumbSeriesBrushKeys)
        {
            tile.ThumbGraph = new Sparkline(HistorySamples, thumbFixedScaleMax,
                thumbSeriesBrushKeys.Length > 0 ? thumbSeriesBrushKeys : ["PrimaryBrush"]);

            // Left corners match the tile's radius. The bar sits in column 0 of the tile's child
            // grid, which is NOT clipped to the tile's CornerRadius, so a square bar painted its
            // own hard corners straight over the rounded ones - the selected tile looked square
            // down its left edge (Steve, 2026-08-08).
            var accentBar = new Border
            {
                Width = 3,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(KillerShell.Services.ThemeManager.Radius("ChartCornerRadius", 4), 0, 0,
                    KillerShell.Services.ThemeManager.Radius("ChartCornerRadius", 4)),
            };

            var labelText = new TextBlock { FontSize = 11, FontWeight = FontWeights.Bold, Text = tile.Label };
            labelText.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            labelText.SetResourceReference(TextBlock.ForegroundProperty, "MonitorTextBrush");

            var summaryText = new TextBlock { FontSize = 10.5, Text = "-" };
            summaryText.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            summaryText.SetResourceReference(TextBlock.ForegroundProperty, "MonitorMutedBrush");
            tile.TileSummaryText = summaryText;

            var textStack = new StackPanel { Margin = new Thickness(10, 7, 10, 4) };
            textStack.Children.Add(labelText);
            textStack.Children.Add(summaryText);

            tile.ThumbGraph.Host.Height = 26;
            tile.ThumbGraph.Host.Margin = new Thickness(10, 0, 10, 7);
            tile.ThumbGraph.Host.BorderThickness = new Thickness(0);
            // The sparkline well sits INSIDE the tile, so it takes the tile's own tier rather than
            // SurfaceBrush - which is #000000 on Black and punched a black hole in every tile.
            tile.ThumbGraph.Host.SetResourceReference(Border.BackgroundProperty, "PaneBrush");

            var body = new StackPanel();
            body.Children.Add(textStack);
            body.Children.Add(tile.ThumbGraph.Host);

            var innerGrid = new Grid();
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            SetColumn(accentBar, 0);
            SetColumn(body, 1);
            innerGrid.Children.Add(accentBar);
            innerGrid.Children.Add(body);

            var tileBorder = new Border
            {
                Margin = new Thickness(6, 3, 6, 3),
                CornerRadius = new CornerRadius(KillerShell.Services.ThemeManager.Radius("ChartCornerRadius", 4)),
                Cursor = Cursors.Hand,
                Child = innerGrid,
            };
            tileBorder.SetResourceReference(Border.BackgroundProperty, "MonitorCellBrush");

            var capturedTile = tile;
            tileBorder.MouseLeftButtonUp += (_, _) => SelectTile(capturedTile);
            tileBorder.MouseEnter += (_, _) =>
            {
                // MonitorHoverBrush = RowHoverBrush everywhere but 98SE, where RowHoverBrush is
                // the window face grey - a hovered black tile turned grey and vanished into the
                // window (Steve, 2026-08-09). There it is a slightly lifted black instead.
                if (!ReferenceEquals(_selectedTile, capturedTile))
                    tileBorder.SetResourceReference(Border.BackgroundProperty, "MonitorHoverBrush");
            };
            tileBorder.MouseLeave += (_, _) =>
            {
                if (!ReferenceEquals(_selectedTile, capturedTile))
                    tileBorder.SetResourceReference(Border.BackgroundProperty, "MonitorCellBrush");
            };

            tile.TileBorder = tileBorder;
            tile.AccentBar = accentBar;
        }

        // ═══════════════════════════════════════════════════════════
        //  SELECTION
        // ═══════════════════════════════════════════════════════════
        private void SelectTile(MetricTile tile)
        {
            if (ReferenceEquals(_selectedTile, tile)) return;
            if (_selectedTile != null) SetTileVisualSelected(_selectedTile, false);
            _selectedTile = tile;
            SetTileVisualSelected(tile, true);
            RebuildDetailFor(tile);

            if (tile.Kind == MetricKind.Cpu && tile.State is CpuState cs)
            {
                var graphArea = cs.GraphArea;
                // Give the graph area real keyboard focus once it has actually landed in the
                // visual tree (RebuildDetailFor just reparented it) so WPF's built-in Shift+F10 /
                // Menu-key handling has something focused to open the context menu on.
                Dispatcher.BeginInvoke(new Action(() => graphArea.Focus()), DispatcherPriority.Background);
            }
        }

        private static void SetTileVisualSelected(MetricTile tile, bool selected)
        {
            // MonitorCellBrush when not selected, ACTUALLY matching the tile's build and hover
            // states now - the comment always said it matched, but the key here was
            // MenuBackgroundBrush, so a deselected tile came back a different color than a
            // freshly built one (on 98SE: #c0c0c0 instead of the black cell).
            tile.TileBorder.SetResourceReference(Border.BackgroundProperty, selected ? "SelectionBg" : "MonitorCellBrush");
            tile.TileSummaryText.SetResourceReference(TextBlock.ForegroundProperty, selected ? "SelectionFg" : "MonitorMutedBrush");
            if (selected)
                tile.AccentBar.SetResourceReference(Border.BackgroundProperty, "PrimaryBrush");
            else
                tile.AccentBar.Background = Brushes.Transparent;
        }

        private void RebuildDetailFor(MetricTile tile)
        {
            _detailTitle.Text = tile.Label;
            _detailDescription.Text = tile.Description;

            _graphsHost.Children.Clear();

            if (tile.Kind == MetricKind.Cpu)
            {
                var cs = (CpuState)tile.State!;
                _graphsHost.Children.Add(BuildGraphCaption("Str_Perf_Utilization"));
                cs.GraphArea.Height = 160;
                _graphsHost.Children.Add(cs.GraphArea);
            }
            else
            {
                for (int i = 0; i < tile.BigGraphs.Length; i++)
                {
                    if (i < tile.GraphCaptionKeys.Length)
                        _graphsHost.Children.Add(BuildGraphCaption(tile.GraphCaptionKeys[i]));
                    tile.BigGraphs[i].Host.Height = tile.BigGraphs.Length > 1 ? 90 : 160;
                    _graphsHost.Children.Add(tile.BigGraphs[i].Host);
                }
            }

            if (tile.Kind == MetricKind.Network)
                _graphsHost.Children.Add(BuildLegend(("TypeWindows", "Str_Perf_Send"), ("PrimaryBrush", "Str_Perf_Receive")));
            else if (tile.Kind == MetricKind.Disk)
                _graphsHost.Children.Add(BuildLegend(("PrimaryBrush", "Str_Perf_ReadSpeed"), ("TypeWindows", "Str_Perf_WriteSpeed")));
            else if (tile.Kind == MetricKind.Gpu && tile.BigGraphs.Length >= 3)
                _graphsHost.Children.Add(BuildLegend(("PrimaryBrush", "Str_Perf_DedicatedMemory"), ("TypeWindows", "Str_Perf_SharedMemory")));

            _fieldsHost.Children.Clear();
            _fieldValueBlocks = new TextBlock[tile.FieldLabelKeys.Length];
            for (int i = 0; i < tile.FieldLabelKeys.Length; i++)
            {
                _fieldsHost.Children.Add(BuildField(tile.FieldLabelKeys[i], out var valueBlock));
                _fieldValueBlocks[i] = valueBlock;
            }

            RefreshDetailFieldValues(tile);
        }

        private void RefreshDetailFieldValues(MetricTile tile)
        {
            if (!ReferenceEquals(_selectedTile, tile) || _fieldValueBlocks == null) return;
            for (int i = 0; i < _fieldValueBlocks.Length && i < tile.FieldValues.Length; i++)
                _fieldValueBlocks[i].Text = tile.FieldValues[i];
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
                tile.ThumbGraph.Push(pct);
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
                    tile.ThumbGraph.Push(clamped);
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
                tile.ThumbGraph.Push(activePct);
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
                tile.ThumbGraph.Push(sent, recv);
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

            tile.ThumbGraph.Push(util);
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
