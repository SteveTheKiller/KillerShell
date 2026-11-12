using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class PaneLayoutSwitchTests
{
    [Fact]
    public Task TheFooterSwitchPicksEveryLayoutAndFollowsTheKeyboard()
        => RuntimeComponentProcess.Run(nameof(PaneLayoutSwitchScenario));
}

public static class PaneLayoutSwitchScenario
{
    private static readonly string[] Buttons = ["PaneOneBtn", "PaneLeftRightBtn", "PaneTopBottomBtn"];

    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var right = (FilePane)window.FindName("RightPane");

        // The rail no longer carries a layout button; the footer is its one home.
        Assert.Null(window.FindName("DualPaneRailBtn"));
        Assert.Null(window.FindName("PaneOrientationButton"));
        ExpectLayout(window, 0);

        ShellWindowFixture.SelectLayout(window, Buttons[1]);
        ExpectLayout(window, 1);
        ShellWindowFixture.PumpUntil(() => right.Active.IsBrowsing);
        var keptTab = right.Active;

        // Every layout from every other one, and the second pane keeps its tab throughout.
        foreach (int layout in new[] { 2, 0, 2, 1, 0, 1, 2 })
        {
            ShellWindowFixture.SelectLayout(window, Buttons[layout]);
            ExpectLayout(window, layout);
            Assert.Same(keptTab, right.Active);
        }

        // Picking the lit one again changes nothing.
        ShellWindowFixture.SelectLayout(window, Buttons[2]);
        ExpectLayout(window, 2);

        // Consecutive picks settle the old slide before changing its axis.
        ShellWindowFixture.SelectLayout(window, Buttons[0]);
        ShellWindowFixture.SelectLayout(window, Buttons[1]);
        ShellWindowFixture.SelectLayout(window, Buttons[2]);
        ExpectLayout(window, 2);

        // The keyboard moves the lit button too.
        PaneKeyboardScenario.Raise(window, Key.Left, ModifierKeys.Control | ModifierKeys.Alt);
        ExpectLayout(window, 1);
        PaneKeyboardScenario.Raise(window, Key.Up, ModifierKeys.Control | ModifierKeys.Alt);
        ExpectLayout(window, 2);
        PaneKeyboardScenario.Raise(window, Key.F10, ModifierKeys.None);
        ExpectLayout(window, 0);
        PaneKeyboardScenario.Raise(window, Key.F10, ModifierKeys.None);
        ExpectLayout(window, 2);
        Assert.Same(keptTab, right.Active);
    }

    private static int _step;

    private static void ExpectLayout(MainWindow window, int layout)
    {
        _step++;
        var right = (FilePane)window.FindName("RightPane");
        ShellWindowFixture.PumpUntil(() => window.DualPane == (layout != 0)
            && (layout == 0 ? !right.IsVisible
                : right.IsVisible && Grid.GetColumn(right) == (layout == 1 ? 2 : 0)
                    && Grid.GetRow(right) == (layout == 2 ? 2 : 0)
                    && double.IsNaN(right.Width) && double.IsNaN(right.Height)),
            diagnostic: () => "step=" + _step + "; want=" + layout + "; vis=" + right.Visibility + "; dual=" + window.DualPane + "; visible=" + right.IsVisible
                + "; col=" + Grid.GetColumn(right) + "; row=" + Grid.GetRow(right)
                + "; w=" + right.Width + "; h=" + right.Height
                + "; checked=" + string.Join(",", Buttons.Select(n => ((RadioButton)window.FindName(n)).IsChecked)));
        if (layout == 0) Assert.Same(window.FindName("LeftPane"), window.Pane);

        // Exactly one button is lit, the one for the layout on screen, and each is drawn and named.
        string[] labels = ["Str_Layout_OnePane", "Str_Layout_LeftRight", "Str_Layout_TopBottom"];
        for (int i = 0; i < Buttons.Length; i++)
        {
            var radio = (RadioButton)window.FindName(Buttons[i]);
            Assert.Equal(i == layout, radio.IsChecked == true);
            Assert.True(radio.ActualWidth > 0 && radio.ActualHeight > 0);
            var icon = (System.Windows.Shapes.Path)radio.Template.FindName("Icon", radio);
            Assert.NotNull(icon.Data);
            Assert.True(icon.ActualWidth > 0 && icon.ActualHeight > 0);
            Assert.Equal(window.FindResource(labels[i]), System.Windows.Automation.AutomationProperties.GetName(radio));
        }
    }
}
