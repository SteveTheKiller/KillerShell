using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShell.Services;
using KillerShell.Tests.Editing;
using KillerShell.Tests.Terminal;
using Xunit;

namespace KillerShell.Tests;

public sealed class DialogBevelThemeTests
{
    [Fact]
    public Task ClassicCheckboxHasBothSunkenEdgesAfterThemeChanges()
        => EditorScenarioProcess.Run(typeof(ClassicCheckboxScenario).FullName!);

    [Fact]
    public Task ThemeCheckboxHasBothSunkenEdgesAfterThemeChanges()
        => EditorScenarioProcess.Run(typeof(ClassicCheckboxScenario).FullName!, "ThemeCheck");
}

public static class ClassicCheckboxScenario
{
    public static void Run(string? styleName = null)
    {
        var app = RuntimeComponentProcess.StartApplication();
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        try
        {
            ThemeManager.Apply(Theme.SE98);
            var checkbox = new CheckBox { Content = "", UseLayoutRounding = true, SnapsToDevicePixels = true };
            if (styleName != null) checkbox.Style = (Style)app.FindResource(styleName);
            foreach (var theme in new[] { Theme.SE98, Theme.Dark, Theme.SE98 })
            {
                ThemeManager.Apply(theme);
                foreach (bool selected in new[] { false, true })
                {
                    checkbox.IsChecked = selected;
                    checkbox.Measure(new Size(100, 100));
                    checkbox.Arrange(new Rect(0, 0, checkbox.DesiredSize.Width, checkbox.DesiredSize.Height));
                    checkbox.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(checkbox);
                    if (theme != Theme.SE98) continue;
                    var indicator = (FrameworkElement)checkbox.Template.FindName(styleName == null ? "box" : "ring", checkbox);
                    Point origin = indicator.TranslatePoint(new Point(), checkbox);
                    int left = (int)Math.Round(origin.X);
                    int top = (int)Math.Round(origin.Y);
                    int width = (int)Math.Round(indicator.ActualWidth);
                    AssertPixel(bitmap, left, top + 3, Colors.Gray);
                    AssertPixel(bitmap, left + 1, top + 3, Colors.Black);
                    AssertPixel(bitmap, left + width - 2, top + 3, Color.FromRgb(0xdf, 0xdf, 0xdf));
                    AssertPixel(bitmap, left + width - 1, top + 3, Colors.White);
                }
            }
        }
        finally { app.Shutdown(); }
    }

    private static void AssertPixel(BitmapSource bitmap, int x, int y, Color expected)
    {
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        Color actual = Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
        Assert.True(actual == expected, $"Checkbox edge at ({x},{y}) was {actual}, expected {expected}.");
    }
}
