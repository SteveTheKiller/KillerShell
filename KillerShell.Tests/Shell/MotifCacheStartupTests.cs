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
        string oldName = DialogChrome.AppName;
        int oldSplit = DialogChrome.WordmarkSplit;
        try
        {
            App.ConfigureDialogChrome();

            string expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerShell", "Motif");
            Assert.Equal("KillerShell", DialogChrome.AppName);
            Assert.Equal(expected, MotifDiskCache.Root);
        }
        finally
        {
            // Later tests in this process must not write motif layers into the real cache folder.
            DialogChrome.AppName = oldName;
            DialogChrome.WordmarkSplit = oldSplit;
        }
    }
}
