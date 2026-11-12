using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class FileViewBehaviorTests
{
    [Fact]
    public Task StandardSortChoiceDefaultsToNewestAndKeepsDirectionControl()
        => RuntimeComponentProcess.Run(nameof(FileSortBehaviorScenario));

    [Fact]
    public Task ZoomCyclesThroughEveryLayoutAndWrapsBack()
        => RuntimeComponentProcess.Run(nameof(FileViewBehaviorScenario));

    [Fact]
    public Task WindowRestoresAndWritesItsOwnViewAndSortPreferences()
        => RuntimeComponentProcess.Run(nameof(FileWindowPreferencesScenario));

    [Fact]
    public Task EachFolderKeepsItsOwnSortAndOthersOpenNewestFirst()
        => RuntimeComponentProcess.Run(nameof(FolderSortScenario));
}

public static class FolderSortScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = window.Pane;
        var sort = (ComboBox)pane.FindName("SortChoice");
        string named = Path.Combine(fixture.Root, "named");
        string other = Path.Combine(fixture.Root, "other");
        Directory.CreateDirectory(named);
        Directory.CreateDirectory(other);

        void Go(string folder)
        {
            var task = (Task)TitlePathInputScenario.Invoke(window, "NavigateTo", folder, true, false, false)!;
            ShellWindowFixture.PumpUntil(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
        }

        Go(named);
        Assert.Equal(4, pane.Active.SortIndex);
        Assert.False(pane.Active.SortAsc);
        sort.SelectedIndex = 1;   // Name, A first

        Go(other);
        Assert.Equal(4, pane.Active.SortIndex);
        Assert.False(pane.Active.SortAsc);
        Assert.Equal(4, sort.SelectedIndex);

        Go(named);
        Assert.Equal(1, pane.Active.SortIndex);
        Assert.True(pane.Active.SortAsc);
        Assert.Equal(1, sort.SelectedIndex);

        // Flipping the direction is remembered too.
        ((Button)pane.FindName("SortDirButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Go(other);
        Go(named);
        Assert.Equal(1, pane.Active.SortIndex);
        Assert.False(pane.Active.SortAsc);
    }
}

public static class FileSortBehaviorScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = window.Pane;
        Assert.Equal(4, pane.Active.SortIndex);
        Assert.False(pane.Active.SortAsc);
        var sort = Assert.IsType<ComboBox>(pane.FindName("SortChoice"));
        Assert.Equal(4, sort.SelectedIndex);
        sort.SelectedIndex = 1;
        Assert.Equal(1, pane.Active.SortIndex);
        Assert.True(pane.Active.SortAsc);
        ((Button)pane.FindName("SortDirButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(pane.Active.SortAsc);
        Assert.Equal(Visibility.Collapsed, ((ComboBoxItem)sort.Items[0]).Visibility);

        pane.LocationRow.Width = 650;
        window.UpdateLayout();
        window.ToolStrip_SizeChanged(pane);
        window.UpdateLayout();
        Assert.Same(pane.LocationRow, pane.ScopeBar.Parent);
        Assert.Equal(0, Grid.GetRow(pane.ScopeBar));
        Assert.True(pane.OverflowPanel.Children.Count > 0);
        Assert.True(pane.ScopePathLabel.ActualWidth >= 250, "Actions must overflow before the readable address width shrinks.");

        pane.LocationRow.Width = 340;
        window.UpdateLayout();
        window.ToolStrip_SizeChanged(pane);
        window.UpdateLayout();
        Assert.True(pane.OverflowPanel.Children.Contains(pane.SortControlsHost));
        Assert.Equal(0, Grid.GetRow(pane.ScopeBar));
        TitlePathInputScenario.Invoke(window, "OpenSortMenuFromKeyboard");
        ShellWindowFixture.PumpUntil(() => sort.IsDropDownOpen);
        foreach (var key in new[] { Key.Enter, Key.Escape })
        {
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = sort };
            TitlePathInputScenario.Invoke(window, "Window_PreviewKeyDown", window, input);
            Assert.False(input.Handled, "The standard sort dropdown must own " + key + ".");
            Assert.True(sort.IsDropDownOpen);
        }
        sort.IsDropDownOpen = false;
        pane.OverflowPopup.IsOpen = false;
    }
}

