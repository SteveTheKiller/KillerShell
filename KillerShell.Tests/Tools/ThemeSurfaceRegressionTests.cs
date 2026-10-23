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

    [Fact]
    public Task BottomListingTabFollowsDetailsAndSearchSummaryAcrossThemes()
        => RuntimeComponentProcess.Run(nameof(BottomListingTabSurfaceScenario));
}

public static class BottomListingTabSurfaceScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = window.Pane;
        pane.NewTabBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => pane.TabStrip.Items.Count == 2 && pane.Active.IsBrowsing);
        pane.ViewDetailsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ThemeManager.Apply(Theme.Malaise);
        PlaceTabs(bottom: true);
        pane.DetailsPaneBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        MatchSurface(pane.DetailsPane.Background, "opening full-width details");

        foreach (Theme theme in Enum.GetValues(typeof(Theme)))
        {
            ThemeManager.Apply(theme);
            MatchSurface(pane.DetailsPane.Background, "theme change with full-width details");
            var edge = Assert.IsType<Border>(pane.FindName("DetailsPaneBottomEdge"));
            Assert.Equal(1, edge.Height);
            Assert.Equal(VerticalAlignment.Bottom, edge.VerticalAlignment);
            Assert.False(edge.IsHitTestVisible);
            Assert.Equal(MenuBarTabBlendScenario.Paint((Brush)pane.FindResource("PaneBorderBrush")),
                MenuBarTabBlendScenario.Paint(edge.Background));

            pane.DetailsPaneBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Dock.Right, DockPanel.GetDock(pane.DetailsPane));
            MatchSurface(pane.ResultsSurface.Background, "moving details to the right");
            Assert.True(edge.IsVisible && edge.ActualWidth > 0, "Right details must retain their bottom rule.");

            pane.DetailsPaneBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Collapsed, pane.DetailsPane.Visibility);
            MatchSurface(pane.ResultsSurface.Background, "closing details");

            pane.Active.Groups[0].Terms[0].Pattern = "*.txt";
            ShellWindowFixture.Click(window, "SearchButton");
            ShellWindowFixture.PumpUntil(() => !pane.Active.IsSearching && pane.PaneStatusBar.IsVisible);
            MatchSurface(pane.ResultsPane.Background, "showing the search summary");

            pane.DetailsPaneBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Dock.Bottom, DockPanel.GetDock(pane.DetailsPane));
            MatchSurface(pane.ResultsPane.Background, "showing details above the search summary");

            var search = (Button)window.FindName("SearchButton");
            ((Panel)search.Parent).Children.OfType<Button>()
                .Single(button => Equals(button.Content, pane.FindResource("Str_Btn_Clear")))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Collapsed, pane.PaneStatusBar.Visibility);
            MatchSurface(pane.DetailsPane.Background, "clearing the search summary");

            PlaceTabs(bottom: false);
            window.SetMenuBar(pane, hidden: false, animate: false, persist: false);
            MatchSurface(pane.LocationRow.Background, "top tabs with the menu shown");
            window.SetMenuBar(pane, hidden: true, animate: false, persist: false);
            MatchSurface(pane.DetailsHeader.Background, "top tabs with the menu hidden");
            window.SetMenuBar(pane, hidden: false, animate: false, persist: false);
            PlaceTabs(bottom: true);
        }

        void PlaceTabs(bool bottom)
        {
            var menu = pane.TabBar.ContextMenu;
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            var label = pane.FindResource(bottom ? "Str_Layout_TabsBottom" : "Str_Layout_TabsTop");
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, label))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            window.UpdateLayout();
        }

        void MatchSurface(Brush expectedBrush, string state)
        {
            window.UpdateLayout();
            Color expected = MenuBarTabBlendScenario.Paint(expectedBrush);
            Color actual = MenuBarTabBlendScenario.Paint(MenuBarTabBlendScenario.TabBorder(pane, pane.Active).Background);
            Assert.True(expected == actual,
                ThemeManager.Current + ": " + state + " expected " + expected + ", actual " + actual + ".");
        }
    }
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
