using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using KillerFamily.Accessibility;
using KillerShell.Controls;

// Moving and minimizing the Performance Monitor's cards: the fourth part of
// PerformanceMonitorControl, beside the counters (PerformanceMonitorControl.cs), the dashboard
// (PerformanceMonitorLayout.cs) and the motion between samples (PerformanceMonitorMotion.cs).
//
// Every box of the top strip and every panel below it is a card. A card is picked up by its
// header line (a summary box by any of it) with the family's grab cursors, and dropped before or
// after another card of the same area; a marker in the gutter shows which. The panels flow two
// to a row whatever their order, and each keeps its own weight, so the CPU is still wider than
// whatever shares its row. Right-click minimizes a card to its header line or restores it, and
// resets the whole dashboard to the designed arrangement. The order and the minimized cards are
// saved as one setting (Services/DashboardLayout.cs), so they come back after a restart.
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

        private readonly Services.DashboardLayout _layout =
            Services.DashboardLayout.Parse(Services.ThemeManager.GetSetting(LayoutSetting));

        // In the designed order, and as shown.
        private readonly List<DashboardCard> _stripCards = [];
        private readonly List<DashboardCard> _panelCards = [];
        private List<DashboardCard> _stripShown = [];
        private List<DashboardCard> _panelsShown = [];

        private void ClearCards()
        {
            DetachPanelRows();
            _summaryStrip.Children.Clear();
            _stripCards.Clear();
            _panelCards.Clear();
            _stripShown = [];
            _panelsShown = [];
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
            _summaryStrip.Children.Clear();
            _stripShown = Shown(Services.DashboardLayout.Strip, _stripCards);
            foreach (var card in _stripShown)
            {
                ApplyFold(card);
                _summaryStrip.Children.Add(card.Frame);
            }
        }

        /// <summary>Two cards to a row, in the user's order. A card moved, added or taken away
        /// re-forms the rows; the cards themselves are moved into them, never rebuilt.</summary>
        private void ArrangePanels()
        {
            DetachPanelRows();
            _panelsShown = Shown(Services.DashboardLayout.Panels, _panelCards);
            for (int i = 0; i < _panelsShown.Count; i += 2)
            {
                var row = new Grid();
                int count = Math.Min(2, _panelsShown.Count - i);
                for (int c = 0; c < count; c++)
                {
                    var card = _panelsShown[i + c];
                    ApplyFold(card);
                    row.ColumnDefinitions.Add(new ColumnDefinition
                        { Width = new GridLength(count == 1 ? 1 : card.Weight, GridUnitType.Star) });
                    SetColumn(card.Frame, c);
                    row.Children.Add(card.Frame);
                }
                _panels.Children.Add(row);
            }
        }

        // A card can belong to one row at a time, so the old rows let go of them first.
        private void DetachPanelRows()
        {
            foreach (var row in _panels.Children.OfType<Grid>()) row.Children.Clear();
            _panels.Children.Clear();
        }

        private List<DashboardCard> Shown(string area, List<DashboardCard> cards)
        {
            var byId = cards.ToDictionary(card => card.Id, StringComparer.Ordinal);
            return _layout.Arrange(area, cards.Select(card => card.Id)).Select(id => byId[id]).ToList();
        }

        // ═══════════════════════════════════════════════════════════
        //  MINIMIZING
        // ═══════════════════════════════════════════════════════════
        /// <summary>A minimized card keeps its header line: the icon, the name and the live
        /// number. It sits at the top of its row instead of stretching to the card beside it.</summary>
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
        private bool _dropAfter;
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
            bool after = _dropAfter, dragging = _cardDragging;
            // Cleared first: releasing the capture raises LostMouseCapture, which lands back here.
            _pressedCard = null;
            _cardDragging = false;
            ShowDropMarker(null, false);
            if (card == null || !dragging) return;

            card.Frame.Opacity = 1;
            DragCursors.EndDrag();
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (!commit || target == null) return;

            var shown = (card.Area == Services.DashboardLayout.Strip ? _stripShown : _panelsShown).Select(c => c.Id).ToList();
            var order = _layout.Move(card.Area, shown, card.Id, target.Id, after);
            if (order.SequenceEqual(shown)) return;
            SaveLayout();
            if (card.Area == Services.DashboardLayout.Strip) ArrangeStrip();
            else ArrangePanels();
        }

        /// <summary>The card under the pointer, and which half of it: the left half drops before
        /// it, the right half after. Nothing is marked where the drop would change nothing.</summary>
        private void FindDropTarget(DashboardCard card, Point at)
        {
            DashboardCard? target = null;
            bool after = false;
            var shown = card.Area == Services.DashboardLayout.Strip ? _stripShown : _panelsShown;
            foreach (var other in shown)
            {
                if (!other.Frame.IsVisible || !other.Frame.IsDescendantOf(this)) continue;
                Rect bounds = other.Frame.TransformToAncestor(this).TransformBounds(new Rect(other.Frame.RenderSize));
                // The gutters belong to the cards on either side of them, so the marker does not
                // blink out between two cards.
                Thickness m = other.Frame.Margin;
                bounds = new Rect(bounds.Left - m.Left, bounds.Top - m.Top,
                    bounds.Width + m.Left + m.Right, bounds.Height + m.Top + m.Bottom);
                if (!bounds.Contains(at)) continue;
                if (other != card)
                {
                    after = at.X > bounds.Left + bounds.Width / 2;
                    var ids = shown.Select(c => c.Id).ToList();
                    if (!Services.DashboardLayout.Reordered(ids, card.Id, other.Id, after).SequenceEqual(ids)) target = other;
                }
                break;
            }
            ShowDropMarker(target, after);
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

        private void ShowDropMarker(DashboardCard? target, bool after)
        {
            if (_dropMarker != null && (_dropTarget != target || _dropAfter != after))
            {
                AdornerLayer.GetAdornerLayer(_dropMarker.AdornedElement)?.Remove(_dropMarker);
                _dropMarker = null;
            }
            _dropTarget = target;
            _dropAfter = after;
            if (target == null || _dropMarker != null) return;

            var layer = AdornerLayer.GetAdornerLayer(target.Frame);
            if (layer == null) return;
            var brush = TryFindResource("MonitorAccentBrush") as Brush ?? SystemColors.HighlightBrush;
            _dropMarker = new DropMarker(target.Frame, after, brush);
            layer.Add(_dropMarker);
        }

        /// <summary>An accent bar in the gutter on the side of the card the drop will land.</summary>
        private sealed class DropMarker : Adorner
        {
            private readonly bool _after;
            private readonly Brush _brush;

            internal DropMarker(UIElement card, bool after, Brush brush) : base(card)
            {
                _after = after;
                _brush = brush;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                const double width = 3;
                Size size = AdornedElement.RenderSize;
                double gutter = AdornedElement is FrameworkElement card
                    ? (_after ? card.Margin.Right : card.Margin.Left) : 0;
                // Centered in the card's margin, or just inside its edge where it has none (98SE).
                double x = gutter >= width
                    ? (_after ? size.Width + (gutter - width) / 2 : -(gutter + width) / 2)
                    : (_after ? size.Width - width : 0);
                drawingContext.DrawRoundedRectangle(_brush, null, new Rect(x, 0, width, size.Height), width / 2, width / 2);
            }
        }
    }
}
