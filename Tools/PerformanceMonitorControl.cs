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
using KillerShell.Shell;

// The control behind a Performance Monitor tab: ONE designed dashboard, in KillerShell's own
// retro-terminal language (MonitorCellBrush cards, MonoFont readouts). A summary strip across the
// top carries the headline number and a trace for CPU, memory, disk, network and GPU; under it
// the CPU and memory panels take the room, every physical disk and network adapter is a row in
// its own panel, and each GPU gets a card. Everything is enumerated from the machine rather than
// assumed to be exactly one, and all of it is live at once - no master/detail, no selection.
//
// The layout is fixed on purpose. It used to be a two-column grid of identical cells the user
// could reorder and resize, which weighted a twelve-thread CPU and an unplugged network adapter
// the same. Now the busy panels get the space and an idle device folds to a single line.
//
// This file is the data side: counters, sampling, the tile model and the Sparkline. The
// dashboard's construction lives in PerformanceMonitorLayout.cs, the other half of this class.
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
namespace KillerShell.Tools
{
    internal sealed partial class PerformanceMonitorControl : Grid
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
        private readonly CancellationTokenSource _lifetimeCts = new();

        private readonly TextBlock _statusLine;
        private readonly DispatcherTimer _statusClearTimer;

        // ── Metric state ─────────────────────────────────────────
        // _tiles is every metric in build order; _gpuTiles is the GPU subset in the same order -
        // SampleGpus pairs sorted LUIDs against it by index, so its order must never change or
        // one adapter's activity would be relabeled as another's.
        private readonly List<MetricTile> _tiles = [];
        private readonly List<MetricTile> _gpuTiles = [];

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
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // summary strip
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // the panels
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // status line

            // PaneBrush, the same tier as the file browser's location row and the ACTIVE TAB, so
            // the tab floats down into this surface and the two read as one. The cards and tiles
            // on top of it take the menu tier, exactly as the file listing and the terminal do -
            // one rule for every tab: the tab's own surface is PaneBrush, its content is
            // MenuBackgroundBrush.
            this.SetResourceReference(Grid.BackgroundProperty, "PaneBrush");
            // Grain over that opaque face, spanning all three rows. PaneContent's own grain layer
            // is the FIRST child of its Grid, so it paints UNDER everything - an opaque root here
            // hides it and the tab comes up as the one flat, textureless surface in the window.
            // Same treatment as the other four tool tabs (Tools/ToolTabChrome.cs Grain), and the
            // same reason the folder location row carries its own grain over its own PaneBrush.
            // Added before the row content so the panel, tiles and status line all paint above it;
            // 0 opacity on 98SE, which draws no grain anywhere.
            var rootGrain = ToolTabChrome.Grain();
            SetRowSpan(rootGrain, 3);
            Children.Add(rootGrain);

            // Both are empty shells until the hardware is known (ApplyStaticInfo -> BuildDashboard);
            // the status line says so in the meantime.
            var summaryStrip = BuildSummaryStrip();     // PerformanceMonitorLayout.cs
            SetRow(summaryStrip, 0);
            Children.Add(summaryStrip);

            var panels = BuildPanelsScroller();         // PerformanceMonitorLayout.cs
            SetRow(panels, 1);
            Children.Add(panels);

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

