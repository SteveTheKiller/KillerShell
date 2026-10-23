using KillerShell.Services;
using Microsoft.Win32;
using Xunit;

namespace KillerShell.Tests.Services;

// The About card's "Open folders in KillerShell": the default verb of file-system folders and
// drives, which is what a double-click runs. Special folders (Folder\shell) are never touched.
public sealed class FolderOpenerSettingTests
{
    private const string Directories = @"Software\Classes\Directory\shell";
    private const string Drives = @"Software\Classes\Drive\shell";
    private const string Exe = @"C:\Users\lab\AppData\Local\Programs\KillerShell\KillerShell.exe";

    private static string? Default(FakeRegistryStore registry, RegistryHive hive, string key)
        => registry.GetString(hive, key, null);

    [Fact]
    public void TurningOnOpensFoldersAndDrivesInKillerShell()
    {
        var registry = new FakeRegistryStore();
        var setting = new FolderOpenerSetting(registry, installed: true, exePath: Exe);

        Assert.True(setting.TurnOn());

        Assert.Equal("KillerShell", Default(registry, RegistryHive.CurrentUser, Directories));
        Assert.Equal("KillerShell", Default(registry, RegistryHive.CurrentUser, Drives));
        Assert.Equal(FolderOpenerState.On, setting.State);
        Assert.False(registry.KeyExists(RegistryHive.CurrentUser, @"Software\Classes\Folder\shell"));
    }

    [Fact]
    public void TurningOnAddsTheVerbWhenNoneIsRegistered()
    {
        var registry = new FakeRegistryStore();
        var setting = new FolderOpenerSetting(registry, installed: true, exePath: Exe);

        setting.TurnOn();

        // The default names a verb, so the verb has to be there to run.
        Assert.Equal(FolderOpenerSetting.VerbCommand(Exe),
            registry.GetString(RegistryHive.CurrentUser, Directories + @"\KillerShell\command", null));
        Assert.Equal(FolderOpenerSetting.VerbCommand(Exe),
            registry.GetString(RegistryHive.CurrentUser, Drives + @"\KillerShell\command", null));
    }

    [Fact]
    public void AnAllUsersVerbIsUsedAsItIs()
    {
        var registry = new FakeRegistryStore();
        string machineExe = @"C:\Program Files\KillerShell\KillerShell.exe";
        registry.SetString(RegistryHive.LocalMachine, Directories + @"\KillerShell\command", null, FolderOpenerSetting.VerbCommand(machineExe));
        registry.SetString(RegistryHive.LocalMachine, Drives + @"\KillerShell\command", null, FolderOpenerSetting.VerbCommand(machineExe));
        var setting = new FolderOpenerSetting(registry, installed: true, exePath: machineExe);

        setting.TurnOn();

        Assert.False(registry.KeyExists(RegistryHive.CurrentUser, Directories + @"\KillerShell"));
    }

    [Fact]
    public void TurningOffPutsBackWhatOpenedFoldersBefore()
    {
        var registry = new FakeRegistryStore();
        registry.SetString(RegistryHive.CurrentUser, Directories, null, "openinfiles");
        var setting = new FolderOpenerSetting(registry, installed: true, exePath: Exe);

        setting.TurnOn();
        setting.TurnOff();

        Assert.Equal("openinfiles", Default(registry, RegistryHive.CurrentUser, Directories));
        // Nothing was there for drives, so nothing is: Windows' own default shows through again.
        Assert.Null(Default(registry, RegistryHive.CurrentUser, Drives));
        Assert.Equal(FolderOpenerState.Off, setting.State);
    }

