using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Shapes;

// The Performance Monitor's dashboard: what goes where, and how much room it gets. The rest of
// PerformanceMonitorControl owns the counters, the sampling and the Sparkline
// (PerformanceMonitorControl.cs), moving and minimizing the cards (PerformanceMonitorCards.cs)
// and the motion between samples (PerformanceMonitorMotion.cs); nothing here reads a counter.
//
// The designed arrangement, which the user can drag into another order:
//
//   summary strip   one box per kind of hardware: its name, headline number, icon and a bare trace
//   row 1           CPU (three fifths) beside memory (two fifths) - the two a person reads first
//   row 2           disks beside network adapters, ONE ROW PER DEVICE inside each panel
//   row 3 on        a card per GPU, two to a row
//
// Designed as a whole rather than a card per device. A machine with four disks and five network
// adapters used to get nine identical full-size cells, most of them flat lines; here a device
// that is doing nothing is one line of text, and the space goes to whatever is busy.
//
// All of its text is the app face, Killer Sans UI (MonoFont), which 98SE and a font chosen in
// Fonts replace like everywhere else in the app.
namespace KillerShell.Tools
{
    internal sealed partial class PerformanceMonitorControl
    {
        private UniformGrid _summaryStrip = null!;
        private StackPanel _panels = null!;
        private ScrollViewer _panelsScroller = null!;

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
            // Same side inset as the panels below, so the strip's first and last tiles line up
            // with the panels' outer edges: 8 from the pane with each card's MonitorTileMargin.
            _summaryStrip.SetResourceReference(FrameworkElement.MarginProperty, "MonitorStripMargin");
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
            // ShadowScrollViewer draws the edge shadows under the scrollbar (Controls.xaml).
            scroller.SetResourceReference(FrameworkElement.StyleProperty, "ShadowScrollViewer");
            // MonitorPanelsPadding: 4 a side so the cards' own 4px MonitorTileMargin lands their
            // edges at 8; 0 on 98SE so the cards run to the pane edge like every other well (the
            // tab strip above runs to the window edge, and anything short of it reads as the
            // right edge being off). It is Padding, inside the scrolling content, so the edge
            // shadows still reach the pane edges.
            scroller.SetResourceReference(Control.PaddingProperty, "MonitorPanelsPadding");
            _panelsScroller = scroller;
            return scroller;
        }

        // ═══════════════════════════════════════════════════════════
        //  DASHBOARD
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Builds a card for every tile BuildTiles just made, in the designed order, and lays them
        /// out in the order the user left them (PerformanceMonitorCards.cs). Runs once per control:
        /// the hardware list is gathered once, and the finished cards are only ever moved, never
        /// rebuilt, so every graph keeps its history for the life of the tab.
        /// </summary>
        private void BuildDashboard()
        {
            ClearCards();   // PerformanceMonitorCards.cs
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
            if (_cpuTile != null) _sumCpu = AddSummaryTile("cpu", MetricKind.Cpu, "Str_Perf_Cpu", "MonitorAccentBrush", 100);
            if (_ramTile != null) _sumRam = AddSummaryTile("ram", MetricKind.Ram, "Str_Perf_Ram", "TypeWindows", 100);
            if (_diskTiles.Count > 0) _sumDisk = AddSummaryTile("disk", MetricKind.Disk, "Str_Perf_Disk", "WarnBrush", 100);
            if (_netTiles.Count > 0) _sumNet = AddSummaryTile("network", MetricKind.Network, "Str_Perf_Network", "MonitorTextBrush", 0);
            if (_gpuTiles.Count > 0 || _gpuEngineAvailable) _sumGpu = AddSummaryTile("gpu", MetricKind.Gpu, "Str_Perf_Gpu", "OkBrush", 100);

            // ── Panels, two to a row ─────────────────────────────
            // CPU beside memory, disks beside network adapters, then the GPUs. A card that is
            // alone in its row (an odd GPU, a VM with no disks) keeps one column of it unless
            // the user sets it to two (PerformanceMonitorCards.cs).
            if (_cpuTile != null) AddPanelCard(BuildCpuPanel(_cpuTile));
            if (_ramTile != null) AddPanelCard(BuildRamPanel(_ramTile));
            if (_diskTiles.Count > 0) AddPanelCard(BuildDevicePanel("disks", _diskTiles));
            if (_netTiles.Count > 0) AddPanelCard(BuildDevicePanel("network", _netTiles));

            // An adapter is named by its model, so a saved layout finds it again after a restart
            // (its LUID changes every boot).
            foreach (var gpu in _gpuTiles) AddPanelCard(BuildGpuPanel(gpu, UniquePanelId("gpu:" + gpu.Description)));

            ArrangeCards();   // PerformanceMonitorCards.cs
        }

