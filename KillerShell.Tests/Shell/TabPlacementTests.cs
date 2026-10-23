using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class TabPlacementTests
{
    [Fact]
    public Task PlacementWorksFromShortcutsAndEveryTabSurfaceAndSurvivesRestart()
        => RuntimeComponentProcess.Run(nameof(TabPlacementScenario));
}

public static class TabPlacementScenario
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "shell-tab-placement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = RuntimeComponentProcess.StartApplication();
        var settings = new Dictionary<string, string>
        {
            ["HomeFolder"] = root, ["EscQuit"] = "quit", ["RememberTabs"] = "0",
            ["CheckForUpdatesOnStartup"] = "0", ["CloseToTray"] = "0"
        };
        ThemeManager.GetSetting = key => settings.TryGetValue(key, out string value) ? value : null;
        ThemeManager.SetSetting = (key, value) => settings[key] = value;
        ThemeManager.Initialize();
        MainWindow? window = null;
        try
        {
            window = Open();
            var left = (FilePane)window.FindName("LeftPane");
            var right = (FilePane)window.FindName("RightPane");
            window.ToggleDualPane();
            ShellWindowFixture.PumpUntil(() => right.Active.IsBrowsing);
            window.FocusPane(left);
            for (int index = 0; index < 8; index++) left.NewTabBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ShellWindowFixture.PumpUntil(() => left.TabOverflowBtn.IsVisible);
            window.UpdateLayout();
            double stripRight = left.TabStripHost.TranslatePoint(new Point(left.TabStripHost.ActualWidth, 0), left.TabBar).X;
            double overflowLeft = left.TabOverflowBtn.TranslatePoint(new Point(), left.TabBar).X;
            Assert.True(stripRight <= overflowLeft + 1,
                "The tab strip must leave room for both the New Tab and overflow buttons.");
            AssertPlacement(window, bottom: false);
            Assert.True(PaneKeyboardScenario.Raise(window, Key.Down, ModifierKeys.Alt | ModifierKeys.Shift).Handled);
            AssertPlacement(window, bottom: true);

            var active = MenuBarTabBlendScenario.TabBorder(left, left.Active);
            foreach (var target in new FrameworkElement[] { active, left.TabBar, left.NewTabBtn, left.TabOverflowBtn })
            {
                Choose(window, target, "Str_Layout_TabsTop");
                AssertPlacement(window, bottom: false);
                Choose(window, target, "Str_Layout_TabsBottom");
                AssertPlacement(window, bottom: true);
            }
            Assert.True(PaneKeyboardScenario.Raise(window, Key.Up, ModifierKeys.Alt | ModifierKeys.Shift).Handled);
            AssertPlacement(window, bottom: false);
            foreach (var theme in new[] { Theme.Sepulchre, Theme.SE98 })
            {
                ThemeManager.Apply(theme);
                PaneKeyboardScenario.Raise(window, Key.Down, ModifierKeys.Alt | ModifierKeys.Shift);
                AssertPlacement(window, bottom: true);
                Assert.Equal(0, left.ResultsPane.Margin.Top);
                Assert.True(left.ResultsPane.Margin.Bottom < 0);
                Assert.Equal(0, left.ResultsPane.CornerRadius.BottomLeft);
                Assert.Equal(0, left.ResultsPane.CornerRadius.BottomRight);
                var selected = MenuBarTabBlendScenario.TabBorder(left, left.Active);
                Assert.Equal(0, selected.Margin.Top);
                Assert.Equal(2, selected.CornerRadius.TopLeft);
                Assert.Equal(2, selected.CornerRadius.TopRight);
            }
            Assert.Contains(settings, pair => pair.Key.StartsWith("WindowView.")
                && pair.Key.EndsWith(".TabsAtBottom") && pair.Value == "1");
            Close(window);
            window = Open();
            AssertPlacement(window, bottom: true);
            PaneKeyboardScenario.Raise(window, Key.Up, ModifierKeys.Alt | ModifierKeys.Shift);
            AssertPlacement(window, bottom: false);
        }
        finally
        {
            if (window?.IsVisible == true) Close(window);
            app.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previousContext);
            Directory.Delete(root, true);
        }
    }

    private static MainWindow Open()
    {
        var window = new MainWindow { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        ShellWindowFixture.PumpUntil(() => window.Pane.Active.IsBrowsing);
        return window;
    }

    private static void Close(MainWindow window)
    {
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        ShellWindowFixture.PumpUntil(() => closed);
    }

    private static void AssertPlacement(MainWindow window, bool bottom)
    {
        window.UpdateLayout();
        foreach (var pane in new[] { (FilePane)window.FindName("LeftPane"), (FilePane)window.FindName("RightPane") })
        {
            Assert.Equal(bottom ? 2 : 0, Grid.GetRow(pane.TabBar));
            Assert.Equal(1, Grid.GetRow(pane.ResultsPane));
            if (!pane.IsVisible || !pane.TabBar.IsVisible) continue;
            double tabTop = pane.TabBar.TranslatePoint(new Point(), pane).Y;
            double contentTop = pane.ResultsPane.TranslatePoint(new Point(), pane).Y;
            Assert.True(bottom ? tabTop > contentTop : tabTop < contentTop);
            var face = MenuBarTabBlendScenario.TabBorder(pane, pane.Active);
            double faceTop = face.TranslatePoint(new Point(), pane).Y;
            double faceBottom = face.TranslatePoint(new Point(0, face.ActualHeight), pane).Y;
            Assert.Equal(bottom, faceTop > faceBottom);
            var title = MenuBarTabBlendScenario.Descendants<TextBlock>(face).Single(item => item.Name == "tabLbl");
            var close = MenuBarTabBlendScenario.Descendants<Button>(face).Single(item => item.Name == "tabClose");
            foreach (var content in new FrameworkElement[] { title, close })
                Assert.True(content.TranslatePoint(new Point(0, content.ActualHeight), pane).Y
                    > content.TranslatePoint(new Point(), pane).Y, "Tab labels and controls must stay upright.");
            var hit = pane.InputHitTest(close.TranslatePoint(new Point(close.ActualWidth / 2, close.ActualHeight / 2), pane))
                as DependencyObject;
            while (hit != null && !ReferenceEquals(hit, close)) hit = VisualTreeHelper.GetParent(hit);
            Assert.Same(close, hit);
        }
    }

    private static void Choose(MainWindow window, FrameworkElement target, string key)
    {
        DependencyObject? current = target;
        ContextMenu? menu = null;
        while (current != null && menu == null)
        {
            menu = (current as FrameworkElement)?.ContextMenu;
            current = VisualTreeHelper.GetParent(current);
        }
        Assert.NotNull(menu);
        menu!.PlacementTarget = target;
        menu.IsOpen = true;
        ShellWindowFixture.PumpUntil(() => menu.IsOpen);
        var choices = menu.Items.OfType<MenuItem>().Where(item => Equals(item.Header, window.FindResource("Str_Layout_TabsTop"))
            || Equals(item.Header, window.FindResource("Str_Layout_TabsBottom"))).ToArray();
        Assert.Equal(2, choices.Length);
        Assert.Single(choices, item => item.IsChecked);
        choices.Single(item => Equals(item.Header, window.FindResource(key)))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
    }
}
