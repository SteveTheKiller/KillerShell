using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class PaneLayoutSizeTests
{
    [Fact]
    public Task RepeatedLayoutChangesPreserveTheWindowSize()
        => RuntimeComponentProcess.Run(nameof(PaneLayoutSizeScenario), timeoutMilliseconds: 180000);
}

public static class PaneLayoutSizeScenario
{
    private static readonly string[] Buttons = ["PaneOneBtn", "PaneLeftRightBtn", "PaneTopBottomBtn"];

    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
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

        // Every footer order, forward (one, side by side, stacked) and backward, four times each.
        foreach (int[] order in new[] { new[] { 1, 2, 0 }, new[] { 2, 1, 0 } })
        for (int cycle = 0; cycle < 4; cycle++)
        foreach (int expected in order)
        {
            ShellWindowFixture.SelectLayout(window, Buttons[expected]);
            ShellWindowFixture.PumpUntil(() => expected == 0
                ? !window.DualPane && !right.IsVisible
                : window.DualPane && right.IsVisible
                    && Grid.GetColumn(right) == (expected == 1 ? 2 : 0)
                    && Grid.GetRow(right) == (expected == 2 ? 2 : 0)
                    && double.IsNaN(right.Width) && double.IsNaN(right.Height));
            sizes.Add($"Order={string.Join("", order)} {cycle}:{expected} Width={window.Width:F1}, Actual={window.ActualWidth:F1}, Min={window.MinWidth:F1}");
            largestChange = Math.Max(largestChange, Math.Abs(window.ActualWidth - width));
            Assert.Equal(left, window.Left);
            Assert.Equal(top, window.Top);
        }
        Assert.True(largestChange < 1,
            "Layout changes changed the window width. " + string.Join("; ", sizes));
        Assert.Equal(minimum, window.MinWidth);
        Assert.Equal(height, window.ActualHeight);

        // A later user resize becomes the new size to preserve, including interrupted slides.
        window.Width = width + 100;
        ShellWindowFixture.PumpUntil(() => Math.Abs(window.ActualWidth - width - 100) < 1);
        foreach (int[] order in new[] { new[] { 1, 2, 0 }, new[] { 2, 1, 0 } })
        for (int cycle = 0; cycle < 4; cycle++)
        {
            foreach (int layout in order) ShellWindowFixture.SelectLayout(window, Buttons[layout]);
            ShellWindowFixture.PumpUntil(() => !window.DualPane && !right.IsVisible);
        }
        Assert.InRange(window.ActualWidth, width + 99, width + 101);
        Assert.Equal(minimum, window.MinWidth);
        Assert.Equal(height, window.ActualHeight);
        Assert.Equal(left, window.Left);
        Assert.Equal(top, window.Top);
    }
}
