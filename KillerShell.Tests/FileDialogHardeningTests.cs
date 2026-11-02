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
using System.Windows.Input;
using KillerShell.Controls;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests;

// Each scenario builds the dialog but never shows it, in a separate STA process like the other picker tests.
public sealed class FileDialogHardeningTests
{
    private static async Task RunScenario(string name)
    {
        string assembly = typeof(FileDialogHardeningTests).Assembly.Location.Replace("'", "''");
        string application = typeof(FileDialog).Assembly.Location.Replace("'", "''");
        string assertions = typeof(Assert).Assembly.Location.Replace("'", "''");
        string script = "$ErrorActionPreference = 'Stop'; try { " +
            "[Reflection.Assembly]::LoadFrom('" + application + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assertions + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assembly + "') | Out-Null; " +
            "[KillerShell.Tests.FileDialogHardeningScenario]::Run('" + name + "'); exit 0 " +
            "} catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -STA -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(90000)) { process.Kill(); Assert.Fail(name + " did not finish."); }
        Assert.True(process.ExitCode == 0, await output + await errors);
    }

    [Fact] public Task UnknownProbesNeverSkipTheOverwriteOrReportNotFound() => RunScenario("UnknownProbes");
    [Fact] public Task ClosingDuringAnAcceptOrEnterNeverAcceptsOrNavigates() => RunScenario("Closing");
    [Fact] public Task AVanishedFolderWalksUpToTheNearestAncestorWithoutBouncing() => RunScenario("VanishedWalk");
    [Fact] public Task AcceptIsEnabledOnlyWhileTheListingIsAvailable() => RunScenario("AcceptState");
    [Fact] public Task TheTreeNodeOfAFolderThatDidNotListIsHonored() => RunScenario("TreeGuard");
    [Fact] public Task QuotedDirectoryInTheNameBoxNavigates() => RunScenario("QuotedDirectory");
    [Fact] public Task UpKeepsWalkingPastTheRecoveryLimitOfAVanishedTenLevelTree() => RunScenario("DeepVanishedTree");
    [Fact] public Task RecentsIgnoreAClickWhileProbingAndAnswerWithinTheShortDeadline() => RunScenario("Recents");
    [Fact] public Task AWildcardAcceptedFromTheButtonRetargetsTheListing() => RunScenario("WildcardAccept");

    [Fact]
    public async Task RecentsAreProbedPerEntryAndADeadOneDoesNotHideTheRest()
    {
        var folders = new[] { @"C:\good1", @"C:\dead", @"C:\good2", @"C:\gone" };
        using var never = new ManualResetEventSlim(false);
        var found = await FileDialog.ExistingFoldersAsync(folders,
            path => path == @"C:\dead" ? WaitThenFalse(never) : path.StartsWith(@"C:\good"), TimeSpan.FromMilliseconds(300));
        never.Set();
        Assert.Equal(new[] { @"C:\good1", @"C:\good2" }, found);
    }

    private static bool WaitThenFalse(ManualResetEventSlim gate) { gate.Wait(); return false; }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("documents", null)]
    [InlineData(@"C:documents", null)]
    [InlineData("C:\\bad|folder", null)]
    [InlineData("C:\\bad\0folder", null)]
    [InlineData(@"C:\Temp", @"C:\Temp")]
    [InlineData(@"\\server\share", @"\\server\share")]
    public void OnlyAFullyQualifiedWellFormedRememberedFolderIsUsed(string? remembered, string? expected)
        => Assert.Equal(expected, FileDialog.UsableRemembered(remembered));
}

public static class FileDialogHardeningScenario
{
    private static readonly PickerListingResult Empty = new(Array.Empty<KillerShell.Controls.PickerEntry>(), true, true);

