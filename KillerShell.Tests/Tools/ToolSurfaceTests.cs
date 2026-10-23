using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KillerShell.Editing;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using KillerShell.Terminal;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class ToolSurfaceTests
{
    [Fact]
    public Task ToolTablesMeetTheirSectionEdgesWithContinuousSquareHeaders()
        => RuntimeComponentProcess.Run(nameof(ToolTableSurfaceScenario));

    [Fact]
    public Task PerformanceCardsUseTheLighterContentSurfaceAndKeepRetroReadouts()
        => RuntimeComponentProcess.Run(nameof(PerformanceSurfaceScenario));

    [Fact]
    public Task ActiveTabMatchesItsAdjacentToolbarHeaderOrContent()
        => RuntimeComponentProcess.Run(nameof(ActiveTabSurfaceScenario));
}

public static class ToolTableSurfaceScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        MainWindow.DemoMode = true;
        var window = fixture.Window;
        var pane = window.Pane;
        var process = new ProcessListControl();
        var events = new EventViewerControl();
        var registry = new RegistryEditorControl();
        try
        {
            foreach (var theme in new[] { Theme.Malaise, Theme.SE98 })
            {
                ThemeManager.Apply(theme);
                Check(new SearchTab("Processes") { Procs = process }, process);
                Check(new SearchTab("Events") { Events = events }, events);
                Check(new SearchTab("Registry") { Registry = registry }, registry);
            }
        }
        finally { process.Shutdown(); events.Shutdown(); registry.Shutdown(); MainWindow.DemoMode = false; }

        void Check(SearchTab tab, Grid control)
        {
            pane.Tabs.Add(tab);
            MenuBarTabBlendScenario.SelectTab(pane, tab);
            var grid = MenuBarTabBlendScenario.Descendants<DataGrid>(control).Single();
            ShellWindowFixture.PumpUntil(() => grid.IsLoaded
                && MenuBarTabBlendScenario.Descendants<DataGridColumnHeader>(grid).All(header => header.IsLoaded));
            Assert.Equal(new Thickness(0), grid.Margin);
            var well = (Grid)grid.Parent;
            Assert.InRange(Math.Abs(grid.TranslatePoint(new Point(), well).X), 0, 0.1);
            Assert.InRange(Math.Abs(grid.ActualWidth - well.ActualWidth), 0, 0.1);
            Assert.InRange(Math.Abs(well.TranslatePoint(new Point(well.ActualWidth, 0), control).X - control.ActualWidth), 0, 0.1);
            var band = MenuBarTabBlendScenario.Descendants<Border>(well).Single(b => b.Name == "ToolTableHeaderBand");
            Assert.Equal(well.ActualWidth, band.ActualWidth, 1);
            Assert.Same(control.FindResource("TableHeaderBrush"), band.Background);
            Assert.Same(control.FindResource("HeaderLineBrush"), band.BorderBrush);
            Assert.Equal(new CornerRadius(0), band.CornerRadius);
            var headers = MenuBarTabBlendScenario.Descendants<DataGridColumnHeader>(grid).Where(h => h.ActualWidth > 0).ToArray();
            Assert.NotEmpty(headers);
            foreach (var header in headers)
            {
                var face = (Border)header.Template.FindName("hdrBg", header);
                Assert.Equal(new CornerRadius(0), face.CornerRadius);
                Assert.Contains(MenuBarTabBlendScenario.Descendants<Border>(header), b => b.Name == "ToolTableHeaderGrain");
            }
        }
    }
}

