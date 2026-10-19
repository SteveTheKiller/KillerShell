using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KillerShell.Models;
using KillerShell.Shell;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Tools
{
    /// <summary>
    /// Switching the Event Viewer's log or level, or closing the tab, while an older read is
    /// still running must never let that read add rows or change the status of the view that
    /// replaced it. Drives the real control on a real dispatcher; only the Windows event log
    /// itself is replaced, so the old read can be held at the log and released on cue.
    /// </summary>
    public sealed class EventViewerReloadTests
    {
        [Theory]
        [InlineData("stale-rows")]
        [InlineData("stale-partial-batch")]
        [InlineData("stale-error")]
        [InlineData("rapid-switches")]
        [InlineData("closed-tab")]
        public async Task OlderLoadsNeverReachTheCurrentView(string scenario)
        {
            string assembly = typeof(EventViewerReloadTests).Assembly.Location.Replace("'", "''");
            string application = typeof(MainWindow).Assembly.Location.Replace("'", "''");
            string assertions = typeof(Assert).Assembly.Location.Replace("'", "''");
            string script = "$ErrorActionPreference = 'Stop'; try { " +
                "[Reflection.Assembly]::LoadFrom('" + application + "') | Out-Null; " +
                "[Reflection.Assembly]::LoadFrom('" + assertions + "') | Out-Null; " +
                "[Reflection.Assembly]::LoadFrom('" + assembly + "') | Out-Null; " +
                "[KillerShell.Tests.Tools.EventViewerReloadScenario]::Run('" + scenario + "'); exit 0 " +
                "} catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }";
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -STA -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000))
            {
                process.Kill();
                Assert.Fail("The Event Viewer scenario did not finish.");
            }
            Assert.True(process.ExitCode == 0, $"Scenario {scenario} exited with {process.ExitCode}.\n" + await output + await errors);
        }
    }

    public static class EventViewerReloadScenario
    {
        // Called by the isolated STA process through its public test entry point.
        public static void Run(string scenario)
        {
            Application.ResourceAssembly = typeof(MainWindow).Assembly;
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string resource in new[] { "Themes/Dark.xaml", "Controls/Controls.xaml", "Controls/AppStyles.xaml", "Strings/en-US.xaml" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/KillerShell;component/" + resource)
                });
            try { Check(scenario); }
            finally { app.Shutdown(); }
        }

        private static void Check(string scenario)
        {
            var log = new FakeLog();
            var control = new EventViewerControl { ReadLog = log.Read };
            ComboBox[] boxes = Descendants<ComboBox>(control).ToArray();
            ComboBox logBox = boxes[0], levelBox = boxes[1];

            // Application is held at the log until released; System answers at once.
            log.Hold("Application");
            levelBox.SelectedIndex = 1;                       // reload: Application, Error
            Assert.True(log.WaitEntered("Application"), "The first load never reached the log.");

            switch (scenario)
            {
                case "stale-rows":
                    log.Rows["Application"] = 120;            // whole batches queued after the switch
                    logBox.SelectedIndex = 1;                 // reload: System
                    break;
                case "stale-partial-batch":
                    log.Rows["Application"] = 7;              // less than one batch: the final flush
                    logBox.SelectedIndex = 1;
                    break;
                case "stale-error":
                    log.Fail["Application"] = true;           // the old read ends in an error
                    logBox.SelectedIndex = 1;
                    break;
                case "rapid-switches":
                    log.Rows["Application"] = 60;
                    levelBox.SelectedIndex = 2;               // Application again, still held
                    levelBox.SelectedIndex = 3;
                    logBox.SelectedIndex = 1;                 // finally System
                    break;
                case "closed-tab":
                    log.Rows["Application"] = 60;
                    control.Shutdown();
                    break;
            }

            // The replacement finishes first; the old read comes back afterwards.
            Thread.Sleep(300);
            Pump();
            log.Release("Application");
            Assert.True(log.WaitAllFinished(), "A load never finished.");
            Pump();

            string[] shown = Descendants<DataGrid>(control).Single().Items.Cast<EventLogEntryInfo>()
                .Select(e => e.LogName).Distinct().ToArray();
            string status = StatusText(control);

            if (scenario == "closed-tab")
            {
                Assert.Empty(shown);
                return;
            }

            Assert.Equal(new[] { "System" }, shown);
            Assert.Equal(FakeLog.SystemRows, Descendants<DataGrid>(control).Single().Items.Count);
            Assert.True(status.Length == 0, "The current load's status was replaced by: " + status);
        }

        private static string StatusText(EventViewerControl control) =>
            control.Children.OfType<TextBlock>().Where(t => Grid.GetRow(t) == 2).Select(t => t.Text).FirstOrDefault() ?? string.Empty;

        private static void Pump()
        {
            // Let every callback the readers queued run, then a little longer for stragglers.
            for (int i = 0; i < 5; i++)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(20);
            }
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is not DependencyObject node) continue;
                if (node is T match) yield return match;
                foreach (T nested in Descendants<T>(node)) yield return nested;
            }
        }

        private sealed class FakeLog
        {
            internal const int SystemRows = 3;
            internal readonly Dictionary<string, int> Rows = new() { ["Application"] = 1, ["System"] = SystemRows };
            internal readonly Dictionary<string, bool> Fail = new();
            private readonly ManualResetEventSlim _gate = new(true);
            private readonly ManualResetEventSlim _entered = new();
            private string _held = string.Empty;
            private int _running;

            internal void Hold(string logName) { _held = logName; _gate.Reset(); }
            internal void Release(string logName) => _gate.Set();
            internal bool WaitEntered(string logName) => _entered.Wait(5000);

            internal bool WaitAllFinished()
            {
                Thread.Sleep(200);   // let the replacement load reach the log too
                var watch = Stopwatch.StartNew();
                while (Volatile.Read(ref _running) > 0 && watch.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(10);
                Thread.Sleep(100);   // the worker's last dispatcher post follows the enumeration
                return Volatile.Read(ref _running) == 0;
            }

            internal IEnumerable<EventLogEntryInfo> Read(string logName, string xpath, CancellationToken token)
            {
                Interlocked.Increment(ref _running);
                try
                {
                    if (logName == _held)
                    {
                        _entered.Set();
                        _gate.Wait();
                    }
                    if (Fail.TryGetValue(logName, out bool fail) && fail)
                        throw new InvalidOperationException("old read failed");
                    int rows = Rows.TryGetValue(logName, out int n) ? n : 0;
                    for (int i = 0; i < rows; i++)
                        yield return new EventLogEntryInfo(logName, "Error", DateTime.UtcNow.AddSeconds(-i), "Fixture",
                            1000 + i, "-", logName + " row " + i, "-", "PC", "-", "1", "1", "-", i.ToString(), "Info", "<Event/>");
                }
                finally { Interlocked.Decrement(ref _running); }
            }
        }
    }
}
