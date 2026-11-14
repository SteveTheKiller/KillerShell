using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class ShortcutDocumentationTests
{
    [Fact]
    public Task ShortcutsCardDocumentsTheMenuKeyInItsListAndKeyboard()
        => RuntimeComponentProcess.Run(nameof(ShortcutDocumentationScenario));
}

public static class ShortcutDocumentationScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        window.Opacity = 1;
        var shortcuts = Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "?"));
        Invoke(shortcuts);
        var overlay = (FrameworkElement)window.FindName("ShortcutsOverlay");
        var list = (StackPanel)window.FindName("ShortcutListHost");
        ShellWindowFixture.PumpUntil(() => overlay.IsVisible && list.IsVisible && list.ActualHeight > 0);
        var gesture = Assert.Single(Descendants(list).OfType<TextBlock>(), text => text.Text == "Menu (Apps)");
        gesture.BringIntoView();
        window.UpdateLayout();
        Assert.True(gesture.IsVisible);
        var row = (Grid)gesture.Parent;
        Assert.Contains(row.Children.OfType<TextBlock>(), text => Grid.GetRow(text) == Grid.GetRow(gesture)
            && Grid.GetColumn(text) == 1 && text.Text == "open the context menu" && text.IsVisible);

        Invoke((Button)window.FindName("KsViewKeyboardBtn"));
        var keyboard = (StackPanel)window.FindName("ShortcutKeyboardHost");
        ShellWindowFixture.PumpUntil(() => keyboard.IsVisible && keyboard.ActualHeight > 0);
        var cap = Assert.Single(Descendants(keyboard).OfType<TextBlock>(), text => text.Text == "\u2630");
        Assert.Contains(Descendants((Grid)cap.Parent).OfType<TextBlock>(),
            text => text.Text == "open the context menu" && text.IsVisible);
    }

    private static void Invoke(Button button)
        => ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
