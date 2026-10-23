using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class PaneLayoutRailTests
{
    [Fact]
    public Task PaneLayoutCyclesBothDirectionsAndMenuChoosesEveryLayout()
        => RuntimeComponentProcess.Run(nameof(PaneLayoutRailScenario));
}

public static class PaneLayoutRailScenario
{
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);

    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var button = (Button)window.FindName("DualPaneRailBtn");
        var right = (FilePane)window.FindName("RightPane");
        Click(button);
        ExpectLayout(window, 1);
        ShellWindowFixture.PumpUntil(() => right.Active.IsBrowsing);
        var keptTab = right.Active;
        Click(button);
        ExpectLayout(window, 2);
        Click(button);
        ExpectLayout(window, 0);

        Click(button, reverse: true);
        ExpectLayout(window, 2);
        Click(button, reverse: true);
        ExpectLayout(window, 1);
        Click(button, reverse: true);
        ExpectLayout(window, 0);

        // Consecutive clicks must settle the old slide before changing its axis.
        Click(button);
        Click(button);
        Click(button);
        ExpectLayout(window, 0);

        var menu = button.ContextMenu;
        foreach (int layout in new[] { 1, 2, 0 })
        {
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.Equal(3, menu.Items.Count);
            Assert.Equal(window.FindResource("Str_Layout_OnePane"), ((MenuItem)menu.Items[0]).Header);
            Assert.Equal(window.FindResource("Str_Layout_LeftRight"), ((MenuItem)menu.Items[1]).Header);
            Assert.Equal(window.FindResource("Str_Layout_TopBottom"), ((MenuItem)menu.Items[2]).Header);
            ((MenuItem)menu.Items[layout]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            ExpectLayout(window, layout);
            Assert.Same(keptTab, right.Active);
        }

        PaneKeyboardScenario.Raise(window, Key.Left, ModifierKeys.Control | ModifierKeys.Alt);
        ExpectLayout(window, 1);
        PaneKeyboardScenario.Raise(window, Key.Up, ModifierKeys.Control | ModifierKeys.Alt);
        ExpectLayout(window, 2);
        PaneKeyboardScenario.Raise(window, Key.F10, ModifierKeys.None);
        ExpectLayout(window, 0);
        PaneKeyboardScenario.Raise(window, Key.F10, ModifierKeys.None);
        ExpectLayout(window, 2);
        Assert.Same(keptTab, right.Active);
        Assert.Null(window.FindName("PaneOrientationButton"));
    }

    private static void Click(Button button, bool reverse = false)
    {
        var original = new byte[256];
        Assert.True(GetKeyboardState(original));
        try
        {
            var state = new byte[256];
            if (reverse) state[0x11] = state[0xA2] = 0x80;
            var expected = reverse ? ModifierKeys.Control : ModifierKeys.None;
            ModifierKeys observed = ModifierKeys.None;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                Assert.True(SetKeyboardState(state));
                observed = Keyboard.Modifiers;
                if (observed == expected) break;
            }
            Assert.Equal(expected, observed);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally { Assert.True(SetKeyboardState(original)); }
    }

    private static void ExpectLayout(MainWindow window, int layout)
    {
        var right = (FilePane)window.FindName("RightPane");
        ShellWindowFixture.PumpUntil(() => window.DualPane == (layout != 0)
            && (layout == 0 ? !right.IsVisible
                : right.IsVisible && Grid.GetColumn(right) == (layout == 1 ? 2 : 0)
                    && Grid.GetRow(right) == (layout == 2 ? 2 : 0)
                    && double.IsNaN(right.Width) && double.IsNaN(right.Height)));
        if (layout == 0) Assert.Same(window.FindName("LeftPane"), window.Pane);
        var button = (Button)window.FindName("DualPaneRailBtn");
        var glyph = (System.Windows.Shapes.Path)button.Template.FindName("LayoutGlyph", button);
        Assert.NotNull(glyph.Data);
        Assert.True(glyph.ActualWidth > 0 && glyph.ActualHeight > 0);
        string label = layout == 0 ? "Str_Layout_OnePane" : layout == 1 ? "Str_Layout_LeftRight" : "Str_Layout_TopBottom";
        Assert.Equal(window.FindResource(label), System.Windows.Automation.AutomationProperties.GetName(button));
    }
}
