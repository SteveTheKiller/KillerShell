using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

// The Performance Monitor's dashboard: what goes where, and how much room it gets. The other
// half of PerformanceMonitorControl (PerformanceMonitorControl.cs) owns the counters, the
// sampling and the Sparkline; nothing here reads a counter.
//
//   summary strip   one tile per kind of hardware: its headline number and a bare trace
//   band 1          CPU (three fifths) beside memory (two fifths) - the two a person reads first
//   band 2          disks beside network adapters, ONE ROW PER DEVICE inside each panel
//   band 3          a card per GPU
//
// Designed as a whole rather than a card per device. A machine with four disks and five network
// adapters used to get nine identical full-size cells, most of them flat lines; here a device
// that is doing nothing is one line of text, and the space goes to whatever is busy.
namespace KillerShell.Tools
{
    internal sealed partial class PerformanceMonitorControl
    {
        private UniformGrid _summaryStrip = null!;
        private StackPanel _panels = null!;

        // The summary strip's tiles. Null for hardware this machine does not have (a server
        // with no GPU counters, a VM with no physical disk), which simply gets no tile.
        private sealed class SummaryTile
        {
            internal TextBlock Value = null!;
            internal Sparkline Trace = null!;
        }

        private SummaryTile? _sumCpu, _sumRam, _sumDisk, _sumNet, _sumGpu;

        private MetricTile? _cpuTile, _ramTile;
        private readonly List<MetricTile> _diskTiles = [];
        private readonly List<MetricTile> _netTiles = [];

        // ═══════════════════════════════════════════════════════════
        //  SHELLS  -  built with the control, filled once the hardware is known
        // ═══════════════════════════════════════════════════════════
        private UniformGrid BuildSummaryStrip()
        {
            _summaryStrip = new UniformGrid { Rows = 1 };
            // Same outer inset as the panels below, so the strip's first and last tiles line up
            // with the panels' outer edges (MonitorGridMargin + each card's MonitorTileMargin).
            _summaryStrip.SetResourceReference(FrameworkElement.MarginProperty, "MonitorGridMargin");
            return _summaryStrip;
        }

