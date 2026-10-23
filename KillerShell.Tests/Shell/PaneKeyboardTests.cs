using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShell.Editing;
using KillerShell.Models;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class PaneKeyboardTests
{
    [Fact]
    public Task ControlF6SwitchesVisiblePanesAndPreservesSelections()
        => RuntimeComponentProcess.Run(nameof(PaneKeyboardScenario));
}

public static class PaneKeyboardScenario
{
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);

    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var left = (FilePane)window.FindName("LeftPane");
        var right = (FilePane)window.FindName("RightPane");
        window.ToggleDualPane();
        ShellWindowFixture.PumpUntil(() => right.Active.IsBrowsing);
        foreach (var pane in new[] { left, right })
        {
            pane.Active.IsBrowsing = false;
            pane.Active.Results.Add(new SearchResult { FileName = "selected.txt", FilePath = pane.Name + "-selected.txt" });
            pane.ResultsList.SelectedItem = pane.Active.Results[0];
        }
        var leftSelection = left.ResultsList.SelectedItem;
        var rightSelection = right.ResultsList.SelectedItem;
        right.ResultsList.Focus();
        Assert.Same(right, window.Pane);
        Assert.True(Raise(right.ResultsList, Key.F6, ModifierKeys.Control).Handled);
        ShellWindowFixture.PumpUntil(() => ReferenceEquals(left, window.Pane) && left.ResultsList.IsKeyboardFocusWithin);
        Assert.True(Raise(left.ResultsList, Key.F6, ModifierKeys.Control).Handled);
        ShellWindowFixture.PumpUntil(() => ReferenceEquals(right, window.Pane) && right.ResultsList.IsKeyboardFocusWithin);
        Assert.Same(leftSelection, left.ResultsList.SelectedItem);
        Assert.Same(rightSelection, right.ResultsList.SelectedItem);

        string originalFolder = right.Active.CurrentFolder;
        Raise(right.ResultsList, Key.L, ModifierKeys.Control);
        ShellWindowFixture.PumpUntil(() => right.AddressBox.IsKeyboardFocusWithin);
        right.AddressBox.Text = @"C:\unfinished-address";
        Assert.True(Raise(right.AddressBox, Key.F6, ModifierKeys.Control).Handled);
        ShellWindowFixture.PumpUntil(() => ReferenceEquals(left, window.Pane) && left.ResultsList.IsKeyboardFocusWithin);
        Assert.Equal(Visibility.Collapsed, right.AddressBox.Visibility);
        Assert.Equal(Visibility.Visible, right.ScopePathLabel.Visibility);
        Assert.Equal(originalFolder, right.Active.CurrentFolder);

        var terminal = new SearchTab("Terminal") { Term = new TerminalControl(TerminalSkin.Default) };
        var editor = new SearchTab("Editor") { Editor = new EditorControl("") };
        foreach (var tab in new[] { terminal, editor })
        {
            window.FocusPane(left);
            right.Tabs.Add(tab);
            right.Active = tab;
            window.FocusPane(right);
            var content = (UIElement?)tab.Term ?? tab.Editor!.TextArea;
            ShellWindowFixture.PumpUntil(() => content.IsKeyboardFocusWithin,
                diagnostic: () => "Pane keyboard setup: tab=" + tab.Title + "; focus=" + Keyboard.FocusedElement
                    + "; visible=" + content.IsVisible + "; enabled=" + content.IsEnabled
                    + "; pane=" + window.Pane.Name + "; active=" + window.IsActive);
            Assert.True(Raise(content, Key.F6, ModifierKeys.Control).Handled);
            ShellWindowFixture.PumpUntil(() => ReferenceEquals(left, window.Pane) && left.ResultsList.IsKeyboardFocusWithin);
            Assert.True(Raise(left.ResultsList, Key.F6, ModifierKeys.Control).Handled);
            ShellWindowFixture.PumpUntil(() => ReferenceEquals(right, window.Pane) && content.IsKeyboardFocusWithin);
        }

        Raise(window, Key.F1, ModifierKeys.None);
        ShellWindowFixture.PumpUntil(() => ((UIElement)window.FindName("ShortcutsOverlay")).IsKeyboardFocusWithin);
        Assert.True(Raise(window, Key.F6, ModifierKeys.Control).Handled);
        Assert.Same(right, window.Pane);
        Raise(window, Key.Escape, ModifierKeys.None);
        ShellWindowFixture.PumpUntil(() => !((UIElement)window.FindName("ShortcutsOverlay")).IsVisible);

        window.ToggleDualPane();
        ShellWindowFixture.PumpUntil(() => !right.IsVisible);
        left.ResultsList.Focus();
        Assert.True(Raise(left.ResultsList, Key.F6, ModifierKeys.Control).Handled);
        Assert.Same(left, window.Pane);
        Assert.False(right.IsVisible);
    }

    internal static KeyEventArgs Raise(UIElement target, Key key, ModifierKeys modifiers)
    {
        var original = new byte[256];
        Assert.True(GetKeyboardState(original));
        try
        {
            var state = new byte[256];
            if ((modifiers & ModifierKeys.Control) != 0) state[0x11] = state[0xA2] = 0x80;
            if ((modifiers & ModifierKeys.Shift) != 0) state[0x10] = state[0xA0] = 0x80;
            if ((modifiers & ModifierKeys.Alt) != 0) state[0x12] = state[0xA4] = 0x80;
            ModifierKeys observed = ModifierKeys.None;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                Assert.True(SetKeyboardState(state));
                observed = Keyboard.Modifiers;
                if (observed == modifiers) break;
            }
            Assert.Equal(modifiers, observed);
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            target.RaiseEvent(input);
            return input;
        }
        finally { Assert.True(SetKeyboardState(original)); }
    }
}
