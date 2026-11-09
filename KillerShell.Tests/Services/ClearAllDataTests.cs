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
    [InlineData(FolderOpenerSetting.PreviousSetting)]
    [InlineData("FolderOpenerPreviousOpen")]
    public void InstallMarkersAndWhatToPutBackSurvive(string name)
        // What the About card replaced is not a preference: without it, turning the setting off
        // after a clear would lose what the user had before.
        => Assert.True(App.SurvivesClearAllData(name));

    // Default apps lists KillerShell through this subkey. Deleting it with the preferences
    // silently undoes the file associations the user set up.
    [Theory]
    [InlineData("Capabilities")]
    [InlineData("capabilities")]
    public void TheAssociationRegistrationSurvives(string subKey)
        => Assert.True(App.SurvivesClearAllDataSubKey(subKey));

    [Fact]
    public void AnyOtherSubKeyIsCleared()
        => Assert.False(App.SurvivesClearAllDataSubKey("SomeFuturePreference"));

    [Theory]
    [InlineData("Theme")]
    [InlineData("DefaultTerminalProfile")]
    [InlineData("HandoffLog")]
    public void PreferencesAreCleared(string name)
        => Assert.False(App.SurvivesClearAllData(name));
}
