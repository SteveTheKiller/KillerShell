using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

using KillerShell.Models;
using KillerShell.Shell;

// The control behind an Event Viewer tab: a filterable, sortable grid over the three classic
// Windows Event Logs. Partial to nothing - same stand-alone shape as ProcessListControl.cs, and
// the same "own host, own control, MOVED not rebuilt between activations" rule: the picked log
// source, the level filter, the free-text filter and the grid's own sort/scroll all belong to
// the control instance, not to the tab that happens to be showing it.
//
// Reads through System.Diagnostics.Eventing.Reader (EventLogReader/EventLogQuery), not the
// classic System.Diagnostics.EventLog. The modern reader takes an XPath filter that the log
// itself evaluates before handing records back, so filtering by level does not mean reading
// every record in the log and throwing most of them away client-side - and it is the only one
// of the two APIs that can read the newest events first (EventLogQuery.ReverseDirection) rather
// than the oldest, which matters on a log with years of history in it.
//
// Unlike ProcessListControl there is no polling timer here: a process list changes constantly
// and a Task Manager tab left open is meant to keep tracking it, but an event log does not
// change on that cadence and re-querying it every couple of seconds would just be noise. Loading
// happens once, the first time the tab is actually shown, and again on a manual refresh or a
// log/level change - never silently in the background while you are looking at something else.
//
// This tab only exists at all behind Ctrl+F12, which relaunches the whole process elevated
// (Elevation.cs RelaunchElevatedEventViewer) - the Security log refuses to open for a process
// that is not, and there is no bare-key variant that would let you land here unelevated in the
// first place. Application and System still work fine without elevation, but the tab does not
// try to be two different things depending on the token it happens to be running with.
namespace KillerShell.Tools
{
    internal sealed class EventViewerControl : Grid
    {
        internal enum LogSource { Application, System, Security, All }
        internal enum LevelFilter { All, Error, Warning, Information }

        // Per-log ceiling on a background load, so picking "All" on a machine with years of
        // Security audit history cannot turn into an unbounded read. High enough to cover any
        // real troubleshooting question, low enough that it is not its own kind of freeze.
        private const int PerLogCap = 2000;

        // Once this many rows have landed, the "Loading..." status clears - the read keeps going
        // past this in the background, but the tab already has enough on screen to be useful.
        private const int FastFirstBatch = 200;
        private const int ApplyBatchSize = 50;

        private readonly ObservableCollection<EventLogEntryInfo> _items = [];
        private readonly ICollectionView _view;

        private readonly ComboBox  _logBox;
        private readonly ComboBox  _levelBox;
        private readonly TextBox   _filterBox;
        private readonly DataGrid  _grid;
        private readonly TextBlock _statusLine;
        private readonly DispatcherTimer _statusClearTimer;

        // Cancelled and replaced on every Reload (log/level change, manual refresh) and cancelled
        // for good on Shutdown - the same "let the background thread notice and stop between
        // records rather than being torn down mid-read" reasoning ProcessListControl's
        // _ownerCts carries, for the same reason: an EventLogReader is a native handle, not
        // something safe to abandon mid-call.
        private CancellationTokenSource? _loadCts;
        private bool _loadedOnce;

        internal EventViewerControl()
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // PaneBrush on the root, exactly like RegistryEditorControl: this control sits on
            // ResultsSurface's MenuBackgroundBrush, a full step darker than the pane, so with no
            // background of its own the whole tab read as a darker slab that did not match the
            // active tab above it - gray on a teal theme. The grid and its host are transparent
            // by design, so what shows through them is THIS brush.
            SetResourceReference(BackgroundProperty, "PaneBrush");
            // Grain over that opaque face, spanning all three rows - an opaque root hides the
            // pane's own grain layer, so the surface has to repaint it or the tab is flat.
            // The toolbar's WrapBar face is opaque above this and carries its own grain.
            var grain = ToolTabChrome.Grain();
            SetRowSpan(grain, 3);
            Children.Add(grain);

            // ToolTabChrome: on 98SE the filter row rides the RAISED menu-bar tier and the grid
            // sits in a sunken white well, matching every other tab kind; both wrappers are
            // inert on the ordinary themes.
            var toolbar = ToolTabChrome.WrapBar(BuildToolbar(out _logBox, out _levelBox, out _filterBox));
            SetRow(toolbar, 0);
            Children.Add(toolbar);

            _view = CollectionViewSource.GetDefaultView(_items);
            _view.Filter = FilterPredicate;
            _view.SortDescriptions.Add(new SortDescription(nameof(EventLogEntryInfo.Time), ListSortDirection.Descending));
            _filterBox.TextChanged += (_, _) => _view.Refresh();

            _grid = BuildGrid();
            _grid.ItemsSource = _view;
            // ToolGridMargin = the 8,0,8,8 the grid always had, except on 98SE, where it is 0: a
            // Win98 well is filled edge to edge, and the 8px gutters showed the well's white face
            // as a border around the table AND around the outside of its scrollbars.
            _grid.SetResourceReference(FrameworkElement.MarginProperty, "ToolGridMargin");
            var gridHost = ToolTabChrome.WrapContent(_grid, "ToolContentBrush");
            SetRow(gridHost, 1);
            Children.Add(gridHost);

            _statusLine = BuildStatusLine();
            SetRow(_statusLine, 2);
            Children.Add(_statusLine);

            _statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _statusClearTimer.Tick += (_, _) => { _statusClearTimer.Stop(); ShowStatus(string.Empty, error: false); };

            // Only the FIRST Loaded kicks off a load - unlike ProcessListControl's Loaded/Refresh
            // pair, this control is not meant to reload every time the tab is switched back to;
            // that would throw away the filter, the picked log/level and the grid's own scroll
            // position on every visit, exactly what "moved not rebuilt" exists to avoid.
            Loaded += (_, _) => { if (!_loadedOnce) { _loadedOnce = true; Reload(); } };
        }

        /// <summary>Torn down when the tab closes, and when the whole window closes with this
        /// tab still open (Session.cs OnClosing, via ShutdownAllEventViewers) - same reasoning as
        /// ProcessListControl.Shutdown().</summary>
        internal void Shutdown()
        {
            _statusClearTimer.Stop();
            CancelLoad();
        }

        /// <summary>Focus the filter box. Called after a tab switch lands on this control.</summary>
        internal void FocusFilter() => _filterBox.Focus();

