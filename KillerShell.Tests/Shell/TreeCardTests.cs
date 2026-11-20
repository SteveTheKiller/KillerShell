using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class TreeCardTests
{
    [Fact]
    public Task TheTreeCardNamesTheFolderAndOpensItInANewTab()
        => RuntimeComponentProcess.Run(nameof(TreeCardScenario));
}

public static class TreeCardScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var tree = (TreeView)window.FindName("FolderTree");
        window.UpdateLayout();
        var panel = (FrameworkElement)window.FindName("TreePanel");
        if (panel.ActualWidth < 100) window.ToggleTreePanel();
        ShellWindowFixture.PumpUntil(() => panel.ActualWidth >= 160);
        TreeViewItem? container = null;
        ShellWindowFixture.PumpUntil(() =>
        {
            window.UpdateLayout();
            container = Descendants(tree).OfType<TreeViewItem>()
                .FirstOrDefault(item => item.DataContext is FolderNode { IsGroup: false } node
                                        && System.IO.Directory.Exists(node.Path));
            return container != null;
        }, diagnostic: () => "tree visible=" + tree.IsVisible + "; items=" + tree.Items.Count);
        var node = (FolderNode)container!.DataContext;

        container.IsSelected = true;
        container.Focus();
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(container), 0, Key.Apps)
        {
            RoutedEvent = Keyboard.KeyUpEvent, Source = container
        });
        ShellWindowFixture.PumpUntil(() => tree.ContextMenu.IsOpen);

        var card = tree.ContextMenu!;
        var head = Assert.Single(card.Items.OfType<MenuItem>(), item => item.Tag as string == "cardhead");
        Assert.Equal(node.NameLabel, AutomationProperties.GetName(head));
        var buttons = KillerShell.Controls.CardMenus.Buttons(card).Where(b => b.CommandParameter is string).ToList();
        Assert.Equal(new[] { "newtab", "explorer", "search", "terminal", "terminaladmin", "analyze", "copypath",
                             "fav", "hidden", "nick", "nickdel", "properties", "shellmenu" },
                     buttons.Select(b => (string)b.CommandParameter).ToArray());
        // Every tile is named and shows its shortcut.
        foreach (var button in buttons)
        {
            Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(button)));
            Assert.False(string.IsNullOrEmpty(AutomationProperties.GetAcceleratorKey(button)), (string)button.CommandParameter);
        }

        // New tab runs once the card has closed and opens the folder in a tab of its own.
        int before = window.Pane.Tabs.Count;
        buttons[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        ShellWindowFixture.PumpUntil(() => window.Pane.Tabs.Count == before + 1
                                           && window.Pane.Active.CurrentFolder == node.Path,
            diagnostic: () => "tabs=" + window.Pane.Tabs.Count + "; folder=" + window.Pane.Active.CurrentFolder);
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }
}