public static class PerformanceSurfaceScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var pane = fixture.Window.Pane;
        var performance = new PerformanceMonitorControl(_ => Task.FromResult(PerformanceHardwareInfo.Empty));
        var tab = new SearchTab("Performance") { Perf = performance };
        pane.Tabs.Add(tab);
        try
        {
            MenuBarTabBlendScenario.SelectTab(pane, tab);
            ShellWindowFixture.PumpUntil(() => MenuBarTabBlendScenario.Descendants<UniformGrid>(performance)
                .Any(g => g.Children.Count > 0));
            foreach (var theme in new[] { Theme.Malaise, Theme.SE98, Theme.Dark })
            {
                ThemeManager.Apply(theme);
                fixture.Window.UpdateLayout();
                Assert.Same(performance.FindResource("MenuBackgroundBrush"), performance.Background);
                var strip = MenuBarTabBlendScenario.Descendants<UniformGrid>(performance).First(g => g.Children.Count > 0);
                var card = Assert.IsType<Border>(strip.Children[0]);
                Assert.Same(Application.Current.FindResource(theme == Theme.SE98 ? "MonitorCellBrush" : "ListPaneBrush"), card.Background);
            }
        }
        finally { performance.Shutdown(); }
    }
}

public static class ActiveTabSurfaceScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = window.Pane;
        pane.NewTabBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ThemeManager.Apply(Theme.Malaise);
        var folder = pane.Active;
        var terminal = new TerminalControl(TerminalSkin.Lcd);
        var editor = new EditorControl("surface-test.txt");
        var processes = new ProcessListControl();
        MainWindow.DemoMode = true;
        try
        {
            pane.ViewDetailsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.SetMenuBar(pane, hidden: false, animate: false, persist: false);
            Matches(folder, pane.LocationRow.Background);
            window.SetMenuBar(pane, hidden: true, animate: false, persist: false);
            Matches(folder, pane.DetailsHeader.Background);
            pane.ViewIconsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Matches(folder, pane.ResultsSurface.Background);

            var document = new SearchTab("Document") { Editor = editor };
            pane.Tabs.Add(document);
            MenuBarTabBlendScenario.SelectTab(pane, document);
            Matches(document, editor.Background);
            window.SetMenuBar(pane, hidden: false, animate: false, persist: false);
            Matches(document, pane.EditorBarRow.Background);

            var shell = new SearchTab("Terminal") { Term = terminal };
            pane.Tabs.Add(shell);
            MenuBarTabBlendScenario.SelectTab(pane, shell);
            Matches(shell, pane.TerminalBarRow.Background);
            window.SetMenuBar(pane, hidden: true, animate: false, persist: false);
            Matches(shell, terminal.ScreenBackground);
            window.SetMenuBar(pane, hidden: false, animate: false, persist: false);
            PlaceTabs(bottom: true);
            Matches(shell, terminal.ScreenBackground);
            var terminalTitle = MenuBarTabBlendScenario.Descendants<TextBlock>(MenuBarTabBlendScenario.TabBorder(pane, shell))
                .Single(text => text.Name == "tabLbl");
            Assert.Same(terminal.ScreenForeground, terminalTitle.Foreground);

            MenuBarTabBlendScenario.SelectTab(pane, folder);
            pane.ViewDetailsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Matches(folder, pane.ResultsSurface.Background);
            MenuBarTabBlendScenario.SelectTab(pane, document);
            Matches(document, editor.Background);
            PlaceTabs(bottom: false);
            Matches(document, pane.EditorBarRow.Background);

            var task = new SearchTab("Processes") { Procs = processes };
            pane.Tabs.Add(task);
            MenuBarTabBlendScenario.SelectTab(pane, task);
            Matches(task, processes.Background);
            window.SetMenuBar(pane, hidden: false, animate: false, persist: false);
            Matches(task, processes.Background);
        }
        finally { processes.Shutdown(); terminal.Close(); MainWindow.DemoMode = false; }

        void Matches(SearchTab tab, Brush expected)
        {
            window.UpdateLayout();
            Assert.Same(expected, MenuBarTabBlendScenario.TabBorder(pane, tab).Background);
        }

        void PlaceTabs(bool bottom)
        {
            var menu = pane.TabBar.ContextMenu;
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            string label = (string)pane.FindResource(bottom ? "Str_Layout_TabsBottom" : "Str_Layout_TabsTop");
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, label))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
    }
}
