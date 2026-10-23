using KillerShell.Services;
using Microsoft.Win32;
using Xunit;

namespace KillerShell.Tests.Services;

// The About card's "Use KillerShell as the default terminal". Values from Microsoft's Windows
// Terminal Group Policy page (learn.microsoft.com/windows/terminal/group-policy) and
// microsoft/terminal src/host/exe/CConsoleHandoff.h.
public sealed class DefaultTerminalSettingTests
{
    private const string Startup = @"Console\%%Startup";
    private const string KillerShell = "{20CA035E-649B-4288-BB97-19CB0871CD62}";
    private const string WtConsole = "{2EACA947-7F5F-4CFA-BA87-8F7FBEEFBE69}";
    private const string WtTerminal = "{E12CFF52-A866-4C77-9A90-F570A7AA2C6B}";
    private const string PreviewConsole = "{06EC847C-C0A5-46B8-92CB-7C92F6E35CD5}";
    private const string Conhost = "{B23D10C0-E52E-411E-9D5B-C09FDF709C7D}";
    private const string Exe = @"C:\Users\lab\AppData\Local\Programs\KillerShell\KillerShell.exe";

    private static FakeRegistryStore MachineWith(params string[] consoleHosts)
    {
        var registry = new FakeRegistryStore();
        foreach (string host in consoleHosts)
            registry.CreateKey(RegistryHive.ClassesRoot, @"PackagedCom\ClassIndex\" + host + @"\Microsoft.WindowsTerminal_1.24.12741.0_x64__8wekyb3d8bbwe");
        return registry;
    }

    private static string? Pair(FakeRegistryStore registry, string name)
        => registry.GetString(RegistryHive.CurrentUser, Startup, name);

    [Fact]
    public void TurningOnPointsWindowsAtTheConsoleHostAndKillerShell()
    {
        var registry = MachineWith(WtConsole);
        var setting = new DefaultTerminalSetting(registry, installed: true, exePath: Exe);

        Assert.True(setting.TurnOn());

        Assert.Equal(WtConsole, Pair(registry, "DelegationConsole"));
        Assert.Equal(KillerShell, Pair(registry, "DelegationTerminal"));
        Assert.Equal(DefaultTerminalState.On, setting.State);
    }

    [Fact]
    public void TurningOffPutsBackThePairItReplaced()
    {
        var registry = MachineWith(WtConsole);
        registry.SetString(RegistryHive.CurrentUser, Startup, "DelegationConsole", WtConsole);
        registry.SetString(RegistryHive.CurrentUser, Startup, "DelegationTerminal", WtTerminal);
        var setting = new DefaultTerminalSetting(registry, installed: true, exePath: Exe);

        setting.TurnOn();
        setting.TurnOff();

        Assert.Equal(WtConsole, Pair(registry, "DelegationConsole"));
        Assert.Equal(WtTerminal, Pair(registry, "DelegationTerminal"));
        Assert.Equal(DefaultTerminalState.Off, setting.State);
    }

    [Fact]
    public void AMissingPairComesBackMissing()
    {
        var registry = MachineWith(WtConsole);
        var setting = new DefaultTerminalSetting(registry, installed: true, exePath: Exe);

        setting.TurnOn();
        setting.TurnOff();

        // Missing is "Let Windows decide"; an empty value would read the same, but missing is
        // what was there.
        Assert.Null(Pair(registry, "DelegationConsole"));
        Assert.Null(Pair(registry, "DelegationTerminal"));
    }

    [Fact]
    public void AChoiceMadeLaterInSettingsIsLeftAlone()
    {
        var registry = MachineWith(WtConsole);
        var setting = new DefaultTerminalSetting(registry, installed: true, exePath: Exe);
        setting.TurnOn();

        // Settings > System > For developers > Terminal: Windows Console Host.
        registry.SetString(RegistryHive.CurrentUser, Startup, "DelegationConsole", Conhost);
        registry.SetString(RegistryHive.CurrentUser, Startup, "DelegationTerminal", Conhost);
        Assert.Equal(DefaultTerminalState.ChangedElsewhere, setting.State);

        setting.TurnOff();

        Assert.Equal(Conhost, Pair(registry, "DelegationConsole"));
        Assert.Equal(Conhost, Pair(registry, "DelegationTerminal"));
        Assert.Equal(DefaultTerminalState.Off, setting.State);
    }

    [Fact]
    public void APortableCopyChangesNothing()
    {
        var registry = MachineWith(WtConsole);
        var setting = new DefaultTerminalSetting(registry, installed: false, exePath: @"E:\Tools\KillerShell.exe");

        Assert.Equal(DefaultTerminalBlocker.Portable, setting.Blocker);
        Assert.False(setting.TurnOn());
        Assert.Null(Pair(registry, "DelegationTerminal"));
        Assert.False(registry.KeyExists(RegistryHive.CurrentUser, @"Software\Classes\CLSID\" + KillerShell));
    }

    [Fact]
    public void WithoutWindowsTerminalNothingChanges()
    {
        var registry = MachineWith();
        var setting = new DefaultTerminalSetting(registry, installed: true, exePath: Exe);

        Assert.Equal(DefaultTerminalBlocker.NoConsoleHost, setting.Blocker);
        Assert.False(setting.TurnOn());
        Assert.Null(Pair(registry, "DelegationTerminal"));
    }

    [Fact]
    public void PreviewsConsoleHostIsUsedWhenStableIsNotInstalled()
    {
        var registry = MachineWith(PreviewConsole);
        var setting = new DefaultTerminalSetting(registry, installed: true, exePath: Exe);

        setting.TurnOn();

        Assert.Equal(PreviewConsole, Pair(registry, "DelegationConsole"));
    }

    [Fact]
    public void TurningOnRegistersTheClassForThisUserWhenNoInstallDid()
    {
        var registry = MachineWith(WtConsole);
        var setting = new DefaultTerminalSetting(registry, installed: true, exePath: Exe);

        setting.TurnOn();

        Assert.Equal("\"" + Exe + "\"", registry.GetString(RegistryHive.CurrentUser,
            @"Software\Classes\CLSID\" + KillerShell + @"\LocalServer32", null));
    }

    [Fact]
    public void AnAllUsersRegistrationIsUsedAsItIs()
    {
        var registry = MachineWith(WtConsole);
        DefaultTerminalSetting.RegisterClass(registry, machine: true, @"C:\Program Files\KillerShell\KillerShell.exe");
        var setting = new DefaultTerminalSetting(registry, installed: true, exePath: @"C:\Program Files\KillerShell\KillerShell.exe");

        setting.TurnOn();

        Assert.False(registry.KeyExists(RegistryHive.CurrentUser, @"Software\Classes\CLSID\" + KillerShell));
    }

    [Fact]
    public void UnregisteringRemovesTheClass()
    {
        var registry = MachineWith(WtConsole);
        DefaultTerminalSetting.RegisterClass(registry, machine: false, Exe);

        DefaultTerminalSetting.UnregisterClass(registry, machine: false);

        Assert.False(registry.KeyExists(RegistryHive.CurrentUser, @"Software\Classes\CLSID\" + KillerShell));
    }

    [Fact]
    public void TurningOnTwiceKeepsTheFirstPairToPutBack()
    {
        var registry = MachineWith(WtConsole);
        registry.SetString(RegistryHive.CurrentUser, Startup, "DelegationConsole", Conhost);
        registry.SetString(RegistryHive.CurrentUser, Startup, "DelegationTerminal", Conhost);
        var setting = new DefaultTerminalSetting(registry, installed: true, exePath: Exe);

        setting.TurnOn();
        setting.TurnOn();
        setting.TurnOff();

        Assert.Equal(Conhost, Pair(registry, "DelegationConsole"));
        Assert.Equal(Conhost, Pair(registry, "DelegationTerminal"));
    }
}
