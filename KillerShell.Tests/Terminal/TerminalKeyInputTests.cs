using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class TerminalKeyInputTests
{
    [Fact]
    public Task SystemKeysReachConPtyAndAncestorCommandsKeepPrecedence()
        => RuntimeComponentProcess.Run(nameof(TerminalKeyInputScenario));
}

public static class TerminalKeyInputScenario
{
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "terminal-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string result = Path.Combine(root, "keys.txt");
        string ready = Path.Combine(root, "ready");
        string script = Path.Combine(root, "read.ps1");
        File.WriteAllText(script, "[IO.File]::WriteAllText('" + ready.Replace("'", "''") + "','ready'); " +
            "$keys=@(); 1..3 | ForEach-Object { $k=[Console]::ReadKey($true); " +
            "$keys+=([string]$k.Key+':'+[string]$k.Modifiers+':'+[int]$k.KeyChar) }; " +
            "[IO.File]::WriteAllLines('" + result.Replace("'", "''") + "',[string[]]$keys)", Encoding.UTF8);
        var app = RuntimeComponentProcess.StartApplication();
        var terminal = new TerminalControl(TerminalSkin.Default);
        var grid = new Grid();
        grid.Children.Add(terminal);
        using var source = new HwndSource(new HwndSourceParameters("KillerShell routed terminal input fixture")
            { WindowStyle = unchecked((int)0x80000000), Width = 800, Height = 500 });
        byte[] original = new byte[256];
        Assert.True(GetKeyboardState(original));
        try
        {
            source.RootVisual = grid;
            grid.Measure(new Size(800, 500));
            grid.Arrange(new Rect(0, 0, 800, 500));
            terminal.Start("powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"", root);
            Wait(() => File.Exists(ready), "child readiness", () => TerminalFixtureOutput.Capture(terminal));
            int commands = 0;
            grid.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.System && e.SystemKey == Key.T) { commands++; e.Handled = true; }
            };
            KeyEventArgs Raise(Key key, ModifierKeys modifiers, bool system = false)
            {
                var state = new byte[256];
                if ((modifiers & ModifierKeys.Alt) != 0) state[0x12] = state[0xA4] = 0x80;
                if ((modifiers & ModifierKeys.Control) != 0) state[0x11] = state[0xA2] = 0x80;
                if ((modifiers & ModifierKeys.Shift) != 0) state[0x10] = state[0xA0] = 0x80;
                Assert.True(SetKeyboardState(state));
                Assert.Equal(modifiers, Keyboard.Modifiers);
                var e = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                // WPF marks Alt input as System before routing. This is the WPF event boundary,
                // not a replacement of TerminalControl's key mapping or ConPTY transport.
                if (system) typeof(KeyEventArgs).GetMethod("MarkSystem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(e, null);
                terminal.RaiseEvent(e);
                return e;
            }
            Assert.True(Raise(Key.B, ModifierKeys.Alt, true).Handled, "Alt+B was dropped at the WPF SystemKey boundary.");
            Assert.True(Raise(Key.T, ModifierKeys.Alt, true).Handled);
            Assert.Equal(1, commands);
            Assert.True(Raise(Key.Left, ModifierKeys.Control).Handled);
            Assert.False(Raise(Key.X, ModifierKeys.None).Handled);
            var text = new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                new TextComposition(InputManager.Current, terminal, "x")) { RoutedEvent = TextCompositionManager.TextInputEvent };
            terminal.RaiseEvent(text);
            Assert.True(text.Handled);
            Wait(() => File.Exists(result), "three decoded keys", () => TerminalFixtureOutput.Capture(terminal));
            Assert.Equal(new[] { "B:Alt:98", "LeftArrow:Control:0", "X:0:120" }, File.ReadAllLines(result));
        }
        finally
        {
            SetKeyboardState(original);
            terminal.Close(); source.RootVisual = null; app.Shutdown();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static void Wait(Func<bool> complete, string stage, Func<string> diagnostics)
    {
        var elapsed = Stopwatch.StartNew();
        while (!complete() && elapsed.ElapsedMilliseconds < 10000)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        Assert.True(complete(), "The real ConPTY keyboard fixture did not complete: " + stage + ".\n" + diagnostics());
    }
}
