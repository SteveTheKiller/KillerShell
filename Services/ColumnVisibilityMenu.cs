using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

// One shared right-click "which columns are shown" menu, used by three otherwise unrelated
// grids: the Event Viewer's DataGrid, the Processes DataGrid, and the file browser's hand-rolled
// Details view (Controls/FilePane.xaml, ResultsView.cs) - which is not a DataGrid at all. The
// column-agnostic Entry below is what makes one implementation cover both shapes: a DataGrid
// column's Visibility and the details view's own ResultsViewState bools are read and written the
// same way underneath, through a getter/setter pair rather than a concrete column type.
namespace KillerShell.Services
{
    internal static class ColumnVisibilityMenu
    {
        /// <summary>
        /// One toggleable column: its label, its persisted default, and how to read/write
        /// whether it is currently shown. Column-agnostic on purpose - see the file header.
        /// </summary>
        internal readonly struct Entry
        {
            internal readonly string Key;                 // stable settings-key suffix - never localized or renamed
            internal readonly string HeaderResourceKey;    // Str_Col_* shown in the toggle menu
            internal readonly bool DefaultVisible;
            internal readonly Func<bool> GetVisible;
            internal readonly Action<bool> SetVisible;

            internal Entry(string key, string headerResourceKey, bool defaultVisible,
                            Func<bool> getVisible, Action<bool> setVisible)
            {
                Key = key;
                HeaderResourceKey = headerResourceKey;
                DefaultVisible = defaultVisible;
                GetVisible = getVisible;
                SetVisible = setVisible;
            }
        }

        private static string SettingKey(string settingsKey, string columnKey)
            => "ColVis_" + settingsKey + "_" + columnKey;

        /// <summary>
        /// Applies every entry's persisted visibility, or its default the first time nothing has
        /// been saved yet. Call once up front (grid construction / window init) - a click on the
        /// menu re-persists from then on through ShowFor's own MenuItem handlers.
        /// </summary>
        internal static void RestoreVisibility(string settingsKey, params Entry[] columns)
        {
            foreach (var c in columns)
            {
                string? saved = ThemeManager.GetSetting(SettingKey(settingsKey, c.Key));
                c.SetVisible(saved != null ? saved == "1" : c.DefaultVisible);
            }
        }

        /// <summary>
        /// Builds and opens a checkable toggle menu anchored to <paramref name="placementTarget"/>
        /// - one MenuItem per entry, IsChecked mirroring its current visibility, toggling and
        /// re-persisting it on click.
        /// </summary>
        internal static void ShowFor(FrameworkElement placementTarget, string settingsKey, bool keyboard,
                                      params Entry[] columns)
        {
            var previousFocus = Keyboard.FocusedElement as UIElement;
            var previousRow = FindAncestor<DataGridRow>(previousFocus);
            var menu = new ContextMenu { PlacementTarget = placementTarget, Placement = PlacementMode.Bottom };
            foreach (var c in columns)
            {
                var entry = c;   // Each handler captures its own entry.
                var item = new MenuItem { IsCheckable = true, IsChecked = entry.GetVisible() };
                item.SetResourceReference(HeaderedItemsControl.HeaderProperty, entry.HeaderResourceKey);
                RoutedEventHandler updateVisibility = (_, _) =>
                {
                    bool visible = item.IsChecked;
                    entry.SetVisible(visible);
                    ThemeManager.SetSetting(SettingKey(settingsKey, entry.Key), visible ? "1" : "0");
                };
                item.Checked += updateVisibility;
                item.Unchecked += updateVisibility;
                menu.Items.Add(item);
            }
            placementTarget.ContextMenu = menu;
            // WPF does not always raise Closed: toggling a file-list column re-lays the header out
            // while the menu closes, and the popup goes away without the event. So the cleanup also
            // runs when IsOpen turns false, once, whichever arrives first.
            bool cleaned = false;
            var isOpen = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ContextMenu.IsOpenProperty, typeof(ContextMenu));
            EventHandler? openChanged = null;
            void CleanUp()
            {
                if (cleaned) return;
                cleaned = true;
                isOpen.RemoveValueChanged(menu, openChanged);
                if (ReferenceEquals(placementTarget.ContextMenu, menu)) placementTarget.ContextMenu = null;
                if (keyboard) placementTarget.Focus();
                else if (previousFocus is { IsVisible: true, IsEnabled: true, Focusable: true }) previousFocus.Focus();
                else previousRow?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
            // Deferred: IsOpen turns false before the popup hands keyboard focus back, so restoring
            // focus right away would be undone by the closing popup.
            openChanged = (_, _) =>
            {
                if (!menu.IsOpen)
                    menu.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(CleanUp));
            };
            menu.Closed += (_, _) => CleanUp();
            menu.IsOpen = true;
            isOpen.AddValueChanged(menu, openChanged);
        }