        // ═══════════════════════════════════════════════════════════
        //  SUMMARY STRIP
        // ═══════════════════════════════════════════════════════════
        /// <summary>One box of the strip: the hardware's name small and muted, its headline number
        /// large in its panel's color under it, and a bare trace across the bottom, with the
        /// hardware's large icon in the room the number leaves at the top right. The whole box is
        /// the handle it is dragged by; minimized, it keeps its name, number and icon.</summary>
        private SummaryTile AddSummaryTile(string id, MetricKind kind, string labelKey, string brushKey, double fixedScaleMax)
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

            var reading = new StackPanel();
            reading.Children.Add(label);
            reading.Children.Add(value);

            // The same 28px icon the panels below carry beside their model names.
            var icon = BuildMetricIcon(kind, Shell.MainWindow.LocStatic(labelKey), 28);
            icon.HorizontalAlignment = HorizontalAlignment.Right;
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(8, 2, 0, 0);

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            SetColumn(icon, 1);
            header.Children.Add(reading);
            header.Children.Add(icon);

            var trace = new Sparkline(_cadence, HistorySamples, fixedScaleMax, brushKey);
            trace.MakeBare();
            trace.Host.Height = 26;
            trace.Host.Margin = new Thickness(0, 4, 0, 0);

            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            body.Children.Add(header);
            body.Children.Add(trace.Host);

