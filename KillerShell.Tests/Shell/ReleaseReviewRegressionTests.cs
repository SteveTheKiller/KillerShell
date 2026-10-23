using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using KillerShell.Editing;
using KillerShell.Models;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Terminal;

// Regressions for the 1.5.0 release review of ef3d12f..6d8cbfc.
[Collection(NativeAccessibilityCollection.Name)]
public sealed class ReleaseReviewRegressionTests
{
    [Fact]
    public Task AltGrZeroTypesInATextBoxInsteadOfClosingDetails()
        => RuntimeComponentProcess.Run(nameof(AltGrZeroScenario));

    [Fact]
    public Task AltShiftDownInTheEditorKeepsTheTabsInPlace()
        => RuntimeComponentProcess.Run(nameof(EditorColumnSelectionScenario));

    [Fact]
    public Task EscapeClosesPatternHelpBeforeTheSearchFlyout()
        => RuntimeComponentProcess.Run(nameof(SearchFlyoutEscapeScenario));

    [Fact]
    public Task MouseBackFromSearchResultsReturnsToTheFolder()
        => RuntimeComponentProcess.Run(nameof(MouseBackFromResultsScenario));

    [Fact]
    public Task SavingAnUntitledDocumentAsTextDropsGuessedColors()
        => RuntimeComponentProcess.Run(nameof(AdoptedPlainTextScenario));

    [Theory]
    [InlineData(0u, true)]    // hardware
    [InlineData(1u, true)]    // remote
    [InlineData(2u, false)]   // software: Microsoft Basic Render Driver
    [InlineData(3u, false)]
    public void SoftwareGpuAdaptersAreNotListed(uint flags, bool listed)
        => Assert.Equal(listed, PerformanceGpuAdapterService.IsHardwareAdapter(flags));
}

internal static class ReviewKeys
{
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);

    // Raises PreviewKeyDown with exactly these virtual keys held, so AltGr (left Ctrl + right
    // Alt) can be told apart from left Ctrl + left Alt.
    internal static KeyEventArgs Raise(UIElement target, Key key, params int[] heldVirtualKeys)
    {
        var original = new byte[256];
        Assert.True(GetKeyboardState(original));
        try
        {
            var state = new byte[256];
            foreach (int vk in heldVirtualKeys) state[vk] = 0x80;
            var expected = ModifierKeys.None;
            if (state[Control] != 0) expected |= ModifierKeys.Control;
            if (state[Menu] != 0) expected |= ModifierKeys.Alt;
            if (state[Shift] != 0) expected |= ModifierKeys.Shift;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                Assert.True(SetKeyboardState(state));
                if (Keyboard.Modifiers == expected) break;
            }
            Assert.Equal(expected, Keyboard.Modifiers);
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            target.RaiseEvent(input);
            return input;
        }
        finally { Assert.True(SetKeyboardState(original)); }
    }

    internal const int Control = 0x11, LeftControl = 0xA2, Menu = 0x12, LeftMenu = 0xA4, RightMenu = 0xA5,
        Shift = 0x10, LeftShift = 0xA0;
}

public static class AltGrZeroScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = (FilePane)window.FindName("LeftPane");
        if (!pane.DetailsPaneOpen) window.DetailsPaneToggle_Click(pane);
        Assert.True(pane.DetailsPaneOpen);
        pane.ResultsList.Focus();
        PaneKeyboardScenario.Raise(pane.ResultsList, Key.L, ModifierKeys.Control);
        ShellWindowFixture.PumpUntil(() => pane.AddressBox.IsKeyboardFocusWithin);

        ReviewKeys.Raise(pane.AddressBox, Key.D0,
            ReviewKeys.Control, ReviewKeys.LeftControl, ReviewKeys.Menu, ReviewKeys.RightMenu);
        Assert.True(pane.DetailsPaneOpen, "AltGr+0 types a character in the address box.");

        // Ctrl+Alt+0 with the left keys still turns details off.
        pane.ResultsList.Focus();
        ShellWindowFixture.PumpUntil(() => pane.ResultsList.IsKeyboardFocusWithin);
        var chord = ReviewKeys.Raise(pane.ResultsList, Key.D0,
            ReviewKeys.Control, ReviewKeys.LeftControl, ReviewKeys.Menu, ReviewKeys.LeftMenu);
        Assert.True(chord.Handled);
        Assert.False(pane.DetailsPaneOpen);
    }
}

