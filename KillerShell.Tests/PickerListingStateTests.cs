using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerShell.Controls;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests;

public sealed class PickerListingStateTests
{
    [Fact]
    public async System.Threading.Tasks.Task ListingStateFollowsTheReaderThroughBusyStaleFailedAndVanished()
    {
        string assembly = typeof(PickerListingStateTests).Assembly.Location.Replace("'", "''");
        string application = typeof(FileDialog).Assembly.Location.Replace("'", "''");
        string assertions = typeof(Assert).Assembly.Location.Replace("'", "''");
        string script = "$ErrorActionPreference = 'Stop'; try { " +
            "[Reflection.Assembly]::LoadFrom('" + application + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assertions + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assembly + "') | Out-Null; " +
            "[KillerShell.Tests.PickerListingStateScenario]::Run(); exit 0 " +
            "} catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -STA -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(); Assert.Fail("Picker listing scenario did not finish."); }
        Assert.True(process.ExitCode == 0, await output + await errors);
    }
}

public static class PickerListingStateScenario
{
    private static readonly PickerListingResult Empty = new(Array.Empty<KillerShell.Controls.PickerEntry>(), true, true);

    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var read = ThemeManager.GetSetting;
            var write = ThemeManager.SetSetting;
            Application? app = null;
            try
            {
                ThemeManager.GetSetting = _ => null;
                ThemeManager.SetSetting = (_, _) => { };
                Application.ResourceAssembly = typeof(FileDialog).Assembly;
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (string resource in new[] { "Themes/Dark.xaml", "Controls/Controls.xaml", "Controls/AppStyles.xaml", "Strings/en-US.xaml" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/KillerShell;component/" + resource)
                    });
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                string first = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
                string second = Path.GetDirectoryName(first)!;

                // Busy: Accept stays off until the reader returns.
                using (var gate = new ManualResetEventSlim(false))
                {
                    var picker = new FileDialog(FileDialogMode.Open);
                    picker.DirectoryListingReader = (_, _, _) => { gate.Wait(); return Empty; };
                    _ = picker.NavigateDirectoryAsync(first);
                    Assert.True(picker.IsDirectoryListingBusy);
                    Assert.False(picker.IsDirectoryListingAvailable);
                    Assert.False(((Button)picker.FindName("AcceptButton")).IsEnabled);
                    gate.Set();
                    Wait(() => picker.IsDirectoryListingAvailable, picker);
                    Assert.False(picker.IsDirectoryListingBusy);
                    Assert.True(((Button)picker.FindName("AcceptButton")).IsEnabled);
                    picker.Close();
                }

                // Stale: a slow listing for a folder left behind never replaces the one now shown.
                using (var slow = new ManualResetEventSlim(false))
                {
                    var picker = new FileDialog(FileDialogMode.Open);
                    picker.DirectoryListingReader = (dir, _, _) => { if (dir == first) slow.Wait(); return Empty; };
                    _ = picker.NavigateDirectoryAsync(first);
                    _ = picker.NavigateDirectoryAsync(second);
                    Wait(() => picker.IsDirectoryListingAvailable, picker);
                    slow.Set();
                    Pump(300);
                    Assert.Equal(second, ((TextBox)picker.FindName("PathBox")).Text);
                    Assert.True(picker.IsDirectoryListingAvailable);
                    picker.Close();
                }

                // Failed: the reader's message is shown and nothing becomes acceptable.
                {
                    var picker = new FileDialog(FileDialogMode.Open);
                    picker.DirectoryListingReader = (_, _, _) => throw new IOException("listing broke");
                    _ = picker.NavigateDirectoryAsync(first);
                    Wait(() => !picker.IsDirectoryListingBusy, picker);
                    Assert.False(picker.IsDirectoryListingAvailable);
                    Assert.Equal("listing broke", ((TextBlock)picker.FindName("SelMeta")).Text);
                    picker.Close();
                }

                // Vanished: a folder that no longer exists says so in the app's language.
                {
                    var picker = new FileDialog(FileDialogMode.Open);
                    picker.DirectoryListingReader = (_, _, _) => new PickerListingResult(Array.Empty<KillerShell.Controls.PickerEntry>(), false, false);
                    _ = picker.NavigateDirectoryAsync(first);
                    Wait(() => !picker.IsDirectoryListingBusy, picker);
                    Assert.False(picker.IsDirectoryListingAvailable);
                    Assert.Equal((string)app.FindResource("Str_Dlg_NoSuchFolder"), ((TextBlock)picker.FindName("SelMeta")).Text);
                    picker.Close();
                }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                app?.Shutdown();
                ThemeManager.GetSetting = read;
                ThemeManager.SetSetting = write;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "Picker listing scenario timed out.");
        if (failure != null) throw failure;
    }

    private static void Pump(int milliseconds)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    private static void Wait(Func<bool> done, FileDialog picker)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (!done())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("Listing state never settled: " + ((TextBlock)picker.FindName("SelMeta")).Text);
            Pump(10);
        }
    }
}
