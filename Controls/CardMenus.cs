using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KillerFamily.Controls;

namespace KillerShell.Controls
{
    /// <summary>
    /// The kit's card menu pieces (Controls.xaml CardMenu, CardSlot and the CardBuilder tile grid), put
    /// together in code for the app's right-click cards: file, tab, terminal, document, folder tree, saved
    /// place, the Tools cards and the small layout cards. Each card is an optional header and a grid of tiles.
    /// </summary>
    internal static class CardMenus
    {
        /// <summary>An empty card: the kit's CardMenu style on a fresh ContextMenu.</summary>
        internal static ContextMenu Card()
        {
            var menu = new ContextMenu();
            menu.SetResourceReference(FrameworkElement.StyleProperty, "CardMenu");
            return menu;
        }

        /// <summary>A slot row: a MenuItem that only hosts its header and is never highlighted.</summary>
        internal static MenuItem Slot()
        {
            var slot = new MenuItem();
            slot.SetResourceReference(FrameworkElement.StyleProperty, "CardSlot");
            return slot;
        }

        /// <summary>
        /// The card's header: an icon, the name of what the card is about in bold, and one dim line
        /// under it. The name is set on the element for the caller to put on its slot, because a
        /// panel has no automation peer of its own.
        /// </summary>
        internal static FrameworkElement Header(FrameworkElement? icon, string title, string meta)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            text.Children.Add(name);
            if (!string.IsNullOrEmpty(meta))
            {
                var detail = new TextBlock { Text = meta, FontSize = 11, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                detail.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
                text.Children.Add(detail);
            }

            var header = new DockPanel { Margin = new Thickness(4, 2, 4, 6) };
            if (icon != null)
            {
                // Added to, not replaced: a fallback dot centers itself in a full-size box with
                // its own margin, and losing that would pull its text 16 px left of an image's.
                var own = icon.Margin;
                icon.Margin = new Thickness(own.Left, own.Top, own.Right + 10, own.Bottom);
                icon.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(icon, Dock.Left);
                header.Children.Add(icon);
            }
            header.Children.Add(text);
            AutomationProperties.SetName(header, title);
            return header;
        }

        /// <summary>
        /// Put a control at the top right of a card header (the folder star). Docked before the text,
        /// which fills what is left, so a long name trims instead of running under it.
        /// </summary>
        internal static void AddTrailing(FrameworkElement header, FrameworkElement element)
        {
            if (header is not DockPanel dock) return;
            DockPanel.SetDock(element, Dock.Right);
            dock.Children.Insert(0, element);
        }

        /// <summary>Fill a header slot, naming the slot itself so a screen reader hears it.</summary>
        internal static void SetHeader(MenuItem slot, FrameworkElement? icon, string title, string meta)
        {
            slot.Header = Header(icon, title, meta);
            AutomationProperties.SetName(slot, title);
        }

        // ── Tile cards (KillerUI CardBuilder.TileGroups, KillerShot's ShotCards the reference) ──
        // 1. Three tiles across, each an MDL2 glyph over a short caption, its shortcut on a second line.
        // 2. Tiles run on in order, related ones side by side, so every row is full but the last.
        // 3. Most used first, destructive last; Delete on a card for one thing sits in the header.
        // 4. A tile that only sometimes applies stays in its place, dimmed, so the rows never shift.
        // 5. No text rows: everything is a tile, or a flyout tile for a list.
        // 6. A big card is customizable (CardEdit); a small card stays fixed.

        /// <summary>The tile card width for three tiles across (the menu adds its 22px halo on each side).</summary>
        internal const double TileWidth = 276;

        /// <summary>Tiles across a card row.</summary>
        internal const int TileColumns = 3;

        /// <summary>The tag on a card header's Delete button.</summary>
        internal const string HeaderDeleteTag = "carddelete";

        internal static string Loc(string key) => Application.Current?.TryFindResource(key) as string ?? key;

        /// <summary>A new tile card at the three-across width.</summary>
        internal static ContextMenu TileCard()
        {
            var menu = new ContextMenu();
            return Prepare(menu);
        }

