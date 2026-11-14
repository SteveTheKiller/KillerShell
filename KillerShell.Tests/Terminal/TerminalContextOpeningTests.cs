using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class TerminalContextOpeningTests
{
    [Fact]
    public Task AnyContextMenuRequestOpensTheRefreshedCardAtTheCaret()
        => RuntimeComponentProcess.Run(nameof(TerminalContextOpeningScenario));
}

public static class TerminalContextOpeningScenario
{
    // Right-click and Apps build the card through the terminal's own path. Any other request
    // (Shift+F10 the shell does not take, a screen reader's context action) arrives as
    // ContextMenuOpening and must go the same way: header refreshed, card anchored at the caret.
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        var terminal = new TerminalControl(TerminalSkin.Default);
        int headerReads = 0;
        terminal.CardHeaderSource = () => { headerReads++; return (null, "PowerShell", "C:\\"); };
        var window = new Window { Content = terminal, Width = 800, Height = 500, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            terminal.Focus();
            ShellWindowFixture.PumpUntil(() => terminal.IsLoaded);
            for (int pass = 1; pass <= 2; pass++)
            {
                var args = (ContextMenuEventArgs)typeof(ContextMenuEventArgs).GetConstructor(
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
                    new[] { typeof(object), typeof(bool) }, null)!.Invoke(new object[] { terminal, true });
                args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
                terminal.RaiseEvent(args);
                ShellWindowFixture.PumpUntil(() => terminal.ContextMenu is { IsOpen: true });
                Assert.Equal(pass, headerReads);
                Assert.Equal(PlacementMode.Bottom, terminal.ContextMenu!.Placement);
                Assert.False(terminal.ContextMenu.PlacementRectangle.IsEmpty);
                terminal.ContextMenu.IsOpen = false;
                ShellWindowFixture.PumpUntil(() => !terminal.ContextMenu.IsOpen);
            }
        }
        finally
        {
            window.Close();
            app.Shutdown();
        }
    }
}
