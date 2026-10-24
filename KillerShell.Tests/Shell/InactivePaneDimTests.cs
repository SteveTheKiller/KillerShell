using System.Threading.Tasks;
using System.Windows;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class InactivePaneDimTests
{
    [Fact]
    public Task TheUnfocusedPaneDimsOnlyWhileTwoPanesShow()
        => RuntimeComponentProcess.Run(nameof(InactivePaneDimScenario));
}

public static class InactivePaneDimScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var left = (FilePane)window.FindName("LeftPane");
        var right = (FilePane)window.FindName("RightPane");
        Assert.Equal(1, left.Opacity, 3);

        window.ToggleDualPane();
        ShellWindowFixture.PumpUntil(() => right.Active.IsBrowsing);
        window.FocusPane(left);
        ShellWindowFixture.PumpUntil(() => Dimmed(right) && Bright(left),
            diagnostic: () => "left=" + left.Opacity + "; right=" + right.Opacity);

        window.FocusPane(right);
        ShellWindowFixture.PumpUntil(() => Dimmed(left) && Bright(right),
            diagnostic: () => "left=" + left.Opacity + "; right=" + right.Opacity);

        // One pane again: nothing to tell apart, so nothing is dimmed.
        window.ToggleDualPane();
        ShellWindowFixture.PumpUntil(() => Bright(left) && !right.IsVisible,
            diagnostic: () => "left=" + left.Opacity + "; right visible=" + right.IsVisible);
    }

    // Slightly, not grayed out: the unfocused pane stays readable.
    private static bool Dimmed(FilePane pane) => pane.Opacity is >= 0.85 and <= 0.95;

    private static bool Bright(FilePane pane) => pane.Opacity > 0.999;
}
