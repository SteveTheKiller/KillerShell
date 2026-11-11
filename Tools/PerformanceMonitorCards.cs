using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using KillerFamily.Accessibility;
using KillerShell.Controls;
// Not "Drop": inside a UIElement that name is the drag-and-drop event.
using CardDrop = KillerShell.Services.DashboardLayout.Drop;

// Moving and minimizing the Performance Monitor's cards: the fourth part of
// PerformanceMonitorControl, beside the counters (PerformanceMonitorControl.cs), the dashboard
// (PerformanceMonitorLayout.cs) and the motion between samples (PerformanceMonitorMotion.cs).
//
// Every box of the top strip and every panel below it is a card. A card is picked up by its
// header line (a summary box by any of it) with the family's grab cursors and dropped on another
// card of the same area: on its left half to go before that card's column, on its right half to
// go after it, and into the empty space at the bottom of a column to stack beneath the last card
// there. A marker in the gutter shows where it will land. The panels take turns down two columns
// that each flow on their own, so a short card leaves no gap under it, and a column is as wide as
// the heaviest card in it, so the CPU's column is still the wider one. Right-click minimizes a card to its header
// line or restores it, sets a panel to one column or across both, and resets the whole dashboard
// to the designed arrangement. All of it is
// saved as one setting (Services/DashboardLayout.cs), so it comes back after a restart.
namespace KillerShell.Tools
{
    internal sealed partial class PerformanceMonitorControl
    {
        private const string LayoutSetting = "PerformanceLayout";

        /// <summary>One card of the dashboard: the frame that moves, the parts it is picked up
        /// by, and what folds away when it is minimized.</summary>
        private sealed class DashboardCard
        {
            internal DashboardCard(string area, string id, Border frame, FrameworkElement[] handles,
                FrameworkElement? details, IReadOnlyList<MetricTile>? rows = null)
            {
                Area = area;
                Id = id;
                Frame = frame;
                Handles = handles;
                Details = details;
                Rows = rows ?? [];
            }

            internal readonly string Area;
            internal readonly string Id;
            internal readonly Border Frame;
            internal readonly FrameworkElement[] Handles;
            internal readonly FrameworkElement? Details;      // null for a device card, whose rows fold instead
            internal readonly IReadOnlyList<MetricTile> Rows;
            internal double Weight = 2;                       // its share of a row it shares
            internal bool Folded;
        }

        /// <summary>One column of the dashboard as shown: its cards, top to bottom, and the
        /// element that holds them (the card itself when it is alone).</summary>
        private sealed class CardSlot
        {
            internal CardSlot(List<DashboardCard> cards, FrameworkElement host)
            {
                Cards = cards;
                Host = host;
            }

            internal readonly List<DashboardCard> Cards;
            internal readonly FrameworkElement Host;
        }

        private Services.DashboardLayout _layout =
            Services.DashboardLayout.Parse(Services.ThemeManager.GetSetting(LayoutSetting));

        // In the designed order, and as shown.
        private readonly List<DashboardCard> _stripCards = [];
        private readonly List<DashboardCard> _panelCards = [];
        private List<CardSlot> _stripSlots = [];
        private List<CardSlot> _panelSlots = [];

        private void ClearCards()
        {
            DetachSlots(_summaryStrip.Children);
            DetachPanelRows();
            _stripCards.Clear();
            _panelCards.Clear();
            _stripSlots = [];
            _panelSlots = [];
        }

        private void AddStripCard(DashboardCard card)
        {
            _stripCards.Add(card);
            WireCard(card);
        }

        private void AddPanelCard(DashboardCard card)
        {
            _panelCards.Add(card);
            WireCard(card);
        }

        /// <summary>Takes a card out of the panels; ArrangePanels then closes the gap.</summary>
        private bool RemovePanelCard(DashboardCard card) => _panelCards.Remove(card);