        /// <summary>Gives a menu made elsewhere (XAML, or a control's own ContextMenu) the tile card look and width.</summary>
        internal static ContextMenu Prepare(ContextMenu menu)
        {
            menu.Items.Clear();
            menu.SetResourceReference(FrameworkElement.StyleProperty, "CardMenu");
            menu.MinWidth = menu.MaxWidth = TileWidth + 44;
            return menu;
        }

        /// <summary>
        /// A header slot for a tile card: the header element (Header above), and on a card for one thing that
        /// thing's Delete as an icon button at the right end.
        /// </summary>
        internal static MenuItem HeaderSlot(ContextMenu card, FrameworkElement header, string? deleteName = null,
            string? deleteKeys = null, Action? delete = null, bool deleteEnabled = true)
        {
            if (delete != null && deleteName != null)
            {
                var button = new Button
                {
                    Content = "\uE74D", Tag = HeaderDeleteTag, IsEnabled = deleteEnabled,
                    ToolTip = CardBuilder.Tip(deleteName, deleteKeys),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                    FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14,
                };
                button.SetResourceReference(FrameworkElement.StyleProperty, "ToolbarButton");
                AutomationProperties.SetName(button, deleteName);
                if (!string.IsNullOrEmpty(deleteKeys)) AutomationProperties.SetAcceleratorKey(button, deleteKeys);
                ToolTipService.SetShowOnDisabled(button, true);
                var run = delete;
                button.Click += (_, e) => { e.Handled = true; card.IsOpen = false; run(); };
                AddTrailing(header, button);
            }
            WatchKeyboardStart(card);
            var slot = CardBuilder.Slot(header, new Thickness(0));
            slot.Tag = "cardhead";
            AutomationProperties.SetName(slot, AutomationProperties.GetName(header));
            return slot;
        }

        private static readonly DependencyProperty WatchedProperty =
            DependencyProperty.RegisterAttached("Watched", typeof(bool), typeof(CardMenus), new PropertyMetadata(false));

