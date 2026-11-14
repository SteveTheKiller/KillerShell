using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class TerminalClearSafetyTests
{
    [Fact]
    public Task ClearDoesNotSubmitPendingCommandAndPreservesItForExplicitEnter()
        => RuntimeComponentProcess.Run(nameof(TerminalClearSafetyScenario));
}

public static class TerminalClearSafetyScenario
{
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "terminal-clear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string result = Path.Combine(root, "submitted.txt");
        var app = RuntimeComponentProcess.StartApplication();
        var terminal = new TerminalControl(TerminalSkin.Default) { ShellKind = TerminalShellKind.Cmd };
        using var source = new HwndSource(new HwndSourceParameters("Terminal clear safety fixture")
            { WindowStyle = unchecked((int)0x80000000), Width = 800, Height = 500 });
        byte[] original = new byte[256];
        Assert.True(GetKeyboardState(original));
        try
        {
            source.RootVisual = terminal;
            terminal.Measure(new Size(800, 500));
            terminal.Arrange(new Rect(0, 0, 800, 500));
            terminal.Start("cmd.exe /d /q", root);
            Wait(() => TerminalFixtureOutput.Capture(terminal).Contains(root), "CMD prompt");
            terminal.Send("echo BF002_SUBMITTED>submitted.txt & rem ");
            // The unsubmitted command can wrap at the prompt's right edge.
            Wait(() => TerminalFixtureOutput.Capture(terminal).Replace("\r", "").Replace("\n", "")
                .Contains("BF002_SUBMITTED"), "pending command echo");
            string screen = TerminalFixtureOutput.Capture(terminal);
            int row = terminal.Buffer.CursorRow, col = terminal.Buffer.CursorCol;
            InvokeClear(terminal, source);
            Pump(400);
            Assert.False(File.Exists(result), "Clear submitted the pending command to the real CMD session.");
            Assert.Equal(row, terminal.Buffer.CursorRow);
            Assert.Equal(col, terminal.Buffer.CursorCol);
            Assert.Equal(screen, TerminalFixtureOutput.Capture(terminal));
            terminal.Send("\r");
            Wait(() => File.Exists(result), "explicit Enter submits preserved input");
            Assert.Equal("BF002_SUBMITTED", File.ReadAllText(result).Trim());
        }
        finally
        {
            SetKeyboardState(original);
            terminal.Close(); source.RootVisual = null; app.Shutdown();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    internal static void InvokeClear(TerminalControl terminal, HwndSource source)
    {
        var state = new byte[256];
        state[0x11] = state[0xA2] = state[0x10] = state[0xA0] = 0x80;
        Assert.True(SetKeyboardState(state));
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, Keyboard.Modifiers);
        Assert.True(terminal.HandleMenuShortcut(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.L)));
        Assert.True(SetKeyboardState(new byte[256]));
    }

    internal static void Wait(Func<bool> ready, string stage)
    {
        var clock = Stopwatch.StartNew();
        while (!ready() && clock.ElapsedMilliseconds < 10000) Pump(20);
        Assert.True(ready(), stage);
    }

    internal static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
