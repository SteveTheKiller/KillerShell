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
public sealed class PlacesCardTests
{
    [Fact]
    public Task TheSavedPlaceCardNamesThePlaceAndOpensItInANewTab()
        => RuntimeComponentProcess.Run(nameof(PlacesCardScenario));
}

public static class PlacesCardScenario
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var add = typeof(MainWindow).GetMethod("AddBookmark", Private)!;
        add.Invoke(window, [fixture.Root]);
        add.Invoke(window, [MainWindow.ThisPc]);
        if (!(bool)typeof(MainWindow).GetField("_bookmarksOpen", Private)!.GetValue(window)!)
            typeof(MainWindow).GetMethod("BookmarksBtn_Click", Private)!.Invoke(window, [window, new RoutedEventArgs()]);

        var list = (ListBox)window.FindName("BookmarksList");
        Border Row(string path)
        {
            Border? row = null;
            ShellWindowFixture.PumpUntil(() =>
            {
                window.UpdateLayout();
                row = Descendants(list).OfType<Border>()
                    .FirstOrDefault(b => b.ContextMenu != null && b.DataContext is Bookmark bm && bm.Path == path);
                return row != null;
            }, diagnostic: () => "places=" + list.Items.Count + "; visible=" + list.IsVisible);
            return row!;
        }

        // A folder: the header names it, and the tree card's verbs are tiles.
        var folderRow = Row(fixture.Root);
        var card = Open(folderRow);
        var place = (Bookmark)folderRow.DataContext;
        Assert.Equal(place.NameLabel, AutomationProperties.GetName(Tagged(card, "cardhead")));
        var buttons = KillerShell.Controls.CardMenus.Buttons(card).Where(b => b.CommandParameter is string).ToList();
        Assert.Equal(new[] { "newtab", "explorer", "search", "terminal", "terminaladmin", "analyze", "copypath",
                             "nick", "nickdel", "properties", "remove" },
                     buttons.Select(b => (string)b.CommandParameter).ToArray());
        foreach (var button in buttons) Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(button)));
        Assert.True(KillerShell.Controls.CardMenus.TileButton(card, "remove")!.IsEnabled);

        // This PC is a place, not a folder: its name, and only Remove is lit.
        var pcCard = Open(Row(MainWindow.ThisPc));
        Assert.Equal(Visibility.Visible, Tagged(pcCard, "cardhead").Visibility);
        Assert.All(KillerShell.Controls.CardMenus.Buttons(pcCard).Where(b => b.CommandParameter is string),
                   b => Assert.Equal((string)b.CommandParameter == "remove", b.IsEnabled));

        // New tab runs once the card has closed and opens the place in a tab of its own.
        card = Open(folderRow);
        card.IsOpen = true;
        buttons = KillerShell.Controls.CardMenus.Buttons(card).Where(b => b.CommandParameter is string).ToList();
        int before = window.Pane.Tabs.Count;
        buttons[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        ShellWindowFixture.PumpUntil(() => window.Pane.Tabs.Count == before + 1
                                           && window.Pane.Active.CurrentFolder == fixture.Root,
            diagnostic: () => "tabs=" + window.Pane.Tabs.Count + "; folder=" + window.Pane.Active.CurrentFolder);
        Assert.False(card.IsOpen);
    }

    // The same opening a right-click on the row raises.
    private static ContextMenu Open(Border row)
    {
        var ctor = typeof(ContextMenuEventArgs).GetConstructors(Private)
            .First(c => c.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(object), typeof(bool) }));
        var args = (ContextMenuEventArgs)ctor.Invoke([row, true]);
        args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
        row.RaiseEvent(args);
        Assert.False(args.Handled);
        var card = row.ContextMenu!;
        card.PlacementTarget = row;
        return card;
    }

    private static Control Tagged(ContextMenu card, string tag)
        => Assert.Single(card.Items.OfType<Control>(), item => item.Tag as string == tag);

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
