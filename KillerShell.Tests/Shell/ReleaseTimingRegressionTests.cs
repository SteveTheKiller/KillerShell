using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShell;
using KillerShell.Models;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class ReleaseTimingRegressionTests
{
    [Fact]
    public Task NavigationDuringRootProbingPreservesTheNewFolder()
        => RuntimeComponentProcess.Run(nameof(PendingSearchNavigationScenario));
}

public static class PendingSearchNavigationScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = (FilePane)window.FindName("LeftPane");
        ShellWindowFixture.PumpUntil(() => pane.Active.StatusKey == "Str_Status_Listed");
        string destination = Directory.CreateDirectory(Path.Combine(fixture.Root, "destination")).FullName;
        File.WriteAllText(Path.Combine(fixture.Root, "needle.txt"), "old folder");
        File.WriteAllText(Path.Combine(destination, "kept.txt"), "new folder");
        var group = new TermGroup();
        group.Terms.Add(new SearchTerm { Mode = SearchTerm.SearchMode.FileName, Pattern = "needle.txt" });
        pane.Active.Groups.Clear();
        pane.Active.Groups.Add(group);

        ThreadPool.GetMinThreads(out int minimumWorkers, out int minimumIo);
        ThreadPool.GetMaxThreads(out int maximumWorkers, out int maximumIo);
        using var occupied = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var originalContext = SynchronizationContext.Current!;
        var operations = new ObservedOperations(originalContext);
        Task blocker = Task.CompletedTask;
        try
        {
            Assert.True(ThreadPool.SetMinThreads(1, minimumIo));
            Assert.True(ThreadPool.SetMaxThreads(1, maximumIo));
            blocker = Task.Run(() => { occupied.Set(); release.Wait(); });
            Assert.True(occupied.Wait(5000), "The controlled root-probe delay did not start.");
            SynchronizationContext.SetSynchronizationContext(operations);
            ShellWindowFixture.Click(window, "SearchButton");
            Assert.True(operations.Started > 0, "The search did not enter its asynchronous operation.");

            pane.AddressBox.Text = destination;
            pane.AddressBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(pane.AddressBox), Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent });
            Assert.Equal(destination, pane.Active.CurrentFolder);

            release.Set();
            Assert.True(ThreadPool.SetMaxThreads(maximumWorkers, maximumIo));
            Assert.True(ThreadPool.SetMinThreads(minimumWorkers, minimumIo));
            ShellWindowFixture.PumpUntil(() => operations.Pending == 0);
            ShellWindowFixture.PumpUntil(() => pane.Active.StatusKey == "Str_Status_Listed" || pane.Active.HasSearch);
            Assert.True(pane.Active.IsBrowsing, "The old search replaced the newly navigated folder.");
            Assert.Equal(destination, pane.Active.CurrentFolder);
            Assert.Equal(destination, pane.ScopePathLabel.Text);
            Assert.Contains(pane.Active.Results, row => row.FilePath == Path.Combine(destination, "kept.txt"));
        }
        finally
        {
            release.Set();
            ThreadPool.SetMaxThreads(maximumWorkers, maximumIo);
            ThreadPool.SetMinThreads(minimumWorkers, minimumIo);
            blocker.GetAwaiter().GetResult();
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }
    }

    private sealed class ObservedOperations(SynchronizationContext dispatcher) : SynchronizationContext
    {
        private int _pending, _started;
        internal int Pending => Volatile.Read(ref _pending);
        internal int Started => Volatile.Read(ref _started);
        public override void OperationStarted() { Interlocked.Increment(ref _pending); Interlocked.Increment(ref _started); }
        public override void OperationCompleted() => Interlocked.Decrement(ref _pending);
        public override void Post(SendOrPostCallback callback, object? state) => dispatcher.Post(callback, state);
        public override void Send(SendOrPostCallback callback, object? state) => dispatcher.Send(callback, state);
    }
}
