using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerFamily.Controls;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Terminal;

/// <summary>
/// The customizable card's drawer closes with the family's small-window X (OverlayCloseButton and the AboutClose
/// glyph, as the dialogs use), not the main window's caption X with its filled red block.
/// </summary>
public sealed class CardDrawerCloseTests
{
    [Fact]
    public Task DrawerCloseIsTheSmallWindowX() => RuntimeComponentProcess.Run(nameof(CardDrawerCloseScenario));
}

public static class CardDrawerCloseScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        ThemeManager.Initialize();
        foreach (var theme in new[] { Theme.Dark, Theme.TrashPolka })
        {
            ThemeManager.Apply(theme);
            var close = CardBuilder.CloseButton("Done");
            var host = new Border { Child = close };
            host.Measure(new Size(40, 40));
            host.Arrange(new Rect(0, 0, 40, 40));
            Assert.Same(app.FindResource("OverlayCloseButton"), close.Style);
            Assert.Equal(app.FindResource("AboutCloseGlyph"), close.Content);
            // No red fill under the glyph, ever: the face keeps the style's transparent background.
            Assert.Equal(BaseValueSource.Style,
                DependencyPropertyHelper.GetValueSource(close, Control.BackgroundProperty).BaseValueSource);
            Assert.Equal(Colors.Transparent, ((SolidColorBrush)close.Background).Color);
        }
        app.Shutdown();
    }
}
