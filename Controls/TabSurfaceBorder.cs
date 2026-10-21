using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerShell
{
    /// <summary>Joins a selected tab's face to its content as the intervening menu collapses.</summary>
    public sealed class TabSurfaceBorder : Border
    {
        public static readonly DependencyProperty MenuBackgroundProperty = DependencyProperty.Register(
            nameof(MenuBackground), typeof(Brush), typeof(TabSurfaceBorder),
            new PropertyMetadata(Brushes.Transparent, Refresh));
        public static readonly DependencyProperty ContentBackgroundProperty = DependencyProperty.Register(
            nameof(ContentBackground), typeof(Brush), typeof(TabSurfaceBorder),
            new PropertyMetadata(null, Refresh));
        public static readonly DependencyProperty BlendProgressProperty = DependencyProperty.Register(
            nameof(BlendProgress), typeof(double), typeof(TabSurfaceBorder),
            new PropertyMetadata(0.0, Refresh));
        private static readonly DependencyPropertyKey UsesContentBackgroundPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(UsesContentBackground), typeof(bool), typeof(TabSurfaceBorder), new PropertyMetadata(false));
        public static readonly DependencyProperty UsesContentBackgroundProperty = UsesContentBackgroundPropertyKey.DependencyProperty;
        public bool UsesContentBackground => (bool)GetValue(UsesContentBackgroundProperty);

        public Brush MenuBackground
        {
            get => (Brush)GetValue(MenuBackgroundProperty);
            set => SetValue(MenuBackgroundProperty, value);
        }

        public Brush? ContentBackground
        {
            get => (Brush?)GetValue(ContentBackgroundProperty);
            set => SetValue(ContentBackgroundProperty, value);
        }

        public double BlendProgress
        {
            get => (double)GetValue(BlendProgressProperty);
            set => SetValue(BlendProgressProperty, value);
        }

        private static void Refresh(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var border = (TabSurfaceBorder)sender;
            Brush menu = border.MenuBackground;
            Brush? content = border.ContentBackground;
            double progress = Math.Max(0, Math.Min(1, border.BlendProgress));
            border.SetValue(UsesContentBackgroundPropertyKey, content != null && progress >= 0.5);
            if (content == null || progress == 0) { border.Background = menu; return; }
            if (progress == 1) { border.Background = content; return; }

            // Composite brushes rather than animating shared theme resources. This also keeps
            // gradient brushes intact, and leaves the tab's border, grain and children alone.
            var area = new RectangleGeometry(new Rect(0, 0, 1, 1));
            var overlay = new DrawingGroup { Opacity = progress };
            overlay.Children.Add(new GeometryDrawing(content, null, area));
            var layers = new DrawingGroup();
            layers.Children.Add(new GeometryDrawing(menu, null, area));
            layers.Children.Add(overlay);
            // Freezing this drawing would recursively freeze its borrowed theme brushes.
            border.Background = new DrawingBrush(layers);
        }
    }
}
