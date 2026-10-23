using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KillerFamily.Layout;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class ShortcutVisibilityTests
{
    [Fact]
    public Task AboutPreferenceRemovesMenuGestureSpaceAndRestoresTheSavedChoice()
        => RuntimeComponentProcess.Run(nameof(ShortcutVisibilityScenario));
}

public static class ShortcutVisibilityScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var saved = new Dictionary<string, string>();
        string? Read(string key) => saved.TryGetValue(key, out string value) ? value : null;
        ShortcutHints.Initialize(Read, (key, value) => saved[key] = value);
        var option = (CheckBox)fixture.Window.FindName("AboutShowShortcuts");
        ShellWindowFixture.PumpUntil(() => option.IsChecked == true);
        var item = new MenuItem { Header = "Open", InputGestureText = "Enter",
            Style = (Style)fixture.Window.FindResource(typeof(MenuItem)) };
        item.ApplyTemplate();
        var gesture = (TextBlock)item.Template.FindName("Gesture", item);
        Assert.Equal(Visibility.Visible, gesture.Visibility);

        option.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
        ShellWindowFixture.PumpUntil(() => gesture.Visibility == Visibility.Collapsed);
        Assert.Equal("0", saved[ShortcutHints.SettingName]);
        item.Measure(new Size(1000, 1000));
        double hiddenWidth = item.DesiredSize.Width;
        option.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
        ShellWindowFixture.PumpUntil(() => gesture.Visibility == Visibility.Visible);
        item.Measure(new Size(1000, 1000));
        Assert.True(item.DesiredSize.Width > hiddenWidth + 24, "Hiding shortcuts removes the text and its column gap.");
        item.InputGestureText = "";
        Assert.Equal(Visibility.Collapsed, gesture.Visibility);

        saved[ShortcutHints.SettingName] = "0";
        ShortcutHints.Initialize(Read, (key, value) => saved[key] = value);
        ShellWindowFixture.PumpUntil(() => option.IsChecked == false);
        Assert.False(ShortcutHints.Current.Enabled);
    }
}
