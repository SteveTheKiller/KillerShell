using System;
using System.IO;
using KillerShell.Controls;
using Xunit;

namespace KillerShell.Tests.Shell;

public sealed class MotifCacheStartupTests
{
    [Fact]
    public void StartupNamesTheAppSoTheTrashPolkaCacheIsOn()
    {
        App.ConfigureDialogChrome();

        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerShell", "Motif");
        Assert.Equal("KillerShell", DialogChrome.AppName);
        Assert.Equal(expected, MotifDiskCache.Root);
    }
}
