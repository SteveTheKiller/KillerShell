using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using KillerShell.Shell;

namespace KillerShell
{
    /// <summary>Open or Save. Picked at construction; changes the accept button and the rules.</summary>
    public enum FileDialogMode { Open, Save }

    /// <summary>
    /// Themed stand-in for Microsoft.Win32.OpenFileDialog / SaveFileDialog. Same chrome, places
    /// rail, view modes and sortable columns as FolderPickerDialog (row styles shared from
    /// Controls.xaml), plus a file name box and a filter combo.
    ///
    /// The property surface mirrors the Win32 dialogs on purpose - Title, Filter, FilterIndex,
    /// FileName, InitialDirectory, DefaultExt, AddExtension, OverwritePrompt, CheckFileExists -
    /// so adopting it at a call site is a one-word change:
    ///
    ///     var dlg = new FileDialog(FileDialogMode.Save) { Title = ..., Filter = ..., FileName = ... };
    ///     if (dlg.ShowDialog(owner) == true) Use(dlg.FileName);
    ///
    /// Multiselect is deliberately NOT implemented yet - no call site in the family needs it, and
    /// a half-working Multiselect is worse than an absent one. Add it when something wants it.
    /// </summary>
    public partial class FileDialog : Window
    {
        // ── Win32-compatible surface ─────────────────────────────────────────────

        /// <summary>Win32 filter syntax: "Desc|*.a;*.b|Other|*.c". Empty means every file.</summary>
        public string Filter { get; set; } = "";

        /// <summary>1-based, like the Win32 dialogs. Out of range is clamped.</summary>
        public int FilterIndex { get; set; } = 1;

        /// <summary>Seeded with a suggested name; on OK, the full chosen path.</summary>
        public string FileName { get; set; } = "";

        public string InitialDirectory { get; set; } = "";

        /// <summary>Appended on save when the typed name has no extension. No leading dot needed.</summary>
        public string DefaultExt { get; set; } = "";

        public bool AddExtension { get; set; } = true;

        /// <summary>Save mode: confirm before replacing an existing file.</summary>
        public bool OverwritePrompt { get; set; } = true;

        /// <summary>Open mode: refuse to return a path that does not exist.</summary>
        public bool CheckFileExists { get; set; } = true;

        // ── internals ────────────────────────────────────────────────────────────

        private readonly FileDialogMode _mode;
        internal Func<string, CancellationToken, PickerEntry[]?> ReadDirectory { get; set; } = Services.PickerDirectoryReader.Read;
        internal Func<CancellationToken, (string Label, string Path)[]> ReadDrives { get; set; } = Services.PickerDirectoryReader.ReadDrives;
        internal Func<string, bool> FolderExists { get; set; } = Directory.Exists;
        internal Func<string, bool> PathIsFile { get; set; } = File.Exists;
        private readonly Services.PickerWork _navigationWork;
        private readonly Services.PickerWork _sortWork;
        private readonly Services.PickerWork _placesWork;
        private readonly Services.PickerWork _validationWork;
        internal Task NavigationTask { get; private set; } = Task.CompletedTask;
        internal Task SortTask { get; private set; } = Task.CompletedTask;
        internal Task PlacesTask { get; private set; } = Task.CompletedTask;
        internal Task AcceptanceTask { get; private set; } = Task.CompletedTask;

        public ObservableCollection<PickerPlace> Places  { get; } = [];
        public ObservableCollection<PickerEntry> Entries { get; } = [];

        private readonly List<PickerEntry> _raw = [];
        private string _currentDir = string.Empty;
        private bool _navigating;
        private bool _built;                 // suppresses filter events during construction
        private int  _viewMode;              // 0 list, 1 icons, 2 details
        private int  _sortKey;               // 0 name, 1 size, 2 modified
        private bool _sortAsc = true;

        // Per-filter-entry patterns, parallel to FilterCombo's items. Empty list = show all.
        private readonly List<string[]> _filterPatterns = [];

