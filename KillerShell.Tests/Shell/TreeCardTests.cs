using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
        if (!tree.IsVisible) window.ToggleTreePanel();
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

        // The same opening the tree gets from a right-click on that row.
        var ctor = typeof(ContextMenuEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .First(c => c.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(object), typeof(bool) }));
        var args = (ContextMenuEventArgs)ctor.Invoke([container, true]);
        args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
        container.RaiseEvent(args);
        Assert.False(args.Handled);

        var card = tree.ContextMenu!;
        var head = Assert.Single(card.Items.OfType<MenuItem>(), item => item.Tag as string == "cardhead");
        Assert.Equal(node.NameLabel, AutomationProperties.GetName(head));
        var quick = Assert.Single(card.Items.OfType<MenuItem>(), item => item.Tag as string == "cardquick");
        var buttons = TabCardScenario.Buttons(quick);
        Assert.Equal(new[] { "newtab", "explorer", "terminal", "search", "copypath" },
                     buttons.Select(b => (string)b.CommandParameter).ToArray());
        foreach (var button in buttons) Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(button)));

        // The rows that stayed rows keep their gestures.
        foreach (var gesture in new[] { "Ctrl+F8", "Shift+F4", "Alt+H", "Ctrl+D", "Alt+Enter", "Shift+F10" })
            Assert.Single(card.Items.OfType<MenuItem>(), item => item.InputGestureText == gesture);

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
