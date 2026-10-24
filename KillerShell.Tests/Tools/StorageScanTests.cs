using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KillerShell.Services;
using KillerShell.Shell;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Tools;

[Trait("Category", "Window")]
public sealed class StorageScanTests
{
    [Theory]
    [InlineData("directory")]
    [InlineData("mft")]
    [InlineData("mft-fallback")]
    [InlineData("completed")]
    [InlineData("rapid")]
    [InlineData("stop")]
    [InlineData("shutdown")]
    [InlineData("dispatcher-shutdown")]
    public async Task ScanLifetimeOwnsItsResultsAndCompletion(string scenario)
    {
        string assembly = typeof(StorageScanTests).Assembly.Location.Replace("'", "''");
        string application = typeof(MainWindow).Assembly.Location.Replace("'", "''");
        string assertions = typeof(Assert).Assembly.Location.Replace("'", "''");
        string script = "$ErrorActionPreference = 'Stop'; try { " +
            "[Reflection.Assembly]::LoadFrom('" + application + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assertions + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assembly + "') | Out-Null; " +
            "[KillerShell.Tests.Tools.StorageScanScenario]::Run('" + scenario + "'); exit 0 " +
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
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill();
            Assert.Fail("The storage scan scenario did not finish.");
        }
        Assert.True(process.ExitCode == 0, $"Storage scenario {scenario} exited with {process.ExitCode}.\n" + await output + await errors);
    }
}

