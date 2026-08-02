using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using KillerShell.Shell;

namespace KillerShell
{
    // Grunge-themed, Explorer-style folder picker. Resizable; quick places + drives on
    // the left; the current folder's contents on the right with list / icon / details
    // views and sortable columns. Files show dimmed for context - only folders select.
    public partial class FolderPickerDialog : Window
    {
        public string? SelectedPath { get; private set; }

        public ObservableCollection<PickerPlace> Places  { get; } = [];
        public ObservableCollection<PickerEntry> Entries { get; } = [];

        private readonly List<PickerEntry> _raw = [];
        private string _currentDir = string.Empty;
        private bool _navigating;   // suppresses selection feedback while lists rebuild
        private int  _viewMode;     // 0 list, 1 icons, 2 details
        private int  _sortKey;      // 0 name, 1 size, 2 modified
        private bool _sortAsc = true;

        // Segoe MDL2 Assets glyphs, built from codepoints so the source stays pure ASCII.
        private static readonly string GlyphHome      = ((char)0xE80F).ToString();   // home
        private static readonly string GlyphDesktop   = ((char)0xE7F4).ToString();   // monitor
        private static readonly string GlyphDocuments = ((char)0xE8A5).ToString();   // document
        private static readonly string GlyphDownloads = ((char)0xE896).ToString();   // download arrow
        private static readonly string GlyphPictures  = ((char)0xE91B).ToString();   // photo
        private static readonly string GlyphDrive     = ((char)0xEDA2).ToString();   // hard drive
        private static readonly string ArrowUp        = ((char)0xE70E).ToString();   // chevron up
        private static readonly string ArrowDown      = ((char)0xE70D).ToString();   // chevron down

