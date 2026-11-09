using System;
using System.Diagnostics;
using System.IO;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

// The Edit profile menu asks each PowerShell where its profile is. Windows PowerShell writes
// redirected output in the console's OEM code page, so an accented user folder (José) used to
// come back garbled, and a host that hung froze the lookup for as long as it hung.
public sealed class ShellProfilesTests
{
    private static readonly string WindowsPowerShell = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");

    [Fact]
    public void AnAccentedProfilePathSurvivesTheTrip()
    {
        const string path = @"C:\Users\José Müller\Documents\WindowsPowerShell\Microsoft.PowerShell_profile.ps1";
        var host = new ShellHost("Windows PowerShell", WindowsPowerShell);
        Assert.Equal(path, ShellProfiles.PathFor(host, "'" + path + "'", 15000));
    }

    [Fact]
    public void AHungHostGivesUpOnTime()
    {
        var host = new ShellHost("Windows PowerShell", WindowsPowerShell);
        var clock = Stopwatch.StartNew();
        string path = ShellProfiles.PathFor(host, "Start-Sleep -Seconds 30; 'C:\\late.ps1'", 3000);
        clock.Stop();
        Assert.Equal(string.Empty, path);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(7), "took " + clock.Elapsed);
    }
}
