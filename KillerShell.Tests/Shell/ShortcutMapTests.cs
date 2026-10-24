using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Trait("Category", "Window")]
public sealed class ShortcutMapTests
{
    [Fact]
    public Task ModifierLayersShowPaneAndDetailsShortcuts()
        => RuntimeComponentProcess.Run(nameof(ShortcutMapScenario));
}

public static class ShortcutMapScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        PaneKeyboardScenario.Raise(window, Key.F1, ModifierKeys.None);
        ShellWindowFixture.Click(window, "KsViewKeyboardBtn");
        var host = (StackPanel)window.FindName("ShortcutKeyboardHost");
        var buttons = Descendants(host).OfType<Button>().ToArray();
        var ctrlAlt = buttons.SingleOrDefault(button => Equals(button.Content, "CTRL+ALT"));
        Assert.NotNull(ctrlAlt);
        ctrlAlt!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        VerifyLabels(host, "Str_Layout_LeftRight", "Str_Layout_TopBottom", "Str_Layout_DetailsBottom",
            "Str_Layout_DetailsRight", "Str_Layout_DetailsOff");

        buttons.Single(button => Equals(button.Content, "CTRL")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        VerifyLabels(host, "Str_Ks_SwitchPane");
        PaneKeyboardScenario.Raise(window, Key.LeftAlt, ModifierKeys.Control | ModifierKeys.Alt);
        VerifyLabels(host, "Str_Layout_LeftRight", "Str_Layout_DetailsRight");
        PaneKeyboardScenario.Raise(window, Key.LeftShift, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);
        VerifyLabels(host, "Str_Ks_ExportCsv");
    }

    private static void VerifyLabels(DependencyObject host, params string[] keys)
    {
        var labels = Descendants(host).OfType<TextBlock>().Select(text => text.Text).ToArray();
        foreach (string key in keys) Assert.Contains(MainWindow.LocStatic(key), labels);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
