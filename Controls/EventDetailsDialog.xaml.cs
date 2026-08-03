using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

using KillerShell.Editing;
using KillerShell.Models;
using KillerShell.Shell;

namespace KillerShell
{
    // Double-click a row in the Event Viewer grid (Shell/EventViewerControl.cs
    // Grid_MouseDoubleClick) to get here: every field the grid's own columns have no room for,
    // plus the record's raw XML behind a toggle. Styled like the app's About card - grain-wrapped
    // surface, PaneBrush info panel, same field-label shape (DimTextBrush Consolas 10 caption over
    // a TextBrush value) - but a real Window rather than an overlay Grid, because
    // EventViewerControl is a stand-alone control with no MainWindow overlay to reach. Same reason
    // ProcessListControl's KillWithConfirm opens ConfirmDialog as its own Window instead of an
    // overlay.
    public partial class EventDetailsDialog : Window
    {
        // The full ordered list the grid was showing (its CURRENT sort/filter, not a stale
        // unsorted copy - EventViewerControl.Grid_MouseDoubleClick reads it straight off the
        // ICollectionView) plus a mutable index into it, so Previous/Next can step through the
        // exact same order without the grid being touched again.
        private readonly IList<EventLogEntryInfo> _entries;
        private int _index;
        private bool _showingXml;

        /// <summary>
        /// The raw-XML view itself, built once in the constructor and hosted inside XmlHost
        /// (EventDetailsDialog.xaml) rather than declared in markup - same reason FieldsPanel's
        /// rows are built in code (BuildFields below): there is nothing here a XAML declaration
        /// would buy over a few lines next to the theming it needs to match.
        /// </summary>
        private readonly TextEditor _xmlEditor;

        private EventLogEntryInfo Current => _entries[_index];

        public EventDetailsDialog(IList<EventLogEntryInfo> entries, int startIndex)
        {
            InitializeComponent();
            _entries = entries;
            _index = startIndex >= 0 && startIndex < entries.Count ? startIndex : 0;

            _xmlEditor = BuildXmlEditor();
            XmlHost.Child = _xmlEditor;

            Loaded += (_, _) => Anim.FadeIn(RootBorder);
            SourceInitialized += (_, _) =>
            {
                ApplyRoundedCorners();
                MainWindow.ApplyThemeBorder(this);
                DialogScreenClamp.Apply(this);
            };

            LoadEntry();
        }

        /// <summary>
        /// The AvalonEdit control behind the raw-XML view, themed the same way
        /// EditorControl.cs (Editing/) themes a document tab - same brush keys, same lift of the
        /// shipped .xshd colors clear of the background (EditorHighlighting.MakeReadable) - so
        /// this reads as the same editor surface the rest of the app already uses, not a second
        /// one invented for this dialog. It needs none of EditorControl's file-on-disk plumbing
        /// (no path, no dirty state, no save), so it is a bare TextEditor rather than a subclass.
        /// </summary>
        private static TextEditor BuildXmlEditor()
        {
            var editor = new TextEditor
            {
                IsReadOnly = true,
                // Wrapped, not NoWrap - the raw XML is pretty-printed with indentation
                // (Models/EventLogEntryInfo.cs RawXmlFormatted) but individual lines, attributes
                // especially, still routinely run wider than the dialog. Preserves the wrap fix
                // the plain TextBox this replaces already had.
                WordWrap = true,
                ShowLineNumbers = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Padding = new Thickness(14, 12, 14, 12),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };

            editor.Options.EnableHyperlinks = false;
            editor.Options.EnableEmailHyperlinks = false;

            // AvalonEdit's built-in XML definition, the same one EditorControl.cs resolves for a
            // .xml file (HighlightingManager.Instance.GetDefinitionByExtension) - this vendored
            // copy ships XML-Mode.xshd as an embedded resource and registers it for ".xml" among
            // other extensions (third_party/AvalonEdit/Highlighting/Resources/Resources.cs), so
            // there is nothing to add or fall back to.
            var highlighting = HighlightingManager.Instance.GetDefinitionByExtension(".xml");

            // Transparent background, not a solid one - XmlHost (the Border this editor sits
            // inside) already paints PaneBrush, and the editor is meant to read as text sitting
            // on that panel rather than a second panel of its own.
            Color bg = Res("PaneBrush", Color.FromRgb(0x2A, 0x2A, 0x2A));
            Color fg = Res("TextBrush", Color.FromRgb(0xE0, 0xE0, 0xE0));
            Color dim = Res("DimTextBrush", Color.FromRgb(0x80, 0x80, 0x80));
            Color accent = Res("PrimaryBrush", Color.FromRgb(0x50, 0xAE, 0xE8));

            editor.Background = Brushes.Transparent;
            editor.Foreground = new SolidColorBrush(fg);
            editor.LineNumbersForeground = new SolidColorBrush(dim);
            editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B));
            editor.TextArea.Caret.CaretBrush = new SolidColorBrush(accent);