        /// <summary>
        /// A card opened from the keyboard (the Apps key or Shift+F10) starts on its first tile, never on a header
        /// button: Enter on Delete would delete. A card that kept focus on itself sends the first arrow there too.
        /// </summary>
        internal static void WatchKeyboardStart(ContextMenu card)
        {
            if ((bool)card.GetValue(WatchedProperty)) return;
            card.SetValue(WatchedProperty, true);
            card.Opened += (_, _) => card.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (InputManager.Current.MostRecentInputDevice is not KeyboardDevice) return;
                var focused = Keyboard.FocusedElement;
                if (focused is Button { Tag: string tag } && tag == HeaderDeleteTag || ReferenceEquals(focused, card)
                    || focused is DependencyObject d && !IsInside(d, card))
                    FirstTile(card)?.Focus();
            }));
            card.PreviewKeyDown += (_, e) =>
            {
                if (!ReferenceEquals(Keyboard.FocusedElement, card)) return;
                if (e.Key is not (Key.Down or Key.Up or Key.Left or Key.Right or Key.Tab or Key.Home)) return;
                if (FirstTile(card) is { } first) { first.Focus(); e.Handled = true; }
            };
        }

        private static bool IsInside(DependencyObject node, DependencyObject root)
        {
            for (DependencyObject? at = node; at != null; at = LogicalTreeHelper.GetParent(at) ?? (at is Visual ? VisualTreeHelper.GetParent(at) : null))
                if (ReferenceEquals(at, root)) return true;
            return false;
        }

        /// <summary>A fixed tile grid for a small card. Empty groups are skipped.</summary>
        internal static MenuItem Tiles(ContextMenu card, params IEnumerable<CardTile>[] groups) => Build(card, null, groups);

        /// <summary>
        /// A customizable tile grid for a big card (the kit's CardEdit): a pencil in the corner opens edit mode, and
        /// tiles move between the card and its Hidden drawer. Locked tiles always show; spares start in the drawer
        /// (both named by tile Id), so the default card is full rows. The layout is kept as "Card.{name}.Hidden".
        /// </summary>
        internal static MenuItem EditableTiles(ContextMenu card, string name, string[] locked, string[] spares, params IEnumerable<CardTile>[] groups)
        {
            var edit = new CardEdit(
                () => Services.ThemeManager.GetSetting($"Card.{name}.Hidden") is { Length: > 0 } saved ? saved : null,
                value => Services.ThemeManager.SetSetting($"Card.{name}.Hidden", value ?? ""))
            {
                Locked = t => locked.Contains(t.Id ?? t.Glyph),
                DefaultHidden = t => spares.Contains(t.Id ?? t.Glyph),
                EditTip = Loc("Str_Card_Customize"), DoneTip = Loc("Str_Card_EditDone"), ResetTip = Loc("Str_Card_EditReset"),
                ResetLabel = Loc("Str_Card_EditResetShort"), HiddenLabel = Loc("Str_Card_Hidden"),
                HiddenHint = Loc("Str_Card_HiddenHint"), LockedTip = Loc("Str_Card_AlwaysShown"),
                MovedText = Loc("Str_Card_TileMoved"),
            };
            return Build(card, edit, groups);
        }

        private static MenuItem Build(ContextMenu card, CardEdit? edit, IEnumerable<CardTile>[] groups)
        {
            // The tiles run on as one list, so every row is full but the last.
            var tiles = groups.SelectMany(g => g).ToList();
            var strip = CardBuilder.TileGroups(card, Loc("Str_Card_Actions"), tiles.Count > 0 ? new[] { tiles } : Array.Empty<List<CardTile>>(), edit, TileColumns);
            var slot = CardBuilder.Slot(strip, new Thickness(0));
            slot.Tag = "cardtiles";
            AutomationProperties.SetName(slot, Loc("Str_Card_Actions"));
            NameTiles(strip, tiles);
            card.Opened -= RenameOnOpen;
            card.Opened += RenameOnOpen;
            card.SetValue(TileListProperty, tiles);
            return slot;
        }

        private static readonly DependencyProperty TileListProperty =
            DependencyProperty.RegisterAttached("TileList", typeof(List<CardTile>), typeof(CardMenus));

        // A customizable card rebuilds its tile buttons when a tile moves, so the names are put back on each opening.
        private static void RenameOnOpen(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu card && card.GetValue(TileListProperty) is List<CardTile> tiles)
                foreach (var slot in card.Items.OfType<MenuItem>())
                    if (slot.Header is DependencyObject root) NameTiles(root, tiles);
        }

        /// <summary>
        /// A tile with an Id carries it as its button's Uid and CommandParameter, the command name tests and the
        /// card's own code read. Matched by accessible name, which is the tile's caption.
        /// </summary>
        private static void NameTiles(DependencyObject root, List<CardTile> tiles)
        {
            foreach (var button in Descendants(root).OfType<Button>())
            {
                // The kit names a tile by its caption without a trailing ellipsis ("Open with...").
                string name = AutomationProperties.GetName(button);
                if (tiles.FirstOrDefault(t => Trimmed(t.Caption) == name) is { Id: { } id })
                {
                    button.Uid = id;
                    button.CommandParameter = id;
                }
            }
        }

        private static string Trimmed(string caption) => caption.TrimEnd('.', '…').TrimEnd();

        /// <summary>A tile that opens a list beside itself.</summary>
        internal static CardTile Flyout(string id, string glyph, string caption, string? shown, IEnumerable<object> rows, bool enabled = true)
        {
            var list = rows.ToList();
            return new CardTile(glyph, caption, null, null, false, enabled && list.Count > 0) { Flyout = list, Id = id, Shown = shown };
        }

        /// <summary>
        /// A flyout tile whose rows are made fresh each time its card opens, for a list that changes (installed
        /// shells, profiles). The kit builds a tile's flyout once from the list it was given, so the rows are swapped
        /// inside that same list, and inside the flyout itself once it exists.
        /// </summary>
        internal static CardTile LiveFlyout(ContextMenu card, string id, string glyph, string caption, string? keys, Action<MenuItem> fill)
        {
            var rows = new List<object>();
            void Refill()
            {
                var holder = new MenuItem();
                fill(holder);
                var fresh = holder.Items.Cast<object>().ToList();
                holder.Items.Clear();
                var owner = rows.OfType<FrameworkElement>().Select(row => row.Parent).OfType<ItemsControl>().FirstOrDefault();
                owner?.Items.Clear();
                rows.Clear();
                rows.AddRange(fresh);
                if (owner != null) foreach (var row in fresh) owner.Items.Add(row);
            }
            Refill();
            card.Opened += (_, _) => Refill();
            return new CardTile(glyph, caption, keys, null) { Flyout = rows, Id = id };
        }

        /// <summary>A built tile by its command name, or null when the card has none.</summary>
        internal static Button? TileButton(ItemsControl card, string id)
        {
            // A customized card rebuilds its tile buttons, and a caller may set state before the card opens,
            // so the names go back on first instead of waiting for the opening.
            if (card.GetValue(TileListProperty) is List<CardTile> tiles)
                foreach (var slot in card.Items.OfType<MenuItem>())
                    if (slot.Header is DependencyObject root) NameTiles(root, tiles);
            return Buttons(card).FirstOrDefault(button => button.CommandParameter as string == id);
        }

        /// <summary>
        /// Sets a built tile's state as its card opens, so a card built once still dims what does not apply now
        /// and fills an on/off tile while it is on.
        /// </summary>
        internal static void SetTile(ItemsControl card, string id, bool enabled, bool? on = null)
        {
            if (TileButton(card, id) is not { } button) return;
            button.IsEnabled = enabled;
            if (on is { } state) CardBuilder.ShowTileState(button, state);
        }

        /// <summary>A tile from string keys: its caption, the short caption under its glyph, and its shortcut.</summary>
        internal static CardTile Tile(string id, int glyph, string captionKey, string? shownKey, string? keys, Action run,
            bool enabled = true, bool? on = null)
            => new(((char)glyph).ToString(), Loc(captionKey), keys, run, false, enabled)
               { Id = id, Shown = shownKey == null ? null : Loc(shownKey), Checked = on };

        /// <summary>One flyout row: the command, its shortcut in the gesture column, and its glyph.</summary>
        internal static MenuItem FlyoutRow(string header, string? keys, string? glyph, RoutedEventHandler click, object? tag = null)
        {
            var row = new MenuItem { Header = header, InputGestureText = keys ?? string.Empty, Tag = tag };
            if (glyph != null)
            {
                var icon = new TextBlock { Text = glyph };
                icon.SetResourceReference(FrameworkElement.StyleProperty, "MenuGlyph");
                row.Icon = icon;
            }
            row.Click += click;
            return row;
        }

        /// <summary>Every tile and header button on a card, in order.</summary>
        internal static IEnumerable<Button> Buttons(ItemsControl card) =>
            card.Items.OfType<MenuItem>().SelectMany(item => Descendants(item.Header as DependencyObject).OfType<Button>());

        /// <summary>
        /// Every command a tile card offers by name: tile buttons carry it in CommandParameter, flyout rows in Tag.
        /// Flyout rows are found even while their flyout is shut, for the card's tests.
        /// </summary>
        internal static IEnumerable<FrameworkElement> Commands(ContextMenu card)
        {
            foreach (var button in Buttons(card))
                if (button.CommandParameter is string) yield return button;
            if (card.GetValue(TileListProperty) is List<CardTile> tiles)
                foreach (var tile in tiles)
                    if (tile.Flyout is { } rows)
                        foreach (var row in rows.OfType<MenuItem>())
                            if (row.Tag is string) yield return row;
        }

        /// <summary>The first tile that can be used.</summary>
        internal static Button? FirstTile(ItemsControl card) =>
            Buttons(card).FirstOrDefault(b => !Equals(b.Tag, HeaderDeleteTag) && b.IsEnabled && b.IsVisible);

        // The card's own controls: a customizable card's hidden measuring copy of its grid, its drawer (a popup)
        // and its edit pencil (a button with no caption) are not tiles.
        private static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
        {
            if (root == null || root is Popup || root is UIElement { Visibility: not Visibility.Visible }) yield break;
            if (root is Button { Tag: null } && AutomationProperties.GetName(root).Length == 0) yield break;
            yield return root;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (var d in Descendants(child)) yield return d;
        }
    }
}