public static class StorageScanScenario
{
    public static void Run(string scenario)
    {
        var originalContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        ThemeManager.GetSetting = _ => null;
        ThemeManager.SetSetting = (_, _) => { };
        Application.ResourceAssembly = typeof(MainWindow).Assembly;
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string resource in new[] { "Themes/Dark.xaml", "Controls/Controls.xaml", "Controls/AppStyles.xaml", "Strings/en-US.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/KillerShell;component/" + resource)
            });
        ThemeManager.Initialize();
        string root = Path.Combine(Path.GetTempPath(), "KillerShell-storage-" + Guid.NewGuid().ToString("N"));
        string first = Path.Combine(root, "old");
        string second = Path.Combine(root, "new");
        string blocked = Path.Combine(first, "blocked");
        Directory.CreateDirectory(blocked);
        Directory.CreateDirectory(Path.Combine(second, "nested"));
        File.WriteAllBytes(Path.Combine(first, "old.bin"), new byte[19]);
        File.WriteAllBytes(Path.Combine(blocked, "old-child.bin"), new byte[23]);
        File.WriteAllBytes(Path.Combine(second, "new.bin"), new byte[37]);
        File.WriteAllBytes(Path.Combine(second, "nested", "tail.bin"), new byte[11]);
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        using var completionPosted = new ManualResetEventSlim();
        int uiThread = Thread.CurrentThread.ManagedThreadId;
        var statuses = new List<string>();
        bool useMft = scenario.StartsWith("mft", StringComparison.Ordinal);
        CancellationToken firstToken = default;
        void WaitForRead(string path, CancellationToken token)
        {
            if (path == (useMft ? first : blocked))
            {
                firstToken = token;
                firstEntered.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(10));
            }
            if (path == second) { secondEntered.Set(); releaseSecond.Wait(TimeSpan.FromSeconds(10)); }
        }
        var control = new StorageAnalyzerControl(first, (path, token) =>
        {
            // Hold the real filesystem read so both scan lifetimes overlap deterministically.
            WaitForRead(path, token);
            return StorageDirectoryReader.Read(path, token);
        }, _ => useMft, (path, token) =>
        {
            WaitForRead(path, token);
            // Supply the filesystem record boundary from the fixture without scanning a volume.
            string file = Path.Combine(path, path == first ? "old.bin" : "new.bin");
            bool unreadable = scenario == "mft-fallback";
            var entry = new StorageMftEntry { Id = 2, ParentId = 1, Name = Path.GetFileName(file), Size = unreadable ? -1 : new FileInfo(file).Length };
            string folderName = path == first ? "blocked" : "nested";
            string childFile = Path.Combine(path, folderName, path == first ? "old-child.bin" : "tail.bin");
            return new StorageMftReadResult(1, new Dictionary<ulong, StorageMftEntry>
            {
                [2] = entry,
                [3] = new StorageMftEntry { Id = 3, ParentId = 1, Name = folderName, Attributes = 0x10 },
                [4] = new StorageMftEntry { Id = 4, ParentId = 3, Name = Path.GetFileName(childFile), Size = unreadable ? -1 : new FileInfo(childFile).Length }
            });
        });
        control.ReportStatus = statuses.Add;
        var window = new Window { Content = control, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        DispatcherHookEventHandler posted = (_, e) =>
        {
            if (Thread.CurrentThread.ManagedThreadId != uiThread && e.Operation.Priority == DispatcherPriority.Normal)
                completionPosted.Set();
        };
        try
        {
            window.Show();
            Pump(TimeSpan.FromMilliseconds(50));
            var scanButton = Descendants(control).OfType<Button>().Single(button => Equals(button.Content, app.FindResource("Str_Storage_Scan")));
            if (scenario == "rapid")
            {
                releaseFirst.Set();
                releaseSecond.Set();
                for (int i = 0; i < 10; i++) control.ScanFolder(i % 2 == 0 ? first : second);
                PumpUntil(() => Equals(scanButton.Content, app.FindResource("Str_Storage_Scan")));
                Pump(TimeSpan.FromMilliseconds(100));
                AssertLatestReport(control, second, statuses);
                return;
            }
            control.ScanFolder(first);
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)), "The old scan must reach its filesystem read.");
            if (!useMft)
                PumpUntil(() => statuses.Contains("1 dirs  1 files  19 B"), "The old scan must report its partial total.");
            app.Dispatcher.Hooks.OperationPosted += posted;
            if (scenario == "dispatcher-shutdown")
            {
                control.Shutdown();
                int statusCount = statuses.Count;
                app.Dispatcher.InvokeShutdown();
                releaseFirst.Set();
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    try { _ = firstToken.WaitHandle; return false; }
                    catch (ObjectDisposedException) { return true; }
                }, TimeSpan.FromSeconds(3)), "The canceled scan must release its token source after the dispatcher stops.");
                Assert.Equal(statusCount, statuses.Count);
                return;
            }
            if (scenario == "shutdown")
            {
                control.Shutdown();
                int statusCount = statuses.Count;
                releaseFirst.Set();
                Assert.True(completionPosted.Wait(TimeSpan.FromSeconds(5)), "The closed scan must queue its completion.");
                Pump(TimeSpan.FromMilliseconds(100));
                Assert.Equal(statusCount, statuses.Count);
                Assert.Throws<ObjectDisposedException>(() => { _ = firstToken.WaitHandle; });
                return;
            }
            if (scenario == "stop")
            {
                scanButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(TimeSpan.FromMilliseconds(100));
                Assert.Equal(app.FindResource("Str_Storage_Stop"), scanButton.Content);
                releaseFirst.Set();
                PumpUntil(() => Equals(scanButton.Content, app.FindResource("Str_Storage_Scan")));
                Assert.StartsWith((string)app.FindResource("Str_Storage_Stopped"), statuses.Last());
                Assert.Equal(19, Assert.IsType<StorageReport>(control.CreateReport()).TotalSize);
                return;
            }
            if (scenario == "completed")
            {
                releaseFirst.Set();
                Assert.True(completionPosted.Wait(TimeSpan.FromSeconds(5)), "The old scan must queue its completion before replacement.");
            }
            control.ScanFolder(second);
            Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(5)), "The replacement scan must reach its filesystem read.");
            releaseFirst.Set();
            Assert.True(completionPosted.Wait(TimeSpan.FromSeconds(5)), "The old scan must queue its completion.");
            Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(app.FindResource("Str_Storage_Stop"), scanButton.Content);
            Assert.DoesNotContain(statuses, status => status.StartsWith((string)app.FindResource("Str_Storage_Stopped"), StringComparison.Ordinal));

            int progressCount = statuses.Count;
            Pump(TimeSpan.FromMilliseconds(350));
            Assert.True(statuses.Count > progressCount, "The replacement scan must keep reporting progress.");
            releaseSecond.Set();
            PumpUntil(() => Equals(scanButton.Content, app.FindResource("Str_Storage_Scan")));
            AssertLatestReport(control, second, statuses);
        }
        finally
        {
            app.Dispatcher.Hooks.OperationPosted -= posted;
            releaseFirst.Set();
            releaseSecond.Set();
            control.Shutdown();
            Pump(TimeSpan.FromMilliseconds(100));
            if (!app.Dispatcher.HasShutdownStarted)
            {
                window.Close();
                app.Shutdown();
            }
            SynchronizationContext.SetSynchronizationContext(originalContext);
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The storage fixture escaped the temporary directory.");
            Directory.Delete(root, true);
        }
    }

    private static void AssertLatestReport(StorageAnalyzerControl control, string target, List<string> statuses)
    {
        StorageReport report = Assert.IsType<StorageReport>(control.CreateReport());
        Assert.Equal(target, report.ScanRoot);
        Assert.Equal(target, report.ViewRoot);
        Assert.Equal(48, report.TotalSize);
        Assert.Collection(report.Root.Children,
            file => Assert.Equal("new.bin", file.Name),
            folder => Assert.Equal("tail.bin", Assert.Single(folder.Children).Name));
        Assert.Equal("1 dirs  2 files  48 B", statuses.Last());
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void PumpUntil(Func<bool> complete, string message = "The replacement scan must finish.")
    {
        var watch = Stopwatch.StartNew();
        while (!complete() && watch.Elapsed < TimeSpan.FromSeconds(5)) Pump(TimeSpan.FromMilliseconds(20));
        Assert.True(complete(), message);
    }

    private static void Pump(TimeSpan duration)
    {
        if (Dispatcher.CurrentDispatcher.HasShutdownStarted) return;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