        private static readonly string GlyphDrive     = ((char)0xEDA2).ToString();
        private static readonly string ArrowUp        = ((char)0xE70E).ToString();
        private static readonly string ArrowDown      = ((char)0xE70D).ToString();

        public FileDialog(FileDialogMode mode = FileDialogMode.Open)
        {
            _mode = mode;
            _navigationWork = new Services.PickerWork(Dispatcher);
            _sortWork = new Services.PickerWork(Dispatcher);
            _placesWork = new Services.PickerWork(Dispatcher);
            _validationWork = new Services.PickerWork(Dispatcher);
            Closed += (_, _) => { _navigationWork.Dispose(); _sortWork.Dispose(); _placesWork.Dispose(); _validationWork.Dispose(); };
            InitializeComponent();
            FileNameBox.TextChanged += (_, _) => _validationWork.Cancel();
            PathBox.TextChanged += (_, _) => _validationWork.Cancel();
            Loaded += (_, _) => Anim.FadeIn(RootBorder);
            void RefreshPickerChrome() => PickerWindowChrome.Apply(this, RootBorder);
            Loaded += (_, _) => Services.ThemeManager.ThemeChanged += RefreshPickerChrome;
            Closed += (_, _) => Services.ThemeManager.ThemeChanged -= RefreshPickerChrome;

            // Size and placement remembered separately from the folder picker: this dialog is a
            // different shape and sharing the keys would make each one fight the other.
            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                if (double.TryParse(Services.ThemeManager.GetSetting("FileDlgW"),
                        System.Globalization.NumberStyles.Float, ci, out double w) &&
                    double.TryParse(Services.ThemeManager.GetSetting("FileDlgH"),
                        System.Globalization.NumberStyles.Float, ci, out double h))
                {
                    Width  = Math.Max(MinWidth,  Math.Min(w, SystemParameters.WorkArea.Width));
                    Height = Math.Max(MinHeight, Math.Min(h, SystemParameters.WorkArea.Height));
                }
                if (double.TryParse(Services.ThemeManager.GetSetting("FileDlgX"),
                        System.Globalization.NumberStyles.Float, ci, out double x) &&
                    double.TryParse(Services.ThemeManager.GetSetting("FileDlgY"),
                        System.Globalization.NumberStyles.Float, ci, out double y))
                {
                    var wa = SystemParameters.WorkArea;
                    if (x > wa.Left - Width + 80 && x < wa.Right - 80 &&
                        y > wa.Top - 20 && y < wa.Bottom - 80)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = x;
                        Top  = y;
                    }
                }
            }
            catch { /* registry unavailable - defaults are fine */ }

            Closing += (_, _) =>
            {
                try
                {
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    Services.ThemeManager.SetSetting("FileDlgW", ActualWidth.ToString(ci));
                    Services.ThemeManager.SetSetting("FileDlgH", ActualHeight.ToString(ci));
                    Services.ThemeManager.SetSetting("FileDlgX", Left.ToString(ci));
                    Services.ThemeManager.SetSetting("FileDlgY", Top.ToString(ci));
                }
                catch { /* not worth failing the close */ }
            };

