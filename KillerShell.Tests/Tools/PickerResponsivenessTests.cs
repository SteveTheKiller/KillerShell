using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class PickerResponsivenessTests
{
    [Fact]
    public Task FilePickerProcessesHeartbeatDuringStorageRead()
        => RuntimeComponentProcess.Run(nameof(FilePickerResponsivenessScenario));

    [Fact]
    public Task FolderPickerProcessesHeartbeatDuringStorageRead()
        => RuntimeComponentProcess.Run(nameof(FolderPickerResponsivenessScenario));

    [Fact]
    public Task FilePickerPlacesDoNotBlock() => RuntimeComponentProcess.Run(nameof(FilePickerPlacesScenario));
    [Fact]
    public Task FolderPickerPlacesDoNotBlock() => RuntimeComponentProcess.Run(nameof(FolderPickerPlacesScenario));
    [Fact]
    public Task FilePickerValidationDoesNotBlock() => RuntimeComponentProcess.Run(nameof(FilePickerValidationScenario));
    [Fact]
    public Task FolderPickerValidationDoesNotBlock() => RuntimeComponentProcess.Run(nameof(FolderPickerValidationScenario));
}

public static class FilePickerPlacesScenario { public static void Run() => PickerResponsivenessScenario.CheckBoundary(false, true); }
public static class FolderPickerPlacesScenario { public static void Run() => PickerResponsivenessScenario.CheckBoundary(true, true); }
public static class FilePickerValidationScenario { public static void Run() => PickerResponsivenessScenario.CheckBoundary(false, false); }
public static class FolderPickerValidationScenario { public static void Run() => PickerResponsivenessScenario.CheckBoundary(true, false); }

public static class FilePickerResponsivenessScenario
{
    public static void Run() => PickerResponsivenessScenario.Run(false);
}

public static class FolderPickerResponsivenessScenario
{
    public static void Run() => PickerResponsivenessScenario.Run(true);
}

