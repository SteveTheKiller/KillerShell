using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class ColumnMouseFocusTests
{
    [Fact]
    public Task AFullAppsPressLeavesTheFileColumnChooserOpen()
        => RuntimeComponentProcess.Run(nameof(FileColumnAppsReleaseScenario));

    [Fact]
    public Task ColumnHeadingsHaveTheirOwnNameAndKeyboardHelp()
        => RuntimeComponentProcess.Run(nameof(ColumnHeaderNameScenario));

    [Fact]
    public Task MouseColumnChooserReturnsFocusToTheProcessRows()
        => RuntimeComponentProcess.Run(nameof(ProcessColumnMouseFocusScenario));

    [Fact]
    public Task MouseColumnChooserReturnsFocusToTheFileRows()
        => RuntimeComponentProcess.Run(nameof(FileColumnMouseFocusScenario));
}

public static class FileColumnAppsReleaseScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        window.Opacity = 1;
        window.Activate();
        var pane = (FilePane)window.FindName("LeftPane");
        window.FocusPane(pane);
        var details = (Button)pane.FindName("ViewDetailsBtn");
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(details)
            .GetPattern(PatternInterface.Invoke)).Invoke();
        var header = (FrameworkElement)pane.FindName("DetailsHeader");
        ShellWindowFixture.PumpUntil(() => window.IsActive && header.IsVisible);
        Assert.True(header.Focus(), "Column headings must accept keyboard focus.");
        ShellWindowFixture.PumpUntil(() => header.IsKeyboardFocusWithin);
        TerminalAppsReleaseScenario.Stroke(window, 0x5D, Key.Apps);
        ShellWindowFixture.PumpUntil(() => header.ContextMenu is { IsOpen: true, IsVisible: true },
            diagnostic: () => "A complete native Apps press must leave the file column chooser open.");
        var menu = header.ContextMenu;
        TerminalAppsReleaseScenario.Stroke(window, 0x1B, Key.Escape);
        ShellWindowFixture.PumpUntil(() => !menu.IsOpen && header.IsKeyboardFocusWithin);
        Assert.Null(header.ContextMenu);
    }
}

public static class ColumnHeaderNameScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var pane = (FilePane)fixture.Window.FindName("LeftPane");
        var header = (FrameworkElement)pane.FindName("DetailsHeader");
        Assert.Equal("Column headings", AutomationProperties.GetName(header));
        Assert.Equal("Press Menu (Apps) to choose visible columns.", AutomationProperties.GetHelpText(header));
    }
}

public static class ProcessColumnMouseFocusScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        window.Opacity = 1;
        window.Activate();
        window.OpenTaskManager();
        DataGrid? grid = null;
        ShellWindowFixture.PumpUntil(() =>
        {
            grid = SearchFlyoutLayoutProbe.Descendants<DataGrid>(window.Pane).FirstOrDefault(item => item.IsVisible);
            return grid != null && grid.Items.Count > 0;
        });
        grid!.SelectedIndex = 0;
        grid.ScrollIntoView(grid.SelectedItem);
        window.UpdateLayout();
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
        Assert.NotNull(row);
        window.Activate();
        ShellWindowFixture.PumpUntil(() => window.IsActive);
        var cell = SearchFlyoutLayoutProbe.Descendants<DataGridCell>(row)
            .First(item => item.IsVisible && item.Focusable);
        Assert.True(cell.Focus(), "The selected process cell must accept focus before opening its column menu.");
        ShellWindowFixture.PumpUntil(() => row.IsKeyboardFocusWithin);
        var header = SearchFlyoutLayoutProbe.Descendants<DataGridColumnHeader>(grid)
            .First(item => item.Column != null && item.IsVisible && item.ActualWidth > 0);
        header.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount,
            MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseUpEvent });
        var menu = header.ContextMenu;
        Assert.NotNull(menu);
        ShellWindowFixture.PumpUntil(() => menu.IsOpen);
        var column = menu.Items.OfType<MenuItem>().First();
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(column)
            .GetPattern(PatternInterface.Invoke)).Invoke();
        menu.IsOpen = false;
        ShellWindowFixture.PumpUntil(() => !menu.IsOpen);
        Assert.True(row.IsKeyboardFocusWithin,
            "Closing a mouse-opened column chooser must preserve process command focus.");
        Assert.Null(header.ContextMenu);
    }
}

public static class FileColumnMouseFocusScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        File.WriteAllText(Path.Combine(fixture.Root, "focus.txt"), "column focus");
        var pane = (FilePane)window.FindName("LeftPane");
        window.Opacity = 1;
        window.Activate();
        window.FocusPane(pane);
        var details = (Button)pane.FindName("ViewDetailsBtn");
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(details)
            .GetPattern(PatternInterface.Invoke)).Invoke();
        ShellWindowFixture.PumpUntil(() => pane.ResultsList.Items.Count > 0);
        pane.ResultsList.SelectedIndex = 0;
        pane.ResultsList.ScrollIntoView(pane.ResultsList.SelectedItem);
        window.UpdateLayout();
        var row = (ListBoxItem)pane.ResultsList.ItemContainerGenerator.ContainerFromIndex(0);
        Assert.NotNull(row);
        Assert.True(row.Focus());
        ShellWindowFixture.PumpUntil(() => pane.ResultsList.IsKeyboardFocusWithin);
        var header = (FrameworkElement)pane.FindName("DetailsHeader");
        header.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount,
            MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonUpEvent });
        var menu = header.ContextMenu;
        Assert.NotNull(menu);
        ShellWindowFixture.PumpUntil(() => menu.IsOpen);
        var size = menu.Items.OfType<MenuItem>().Single(item =>
            Equals(item.Header, window.FindResource("Str_Col_Size")));
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(size)
            .GetPattern(PatternInterface.Invoke)).Invoke();
        menu.IsOpen = false;
        ShellWindowFixture.PumpUntil(() => !menu.IsOpen);
        Assert.True(pane.ResultsList.IsKeyboardFocusWithin,
            "Closing a mouse-opened column chooser must preserve file command focus.");
        Assert.Null(header.ContextMenu);
    }
}