    [Fact]
    public void AFileManagerChosenLaterIsLeftAlone()
    {
        var registry = new FakeRegistryStore();
        var setting = new FolderOpenerSetting(registry, installed: true, exePath: Exe);
        setting.TurnOn();

        registry.SetString(RegistryHive.CurrentUser, Directories, null, "openinfiles");
        Assert.Equal(FolderOpenerState.ChangedElsewhere, setting.State);

        setting.TurnOff();

        Assert.Equal("openinfiles", Default(registry, RegistryHive.CurrentUser, Directories));
        // Drives were still KillerShell's, so they go back.
        Assert.Null(Default(registry, RegistryHive.CurrentUser, Drives));
    }

    [Fact]
    public void TickingAgainAfterAnotherProgramTookFoldersKeepsWhatToPutBack()
    {
        var registry = new FakeRegistryStore();
        var setting = new FolderOpenerSetting(registry, installed: true, exePath: Exe);
        setting.TurnOn();
        registry.SetString(RegistryHive.CurrentUser, Directories, null, "openinfiles");   // drives still KillerShell's

        setting.TurnOn();
        setting.TurnOff();

        // "KillerShell" is never what to put back: drives return to what they had before any of it.
        Assert.Equal("openinfiles", Default(registry, RegistryHive.CurrentUser, Directories));
        Assert.Null(Default(registry, RegistryHive.CurrentUser, Drives));
        Assert.Equal(FolderOpenerState.Off, setting.State);
    }

    [Fact]
    public void TurningOffWithNothingSavedLetsWindowsDecide()
    {
        var registry = new FakeRegistryStore();
        var setting = new FolderOpenerSetting(registry, installed: true, exePath: Exe);
        setting.TurnOn();
        registry.DeleteValue(RegistryHive.CurrentUser, @"Software\KillerShell", FolderOpenerSetting.PreviousSetting);

        setting.TurnOff();

        Assert.Null(Default(registry, RegistryHive.CurrentUser, Directories));
        Assert.Null(Default(registry, RegistryHive.CurrentUser, Drives));
    }

    [Fact]
    public void AVerbPointingAtAnotherCopyIsRewrittenForThisOne()
    {
        // Left by a portable copy registered from the Associations card and deleted since: as the
        // double-click default it would run an exe that is not there.
        var registry = new FakeRegistryStore();
        registry.SetString(RegistryHive.CurrentUser, Directories + @"\KillerShell\command", null,
                           FolderOpenerSetting.VerbCommand(@"C:\Users\lab\Downloads\KillerShell.exe"));
        var setting = new FolderOpenerSetting(registry, installed: true, exePath: Exe);

        setting.TurnOn();

        Assert.Equal(FolderOpenerSetting.VerbCommand(Exe),
            registry.GetString(RegistryHive.CurrentUser, Directories + @"\KillerShell\command", null));
    }

    [Fact]
    public void AFolderOpensAsANewWindowWithoutTheSavedSession()
        // --new-window: a folder double-clicked in Explorer must not restore every saved tab first.
        => Assert.Equal("\"" + Exe + "\" --new-window \"%1\\.\"", FolderOpenerSetting.VerbCommand(Exe));

    [Fact]
    public void APortableCopyChangesNothing()
    {
        var registry = new FakeRegistryStore();
        var setting = new FolderOpenerSetting(registry, installed: false, exePath: @"E:\Tools\KillerShell.exe");

        Assert.False(setting.CanChange);
        Assert.Equal("Str_About_OpenFoldersPortable", setting.NoteKey);
        Assert.True(setting.ShowsNote);
        Assert.False(setting.TurnOn());
        Assert.Null(Default(registry, RegistryHive.CurrentUser, Directories));
    }

    [Fact]
    public void AMissingVerbIsAddedAgainWhileItIsOn()
    {
        var registry = new FakeRegistryStore();
        var setting = new FolderOpenerSetting(registry, installed: true, exePath: Exe);
        setting.TurnOn();

        registry.DeleteKey(RegistryHive.CurrentUser, Directories + @"\KillerShell");
        setting.Repair();

        Assert.True(registry.KeyExists(RegistryHive.CurrentUser, Directories + @"\KillerShell\command"));
    }
}
