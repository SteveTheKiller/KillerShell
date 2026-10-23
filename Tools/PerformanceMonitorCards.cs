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
// go after it, and into the empty space under a minimized card to stack beneath it, so the room
// a minimized card leaves can be filled. A marker in the gutter shows where it will land. The
// panels flow two columns to a row whatever their order, and each keeps its own weight, so the
// CPU is still wider than whatever shares its row. Right-click minimizes a card to its header
// line or restores it, and resets the whole dashboard to the designed arrangement. All of it is
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

        private readonly Services.DashboardLayout _layout =
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
            card.Frame.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowCardMenu(card);
            };
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

        /// <summary>Two columns to a row, in the user's order. A card moved, added or taken away
        /// re-forms the rows; the cards themselves are moved into them, never rebuilt.</summary>
        private void ArrangePanels()
        {
            DetachPanelRows();
            _panelSlots = Slots(Services.DashboardLayout.Panels, _panelCards);
            foreach (var pair in Services.DashboardLayout.Rows(_panelSlots))
            {
                var row = new Grid();
                for (int c = 0; c < pair.Length; c++)
                {
                    double weight = pair.Length == 1 ? 1 : pair[c].Cards.Max(card => card.Weight);
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
                    SetColumn(pair[c].Host, c);
                    row.Children.Add(pair[c].Host);
                }
                _panels.Children.Add(row);
            }
        }

        /// <summary>
        /// The user's slots for these cards, each with the element that holds it. A card alone is
        /// its own host. A stack is a grid that gives every card its own height and the last one
        /// whatever the row has left, so a full card at the bottom of a stack still ends level
        /// with the column beside it and a minimized one leaves its gap below.
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
                    host.RowDefinitions.Add(new RowDefinition
                        { Height = i < stack.Count - 1 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
                    SetRow(stack[i].Frame, i);
                    host.Children.Add(stack[i].Frame);
                }
                slots.Add(new CardSlot(stack, host));
            }
            return slots;
        }

        // A card can belong to one parent at a time, so the old rows and stacks let go of them first.
        private void DetachPanelRows()
        {
            foreach (var row in _panels.Children.OfType<Grid>()) DetachSlots(row.Children);
            _panels.Children.Clear();
        }

        private static void DetachSlots(UIElementCollection hosts)
        {
            foreach (var stack in hosts.OfType<Grid>()) stack.Children.Clear();
            hosts.Clear();
        }

        private List<CardSlot> SlotsOf(string area)
            => area == Services.DashboardLayout.Strip ? _stripSlots : _panelSlots;

        private static List<List<string>> Ids(List<CardSlot> slots)
            => slots.Select(slot => slot.Cards.Select(card => card.Id).ToList()).ToList();

        // ═══════════════════════════════════════════════════════════
        //  MINIMIZING
        // ═══════════════════════════════════════════════════════════
        /// <summary>A minimized card keeps its header line: the icon, the name and the live
        /// number. It sits at the top of its column instead of stretching to the card beside it,
        /// which leaves room under it that another card can be dropped into.</summary>
        private void ApplyFold(DashboardCard card)
        {
            bool folded = _layout.IsFolded(card.Area, card.Id);
            card.Folded = folded;
            card.Frame.VerticalAlignment = folded ? VerticalAlignment.Top : VerticalAlignment.Stretch;
            card.Details?.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
            foreach (var row in card.Rows)
            {
                row.Folded = folded;
                ApplyRowVisibility(row);   // PerformanceMonitorLayout.cs
            }
        }

        private void SetCardFolded(DashboardCard card, bool folded)
        {
            _layout.SetFolded(card.Area, card.Id, folded);
            SaveLayout();
            ApplyFold(card);
        }

        private void ResetLayout()
        {
            _layout.Reset();
            SaveLayout();
            ArrangeCards();
        }

        private void SaveLayout() => Services.ThemeManager.SetSetting(LayoutSetting, _layout.Serialize());

        private void ShowCardMenu(DashboardCard card)
        {
            var menu = new ContextMenu { PlacementTarget = card.Frame };
            // E921 and E923: the window's own Minimize and Restore glyphs (MainWindow.xaml,
            // Chrome.cs), because these do the same to a card.
            AddCardMenuItem(menu, card.Folded ? "Str_Menu_Restore" : "Str_Menu_Minimize",
                card.Folded ? 0xE923 : 0xE921, () => SetCardFolded(card, !card.Folded));
            menu.Items.Add(new Separator());
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
            var slots = _layout.Move(card.Area, shown, card.Id, target.Id, drop);
            if (Services.DashboardLayout.Same(slots, shown)) return;
            SaveLayout();
            if (card.Area == Services.DashboardLayout.Strip) ArrangeStrip();
            else ArrangePanels();
        }

        /// <summary>
        /// Where the carried card would land. Over a card, its left half drops before that card's
        /// column and its right half after it. Below the last card of a column, in the room a
        /// minimized card leaves, it stacks at the bottom of that column. Nothing is marked where
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
                foreach (var slot in slots)
                {
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

        /// <summary>The whole column a slot was given, gap and all, in this control.</summary>
        private Rect? SlotBounds(CardSlot slot)
        {
            if (!slot.Host.IsVisible || VisualTreeHelper.GetParent(slot.Host) is not Visual parent || !parent.IsDescendantOf(this))
                return null;
            return parent.TransformToAncestor(this).TransformBounds(LayoutInformation.GetLayoutSlot(slot.Host));
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
