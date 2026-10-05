using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShell.Shell;
using KillerShell.Terminal;

namespace KillerShell
{
    public partial class TerminalProfilesDialog : Window
    {
        private readonly ObservableCollection<TerminalProfileEntry> _profiles;
        private TerminalProfileEntry? _editing;
        private string _defaultId;
        private bool _loading;

        public TerminalProfilesDialog()
        {
            InitializeComponent();
            _profiles = new(TerminalProfileStore.Load().Select(p => p.Copy()));
            _defaultId = Services.ThemeManager.GetSetting("DefaultTerminalProfile") ?? "pwsh";
            ProfilesList.ItemsSource = _profiles;
            ProfilesList.SelectedIndex = 0;
            SourceInitialized += (_, _) => MainWindow.ApplyThemeBorder(this);
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        }

        private void StoreEditor()
        {
            if (_editing == null || _loading) return;
            _editing.Name = NameBox.Text.Trim();
            _editing.Executable = ExeBox.Text.Trim();
            _editing.Arguments = ArgsBox.Text;
            _editing.StartFolder = FolderBox.Text.Trim();
            if (Enum.TryParse(KindBox.SelectedValue as string, out TerminalShellKind kind)) _editing.CustomKind = kind;
            _editing.Hidden = HiddenBox.IsChecked == true;
            _editing.Elevated = AdminBox.IsChecked == true;
            if (DefaultBox.IsChecked == true) { _defaultId = _editing.Id; _editing.Hidden = false; }
        }

        private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            StoreEditor();
            _editing = ProfilesList.SelectedItem as TerminalProfileEntry;
            if (_editing == null) return;
            _loading = true;
            NameBox.Text = _editing.Name; ExeBox.Text = _editing.Executable; ArgsBox.Text = _editing.Arguments; FolderBox.Text = _editing.StartFolder;
            DefaultBox.IsChecked = _editing.Id == _defaultId; HiddenBox.IsChecked = _editing.Hidden; AdminBox.IsChecked = _editing.Elevated;
            bool custom = _editing.Kind == TerminalShellKind.Custom;
            KindBox.SelectedValue = custom ? _editing.CustomKind.ToString() : _editing.Kind == TerminalShellKind.Wsl ? "Custom" : _editing.Kind.ToString();
            KindBox.IsEnabled = custom;
            ExeBox.IsEnabled = ArgsBox.IsEnabled = RemoveBtn.IsEnabled = custom;
            AdminBox.IsEnabled = _editing.Kind != TerminalShellKind.Wsl;
            _loading = false;
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            StoreEditor();
            var entry = new TerminalProfileEntry { Name = MainWindow.LocStatic("Str_Term_Custom") + " " + (_profiles.Count + 1), Kind = TerminalShellKind.Custom };
            _profiles.Add(entry); ProfilesList.SelectedItem = entry; NameBox.Focus(); NameBox.SelectAll();
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (_editing?.Kind != TerminalShellKind.Custom) return;
            var removed = _editing; _editing = null; _profiles.Remove(removed);
            if (_defaultId == removed.Id) _defaultId = "pwsh";
            ProfilesList.SelectedIndex = 0;
        }

        private void Move(int offset)
        {
            StoreEditor();
            int from = ProfilesList.SelectedIndex, to = from + offset;
            if (from < 0 || to < 0 || to >= _profiles.Count) return;
            var selected = _editing;
            _loading = true; _profiles.Move(from, to); ProfilesList.SelectedItem = selected; _loading = false;
        }
        private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
        private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            StoreEditor();
            if (_profiles.Any(p => string.IsNullOrWhiteSpace(p.Name) || (p.Kind == TerminalShellKind.Custom && string.IsNullOrWhiteSpace(p.Executable))
                    || (p.Name + p.Executable + p.Arguments + p.StartFolder).IndexOfAny(['\r', '\n', '\0']) >= 0)
                || _profiles.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _profiles.Count)
            { ErrorText.Text = MainWindow.LocStatic("Str_Term_ProfileInvalid"); return; }
            if (_profiles.FirstOrDefault(p => p.Id == _defaultId)?.Hidden != false) _defaultId = "pwsh";
            if (!MainWindow.DemoMode) TerminalProfileStore.Save(_profiles, _defaultId);
            Close();
        }
        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
    }
}
