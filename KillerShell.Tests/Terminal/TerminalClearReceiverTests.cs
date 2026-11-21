using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class TerminalClearReceiverTests
{
    [Fact]
    public Task ToolbarClearRemovesHistoryWithoutSubmittingPendingReaderInput()
        => RuntimeComponentProcess.Run(nameof(TerminalToolbarClearReceiverScenario));

    [Fact]
    public Task CardClearRemovesHistoryWithoutSendingTextEnterOrInterruptToRunningReader()
        => RuntimeComponentProcess.Run(nameof(TerminalClearReceiverScenario));
}

public static class TerminalToolbarClearReceiverScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        string result = Path.Combine(fixture.Root, "toolbar-received.txt");
        string script = Path.Combine(fixture.Root, "toolbar-reader.ps1");
        File.WriteAllText(script,
            "1..100 | ForEach-Object { [Console]::WriteLine('history '+$_) }; " +
            "[Console]::WriteLine('KS_TOOLBAR_READER_READY'); " +
            "$line=[Console]::ReadLine(); [IO.File]::WriteAllText('" + result.Replace("'", "''") + "',$line)", Encoding.UTF8);
        var window = fixture.Window;
        window.OpenShell(TerminalProfile.Cmd(), fixture.Root);
        var pane = window.Pane;
        var terminal = pane.Active.Term!;
        TerminalClearSafetyScenario.Wait(() => TerminalFixtureOutput.Capture(terminal).Contains(fixture.Root), "CMD prompt");
        terminal.Send("powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"\r");
        TerminalClearSafetyScenario.Wait(() => TerminalFixtureOutput.Capture(terminal).Contains("KS_TOOLBAR_READER_READY"), "toolbar input reader");
        Assert.True(terminal.Buffer.ScrollbackCount > 0);
        terminal.Send("before");
        TerminalClearSafetyScenario.Wait(() => TerminalClearReceiverScenario.VisibleText(terminal).Contains("before"), "pending input");
        string screen = TerminalClearReceiverScenario.VisibleText(terminal);
        var button = (Button)pane.FindName("TermClearBtn");
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(button)
            .GetPattern(PatternInterface.Invoke)).Invoke();
        TerminalClearSafetyScenario.Pump(300);
        Assert.False(File.Exists(result), "Toolbar Clear submitted text to the running reader.");
        Assert.Equal(0, terminal.Buffer.ScrollbackCount);
        Assert.Equal(screen, TerminalClearReceiverScenario.VisibleText(terminal));
        terminal.Send("after\r");
        TerminalClearSafetyScenario.Wait(() => File.Exists(result), "explicit reader completion");
        Assert.Equal("beforeafter", File.ReadAllText(result));
    }
}

public static class TerminalClearReceiverScenario
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "terminal-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string result = Path.Combine(root, "received.txt");
        string script = Path.Combine(root, "read.ps1");
        File.WriteAllText(script,
            "1..100 | ForEach-Object { [Console]::WriteLine('history '+$_) }; " +
            "[Console]::WriteLine('BF002_READER_READY'); " +
            "$line=[Console]::ReadLine(); [IO.File]::WriteAllText('" + result.Replace("'", "''") + "',$line)", Encoding.UTF8);
        var app = RuntimeComponentProcess.StartApplication();
        var terminal = new TerminalControl(TerminalSkin.Default);
        var window = new Window { Content = terminal, Width = 800, Height = 500,
            ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            terminal.Start("powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"", root);
            TerminalClearSafetyScenario.Wait(() => TerminalFixtureOutput.Capture(terminal).Contains("BF002_READER_READY"), "running input reader");
            Assert.True(terminal.Buffer.ScrollbackCount > 0, "The history-removal check needs actual scrollback.");
            terminal.Send("before");
            TerminalClearSafetyScenario.Wait(() => VisibleText(terminal).Contains("before"), "pending reader input echo");
            string screen = VisibleText(terminal);
            int row = terminal.Buffer.CursorRow, col = terminal.Buffer.CursorCol;
            terminal.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(terminal),
                Environment.TickCount, Key.Apps) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            Assert.NotNull(terminal.ContextMenu);
            terminal.ContextMenu.UpdateLayout();
            // The card's own tile, not its hidden measuring copy (CardMenus skips that copy and the drawer).
            var clear = Assert.Single(KillerShell.Controls.CardMenus.Buttons(terminal.ContextMenu),
                button => AutomationProperties.GetName(button) == "Clear scrollback");
            var peer = UIElementAutomationPeer.CreatePeerForElement(clear);
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
            invoke.Invoke();
            TerminalClearSafetyScenario.Wait(() => !terminal.ContextMenu.IsOpen, "automation invocation closes card");
            TerminalClearSafetyScenario.Pump(300);
            Assert.False(File.Exists(result), "Clear sent Enter to the running reader.");
            Assert.Equal(0, terminal.Buffer.ScrollbackCount);
            Assert.Equal(screen, VisibleText(terminal));
            Assert.Equal(row, terminal.Buffer.CursorRow);
            Assert.Equal(col, terminal.Buffer.CursorCol);
            terminal.Send("after\r");
            TerminalClearSafetyScenario.Wait(() => File.Exists(result), "explicit reader completion");
            Assert.Equal("beforeafter", File.ReadAllText(result));
        }
        finally
        {
            terminal.Close(); window.Close(); app.Shutdown();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    internal static string VisibleText(TerminalControl terminal)
        => string.Join("\n", Enumerable.Range(terminal.Buffer.ScrollbackCount, terminal.Buffer.Rows)
            .Select(line => string.Concat(terminal.Buffer.LineAt(line).Select(cell => cell.Text))));
}
