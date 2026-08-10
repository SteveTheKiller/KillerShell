using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using KillerShell.Models;
using KillerShell.Shell;

namespace KillerShell
{
    // Double-click a service row in Tools/ProcessListControl.cs (Grid_MouseDoubleClick, Services
    // mode) to get here. Same shape as ProcessDetailsDialog.xaml.cs - see the remark there.
    public partial class ServiceDetailsDialog : Window
    {
        private readonly IList<ServiceInfo> _entries;
        private int _index;

        private ServiceInfo Current => _entries[_index];

        public ServiceDetailsDialog(IList<ServiceInfo> entries, int startIndex)
        {
            InitializeComponent();
            _entries = entries;
            _index = startIndex >= 0 && startIndex < entries.Count ? startIndex : 0;

            Loaded += (_, _) => Anim.FadeIn(RootBorder);

            // Opens sized to its content, then becomes an ordinary resizable window whose body
            // scrolls instead of overflowing (Controls/DialogContentSize.cs).
            DialogContentSize.ReleaseAfterFirstRender(this, BodyRow);

            SourceInitialized += (_, _) =>
            {
                ApplyRoundedCorners();
                MainWindow.ApplyThemeBorder(this);
                DialogScreenClamp.Apply(this);
            };

            LoadEntry();
        }

        private void LoadEntry()
        {
            FieldsPanel.Children.Clear();
            BuildFields();

            PrevBtn.IsEnabled = _index > 0;
            NextBtn.IsEnabled = _index < _entries.Count - 1;
        }

        private void BuildFields()
        {
            var left = new StackPanel();
            var right = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };

            AddField(left,  "Str_Col_SvcName",        Current.Name);
            AddField(left,  "Str_Col_SvcDisplayName",  Current.DisplayName);
            AddField(left,  "Str_Col_SvcStatus",       Current.Status);
            AddField(left,  "Str_Col_SvcStartType",    Current.StartupType, last: true);

            AddField(right, "Str_Col_SvcLogOnAs", Current.LogOnAs);
            AddField(right, "Str_Col_SvcPath",    Current.Path, last: true);

            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 1);
            columns.Children.Add(left);
            columns.Children.Add(right);
            FieldsPanel.Children.Add(columns);

            AddField(FieldsPanel, "Str_Col_SvcDescription", Current.Description, wrap: true, last: true, topMargin: 14);
        }

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

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.None)
            {
                if (e.Key == Key.Left && PrevBtn.IsEnabled) { Prev_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
                if (e.Key == Key.Right && NextBtn.IsEnabled) { Next_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
            }

            base.OnPreviewKeyDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }

            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
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

        internal static string FormatDetails(ServiceInfo s)
        {
            string nl = Environment.NewLine;
            return MainWindow.LocStatic("Str_Col_SvcName")        + ": " + s.Name + nl
                 + MainWindow.LocStatic("Str_Col_SvcDisplayName") + ": " + s.DisplayName + nl
                 + MainWindow.LocStatic("Str_Col_SvcStatus")      + ": " + s.Status + nl
                 + MainWindow.LocStatic("Str_Col_SvcStartType")   + ": " + s.StartupType + nl
                 + MainWindow.LocStatic("Str_Col_SvcLogOnAs")     + ": " + s.LogOnAs + nl
                 + MainWindow.LocStatic("Str_Col_SvcPath")        + ": " + s.Path + nl
                 + MainWindow.LocStatic("Str_Col_SvcDescription") + ": " + s.Description;
        }

        private void CopyDetails_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(FormatDetails(Current)); }
            catch { /* clipboard unavailable - not worth a dialog over */ }
        }
    }
}
