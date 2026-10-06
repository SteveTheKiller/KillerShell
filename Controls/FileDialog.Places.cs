using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace KillerShell
{
    public partial class FileDialog
    {
        private const string PinnedKey = "FileDlgPinned";
        private const string LastOpenKey = "FileDlgLastOpenDir";
        private const string LastSaveKey = "FileDlgLastSaveDir";
        private static readonly string GlyphFolderPlace = ((char)0xE8B7).ToString();

        private void BuildPlaces()
        {
            Places.Clear();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in PinnedPaths())
                if (added.Add(p.TrimEnd('\\'))) AddPlace(LabelFor(p), p, pinned: true);

            foreach (var place in ExplorerQuickAccessPlaces())
                if (added.Add(place.Path.TrimEnd('\\'))) AddPlace(place.Label, place.Path);

            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                string label;
                try { label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.DriveType.ToString() : d.VolumeLabel.Trim(); }
                catch { label = d.DriveType.ToString(); }
                if (added.Add(d.RootDirectory.FullName.TrimEnd('\\')))
                    AddPlace($"{d.Name.TrimEnd('\\')}  {label}", d.RootDirectory.FullName);
            }
        }

        private static List<(string Label, string Path)> ExplorerQuickAccessPlaces()
        {
            const string QuickAccess = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";
            var places = new List<(string Label, string Path)>();
            object? shell = null, folder = null, items = null;
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type == null) return places;
                shell = Activator.CreateInstance(type);
                folder = shell!.GetType().InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, [QuickAccess]);
                if (folder == null) return places;
                items = folder.GetType().InvokeMember("Items", System.Reflection.BindingFlags.InvokeMethod, null, folder, null);
                int count = Convert.ToInt32(items!.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, items, null));
                for (int i = 0; i < count; i++)
                {
                    object? item = null;
                    try
                    {
                        item = items.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, items, [i]);
                        if (item == null) continue;
                        object? Property(string name) => item.GetType().InvokeMember(name, System.Reflection.BindingFlags.GetProperty, null, item, null);
                        if (!Convert.ToBoolean(Property("IsFolder"))) continue;
                        string path = Convert.ToString(Property("Path")) ?? "";
                        string name = Convert.ToString(Property("Name")) ?? "";
                        if (Directory.Exists(path)) places.Add((name.Length > 0 ? name : LabelFor(path), path));
                    }
                    catch { /* One unreadable entry must not hide the remaining folders. */ }
                    finally { if (item != null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item); }
                }
            }
            catch { /* Quick Access is optional; local pins and drives remain available. */ }
            finally
            {
                if (items != null && Marshal.IsComObject(items)) Marshal.FinalReleaseComObject(items);
                if (folder != null && Marshal.IsComObject(folder)) Marshal.FinalReleaseComObject(folder);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
            return places;
        }

        /// <summary>
        /// The persisted pin list. First run (key absent, null) seeds the five standard folders;
        /// an EMPTY stored value means the user unpinned everything and must stay empty.
        /// </summary>
        private static List<string> PinnedPaths()
        {
            string? saved = Services.ThemeManager.GetSetting(PinnedKey);
            if (saved != null)
                return [.. saved.Split('|').Where(s => s.Length > 0)];

            string[] defaults =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            ];
            return [.. defaults.Where(p => !string.IsNullOrEmpty(p))];
        }

        /// <summary>Localized label for the five standard folders, plain folder name otherwise.</summary>
        private static string LabelFor(string path)
        {
            string p = path.TrimEnd('\\');
            bool Is(string other) => other.Length > 0 &&
                p.Equals(other.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

            if (Is(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))  return Loc("Str_QA_Home");
            if (Is(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)))      return Loc("Str_QA_Desktop");
            if (Is(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)))  return Loc("Str_QA_Documents");
            if (Is(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")))
                                                                                       return Loc("Str_QA_Downloads");
            if (Is(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)))   return Loc("Str_QA_Pictures");

            var name = Path.GetFileName(p);
            return name.Length == 0 ? p : name;
        }

        private void AddPlace(string label, string path, bool pinned = false)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Places.Add(new PickerPlace(pinned ? GlyphFolderPlace : GlyphDrive, label, path, pinned));
        }

        private void PinPlace(string path)
        {
            var list = PinnedPaths();
            if (list.Any(p => p.TrimEnd('\\').Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                return;
            list.Add(path);
            Services.ThemeManager.SetSetting(PinnedKey, string.Join("|", list));
            BuildPlaces();
            SyncPlacesSelection();
        }

        private PickerPlace? _placesMenuPlace;

        private void Places_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            _placesMenuPlace = ItemUnder<PickerPlace>(e.OriginalSource as DependencyObject);
            // Drives are dynamic, not pinned - nothing to remove; empty space likewise.
            if (_placesMenuPlace is not { Pinned: true }) e.Handled = true;
        }

        private void UnpinPlace_Click(object sender, RoutedEventArgs e)
        {
            if (_placesMenuPlace is not { Pinned: true } pl) return;
            var list = PinnedPaths()
                .Where(p => !p.TrimEnd('\\').Equals(pl.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                .ToList();
            Services.ThemeManager.SetSetting(PinnedKey, string.Join("|", list));
            BuildPlaces();
            SyncPlacesSelection();
        }

        private PickerEntry? _filesMenuEntry;

        private void Files_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            _filesMenuEntry = ItemUnder<PickerEntry>(e.OriginalSource as DependencyObject);
            if (_filesMenuEntry is not { IsFolder: true }) e.Handled = true;   // only folders pin
        }

        private void FilePin_Click(object sender, RoutedEventArgs e)
        {
            if (_filesMenuEntry is { IsFolder: true } en) PinPlace(en.FullPath);
        }

        /// <summary>Marks the place matching the current folder, or clears the marker.</summary>
        private void SyncPlacesSelection()
        {
            bool was = _navigating;
            _navigating = true;
            PlacesList.SelectedItem = _currentDir.Length == 0 ? null : Places.FirstOrDefault(p =>
                p.Path.TrimEnd('\\').Equals(_currentDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            _navigating = was;
        }

        /// <summary>The row model under a right-click, resolved by walking up to the ListBoxItem.</summary>
        private static T? ItemUnder<T>(DependencyObject? d) where T : class
        {
            while (d != null)
            {
                if (d is ListBoxItem lbi) return lbi.DataContext as T;
                d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

    }
}
