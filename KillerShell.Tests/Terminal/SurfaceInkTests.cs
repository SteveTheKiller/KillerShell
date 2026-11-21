using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShell.Editing;
using KillerShell.Services;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

/// <summary>
/// Trash Polka: the terminal screen and the text editor leave their opaque fill off so the pinned pane ink behind
/// them shows; every other theme keeps the solid page. Measured from the rendered pixels.
/// </summary>
public sealed class SurfaceInkTests
{
    [Fact]
    public Task TerminalAndEditorShowTheInkOnlyInTrashPolka() => RuntimeComponentProcess.Run(nameof(SurfaceInkScenario));
}

public static class SurfaceInkScenario
{
    public static void Run()
    {
        RuntimeComponentProcess.StartApplication();

        ThemeManager.Apply(Theme.Dark);
        Assert.Equal(255, BlankAlpha(new TerminalControl(TerminalSkin.Default)));
        // AvalonEdit only draws inside a window, so the editor is read from the page brush it was given:
        // the solid page baked with grain is fully opaque, grain alone is drawn at the grain opacity.
        Assert.Equal(1.0, Editor().Background.Opacity);

        ThemeManager.Apply(Theme.TrashPolka);
        Assert.True(BlankAlpha(new TerminalControl(TerminalSkin.Default)) < 255, "terminal screen still paints an opaque fill");
        Assert.True(Editor().Background.Opacity < 1.0, "editor page still paints an opaque fill");
        // The LCD skin is its own phosphor black on every theme.
        Assert.Equal(255, BlankAlpha(new TerminalControl(TerminalSkin.Lcd)));
    }

    private static EditorControl Editor()
    {
        var editor = new EditorControl(string.Empty);
        editor.ApplyTheme();
        return editor;
    }

    // The highest alpha anywhere in a blank stretch: an opaque fill reads 255, grain alone stays well under.
    private static int BlankAlpha(FrameworkElement surface)
    {
        surface.Width = 200;
        surface.Height = 120;
        surface.Measure(new Size(200, 120));
        surface.Arrange(new Rect(0, 0, 200, 120));
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(200, 120, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var pixels = new byte[200 * 120 * 4];
        bitmap.CopyPixels(pixels, 200 * 4, 0);
        int max = 0;
        for (int y = 80; y < 120; y++)
            for (int x = 100; x < 200; x++)
                max = System.Math.Max(max, pixels[(y * 200 + x) * 4 + 3]);
        return max;
    }
}