public static class FileViewBehaviorScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var pane = fixture.Window.Pane;
        ((Button)pane.FindName("ViewListBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pane.ViewState.RowIconSize = 16;
        pane.ViewState.Density = 4;
        pane.ResultsList.Focus();
        PaneKeyboardScenario.Raise(pane.ResultsList, Key.OemPlus, ModifierKeys.Control);
        Assert.Equal(0, pane.ViewMode);
        Assert.Equal(20, pane.ViewState.RowIconSize);
        for (int i = 0; i < 6; i++) PaneKeyboardScenario.Raise(pane.ResultsList, Key.OemPlus, ModifierKeys.Control);
        Assert.Equal(2, pane.ViewMode);
        Assert.Equal(16, pane.ViewState.RowIconSize);
        Assert.Equal(4, pane.ViewState.Density);
        for (int i = 0; i < 7; i++) PaneKeyboardScenario.Raise(pane.ResultsList, Key.OemPlus, ModifierKeys.Control);
        Assert.Equal(1, pane.ViewMode);
        Assert.Equal(32, pane.ViewState.TileSize);
        for (int i = 0; i < 7; i++) PaneKeyboardScenario.Raise(pane.ResultsList, Key.OemPlus, ModifierKeys.Control);
        Assert.Equal(0, pane.ViewMode);
        Assert.Equal(16, pane.ViewState.RowIconSize);
        PaneKeyboardScenario.Raise(pane.ResultsList, Key.OemMinus, ModifierKeys.Control);
        Assert.Equal(1, pane.ViewMode);
        Assert.Equal(256, pane.ViewState.TileSize);
    }
}

public static class FileWindowPreferencesScenario
{
    public static void Run()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = RuntimeComponentProcess.StartApplication();
        string root = Path.Combine(Path.GetTempPath(), "shell-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = new Dictionary<string, string>
        {
            ["HomeFolder"] = root, ["EscQuit"] = "quit", ["RememberTabs"] = "0",
            ["CheckForUpdatesOnStartup"] = "0", ["CloseToTray"] = "0",
            ["ResultsViewL"] = "0", ["ResultsSortL"] = "1|1",
        };
        string? Read(string key) => settings.TryGetValue(key, out var value) ? value : null;
        void Write(string key, string value) => settings[key] = value;
        string viewKey, sortKey;
        // A browsed folder takes its own remembered sort, not the window's, which is left for
        // search results.
        settings["FolderSorts"] = "3|0|" + root;
        using (var saved = new WindowViewSettings(MainWindow.IsElevated ? "admin" : "normal", Read, Write))
        {
            saved.Set("ResultsViewL", "2");
            saved.Set("ResultsSortL", "5|1");
            saved.Set("ResultsRowIconSizeL", "24");
            viewKey = saved.Key("ResultsViewL");
            sortKey = saved.Key("ResultsSortL");
        }
        ThemeManager.GetSetting = Read;
        ThemeManager.SetSetting = Write;
        ThemeManager.Initialize();
        MainWindow? window = null;
        try
        {
            window = Open();
            Assert.Equal(2, window.Pane.ViewMode);
            Assert.Equal(3, window.Pane.Active.SortIndex);
            Assert.False(window.Pane.Active.SortAsc);
            Assert.Equal(24, window.Pane.ViewState.RowIconSize);
            Click(window, "ViewIconsBtn");
            ((ComboBox)window.Pane.FindName("SortChoice")).SelectedIndex = 1;
            Assert.Equal("1", Read(viewKey));
            Assert.Equal("1|1|" + root, Read("FolderSorts"));
            Assert.Equal("5|1", Read(sortKey));
            Assert.Equal("0", Read("ResultsViewL"));
        }
        finally
        {
            if (window != null) Close(window);
            app.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previousContext);
            Directory.Delete(root);
        }
    }

    private static MainWindow Open()
    {
        var window = new MainWindow { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        ShellWindowFixture.PumpUntil(() => window.Pane.Active.IsBrowsing);
        return window;
    }

    private static void Click(MainWindow window, string name)
        => ((Button)window.Pane.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Close(MainWindow window)
    {
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        ShellWindowFixture.PumpUntil(() => closed);
    }
}
