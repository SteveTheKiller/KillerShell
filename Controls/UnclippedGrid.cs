using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerShell
{
    // A Grid that clips only when asked to. WPF also clips a panel laid out a fraction of a pixel
    // smaller than it asked for, which the app zoom's LayoutTransform does at sizes like 110%,
    // and that cut off the pane shadow above the footer. ClipToBounds still clips.
    public sealed class UnclippedGrid : Grid
    {
        protected override Geometry? GetLayoutClip(Size layoutSlotSize)
            => ClipToBounds ? base.GetLayoutClip(layoutSlotSize) : null;
    }
}
