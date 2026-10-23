using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShell.Services;
using KillerShell.Models;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class MenuBarTabBlendTests
{
    [Fact]
    public Task HidingTheMenuJoinsTheActiveTabToTheFileSurface()
        => RuntimeComponentProcess.Run(nameof(MenuBarTabBlendScenario));

    [Fact]
    public Task HiddenMenuTracksTheSelectedTerminalAndItsSkin()
        => RuntimeComponentProcess.Run(nameof(MenuBarTerminalBlendScenario));

    [Fact]
    public Task ReversingTheMenuAnimationKeepsTheTabAndRowTogether()
        => RuntimeComponentProcess.Run(nameof(MenuBarAnimationScenario));

    [Fact]
    public Task EveryThemeTracksContentAndKeepsTheOtherPaneIndependent()
        => RuntimeComponentProcess.Run(nameof(MenuBarThemeScenario));

    [Fact]
    public Task IntermediateFramesLeaveSharedThemeBrushesMutable()
        => RuntimeComponentProcess.Run(nameof(MenuBarBrushOwnershipScenario));
}

public static class MenuBarBrushOwnershipScenario
{
    public static void Run()
    {
        var menu = new SolidColorBrush(Colors.Red);
        var content = new SolidColorBrush(Colors.Blue);
        var border = new TabSurfaceBorder { MenuBackground = menu, ContentBackground = content, BlendProgress = 0.5 };
        Color halfway = MenuBarTabBlendScenario.Paint(border.Background);
        Assert.NotEqual(Colors.Red, halfway);
        Assert.NotEqual(Colors.Blue, halfway);
        Assert.False(menu.IsFrozen, "Blending a tab must not freeze the shared menu brush.");
        Assert.False(content.IsFrozen, "Blending a tab must not freeze the shared content brush.");
    }
}

public static class MenuBarThemeScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = (FilePane)window.FindName("LeftPane");
        var right = (FilePane)window.FindName("RightPane");
        window.ToggleDualPane();
        ShellWindowFixture.PumpUntil(() => right.Active != null && right.Active.IsBrowsing);
        window.Pane.NewTabBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.FocusPane(pane);
        var folder = pane.Active;
        var terminal = new TerminalControl(TerminalSkin.Default);
        var tab = new SearchTab("Terminal") { Term = terminal };
        pane.Tabs.Add(tab);
        pane.NewTabBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        foreach (Theme theme in Enum.GetValues(typeof(Theme)))
        {
            ThemeManager.Apply(theme);
            terminal.RefreshTheme();
            MenuBarTabBlendScenario.SelectTab(pane, tab);
            window.SetMenuBar(pane, hidden: false, animate: false, persist: false);
            var border = MenuBarTabBlendScenario.TabBorder(pane, tab);
            Color original = MenuBarTabBlendScenario.Paint(border.Background);
            Color rightOriginal = MenuBarTabBlendScenario.Paint(MenuBarTabBlendScenario.TabBorder(right, right.Active).Background);
            Capture(pane, theme + "-menu");
            window.SetMenuBar(pane, hidden: true, animate: false, persist: false);
            window.UpdateLayout();
            border = MenuBarTabBlendScenario.TabBorder(pane, tab);
            Assert.Equal(MenuBarTabBlendScenario.Paint((Brush)pane.FindResource("TerminalBackgroundBrush")),
                MenuBarTabBlendScenario.Paint(border.Background));
            Assert.Equal(rightOriginal, MenuBarTabBlendScenario.Paint(MenuBarTabBlendScenario.TabBorder(right, right.Active).Background));
            Assert.Equal(Visibility.Visible, right.LocationRow.Visibility);
            Capture(pane, theme + "-terminal");
            MenuBarTabBlendScenario.SelectTab(pane, folder);
            var folderBorder = MenuBarTabBlendScenario.TabBorder(pane, folder);
            Assert.Equal(MenuBarTabBlendScenario.Paint((Brush)pane.FindResource("ListPaneBrush")),
                MenuBarTabBlendScenario.Paint(folderBorder.Background));
            Capture(pane, theme + "-files");
            MenuBarTabBlendScenario.SelectTab(pane, tab);
            window.SetMenuBar(pane, hidden: false, animate: false, persist: false);
            window.UpdateLayout();
            Assert.Equal(original, MenuBarTabBlendScenario.Paint(MenuBarTabBlendScenario.TabBorder(pane, tab).Background));
        }
    }

    private static void Capture(FilePane pane, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KILLERSHELL_UI_EVIDENCE");
        if (string.IsNullOrEmpty(directory)) return;
        pane.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { }));
        pane.UpdateLayout();
        int width = (int)Math.Ceiling(pane.ActualWidth);
        int height = Math.Min(180, (int)Math.Ceiling(pane.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(pane);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(file);
    }
}

