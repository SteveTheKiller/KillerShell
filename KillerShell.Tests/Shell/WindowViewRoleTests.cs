using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

// Which set of per-window view settings a window reserves (ResultsView.cs WindowViews).
public sealed class WindowViewRoleTests
{
    [Theory]
    [InlineData(false, false, false, "normal")]
    [InlineData(false, true, false, "admin")]
    [InlineData(true, false, false, "demo.normal")]
    // A handed-off program's window must never take a normal window's slot: slot 0 is the one
    // that reads the settings saved before slots existed.
    [InlineData(false, false, true, "console")]
    [InlineData(false, true, true, "console")]
    public void EachKindOfWindowKeepsItsOwnViewSettings(bool demo, bool elevated, bool console, string role)
        => Assert.Equal(role, MainWindow.WindowViewRole(demo, elevated, console));
}