            // The shipped .xshd colors were written for a white editor - same lift EditorControl
            // applies to a document tab, keyed against PaneBrush since that is what this control
            // is actually sitting on.
            EditorHighlighting.MakeReadable(highlighting, bg);
            if (highlighting != null) editor.SyntaxHighlighting = highlighting;

            return editor;
        }

        private static Color Res(string key, Color fallback)
        {
            if (Application.Current?.TryFindResource(key) is SolidColorBrush b) return b.Color;
            return fallback;
        }

        /// <summary>Repopulates every field, plus the raw-XML view, from the current entry -
        /// called once by the constructor and again by Prev_Click/Next_Click so navigating never
        /// means tearing down and rebuilding the whole dialog.</summary>
        private void LoadEntry()
        {
            FieldsPanel.Children.Clear();
            BuildFields();

            _xmlEditor.Text = string.IsNullOrEmpty(Current.RawXml)
                ? MainWindow.LocStatic("Str_Evt_NoMessage")
                : Current.RawXmlFormatted;

            PrevBtn.IsEnabled = _index > 0;
            NextBtn.IsEnabled = _index < _entries.Count - 1;
        }

        /// <summary>Two side-by-side columns of label/value fields (Grid, not two independent
        /// StackPanels, so both columns' labels line up evenly), with Message spanning the full
        /// width beneath - the grid columns are compact and short, Message is often long and
        /// needs the room. FieldsPanel itself stays the plain vertical StackPanel the XAML
        /// declares; this just adds the columns Grid as its first child and the message field as
        /// its second.</summary>
        private void BuildFields()
        {
            var left = new StackPanel();
            var right = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };

            AddField(left,  "Str_Evt_DetailsLevel",    Current.Level);
            AddField(left,  "Str_Col_EvtLog",          Current.LogName);
            AddField(left,  "Str_Evt_DetailsTime",     Current.TimeLabel);
            AddField(left,  "Str_Evt_DetailsSource",   Current.Source);
            AddField(left,  "Str_Evt_DetailsId",       Current.EventId.ToString(CultureInfo.InvariantCulture));
            AddField(left,  "Str_Evt_DetailsCategory", Current.TaskCategory);
            AddField(left,  "Str_Evt_Keywords",        Current.Keywords);
            AddField(left,  "Str_Evt_Computer",        Current.Computer, last: true);

            AddField(right, "Str_Evt_User",            Current.User);
            AddField(right, "Str_Evt_ProcessId",       Current.ProcessId);
            AddField(right, "Str_Evt_ThreadId",        Current.ThreadId);
            AddField(right, "Str_Evt_ActivityId",      Current.ActivityId);
            AddField(right, "Str_Evt_RecordId",        Current.RecordId);
            AddField(right, "Str_Evt_Opcode",          Current.Opcode, last: true);

            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 1);
            columns.Children.Add(left);
            columns.Children.Add(right);
            FieldsPanel.Children.Add(columns);

            // Full, untruncated - this dialog is exactly where the grid's clipped Message column
            // belongs in full (EventViewerControl.cs BuildGrid remark on the message column).
            AddField(FieldsPanel, "Str_Evt_DetailsMessage", Current.Message, wrap: true, last: true, topMargin: 14);
        }

        /// <summary>One labeled field, same shape as the About card's info panel
        /// (MainWindow.xaml AboutInfoGrid): a DimTextBrush Consolas 10 caption over a
        /// TextBrush value.</summary>
        private void AddField(Panel target, string labelKey, string value, bool wrap = false, bool last = false, double topMargin = 0)
        {
            var stack = new StackPanel { Margin = new Thickness(0, topMargin, 0, last ? 0 : 10) };

            var label = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 10 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            label.SetResourceReference(TextBlock.TextProperty, labelKey);
            stack.Children.Add(label);

            var val = new TextBlock
            {
                Text = string.IsNullOrEmpty(value) ? "-" : value,
                FontSize = 12,
                Margin = new Thickness(0, 1, 0, 0),
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            };
            val.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            stack.Children.Add(val);

            target.Children.Add(stack);
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private void ApplyRoundedCorners()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int pref = 2;   // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
            }
            catch { /* pre-Win11: no rounded-corner API */ }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

        /// <summary>Left/Right step to the previous/next record. This has to be a Preview
        /// (tunneling) handler at the Window level rather than living in OnKeyDown below: the
        /// raw-XML view (_xmlEditor, an AvalonEdit TextEditor) is a multi-line, focusable text
        /// control and, when it has focus, an unmodified arrow key is its own native caret-move
        /// and gets marked handled before the bubbling KeyDown event would ever reach this
        /// window's override - the same reason Ctrl+C is deliberately left out of OnKeyDown
        /// rather than force-intercepted. Scoped to "no modifier held" so Shift+Left/Right still
        /// lets the editor extend a text selection instead of paging records.</summary>
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.None)
            {
                if (e.Key == Key.Left && PrevBtn.IsEnabled) { Prev_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
                if (e.Key == Key.Right && NextBtn.IsEnabled) { Next_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
            }

            base.OnPreviewKeyDown(e);
        }

        /// <summary>Dialog-scoped shortcuts for all four footer actions (Steve wants every
        /// function reachable from the keyboard now that the buttons are icon-only). Same
        /// override-OnKeyDown pattern FileDialog.xaml.cs already uses for its own Escape-to-close,
        /// not the main window's global Window_PreviewKeyDown table - this binding is local to the
        /// dialog. Alt+letter combos arrive as Key.System with the real key in e.SystemKey, not
        /// e.Key, because Alt is held.
        ///
        /// Ctrl+C is intentionally NOT force-intercepted: the raw-XML view (_xmlEditor) is a
        /// read-only AvalonEdit control with its own native Ctrl+C selected-text copy, and when
        /// it (or any other control) already handles the key its handler marks e.Handled = true
        /// before the event bubbles up to this override, so the override never fires and normal
        /// text-selection copy wins. Only when nothing already claimed the key does Ctrl+C fall
        /// through to the dialog's "copy details" action.</summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }

            if (e.Key == Key.System)
            {
                if (e.SystemKey == Key.X) { XmlToggle_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
                if (e.SystemKey == Key.S) { SearchOnline_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CopyDetails_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (_index <= 0) return;
            _index--;
            LoadEntry();
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_index >= _entries.Count - 1) return;
            _index++;
            LoadEntry();
        }

        private void XmlToggle_Click(object sender, RoutedEventArgs e)
        {
            _showingXml = !_showingXml;
            FormattedScroll.Visibility = _showingXml ? Visibility.Collapsed : Visibility.Visible;
            XmlHost.Visibility         = _showingXml ? Visibility.Visible   : Visibility.Collapsed;
            // The button is icon-only now (same glyph either way, it is a toggle) - only the
            // tooltip text flips between the two states, same "(Alt+X)" shortcut both ways.
            XmlToggleBtn.SetResourceReference(FrameworkElement.ToolTipProperty,
                _showingXml ? "Str_TT_EvtViewFormatted" : "Str_TT_EvtViewXml");
        }

        /// <summary>Opens the user's default browser on a web search for this event - a
        /// provider-specific event ID means little on its own, and this is the fastest way to
        /// whatever forum thread or Microsoft doc already explains it. Same
        /// Process.Start(UseShellExecute=true) technique every other link in the app opens with
        /// (Shell/Chrome.cs Hyperlink_RequestNavigate, Shell/About.cs OpenUrl).</summary>
        private void SearchOnline_Click(object sender, RoutedEventArgs e)
        {
            string query = string.IsNullOrEmpty(Current.Source)
                ? "event " + Current.EventId.ToString(CultureInfo.InvariantCulture)
                : Current.Source + " event " + Current.EventId.ToString(CultureInfo.InvariantCulture);
            string url = "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* no browser available - ignore, same as About.cs OpenUrl */ }
        }

        /// <summary>The same formatted block the grid's own right-click "Copy details" produces
        /// (EventViewerControl.FormatDetails) - this dialog is self-sufficient, so getting the
        /// full text out does not mean closing it and right-clicking the row again.</summary>
        private void CopyDetails_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(EventViewerControl.FormatDetails(Current)); }
            catch { /* clipboard unavailable - not worth a dialog over */ }
        }
    }
}