                Services.PerformanceHardwareInfo info;
                try
                {
                    // LongRunning, not a pooled Task.Run - GatherStaticInfo does WMI work, and
                    // WMI's ManagementObjectSearcher/ManagementObject are COM RCWs that must run
                    // out their whole call on the SAME thread rather than a ThreadPool thread that
                    // might get reused mid-cleanup (see the long remark on this in
                    // ProcessListControl.cs Refresh() - it is the exact crash this app already hit
                    // once for real).
                    CancellationToken token = _lifetimeCts.Token;
                    info = await Task.Factory.StartNew(
                        () => Services.PerformanceHardwareService.Gather(token), token,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    ShowStatus(string.Format(MainWindow.LocStatic("Str_Perf_GatherFailed"), ex.Message), error: true);
                    info = Services.PerformanceHardwareInfo.Empty;
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
            _lifetimeCts.Cancel();
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
                        cs.Performance?.Dispose();
                        cs.Processes?.Dispose();
                        cs.Threads?.Dispose();
                        foreach (var c in cs.CoreCounters) c.Dispose();
                        cs.CoreCounters = [];
                        cs.Total = null;
                        cs.Performance = null;
                        cs.Processes = null;
                        cs.Threads = null;
                        break;
                    case MetricKind.Ram:
                        var rs = (RamState)tile.State!;
                        rs.Avail?.Dispose();
                        rs.Committed?.Dispose();
                        rs.Free?.Dispose();
                        rs.Avail = null;
                        rs.Committed = null;
                        rs.Free = null;
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
        private static string FormatLinkSpeed(ulong bitsPerSecond)
            => Services.PerformanceMetricFormatter.LinkSpeed(bitsPerSecond);

        private void ApplyStaticInfo(Services.PerformanceHardwareInfo info)
        {
            // The device names that used to fill a hardware panel of their own now sit under the
            // title of the panel they describe (BuildDashboard), so this only keeps the total.
            _totalRamGb = info.TotalRamGb;

            BuildTiles(info);
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
        //  BUILD - shared card pieces (the dashboard itself is PerformanceMonitorLayout.cs)
        // ═══════════════════════════════════════════════════════════
        private static TextBlock BuildGraphCaption(string key)
        {
            var tb = new TextBlock { FontSize = 10.5, Margin = new Thickness(0, 6, 0, 2) };
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "MonitorMutedBrush");
            tb.SetResourceReference(TextBlock.TextProperty, key);
            return tb;
        }

        /// <summary>Small color-dot + label + live value under a multi-series graph. This is the
        /// one authoritative readout for those series: no duplicate value strip below it.</summary>
        private static WrapPanel BuildLegend(out TextBlock[] valueBlocks,
            params (string BrushKey, string LabelKey)[] entries)
        {
            var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
            var values = new List<TextBlock>(entries.Length);
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
                var value = new TextBlock { FontSize = 10.5, Text = "-", Margin = new Thickness(5, 0, 0, 0) };
                value.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                value.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
                item.Children.Add(dot);
                item.Children.Add(text);
                item.Children.Add(value);
                panel.Children.Add(item);
                values.Add(value);
            }
            valueBlocks = [.. values];
            return panel;
        }

        private static StackPanel BuildField(string labelKey, string valueBrushKey, out TextBlock valueBlock)
        {
            var label = new TextBlock { FontSize = 10 };
            label.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            label.SetResourceReference(TextBlock.ForegroundProperty, "MonitorMutedBrush");
            label.SetResourceReference(TextBlock.TextProperty, labelKey);

            var value = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0), Text = "-" };
            value.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            value.SetResourceReference(TextBlock.ForegroundProperty, valueBrushKey);

            // No MinWidth (the UniformGrid's equal thirds size the cells) and only a whisker of
            // bottom margin - the strip is the last thing in the card and rides the body inset.
            var stack = new StackPanel { Margin = new Thickness(0, 0, 12, 2) };
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
            internal TextBlock TileSummaryText = null!;   // the live headline beside the panel or row title
            internal string SummaryBrushKey = "MonitorMutedBrush";
            internal TextBlock[] LegendValueBlocks = [];
            internal TextBlock[] FieldValueBlocks = [];   // built with the panel, always live
            internal Sparkline[] BigGraphs = [];
            internal string[] FieldLabelKeys = [];
            internal string[] FieldBrushKeys = [];
            internal string[] FieldValues = [];
            internal object? State;

            // The one number the summary strip reads for this metric: a percentage for CPU, RAM,
            // disk and GPU, bytes per second for a network adapter.
            internal double Primary;

