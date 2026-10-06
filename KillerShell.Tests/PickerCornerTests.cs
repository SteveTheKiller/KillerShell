using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KillerShell.Tests;

public sealed class PickerCornerTests
{
    [Fact]
    public void ContentIsClippedAtEveryCornerAfterResizingAndChangingThemes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var border = new PickerBorder
                {
                    Background = Brushes.White,
                    CornerRadius = new CornerRadius(6),
                    Child = new Border { Background = Brushes.Red }
                };
                foreach (int width in new[] { 80, 140 })
                {
                    foreach (double scale in new[] { 1.0, 1.25, 2.0 })
                    {
                        foreach (double radius in new[] { 6.0, 0.0, 6.0 })
                        {
                            border.CornerRadius = new CornerRadius(radius);
                            border.Measure(new Size(width, 60));
                            border.Arrange(new Rect(0, 0, width, 60));
                            border.UpdateLayout();
                            int w = (int)(width * scale), h = (int)(60 * scale);
                            var image = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                            image.Render(border);
                            var pixels = new byte[w * h * 4];
                            image.CopyPixels(pixels, w * 4, 0);
                            byte expected = radius == 0 ? (byte)255 : (byte)0;
                            foreach (int offset in new[] { 0, (w - 1) * 4, (h - 1) * w * 4, (w * h - 1) * 4 })
                                Assert.Equal(expected, pixels[offset + 3]);
                            Assert.Equal((byte)255, pixels[((h / 2) * w + w / 2) * 4 + 3]);
                        }
                    }
                }
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }
}