            SourceInitialized += (_, _) =>
            {
                PickerWindowChrome.Apply(this, RootBorder);
                var src = (System.Windows.Interop.HwndSource?)PresentationSource.FromVisual(this);
                src?.AddHook((h, msg, w, l, ref handled) =>
                {
                    if (msg == 0x0014 /* WM_ERASEBKGND */) { handled = true; return new IntPtr(1); }
                    return IntPtr.Zero;
                });
            };
        }

        /// <summary>
        /// Sets the owner and shows modally. Everything that depends on Filter / FileName /
        /// InitialDirectory is wired HERE rather than in the constructor, because callers set
        /// those as object-initializer properties after construction.
        /// </summary>
        public bool? ShowDialog(Window? owner)
        {
            if (owner != null && owner.IsVisible) Owner = owner;

            HeadingText.Text    = Title ?? "";
            AcceptButton.Content = Loc(_mode == FileDialogMode.Save ? "Str_Btn_Save" : "Str_Btn_Open");

            // Open mode has nothing to name, so the box is for typing/filtering a path, not a
            // new file. It stays visible: typing an exact name is faster than hunting for it.
            BuildFilters();
            BuildPlaces();
            PlacesList.ItemsSource = Places;
            FileList.ItemsSource   = Entries;
            ApplyView();

            // A seeded FileName can be a bare name ("export.ics"), a full path, or empty.
            string startDir = InitialDirectory;
            string? seededDirectory = null;
            string seedName = "";
            if (!string.IsNullOrWhiteSpace(FileName))
            {
                if (FileName.IndexOfAny(['\\', '/']) >= 0)
                {
                    var d = Path.GetDirectoryName(FileName);
                    seededDirectory = d;
                    seedName = Path.GetFileName(FileName);
                }
                else seedName = FileName;
            }
            string? remembered = Services.ThemeManager.GetSetting(_mode == FileDialogMode.Open ? LastOpenKey : LastSaveKey);
            _built = true;
            NavigateInitially([seededDirectory, startDir, remembered, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)]);
            FileNameBox.Text = seedName;

            // Save: preselect the stem so typing replaces the name but keeps the extension
            // visible. Open: caret at the end.
            FileNameBox.Focus();
            if (_mode == FileDialogMode.Save && seedName.Length > 0)
            {
                int dot = seedName.LastIndexOf('.');
                FileNameBox.Select(0, dot > 0 ? dot : seedName.Length);
            }
            else FileNameBox.CaretIndex = FileNameBox.Text.Length;

            return ShowDialog();
        }

        // ── Filters ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Parses Win32 filter syntax into the combo plus a parallel pattern list. A malformed
        /// filter (odd number of segments) degrades to "all files" rather than throwing - a bad
        /// filter string should not stop someone opening a file.
        /// </summary>
        private void BuildFilters()
        {
            FilterCombo.Items.Clear();
            _filterPatterns.Clear();

            var parts = (Filter ?? "").Split('|');
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                var label = parts[i].Trim();
                var pats  = parts[i + 1].Split(';')
                                        .Select(p => p.Trim())
                                        .Where(p => p.Length > 0)
                                        .ToArray();
                if (label.Length == 0 || pats.Length == 0) continue;
                FilterCombo.Items.Add(label);
                _filterPatterns.Add(pats);
            }

            if (FilterCombo.Items.Count == 0)
            {
                FilterCombo.Items.Add(Loc("Str_Dlg_AllFiles"));
                _filterPatterns.Add(["*.*"]);
            }

            int idx = FilterIndex - 1;
            FilterCombo.SelectedIndex = idx >= 0 && idx < FilterCombo.Items.Count ? idx : 0;
            FilterLabel.Visibility = FilterCombo.Visibility =
                FilterCombo.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_built) return;
            FilterIndex = FilterCombo.SelectedIndex + 1;
            ApplySort();
        }

        /// <summary>True when the name passes the active filter. Folders are never filtered out.</summary>
        private static bool PassesFilter(PickerEntry en, string[] pats)
        {
            if (en.IsFolder) return true;
            return pats.Any(p => p == "*.*" || p == "*" || WildcardMatch(en.Name, p));
        }

        /// <summary>Case-insensitive glob. Anchored, so "*.ics" does not match "a.icsx".</summary>
        private static bool WildcardMatch(string name, string pattern)
        {
            var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(name, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // ── Quick places ─────────────────────────────────────────────────────────


        private static string Loc(string key)
            => Application.Current.TryFindResource(key) as string ?? key;

        // ── Navigation ───────────────────────────────────────────────────────────

        private void NavigateTo(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            var read = ReadDirectory;
            StartNavigation(token => (dir, read(dir, token)));
        }

        private void NavigateInitially(string?[] candidates)
        {
            var exists = FolderExists;
            var read = ReadDirectory;
            StartNavigation(token =>
            {
                foreach (string? candidate in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(candidate) && exists(candidate!)) return (candidate!, read(candidate!, token));
                }
                return (string.Empty, (PickerEntry[]?)null);
            });
        }

        private void StartNavigation(Func<CancellationToken, (string Directory, PickerEntry[]? Entries)> read)
        {
            _sortWork.Cancel();
            _validationWork.Cancel();
            NavigationTask = _navigationWork.RunAsync(read, async (result, token) =>
            {
                if (result.Entries == null) { SelMeta.Text = Loc("Str_Dlg_NoSuchFolder"); return; }
                string dir = result.Directory;
                _currentDir = dir;
                PathBox.Text = dir;
                _raw.Clear();
                _raw.AddRange(result.Entries);
                await ApplySortAsync();
                token.ThrowIfCancellationRequested();
                UpButton.IsEnabled = Directory.GetParent(dir) != null;
                UpdateInfoSummary();
                SyncPlacesSelection();
            }, _ => SelMeta.Text = Loc("Str_Dlg_NoSuchFolder"));
        }

        private void Up_Click(object sender, RoutedEventArgs e)
        {
            var parent = Directory.GetParent(_currentDir);
            if (parent != null) NavigateTo(parent.FullName);
        }

        private void Places_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_navigating) return;
            if (PlacesList.SelectedItem is PickerPlace p) NavigateTo(p.Path);
        }

        private void Files_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_navigating) return;
            if (FileList.SelectedItem is PickerEntry en)
            {
                // Selecting a FILE fills the name box - that is the value being chosen. Selecting
                // a folder does not: it is a navigation target, and overwriting the typed name
                // with a folder name would lose what the user was in the middle of typing.
                if (!en.IsFolder) FileNameBox.Text = en.Name;
                SelName.Text = en.Name;
                SelMeta.Text = en.IsFolder ? en.ModifiedLabel : $"{en.SizeLabel}  |  {en.ModifiedLabel}";
            }
            else UpdateInfoSummary();
        }

        private void Files_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileList.SelectedItem is not PickerEntry en) return;
            if (en.IsFolder) NavigateTo(en.FullPath);
            else Accept();
        }

        private void PathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var typed = PathBox.Text?.Trim();
            if (!string.IsNullOrEmpty(typed)) NavigateTo(typed!);
            e.Handled = true;
        }

        private void FileNameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;

            var typed = FileNameBox.Text?.Trim() ?? "";

            // A directory typed into the name box navigates instead of accepting - matches the
            // Win32 dialogs, and is how people paste a path in.
            if (!TryResolveName(typed, true, out var asDir)) return;
            var exists = FolderExists;
            AcceptanceTask = _validationWork.RunAsync(_ => typed.Length > 0 && exists(asDir), (isFolder, token) =>
            {
                if (isFolder) { NavigateTo(asDir); FileNameBox.Clear(); return Task.CompletedTask; }
                if (typed.IndexOfAny(['*', '?']) >= 0)
                {
                    _filterPatterns.Insert(0, [typed]);
                    FilterCombo.Items.Insert(0, typed);
                    FilterCombo.SelectedIndex = 0;
                    FileNameBox.Clear();
                }
                else Accept();
                return Task.CompletedTask;
            });
        }

        private void UpdateInfoSummary()
        {
            int folders = _raw.Count(x => x.IsFolder);
            int shown   = Entries.Count(x => !x.IsFolder);
            var leaf    = Path.GetFileName(_currentDir.TrimEnd('\\'));
            SelName.Text = leaf.Length == 0 ? _currentDir : leaf;
            SelMeta.Text = string.Format(Loc("Str_Sum_Counts"), folders, shown);
        }

        // ── View modes ───────────────────────────────────────────────────────────

        private void ViewList_Click(object sender, RoutedEventArgs e)    => SetView(0);
        private void ViewIcons_Click(object sender, RoutedEventArgs e)   => SetView(1);
        private void ViewDetails_Click(object sender, RoutedEventArgs e) => SetView(2);

        private void SetView(int mode)
        {
            _viewMode = mode;
            ApplyView();
        }

        /// <summary>
        /// The three views differ in panel, template AND scroll direction - that last one is the
        /// part that is easy to miss. List view wraps into columns and scrolls sideways, which only
        /// works if vertical scrolling is DISABLED: an enabled vertical ScrollViewer hands the panel
        /// infinite height, so a vertical WrapPanel never wraps and you get one tall column.
        /// </summary>
        private void ApplyView()
        {
            Resources["PickerIconSize"] = (double)_pickerIconSize;
            Resources["PickerIconContentWidth"] = (double)Math.Max(84, _pickerIconSize + 24);
            Resources["PickerIconTileWidth"] = (double)Math.Max(96, _pickerIconSize + 36);
            Resources["PickerIconTileHeight"] = (double)(_pickerIconSize + 44);
            switch (_viewMode)
            {
                case 1:  // icons: grid, wraps across, scrolls down
                    FileList.ItemsPanel   = (ItemsPanelTemplate)FindResource("PanelIconGrid");
                    FileList.ItemTemplate = (DataTemplate)FindResource("IconTemplate");
                    ScrollViewer.SetHorizontalScrollBarVisibility(FileList, ScrollBarVisibility.Disabled);
                    ScrollViewer.SetVerticalScrollBarVisibility(FileList, ScrollBarVisibility.Auto);
                    break;

                case 2:  // details: one row per entry, scrolls down
                    FileList.ItemsPanel   = (ItemsPanelTemplate)FindResource("PanelStack");
                    FileList.ItemTemplate = (DataTemplate)FindResource("DetailsTemplate");
                    ScrollViewer.SetHorizontalScrollBarVisibility(FileList, ScrollBarVisibility.Disabled);
                    ScrollViewer.SetVerticalScrollBarVisibility(FileList, ScrollBarVisibility.Auto);
                    break;

                default: // list: columns of small icons, scrolls RIGHT
                    FileList.ItemsPanel   = (ItemsPanelTemplate)FindResource("PanelListCols");
                    FileList.ItemTemplate = (DataTemplate)FindResource("RowTemplate");
                    ScrollViewer.SetHorizontalScrollBarVisibility(FileList, ScrollBarVisibility.Auto);
                    ScrollViewer.SetVerticalScrollBarVisibility(FileList, ScrollBarVisibility.Disabled);
                    break;
            }

            DetailsHeader.Visibility = _viewMode == 2 ? Visibility.Visible : Visibility.Collapsed;
            ViewListBtn.Tag    = _viewMode == 0 ? "on" : null;
            ViewIconsBtn.Tag   = _viewMode == 1 ? "on" : null;
            ViewDetailsBtn.Tag = _viewMode == 2 ? "on" : null;
        }

        /// <summary>
        /// List view (the default) wraps into columns and scrolls RIGHT with vertical scrolling
        /// explicitly disabled (see ApplyView) - a plain mouse wheel only ever drives vertical
        /// scroll, so without this it had nothing to grab onto. Icons/Details already scroll fine
        /// under the wheel since they scroll vertically.
        /// </summary>
        private int _pickerIconSize = 32;
        private int _pickerWheelDelta;
        private static readonly int[] PickerIconSizes = [16, 32, 48, 96];

        private void ChangePickerView(int delta)
        {
            _pickerWheelDelta += delta;
            int steps = _pickerWheelDelta / 120;
            _pickerWheelDelta %= 120;
            if (steps == 0) return;
            int current = _viewMode == 2 ? 0 : _viewMode == 0 ? 1 :
                Array.IndexOf(PickerIconSizes, _pickerIconSize) + 2;
            int next = Math.Max(0, Math.Min(PickerIconSizes.Length + 1, current + steps));
            if (next >= 2) _pickerIconSize = PickerIconSizes[next - 2];
            SetView(next == 0 ? 2 : next == 1 ? 0 : 1);
        }

        private void FileList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                ChangePickerView(e.Delta);
                e.Handled = true;
                return;
            }
            _pickerWheelDelta = 0;
            if (_viewMode != 0) return;
            var sv = FindScrollViewer(FileList);
            if (sv == null) return;
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer found) return found;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var result = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (result != null) return result;
            }
            return null;
        }

        // ── Sorting ──────────────────────────────────────────────────────────────

        private void SortName_Click(object sender, RoutedEventArgs e)     => SetSort(0);
        private void SortSize_Click(object sender, RoutedEventArgs e)     => SetSort(1);
        private void SortModified_Click(object sender, RoutedEventArgs e) => SetSort(2);

        private void SetSort(int key)
        {
            if (_sortKey == key) _sortAsc = !_sortAsc;
            else { _sortKey = key; _sortAsc = true; }
            ApplySort();
        }

        /// <summary>
        /// Rebuilds Entries from _raw: filter applied, folders always before files, then the
        /// active sort key. Folders-first is not a sort key - it is the frame the sort runs in.
        /// </summary>
        private void ApplySort() => SortTask = ApplySortAsync();

        private Task ApplySortAsync()
        {
            var raw = _raw.ToArray();
            int key = _sortKey, filter = FilterCombo.SelectedIndex;
            bool ascending = _sortAsc;
            string[] patterns = filter >= 0 && filter < _filterPatterns.Count ? _filterPatterns[filter].ToArray() : ["*"];
            string? selected = (FileList.SelectedItem as PickerEntry)?.FullPath;
            return _sortWork.RunAsync(token =>
            {
                var visible = raw.Where(entry => PassesFilter(entry, patterns));
                IOrderedEnumerable<PickerEntry> ordered = key switch
                {
                    1 => ascending ? visible.OrderBy(x => !x.IsFolder).ThenBy(x => x.SizeBytes)
                                   : visible.OrderBy(x => !x.IsFolder).ThenByDescending(x => x.SizeBytes),
                    2 => ascending ? visible.OrderBy(x => !x.IsFolder).ThenBy(x => x.Modified)
                                   : visible.OrderBy(x => !x.IsFolder).ThenByDescending(x => x.Modified),
                    _ => ascending ? visible.OrderBy(x => !x.IsFolder).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                                   : visible.OrderBy(x => !x.IsFolder).ThenByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase),
                };
                return ordered.ToArray();
            }, async (ordered, token) =>
            {
                await Services.PickerWork.PublishAsync(ordered, Entries, token, MutateRows);
                token.ThrowIfCancellationRequested();
                if (selected != null && FileList.SelectedItem == null)
                    MutateRows(() => FileList.SelectedItem = Entries.FirstOrDefault(entry => entry.FullPath == selected));
                NameArrow.Text = key == 0 ? (ascending ? ArrowUp : ArrowDown) : "";
                SizeArrow.Text = key == 1 ? (ascending ? ArrowUp : ArrowDown) : "";
                ModArrow.Text = key == 2 ? (ascending ? ArrowUp : ArrowDown) : "";
                EmptyHint.Visibility = Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            });
        }

        private void MutateRows(Action change)
        {
            bool was = _navigating;
            _navigating = true;
            try { change(); }
            finally { _navigating = was; }
        }

        // ── Accept / cancel ──────────────────────────────────────────────────────

        private void OK_Click(object sender, RoutedEventArgs e) => Accept();

        private bool TryResolveName(string typed, bool allowWildcard, out string full)
        {
            full = "";
            try
            {
                // A pasted quoted path is supported; quotes inside a name are invalid.
                if (typed.Length >= 2 && typed[0] == '"' && typed[typed.Length - 1] == '"')
                    typed = typed.Substring(1, typed.Length - 2);
                full = Path.GetFullPath(Path.IsPathRooted(typed) ? typed : Path.Combine(_currentDir, typed));
                string relative = full.Substring(Path.GetPathRoot(full).Length);
                foreach (string component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                {
                    if (component.Length > 255) throw new PathTooLongException();
                    foreach (char character in component)
                        if (Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0 &&
                            !(allowWildcard && (character == '*' || character == '?')))
                            throw new ArgumentException();
                }
                return true;
            }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException ||
                                          error is PathTooLongException || error is System.Security.SecurityException)
            {
                SelMeta.Text = Loc("Str_Err_InvalidChars");
                FileNameBox.Focus();
                return false;
            }
        }

        /// <summary>
        /// Resolves the name box to a full path and applies the mode's rules. Anything that fails
        /// leaves the dialog OPEN with focus back in the name box - a file dialog that closes on a
        /// bad name and makes you start over is the worst outcome.
        /// </summary>
        private void Accept()
        {
            var typed = FileNameBox.Text?.Trim() ?? "";
            if (typed.Length == 0)
            {
                // Nothing typed but a file is highlighted: take that.
                if (FileList.SelectedItem is PickerEntry sel && !sel.IsFolder) typed = sel.Name;
                else { FileNameBox.Focus(); return; }
            }

            if (!TryResolveName(typed, false, out var full)) return;
            if (!NavigationTask.IsCompleted) return;

            if (_mode == FileDialogMode.Save)
            {
                if (AddExtension && !string.IsNullOrEmpty(DefaultExt) &&
                    string.IsNullOrEmpty(Path.GetExtension(full)))
                {
                    full += DefaultExt.StartsWith(".") ? DefaultExt : "." + DefaultExt;
                }

            }
            bool save = _mode == FileDialogMode.Save, overwrite = OverwritePrompt, checkFile = CheckFileExists;
            var folderExists = FolderExists;
            var fileExists = PathIsFile;
            AcceptanceTask = _validationWork.RunAsync(_ =>
            {
                string? directory = Path.GetDirectoryName(full);
                bool validFolder = !save || (!string.IsNullOrEmpty(directory) && folderExists(directory!));
                bool existingFile = (save ? overwrite : checkFile) && fileExists(full);
                return (validFolder, existingFile);
            }, (validation, token) =>
            {
                if (!validation.validFolder)
                {
                    SelMeta.Text = Loc("Str_Dlg_NoSuchFolder");
                    FileNameBox.Focus();
                    return Task.CompletedTask;
                }
                if (save && overwrite && validation.existingFile)
                {
                    var confirm = new ConfirmDialog(
                        string.Format(Loc("Str_Dlg_OverwriteMsg"), Path.GetFileName(full)),
                        null,
                        Loc("Str_Btn_Replace")) { Owner = this };
                    confirm.ShowDialog();
                    if (!confirm.Confirmed) { FileNameBox.Focus(); return Task.CompletedTask; }
                }
                if (!save && checkFile && !validation.existingFile)
                {
                    SelMeta.Text = Loc("Str_Dlg_NoSuchFile");
                    FileNameBox.Focus();
                    FileNameBox.SelectAll();
                    return Task.CompletedTask;
                }
                token.ThrowIfCancellationRequested();
                FileName = full;
                string? acceptedFolder = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(acceptedFolder))
                    Services.ThemeManager.SetSetting(save ? LastSaveKey : LastOpenKey, acceptedFolder);
                DialogResult = true;
                Close();
                return Task.CompletedTask;
            }, _ => SelMeta.Text = Loc("Str_Dlg_NoSuchFile"));
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

        // The status bar's size grip (98SE): hand the drag to Windows as a bottom-right resize,
        // the same as the main window footer's grip (Chrome.cs).
        private void ResizeGrip_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (WindowState != WindowState.Normal) return;
            e.Handled = true;
            SendMessage(new System.Windows.Interop.WindowInteropHelper(this).Handle,
                        0x00A1 /* WM_NCLBUTTONDOWN */, new IntPtr(17 /* HTBOTTOMRIGHT */), IntPtr.Zero);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
            base.OnKeyDown(e);
        }
    }
}