            // Disk and network ROWS only (PerformanceMonitorLayout.cs BuildDeviceRow): a device
            // with nothing happening on it folds down to its one header line.
            internal FrameworkElement? RowGraph;
            internal TextBlock? RowIdleText;
            internal bool RowExpanded;
            internal int IdleTicks;
        }

        private sealed class CpuState
        {
            internal PerformanceCounter? Total;
            internal PerformanceCounter? Performance;   // % of base clock, for the live speed
            internal PerformanceCounter? Processes;
            internal PerformanceCounter? Threads;
            internal PerformanceCounter[] CoreCounters = [];
            // One square per logical processor, its fill's opacity tracking that processor's
            // load. Replaces a grid of per-core graphs that were mostly empty boxes on a
            // many-thread CPU.
            internal Border[] CoreFills = [];
            internal FrameworkElement[] CoreCells = [];
            internal UniformGrid CoreStrip = null!;
            internal Sparkline AggregateGraph = null!;
            internal int PhysicalCores;
            internal int LogicalProcessors;
            internal int BaseMhz;
        }

        private sealed class RamState
        {
            internal PerformanceCounter? Avail;
            internal PerformanceCounter? Committed;
            internal PerformanceCounter? Free;          // free and zeroed pages; available minus this is cached
            internal ColumnDefinition[] BarColumns = [];
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

        private void BuildTiles(Services.PerformanceHardwareInfo info)
        {
            _tiles.Clear();
            _gpuTiles.Clear();

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

            BuildDashboard();   // PerformanceMonitorLayout.cs
        }

        private static bool SafeCategoryExists(string category)
            => Services.PerformanceCounterService.CategoryExists(category);

        private static MetricTile BuildCpuTile(Services.PerformanceHardwareInfo info)
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
                AggregateGraph = new Sparkline(HistorySamples, 100, "MonitorAccentBrush"),
            };

            tile.State = cs;
            tile.SummaryBrushKey = "MonitorAccentBrush";
            // Two facts that never change, then four that do (SampleCpuTile fills those in).
            tile.FieldLabelKeys = ["Str_Perf_Cores", "Str_Perf_BaseSpeed", "Str_Perf_Speed",
                                   "Str_Perf_Processes", "Str_Perf_Threads", "Str_Perf_UpTime"];
            tile.FieldBrushKeys = ["MonitorTextBrush", "MonitorTextBrush", "MonitorAccentBrush",
                                   "MonitorTextBrush", "MonitorTextBrush", "MonitorTextBrush"];
            tile.FieldValues =
            [
                info.CpuCores > 0 && info.CpuThreads > 0
                    ? info.CpuCores.ToString(CultureInfo.InvariantCulture) + "C / "
                      + info.CpuThreads.ToString(CultureInfo.InvariantCulture) + "T"
                    : info.CpuCores > 0 ? info.CpuCores.ToString(CultureInfo.InvariantCulture) : "-",
                info.CpuBaseMhz > 0 ? (info.CpuBaseMhz / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " GHz" : "-",
                "-", "-", "-", "-",
            ];

            return tile;
        }

        private static MetricTile BuildRamTile(Services.PerformanceHardwareInfo info)
        {
            var tile = new MetricTile
            {
                Kind = MetricKind.Ram,
                Label = MainWindow.LocStatic("Str_Perf_Ram"),
                Description = info.Ram,
                State = new RamState(),
                SummaryBrushKey = "TypeWindows",
                BigGraphs = [new Sparkline(HistorySamples, 100, "TypeWindows")],
                // In use is already the complete headline (used / total and percentage).
                FieldLabelKeys = ["Str_Perf_Available", "Str_Perf_Committed"],
                FieldBrushKeys = ["MonitorTextBrush", "MonitorTextBrush"],
                FieldValues = ["-", "-"],
            };

            return tile;
        }

        private static MetricTile BuildDiskTile(Services.PerformanceDiskInfo d, int index)
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
                SummaryBrushKey = "WarnBrush",
                // Read = accent (the app's own "primary flow" color everywhere else), Write = the
                // family's second bright, theme-stable color (TypeWindows, reused from KillerScan's
                // device-type palette) - same two-color convention as the network graph below.
                // Mirrored: reads rise from the center line and writes hang below it, so the two
                // never hide each other. Active time is the row's headline number, not a graph.
                BigGraphs =
                [
                    new Sparkline(HistorySamples, 0, "MonitorAccentBrush", "TypeWindows")
                        { Mirrored = true, ScaleFormatter = FormatThroughput },
                ],
                FieldLabelKeys = [],
                FieldBrushKeys = [],
                FieldValues = [],
            };

            return tile;
        }

        private static MetricTile BuildNetworkTile(string instanceName, int index, int totalCount)
        {
            string label = MainWindow.LocStatic("Str_Perf_Network") + (totalCount > 1 ? " " + index : "");
            var tile = new MetricTile
            {
                Kind = MetricKind.Network,
                Label = label,
                Description = instanceName,
                State = new NetState { InstanceName = instanceName },
                // Send = blue, rising from the center line; Receive = theme-aware green, below it.
                BigGraphs =
                [
                    new Sparkline(HistorySamples, 0, "TypeWindows", "OkBrush")
                        { Mirrored = true, ScaleFormatter = FormatThroughput },
                ],
                FieldLabelKeys = [],
                FieldBrushKeys = [],
                FieldValues = [],
            };

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
                SummaryBrushKey = "OkBrush",
            };

            var utilGraph = new Sparkline(HistorySamples, 100, "OkBrush");
            if (gs.MemoryAvailable)
            {
                // Dedicated and shared memory use the same byte scale, so they belong in one
                // two-series plot. The colored legend below identifies both lines; repeating
                // those labels as graph captions only spends vertical space and separates data
                // that is easier to compare when overlaid.
                var memory = new Sparkline(HistorySamples, 0, "MonitorAccentBrush", "TypeWindows")
                    { ScaleFormatter = FormatBytes };
                tile.BigGraphs = [utilGraph, memory];
                tile.FieldLabelKeys = [];
                tile.FieldBrushKeys = [];
                tile.FieldValues = [];
            }
            else
            {
                tile.BigGraphs = [utilGraph];
                tile.FieldLabelKeys = [];
                tile.FieldBrushKeys = [];
                tile.FieldValues = [];
            }

            return tile;
        }

        // ═══════════════════════════════════════════════════════════
        //  CARDS  -  the one surface every panel of the dashboard sits on
        // ═══════════════════════════════════════════════════════════
        private static Border BuildCard(FrameworkElement body)
        {
            var cellRadius = new CornerRadius(KillerShell.Services.ThemeManager.Radius("ChartCornerRadius", 4));

            // The cell's own grain layer, under its content and over its own opaque face.
            // The control's ROOT paints grain across all three rows (see the ctor), but an
            // opaque MonitorCellBrush card sits ON TOP of that and covers it, so without this
            // the cards were the flat, textureless surfaces on an otherwise textured tab.
            // Every opaque face in this app repaints grain over itself for the same reason -
            // the folder LocationRow (Controls/FilePane.xaml) over its own PaneBrush, and
            // ToolTabChrome.WrapBar over its bar face. GrainOpacity is 0 on 98SE, so this
            // paints nothing there and that theme is unaffected.
            var cellGrain = ToolTabChrome.Grain();
            // Match the card's rounding: a Border does not clip its child, so a square grain
            // rectangle would paint noise into the four rounded corners the face leaves empty.
            cellGrain.CornerRadius = cellRadius;

            // The padding moves off the Border and onto the body, because a Border's Padding
            // insets its WHOLE child - grain included - which would leave an untextured ring
            // inside the card's edge. Same on-screen inset either way.
            body.Margin = new Thickness(14, 10, 14, 8);   // bottom rides on the fields' own 6px
            var cellHost = new Grid();
            cellHost.Children.Add(cellGrain);
            cellHost.Children.Add(body);

            var cell = new Border
            {
                CornerRadius = cellRadius,
                BorderThickness = new Thickness(1),
                // Stretch, the default: two cards sharing a band of the dashboard are meant to
                // end on the same line. The body inside stays top-aligned, so a shorter panel's
                // spare room falls below its content rather than being spread through it.
                Child = cellHost,
            };
            cell.SetResourceReference(Border.BackgroundProperty, "MonitorCellBrush");
            cell.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
            cell.SetResourceReference(FrameworkElement.MarginProperty, "MonitorTileMargin");
            return cell;
        }

        /// <summary>Writes a tile's current FieldValues into its cell's own value blocks -
        /// every cell is live all the time now, there is no selected-tile gate.</summary>
        private static void RefreshDetailFieldValues(MetricTile tile)
        {
            for (int i = 0; i < tile.FieldValueBlocks.Length && i < tile.FieldValues.Length; i++)
                tile.FieldValueBlocks[i].Text = tile.FieldValues[i];
        }

        // ═══════════════════════════════════════════════════════════
        //  CPU per-processor counters (the heat strip under the CPU graph)
        // ═══════════════════════════════════════════════════════════
        private static void SetUpCoreCountersIfNeeded(CpuState cs)
        {
            if (cs.CoreCounters.Length > 0) return;
            try
            {
                var names = new List<string>();
                foreach (string inst in Services.PerformanceCounterService.GetInstanceNames("Processor"))
                    if (inst != "_Total" && int.TryParse(inst, out _)) names.Add(inst);
                names.Sort((a, b) => int.Parse(a, CultureInfo.InvariantCulture).CompareTo(int.Parse(b, CultureInfo.InvariantCulture)));

                var counters = new PerformanceCounter[names.Count];
                for (int i = 0; i < names.Count; i++)
                {
                    counters[i] = TryCreateCounter("Processor", "% Processor Time", names[i])!;
                    if (counters[i] == null) return;
                    Services.PerformanceCounterService.Prime(counters[i]);
                }
                cs.CoreCounters = counters;
                BuildCoreCells(cs);   // PerformanceMonitorLayout.cs
            }
            catch { /* leave empty - the strip just stays hidden rather than throw */ }
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
                            cs.Total = TryCreateCounter("Processor", "% Processor Time", "_Total");
                            Services.PerformanceCounterService.Prime(cs.Total);

                            // Each of these is optional on its own: a machine missing one loses
                            // that one readout (its field stays a dash), not the CPU panel.
                            cs.Performance = TryCreateCounter("Processor Information", "% Processor Performance", "_Total");
                            Services.PerformanceCounterService.Prime(cs.Performance);
                            cs.Processes = TryCreateCounterNoInstance("System", "Processes");
                            cs.Threads = TryCreateCounterNoInstance("System", "Threads");
                            SetUpCoreCountersIfNeeded(cs);
                            break;
                        }
                        case MetricKind.Ram:
                        {
                            var rs = (RamState)tile.State!;
                            rs.Avail = TryCreateCounterNoInstance("Memory", "Available MBytes");
                            rs.Committed = TryCreateCounterNoInstance("Memory", "Committed Bytes");
                            rs.Free = TryCreateCounterNoInstance("Memory", "Free & Zero Page List Bytes");
                            break;
                        }
                        case MetricKind.Disk:
                        {
                            var ds = (DiskState)tile.State!;
                            ds.PercentTime = TryCreateCounter("PhysicalDisk", "% Disk Time", ds.InstanceName);
                            ds.ReadBytes = TryCreateCounter("PhysicalDisk", "Disk Read Bytes/sec", ds.InstanceName);
                            ds.WriteBytes = TryCreateCounter("PhysicalDisk", "Disk Write Bytes/sec", ds.InstanceName);
                            Services.PerformanceCounterService.Prime(ds.PercentTime);
                            Services.PerformanceCounterService.Prime(ds.ReadBytes);
                            Services.PerformanceCounterService.Prime(ds.WriteBytes);
                            break;
                        }
                        case MetricKind.Network:
                        {
                            var ns = (NetState)tile.State!;
                            ns.Sent = TryCreateCounter("Network Interface", "Bytes Sent/sec", ns.InstanceName);
                            ns.Recv = TryCreateCounter("Network Interface", "Bytes Received/sec", ns.InstanceName);
                            Services.PerformanceCounterService.Prime(ns.Sent);
                            Services.PerformanceCounterService.Prime(ns.Recv);
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
            => Services.PerformanceCounterService.Create(category, counter, instance);

        private static PerformanceCounter? TryCreateCounterNoInstance(string category, string counter)
            => Services.PerformanceCounterService.Create(category, counter);

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
            UpdateSummary();   // PerformanceMonitorLayout.cs - after every panel has its number
        }

        private static void SampleCpuTile(MetricTile tile)
        {
            var cs = (CpuState)tile.State!;
            if (cs.Total == null) return;
            try
            {
                if (!Services.PerformanceCounterService.TrySample(cs.Total, out double rawPercent)) return;
                double pct = Services.PerformanceCounterService.ClampPercent(rawPercent);
                tile.TileSummaryText.Text = pct.ToString("0", CultureInfo.InvariantCulture) + " %";
                tile.Primary = pct;
                cs.AggregateGraph.Push(pct);

                for (int i = 0; i < cs.CoreCounters.Length && i < cs.CoreFills.Length; i++)
                {
                    if (!Services.PerformanceCounterService.TrySample(cs.CoreCounters[i], out double rawCore)) break;
                    double c = Services.PerformanceCounterService.ClampPercent(rawCore);
                    // A floor, so an idle processor is a faint square rather than a hole in the
                    // strip; the rest of the range is the load.
                    cs.CoreFills[i].Opacity = 0.10 + 0.90 * (c / 100.0);
                    cs.CoreCells[i].ToolTip = i.ToString(CultureInfo.InvariantCulture) + ": "
                        + c.ToString("0", CultureInfo.InvariantCulture) + " %";
                }

                // Live clock = the base clock scaled by how hard the package is being driven. It
                // runs past 100 % under turbo, which is the point of showing it.
                if (cs.Performance != null && cs.BaseMhz > 0
                    && Services.PerformanceCounterService.TrySample(cs.Performance, out double perfPct) && perfPct > 0)
                    tile.FieldValues[2] = (cs.BaseMhz * perfPct / 100.0 / 1000.0)
                        .ToString("0.00", CultureInfo.InvariantCulture) + " GHz";
                if (cs.Processes != null && Services.PerformanceCounterService.TrySample(cs.Processes, out double procs))
                    tile.FieldValues[3] = procs.ToString("N0", CultureInfo.CurrentCulture);
                if (cs.Threads != null && Services.PerformanceCounterService.TrySample(cs.Threads, out double threads))
                    tile.FieldValues[4] = threads.ToString("N0", CultureInfo.CurrentCulture);
                tile.FieldValues[5] = Services.PerformanceMetricFormatter.UpTime(
                    TimeSpan.FromMilliseconds(GetTickCount64()));

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
                if (!Services.PerformanceCounterService.TrySample(rs.Avail, out double availMb)) return;
                if (_totalRamGb > 0)
                {
                    double availGb = availMb / 1024.0;
                    double usedGb = Math.Max(0, _totalRamGb - availGb);
                    double pct = usedGb / _totalRamGb * 100.0;
                    tile.TileSummaryText.Text = usedGb.ToString("0.0", CultureInfo.InvariantCulture) + "/" +
                        _totalRamGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB (" +
                        pct.ToString("0", CultureInfo.InvariantCulture) + "%)";
                    double clamped = Math.Min(100, pct);
                    tile.Primary = clamped;
                    if (tile.BigGraphs.Length > 0) tile.BigGraphs[0].Push(clamped);
                    tile.FieldValues[0] = availGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";

                    // The composition bar: in use, then cached (available memory that still holds
                    // file data and is handed back on demand), then truly free. Without the free
                    // counter the bar is two segments, in use and available.
                    double freeGb = availGb;
                    if (rs.Free != null && Services.PerformanceCounterService.TrySample(rs.Free, out double freeBytes))
                        freeGb = Math.Min(availGb, freeBytes / 1024.0 / 1024.0 / 1024.0);
                    double cachedGb = Math.Max(0, availGb - freeGb);
                    if (rs.BarColumns.Length == 3)
                    {
                        rs.BarColumns[0].Width = new GridLength(usedGb, GridUnitType.Star);
                        rs.BarColumns[1].Width = new GridLength(cachedGb, GridUnitType.Star);
                        rs.BarColumns[2].Width = new GridLength(freeGb, GridUnitType.Star);
                    }
                    if (tile.LegendValueBlocks.Length >= 3)
                    {
                        tile.LegendValueBlocks[0].Text = usedGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
                        tile.LegendValueBlocks[1].Text = cachedGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
                        tile.LegendValueBlocks[2].Text = freeGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
                    }
                }
                else
                {
                    tile.TileSummaryText.Text = availMb.ToString("0", CultureInfo.InvariantCulture) + " MB free";
                }

                if (rs.Committed != null)
                {
                    if (!Services.PerformanceCounterService.TrySample(rs.Committed, out double committedBytes)) return;
                    double committedMb = committedBytes / 1024.0 / 1024.0;
                    tile.FieldValues[1] = (committedMb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
                }

                RefreshDetailFieldValues(tile);
            }
            catch { }
        }

        private static void SampleDiskTile(MetricTile tile)
        {
            var ds = (DiskState)tile.State!;
            try
            {
                if (!Services.PerformanceCounterService.TrySample(ds.PercentTime, out double activeRaw) ||
                    !Services.PerformanceCounterService.TrySample(ds.ReadBytes, out double readBps) ||
                    !Services.PerformanceCounterService.TrySample(ds.WriteBytes, out double writeBps)) return;
                double activePct = Services.PerformanceCounterService.ClampPercent(activeRaw);

                tile.TileSummaryText.Text = activePct.ToString("0", CultureInfo.InvariantCulture) + " %";
                tile.Primary = activePct;
                tile.BigGraphs[0].Push(readBps, writeBps);
                UpdateRowActivity(tile, activePct >= 1 || readBps + writeBps >= RowActivityBytes);   // PerformanceMonitorLayout.cs
                if (tile.LegendValueBlocks.Length >= 2)
                {
                    tile.LegendValueBlocks[0].Text = FormatThroughput(readBps);
                    tile.LegendValueBlocks[1].Text = FormatThroughput(writeBps);
                }

                RefreshDetailFieldValues(tile);
            }
            catch { }
        }

        private static void SampleNetworkTile(MetricTile tile)
        {
            var ns = (NetState)tile.State!;
            try
            {
                if (!Services.PerformanceCounterService.TrySample(ns.Sent, out double sent) ||
                    !Services.PerformanceCounterService.TrySample(ns.Recv, out double recv)) return;

                if (tile.LegendValueBlocks.Length >= 2)
                {
                    tile.LegendValueBlocks[0].Text = FormatThroughput(sent);
                    tile.LegendValueBlocks[1].Text = FormatThroughput(recv);
                }
                tile.Primary = sent + recv;
                tile.TileSummaryText.Text = FormatThroughput(sent + recv);
                tile.BigGraphs[0].Push(sent, recv);
                UpdateRowActivity(tile, sent + recv >= RowActivityBytes);   // PerformanceMonitorLayout.cs

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
                    _cachedGpuEngineInstances =
                        Services.PerformanceCounterService.GetInstanceNames("GPU Engine");

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
                        PerformanceCounter? created = TryCreateCounter(
                            "GPU Engine", "Utilization Percentage", inst);
                        try
                        {
                            if (created != null)
                            {
                                Services.PerformanceCounterService.Prime(created);
                                _gpuEngineCounters[inst] = created;
                                created = null;
                            }
                        }
                        finally { created?.Dispose(); }
                        continue;
                    }

                    if (Services.PerformanceCounterService.TrySample(pc, out double sample))
                    {
                        luidUtil.TryGetValue(luid, out double cur);
                        luidUtil[luid] = cur + sample;
                    }
                }

                var luidDedicated = new Dictionary<string, double>(StringComparer.Ordinal);
                var luidShared = new Dictionary<string, double>(StringComparer.Ordinal);
                if (_gpuMemoryAvailable)
                {
                    if (rescan)
                    {
                        _cachedGpuMemInstances =
                            Services.PerformanceCounterService.GetInstanceNames("GPU Adapter Memory");

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
                        if (!_gpuMemDedicatedCounters.TryGetValue(inst, out var dedPc))
                        {
                            dedPc = TryCreateCounter("GPU Adapter Memory", "Dedicated Usage", inst);
                            if (dedPc != null) _gpuMemDedicatedCounters[inst] = dedPc;
                        }
                        if (!_gpuMemSharedCounters.TryGetValue(inst, out var shrPc))
                        {
                            shrPc = TryCreateCounter("GPU Adapter Memory", "Shared Usage", inst);
                            if (shrPc != null) _gpuMemSharedCounters[inst] = shrPc;
                        }
                        if (dedPc != null && shrPc != null &&
                            Services.PerformanceCounterService.TrySample(dedPc, out double ded) &&
                            Services.PerformanceCounterService.TrySample(shrPc, out double shr))
                        {
                            luidDedicated.TryGetValue(luid, out double curD); luidDedicated[luid] = curD + ded;
                            luidShared.TryGetValue(luid, out double curS); luidShared[luid] = curS + shr;
                        }
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
            => Services.PerformanceMetricFormatter.GpuLuid(instanceName);

        private static void ApplyGpuSample(MetricTile tile, double util, double dedicatedBytes, double sharedBytes)
        {
            util = Math.Min(100, Math.Max(0, util));
            var gs = (GpuState)tile.State!;

            tile.BigGraphs[0].Push(util);
            tile.Primary = util;
            tile.TileSummaryText.Text = util.ToString("0", CultureInfo.InvariantCulture) + " %";

            if (gs.MemoryAvailable && tile.BigGraphs.Length >= 2)
            {
                tile.BigGraphs[1].Push(dedicatedBytes, sharedBytes);
                if (tile.LegendValueBlocks.Length >= 2)
                {
                    tile.LegendValueBlocks[0].Text = FormatBytes(dedicatedBytes);
                    tile.LegendValueBlocks[1].Text = FormatBytes(sharedBytes);
                }
            }

            RefreshDetailFieldValues(tile);
        }

        // ═══════════════════════════════════════════════════════════
        //  FORMATTING
        // ═══════════════════════════════════════════════════════════
        private static string FormatThroughput(double bytesPerSec)
            => Services.PerformanceMetricFormatter.Throughput(bytesPerSec);

        private static string FormatBytes(double bytes)
            => Services.PerformanceMetricFormatter.Bytes(bytes);

        // Milliseconds since boot. Environment.TickCount wraps after 24.9 days, which is well
        // inside how long a server or a field laptop stays up.
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

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
            private readonly Polygon[] _fills;
            private readonly Line[] _gridLines = new Line[3];
            private readonly TextBlock _scaleLabel;
            private readonly Border _grain;
            private readonly List<double>[] _seriesSamples;
            private readonly int _maxSamples;
            private readonly Services.MetricHistory _history;

            /// <summary>
            /// Two series drawn away from a shared center line instead of over each other: the
            /// first rises from it, the second hangs below it, both on the one scale. For a pair
            /// that are read against each other (send and receive, read and write), where an
            /// overlay lets the busier trace bury the quieter one.
            /// </summary>
            internal bool Mirrored { get; set; }

            /// <summary>What the top of an AUTO-scaled graph is worth, in words. A trace with no
            /// stated scale only shows shape; this is what makes it a measurement. Fixed-scale
            /// graphs are percentages and label themselves.</summary>
            internal Func<double, string>? ScaleFormatter { get; set; }

            internal Sparkline(int maxSamples, double fixedScaleMax, params string[] brushKeys)
            {
                _maxSamples = maxSamples;
                int n = Math.Max(1, brushKeys.Length);
                _lines = new Polyline[n];
                _fills = new Polygon[n];
                _seriesSamples = new List<double>[n];
                _history = new Services.MetricHistory(n, maxSamples, fixedScaleMax);

                _canvas = new Canvas { ClipToBounds = true };

                // Quarter lines first, so the fills and traces draw over them. Quarters of a
                // plain graph are 25/50/75 %; on a mirrored one the middle line IS the axis.
                for (int i = 0; i < _gridLines.Length; i++)
                {
                    var rule = new Line
                    {
                        StrokeThickness = 1,
                        StrokeDashArray = [2, 4],
                        Opacity = 0.28,
                        SnapsToDevicePixels = true,
                    };
                    rule.SetResourceReference(Shape.StrokeProperty, "MonitorMutedBrush");
                    _gridLines[i] = rule;
                    _canvas.Children.Add(rule);
                }

                // Every fill under every trace, so a second series' fill never paints over the
                // first series' line.
                for (int i = 0; i < n; i++)
                {
                    var fill = new Polygon { Opacity = 0.18 };
                    fill.SetResourceReference(Shape.FillProperty, i < brushKeys.Length ? brushKeys[i] : "MonitorAccentBrush");
                    _fills[i] = fill;
                    _canvas.Children.Add(fill);
                }
                for (int i = 0; i < n; i++)
                {
                    _seriesSamples[i] = (List<double>)_history.Series[i];
                    var line = new Polyline { StrokeThickness = 1.5 };
                    line.SetResourceReference(Shape.StrokeProperty, i < brushKeys.Length ? brushKeys[i] : "MonitorAccentBrush");
                    _lines[i] = line;
                    _canvas.Children.Add(line);
                }
                _canvas.SizeChanged += (_, _) => Redraw();

                _scaleLabel = new TextBlock
                {
                    FontSize = 9,
                    Margin = new Thickness(5, 2, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    IsHitTestVisible = false,
                    Text = fixedScaleMax > 0 ? fixedScaleMax.ToString("0", CultureInfo.InvariantCulture) + " %" : string.Empty,
                };
                _scaleLabel.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                _scaleLabel.SetResourceReference(TextBlock.ForegroundProperty, "MonitorMutedBrush");

                var wellRadius = new CornerRadius(KillerShell.Services.ThemeManager.Radius("SmallCornerRadius", 3));

                // The graph well repaints grain over its own opaque face, exactly like the metric
                // cell it sits inside (see BuildDetailBody): an opaque MonitorCellBrush surface
                // covers the grain painted below it, and this well was the one such surface left
                // flat after the cells and the info panel were fixed on 2026-08-10. Grain first,
                // canvas second, so the plot lines draw over the texture; rounding matched to the
                // well's own corners because a Border does not clip its child.
                var wellGrain = ToolTabChrome.Grain();
                wellGrain.CornerRadius = wellRadius;
                _grain = wellGrain;

                var wellHost = new Grid();
                wellHost.Children.Add(wellGrain);
                wellHost.Children.Add(_canvas);
                wellHost.Children.Add(_scaleLabel);   // over the plot, in its top-left corner

                Host = new Border
                {
                    Height = 52,
                    CornerRadius = wellRadius,
                    BorderThickness = new Thickness(1),
                    Child = wellHost,
                };
                Host.SetResourceReference(Border.BackgroundProperty, "MonitorCellBrush");
                Host.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
            }

            /// <summary>One value per series, in the same order the brush keys were given.</summary>
            internal void Push(params double[] values)
            {
                _history.Push(values);
                Redraw();
            }

            /// <summary>
            /// The summary strip's version: the trace and its fill with nothing around them - no
            /// well, no border, no quarter lines, no scale. At that size it is a shape to glance
            /// at beside a number, and the full graph it summarizes is on the same screen.
            /// </summary>
            internal void MakeBare()
            {
                Host.BorderThickness = new Thickness(0);
                Host.Background = Brushes.Transparent;
                _grain.Visibility = Visibility.Collapsed;
                _scaleLabel.Visibility = Visibility.Collapsed;
                foreach (var rule in _gridLines) rule.Visibility = Visibility.Collapsed;
            }

            private void Redraw()
            {
                double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
                double stepX = _maxSamples > 1 ? w / (_maxSamples - 1) : 0;

                for (int i = 0; i < _gridLines.Length; i++)
                {
                    double y = Math.Round(h * (i + 1) / 4.0);
                    _gridLines[i].X1 = 0; _gridLines[i].X2 = w;
                    _gridLines[i].Y1 = y; _gridLines[i].Y2 = y;
                }

                if (ScaleFormatter != null) _scaleLabel.Text = ScaleFormatter(_history.ScaleMax);

                // A plain graph grows up from its floor. A mirrored one has its axis across the
                // middle and half the height to each side of it.
                bool mirrored = Mirrored && _lines.Length == 2;
                double axis = mirrored ? h / 2 : h;
                double reach = mirrored ? h / 2 : h;

                for (int s = 0; s < _lines.Length; s++)
                {
                    var samples = _seriesSamples[s];
                    if (w <= 0 || h <= 0 || samples.Count == 0)
                    {
                        _lines[s].Points = [];
                        _fills[s].Points = [];
                        continue;
                    }

                    double direction = mirrored && s == 1 ? +1 : -1;   // screen y grows downward
                    var pts = new PointCollection(samples.Count);
                    int startIndex = _maxSamples - samples.Count;   // newest sample lands at the right edge
                    for (int i = 0; i < samples.Count; i++)
                    {
                        double x = (startIndex + i) * stepX;
                        double frac = _history.ScaleMax > 0 ? Math.Min(1.0, samples[i] / _history.ScaleMax) : 0;
                        pts.Add(new Point(x, axis + direction * frac * reach));
                    }
                    _lines[s].Points = pts;

                    // The same outline closed back along the axis.
                    var area = new PointCollection(samples.Count + 2);
                    foreach (var p in pts) area.Add(p);
                    area.Add(new Point(pts[pts.Count - 1].X, axis));
                    area.Add(new Point(pts[0].X, axis));
                    _fills[s].Points = area;
                }
            }
        }
    }
}
