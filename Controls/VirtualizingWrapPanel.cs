using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace KillerShell
{
    // A WrapPanel that virtualizes. WPF ships VirtualizingStackPanel and a plain WrapPanel, and
    // there is no combination of the two: put an ItemsControl in a WrapPanel and every item is
    // realized, so the icon view of a 100k-result search would build 100k tiles up front and die.
    // The results list routinely reaches that size, which is why this exists rather than the
    // WrapPanel the folder picker gets away with (that one lists a single directory).
    //
    // The simplifying assumption is uniform tile size: every item occupies exactly
    // ItemWidth x ItemHeight, like Explorer's icon views. That turns "which items are visible"
    // into arithmetic instead of a layout pass, which is the whole trick - the panel never has to
    // measure an unrealized item to know where it lands.
    //
    // Scrolling is pixel-based (IScrollInfo) and runs along one axis only. Horizontal orientation
    // (icons view) fills rows left to right, wraps to the viewport width and scrolls vertically.
    // Vertical orientation (Compact List, Explorer's List) fills columns top to bottom, wraps to
    // the viewport height and scrolls horizontally, so a folder of any size only realizes the
    // columns on screen. The mouse wheel follows the scrolling axis in both.
    //
    // Container recycling is supported and is what the results list uses
    // (VirtualizingPanel.VirtualizationMode="Recycling"): the generator hands back an existing
    // container instead of building one, and the only extra work here is re-parenting it to the
    // right child slot when it comes back at a different position.
    public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
    {
        // ── Tile size ────────────────────────────────────────────
        public static readonly DependencyProperty ItemWidthProperty =
            DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
                new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty ItemHeightProperty =
            DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
                new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double ItemWidth
        {
            get => (double)GetValue(ItemWidthProperty);
            set => SetValue(ItemWidthProperty, value);
        }

        public double ItemHeight
        {
            get => (double)GetValue(ItemHeightProperty);
            set => SetValue(ItemHeightProperty, value);
        }

        public static readonly DependencyProperty OrientationProperty =
            DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(VirtualizingWrapPanel),
                new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public Orientation Orientation
        {
            get => (Orientation)GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        private bool IsVertical => Orientation == Orientation.Vertical;

        // ── Layout arithmetic ────────────────────────────────────
        // A "line" is a row in horizontal orientation and a column in vertical orientation;
        // _perLine is how many items fit across one before it wraps.
        private int    _perLine   = 1;
        private int    _itemCount;
        private double _pixelStep = 16;   // one wheel/line unit, recomputed from the item size

        private double LineSize  => Math.Max(1, IsVertical ? ItemWidth : ItemHeight);
        private double ScrollPos => IsVertical ? _offset.X : _offset.Y;
        private double ViewSpan  => IsVertical ? _viewport.Width : _viewport.Height;

        private int PerLineFor(Size available)
        {
            double item  = IsVertical ? ItemHeight : ItemWidth;
            double space = IsVertical ? available.Height : available.Width;
            if (item <= 0 || double.IsInfinity(space) || space <= 0) return 1;
            return Math.Max(1, (int)Math.Floor(space / item));
        }

        private int FirstVisibleIndex()
        {
            int line = (int)Math.Floor(ScrollPos / LineSize);
            return Math.Max(0, line * _perLine);
        }

        private int LastVisibleIndex()
        {
            int lastLine = (int)Math.Floor((ScrollPos + ViewSpan - 0.1) / LineSize);
            // One line of overscan keeps a wheel notch from exposing blank tiles before the next
            // measure pass lands.
            return Math.Min(_itemCount - 1, ((lastLine + 1) * _perLine) + _perLine - 1);
        }

        // ── Measure ──────────────────────────────────────────────
        protected override Size MeasureOverride(Size availableSize)
        {
            // Touching InternalChildren is what instantiates the ItemContainerGenerator. It has to
            // happen before ItemContainerGenerator is used, on every pass, not just the first.
            var children  = InternalChildren;
            var itemsCtrl = ItemsControl.GetItemsOwner(this);
            if (itemsCtrl == null) return new Size(0, 0);

            _itemCount = itemsCtrl.Items.Count;
            _perLine   = PerLineFor(availableSize);
            _pixelStep = Math.Max(1, LineSize / 3);

            int lines = _perLine > 0 ? (int)Math.Ceiling((double)_itemCount / _perLine) : 0;

            var extent = IsVertical
                ? new Size(lines * ItemWidth, _perLine * ItemHeight)
                : new Size(_perLine * ItemWidth, lines * ItemHeight);
            var viewport = new Size(
                double.IsInfinity(availableSize.Width)  ? extent.Width  : availableSize.Width,
                double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height);

            UpdateScrollInfo(extent, viewport);

            if (_itemCount == 0)
            {
                RemoveInternalChildRange(0, children.Count);
                return new Size(0, 0);
            }

            int first = FirstVisibleIndex();
            int last  = Math.Min(LastVisibleIndex(), _itemCount - 1);

            RealizeRange(first, last);
            CleanUpOutside(first, last);

            var tile = new Size(ItemWidth, ItemHeight);
            foreach (UIElement child in InternalChildren) child.Measure(tile);

            return new Size(
                double.IsInfinity(availableSize.Width)  ? extent.Width  : availableSize.Width,
                double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height);
        }

        // Generates containers for [first, last], reusing whatever the generator hands back.
        private void RealizeRange(int first, int last)
        {
            var generator = ItemContainerGenerator;
            var startPos  = generator.GeneratorPositionFromIndex(first);

            // Offset 0 means the position IS a realized container, so we insert AT its child index;
            // anything else means we are between containers and the new one goes after.
            int childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;

            using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
            {
                for (int i = first; i <= last; i++, childIndex++)
                {
                    if (generator.GenerateNext(out bool newlyRealized) is not UIElement child) break;

                    if (newlyRealized)
                    {
                        InsertOrAdd(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    else
                    {
                        // Recycling can hand back a container that is already a child. If it is
                        // sitting in the wrong slot, move it rather than adding a duplicate; if it
                        // is not a child at all, it still needs preparing.
                        int existing = InternalChildren.IndexOf(child);
                        if (existing < 0)
                        {
                            InsertOrAdd(childIndex, child);
                            generator.PrepareItemContainer(child);
                        }
                        else if (existing != childIndex)
                        {
                            RemoveInternalChildRange(existing, 1);
                            InsertOrAdd(childIndex, child);
                        }
                    }
                }
            }
        }

        private void InsertOrAdd(int index, UIElement child)
        {
            if (index >= InternalChildren.Count) AddInternalChild(child);
            else                                 InsertInternalChild(index, child);
        }

        // Hands back every container whose item has scrolled out of range. Walking backwards keeps
        // the child indices stable as entries are removed.
        private void CleanUpOutside(int first, int last)
        {
            var generator = ItemContainerGenerator;
            for (int childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
            {
                var pos       = new GeneratorPosition(childIndex, 0);
                int itemIndex = generator.IndexFromGeneratorPosition(pos);
                if (itemIndex < 0) continue;
                if (itemIndex < first || itemIndex > last)
                {
                    generator.Remove(pos, 1);
                    RemoveInternalChildRange(childIndex, 1);
                }
            }
        }

        // ── Arrange ──────────────────────────────────────────────
        protected override Size ArrangeOverride(Size finalSize)
        {
            var generator = ItemContainerGenerator;
            _perLine = PerLineFor(finalSize);

            for (int childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
            {
                var child     = InternalChildren[childIndex];
                int itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
                if (itemIndex < 0) continue;

                int line = itemIndex / _perLine;
                int slot = itemIndex % _perLine;

                child.Arrange(IsVertical
                    ? new Rect((line * ItemWidth) - _offset.X, slot * ItemHeight, ItemWidth, ItemHeight)
                    : new Rect(slot * ItemWidth, (line * ItemHeight) - _offset.Y, ItemWidth, ItemHeight));
            }
            return finalSize;
        }

        // A shrinking collection can leave the offset past the new end, and recycled containers
        // must be dropped on a Reset or they come back bound to the wrong item.
        protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
        {
            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Remove:
                case NotifyCollectionChangedAction.Replace:
                case NotifyCollectionChangedAction.Move:
                    RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                    break;
                case NotifyCollectionChangedAction.Reset:
                    RemoveInternalChildRange(0, InternalChildren.Count);
                    SetVerticalOffset(0);
                    SetHorizontalOffset(0);
                    break;
            }
            InvalidateMeasure();
        }

        // ── IScrollInfo ──────────────────────────────────────────
        private Size  _extent   = new(0, 0);
        private Size  _viewport = new(0, 0);
        private Point _offset;

        public bool CanVerticallyScroll   { get; set; }
        public bool CanHorizontallyScroll { get; set; }   // accepted; the orientation decides the axis

        public double ExtentWidth      => _extent.Width;
        public double ExtentHeight     => _extent.Height;
        public double ViewportWidth    => _viewport.Width;
        public double ViewportHeight   => _viewport.Height;
        public double HorizontalOffset => _offset.X;
        public double VerticalOffset   => _offset.Y;

        public ScrollViewer? ScrollOwner { get; set; }

        private void UpdateScrollInfo(Size extent, Size viewport)
        {
            bool changed = false;

            if (extent != _extent)     { _extent = extent;     changed = true; }
            if (viewport != _viewport) { _viewport = viewport; changed = true; }

            // Shrinking content (a new search, a filter) can strand the offset past the end.
            double maxY = Math.Max(0, _extent.Height - _viewport.Height);
            if (_offset.Y > maxY) { _offset.Y = maxY; changed = true; }
            double maxX = Math.Max(0, _extent.Width - _viewport.Width);
            if (_offset.X > maxX) { _offset.X = maxX; changed = true; }

            if (changed) ScrollOwner?.InvalidateScrollInfo();
        }

        public void SetVerticalOffset(double offset)
        {
            double maxY = Math.Max(0, _extent.Height - _viewport.Height);
            offset = Math.Max(0, Math.Min(offset, maxY));
            if (Math.Abs(offset - _offset.Y) < 0.01) return;

            _offset.Y = offset;
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateMeasure();   // a new offset means a different realized range
        }

        // Only the vertical orientation scrolls sideways; a horizontal panel wraps to the width.
        public void SetHorizontalOffset(double offset)
        {
            if (!IsVertical) return;

            double maxX = Math.Max(0, _extent.Width - _viewport.Width);
            offset = Math.Max(0, Math.Min(offset, maxX));
            if (Math.Abs(offset - _offset.X) < 0.01) return;

            _offset.X = offset;
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateMeasure();   // a new offset means a different realized range
        }

        // Moves along whichever axis this orientation scrolls.
        private void ScrollBy(double delta)
        {
            if (IsVertical) SetHorizontalOffset(_offset.X + delta);
            else            SetVerticalOffset(_offset.Y + delta);
        }

        public void LineUp()   { if (!IsVertical) SetVerticalOffset(_offset.Y - _pixelStep); }
        public void LineDown() { if (!IsVertical) SetVerticalOffset(_offset.Y + _pixelStep); }
        public void PageUp()   { if (!IsVertical) SetVerticalOffset(_offset.Y - _viewport.Height); }
        public void PageDown() { if (!IsVertical) SetVerticalOffset(_offset.Y + _viewport.Height); }

        // The wheel follows the scrolling axis, so a plain wheel moves Compact List's columns.
        public void MouseWheelUp()   => ScrollBy(-(_pixelStep * 3));
        public void MouseWheelDown() => ScrollBy(_pixelStep * 3);

        public void LineLeft()  { if (IsVertical) SetHorizontalOffset(_offset.X - _pixelStep); }
        public void LineRight() { if (IsVertical) SetHorizontalOffset(_offset.X + _pixelStep); }
        public void PageLeft()  { if (IsVertical) SetHorizontalOffset(_offset.X - _viewport.Width); }
        public void PageRight() { if (IsVertical) SetHorizontalOffset(_offset.X + _viewport.Width); }
        public void MouseWheelLeft()  { if (IsVertical) SetHorizontalOffset(_offset.X - (_pixelStep * 3)); }
        public void MouseWheelRight() { if (IsVertical) SetHorizontalOffset(_offset.X + (_pixelStep * 3)); }

        // Keyboard navigation and SelectedItem changes route through here. Movement is along the
        // scrolling axis only, so this scrolls the target's row (or column) just inside the
        // viewport.
        public Rect MakeVisible(Visual visual, Rect rectangle)
        {
            if (visual is not UIElement child) return rectangle;

            int childIndex = InternalChildren.IndexOf(child);
            if (childIndex < 0) return rectangle;

            int itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(
                new GeneratorPosition(childIndex, 0));
            if (itemIndex < 0) return rectangle;

            ScrollLineIntoView(itemIndex);

            int line = itemIndex / _perLine;
            int slot = itemIndex % _perLine;
            return IsVertical
                ? new Rect((line * ItemWidth) - _offset.X, slot * ItemHeight, ItemWidth, ItemHeight)
                : new Rect(slot * ItemWidth, (line * ItemHeight) - _offset.Y, ItemWidth, ItemHeight);
        }

        private void ScrollLineIntoView(int itemIndex)
        {
            double start = (itemIndex / _perLine) * LineSize;
            double end   = start + LineSize;

            if (start < ScrollPos)                  ScrollTo(start);
            else if (end > ScrollPos + ViewSpan)    ScrollTo(end - ViewSpan);
        }

        private void ScrollTo(double pos)
        {
            if (IsVertical) SetHorizontalOffset(pos);
            else            SetVerticalOffset(pos);
        }

        // ListBox.ScrollIntoView lands here when the target item has no container yet, which is
        // the normal case in a large folder: scroll its line into range so the next layout pass
        // realizes it.
        protected override void BringIndexIntoView(int index)
        {
            if (index < 0 || index >= _itemCount) return;
            ScrollLineIntoView(index);
            UpdateLayout();
        }
    }
}
