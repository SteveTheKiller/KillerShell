using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using KillerShell.Editing;
using KillerShell.Models;
using KillerShell.Shell;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class TitleInputRegressionTests
{
    [Fact]
    public Task PathEditorsRetainTheirEnterAndEscapeKeys()
        => RuntimeComponentProcess.Run(nameof(TitlePathInputScenario));

    [Fact]
    public Task SearchPickersRetainEnterForTheirOwnSelection()
        => RuntimeComponentProcess.Run(nameof(TitleSearchPickerScenario));

    [Fact]
    public Task RestoredDensityReachesExistingTerminalAndEditorContent()
        => RuntimeComponentProcess.Run(nameof(RestoredContentDensityScenario));
}

public static class TitlePathInputScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        foreach (var box in new[] { window.Pane.AddressBox, window.Pane.TermCwdBox, window.Pane.EditorPathBox })
            foreach (var key in new[] { Key.Enter, Key.Escape })
            {
                var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = box };
                Invoke(window, "Window_PreviewKeyDown", window, input);
                Assert.False(input.Handled, box.Name + " must receive " + key + " before window commands.");
                Assert.True(window.IsEnabled);
            }
    }

    internal static object? Invoke(object target, string method, params object[] args)
        => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}

public static class TitleSearchPickerScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
        TitlePathInputScenario.Invoke(window, "OpenSortMenuFromKeyboard");
        ShellWindowFixture.PumpUntil(() => window.Pane.SortChoice.IsDropDownOpen);
        foreach (var picker in new FrameworkElement[] { new ComboBoxItem(), new DatePickerTextBox(), window.Pane.SortChoice })
        {
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = picker };
            TitlePathInputScenario.Invoke(window, "TitleSearch_KeyDown", picker, input);
            Assert.False(input.Handled, picker.GetType().Name + " must accept its value without starting a search.");
        }
        window.Pane.SortChoice.IsDropDownOpen = false;
        window.Pane.OverflowPopup.IsOpen = false;
    }
}

public static class RestoredContentDensityScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var terminal = new TerminalControl(TerminalSkin.Default);
        var editor = new EditorControl("");
        window.Pane.Tabs.Add(new SearchTab("Terminal") { Term = terminal });
        window.Pane.Tabs.Add(new SearchTab("Editor") { Editor = editor });
        TitlePathInputScenario.Invoke(window, "SetWindowViewSetting", "ResultsDensityL", "4");
        TitlePathInputScenario.Invoke(window, "InitResultsView");
        Assert.Equal(4, window.Pane.ViewState.Density);
        Assert.Equal(4, editor.DensityLevel);
        Assert.Equal(4, typeof(TerminalControl).GetField("_densityLevel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal));
    }
}
