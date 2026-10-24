using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Input;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class MouseHistoryStepTests
{
    [Fact]
    public Task OneMouseBackPressGoesBackOneFolder()
        => RuntimeComponentProcess.Run(nameof(MouseHistoryStepScenario));
}

public static class MouseHistoryStepScenario
{
    private const int WM_XBUTTONUP = 0x020C;

    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = window.Pane;
        string a = Directory.CreateDirectory(Path.Combine(fixture.Root, "a")).FullName;
        string b = Directory.CreateDirectory(Path.Combine(fixture.Root, "b")).FullName;
        string c = Directory.CreateDirectory(Path.Combine(fixture.Root, "c")).FullName;
        foreach (string folder in new[] { a, b, c })
        {
            var nav = (Task)typeof(KillerShell.Shell.MainWindow)
                .GetMethod("NavigateTo", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [folder, true, false, false])!;
            ShellWindowFixture.PumpUntil(() => nav.IsCompleted);
        }
        Assert.Equal(c, pane.Active.CurrentFolder);

        // A real press arrives as the button going down over the pane, then WM_XBUTTONUP on the
        // window. Both used to navigate, so one press went back two folders.
        pane.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.XButton1)
            { RoutedEvent = Mouse.PreviewMouseDownEvent });
        object[] args = [IntPtr.Zero, WM_XBUTTONUP, new IntPtr(1 << 16), IntPtr.Zero, false];
        typeof(KillerShell.Shell.MainWindow)
            .GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        Assert.True((bool)args[4]);
        ShellWindowFixture.PumpUntil(() => pane.Active.CurrentFolder != c);
        // Let a second, duplicate navigation land if one was started.
        var settle = System.Diagnostics.Stopwatch.StartNew();
        ShellWindowFixture.PumpUntil(() => settle.ElapsedMilliseconds > 400);
        Assert.Equal(b, pane.Active.CurrentFolder);
    }
}
