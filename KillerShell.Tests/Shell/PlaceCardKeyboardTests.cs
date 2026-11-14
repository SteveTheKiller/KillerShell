using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class PlaceCardKeyboardTests
{
    [Fact]
    public Task AppsOnASavedPlaceOpensItsCard()
        => RuntimeComponentProcess.Run("PlaceCardKeyboardScenario");
}

public static class PlaceCardKeyboardScenario
{
    // The saved-place card hangs on the row's inner border, but keyboard focus sits on the list
    // item around it. The Apps key on a focused place must still open that place's card.
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var list = (ListBox)window.FindName("BookmarksList");
        if (!list.IsVisible) ShellWindowFixture.Click(window, "BookmarksBtn");
        ShellWindowFixture.PumpUntil(() => list.IsVisible && list.Items.Count > 0);
        const int index = 0;
        var place = (Bookmark)list.Items[index];
        list.UpdateLayout();
        var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
        Assert.True(item.Focus(), "A saved place must take keyboard focus.");
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(item), 0, Key.Apps) { RoutedEvent = Keyboard.KeyUpEvent, Source = item });
        ContextMenu? card = null;
        ShellWindowFixture.PumpUntil(() => (card = Descendants(item).OfType<FrameworkElement>()
            .Select(e => e.ContextMenu).FirstOrDefault(m => m != null)) is { IsOpen: true });
        try
        {
            var head = Assert.Single(card!.Items.OfType<MenuItem>(), m => m.Tag as string == "cardhead");
            Assert.Equal(place.NameLabel, AutomationProperties.GetName(head));
        }
        finally { card!.IsOpen = false; }
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }
}
