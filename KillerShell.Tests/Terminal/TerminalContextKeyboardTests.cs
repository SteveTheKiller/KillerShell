using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class TerminalContextKeyboardTests
{
    [Fact]
    public Task AppsOpensTheTerminalCardAnchoredToTheTerminal()
        => RuntimeComponentProcess.Run(nameof(TerminalContextKeyboardScenario));
}

public static class TerminalContextKeyboardScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        var terminal = new TerminalControl(TerminalSkin.Default);
        var window = new Window { Content = terminal, Width = 800, Height = 500,
            ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            window.Activate();
            terminal.Focus();
            ShellWindowFixture.PumpUntil(() => window.IsActive && terminal.IsKeyboardFocusWithin);
            var text = (ITextProvider)UIElementAutomationPeer.CreatePeerForElement(terminal)
                .GetPattern(PatternInterface.Text);
            var caret = Assert.Single(text.GetSelection()).GetBoundingRectangles();
            Assert.Equal(4, caret.Length);
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(terminal),
                Environment.TickCount, Key.Apps) { RoutedEvent = Keyboard.PreviewKeyUpEvent };
            terminal.RaiseEvent(key);
            Assert.True(key.Handled, "Apps must open the terminal context card.");
            Assert.NotNull(terminal.ContextMenu);
            Assert.True(terminal.ContextMenu.IsOpen);
            Assert.Same(terminal, terminal.ContextMenu.PlacementTarget);
            Assert.Equal(PlacementMode.Bottom, terminal.ContextMenu.Placement);
            Rect anchor = terminal.ContextMenu.PlacementRectangle;
            Assert.False(anchor.IsEmpty, "The keyboard card must be anchored to the caret cell.");
            Point position = terminal.PointToScreen(anchor.TopLeft);
            Assert.InRange(position.X, caret[0] - 1, caret[0] + 1);
            Assert.InRange(position.Y, caret[1] - 1, caret[1] + 1);
            Assert.True(anchor.Height > 0 && anchor.Height < terminal.ActualHeight / 4,
                "The anchor must cover a caret cell rather than the terminal surface.");
            Assert.True(terminal.ContextMenu.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)),
                "The opened card must admit keyboard focus.");
            terminal.ContextMenu.IsOpen = false;
        }
        finally { terminal.Close(); window.Close(); app.Shutdown(); }
    }
}
