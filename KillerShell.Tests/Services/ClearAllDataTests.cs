using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

// The About card's Clear all Data, decided value by value without touching the real registry.
public sealed class ClearAllDataTests
{
    [Theory]
    [InlineData("Installed")]
    [InlineData("InstallPath")]
    [InlineData("Version")]
    [InlineData(DefaultTerminalSetting.PreviousSetting)]
    public void InstallMarkersAndTheTerminalToPutBackSurvive(string name)
        // The saved default terminal is not a preference: without it, turning the default
        // terminal off after a clear would lose the terminal the user had before.
        => Assert.True(App.SurvivesClearAllData(name));

    [Theory]
    [InlineData("Theme")]
    [InlineData("DefaultTerminalProfile")]
    [InlineData("HandoffLog")]
    public void PreferencesAreCleared(string name)
        => Assert.False(App.SurvivesClearAllData(name));
}
