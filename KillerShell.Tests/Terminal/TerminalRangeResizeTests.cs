using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using System.Windows.Threading;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Trait("Category", "Window")]
public sealed class TerminalRangeResizeTests
{
    [Fact]
    public Task RetainedRangeKeepsItsSurvivingLineAcrossRealLayoutChanges()
        => RuntimeComponentProcess.Run(nameof(TerminalRangeResizeScenario));
}

public static class TerminalRangeResizeScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        var terminal = new TerminalControl(TerminalSkin.Default);
        using var source = new HwndSource(new HwndSourceParameters("KillerShell terminal range resize fixture")
        { WindowStyle = unchecked((int)0x80000000), Width = 1000, Height = 700 });
        try
        {
            terminal.Width = 800;
            terminal.Height = 500;
            var panel = new Grid();
            panel.Children.Add(terminal);
            source.RootVisual = panel;
            terminal.Measure(new Size(800, 500));
            terminal.Arrange(new Rect(0, 0, 800, 500));
            Pump();
            terminal.StartDemo("first\r\nsecond café\U0001F600\r\nthird");
            var peer = UIElementAutomationPeer.CreatePeerForElement(terminal)!;
            var provider = (ITextProvider)peer.GetPattern(PatternInterface.Text);
            var retained = provider.DocumentRange.FindText("second café\U0001F600", false, false);
            Assert.NotNull(retained);
            Assert.Equal("second café\U0001F600", retained!.GetText(-1));
            int originalColumns = terminal.Buffer.Cols;
            foreach (var size in new[] { new Size(900, 600), new Size(700, 450) })
            {
                terminal.Width = size.Width;
                terminal.Height = size.Height;
                terminal.Measure(size);
                terminal.Arrange(new Rect(new Point(), size));
                Pump();
                Assert.NotEqual(originalColumns, terminal.Buffer.Cols);
                Assert.Equal("second café\U0001F600", retained.GetText(-1));
                retained.Select();
                Assert.Equal("second café\U0001F600", Assert.Single(provider.GetSelection()).GetText(-1));
                retained.ScrollIntoView(true);
                Assert.Contains("second café\U0001F600", Assert.Single(provider.GetVisibleRanges()).GetText(-1));
            }
        }
        finally { terminal.Close(); source.RootVisual = null; app.Shutdown(); }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
