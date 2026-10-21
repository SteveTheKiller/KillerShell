using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Text;
using System.Windows.Interop;
using System.Windows.Threading;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class TerminalNativeAutomationTests
{
    [Fact]
    public Task WindowsTextPatternReadsOutputSelectionAndBoundedUpdates()
        => RuntimeComponentProcess.Run(nameof(TerminalNativeAutomationScenario));

    [Fact]
    public Task WindowsTextPatternProducerCompletesWithOneThreadPoolWorker()
        => RuntimeComponentProcess.Run(nameof(TerminalSingleWorkerAutomationScenario));

    [Fact]
    public Task WindowsTextPatternReceivesNativeEventsWithOneThreadPoolWorker()
        => RuntimeComponentProcess.Run(nameof(TerminalSingleWorkerNativeEventsScenario));
}

public static class TerminalSingleWorkerNativeEventsScenario
{
    public static void Run()
    {
        ThreadPool.GetMinThreads(out _, out int minimumIo);
        ThreadPool.GetMaxThreads(out _, out int maximumIo);
        Assert.True(ThreadPool.SetMinThreads(1, minimumIo));
        Assert.True(ThreadPool.SetMaxThreads(1, maximumIo));
        TerminalNativeAutomationScenario.Run();
    }
}

public static class TerminalSingleWorkerAutomationScenario
{
    public static void Run()
    {
        ThreadPool.GetMinThreads(out _, out int minimumIo);
        ThreadPool.GetMaxThreads(out _, out int maximumIo);
        Assert.True(ThreadPool.SetMinThreads(1, minimumIo));
        Assert.True(ThreadPool.SetMaxThreads(1, maximumIo));
        ThreadPool.GetMaxThreads(out int workers, out _);
        Assert.Equal(1, workers);
        Console.WriteLine("Controlled child-process ThreadPool worker limit=" + workers);
        TerminalNativeAutomationScenario.RunWithBlockedWorker();
    }
}

public static class TerminalNativeAutomationScenario
{
    public static void RunWithBlockedWorker()
    {
        using var workerEntered = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        using var output = new OutputStream();
        using var cancel = new CancellationTokenSource();
        var startNoise = new TaskCompletionSource<bool>();
        Application? app = null;
        TerminalControl? terminal = null;
        Task blockedWorker = Task.CompletedTask;
        Task producer = Task.CompletedTask;
        try
        {
            blockedWorker = Task.Run(() => { workerEntered.Set(); releaseWorker.Wait(); });
            Assert.True(workerEntered.Wait(5000), "The controlled pool worker did not start.");
            app = RuntimeComponentProcess.StartApplication();
            terminal = new TerminalControl(TerminalSkin.Default);
            producer = StartProducer(output, cancel.Token, startNoise.Task);
            terminal.ReadOutput(output);
            startNoise.SetResult(true);
            Wait(() => producer.IsCompleted, "bounded producer with occupied pool worker", 10000);
            producer.GetAwaiter().GetResult();
            Assert.False(blockedWorker.IsCompleted);
            Wait(() => string.Join("", Enumerable.Range(0, terminal.Buffer.TotalLines)
                .Select(line => new string(terminal.Buffer.LineAt(line).Select(cell => (char)cell.Ch).ToArray())))
                .Contains("noise-0099"), "parsed bounded producer output");
        }
        finally
        {
            cancel.Cancel();
            startNoise.TrySetResult(true);
            releaseWorker.Set();
            try
            {
                terminal?.Close();
                Wait(() => producer.IsCompleted && blockedWorker.IsCompleted, "controlled producer shutdown");
                producer.GetAwaiter().GetResult();
                blockedWorker.GetAwaiter().GetResult();
            }
            finally { app?.Shutdown(); }
        }
    }

