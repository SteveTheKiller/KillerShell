using System;
using System.Collections.Generic;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

// A plain launch (the taskbar or Start icon, no arguments) must bring back a KillerShell that is
// hidden in the notification area instead of starting a second copy. Anything with arguments is
// a job of its own (a folder, --new-window, --cwd, --edit, the Run value's --minimized) and keeps
// opening its own window.
public sealed class TrayWakeTests
{
    private static readonly IntPtr Own = new(0x10), Hidden = new(0x20), Shown = new(0x30);

    [Fact]
    public void OnlyABareLaunchLooksForAHiddenInstance()
    {
        Assert.True(TrayWake.IsPlainLaunch([]));
        Assert.False(TrayWake.IsPlainLaunch(["--new-window"]));
        Assert.False(TrayWake.IsPlainLaunch([@"C:\Users"]));
        Assert.False(TrayWake.IsPlainLaunch(["--minimized"]));
    }

    [Fact]
    public void PicksTheInstanceWhoseWindowsAreAllHidden()
    {
        var trays = new List<(IntPtr Handle, int Pid)> { (Own, 1), (Shown, 2), (Hidden, 3) };
        IntPtr picked = TrayWake.PickHidden(trays, ownPid: 1, processHasVisibleWindow: pid => pid == 2);
        Assert.Equal(Hidden, picked);
    }

    [Fact]
    public void NeverPicksItselfOrAVisibleInstance()
    {
        var trays = new List<(IntPtr Handle, int Pid)> { (Own, 1), (Shown, 2) };
        Assert.Equal(IntPtr.Zero, TrayWake.PickHidden(trays, ownPid: 1, processHasVisibleWindow: pid => pid == 2));
        // The launching process has no main window yet, so its own tray handle would look hidden.
        Assert.Equal(IntPtr.Zero, TrayWake.PickHidden([(Own, 1)], ownPid: 1, processHasVisibleWindow: _ => false));
    }
}
