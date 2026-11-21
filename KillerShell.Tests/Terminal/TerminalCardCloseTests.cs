using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class TerminalCardCloseTests
{
    [Fact]
    public Task TheTerminalCardHasNoCloseButton()
        => RuntimeComponentProcess.Run(nameof(TerminalCardCloseScenario));
}

public static class TerminalCardCloseScenario
{
    // A lone X on a card reads as "close this menu", and one click on it ended a running shell.
    // The card carries no Close at all: Ctrl+W and the tab's own close button close the terminal.
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        var terminal = new TerminalControl(TerminalSkin.Default);
        terminal.CardHeaderSource = () => (null, "PowerShell", "C:\\");
        var window = new Window { Content = terminal, Width = 800, Height = 500, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            ShellWindowFixture.PumpUntil(() => terminal.IsLoaded);
            var args = (ContextMenuEventArgs)typeof(ContextMenuEventArgs).GetConstructor(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
                new[] { typeof(object), typeof(bool) }, null)!.Invoke(new object[] { terminal, true });
            args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
            terminal.RaiseEvent(args);
            ShellWindowFixture.PumpUntil(() => terminal.ContextMenu is { IsOpen: true, IsVisible: true });
            var card = terminal.ContextMenu!;

            var head = card.Items.OfType<MenuItem>().Single(item => item.Tag as string == "cardhead");
            Assert.Empty(Logical((DependencyObject)head.Header).OfType<ButtonBase>());
            var tiles = KillerShell.Controls.CardMenus.Buttons(card).Where(b => b.CommandParameter is string).ToList();
            // Three full rows: Fonts and Reset prompt start in the customizable card's Hidden drawer.
            Assert.Equal(9, tiles.Count);
            var close = app.FindResource("Str_Term_Close");
            Assert.DoesNotContain(tiles, b => Equals(AutomationProperties.GetName(b), close));
            Assert.DoesNotContain(card.Items.OfType<MenuItem>(), item => Equals(item.Header, close));
            card.IsOpen = false;
        }
        finally
        {
            window.Close();
            app.Shutdown();
        }
    }

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var deeper in Logical(child)) yield return deeper;
        }
    }
}
