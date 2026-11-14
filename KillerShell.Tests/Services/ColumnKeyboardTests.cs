using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class ColumnKeyboardTests
{
    [Fact]
    public Task HeaderAppsCanToggleAndPersistAColumn()
        => RuntimeComponentProcess.Run(nameof(ColumnKeyboardScenario));

    [Fact]
    public Task FilesHeaderAppsCanToggleAColumn()
        => RuntimeComponentProcess.Run(nameof(FilesColumnKeyboardScenario));
}

public static class FilesColumnKeyboardScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var pane = (FilePane)fixture.Window.FindName("LeftPane");
        fixture.Window.FocusPane(pane);
        var button = (Button)pane.FindName("ViewDetailsBtn");
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();
        var header = (FrameworkElement)pane.FindName("DetailsHeader");
        ShellWindowFixture.PumpUntil(() => header.IsVisible);
        Assert.True(header.Focusable, "The Files header must be reachable by keyboard.");
        var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(header),
            Environment.TickCount, Key.Apps) { RoutedEvent = Keyboard.PreviewKeyUpEvent };
        header.RaiseEvent(input);
        Assert.True(input.Handled, "Apps on the Files header must open its chooser.");
        var menu = header.ContextMenu;
        Assert.NotNull(menu);
        Assert.True(menu.IsOpen);
        var size = menu.Items.OfType<MenuItem>().Single(item =>
            item.Header as string == fixture.Window.FindResource("Str_Col_Size") as string);
        Assert.True(pane.ViewState.SizeVisible);
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(size).GetPattern(PatternInterface.Invoke)).Invoke();
        ShellWindowFixture.PumpUntil(() => !pane.ViewState.SizeVisible);
        menu.IsOpen = false;
    }
}

public static class ColumnKeyboardScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        var settings = new Dictionary<string, string>();
        ThemeManager.GetSetting = key => settings.TryGetValue(key, out var value) ? value : null;
        ThemeManager.SetSetting = (key, value) => settings[key] = value;
        var grid = new DataGrid { AutoGenerateColumns = false };
        var column = new DataGridTextColumn { Header = "Size" };
        grid.Columns.Add(column);
        ColumnVisibilityMenu.AttachTo(grid, "KeyboardFixture", (column, "Size", "Str_Col_Size", true));
        var window = new Window { Content = grid, Width = 500, Height = 300,
            Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var header = Descendants(grid).OfType<DataGridColumnHeader>().Single(h => h.Column == column);
            Assert.True(header.Focusable, "The header must be reachable by keyboard.");
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(header),
                Environment.TickCount, Key.Apps) { RoutedEvent = Keyboard.PreviewKeyUpEvent };
            header.RaiseEvent(input);
            Assert.True(input.Handled, "Apps on a column header must open the column chooser.");
            var menu = header.ContextMenu;
            Assert.NotNull(menu);
            Assert.True(menu.IsOpen);
            var item = Assert.Single(menu.Items.OfType<MenuItem>());
            var peer = UIElementAutomationPeer.CreatePeerForElement(item);
            var toggle = Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle));
            toggle.Toggle();
            Assert.Equal(Visibility.Collapsed, column.Visibility);
            toggle.Toggle();
            Assert.Equal(Visibility.Visible, column.Visibility);
            var invoke = (IInvokeProvider)peer.GetPattern(PatternInterface.Invoke);
            invoke.Invoke();
            ShellWindowFixture.PumpUntil(() => column.Visibility == Visibility.Collapsed);
            menu.IsOpen = false;
            var reopened = new DataGridTextColumn();
            var reopenedGrid = new DataGrid();
            reopenedGrid.Columns.Add(reopened);
            ColumnVisibilityMenu.AttachTo(reopenedGrid, "KeyboardFixture", (reopened, "Size", "Str_Col_Size", true));
            Assert.Equal(Visibility.Collapsed, reopened.Visibility);
        }
        finally { window.Close(); app.Shutdown(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var next in Descendants(child)) yield return next;
        }
    }
}
