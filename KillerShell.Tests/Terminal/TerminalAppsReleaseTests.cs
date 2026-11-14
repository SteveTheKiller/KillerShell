using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class TerminalAppsReleaseTests
{
    [Fact]
    public Task NativeAppsReleaseLeavesTheTerminalCardOpenForTabAndEscape()
        => RuntimeComponentProcess.Run(nameof(TerminalAppsReleaseScenario));
}

public static class TerminalAppsReleaseScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        window.Opacity = 1;
        window.WindowState = WindowState.Normal;
        window.Left = SystemParameters.WorkArea.Left + 20;
        window.Top = SystemParameters.WorkArea.Top + 20;
        window.Width = Math.Min(1000, SystemParameters.WorkArea.Width - 40);
        window.Height = Math.Min(700, SystemParameters.WorkArea.Height - 40);
        window.Activate();
        window.OpenShell(TerminalProfile.Cmd(), fixture.Root);
        ShellWindowFixture.PumpUntil(() => SearchFlyoutLayoutProbe.Descendants<TerminalControl>(window).Any());
        var terminal = Assert.Single(SearchFlyoutLayoutProbe.Descendants<TerminalControl>(window));
        terminal.Focus();
        Keyboard.Focus(terminal);
        ShellWindowFixture.PumpUntil(() => window.IsActive && terminal.IsKeyboardFocusWithin);
        Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
        Stroke(window, 0x5D, Key.Apps);
        ShellWindowFixture.PumpUntil(() => ConnectedVisible(terminal.ContextMenu),
            diagnostic: () => "A completed native Apps press and release must leave the actual terminal card visible.");
        var menu = terminal.ContextMenu;
        Assert.Same(terminal, menu.PlacementTarget);
        var beforeTab = Keyboard.FocusedElement;
        Stroke(window, 0x09, Key.Tab);
        ShellWindowFixture.PumpUntil(() => menu.IsKeyboardFocusWithin && !ReferenceEquals(beforeTab, Keyboard.FocusedElement),
            diagnostic: () => "Native Tab must move keyboard focus within the open terminal card.");
        Assert.True(ConnectedVisible(menu));
        Stroke(window, 0x1B, Key.Escape);
        ShellWindowFixture.PumpUntil(() => !menu.IsOpen && terminal.IsKeyboardFocusWithin,
            diagnostic: () => "Native Escape must close the card and return focus to its terminal.");
    }

    private static bool ConnectedVisible(ContextMenu? menu)
        => menu != null && menu.IsOpen && menu.IsVisible && menu.ActualWidth > 0 && menu.ActualHeight > 0
            && PresentationSource.FromVisual(menu) is HwndSource source && Owned(source.Handle);

    internal static void Stroke(Window window, byte virtualKey, Key expected)
    {
        bool released = false, held = false;
        ProcessInputEventHandler receipt = (_, args) =>
        {
            if (args.StagingItem.Input is KeyEventArgs key
                && (key.Key == expected || key.SystemKey == expected)
                && (key.RoutedEvent == Keyboard.PreviewKeyUpEvent || key.RoutedEvent == Keyboard.KeyUpEvent))
                window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => released = true));
        };
        InputManager.Current.PostProcessInput += receipt;
        try
        {
            TakeForeground(window);
            Assert.True(Owned(GetForegroundWindow()), "Native input requires the test's own foreground process.");
            uint extended = virtualKey == 0x5D ? 1u : 0u;
            keybd_event(virtualKey, 0, extended, UIntPtr.Zero);
            held = true;
            keybd_event(virtualKey, 0, extended | 2u, UIntPtr.Zero);
            held = false;
            ShellWindowFixture.PumpUntil(() => released,
                diagnostic: () => "The real native key release must finish processing before checking the card.");
        }
        finally
        {
            InputManager.Current.PostProcessInput -= receipt;
            if (held)
            {
                window.Activate();
                ShellWindowFixture.PumpUntil(() => Owned(GetForegroundWindow()));
                keybd_event(virtualKey, 0, (virtualKey == 0x5D ? 1u : 0u) | 2u, UIntPtr.Zero);
            }
        }
    }

    private static bool Owned(IntPtr handle)
    {
        GetWindowThreadProcessId(handle, out uint pid);
        return handle != IntPtr.Zero && pid == (uint)Process.GetCurrentProcess().Id;
    }

    /// <summary>
    /// WPF's Activate can report the window active while Windows keeps another program in front
    /// (its foreground lock), and native keys then go to that program. Windows lets the process
    /// that sent the last input take the foreground, so a zero-distance mouse move comes first.
    /// </summary>
    private static void TakeForeground(Window window)
    {
        if (Owned(GetForegroundWindow())) return;
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        mouse_event(0x0001, 0, 0, 0, UIntPtr.Zero);
        SetForegroundWindow(handle);
        window.Activate();
        ShellWindowFixture.PumpUntil(() => Owned(GetForegroundWindow()),
            diagnostic: () => "The test window could not take the foreground.");
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