    public static void Run(string name)
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
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
                switch (name)
                {
                    case "UnknownProbes": UnknownProbes(app); break;
                    case "Closing": Closing(); break;
                    case "VanishedWalk": VanishedWalk(); break;
                    case "AcceptState": AcceptState(); break;
                    case "TreeGuard": TreeGuard(); break;
                    case "QuotedDirectory": QuotedDirectory(); break;
                    case "DeepVanishedTree": DeepVanishedTree(); break;
                    case "Recents": Recents(); break;
                    case "WildcardAccept": WildcardAccept(); break;
                    default: throw new ArgumentException(name);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), name + " timed out.");
        if (failure != null) throw failure;
    }

    private static string Shown => Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');

    private static FileDialog Listed(FileDialogMode mode, string folder, Func<string, bool, CancellationToken, PickerListingResult>? reader = null)
    {
        var picker = new FileDialog(mode);
        picker.DirectoryListingReader = reader ?? ((_, _, _) => Empty);
        picker.FolderExistsProbe = _ => true;
        _ = picker.NavigateDirectoryAsync(folder);
        Wait(() => picker.IsDirectoryListingAvailable, "listing");
        return picker;
    }

    private static T Find<T>(FileDialog picker, string name) where T : class => (T)picker.FindName(name);

    private static void Click(FileDialog picker) => Find<Button>(picker, "AcceptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Enter(FileDialog picker, string text)
    {
        var box = Find<TextBox>(picker, "FileNameBox");
        box.Text = text;
        box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    private static string Meta(FileDialog picker) => Find<TextBlock>(picker, "SelMeta").Text;

    private static void UnknownProbes(Application app)
    {
        string noFile = (string)app.FindResource("Str_Dlg_NoSuchFile");
        string noFolder = (string)app.FindResource("Str_Dlg_NoSuchFolder");
        // The overwrite check cannot be answered: Save must not go ahead as though nothing is there.
        {
            var picker = Listed(FileDialogMode.Save, Shown);
            picker.FileExistsProbe = _ => throw new IOException("share offline");
            Find<TextBox>(picker, "FileNameBox").Text = "report.txt";
            Click(picker);
            Wait(() => Meta(picker) == "share offline", "the probe message");
            Assert.Equal("", picker.FileName);
            Assert.NotEqual(noFile, Meta(picker));
            picker.Close();
        }
        // A probe that timed out is unknown too, and says so in the system's words.
        {
            var picker = Listed(FileDialogMode.Save, Shown);
            picker.FileExistsProbe = _ => throw new TimeoutException();
            Find<TextBox>(picker, "FileNameBox").Text = "report.txt";
            Click(picker);
            Wait(() => Meta(picker) == new System.ComponentModel.Win32Exception(1460).Message, "the timeout message");
            Assert.Equal("", picker.FileName);
            picker.Close();
        }
        // Open: an unanswerable existence check is not "no such file".
        {
            var picker = Listed(FileDialogMode.Open, Shown);
            picker.FileExistsProbe = _ => throw new IOException("share offline");
            Find<TextBox>(picker, "FileNameBox").Text = "report.txt";
            Click(picker);
            Wait(() => Meta(picker) == "share offline", "the probe message");
            Assert.NotEqual(noFile, Meta(picker));
            Assert.Equal("", picker.FileName);
            picker.Close();
        }
        // Save into a folder that cannot be checked is refused without calling it missing.
        {
            var picker = Listed(FileDialogMode.Save, Shown);
            picker.FolderExistsProbe = _ => throw new IOException("share offline");
            Find<TextBox>(picker, "FileNameBox").Text = "report.txt";
            Click(picker);
            Wait(() => Meta(picker) == "share offline", "the probe message");
            Assert.NotEqual(noFolder, Meta(picker));
            Assert.Equal("", picker.FileName);
            picker.Close();
        }
    }

    private static void Closing()
    {
        // Cancel while the existence check is pending: the answer arriving later accepts nothing.
        using (var gate = new ManualResetEventSlim(false))
        {
            var picker = Listed(FileDialogMode.Open, Shown);
            picker.FileExistsProbe = _ => { gate.Wait(); return true; };
            Find<TextBox>(picker, "FileNameBox").Text = "report.txt";
            Click(picker);
            Pump(200);
            picker.Close();
            gate.Set();
            Pump(500);
            Assert.Equal("", picker.FileName);
        }
        // Closing before the overwrite probe answers never builds the prompt on the closed window.
        using (var gate = new ManualResetEventSlim(false))
        {
            var picker = Listed(FileDialogMode.Save, Shown);
            picker.FileExistsProbe = _ => { gate.Wait(); return true; };
            Find<TextBox>(picker, "FileNameBox").Text = "report.txt";
            Click(picker);
            Pump(200);
            picker.Close();
            gate.Set();
            Pump(500);
            Assert.Equal("", picker.FileName);
        }
        // Enter with a folder typed: closing before the check answers leaves the box alone.
        using (var gate = new ManualResetEventSlim(false))
        {
            int reads = 0;
            var picker = Listed(FileDialogMode.Open, Shown, (_, _, _) => { Interlocked.Increment(ref reads); return Empty; });
            picker.FolderExistsProbe = _ => { gate.Wait(); return true; };
            Enter(picker, Path.GetDirectoryName(Shown)!);
            Pump(200);
            picker.Close();
            gate.Set();
            Pump(500);
            Assert.Equal(Path.GetDirectoryName(Shown), Find<TextBox>(picker, "FileNameBox").Text);
            Assert.Equal(1, reads);
        }
    }

    private static void VanishedWalk()
    {
        string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
        string deep = Path.Combine(root, "a", "b", "c");
        int reads = 0;
        string alive = root;
        Func<string, bool, CancellationToken, PickerListingResult> reader = (dir, _, _) =>
        {
            if (Interlocked.Increment(ref reads) > 60) throw new InvalidOperationException("The dialog is still bouncing between folders.");
            return dir.Length <= alive.Length ? Empty : new PickerListingResult(Array.Empty<KillerShell.Controls.PickerEntry>(), false, false);
        };
        alive = deep;
        var picker = Listed(FileDialogMode.Open, deep, reader);
        alive = root;
        reads = 0;
        _ = picker.NavigateDirectoryAsync(deep);
        Wait(() => Find<TextBox>(picker, "PathBox").Text == root && picker.IsDirectoryListingAvailable && !picker.IsDirectoryListingBusy, "the nearest existing ancestor");
        Assert.True(reads <= 6, "Reads: " + reads);
        Assert.True(Find<Button>(picker, "AcceptButton").IsEnabled);
        picker.Close();

        // Nothing exists at all: the walk stops after a bounded number of tries and leaves a usable dialog.
        reads = 0;
        var lost = Listed(FileDialogMode.Open, root, (_, _, _) => Empty);
        lost.DirectoryListingReader = (_, _, _) =>
        {
            if (Interlocked.Increment(ref reads) > 60) throw new InvalidOperationException("The dialog is still bouncing between folders.");
            return new PickerListingResult(Array.Empty<KillerShell.Controls.PickerEntry>(), false, false);
        };
        _ = lost.NavigateDirectoryAsync(deep);
        Pump(1500);
        Assert.False(lost.IsDirectoryListingBusy);
        Assert.True(reads <= 14, "Reads: " + reads);
        Assert.True(Find<TextBox>(lost, "PathBox").IsEnabled);
        lost.Close();
    }

    private static void AcceptState()
    {
        string root = Shown;
        bool fail = false;
        var picker = Listed(FileDialogMode.Open, root, (_, _, _) => fail ? throw new IOException("listing broke") : Empty);
        var accept = Find<Button>(picker, "AcceptButton");
        Assert.True(accept.IsEnabled);
        fail = true;
        _ = picker.NavigateDirectoryAsync(root);
        Wait(() => !picker.IsDirectoryListingBusy, "the failed listing");
        Pump(100);
        Assert.False(picker.IsDirectoryListingAvailable);
        Assert.Equal(picker.IsDirectoryListingAvailable && !picker.IsDirectoryListingBusy, accept.IsEnabled);
        picker.Close();
    }

    private static void TreeGuard()
    {
        string root = Shown;
        int reads = 0;
        bool fail = false;
        var picker = Listed(FileDialogMode.Open, root, (_, _, _) => { Interlocked.Increment(ref reads); return fail ? throw new IOException("listing broke") : Empty; });
        var tree = Find<TreeView>(picker, "FolderTreeCtl");
        void Select() => tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, new FolderNode(root, "Temp", false))
        { RoutedEvent = TreeView.SelectedItemChangedEvent });
        // Available and idle: the node of the folder shown is not a request.
        Select();
        Pump(300);
        Assert.Equal(1, reads);
        // The listing of that folder failed: choosing its node tries again.
        fail = true;
        _ = picker.NavigateDirectoryAsync(root);
        Wait(() => !picker.IsDirectoryListingBusy, "the failed listing");
        int afterFailure = reads;
        fail = false;
        Select();
        Wait(() => reads > afterFailure && picker.IsDirectoryListingAvailable, "the retry");
        picker.Close();
    }

    private static void WildcardAccept()
    {
        // A pattern typed into the name box and accepted with the button retargets the listing, as
        // Enter does, instead of being looked up as the name of a file.
        {
            var picker = Listed(FileDialogMode.Open, Shown);
            picker.FileExistsProbe = _ => false;
            var combo = Find<ComboBox>(picker, "FilterCombo");
            Find<TextBox>(picker, "FileNameBox").Text = "*.log";
            Click(picker);
            Wait(() => combo.Items.Count > 0 && Equals(combo.Items[0], "*.log"), "the pattern as the first filter");
            Assert.Equal(0, combo.SelectedIndex);
            Assert.Equal("", Find<TextBox>(picker, "FileNameBox").Text);
            Assert.Equal("", picker.FileName);
            Assert.NotEqual((string)Application.Current.FindResource("Str_Dlg_NoSuchFile"), Meta(picker));
            picker.Close();
        }
        // Several quoted names in a multiselect keep their own path and are not read as a pattern.
        {
            var picker = Listed(FileDialogMode.Open, Shown);
            picker.Multiselect = true;
            picker.FileExistsProbe = _ => true;
            var combo = Find<ComboBox>(picker, "FilterCombo");
            int filters = combo.Items.Count;
            Find<TextBox>(picker, "FileNameBox").Text = "\"a.txt\" \"b.txt\"";
            Click(picker);
            Wait(() => picker.FileNames.Length == 2, "the quoted names");
            Assert.Equal(filters, combo.Items.Count);
            picker.Close();
        }
    }

    private static void QuotedDirectory()
    {
        string root = Shown;
        string parent = Path.GetDirectoryName(root)!;
        var asked = new List<string>();
        var picker = Listed(FileDialogMode.Open, root, (dir, _, _) => { lock (asked) asked.Add(dir); return Empty; });
        Enter(picker, "\"" + parent + "\"");
        Wait(() => { lock (asked) return asked.Contains(parent); }, "the unquoted folder");
        Assert.Equal("", Find<TextBox>(picker, "FileNameBox").Text);
        picker.Close();
    }

    private static void DeepVanishedTree()
    {
        string tree = Path.Combine(Shown, "KillerShellDeep-" + Guid.NewGuid().ToString("N"));
        string deep = tree;
        for (int level = 1; level <= 10; level++) deep = Path.Combine(deep, "d" + level);
        Directory.CreateDirectory(deep);
        try
        {
            var picker = new FileDialog(FileDialogMode.Open);
            _ = picker.NavigateDirectoryAsync(deep);
            Wait(() => picker.IsDirectoryListingAvailable, "the deep folder");
            Directory.Delete(tree, true);
            _ = picker.NavigateDirectoryAsync(deep);
            Wait(() => !picker.IsDirectoryListingBusy, "the walk to stop");
            Pump(300);
            Assert.False(picker.IsDirectoryListingAvailable);
            // The walk gave up inside the vanished tree. Each Up carries it further, until a folder lists.
            Assert.True(Find<Button>(picker, "UpButton").IsEnabled);
            for (int press = 0; press < 3 && !picker.IsDirectoryListingAvailable; press++)
            {
                Find<Button>(picker, "UpButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Wait(() => !picker.IsDirectoryListingBusy, "Up to settle");
                Pump(300);
            }
            Assert.True(picker.IsDirectoryListingAvailable, "Up never got past the recovery limit.");
            Assert.Equal(Shown, Find<TextBox>(picker, "PathBox").Text);
            picker.Close();
        }
        finally { try { Directory.Delete(tree, true); } catch (DirectoryNotFoundException) { } }
    }

    private static void Recents()
    {
        ThemeManager.GetSetting = key => key == "FileDlgRecents" ? @"C:\good1|C:\dead|C:\good2" : null;
        using var gate = new ManualResetEventSlim(false);
        int deadProbes = 0;
        var picker = Listed(FileDialogMode.Open, Shown);
        picker.FolderExistsProbe = path =>
        {
            if (path.StartsWith(@"C:\good")) return true;
            Interlocked.Increment(ref deadProbes);
            gate.Wait();
            return false;
        };
        var started = Stopwatch.StartNew();
        var first = picker.ProbeRecentsAsync();
        var second = picker.ProbeRecentsAsync();
        // A click while the probe is in flight does nothing: no second round of probes, no second popup.
        Assert.True(second.IsCompleted);
        Assert.Null(second.Result);
        Wait(() => first.IsCompleted, "the deadline");
        started.Stop();
        Assert.Equal(new[] { @"C:\good1", @"C:\good2" }, first.Result);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), "Took " + started.Elapsed);
        Assert.Equal(1, deadProbes);
        // Once it has answered, the next click probes again.
        gate.Set();
        var third = picker.ProbeRecentsAsync();
        Wait(() => third.IsCompleted, "the next probe");
        Assert.NotNull(third.Result);
        picker.Close();
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

    private static void Wait(Func<bool> done, string what)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (!done())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("Never reached: " + what);
            Pump(10);
        }
    }

    private sealed class TestPresentationSource : PresentationSource
    {
        public override System.Windows.Media.Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget GetCompositionTargetCore() => null!;
    }
}
