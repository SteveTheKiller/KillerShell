using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShell.Services;
using KillerShell.Tests.Editing;
using KillerShell.Tests.Terminal;
using Xunit;

namespace KillerShell.Tests;

public sealed class ClassicTextBoxBevelTests
{
    [Fact]
    public Task TextFieldsHaveTheCompleteSunkenWellAndRestoreTheirModernRendering()
        => EditorScenarioProcess.Run(typeof(ClassicTextBoxBevelScenario).FullName!);
}

public static class ClassicTextBoxBevelScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        try
        {
            foreach (bool multiline in new[] { false, true })
            {
                var field = new TextBox
                {
                    Style = (Style)app.FindResource("DarkTextBox"), Text = "Example",
                    Width = 240, Height = multiline ? 70 : 22, AcceptsReturn = multiline,
                    FontSize = 12.5, Focusable = false, UseLayoutRounding = true,
                    SnapsToDevicePixels = true
                };
                var window = new Window
                {
                    Content = field, SizeToContent = SizeToContent.WidthAndHeight,
                    ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000
                };
                window.Show();
                ThemeManager.Apply(Theme.Dark);
                byte[] modern = Pixels(Render(field));
                ThemeManager.Apply(Theme.SE98);
                BitmapSource classic = Render(field);
                int right = classic.PixelWidth - 1, bottom = classic.PixelHeight - 1;
                AssertPixel(classic, 0, 5, Colors.Gray);
                AssertPixel(classic, 1, 5, Colors.Black);
                AssertPixel(classic, right - 1, 5, Color.FromRgb(0xdf, 0xdf, 0xdf));
                AssertPixel(classic, right, 5, Colors.White);
                AssertPixel(classic, 100, 0, Colors.Gray);
                AssertPixel(classic, 100, 1, Colors.Black);
                AssertPixel(classic, 100, bottom - 1, Color.FromRgb(0xdf, 0xdf, 0xdf));
                AssertPixel(classic, 100, bottom, Colors.White);
                ThemeManager.Apply(Theme.Dark);
                Assert.Equal(modern, Pixels(Render(field)));
                window.Close();
            }
        }
        finally { app.Shutdown(); }
    }

    private static BitmapSource Render(FrameworkElement field)
    {
        field.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        field.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Round(field.ActualWidth),
            (int)Math.Round(field.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(field);
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static void AssertPixel(BitmapSource bitmap, int x, int y, Color expected)
    {
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        Color actual = Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
        Assert.True(actual == expected, $"Text field edge at ({x},{y}) was {actual}, expected {expected}.");
    }
}
