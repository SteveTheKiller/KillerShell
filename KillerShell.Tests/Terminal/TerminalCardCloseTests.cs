using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
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
    public Task CloseSitsAtTheTopRightOfTheTerminalCard()
        => RuntimeComponentProcess.Run(nameof(TerminalCardCloseScenario));
}

public static class TerminalCardCloseScenario
{
    // Close lives in the card's header, top right, so the quick row keeps five buttons and the
    // card stays narrow. It closes the card and asks the window to close the tab, every time the
    // card opens.
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        var terminal = new TerminalControl(TerminalSkin.Default);
        terminal.CardHeaderSource = () => (null, "PowerShell", "C:\\");
        int closes = 0;
        terminal.MenuCommand += command => { if (command == TerminalMenuCommand.CloseTab) closes++; };
        var window = new Window { Content = terminal, Width = 800, Height = 500, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            ShellWindowFixture.PumpUntil(() => terminal.IsLoaded);
            for (int pass = 1; pass <= 2; pass++)
            {
                var args = (ContextMenuEventArgs)typeof(ContextMenuEventArgs).GetConstructor(
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
                    new[] { typeof(object), typeof(bool) }, null)!.Invoke(new object[] { terminal, true });
                args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
                terminal.RaiseEvent(args);
                ShellWindowFixture.PumpUntil(() => terminal.ContextMenu is { IsOpen: true, IsVisible: true });
                var card = terminal.ContextMenu!;
                var head = card.Items.OfType<MenuItem>().Single(item => item.Tag as string == "cardhead");
                var close = Assert.Single(Logical((DependencyObject)head.Header).OfType<Button>());
                Assert.Equal(Dock.Right, DockPanel.GetDock(close));
                Assert.Equal(VerticalAlignment.Top, close.VerticalAlignment);
                Assert.Equal(app.FindResource("Str_Term_Close"), AutomationProperties.GetName(close));

                var quick = card.Items.OfType<MenuItem>().Single(item => item.Tag as string == "cardquick");
                var strip = Assert.IsType<UniformGrid>(((Border)quick.Header).Child);
                Assert.Equal(5, strip.Children.Count);
                Assert.DoesNotContain(strip.Children.OfType<FrameworkElement>(),
                    b => Equals(AutomationProperties.GetName(b), app.FindResource("Str_Term_Close")));

                ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(close).GetPattern(PatternInterface.Invoke)).Invoke();
                ShellWindowFixture.PumpUntil(() => !card.IsOpen && closes == pass);
            }
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