        private ScrollViewer BuildPanelsScroller()
        {
            _panels = new StackPanel { Orientation = Orientation.Vertical };

            var scroller = new ScrollViewer
            {
                Content = _panels,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            // MonitorGridMargin: 2 a side so the cards' own 6px MonitorTileMargin lands their
            // edges at 8; 0 on 98SE so the cards run to the pane edge like every other well (the
            // tab strip above runs to the window edge, and anything short of it reads as the
            // right edge being off).
            scroller.SetResourceReference(FrameworkElement.MarginProperty, "MonitorGridMargin");
            return scroller;
        }

        // ═══════════════════════════════════════════════════════════
        //  DASHBOARD
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Lays the tiles BuildTiles just made into the dashboard. Runs once per control: the
        /// hardware list is gathered once, and the finished elements are never rebuilt, so every
        /// graph keeps its history for the life of the tab.
        /// </summary>
        private void BuildDashboard()
        {
            _summaryStrip.Children.Clear();
            _panels.Children.Clear();
            _sumCpu = _sumRam = _sumDisk = _sumNet = _sumGpu = null;

            _cpuTile = _tiles.FirstOrDefault(t => t.Kind == MetricKind.Cpu);
            _ramTile = _tiles.FirstOrDefault(t => t.Kind == MetricKind.Ram);
            _diskTiles.Clear();
            _diskTiles.AddRange(_tiles.Where(t => t.Kind == MetricKind.Disk));
            _netTiles.Clear();
            _netTiles.AddRange(_tiles.Where(t => t.Kind == MetricKind.Network));

            // ── Summary strip ────────────────────────────────────
            // Each tile takes its panel's color, so the eye can go from a number up here to the
            // panel it came from without reading a label.
            if (_cpuTile != null) _sumCpu = AddSummaryTile("Str_Perf_Cpu", "MonitorAccentBrush", 100);
            if (_ramTile != null) _sumRam = AddSummaryTile("Str_Perf_Ram", "TypeWindows", 100);
            if (_diskTiles.Count > 0) _sumDisk = AddSummaryTile("Str_Perf_Disk", "WarnBrush", 100);
            if (_netTiles.Count > 0) _sumNet = AddSummaryTile("Str_Perf_Network", "MonitorTextBrush", 0);
            if (_gpuTiles.Count > 0) _sumGpu = AddSummaryTile("Str_Perf_Gpu", "OkBrush", 100);

            // ── Band 1: CPU and memory ───────────────────────────
            if (_cpuTile != null || _ramTile != null)
            {
                var band = new Grid();
                band.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
                band.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
                if (_cpuTile != null) band.Children.Add(BuildCpuPanel(_cpuTile));
                if (_ramTile != null)
                {
                    var ram = BuildRamPanel(_ramTile);
                    SetColumn(ram, 1);
                    band.Children.Add(ram);
                }
                _panels.Children.Add(band);
            }

            // ── Band 2: disks and network adapters ───────────────
            if (_diskTiles.Count > 0 || _netTiles.Count > 0)
            {
                var band = new Grid();
                band.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                band.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                Border? disks = _diskTiles.Count > 0 ? BuildDevicePanel("Str_Perf_Disk", _diskTiles) : null;
                Border? nets  = _netTiles.Count > 0 ? BuildDevicePanel("Str_Perf_Network", _netTiles) : null;

                // With only one of the two present it takes the whole band rather than sitting
                // beside an empty half.
                if (disks != null)
                {
                    if (nets == null) SetColumnSpan(disks, 2);
                    band.Children.Add(disks);
                }
                if (nets != null)
                {
                    if (disks == null) SetColumnSpan(nets, 2); else SetColumn(nets, 1);
                    band.Children.Add(nets);
                }
                _panels.Children.Add(band);
            }

            // ── Band 3: GPUs ─────────────────────────────────────
            if (_gpuTiles.Count > 0)
            {
                var band = new UniformGrid { Columns = Math.Min(2, _gpuTiles.Count) };
                foreach (var gpu in _gpuTiles) band.Children.Add(BuildGpuPanel(gpu));
                _panels.Children.Add(band);
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  SUMMARY STRIP
        // ═══════════════════════════════════════════════════════════
        private SummaryTile AddSummaryTile(string labelKey, string brushKey, double fixedScaleMax)
        {
            var label = new TextBlock { FontSize = 10.5 };
            label.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            label.SetResourceReference(TextBlock.ForegroundProperty, "MonitorMutedBrush");
            label.SetResourceReference(TextBlock.TextProperty, labelKey);

            var value = new TextBlock
            {
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Text = "-",
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            value.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            value.SetResourceReference(TextBlock.ForegroundProperty, brushKey);

            var trace = new Sparkline(HistorySamples, fixedScaleMax, brushKey);
            trace.MakeBare();
            trace.Host.Height = 26;
            trace.Host.Margin = new Thickness(0, 4, 0, 0);

            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            body.Children.Add(label);
            body.Children.Add(value);
            body.Children.Add(trace.Host);

            _summaryStrip.Children.Add(BuildCard(body));
            return new SummaryTile { Value = value, Trace = trace };
        }

        /// <summary>
        /// One tick's worth of headline numbers, after every panel has sampled. Several disks or
        /// GPUs report their BUSIEST one, because the question the strip answers is "is anything
        /// pinned", and an average of one saturated disk and three idle ones says no. Network
        /// adapters add up, because traffic is traffic whichever adapter carried it.
        /// </summary>
        private void UpdateSummary()
        {
            if (_sumCpu != null && _cpuTile != null) SetSummaryPercent(_sumCpu, _cpuTile.Primary);
            if (_sumRam != null && _ramTile != null) SetSummaryPercent(_sumRam, _ramTile.Primary);
            if (_sumDisk != null) SetSummaryPercent(_sumDisk, _diskTiles.Max(t => t.Primary));
            if (_sumGpu != null) SetSummaryPercent(_sumGpu, _gpuTiles.Max(t => t.Primary));
            if (_sumNet != null)
            {
                double total = _netTiles.Sum(t => t.Primary);
                _sumNet.Value.Text = FormatThroughput(total);
                _sumNet.Trace.Push(total);
            }
        }

        private static void SetSummaryPercent(SummaryTile tile, double percent)
        {
            tile.Value.Text = percent.ToString("0", CultureInfo.InvariantCulture) + " %";
            tile.Trace.Push(percent);
        }

        // ═══════════════════════════════════════════════════════════
        //  PANEL PIECES
        // ═══════════════════════════════════════════════════════════
        /// <summary>Title on the left, the panel's live headline on the right, at the size that
        /// makes it the first thing read. The headline block becomes the tile's
        /// TileSummaryText, which is what the sampling code writes to.</summary>
        private static Grid BuildPanelHeader(MetricTile tile, double titleSize, double valueSize)
        {
            var title = new TextBlock
            {
                FontSize = titleSize,
                FontWeight = FontWeights.Bold,
                Text = tile.Label,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            title.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            title.SetResourceReference(TextBlock.ForegroundProperty, "MonitorTextBrush");

            var value = new TextBlock
            {
                FontSize = valueSize,
                FontWeight = FontWeights.Bold,
                Text = "-",
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            value.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            value.SetResourceReference(TextBlock.ForegroundProperty, tile.SummaryBrushKey);
            tile.TileSummaryText = value;

            // One height for every panel header, whatever size its headline is set in, so the
            // titles of two panels sharing a band sit on the same line.
            var header = new Grid { MinHeight = 32 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            SetColumn(value, 1);
            header.Children.Add(title);
            header.Children.Add(value);
            return header;
        }

        // Hardware names get their own line under the title. They are what tells two disks, two
        // GPUs or two network adapters apart, and sharing the title's line truncated them.
        private static TextBlock BuildDescription(string text)
        {
            var description = new TextBlock
            {
                FontSize = 11,
                Text = text,
                ToolTip = text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            };
            description.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            description.SetResourceReference(TextBlock.ForegroundProperty, "MonitorMutedBrush");
            return description;
        }

        // A fixed-column strip, not a WrapPanel: equal columns always fit, whatever the panel's
        // width, so the strip's height never changes as the window is resized.
        private static UniformGrid BuildFields(MetricTile tile, int columns)
        {
            var fields = new UniformGrid { Columns = columns, Margin = new Thickness(0, 8, 0, 0) };
            tile.FieldValueBlocks = new TextBlock[tile.FieldLabelKeys.Length];
            for (int i = 0; i < tile.FieldLabelKeys.Length; i++)
            {
                string brushKey = i < tile.FieldBrushKeys.Length ? tile.FieldBrushKeys[i] : "MonitorTextBrush";
                var field = BuildField(tile.FieldLabelKeys[i], brushKey, out var valueBlock);
                field.Margin = new Thickness(0, 0, 12, 6);
                fields.Children.Add(field);
                tile.FieldValueBlocks[i] = valueBlock;
            }
            RefreshDetailFieldValues(tile);
            return fields;
        }

        // ═══════════════════════════════════════════════════════════
        //  CPU
        // ═══════════════════════════════════════════════════════════
        private static Border BuildCpuPanel(MetricTile tile)
        {
            var cs = (CpuState)tile.State!;

            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            body.Children.Add(BuildPanelHeader(tile, titleSize: 16, valueSize: 24));
            body.Children.Add(BuildDescription(tile.Description));

            cs.AggregateGraph.Host.Height = 132;
            cs.AggregateGraph.Host.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(cs.AggregateGraph.Host);

            // The per-processor strip. Hidden until its counters exist (BuildCoreCells): a
            // machine that cannot enumerate them gets the panel without the strip, not an empty
            // caption over nothing.
            cs.CoreStrip = new UniformGrid();
            var coreBlock = new StackPanel { Visibility = Visibility.Collapsed };
            coreBlock.Children.Add(BuildGraphCaption("Str_Perf_LogicalProcessors"));
            coreBlock.Children.Add(cs.CoreStrip);
            body.Children.Add(coreBlock);

            body.Children.Add(BuildFields(tile, columns: 3));
            return BuildCard(body);
        }

        /// <summary>
        /// One square per logical processor, called once the per-processor counters exist
        /// (SetUpCoreCountersIfNeeded). Thirty-two to a row at most, so a 64- or 128-thread
        /// machine wraps into rows of readable squares instead of a row of slivers.
        /// </summary>
        private static void BuildCoreCells(CpuState cs)
        {
            int n = cs.CoreCounters.Length;
            if (n == 0 || cs.CoreStrip == null) return;

            int rows = (n + 31) / 32;
            cs.CoreStrip.Rows = rows;
            cs.CoreStrip.Columns = (n + rows - 1) / rows;
            cs.CoreStrip.Children.Clear();

            var radius = new CornerRadius(KillerShell.Services.ThemeManager.Radius("SmallCornerRadius", 3));
            cs.CoreFills = new Border[n];
            cs.CoreCells = new FrameworkElement[n];
            for (int i = 0; i < n; i++)
            {
                // The fill is a separate element so its opacity can carry the load without
                // fading the square's outline along with it.
                var fill = new Border { CornerRadius = radius, Opacity = 0.10 };
                fill.SetResourceReference(Border.BackgroundProperty, "MonitorAccentBrush");

                var cell = new Border
                {
                    Height = 20,
                    Margin = new Thickness(1),
                    CornerRadius = radius,
                    BorderThickness = new Thickness(1),
                    Child = fill,
                };
                cell.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");

                cs.CoreFills[i] = fill;
                cs.CoreCells[i] = cell;
                cs.CoreStrip.Children.Add(cell);
            }

            if (cs.CoreStrip.Parent is FrameworkElement block) block.Visibility = Visibility.Visible;
        }

        // ═══════════════════════════════════════════════════════════
        //  MEMORY
        // ═══════════════════════════════════════════════════════════
        private static Border BuildRamPanel(MetricTile tile)
        {
            var rs = (RamState)tile.State!;

            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            // Smaller than the CPU's headline: this one is "12.4/31.9 GB (39%)", not two digits.
            body.Children.Add(BuildPanelHeader(tile, titleSize: 16, valueSize: 14));
            body.Children.Add(BuildDescription(tile.Description));

            var graph = tile.BigGraphs[0];
            graph.Host.Height = 132;
            graph.Host.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(graph.Host);

            // What the total is made of, as one bar: in use, cached, free. Star widths, so the
            // three sizes ARE the three numbers and SampleRamTile only has to set them.
            var bar = new Grid { Height = 12 };
            string[] segmentBrushes = ["TypeWindows", "MonitorAccentBrush", "MonitorMutedBrush"];
            rs.BarColumns = new ColumnDefinition[segmentBrushes.Length];
            for (int i = 0; i < segmentBrushes.Length; i++)
            {
                // Equal thirds until the first sample lands, rather than three zero-width stars.
                rs.BarColumns[i] = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
                bar.ColumnDefinitions.Add(rs.BarColumns[i]);

                var segment = new Border { Opacity = i == 2 ? 0.35 : 1.0 };   // free reads as empty
                segment.SetResourceReference(Border.BackgroundProperty, segmentBrushes[i]);
                SetColumn(segment, i);
                bar.Children.Add(segment);
            }
            var barFrame = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(1),
                Margin = new Thickness(0, 10, 0, 0),
                CornerRadius = new CornerRadius(KillerShell.Services.ThemeManager.Radius("SmallCornerRadius", 3)),
                Child = bar,
            };
            barFrame.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
            body.Children.Add(barFrame);

            body.Children.Add(BuildLegend(out tile.LegendValueBlocks,
                ("TypeWindows", "Str_Perf_InUse"), ("MonitorAccentBrush", "Str_Perf_Cached"),
                ("MonitorMutedBrush", "Str_Perf_Free")));

            body.Children.Add(BuildFields(tile, columns: 2));
            return BuildCard(body);
        }

        // ═══════════════════════════════════════════════════════════
        //  DISKS AND NETWORK ADAPTERS  -  one panel per kind, one row per device
        // ═══════════════════════════════════════════════════════════
        /// <summary>Bytes per second below which a device counts as doing nothing. A kilobyte: a
        /// connected adapter chatters a few hundred bytes of broadcast traffic forever, and that
        /// is not activity anyone opened this tab to see.</summary>
        private const double RowActivityBytes = 1024;

        /// <summary>A full history window. Folding any sooner would hide a graph that still has
        /// the burst that opened it on screen.</summary>
        private const int RowIdleTicksToFold = HistorySamples;

        private static Border BuildDevicePanel(string titleKey, List<MetricTile> devices)
        {
            var title = new TextBlock { FontSize = 16, FontWeight = FontWeights.Bold };
            title.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            title.SetResourceReference(TextBlock.ForegroundProperty, "MonitorTextBrush");
            title.SetResourceReference(TextBlock.TextProperty, titleKey);

            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            body.Children.Add(title);

            for (int i = 0; i < devices.Count; i++)
            {
                // A hairline between devices, none above the first: the title already separates
                // it from the panel's edge.
                if (i > 0)
                {
                    var rule = new Border { Height = 1, Opacity = 0.6, Margin = new Thickness(0, 8, 0, 0) };
                    rule.SetResourceReference(Border.BackgroundProperty, "PaneBorderBrush");
                    body.Children.Add(rule);
                }
                body.Children.Add(BuildDeviceRow(devices[i]));
            }
            return BuildCard(body);
        }

        /// <summary>
        /// A device's row: its name, model and headline number on one line, and under that its
        /// mirrored graph and the two rates. The part under the line starts folded away and
        /// opens the first time the device does anything (UpdateRowActivity).
        /// </summary>
        private static StackPanel BuildDeviceRow(MetricTile tile)
        {
            var label = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.Bold, Text = tile.Label };
            label.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            label.SetResourceReference(TextBlock.ForegroundProperty, "MonitorTextBrush");

            var model = BuildDescription(tile.Description);
            model.Margin = new Thickness(10, 0, 10, 0);
            model.VerticalAlignment = VerticalAlignment.Bottom;

            var idle = new TextBlock { FontSize = 10.5, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Bottom };
            idle.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            idle.SetResourceReference(TextBlock.ForegroundProperty, "MonitorMutedBrush");
            idle.SetResourceReference(TextBlock.TextProperty, "Str_Perf_Idle");
            tile.RowIdleText = idle;

            var value = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.Bold, Text = "-", VerticalAlignment = VerticalAlignment.Bottom };
            value.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            value.SetResourceReference(TextBlock.ForegroundProperty,
                tile.Kind == MetricKind.Network ? "MonitorTextBrush" : tile.SummaryBrushKey);
            tile.TileSummaryText = value;

            var header = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            SetColumn(model, 1);
            SetColumn(idle, 2);
            SetColumn(value, 3);
            header.Children.Add(label);
            header.Children.Add(model);
            header.Children.Add(idle);
            header.Children.Add(value);

            var graph = tile.BigGraphs[0];
            graph.Host.Height = 56;
            graph.Host.Margin = new Thickness(0, 6, 0, 0);

            var detail = new StackPanel { Visibility = Visibility.Collapsed };
            detail.Children.Add(graph.Host);
            detail.Children.Add(tile.Kind == MetricKind.Network
                ? BuildLegend(out tile.LegendValueBlocks,
                    ("TypeWindows", "Str_Perf_Send"), ("OkBrush", "Str_Perf_Receive"))
                : BuildLegend(out tile.LegendValueBlocks,
                    ("MonitorAccentBrush", "Str_Perf_ReadSpeed"), ("TypeWindows", "Str_Perf_WriteSpeed")));
            tile.RowGraph = detail;

            var row = new StackPanel();
            row.Children.Add(header);
            row.Children.Add(detail);
            return row;
        }

        /// <summary>
        /// Opens a row the moment its device does something, and folds it again only after a
        /// whole history window of nothing. Asymmetric on purpose: opening late would miss the
        /// start of the activity being looked for, and folding early would flap on a device
        /// that works in bursts.
        /// </summary>
        private static void UpdateRowActivity(MetricTile tile, bool active)
        {
            if (tile.RowGraph == null) return;

            if (active)
            {
                tile.IdleTicks = 0;
                if (!tile.RowExpanded) SetRowExpanded(tile, true);
            }
            else if (tile.RowExpanded && ++tile.IdleTicks >= RowIdleTicksToFold)
            {
                SetRowExpanded(tile, false);
            }
        }

        private static void SetRowExpanded(MetricTile tile, bool expanded)
        {
            tile.RowExpanded = expanded;
            tile.IdleTicks = 0;
            if (tile.RowGraph != null)
                tile.RowGraph.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            if (tile.RowIdleText != null)
                tile.RowIdleText.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        }

        // ═══════════════════════════════════════════════════════════
        //  GPU
        // ═══════════════════════════════════════════════════════════
        private static Border BuildGpuPanel(MetricTile tile)
        {
            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            body.Children.Add(BuildPanelHeader(tile, titleSize: 16, valueSize: 24));
            body.Children.Add(BuildDescription(tile.Description));

            var util = tile.BigGraphs[0];
            util.Host.Height = 84;

            if (tile.BigGraphs.Length < 2)
            {
                body.Children.Add(BuildGraphCaption("Str_Perf_Utilization"));
                body.Children.Add(util.Host);
                return BuildCard(body);
            }

            // Utilization beside memory rather than stacked: the card is full width, and two
            // wide shallow graphs over each other spend height the panels above want.
            var memory = tile.BigGraphs[1];
            memory.Host.Height = 84;

            var graphs = new Grid();
            graphs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            graphs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            graphs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            graphs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            graphs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var utilCaption = BuildGraphCaption("Str_Perf_Utilization");
            // The memory graph's caption IS its legend: it names both series and carries their
            // live values, so a second caption would only repeat it.
            var memoryLegend = BuildLegend(out tile.LegendValueBlocks,
                ("MonitorAccentBrush", "Str_Perf_DedicatedMemory"), ("TypeWindows", "Str_Perf_SharedMemory"));
            memoryLegend.VerticalAlignment = VerticalAlignment.Bottom;

            SetColumn(memoryLegend, 2);
            SetRow(util.Host, 1);
            SetRow(memory.Host, 1);
            SetColumn(memory.Host, 2);
            graphs.Children.Add(utilCaption);
            graphs.Children.Add(memoryLegend);
            graphs.Children.Add(util.Host);
            graphs.Children.Add(memory.Host);

            body.Children.Add(graphs);
            return BuildCard(body);
        }
    }
}
