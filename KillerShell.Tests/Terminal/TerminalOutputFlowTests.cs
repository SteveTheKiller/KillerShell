using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using KillerShell.Services;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class TerminalOutputFlowTests
{
    [Fact]
    public Task DispatcherBatchYieldsBeforeTheWholeStreamIsParsed()
        => RuntimeComponentProcess.Run(nameof(TerminalOutputFlowScenario));
}

internal static class RuntimeComponentProcess
{
    internal static async Task Run(string scenario, int timeoutMilliseconds = 60000)
    {
        string assembly = typeof(TerminalOutputFlowTests).Assembly.Location.Replace("'", "''");
        string application = typeof(TerminalControl).Assembly.Location.Replace("'", "''");
        string assertions = typeof(Assert).Assembly.Location.Replace("'", "''");
        string script = "$ErrorActionPreference='Stop'; try { " +
            "[Reflection.Assembly]::LoadFrom('" + application + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assertions + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assembly + "') | Out-Null; " +
            "[KillerShell.Tests.Terminal." + scenario + "]::Run(); exit 0 " +
            "} catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -STA -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            process.Kill();
            process.WaitForExit();
            Assert.Fail("The runtime component scenario exceeded its deadline.\n" + await output + await errors);
        }
        Assert.True(process.ExitCode == 0, await output + await errors);
    }

    internal static Application StartApplication()
    {
        ThemeManager.GetSetting = _ => null;
        ThemeManager.SetSetting = (_, _) => { };
        Application.ResourceAssembly = typeof(TerminalControl).Assembly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string resource in new[] { "Themes/Dark.xaml", "Controls/Controls.xaml", "Controls/AppStyles.xaml", "Strings/en-US.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/KillerShell;component/" + resource) });
        return app;
    }
}

public static class TerminalOutputFlowScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        try { FiniteOrderedOutput(); FragmentedOutput(); CloseReleasesBackpressure(); }
        finally { app.Shutdown(); }
    }

    private static void FiniteOrderedOutput()
    {
        var terminal = new TerminalControl(TerminalSkin.Default);
        string[] lines = Enumerable.Range(0, 3000).Select(index => index.ToString("D4") + ": abcdefghijklmnopqrstuvwxyz café\U0001F600").ToArray();
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\r\n", lines));
        using var source = new ObservedStream(bytes);
        try
        {
            terminal.ReadOutput(source);
            Assert.True(source.Completed.Wait(10000), "The finite source was not read.");
            int batch = terminal.ProcessOutput();
            Assert.InRange(batch, 1, 65536);
            Assert.True(terminal.Buffer.TotalLines < lines.Length, "One UI dispatch parsed the entire backlog.");
            int total = batch;
            int next;
            while ((next = terminal.ProcessOutput()) > 0) total += next;
            Assert.Equal(bytes.Length, total);
            for (int index = 0; index < lines.Length; index++)
            {
                var actual = new StringBuilder();
                foreach (var cell in terminal.Buffer.LineAt(index))
                    actual.Append(cell.Text);
                Assert.Equal(lines[index], actual.ToString().TrimEnd());
            }
        }
        finally { terminal.Close(); }
    }

    private static void FragmentedOutput()
    {
        var terminal = new TerminalControl(TerminalSkin.Default);
        string esc = ((char)27).ToString();
        using var source = new ObservedStream(Encoding.UTF8.GetBytes("café\U0001F600" + esc + "[31mred" + esc + "[0m"), 1);
        try
        {
            terminal.ReadOutput(source);
            Assert.True(source.Completed.Wait(10000));
            while (terminal.ProcessOutput() > 0) { }
            var line = terminal.Buffer.LineAt(0);
            Assert.Equal(0xE9, line[3].Ch);
            Assert.Equal(0x1F600, line[4].Ch);
            Assert.True(line[5].IsContinuation);
            Assert.Equal('r', line[6].Ch);
            Assert.Equal(1, line[6].Fg);
            Assert.Equal('d', line[8].Ch);
        }
        finally { terminal.Close(); }
    }

    private static void CloseReleasesBackpressure()
    {
        var terminal = new TerminalControl(TerminalSkin.Default);
        using var source = new ObservedStream(new byte[2 * 1024 * 1024]);
        try
        {
            terminal.ReadOutput(source);
            Assert.True(source.CapacityReached.Wait(10000));
            Assert.False(source.Completed.Wait(150), "The reader consumed an unbounded pending stream.");
            Assert.InRange(source.ReadBytes, 1, 1024 * 1024);
            int version = terminal.Buffer.Version;
            terminal.Close();
            Assert.True(source.Completed.Wait(10000), "Close did not release the blocked reader to drain output.");
            Assert.Equal(2 * 1024 * 1024, source.ReadBytes);
            Assert.Equal(0, terminal.ProcessOutput());
            Assert.Equal(version, terminal.Buffer.Version);
        }
        finally { terminal.Close(); }
    }

    private sealed class ObservedStream(byte[] bytes, int fragment = 8192) : MemoryStream(bytes)
    {
        internal readonly ManualResetEventSlim Completed = new(false);
        internal readonly ManualResetEventSlim CapacityReached = new(false);
        private long _readBytes;
        internal long ReadBytes => Interlocked.Read(ref _readBytes);
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, Math.Min(count, fragment));
            if (Interlocked.Add(ref _readBytes, read) >= 1024 * 1024 - 8192) CapacityReached.Set();
            if (read == 0) Completed.Set();
            return read;
        }
    }
}
