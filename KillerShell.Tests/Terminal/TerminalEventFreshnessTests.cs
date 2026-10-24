using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class TerminalEventFreshnessTests
{
    [Fact]
    public Task FinalTextIsAvailableAtItsChangeNotification()
        => RuntimeComponentProcess.Run(nameof(TerminalEventFreshnessScenario));
}

public static class TerminalEventFreshnessScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        string? notifiedText = null;
        bool observe = false;
        var terminal = new TerminalControl(TerminalSkin.Default, (peer, change) =>
        {
            if (observe && change == AutomationEvents.TextPatternOnTextChanged)
            {
                var notifiedProvider = (ITextProvider)peer.GetPattern(PatternInterface.Text);
                notifiedText = notifiedProvider.DocumentRange.GetText(-1);
            }
            peer.RaiseAutomationEvent(change);
        });
        using var source = new HwndSource(new HwndSourceParameters("KillerShell text notification fixture")
        { WindowStyle = unchecked((int)0x80000000), Width = 800, Height = 500 });
        using var output = new TwoStageOutput();
        using var registered = new ManualResetEventSlim();
        using var releaseClient = new ManualResetEventSlim();
        Task client = Task.CompletedTask;
        try
        {
            source.RootVisual = terminal;
            terminal.Measure(new Size(800, 500));
            terminal.Arrange(new Rect(0, 0, 800, 500));
            var peer = UIElementAutomationPeer.CreatePeerForElement(terminal)!;
            var provider = (ITextProvider)peer.GetPattern(PatternInterface.Text);
            IntPtr handle = source.Handle;
            client = Task.Run(() =>
            {
                var root = AutomationElement.FromHandle(handle);
                var element = root.FindFirst(TreeScope.Subtree,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                Assert.NotNull(element);
                AutomationEventHandler listener = (_, _) => { };
                Automation.AddAutomationEventHandler(TextPattern.TextChangedEvent, element, TreeScope.Element, listener);
                try { registered.Set(); releaseClient.Wait(); }
                finally { Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, element, listener); }
            });
            ShellWindowFixture.PumpUntil(() => registered.IsSet || client.IsCompleted);
            if (client.IsFaulted) client.GetAwaiter().GetResult();
            ShellWindowFixture.PumpUntil(() => AutomationPeer.ListenerExists(AutomationEvents.TextPatternOnTextChanged));

            terminal.ReadOutput(output);
            ParseNextOutput(terminal);
            Thread.Sleep(100);
            Assert.Contains("initial", provider.DocumentRange.GetText(-1));
            output.ReleaseFinal.Set();
            ParseNextOutput(terminal);
            observe = true;
            ShellWindowFixture.PumpUntil(() => notifiedText != null);
            Assert.Contains("FINAL-OUTPUT", notifiedText);
        }
        finally
        {
            output.ReleaseFinal.Set();
            releaseClient.Set();
            ShellWindowFixture.PumpUntil(() => client.IsCompleted);
            try { client.GetAwaiter().GetResult(); }
            finally { terminal.Close(); source.RootVisual = null; app.Shutdown(); }
        }
    }

    private static void ParseNextOutput(TerminalControl terminal)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 5000)
        {
            if (terminal.ProcessOutput() > 0) return;
            Thread.Sleep(1);
        }
        Assert.Fail("The controlled terminal output did not reach the parser.");
    }

    private sealed class TwoStageOutput : Stream
    {
        internal readonly ManualResetEventSlim ReleaseFinal = new();
        private int _stage;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int stage = _stage++;
            if (stage > 1) return 0;
            if (stage == 1) ReleaseFinal.Wait();
            byte[] bytes = Encoding.UTF8.GetBytes(stage == 0 ? "initial\r\n" : "FINAL-OUTPUT\r\n");
            Array.Copy(bytes, 0, buffer, offset, bytes.Length);
            return bytes.Length;
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