        /// <summary>The id itself, or the id numbered from #2 when a card already has it: two
        /// adapters of the same model are told apart by their order.</summary>
        private string UniquePanelId(string id)
        {
            string unique = id;
            for (int n = 2; _panelCards.Any(card => card.Id == unique); n++) unique = id + "#" + n;
            return unique;
        }

        private void WireCard(DashboardCard card)
        {
            foreach (var handle in card.Handles)
            {
                handle.Cursor = DragCursors.Open;
                handle.MouseLeftButtonDown += (_, e) =>
                {
                    _pressedCard = card;
                    _pressedAt = e.GetPosition(this);
                };
            }
            // A double-click anywhere on a card minimizes or restores it, as on a window's title bar.
            card.Frame.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2) return;
                _pressedCard = null;   // the second click of a double-click never starts a drag
                e.Handled = true;
                SetCardFolded(card, !card.Folded);
            };
            // The menu opens from the mouse and from the keyboard (Shift+F10, the Menu key), as the
            // other tool tabs' menus do (ProcessListControl). A card takes focus, Enter or Space
            // minimizes or restores it, and a screen reader hears its title.
            card.Frame.Focusable = true;
            card.Frame.ContextMenuOpening += (_, e) =>
            {
                e.Handled = true;
                ShowCardMenu(card);
            };
            card.Frame.KeyDown += (_, e) =>
            {
                if (e.Key is not (Key.Enter or Key.Space) || Keyboard.Modifiers != ModifierKeys.None) return;
                e.Handled = true;
                SetCardFolded(card, !card.Folded);
            };
            card.Frame.Loaded += (_, _) =>
            {
                if (FirstText(card.Handles[0]) is { } title)
                    System.Windows.Automation.AutomationProperties.SetName(card.Frame, title);
            };
        }

        /// <summary>The first non-empty text in a card's header: its name, for a screen reader.</summary>
        private static string? FirstText(DependencyObject root)
        {
            if (root is TextBlock { Text: { Length: > 0 } text }) return text;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (FirstText(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
            return null;
        }

        // ═══════════════════════════════════════════════════════════
        //  ARRANGING
        // ═══════════════════════════════════════════════════════════
        private void ArrangeCards()
        {
            ArrangeStrip();
            ArrangePanels();
        }

        private void ArrangeStrip()
        {
            DetachSlots(_summaryStrip.Children);
            _stripSlots = Slots(Services.DashboardLayout.Strip, _stripCards);
            foreach (var slot in _stripSlots) _summaryStrip.Children.Add(slot.Host);
        }

        /// <summary>Two columns that each flow on their own, in the user's order, so a short card
        /// leaves no gap under it: the next card in its column moves up to meet it. A card moved,
        /// added or taken away re-forms the bands; the cards themselves are moved into them, never
        /// rebuilt. A card set to two columns spans a band of its own. A column is as wide as the
        /// heaviest card in it, so the CPU's column is still the wider one.</summary>
        private void ArrangePanels()
        {
            DetachPanelRows();
            _panelSlots = Slots(Services.DashboardLayout.Panels, _panelCards);
            foreach (var columns in Services.DashboardLayout.Bands(_panelSlots, IsWide))
            {
                var band = new Grid();
                if (columns.Length == 1)
                {
                    band.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    band.Children.Add(columns[0][0].Host);
                    _panels.Children.Add(band);
                    continue;
                }
                for (int c = 0; c < columns.Length; c++)
                {
                    // An empty right column beside a lone card is weighted like any other card.
                    double weight = columns[c].Count == 0 ? 2 : columns[c].SelectMany(slot => slot.Cards).Max(card => card.Weight);
                    band.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
                    // Stretched to the band's height, so the room under its last card belongs to
                    // the column and a card can be dropped into it (FindDropTarget).
                    var column = new StackPanel { Orientation = Orientation.Vertical, Background = Brushes.Transparent };
                    foreach (var slot in columns[c]) column.Children.Add(slot.Host);
                    SetColumn(column, c);
                    band.Children.Add(column);
                }
                _panels.Children.Add(band);
            }
        }

        private bool IsWide(CardSlot slot) => slot.Cards.Any(card => _layout.IsWide(card.Area, card.Id));

        /// <summary>
        /// The user's slots for these cards, each with the element that holds it. A card alone is
        /// its own host. A stack is a grid that gives every card its own height, top to bottom.
        /// </summary>
        private List<CardSlot> Slots(string area, List<DashboardCard> cards)
        {
            var byId = cards.ToDictionary(card => card.Id, StringComparer.Ordinal);
            var slots = new List<CardSlot>();
            foreach (var ids in _layout.Slots(area, cards.Select(card => card.Id)))
            {
                var stack = ids.Select(id => byId[id]).ToList();
                foreach (var card in stack)
                {
                    ApplyFold(card);
                    SetRow(card.Frame, 0);
                    SetColumn(card.Frame, 0);
                }
                if (stack.Count == 1)
                {
                    slots.Add(new CardSlot(stack, stack[0].Frame));
                    continue;
                }

                var host = new Grid();
                for (int i = 0; i < stack.Count; i++)
                {
                    host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    SetRow(stack[i].Frame, i);
                    host.Children.Add(stack[i].Frame);
                }
                slots.Add(new CardSlot(stack, host));
            }
            return slots;
        }

        // A card can belong to one parent at a time, so the old bands, columns and stacks let go
        // of them first. Every Panel under _panels is one of those; a card itself is a Border.
        private void DetachPanelRows() => DetachSlots(_panels.Children);

        private static void DetachSlots(UIElementCollection hosts)
        {
            foreach (var holder in hosts.OfType<Panel>()) DetachSlots(holder.Children);
            hosts.Clear();
        }

        private List<CardSlot> SlotsOf(string area)
            => area == Services.DashboardLayout.Strip ? _stripSlots : _panelSlots;

        private static List<List<string>> Ids(List<CardSlot> slots)
            => slots.Select(slot => slot.Cards.Select(card => card.Id).ToList()).ToList();

        // ═══════════════════════════════════════════════════════════
        //  MINIMIZING
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// A minimized card keeps its header line: the icon, the name and the live number. A panel
        /// keeps its own height either way rather than stretching to the column beside it, and the
        /// next card in its column moves up to meet it. The top strip's open boxes still share one
        /// height, so the strip reads as one row.
        /// </summary>
        private void ApplyFold(DashboardCard card)
        {
            bool folded = _layout.IsFolded(card.Area, card.Id);
            card.Folded = folded;
            card.Frame.VerticalAlignment = folded || card.Area == Services.DashboardLayout.Panels
                ? VerticalAlignment.Top : VerticalAlignment.Stretch;
            card.Details?.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
            foreach (var row in card.Rows)
            {
                row.Folded = folded;
                ApplyRowVisibility(row);   // PerformanceMonitorLayout.cs
            }
        }

        private void SetCardFolded(DashboardCard card, bool folded)
        {
            ReloadLayout();
            _layout.SetFolded(card.Area, card.Id, folded);
            SaveLayout();
            ApplyFold(card);
        }

        private void SetCardWide(DashboardCard card, bool wide)
        {
            ReloadLayout();
            _layout.SetWide(card.Area, card.Id, wide);
            SaveLayout();
            ArrangePanels();
        }

        private void ResetLayout()
        {
            _layout.Reset();
            SaveLayout();
            ArrangeCards();
        }

        /// <summary>
        /// Each window has its own Performance tab and one saved layout between them: a change
        /// starts from what is saved, so it does not write back an older copy over a change made
        /// in another window since.
        /// </summary>
        private void ReloadLayout() => _layout = Services.DashboardLayout.Parse(Services.ThemeManager.GetSetting(LayoutSetting));

        private void SaveLayout() => Services.ThemeManager.SetSetting(LayoutSetting, _layout.Serialize());

        private void ShowCardMenu(DashboardCard card)
        {
            var menu = new ContextMenu { PlacementTarget = card.Frame };
            // E921 and E923: the window's own Minimize and Restore glyphs (MainWindow.xaml,
            // Chrome.cs), because these do the same to a card.
            AddCardMenuItem(menu, card.Folded ? "Str_Menu_Restore" : "Str_Menu_Minimize",
                card.Folded ? 0xE923 : 0xE921, () => SetCardFolded(card, !card.Folded));
            menu.Items.Add(new Separator());
            // The top strip is always one row, so only the panels below it choose a width.
            if (card.Area == Services.DashboardLayout.Panels)
            {
                bool wide = _layout.IsWide(card.Area, card.Id);
                AddWidthMenuItem(menu, "Str_Perf_OneColumn", OneColumnIcon, !wide, () => SetCardWide(card, false));
                AddWidthMenuItem(menu, "Str_Perf_TwoColumns", TwoColumnsIcon, wide, () => SetCardWide(card, true));
                menu.Items.Add(new Separator());
            }
            // E777: the app's "start over" glyph, as Processes' Restart uses it.
            AddCardMenuItem(menu, "Str_Perf_ResetLayout", 0xE777, ResetLayout, enabled: !_layout.IsDefault);
            menu.IsOpen = true;
        }

        // The icon-in-a-Viewbox row every tool tab's context menu is built from
        // (ProcessListControl.AddMenuItem). Neither action has a shortcut to show.
        private static void AddCardMenuItem(ContextMenu menu, string headerKey, int glyph, Action click, bool enabled = true)
        {
            var item = new MenuItem { IsEnabled = enabled };
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, headerKey);
            var icon = new Glyph { Text = ((char)glyph).ToString() };
            icon.SetResourceReference(FrameworkElement.StyleProperty, "MenuGlyph");
            item.Icon = new Viewbox { Width = 14, Height = 14, Stretch = Stretch.Uniform, Child = icon };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }

        // Outlines in a 14px box, drawn like the pane layout menu's (Shell/PaneLayoutMenu.cs): a
        // card in one half of the row, and a card across all of it.
        private const string OneColumnIcon = "M0.5,0.5 H6.5 V13.5 H0.5 Z";
        private const string TwoColumnsIcon = "M0.5,0.5 H13.5 V13.5 H0.5 Z";

        /// <summary>One of the pair that sets a card's width. The checkmark takes the icon's
        /// place in the family template's reserved gutter while it is the card's width.</summary>
        private static void AddWidthMenuItem(ContextMenu menu, string headerKey, string geometry, bool chosen, Action click)
        {
            var icon = new System.Windows.Shapes.Path { Width = 14, Height = 14, Stretch = Stretch.None,
                StrokeThickness = 1, Data = Geometry.Parse(geometry) };
            icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(MenuItem), 1) });
            var item = new MenuItem { Icon = icon, IsCheckable = true, IsChecked = chosen };
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, headerKey);
            item.Click += (_, _) => { if (!chosen) click(); };
            menu.Items.Add(item);
        }

        // ═══════════════════════════════════════════════════════════
        //  DRAGGING
        // ═══════════════════════════════════════════════════════════
        private DashboardCard? _pressedCard;   // the button went down on its handle
        private Point _pressedAt;
        private bool _cardDragging;
        private DashboardCard? _dropTarget;
        private CardDrop _drop;
        private DropMarker? _dropMarker;

        /// <summary>The drag runs on the control rather than the card: a card is moved between
        /// rows when it is dropped, and a capture held by the card would be lost with it.</summary>
        private void WireCardDrag()
        {
            PreviewMouseMove += CardDrag_Move;
            PreviewMouseLeftButtonUp += CardDrag_Up;
            LostMouseCapture += (_, _) => EndCardDrag(commit: false);
        }

        private void CardDrag_Move(object sender, MouseEventArgs e)
        {
            var card = _pressedCard;
            if (card == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndCardDrag(commit: false); return; }

            Point at = e.GetPosition(this);
            if (!_cardDragging)
            {
                // A click on a header is not a drag until the pointer has really moved.
                if (Math.Abs(at.X - _pressedAt.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(at.Y - _pressedAt.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                if (!CaptureMouse()) { _pressedCard = null; return; }
                _cardDragging = true;
                DragCursors.BeginDrag();
                card.Frame.Opacity = 0.55;
            }

            if (card.Area == Services.DashboardLayout.Panels) ScrollWhileDragging(e.GetPosition(_panelsScroller));
            FindDropTarget(card, at);
        }

        private void CardDrag_Up(object sender, MouseButtonEventArgs e)
        {
            if (_pressedCard == null) return;
            if (!_cardDragging) { _pressedCard = null; return; }
            e.Handled = true;
            EndCardDrag(commit: true);
        }

        private void EndCardDrag(bool commit)
        {
            var card = _pressedCard;
            var target = _dropTarget;
            var drop = _drop;
            bool dragging = _cardDragging;
            // Cleared first: releasing the capture raises LostMouseCapture, which lands back here.
            _pressedCard = null;
            _cardDragging = false;
            ShowDropMarker(null, CardDrop.Before);
            if (card == null || !dragging) return;

            card.Frame.Opacity = 1;
            DragCursors.EndDrag();
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (!commit || target == null) return;

            var shown = Ids(SlotsOf(card.Area));
            ReloadLayout();
            var slots = _layout.Move(card.Area, shown, card.Id, target.Id, drop);
            if (Services.DashboardLayout.Same(slots, shown)) return;
            SaveLayout();
            if (card.Area == Services.DashboardLayout.Strip) ArrangeStrip();
            else ArrangePanels();
        }

        /// <summary>
        /// Where the carried card would land. Over a card, its left half drops before that card's
        /// column and its right half after it. Below the last card of a column, in the room a
        /// shorter card leaves, it stacks at the bottom of that column. Nothing is marked where
        /// the drop would change nothing.
        /// </summary>
        private void FindDropTarget(DashboardCard card, Point at)
        {
            DashboardCard? target = null;
            var drop = CardDrop.Before;
            var slots = SlotsOf(card.Area);
            bool overCard = false;

            foreach (var other in slots.SelectMany(slot => slot.Cards))
            {
                if (BoundsOf(other.Frame, withMargin: true) is not { } bounds || !bounds.Contains(at)) continue;
                overCard = true;
                if (other != card)
                {
                    target = other;
                    drop = at.X > bounds.Left + bounds.Width / 2 ? CardDrop.After : CardDrop.Before;
                }
                break;
            }

            if (!overCard)
            {
                // From the end, so the room at the bottom of a panel column goes to the last slot
                // in that column, whose bounds are the only ones reaching it first.
                for (int i = slots.Count - 1; i >= 0; i--)
                {
                    var slot = slots[i];
                    var last = slot.Cards[^1];
                    if (SlotBounds(slot) is not { } column || !column.Contains(at)) continue;
                    if (BoundsOf(last.Frame, withMargin: true) is { } lastBounds && at.Y > lastBounds.Bottom)
                    {
                        target = last;
                        drop = CardDrop.Below;
                    }
                    break;
                }
            }

            if (target != null)
            {
                var ids = Ids(slots);
                if (Services.DashboardLayout.Same(Services.DashboardLayout.Rearranged(ids, card.Id, target.Id, drop), ids))
                    target = null;
            }
            ShowDropMarker(target, drop);
        }

        /// <summary>A card's bounds in this control, with the gutters around it counted as its
        /// own so the marker does not blink out between two cards.</summary>
        private Rect? BoundsOf(FrameworkElement element, bool withMargin)
        {
            if (!element.IsVisible || !element.IsDescendantOf(this)) return null;
            Rect bounds = element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize));
            if (!withMargin) return bounds;
            Thickness m = element.Margin;
            return new Rect(bounds.Left - m.Left, bounds.Top - m.Top,
                bounds.Width + m.Left + m.Right, bounds.Height + m.Top + m.Bottom);
        }

        /// <summary>The room a slot was given, in this control. In a panel column that is
        /// everything from the slot's top to the bottom of the column, gap and all.</summary>
        private Rect? SlotBounds(CardSlot slot)
        {
            if (!slot.Host.IsVisible || VisualTreeHelper.GetParent(slot.Host) is not Visual parent || !parent.IsDescendantOf(this))
                return null;
            Rect room = LayoutInformation.GetLayoutSlot(slot.Host);
            if (parent is StackPanel { Orientation: Orientation.Vertical } column)
                room = new Rect(room.Left, room.Top, room.Width, Math.Max(room.Height, column.ActualHeight - room.Top));
            return parent.TransformToAncestor(this).TransformBounds(room);
        }

        // Near the top or bottom of the panels, a carried card scrolls them, so a card can be
        // taken to a row that is out of view.
        private void ScrollWhileDragging(Point inScroller)
        {
            const double edge = 28, step = 14;
            if (inScroller.Y < edge) _panelsScroller.ScrollToVerticalOffset(_panelsScroller.VerticalOffset - step);
            else if (inScroller.Y > _panelsScroller.ActualHeight - edge)
                _panelsScroller.ScrollToVerticalOffset(_panelsScroller.VerticalOffset + step);
        }

        private void ShowDropMarker(DashboardCard? target, CardDrop drop)
        {
            if (_dropMarker != null && (_dropTarget != target || _drop != drop))
            {
                AdornerLayer.GetAdornerLayer(_dropMarker.AdornedElement)?.Remove(_dropMarker);
                _dropMarker = null;
            }
            _dropTarget = target;
            _drop = drop;
            if (target == null || _dropMarker != null) return;

            var layer = AdornerLayer.GetAdornerLayer(target.Frame);
            if (layer == null) return;
            var brush = TryFindResource("MonitorAccentBrush") as Brush ?? SystemColors.HighlightBrush;
            _dropMarker = new DropMarker(target.Frame, drop, brush);
            layer.Add(_dropMarker);
        }

        /// <summary>An accent bar in the gutter on the side of the card the drop will land: down
        /// its left or right edge, or along its bottom for a card stacked beneath it.</summary>
        private sealed class DropMarker : Adorner
        {
            private readonly CardDrop _drop;
            private readonly Brush _brush;

            internal DropMarker(UIElement card, CardDrop drop, Brush brush) : base(card)
            {
                _drop = drop;
                _brush = brush;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                const double thickness = 3;
                Size size = AdornedElement.RenderSize;
                Thickness margin = AdornedElement is FrameworkElement card ? card.Margin : new Thickness(0);
                Rect bar;
                if (_drop == CardDrop.Below)
                {
                    // Centered in the card's bottom margin, or just inside its edge where it has none (98SE).
                    double y = margin.Bottom >= thickness ? size.Height + (margin.Bottom - thickness) / 2 : size.Height - thickness;
                    bar = new Rect(0, y, size.Width, thickness);
                }
                else
                {
                    double gutter = _drop == CardDrop.After ? margin.Right : margin.Left;
                    double x = gutter >= thickness
                        ? (_drop == CardDrop.After ? size.Width + (gutter - thickness) / 2 : -(gutter + thickness) / 2)
                        : (_drop == CardDrop.After ? size.Width - thickness : 0);
                    bar = new Rect(x, 0, thickness, size.Height);
                }
                drawingContext.DrawRoundedRectangle(_brush, null, bar, thickness / 2, thickness / 2);
            }
        }
    }
}
