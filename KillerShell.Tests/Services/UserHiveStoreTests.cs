using KillerShell.Services;
using Microsoft.Win32;
using Xunit;

namespace KillerShell.Tests.Services;

// Another user's registry seen as HKCU, for an all-users uninstall that puts back each user's
// "Open folders in KillerShell" (UserHives.cs). Their Software\Classes lives in a hive of its own.
public sealed class UserHiveStoreTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string MachineExe = @"C:\Program Files\KillerShell\KillerShell.exe";

    [Fact]
    public void ClassesGoToTheUsersClassesHive()
    {
        var registry = new FakeRegistryStore();
        var store = new UserHiveStore(registry, Sid, Sid + "_Classes");

        store.SetString(RegistryHive.CurrentUser, @"Software\Classes\Directory\shell", null, "KillerShell");

        Assert.Equal("KillerShell", registry.GetString(RegistryHive.Users, Sid + @"_Classes\Directory\shell", null));
    }

    [Fact]
    public void EverythingElseGoesToTheUsersOwnHive()
    {
        var registry = new FakeRegistryStore();
        var store = new UserHiveStore(registry, Sid, Sid + "_Classes");

        store.SetString(RegistryHive.CurrentUser, @"Software\KillerShell", "Theme", "Blood");

        Assert.Equal("Blood", registry.GetString(RegistryHive.Users, Sid + @"\Software\KillerShell", "Theme"));
    }

    [Fact]
    public void AllUsersKeysAreLeftAsTheyAre()
    {
        var registry = new FakeRegistryStore();
        registry.SetString(RegistryHive.LocalMachine, @"Software\Classes\Directory\shell\KillerShell", null, "Open in KillerShell");
        var store = new UserHiveStore(registry, Sid, Sid + "_Classes");

        Assert.True(store.KeyExists(RegistryHive.LocalMachine, @"Software\Classes\Directory\shell\KillerShell"));
    }

    [Fact]
    public void WithoutAClassesHiveNothingIsWrittenInItsPlace()
    {
        // A profile whose UsrClass.dat is missing: its NTUSER.DAT has a Software\Classes key of
        // its own, which is not what Windows reads for that user.
        var registry = new FakeRegistryStore();
        var store = new UserHiveStore(registry, Sid, classesRoot: null);

        store.SetString(RegistryHive.CurrentUser, @"Software\Classes\Directory\shell", null, "KillerShell");

        Assert.False(registry.KeyExists(RegistryHive.Users, Sid + @"\Software\Classes"));
        Assert.Null(store.GetString(RegistryHive.CurrentUser, @"Software\Classes\Directory\shell", null));
    }

    [Fact]
    public void AnotherUsersFoldersGoBackToWhatOpenedThemBefore()
    {
        var registry = new FakeRegistryStore();
        const string pilot = "\"C:\\Program Files\\Pilot\\Pilot.exe\" \"%1\"";
        registry.SetString(RegistryHive.Users, Sid + @"_Classes\Drive\shell\open\command", null, pilot);
        new FolderOpenerSetting(new UserHiveStore(registry, Sid, Sid + "_Classes"), installed: true, exePath: MachineExe).TurnOn();

        new FolderOpenerSetting(new UserHiveStore(registry, Sid, Sid + "_Classes"), installed: false, exePath: MachineExe).TurnOffFor(MachineExe);

        Assert.Equal(pilot, registry.GetString(RegistryHive.Users, Sid + @"_Classes\Drive\shell\open\command", null));
        Assert.False(registry.KeyExists(RegistryHive.Users, Sid + @"_Classes\Directory\shell\open"));
        Assert.Null(registry.GetString(RegistryHive.Users, Sid + @"_Classes\Directory\shell", null));
    }
}
