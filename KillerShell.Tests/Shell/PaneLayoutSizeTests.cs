using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class PaneLayoutSizeTests
{
    [Fact]
    public Task RepeatedLayoutCyclesPreserveTheWindowSize()
        => RuntimeComponentProcess.Run(nameof(PaneLayoutSizeScenario), timeoutMilliseconds: 180000);
}

public static class PaneLayoutSizeScenario
{
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);

    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var button = (Button)window.FindName("DualPaneRailBtn");
        var right = (FilePane)window.FindName("RightPane");
        window.Left = SystemParameters.WorkArea.Left + 32;
        window.Width = 900;
        ShellWindowFixture.PumpUntil(() => Math.Abs(window.ActualWidth - 900) < 1);
        double width = window.ActualWidth;
        double height = window.ActualHeight;
        double left = window.Left;
        double top = window.Top;
        double minimum = window.MinWidth;
        var sizes = new List<string>();
        double largestChange = 0;
        foreach (bool reverse in new[] { false, true })
        for (int cycle = 0; cycle < 4; cycle++)
        {
            for (int layout = 1; layout <= 3; layout++)
            {
                Click(button, reverse);
                int expected = reverse ? (3 - layout) % 3 : layout % 3;
                ShellWindowFixture.PumpUntil(() => expected == 0
                    ? !window.DualPane && !right.IsVisible
                    : window.DualPane && right.IsVisible
                        && Grid.GetColumn(right) == (expected == 1 ? 2 : 0)
                        && Grid.GetRow(right) == (expected == 2 ? 2 : 0)
                        && double.IsNaN(right.Width) && double.IsNaN(right.Height));
                sizes.Add($"Reverse={reverse} {cycle}:{expected} Width={window.Width:F1}, Actual={window.ActualWidth:F1}, Min={window.MinWidth:F1}");
                largestChange = Math.Max(largestChange, Math.Abs(window.ActualWidth - width));
                Assert.Equal(left, window.Left);
                Assert.Equal(top, window.Top);
            }
        }
        Assert.True(largestChange < 1,
            "Layout cycles changed the window width. " + string.Join("; ", sizes));
        Assert.Equal(minimum, window.MinWidth);
        Assert.Equal(height, window.ActualHeight);

        // A later user resize becomes the new size to preserve, including interrupted slides.
        window.Width = width + 100;
        ShellWindowFixture.PumpUntil(() => Math.Abs(window.ActualWidth - width - 100) < 1);
        foreach (bool reverse in new[] { false, true })
        for (int cycle = 0; cycle < 4; cycle++)
        {
            Click(button, reverse);
            Click(button, reverse);
            Click(button, reverse);
            ShellWindowFixture.PumpUntil(() => !window.DualPane && !right.IsVisible);
        }
        Assert.InRange(window.ActualWidth, width + 99, width + 101);
        Assert.Equal(minimum, window.MinWidth);
        Assert.Equal(height, window.ActualHeight);
        Assert.Equal(left, window.Left);
        Assert.Equal(top, window.Top);
    }

    private static void Click(Button button, bool reverse)
    {
        var original = new byte[256];
        Assert.True(GetKeyboardState(original));
        try
        {
            var state = new byte[256];
            if (reverse) state[0x11] = state[0xA2] = 0x80;
            var expected = reverse ? ModifierKeys.Control : ModifierKeys.None;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                Assert.True(SetKeyboardState(state));
                if (Keyboard.Modifiers == expected) break;
            }
            Assert.Equal(expected, Keyboard.Modifiers);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally { Assert.True(SetKeyboardState(original)); }
    }
}
