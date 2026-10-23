using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using KillerShell.Shell;

namespace KillerShell.Tools
{
    /// <summary>
    /// The 98SE tier treatment for the code-built tool tabs (Event Viewer, Processes/Services,
    /// Registry Editor). WrapBar puts a control's filter/address row on the same RAISED menu-bar
    /// tier every other tab kind has: an opaque PaneBrush face plus the BarEdge highlight and
    /// shadow, both zero-thickness on every ordinary theme so nothing moves there. WrapContent
    /// sinks a content area into the same four-border crossed well the sidebar tree and the file
    /// listing use, over a face brush that is Transparent on every ordinary theme.
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
            // Grain over the opaque face, under the bar's controls - the same layering the
            // folder LocationRow uses (FilePane.xaml): an opaque face covers whatever grain is
            // painted below it, so the bar has to carry its own or it is the one flat,
            // textureless strip on the tab. GrainOpacity is 0 on 98SE, so this paints nothing
            // there.
            host.Children.Add(Grain());
            host.Children.Add(bar);
            host.Children.Add(Edge("BarEdgeBrush", "BarEdgeThickness", 3));
            host.Children.Add(Edge("BarEdgeDarkBrush", "BarEdgeDarkThickness", 3));
            return host;
        }

        /// <summary>Film grain overlay for an opaque tool-tab surface. Non-hit-testable, sized
        /// by its host; the caller sets Grid.SetRowSpan when it must cover multiple rows.</summary>
        internal static Border Grain()
        {
            var g = new Border { IsHitTestVisible = false };
            g.SetResourceReference(Border.BackgroundProperty, "GrainTileBrush");
            g.SetResourceReference(UIElement.OpacityProperty, "GrainOpacity");
            return g;
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

        /// <summary>One square header band across the full table well, including its filler.</summary>
        internal static Grid WrapTable(DataGrid table, string faceKey)
        {
            table.Margin = new Thickness(0);
            var headerStyle = new Style(typeof(DataGridColumnHeader), table.ColumnHeaderStyle);
            headerStyle.Setters.Add(new EventSetter(FrameworkElement.LoadedEvent,
                new RoutedEventHandler(TableHeader_Loaded)));
            table.ColumnHeaderStyle = headerStyle;

            var host = WrapContent(table, faceKey);
            var band = new Border
            {
                Name = "ToolTableHeaderBand",
                VerticalAlignment = VerticalAlignment.Top,
                BorderThickness = new Thickness(0, 0, 0, 1),
                IsHitTestVisible = false,
                Child = Grain()
            };
            band.SetBinding(FrameworkElement.HeightProperty,
                new Binding(nameof(DataGrid.ColumnHeaderHeight)) { Source = table });
            band.SetResourceReference(Border.BackgroundProperty, "TableHeaderBrush");
            band.SetResourceReference(Border.BorderBrushProperty, "HeaderLineBrush");
            host.Children.Insert(1, band);
            return host;
        }

        private static void TableHeader_Loaded(object sender, RoutedEventArgs e)
        {
            var header = (DataGridColumnHeader)sender;
            if (header.Template?.FindName("hdrBg", header) is not Border face || face.Child is not Grid content) return;
            foreach (UIElement child in content.Children)
                if (child is FrameworkElement element && element.Name == "ToolTableHeaderGrain") return;
            var grain = Grain();
            grain.Name = "ToolTableHeaderGrain";
            Grid.SetColumnSpan(grain, System.Math.Max(1, content.ColumnDefinitions.Count));
            // Undo the header's text inset so grain reaches the same edges as its face.
            grain.Margin = new Thickness(-face.Padding.Left, -face.Padding.Top, -face.Padding.Right, -face.Padding.Bottom);
            content.Children.Insert(0, grain);
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
