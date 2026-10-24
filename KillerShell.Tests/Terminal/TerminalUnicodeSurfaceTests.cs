using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Trait("Category", "Window")]
public sealed class TerminalUnicodeSurfaceTests
{
    [Fact]
    public Task NativeTextRangesAndClipboardShareUnicodeCellBoundaries()
        => RuntimeComponentProcess.Run(nameof(TerminalUnicodeAutomationScenario));

    [Fact]
    public Task RenderedGlyphRunsUseWideAdvancesAndRetainCombiningText()
        => RuntimeComponentProcess.Run(nameof(TerminalUnicodeRenderScenario));
}

public static class TerminalUnicodeAutomationScenario
{
    internal const string Sample = "A\u65e5B e\u0301 \U0001F600D";

    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        string? copied = null;
        var terminal = new TerminalControl(TerminalSkin.Default, clipboardWrite: text => copied = text);
        using var source = new HwndSource(new HwndSourceParameters("KillerShell Unicode text fixture")
            { WindowStyle = unchecked((int)0x80000000), Width = 800, Height = 500 });
        try
        {
            source.RootVisual = terminal;
            terminal.Measure(new Size(800, 500));
            terminal.Arrange(new Rect(0, 0, 800, 500));
            string script = "[Console]::OutputEncoding = New-Object Text.UTF8Encoding; [Console]::Write('" +
                Sample + "'); Start-Sleep -Seconds 30";
            terminal.Start("powershell.exe -NoProfile -NonInteractive -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)), Path.GetTempPath());
            Wait(() => string.Concat(terminal.Buffer.LineAt(0).Select(cell => cell.Text)).Contains(Sample));
            IntPtr handle = source.Handle;
            Task client = Task.Run(() =>
            {
                var element = AutomationElement.FromHandle(handle).FindFirst(TreeScope.Subtree,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                Assert.NotNull(element);
                var provider = (TextPattern)element.GetCurrentPattern(TextPattern.Pattern);
                Assert.StartsWith(Sample, provider.DocumentRange.GetText(-1));
                var a = provider.DocumentRange.FindText("A", false, false).GetBoundingRectangles()[0];
                var wide = provider.DocumentRange.FindText("\u65e5", false, false).GetBoundingRectangles()[0];
                Assert.Equal(a.Width * 2, wide.Width, 5);
                var b = provider.DocumentRange.FindText("B", false, false).GetBoundingRectangles()[0];
                Assert.Equal(a.X + a.Width * 3, b.X, 5);
                var combined = provider.DocumentRange.FindText("e\u0301", false, false);
                combined.ExpandToEnclosingUnit(TextUnit.Character);
                Assert.Equal("e\u0301", combined.GetText(-1));
                Assert.Equal("e\u0301", provider.DocumentRange.FindText("\u0301", false, false).GetText(-1));
                Assert.Equal(a.Width, combined.GetBoundingRectangles()[0].Width, 5);
                Assert.Equal(1, combined.Move(TextUnit.Character, 1));
                Assert.Equal(" ", combined.GetText(-1));
                var selection = provider.DocumentRange.FindText(Sample, false, false);
                selection.Select();
                Assert.Equal(Sample, Assert.Single(provider.GetSelection()).GetText(-1));
            });
            Wait(() => client.IsCompleted);
            client.GetAwaiter().GetResult();
            Assert.True(terminal.CopySelection());
            Assert.Equal(Sample, copied);
        }
        finally { terminal.Close(); source.RootVisual = null; app.Shutdown(); }
    }

    internal static void Wait(Func<bool> complete)
    {
        var watch = Stopwatch.StartNew();
        while (!complete() && watch.ElapsedMilliseconds < 15000)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        Assert.True(complete(), "The Unicode terminal fixture did not finish.");
    }
}

public static class TerminalUnicodeRenderScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        var terminal = new TerminalControl(TerminalSkin.Default);
        using var source = new HwndSource(new HwndSourceParameters("KillerShell Unicode render fixture")
            { WindowStyle = unchecked((int)0x80000000), Width = 800, Height = 160 });
        try
        {
            source.RootVisual = terminal;
            terminal.Measure(new Size(800, 160));
            terminal.Arrange(new Rect(0, 0, 800, 160));
            terminal.StartDemo(TerminalUnicodeAutomationScenario.Sample + "\r\n" +
                "0123456789\r\n\u65e5\u672c\u8a9e  e\u0301  \U0001F469\u200d\U0001F4BB\r\n| \u0301|");
            terminal.UpdateLayout();
            terminal.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Console.WriteLine("render setup: width=" + terminal.ActualWidth + ", height=" + terminal.ActualHeight +
                ", cell=" + (terminal.WidthForColumns(1) - 4) + ", rows=" + terminal.Buffer.Rows +
                ", text=" + string.Concat(terminal.Buffer.LineAt(0).Select(cell => cell.Text)).TrimEnd());
            TerminalUnicodeAutomationScenario.Wait(() => Runs(VisualTreeHelper.GetDrawing(terminal)).Any());
            var bitmap = new RenderTargetBitmap(800, 160, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(terminal);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(Path.GetTempPath(), "terminal-width.png"))) png.Save(file);
            var runs = Runs(VisualTreeHelper.GetDrawing(terminal)).ToArray();
            Assert.NotEmpty(runs);
            var ascii = runs.First(run => run.GlyphTypeface.CharacterToGlyphMap.TryGetValue('A', out ushort glyph)
                && run.GlyphIndices[0] == glyph && run.BaselineOrigin.X == 0);
            double cell = ascii.AdvanceWidths[0];
            var cjk = runs.First(run => run.GlyphTypeface.CharacterToGlyphMap.TryGetValue(0x65E5, out ushort glyph)
                && run.GlyphIndices[0] == glyph && Math.Abs(run.BaselineOrigin.X - cell) < 0.01);
            Assert.Equal(2 * cell, cjk.AdvanceWidths[0], 5);
            Assert.Contains(runs, run => run.Characters != null && run.Characters.Contains('\u0301'));
            Assert.Contains(runs, run => run.Characters != null && run.Characters.Count >= 2
                && run.Characters[0] == ' ' && run.Characters.Contains('\u0301'));
        }
        finally { terminal.Close(); source.RootVisual = null; app.Shutdown(); }
    }

    private static IEnumerable<GlyphRun> Runs(Drawing drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun;
        if (drawing is DrawingGroup group)
            foreach (Drawing child in group.Children)
                foreach (var run in Runs(child)) yield return run;
    }
}