        /// <summary>
        /// DataGrid convenience: restores every column's persisted visibility up front, then
        /// wires a right-click on the grid's OWN header row that rebuilds and opens the menu
        /// anchored to whichever column header was actually clicked.
        /// </summary>
        internal static void AttachTo(DataGrid grid, string settingsKey,
            params (DataGridColumn Column, string Key, string HeaderResourceKey, bool DefaultVisible)[] columns)
        {
            var entries = BuildEntries(columns);
            RestoreVisibility(settingsKey, entries);
            grid.PreviewMouseRightButtonUp += (_, e) => HandleHeaderRightClick(e, settingsKey, entries);
            AttachKeyboard(grid, () => settingsKey, () => entries);
        }

        internal static void AttachKeyboard(DataGrid grid, Func<string> settingsKey, Func<Entry[]> entries)
        {
            var style = new Style(typeof(DataGridColumnHeader), grid.ColumnHeaderStyle);
            style.Setters.Add(new Setter(UIElement.FocusableProperty, true));
            style.Setters.Add(new Setter(Control.IsTabStopProperty, true));
            grid.ColumnHeaderStyle = style;
            grid.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Apps || Keyboard.Modifiers != ModifierKeys.None) return;
                var header = FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
                if (header != null && FindAncestor<DataGrid>(header) == grid) e.Handled = true;
            };
            grid.PreviewKeyUp += (_, e) =>
            {
                if (e.Key != Key.Apps || Keyboard.Modifiers != ModifierKeys.None) return;
                var header = FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
                if (header == null || FindAncestor<DataGrid>(header) != grid) return;
                ShowFor(header, settingsKey(), true, entries());
                e.Handled = true;
            };
        }

        /// <summary>
        /// Turns a DataGrid's own (column, key, header, default) tuples into Entry structs,
        /// without wiring RestoreVisibility or a right-click handler - split out of AttachTo so a
        /// control with more than one column SET for the same grid (Tools/ProcessListControl.cs,
        /// which swaps between a Processes column set and a Services one) can build both sets and
        /// wire its OWN single right-click handler that picks whichever set is current, instead of
        /// AttachTo silently wiring two independent handlers that would both fire on every
        /// right-click and open two menus at once.
        /// </summary>
        internal static Entry[] BuildEntries(
            params (DataGridColumn Column, string Key, string HeaderResourceKey, bool DefaultVisible)[] columns)
        {
            var entries = new Entry[columns.Length];
            for (int i = 0; i < columns.Length; i++)
            {
                var c = columns[i];
                entries[i] = new Entry(c.Key, c.HeaderResourceKey, c.DefaultVisible,
                    () => c.Column.Visibility == Visibility.Visible,
                    v => c.Column.Visibility = v ? Visibility.Visible : Visibility.Collapsed);
            }
            return entries;
        }

        /// <summary>
        /// The actual "right-click a header, open the toggle menu anchored to it" behavior,
        /// pulled out of AttachTo's inline lambda so a caller juggling more than one column set
        /// for the same grid (see BuildEntries) can call it directly with whichever set applies.
        /// </summary>
        internal static void HandleHeaderRightClick(MouseButtonEventArgs e, string settingsKey, Entry[] entries)
        {
            var header = FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
            if (header == null) return;   // right-click landed on a row, not the header - leave it alone
            ShowFor(header, settingsKey, false, entries);
            e.Handled = true;
        }

        private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T match) return match;
                d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
            }
            return null;
        }
    }
}
