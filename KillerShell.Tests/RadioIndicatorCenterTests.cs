using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KillerShell.Tests
{
    /// <summary>
    /// Renders the theme picker's indicator row and measures it. The ring has to sit on the
    /// middle of the label's capital letters in both UI faces (Killer Sans UI on every theme but
    /// 98SE, Microsoft Sans Serif on 98SE) and at every app size, which is exactly what a fixed
    /// pixel nudge could not do.
    /// </summary>
    public sealed class RadioIndicatorCenterTests
    {
        [Theory]
        [InlineData("KillerSansUI", 1.0)]
        [InlineData("KillerSansUI", 1.25)]
        [InlineData("KillerSansUI", 1.5)]
        [InlineData("KillerSansUI", 2.0)]
        [InlineData("Microsoft Sans Serif", 1.0)]
        [InlineData("Microsoft Sans Serif", 1.25)]
        [InlineData("Microsoft Sans Serif", 1.5)]
        [InlineData("Microsoft Sans Serif", 2.0)]
        public void RingSitsOnTheMiddleOfTheCapitals(string face, double scale)
        {
            double offset = double.NaN;
            Exception? failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try { offset = Measure(face, scale); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw failure;

            // One device pixel of tolerance: the ring and the glyphs snap to the pixel grid
            // separately, so a half-pixel either way is rasterization, not placement.
            Assert.InRange(offset, -1.0, 1.0);
        }

        [Theory]
        [InlineData("ThemeRadio", "KillerSansUI")]
        [InlineData("ThemeRadio", "Microsoft Sans Serif")]
        [InlineData("ThemeCheck", "KillerSansUI")]
        [InlineData("ThemeCheck", "Microsoft Sans Serif")]
        public void TheShippedStylesCenterTheirIndicator(string styleKey, string face)
        {
            double offset = double.NaN;
            Exception? failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try { offset = MeasureShippedStyle(styleKey, face); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw failure;
            Assert.InRange(offset, -1.0, 1.0);
        }

        private static double MeasureShippedStyle(string styleKey, string face)
        {
            var controls = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/KillerShell;component/Controls/Controls.xaml", UriKind.Absolute),
            };
            var root = new System.Windows.Controls.Border { Background = Brushes.White, Width = 200, UseLayoutRounding = true };
            // Only the indicator outline is colored, so the measurement sees ring and label alone.
            root.Resources.MergedDictionaries.Add(controls);
            root.Resources["DimTextBrush"] = new SolidColorBrush(Color.FromRgb(255, 0, 0));
            root.Resources["TextBrush"] = new SolidColorBrush(Color.FromRgb(0, 0, 255));
            root.Resources["RadioWellBrush"] = Brushes.Transparent;
            root.Resources["RowHoverBrush"] = Brushes.Transparent;
            root.Resources["PrimaryBrush"] = Brushes.Transparent;
            root.Resources["UiFont"] = Family(face);

            Control indicator = styleKey == "ThemeRadio"
                ? new System.Windows.Controls.RadioButton { Content = "HHHH" }
                : new System.Windows.Controls.CheckBox { Content = "HHHH" };
            indicator.Style = (Style)controls[styleKey];
            indicator.FontFamily = Family(face);
            indicator.Margin = new Thickness(8, 0, 0, 0);
            root.Child = indicator;
            return MeasureRendered(root);
        }

        private static FontFamily Family(string face) => face == "KillerSansUI"
            ? new FontFamily(new Uri(Path.Combine(FindRepoRoot(), "Fonts") + Path.DirectorySeparatorChar), "./#Killer Sans UI, Segoe UI")
            : new FontFamily(face);

        private static double Measure(string face, double scale)
        {
            string family = face == "KillerSansUI"
                ? new Uri(Path.Combine(FindRepoRoot(), "Fonts") + Path.DirectorySeparatorChar).AbsoluteUri
                  + "#Killer Sans UI, Segoe UI"
                : face;
            string xaml =
                "<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                " xmlns:kl='clr-namespace:KillerFamily.Layout;assembly=KillerShell'" +
                " Background='White' Width='200' UseLayoutRounding='True'>" +
                $"<Border.LayoutTransform><ScaleTransform ScaleX='{scale}' ScaleY='{scale}'/></Border.LayoutTransform>" +
                $"<Border x:Name='row' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Padding='10,4,10,4' TextElement.FontFamily=\"{family}\" TextElement.FontSize='12'>" +
                "<DockPanel LastChildFill='True'>" +
                "<Grid Width='16' Height='16' VerticalAlignment='Center' DockPanel.Dock='Left'>" +
                "<Grid.Margin><MultiBinding Converter='{x:Static kl:CapCenterMargin.Instance}' ConverterParameter='0,0,8,0'>" +
                "<Binding ElementName='row' Path='(TextElement.FontFamily)'/>" +
                "<Binding ElementName='row' Path='(TextElement.FontSize)'/>" +
                "</MultiBinding></Grid.Margin>" +
                "<Ellipse Width='14' Height='14' Stroke='#FF0000' StrokeThickness='1.5'/>" +
                "</Grid>" +
                "<TextBlock Text='HHHH' Foreground='#0000FF' VerticalAlignment='Center'/>" +
                "</DockPanel></Border></Border>";

            return MeasureRendered((FrameworkElement)XamlReader.Parse(xaml));
        }

        private static double MeasureRendered(FrameworkElement root)
        {
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            root.UpdateLayout();

            int w = (int)Math.Ceiling(root.DesiredSize.Width), h = (int)Math.Ceiling(root.DesiredSize.Height);
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var px = new byte[w * h * 4];
            bitmap.CopyPixels(px, w * 4, 0);

            int ringTop = int.MaxValue, ringBottom = -1, capTop = int.MaxValue, capBottom = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    byte b = px[i], g = px[i + 1], r = px[i + 2];
                    if (r > 200 && b < 150 && g < 150) { ringTop = Math.Min(ringTop, y); ringBottom = Math.Max(ringBottom, y); }
                    if (b > 200 && r < 150 && g < 150) { capTop = Math.Min(capTop, y); capBottom = Math.Max(capBottom, y); }
                }

            Assert.True(ringBottom >= 0 && capBottom >= 0, "ring or label did not render");
            return (ringTop + ringBottom) / 2.0 - (capTop + capBottom) / 2.0;
        }

        private static string FindRepoRoot()
        {
            string? dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "KillerShell.csproj"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new DirectoryNotFoundException("KillerShell.csproj not found above the test output.");
        }
    }
}