            var frame = BuildCard(body);
            AddStripCard(new DashboardCard(Services.DashboardLayout.Strip, id, frame, [frame], trace.Host));
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
            if (_sumGpu != null)
            {
                if (_gpuCounterTiles.Count > 0) SetSummaryPercent(_sumGpu, _gpuCounterTiles.Values.Max(t => t.Primary));
                else ShowDash(_sumGpu.Value);
            }
            if (_sumNet != null)
            {
                double total = _netTiles.Sum(t => t.Primary);
                CountTo(_sumNet.Value, total, FormatThroughput);
                _sumNet.Trace.Push(total);
            }
        }

        private void SetSummaryPercent(SummaryTile tile, double percent)
        {
            CountTo(tile.Value, percent, FormatPercent);
            tile.Trace.Push(percent);
        }

        // ═══════════════════════════════════════════════════════════
        //  PANEL PIECES
        // ═══════════════════════════════════════════════════════════
        private static FrameworkElement BuildMetricIcon(MetricKind kind, string label, double size)
        {
            string outline = kind switch
            {
                MetricKind.Cpu => "M4,4 H12 V12 H4 Z M6,1 V4 M10,1 V4 M6,12 V15 M10,12 V15 M1,6 H4 M1,10 H4 M12,6 H15 M12,10 H15 M6,6 H10 V10 H6 Z",
                MetricKind.Ram => "M1,4 H15 V11 H1 Z M3,6 H6 V9 H3 Z M9,6 H12 V9 H9 Z M3,11 V14 M5,11 V14 M7,11 V14 M10,11 V14 M12,11 V14 M14,11 V14",
                MetricKind.Disk => "M3,1 H13 V15 H3 Z M11,7 A3,3 0 1 1 5,7 A3,3 0 1 1 11,7 M8,7 L12,11 M5,13 H7 M10,13 H11",
                MetricKind.Network => "M6,1 H10 V5 H6 Z M8,5 V8 M3,8 H13 M3,8 V11 M13,8 V11 M1,11 H5 V15 H1 Z M11,11 H15 V15 H11 Z",
                _ => "M1,2 V14 M1,4 H15 V12 H1 M11,8 A3,3 0 1 1 5,8 A3,3 0 1 1 11,8 M8,5 V11 M5,8 H11 M4,12 V14 H12 V12",
            };
            var path = new Path
            {
                Data = Geometry.Parse(outline), StrokeThickness = 1,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            path.SetResourceReference(Shape.StrokeProperty, "MonitorTextBrush");
            var canvas = new Canvas { Width = 16, Height = 16 };
            canvas.Children.Add(path);
            var icon = new Viewbox
            {
                Width = size, Height = size, Child = canvas, ToolTip = label,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(icon, label);
            return icon;
        }

        private static FrameworkElement BuildMetricTitle(MetricTile tile, double size)
        {
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            var icon = BuildMetricIcon(tile.Kind, tile.Label, size);
            title.Children.Add(icon);
            var label = new TextBlock
            {
                Text = tile.Label, FontSize = size, FontWeight = FontWeights.Bold,
                Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            label.SetResourceReference(TextBlock.ForegroundProperty, "MonitorTextBrush");
            title.Children.Add(label);
            return title;
        }

        /// <summary>
        /// A panel's one header line: a large icon in the corner, the hardware's own name beside
        /// it (it says more than "CPU" would, and the icon already says that), and the live
        /// headline on the right. The headline block becomes the tile's TileSummaryText, which is
        /// what the sampling code writes to. It is also the handle the card is dragged by, and
        /// all of the card that is left when it is minimized.
        /// </summary>
        private static Grid BuildPanelHeader(MetricTile tile, double valueSize, string name)
        {
            var icon = BuildMetricIcon(tile.Kind, tile.Label, 28);

            var title = new TextBlock
            {
                FontSize = 14,
                Text = name,
                ToolTip = tile.Description,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(10, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            title.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            title.SetResourceReference(TextBlock.ForegroundProperty, "MonitorTextBrush");

            var value = new TextBlock
            {
                FontSize = valueSize,
                FontWeight = FontWeights.Bold,
                Text = "-",
                VerticalAlignment = VerticalAlignment.Center,
            };
            value.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            value.SetResourceReference(TextBlock.ForegroundProperty, tile.SummaryBrushKey);
            tile.TileSummaryText = value;

            // One height for every panel header, whatever size its headline is set in, so the
            // titles of two panels sharing a row sit on the same line.
            var header = new Grid { MinHeight = 32, Background = Brushes.Transparent };   // hit-testable across its width
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            SetColumn(title, 1);
            SetColumn(value, 2);
            header.Children.Add(icon);
            header.Children.Add(title);
            header.Children.Add(value);
            return header;
        }

        // A device row's model, muted beside its title: what tells two disks or two network
        // adapters apart. It trims rather than wraps, with the whole name on hover.
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
        private static DashboardCard BuildCpuPanel(MetricTile tile)
        {
            var cs = (CpuState)tile.State!;

            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            var header = BuildPanelHeader(tile, valueSize: 24, tile.Description);
            body.Children.Add(header);

            var details = new StackPanel();   // everything under the header line, which minimizing folds away
            cs.AggregateGraph.Host.Height = 132;
            cs.AggregateGraph.Host.Margin = new Thickness(0, 8, 0, 0);
            details.Children.Add(cs.AggregateGraph.Host);

            // The per-processor strip. Hidden until its counters exist (BuildCoreCells): a
            // machine that cannot enumerate them gets the panel without the strip, not an empty
            // caption over nothing.
            cs.CoreStrip = new UniformGrid();
            var coreBlock = new StackPanel { Visibility = Visibility.Collapsed };
            coreBlock.Children.Add(BuildGraphCaption("Str_Perf_LogicalProcessors"));
            coreBlock.Children.Add(cs.CoreStrip);
            details.Children.Add(coreBlock);

            details.Children.Add(BuildFields(tile, columns: 3));
            body.Children.Add(details);
            return new DashboardCard(Services.DashboardLayout.Panels, "cpu", BuildCard(body), [header], details) { Weight = 3 };
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
        private static DashboardCard BuildRamPanel(MetricTile tile)
        {
            var rs = (RamState)tile.State!;

            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            // Smaller than the CPU's headline: this one is "12.4/31.9 GB (39%)", not two digits.
            var header = BuildPanelHeader(tile, valueSize: 14, tile.Description);
            body.Children.Add(header);

            var details = new StackPanel();   // everything under the header line, which minimizing folds away
            var graph = tile.BigGraphs[0];
            graph.Host.Height = 132;
            graph.Host.Margin = new Thickness(0, 8, 0, 0);
            details.Children.Add(graph.Host);

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
            details.Children.Add(barFrame);

            details.Children.Add(BuildLegend(out tile.LegendValueBlocks,
                ("TypeWindows", "Str_Perf_InUse"), ("MonitorAccentBrush", "Str_Perf_Cached"),
                ("MonitorMutedBrush", "Str_Perf_Free")));

            details.Children.Add(BuildFields(tile, columns: 2));
            body.Children.Add(details);
            return new DashboardCard(Services.DashboardLayout.Panels, "ram", BuildCard(body), [header], details);
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

        /// <summary>One card for every disk, or every network adapter: a row per device. Any row's
        /// header line picks the card up, and minimizing it holds every row on that line.</summary>
        private static DashboardCard BuildDevicePanel(string id, List<MetricTile> devices)
        {
            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            var handles = new List<FrameworkElement>(devices.Count);

            for (int i = 0; i < devices.Count; i++)
            {
                // Separate devices while keeping the first row close to the panel's edge.
                if (i > 0)
                {
                    var rule = new Border { Height = 1, Opacity = 0.6, Margin = new Thickness(0, 8, 0, 0) };
                    rule.SetResourceReference(Border.BackgroundProperty, "PaneBorderBrush");
                    body.Children.Add(rule);
                }
                body.Children.Add(BuildDeviceRow(devices[i], out var header));
                handles.Add(header);
            }
            return new DashboardCard(Services.DashboardLayout.Panels, id, BuildCard(body), [.. handles], details: null, devices);
        }

        /// <summary>
        /// A device's row: its name, model and headline number on one line, and under that its
        /// mirrored graph and the two rates. The part under the line starts folded away and
        /// opens the first time the device does anything (UpdateRowActivity).
        /// </summary>
        private static StackPanel BuildDeviceRow(MetricTile tile, out Grid header)
        {
            var label = BuildMetricTitle(tile, 12.5);

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

            header = new Grid { Margin = new Thickness(0, 8, 0, 0), Background = Brushes.Transparent };   // hit-testable across its width
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
            ApplyRowVisibility(tile);
        }

        /// <summary>A minimized card keeps every row on its header line, busy or not; the idle
        /// mark still says which devices are doing nothing.</summary>
        private static void ApplyRowVisibility(MetricTile tile)
        {
            tile.RowGraph?.Visibility = tile.RowExpanded && !tile.Folded ? Visibility.Visible : Visibility.Collapsed;
            tile.RowIdleText?.Visibility = tile.RowExpanded ? Visibility.Collapsed : Visibility.Visible;
        }

        // ═══════════════════════════════════════════════════════════
        //  GPU
        // ═══════════════════════════════════════════════════════════
        /// <summary>Like the CPU and memory cards, named by the adapter's model. Counters that no
        /// adapter claimed have no model to show, so that card is named by what it is and keeps
        /// its LUID for the hover.</summary>
        private static DashboardCard BuildGpuPanel(MetricTile tile, string id)
        {
            var gs = (GpuState)tile.State!;
            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            var header = BuildPanelHeader(tile, valueSize: 24, gs.Unassigned ? tile.Label : tile.Description);
            body.Children.Add(header);

            var details = new StackPanel();   // everything under the header line, which minimizing folds away
            body.Children.Add(details);
            var card = new DashboardCard(Services.DashboardLayout.Panels, id, BuildCard(body), [header], details);
            gs.Card = card;
            if (tile.BigGraphs.Length == 0) return card;
            var util = tile.BigGraphs[0];
            util.Host.Height = 84;

            if (tile.BigGraphs.Length < 2)
            {
                details.Children.Add(BuildGraphCaption("Str_Perf_Utilization"));
                details.Children.Add(util.Host);
                return card;
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

            details.Children.Add(graphs);
            return card;
        }
    }
}
