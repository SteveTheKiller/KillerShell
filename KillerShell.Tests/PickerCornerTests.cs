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
                    BorderBrush = Brushes.Blue,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Child = new Border { Background = Brushes.Red }
                };
                foreach (int width in new[] { 80, 140 })
                {
                    foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
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

    [Fact]
    public void FilledContentPreservesTheCurvedStrokeAndExistingClip()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var original = new RectangleGeometry(new Rect(0, 0, 78, 58));
                var child = new Border { Background = Brushes.Blue, Clip = original };
                var border = new PickerBorder
                {
                    Background = Brushes.Blue, BorderBrush = Brushes.Red,
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = child
                };
                border.Measure(new Size(80, 60));
                border.Arrange(new Rect(0, 0, 80, 60));
                border.UpdateLayout();
                Assert.False(child.Clip.FillContains(new Point(1, 1)));
                Assert.True(child.Clip.FillContains(new Point(20, 20)));
                var image = new RenderTargetBitmap(640, 480, 768, 768, PixelFormats.Pbgra32);
                image.Render(border);
                var pixels = new byte[640 * 480 * 4];
                image.CopyPixels(pixels, 640 * 4, 0);
                foreach (Point point in new[] { new Point(16, 16), new Point(623, 16), new Point(16, 463), new Point(623, 463) })
                {
                    int offset = ((int)point.Y * 640 + (int)point.X) * 4;
                    Assert.True(pixels[offset + 2] >= 240, "The curved stroke was painted over.");
                    Assert.True(pixels[offset] <= 15, "Content escaped across the curved stroke.");
                }
                border.Child = null;
                border.Measure(new Size(80, 60));
                border.Arrange(new Rect(0, 0, 80, 60));
                Assert.Same(original, child.Clip);
                border.Child = child;
                border.BorderThickness = new Thickness(2, 4, 6, 8);
                border.Padding = new Thickness(3);
                border.CornerRadius = new CornerRadius(6, 10, 0, 3);
                border.Measure(new Size(80, 60));
                border.Arrange(new Rect(0, 0, 80, 60));
                border.UpdateLayout();
                Assert.False(child.Clip.FillContains(border.TranslatePoint(new Point(2.1, 4.1), child)));
                Assert.True(child.Clip.FillContains(border.TranslatePoint(new Point(73.9, 51.9), child)));
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }
}
