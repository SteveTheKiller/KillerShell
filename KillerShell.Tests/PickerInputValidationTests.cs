using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShell.Services;
using KillerShell.Controls;
using Xunit;

namespace KillerShell.Tests;

public sealed class PickerInputValidationTests
{
    [Fact]
    public async Task MalformedNamesStayInThePickerThroughButtonAndEnter()
    {
        string assembly = typeof(PickerInputValidationTests).Assembly.Location.Replace("'", "''");
        string application = typeof(FileDialog).Assembly.Location.Replace("'", "''");
        string assertions = typeof(Assert).Assembly.Location.Replace("'", "''");
        string script = "$ErrorActionPreference = 'Stop'; try { " +
            "[Reflection.Assembly]::LoadFrom('" + application + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assertions + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assembly + "') | Out-Null; " +
            "[KillerShell.Tests.PickerInputValidationScenario]::Run(); exit 0 " +
            "} catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -STA -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(); Assert.Fail("Picker validation did not finish."); }
        Assert.True(process.ExitCode == 0, await output + await errors);
    }
}

public static class PickerInputValidationScenario
{
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
                // The listing continues on the context captured when the picker is built, as it is under Dispatcher.Run.
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
                string invalid = (string)app.FindResource("Str_Err_InvalidChars");
                foreach (var mode in new[] { FileDialogMode.Open, FileDialogMode.Save })
                foreach (bool enter in new[] { false, true })
                foreach (string text in new[]
                {
                    "bad|name.txt", "bad\"name.txt", "bad:name.txt", "bad<name.txt", "bad>name.txt",
                    new string('a', 300) + ".txt", "\"fine.txt\" \"bad|name.txt\""
                })
                {
                    var picker = new FileDialog(mode) { Multiselect = true };
                    picker.DirectoryListingReader = (_, _, _) => new PickerListingResult(Array.Empty<KillerShell.Controls.PickerEntry>(), true, true);
                    _ = picker.NavigateDirectoryAsync(Path.GetTempPath());
                    WaitFor(picker);
                    var name = (TextBox)picker.FindName("FileNameBox");
                    name.Text = text;
                    if (enter)
                        name.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, Key.Enter)
                        { RoutedEvent = Keyboard.KeyDownEvent });
                    else
                        ((Button)picker.FindName("AcceptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(text, name.Text);
                    Assert.Null(picker.DialogResult);
                    Assert.Equal(invalid, ((TextBlock)picker.FindName("SelMeta")).Text);
                    picker.Close();
                }

                // The path box only navigates to fully qualified paths: relative and drive-relative
                // text would otherwise be looked up against the process's own folder.
                foreach (string text in new[] { "documents", "C:documents", @"\documents" })
                {
                    int reads = 0;
                    var picker = new FileDialog(FileDialogMode.Open);
                    picker.DirectoryListingReader = (_, _, _) => { reads++; return new PickerListingResult(Array.Empty<KillerShell.Controls.PickerEntry>(), true, true); };
                    var path = (TextBox)picker.FindName("PathBox");
                    path.Text = text;
                    path.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, Key.Enter)
                    { RoutedEvent = Keyboard.KeyDownEvent });
                    Assert.Equal(0, reads);
                    Assert.Equal((string)app.FindResource("Str_Dlg_NoSuchFolder"), ((TextBlock)picker.FindName("SelMeta")).Text);
                    picker.Close();
                }

                // Existence checks run off the UI thread, and a second accept while one is pending does nothing.
                {
                    int uiThread = Environment.CurrentManagedThreadId, probeThread = -1, calls = 0;
                    using var gate = new ManualResetEventSlim(false);
                    var picker = new FileDialog(FileDialogMode.Open);
                    picker.DirectoryListingReader = (_, _, _) => new PickerListingResult(Array.Empty<KillerShell.Controls.PickerEntry>(), true, true);
                    picker.FileExistsProbe = _ => { Interlocked.Increment(ref calls); probeThread = Environment.CurrentManagedThreadId; gate.Wait(); return false; };
                    _ = picker.NavigateDirectoryAsync(Path.GetTempPath());
                    WaitFor(picker);
                    ((TextBox)picker.FindName("FileNameBox")).Text = "missing.txt";
                    var accept = (Button)picker.FindName("AcceptButton");
                    accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Pump(300);
                    Assert.Equal(1, calls);
                    Assert.NotEqual(uiThread, probeThread);
                    gate.Set();
                    var end = DateTime.UtcNow.AddSeconds(10);
                    while (((TextBlock)picker.FindName("SelMeta")).Text != (string)app.FindResource("Str_Dlg_NoSuchFile"))
                    {
                        Assert.True(DateTime.UtcNow < end, "The missing file was never reported.");
                        Pump(10);
                    }
                    Assert.Equal(1, calls);
                    picker.Close();
                }

                // Choosing the node of the folder already shown does not list it again.
                {
                    int reads = 0;
                    string shown = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
                    var picker = new FileDialog(FileDialogMode.Open);
                    picker.DirectoryListingReader = (_, _, _) => { Interlocked.Increment(ref reads); return new PickerListingResult(Array.Empty<KillerShell.Controls.PickerEntry>(), true, true); };
                    _ = picker.NavigateDirectoryAsync(shown);
                    WaitFor(picker);
                    Assert.Equal(1, reads);
                    var tree = (TreeView)picker.FindName("FolderTreeCtl");
                    tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, new FolderNode(shown, "Temp", false))
                    { RoutedEvent = TreeView.SelectedItemChangedEvent });
                    Pump(300);
                    Assert.Equal(1, reads);
                    picker.Close();
                }

                // Accepting a highlighted folder remembers that folder, not the one it was inside.
                {
                    string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
                    string chosen = Path.Combine(parent, "chosen");
                    var saved = new System.Collections.Generic.List<string>();
                    ThemeManager.SetSetting = (key, value) => saved.Add(value + "@" + key);
                    var picker = new FileDialog(FileDialogMode.Folder) { AcceptHighlightedFolder = true };
                    picker.FolderExistsProbe = _ => true;
                    picker.DirectoryListingReader = (_, _, _) => new PickerListingResult(
                        new[] { new KillerShell.Controls.PickerEntry("chosen", chosen, true, 0, DateTime.Now) }, true, true);
                    _ = picker.NavigateDirectoryAsync(parent);
                    WaitFor(picker);
                    var list = (ListBox)picker.FindName("FileList");
                    list.ItemsSource = picker.Entries;   // ShowDialog binds it; the picker here is never shown
                    list.SelectedIndex = 0;
                    ((Button)picker.FindName("AcceptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var wait = DateTime.UtcNow.AddSeconds(10);
                    while (!saved.Exists(x => x.EndsWith("@FileDlgLastSaveDir")) && DateTime.UtcNow < wait) Pump(10);
                    Assert.Contains(chosen + "@FileDlgLastSaveDir", saved);
                    Assert.DoesNotContain(parent + "@FileDlgLastSaveDir", saved);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Picker input validation timed out.");
        if (failure != null) throw failure;
    }

    private static void Pump(int milliseconds)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    private static void WaitFor(FileDialog picker)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (!picker.IsDirectoryListingAvailable)
        {
            if (DateTime.UtcNow > end)
                throw new TimeoutException("The picker listing did not finish: " + ((TextBlock)picker.FindName("SelMeta")).Text
                    + " busy=" + picker.IsDirectoryListingBusy + " task=" + picker.DirectoryListingTask.Status + " " + picker.DirectoryListingTask.Exception);
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    private sealed class TestPresentationSource : PresentationSource
    {
        public override System.Windows.Media.Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget GetCompositionTargetCore() => null!;
    }
}