        // ═══════════════════════════════════════════════════════════
        //  BUILD
        // ═══════════════════════════════════════════════════════════
        private Grid BuildToolbar(out ComboBox logBox, out ComboBox levelBox, out TextBox filterBox)
        {
            var bar = new Grid { Margin = new Thickness(8, 8, 8, 6) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            logBox = new ComboBox
            {
                Width = 130,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            AddItem(logBox, LogSource.Application, "Str_EvtLog_Application");
            AddItem(logBox, LogSource.System,      "Str_EvtLog_System");
            AddItem(logBox, LogSource.Security,    "Str_EvtLog_Security");
            AddItem(logBox, LogSource.All,         "Str_EvtLog_All");
            logBox.SelectedIndex = 0;
            logBox.SelectionChanged += (_, _) => Reload();
            logBox.SetResourceReference(ToolTipProperty, "Str_TT_EvtLogPicker");
            SetColumn(logBox, 0);
            bar.Children.Add(logBox);

            levelBox = new ComboBox
            {
                Width = 130,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            AddItem(levelBox, LevelFilter.All,         "Str_EvtLevel_All");
            AddItem(levelBox, LevelFilter.Error,       "Str_EvtLevel_Error");
            AddItem(levelBox, LevelFilter.Warning,     "Str_EvtLevel_Warning");
            AddItem(levelBox, LevelFilter.Information, "Str_EvtLevel_Information");
            levelBox.SelectedIndex = 0;
            levelBox.SelectionChanged += (_, _) => Reload();
            levelBox.SetResourceReference(ToolTipProperty, "Str_TT_EvtLevelPicker");
            SetColumn(levelBox, 1);
            bar.Children.Add(levelBox);

            filterBox = new TextBox
            {
                Margin = new Thickness(0, 0, 6, 0),
                Height = 26,
                Padding = new Thickness(6, 0, 6, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 12,
            };
            filterBox.SetResourceReference(ToolTipProperty, "Str_TT_EvtFilter");
            SetColumn(filterBox, 2);
            bar.Children.Add(filterBox);

            // E72C (Refresh): loads the current log/level selection again from scratch. No
            // polling timer here (see the file header) - this is the only way back to a fresh
            // read short of switching the log or level, which already reload on their own.
            var refreshBtn = new Button
            {
                Content = ((char)0xE72C).ToString(),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                Style = (Style)FindResourceStatic("SurfaceButton"),
            };
            refreshBtn.SetResourceReference(ToolTipProperty, "Str_TT_EvtRefresh");
            refreshBtn.Click += (_, _) => Reload();
            SetColumn(refreshBtn, 3);
            bar.Children.Add(refreshBtn);

            return bar;
        }

        private static void AddItem(ComboBox box, object tag, string resourceKey)
        {
            var item = new ComboBoxItem { Tag = tag };
            item.SetResourceReference(ContentControl.ContentProperty, resourceKey);
            box.Items.Add(item);
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

            var level    = LevelColumn();
            var log      = Col("Str_Col_EvtLog",      nameof(EventLogEntryInfo.LogName), 90);
            var time     = Col("Str_Col_EvtTime",     nameof(EventLogEntryInfo.TimeLabel), 150,
                                sortMember: nameof(EventLogEntryInfo.Time));
            var source   = Col("Str_Col_EvtSource",   nameof(EventLogEntryInfo.Source), 170);
            var id       = Col("Str_Col_EvtId",       nameof(EventLogEntryInfo.EventId), 70);
            var category = Col("Str_Col_EvtCategory", nameof(EventLogEntryInfo.TaskCategory), 130);

            var message = Col("Str_Col_EvtMessage", nameof(EventLogEntryInfo.Message), 420);
            // Event messages routinely run to several lines - the grid still just clips a long
            // one at the column edge, same as CommandLine/Path do on the Processes tab, but the
            // full text rides along as the cell's own tooltip so it is still reachable without a
            // separate details pane. "Copy message" / "Copy details" on the row's context menu
            // are the other way to get it out, for pasting somewhere that is not a tooltip.
            var messageStyle = new Style(typeof(TextBlock));
            messageStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(EventLogEntryInfo.Message))));
            message.ElementStyle = messageStyle;

            grid.Columns.Add(level);
            grid.Columns.Add(log);
            grid.Columns.Add(time);
            grid.Columns.Add(source);
            grid.Columns.Add(id);
            grid.Columns.Add(category);
            grid.Columns.Add(message);

            // Right-click any column header for a show/hide checklist, persisted per grid
            // (Services/ColumnVisibilityMenu.cs) - the same shared menu the Processes tab uses.
            // Task Category is hidden by default here (2026-08-02): "All logs" already
            // repeats the log-specific category vocabulary in Message, and Level/Log/Time/Source/
            // Event ID/Message cover what a first look at this tab actually needs.
            Services.ColumnVisibilityMenu.AttachTo(grid, "EventViewer",
                (level,    "Level",    "Str_Col_EvtLevel",    true),
                (log,      "Log",      "Str_Col_EvtLog",      true),
                (time,     "Time",     "Str_Col_EvtTime",     true),
                (source,   "Source",   "Str_Col_EvtSource",   true),
                (id,       "Id",       "Str_Col_EvtId",       true),
                (category, "Category", "Str_Col_EvtCategory", false),
                (message,  "Message",  "Str_Col_EvtMessage",  true));

            grid.ContextMenuOpening += Grid_ContextMenuOpening;
            grid.MouseDoubleClick += Grid_MouseDoubleClick;
            return grid;
        }

        private static DataGridTemplateColumn LevelColumn()
        {
            var ellipse = new FrameworkElementFactory(typeof(Ellipse));
            ellipse.SetValue(WidthProperty, 8.0);
            ellipse.SetValue(HeightProperty, 8.0);
            ellipse.SetValue(MarginProperty, new Thickness(0, 0, 6, 0));
            ellipse.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            ellipse.SetBinding(Shape.FillProperty,
                new Binding(nameof(EventLogEntryInfo.Level)) { Converter = LevelBrushConverter.Instance });

            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(nameof(EventLogEntryInfo.Level)));
            text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);

            var panel = new FrameworkElementFactory(typeof(StackPanel));
            panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            panel.AppendChild(ellipse);
            panel.AppendChild(text);

            var header = new TextBlock();
            header.SetResourceReference(TextBlock.TextProperty, "Str_Col_EvtLevel");

