using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class TerminalInputFlowTests
{
    [Fact]
    public Task BlockedConPtyInputDoesNotBlockDispatcherOrClose()
        => RuntimeComponentProcess.Run(nameof(TerminalInputFlowScenario));

    [Fact]
    public Task OversizedInputIsRejectedWholeWithNoticeAndLaterUnicodeRemainsOrdered()
        => RuntimeComponentProcess.Run(nameof(TerminalInputOrderScenario));
}

public static class TerminalInputOrderScenario
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "terminal-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string result = Path.Combine(root, "result.txt");
        string script = Path.Combine(root, "read.ps1");
        File.WriteAllText(script, "[Console]::InputEncoding = New-Object Text.UTF8Encoding; " +
            "$a=[Console]::ReadLine(); $b=[Console]::ReadLine(); " +
            "$result='" + result.Replace("'", "''") + "'; " +
            "[IO.File]::WriteAllLines($result+'.tmp', [string[]]@($a,$b)); " +
            "[IO.File]::Move($result+'.tmp', $result)", Encoding.UTF8);
        var app = RuntimeComponentProcess.StartApplication();
        var terminal = new TerminalControl(TerminalSkin.Default);
        try
        {
            terminal.Measure(new Size(800, 500));
            terminal.Arrange(new Rect(0, 0, 800, 500));
            terminal.Start("powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"", root);
            terminal.Send(new string('x', 4 * 1024 * 1024 + 1));
            var warning = new StringBuilder();
            for (int line = 0; line < terminal.Buffer.TotalLines; line++)
                foreach (var cell in terminal.Buffer.LineAt(line))
                    if (cell.Ch != 0) warning.Append(char.ConvertFromUtf32(cell.Ch));
            Assert.Contains("Input is busy. The new text was not sent.", warning.ToString());
            terminal.Send("caf\u00e9 ");
            terminal.Send("\u65e5\u672c\r");
            terminal.Send("second\r");
            var elapsed = Stopwatch.StartNew();
            while (!File.Exists(result) && elapsed.ElapsedMilliseconds < 10000)
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
            }
            Assert.True(File.Exists(result), "The real ConPTY child did not receive the ordered input.\n"
                + TerminalFixtureOutput.Capture(terminal));
            Assert.Equal(new[] { "caf\u00e9 \u65e5\u672c", "second" }, File.ReadAllLines(result));
        }
        finally { terminal.Close(); app.Shutdown(); try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}

internal static class TerminalFixtureOutput
{
    internal static string Capture(TerminalControl terminal)
    {
        var text = new StringBuilder("Terminal output:\n");
        for (int line = 0; line < terminal.Buffer.TotalLines; line++)
        {
            foreach (var cell in terminal.Buffer.LineAt(line)) text.Append(cell.Text);
            text.AppendLine();
        }
        return text.ToString();
    }
}

public static class TerminalInputFlowScenario
{
    public static void Run()
    {
        using var finished = new ManualResetEventSlim();
        var watchdog = new Thread(() =>
        {
            if (finished.Wait(8000)) return;
            Console.Error.WriteLine("The dispatcher remained blocked while sending input or closing the real ConPTY.");
            Environment.Exit(71);
        }) { IsBackground = true, Name = "Terminal input fixture watchdog" };
        watchdog.Start();
        string root = Path.Combine(Path.GetTempPath(), "terminal-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string ready = Path.Combine(root, "pid.txt");
        var app = RuntimeComponentProcess.StartApplication();
        var terminal = new TerminalControl(TerminalSkin.Default);
        try
        {
            terminal.Measure(new Size(800, 500));
            terminal.Arrange(new Rect(0, 0, 800, 500));
            terminal.Start("powershell.exe -NoProfile -NonInteractive -Command \"$ready='" +
                ready.Replace("'", "''") + "'; [IO.File]::WriteAllText($ready+'.tmp', [string]$PID); " +
                "[IO.File]::Move($ready+'.tmp', $ready); Start-Sleep -Seconds 30\"", root);
            Assert.True(SpinWait.SpinUntil(() => File.Exists(ready), 3000), "The controlled child did not start.");
            using var child = Process.GetProcessById(int.Parse(File.ReadAllText(ready)));
            int heartbeats = 0;
            var heartbeat = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            heartbeat.Tick += (_, _) => heartbeats++;
            heartbeat.Start();
            try
            {
                var elapsed = Stopwatch.StartNew();
                terminal.Send(new string('x', 2 * 1024 * 1024));
                Assert.True(elapsed.ElapsedMilliseconds < 500, "Publishing input blocked the dispatcher.");
                var frame = new DispatcherFrame();
                var observation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                observation.Tick += (_, _) => { observation.Stop(); frame.Continue = false; };
                observation.Start();
                Dispatcher.PushFrame(frame);
                Assert.True(heartbeats >= 3, "The dispatcher heartbeat stopped while input was pending.");
                elapsed.Restart();
                terminal.Close();
                Assert.True(elapsed.ElapsedMilliseconds < 500, "Closing the terminal blocked on a pipe.");
                Assert.True(child.WaitForExit(3000), "Closing the terminal did not stop its owned child.");
            }
            finally { heartbeat.Stop(); }
        }
        finally
        {
            terminal.Close(); app.Shutdown(); finished.Set();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
