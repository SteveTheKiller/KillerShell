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

    private const string Pilot = "\"C:\\Program Files\\Pilot\\Pilot.exe\" \"%1\"";

    [Fact]
    public void ProgramsAskingWindowsToOpenAFolderGetKillerShellToo()
    {
        // Start-Process and ShellExecute ask for "open", which a custom default never reaches
        // (KillerLab-W11, first KS-C run). Directory and Drive have no "open" of their own, so
        // Explorer's on Folder answers unless one sits in front of it.
        var registry = new FakeRegistryStore();
        var setting = new FolderOpenerSetting(registry, installed: true, exePath: Exe);

        setting.TurnOn();

        Assert.Equal(FolderOpenerSetting.VerbCommand(Exe),
            registry.GetString(RegistryHive.CurrentUser, Directories + @"\open\command", null));
        Assert.Equal(FolderOpenerSetting.VerbCommand(Exe),
            registry.GetString(RegistryHive.CurrentUser, Drives + @"\open\command", null));
    }

    [Fact]
    public void TurningOffTakesAwayTheOpenVerbItAdded()
    {
        var registry = new FakeRegistryStore();
        var setting = new FolderOpenerSetting(registry, installed: true, exePath: Exe);

        setting.TurnOn();
        setting.TurnOff();

        Assert.False(registry.KeyExists(RegistryHive.CurrentUser, Directories + @"\open"));
        Assert.False(registry.KeyExists(RegistryHive.CurrentUser, Drives + @"\open"));
    }

    [Fact]
    public void AnotherFileManagersOpenVerbComesBack()
    {
        var registry = new FakeRegistryStore();
        registry.SetString(RegistryHive.CurrentUser, Directories + @"\open", "MUIVerb", "Open in Pilot");
        registry.SetString(RegistryHive.CurrentUser, Directories + @"\open", "ExplorerCommandHandler", "{66666666-7777-8888-9999-000000000000}");
        registry.SetString(RegistryHive.CurrentUser, Directories + @"\open\command", null, Pilot);
        registry.SetString(RegistryHive.CurrentUser, Directories + @"\open\command", "DelegateExecute", "{11111111-2222-3333-4444-555555555555}");
        registry.SetString(RegistryHive.CurrentUser, Directories + @"\open\ddeexec", null, "[open(\"%1\")]");
        var setting = new FolderOpenerSetting(registry, installed: true, exePath: Exe);

        setting.TurnOn();
        // A handler, a DelegateExecute or a DDE conversation beside the command would run instead
        // of it, or alongside it.
        Assert.Null(registry.GetString(RegistryHive.CurrentUser, Directories + @"\open", "ExplorerCommandHandler"));
        Assert.Null(registry.GetString(RegistryHive.CurrentUser, Directories + @"\open\command", "DelegateExecute"));
        Assert.False(registry.KeyExists(RegistryHive.CurrentUser, Directories + @"\open\ddeexec"));
        setting.TurnOff();

        Assert.Equal(Pilot, registry.GetString(RegistryHive.CurrentUser, Directories + @"\open\command", null));
        Assert.Equal("Open in Pilot", registry.GetString(RegistryHive.CurrentUser, Directories + @"\open", "MUIVerb"));
        Assert.Equal("{66666666-7777-8888-9999-000000000000}",
            registry.GetString(RegistryHive.CurrentUser, Directories + @"\open", "ExplorerCommandHandler"));
        Assert.Equal("{11111111-2222-3333-4444-555555555555}",
            registry.GetString(RegistryHive.CurrentUser, Directories + @"\open\command", "DelegateExecute"));
        Assert.Equal("[open(\"%1\")]", registry.GetString(RegistryHive.CurrentUser, Directories + @"\open\ddeexec", null));
        Assert.False(registry.KeyExists(RegistryHive.CurrentUser, Drives + @"\open"));
    }

    [Fact]
    public void AnOpenVerbWithoutACommandComesBackWithoutOne()
    {
        var registry = new FakeRegistryStore();
        registry.SetString(RegistryHive.CurrentUser, Directories + @"\open", "MUIVerb", "Open in Pilot");
        var setting = new FolderOpenerSetting(registry, installed: true, exePath: Exe);

        setting.TurnOn();
        setting.TurnOff();

        Assert.Equal("Open in Pilot", registry.GetString(RegistryHive.CurrentUser, Directories + @"\open", "MUIVerb"));
        Assert.False(registry.KeyExists(RegistryHive.CurrentUser, Directories + @"\open\command"));
    }

    [Fact]
    public void TheOpenVerbFollowsItsDefaultWhenAnotherProgramTakesDoubleClicks()
    {
        // The box shows off once something else owns double-clicks, so programs must not keep
        // reaching KillerShell through the open verb either.
        var registry = new FakeRegistryStore();
        var setting = new FolderOpenerSetting(registry, installed: true, exePath: Exe);
        setting.TurnOn();

        registry.SetString(RegistryHive.CurrentUser, Directories, null, "openinfiles");
        setting.Repair();

        Assert.False(registry.KeyExists(RegistryHive.CurrentUser, Directories + @"\open"));
        Assert.Equal(FolderOpenerSetting.VerbCommand(Exe),
            registry.GetString(RegistryHive.CurrentUser, Drives + @"\open\command", null));
    }

    [Fact]
    public void TheOpenVerbFollowsADefaultThatWasReset()
    {
        var registry = new FakeRegistryStore();
        registry.SetString(RegistryHive.CurrentUser, Drives + @"\open\command", null, Pilot);
        var setting = new FolderOpenerSetting(registry, installed: true, exePath: Exe);
        setting.TurnOn();

        registry.DeleteValue(RegistryHive.CurrentUser, Drives, string.Empty);
        setting.Repair();

        Assert.Equal(Pilot, registry.GetString(RegistryHive.CurrentUser, Drives + @"\open\command", null));
    }

    [Fact]
    public void ACopyBeingRemovedTurnsItOffWhereItOpensFolders()
    {
        // An all-users uninstall run by SYSTEM or another administrator reaches each user's own
        // registry (UserHives.cs): an open verb naming the deleted exe would break every folder.
        var registry = new FakeRegistryStore();
        string machineExe = @"C:\Program Files\KillerShell\KillerShell.exe";
        new FolderOpenerSetting(registry, installed: true, exePath: machineExe).TurnOn();

        new FolderOpenerSetting(registry, installed: false, exePath: machineExe).TurnOffFor(machineExe);

        Assert.False(registry.KeyExists(RegistryHive.CurrentUser, Directories + @"\open"));
        Assert.Null(Default(registry, RegistryHive.CurrentUser, Drives));
    }

    [Fact]
    public void ACopyBeingRemovedLeavesAUsersOwnCopyAlone()
    {
        var registry = new FakeRegistryStore();
        new FolderOpenerSetting(registry, installed: true, exePath: Exe).TurnOn();
        string machineExe = @"C:\Program Files\KillerShell\KillerShell.exe";

        new FolderOpenerSetting(registry, installed: false, exePath: machineExe).TurnOffFor(machineExe);

        Assert.Equal(FolderOpenerSetting.VerbCommand(Exe),
            registry.GetString(RegistryHive.CurrentUser, Directories + @"\open\command", null));
        Assert.Equal("KillerShell", Default(registry, RegistryHive.CurrentUser, Directories));
    }

    [Fact]
    public void AnOpenVerbAnotherProgramTakesLaterIsLeftAlone()
    {
        var registry = new FakeRegistryStore();
        var setting = new FolderOpenerSetting(registry, installed: true, exePath: Exe);
        setting.TurnOn();

        registry.SetString(RegistryHive.CurrentUser, Directories + @"\open\command", null, Pilot);
        Assert.Equal(FolderOpenerState.ChangedElsewhere, setting.State);
        setting.TurnOff();

        Assert.Equal(Pilot, registry.GetString(RegistryHive.CurrentUser, Directories + @"\open\command", null));
        // The rest was still KillerShell's, so it goes back.
        Assert.False(registry.KeyExists(RegistryHive.CurrentUser, Drives + @"\open"));
        Assert.Null(Default(registry, RegistryHive.CurrentUser, Directories));
    }

    [Fact]
    public void AnOpenVerbLeftByAnotherCopyIsMendedByTheOneThatStays()
    {
        // Removing a duplicate install keeps the setting (App.RemoveInstallConflict), so the copy
        // that stays finds the open verb naming the one that went.
        var registry = new FakeRegistryStore();
        new FolderOpenerSetting(registry, installed: true, exePath: Exe).TurnOn();
        string machineExe = @"C:\Program Files\KillerShell\KillerShell.exe";
        var setting = new FolderOpenerSetting(registry, installed: true, exePath: machineExe);

        Assert.Equal(FolderOpenerState.On, setting.State);
        setting.Repair();

        Assert.Equal(FolderOpenerSetting.VerbCommand(machineExe),
            registry.GetString(RegistryHive.CurrentUser, Directories + @"\open\command", null));
        Assert.Equal(FolderOpenerSetting.VerbCommand(machineExe),
            registry.GetString(RegistryHive.CurrentUser, Drives + @"\open\command", null));
    }

    [Fact]
    public void APortableCopyThatFindsItOnSaysNothingAboutInstalling()
    {
        // The installed copy turned it on; the portable copy may turn it off, and only then
        // does "install KillerShell to open folders in it" apply.
        var registry = new FakeRegistryStore();
        new FolderOpenerSetting(registry, installed: true, exePath: Exe).TurnOn();
        var portable = new FolderOpenerSetting(registry, installed: false, exePath: @"E:\Tools\KillerShell.exe");

        Assert.Equal("Str_About_OpenFoldersHint", portable.NoteKey);
        Assert.False(portable.ShowsNote);

        portable.TurnOff();
        Assert.Equal("Str_About_OpenFoldersPortable", portable.NoteKey);
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