            return new DataGridTemplateColumn
            {
                Header         = header,
                Width          = new DataGridLength(120),
                SortMemberPath = nameof(EventLogEntryInfo.Level),
                CellTemplate   = new DataTemplate { VisualTree = panel },
            };
        }

        /// <summary>
        /// Level -> dot color. The three brushes are fixed, non-theme keys (Controls.xaml,
        /// beside DangerRed) rather than DynamicResource theme brushes - a converter runs once
        /// per binding value and never again on a later theme switch, so a brush that DID vary
        /// by theme would go stale the moment the theme changed after this ran.
        /// </summary>
        private sealed class LevelBrushConverter : IValueConverter
        {
            internal static readonly LevelBrushConverter Instance = new();

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                string level = value as string ?? string.Empty;
                string key = level switch
                {
                    "Critical" or "Error" => "DangerRed",
                    "Warning"             => "WarningAmber",
                    _                     => "InfoBlue",
                };
                return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
                => throw new NotSupportedException();
        }

        private static DataGridTextColumn Col(string headerKey, string bindingPath, double width,
                                              string? sortMember = null)
        {
            var header = new TextBlock();
            header.SetResourceReference(TextBlock.TextProperty, headerKey);
            return new DataGridTextColumn
            {
                Header         = header,
                Binding        = new Binding(bindingPath),
                Width          = new DataGridLength(width),
                SortMemberPath = sortMember ?? bindingPath,
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

        // Resolved through the application's merged dictionaries rather than this.FindResource -
        // at construction time the control has not been added to a visual tree yet (same remark
        // as ProcessListControl.FindResourceStatic).
        private static object FindResourceStatic(string key)
            => Application.Current.TryFindResource(key)
               ?? throw new InvalidOperationException($"Missing resource: {key}");

        // ═══════════════════════════════════════════════════════════
        //  FILTER (client-side, over whatever the current log/level load already produced)
        // ═══════════════════════════════════════════════════════════
        private bool FilterPredicate(object obj)
        {
            if (obj is not EventLogEntryInfo e) return false;
            string q = _filterBox.Text;
            if (q.Length == 0) return true;

            return e.Source.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || e.Message.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || e.TaskCategory.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || e.EventId.ToString(CultureInfo.InvariantCulture).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ═══════════════════════════════════════════════════════════
        //  LOAD
        // ═══════════════════════════════════════════════════════════
        /// <summary>Starts a fresh read of the currently picked log source and level, replacing
        /// whatever is on screen. Called on first show, on a log/level change, and from the
        /// refresh button.</summary>
        private void Reload()
        {
            CancelLoad();
            _items.Clear();

            // --demo has no real machine to read a log from (Shell/DemoMode.cs), so this never
            // touches EventLogReader in demo mode - a fixed fabricated set instead, same
            // "everything fixed" rule the rest of demo mode follows. The log/level pickers still
            // work; PopulateDemoEvents filters the same fake rows the way a real Reload would
            // have filtered real ones, so switching "System" -> "Security" in a capture still
            // shows something plausible rather than an empty grid.
            if (MainWindow.DemoMode)
            {
                PopulateDemoEvents(SelectedSource(), SelectedLevel());
                ShowStatus(string.Empty, error: false);
                return;
            }

            var cts = new CancellationTokenSource();
            _loadCts = cts;

            var source = SelectedSource();
            var level  = SelectedLevel();
            string[] logs = source == LogSource.All
                ? ["Application", "System", "Security"]
                : [LogNameFor(source)];

            ShowStatus(MainWindow.LocStatic("Str_Evt_Loading"), error: false, sticky: true);

            // LongRunning, same reasoning as ProcessListControl's WMI work: EventLogReader holds
            // a native handle, and a pooled ThreadPool thread being reused mid-read is exactly
            // the kind of thing that class of bug comes from.
            Task.Factory.StartNew(() => RunLoad(logs, level, cts.Token),
                cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void CancelLoad()
        {
            _loadCts?.Cancel();
            _loadCts = null;
        }

        // ═══════════════════════════════════════════════════════════
        //  DEMO DATA  -  --demo, see Reload() above. The same fabricated MSP workstation the rest
        //  of demo mode invents (Shell/DemoMode.cs, Services/DemoFileSystem.cs) - the agent/backup
        //  jobs the fake terminal session and file listings already reference show up here too, so
        //  a capture with two of these tabs open agrees with itself about what actually happened.
        // ═══════════════════════════════════════════════════════════
        private static readonly DateTime DemoNow = new(2026, 7, 3, 8, 12, 0, DateTimeKind.Utc);

        private void PopulateDemoEvents(LogSource source, LevelFilter level)
        {
            var all = new List<EventLogEntryInfo>
            {
                new("System", "Error", DemoNow.AddHours(-9), "Microsoft-Windows-WMI", 10,
                    "None", "Event filter with query \"SELECT * FROM __InstanceModificationEvent WITHIN 60 WHERE TargetInstance ISA 'Win32_Processor'\" could not be reactivated in namespace \"//./root/CIMV2\" because of error 0x80041003. Events cannot be delivered through this filter until the problem is corrected.",
                    "Classic", "WKS-DEMO01", "SYSTEM", "1220", "3164", "-", "184213", "Info", DemoEventXml("System", 10, "Microsoft-Windows-WMI")),
                new("System", "Warning", DemoNow.AddHours(-8).AddMinutes(-40), "Disk", 153,
                    "None", "The IO operation at logical block address 0x2a41f0 for Disk 0 (PDO name: \\Device\\00000047) was retried.",
                    "Classic", "WKS-DEMO01", "SYSTEM", "4", "8", "-", "184198", "Info", DemoEventXml("System", 153, "Disk")),
                new("System", "Information", DemoNow.AddHours(-8).AddMinutes(-1), "Service Control Manager", 7036,
                    "None", "The Background Intelligent Transfer Service service entered the running state.",
                    "Classic", "WKS-DEMO01", "SYSTEM", "812", "-", "-", "184190", "Info", DemoEventXml("System", 7036, "Service Control Manager")),

                // The unclean-shutdown pair, which is the one thing every level filter in the
                // picker should be able to demonstrate: Critical is the level the grid's dot turns
                // red for and nothing else in the set produces one.
                new("System", "Critical", DemoNow.AddHours(-14).AddMinutes(-3), "Microsoft-Windows-Kernel-Power", 41,
                    "(63)", "The system has rebooted without cleanly shutting down first. This error could be caused if the system stopped responding, crashed, or lost power unexpectedly.",
                    "Classic", "WKS-DEMO01", "SYSTEM", "4", "8", "-", "184102", "Info", DemoEventXml("System", 41, "Microsoft-Windows-Kernel-Power")),
                new("System", "Information", DemoNow.AddHours(-13).AddMinutes(-58), "Microsoft-Windows-Kernel-General", 12,
                    "None", "The operating system started at system time 2026-07-02T18:14:31.500000000Z.",
                    "Classic", "WKS-DEMO01", "SYSTEM", "4", "8", "-", "184104", "Info", DemoEventXml("System", 12, "Microsoft-Windows-Kernel-General")),

                new("System", "Error", DemoNow.AddHours(-13).AddMinutes(-51), "Service Control Manager", 7000,
                    "None", "The Veeam Agent for Microsoft Windows service failed to start due to the following error:\r\nThe service did not respond to the start or control request in a timely fashion.",
                    "Classic", "WKS-DEMO01", "SYSTEM", "812", "-", "-", "184117", "Info", DemoEventXml("System", 7000, "Service Control Manager")),
                new("System", "Error", DemoNow.AddHours(-11).AddMinutes(-27), "Microsoft-Windows-DistributedCOM", 10016,
                    "None", "The application-specific permission settings do not grant Local Activation permission for the COM Server application with CLSID {2593f8b9-4eaf-457c-b68a-50f6b8ea6b54} to the user WKS-DEMO01\\Demo. This security permission can be modified using the Component Services administrative tool.",
                    "Classic", "WKS-DEMO01", "Demo", "1044", "2160", "-", "184141", "Info", DemoEventXml("System", 10016, "Microsoft-Windows-DistributedCOM")),

                new("System", "Warning", DemoNow.AddHours(-10).AddMinutes(-44), "Microsoft-Windows-DNS-Client", 1014,
                    "(1014)", "Name resolution for the name fairview-fs01.fairview.local timed out after none of the configured DNS servers responded.",
                    "Classic", "WKS-DEMO01", "SYSTEM", "1188", "-", "-", "184155", "Info", DemoEventXml("System", 1014, "Microsoft-Windows-DNS-Client")),
                new("System", "Warning", DemoNow.AddHours(-9).AddMinutes(-52), "Microsoft-Windows-Time-Service", 134,
                    "None", "NtpClient was unable to set a domain peer to use as a time source because of DNS resolution error on 'fairview-dc01.fairview.local'. NtpClient will try again in 15 minutes.",
                    "Classic", "WKS-DEMO01", "LOCAL SERVICE", "1188", "-", "-", "184161", "Info", DemoEventXml("System", 134, "Microsoft-Windows-Time-Service")),

                // Group Policy is the one classic provider that genuinely writes at Verbose, so
                // this is what stops the level column from only ever showing four of its five
                // possible values.
                new("System", "Verbose", DemoNow.AddHours(-13).AddMinutes(-49), "Microsoft-Windows-GroupPolicy", 5312,
                    "None", "List of applicable Group Policy objects:\r\n\r\n\tLocal Group Policy\r\n\tWorkstation Baseline\r\n\tBitLocker Enforcement",
                    "Classic", "WKS-DEMO01", "SYSTEM", "1076", "1080", "{c9d1a2f0-3b6e-4d51-8a77-2e4f9b0c1d63}", "184121", "Info", DemoEventXml("System", 5312, "Microsoft-Windows-GroupPolicy")),

                new("System", "Information", DemoNow.AddHours(-5).AddMinutes(-8), "Microsoft-Windows-WindowsUpdateClient", 19,
                    "Windows Update Agent", "Installation Successful: Windows successfully installed the following update: 2026-06 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5041299).",
                    "Classic", "WKS-DEMO01", "SYSTEM", "1188", "-", "-", "184226", "Info", DemoEventXml("System", 19, "Microsoft-Windows-WindowsUpdateClient")),
                new("System", "Information", DemoNow.AddMinutes(-34), "Microsoft-Windows-Kernel-General", 16,
                    "None", "The access history in hive \\??\\C:\\Users\\Demo\\ntuser.dat was cleared updating 128 keys and creating 4 modified pages.",
                    "Classic", "WKS-DEMO01", "SYSTEM", "4", "8", "-", "184270", "Info", DemoEventXml("System", 16, "Microsoft-Windows-Kernel-General")),
                new("Application", "Error", DemoNow.AddHours(-2).AddMinutes(-6), "Application Error", 1000,
                    "(100)", "Faulting application name: SentinelServiceHost.exe, version 23.4.6.1, time stamp 0x6620a1b0\r\nFaulting module name: ntdll.dll, version 10.0.22621.3155, time stamp 0x1c93a2e4\r\nException code: 0xc0000005\r\nFault offset: 0x0000000000058a10\r\nFaulting process id: 0x7c4\r\nFaulting application start time: 0x1dbb2c4e0a1f2b3\r\nReport Id: 6b2e9e13-1c4a-4a2e-9e1e-7f0a2d3c4b5e",
                    "Classic", "WKS-DEMO01", "-", "1988", "5544", "-", "184240", "Info", DemoEventXml("Application", 1000, "Application Error")),
                new("Application", "Warning", DemoNow.AddHours(-1).AddMinutes(-30), "MsiInstaller", 1015,
                    "None", "Windows Installer reconfigured the product. Product Name: Microsoft Edge WebView2 Runtime. Product Version: 126.0.2592.68. Reconfiguration success or error status: 0.",
                    "Classic", "WKS-DEMO01", "SYSTEM", "2988", "-", "-", "184255", "Info", DemoEventXml("Application", 1015, "MsiInstaller")),
                new("Application", "Information", DemoNow.AddMinutes(-52), "ESENT", 326,
                    "General", "svchost (812,S,0) SRUJet: The database engine started a new instance (0).",
                    "Classic", "WKS-DEMO01", "SYSTEM", "812", "-", "-", "184261", "Info", DemoEventXml("Application", 326, "ESENT")),
                new("Application", "Information", DemoNow.AddMinutes(-18), "KillerShell", 1,
                    "None", "KillerShell 1.1.0 started for user Demo.",
                    "Classic", "WKS-DEMO01", "Demo", "5116", "-", "-", "184268", "Info", DemoEventXml("Application", 1, "KillerShell")),

                new("Application", "Error", DemoNow.AddHours(-12).AddMinutes(-22), ".NET Runtime", 1026,
                    "None", "Application: LegacyAssetSync.exe\r\nCoreCLR Version: 4.8.9282.0\r\nDescription: The process was terminated due to an unhandled exception.\r\nException Info: System.Net.WebException: The remote server returned an error: (401) Unauthorized.\r\n   at LegacyAssetSync.Api.Fetch(String tenant)",
                    "Classic", "WKS-DEMO01", "Demo", "3372", "4108", "-", "184133", "Info", DemoEventXml("Application", 1026, ".NET Runtime")),
                new("Application", "Error", DemoNow.AddHours(-7).AddMinutes(-15), "MsiInstaller", 11708,
                    "None", "Product: Legacy Asset Sync - Installation failed. Installation success or error status: 1603.",
                    "Classic", "WKS-DEMO01", "Demo", "2988", "-", "-", "184186", "Info", DemoEventXml("Application", 11708, "MsiInstaller")),
                new("Application", "Error", DemoNow.AddHours(-4).AddMinutes(-41), "Application Hang", 1002,
                    "(101)", "The program OUTLOOK.EXE version 16.0.17928.20114 stopped interacting with Windows and was closed. A problem caused this program to stop interacting with Windows.\r\nProcess ID: 1c24\r\nStart Time: 01dbb2c40f1e9a7",
                    "Classic", "WKS-DEMO01", "Demo", "7204", "7208", "-", "184219", "Info", DemoEventXml("Application", 1002, "Application Hang")),

                new("Application", "Warning", DemoNow.AddHours(-4).AddMinutes(-40), "Windows Error Reporting", 1001,
                    "None", "Fault bucket 1274118253458122611, type 5\r\nEvent Name: AppHangB1\r\nResponse: Not available\r\nCab Id: 0\r\n\r\nProblem signature:\r\nP1: OUTLOOK.EXE\r\nP2: 16.0.17928.20114\r\nP3: 6620a1b0",
                    "Classic", "WKS-DEMO01", "Demo", "7208", "-", "-", "184220", "Info", DemoEventXml("Application", 1001, "Windows Error Reporting")),
                new("Application", "Warning", DemoNow.AddHours(-3).AddMinutes(-12), "Microsoft-Windows-Search", 3036,
                    "(3)", "The content source <csc://{S-1-5-21-111111111-222222222-333333333-1001}/> cannot be accessed. Context: Application, SystemIndex Catalog. Details: The device is not ready.",
                    "Classic", "WKS-DEMO01", "SYSTEM", "3856", "-", "-", "184231", "Info", DemoEventXml("Application", 3036, "Microsoft-Windows-Search")),

                new("Application", "Information", DemoNow.AddHours(-13).AddMinutes(-44), "Microsoft-Windows-Security-SPP", 1003,
                    "None", "The Software Protection service has completed licensing status check.\r\nApplication Id=55c92734-d682-4d71-983e-d6ec3f16059f\r\nLicensing Status=Licensed",
                    "Classic", "WKS-DEMO01", "NETWORK SERVICE", "3120", "-", "-", "184126", "Info", DemoEventXml("Application", 1003, "Microsoft-Windows-Security-SPP")),
                new("Application", "Information", DemoNow.AddHours(-9).AddMinutes(-11), "Veeam Agent", 190,
                    "None", "Backup job 'Workstation daily' finished with Success. Processed 96.4 GB in 41 minutes, transferred 3.2 GB to D:\\Backups.",
                    "Classic", "WKS-DEMO01", "SYSTEM", "4472", "-", "-", "184168", "Info", DemoEventXml("Application", 190, "Veeam Agent")),
                new("Application", "Information", DemoNow.AddHours(-2).AddMinutes(-58), "SecurityCenter", 15,
                    "None", "Updated Antivirus status successfully to SECURITY_PRODUCT_STATE_ON.",
                    "Classic", "WKS-DEMO01", "SYSTEM", "3120", "-", "-", "184236", "Info", DemoEventXml("Application", 15, "SecurityCenter")),
                new("Application", "Information", DemoNow.AddMinutes(-6), "KillerShell", 2,
                    "None", "Export written: C:\\Users\\Demo\\Documents\\Exports\\killershell-report.html (780 KB, 2,418 rows).",
                    "Classic", "WKS-DEMO01", "Demo", "5116", "-", "-", "184272", "Info", DemoEventXml("Application", 2, "KillerShell")),
                new("Security", "Information", DemoNow.AddHours(-13).AddMinutes(-2), "Microsoft-Windows-Security-Auditing", 4624,
                    "Logon", "An account was successfully logged on.\r\n\r\nSubject:\r\n\tSecurity ID:\t\tS-1-5-18\r\n\tAccount Name:\t\tWKS-DEMO01$\r\n\r\nLogon Type:\t\t\t2\r\n\r\nNew Logon:\r\n\tSecurity ID:\t\tS-1-5-21-111111111-222222222-333333333-1001\r\n\tAccount Name:\t\tDemo\r\n\tAccount Domain:\t\tWKS-DEMO01",
                    "Audit Success", "WKS-DEMO01", "S-1-5-21-111111111-222222222-333333333-1001", "824", "1288", "{5b1e2a0c-1f4d-4b3e-9c2a-8e7f6d5c4b3a}", "51204", "Info", DemoEventXml("Security", 4624, "Microsoft-Windows-Security-Auditing")),
                new("Security", "Information", DemoNow.AddHours(-13).AddMinutes(-2), "Microsoft-Windows-Security-Auditing", 4672,
                    "Special Logon", "Special privileges assigned to new logon.\r\n\r\nSubject:\r\n\tSecurity ID:\t\tS-1-5-21-111111111-222222222-333333333-1001\r\n\tAccount Name:\t\tDemo\r\n\tAccount Domain:\t\tWKS-DEMO01",
                    "Audit Success", "WKS-DEMO01", "S-1-5-21-111111111-222222222-333333333-1001", "824", "1288", "-", "51205", "Info", DemoEventXml("Security", 4672, "Microsoft-Windows-Security-Auditing")),
                new("Security", "Warning", DemoNow.AddHours(-6).AddMinutes(-11), "Microsoft-Windows-Security-Auditing", 4625,
                    "Logon", "An account failed to log on.\r\n\r\nSubject:\r\n\tSecurity ID:\t\tS-1-0-0\r\n\tAccount Name:\t\t-\r\n\r\nLogon Type:\t\t\t3\r\n\r\nAccount For Which Logon Failed:\r\n\tAccount Name:\t\tadministrator\r\n\r\nFailure Reason:\t\tUnknown user name or bad password.",
                    "Audit Failure", "WKS-DEMO01", "S-1-0-0", "-", "-", "-", "51260", "Info", DemoEventXml("Security", 4625, "Microsoft-Windows-Security-Auditing")),

                // A second failed logon from the same source a few seconds later, because a lone
                // 4625 reads as a typo and a run of them reads as the thing an admin opens this
                // tab to find. Same workstation name in both, deliberately.
                new("Security", "Warning", DemoNow.AddHours(-6).AddMinutes(-10), "Microsoft-Windows-Security-Auditing", 4625,
                    "Logon", "An account failed to log on.\r\n\r\nSubject:\r\n\tSecurity ID:\t\tS-1-0-0\r\n\tAccount Name:\t\t-\r\n\r\nLogon Type:\t\t\t3\r\n\r\nAccount For Which Logon Failed:\r\n\tAccount Name:\t\tadmin\r\n\r\nFailure Reason:\t\tUnknown user name or bad password.\r\n\r\nNetwork Information:\r\n\tWorkstation Name:\tWKS-0421\r\n\tSource Network Address:\t10.20.4.121",
                    "Audit Failure", "WKS-DEMO01", "S-1-0-0", "-", "-", "-", "51261", "Info", DemoEventXml("Security", 4625, "Microsoft-Windows-Security-Auditing")),

                new("Security", "Information", DemoNow.AddHours(-13).AddMinutes(-2), "Microsoft-Windows-Security-Auditing", 4776,
                    "Credential Validation", "The computer attempted to validate the credentials for an account.\r\n\r\nAuthentication Package:\tMICROSOFT_AUTHENTICATION_PACKAGE_V1_0\r\nLogon Account:\tDemo\r\nSource Workstation:\tWKS-DEMO01\r\nError Code:\t0x0",
                    "Audit Success", "WKS-DEMO01", "SYSTEM", "824", "1288", "-", "51203", "Info", DemoEventXml("Security", 4776, "Microsoft-Windows-Security-Auditing")),
                new("Security", "Information", DemoNow.AddHours(-12).AddMinutes(-51), "Microsoft-Windows-Security-Auditing", 4688,
                    "Process Creation", "A new process has been created.\r\n\r\nCreator Subject:\r\n\tSecurity ID:\t\tS-1-5-21-111111111-222222222-333333333-1001\r\n\tAccount Name:\t\tDemo\r\n\r\nProcess Information:\r\n\tNew Process ID:\t\t0x13f8\r\n\tNew Process Name:\tC:\\Program Files\\PowerShell\\7\\pwsh.exe\r\n\tToken Elevation Type:\tTokenElevationTypeFull (2)",
                    "Audit Success", "WKS-DEMO01", "S-1-5-21-111111111-222222222-333333333-1001", "824", "1288", "-", "51218", "Info", DemoEventXml("Security", 4688, "Microsoft-Windows-Security-Auditing")),
                new("Security", "Information", DemoNow.AddHours(-11).AddMinutes(-6), "Microsoft-Windows-Security-Auditing", 4720,
                    "User Account Management", "A user account was created.\r\n\r\nSubject:\r\n\tAccount Name:\t\tDemo\r\n\r\nNew Account:\r\n\tSecurity ID:\t\tS-1-5-21-111111111-222222222-333333333-1042\r\n\tAccount Name:\t\tsvc-backup\r\n\tAccount Domain:\t\tWKS-DEMO01",
                    "Audit Success", "WKS-DEMO01", "S-1-5-21-111111111-222222222-333333333-1001", "824", "1288", "-", "51229", "Info", DemoEventXml("Security", 4720, "Microsoft-Windows-Security-Auditing")),
                new("Security", "Information", DemoNow.AddHours(-11).AddMinutes(-5), "Microsoft-Windows-Security-Auditing", 4732,
                    "Security Group Management", "A member was added to a security-enabled local group.\r\n\r\nSubject:\r\n\tAccount Name:\t\tDemo\r\n\r\nMember:\r\n\tSecurity ID:\t\tS-1-5-21-111111111-222222222-333333333-1042\r\n\r\nGroup:\r\n\tGroup Name:\t\tBackup Operators",
                    "Audit Success", "WKS-DEMO01", "S-1-5-21-111111111-222222222-333333333-1001", "824", "1288", "-", "51230", "Info", DemoEventXml("Security", 4732, "Microsoft-Windows-Security-Auditing")),
                new("Security", "Information", DemoNow.AddHours(-9).AddMinutes(-40), "Microsoft-Windows-Security-Auditing", 1102,
                    "Log clear", "The audit log was cleared.\r\n\r\nSubject:\r\n\tSecurity ID:\tS-1-5-21-111111111-222222222-333333333-1001\r\n\tAccount Name:\tDemo\r\n\tDomain Name:\tWKS-DEMO01\r\n\tLogon ID:\t0x1e4a2",
                    "Audit Success", "WKS-DEMO01", "S-1-5-21-111111111-222222222-333333333-1001", "824", "1288", "-", "51240", "Info", DemoEventXml("Security", 1102, "Microsoft-Windows-Security-Auditing")),
                new("Security", "Information", DemoNow.AddHours(-1).AddMinutes(-4), "Microsoft-Windows-Security-Auditing", 5379,
                    "User Account Management", "Credential Manager credentials were read.\r\n\r\nSubject:\r\n\tAccount Name:\t\tDemo\r\n\r\nRead Operation:\t\tEnumerate Credentials\r\n\r\nThis event occurs when a user performs a read operation on stored credentials.",
                    "Audit Success", "WKS-DEMO01", "S-1-5-21-111111111-222222222-333333333-1001", "824", "1288", "-", "51288", "Info", DemoEventXml("Security", 5379, "Microsoft-Windows-Security-Auditing")),
                new("Security", "Information", DemoNow.AddMinutes(-41), "Microsoft-Windows-Security-Auditing", 4634,
                    "Logoff", "An account was logged off.\r\n\r\nSubject:\r\n\tSecurity ID:\t\tS-1-5-21-111111111-222222222-333333333-1042\r\n\tAccount Name:\t\tsvc-backup\r\n\tAccount Domain:\t\tWKS-DEMO01\r\n\tLogon ID:\t\t0x2c9f1\r\n\r\nLogon Type:\t\t\t5",
                    "Audit Success", "WKS-DEMO01", "S-1-5-21-111111111-222222222-333333333-1042", "824", "1288", "-", "51301", "Info", DemoEventXml("Security", 4634, "Microsoft-Windows-Security-Auditing")),
            };

            IEnumerable<EventLogEntryInfo> filtered = source == LogSource.All
                ? all
                : all.Where(e => string.Equals(e.LogName, LogNameFor(source), StringComparison.OrdinalIgnoreCase));

            if (level != LevelFilter.All)
                filtered = filtered.Where(e => string.Equals(e.Level, level.ToString(), StringComparison.OrdinalIgnoreCase));

            foreach (var e in filtered) _items.Add(e);
        }

        /// <summary>A small, well-formed EventRecord-shaped XML body for the raw-XML view
        /// (Controls/EventDetailsDialog.xaml) - real events carry far more, but this is enough to
        /// show the view is not just switching to a blank tab.</summary>
        private static string DemoEventXml(string logName, int eventId, string provider)
            => "<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\">"
             + "<System><Provider Name=\"" + provider + "\"/><EventID>" + eventId + "</EventID>"
             + "<Channel>" + logName + "</Channel><Computer>WKS-DEMO01</Computer></System>"
             + "<EventData/></Event>";

        private LogSource SelectedSource()
            => _logBox.SelectedItem is ComboBoxItem { Tag: LogSource s } ? s : LogSource.Application;

        private LevelFilter SelectedLevel()
            => _levelBox.SelectedItem is ComboBoxItem { Tag: LevelFilter l } ? l : LevelFilter.All;

        private static string LogNameFor(LogSource s) => s switch
        {
            LogSource.System   => "System",
            LogSource.Security => "Security",
            _                  => "Application",
        };

        /// <summary>
        /// The XPath handed to EventLogQuery so the LOG itself only returns matching records,
        /// rather than this reading everything and throwing rows away client-side. Level 0
        /// (LogAlways) is folded into Information - most entries written through the classic
        /// EventLog.WriteEntry API with no explicit level land there in the modern schema.
        /// </summary>
        private static string XPathFor(LevelFilter level) => level switch
        {
            LevelFilter.Error       => "*[System[(Level=1 or Level=2)]]",
            LevelFilter.Warning     => "*[System[Level=3]]",
            LevelFilter.Information => "*[System[(Level=4 or Level=0)]]",
            _                       => "*",
        };

        /// <summary>
        /// Runs entirely off the UI thread: opens each log in turn, reads newest-first up to
        /// PerLogCap records, and applies them to the grid in small batches as they come in
        /// rather than waiting for the whole read to finish - the fast/slow split
        /// ProcessListControl's Refresh/BuildSamples pair uses, adapted to a one-shot load
        /// instead of a repeating tick.
        /// </summary>
        private void RunLoad(string[] logs, LevelFilter level, CancellationToken token)
        {
            int totalApplied = 0;
            bool firstBatchDone = false;
            string? errorText = null;
            string xpath = XPathFor(level);

            foreach (string logName in logs)
            {
                if (token.IsCancellationRequested) return;

                var batch = new List<EventLogEntryInfo>(ApplyBatchSize);
                try
                {
                    var query = new EventLogQuery(logName, PathType.LogName, xpath)
                    {
                        ReverseDirection = true,   // newest first - what a viewer wants to see
                    };
                    using var reader = new EventLogReader(query);

                    int readForThisLog = 0;
                    EventRecord? rec;
                    while (!token.IsCancellationRequested && readForThisLog < PerLogCap
                           && (rec = reader.ReadEvent()) != null)
                    {
                        using (rec) batch.Add(ToEntry(logName, rec));
                        readForThisLog++;

                        if (batch.Count >= ApplyBatchSize)
                        {
                            ApplyBatch(batch, ref totalApplied, ref firstBatchDone);
                            batch = new List<EventLogEntryInfo>(ApplyBatchSize);
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Security without elevation, or any log this process token cannot see into.
                    // Skip it and keep going - on "All" the other two logs still load, and on a
                    // single-log pick the grid just comes back empty with the reason on the
                    // status line instead of the tab failing outright.
                    errorText ??= MainWindow.LocStatic("Str_Evt_AccessDenied");
                }
                catch (EventLogNotFoundException)
                {
                    // A log that does not exist on this machine - same treatment, skip and
                    // keep going rather than aborting the whole load over one missing log.
                }
                catch (Exception ex)
                {
                    errorText = string.Format(MainWindow.LocStatic("Str_Evt_RefreshFailed"), ex.Message);
                }

                if (batch.Count > 0) ApplyBatch(batch, ref totalApplied, ref firstBatchDone);
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (errorText != null) ShowStatus(errorText, error: true);
                else ShowStatus(string.Empty, error: false);
            }));
        }

        private void ApplyBatch(List<EventLogEntryInfo> batch, ref int totalApplied, ref bool firstBatchDone)
        {
            var captured = batch;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var e in captured) _items.Add(e);
            }));

            totalApplied += captured.Count;
            if (!firstBatchDone && totalApplied >= FastFirstBatch)
            {
                firstBatchDone = true;
                Dispatcher.BeginInvoke(new Action(() => ShowStatus(string.Empty, error: false)));
            }
        }

        /// <summary>Everything RunLoad worked out about one record, read off the UI thread.
        /// Every field is read defensively - LevelDisplayName/TaskDisplayName/FormatDescription
        /// all throw EventLogException for a provider Windows has no manifest for (common on
        /// forwarded and legacy-source events), and a single bad record must not blank out the
        /// rest of the batch.</summary>
        private static EventLogEntryInfo ToEntry(string logName, EventRecord rec)
        {
            string level = LevelLabel(SafeLevel(rec));
            DateTime time = SafeTime(rec);
            string source = SafeString(() => rec.ProviderName);
            int id = SafeId(rec);
            string category = SafeString(() => rec.TaskDisplayName);
            string message = SafeMessage(rec);

            // Everything past here only ever surfaces in the details dialog (double-click a
            // row) - never the grid - so a provider that throws on one of these just shows "-"
            // there rather than losing the whole row the way an unguarded read would.
            string keywords   = SafeKeywords(rec);
            string computer   = SafeString(() => rec.MachineName);
            string user       = SafeUser(rec);
            string processId  = SafeUInt(() => rec.ProcessId);
            string threadId   = SafeUInt(() => rec.ThreadId);
            string activityId = SafeGuid(() => rec.ActivityId);
            string recordId   = SafeLong(() => rec.RecordId);
            string opcode     = SafeString(() => rec.OpcodeDisplayName);
            string rawXml     = SafeXml(rec);

            return new EventLogEntryInfo(logName, level, time, source, id, category, message,
                keywords, computer, user, processId, threadId, activityId, recordId, opcode, rawXml);
        }

        private static byte? SafeLevel(EventRecord rec) { try { return rec.Level; } catch { return null; } }
        private static DateTime SafeTime(EventRecord rec) { try { return rec.TimeCreated ?? DateTime.MinValue; } catch { return DateTime.MinValue; } }
        private static int SafeId(EventRecord rec) { try { return rec.Id; } catch { return 0; } }

        private static string SafeString(Func<string?> get)
        {
            try { return get() ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string SafeMessage(EventRecord rec)
        {
            try { return rec.FormatDescription() ?? MainWindow.LocStatic("Str_Evt_NoMessage"); }
            catch { return MainWindow.LocStatic("Str_Evt_NoMessage"); }
        }

        /// <summary>"Audit Success" / "Audit Failure" and friends. KeywordsDisplayNames throws
        /// for a provider Windows has no manifest for, same reason LevelDisplayName does.</summary>
        private static string SafeKeywords(EventRecord rec)
        {
            try
            {
                var names = rec.KeywordsDisplayNames;
                return names == null ? string.Empty : string.Join(", ", names);
            }
            catch { return string.Empty; }
        }

        /// <summary>The account name when the SID resolves, otherwise the raw SID, otherwise
        /// "-" - a deleted account or a foreign domain SID is common enough on a real machine's
        /// Security log that "could not translate" must not blank out the whole field.</summary>
        private static string SafeUser(EventRecord rec)
        {
            try
            {
                var sid = rec.UserId;
                if (sid == null) return "-";
                try { return sid.Translate(typeof(System.Security.Principal.NTAccount)).ToString(); }
                catch { return sid.Value; }
            }
            catch { return "-"; }
        }

        private static string SafeUInt(Func<int?> get)
        {
            try { var v = get(); return v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "-"; }
            catch { return "-"; }
        }

        private static string SafeLong(Func<long?> get)
        {
            try { var v = get(); return v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "-"; }
            catch { return "-"; }
        }

        private static string SafeGuid(Func<Guid?> get)
        {
            try { var v = get(); return v.HasValue ? v.Value.ToString() : "-"; }
            catch { return "-"; }
        }

        private static string SafeXml(EventRecord rec)
        {
            try { return rec.ToXml() ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string LevelLabel(byte? level) => level switch
        {
            1 => "Critical",
            2 => "Error",
            3 => "Warning",
            4 => "Information",
            5 => "Verbose",
            _ => "Information",   // 0 (LogAlways) - most classic EventLog.WriteEntry calls land here
        };

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
        //  ACTIONS  -  right-click on a row. Read-only tab: nothing here is destructive, so
        //  there is no themed confirm dialog to show - just clipboard copies.
        // ═══════════════════════════════════════════════════════════
        /// <summary>Double-click a row: the full-detail card (Controls/EventDetailsDialog.xaml),
        /// styled like the app's About card - this is where the grid's own truncated Message
        /// column, and everything that never made it into the grid at all, actually lives. Hands
        /// the dialog the FULL ordered list straight off the view (its current sort/filter, not a
        /// stale unsorted copy of _items) plus the clicked row's index into it, so the dialog's
        /// own Previous/Next buttons page through exactly what the grid was showing.</summary>
        private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_grid.SelectedItem is not EventLogEntryInfo entry) return;

            var ordered = _view.Cast<EventLogEntryInfo>().ToList();
            int index = ordered.IndexOf(entry);
            if (index < 0) index = 0;

            var dlg = new EventDetailsDialog(ordered, index) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
        }

        private void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (_grid.SelectedItem is not EventLogEntryInfo entry) { e.Handled = true; return; }

            var menu = new ContextMenu { PlacementTarget = _grid };

            MenuItem Item(string headerKey, string glyph, RoutedEventHandler click)
            {
                var item = new MenuItem();
                item.SetResourceReference(HeaderedItemsControl.HeaderProperty, headerKey);
                var icon = new TextBlock { Text = glyph };
                icon.SetResourceReference(FrameworkElement.StyleProperty, "MenuGlyph");
                var iconBox = new Viewbox { Width = 14, Height = 14, Stretch = Stretch.Uniform, Child = icon };
                item.Icon = iconBox;
                item.Click += click;
                menu.Items.Add(item);
                return item;
            }

            // E8C8: MDL2's Copy, two stacked documents - the glyph every copy action in the app
            // uses (the folder tree's Copy full path, the editor, the terminal, the Registry
            // Editor, the Storage Analyzer). It was E71B, which is LINK - a chain.
            Item("Str_Menu_EvtCopyMessage", ((char)0xE8C8).ToString(),
                 (_, _) => CopyToClipboard(entry.Message, "Str_Evt_MessageCopied"));
            Item("Str_Menu_EvtCopyDetails", ((char)0xE8C8).ToString(),
                 (_, _) => CopyToClipboard(FormatDetails(entry), "Str_Evt_DetailsCopied"));

            menu.IsOpen = true;
            e.Handled = true;
        }

        /// <summary>A formatted block meant for pasting into a ticket - KillerShell is aimed at
        /// MSP field techs, and this is exactly the shape they would want to hand off. Internal
        /// (not private) so the details dialog's own "Copy details" button (Controls/
        /// EventDetailsDialog.xaml.cs) can reuse the exact same text the row's right-click menu
        /// already produces, rather than a second copy that could drift from it.</summary>
        internal static string FormatDetails(EventLogEntryInfo e)
        {
            string nl = Environment.NewLine;
            return MainWindow.LocStatic("Str_Evt_DetailsLevel")    + ": " + e.Level + nl
                 + MainWindow.LocStatic("Str_Evt_DetailsTime")     + ": " + e.TimeLabel + nl
                 + MainWindow.LocStatic("Str_Evt_DetailsSource")   + ": " + e.Source + nl
                 + MainWindow.LocStatic("Str_Evt_DetailsId")       + ": "
                 + e.EventId.ToString(CultureInfo.InvariantCulture) + nl
                 + MainWindow.LocStatic("Str_Evt_DetailsCategory") + ": " + e.TaskCategory + nl
                 + MainWindow.LocStatic("Str_Evt_Keywords")   + ": " + e.Keywords + nl
                 + MainWindow.LocStatic("Str_Evt_Computer")   + ": " + e.Computer + nl
                 + MainWindow.LocStatic("Str_Evt_User")       + ": " + e.User + nl
                 + MainWindow.LocStatic("Str_Evt_ProcessId")  + ": " + e.ProcessId + nl
                 + MainWindow.LocStatic("Str_Evt_ThreadId")   + ": " + e.ThreadId + nl
                 + MainWindow.LocStatic("Str_Evt_ActivityId") + ": " + e.ActivityId + nl
                 + MainWindow.LocStatic("Str_Evt_RecordId")   + ": " + e.RecordId + nl
                 + MainWindow.LocStatic("Str_Evt_Opcode")     + ": " + e.Opcode + nl
                 + MainWindow.LocStatic("Str_Evt_DetailsMessage")  + ":" + nl + e.Message;
        }

        private void CopyToClipboard(string text, string statusKey)
        {
            try
            {
                Clipboard.SetText(string.IsNullOrEmpty(text) ? " " : text);
                ShowStatus(MainWindow.LocStatic(statusKey), error: false);
            }
            catch (Exception ex)
            {
                ShowStatus(string.Format(MainWindow.LocStatic("Str_Evt_CopyFailed"), ex.Message), error: true);
            }
        }
    }
}
