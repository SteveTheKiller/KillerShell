using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using System.Windows.Interop;
using System.Windows.Threading;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class TerminalAutomationTests
{
    [Fact]
    public Task TerminalExposesItsOutputThroughTextAutomation()
        => RuntimeComponentProcess.Run(nameof(TerminalAutomationScenario));

    [Fact]
    public Task NativeAutomationSelectionCopiesFullUnicodeWithoutTouchingTheHostClipboard()
        => RuntimeComponentProcess.Run(nameof(TerminalClipboardScenario));
}

public static class TerminalClipboardScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        string? copied = null;
        int writes = 0;
        var terminal = new TerminalControl(TerminalSkin.Default, clipboardWrite: text => { copied = text; writes++; });
        using var source = new HwndSource(new HwndSourceParameters("KillerShell native terminal copy fixture")
        { WindowStyle = unchecked((int)0x80000000), Width = 800, Height = 500 });
        try
        {
            source.RootVisual = terminal;
            terminal.Measure(new Size(800, 500));
            terminal.Arrange(new Rect(0, 0, 800, 500));
            terminal.StartDemo("café\U0001F600");
            IntPtr handle = source.Handle;
            Task client = Task.Run(() =>
            {
                var root = AutomationElement.FromHandle(handle);
                var element = root.FindFirst(TreeScope.Subtree,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                Assert.NotNull(element);
                var text = (TextPattern)element.GetCurrentPattern(TextPattern.Pattern);
                var selection = text.DocumentRange.FindText("café\U0001F600", false, false);
                Assert.NotNull(selection);
                selection.Select();
                Assert.Equal("café\U0001F600", Assert.Single(text.GetSelection()).GetText(-1));
            });
            var deadline = Stopwatch.StartNew();
            while (!client.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
            }
            Assert.True(client.IsCompleted, "The native clipboard selection fixture timed out.");
            client.GetAwaiter().GetResult();
            Assert.True(terminal.CopySelection());
            Assert.Equal(1, writes);
            Assert.Equal("café\U0001F600", copied);
        }
        finally { terminal.Close(); source.RootVisual = null; app.Shutdown(); }
    }
}

public static class TerminalAutomationScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        var terminal = new TerminalControl(TerminalSkin.Default);
        try
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(terminal);
            Assert.NotNull(peer);
            var provider = Assert.IsAssignableFrom<ITextProvider>(peer!.GetPattern(PatternInterface.Text));
            Assert.Equal("Terminal", peer.GetName());
            Assert.Equal(AutomationControlType.Document, peer.GetAutomationControlType());
            Assert.True(terminal.Focusable);
            Assert.Equal(SupportedTextSelection.Single, provider.SupportedTextSelection);
            using var source = new HwndSource(new HwndSourceParameters("KillerShell terminal automation fixture")
            { WindowStyle = unchecked((int)0x80000000), Width = 800, Height = 500 });
            source.RootVisual = terminal;
            terminal.Measure(new Size(800, 500));
            terminal.Arrange(new Rect(0, 0, 800, 500));
            terminal.StartDemo("first\r\ncafé\U0001F600\r\nlast");
            Assert.Contains("first\r\ncafé\U0001F600\r\nlast", provider.DocumentRange.GetText(-1));
            Assert.Equal("", Assert.Single(provider.GetSelection()).GetText(-1));
            var word = provider.DocumentRange.FindText("café\U0001F600", false, false);
            Assert.NotNull(word);
            word!.Select();
            Assert.True(terminal.HasSelection);
            Assert.Equal("café\U0001F600", Assert.Single(provider.GetSelection()).GetText(-1));
            Assert.NotEmpty(word.GetBoundingRectangles());
            var scalar = word.Clone();
            scalar.ExpandToEnclosingUnit(TextUnit.Character);
            Assert.Equal("c", scalar.GetText(-1));
            Assert.Equal(4, scalar.Move(TextUnit.Character, 4));
            Assert.Equal("\U0001F600", scalar.GetText(-1));
            Assert.Equal("", scalar.GetText(1));
            terminal.ClearSelection();
            Assert.Equal("", Assert.Single(provider.GetSelection()).GetText(-1));
            terminal.SelectAll();
            Assert.Contains("café\U0001F600", Assert.Single(provider.GetSelection()).GetText(-1));
            terminal.ClearSelection();
            // Existing ranges follow retained buffer lines as output enters scrollback.
            for (int index = 0; index < 80; index++)
            {
                terminal.Buffer.Execute(13);
                terminal.Buffer.Execute(10);
                terminal.Buffer.Print('z');
            }
            Assert.Equal("café\U0001F600", word.GetText(-1));
            Assert.DoesNotContain("café", Assert.Single(provider.GetVisibleRanges()).GetText(-1));
            word.ScrollIntoView(true);
            Assert.Contains("café\U0001F600", Assert.Single(provider.GetVisibleRanges()).GetText(-1));
            word.Select();
            Assert.Equal("café\U0001F600", Assert.Single(provider.GetSelection()).GetText(-1));
            source.RootVisual = null;
        }
        finally { terminal.Close(); app.Shutdown(); }
    }
}