internal static class PickerResponsivenessScenario
{
    internal static void CheckBoundary(bool folder, bool places)
    {
        var app = RuntimeComponentProcess.StartApplication();
        Window picker = folder ? new FolderPickerDialog(Path.GetTempPath()) : new FileDialog();
        using var heartbeat = new ManualResetEventSlim();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => heartbeat.Set();
        int pulseDuringRead = 0;
        bool Probe()
        {
            bool pulsed = heartbeat.Wait(TimeSpan.FromSeconds(1));
            Interlocked.Exchange(ref pulseDuringRead, pulsed ? 1 : -1);
            return false;
        }
        try
        {
            PumpUntil(() => Navigation(picker).IsCompleted);
            if (places)
            {
                (string Label, string Path)[] Read(CancellationToken token) { Probe(); return []; }
                if (picker is FileDialog file) file.ReadDrives = Read;
                else ((FolderPickerDialog)picker).ReadDrives = Read;
            }
            else
            {
                if (picker is FileDialog file)
                {
                    file.PathIsFile = _ => Probe();
                    ((TextBox)picker.FindName("FileNameBox")).Text = Path.Combine(Path.GetTempPath(), "missing.txt");
                }
                else
                {
                    ((FolderPickerDialog)picker).FolderExists = _ => Probe();
                    ((TextBox)picker.FindName("PathBox")).Text = Path.GetTempPath();
                }
            }
            timer.Start();
            picker.GetType().GetMethod(places ? "BuildPlaces" : "OK_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(picker, places ? null : new object[] { picker, new RoutedEventArgs() });
            PumpUntil(() => Volatile.Read(ref pulseDuringRead) != 0);
            Assert.Equal(1, pulseDuringRead);
            Task pending = picker is FileDialog dialog
                ? (places ? dialog.PlacesTask : dialog.AcceptanceTask)
                : (places ? ((FolderPickerDialog)picker).PlacesTask : ((FolderPickerDialog)picker).AcceptanceTask);
            PumpUntil(() => pending.IsCompleted);
        }
        finally { timer.Stop(); picker.Close(); app.Shutdown(); }
    }

    internal static void Run(bool folder)
    {
        var app = RuntimeComponentProcess.StartApplication();
        Window picker = folder ? new FolderPickerDialog(Path.GetTempPath()) : new FileDialog();
        using var heartbeat = new ManualResetEventSlim();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => heartbeat.Set();
        int pulseDuringRead = 0;
        try
        {
            PickerEntry[] Read(string path, CancellationToken token)
            {
                bool pulsed = heartbeat.Wait(TimeSpan.FromSeconds(1));
                Interlocked.Exchange(ref pulseDuringRead, pulsed ? 1 : -1);
                return [new PickerEntry("sample.txt", Path.Combine(path, "sample.txt"), false, 1, DateTime.MinValue)];
            }
            SetReader(picker, Read);
            timer.Start();
            Navigate(picker, Path.GetTempPath());
            PumpUntil(() => Entries(picker).Count == 1 && Volatile.Read(ref pulseDuringRead) != 0);
            Assert.Equal(1, pulseDuringRead);
            timer.Stop();
            LatestRequestWins(picker);
            SelectionAndSortingSurvive(picker);
            FailureDoesNotReplaceTheCurrentRows(picker);
            PublicationYieldsToInput(picker);
            NewSelectionDuringPublicationSurvives(picker);
            NewNavigationStopsOldPublication(picker);
            ClosureRejectsLateResults(picker);
        }
        finally { timer.Stop(); picker.Close(); app.Shutdown(); }
    }

    private static void LatestRequestWins(Window picker)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        SetReader(picker, (path, token) =>
        {
            if (path.EndsWith("old", StringComparison.Ordinal))
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return [Entry("old.txt")];
            }
            return [Entry("new.txt")];
        });
        Navigate(picker, Path.Combine(Path.GetTempPath(), "old"));
        Task old = Navigation(picker);
        PumpUntil(() => entered.IsSet);
        Navigate(picker, Path.Combine(Path.GetTempPath(), "new"));
        PumpUntil(() => Navigation(picker).IsCompleted);
        Assert.Equal("new.txt", Assert.Single(Entries(picker)).Name);
        release.Set();
        PumpUntil(() => old.IsCompleted);
        Assert.Equal("new.txt", Assert.Single(Entries(picker)).Name);
    }

    private static void PublicationYieldsToInput(Window picker)
    {
        int countAtInput = -1;
        bool scheduled = false;
        Entries(picker).CollectionChanged += (_, _) =>
        {
            if (scheduled || Entries(picker).Count == 0) return;
            scheduled = true;
            picker.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                new Action(() => countAtInput = Entries(picker).Count));
        };
        SetReader(picker, (path, token) => Enumerable.Range(0, 1024).Select(i => Entry(i.ToString("D4") + ".txt")).ToArray());
        Navigate(picker, Path.GetTempPath());
        PumpUntil(() => Navigation(picker).IsCompleted && countAtInput >= 0);
        Assert.Equal(1024, Entries(picker).Count);
        Assert.InRange(countAtInput, 1, 128);
    }

    private static void SelectionAndSortingSurvive(Window picker)
    {
        var selected = new PickerEntry("b.txt", Path.Combine(Path.GetTempPath(), "b.txt"), false, 10, DateTime.MinValue);
        SetReader(picker, (path, token) =>
        [
            new PickerEntry("folder", Path.Combine(path, "folder"), true, 0, DateTime.MinValue),
            new PickerEntry("a.txt", Path.Combine(path, "a.txt"), false, 20, DateTime.MinValue),
            selected,
            new PickerEntry("other.bin", Path.Combine(path, "other.bin"), false, 30, DateTime.MinValue),
        ]);
        Navigate(picker, Path.GetTempPath());
        PumpUntil(() => Navigation(picker).IsCompleted);
        var list = (ListBox)picker.FindName(picker is FileDialog ? "FileList" : "FolderList");
        list.ItemsSource = Entries(picker);
        list.SelectedItem = selected;
        picker.GetType().GetMethod("SetSort", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, new object[] { 1 });
        PumpUntil(() => (picker is FileDialog file ? file.SortTask : ((FolderPickerDialog)picker).SortTask).IsCompleted);
        Assert.Same(selected, list.SelectedItem);
        Assert.True(Entries(picker)[0].IsFolder);
        Assert.Equal(picker is FileDialog ? "b.txt" : "other.bin", Entries(picker)[1].Name);
        if (picker is FileDialog dialog)
        {
            dialog.Filter = "Text|*.txt";
            typeof(FileDialog).GetMethod("BuildFilters", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null);
            typeof(FileDialog).GetMethod("ApplySort", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null);
            PumpUntil(() => dialog.SortTask.IsCompleted);
            Assert.DoesNotContain(dialog.Entries, entry => entry.Name == "other.bin");
            Assert.Same(selected, list.SelectedItem);
        }
    }

    private static void FailureDoesNotReplaceTheCurrentRows(Window picker)
    {
        var before = Entries(picker).ToArray();
        SetReader(picker, (path, token) => throw new IOException("Controlled unavailable share"));
        Navigate(picker, Path.GetTempPath());
        PumpUntil(() => Navigation(picker).IsCompleted);
        Assert.Equal(before, Entries(picker));
        Assert.Equal(Application.Current.FindResource("Str_Dlg_NoSuchFolder"), ((TextBlock)picker.FindName("SelMeta")).Text);
    }

    private static void NewNavigationStopsOldPublication(Window picker)
    {
        bool scheduled = false;
        System.Collections.Specialized.NotifyCollectionChangedEventHandler changed = (_, _) =>
        {
            if (scheduled || Entries(picker).Count == 0) return;
            scheduled = true;
            picker.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                new Action(() => Navigate(picker, Path.Combine(Path.GetTempPath(), "new"))));
        };
        Entries(picker).CollectionChanged += changed;
        try
        {
            SetReader(picker, (path, token) => path.EndsWith("new", StringComparison.Ordinal)
                ? [Entry("replacement.txt")]
                : Enumerable.Range(0, 1024).Select(i => Entry(i.ToString("D4") + ".txt")).ToArray());
            Navigate(picker, Path.Combine(Path.GetTempPath(), "large"));
            Task old = Navigation(picker);
            PumpUntil(() => !ReferenceEquals(Navigation(picker), old) && Navigation(picker).IsCompleted);
            Assert.Equal("replacement.txt", Assert.Single(Entries(picker)).Name);
        }
        finally { Entries(picker).CollectionChanged -= changed; }
    }

    private static void NewSelectionDuringPublicationSurvives(Window picker)
    {
        var list = (ListBox)picker.FindName(picker is FileDialog ? "FileList" : "FolderList");
        list.SelectedItem = Entries(picker).Last();
        PickerEntry? newSelection = null;
        bool scheduled = false;
        System.Collections.Specialized.NotifyCollectionChangedEventHandler changed = (_, _) =>
        {
            if (scheduled || Entries(picker).Count == 0) return;
            scheduled = true;
            picker.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                newSelection = Entries(picker)[0];
                list.SelectedItem = newSelection;
            }));
        };
        Entries(picker).CollectionChanged += changed;
        try
        {
            picker.GetType().GetMethod("ApplySort", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, null);
            PumpUntil(() => (picker is FileDialog file ? file.SortTask : ((FolderPickerDialog)picker).SortTask).IsCompleted);
            Assert.NotNull(newSelection);
            Assert.Same(newSelection, list.SelectedItem);
        }
        finally { Entries(picker).CollectionChanged -= changed; }
    }

    private static void ClosureRejectsLateResults(Window picker)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        SetReader(picker, (path, token) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return [Entry("closed.txt")];
        });
        Navigate(picker, Path.GetTempPath());
        Task closing = Navigation(picker);
        PumpUntil(() => entered.IsSet);
        picker.Close();
        release.Set();
        PumpUntil(() => closing.IsCompleted);
        Assert.DoesNotContain(Entries(picker), entry => entry.Name == "closed.txt");
    }

    private static PickerEntry Entry(string name) => new(name, Path.Combine(Path.GetTempPath(), name), false, 1, DateTime.MinValue);

    private static Task Navigation(Window picker) => picker is FileDialog file ? file.NavigationTask : ((FolderPickerDialog)picker).NavigationTask;

    private static void SetReader(Window picker, Func<string, CancellationToken, PickerEntry[]?> read)
    {
        PickerListing? Listing(string path, CancellationToken token)
            => read(path, token) is { } entries ? new PickerListing(entries, PickerListingLimit.None) : null;
        if (picker is FileDialog file) file.ReadDirectory = Listing;
        else ((FolderPickerDialog)picker).ReadDirectory = Listing;
    }

    private static ObservableCollection<PickerEntry> Entries(Window picker)
        => picker is FileDialog file ? file.Entries : ((FolderPickerDialog)picker).Entries;

    private static void Navigate(Window picker, string path)
        => picker.GetType().GetMethod("NavigateTo", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(picker, new object[] { path });

    private static void PumpUntil(Func<bool> complete)
    {
        var elapsed = Stopwatch.StartNew();
        while (!complete() && elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
        Assert.True(complete(), "Picker work did not finish before the fixture deadline.");
    }
}
