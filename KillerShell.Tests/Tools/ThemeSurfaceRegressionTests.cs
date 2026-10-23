using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class ThemeSurfaceRegressionTests
{
    [Fact]
    public Task BlackDetailsHeaderSharesTheContentSurfaceAndKeepsItsAccentRule()
        => RuntimeComponentProcess.Run(nameof(BlackHeaderSurfaceScenario));

    [Fact]
    public Task DarkDefaultTerminalUsesTheContentSurfaceWithoutChangingRetroSkins()
        => RuntimeComponentProcess.Run(nameof(DarkTerminalSurfaceScenario));

    [Fact]
    public Task FixedThemesKeepCreamTabRingsAndNeutralThemesKeepTheirAccent()
        => RuntimeComponentProcess.Run(nameof(FixedTabRingScenario));

    [Fact]
    public Task DefaultTerminalTabKeepsThemeGrainWithEitherToolbarOrTabPlacement()
        => RuntimeComponentProcess.Run(nameof(TerminalTabGrainScenario));
}

public static class BlackHeaderSurfaceScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        ThemeManager.Apply(Theme.Black);
        var pane = fixture.Window.Pane;
        pane.ViewDetailsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        fixture.Window.UpdateLayout();
        Assert.Equal(ColorOf(pane.ResultsSurface.Background), ColorOf(pane.DetailsHeader.Background));
        Assert.Same(pane.FindResource("HeaderLineBrush"), pane.DetailsHeader.BorderBrush);
        Assert.Equal(1, pane.DetailsHeader.BorderThickness.Bottom);
    }

    internal static Color ColorOf(object brush) => Assert.IsType<SolidColorBrush>(brush).Color;
}

public static class DarkTerminalSurfaceScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        ThemeManager.Initialize();
        var terminal = new TerminalControl(TerminalSkin.Default);
        var lcd = new TerminalControl(TerminalSkin.Lcd);
        try
        {
            ThemeManager.Apply(Theme.Dark);
            terminal.RefreshTheme();
            lcd.RefreshTheme();
            Assert.Equal(BlackHeaderSurfaceScenario.ColorOf(app.FindResource("ListPaneBrush")),
                BlackHeaderSurfaceScenario.ColorOf(terminal.ScreenBackground));
            Assert.Equal(Color.FromRgb(10, 20, 12), BlackHeaderSurfaceScenario.ColorOf(lcd.ScreenBackground));
            ThemeManager.Apply(Theme.SE98);
            terminal.RefreshTheme();
            Assert.Equal(Colors.Black, BlackHeaderSurfaceScenario.ColorOf(terminal.ScreenBackground));
        }
        finally { terminal.Close(); lcd.Close(); app.Shutdown(); }
    }
}

public static class FixedTabRingScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var app = Application.Current;
        ShellWindowFixture.Click(fixture.Window, "DualPaneRailBtn");
        foreach (var theme in new[] { Theme.Blood, Theme.Greed, Theme.Cyanotic })
        {
            ThemeManager.Apply(theme);
            fixture.Window.UpdateLayout();
            var cream = theme == Theme.Blood ? Color.FromRgb(248, 201, 158) : Color.FromRgb(224, 212, 154);
            Assert.Equal(cream, BlackHeaderSurfaceScenario.ColorOf(app.FindResource("TabActiveRingBrush")));
            Assert.Equal(cream, BlackHeaderSurfaceScenario.ColorOf(fixture.Window.Pane.ResultsPane.BorderBrush));
            Assert.Equal(cream, BlackHeaderSurfaceScenario.ColorOf(fixture.Window.Pane.PaneFocusBottomEdge.BorderBrush));
            var fade = Assert.IsType<LinearGradientBrush>(app.FindResource("TabActiveRingFadeBrush"));
            Assert.Equal(cream, fade.GradientStops[0].Color);
            Assert.Equal(Color.FromArgb(0, cream.R, cream.G, cream.B), fade.GradientStops.Last().Color);
        }
        ThemeManager.Apply(Theme.Dark);
        ThemeManager.ApplyAccent(Theme.Dark, Accent.Blue);
        Assert.Equal(BlackHeaderSurfaceScenario.ColorOf(app.FindResource("PrimaryBrush")),
            BlackHeaderSurfaceScenario.ColorOf(app.FindResource("TabActiveRingBrush")));
        ThemeManager.Apply(Theme.SE98);
        Assert.Equal(0, BlackHeaderSurfaceScenario.ColorOf(app.FindResource("TabActiveRingBrush")).A);
    }
}

public static class TerminalTabGrainScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = window.Pane;
        var terminal = new TerminalControl(TerminalSkin.Default);
        var tab = new SearchTab("PowerShell") { Term = terminal };
        pane.Tabs.Add(tab);
        try
        {
            ThemeManager.Apply(Theme.Malaise);
            MenuBarTabBlendScenario.SelectTab(pane, tab);
            foreach (bool bottom in new[] { false, true })
            {
                var menu = pane.TabBar.ContextMenu;
                menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
                var label = pane.FindResource(bottom ? "Str_Layout_TabsBottom" : "Str_Layout_TabsTop");
                menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, label))
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                foreach (bool hidden in new[] { false, true })
                {
                    window.SetMenuBar(pane, hidden, animate: false, persist: false);
                    window.UpdateLayout();
                    var grain = MenuBarTabBlendScenario.Descendants<Border>(MenuBarTabBlendScenario.TabBorder(pane, tab))
                        .Single(border => border.Name == "tabGrain");
                    Assert.Equal((double)pane.FindResource("GrainOpacity"), grain.Opacity);
                    Assert.True(grain.Opacity > 0);
                }
            }
        }
        finally { terminal.Close(); }
    }
}
