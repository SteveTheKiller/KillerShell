using System.Windows;
using System.Windows.Controls;

namespace KillerShell.Shell
{
    /// <summary>
    /// The 98SE tier treatment for the code-built tool tabs (Event Viewer, Processes/Services,
    /// Registry Editor). WrapBar puts a control's filter/address row on the same RAISED menu-bar
    /// tier every other tab kind has: an opaque PaneBrush face plus the BarEdge highlight and
    /// shadow, both zero-thickness on every ordinary theme so nothing moves there. WrapContent
    /// sinks a content area into the same four-border crossed well the sidebar tree and the file
    /// listing use, over a face brush that is Transparent on every ordinary theme.
    /// (Steve, 2026-08-09: "the content of the table and the table header should be sunken, but
    /// the dropdowns and search field should be in the raised menubar".)
    /// </summary>
    internal static class ToolTabChrome
    {
        /// <summary>Raised menu-bar host: PaneBrush face under the bar, BarEdge ring over it.</summary>
        internal static Grid WrapBar(UIElement bar)
        {
            var host = new Grid();
            var face = new Border();
            face.SetResourceReference(Border.BackgroundProperty, "PaneBrush");
            host.Children.Add(face);
            host.Children.Add(bar);
            host.Children.Add(Edge("BarEdgeBrush", "BarEdgeThickness", 3));
            host.Children.Add(Edge("BarEdgeDarkBrush", "BarEdgeDarkThickness", 3));
            return host;
        }

        /// <summary>Sunken well host: an optional face (Transparent off 98SE) under the content,
        /// the double crossed bevel over it.</summary>
        internal static Grid WrapContent(UIElement content, string faceKey)
        {
            var host = new Grid();
            var face = new Border();
            face.SetResourceReference(Border.BackgroundProperty, faceKey);
            host.Children.Add(face);
            host.Children.Add(content);
            host.Children.Add(Edge("PaneBevelDarkBrush", "PaneBevelLightThickness", 5));
            host.Children.Add(Edge("PaneBevelLightBrush", "PaneBevelDarkThickness", 5));
            host.Children.Add(Inner("PaneBevelDark2Brush", "PaneBevel2LightThickness"));
            host.Children.Add(Inner("PaneBevelLight2Brush", "PaneBevel2DarkThickness"));
            return host;
        }

        private static Border Edge(string brushKey, string thicknessKey, int z)
        {
            var b = new Border { IsHitTestVisible = false };
            b.SetResourceReference(Border.BorderBrushProperty, brushKey);
            b.SetResourceReference(Border.BorderThicknessProperty, thicknessKey);
            Panel.SetZIndex(b, z);
            return b;
        }

        private static Border Inner(string brushKey, string thicknessKey)
        {
            var b = Edge(brushKey, thicknessKey, 5);
            b.SetResourceReference(FrameworkElement.MarginProperty, "PaneBevelInnerMargin");
            return b;
        }
    }
}