        public FolderPickerDialog(string? initialPath = null)
        {
            InitializeComponent();
            Loaded += (_, _) => Anim.FadeIn(RootBorder);

            // Remember the last picker size AND placement across opens and restarts
            // (HKCU\Software\KillerShell via the shared registry setting hooks).
            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                if (double.TryParse(Services.ThemeManager.GetSetting("PickerW"),
                        System.Globalization.NumberStyles.Float, ci, out double w) &&
                    double.TryParse(Services.ThemeManager.GetSetting("PickerH"),
                        System.Globalization.NumberStyles.Float, ci, out double h))
                {
                    Width  = Math.Max(MinWidth,  Math.Min(w, SystemParameters.WorkArea.Width));
                    Height = Math.Max(MinHeight, Math.Min(h, SystemParameters.WorkArea.Height));
                }
                if (double.TryParse(Services.ThemeManager.GetSetting("PickerX"),
                        System.Globalization.NumberStyles.Float, ci, out double x) &&
                    double.TryParse(Services.ThemeManager.GetSetting("PickerY"),
                        System.Globalization.NumberStyles.Float, ci, out double y))
                {
                    // Clamp so a saved spot from a detached monitor can't strand it off-screen.
                    var wa = SystemParameters.VirtualScreenWidth;
                    var ha = SystemParameters.VirtualScreenHeight;
                    if (x > -Width + 60 && x < wa - 60 && y >= 0 && y < ha - 60)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = x;
                        Top  = y;
                    }
                }
            }
            catch { /* bad/missing value - keep the default size/placement */ }
            Closing += (_, _) =>
            {
                try
                {
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    Services.ThemeManager.SetSetting("PickerW", Width.ToString(ci));
                    Services.ThemeManager.SetSetting("PickerH", Height.ToString(ci));
                    Services.ThemeManager.SetSetting("PickerX", Left.ToString(ci));
                    Services.ThemeManager.SetSetting("PickerY", Top.ToString(ci));
                }
                catch { /* registry unavailable - not worth failing the close */ }
            };
            // Win11 rounds the HWND and draws the drop shadow (no-op on Win10) -
            // same native-chrome approach as the KillerScan main window. The hook also
            // claims WM_ERASEBKGND (KillerPDF's anti-flash trick for resizes).
            SourceInitialized += (_, _) =>
            {
                ApplyRoundedCorners();
                MainWindow.ApplyThemeBorder(this);
                var src = (System.Windows.Interop.HwndSource?)PresentationSource.FromVisual(this);
                src?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    if (msg == 0x0014 /* WM_ERASEBKGND */) { handled = true; return new IntPtr(1); }
                    return IntPtr.Zero;
                });
            };

            BuildPlaces();
            PlacesList.ItemsSource = Places;
            FolderList.ItemsSource = Entries;
            ApplyView();

            string start = !string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath)
                ? initialPath!
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            NavigateTo(start);
        }

        // ── Quick places: known folders + ready drives ───────────────────────────
        private void BuildPlaces()
        {
            AddPlace(GlyphHome,      Loc("Str_QA_Home"),      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            AddPlace(GlyphDesktop,   Loc("Str_QA_Desktop"),   Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            AddPlace(GlyphDocuments, Loc("Str_QA_Documents"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            AddPlace(GlyphDownloads, Loc("Str_QA_Downloads"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            AddPlace(GlyphPictures,  Loc("Str_QA_Pictures"),  Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));

            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                string label;
                try { label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.DriveType.ToString() : d.VolumeLabel.Trim(); }
                catch { label = d.DriveType.ToString(); }
                AddPlace(GlyphDrive, $"{d.Name.TrimEnd('\\')}  {label}", d.RootDirectory.FullName);
            }
        }

        private void AddPlace(string glyph, string label, string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Places.Add(new PickerPlace(glyph, label, path));
        }

        private static string Loc(string key)
            => Application.Current.TryFindResource(key) as string ?? key;

        // ── Navigation ───────────────────────────────────────────────────────────
        private void NavigateTo(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

            _navigating = true;
            _currentDir  = dir;
            PathBox.Text = dir;
            _raw.Clear();

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    DirectoryInfo info;
                    try { info = new DirectoryInfo(sub); } catch { continue; }
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    _raw.Add(new PickerEntry(info.Name, sub, true, 0, SafeTime(() => info.LastWriteTime)));
                }
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    FileInfo fi;
                    try { fi = new FileInfo(file); } catch { continue; }
                    if ((fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    _raw.Add(new PickerEntry(fi.Name, file, false, SafeLen(fi), SafeTime(() => fi.LastWriteTime)));
                }
            }
            catch { /* unauthorized / unreadable - show what we have */ }

            ApplySort();
            UpButton.IsEnabled = Directory.GetParent(dir) != null;
            UpdateInfoSummary();
            _navigating = false;
        }

        private static DateTime SafeTime(Func<DateTime> get)
        {
            try { return get(); } catch { return DateTime.MinValue; }
        }

        private static long SafeLen(FileInfo fi)
        {
            try { return fi.Length; } catch { return 0; }
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

        private void Folders_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_navigating) return;
            if (FolderList.SelectedItem is PickerEntry en)
            {
                // Only folders are pickable; a file click just shows its details below.
                if (en.IsFolder) PathBox.Text = en.FullPath;
                SelName.Text = en.Name;
                SelMeta.Text = en.IsFolder
                    ? en.ModifiedLabel
                    : $"{en.SizeLabel}  |  {en.ModifiedLabel}";
            }
            else UpdateInfoSummary();
        }

        private void Folders_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FolderList.SelectedItem is PickerEntry en && en.IsFolder) NavigateTo(en.FullPath);
        }

        private void PathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var typed = PathBox.Text?.Trim();
            if (!string.IsNullOrEmpty(typed) && Directory.Exists(typed)) NavigateTo(typed!);
            e.Handled = true;
        }

        // Footer info when nothing is selected: current folder + content counts.
        private void UpdateInfoSummary()
        {
            int folders = _raw.Count(x => x.IsFolder);
            int files   = _raw.Count - folders;
            var leaf    = Path.GetFileName(_currentDir.TrimEnd('\\'));
            SelName.Text = leaf.Length == 0 ? _currentDir : leaf;
            SelMeta.Text = string.Format(Loc("Str_Sum_Counts"), folders, files);
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

        private void ApplyView()
        {
            FolderList.ItemsPanel   = (ItemsPanelTemplate)FindResource(_viewMode == 1 ? "PanelWrap" : "PanelStack");
            FolderList.ItemTemplate = (DataTemplate)FindResource(_viewMode == 2 ? "DetailsTemplate" : "RowTemplate");
            DetailsHeader.Visibility = _viewMode == 2 ? Visibility.Visible : Visibility.Collapsed;
            ViewListBtn.Tag    = _viewMode == 0 ? "on" : null;
            ViewIconsBtn.Tag   = _viewMode == 1 ? "on" : null;
            ViewDetailsBtn.Tag = _viewMode == 2 ? "on" : null;
        }

        // ── Sorting ──────────────────────────────────────────────────────────────
        private void SortName_Click(object sender, RoutedEventArgs e)     => SetSort(0);
        private void SortSize_Click(object sender, RoutedEventArgs e)     => SetSort(1);
        private void SortModified_Click(object sender, RoutedEventArgs e) => SetSort(2);

        private void SetSort(int key)
        {
            if (_sortKey == key) _sortAsc = !_sortAsc;
            else { _sortKey = key; _sortAsc = key == 0; }   // size/modified start biggest/newest first
            ApplySort();
        }

        // Folders always list before files; within each group the sort key applies
        // (folders fall back to name order for the size key - they have no size here).
        private void ApplySort()
        {
            int Cmp(PickerEntry a, PickerEntry b)
            {
                int r = _sortKey switch
                {
                    1 => (a.IsFolder && b.IsFolder)
                            ? string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
                            : a.SizeBytes.CompareTo(b.SizeBytes),
                    2 => a.Modified.CompareTo(b.Modified),
                    _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                };
                return _sortAsc ? r : -r;
            }

            var folders = _raw.Where(x => x.IsFolder).ToList();
            var files   = _raw.Where(x => !x.IsFolder).ToList();
            folders.Sort(Cmp);
            files.Sort(Cmp);

            Entries.Clear();
            foreach (var en in folders) Entries.Add(en);
            foreach (var en in files)   Entries.Add(en);

            EmptyHint.Visibility = Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NameArrow.Text = _sortKey == 0 ? (_sortAsc ? ArrowUp : ArrowDown) : string.Empty;
            SizeArrow.Text = _sortKey == 1 ? (_sortAsc ? ArrowUp : ArrowDown) : string.Empty;
            ModArrow.Text  = _sortKey == 2 ? (_sortAsc ? ArrowUp : ArrowDown) : string.Empty;
        }

        // ── Chrome / confirm ─────────────────────────────────────────────────────
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

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            var path = PathBox.Text?.Trim();
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                SelectedPath = path;
                DialogResult = true;
            }
            Close();
        }
    }

    public sealed class PickerPlace(string glyph, string label, string path)
    {
        public string Glyph { get; } = glyph;
        public string Label { get; } = label;
        public string Path  { get; } = path;
    }

    // One row in the folder pane: a subfolder or a (dimmed, non-pickable) file.
    public sealed class PickerEntry(string name, string fullPath, bool isFolder, long sizeBytes, DateTime modified)
    {
        private static readonly string GlyphFolder = ((char)0xE8B7).ToString();
        private static readonly string GlyphFile   = ((char)0xE8A5).ToString();

        public string   Name      { get; } = name;
        public string   FullPath  { get; } = fullPath;
        public bool     IsFolder  { get; } = isFolder;
        public long     SizeBytes { get; } = sizeBytes;
        public DateTime Modified  { get; } = modified;

        public string Glyph         => IsFolder ? GlyphFolder : GlyphFile;

        /// <summary>Real Explorer icon, 16px, for the list and details rows. Lazily fetched and
        /// cached by extension in ShellIcons, so binding it per row is cheap.</summary>
        public System.Windows.Media.ImageSource? Icon
            => Services.ShellIcons.Small(FullPath, IsFolder);

        /// <summary>Real Explorer icon, 32px, for the icon grid.</summary>
        public System.Windows.Media.ImageSource? IconLarge
            => Services.ShellIcons.Large(FullPath, IsFolder);
        public string SizeLabel     => IsFolder ? string.Empty : FormatSize(SizeBytes);
        public string ModifiedLabel => Modified == DateTime.MinValue ? string.Empty : Modified.ToString("yyyy-MM-dd HH:mm");

        private static string FormatSize(long b)
        {
            if (b < 1024) return b + " B";
            double kb = b / 1024.0;
            if (kb < 1024) return kb.ToString("0") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.0") + " MB";
            return (mb / 1024.0).ToString("0.00") + " GB";
        }
    }
}