public static class EditorColumnSelectionScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = (FilePane)window.FindName("LeftPane");
        var right = (FilePane)window.FindName("RightPane");
        var editor = new SearchTab("Editor") { Editor = new EditorControl("") };
        // The same route as PaneKeyboardScenario: focus moves into the editor on pane focus.
        window.ToggleDualPane();
        ShellWindowFixture.PumpUntil(() => right.Active.IsBrowsing);
        window.FocusPane(pane);
        right.Tabs.Add(editor);
        right.Active = editor;
        window.FocusPane(right);
        var text = editor.Editor!.TextArea;
        ShellWindowFixture.PumpUntil(() => text.IsKeyboardFocusWithin,
            diagnostic: () => "focus=" + Keyboard.FocusedElement + "; visible=" + text.IsVisible + "; active=" + window.IsActive);
        Assert.False(window.TabsAtBottom);

        ReviewKeys.Raise(text, Key.Down, ReviewKeys.Menu, ReviewKeys.LeftMenu, ReviewKeys.Shift, ReviewKeys.LeftShift);
        Assert.False(window.TabsAtBottom, "Alt+Shift+Down in the editor is column selection.");
    }
}

public static class SearchFlyoutEscapeScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var popup = (Popup)window.FindName("TitleSearchPopup");
        var help = (FrameworkElement)window.FindName("PatternHelpOverlay");
        var surface = (FrameworkElement)window.FindName("TitleSearchSurface");
        ShellWindowFixture.Click(window, "SearchPanelBtn");
        ShellWindowFixture.PumpUntil(() => popup.IsOpen);
        ShellWindowFixture.Click(window, "PatternHelpButton");
        ShellWindowFixture.PumpUntil(() => help.Visibility == Visibility.Visible);

        // Esc pressed while typing in the include box, which sits inside the flyout.
        var include = (TextBox)window.FindName("IncludePatternsBox");
        Assert.True(surface.IsAncestorOf(include));
        PaneKeyboardScenario.Raise(include, Key.Escape, ModifierKeys.None);
        ShellWindowFixture.PumpUntil(() => help.Visibility != Visibility.Visible);
        Assert.True(popup.IsOpen, "The first Esc closes only the pattern help.");

        PaneKeyboardScenario.Raise(include, Key.Escape, ModifierKeys.None);
        ShellWindowFixture.PumpUntil(() => !popup.IsOpen);
    }
}

public static class MouseBackFromResultsScenario
{
    private const int WM_XBUTTONUP = 0x020C;

    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = window.Pane;
        string a = Directory.CreateDirectory(Path.Combine(fixture.Root, "a")).FullName;
        string b = Directory.CreateDirectory(Path.Combine(fixture.Root, "b")).FullName;
        foreach (string folder in new[] { a, b })
        {
            var nav = (Task)typeof(KillerShell.Shell.MainWindow)
                .GetMethod("NavigateTo", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [folder, true, false, false])!;
            ShellWindowFixture.PumpUntil(() => nav.IsCompleted);
        }
        Assert.Equal(b, pane.Active.CurrentFolder);

        // A search on this folder tab shows results but keeps its folder history.
        pane.Active.IsBrowsing = false;
        object[] args = [IntPtr.Zero, WM_XBUTTONUP, new IntPtr(1 << 16), IntPtr.Zero, false];
        typeof(KillerShell.Shell.MainWindow)
            .GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        Assert.True((bool)args[4]);
        ShellWindowFixture.PumpUntil(() => pane.Active.CurrentFolder == a);
    }
}

public static class AdoptedPlainTextScenario
{
    public static void Run()
    {
        var editor = new EditorControl("") { Text = "<?xml version=\"1.0\"?>\n<settings>\n  <item name=\"a\"/>\n</settings>\n" };
        editor.SniffLanguage();
        Assert.NotNull(editor.SyntaxHighlighting);

        editor.AdoptPath(Path.Combine(Path.GetTempPath(), "notes.txt"), new UTF8Encoding(false));
        Assert.Null(editor.SyntaxHighlighting);
    }
}
