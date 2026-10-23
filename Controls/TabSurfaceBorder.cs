using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerShell
{
    /// <summary>Joins a selected tab's face to its content as the intervening menu collapses.</summary>
    public sealed class TabSurfaceBorder : Border
    {
        protected override void OnRender(DrawingContext drawingContext)
        {
            // The right-only divider shares the next tab's left outline, rather than
            // sitting one pixel inside the preceding tab. Child content stays in place.
            // Not on a 98SE bottom strip, whose edges are drawn on the tab's true bounds below.
            bool dividerOnly = BorderThickness == new Thickness(0, 0, 1, 0) && !RetroBottomEdges;
            if (dividerOnly) drawingContext.PushTransform(new TranslateTransform(1, 0));
            base.OnRender(drawingContext);
            if (RetroBottomEdges) DrawRetroBottomEdges(drawingContext);
            if (dividerOnly) drawingContext.Pop();
        }

        /// <summary>
        /// 98SE with the tabs below the page. The template's bevel borders are zeroed for this case
        /// (TabPlacement.cs) and the edges are drawn here pixel by pixel, because Win98 cuts a tab's
        /// foot corners on the diagonal and stacked borders cannot. Light comes from the top-left
        /// as everywhere else in Win98: white left edge, #808080 then black on the right and foot,
        /// with the shadow owning both foot corners. No top edge: the tab hangs from the page.
        /// </summary>
        private void DrawRetroBottomEdges(DrawingContext dc)
        {
            int w = (int)Math.Round(RenderSize.Width), h = (int)Math.Round(RenderSize.Height);
            if (w < 6 || h < 4) return;
            // The template flips this border upside down for a bottom strip; draw in screen
            // orientation by flipping back.
            dc.PushTransform(new MatrixTransform(1, 0, 0, -1, 0, h));
            Brush light = (TryFindResource("BevelLightBrush") as Brush) ?? Brushes.White;
            Brush shadow = (TryFindResource("BevelDarkBrush") as Brush) ?? Brushes.Gray;
            Brush dark = (TryFindResource("BevelOuterDarkBrush") as Brush) ?? Brushes.Black;
            Brush face = (TryFindResource("BackgroundBrush") as Brush) ?? Brushes.Silver;
            void Px(Brush b, int x, int y, int cw = 1, int ch = 1) => dc.DrawRectangle(b, null, new Rect(x, y, cw, ch));

            // The selected tab is part of the page, so it wears the page's face rather than the
            // listing's: the listing keeps its own sunken edge inside the page above it.
            if (UsesContentBackground && TryFindResource("MenuBackgroundBrush") is Brush page)
                Px(page, 0, 0, w, h);

            // Cut corners show the strip behind the tab.
            Px(face, 0, h - 2, 1, 2); Px(face, 1, h - 1);
            Px(face, w - 1, h - 2, 1, 2); Px(face, w - 2, h - 1);
            Px(light, 0, 0, 1, h - 2);            // left highlight
            Px(light, 1, h - 2);                  // its diagonal into the foot
            Px(shadow, w - 2, 0, 1, h - 2);       // right, inner
            Px(dark, w - 1, 0, 1, h - 2);         // right, outer
            Px(shadow, 2, h - 2, w - 4, 1);       // foot, inner
            Px(dark, 2, h - 1, w - 4, 1);         // foot, outer
            Px(dark, w - 2, h - 2);               // the shadow's diagonal
            dc.Pop();
        }

        public static readonly DependencyProperty RetroBottomEdgesProperty = DependencyProperty.Register(
            nameof(RetroBottomEdges), typeof(bool), typeof(TabSurfaceBorder),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
        public bool RetroBottomEdges
        {
            get => (bool)GetValue(RetroBottomEdgesProperty);
            set => SetValue(RetroBottomEdgesProperty, value);
        }

        public static readonly DependencyProperty DividerBrushProperty = DependencyProperty.Register(
            nameof(DividerBrush), typeof(Brush), typeof(TabSurfaceBorder), new PropertyMetadata(null, RefreshDivider));
        private static readonly DependencyPropertyKey FadedDividerBrushPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(FadedDividerBrush), typeof(Brush), typeof(TabSurfaceBorder), new PropertyMetadata(null));
        public static readonly DependencyProperty FadedDividerBrushProperty = FadedDividerBrushPropertyKey.DependencyProperty;
        public Brush? FadedDividerBrush => (Brush?)GetValue(FadedDividerBrushProperty);
        public Brush? DividerBrush
        {
            get => (Brush?)GetValue(DividerBrushProperty);
            set => SetValue(DividerBrushProperty, value);
        }

        private static void RefreshDivider(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var border = (TabSurfaceBorder)sender;
            Brush? brush = border.DividerBrush;
            if (brush is SolidColorBrush solid)
            {
                var color = solid.Color;
                var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 0));
                fade.GradientStops.Add(new GradientStop(color, 0.75));
                fade.Freeze();
                brush = fade;
            }
            border.SetValue(FadedDividerBrushPropertyKey, brush);
        }

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
            border.SetValue(UsesContentBackgroundPropertyKey,
                content != null && (progress >= 0.5 || ReferenceEquals(menu, content)));
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