public static class MenuBarAnimationScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        ThemeManager.Apply(Theme.Malaise);
        var pane = (FilePane)window.FindName("LeftPane");
        pane.NewTabBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        var tab = MenuBarTabBlendScenario.TabBorder(pane, pane.Active);
        Color original = MenuBarTabBlendScenario.Paint(tab.Background);
        var settled = System.Diagnostics.Stopwatch.StartNew();
        ShellWindowFixture.PumpUntil(() => settled.ElapsedMilliseconds >= 300);
        double openHeight = pane.LocationRow.ActualHeight;
        Assert.True(openHeight > 1, "The menu must be laid out before the animation starts.");
        window.SetMenuBar(pane, hidden: true, animate: true, persist: false);
        double before = pane.LocationRow.Height;
        Color halfway = MenuBarTabBlendScenario.Paint(tab.Background);
        Assert.True(before > 0);
        window.SetMenuBar(pane, hidden: false, animate: true, persist: false);
        Assert.InRange(Math.Abs(pane.LocationRow.Height - before), 0, 1);
        Assert.Equal(halfway, MenuBarTabBlendScenario.Paint(tab.Background));
        ShellWindowFixture.PumpUntil(() => double.IsNaN(pane.LocationRow.Height));
        Assert.Equal(original, MenuBarTabBlendScenario.Paint(tab.Background));
        Assert.Equal(Visibility.Visible, pane.LocationRow.Visibility);
        Assert.InRange(Math.Abs(pane.LocationRow.ActualHeight - openHeight), 0, 1);
    }
}

public static class MenuBarTerminalBlendScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        ThemeManager.Apply(Theme.SE98);
        var pane = (FilePane)window.FindName("LeftPane");
        var folder = pane.Active;
        var terminal = new TerminalControl(TerminalSkin.Default);
        var tab = new SearchTab("Terminal") { Term = terminal };
        pane.Tabs.Add(tab);
        pane.NewTabBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        MenuBarTabBlendScenario.SelectTab(pane, tab);
        Assert.True(pane.TerminalHost.IsVisible);
        var menu = (ContextMenu)pane.FindResource("BarMenu");
        ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ShellWindowFixture.PumpUntil(() => pane.TerminalBarRow.Visibility == Visibility.Collapsed);
        var border = MenuBarTabBlendScenario.TabBorder(pane, tab);
        Assert.Equal(Colors.Black, MenuBarTabBlendScenario.Paint(border.Background));
        var title = MenuBarTabBlendScenario.Descendants<TextBlock>(border).Single(text => text.Name == "tabLbl");
        var close = MenuBarTabBlendScenario.Descendants<Button>(border).Single(button => button.Name == "tabClose");
        void FindCurrentTerminalTab()
        {
            // Selection and layout can rebuild the tab's item template.
            border = MenuBarTabBlendScenario.TabBorder(pane, tab);
            title = MenuBarTabBlendScenario.Descendants<TextBlock>(border).Single(text => text.Name == "tabLbl");
            close = MenuBarTabBlendScenario.Descendants<Button>(border).Single(button => button.Name == "tabClose");
        }
        Color terminalText = MenuBarTabBlendScenario.Paint((Brush)pane.FindResource("TerminalForegroundBrush"));
        ShellWindowFixture.PumpUntil(() =>
            {
                FindCurrentTerminalTab();
                return terminalText == MenuBarTabBlendScenario.Paint(title.Foreground)
                    && terminalText == MenuBarTabBlendScenario.Paint(close.Foreground);
            },
            diagnostic: () => "Blend=" + pane.MenuBarBlend + "; active=" + tab.IsActive + "; terminal=" + tab.IsTerminal
                + "; text=" + pane.MenuBarContentForeground + "; title=" + title.Foreground + "; close=" + close.Foreground);
        Assert.True(terminalText == MenuBarTabBlendScenario.Paint(title.Foreground), "The terminal tab title must use readable terminal text.");
        Assert.True(terminalText == MenuBarTabBlendScenario.Paint(close.Foreground),
            "The terminal tab close button must use readable terminal text: " + close.Foreground
            + "; source=" + DependencyPropertyHelper.GetValueSource(close, Control.ForegroundProperty).BaseValueSource);

        terminal.SetSkin(TerminalSkin.Lcd);
        ShellWindowFixture.PumpUntil(() =>
        {
            FindCurrentTerminalTab();
            return MenuBarTabBlendScenario.Paint(border.Background) == Color.FromRgb(10, 20, 12);
        });
        Assert.Equal(Color.FromRgb(10, 20, 12), MenuBarTabBlendScenario.Paint(border.Background));

        MenuBarTabBlendScenario.SelectTab(pane, folder);
        ShellWindowFixture.PumpUntil(() => pane.ResultsList.IsVisible);
        var folderBorder = MenuBarTabBlendScenario.TabBorder(pane, folder);
        FindCurrentTerminalTab();
        Assert.Equal(Colors.White, MenuBarTabBlendScenario.Paint(folderBorder.Background));
        Assert.NotEqual(Colors.Black, MenuBarTabBlendScenario.Paint(border.Background));
    }
}

public static class MenuBarTabBlendScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        ThemeManager.Apply(Theme.Malaise);
        var pane = (FilePane)window.FindName("LeftPane");
        // Use the visible New Tab action so the strip has two live tab templates.
        pane.NewTabBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => pane.TabStrip.Items.Count == 2);
        window.UpdateLayout();
        Border active = TabBorder(pane, pane.Active);
        Border inactive = TabBorder(pane, pane.Tabs.First(tab => tab != pane.Active));
        Color initial = Paint(active.Background);
        Color other = Paint(inactive.Background);
        Color content = Paint((Brush)pane.FindResource("ListPaneBrush"));
        Assert.NotEqual(content, initial);

        var menu = (ContextMenu)pane.FindResource("BarMenu");
        ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ShellWindowFixture.PumpUntil(() => pane.LocationRow.Visibility == Visibility.Collapsed);

        Assert.Equal(content, Paint(active.Background));
        Assert.Equal(other, Paint(inactive.Background));
    }

    internal static Border TabBorder(FilePane pane, object tab)
        => Descendants<Border>(pane.TabStrip)
            .Single(border => border.Name == "tabBd" && ReferenceEquals(border.DataContext, tab));

    internal static void SelectTab(FilePane pane, SearchTab tab)
    {
        var window = (KillerShell.Shell.MainWindow)Window.GetWindow(pane);
        // The fixture owns the tab model; refocusing installs its normal live content hosts.
        window.FocusPane((FilePane)window.FindName("RightPane"));
        pane.Active = tab;
        window.FocusPane(pane);
        window.UpdateLayout();
    }

    internal static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    internal static Color Paint(Brush brush)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(brush, null, new Rect(0, 0, 8, 8));
        var bitmap = new RenderTargetBitmap(8, 8, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(4, 4, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
}