    private static Task StartProducer(OutputStream output, CancellationToken cancel, Task startNoise)
        => Task.Factory.StartNew(() =>
        {
            startNoise.GetAwaiter().GetResult();
            try
            {
                for (int index = 0; index < 100; index++)
                {
                    output.Append("noise-" + index.ToString("D4") + "\r\n", cancel);
                    if (cancel.WaitHandle.WaitOne(10)) cancel.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException) { }
            finally { output.Complete(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        var notificationClock = Stopwatch.StartNew();
        var providerEmissions = new ConcurrentQueue<long>();
        var terminal = new TerminalControl(TerminalSkin.Default, (peer, notification) =>
        {
            if (notification == AutomationEvents.TextPatternOnTextChanged)
                providerEmissions.Enqueue(notificationClock.ElapsedMilliseconds);
            peer.RaiseAutomationEvent(notification);
        });
        using var output = new OutputStream();
        using var cancel = new CancellationTokenSource();
        var startNoise = new TaskCompletionSource<bool>();
        using var source = new HwndSource(new HwndSourceParameters("KillerShell native terminal automation fixture")
        { WindowStyle = unchecked((int)0x80000000), Width = 800, Height = 500 });
        source.RootVisual = terminal;
        terminal.Measure(new Size(800, 500));
        terminal.Arrange(new Rect(0, 0, 800, 500));
        output.Append("native café\U0001F600\r\n", cancel.Token);
        terminal.ReadOutput(output);
        Task producer = StartProducer(output, cancel.Token, startNoise.Task);
        try
        {
            Wait(() => terminal.Buffer.LineAt(0)[0].Ch == 'n', "initial output");
            IntPtr handle = source.Handle;
            Task client = Task.Run(() => Client(handle, startNoise, producer, notificationClock, providerEmissions));
            Wait(() => client.IsCompleted, "native UI Automation client");
            client.GetAwaiter().GetResult();
            Console.WriteLine("PASS native TextPattern: Unicode output, caret, selection, range movement, visible ranges, scrollback and bounded TextChanged events.");
        }
        finally
        {
            cancel.Cancel();
            startNoise.TrySetResult(true);
            terminal.Close();
            Wait(() => producer.IsCompleted, "bounded producer shutdown");
            producer.GetAwaiter().GetResult();
            source.RootVisual = null;
            app.Shutdown();
        }
    }

    private static void Client(IntPtr handle, TaskCompletionSource<bool> startNoise, Task producer,
                               Stopwatch eventClock, ConcurrentQueue<long> providerEmissions)
    {
        Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
        AutomationElement root = AutomationElement.FromHandle(handle);
        var terminal = root.FindFirst(TreeScope.Subtree,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        Assert.NotNull(terminal);
        Assert.Equal("Terminal", terminal.Current.Name);
        var text = (TextPattern)terminal.GetCurrentPattern(TextPattern.Pattern);
        Assert.Contains("native café\U0001F600", text.DocumentRange.GetText(-1));
        Assert.Equal("", Assert.Single(text.GetSelection()).GetText(-1));
        Assert.NotEmpty(text.GetVisibleRanges());
        var unicode = text.DocumentRange.FindText("café\U0001F600", false, false);
        Assert.NotNull(unicode);
        unicode.Select();
        Assert.Equal("café\U0001F600", Assert.Single(text.GetSelection()).GetText(-1));
        Assert.NotEmpty(unicode.GetBoundingRectangles());
        var scalar = unicode.Clone();
        Assert.Equal(4, scalar.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, 4));
        Assert.Equal("\U0001F600", scalar.GetText(-1));

        int notifications = 0;
        var eventTimes = new ConcurrentQueue<string>();
        AutomationEventHandler changed = (sender, args) =>
        {
            if (args.EventId == TextPattern.TextChangedEvent) Interlocked.Increment(ref notifications);
            eventTimes.Enqueue(eventClock.ElapsedMilliseconds + ":" +
                args.EventId.Id + ":" + string.Join(".", ((AutomationElement)sender).GetRuntimeId()));
        };
        Automation.AddAutomationEventHandler(TextPattern.TextChangedEvent, terminal, TreeScope.Element, changed);
        try
        {
            var observation = Stopwatch.StartNew();
            startNoise.TrySetResult(true);
            Assert.True(producer.Wait(10000), "The bounded output producer did not complete.");
            producer.GetAwaiter().GetResult();
            var deadline = Stopwatch.StartNew();
            while (!text.DocumentRange.GetText(-1).Contains("noise-0099") && deadline.ElapsedMilliseconds < 5000)
                Thread.Sleep(20);
            Assert.Contains("noise-0099", text.DocumentRange.GetText(-1));
            Thread.Sleep(500);
            int allowed = (int)Math.Ceiling(observation.Elapsed.TotalSeconds * 5) + 2;
            Console.WriteLine("native_text_changed_events=" + Volatile.Read(ref notifications) +
                "; observation_ms=" + observation.ElapsedMilliseconds +
                "; provider_emissions=" + string.Join(",", providerEmissions) +
                "; event_times=" + string.Join(",", eventTimes));
            long[] emitted = providerEmissions.ToArray();
            Assert.InRange(emitted.Length, 1, allowed);
            for (int index = 1; index < emitted.Length; index++)
                Assert.True(emitted[index] - emitted[index - 1] >= 200,
                    "The provider emitted text notifications faster than the bounded rate.");
            // The Windows UIA bridge can deliver the same emission more than once.
            Assert.True(Volatile.Read(ref notifications) > 0, "The native client received no text notification.");
            Assert.Contains("noise-0099", string.Join("", text.GetVisibleRanges().Select(range => range.GetText(-1))));
            Assert.Equal("café\U0001F600", unicode.GetText(-1));
            unicode.ScrollIntoView(true);
            Assert.Contains("café\U0001F600", string.Join("", text.GetVisibleRanges().Select(range => range.GetText(-1))));
        }
        finally { Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, terminal, changed); }
    }

    private static void Wait(Func<bool> complete, string stage, int timeoutMilliseconds = 20000)
    {
        var watch = Stopwatch.StartNew();
        while (!complete() && watch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        Assert.True(complete(), "Timed out: " + stage);
    }

    private sealed class OutputStream : Stream
    {
        private readonly BlockingCollection<byte[]> _chunks = new(4);
        private byte[]? _current;
        private int _offset;
        internal void Append(string text, CancellationToken cancel) => _chunks.Add(Encoding.UTF8.GetBytes(text), cancel);
        internal void Complete() => _chunks.CompleteAdding();
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_current == null || _offset == _current.Length)
            {
                try { _current = _chunks.Take(); }
                catch (InvalidOperationException) { return 0; }
                _offset = 0;
            }
            int read = Math.Min(count, _current.Length - _offset);
            Array.Copy(_current, _offset, buffer, offset, read);
            _offset += read;
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
